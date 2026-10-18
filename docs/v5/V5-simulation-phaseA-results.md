# V5 full-system simulation: Phase A results

**Run:** 2026-10-17, owner's desktop, `simulate.cmd` → **PASS** (10/10 tests, 351 expectations, 0 failed).
**Scope approved by the owner:** Phase A is test code only; no production code or schema changes.

## What Phase A adds

`tests/Forge.Simulation.Tests` (in `Forge.slnx`; every test tagged `Category=Simulation`):

| Part | What it does |
|---|---|
| `Engine/SimClock` | The host's `TimeProvider`; simulated time only moves forward. Event logs record offsets, never wall-clock times. |
| `Engine/EventLog` | A seeded plan generated before anything executes, written as JSON lines (`events.jsonl`). |
| `Engine/SimApi` | Sends real authenticated commands with idempotency keys and never asserts; refusals are outcomes. Can drop a response to simulate a lost connection. |
| `Engine/Invariants` | After every step: balances reported by Forge equal an independent recomputation of the ledger; no negative stock. At the end: the audit chain is intact. A business-state hash (balances plus the ledger multiset, without ids or timestamps) for reproducibility. |
| `Engine/SimReport` | Expected-versus-actual table per scenario and seed (`results.md`, `results.json`), written to `.forge-logs/simulation/<scenario>/seed-<n>/`. A scenario that checked nothing cannot pass. |
| `Engine/SimFactory` | The reference Slitting plant on its own disposable database (the restored baseline), plus inspector, quality engineer, quality manager, shipper and purchasing manager, and one lot-tracked bought part. Never the live database. |

Real domain services only: every step goes through the HTTP API, the command pipeline and PostgreSQL. Nothing is mocked.

Expected outcomes are computed from the seeded plan and the specification (ADR 0035, the hold rules, the idempotency contract), not from Forge's code.

`verify.cmd` now runs `dotnet test` with `--filter "Category!=Simulation"`. `simulate.cmd` builds and runs only the simulation project and logs to `.forge-logs/simulate.log` and `simulate.status`.

## Results (seeds 7, 42, 1009)

| Scenario | Amendment item | Each seed | What is proven |
|---|---|---|---|
| S09 quality hold scope | Quality hold | 37/37 | A Quality hold on lot L1 stops L1 shipping (422 `on_hold`), while L2 of the same part ships. A material handler cannot release the hold (403); the quality engineer can. L1 then ships. On-hand per lot equals received minus shipped after every step. |
| S10 supplier MRB | Quality / MRB disposition | 99/99 | Four inspected deliveries with partial rejections, in a seeded order: return to vendor (ship, then credit), rework by supplier (RTV marked for rework), scrap, and use as is. Released, Hold and Inspection quantities equal the oracle after every step. Authorization: material handler cannot disposition or credit (403); the deciding engineer cannot approve their own use-as-is (403); the quality manager can. A second disposition is refused (422). No open rejections at the end. |
| S15 network interruption | Network interruption | 23–27 | A receipt whose response is lost, retried 1–3 times with the same key: same receipt id, exactly one ledger receipt. Five concurrent terminals with one key: one receipt, all five get 200 with the same id. The same key with a different body: 409, quantity unchanged. A hold whose response is lost, then retried: exactly one hold. |
| Reproducibility | Reproducibility | 3/3 | S10 seed 42 run twice on two fresh databases: identical event-log text and identical state hash. Seeds 42 and 43 give different plans. |

The S10 seed-42 state hash (`904C1BE0A0B6…`) was the same in three independent runs: the two reproducibility runs and the S10 test.

Every step in every run also passed the conservation, non-negativity and audit-chain invariants.

## Defects found

None in Phase A. Every expectation drawn from the specification matched Forge's behavior.

One test-side fix was needed: analyzer CA1822 on `SimApi.SendAndLoseResponse`, which is now static.

## Limits of Phase A, stated plainly

- These are the first three scenarios on the reference plant, not the 300-order, 70-operator factory. The factory generator, the 30/60/90-day horizons and the remaining disruptive events (machine failure, absenteeism, rush orders, material shortage and the others) are Phase B.
- Production NCR (MRB) dispositions and customer concessions are not covered yet. S10 covers supplier dispositions only.
- Runtime: about 13 minutes for the 10 tests, mostly host start-up and database restore per run.

## Follow-up, same day: negative controls, spec sources, reproducibility

**Run:** 2026-10-17 14:33–14:56, `simulate.cmd` → **PASS** (16/16 tests in 10 minutes). The log is in `.forge-logs/simulate-pass-20261017-controls.log`, and the summary page is `.forge-logs/simulation/summary.md`, also copied to `.forge-logs/simulation-summary-20261017.md`.

### Negative controls: proof that a green run means something

Each control runs a scenario with one deliberate break. The test passes only if the checks aimed at that break fail.

| Control | What is broken | Caught by |
|---|---|---|
| `HoldWrongLot` (S09) | The hold is placed on the other lot | 9 failures: the held lot ships, the free lot is refused, on-hand quantities are wrong |
| `OracleOffByOne` (S10) | The expected-result calculation counts one extra unit as released | 11 inventory failures, at every later stock check |
| `SelfApproveUseAsIs` (S10) | The deciding engineer approves their own use-as-is | 4 failures: approval refused, stock stays on hold |
| `RetryWithFreshKey` (S15) | A lost receipt is retried with a new key | 2 failures: two receipts in the ledger, double quantity |

All four were caught.

### Every check names its source

Each check is tagged with its basis and the rule it tests. Both appear in every `results.md` and in the summary.

| Basis | Checks | Rules |
|---|---|---|
| Spec (ADR 0034, ADR 0035) | 198 | 13 |
| Invariant (amendment: conservation, no negative stock, audit, reproducibility) | 159 | 4 |
| Plan (setup and seeded arithmetic) | 57 | 3 |
| Inferred | 45 | 5 |
| Code only | 37 | 3 |

### Rules for the owner to rule on

Forge already behaves this way and every check passed, but none of these rules is written in an ADR or the README:

1. **Idempotency contract (code only).** The README says only that every command needs an `Idempotency-Key`. Three rules live in a code comment on `IIdempotencyStore`:
   - a retry with the same key and body returns the original result and applies nothing again;
   - a key reused for a different request is refused with 409;
   - both apply to every command.

   Suggest writing these into an ADR.
2. **Concurrent retries (inferred).** Simultaneous requests with one key take effect once. Each caller gets either the result or an in-progress 409. In practice all five callers got 200.
3. **One disposition per rejection (inferred).** A dispositioned supplier rejection refuses a second, different disposition (422).
4. **One rejection per inspection (inferred).** An inspection with a rejected quantity records exactly one supplier rejection.
5. **RTV quantity (inferred).** A return to vendor carries exactly the rejected quantity.
6. **"Open" rejections (inferred).** These are the ones undecided or awaiting use-as-is approval.

### Reproducibility for every scenario

S09, S10 and S15 each ran twice with seed 42 on fresh databases. Each pair produced identical event logs and identical state hashes.

All nine state hashes also equal those from the first Phase A run about two hours earlier.

### Housekeeping

- Event logs now write `+20m` instead of the escaped form `\u002B20m`.
- `simulate.cmd` clears old results before a run, so the summary covers that run only.

### Full gate with the simulation filter

`verify.cmd`, 2026-10-17 14:57–16:38: **PASS**.

| Suite | Result |
|---|---|
| Kernel | 44/44 |
| Planning | 303/303 |
| Architecture | 32/32 |
| PostgreSQL integration | 347/347 |
| UI | 213/213 |
| **Total** | **939 checks** |

The build was clean and includes the simulation project. Under `Category!=Simulation`, the simulation project ran no tests, as intended. The log is `.forge-logs/verify-pass-20261017-sim-filter.log`.

## Owner rulings implemented: ADR 0055, 2026-10-18

The owner ruled on all six open rules. The owner also approved a production change: a supplier rejection is decided
per quantity, and an open return to vendor can be reduced.

**Production change.**
- Migration `inventory/0021_split_supplier_rejections`: a `split_from` column, plus triggers so quantities only go down
  and a return can't change once shipped.
- `DispositionRejection` takes an optional `quantity`.
- New command: `POST /api/inventory/rtvs/{id}/reduce`.
- Receiving screen: a quantity on Disposition, a Reduce action on open returns, and "split from" shown on remainder
  rejections.

The full design is in `docs/adr/0055-v5-owner-rulings-idempotency-and-rejection-quantities.md`.

**Simulation.**
- Every former Inferred or Code-only check now cites ADR 0055. The summary lists **0** rules needing a ruling.
- New scenario **S11 split dispositions**:
  - part of a rejection returned, the open return then reduced;
  - one remainder used as is with a second person's approval, the other scrapped;
  - every rejected unit accounted for in exactly one rejection.
- New negative control `ReleaseReducedPart`: caught, with 8 failures.

**Runs, 2026-10-18.**

| Gate | Result |
|---|---|
| Focused check (`.forge-logs/rerun-split.cmd`) | **PASS**: Release build with 0 warnings; 24/24 targeted integration tests (including 2 new `V5RejectionSplitTests` and the migration counts, now 110); 216/216 UI |
| `simulate.cmd` | **PASS**: 21/21 tests, 662 expectations, 5/5 negative controls caught. Log `.forge-logs/simulate-pass-20261018-adr0055.log`, summary `.forge-logs/simulation-summary-20261018.md` |
| `verify.cmd` (01:33–03:10) | **PASS**: 944 checks: Kernel 44, Planning 303, Architecture 32, integration 349 (347 + 2 new), UI 216 (213 + 3 new). Log `.forge-logs/verify-pass-20261018-adr0055.log` |

The state hashes of S09, S10 and S15 are unchanged from 2026-10-17, so the feature changed no existing behavior.
