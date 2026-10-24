import assert from 'node:assert/strict';
import test from 'node:test';
import { causePerformanceHtml } from '../../src/Forge.Host/wwwroot/js/cause-performance.js';

// Sprint 10 §16: unadjusted beside adjusted, official FPY beside employee-attributable FPY, every exclusion named.

const report = {
  basis: 'Basis text.',
  paidMinutes: 600, accountedMinutes: 600, gapMinutes: 0, overlapMinutes: 0, operationsOutsideEfficiencyScope: 1, qualityRestricted: false,
  efficiency: { unadjustedEfficiencyPercent: 71.5, controllableEfficiencyPercent: 110, verifiedExternalDelayHours: 3, status: 'Calculated from accepted-output standard hours.' },
  firstPass: { officialProcessFpyPercent: 92, employeeAttributableFpyPercent: 96.84, employeeAttributionCoveragePercent: 95, externallyCausedQuantity: 5, unassignedQuantity: 0, inspectedQuantity: 100, employeeMetricStatus: 'Uses only verified units.' },
  firstPassCohorts: [
    { inspectionId: 11, orderNumber: 1042, sequence: 10, quantity: 92, passed: true, cause: 'EmployeeControlled', verified: true, attributionState: 'Verified', attributionId: 3 },
    { inspectionId: 12, orderNumber: 1042, sequence: 20, quantity: 5, passed: false, cause: 'Machine', verified: false, attributionState: 'Proposed', attributionId: 4 },
  ],
  shifts: [{
    clockInAt: '2026-10-20T12:00:00Z', clockOutAt: '2026-10-20T22:00:00Z', openShift: false, directLaborMinutes: 420, verifiedExternalDelayMinutes: 180, reportedUnverifiedDelayMinutes: 0,
    reconciliation: { expectedPaidMinutes: 600, accountedPaidMinutes: 600, gapMinutes: 0, overlapMinutes: 0, dataQualityFlags: [] },
    categories: [{ code: 'Labor:Run', label: 'Productive operation', minutes: 390, source: 'Operation labor', externalDelay: false }, { code: 'MachineDowntime:Verified', label: 'Verified machine downtime during assigned work', minutes: 120, source: 'Maintenance downtime', externalDelay: true }],
    machineEvents: [{ machineCode: 'CNC-01', source: 'Maintenance downtime', fullEventMinutes: 300, employeeImpactMinutes: 120, verified: true }],
    pendingReviewNotes: ['Break 10:00–10:15 is waiting for review and is not counted yet.'],
  }],
};

test('adjusted and unadjusted efficiency, and both first-pass yields, are shown side by side', () => {
  const html = causePerformanceHtml(report, { permissions: [] });
  assert.match(html, /110%/);
  assert.match(html, /Unadjusted 71\.5%/);
  assert.match(html, /92%/);
  assert.match(html, /96\.84%/);
  assert.match(html, /5 externally caused excluded/);
  assert.match(html, /outside the efficiency scope/);
});

test('a machine event shows its full length and only the minutes charged to the shift', () => {
  const html = causePerformanceHtml(report, { permissions: [] });
  assert.match(html, /CNC-01 · Maintenance downtime: event 5 h, charged to this shift 2 h/);
  assert.match(html, /waiting for review and is not counted yet/);
});

test('only Quality roles see attribution actions, and nobody sees them without Quality access', () => {
  assert.doesNotMatch(causePerformanceHtml(report, { permissions: ['workforce.read'] }), /data-cp-verify/);
  assert.match(causePerformanceHtml(report, { personId: 'q', permissions: ['quality.disposition'] }), /data-cp-verify="4"/);
  assert.match(causePerformanceHtml(report, { permissions: ['quality.inspect'] }), /data-cp-correct="11\|3"/);
  const restricted = causePerformanceHtml({ ...report, qualityRestricted: true, firstPass: null, firstPassCohorts: [] }, { permissions: [] });
  assert.match(restricted, /not readable with your access/);
  assert.doesNotMatch(restricted, /96\.84/);
});
