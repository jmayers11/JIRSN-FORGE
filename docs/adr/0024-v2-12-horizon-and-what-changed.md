# ADR 0024 — V2.12: Horizon's future events and "What changed?"

## Context
V2 §38–39: Horizon should show what lies ahead as well as the schedule, and anyone should be able to ask
"What changed on SL-02?" about one asset, machine, project, document, change or part. Both answers must come
from records. A forecast, an inferred cause or a guessed date would look like knowledge Forge does not have.

## Decisions
1. **One read service, no new tables.** `HistoryService` reads the records other contexts already keep,
   through their contracts:
   - asset history;
   - work orders, downtime and outages;
   - project changes and tasks;
   - document revisions;
   - engineering changes and their reviews;
   - alternates;
   - obsolescence records.

   Every event carries its time, kind, detail, actor (when the record has one), a link (`kind:id`) to the
   record, and the name of the record set it came from. No migration is added.
2. **Resolving a subject.** A code is matched in this order: asset, machine, project, document, change,
   part. `kind` narrows the match. A code that matches nothing is `subject_not_found` (404). Reading needs
   the read permission of the subject's context (maintenance, projects or engineering). Engineering records
   of programs the reader cannot see are left out, and a document or change of such a program is reported
   as not found.
3. **What belongs to a subject.**
   - An asset: its history, its and its machine's work orders, its machine's downtime and outages,
     obsolescence of parts installed in it, and alternates approved for it.
   - A machine: the same, from the machine side.
   - A project: approval, change requests and decisions, completed tasks, and the outages it proposed.
   - A document: revisions as drafted, submitted, approved, released and obsoleted, and the changes that
     carried them.
   - A change: the change's own events, its lines and its decided reviews.
   - A part: revision effective dates, changes, alternates and obsolescence.
4. **History is the past; Horizon is the future.** A planned downtime window that has not started is not a
   change yet, so it is left out of history and shown on Horizon. Future events are only dates stored on
   records:
   - item and document revisions taking effect or ending;
   - document reviews due;
   - change effective dates (requested, until released);
   - alternates starting or ending;
   - last-time-buy, end-of-life and end-of-support dates;
   - proposed and approved outages;
   - planned downtime not created by an outage;
   - open work-order due dates;
   - project end dates and open tasks.

   Each kind is shown only to readers with that context's read permission and program access.
5. **The same answer everywhere.** The drawer (`history.js`), the Analyst ("What changed on SL-02?") and the
   `dated-events` report all call the same service.
   - The parser takes the code after "on/to/for/about/with/of", or else a recognized machine or part. A
     code needs a letter and a digit or dash, so "since 6am" is never read as a subject.
   - Without a subject, "What changed?" still compares approved schedules (Phase 8).

## Consequences
- History is only as complete as the records. Events from before a record existed (for example labor
  qualifications before V2.11) do not appear, and nothing is reconstructed.
- Dates without a time (due dates, effective dates of documents) are placed at the start of the day, UTC.
- The history lists events; it does not explain why they happened. Each link opens the record that does.
