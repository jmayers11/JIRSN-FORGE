# Sprint 10 implementation report

Date: 2026-10-23
Specification: `Forge_Sprint_10_Training_Performance_Workforce_Intelligence.md`
Status: Core implementation pass complete; available local verification passed; full Sprint 9/Sprint 10 acceptance remains open.

## Delivered

Forge now has a source-backed Training & Performance Command Center on the Workforce page. It summarizes qualification expiry, machine/shift capability gaps, visible Workforce and Safety readiness blockers, operation labor context, attendance reliability, nominations, and material/suspect-report disruptions. The `!` alerts link to their source records. Missing standards, unavailable sources, incomplete attendance classification, and unverified quality evidence remain explicit rather than being turned into zeroes or employee scores. Attendance review supports calendar-quarter, month-to-date, rolling-90-day, and year-to-date windows and shows the scheduled, eligible, excluded, and pending counts behind the metric.

Competency records now separate course completion, practical assessment, and machine-specific authorization. Planning uses current qualifications and machine authorizations as distinct capacity/assignment gates. Attendance uses effective-dated policy versions and scheduled arrival opportunities. Classifications require independent review; corrections supersede the source event and return to review instead of overwriting history. Development nominations retain rationale, source references, expected plant benefit, estimate fields, and human-reviewed status transitions. Compensation consideration stays permission-limited and never changes pay automatically.

Operation labor views compare released routing setup/run targets with recorded operator and training hours and reported good/scrap quantities. Earned standard hours and efficiency remain unavailable without accepted-output evidence; completed variance includes its limitation. This keeps reported output from being silently treated as quality-accepted output.

Shop Floor users can record suspect scrap separately from final Quality disposition and request material from the operation. Replacement tasks link to one suspect report. Released orders can generate audited staging tasks for uncovered BOM quantities. Additional/out-of-BOM requests require a reason and independent Inventory approval; those tasks do not change released BOM readiness. Partial picks leave the unfulfilled quantity visible for another request. Material remains unavailable at point of use while in transit. A distinct active worker authenticates with badge and PIN to accept the handoff. The Inventory queue exposes exception reporting and append-only custody history.

Material-task state changes and custody exceptions feed the existing durable notification system. Default rules create in-app Planner, Inventory Controller, and Material Handler alerts; administrators can configure Email when the deployment has an email provider. Workforce intelligence includes linked material and suspect-report alerts without attributing fault or bypassing qualification/safety gates.

## Source map

- Workforce and Competency: `src/Forge.Host/wwwroot/js/pages/workforce-intelligence.js`, `src/Forge.Host/wwwroot/js/workforce-intelligence.mjs`, `src/Forge.Workforce/AttendanceReliability.cs`, `src/Forge.Workforce/DevelopmentNominations.cs`, `src/Forge.Competency/CompetencyService.cs`, `src/Forge.Execution/ProductionLaborMetrics.cs`.
- Material and quality flow: `src/Forge.Inventory/InventoryService.Tasks.cs`, `src/Forge.Quality/QualityService.cs`, `src/Forge.Host/Api/MaterialsEndpoints.cs`, `src/Forge.Host/Api/QualityEndpoints.cs`, `src/Forge.Host/wwwroot/js/pages/shopfloor.js`, `src/Forge.Host/wwwroot/js/pages/inventory.js`, `src/Forge.Host/wwwroot/js/pages/orders.js`.
- Planning and alerts: `src/Forge.Planning/MaterialFlow.cs`, `src/Forge.Planning/ShiftFactsProvider.cs`, `src/Forge.Host/Notifications/NotificationEventSubscriber.cs`, `src/Forge.Host/Notifications/NotificationDelivery.cs`.
- Additive migrations: Competency `0002`–`0003`; Execution `0012`; Inventory `0022`–`0025`; Kernel/Postgres `0022`–`0026`; Quality `0016`; Resources `0006`–`0008`; Workforce `0006`–`0009`.

## Verification and rollout

Verification after implementation: `node --test tests/ui/*.test.mjs` passed 335/335; isolated Release build of `Forge.Host` passed with 0 warnings and 0 errors; `Forge.Planning.Tests` passed 322/322; deterministic integration calculation tests passed 12/12; `git diff --check` passed. The integration project compiles. Database-backed integration cases could not start because Docker Engine is unavailable and `FORGE_TEST_POSTGRES` is unset. Full solution restore could not reach `api.nuget.org`; offline cache lacks `NetArchTest.Rules` and `Microsoft.Extensions.TimeProvider.Testing`. The live `PLANT1` preview and database were not changed; no WhiskerWorks seed or Sprint 10 migration was applied.

## Known limits

- The delivered Workforce `!` feed is a source-linked alert view; it does not yet rank and compare multi-step material recovery plans against staffing and source-center capacity.
- Custody supports full receipt acknowledgment and exception evidence. Partial acceptance/rejection, controlled return-to-stores, and reversal workflows need a separate audited implementation before this is full Section 15 acceptance.
- Sprint 10's expanded 60-worker fixture and all 38 material scenarios have not been exercised. Existing WhiskerWorks seed scripts are for the Sprint 9 demo and require a genuinely isolated database.
- Email delivery is available through the existing durable outbox/provider configuration; the new default material rules are InApp until a deployment's provider and notification policy are configured.
- Earned standard hours and labor efficiency remain N/A until operation-scoped accepted-output evidence is available. Current operation views preserve target/actual context and explain the missing basis.
- Sprint 9 remains subject to its own open scenario assertions and isolated-data gate. Sprint 10 must not be described as fully accepted until the relevant Sprint 9 regression gate and Sprint 10 scenario suite pass.
