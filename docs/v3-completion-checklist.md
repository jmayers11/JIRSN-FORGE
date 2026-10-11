# V3 completion checklist

Owner request: complete V3 and record the result. Last checkpoint: 2026-10-11.

This is the release checklist, not a completion declaration. Reconcile it with the base V3 brief and Claude's numbered V3.12–V3.28 plan when supplied. The governance expansion alone cannot prove the original V3 scope is complete.

## Increment verification

| Increment | Current state | Remaining gate |
| --- | --- | --- |
| V3.1–V3.8 | Previously verified and committed through `0232d18`; historical details in AI log | Preserve regression coverage |
| V3.9 | Full verification PASS: Planning 257, architecture 32, kernel 33, integration 81; zero build warnings/errors | Record Git checkpoint, then continue V3.10 |
| V3.10 | Package and additional fixes/tests staged in `.forge-logs/staging-v310` | Apply after V3.9 checkpoint; build, relevant API tests, full verification, commit |
| V3.11 | Package and additional fixes/tests staged in `.forge-logs/staging-v311` | Apply after V3.10 checkpoint; build, relevant API tests, full verification, commit |
| V3.12 | Prior handoff identifies business-record evidence | Implement reusable evidence separate from controlled documents, integrate receipt/inspection/shipment/FAI, verify |
| V3.13–V3.28 | Numbered source plan not supplied yet | Map base requirements and expansion, implement and verify each increment |

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
