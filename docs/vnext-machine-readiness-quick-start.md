# Machine Readiness Kanban — Quick Start

## Open the board

From **Plant Overview**, choose **Machine Readiness**. The Supervisor navigation group also includes the board for
roles that can read resources, maintenance or operation readiness.

## Read the signals

- **Blocked / Down:** Forge records an active outage or restriction, or an assigned/scheduled operation fails a
  readiness check. Expand the card to see every blocker and its evidence.
- **Attention:** an open PM, inspection or calibration order is overdue, or a recorded planned downtime window is
  coming up. Attention does not mean a machine is locked out; Forge shows a hard lockout in Blocked / Down.
- **Ready:** a linked, eligible production operation passes Forge readiness at the displayed read time.
- **Unknown / Not verified:** there is no current eligible operation, required readiness data is missing, or a source
  read failed. Do not treat Unknown as Ready.

“Ready” is not a machine-running signal. Forge has no live machine telemetry connection in this implementation. The
page reads its sources separately and shows the time it last fetched them; refresh before relying on changed plant
conditions.

## Find the source record

Use the work-center, signal and search filters. Open **Details and related work** to inspect readiness dimensions,
maintenance and schedule context. Use the order, work-center or maintenance links to continue in the authoritative
module. The Kanban is read-only: do not attempt to clear a blocker from this page.

## Current limits

The board does not report machine output/progress, a live running state, causal downstream exception chains, or
configured warning thresholds. No Nexus/time-clock adapter or workforce schedule commit is connected. A failed
operation readiness check is shown with its Forge explanation; it does not create a new maintenance or quality record.
