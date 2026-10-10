# Rosterix 2.0: Build Guide

The product brief this repository implements: the data model, the business rules, the nine AI use cases, security/RBAC, every screen, the design system, the seed data and the definition of done. For how to run it and what has been verified, read [README.md](README.md); for every assumption and how to reverse it, [DECISIONS.md](DECISIONS.md).

## 1. What you are building

Rosterix 2.0 is an enterprise workforce, roster and allowance-management platform (successor to the legacy "Compass" allowance tool). It covers the full lifecycle: project creation → shift tagging → employee tagging → multi-level approvals → roster planning → allowance calculation → exception review → payroll submission → arrears, movements, delegation, and full-and-final settlement. Layered across it is an AI assistance layer of nine advisory use cases. Rosterix hands a finished payroll batch to an external system (CNB); it does not calculate payroll itself.

**The single most important product rule.** AI drafts. Humans commit. Every AI output is visible, explained in plain language, confidence-scored, dismissible and reversible. Nothing an AI service produces is written to a persisted record without an explicit human action. There is no "auto-apply", no "apply all", and no setting that turns this off. A test proves it.

**Personas (build for all six).**

| Role | Persona (demo login) | Primary job |
|---|---|---|
| PM | Priya Nair | Creates projects, tags projects/employees, builds rosters, processes allowances |
| LM | Arjun Mehta | Approves PM requests (mostly on mobile), delegates when travelling |
| HRBP | Meera Iyer | Approves tagging for a BU, reviews exceptions weekly |
| RH | Karthik Subramaniam | Final approval on tagging and aged arrears; highest-stakes, least context |
| HRSS | Fathima Rasheed | Owns foundation data, movements/backtagging, org-wide anomalies, payroll; sole approver of allowance compensation |
| EMP | Ravi Kumar | Views own roster, signs declarations, sees how allowance was calculated, raises a query |

## 2. Technology stack

Angular 16 SPA + PWA (standalone components, signals, no NgRx; hand-rolled component library on CSS custom properties) · .NET 8 ASP.NET Core Web API, Clean Architecture · EF Core 8 code-first with migrations, no lazy loading · SQL Server 2022 (Docker locally, Azure SQL in cloud; tests use EF InMemory) · Azure AD OIDC (auth-code + PKCE) in staging/production, DevAuth role switcher locally · Docker + compose locally, AKS manifests + Helm to deploy · GitHub Actions + Jenkinsfile · Serilog (JSON console + file, sensitive fields masked) · xUnit + FluentAssertions, Jest, Playwright + axe-core.

## 3. Domain model

Every entity has Id (GUID), CreatedAtUtc, CreatedBy, ModifiedAtUtc, ModifiedBy, RowVersion. Soft-delete via IsActive where noted; never hard-delete anything in an audit trail.

* **Organisation & people:** Region, BusinessUnit, Employee (roles are flags: a person can hold several), Delegation.
* **Projects & tagging:** Project, ProjectShiftTag, EmployeeTag.
* **Foundation data (HRSS-owned):** ShiftType, ShiftRule (parameters are JSON, editable, never hard-coded), AllowanceRate (effective-dated), HolidayCalendar.
* **Roster:** Roster, RosterEntry, RosterChangeLog.
* **Approvals:** ApprovalRequest, ApprovalStep, ApprovalChainDefinition (seeded, read-only: SEC-02).
* **Allowance, exceptions, payroll:** AllowanceRun, AllowanceLine (with a plain-language CalculationTrace), ExceptionItem, Arrear (computed age bucket), PayrollBatch, Declaration, Settlement.
* **Cross-cutting:** Notification, AuditLog, AiSuggestion, AiFeedback.

The rule engine (`IRuleEngine.Evaluate(RuleContext) → RuleResult[]`) is called by three callers with one implementation: the roster editor (live), the allowance calculator (batch) and AI-UC-04. Never duplicate rule logic.

## 4. Business rules (the highest-value surface)

Each rule returns `{ ruleCode, severity, message (plain language + how to fix), affectedEntities[], suggestedFix? }`. Block prevents the save and explains why; Warn permits the save after an explicit, recorded acknowledgement. Parameters live in `ShiftRule.Parameters`, editable by HRSS.

| Code | Rule | Default | Severity |
|---|---|---|---|
| BR-01 | No overlapping active tags on two projects for the same date | | Block |
| BR-02 | Max consecutive working days | 6 | Block |
| BR-03 | Min rest between consecutive shifts | 11 h | Block |
| BR-04 | A roster entry requires an approved employee tag covering that date | | Block |
| BR-05 | Roster dates must fall within the project's active range | | Block |
| BR-06 | No shift on an approved leave day or restricted holiday | | Warn |
| BR-07 | Max scheduled hours per week | 48 | Warn |
| BR-08 | Allowance only for allowance-eligible shift types | | Block |
| BR-09 | Min weekly offs per calendar week | 1 | Block |
| BR-10 | An effective allowance rate must exist for (shift type, BU, grade) on the work date | | Block |
| BR-11 | Delegation target must be same role + grade, and not a subordinate | | Block |
| BR-12 | A backdated change may not orphan/overlap an existing approved tag elsewhere | | Block |

Test gate: one test per rule, both the passing and the failing branch. At least 90 % coverage on the Domain project.

## 5. The nine AI use cases

All nine are deterministic, explainable heuristics over the application's own seeded history, behind an `IAiProvider` seam. No external model is required. Each returns confidence (0–1), confidenceBand (Low < 0.5 / Medium 0.5–0.79 / High ≥ 0.8), summary, reasons[] (each linked to its evidence record), impact and `reversible: true`. No AI service has a write path.

| Code | Use case | Heuristic |
|---|---|---|
| AI-UC-01 | Smart project setup | Pre-fills a new project from the PM's last 3 projects in the BU |
| AI-UC-02 | Employee tagging | Ranks candidates by prior tag, eligibility, overlap, availability, grade fit, shift history; top 10 with reasons |
| AI-UC-03 | Roster generation | Drafts a month from the last 3 published rosters; runs the rule engine; lands as Draft/AiDraft, never publishes |
| AI-UC-04 | Constraint validation | Rule engine on every edit (≤ 200 ms debounce) + a near-miss band |
| AI-UC-05 | Cost & allowance prediction | Month-end cost vs the last 3 months + drivers; per-employee plain-language trace |
| AI-UC-06 | Conflict / ripple preview | Before/after diff of every downstream record before a movement/backtag commits |
| AI-UC-07 | Approval risk | Scores pending requests; sorts the queue; never blocks; arrear variant adds age bucket + repeat incidence |
| AI-UC-08 | Preference learning | Per-employee shift affinity from accepted history; editable on the employee record |
| AI-UC-09 | Anomaly detection | z-score ≥ 2.5 vs baseline, duplicate claims, rate mismatches, impossible hours |

Shared UI contract: `rx-ai-suggestion-card`, `rx-confidence-badge` (band word + icon + number, never colour-only), `rx-why-popover` (keyboard-accessible, Escape-dismissible, focus-trapped), `rx-ai-assistant-panel` (collapsed by default, state persists per user), `rx-impact-preview`. No AI feature may add perceptible latency to a high-frequency task.

## 6. Security and RBAC (hard gates)

SEC-01 only HRSS approves allowance compensation (never PM/LM/RH, even via delegation) · SEC-02 no role may modify the approval flow or allowance financial data · SEC-03 delegation only to a same-level peer of the same role, never a subordinate · SEC-04 no secrets in code/config · SEC-05 TLS 1.2+, HTTPS, HSTS, redirect · SEC-06 sensitive fields masked in logs · SEC-07 audit every state-changing action · SEC-08 Azure AD in non-dev, DevAuth only in Development · SEC-09 containers run as non-root · SEC-10 no plaintext PII on disk.

Policies: CanApproveCompensation=HRSS · CanApproveProjectTagging=LM|HRBP|RH · CanApproveEmployeeTagging=LM|HRBP · CanManageFoundationData=HRSS · CanSubmitPayroll=HRSS · CanDelegate=PM|LM · CanBuildRoster=PM|LM|HRSS.

## 7. Accessibility: WCAG 2.2 AA is a build gate

2.4.11 Focus Appearance (2 px solid, 2 px offset, ≥ 3:1, never removed without a replacement) · 2.5.7 Dragging Movements (every drag has a keyboard/click equivalent; drag is an accelerator only) · 2.5.8 Target Size (≥ 24 × 24, 32 compact, 40 comfortable). Plus: text ≥ 4.5:1 (≥ 3:1 large/UI), full keyboard operability with skip-to-content, semantic landmarks, correct grid ARIA, live regions for async results, `prefers-reduced-motion`, no meaning by colour alone. axe-core runs in Playwright against every route for every role with zero serious/critical violations.

## 8. Design system

A high-density operations console for long sessions: calm legibility and unambiguous state. Boldness is spent in exactly one place: the AI layer is visually distinct so a user always knows what came from the machine. Tokens are CSS custom properties (light + dark): surfaces, text, human action blue, **AI purple reserved exclusively for AI**, status colours, density (32/40 px rows), focus ring. IBM Plex Sans throughout; IBM Plex Mono for identifiers, dates and money (tabular figures). Sentence case. Buttons name the outcome ("Publish roster"). Errors say what happened and how to fix it. AI copy is honest about uncertainty.

## 9. Screens and flows

All nine phases exist, are role-guarded, reachable and seeded: **Auth & landing** (login, role-aware home, delegation banner) · **Projects & shift tagging** · **Employee tagging** (multi-step flow with AI-UC-02, conflicts via AI-UC-04, bulk CSV) · **Approvals** (risk-sorted queue with explicit sort override, inline context, bulk approve with mandatory review, reject with required reason, mobile-first at 390 px) · **Roster** (grid, density toggle, keyboard and drag, AI draft with AI-marked cells, live validation, upload/download, publish as a distinct confirmed action with lead-time flag) · **Allowance** (calculate, forecast, process → compensation approval by HRSS) · **Exceptions & anomalies** · **Reports & payroll** (server-side paging, column chooser, saved filters, CSV + XLSX; pre-submission checklist) · **Movements, delegation, arrears, settlement, self-service, foundation data, notifications**.

## 10. Seed data

Deterministic (`Random(42)`) so AI heuristics are reproducible: 2 regions, 4 business units, 12 shift types, a 2026 holiday calendar, 12 rules, ~20 effective-dated rates including one mid-period change, ~120 employees (six fixed personas), 10 projects, ~85 employee tags, 4 months of published rosters + a current draft for 3 projects, 3 allowance runs with traces, ~25 exceptions, 8 arrears across all age buckets, 15 pending approvals spanning the full risk range, planted anomalies (1 duplicate, 1 rate mismatch, 2 outliers), 2 delegations (one expiring in 2 days), an exiting employee with an open settlement, declarations and notifications for Ravi.

## 11. Build order

1 scaffold, EF model, migrations, seed → 2 auth, RBAC, audit → 3 design system → 4 rule engine + 12 rules + tests → 5 projects, tagging, approvals → 6 roster → 7 allowance, exceptions, reports, payroll → 8 movements, delegation, arrears, settlement, self-service, notifications → 9 nine AI services + shared AI components → 10 full suite, axe sweep, deploy artefacts, docs.

## 12. Definition of done

**Runs:** `docker compose up` brings up SQL + API + web with no manual step; migrations + seed on first boot; README gets a new developer running in ≤ 3 commands; no TODO / `NotImplementedException` / stubbed screen / lorem ipsum.
**Functional:** all six personas complete their journey; all nine phases navigable; all nine AI use cases return real suggestions with ≥ 2 genuine reasons; no AI output reaches a persisted record without an explicit human action (proven by test); all 12 rules enforced identically in the grid, the calculator and AI-UC-04.
**Security:** every SEC-01…10 holds with negative integration tests; every state change writes an AuditLog row; secret scan passes.
**Accessibility:** axe zero serious/critical on every route for every role; every drag has a working keyboard path; every target ≥ 24 × 24; full keyboard journey with no trap; contrast passes light + dark.
**Tests:** Domain ≥ 90 % coverage, one test per rule both branches; API integration tests including RBAC negatives; Playwright one journey per persona; `dotnet build`, `dotnet test`, `ng build --configuration production`, `jest`, `npx playwright test` all pass.
**Docs:** DECISIONS.md lists every assumption + reversal; Swagger complete with examples and error codes; README has architecture, the AI-layer explanation and the per-persona demo script.

## 13. What this build is NOT

Not a payroll engine (Rosterix hands a batch to CNB). Not an integration build (Informatica/WH/MDM feeds are seeded tables + a documented ingestion interface). Not autonomous AI (no auto-apply path in v1, by design).

## 14. Current state of this repository

The full vertical is built: the .NET 8 solution (domain, rule engine, nine AI services, API, RBAC/SEC), the Angular 16 SPA with all nine phases of screens and the shared AI components, the deployment artefacts, and the test suites. What was and was not run is recorded precisely in [DECISIONS.md D-00](DECISIONS.md#d-00-what-is-verified-and-what-is-not): everything has been run except the Docker/SQL Server path, the Helm chart and Azure AD sign-in, which need infrastructure the build environment does not have.
