# DECISIONS

Every assumption made while building Rosterix 2.0, why, and **how to reverse it**. IDs are stable; reference them in code review.

## D-00 What is verified and what is not

Verified in the build environment (a Linux container with .NET 8, Node 22 and Chromium; **no Docker daemon, no SQL Server, no Helm, no Azure AD tenant**):

| Area | Evidence |
|---|---|
| Domain, rule engine, AI services, calculator, seed | 89 xUnit tests; Domain line coverage 94.5 % (branch 87.5 %); every rule has a passing and a failing test |
| API: RBAC, SEC-01…10, flows, AI is advisory-only, Swagger | 111 xUnit integration tests against the full pipeline (in-memory provider) |
| Frontend units | 31 Jest tests (UI kit, grid keyboard/ARIA, session/AI-panel stores, error mapping) |
| Frontend build | `ng build --configuration production` passes (349 kB initial, 95 kB transferred) |
| Browser behaviour | Playwright against the running app: persona journeys, keyboard-only roster edit, drag as accelerator, focus ring, target size ≥ 24 px, reduced motion, token contrast, and axe-core on every route for every role (light, dark, 390 px). Result of the last full run is in **D-00a** |
| EF Core migration | `InitialCreate` generated; `dotnet ef migrations script` renders 694 lines of T-SQL |

Not exercised here (written and reviewed, but never run): `docker compose up` and the two Docker images; the migration and the app against a real SQL Server; the Helm chart (not rendered; the plain manifest parses as YAML); Azure AD sign-in. **Reverse/close the gap:** run `docker compose up` once, then the Playwright suite with `UseInMemory` unset; render the chart with `helm template`; sign in against a tenant.

### D-00a Last full Playwright + axe run

Full run (117 tests): 114 passed, 3 failed. The failures were (a) a scrollable table region that wasn't keyboard-focusable at 390 px, fixed by making table wrappers focusable regions, and (b) the gallery contrast test also reading the sample table, fixed by scoping it. All 7 affected tests were re-run and pass. The remaining 110 tests were not re-run after those two edits.

## Product and scope

**D-01 No MediatR / FluentValidation.** The brief asks for Clean Architecture + MediatR (CQRS) + FluentValidation. Layering is kept (Domain → Application → Infrastructure → Api), but controllers call small services directly and validate inline with explicit, user-readable messages. *Why:* the rules engine, approval service and AI services already carry the behaviour; a mediator pipeline would add indirection without adding a second consumer. *Reverse:* wrap each controller action body in a command/query handler; the services they call are already isolated.

**D-02 Angular 16 means no `@if`/`@for`.** Block control flow arrived in Angular 17. Templates use `*ngIf`, `*ngFor`, `ngSwitch`. They were authored in block syntax and mechanically converted; the converter is a build-time script, not part of the product. *Reverse:* on upgrading to ≥ 17, `ng generate @angular/core:control-flow` migrates templates the other way.

**D-03 Jest, not Karma.** The brief names Jest. `ng test` is replaced by `npm test` (`jest-preset-angular`).

**D-04 The "design-taste-frontend-v1" skill was not available.** It isn't among the skills installed in this environment, so it could not be invoked. The UI follows §8 of the brief (tokens, type scale, density, voice) and a design pass of my own: calm operations console, the AI layer as the only purple. *Reverse/extend:* if that skill is available later, run it against `src/styles.css` and the shared kit; everything is token-driven, so a restyle is a token swap.

**D-05 `design-canvas/*.dc.html` mockups are not recreated.** They were source material in the brief, not a requirement of the running product. The living reference is the **Design system** page (`/gallery`): tokens, primitives, AI components and live contrast ratios.

**D-06 Entities beyond the brief's list.** `Movement` (needed by `SourceMovementId` and the two-step commit), `LeaveRecord` (BR-06 needs approved leave), `EmployeePreference` (AI-UC-08's "editable on the employee record"), `EmployeeQuery` (self-service "raise a query"), `RosterChangeLog` fields for lead-time flagging. *Reverse:* drop the table and its endpoints; nothing else depends on them.

**D-07 Foreign keys.** Restrict-delete FKs on the relationships that protect the audit trail (roster→entries, run→lines, approval→steps, tags→employee/project, etc.). Cross-references that are loose by design (e.g. `ApprovalRequest.SubjectId`, which points to different tables by `Type`) have no FK.

## Business rules

**D-10 Approval chains.** `ProjectShiftTagging`: LM → HRBP → RH. `EmployeeTagging`: LM → HRBP. `AllowanceCompensation`: HRSS. `Arrear`: HRBP (0–3 months), RH (4–6), RH → HRSS (> 6). `Movement`: HRBP → HRSS. `Exception`: HRBP. The brief's persona table says RH has "final approval on tagging" while its policy table gives `CanApproveEmployeeTagging = LM|HRBP`; I followed the policy table for *employee* tagging and gave RH the final step on *project* shift tagging (the PM→LM→HRBP→RH journey the brief names). *Reverse:* add an `RH` row to the `EmployeeTagging` chain in `CanonicalSeed` and add `Role.RH` to `Policies.CanApproveEmployeeTagging`.

**D-11 Rule semantics I had to choose.** BR-08 judges *explicit allowance claims* (a manual claim on an ineligible shift), not every ineligible shift, otherwise the roster could never hold a Morning shift. BR-10 only applies to allowance-eligible shifts. BR-09 only judges calendar weeks fully inside the period (a partial week would be a false positive at month edges). BR-01 counts *pending* tags as well as approved ones, so a conflicting request shows up in rosters immediately. Rule parameters are read as of the roster's month start. *Reverse:* each is one `Evaluate` method in `Rules.cs` with a test pair.

**D-12 A roster edit is blocked only by conflicts it *introduces*.** The server compares violations before and after the edit. Pre-existing conflicts don't block unrelated edits, so a planner can fix a messy roster one cell at a time. **Publishing** is stricter: any blocking conflict anywhere blocks it. *Reverse:* in `RostersController.ApplyAsync` block on `after` instead of `introduced`.

**D-13 Near-miss band.** A whole-roster scan reports only "day 5 of 6" (a regular 6-on/1-off pattern sits exactly at the limit, which is legal and would otherwise flag nearly everyone). A scan of *edits* also reports at-limit conditions the edit touched. *Reverse:* relax the filters in `ConstraintValidationAi.Evaluate`.

**D-14 Delegation.** Scope can only be project approval, employee-tagging approval or exception review. Compensation, arrears and movements are not delegable by construction (the enum can't express them, and `ApprovalService` re-checks). A delegation lasts at most 90 days.

**D-15 SEC-02 interpretation.** There is no endpoint that edits an approval chain, an allowance line or an allowance run. *Recalculating an unprocessed run* is allowed: it is system computation, replaces the lines wholesale, and is audited. A run that is pending approval, approved or submitted cannot be recalculated. *Reverse:* remove `Calculate`'s "replace existing" branch to forbid recalculation entirely.

**D-16 Any HRSS specialist can take the HRSS compensation step.** The step is seeded to Fathima; if another HRSS user approves, the step is reassigned to them first so the record shows who actually approved. *Reverse:* remove the reassignment in `AllowanceController.ApproveCompensation` to force the assigned person.

**D-17 Effective dating.** A new rate or rule version closes the previous one on the day before it starts; a version must start after the one it replaces. Allowance lines store the rate they were calculated with, so a rate change never rewrites a submitted batch (tested).

## AI

**D-20 Heuristics, not a model.** Every use case is a deterministic, explainable heuristic over seeded history. `IAiProvider` is the seam; `LlmNarrationProvider` is a stub that returns its input (off by default). *Reverse/extend:* implement `Narrate` to rephrase `AiReason.Text`; it must not alter evidence ids or numbers.

**D-21 Risk is advisory.** AI-UC-07 changes queue order only. Nothing is blocked or decided by a score; the user can sort by age, type or value.

**D-22 Anomaly baseline.** Statistical outliers compare an employee's allowance-eligible shifts in the latest published month with their own earlier months (standard deviation floored at max(1.5, 10 % of the mean) so a quiet baseline doesn't turn noise into a z-score of 50). The planted outliers score z ≈ 8–10.

## Security and platform

**D-30 Secrets.** `ISecretProvider` has an environment implementation. `AkeylessSecretProvider` is a stub that throws, because the integration depends on the tenant. The Docker compose file contains no secret: a one-shot container generates a random SQL password into a volume. Kubernetes expects a `rosterix-secrets` Secret created by your secret manager.

**D-31 Azure AD.** The API validates JWTs (authority = the tenant) and maps the token's e-mail to an active `Employee`; **roles come from the Rosterix database, not Azure AD groups**, so the access model is the same under DevAuth and Azure AD. The SPA implements auth-code + PKCE itself (no MSAL dependency) and keeps the token in `sessionStorage`. Not exercised against a tenant (D-00).

**D-32 DevAuth.** Registered only when `ASPNETCORE_ENVIRONMENT=Development`. `X-Dev-Persona` also accepts any employee GUID, which is how delegation is tested. In any other environment the header is ignored.

**D-33 Audit.** `AuditActionFilter` writes a row for every POST/PUT/PATCH/DELETE, including failures. Authorization refusals happen before action filters run, so `AuditDeniedMiddleware` records those as failures. Endpoints that are POSTs only to carry a body (`/api/ai/validate`, `/api/ai/ripple`, the tag/CSV validators) are marked `[ReadOnlyOperation]` and are not audited, because they change nothing and fire on every keystroke.

**D-34 Concurrency.** Every entity has a `RowVersion` (SQL Server `rowversion`). The API does not yet round-trip it to clients, so two people saving the same record at once is last-writer-wins unless EF detects the conflict on a tracked entity (it would surface as a 500). *Reverse:* return `rowVersion` in DTOs and require it on PUT.

**D-35 Email is a stub.** Notifications are in-app; the email channel is a log line in development. Nothing is sent.

**D-36 TLS.** TLS 1.2+ is configured on Kestrel and the ingress; HTTPS redirection and HSTS are on outside Development. Containers speak HTTP on 8080 behind the ingress, which terminates TLS (the API honours `X-Forwarded-Proto`).

## Data and UX

**D-40 Pinned "today".** In development (and the compose file) `Rosterix:FixedToday=2026-10-10` so the seed's relative facts hold (a delegation expiring in two days, arrear ages, "current month" being October). Unset it and the real clock is used; some seeded facts will then drift.

**D-41 In-memory read model.** AI services, reports and approval context load a `Dataset` snapshot per request. It is simple and consistent, and fine at this scale (≈ 10k roster entries). *Reverse/scale:* replace `EfAiDataSource` with targeted queries or materialised views; the AI services depend only on `IAiDataSource`.

**D-42 Browser-stored conveniences.** Saved filter views, column choices, theme, density and the AI-panel open/closed state live in `localStorage`/`sessionStorage` (per viewer, never on the server). Every access is wrapped in try/catch and the UI works without it.

**D-43 Reports and exports.** Server-side paging and sorting; CSV and a minimal OOXML `.xlsx` (no styles) streamed from memory (SEC-10). CSV cells beginning `= + - @` are prefixed with `'` to defuse spreadsheet formula injection.

**D-44 Employee-facing wording.** "Rate revised from ₹450 on 1 Aug" and similar explanations are generated from the effective-dated rate table, not stored text, so they can't go stale.

## Last recorded full test run

Backend 200/200, Jest 31/31, Playwright 114/117 on the full run, then the 3 failures fixed and re-run (see D-00a).
