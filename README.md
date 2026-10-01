# JIRSN Forge

A manufacturing operations platform that answers whether work can actually happen: with the people,
equipment, materials, qualifications and safety conditions available, at the time it is needed.

Architecture: **JIRSN Forge Software Architecture v0.1** (approved 2026-09-30). The phase plan is
in section 31. Implemented so far: **Phase 1 — Kernel**, **Phase 2 — Readiness slice**, **Phase 3 — Engineering and orders** and **Phase 4 — Capacity and scheduling**.

## What Phase 1 contains

| Area | Where | Notes |
| --- | --- | --- |
| Engine contract | `src/Forge.Kernel/Engines` | Pure `Evaluate(snapshot, parameters, asOf)`; evidence trees; input/output fingerprints for determinism |
| Canonical JSON | `src/Forge.Kernel/Serialization` | Order-independent hashing for fingerprints and audit |
| Authorization | `src/Forge.Kernel/Security` | Role AND program access AND data markings; every failed check is reported |
| Events | `src/Forge.Kernel/Events` | Envelope with business time vs recorded time, correlation and causation |
| Audit | `src/Forge.Kernel/Audit` | Hash-chained, append-only (database triggers), verifiable end to end |
| Access management | `src/Forge.Kernel/Access` | People, roles, program access; idempotent, audited, separation of duties |
| PostgreSQL | `src/Forge.Kernel.Postgres` | Migrations, unit of work, transactional outbox, relay, event log, subscriptions |
| Host | `src/Forge.Host` | ASP.NET Core API, OIDC (JWT) authentication, health checks |

## What Phase 2 contains

| Context | Where | Owns |
| --- | --- | --- |
| Competency | `src/Forge.Competency` | Qualification types and prerequisites; training; certification (by a certifier who is not the person) with expiry |
| Resources | `src/Forge.Resources` | Site → safety area → work center → machine; activity types; machine requirement profiles; activities allowed at a work center |
| Workforce | `src/Forge.Workforce` | Workers, assignments (incl. trainee), expiring presence evidence, unavailability without reasons |
| Safety rules | `src/Forge.SafetyRules` | Versioned staffing rules: drafted by a safety engineer, put in force only by a different approver |
| Planning | `src/Forge.Planning` | Engines only: personnel composition (bipartite matching), operation readiness, reallocation impact preview |

Readiness in Phase 2 evaluates the machine requirement profile, the qualified operator and safety
staffing. Equipment, material, engineering, tooling and quality are reported as "not evaluated"
until their contexts exist. Semantics chosen for open decisions are recorded in
`docs/adr/0002-phase-2-readiness-decisions.md`.

## What Phase 3 contains

| Context | Where | Owns |
| --- | --- | --- |
| Engineering | `src/Forge.Engineering` | Items, revisions with effectivity, BOMs (loop detection), routings; release validates and freezes a revision |
| Execution | `src/Forge.Execution` | Production orders: Planned → FirmPlanned → Released, Held, Cancelled; release snapshots BOM and routing |
| Planning | `src/Forge.Planning` | Order readiness per operation, across every eligible machine |

Orders tied to a program are listed and readable only by people with access to that program; to
everyone else they look like they do not exist. See `docs/adr/0003-phase-3-engineering-and-orders.md`.

## What Phase 4 contains

| Area | Where | Notes |
| --- | --- | --- |
| Shift calendars | `src/Forge.Resources` | Site time zone (IANA, via NodaTime), recurring shifts, work-center shifts, closures |
| Worker schedules | `src/Forge.Workforce` | A worker's shift; future absences come from recorded unavailability |
| Maintenance | `src/Forge.Maintenance` | Unplanned outages (open until restored) and planned maintenance windows |
| Engines | `src/Forge.Planning/Engines` | Executable capacity over time, qualified-labor supply (Skills MRP), finite scheduler with binding constraints, drop-dead dates, constraint finder |
| Schedules and constraints | `src/Forge.Planning` | Schedule versions (proposed, approved by a second person); constraints with an Open → Investigating → Resolved → Verified lifecycle |

Readiness now evaluates Equipment from recorded downtime. Forecasts use projected presence from
shift schedules and are labelled as projections. See `docs/adr/0004-phase-4-capacity-and-scheduling.md`.

Build rules enforced at compile time: nullable, warnings as errors, and a ban on `DateTime.Now`,
`Guid.NewGuid` and `Random` in production code (use `TimeProvider` and `ForgeIds`).
Architecture dependency rules are tests in `tests/Forge.Architecture.Tests`.

## Prerequisites

- .NET SDK 10
- PostgreSQL 16 or later for the integration tests, either:
  - a local install: copy `forge.local.example.cmd` to `forge.local.cmd` and set the `postgres`
    password (the file is not committed; each test creates and migrates its own database), or
  - Docker Desktop running: tests start a throwaway PostgreSQL 16 container

## Build and test

```
dotnet build Forge.slnx
dotnet test Forge.slnx
```

On Windows, double-click `verify.cmd`; results go to `.forge-logs\verify.log`.

## Run locally

```
docker compose up -d
dotnet run --project src/Forge.Host
```

Development mode uses header authentication (`X-Forge-Dev-Subject: dev-admin`) and bootstraps
`dev-admin` as the first security administrator. The host refuses to start in that mode outside
the Development environment. Every command needs an `Idempotency-Key` header and a `reason`.

```
curl -H "X-Forge-Dev-Subject: dev-admin" http://localhost:5080/api/kernel/me
```

## Kernel API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/kernel/me` | registered, active |
| GET | `/api/kernel/people` | `kernel.people.read` |
| POST | `/api/kernel/people` | `kernel.access.manage` |
| POST | `/api/kernel/people/{id}/roles` | `kernel.access.manage` |
| POST | `/api/kernel/people/{id}/roles/{role}/revoke` | `kernel.access.manage` |
| POST | `/api/kernel/people/{id}/programs` | `kernel.access.manage` |
| POST | `/api/kernel/people/{id}/programs/{programId}/revoke` | `kernel.access.manage` |
| GET | `/api/kernel/audit` | `kernel.audit.read` |
| GET | `/api/kernel/audit/verify` | `kernel.audit.read` |
| GET | `/health/live`, `/health/ready` | none |

## Phase 2 API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/resources/` | `resources.read` |
| POST | `/api/resources/sites`, `/safety-areas`, `/work-centers`, `/machines`, `/activity-types` | `resources.manage` |
| POST | `/api/resources/machines/{machine}/activities`, `/work-centers/{wc}/activities` | `resources.manage` |
| GET | `/api/competency/qualification-types`, `/api/competency/people/{id}` | `competency.read` |
| POST | `/api/competency/qualification-types` (+ `/{code}/prerequisites`), `/people/{id}/training` | `competency.manage` |
| POST | `/api/competency/people/{id}/certifications`, `/suspensions`, `/revocations` | `competency.certify` |
| POST | `/api/workforce/workers/{id}` | `workforce.manage` |
| POST | `/api/workforce/workers/{id}/assignment`, `/assignment/end`, `/presence`, `/departure`, `/unavailability` | `workforce.supervise` |
| GET | `/api/safety-rules/` | `safety.rules.read` |
| POST | `/api/safety-rules/drafts`, `/{code}/draft/discard` | `safety.rules.draft` |
| POST | `/api/safety-rules/{code}/versions/{v}/publish`, `/{code}/retire` | `safety.rules.approve` |
| GET | `/api/readiness/machines/{machine}?activity=`, `/api/readiness/active-work?safetyArea=` | `planning.readiness.read` |
| POST | `/api/readiness/reallocation-preview/{personId}` (read-only) | `planning.readiness.read` |

## Phase 3 API

| Method | Path | Permission |
| --- | --- | --- |
| GET | `/api/engineering/items`, `/items/{part}/revisions`, `/items/{part}/revisions/{rev}` | `engineering.read` |
| POST | `/api/engineering/items`, `/items/{part}/revisions`, `.../bom`, `.../bom/{line}/remove`, `.../routing`, `.../routing/{seq}/remove` | `engineering.manage` |
| POST | `/api/engineering/items/{part}/revisions/{rev}/release`, `/withdraw` | `engineering.release` |
| GET | `/api/orders/`, `/api/orders/{number}` | `execution.orders.read` + program access |
| POST | `/api/orders/`, `/{number}/firm`, `/hold`, `/resume`, `/cancel` | `execution.orders.manage` |
| POST | `/api/orders/{number}/release` | `execution.orders.release` |
| GET | `/api/orders/{number}/readiness` | `planning.readiness.read` + program access |

## Phase 4 API

| Method | Path | Permission |
| --- | --- | --- |
| POST | `/api/resources/shifts`, `/work-centers/{wc}/shifts`, `/sites/{site}/closures` | `resources.manage` |
| POST | `/api/workforce/workers/{id}/shift` | `workforce.manage` |
| GET | `/api/maintenance/downtime?from=&to=` | `maintenance.read` |
| POST | `/api/maintenance/machines/{machine}/down`, `/restore` | `maintenance.downtime.report` |
| POST | `/api/maintenance/machines/{machine}/planned` | `maintenance.plan` |
| GET | `/api/planning/capacity?from=&to=&workCenter=` | `planning.readiness.read` |
| POST | `/api/planning/schedules` (propose), `/schedules/{id}/approve` | `planning.schedule.propose` / `.approve` |
| GET | `/api/planning/schedules/proposed`, `/approved`, `/{id}` | `planning.schedule.read` (program-filtered) |
| GET | `/api/planning/constraints?includeClosed=` | `planning.schedule.read` (program-filtered) |
| POST | `/api/planning/constraints/{id}/investigate`, `/resolve`, `/verify` | `planning.constraints.manage` |
| GET | `/api/orders/{number}/drop-dead-dates?shipDays=` | `planning.readiness.read` + program access |
