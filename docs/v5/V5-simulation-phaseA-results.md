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
