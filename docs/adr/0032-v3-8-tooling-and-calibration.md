# ADR 0032 — V3.8: tooling and calibration

## Context
The V3 brief (Z, AA) asks for tools to be treated as resources. A tool has identity, a type, a serial, a location,
custody, a condition, the machines and operations it is eligible for, and a usage count against a limit. Tools
have calibration intervals, history and certificates, and service cycles (a blade out for sharpening). Validity
is judged at the time of use. Until V3.8 Forge knew only Quality's gauges: a code, a due date and a certificate.

## Decisions
1. **Tools live in Resources.**
   - `resources.tool_type` sets the kind (gauge, cutting, fixture, die, mold, other) and the limits shared by its
     tools: calibration interval, usage limit over its life, and uses between services.
   - `resources.tool` holds one tool: serial, place, custody (in house, at a vendor, with a person) and
     custodian, condition (good, worn, damaged), eligible machines and activities (empty means any), usage
     count, uses since service, calibration due and certificate, and status (available, out for service,
     retired).
   - Registering and retiring tools needs `resources.tools.manage`. Calibrating needs
     `resources.tools.calibrate`. Moving, using, sending for service, returning and recording condition need
     `resources.tools.handle`. Sending for service, a condition change and retiring need a reason.
2. **Every change is an event, kept for good.** `resources.tool_event` (append-only) records registration, moves,
   calibrations (passed or failed), uses (with what they were for), service sent and returned, condition and
   retirement. The tool row is the current state; the history is the truth.
3. **Validity is judged at the moment of use.** `ToolValidity.At(tool, type, history, at, machine, activity)`
   replays the history up to that moment. A tool is valid only when all of these hold then:
   - it is registered, not retired and not out for service;
   - it is not damaged;
   - a passing calibration is in force on that day, for gauges and for types with a calibration interval;
   - it is under its usage limit;
   - it is not due for service;
   - it is eligible for the machine and activity.

   `GET /api/resources/tools?at=` answers for any earlier moment.
4. **A service cycle is a send and a return.** Sending for service puts the tool at the vendor. Returning brings
   it back with its condition and sets the uses since service to zero; it may also carry a new calibration.
5. **Uses are recorded where they happen.** A use is checked and counted in the same transaction:
   - `POST /api/resources/tools/{code}/uses` records one directly;
   - a shop-floor start may name its `toolCode`, and is refused when the tool is not valid for that machine and
     activity;
   - an inspection counts a use of its gauge.

   Other contexts do this through `ToolUse.RecordAsync` in the Resources contracts.
6. **Today's gauges move into this model.**
   - Migration `quality/0004` copies each gauge into a tool of the `GAUGE` type, with its calibrations as
     events. A gauge with recorded calibrations starts with no due date until its first copied calibration.
   - Quality's gauge commands and `GET /api/quality/gauges` now read and write tools. Inspections keep their
     codes (`gauge_not_found`, `gauge_out_of_calibration`) and also apply the tool's custody, condition and
     service rules.
   - `quality.gauge` and `quality.calibration` stay as they were and are no longer written.

## Consequences
- The Work Centers page gains a Tools and gauges panel: validity now, with why not; history; calibrate; send for
  service; return; condition.
- The `tool-status` report; the Analyst answers "Which tools are out for service?" and "Which gauges are due for
  calibration?".
- Migrations `resources/0004` and `quality/0004`; migration count 62.
