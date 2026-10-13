# V4 implementation and continuation plan

Started: 2026-10-13, after verified V3 completion. Product Owner approval: “V4 approved to start after V3 is completed.” The original review STOP is historical; implementation approval is active. V3 source is `c879a9e65a15eecd46fd05bab37b29a6016dd6b9`, completion handoff/log `ac2fea3`. V3's 565-test PASS does not prove V4 behavior.

## First implementation step — canonical work identification

Status: **implemented and locally verified, 2026-10-13**. This is the first part of V4.1, not completion of the whole identification/scanning increment. Exact source commit is recorded in `AI_HANDOFF.md`.

Focused gate `.forge-logs/v4-identification-focused.log`/`.status` **PASS: 379 tests** — Planning 303/303, Kernel 33/33, Architecture 32/32 and Integration assembly 11/11 in 2.6525 minutes including cleanup. The eleven cases are four new pure parser facts, five new real API/configuration cases and two retained work-inbox source-authority regressions. Release build has zero warnings/errors in 52.67 seconds. The earlier frozen-Host compile also passed with zero warnings/errors in 2:08.52. All six changed source/test SHA-256 values remained unchanged throughout the focused gate. No dependency, migration or JavaScript change is included; no full V4 release/device/scan workflow verification is claimed.

Add a read-only Host resolver for existing production orders and operation `(order, sequence)` keys. Keep the source-specific codec and kinds in Host, leaving Kernel free of new operational models. Resolve through `IExecutionLookup` and the shared `IWorkSourceResolver`; every current actual resource must pass the existing source read permission. Independently require the operation sequence to exist. Possession of a code supplies neither authentication nor execution authority.

API: authenticated `POST /api/identification/resolve`, body `{ "code": "..." }`.

| Code | Authoritative subject |
| --- | --- |
| `forge:v1:plant-example:order:2048` | Existing production order 2048 |
| `forge:v1:plant-example:operation:2048:10` | Existing operation 10 on order 2048 |

`plant-example` is an example, not a deployed organization assignment. Configure a stable `Forge:Identification:Namespace` explicitly for the deployment. Accepted namespace characters are ASCII letters/digits, period, underscore and hyphen; the first character is alphanumeric and length is 1–64. Matching is ordinal and case-sensitive. Do not infer a namespace or treat it as tenant isolation. Unconfigured/invalid configuration fails closed with `503 identification_not_configured`.

Input is bounded to 256 characters. Positive order/sequence values use canonical decimal notation within Int64/Int32 ranges; leading-zero aliases, whitespace/control characters, arbitrary URLs, unknown kinds/versions and malformed components fail with `400 invalid_identification`. A valid code for another namespace returns `400 foreign_namespace`. Unknown subjects and inaccessible sources share `404 identification_not_found`.

Successful resolution returns only normalized code/namespace/kind, order number, optional sequence, part, revision and recorded status. It does not start work, issue material, create inventory, change a badge/operator, persist a raw attempt, or write a business event/audit/idempotency record. Accepted business transactions continue to use existing commands and their transaction-time/current-replay checks. Resolution is a current preview, never a cached execution grant.

Verified first-step acceptance covers strict decoder boundaries and configured namespace; actual Planned/Released/Held order and Pending/InProgress operation status; nonexistent operations; A-only scoped OrdersRead despite A+B membership; same-code program and permission withdrawal/restoration; registered readers lacking source permission plus unauthenticated/unregistered callers; identical missing/inaccessible responses; repeated resolution; and exact unchanged source/audit/event/idempotency rows across every resolution interval. Normal source commands create and change all business facts; SQL is read-only verification. The source commit and next action are recorded in the handoff.

## Remaining V4.1 work

1. Extend canonical references to material `(part, lot)` and location through owner-owned point lookup contracts. Use recorded lot identity kind; bare lot text cannot identify material uniquely. Define deterministic escaping for keys that contain separators, then cover two parts sharing the same lot text. Do not duplicate the inventory ledger or infer a WIP/remnant identity from a display name.
2. Add optional keyboard/manual scan input and staged selections to existing inventory task and shop-floor flows. Display the resolved subject before the explicit action. Invalidate delayed resolutions and selections on badge switch, idle, lock, disposal and outage. Keep existing Nexus/badge assurance and no-offline consequential behavior.
3. Verify typed scanned material part and lot inside the existing pick command's transaction, preserving legacy inputs and actual stock, location, hold, configuration and authority checks. Repeated scans are allowed; command retry must remain exactly once and recheck current access.
4. Implement label/template/print attempt/alias lifecycle under approved copy, replacement, void and retention policies. Reprint/replacement must reference the same physical subject and preserve history. A printer response is not proof of physical attachment; indistinguishable copied labels remain an explicit physical-validation limit.
5. Add contextual scan provenance linked to authoritative accepted commands; distinguish transient invalid attempts from accepted production/movement/inspection facts. Persist raw attempts only with a defined bounded/redacted retention policy. Add configured scanner adapters and real scanner/printer exercises before device/performance claims.

The first resolver does not complete label Scenario E, scan-to-execute, material identity validation or server-enforced staged-session Scenario L. Those gates remain open until their implementations and actual tests pass.

## Dependency order and decisions

| Work | Dependency / decision boundary |
| --- | --- |
| V4.1 identification foundation | D02: explicit namespace, one organization per deployment. Current source access and existing authoritative keys are required. |
| Optional transient scanning | D05: optional existing keyboard/manual conventions can proceed; mandatory scan policy, camera/device deployment and device attestation remain organization choices. |
| Labels, aliases and persisted attempts | D04 copy/granularity/void policy and D09/D19 retention/volume choices must be explicit before consequential lifecycle and raw-attempt retention. |
| Minimal V4.3 brought forward | Organization-approved profile/scope/process/requirement applicability and effective relevant policies precede dependent execution/release/retention behavior. Do not invent organization approvals. |
| V4.2 execution, release and investigations | Reuse verified V3 transaction/source protections; implement exact inspection criteria/quantity/lot coverage and product-release decisions, retrospective calibration impact and complete investigation traversal with separate acceptance. D06–D08 apply. |
| V4.4 supplier/customer quality | Extend existing procurement/customer-property identities with approved evaluation, loan/consignment and communication policy; D11–D13/D18. |
| V4.5 audit and corrective action | Extend existing CAPA IDs/state compatibly; governed competence/impartiality, investigation/effectiveness and follow-up; D14/D15. |
| V4.6 metrics, review and coverage | Approved cohorts, complete authorized aggregation, review decisions/actions and qualified licensed normative mapping; D01/D16/D17. |

D20 implementation authorization is satisfied by the direct conditional approval after V3 completion. Other D01–D20 policy/source choices remain explicit inputs to the features they govern; they are not a reason to ask again for the same generic implementation approval. Exact ISO clause references remain `RequiresLicensedVerification`, and no certification or complete-conformity claim is made. Namespace configuration provides collision protection, not multi-organization tenancy.

## Working and verification rules

Keep one parent-owned build/test lane and freeze source before each gate. Use additive owner contracts and existing Kernel authorization, audit, outbox and idempotency paths; do not introduce duplicate operational state. Preserve failed logs separately and record the exact verified source commit. No deployment, purchases, outbound customer communication, destructive retention operation or multi-tenant design is authorized by this implementation checkpoint.

Never read, print, copy or commit `forge.local.cmd`; local verification may consume it in memory. Existing free usage resets at exhaustion are authorized, but this chat has no established authenticated redemption mechanism. Keep Claude's next action and actual remaining gates in `AI_HANDOFF.md` and `AI_SESSION_LOG.md`.
