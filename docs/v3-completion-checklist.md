# V3 completion checklist

Owner request: complete V3 and record the result. Last checkpoint: 2026-10-11.

This is the release checklist, not a completion declaration. The supplied `JIRSN Forge V3 Plan.md` defines the numbered roadmap and acceptance gates; use it together with the governance expansion.

## Increment verification

| Increment | Current state | Remaining gate |
| --- | --- | --- |
| V3.1–V3.8 | Previously verified and committed through `0232d18`; historical details in AI log | Preserve regression coverage |
| V3.9 | Full verification PASS: Planning 257, architecture 32, kernel 33, integration 81; zero build warnings/errors; commit `411c3c9` | Complete |
| V3.10 | Full PASS: Planning 262, architecture 32, kernel 33, integration 85; zero warnings/errors; selected API 14/14; commit `f01f611` | Complete |
| V3.11 | Full PASS: Planning 266, architecture 32, kernel 33, integration 90; zero warnings/errors; commit `4f18d85` | Complete |
| V3.12 | Full verify PASS; Planning 266, architecture 32, kernel 33, integration 92; commit `5db186c` | Complete |
| V3.13 | Full verify PASS; Planning 266, architecture 32, kernel 33, integration 94; focused API 17/17; zero warnings/errors; commit `20bfd9f` | Complete |
| V3.14 | Full verify PASS; Planning 266, architecture 32, kernel 33, integration 95; focused PostgreSQL API 1/1; zero build warnings/errors; commit `2d83b5a` | Complete |
| V3.15 | Full verify PASS: Planning 266, Architecture 32, Kernel 33, Integration 100; zero build warnings/errors; all V3.15 PostgreSQL acceptance tests passed | Complete (commit recorded in AI handoff) |
| V3.16–V3.28 | Numbered source plan supplied in `JIRSN Forge V3 Plan.md` | Implement each roadmap increment and its explicit acceptance gate |

## Governance expansion validation

| Scenario | Current coverage | Remaining evidence |
| --- | --- | --- |
| A — expired required gauge | V39 unit/API tests pass in focused runs | Full suite/release checkpoint |
| B — historical calibration | Existing ToolValidity and V38 tests | Confirm full-suite pass and relevant inspection-use linkage |
| C — shared terminal actor protection | Kiosk authenticates badge for operations; training signs badge/PIN | Configurable inactivity lock, secure switching and explicit shared-session regression |
| D — step-up hold release | JWT validation and hold release permission foundation | Trusted Nexus assurance, configured step-up, signature binding and end-to-end test |
| E — authenticated but unqualified | Existing operator authorization and trainee/qualification tests | Retain in full verification and contextual authorization migration |
| F — temporary authority | Missing | Approved scoped timed delegation and expiry tests |
| G — generated shift handoff | Analyst shift brief and authoritative source records exist | Persistent pass-on, checklist, acknowledgment/actions and scenario test |
| H — controlled priority impact | Scheduler/what-if foundation exists | Audited proposal/impact/authorized human commit with displaced commitments |
| I — downstream hold impact | Production holds plus staged generalized gates/register | Downstream impact read model, all activity gates, evidence/signoff policy |
| J — outside-processing round-trip | Inventory ledger/custody/genealogy/PO foundation; staged receiving | External processing lifecycle, ownership/custody, evidence and acceptance test |
| K — closeout 20-unit discrepancy | Material/production ledger facts exist | Shift reconciliation and explicit unresolved exception test |
| L — failure/recovery/communications | Outages, capacity, goals and what-if foundation | General recovery options, notification rules and human decision trace |

## Release rule

Do not mark V3 done until the reconciled requirements are implemented, required migration paths and tests pass, source commits are recorded, and the handoff/session log contain final counts, limitations and deployment configuration. Staged code, a proposed architecture or an available package does not count as a finished increment. Real Nexus/communication provider configuration must be distinguished from local contract tests.
