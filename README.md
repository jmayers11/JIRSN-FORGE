# JIRSN Forge

A manufacturing operations platform that answers whether work can actually happen: with the people,
equipment, materials, qualifications and safety conditions available, at the time it is needed.

Architecture: **JIRSN Forge Software Architecture v0.1** (approved 2026-09-30). The phase plan is
in section 31; this repository currently implements **Phase 1 — Kernel**.

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
