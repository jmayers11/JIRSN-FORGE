import assert from 'node:assert/strict';
import test from 'node:test';
import { scrapActions, scrapRequestBody, scrapPreviewHtml, scrapTab, scrapStateLabel } from '../../src/Forge.Host/wwwroot/js/scrap.js';

// Sprint 10 §20: the screen only offers the steps the server would allow; the server enforces every rule again.

const planner = { personId: 'p1', permissions: ['inventory.read', 'inventory.scrap.request'] };
const controller = { personId: 'c1', permissions: ['inventory.read', 'inventory.scrap.request', 'inventory.approve', 'inventory.transact', 'inventory.manage'] };
const quality = { personId: 'q1', permissions: ['inventory.read', 'quality.disposition'] };
const request = (state, extra = {}) => ({ requestId: 7, kind: 'Scrap', state, requestedBy: 'person:p1', partNumber: 'COIL-AL', locationCode: 'STORES', stockStatus: 'Released', quantity: 12, reasonCode: 'Damaged', ...extra });

test('a requester may withdraw but never decide their own request', () => {
  assert.deepEqual(scrapActions(request('PendingApproval'), planner), ['cancel']);
  assert.deepEqual(scrapActions(request('PendingApproval', { requestedBy: 'person:c1' }), controller), ['cancel']);
  assert.deepEqual(scrapActions(request('PendingApproval'), controller), ['approve', 'reject']);
});

test('quality-held requests are decided by Quality, approved ones executed separately', () => {
  assert.deepEqual(scrapActions(request('PendingDisposition'), controller), []);
  assert.deepEqual(scrapActions(request('PendingDisposition'), quality), ['dispose']);
  assert.deepEqual(scrapActions(request('Approved'), quality), []);
  assert.deepEqual(scrapActions(request('Approved'), controller), ['execute']);
});

test('executed scrap is corrected once, by a linked reversal', () => {
  const executed = request('Executed');
  assert.deepEqual(scrapActions(executed, planner, [executed]), ['reverse']);
  const reversal = { requestId: 9, kind: 'Reversal', state: 'PendingApproval', correctsRequestId: 7, requestedBy: 'person:p1' };
  assert.deepEqual(scrapActions(executed, planner, [executed, reversal]), []);
  assert.deepEqual(scrapActions(executed, planner, [executed, { ...reversal, state: 'Rejected' }]), ['reverse']);
});

test('the request body is checked before anything is sent', () => {
  assert.throws(() => scrapRequestBody({ partNumber: 'COIL-AL', locationCode: 'STORES', quantity: '0', reasonCode: 'Damaged' }), /above zero/);
  assert.throws(() => scrapRequestBody({ partNumber: 'COIL-AL', locationCode: 'STORES', quantity: '2', reasonCode: 'Other', notes: '' }), /Describe why/);
  const body = scrapRequestBody({ partNumber: 'COIL-AL', locationCode: 'STORES', lotNumber: '', status: 'Hold', quantity: '2', reasonCode: 'QualityReject', linkedOrderNumber: '1001', linkedNcr: 'NCR-1' });
  assert.equal(body.lotNumber, null);
  assert.equal(body.linkedOrderNumber, 1001);
  assert.equal(body.linkedPoNumber, null);
  assert.equal(body.status, 'Hold');
});

test('the preview says plainly when the request would be refused or needs allocations released', () => {
  const eligibility = { onHand: 20, allocatedToTasks: 5, pickedForTasks: 0, pendingScrap: 3, packed: 0, eligible: 12, eligibleIfAllocationsReleased: 17, allocatedTaskIds: [44] };
  const base = { eligibility, estimatedValue: null, valueBasis: 'No purchase price is recorded for this part; Forge has no costing module.', planning: { nettable: true, affectedOrders: [] } };
  assert.match(scrapPreviewHtml(base, 10), /free to scrap/);
  assert.match(scrapPreviewHtml(base, 15), /allocated to material tasks #44/);
  assert.match(scrapPreviewHtml(base, 18), /would be refused and nothing would change/);
  assert.match(scrapPreviewHtml({ ...base, planning: { nettable: true, affectedOrders: [{ orderNumber: 1001, partNumber: 'ROLL-100', beforeMaterialState: 'Ready', proposedMaterialState: 'Shortfall', beforeScheduleStatus: 'OnTime', proposedScheduleStatus: 'Late', completionShiftHours: 6 }] } }, 10), /#1001.*Ready → <b>Shortfall<\/b>/s);
  assert.match(scrapPreviewHtml({ ...base, planning: null, planningRestricted: true }, 10), /cannot read the schedule/);
});

test('the tab lists open requests with their decider and hides actions the reader cannot take', () => {
  const html = scrapTab({ requests: { data: [request('PendingDisposition', { requiresQuality: true, holdTransactionId: 5 }), request('Executed', { requestId: 8, decidedBy: 'policy:2', policyId: 2, executedBy: 'person:c1', transactionId: 99 })] }, policies: { data: [] } }, planner);
  assert.match(html, /Waiting for Quality disposition/);
  assert.match(html, /On hold while Quality decides/);
  assert.match(html, /Site rule 2/);
  assert.match(html, /data-scrap-cancel="7"/);
  assert.doesNotMatch(html, /data-scrap-dispose/);
  assert.match(html, /data-scrap-new/);
  assert.equal(scrapStateLabel('Executed').tone, 'ok');
});
