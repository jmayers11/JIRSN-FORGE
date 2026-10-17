# V5-S1 implementation notes

**Date:** 2026-10-16  
**Approval:** The owner approved the proposed V5-S1 after V4 completion. Scope is the Inventory-owned child identity/genealogy integration proof and the smallest owning-domain correction if the test proved the byproduct parent edge was missing. Allocation optimization, forecasting, new MRB work and external connectors remain out of scope.

## Change

The test-first proof confirmed that a byproduct was associated with its production order but did not identify the actual issued parent lots. V5-S1 adds explicit parent records to a byproduct transformation line:

- A lot-tracked or serialized byproduct must name one or more tracked parent part/lot pairs with positive quantities.
- Every parent must exist and have been issued to the same order. Duplicate parents on one output line are rejected.
- Parent quantities are recorded as attribution metadata, not as another material-accounting output. Across byproducts, a parent's attributed quantity cannot exceed the quantity recorded as consumed for that issued lot.
- Untracked, non-byproduct lines keep their earlier behavior; explicit parents are rejected outside a tracked byproduct.
- The genealogy graph retains the existing order-to-byproduct edge and adds a parent-instance-to-child-instance edge for each explicit parent, using the recorded parent quantity.
- Existing byproducts are not backfilled. Their historical physical parents cannot be recovered from the production-order relationship alone.

The new additive migration is [`0020_transformation_line_parents.sql`](../../src/Forge.Inventory/Infrastructure/Migrations/0020_transformation_line_parents.sql). Its append-only parent table references both the transformation line and the source lot identity `(part_number, lot_number)`. It adds no new subsystem or changes to the stock ledger.

## Migration and rollback

Apply migration 0020 through the existing migration runner before serving the updated application. There is no data backfill and no automated down migration. The migration runner treats migration history as immutable.

The supported rollback is application rollback only; leave the additive table, its trigger/index and migration record in place. The previous application ignores the extra table. It also cannot display new parent evidence, so pause tracked-byproduct recording until the compatible application is restored. If schema rollback is required before any parent rows are written, restore a complete pre-0020 database snapshot so schema and migration history stay consistent. If parent rows exist, do not drop or rewrite them; restore a consistent pre-upgrade snapshot or roll the application forward with a repair. Do not edit migration history or run an ad hoc `DROP TABLE` rollback.

## Verification

- `V32Tests` proves a 1000-unit source split into three distinct child lots and resolves each canonical `roll` scan to its exact part, lot and identity kind.
- `V33Tests` proves a two-parent byproduct trace, preserves the order edge, rejects a missing parent, an unissued parent and parent attribution above consumed quantity, and balances consumed/remnant/loss quantities against issued source lots.
- `V41LabelLifecycleTests.Reprinting_a_child_label_keeps_the_same_lot_and_holding_one_child_leaves_its_sibling_released` proves single-parent persistence and trace, scans the actual original and reprint QR artifacts to the same child lot, compares inventory ledger snapshots before and after reprint, and holds one child while its sibling stays released.
- Combined focused command passed **7 tests, 0 failures**:
  `dotnet test tests/Forge.Integration.Tests/Forge.Integration.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~V32Tests|FullyQualifiedName~V33Tests|FullyQualifiedName~V41LabelLifecycleTests"`
- All UI test files passed: **212/212**.
- Full `verify.cmd` did not reach solution build or full tests. Restore failed with `NU1900`/`NU1301` because `api.nuget.org` was unreachable. A cache-only restore with `--ignore-failed-sources -p:NuGetAudit=false` also failed because `Microsoft.Extensions.TimeProvider.Testing` and `NetArchTest.Rules` were unavailable. `.forge-logs/verify.status` records this incomplete full-gate attempt as `FAIL`; the earlier 937-check V4 result is not a V5 full-suite result.

The focused runs loaded local database settings by invoking `forge.local.cmd` inside the test process command. Its contents were not displayed or copied. No commit, push or deployment was made. Physical printer/scanner behavior and production migration/recovery remain untested.
