# ADR 0006 — Plant setup: editable, industry-neutral plant structure

## Context
Forge was built against a slitting reference plant, but customers bond, wind, laminate, machine and
paint. Engineers need to add and change sites, safety areas, work centers, machines, activity types,
machine requirement profiles and shift assignments themselves, after the plant is live.

## Decisions
1. **Codes are immutable; everything else is editable.** Codes appear on orders, routings, labor and
   audit history. Names, pictures, time zones, descriptions and placement (work center → safety area,
   machine → work center) can change. All edits require ResourcesManage and a reason.
2. **Retire, never delete.** Safety areas, work centers, machines and activity types carry an `active`
   flag (migration `resources/0003_plant_setup`). `Find*`/`Exists` lookups see only active records, so
   nothing new can refer to a retired one; `List*` return everything with the flag, and the planning
   snapshot and the default `GET /api/resources/` leave retired records out.
3. **Nothing in use can be pulled out from under work.** Retiring, moving, or removing a machine's or
   work center's activity asks `IResourceUsage` (a Resources contract implemented in Planning, which
   already composes Execution, Engineering and Workforce): unfinished operations on released or held
   orders, draft or in-force routing revisions, workers' home work centers and current assignments.
   A safety area or work center with active children cannot be retired; a child cannot be reactivated
   under a retired parent. Activity types still set up on a machine or work center cannot be retired.
4. **Moves stay within a site.** A work center keeps its site's shifts and time zone; moving it to
   another site means creating a new work center there.
5. **Pictures are a fixed set.** A work center may choose one of the stage photos or "plain"; with
   none chosen the UI guesses from the name and falls back to a plain texture, never a wrong photo.

## Consequences
Plans and fingerprints computed after this change include the `Active` flag. Shift definitions and
site closures are not yet editable in place (add new ones and reassign); that can follow the same pattern.
