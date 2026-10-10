# PLAN

Milestones from the brief, in build order, with what verifies each and where it stands. Every milestone was verified before the next began.

| # | Milestone | Verified with | Status |
|---|---|---|---|
| 1 | Solution scaffold, EF model, migrations, seed | API boots and seeds; counts asserted in `SeedTests`; `InitialCreate` generates T-SQL | Done. *Against real SQL Server: not run (no Docker here)* |
| 2 | Auth (DevAuth + Azure AD scheme), RBAC policies, audit | `SecurityTests`, `RbacMatrixTests` (≈ 40 negative cases) | Done. *Azure AD not run against a tenant* |
| 3 | Design system: tokens, primitives, grid, a11y utilities | `/gallery` page; axe clean; token contrast test (light + dark) | Done |
| 4 | Rule engine + 12 rules + tests | `RuleTests`: pass and fail per rule; 94.5 % line coverage on Domain | Done |
| 5 | Projects, tagging, approval chain | `PM_to_LM_to_HRBP_to_RH_*` (API and Playwright) | Done |
| 6 | Roster grid, upload, publish, live validation | Playwright: keyboard-only edit, drag alternative, blocked change, blocked publish | Done |
| 7 | Allowance, exceptions, reports, payroll | `Calculate_process_approve_*`, payroll gate tests, `Reports_*` | Done |
| 8 | Movements + ripple, delegation, arrears, settlement, self-service, notifications | `Backdated_movement_*`, `Arrears_route_*`, `Settlement_*`, `Employee_self_service_*` | Done |
| 9 | Nine AI services + shared AI components | `AiServiceTests` (reasons against seeded data), `AiAdvisoryOnlyTests` | Done |
| 10 | Full suite, axe sweep, deploy artefacts, docs | see `DECISIONS.md` D-00 / D-00a | Done, with the not-run items in D-00 |

## File tree

```
Rosterix.sln  global.json  Directory.Build.props  docker-compose.yml  .env.example  .editorconfig  Jenkinsfile
README.md  PLAN.md  DECISIONS.md  BUILD_GUIDE.md
src/
  Rosterix.Domain/          Entities.cs Enums.cs ShiftMath.cs  Rules/{RuleModel,Rules}.cs
  Rosterix.Application/     Security/Policies.cs  Data/{Dataset,CanonicalSeed,Personas,RuleContextFactory,IClock}.cs
                            Allowance/AllowanceCalculator.cs  Ai/{AiContract,SetupTaggingRoster,ValidateForecastRipple,RiskAnomaly}.cs
  Rosterix.Infrastructure/  Persistence/{RosterixDbContext,DbSeeder,DesignTimeFactory}.cs + Migrations/
                            Services/{EfAiDataSource,ApprovalService,Notifier,Audit}.cs
  Rosterix.Api/             Program.cs  SwaggerFilters.cs  Auth/{DevAuth,CurrentUser,AuditActionFilter}.cs
                            Controllers/{Core,Projects,Approvals,Rosters,Allowance,Ai,Reports,People}Controller.cs
  Rosterix.Web/             src/styles.css (tokens + primitives)  src/app/{shell,app.routes,app.config}.ts
                            src/app/core/{api,auth,interceptors,ui.services}.ts
                            src/app/shared/{ui,tools,roster-grid,fmt}.ts
                            src/app/pages/{login,home,projects,tagging,approvals,roster,allowance,exceptions,reports,people,me,foundation,gallery}.ts
                            e2e/{a11y,journeys}.spec.ts  playwright.config.ts
tests/
  Rosterix.Domain.Tests/    RuleTests EntityTests ValueAndServiceTests
  Rosterix.Api.Tests/       SecurityTests FlowTests RbacAndAiTests
deploy/                     Dockerfile.api Dockerfile.web Dockerfile.ci nginx.conf.template k8s/ helm/rosterix/
.github/workflows/ci.yml
```

## What comes next

1. Run `docker compose up` once and the Playwright suite against SQL Server (the only unverified runtime path).
2. Render the Helm chart and point Azure AD at a real tenant.
3. Return `rowVersion` through the API for optimistic concurrency (D-34).
4. Replace the stubbed email channel and the Akeyless secret provider with the real integrations (D-30, D-35).
