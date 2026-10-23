# ADR 0058: visual plant floor map and official workforce titles

Accepted, 2026-10-22.

## Context

The Workforce Kanban's horizontal work-center lanes become difficult to scan as a plant grows. Supervisors want a
single responsive view of each work center, its photo and the people currently confirmed on site, with the map arranged
to resemble the plant. Forge already records work-center pictures, assignments and time-bounded presence, but it has no
persisted floor positions or official employee title field.

## Decisions

1. Workforce Kanban has two coordinated views: a four-column staffing board (showing the first sixteen centers in four
   rows on desktop) and an image-backed site map. Both use current assignment and presence records; neither claims to
   track a person's live physical position. The board reflows to three, two and one columns on smaller screens.
2. Each site map uses the supplied cropped factory-floor image until an authorized user uploads that site's map.
   Work-center markers have saved 0–1000 normalized coordinates within each site map. Positions can be moved by drag
   and drop or accessible arrow buttons. A full active-center layout saves in one reason-required command, checks
   collisions, emits center-change events and writes an audit snapshot.
3. `resources.floor-map.manage` is the narrow permission for moving markers and uploading/removing a site map. It is
   granted to FacilitiesManager, ManufacturingEngineer and SecurityAdministrator; a facilities manager also receives
   `workforce.read` and `workforce.supervise` for the staffing view. This does not grant full Plant Setup authority.
4. `workforce.worker.job_title` stores an optional title maintained by staff with `workforce.manage`. Worker photos
   are optional and stored by content hash; `workforce.photos.manage` allows photo changes without broader worker
   administration. FacilitiesManager, ManufacturingEngineer and SecurityAdministrator have this permission. Image
   changes require an audit reason. Images are limited to PNG, JPEG or WebP and 8 MiB. Image reads require
   `resources.read` or `workforce.read`, send `Cache-Control: private, no-store`, and set
   `X-Content-Type-Options: nosniff`.
5. People with current presence are shown under their assigned work center, with name, title, and a photo or initials.
   People without current presence remain available in a collapsed section. A machine assignment is shown when Forge
   has one. This presentation reflects current assignment and presence records, not an indoor-positioning signal.
6. Resource migration `0007_visual_plant_map.sql`, workforce migration `0007_worker_photos.sql` and kernel migration
   `0020_facilities_visual_map_access.sql` add nullable image metadata, marker coordinates and narrow role access;
   earlier nullable position and job-title fields remain in migrations `0006_floor_layout.sql` and
   `0006_official_job_titles.sql`.

## Consequences

- The default staffing board uses a four-column grid without horizontal scrolling. The map uses every active center and
  clearly labels the supplied floor image as a reference until a site-specific image is uploaded.
- A title may be blank until a workforce manager records it; the Kanban labels that case rather than guessing a role.
- The board is an operational assignment view, not a time-clock, indoor-positioning or machine telemetry system.

## Verification

UI tests cover name/title/photo rendering, the four-column board, map markers, role-gated editing, drag and save with a
reason. Integration tests cover Facilities Manager map layout and image access, planner denial, image validation,
worker-photo permission gates and persisted coordinates.
