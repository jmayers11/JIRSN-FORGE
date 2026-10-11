# ADR 0041 — V3.17 priority governance

Status: implemented and verified.

Priority is an Execution-owned order fact that changes the deterministic scheduler's queue order. A user may not change it as an unreviewed field edit. Planning computes a before/after finite-capacity schedule from one consistent unit of work and returns a target-order projection plus the other visible released orders whose projected completions move later. Program-hidden orders are omitted from the response and audit impact list; they remain part of the opaque source fingerprint so the preview still covers the actual schedule inputs.

The preview returns the current priority, proposed priority, `asOf` and fingerprint. The Execution command requires the expected current priority, preview timestamp/fingerprint and an audit reason. It re-computes the impact in the command transaction and rejects previews older than five minutes or whose planning inputs changed. It rechecks the target order and uses the scoped `execution.orders.manage` authority before writing. Cancelled/completed orders cannot be changed. A successful change atomically updates the order, emits `OrderPriorityChanged`, and records the authority, old/new values, fingerprint and visible displaced orders in the audit trail. There is no separate preview persistence table; the source fingerprint is the review token, and the existing command idempotency and audit pipeline remain authoritative.

The scheduler itself now honors configured active kernel holds and Quality's inspection/NCR state while projecting operations. A higher priority changes queue ordering only: blocked work remains unschedulable, and the existing execution authorization/readiness gates remain in force. The shared schedule, goals, Analyst and maintenance-impact projections receive Quality state so they use the same quality rule.

API: `POST /api/orders/{orderNumber}/priority-preview` with `{ "priority": 1 }`; `POST /api/orders/{orderNumber}/priority` with `priority`, `expectedPriority`, `previewAsOf`, `previewFingerprint` and `reason`.

Verification: focused PostgreSQL acceptance passed 2/2 for displaced-order preview and command, changed-input staleness, unauthorized caller denial, and quality/hold blocking. Full `verify.cmd` passed: clean Release build (0 warnings/errors), Planning 266/266, Architecture 32/32, Kernel 33/33, Integration 106/106 (23.0942 minutes). Evidence: `.forge-logs/verify-v317.log` and `.forge-logs/verify-v317.status` (PASS).
