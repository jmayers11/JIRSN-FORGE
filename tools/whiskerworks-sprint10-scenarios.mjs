// Sprint 10 acceptance matrix from the approved workforce-intelligence brief.
// These are test definitions, not evidence that database-backed scenarios have passed.
const core = [
  ['PTO coverage gap', 'Workforce', 'Approved CNC setup PTO removes the next-shift specialist; the gap and safe alternatives remain explicit.'],
  ['Unexpected callout recovery', 'Workforce', 'A same-day proposal ranks only currently qualified alternatives and reports source-center impact.'],
  ['No-show versus callout', 'Attendance', 'A missed clock-in remains pending until the shift ends and independent review distinguishes it from a reported callout.'],
  ['Two-person crew constraint', 'Safety', 'Removing one member of a required two-person crew makes the operation non-executable.'],
  ['Expired qualification block', 'Competency', 'A qualification expired yesterday and cannot authorize dispatch.'],
  ['Machine-specific authorization', 'Competency', 'CNC-01 authorization does not authorize CNC-03.'],
  ['Pending practical authorization', 'Competency', 'J-STD-001 without a passed machine practical does not authorize independent work.'],
  ['IPC reassessment window', 'Competency', 'IPC-A-610 due for reassessment is visible under the configured policy.'],
  ['Quality trend confounders', 'Quality', 'Harness trend is shown with equipment/material context and does not assign operator fault.'],
  ['Inaccurate labor standard', 'Performance', 'An unsupported or inaccurate standard is marked for review, not used as a worker score.'],
  ['Downtime-context labor', 'Performance', 'Lower output during recorded machine downtime retains the downtime context and is not treated as operator fault.'],
  ['Advanced training prerequisites', 'Training', 'Prerequisites and future bottleneck benefit are shown before human nomination.'],
  ['Multi-person packaging-safe recovery', 'Workforce', 'CNC coverage is restored without leaving Packaging below its safety or capacity requirements.'],
  ['Rework qualification expansion', 'Work Orders', 'Added solder/test work adds required qualifications and labor hours while retaining the original disposition.'],
  ['Sixty-shift attendance denominator', 'Attendance', 'The selected 60-shift quarter shows 53/55 eligible arrivals, 4 PTO days, 1 protected leave day, and 5 excluded opportunities.'],
  ['Attendance correction review', 'Attendance', 'A no-show correction supersedes the original event and returns the change for independent review.'],
  ['Quarter rollover history', 'Attendance', 'The current period resets at quarter rollover while the prior quarter remains queryable.'],
  ['Effective-dated attendance policy', 'Attendance', 'A new policy affects its effective period only and does not rewrite historical results.'],
  ['Insufficient evidence nomination', 'Training', 'A worker below the sample threshold receives insufficient evidence and no ranking.'],
  ['Advisory advancement packet', 'Development', 'A promotion packet requires human review and never changes compensation automatically.'],
  ['Forge-only workflow', 'Architecture', 'Employee, attendance, and qualification workflows continue with Nexus disconnected.'],
  ['Personnel privacy and tenant scope', 'Security', 'Cross-tenant and role checks prevent unauthorized access to sensitive personnel records.'],
];

const material = [
  ['Suspect scrap report', 'Quality / Inventory', 'Eight reported defects enter suspect hold and notify Quality without posting final scrap.'],
  ['Split disposition reconciliation', 'Quality / Inventory', 'Five units scrap and three rework; stock, genealogy, cost, and order quantities reconcile.'],
  ['Linked replacement request', 'Inventory', 'Five replacement units link to one suspect report, deduplicate, and enter the stockroom queue.'],
  ['Partial issue and backorder', 'Inventory', 'A request for 20 with 12 available issues at most 12 and leaves 8 backordered.'],
  ['Concurrent last-stock requests', 'Inventory / Concurrency', 'Two requests for the final 10 connectors cannot reserve or issue more than 10 total.'],
  ['Non-BOM approval gate', 'Inventory / Engineering', 'A non-BOM request requires planner approval and does not silently amend the released order.'],
  ['Rework routing expansion', 'Quality / Engineering / Planning', 'Approved rework adds a connector, inspection step, labor standard, MRP demand, and training requirement.'],
  ['Due-date risk email', 'Notifications / Planning', 'A projected shortage emits one actionable, permission-checked planner email with a deep link.'],
  ['Overdue escalation de-duplication', 'Notifications', 'An unfilled urgent request escalates once at threshold and resolves when fulfilled.'],
  ['Email provider retry', 'Notifications / Resilience', 'Provider failure retries delivery without duplicating a request or inventory transaction.'],
  ['Held stock pick denial', 'Inventory / Quality', 'Quality-held stock cannot be picked even when its physical quantity is positive.'],
  ['Network retry idempotency', 'Inventory / Idempotency', 'Repeated submission of one operator request creates a single canonical request.'],
  ['Manufacturing replacement recovery', 'Planning / Workforce', 'A replacement order proposal checks qualified operators and source-center impact before human approval.'],
  ['Machine-fault variance context', 'Performance / Maintenance', 'A recorded machine fault remains a contextual variance reason and does not penalize an employee.'],
  ['Partial issue cancellation', 'Inventory / Audit', 'Cancellation after partial issue reconciles the remaining demand without deleting history.'],
  ['Cross-role and cross-site permissions', 'Security / Inventory', 'Operator, stockroom, planner, Quality, and supervisor permissions are enforced across site and tenant boundaries.'],
];

export const sprint10ScenarioCatalog = [
  ...core.map(([title, domain, acceptance], index) => ({
    code: `WW-S10-${String(index + 1).padStart(2, '0')}`,
    phase: 'core', title, domain, acceptance,
    executionStatus: 'Pending isolated-database scenario replay',
  })),
  ...material.map(([title, domain, acceptance], index) => ({
    code: `WW-S10-${String(index + 23).padStart(2, '0')}`,
    phase: 'material-extension', title, domain, acceptance,
    executionStatus: 'Pending isolated-database scenario replay',
  })),
];

export function validateSprint10ScenarioCatalog(catalog = sprint10ScenarioCatalog) {
  if (!Array.isArray(catalog) || catalog.length !== 38) throw new Error('Sprint 10 acceptance requires exactly 38 scenario definitions.');
  const expected = Array.from({ length: 38 }, (_, index) => `WW-S10-${String(index + 1).padStart(2, '0')}`);
  if (catalog.some((scenario, index) => scenario.code !== expected[index] || !scenario.title || !scenario.domain || !scenario.acceptance || !scenario.executionStatus))
    throw new Error('Sprint 10 scenarios must be complete, unique, and ordered by acceptance ID.');
  if (catalog.slice(0, 22).some(scenario => scenario.phase !== 'core') || catalog.slice(22).some(scenario => scenario.phase !== 'material-extension'))
    throw new Error('The Sprint 10 catalog must preserve its 22 core and 16 material-extension cases.');
  return true;
}
