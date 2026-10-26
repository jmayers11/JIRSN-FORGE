import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

const proposal = (checks, status = 'Proposed') => ({
  proposalId: 'p-1', partNumber: 'ROLL-100', revision: 'A', quantity: 5, dueDate: '2026-11-06', priority: 2, ncrId: 12, replacesOrderNumber: 7, status,
  rationale: 'Replace scrapped rolls', proposedBy: 'person:eng', checkedAt: '2026-10-25T12:00:00Z', orderNumber: null, checks,
});

test('a replacement order proposal shows each operation, the qualified people who could move and what each move leaves', async t => {
  installBrowser(t);
  const { proposalHtml, uncovered } = await import('../../src/Forge.Host/wwwroot/js/replacement-orders.js');
  const checks = [
    { sequence: 10, description: 'Slit', workCenterCode: 'SLIT-WC', machineCode: 'SLITTER-A', activityCode: 'RUN_SLITTER', readyNow: false, currentConstraint: 'No operator',
      candidates: [{ personId: 's', personName: 'Sarah', requiresSourceBackfill: true, sourceImpacts: ['STAGE_ROLL on SLIT-WC would be unstaffed'], evidence: ['Backfill: Dana to STAGE_ROLL on SLIT-WC.'] }], limitations: [] },
    { sequence: 20, description: 'Kit', workCenterCode: 'SLIT-WC', machineCode: null, activityCode: 'KITTING', readyNow: false, currentConstraint: 'Nobody on it now holds FIRST-AID.', candidates: [], limitations: [] },
  ];
  const planner = { permissions: ['execution.orders.release'] }, engineer = { permissions: ['execution.orders.manage'] };
  const html = proposalHtml(proposal(checks), planner);
  assert.match(html, /replaces scrap on NCR 12/);
  assert.match(html, /1 qualified option/);
  assert.match(html, /needs backfill/);
  assert.match(html, /Leaves: STAGE_ROLL on SLIT-WC would be unstaffed/);
  assert.match(html, /Backfill: Dana/);
  assert.match(html, /Nobody qualified for KITTING on SLIT-WC/);
  assert.match(html, /data-replacement-decide="approve"/);
  assert.equal(uncovered(proposal(checks)).length, 1);
  // Only people who may release orders decide, and only while it is open.
  assert.doesNotMatch(proposalHtml(proposal(checks), engineer), /data-replacement-decide/);
  assert.doesNotMatch(proposalHtml(proposal(checks, 'Approved'), planner), /data-replacement-decide/);
});
