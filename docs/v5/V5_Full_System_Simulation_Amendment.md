# Forge V5 — Full-System Digital Factory Simulation Amendment

Expand the V5 manufacturing stress test into a comprehensive, enterprise-wide operational validation framework.

**Primary objective:** Test every implemented functional area of Forge, including the interactions and dependencies between modules, using one consistent synthetic manufacturing environment.

## Mandatory scope

Inventory all implemented Forge modules, services, APIs, workflows, permissions, background jobs and integrations. Build a coverage matrix mapping each one to individual, cross-module and full-system simulation tests.

Include demand management, planning, MRP calculations, procurement, suppliers, receiving, inspection, quality holds, NCR/MRB dispositions, CAPA where implemented, engineering changes, document control, material allocation, manufacturing, workforce, training, safety, maintenance, tooling, calibration, warehouse, shipping, identity, reporting, printing, and infrastructure.

Do not mark planned features as operational or silently omit incomplete modules.

## Simulation environment

Create a fictional manufacturing organization with:

- 150–500 competing customer orders.
- Multiple customers and priority levels.
- Eight or more departments and shared resources.
- Multiple shifts and 45–100 operators with different qualifications.
- Thirty-, sixty-, and ninety-day planning horizons.
- Complex BOMs, routings, intermediate WIP, raw material, approved alternates and finished goods.
- Realistic supplier lead times, quality events, maintenance requirements and production yields.

Use fixed random seeds and reproducible event logs. Support both controlled individual scenarios and a combined long-running factory simulation.

## Required disruptive events

Simulate operator callouts, PTO, training restrictions, staffing transfers, overtime, machine breakdowns, overdue maintenance, calibration expiration, supplier delays, inaccurate inventory, material shortages, quality holds, partial inspections, MRB dispositions, rework, scrap, customer concessions, engineering revisions, alternate material approvals, expedited orders, document errors, incorrect scans, failed printing, network interruptions and concurrent transactions.

Each event must propagate through all affected modules.

## Quality and MRB requirements

Verify that quality holds immediately affect eligible material and downstream operations according to scope.

Test scrap, rework, repair, return-to-vendor, use-as-is and other configured dispositions, including authorization, evidence, inventory consequences, capacity impact, supplier implications, genealogy and customer delivery risk.

Never permit an unauthorized disposition or silently release material.

## Workforce impact requirements

For every callout, reassignment, overtime decision or qualification change, recalculate executable capacity and affected delivery commitments.

Respect staffing composition, two-person safety requirements, training, actual attendance, transfer time, reduced cross-training efficiency and competing departmental needs.

Compare immediate gains with cumulative downstream losses.

## Simulation integrity

Use the real Forge domain services and business rules wherever feasible. Do not substitute simplified mock calculations for production logic and call them validation.

Simulations must be isolated from live records. Every scenario must have independently defined expected outcomes and explicit pass/fail criteria.

Verify inventory conservation, unique physical identifiers, complete genealogy, transaction idempotency, authorization, reproducibility, scheduling feasibility and data consistency.

## Required outputs

1. Complete Forge module coverage inventory.
2. Synthetic factory and event generator.
3. Scenario library covering every implemented module.
4. Cross-module dependency and impact matrix.
5. Baseline versus scenario KPI comparisons.
6. Immediate, seven-day, thirty-day and ninety-day consequence analysis.
7. Expected-versus-actual test results.
8. Defect register with severity, reproduction steps and root-cause evidence.
9. Performance, concurrency, resilience and recovery findings.
10. Operational readiness scorecard with evidence and explicit untested areas.

Do not invent passing tests or claim a simulation works merely because the dashboard renders.

Start with repository discovery and the proposed test architecture. Identify existing simulation engines and test infrastructure before adding new components. Obtain approval before production code or schema changes.

**Success criterion:** Forge must demonstrate that the same underlying operational event produces consistent, traceable, authorized and economically meaningful consequences across the entire manufacturing system.