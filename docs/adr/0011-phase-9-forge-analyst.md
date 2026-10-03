# ADR 0011 — Phase 9: Forge Analyst

## Context
Spec §58–62 and §66–67: Forge Analyst answers manufacturing questions in plain language, shows what
it understood when a request is ambiguous, turns report requests into report definitions, compiles
operational briefs, and may use an optional generative AI provider — which must never become a
permission bypass, an authority on facts, or a requirement (principles 3, 4, 21, 22, 24).

## Decisions
1. **Native, deterministic parsing.** `QueryParser` (in `Forge.Planning.Analyst`) is rule-based and pure:
   intents (§59), Forge's own entity codes and names (orders, machines, work centers, parts, programs,
   people, shifts), relative and absolute periods, "since" times, durations, grouping words and output
   formats. The same question always gives the same interpretation, and it is unit tested.
2. **The vocabulary is the reader's.** The parser only recognizes what the asker may see: programs they
   have access to, orders outside other programs, and people's names only with workforce read access.
3. **Show what was understood.** Every answer carries the interpretation: intent, entities, period, a
   one-line "I understood", a confidence, and any ambiguity ("machine A could be SLITTER-A or PRESS-A").
   Missing or ambiguous entities are asked about, never guessed. Report requests are shown as a report
   request with a Generate action before anything is exported (§60).
4. **Answers come from Forge's services, called as the asker.** Order status from the approved schedule
   and order readiness; machine blockers from the readiness engine; unavailable machines from downtime;
   qualified people from qualifications, shifts and machine requirements; "what changed" from Horizon
   history; affected orders from binding constraints and constraint findings; reports from the reporting
   engine. Each needs the read permission the screens need. If any is missing the answer is "Not
   authorized" with no partial data. Absence reasons are never shown.
5. **Scenarios never touch production state (principle 17).** "What happens if SLITTER-A goes down
   tomorrow?" runs the scheduler twice on one snapshot — as recorded and with the change — and compares
   the plans order by order. "Which orders are affected by Sarah's absence?" compares the recorded plan
   with one where she has no recorded absence. Nothing is saved.
6. **Conversational reporting (§66) maps to the standard catalog.** The parser picks the report, period,
   filters, grouping and output; a period longer than the report allows is clamped and the answer says
   so, as it does for a filter the report does not have. The reporting engine computes everything.
   Two report additions support it: production output by **shift** (column, filter, grouping) and
   **qualifications expiring** (a forward-looking report, default the next 90 days).
7. **Brief me (§67).** A brief for the current or a named shift: orders needing attention, changes since
   the previous shift (including priority changes), constraints and the projected bottleneck in the
   shift, staffing (unavailability without reasons), machines and maintenance, material arrivals,
   quality holds and qualification expiries. Sections the reader may not read are left out and named.
8. **Generative AI is optional and downstream (§61).** `IAnalystNarrator` is disabled by default. When
   configured (`Forge:Analyst:Provider = anthropic`, `ApiKey`, `Model`), it receives only the question
   and the finished, permission-filtered answer, with instructions to restate it without adding facts;
   it has no database access. Its text is labeled and shown above the facts, which stay authoritative.
   It is called only when the person asks for a summary, never for answers they may not see, and any
   failure leaves the answer unchanged.
9. **Permission** `planning.analyst.use` opens Forge Analyst; it is given to the same roles as reports.
10. **CSV exports carry their context** (report, period, filters, generated time) in leading rows, as
    Excel exports already did on their summary sheet (§65).

## Not yet
Charts in answers, scheduled briefs and report subscriptions, multi-turn follow-ups ("and for Paint?"),
and alerting levels (§68).
