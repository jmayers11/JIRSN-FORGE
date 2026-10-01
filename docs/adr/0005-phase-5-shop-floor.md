# ADR 0005: Phase 5 shop-floor execution, badges, attendance and supervised training

Date: 2026-10-01. Status: accepted as defaults for owner review.

## Four separate records (spec §21, rule 12)

| Concept | Where | Record |
| --- | --- | --- |
| Attendance | Workforce | `workforce.attendance`: clock in / clock out. Clocking in is also presence evidence (12 h, ends at clock-out). |
| Direct labor | Execution | `execution.labor`: person, operation, machine, role (Operator, Trainee, Trainer), start/end. |
| Operation authentication | Execution | The badge that started the labor, checked against presence, qualifications, machine status and safety at that moment. |
| Electronic signoff | Execution | `execution.electronic_signoff`: person, method (Badge or BadgeAndPin), action, subject, meaning, time, terminal. Append-only. |

Payroll is not calculated. A person is on one operation at a time (unique open labor per person).
Labor is not a workforce assignment: assignments stay the supervisor's tool and still drive
current-state safety; labor records who did the work.

## Badges (spec §22)

- Any reader that types the badge value works (barcode, QR, NFC, RFID, keyboard wedge). Values are
  normalized (case, spaces) and stored only as a SHA-256 digest; one active badge per person.
- PINs (4-8 digits) are PBKDF2-SHA256 hashes with a per-badge salt. A person sets their own PIN at a
  terminal; changing it needs the current PIN.
- Badge numbers and PINs are `Secret` values in commands: they serialize as "redacted", so they never
  reach the audit trail, events, idempotency fingerprints or logs (integration-tested).
- Known gap: repeated wrong PINs are rejected and audited but not yet locked out. Terminals should
  rate-limit; a lockout counter is planned with Sentinel integration.

## Terminals

- A terminal is a Forge identity with the `ShopFloorTerminal` role. It records what badged people
  do; the audit actor is the terminal, the person comes from the badge. Supervisors can act as a
  terminal too. Program orders need program access for both the terminal and the badged person.
- Duplicate scans are harmless: a second clock-in or start of the same operation is "no change".

## Starting work

1. Presence: the operator needs current presence evidence (clock-in or supervisor confirmation).
2. Machine: must be eligible for the operation and not down.
3. Safety and machine requirements: the readiness engines evaluate the operator (or, for
   supervised training, the trainer) at the operation before the command runs. A Safety, Equipment
   or RequirementProfile failure refuses the start, and the refusal is audited with the reason. If
   safety cannot be evaluated, work does not start.
4. Qualifications (operator-authorization engine, pure): routing qualifications plus the machine
   activity's operator qualifications.
   - all held and current → start as Operator;
   - missing ones all "in training" → a present, authorized trainer must badge;
   - otherwise → not eligible; contact a trainer, supervisor or training coordinator.

## Trainers (spec §23-25, §80)

- A trainer for qualification Q holds Q and a current Trainer-kind qualification that has Q as a
  prerequisite. Being a qualified operator does not make someone a trainer.
- A supervised session records trainee, trainer, operation, machine, qualifications and times; both
  get labor entries. Ending either ends the session.
- The trainer evaluates with badge + PIN (NeedsMoreTraining or ReadyForAssessment). Nothing is ever
  granted by a session or an evaluation; a certifier still records the qualification.

## Quantities

- Good and scrap per report; scrap needs a reason; cumulative good cannot exceed the order quantity.
- Trainees report under supervision; trainers do not report for themselves.
- "Complete" closes the operation and its open labor. The last operation completing completes the
  order (new status `Completed`). Reports are append-only.
- Scheduling: completed operations drop out; started ones keep the share of planned time matching
  the quantity not yet good (scheduler engine version 2).

## Schedule attainment

- For a period: the operations that the schedule approved and in force at the period's start planned
  to finish inside it; attainment is planned minutes completed by the period's end ÷ planned minutes
  due. Cancelled orders are listed and not counted. No approved plan → no figure (never 0 or 100).
- Factory Horizon shows the last 24 hours. Actual output is good units on final operations per site
  day.
