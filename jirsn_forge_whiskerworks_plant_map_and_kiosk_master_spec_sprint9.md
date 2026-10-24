# JIRSN Forge — WhiskerWorks Precision Manufacturing (`WW-P01`), Interactive Plant Map, Supervisor Kanban Editor & Operator Kiosk HMI Master Specification

**Implementation Brief for Codex, with Independent Claude Verification**  
**Status:** Unified Master Specification (Merges the *WhiskerWorks Precision Manufacturing Full Plant Seed & Map Spec*, *Sprint 8 Supervisor Kanban Employee Editor & V8 Polish*, and *Sprint 9 Brand Login, Clean Floor-Plan Asset & Operator Kiosk HMI Redesign*)  
**Primary Objective:** Replace the legacy coil-slitting sample plant with **WhiskerWorks Precision Manufacturing — Purrcision Plant 01 (`WW-P01`)**, a memorable, fully fictional precision-manufacturing site that exercises JIRSN Forge's real `.NET 10` MRP/MES, workforce, quality, maintenance, scheduling, inventory, and what-if engines—while delivering an interactive 15-zone tablet/desktop Plant Map, inline Supervisor Employee Card & IPC Training management, a split-screen Opening Login experience, and a ruggedized forward-facing Shop-Floor Operator Kiosk HMI.

## Executive Summary & Phase 0 V8 Repository Gap Analysis

An inspection of the **JIRSN Forge V8 (`Forge.Host NEWV8`)** codebase (`src/Forge.Host/`) confirms that several foundational primitives required by the WhiskerWorks specification have already been built in V8, while specific seed, map geometry, training-matrix, login, and operator-kiosk gaps remain to be implemented.

| Capability Area | V8 Repository Baseline (`src/Forge.Host/...`) | Current Status in V8 | Required Action in This Master Spec |
|---|---|---|---|
| **Binary Plant Map & Worker Photo Storage** | `Workforce/PlantVisualService.cs`, `Api/PlantVisualEndpoints.cs` (`GET/POST/DELETE /api/workforce/workers/{id}/photo`, `/api/resources/sites/{code}/map/image`, `PUT .../map/work-centers`) | **Implemented** | Reuse directly for WhiskerWorks worker avatars and custom tenant floor-plan uploads. |
| **Authoritative Machine Signal-Light Derivation** | `wwwroot/js/pages/machine-kanban.js` (`deriveMachineCards()`, lines 35–130) | **Implemented** | Export and share `deriveMachineCards()` with `#/workforce-kanban` so the Plant Map displays live `Green` (`ready`/`running`), `Yellow` (`attention`), `Red` (`blocked`), and `Gray` (`unknown`) signal lights derived from real backend readiness, maintenance, and safety rules. |
| **Workforce Kanban & Spatial Floor Map** | `wwwroot/js/pages/workforce-kanban.js` (`renderBoard()`, `renderFloorMap()`, `mapPositionsForCenters()`) | **Partially Implemented** | 1. Replace `sample-plant-floor.png` (which currently has baked-in fake `SL-01 Running 2/2` status text painted into the PNG) with a **clean data-driven 15-zone WhiskerWorks 2D SVG/Canvas floor plan**.<br>2. Update `mapPositionsForCenters()` (`lines 355–365`) from legacy `/slit\|paint/` regexes to the 15 WhiskerWorks zones (`RCV` through `OFF`).<br>3. Add pan/zoom, composable filters, and **Inline Employee Card Management & IPC Training Drawer** (Part II). |
| **WhiskerWorks (`WW-P01`) Deterministic Seed & 16 Scenarios** | `tools/seed-sample-plant.mjs`, `Api/OperationsEndpoints.cs`, `wwwroot/js/pages/scenarios.js` | **Proposed (Needs Implementation)** | Build the deterministic, tenant-scoped WhiskerWorks seed (`WW-CAT-1000` to `WW-CAT-5000`, 15 zones, 20–28 machines, 48–64 workers, IPC/machine qualification matrix, and 16 disruption scenarios) plus the Demo Scenario Control Center (Part I). |
| **Opening Login Screen & Brand Logo Assets** | `wwwroot/js/app.js` (`renderLogin`, lines 125–165), `wwwroot/css/forge.css` (`lines 49–87`), `wwwroot/img/brand/*` | **Needs Redesign** | Fix double-logo vertical stacking (`hero-banner.jpg` + `login-lockup.jpg`) with a responsive **Split-Screen Enterprise Login**, fix white cutout halos on `forge-logo-primary.png` using clean alpha icon + CSS metallic wordmark, and surface a clear `FICTIONAL / SAMPLE DATA — WhiskerWorks WW-P01` badge (Part III). |
| **Forward-Facing Shop-Floor Operator Kiosk** | `wwwroot/js/pages/shopfloor.js` (`lines 1–440`), `wwwroot/js/scan-session.mjs`, `wwwroot/css/forge.css` (`lines 215–232`) | **Partially Implemented** | Upgrade `#/shop-floor` with a 10-foot **Locked Station Attract HUD & Scanner Focus Indicator**, a **Single Shared Touch Numpad + Quick-Add Chips (`+1`, `+5`, `+10`, `Remaining`)**, a **Segmented Progress Bar + Operator Photo + IPC/Safety Pre-Start Check**, **Touchable Job Dispatch Cards**, and a 1-tap **`Machine Down`** button (Part III). |


## Part I — WhiskerWorks Precision Manufacturing (`WW-P01`): Full Fictional Plant, Seeded Operations, IPC Training Matrix & 16 Disruption Scenarios

### 1. Ground Rules & Safety Isolation

1. **Inspect Before Coding:** Inspect the repository, schema, seed tooling, existing sample-plant entities, plant map, authorization, scheduling, workforce, safety, and simulation engines before writing code. Produce a gap analysis and implementation plan.
2. **Reuse Real Engines—Zero Fake Metrics:** Reuse existing domain models, APIs, and services (`Forge.Kernel`, `Forge.Resources`, `Forge.Workforce`, `Forge.Competency`, `Forge.Engineering`, `Forge.Planning`, `Forge.Materials`, `Forge.Execution`, `Forge.Quality`, `Forge.Maintenance`). Do not create a parallel mock ERP, duplicate business rules, or hardcode fake dashboard metrics.
3. **100% Fictional Entities:** All names, companies, personnel, customers, suppliers, addresses, drawings, rates, orders, and identifiers must be invented. Do not import or imitate any actual employer's proprietary information, barcode formats, documents, plant layout, or production data.
4. **Tenant & Demo Reset Safety:** The sample factory must be safely isolated from production tenants. Seed operations must be idempotent, versioned, auditable, and reversible only within the designated demo tenant (`WW-DEMO` / `WW-P01`). Require explicit confirmation (`confirmWithReason`) before reset; never delete real customer data.
5. **Epistemic Transparency (`FICTIONAL / SAMPLE DATA`):** Clearly mark the entire demo **`FICTIONAL / SAMPLE DATA`**. Distinguish simulated values from live observations, and distinguish scenario-only proposals from committed schedule changes.
6. **Backward Compatibility:** Preserve existing Forge functionality and existing user data. Any changes to demo selection or seeding must be backward-compatible and migration-safe.

### 2. Company, Plant Identity & Finished Product Families

* **Fictional Company:** WhiskerWorks Precision Manufacturing
* **Fictional Site:** WhiskerWorks — Purrcision Plant 01 (`WW-P01`)
* **Industry:** Precision-machined and assembled oversized novelty cat toys, with electromechanical products
* **Tagline:** *Absurd products. Serious manufacturing.*

Products must be playfully named and visually recognizable while their routings, constraints, multi-level BOMs, and accounting quantities remain technically rigorous:

| Finished SKU | Product Name | Core Production Route | Key Subassemblies, Raw Materials & Lot-Split Behavior |
|---|---|---|---|
| **`WW-CAT-1000`** | **Titan Feather Wand** — 6-foot modular telescopic wand | `RCV → CNC → FAB → SFT → ASM → TST → PKG → SHP` | CNC-turned 6061-T6 aluminum wand segments (`WW-ALU-BAR-12`), anodized ferrule, kevlar tether cord (cut from bulk spool `WW-ROPE-KEV-500M` via parent-child lot split), synthetic ostrich plume cluster (`WW-PLUME-XL`). |
| **`WW-CAT-2000`** | **Turbo Laser Chase Arena** — motorized obstacle course | `RCV → CNC + ELE → ASM → TST → PKG → SHP` | CNC-milled Delrin ring track, brushed DC gearmotor (`WW-MOT-24V-DC`), Class-1 eye-safe diode module (`WW-LAS-MOD-01`), soldered controller PCB (`WW-PCB-CTRL-200` requiring `IPC J-STD-001` & `IPC-A-610`), wire harness (`WW-HARN-200` requiring `IPC/WHMA-A-620`). |
| **`WW-CAT-3000`** | **Mega Yarn Launcher** — programmable launcher | `RCV → CNC + ELE + SFT → ASM → TST → PKG → SHP` | CNC flywheel housing, dual high-torque launch motors (`WW-MOT-24V-DC`), programmable timer PCB (`WW-PCB-CTRL-300`), braided plush yarn projectiles (wound from bulk yarn cones `WW-YARN-BULK-1000M` via lot split). |
| **`WW-CAT-4000`** | **Sky-Scratcher Tower** — 8-foot modular scratching tower | `RCV → CNC + FAB + SFT → ASM → QAL → PKG → SHP` | Heavy structural baseplate (requires **2-person team lift / 2-qualified-operator safety rule** on `FAB-WELD-01` and `ASM-03`), precision-wrapped sisal rope columns (split from master sisal spool `WW-SISAL-SPOOL-250M`), upholstered perch pads (`WW-FAB-PLUSH-ROLL`). |
| **`WW-CAT-5000`** | **Auto-Box Deployment Station** — mechanized cardboard box dispenser | `RCV → FAB + ELE → ASM → TST → PKG → SHP` | Sheet-metal formed chute (`FAB-BRAKE-01`), optical cat-proximity sensor array (`WW-SENS-PROX-05`), actuator harness (`WW-HARN-500`), energized 24V/120V functional test (`TST-EL-01`). |

Define credible fictional part numbers, revision-controlled BOMs, controlled work instructions (`WI-CNC-101`, `WI-ELE-610`, `WI-ASM-400`), routings, tooling, calibrated gauges, labor standards, machine cycle times, setup times, yields, scrap allowances, lead times, and safety constraints. Do not imply real-world product safety certification.

### 3. Complete Physical Site / Digital Twin (`WW-P01` — 15 Zones)

Build one full fictional site (`WW-P01`) with a logical, traversable floor plan, unique safety-area (`safetyAreaCode`) and work-center (`workCenterCode`) IDs, and deterministic default floor-map coordinates (`mapX`, `mapY` on the `0–1000` coordinate grid used by `PlantVisualEndpoints.cs` and `workforce-kanban.js`):

| Zone Code | Department / Zone Name | Work Center Code(s) | Seeded Machines / Stations | Default Map Coordinates (`mapX`, `mapY`) | Staffing, Safety & Operational Role |
|---|---|---|---|---|---|
| **`RCV`** | **Dock / Receiving** | `WC-RCV` | Dock Doors `RCV-DOCK-01`, `RCV-DOCK-02` (staging bays, no production machine) | `(110, 180)` | Incoming PO receipts, supplier lot barcoding, incoming inspection queue, RTV staging. Forklift requires `QUAL-PIT-FORKLIFT`. |
| **`RAW`** | **Raw Materials Warehouse** | `WC-RAW` | Controlled racks `RAW-RACK-A`, `RAW-SPOOL-CRIB` (storage zone, no production machine) | `(280, 180)` | Bar stock, Delrin, motors, fasteners, master sisal/kevlar/plush spools for parent-child lot splitting. |
| **`CNC`** | **CNC Machining** | `WC-CNC-MILL`, `WC-CNC-LATHE` | `CNC-01` (3-Axis Mill), `CNC-02` (5-Axis Mill), `CNC-03` (Precision Lathe) | `WC-CNC-MILL`: `(480, 180)`<br>`WC-CNC-LATHE`: `(650, 180)` | Separate qualifications for Mills (`QUAL-CNC-MILL`) vs. Lathe (`QUAL-CNC-LATHE`) and Setup/Programming (`QUAL-CNC-SETUP`). Adjoining Tool Crib. |
| **`FAB`** | **Fabrication** | `WC-FAB` | `FAB-SAW-01` ( Band Saw), `FAB-BRAKE-01` (Press Brake), `FAB-WELD-01` (Heavy Frame Joiner), `FAB-DEBURR-01` | `(830, 180)` | `FAB-WELD-01` enforces a **mandatory 2-qualified-person safety rule** (`SAFE-FAB-2P`) for `WW-CAT-4000` tower frames. |
| **`ELE`** | **Electronics Assembly** | `WC-ELE-PCB`, `WC-ELE-HARN` | `ELE-SMT-01`, `ELE-SOLD-01`, `ELE-SOLD-02`, `ELE-HARN-01`, `ELE-PROG-01` | `WC-ELE-PCB`: `(110, 480)`<br>`WC-ELE-HARN`: `(280, 480)` | ESD benches; maps `IPC-A-610`, `IPC J-STD-001` (soldering), and `IPC/WHMA-A-620` (cable/harness) to specific operations. |
| **`SFT`** | **Soft Goods & Spool Cutting** | `WC-SFT` | `SFT-CUT-01` (Rotary Spool Cutter), `SFT-SEW-01` (Industrial Stitcher), `SFT-WRAP-01` (Sisal Post Winder) | `(460, 480)` | Executes parent-child roll/spool splits (rope, fabric, feather plumes) with barcode label issuance and scrap reconciliation. |
| **`KIT`** | **Kitting / Staging** | `WC-KIT` | Kitting lanes `KIT-LANE-01`, `KIT-LANE-02` (non-machine staging zone) | `(640, 480)` | Job-specific kit verification, shortage staging, return-to-stock. Safe independent fallback duty for displaced operators who hold `QUAL-MAT-KIT`. |
| **`ASM`** | **Final Assembly** | `WC-ASM-STD`, `WC-ASM-HVY` | `ASM-01` (Wand/Launcher Line), `ASM-02` (Laser/Box Line), `ASM-03` (Heavy Tower Rig) | `WC-ASM-STD`: `(820, 480)`<br>`WC-ASM-HVY`: `(920, 480)` | `ASM-01`/`02` require 1–2 assemblers + calibrated torque driver; `ASM-03` requires **2 qualified heavy-assembly operators** (`SAFE-ASM-2P`). |
| **`QAL`** | **Quality / Metrology & MRB** | `WC-QAL` | `QAL-CMM-01` (Coordinate Measuring Machine), `QAL-BENCH-01`, `QAL-MRB-CAGE` | `(110, 780)` | Incoming FAI, in-process, final inspection, gauge calibration, MRB quarantine cage. Separates inspection competence from MRB disposition authority. |
| **`TST`** | **Functional Test** | `WC-TST` | `TST-MECH-01` (Cycle/Drop Rig), `TST-EL-01` (Energized Electrical & Laser Bench), `TST-REWORK-01` | `(290, 780)` | `TST-EL-01` requires `QUAL-ELEC-TEST-SAFE` (energized hazard qualification) + calibrated multimeter/optical power meter—IPC training alone never authorizes energized testing. |
| **`PKG`** | **Packaging** | `WC-PKG` | `PKG-LINE-01` (Carton Erector & Weigh Scale), `PKG-LABEL-01` | `(470, 780)` | Carton pack, weight verification, barcode label print/reconciliation. |
| **`SHP`** | **Finished Goods / Shipping** | `WC-SHP` | Outbound Dock `SHP-DOCK-01`, `SHP-PALLET-01` | `(640, 780)` | Put-away, container sealing, two-person Pack & Ship verification against customer demand. |
| **`MNT`** | **Maintenance / Tooling** | `WC-MNT` | `MNT-CRIB-01` (Tool & Calibration Crib), `MNT-BAY-01` | `(800, 780)` | PM plans, LOTO (`QUAL-MNT-LOTO`), spare parts, gauge calibration, documented return-to-service release. |
| **`FAC`** | **Facilities / Utilities** | `WC-FAC` | `FAC-AIR-01` (Compressed Air), `FAC-ESD-MON` (Plant ESD/Power Monitor) | `(910, 780)` | Controlled utility shutdowns and environmental monitoring. |
| **`OFF`** | **Offices / Shift Command** | `WC-OFF` | Supervisor Command Desk, Training Room, Safety Board (administrative zone, no machines) | `(500, 910)` | Shift handoff review, classroom/practical training signoffs, scenario control center. |

### 4. Seeded Enterprise Scale & Mandatory Department Training / IPC Matrix (`Section 11A`)

#### 4.1 Target Seed Scale (`tools/seed-whiskerworks.mjs` / Demo Seeder)

| Data Domain | Seeded Target in `WW-P01` |
|---|---|
| **Plants & Sites** | 1 fully configured site (`WW-P01` — WhiskerWorks Purrcision Plant 01) |
| **Zones / Safety Areas** | 15 (`RCV`, `RAW`, `CNC`, `FAB`, `ELE`, `SFT`, `KIT`, `ASM`, `QAL`, `TST`, `PKG`, `SHP`, `MNT`, `FAC`, `OFF`) |
| **Production & Support Work Centers** | 16 (`WC-RCV`, `WC-RAW`, `WC-CNC-MILL`, `WC-CNC-LATHE`, `WC-FAB`, `WC-ELE-PCB`, `WC-ELE-HARN`, `WC-SFT`, `WC-KIT`, `WC-ASM-STD`, `WC-ASM-HVY`, `WC-QAL`, `WC-TST`, `WC-PKG`, `WC-SHP`, `WC-MNT`) |
| **Machines & Test Stations** | 24 production/test/utility assets with distinct capabilities and PM/calibration intervals |
| **Employees** | 52 fictional workers across Day (`SHIFT-DAY`), Swing (`SHIFT-SWG`), and Weekend/Overlap shifts |
| **Qualification & Training Types** | 18 versioned qualifications (including prerequisite chains, IPC editions, LOTO, PIT/forklift, and machine-specific authorizations) |
| **Finished Product Families** | 5 (`WW-CAT-1000` through `WW-CAT-5000`) with multi-level BOMs and released revisions |
| **Purchased & Manufactured Components** | 95 components (including bulk spools/rolls for parent-child lot splitting, PCBs, harnesses, motors, housings, fasteners) |
| **Suppliers & Customers** | 10 fictional suppliers + 10 fictional customers |
| **Open Sales Orders & Work Orders** | 28 open customer demand lines + 42 planned/released/running/held production orders |
| **Inventory Lots, PMs & Quality Records** | Full lot genealogy (with parent/child split spools), active PM/calibration schedules, incoming FAIs, in-process inspections, open NCRs, MRB holds, and CAPAs |

#### 4.2 Mandatory Safety, Machine Qualifications & IPC Training Matrix

Enforce the full prerequisite chain inside `Forge.Competency` / `Forge.Workforce` / `Forge.Execution`:
`QUAL-SITE-SAFE (Site Orientation & Stop-Work Authority)` → `Department Hazard Training` → `Course / Standard (e.g., IPC-A-610H / J-STD-001H / WHMA-A-620E)` → `Observed Practical Assessment` → `Machine/Operation Independent Authorization`.

| Area / Operation | Seeded Qualification Codes & Required Prerequisite Chain | Enforcement Behavior on Map, Kanban & Operator Kiosk |
|---|---|---|
| **All Plant Personnel** | `QUAL-SITE-SAFE` (Site Orientation, HazCom, PPE, Emergency Evacuation, Ergonomics, Stop-Work Authority) | Baseline prerequisite for all shop-floor presence and activity assignments. |
| **CNC Mills (`CNC-01`, `CNC-02`)** | `QUAL-CNC-MILL` (requires `QUAL-SITE-SAFE` + Guarding/Interlocks + Workholding + Offsets + Practical Signoff) | Operator qualified on `CNC-01`/`02` (`QUAL-CNC-MILL`) is **blocked** if assigned to `CNC-03` Lathe without `QUAL-CNC-LATHE`. |
| **CNC Lathe (`CNC-03`)** | `QUAL-CNC-LATHE` (requires `QUAL-SITE-SAFE` + Chuck/Live-Spindle Guarding + Lathe Practical Signoff) | Distinct from mill qualification; trainees (`InTraining`) require an assigned trainer holding `QUAL-TRAINER-CNC`. |
| **CNC Setup & Programming** | `QUAL-CNC-SETUP` (requires `QUAL-CNC-MILL` or `QUAL-CNC-LATHE` + Toolpath/First-Article Verification) | Restricts setup/program revision signoff and setup activities (`SETUP_CNC`) to authorized setup leads. |
| **Fabrication & 2-Person Joining (`FAB-WELD-01`)** | `QUAL-FAB-CUT`, `QUAL-FAB-JOIN` + Safety Rule `SAFE-FAB-2P` (requires **2 independently qualified `QUAL-FAB-JOIN` operators**) | If 1 of the 2 operators is called out or moved, `FAB-WELD-01` transitions to **`Red / BLOCKED`**; remaining operator is prompted for safe reassignment or kitting. |
| **Electrical / PCB Assembly (`ELE-SMT-01`)** | `QUAL-IPC-A610` (*IPC-A-610 Rev H — Acceptability of Electronic Assemblies* + ESD Control `QUAL-ESD-SAFE`) | Required for PCB population and visual verification; does **not** authorize hand soldering or energized testing. |
| **Soldering & PCB Rework (`ELE-SOLD-01/02`)** | `QUAL-IPC-JSTD001` (*IPC J-STD-001 Rev H — Requirements for Soldered Electrical/Electronic Assemblies*) + `QUAL-SOLD-AUTH` (Site Practical Soldering Assessment) | Holding `QUAL-IPC-A610` or course attendance alone without `QUAL-IPC-JSTD001` + `QUAL-SOLD-AUTH` blocks soldering start (`409 Conflict` / Red readiness). |
| **Cable & Wire Harness (`ELE-HARN-01`)** | `QUAL-IPC-A620` (*IPC/WHMA-A-620 Rev E — Cable and Wire Harness Assemblies* + Crimp Pull-Test Practical) | Required for `WW-HARN-200` and `WW-HARN-500` harness operations; missing `QUAL-IPC-A620` blocks harness ops while allowing authorized kitting/mechanical work. |
| **Electronics Inspection (`WC-QAL`)** | `QUAL-INSP-ELEC` (requires `QUAL-IPC-A610` + `QUAL-IPC-A620` + Independent Inspector Authorization) | Enforces separation of duties: operator who built the assembly cannot sign off final acceptance. |
| **Electrical Functional Test (`TST-EL-01`)** | `QUAL-ELEC-TEST-SAFE` (Electrical Hazard Assessment, Safe Isolation, Voltage Controls, Calibrated Instrument Verification) | **Never assume IPC training authorizes energized testing.** Lacking `QUAL-ELEC-TEST-SAFE` blocks `TST-EL-01`. |
| **Mechanical & Heavy Assembly (`ASM-01..03`)** | `QUAL-ASM-TORQUE` (Calibrated Torque Tool & Pinch-Point Safety) + `QUAL-ASM-HVY` + Safety Rule `SAFE-ASM-2P` on `ASM-03` | `ASM-03` (8-ft Sky-Scratcher Tower) requires 2 `QUAL-ASM-HVY` workers; a trainee (`InTraining`) cannot satisfy the 2-person rule unless an extra trainer is present. |
| **Quality / MRB Disposition** | `QUAL-QAL-INSP` (Inspection Competence & Lot Genealogy) vs. `QUAL-MRB-DISP` (Material Review Board Disposition Authority) | Inspectors can record measurements and place holds, but only `QUAL-MRB-DISP` + `QualityManager` can authorize MRB disposition. |
| **Maintenance & Return-to-Service** | `QUAL-MNT-LOTO` (Hazardous Energy Control / Lockout-Tagout) + `QUAL-MNT-RTS` (Return-to-Service Release) | A supervisor cannot clear a `Red / BLOCKED` maintenance lockout by acknowledging an alarm; requires documented maintenance release. |
| **Forklift / Powered Industrial Trucks** | `QUAL-PIT-FORKLIFT` (Equipment-Specific PIT Evaluation & Active Expiration Date) | Generic warehouse assignment (`WC-RCV` / `WC-RAW`) never implies forklift authorization; expired `QUAL-PIT-FORKLIFT` blocks pallet transport. |

### 5. Demo Scenario Control Center & 16 Deterministic Disruption Scenarios

Add a permission-gated **Demo Scenario Control Center** panel inside `#/scenarios` (and accessible via a top banner button in `WW-P01` demo mode for `SecurityAdministrator`, `Planner`, and `Supervisor` roles) exposing 7 tenant-scoped, idempotent controls:
1. **`Seed Clean Plant`** (`POST /api/demo/whiskerworks/seed` or CLI `node tools/seed-whiskerworks.mjs --reset`)
2. **`Start Shift`** (activates Day Shift roster with 3 confirmed callouts and 2 unconfirmed missing clock-ins)
3. **`Inject Disruption`** (picker for the 16 canonical scenarios below)
4. **`Advance Simulated Time`** (`+15m`, `+1h`, `+4h`, `+1 shift`—clearly labeled `SIMULATED DEMO CLOCK` in the topbar so simulated state is never confused with real-world time)
5. **`Compare Baseline`** (opens side-by-side KPI, readiness, and order delivery delta table against the clean baseline snapshot)
6. **`Reset Demo`** (requires `confirmWithReason` and tenant-check guard `siteCode === 'WW-P01'`)
7. **`Export Evidence`** (downloads a JSON/CSV verification bundle containing baseline state, injected event, affected entities, readiness blocks, and assertion results)

| Scenario # | Scenario Code & Title | Injected Event(s) in `WW-P01` | Expected Authoritative System & UI Behavior |
|---|---|---|---|
| **S01** | `WW-S01-BASELINE` — Healthy Baseline | All 24 machines commissioned, PMs current, full shift present, WIP balanced. | All active machines `Green`; 0 blocking safety/quality exceptions; all released work orders executable. |
| **S02** | `WW-S02-ABSENCES` — Shift Start Absences | 3 confirmed `CallOut` records + 2 unconfirmed missing clock-ins (`Projected` only). | Unconfirmed absences show as `Expected · not clocked in` (never auto-disciplined); affected work centers flag staffing gap on Map & Kanban. |
| **S03** | `WW-S03-TWO-PERSON` — Two-Person Safety Block | 1 of 2 qualified operators on `ASM-03` (`WW-CAT-4000` Sky-Scratcher Tower) is moved or absent. | `ASM-03` immediately transitions to `Red / BLOCKED` (`SAFE-ASM-2P`); UI offers qualified replacement from pool or reassigns remaining worker to safe `WC-KIT` kitting duty. |
| **S04** | `WW-S04-CNC-DOWN` — Unplanned CNC Breakdown | `CNC-01` reports unplanned spindle coolant failure (`Unplanned` downtime). | `CNC-01` turns `Red / STOPPED` with pulsing unacknowledged badge; downstream `WW-CAT-1000` and `WW-CAT-2000` operations show ripple delay in `CauseMap` and `Scenarios`. |
| **S05** | `WW-S05-INSP-SOON` — Upcoming Inspection / PM | `CNC-02` has a PM/calibration due in 4 hours; operator qualification expires in 5 days. | `CNC-02` displays `Yellow / ATTENTION SOON` with exact due timestamp; remains executable until the mandatory window actually expires, then transitions to `Red`. |
| **S06** | `WW-S06-MRB-HOLD` — Motor Lot Quality Hold | Incoming lot `LOT-MOT-24V-881` (`WW-MOT-24V-DC`) fails receiving torque test and enters MRB Quarantine. | Hold propagates automatically through pegged work orders for `WW-CAT-2000` and `WW-CAT-3000`; operations cannot start until MRB disposition or approved alternate lot is selected. |
| **S07** | `WW-S07-SHORTAGE` — Bearing & Sensor Shortage | Supplier shipment for `WW-SENS-PROX-05` is delayed by 4 days. | `WC-KIT` and `WW-CAT-5000` orders flag `Material Shortfall` on Factory Horizon KPI #5 and MRP Exceptions tab with 1-click `Alternates` / `Open POs` action. |
| **S08** | `WW-S08-LOT-SPLIT` — Master Spool Lot Split & Hold | 250m master sisal spool `LOT-SISAL-M01` is split at `SFT-CUT-01` into child lots `M01-A` (100m), `M01-B` (100m), `M01-C` (45m) + 5m scrap. | Parent-child genealogy tree reconciles 100% of quantity; placing a hold on parent `M01` propagates hold visibility to all unshipped child lots and `WW-CAT-4000` towers. |
| **S09** | `WW-S09-ECN-REV` — Engineering Revision Release | `WW-PCB-CTRL-200` releases `Rev B` (`ECN-2026-04`) updating work instruction `WI-ELE-610 Rev B`. | Operators trained only on `WI-ELE-610 Rev A` are blocked from starting `Rev B` orders until controlled document training is recorded in the Training Drawer. |
| **S10** | `WW-S10-RUSH-ORDER` — Rush VIP Customer Order | Priority-1 rush order for 50× `WW-CAT-2000` inserted inside finite capacity horizon. | Priority preview modal and `Scenarios` show exact displaced orders (`AtRisk`/`Late`) and evaluate overtime / second-mill routing tradeoffs without inventing capacity. |
| **S11** | `WW-S11-CAL-EXPIRED` — Out-of-Calibration Gauge & PM | Torque wrench `GAUGE-TRQ-04` and optical power meter `GAUGE-OPT-01` pass calibration due time. | Affected inspection and assembly operations on `ASM-01` and `TST-EL-01` block immediately; operator kiosk prevents selecting the expired gauge. |
| **S12** | `WW-S12-SCAN-OUTAGE` — Scanner / Label Printer Outage | Simulated network timeout / 503 during barcode staging and label print attempt. | Terminal fail-closed state preserves typed modal entries without duplicate transactions; label print attempt enters `PendingReconciliation` requiring human observation signoff. |
| **S13** | `WW-S13-IPC-MISMATCH` — IPC & Machine Qualification Blocks | Covers the 12 mandatory Section 11A test cases (e.g., `IPC-A-610` worker assigned to `IPC J-STD-001` soldering; `CNC-01` mill operator assigned to `CNC-03` lathe; trainee assigned without trainer). | Server and UI reject every unauthorized assignment (`409` / `Red` readiness block) and display the exact missing course/practical authorization or eligible trainer path. |
| **S14** | `WW-S14-RECOVERY-BOARD` — Recovery Action Board | Supervisor creates recovery actions with owners, due dates, and blocked dependencies after a disruption. | Actions appear on the Recovery / Shift Continuity board with traceable completion evidence and audit records. |
| **S15** | `WW-S15-HANDOFF` — Shift Handoff Continuity | Day Shift supervisor generates shift pass-on snapshot with open `CNC-01` downtime and unconfirmed absences. | Swing Shift supervisor and incoming kiosk operators see the reviewed pass-on banner and acknowledge receipt without exposing private HR absence reasons. |
| **S16** | `WW-S16-CASCADE` — Multi-Event Cascade | Simultaneous callout on `ASM-03` + `CNC-01` breakdown + `LOT-MOT-24V-881` quality hold. | Plant Exceptions panel deduplicates root causes, ranks constraint chains in `CauseMap`, and validates multi-move recovery proposals before commit. |


## Part II — Interactive Plant Overview Map, Clean Floor-Plan Asset & Inline Supervisor Kanban Editor (Sprint 8 + WhiskerWorks Map Integration)

### Task 8.1 — Inline Employee Management Bar & Training/IPC Qualifications Drawer on `#/workforce-kanban` (`wwwroot/js/pages/workforce-kanban.js` & `wwwroot/css/forge.css`)

**Why This Is Critical for Both the User Request & WhiskerWorks Section 11A:**  
When a Supervisor or Manager clicks an employee card on **`#/workforce-kanban`** (either in the 4×4 Staffing Board, the Planning Rail, or a Plant Map zone pin), `cardHtml` (`workforce-kanban.js:437–463`) currently only stages a hypothetical move. To resolve WhiskerWorks training/IPC gaps, upload a worker photo, edit their official job title, confirm clock-in presence, record a callout, or commit a live move, the supervisor needs direct action buttons right inside the expanded employee card.

#### Step 8.1A — Update Imports in `wwwroot/js/pages/workforce-kanban.js` (`lines 1–4`)

```javascript
import { command, fetchFile, readPost, removeVisualImage, tryGet, uploadVisualImage } from '../api.js';
import { esc, $, $$, errorBlock, emptyBlock, fmtDateTime, toast, confirmWithReason, drawer } from '../ui.js';
import { canAction, formModal } from '../forms.js';
import { stagePhoto } from '../icons.js';
import { deriveMachineCards } from './machine-kanban.js';
```

#### Step 8.1B — Add `renderQuickManageBar(worker, move)` to `cardHtml(worker, lane)` (`wwwroot/js/pages/workforce-kanban.js:437–463`)

```javascript
  function renderQuickManageBar(worker, move) {
    const canTitle = canAction(ctx.me, 'workforce.manage');
    const canPhoto = canAction(ctx.me, 'workforce.photos.manage');
    const canSupervise = canAction(ctx.me, 'workforce.supervise');
    const canTraining = ['competency.read', 'competency.manage', 'competency.certify'].some(p => canAction(ctx.me, p));
    const canApplyMove = canSupervise && move && !moveIncomplete(move);

    return `<div class="staff-card-manage" role="group" aria-label="Manage ${esc(worker.displayName)}">
      <div class="staff-card-manage-head">Manage employee</div>
      <div class="staff-card-manage-actions">
        ${canTraining ? `<button class="btn small" type="button" data-emp-training="${esc(worker.personId)}">Training &amp; IPC quals</button>` : ''}
        ${canTitle ? `<button class="btn small" type="button" data-emp-title="${esc(worker.personId)}">Edit title</button>` : ''}
        ${canPhoto ? `<label class="btn small staff-photo-upload-btn">${worker.hasPhoto ? 'Replace photo' : 'Upload photo'}<input type="file" accept="image/png,image/jpeg,image/webp" data-emp-photo-upload="${esc(worker.personId)}" aria-label="Upload photo for ${esc(worker.displayName)}"></label>` : ''}
        ${canPhoto && worker.hasPhoto ? `<button class="btn small ghost" type="button" data-emp-photo-remove="${esc(worker.personId)}">Remove photo</button>` : ''}
        ${canSupervise && !confirmedOnSite(worker) ? `<button class="btn small" type="button" data-emp-presence="${esc(worker.personId)}">Confirm on site</button>` : ''}
        ${canSupervise ? `<button class="btn small" type="button" data-emp-unavailable="${esc(worker.personId)}">Record callout / PTO</button>` : ''}
        ${canApplyMove ? `<button class="btn small primary" type="button" data-emp-apply-move="${esc(worker.personId)}">Apply live assignment</button>` : ''}
        <a class="btn small ghost" href="#/workforce?personId=${encodeURIComponent(worker.personId)}">Open full profile →</a>
      </div>
    </div>`;
  }
```

Inside `cardHtml(worker, lane)` (`line 461`), inject `${renderQuickManageBar(worker, move)}` inside the `expanded` container:

```javascript
      ${expanded ? `<div class="staff-card-proposal-controls">${controls}<div class="staff-card-scenario-row"><a class="btn small" href="#/scenarios?kind=PersonUnavailable&amp;personId=${encodeURIComponent(worker.personId)}&amp;scenarioName=${encodeURIComponent(`Model absence · ${worker.displayName}`)}">Model absence scenario</a></div>${renderQuickManageBar(worker, move)}</div>` : ''}
```

#### Step 8.1C — Add Inline Handlers & Interactive Training/IPC Drawer (`wwwroot/js/pages/workforce-kanban.js`)

Add these methods inside `workforceKanbanPage(page, ctx)` and bind `[data-emp-training]`, `[data-emp-title]`, `[data-emp-photo-upload]`, `[data-emp-photo-remove]`, `[data-emp-presence]`, `[data-emp-unavailable]`, and `[data-emp-apply-move]` inside `wireCards()` (preserving role-based separation of duties so a supervisor can request training or assign a trainer, while only `competency.certify` can grant a certification with documented evidence):

```javascript
  function editWorkerTitle(worker) {
    formModal({
      title: `Set ${worker.displayName}'s official title`,
      detail: 'Displayed beneath their name across Workforce Kanban and the WhiskerWorks plant map. Leave blank to clear.',
      confirmLabel: 'Save title',
      reasonRequired: true,
      fields: [
        { name: 'jobTitle', label: 'Official title', value: worker.jobTitle ?? '', placeholder: 'IPC-610 Lead Electronics Assembler' },
      ],
    }, (v, reason) => command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/job-title`, { jobTitle: v.jobTitle || null }, reason), () => load());
  }

  function confirmWorkerPresence(worker) {
    formModal({
      title: `Record ${worker.displayName}'s presence`,
      detail: 'Only clocked-in or supervisor-confirmed presence counts as on site. Unconfirmed missing punches never trigger automatic disciplinary actions.',
      confirmLabel: 'Record presence',
      reasonRequired: true,
      fields: [
        { name: 'kind', label: 'Basis', type: 'select', value: 'SupervisorConfirmed', options: [['SupervisorConfirmed', 'Supervisor confirmed on site'], ['ClockedIn', 'Clocked in'], ['Projected', 'Projected (planning only)']] },
        { name: 'validUntil', label: 'Valid until (optional)', type: 'datetime-local' },
      ],
    }, (v, reason) => command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/presence`, {
      kind: v.kind,
      validUntil: v.validUntil ? new Date(v.validUntil).toISOString() : null,
    }, reason), () => load());
  }

  function recordWorkerUnavailable(worker) {
    const now = new Date();
    const end = new Date(now.getTime() + 8 * 3600 * 1000);
    const toLocalInput = d => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
    formModal({
      title: `Record ${worker.displayName} unavailable`,
      detail: 'Records a call-out, PTO, or training window on the planning rail without exposing private HR reasons on shared displays.',
      confirmLabel: 'Record unavailability',
      reasonRequired: true,
      fields: [
        { name: 'kind', label: 'Kind', type: 'select', value: 'CallOut', options: [['CallOut', 'Call-out'], ['Unavailable', 'Unavailable'], ['ApprovedPto', 'Approved time off'], ['Training', 'Training']] },
        { name: 'startsAt', label: 'From', type: 'datetime-local', value: toLocalInput(now), required: true },
        { name: 'endsAt', label: 'To', type: 'datetime-local', value: toLocalInput(end), required: true },
      ],
    }, (v, reason) => {
      const startsAt = v.startsAt ? new Date(v.startsAt).toISOString() : null;
      const endsAt = v.endsAt ? new Date(v.endsAt).toISOString() : null;
      if (!startsAt || !endsAt || endsAt <= startsAt) throw new Error('Enter a start and a later end.');
      return command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/unavailability`, { kind: v.kind, startsAt, endsAt }, reason);
    }, () => load());
  }

  async function applyLiveMove(worker) {
    const move = state.moves.get(worker.personId);
    if (!move || moveIncomplete(move)) return;
    if (move.unassign) {
      const ok = await confirmWithReason({
        title: `End assignment for ${worker.displayName}`,
        detail: `${worker.displayName} will be unassigned from ${worker.assignment?.workCenterCode ?? 'their current work'}.`,
        confirmLabel: 'End live assignment',
        reasonRequired: true,
      }, reason => command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/assignment/end`, {}, reason));
      if (ok) { state.moves.delete(worker.personId); await load(); }
      return;
    }
    const ok = await confirmWithReason({
      title: `Assign ${worker.displayName} to ${move.workCenterCode}`,
      detail: `Server-side validation will verify machine readiness, safety staffing, and IPC/machine qualifications for: ${[move.activityCode, move.machineCode, move.workCenterCode].filter(Boolean).join(' · ')}.`,
      confirmLabel: 'Apply live assignment',
      reasonRequired: true,
    }, reason => command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/assignment`, {
      safetyAreaCode: move.safetyAreaCode || null,
      workCenterCode: move.workCenterCode || null,
      machineCode: move.machineCode || null,
      activityCode: move.activityCode,
      asTrainee: Boolean(move.asTrainee),
    }, reason));
    if (ok) { state.moves.delete(worker.personId); await load(); }
  }

  async function openEmployeeTrainingDrawer(worker) {
    const canManageQual = canAction(ctx.me, 'competency.manage');
    const canCertify = canAction(ctx.me, 'competency.certify');
    const statusTone = { InTraining: 'info', Qualified: 'ok', Suspended: 'warn', Revoked: 'bad', Expired: 'bad' };

    const [qualsRes, typesRes, docTrainRes] = await Promise.all([
      tryGet(`/api/competency/people/${encodeURIComponent(worker.personId)}`),
      tryGet('/api/competency/qualification-types'),
      tryGet(`/api/engineering/document-training?personId=${encodeURIComponent(worker.personId)}`),
    ]);
    const types = typesRes.data ?? [];
    const typeName = code => types.find(t => t.code === code)?.name ?? code;
    const typeOptions = () => types.map(t => [t.code, `${t.name} (${t.code})`]);
    const quals = qualsRes.data ?? [];
    const docRecords = (docTrainRes.data?.records ?? []).filter(r => !r.personId || r.personId === worker.personId);

    const box = drawer(`Training, IPC & machine qualifications · ${worker.displayName}`, `
      <div class="stack" id="emp-training-drawer">
        <div class="row">
          <div><b>${esc(worker.displayName)}</b> <span class="muted">· ${esc(worker.jobTitle || 'Official title not set')}</span><div class="faint">Home work center: ${esc(worker.homeWorkCenterCode ?? '—')} · Shift: ${esc(worker.shiftCode ?? '—')}</div></div>
          <div class="inline">
            ${canManageQual ? '<button class="btn small primary" type="button" id="drawer-start-training">+ Start training</button>' : ''}
            ${canCertify ? '<button class="btn small" type="button" id="drawer-certify">+ Certify (Authorized Certifier)</button>' : ''}
            ${canManageQual ? '<button class="btn small" type="button" id="drawer-doc-training">+ Document training</button>' : ''}
          </div>
        </div>
        <section class="panel">
          <header><h3>Machine, safety &amp; IPC qualifications (${quals.length})</h3></header>
          ${qualsRes.error ? errorBlock(qualsRes.error, 'qualifications') : !quals.length ? '<div class="muted">No qualifications recorded for this employee yet.</div>' : `
          <table class="table"><thead><tr><th>Qualification / Standard</th><th>Status</th><th>Certified</th><th>Review / Expires</th><th></th></tr></thead><tbody>
            ${quals.map(q => `<tr>
              <td><b>${esc(typeName(q.code))}</b> <span class="mono faint">${esc(q.code)}</span></td>
              <td><span class="pill ${statusTone[q.status] ?? 'none'}">${esc(q.status)}</span></td>
              <td>${q.certifiedAt ? esc(fmtDateTime(q.certifiedAt)) : '—'}</td>
              <td>${q.expiresAt ? esc(fmtDateTime(q.expiresAt)) : '—'}</td>
              <td class="row-actions">
                ${canCertify && q.status === 'InTraining' ? `<button class="btn small" type="button" data-drawer-qual="certify" data-code="${esc(q.code)}">Certify</button>` : ''}
                ${canCertify && q.status === 'Qualified' ? `<button class="btn small ghost" type="button" data-drawer-qual="suspensions" data-code="${esc(q.code)}">Suspend</button>` : ''}
                ${canCertify && (q.status === 'Qualified' || q.status === 'Suspended') ? `<button class="btn small ghost" type="button" data-drawer-qual="revocations" data-code="${esc(q.code)}">Revoke</button>` : ''}
              </td>
            </tr>`).join('')}
          </tbody></table>`}
        </section>
        <section class="panel">
          <header><h3>Controlled work-instruction training (${docRecords.length})</h3></header>
          ${docTrainRes.error ? errorBlock(docTrainRes.error, 'document training') : !docRecords.length ? '<div class="muted">No controlled document training recorded for this employee.</div>' : `
          <table class="table"><thead><tr><th>Document</th><th>Revision</th><th>Trained on</th></tr></thead><tbody>
            ${docRecords.map(r => `<tr><td class="mono">${esc(r.documentNumber)}</td><td class="mono">${esc(r.revision)}</td><td>${esc(r.trainedOn ?? '—')}</td></tr>`).join('')}
          </tbody></table>`}
        </section>
      </div>
    `);
    /* Bind #drawer-start-training, #drawer-certify, #drawer-doc-training, and [data-drawer-qual] as specified in Sprint 8 Task 8.1C */
  }
```


### Task 8.2 — Interactive WhiskerWorks 2D Floor Plan, Signal Lights & Clean Reference Map Asset (`wwwroot/js/pages/workforce-kanban.js`, `wwwroot/img/plants/sample-plant-floor.png`)

**Problem in V8 Today:**
1. `wwwroot/img/plants/sample-plant-floor.png` has static status boxes (`SL-01 Running 2/2`, `SL-02 Inspection Due 1/2`, `SL-03 Stopped 0/2`, `AS-01 Short Staff`) permanently painted into the background PNG image, colliding with live DOM pins and violating WhiskerWorks Section 4.2 (*"Machine status must be derived from authoritative readiness and safety rules, not manually painted onto the UI"*).
2. `mapPositionsForCenters()` (`workforce-kanban.js:355–365`) only checks legacy coil-plant regexes (`/slit/`, `/wind|paint/`).
3. `.plant-map-pin` currently shows only the work-center name and worker avatars—it does not show derived **Machine Signal Lights (`Green / Yellow / Red / Gray`)**, pan/zoom controls (`Fit Plant`, `Reset View`), or composable layer filters (`Machines`, `Staffing`, `Orders`, `Materials`, `Quality`, `Safety`, `Maintenance`, `Bottlenecks`).

#### Step 8.2A — Update `mapPositionsForCenters()` in `wwwroot/js/pages/workforce-kanban.js` (`lines 355–367`) for WhiskerWorks (`WW-P01`)

```javascript
    const defaults = center => {
      const code = String(center.code ?? '').toUpperCase();
      const name = `${center.code} ${center.name}`.toLowerCase();
      const wwMap = {
        'WC-RCV': { mapX: 110, mapY: 180 },
        'WC-RAW': { mapX: 280, mapY: 180 },
        'WC-CNC-MILL': { mapX: 480, mapY: 180 },
        'WC-CNC-LATHE': { mapX: 650, mapY: 180 },
        'WC-FAB': { mapX: 830, mapY: 180 },
        'WC-ELE-PCB': { mapX: 110, mapY: 480 },
        'WC-ELE-HARN': { mapX: 280, mapY: 480 },
        'WC-SFT': { mapX: 460, mapY: 480 },
        'WC-KIT': { mapX: 640, mapY: 480 },
        'WC-ASM-STD': { mapX: 810, mapY: 480 },
        'WC-ASM-HVY': { mapX: 915, mapY: 480 },
        'WC-QAL': { mapX: 110, mapY: 780 },
        'WC-TST': { mapX: 290, mapY: 780 },
        'WC-PKG': { mapX: 470, mapY: 780 },
        'WC-SHP': { mapX: 640, mapY: 780 },
        'WC-MNT': { mapX: 800, mapY: 780 },
        'WC-FAC': { mapX: 915, mapY: 780 },
        'WC-OFF': { mapX: 500, mapY: 910 },
      };
      if (wwMap[code]) return wwMap[code];
      if (/rcv|receiv|dock/.test(name)) return { mapX: 110, mapY: 180 };
      if (/raw|warehouse/.test(name)) return { mapX: 280, mapY: 180 };
      if (/lathe/.test(name)) return { mapX: 650, mapY: 180 };
      if (/cnc|mill|slit/.test(name)) return { mapX: 480, mapY: 180 };
      if (/fab|weld|brake/.test(name)) return { mapX: 830, mapY: 180 };
      if (/pcb|solder/.test(name)) return { mapX: 110, mapY: 480 };
      if (/harn|cable|ele/.test(name)) return { mapX: 280, mapY: 480 };
      if (/soft|sft|spool|textile/.test(name)) return { mapX: 460, mapY: 480 };
      if (/kit|stag/.test(name)) return { mapX: 640, mapY: 480 };
      if (/assembl|asm/.test(name)) return { mapX: 810, mapY: 480 };
      if (/inspect|quality|qal|mrb/.test(name)) return { mapX: 110, mapY: 780 };
      if (/test|tst/.test(name)) return { mapX: 290, mapY: 780 };
      if (/pack|pkg/.test(name)) return { mapX: 470, mapY: 780 };
      if (/ship|shp/.test(name)) return { mapX: 640, mapY: 780 };
      if (/maint|mnt|tool/.test(name)) return { mapX: 800, mapY: 780 };
      if (/fac|util/.test(name)) return { mapX: 915, mapY: 780 };
      if (/office|command|off/.test(name)) return { mapX: 500, mapY: 910 };
      return null;
    };
```

#### Step 8.2B — Replace Burned-In `sample-plant-floor.png` with Clean Architectural Vector/Raster Floor Plan & Live Signal Lights

1. **Clean Floor-Plan Background (`renderFloorPlanSvg()` + Clean `sample-plant-floor.png`)**:
   - Replace `/img/plants/sample-plant-floor.png` with a clean dark-graphite architectural floor plan containing **only** structural bay walls, aisle markings, dock doors, directional material-flow arrows (`RCV → RAW → CNC/FAB/ELE/SFT → KIT → ASM → QAL/TST → PKG → SHP`), and subtle zone bay codes (`RCV`, `RAW`, `CNC`, `FAB`, `ELE`, `SFT`, `KIT`, `ASM`, `QAL`, `TST`, `PKG`, `SHP`, `MNT`, `FAC`, `OFF`)—with **zero** baked-in machine status pills or worker headcounts.
2. **Authoritative Machine Signal Lights on Map Pins (`workforce-kanban.js`)**:
   - In `load()` inside `workforce-kanban.js`, also fetch the readiness/maintenance inputs used by `deriveMachineCards()` (or share a cached helper) so each `.plant-map-pin` renders the worst-case authoritative signal light for the machines in that work center:
     - **`Green (READY / RUNNING)`**: `<span class="machine-signal-dot ready" title="All machines ready/running">✓</span>` (label explicitly distinguishes `RUNNING` when an active operation is clocked in vs. `READY`).
     - **`Yellow (ATTENTION SOON)`**: `<span class="machine-signal-dot attention" title="Upcoming PM, inspection, or qualification expiry">!</span>`
     - **`Red (STOPPED / BLOCKED)`**: `<span class="machine-signal-dot blocked is-pulsing" title="Blocked by downtime, overdue PM, quality hold, or missing safety/IPC qualification">■</span>` (respecting `prefers-reduced-motion: reduce`).
     - **`Gray (UNKNOWN / OFFLINE)`**: `<span class="machine-signal-dot unknown" title="Telemetry stale, offline, or unverified">?</span>`
   - Clicking any machine inside an expanded map pin opens a **Machine Readiness & Qualification Drawer** showing: current signal light and provenance timestamp, assigned operator(s), required safety staffing (e.g., `2/2 required for SAFE-ASM-2P`), required IPC/machine courses, open maintenance/calibration status, current production order, and 1-click **`Move Employee` / `Find Qualified Coverage` / `Analyze Impact`** buttons wired to the shared `state.moves` Kanban proposal state.

### Task 8.3 — Dark-Mode WCAG 2.1 AA Contrast Fixes & Machine Kanban KPI Strip (`wwwroot/css/forge.css`, `wwwroot/js/pages/machine-kanban.js`, `wwwroot/js/pages/workforce.js`, `wwwroot/js/pages/workforce-admin.js`, `wwwroot/js/executable.js`)

Apply all remaining Sprint 8 polish items so V8's Kanban and Map surfaces pass WCAG 2.1 AA (`≥ 4.5:1`):
1. **WCAG 2.1 AA Dark-Surface Status Colors (`wwwroot/css/forge.css`, lines 845 & 899–901)**:
   ```css
   .staff-presence-tag { color: #4ADE80; font-weight: 650; }
   .machine-status.bad { color: #FCA5A5; }
   .machine-status.warn { color: #FCD34D; }
   .machine-status.ok { color: #4ADE80; }
   ```
2. **Machine Readiness KPI Strip & Lane Count Pills (`wwwroot/js/pages/machine-kanban.js`, lines 204 & 230)**:
   Render the 4-card `.machine-board-kpis` summary (`Blocked / Down`, `Attention`, `Ready`, `Unknown / Not verified`) and `<span class="pill ${lane.tone}">${items.length}</span>` in each lane header.
3. **Deep-Link Auto-Selection (`#/workforce?personId=...`) & Searchable Document Training Picker (`pages/workforce.js`, `pages/workforce-admin.js`, `executable.js`)**:
   Forward `ctx.query.get('personId')` into `appendWorkforceAdmin(page, ctx.me, ctx.query.get('personId'))` so clicking `Open full profile →` from any Kanban or Plant Map card selects and scrolls to that employee automatically, and replace the raw UUID text box in `appendDocumentTraining()` with a directory `<select>`.


## Part III — Sprint 9: Opening Brand Identity, Split-Screen Login & Forward-Facing Shop-Floor Operator Kiosk HMI

### Task 9.1 — Split-Screen Enterprise Login, Clean Alpha Hawk Logo & Demo Tenant Banner (`wwwroot/js/app.js` & `wwwroot/css/forge.css`)

**Problems in V8 Today (`wwwroot/js/app.js:125–165`, `wwwroot/css/forge.css:49–87`):**
1. **Double-Logo Vertical Stacking:** `renderLogin()` stacks a `1100px`-wide `<img class="login-hero" src="/img/brand/hero-banner.jpg">` above `.login-card`, and then renders `<img class="login-lockup" src="/img/brand/login-lockup.jpg">` a second time inside `.login-card`. On 1366×768 laptops and 1024×768 tablets, the logo is duplicated twice and the `Sign in` button is pushed below the fold.
2. **White Cutout Halos on `forge-logo-primary.png`:** The sidebar image `forge-logo-primary.png` has a 1–2px jagged white matte fringe around the hawk wingtips and `JIRSN` sub-letters against `--sidebar (#12161B)`. Meanwhile, `forge.css:53–61` already defines clean CSS rules for `.brand .word small` (`JIRSN`) and `.brand .word b` (`FORGE` metallic text gradient).
3. **Missing Demo Environment Indicator:** WhiskerWorks Section 1 requires clearly marking the demo `FICTIONAL / SAMPLE DATA` so evaluators never confuse demo state with a live customer plant.

#### Step 9.1A — Replace Stacked `renderLogin()` in `wwwroot/js/app.js` (`lines 125–165`) with a Split-Screen Enterprise Sign-In + 1-Click Kiosk Launcher

```javascript
async function renderLogin() {
  const mode = await loadAuthMode();
  const oidcError = consumeOidcError();
  const detail = mode.mode === 'oidc'
    ? 'Sign in with your organization account through JIRSN Nexus.'
    : 'Development sign-in is enabled for this WhiskerWorks (WW-P01) evaluation environment.';
  const form = mode.mode === 'oidc'
    ? `<button class="btn primary" id="oidc-login" type="button">Sign in with JIRSN Nexus</button>`
    : `<div class="field"><label for="u">Role or username (e.g. admin, planner, supervisor, inspector, kiosk)</label><input class="input" id="u" value="admin" autocomplete="username"></div>
       <div class="login-actions-row">
         <button class="btn primary" type="submit">Sign in to Forge</button>
         <button class="btn ghost" type="button" id="quick-kiosk-login" title="Sign in as ShopFloorTerminal and launch full-screen operator kiosk">Launch Operator Kiosk →</button>
       </div>`;
  app.innerHTML = `<main class="login"><div class="login-split">
    <section class="login-showcase" aria-label="JIRSN Manufacturing Suite">
      <div class="login-demo-badge" role="note">FICTIONAL / SAMPLE DATA · WhiskerWorks Purrcision Plant 01 (WW-P01)</div>
      <img class="login-hero" src="/img/brand/hero-banner.jpg" alt="JIRSN Forge — Plan. Synchronize. Produce. Deliver.">
      <div class="login-suite-pills" aria-label="JIRSN Architecture">
        <span class="pill info">JIRSN Forge · MRP / MES / QMS</span>
        <span class="pill none">JIRSN Nexus · Zero-Trust Identity</span>
        <span class="pill none">JIRSN Sentinel · Cryptographic Audit</span>
      </div>
    </section>
    <form class="login-card" id="login">
      <div class="login-brand-head">
        <img class="login-hawk-mark" src="/img/brand/forge-logo-icon.png" alt="">
        <div class="word"><small>JIRSN</small><b>FORGE</b></div>
      </div>
      <h1>Manufacturing Operations Sign-In</h1>
      <p class="muted">${esc(detail)}</p>
      ${form}
      <div class="notice bad ${oidcError ? '' : 'hidden'}" id="err" role="alert" aria-live="assertive">${esc(oidcError ?? '')}</div>
    </form>
  </div></main>`;
  if (mode.mode === 'oidc') {
    $('#oidc-login')?.addEventListener('click', () => startOidcLogin().catch(err => {
      $('#err').textContent = err.message; $('#err').classList.remove('hidden');
    }));
    return;
  }
  $('#u')?.focus();
  $('#quick-kiosk-login')?.addEventListener('click', async () => {
    try {
      await devLogin('kiosk');
      session.me = await loadMe();
      await loadDirectory();
      location.hash = '#/shop-floor';
      boot();
    } catch (err) { $('#err').textContent = err.message; $('#err').classList.remove('hidden'); }
  });
  $('#login').addEventListener('submit', async e => {
    e.preventDefault();
    try {
      await devLogin($('#u').value.trim());
      session.me = await loadMe();
      await loadDirectory();
      boot();
    } catch (err) { $('#err').textContent = err.message; $('#err').classList.remove('hidden'); }
  });
}
```

#### Step 9.1B — Add Split-Screen Login & Clean Brand Lockup CSS (`wwwroot/css/forge.css`)

```css
/* Split-Screen Enterprise Login & Clean Hawk Brand Lockup */
.login {
  min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 28px;
  background: radial-gradient(circle at 30% 25%, rgba(249,115,22,.16), transparent 55%),
              linear-gradient(rgba(10,13,17,.74), rgba(10,13,17,.92)),
              url(/img/brand/bg-factory-wide.jpg) center / cover no-repeat, var(--bg);
}
.login-split {
  display: grid; grid-template-columns: 1.25fr 0.95fr; gap: 28px; align-items: center;
  width: min(1180px, 96vw);
}
.login-showcase {
  display: flex; flex-direction: column; gap: 14px;
}
.login-demo-badge {
  display: inline-flex; align-items: center; align-self: flex-start; gap: 8px;
  padding: 5px 12px; border-radius: 4px; font: 700 11.5px var(--font-display);
  letter-spacing: .08em; text-transform: uppercase;
  background: rgba(245, 158, 11, 0.18); border: 1px solid var(--warn); color: #FCD34D;
}
.login-hero {
  width: 100%; max-height: 360px; object-fit: cover; border-radius: 8px;
  border: 1px solid rgba(249,115,22,.35); box-shadow: 0 22px 60px rgba(0,0,0,.75);
}
.login-suite-pills { display: flex; flex-wrap: wrap; gap: 8px; }
.login-card {
  width: 100%; max-width: 440px; justify-self: center;
  background: linear-gradient(180deg, rgba(28,34,41,.95), rgba(18,22,27,.98));
  border: 1px solid var(--line-2); border-top: 3px solid var(--forge-orange);
  border-radius: 8px; padding: 26px 28px; box-shadow: 0 24px 64px rgba(0,0,0,.75);
}
.login-brand-head {
  display: flex; align-items: center; gap: 12px; margin-bottom: 14px;
  padding-bottom: 12px; border-bottom: 1px solid var(--line);
}
.login-hawk-mark { width: 48px; height: 48px; object-fit: contain; filter: drop-shadow(0 4px 10px rgba(249,115,22,.3)); }
.login-brand-head .word small { display: block; font-size: 11px; letter-spacing: .24em; color: var(--forge-orange); font-weight: 700; }
.login-brand-head .word b {
  font: 800 26px/1 var(--font-display); letter-spacing: .08em;
  background: linear-gradient(180deg, #fff 0%, #cbd5e1 55%, #94a3b8 100%);
  -webkit-background-clip: text; -webkit-text-fill-color: transparent;
}
.login-actions-row { display: flex; flex-wrap: wrap; gap: 10px; margin-top: 6px; }
@media (max-width: 960px) {
  .login-split { grid-template-columns: 1fr; max-width: 540px; }
  .login-hero { max-height: 220px; }
}
```


### Task 9.2 — Forward-Facing Shop-Floor Operator Kiosk HMI Redesign (`wwwroot/js/pages/shopfloor.js` & `wwwroot/css/forge.css`)

**Why `#/shop-floor` Needs These 5 Upgrades:**
1. **Locked Attract Screen Is Too Small (`shopfloor.js:206–213`):** Currently shows a plain page header and a `72px` icon. Operators walking up to a VESA-mounted touch screen need a 10-foot **Station HUD** with a live **Site Clock**, **Scanner Focus Status Pill** (green when `#badge` has keyboard focus, amber *"Tap screen to arm badge scanner"* when blurred), and a **Fictional Demo Badge Quick-Select Strip** (when in `WW-P01` demo mode so evaluators without a physical USB barcode scanner can tap a seeded WhiskerWorks operator badge in 1 click!).
2. **Dual 24-Button Numpad Stacking (`shopfloor.js:248–254`):** Currently renders two full 12-button `.kiosk-numpad` grids simultaneously (one under `#good`, one under `#scrap`), pushing `Work instructions`, `Material & remnants`, and `Stop work` off the bottom of a 1024×768 screen.
3. **Missing Visual Progress Bar, Operator Photo & Safety/IPC Readiness Checklist (`shopfloor.js:241–246`):** Progress is buried in small grey text (`420.000 of 1,000.000 good so far`). Operators need a bold segmented progress bar (`Good` green, `Scrap` red, `Remaining` steel), 4 numeric readout cards (`TARGET`, `GOOD`, `SCRAP`, `REMAINING`), and their circular photo from `/api/workforce/workers/{id}/photo`.
4. **Dense 6-Column HTML Table for "Work You Can Start" (`shopfloor.js:273–284`):** Replace or augment the queue table with glove-friendly **Job Dispatch Cards** with large machine selector pills, `Scheduled for you` badges, and clear IPC/safety block explanations when an operation is not executable.
5. **Missing 1-Tap `Machine Down` Button & Touchable Scrap Reason Tiles (`shopfloor.js:251–261`):** Operators can flag a quality hold or NCR, but cannot report their machine down (`POST /api/maintenance/downtime`) from `#/shop-floor`, and have to use a tiny `<select id="reason">` for scrap reasons.

#### Step 9.2A — Upgrade the Locked Attract Screen with Live Scanner Focus Indicator & Demo Badge Quick-Tap (`wwwroot/js/pages/shopfloor.js:206–218`)

```javascript
    if (!terminal.person) {
      page.innerHTML = `${head}
        <section class="panel kiosk-attract-hud">
          <div class="kiosk-attract-top">
            <div>
              <span class="eyebrow">SHOP-FLOOR EXECUTION TERMINAL</span>
              <h2>Scan Operator Badge to Begin</h2>
              <p class="muted">Scan your physical badge with the station wedge scanner, or enter your badge code and press Enter.</p>
            </div>
            <div class="kiosk-focus-pill is-armed" id="badge-focus-status" role="status" aria-live="polite">● Scanner Ready — Waiting for Badge Scan</div>
          </div>
          ${scanBox('badge', 'Employee badge barcode', 'Scan badge barcode…', 'Use badge')}
          <div class="kiosk-safety-reminder">
            <span class="pill info">Safety &amp; IPC Gate Active</span>
            <span class="muted small">Machine guarding, two-person safety rules, calibrated gauges, and IPC-A-610 / J-STD-001 / WHMA-A-620 authorizations are verified automatically before operation start.</span>
          </div>
        </section>`;
      bindTopControls();
      const badgeInput = $('#badge', page);
      const focusStatus = $('#badge-focus-status', page);
      const updateFocusIndicator = () => {
        if (!focusStatus) return;
        const focused = document.activeElement === badgeInput;
        focusStatus.className = `kiosk-focus-pill ${focused ? 'is-armed' : 'is-blurred'}`;
        focusStatus.textContent = focused
          ? '● Scanner Armed — Ready for Badge Scan'
          : '▲ Tap Here to Arm Barcode Scanner';
      };
      badgeInput?.addEventListener('focus', updateFocusIndicator);
      badgeInput?.addEventListener('blur', updateFocusIndicator);
      focusStatus?.addEventListener('click', () => badgeInput?.focus());
      badgeInput?.focus();
      updateFocusIndicator();
      bindScan('badge', code => identify(code));
      return;
    }
```

#### Step 9.2B — Replace Dual Numpads with Single Active Touch Keypad + Quick-Add Chips + Touchable Scrap Reason Tiles (`wwwroot/js/pages/shopfloor.js:126–142 & 241–263`)

Maintain `let activeQtyField = 'good';` in `shopfloorPage` state. Render **one** shared `.kiosk-shared-keypad` that routes digits and quick-increment taps (`+1`, `+5`, `+10`, `Remaining`) to whichever input (`#good` or `#scrap`) is currently selected, while keeping `[data-numpad-target="good"]` and `[data-numpad-target="scrap"]` attributes intact so existing UI tests pass without modification:

```javascript
  function sharedKeypadHtml(remainingQty) {
    const rem = Math.max(0, Number(remainingQty ?? 0));
    return `<div class="kiosk-keypad-panel" role="group" aria-label="Touch quantity entry keypad">
      <div class="kiosk-keypad-target-bar">
        <span class="muted small">Keypad target:</span>
        <button class="btn small primary" type="button" data-focus-qty="good" id="target-btn-good">Good quantity</button>
        <button class="btn small ghost" type="button" data-focus-qty="scrap" id="target-btn-scrap">Scrap quantity</button>
      </div>
      <div class="kiosk-quick-chips" role="group" aria-label="Quick quantity increments">
        <button class="btn small" type="button" data-qty-add="1">+1</button>
        <button class="btn small" type="button" data-qty-add="5">+5</button>
        <button class="btn small" type="button" data-qty-add="10">+10</button>
        <button class="btn small" type="button" data-qty-add="25">+25</button>
        ${rem > 0 ? `<button class="btn small" type="button" data-qty-fill="${rem}">Fill remaining (${qty(rem)})</button>` : ''}
      </div>
      <div class="kiosk-numpad" id="shared-numpad" data-active-target="good">
        ${['7','8','9','4','5','6','1','2','3','0','.'].map(key =>
          `<button class="btn" type="button" data-numpad-target="good" data-shared-key="${key}" data-numpad-key="${key}">${key}</button>`
        ).join('')}
        <button class="btn ghost" type="button" data-numpad-target="good" data-shared-key="clear" data-numpad-key="clear">Clear</button>
      </div>
    </div>`;
  }
```

And inside the active work panel (`shopfloor.js:241–263`):
1. Compute `const targetQty = Number(w.orderQuantity ?? 0);`, `const goodSoFar = Number(w.goodQuantity ?? 0);`, `const scrapSoFar = Number(w.scrapQuantity ?? 0);`, `const remainingQty = Math.max(0, targetQty - goodSoFar);`, and `const pctGood = targetQty > 0 ? Math.min(100, Math.round((goodSoFar / targetQty) * 100)) : 0;`.
2. Render the **Segmented Progress Bar & 4 Readout Tiles** (`Target`, `Good so far`, `Scrap`, `Remaining`) at the top of the `Now working` panel:
   ```javascript
   <div class="kiosk-progress-strip" role="region" aria-label="Order completion progress">
     <div class="kiosk-progress-bar" aria-hidden="true">
       <div class="kiosk-progress-good" style="width:${pctGood}%"></div>
     </div>
     <div class="kiosk-metrics-row">
       <div class="kiosk-metric"><span class="k">Target</span><b class="v">${qty(targetQty)}</b></div>
       <div class="kiosk-metric ok"><span class="k">Good so far</span><b class="v">${qty(goodSoFar)}</b></div>
       <div class="kiosk-metric ${scrapSoFar > 0 ? 'warn' : ''}"><span class="k">Scrap so far</span><b class="v">${qty(scrapSoFar)}</b></div>
       <div class="kiosk-metric"><span class="k">Remaining</span><b class="v">${qty(remainingQty)}</b></div>
     </div>
   </div>
   ```
3. Alongside `#reason` (`<select class="select" id="reason">`), render touchable **Scrap Reason Quick-Tiles** (`data-scrap-tile="${r.code}"`) that highlight and set `#reason.value = r.code` on tap so gloved operators never have to open a small native OS dropdown.
4. In the `.kiosk-quick-actions` bar (`shopfloor.js:255–260`), add a 1-tap **`Report machine down`** button (`id="kiosk-machine-down"`) when `w.machineCode && canAction(ctx.me, 'maintenance.downtime.manage')` (or `execution.shopfloor.record`):
   ```javascript
   ${w.machineCode ? `<button class="btn danger" type="button" id="kiosk-machine-down">Report machine down (${esc(w.machineCode)})</button>` : ''}
   ```
   Binding `#kiosk-machine-down` opens `formModal` posting `{ machineCode: w.machineCode, kind: 'Unplanned', reason: v.reason }` to `/api/maintenance/downtime`, immediately turning that machine's signal light **`Red / STOPPED`** on the Machine Readiness Kanban and WhiskerWorks Plant Map!

#### Step 9.2C — Kiosk HMI Styles in `wwwroot/css/forge.css`

```css
/* Sprint 9 Operator Kiosk HMI Upgrades */
.kiosk-attract-hud {
  border-top: 3px solid var(--forge-orange);
  padding: 22px 24px;
}
.kiosk-attract-top {
  display: flex; align-items: flex-start; justify-content: space-between; gap: 16px; flex-wrap: wrap;
  margin-bottom: 14px;
}
.kiosk-focus-pill {
  padding: 10px 16px; border-radius: 999px; font: 700 13px var(--font-display);
  letter-spacing: .04em; cursor: pointer; user-select: none;
}
.kiosk-focus-pill.is-armed {
  background: rgba(21, 128, 61, 0.22); border: 1px solid #4ADE80; color: #4ADE80;
}
.kiosk-focus-pill.is-blurred {
  background: rgba(245, 158, 11, 0.22); border: 1px solid #FCD34D; color: #FCD34D;
}
.kiosk-safety-reminder {
  display: flex; align-items: center; gap: 10px; flex-wrap: wrap;
  margin-top: 14px; padding-top: 12px; border-top: 1px solid var(--line);
}
.kiosk-progress-strip { margin: 12px 0 16px; }
.kiosk-progress-bar {
  height: 12px; background: #11161C; border: 1px solid var(--line-2);
  border-radius: 999px; overflow: hidden; margin-bottom: 10px;
}
.kiosk-progress-good {
  height: 100%; background: linear-gradient(90deg, #16A34A, #4ADE80);
  transition: width .25s ease;
}
.kiosk-metrics-row {
  display: grid; grid-template-columns: repeat(4, minmax(110px, 1fr)); gap: 10px;
}
.kiosk-metric {
  background: var(--panel-2); border: 1px solid var(--line); border-radius: var(--radius);
  padding: 8px 12px;
}
.kiosk-metric .k { display: block; font-size: 11px; color: var(--muted); text-transform: uppercase; letter-spacing: .06em; }
.kiosk-metric .v { font: 700 18px/1.2 var(--font-display); color: var(--text); }
.kiosk-metric.ok .v { color: #4ADE80; }
.kiosk-metric.warn .v { color: #FCD34D; }
.kiosk-keypad-panel {
  background: var(--panel-2); border: 1px solid var(--line); border-radius: var(--radius);
  padding: 12px;
}
.kiosk-keypad-target-bar, .kiosk-quick-chips {
  display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-bottom: 10px;
}
```


## Part IV — Connected Kanban Boards, Delivery Plan & Required Codex / Claude Verification Handoff

### 1. Connected Kanban Boards & Shared State Integrity (Section 8 of WhiskerWorks Brief)

Use shared IDs, authoritative status models, and server-side validation across Forge's Kanban boards:
1. **Workforce Kanban & Spatial Plant Map (`#/workforce-kanban`) — Phase 1–3 Priority:** 4×4 compact work-center grid + 15-zone WhiskerWorks interactive floor plan, touch select-and-move, inline Employee Card Editor & IPC Training Drawer, safety warnings, and explicit Scenario vs. Live Commit distinction.
2. **Machine Readiness Kanban (`#/machine-kanban`) — Phase 1–3 Priority:** 4 lanes (`Blocked / Down`, `Attention`, `Ready`, `Unknown / Not verified`) powered by `deriveMachineCards()` with KPI count strip, lane count pills, and WCAG AA high-contrast status labels.
3. **Production Orders, Materials/Kitting, Quality/MRB, Maintenance, Training/Qualifications, Engineering Change & Recovery Boards:** Wire incrementally using existing endpoints (`/api/orders/`, `/api/inventory/tasks`, `/api/quality/ncrs`, `/api/maintenance/work-orders`, `/api/competency/qualification-types`, `/api/engineering/changes`, `/api/execution/shifts/handoffs`). Do not force unsupported boards into the first release if backend services require new schema; label any deferred board views transparently in the handoff matrix.

### 2. Phased Delivery Plan for Codex & Claude

* **Phase 0 — Discovery & Baseline Verification:** Run `node --test tests/ui/*.test.mjs` and `dotnet test` to baseline V8; verify existing `workforce-kanban.js`, `machine-kanban.js`, and `PlantVisualEndpoints.cs`.
* **Phase 1 — WhiskerWorks (`WW-P01`) Deterministic Seed & IPC/Safety Matrix:** Build `tools/seed-whiskerworks.mjs` (and/or demo seed endpoint) populating the 15 zones, 16 work centers, 24 machines, 52 employees, 18 qualifications (`IPC-A-610H`, `IPC J-STD-001H`, `IPC/WHMA-A-620E`, `CNC-01` vs. `CNC-03`, `SAFE-FAB-2P`, `SAFE-ASM-2P`, `QUAL-ELEC-TEST-SAFE`, `QUAL-PIT-FORKLIFT`, `QUAL-MNT-LOTO`), 5 cat-toy product families (`WW-CAT-1000..5000`), multi-level BOMs, parent-child spool lots, and initial work orders.
* **Phase 2 — Interactive WhiskerWorks Plant Map & Inline Employee Card Editor (Sprint 8 + Map):** Implement Tasks 8.1, 8.2, and 8.3 in `workforce-kanban.js`, `machine-kanban.js`, `workforce.js`, `workforce-admin.js`, `executable.js`, and `forge.css`, replacing the baked-in status boxes on `sample-plant-floor.png` with a clean 15-zone floor plan and live derived signal lights.
* **Phase 3 — Opening Split-Screen Login & Operator Kiosk HMI Redesign (Sprint 9):** Implement Tasks 9.1 and 9.2 in `app.js`, `shopfloor.js`, and `forge.css`.
* **Phase 4 — Scenario Control Center & 16 Seeded Disruptions:** Implement the Demo Scenario Control Center (`S01` through `S16`) with baseline comparison and evidence export.
* **Phase 5 — Automated Verification & Independent Claude Audit:** Run unit, integration, UI, and seeded scenario assertion suites.

### 3. Mandatory Acceptance & Regression Test Checklist (32 Assertions)

Codex must verify—and Claude must independently audit—that all 32 acceptance checks pass:

#### A. WhiskerWorks Seed, BOM & Genealogy Integrity (Checks 1–7)
1. Deterministic/idempotent WhiskerWorks (`WW-P01`) seed and repeatable reset scoped strictly to the demo tenant.
2. Multi-level BOM quantities and inventory conservation across all 5 cat-toy product families (`WW-CAT-1000` to `WW-CAT-5000`).
3. Parent-child lot splitting on bulk spools (`WW-SISAL-SPOOL-250M` / `WW-ROPE-KEV-500M`) with barcode label issuance and 100% quantity reconciliation.
4. Order routing, work-center calendars, staffing, and finite-capacity constraints derived from real Forge engines.
5. Map ↔ Machine Kanban ↔ Worker Card ↔ API state consistency after page refresh and simulated events.
6. Offline/reconnect or 5xx retry preserves modal form inputs and never duplicates work, inventory, or attendance transactions.
7. Zero real employee, customer, or vendor data or proprietary labels anywhere in the seed or UI; `FICTIONAL / SAMPLE DATA` badge visible on Login and Demo views.

#### B. Machine Signal Lights, Safety Rules & IPC Qualification Enforcement (Checks 8–22)
8. `Green / Yellow / Red / Gray` signal-light derivation and precedence; stale/missing telemetry is `Gray`, never `Green` by default.
9. `Red / BLOCKED` machine cannot start work via the Operator Kiosk, Orders UI, or direct API call.
10. Acknowledging an alarm stops the visual pulse (`prefers-reduced-motion` respected) but **never** clears an underlying blocking safety, maintenance, or quality condition.
11. CNC trainee (`InTraining`) assigned to `CNC-01` without independent authorization cannot start work alone; supervised trainer path is displayed.
12. CNC operator qualified for `CNC-01` Mill (`QUAL-CNC-MILL`) is blocked from operating `CNC-03` Lathe (`QUAL-CNC-LATHE`).
13. CNC setup/program edit attempted by an operator without `QUAL-CNC-SETUP` is rejected.
14. Electronics assembler holding `IPC-A-610` (`QUAL-IPC-A610`) but lacking `IPC J-STD-001` (`QUAL-IPC-JSTD001`) + practical soldering authorization (`QUAL-SOLD-AUTH`) is blocked from starting soldering on `ELE-SOLD-01`.
15. Cable assembler lacking `IPC/WHMA-A-620` (`QUAL-IPC-A620`) is blocked from harness operation on `ELE-HARN-01` while remaining eligible for other authorized duties.
16. IPC course recorded without site-specific practical soldering authorization (`QUAL-SOLD-AUTH`) blocks independent soldering work.
17. Electrical test technician lacking `QUAL-ELEC-TEST-SAFE` is blocked from energized testing on `TST-EL-01` even if they hold all IPC assembly credentials.
18. Forklift / pallet transport task requested for a warehouse worker without current `QUAL-PIT-FORKLIFT` is rejected.
19. Two-person safety operation (`FAB-WELD-01` or `ASM-03`) loses 1 of 2 qualified operators → machine immediately blocks (`Red`); remaining worker can be reassigned or given independently validated kitting work (`WC-KIT`).
20. Qualification or inspection nearing expiry shows `Yellow / ATTENTION SOON`; once expired/revoked, affected operations transition to `Red / BLOCKED`.
21. Maintenance return-to-service requires documented `QUAL-MNT-RTS` release; a supervisor cannot manually toggle a red machine to green.
22. Missing clock-in remains `Expected · not clocked in` until verified; no automatic disciplinary or payroll action is generated.

#### C. Supervisor Kanban Editor, Opening Login & Operator Kiosk HMI (Checks 23–32)
23. Clicking any employee card on `#/workforce-kanban` (Board, Rail, or Plant Map pin) reveals permission-gated **Manage employee** actions (`Training & IPC quals`, `Edit title`, `Upload/Remove photo`, `Confirm on site`, `Record callout / PTO`, `Apply live assignment`, `Open full profile →`).
24. Opening `Training & IPC quals` drawer allows starting training, recording document training, or (with `competency.certify`) certifying/suspending/revoking with audit reasons and evidence.
25. Clicking `Open full profile →` (`#/workforce?personId=...`) auto-selects and scrolls to that employee in `Manage people`.
26. `.machine-status.bad/warn/ok` and `.staff-presence-tag` achieve `≥ 8.9:1` WCAG 2.1 AA contrast on dark surfaces, and `#/machine-kanban` displays the 4-card KPI count strip and lane count pills.
27. Clean `sample-plant-floor.png` / SVG floor plan has **zero** burned-in raster status cards and aligns all 15 WhiskerWorks zones (`RCV` through `OFF`).
28. Opening Login (`renderLogin()`) uses the responsive split-screen layout with a single clean brand lockup, `FICTIONAL / SAMPLE DATA` badge, and 1-click `Launch Operator Kiosk →` button.
29. Locked Operator Kiosk (`#/shop-floor`) displays the 10-foot Station Attract HUD with live **Scanner Armed / Tap to Arm** focus indicator.
30. Active Operator Kiosk displays the segmented completion progress bar, 4 numeric readout tiles (`TARGET`, `GOOD`, `SCRAP`, `REMAINING`), and a **single shared Touch Numpad** with `+1`, `+5`, `+10`, `+25`, and `Fill remaining` quick chips.
31. Active Operator Kiosk provides touchable **Scrap Reason Tiles** and a 1-tap **`Report machine down`** button that logs unplanned downtime and updates the Plant Map and Machine Kanban.
32. All 16 seeded WhiskerWorks disruption scenarios (`S01`–`S16`) execute deterministically and pass their automated assertion sets.

### 4. Required Codex Handoff Deliverables

Upon completion, Codex must provide:
1. **Changed-File & New-File Inventory** across `src/`, `tools/`, and `tests/`.
2. **Schema / Migration Summary** (confirming whether all changes fit existing tables or documenting any additive migrations).
3. **WhiskerWorks Seed & Reset Instructions** (`node tools/seed-whiskerworks.mjs` and UI Demo Scenario Control Center usage) plus tenant-isolation safety notes.
4. **Desktop & Tablet Screenshots** of: (a) Split-Screen Login, (b) WhiskerWorks 15-Zone Interactive Plant Map with Signal Lights, (c) Expanded Employee Card + IPC Training Drawer on Workforce Kanban, and (d) Redesigned Shop-Floor Operator Kiosk HMI.
5. **Scenario Execution Inventory (`S01`–`S16`)** and **Actual Test Commands & Outputs** (`node --test tests/ui/*.test.mjs`, `dotnet test`).
6. **Implemented / Partially Implemented / Proposed Matrix** for independent Claude audit verification.
