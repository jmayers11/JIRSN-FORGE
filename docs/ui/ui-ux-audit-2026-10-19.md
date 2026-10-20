# Forge UI/UX review and production-order workspace

## Scope

This source-level review covers every routed page module in `src/Forge.Host/wwwroot/js/pages/`, plus the separate
Receiving screen. It is a review of the shipped interface structure and shared UI helpers; database-dependent visual
checks for every role and record state still require a browser walkthrough against seeded data.

The first implementation from this review is the production-order workspace described below. The app's maintenance
work orders remain a separate workflow for asset checklists, labor, parts and maintenance history; they do not have
manufacturing routings or a bill of materials.

## Implemented: production orders

- The order detail header now presents part/revision, order quantity, due date, priority, status, program/WBS/contract
  context when present, creator and release information.
- The existing workspace now has distinct **Operations** and **Bill of materials** tabs. Before this change those
  two sections shared one grid wrapper, so tab grouping could not separate them.
- The Operations table shows the released routing details and a print action for each operation with applicable,
  controlled Work Instruction documents. Item-wide documents apply to every operation; operation-specific documents
  apply only to their matching sequence. The printed packet reads the exact revision recorded at order release (or
  the currently applicable/pinned revision for an unreleased order), embeds browser-printable files, and offers
  downloads for file types the browser cannot render. Access still follows Engineering read permission and the
  existing document/file authorization endpoints.
- The Production Orders list has a dedicated search for order number, part, revision and status. It works with a
  single visible order and reports the match count. Shared table sorting remains available without a second search
  field.

## Findings for follow-up

1. **Make browse/search behavior consistent on entity queues.** A source scan found shared table search/sort enabled
   in Orders, Inventory, Procurement and Quality. Several other browse surfaces have dense tables but rely on
   page-specific controls or no common filter. Review Engineering, Maintenance, Material Plan, Programs, Projects,
   Scheduling, Shipping, Work Centers, Receiving, Audits/CAPA and Commercial Quality first. Apply search only to
   entity lists; history, evidence and small detail tables should not all receive generic filters automatically.
2. **Give long records a common hierarchy.** Order and Work Center views have tab workspaces. Engineering item/document,
   maintenance asset/work-order, purchase-order and quality/NCR details are natural candidates for a compact fact
   header and tabs when their page sections become long. Keep the identity and current lifecycle state visible while
   switching sections.
3. **Improve dense-table use on small screens.** Review the long engineering, MRP, maintenance and quality tables at
   narrow widths. Prefer keeping critical identity/status columns readable and exposing secondary evidence in a
   row detail or panel, rather than shrinking all columns equally.
4. **Normalize list feedback.** Across browse routes, align result counts, clear empty states, error recovery and
   loading feedback. Preserve route-specific permission and program visibility; a search field must only filter the
   records already returned by the authorized API.
5. **Continue browser accessibility checks per interaction pattern.** Shared focus handling, AA contrast, accessible
   workspace tabs and kiosk controls are already in place. Extend keyboard/screen-reader checks to each newly added
   table filter, record tab and print preview as those pages are upgraded.

## Page inventory reviewed

| Area | Routed screens |
|---|---|
| Plan | Home, Horizon, Programs, Projects, Engineering, Scheduling, Scenarios, Material Plan |
| Make | Production Orders, Work Centers/Tools, Shop Floor, Plant Setup, Workforce/Manage People, Safety Rules, Shift Continuity, Inventory, Receiving, Procurement, Maintenance, Shipping |
| Quality | Quality, Product Acceptance, Investigations, Quality Management, QMS Planning, Audits & CAPA, QMS Reviews, Quality Metrics, Customer & Provider Quality |
| Admin | My Work, Notifications, Data Integrity, Reports, Forge Analyst, Access Control, Settings |

## Validation

`node --test tests/ui/*.test.mjs` passes **262/262** checks, including single-row search, result announcements,
operation-specific versus item-wide work-instruction selection, separate order tabs, and protected file-name handling.
`node --check` passes for all **63** frontend modules and `git diff --check` found no whitespace errors (Git reported
its existing LF-to-CRLF working-copy notices). This UI-only change does not validate backend, database, or seeded-role
browser behavior.
