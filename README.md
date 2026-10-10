# Rosterix 2.0

Enterprise workforce, roster and allowance management (successor to the legacy "Compass" allowance tool).

```
project creation → shift tagging → employee tagging → multi-level approvals → roster planning →
allowance calculation → exception review → payroll submission → arrears, movements, delegation, settlement
```

Layered across it is an **AI assistance layer of nine advisory use cases**. Rosterix hands a finished payroll batch to an external system (CNB); it does not calculate payroll itself.

> **AI drafts. Humans commit.** Every AI output is visible, explained in plain language, confidence-scored, dismissible and reversible. Nothing an AI service produces is written to a persisted record without an explicit human action. There is no auto-apply, no "apply all", and no setting that turns this off. Tests prove it (see [The AI layer](#the-ai-layer)).

## Run it

**Backend only, no Docker (3 commands)**

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
UseInMemory=true ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Rosterix.Api --urls http://localhost:5199
```

API + Swagger: <http://localhost:5199/swagger>. The database is seeded on first boot.

**Whole app**

```bash
cd src/Rosterix.Web && npm ci && npm start          # SPA on http://localhost:4200, proxies /api to :5199
```

**Docker (SQL Server 2022 + API + web)**

```bash
docker compose up        # web http://localhost:4200 · api http://localhost:5199/swagger
```

Migrations and the seed run on first boot; a one-shot service generates a random SQL Server password into a volume, so there is no secret in the repo and no manual step. See [Verification status](#verification-status) for what has and hasn't been exercised.

**Sign in:** the login page is a development role-switcher with six personas. For API calls send `X-Dev-Persona: priya|arjun|meera|karthik|fathima|ravi` (or any employee GUID).

## Tests

```bash
dotnet test                                                       # 200 backend tests (rules, AI, RBAC/SEC, flows)
cd src/Rosterix.Web && npx jest                                   # 31 frontend unit tests
cd src/Rosterix.Web && npx ng build --configuration production    # production build + service worker
cd src/Rosterix.Web && npx playwright test                        # E2E + axe-core on every route for every role
```

`playwright.config.ts` starts the API (in-memory) and the SPA itself. In a sandbox without Playwright's own browser, set `PW_CHROMIUM` to a Chromium binary.

## Architecture

```
┌──────────────── Angular 16 SPA (standalone components, signals, PWA) ────────────────┐
│  shell · role-guarded routes · shared UI kit · roster grid · AI components           │
└───────────────────────────────────────┬──────────────────────────────────────────────┘
                                        │ /api (JSON, enums as strings)
┌───────────────────────────────────────▼──────────────────────────────────────────────┐
│ Rosterix.Api         DevAuth (Development only) | Azure AD JWT · policies · AuditActionFilter · controllers │
├──────────────────────────────────────────────────────────────────────────────────────┤
│ Rosterix.Infrastructure   EF Core 8 (SQL Server / InMemory) · DbSeeder · EfAiDataSource · ApprovalService · Notifier │
├──────────────────────────────────────────────────────────────────────────────────────┤
│ Rosterix.Application      Policies · DelegationValidator · AllowanceCalculator · CanonicalSeed │
│                           IAiProvider + nine AI services (read-only) · Dataset read model │
├──────────────────────────────────────────────────────────────────────────────────────┤
│ Rosterix.Domain           Entities · value objects · RuleEngine (BR-01 … BR-12)       │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

**One rule engine, three callers.** `IRuleEngine.Evaluate(RuleContext)` is called by the roster editor (live, on every edit), the allowance calculator (batch) and AI-UC-04. All three build their context with `RuleContextFactory`, so a rule can't behave differently in the grid and in the calculator. Rule limits (6 consecutive days, 11 h rest, 48 h/week, 1 weekly off) live in `ShiftRule.Parameters` and are edited by HRSS in Foundation data; none is hard-coded.

| Rule | What | Default | Severity |
|---|---|---|---|
| BR-01 | No overlapping active tags on two projects | | Block |
| BR-02 | Max consecutive working days | 6 | Block |
| BR-03 | Minimum rest between shifts | 11 h | Block |
| BR-04 | A roster entry needs an approved tag covering the date | | Block |
| BR-05 | Roster dates inside the project's active range | | Block |
| BR-06 | No shift on approved leave or a restricted holiday | | Warn |
| BR-07 | Max scheduled hours per week | 48 h | Warn |
| BR-08 | Allowance only for eligible shift types | | Block |
| BR-09 | Minimum weekly offs per calendar week | 1 | Block |
| BR-10 | An effective allowance rate must exist | | Block |
| BR-11 | Delegation: same role and grade, not a subordinate | | Block |
| BR-12 | A backdated change may not overlap an approved tag elsewhere | | Block |

Block prevents the save and explains why and how to fix it. Warn permits the save after an explicit, recorded acknowledgement.

## The AI layer

All nine use cases are deterministic, explainable heuristics over the application's own seeded history behind an `IAiProvider` seam, so the product is fully usable offline. `LlmNarrationProvider` is a feature-flagged stub (off by default) that could later rephrase reasons into prose without touching any data point.

| Code | Use case | What it reads | Where it appears |
|---|---|---|---|
| AI-UC-01 | Smart project setup | PM's last 3 projects in the BU | New project form |
| AI-UC-02 | Employee tagging | prior tags, eligibility, overlap, leave, grade, shift history | Tag flow, step 2 |
| AI-UC-03 | Roster generation | last 3 published rosters; self-reports conflicts via the rule engine | Roster → Suggest a draft |
| AI-UC-04 | Constraint validation | the rule engine + near-miss band | Roster edits, upload, publish |
| AI-UC-05 | Cost & allowance prediction | draft roster vs last 3 months; line-by-line trace | Allowance, Roster, My work |
| AI-UC-06 | Conflict / ripple preview | tags, roster entries, allowance lines, arrears | Movements |
| AI-UC-07 | Approval risk | rejection rate, date deviation, overlap, value vs median, wait; arrear variant adds age bucket and repeat incidence | Approvals queue and detail |
| AI-UC-08 | Preference learning | accepted shift/off-day history, editable override | Roster side card, My work |
| AI-UC-09 | Anomaly detection | z-score ≥ 2.5 vs the employee's baseline, duplicates, rate mismatches, impossible hours | Anomaly dashboard |

Every response has the same shape: `confidence` (0–1), `confidenceBand` (Low < 0.5, Medium 0.5–0.79, High ≥ 0.8), `summary`, `reasons[]` (each linked to its evidence record), `impact`, `reversible: true`.

**Why it is safe, and how that is tested**

* No AI service has a write path: they read an `IAiDataSource` snapshot and return a value. `Services_never_mutate_the_snapshot_they_read` and `There_is_no_auto_apply_surface` check this.
* `No_AI_endpoint_persists_anything_without_an_explicit_human_action` calls every AI endpoint and asserts that no table changed.
* The only AI-adjacent writes are explicit human actions: accepting a draft (`POST /api/rosters/accept-draft`, creates a *Draft* that is never published), and `POST /api/ai/feedback` (accept/modify/dismiss, which records who and why).
* The UI contract is built once and reused: `rx-ai-card`, `rx-confidence`, `rx-why` (focus-trapped, Escape-dismissible), `rx-ai-panel` (collapsed by default, state remembered per user), `rx-impact`. AI content is the only thing coloured purple, and it is always labelled "AI".

## Security (SEC-01 … SEC-10)

| ID | Rule | Where | Test |
|---|---|---|---|
| SEC-01 | Only HRSS approves allowance compensation, never via delegation | `Policies.CanApproveCompensation`, `ApprovalService.DecideAsync` | `SEC01_*` (PM/LM/HRBP/RH/EMP → 403, both endpoints) |
| SEC-02 | Nobody modifies the approval flow or allowance financial data | chains seeded read-only; no write route; no line/run edit route | `SEC02_*` (POST/PUT/PATCH/DELETE → 405) |
| SEC-03 | Delegate only to a same-level peer, never a subordinate | `DelegationValidator` (wraps BR-11) | `SEC03_*` (→ 400) |
| SEC-04 | No secrets in code/config | env vars, `ISecretProvider`, compose generates its own | `SEC04_no_secrets_in_source_or_config` |
| SEC-05 | TLS 1.2+, HTTPS, HSTS, redirect | Kestrel, `UseHsts`, ingress annotations | `SEC05_*` |
| SEC-06 | Sensitive fields masked in logs and audit | `Masking`, Serilog | `SEC06_*`, `SEC07_audit_masks_*` |
| SEC-07 | Every state change audited (type, UTC time, success/failure, actor, IP, endpoint, resource) | `AuditActionFilter` + `AuditDeniedMiddleware` (denials) | `SEC07_*` |
| SEC-08 | Azure AD in non-dev; DevAuth only in Development | `Program.cs` | `SEC08_*` |
| SEC-09 | Containers run as non-root | Dockerfiles, pod `runAsNonRoot` | `SEC09_*` |
| SEC-10 | No plaintext PII on disk | exports are streamed from memory | `SEC10_*` |

Authorization policies (single source of truth in `Policies.cs`): `CanApproveCompensation`=HRSS · `CanApproveProjectTagging`=LM|HRBP|RH · `CanApproveEmployeeTagging`=LM|HRBP · `CanManageFoundationData`=HRSS · `CanSubmitPayroll`=HRSS · `CanDelegate`=PM|LM · `CanBuildRoster`=PM|LM|HRSS.

## Accessibility (WCAG 2.2 AA is a build gate)

* **2.4.11 Focus appearance** 2 px solid ring, 2 px offset, ≥ 3:1, on every interactive element including grid cells.
* **2.5.7 Dragging** every drag has a keyboard path: select cells → `Enter` → pick a shift → confirm. Drag from the legend chips is an accelerator that opens the same dialog.
* **2.5.8 Target size** every interactive target ≥ 24 × 24 (32 default). Asserted by Playwright on five busy screens.
* Text ≥ 4.5:1 and UI ≥ 3:1 in light **and** dark. The **Design system** page (`/gallery`) computes every token pair live from the active theme and a test asserts none fails.
* Skip link, semantic landmarks, ARIA grid (`aria-rowcount`/`aria-colcount`/`aria-rowindex`/`aria-colindex`, roving tabindex), live regions for async results, focus moved to the page heading after navigation, `prefers-reduced-motion` honoured, no meaning by colour alone (every status has an icon and a label).
* `axe-core` runs in Playwright against every route for every role that can open it, in light, dark and at 390 px, plus detail routes. Zero serious or critical violations.

## Personas and a demo script

| Persona | Sign in as | Try this |
|---|---|---|
| **PM** Priya Nair | `priya` | **Projects → New project**: see AI-UC-01 pre-fill, accept or dismiss it. **Tagging → Tag employees**: pick the window, take AI-UC-02's ranked people, see the conflict step, submit. **Roster** (RET-01, October): conflicts list shows BR-02/BR-03/BR-06 planted in the draft; press Enter on a cell to assign with live validation; try **Publish** and see it blocked. **Roster → November → Suggest a draft**: accept it as a draft (never published). **Allowance**: calculate a month, see the forecast and the per-line trace. |
| **LM** Arjun Mehta | `arjun` | Open **Approvals** on a phone-width window: risk-sorted, tap through to the diff, approve, or reject (a reason is required). **Delegation**: see why a different-grade colleague or a subordinate can't be chosen; delegate to Nikhil Rao. |
| **HRBP** Meera Iyer | `meera` | **Approvals**: the *Tag 3 people from Festive fulfilment* request is High risk with an overlap check. **Exceptions**: filter, bulk-acknowledge, resolve with a reason. |
| **RH** Karthik Subramaniam | `karthik` | **Approvals**: the aged arrears carry an AI risk score (age bucket + repeat incidence). **Arrears**: see the routing by age. |
| **HRSS** Fathima Rasheed | `fathima` | **Anomalies**: the planted duplicate, rate mismatch and two outliers; dismiss with a reason. **Allowance**: approve compensation (the only role that can). **Payroll**: the checklist blocks submission until anomalies are resolved. **Movements**: preview the ripple of a backdated move, stage, then commit in a second step. **Foundation data**: add a rate effective next month; the 1 Aug NIGHT_A change shows effective dating. |
| **EMP** Ravi Kumar | `ravi` | **My work**: roster, *My allowance explained* (each line's trace, including the ₹450 → ₹525 revision), sign a declaration, raise a query. |

**Guardrail smoke test**

```bash
RUN=$(curl -s -H "X-Dev-Persona: fathima" localhost:5199/api/allowance-runs | python3 -c "import sys,json;print(json.load(sys.stdin)[0]['id'])")
curl -s -o /dev/null -w "PM   %{http_code}\n" -X POST -H "X-Dev-Persona: priya"   localhost:5199/api/allowance-runs/$RUN/approve-compensation   # 403
curl -s -o /dev/null -w "HRSS %{http_code}\n" -X POST -H "X-Dev-Persona: fathima" localhost:5199/api/allowance-runs/$RUN/approve-compensation   # 200
curl -s -o /dev/null -w "chain %{http_code}\n" -X POST -H "X-Dev-Persona: fathima" localhost:5199/api/approval-chains                           # 405
```

## Seed data

Deterministic (`Random(42)`, hash-derived GUIDs, a pinned "today" of 2026-10-10 in development), so AI results are reproducible. 2 regions, 4 business units, 12 shift types, 12 rules, 21 effective-dated rates (NIGHT_A for Retail Ops: ₹450 until 31 Jul 2026, ₹525 from 1 Aug), 120 employees with a hierarchy, 10 projects, 80 tags in Priya's portfolio (121 overall), four months of published rosters plus an October draft for three projects, 3 allowance runs with traces, 25 exceptions, 8 arrears across all age buckets, 15 pending approvals across the full risk range, planted anomalies, 2 delegations (one expiring in 2 days), an exiting employee with an open settlement, and declarations and notifications for Ravi.

## Verification status

What has actually been run, here:

| Check | Result |
|---|---|
| `dotnet build`, `dotnet test` | 200 tests pass (89 domain/AI, 111 API integration). Domain line coverage 94.5 % |
| Every rule, both branches | `RuleTests` (one pass + one fail per rule, plus parameter and severity overrides) |
| `ng build --configuration production` | passes; 349 kB initial (95 kB transferred) |
| `npx jest` | 31 tests pass |
| Playwright + axe | see the table in `DECISIONS.md` D-00 for the last full run |
| API against the in-memory provider | boots, seeds, enforces every guardrail |
| EF Core migration `InitialCreate` | generates and its SQL script renders (694 lines) |

What has **not** been run, and why. This build environment has no Docker daemon, no SQL Server, no Helm and no Azure AD tenant:

* `docker compose up` (SQL Server + API + web) and the Docker images: written and reviewed, not built or started here.
* The migration against a real SQL Server: the SQL script was generated, not executed.
* The Helm chart: written, not rendered (`helm` is not installable here); the plain manifests parse as YAML.
* Azure AD sign-in (auth-code + PKCE client, JWT validation): implemented, not exercised against a tenant.

Everything else, and every assumption with how to reverse it, is in [`DECISIONS.md`](DECISIONS.md). The milestone plan and file tree are in [`PLAN.md`](PLAN.md); the original product brief is in [`BUILD_GUIDE.md`](BUILD_GUIDE.md).

## Repository layout

```
src/Rosterix.Domain          entities, enums, value objects, rule engine (12 rules)
src/Rosterix.Application     policies, DelegationValidator, AllowanceCalculator, canonical seed, nine AI services
src/Rosterix.Infrastructure  DbContext, migrations, DbSeeder, EfAiDataSource, ApprovalService, Notifier, audit sink
src/Rosterix.Api             controllers, DevAuth, audit filter, Swagger
src/Rosterix.Web             Angular 16 SPA (+ e2e/ Playwright and axe)
tests/                       Rosterix.Domain.Tests, Rosterix.Api.Tests
deploy/                      Dockerfiles, nginx, k8s manifests, Helm chart, CI image
```
