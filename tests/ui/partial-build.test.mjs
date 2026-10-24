import assert from 'node:assert/strict';
import test from 'node:test';
import { partialBuildHtml, partialBuildBody } from '../../src/Forge.Host/wwwroot/js/partial-build.js';

// Sprint 10 §17–19: the partial-build preview shows reach per operation, its limits, storage, usable dates and why it cannot be approved.

const planner = { personId: 'p1', permissions: ['planning.schedule.read', 'planning.schedule.approve'] };
const viewer = { personId: 'p2', permissions: ['planning.schedule.read'] };
const plan = {
  orderNumber: 7, partNumber: 'FRAME', asOf: '2026-10-24T12:00:00Z', ruleVersion: 'partial-build-v1', stopSequence: 20, blockedSequence: 30,
  operations: [
    { sequence: 10, description: 'Form', done: 0, reachable: 50, additional: 50, limitedBy: null },
    { sequence: 20, description: 'Weld', done: 0, reachable: 50, additional: 50, limitedBy: null },
    { sequence: 30, description: 'Fit motor', done: 0, reachable: 0, additional: 0, limitedBy: 'MOTOR' },
  ],
  feasibility: { limitingConstraints: ['Approved WIP storage at WIP-RACK'] }, quantity: 30, materials: [{ partNumber: 'TUBE', consumedAtSequence: 10, quantity: 60 }],
  storageQuantity: 30, storageLocation: 'WIP-RACK',
  storage: [{ rule: { locationCode: 'WIP-RACK', maxDwellHours: 120 }, status: '30 of 30 free at WIP-RACK (0 on hand, 0 reserved; approved 30, Rack RR-12).' }],
  materialDates: [{ partNumber: 'MOTOR', supplierPromisedArrival: '2026-10-28T00:00:00Z', expectedUsableAt: '2026-10-28T14:00:00Z', arrivalConfidence: 'Promised date only', source: 'PO 9 line 1' }],
  expectedRestart: '2026-10-28T14:00:00Z', expectedDwellHours: 98, maxDwellHours: 120, workBroughtForwardMinutes: 120, canApprove: true,
  blockers: [], notes: ['Approving reserves the listed material and storage space.'], comparison: 'Partial: 30 units go through operation 20 now.', activeBuild: null,
};

test('the preview shows reach per operation, the stopping point, the block and what approval reserves', () => {
  const html = partialBuildHtml(plan, planner);
  assert.match(html, /stop<\/span>/);
  assert.match(html, /blocked<\/span>/);
  assert.match(html, /<strong>30<\/strong> through operation 20, 30 waiting at WIP-RACK/);
  assert.match(html, /Approved WIP storage at WIP-RACK/);
  assert.match(html, /60 TUBE/);
  assert.match(html, /Rack RR-12/);
  assert.match(html, /max dwell 120 h/);
  assert.match(html, /Promised date only/);
  assert.match(html, /data-pb-approve/);
});

test('only an approver sees the approval button, and blockers replace it', () => {
  assert.doesNotMatch(partialBuildHtml(plan, viewer), /data-pb-approve/);
  const blocked = partialBuildHtml({ ...plan, canApprove: false, quantity: 0, blockers: ['No approved WIP storage rule exists for FRAME.'] }, planner);
  assert.doesNotMatch(blocked, /data-pb-approve/);
  assert.match(blocked, /Why it cannot be approved now/);
  assert.match(blocked, /No approved WIP storage rule/);
});

test('an approved build can be completed or cancelled; options become the request body', () => {
  const html = partialBuildHtml({ ...plan, canApprove: false, activeBuild: { buildId: 4, quantity: 30, sequence: 20, storageLocation: 'WIP-RACK' } }, planner);
  assert.match(html, /data-pb-close="complete\|4"/);
  assert.match(html, /data-pb-close="cancel\|4"/);
  assert.deepEqual(partialBuildBody({ receivingHours: '2', inspectionHours: '', putAwayHours: 1, quantity: '', partialAllowed: true }),
    { receivingHours: 2, inspectionHours: null, putAwayHours: 1, stagingHours: 0, setupHours: 0, quantity: null, partialAllowed: true });
});
