import assert from 'node:assert/strict';
import test from 'node:test';
import { canAction } from '../../src/Forge.Host/wwwroot/js/ui-authority.mjs';
import { receivingTab } from '../../src/Forge.Host/wwwroot/js/receiving.js';
import { outsideProcessingTab } from '../../src/Forge.Host/wwwroot/js/outside-processing.js';

const permission = 'procurement.approve';
const at = Date.parse('2026-10-12T12:00:00Z');
const grant = extra => ({ permission, startsAt: '2026-10-12T11:00:00Z', expiresAt: '2026-10-12T13:00:00Z', scopes: [], ...extra });

test('custom role controls use the exact effective business permission', () => {
  const custom = { roles: ['ExpediteApprover'], permissions: [permission] };
  assert.equal(canAction(custom, permission), true);
  assert.equal(canAction(custom, 'procurement.manage'), false);
  assert.equal(canAction({ roles: ['PurchasingManager'], permissions: [] }, permission), false);
  assert.equal(canAction(null, permission), false);
});

test('delegated controls are valid at the start and expire at the exact end', () => {
  const me = { permissions: [], delegatedPermissions: [grant()] };
  assert.equal(canAction(me, permission, null, at - 3600001), false);
  assert.equal(canAction(me, permission, null, at - 3600000), true);
  assert.equal(canAction(me, permission, null, at), true);
  assert.equal(canAction(me, permission, null, at + 3600000), false);
  assert.equal(canAction({ delegatedPermissions: [grant({ startsAt: 'invalid' })] }, permission, null, at), false);
});

test('scoped controls match their record and allow a context-free form to collect inputs', () => {
  const me = { scopedPermissions: [{ permission, scopeType: 'work_center', scopeId: 'SLIT-WC' }] };
  assert.equal(canAction(me, permission), true);
  assert.equal(canAction(me, permission, { workCenterCode: 'slit-wc' }), true);
  assert.equal(canAction(me, permission, { workCenterCode: 'PAINT-WC' }), false);
  assert.equal(canAction(me, permission, {}), false);
  assert.equal(canAction({ scopedPermissions: [{ permission, scopeType: 'unknown', scopeId: '' }] }, permission, {}), false);
});

test('all delegated scope dimensions must match while separate direct scopes are alternatives', () => {
  const scopes = [{ scopeType: 'program', scopeId: 'P1' }, { scopeType: 'facility', scopeId: 'PLANT1' }];
  const me = { delegatedPermissions: [grant({ scopes })] };
  assert.equal(canAction(me, permission, { programId: 'P1', facilityId: 'PLANT1' }, at), true);
  assert.equal(canAction(me, permission, { programId: 'P1', facilityId: 'PLANT2' }, at), false);
  assert.equal(canAction(me, permission, { programId: 'P1' }, at), false);
  assert.equal(canAction({ scopedPermissions: scopes.map(scope => ({ permission, ...scope })) }, permission, { facilityId: 'PLANT1' }), true);
});

test('a delegated amount must fit both source and recipient ceilings in their currency', () => {
  const me = {
    approvalThresholds: [{ permission, maximumAmount: 750, currency: 'USD' }],
    delegatedPermissions: [grant({ sourceThreshold: { permission, maximumAmount: 500, currency: 'USD' } })],
  };
  assert.equal(canAction(me, permission, null, at), true, 'the form can open before amount is entered');
  assert.equal(canAction(me, permission, { amount: 500, currency: 'usd' }, at), true);
  assert.equal(canAction(me, permission, { amount: 501, currency: 'USD' }, at), false);
  assert.equal(canAction(me, permission, { amount: 400, currency: 'EUR' }, at), false);
  assert.equal(canAction(me, permission, { currency: 'USD' }, at), false);
  me.approvalThresholds[0].maximumAmount = 250;
  assert.equal(canAction(me, permission, { amount: 251, currency: 'USD' }, at), false);
});

test('the recipient ceiling applies to direct grants and one failed delegation cannot hide another valid grant', () => {
  const me = { permissions: [permission], approvalThresholds: [{ permission, maximumAmount: 100, currency: 'USD' }], delegatedPermissions: [grant({ sourceThreshold: { maximumAmount: 10, currency: 'USD' } })] };
  assert.equal(canAction(me, permission, { amount: 50, currency: 'USD' }, at), true);
  assert.equal(canAction(me, permission, { amount: 101, currency: 'USD' }, at), false);
  assert.equal(canAction({ delegatedPermissions: [grant({ scopes: [{ scopeType: 'shift', scopeId: 'DAY' }] }), grant({ scopes: [{ scopeType: 'shift', scopeId: 'NIGHT' }] })] }, permission, { shiftId: 'NIGHT' }, at), true);
});

test('a custom inventory role sees outside-processing commands without receiving inspection authority', () => {
  const me = { roles: ['ProcessorDesk'], permissions: ['inventory.transact'] };
  const html = outsideProcessingTab({ data: { dispatches: [] } }, me);
  assert.match(html, /data-process-dispatch/);
  assert.match(html, /data-process-return/);
  assert.doesNotMatch(outsideProcessingTab({ data: { dispatches: [] } }, { roles: ['MaterialHandler'], permissions: [] }), /data-process-dispatch/);
});

test('rendered receiving inspection, disposition and approval controls follow separate exact permissions', () => {
  const rec = {
    receipts: { data: [{ receipt: { transactionId: 1, partNumber: 'P', quantity: 5, at: '2026-10-12T10:00:00Z' }, accepted: 0, rejected: 0, remaining: 5, inspections: [], faiStatus: 'NotRequired' }] },
    rejections: { data: [{ rejectionId: 2, partNumber: 'P', quantity: 1, disposition: 'Pending' }, { rejectionId: 3, partNumber: 'P', quantity: 1, disposition: 'UseAsIs' }] },
    rtvs: { data: [] }, returns: { data: [] },
  };
  const inspect = receivingTab(rec, { roles: ['InspectionContractor'], permissions: ['quality.inspect'] });
  assert.match(inspect, /data-inspect=/);
  assert.doesNotMatch(inspect, /data-dispose=|data-approve=|data-rma-add/);
  const dispose = receivingTab(rec, { roles: ['DispositionSpecialist'], permissions: ['quality.disposition'] });
  assert.match(dispose, /data-dispose=/);
  assert.doesNotMatch(dispose, /data-inspect=|data-approve=/);
  const approve = receivingTab(rec, { roles: ['ConcessionSigner'], permissions: ['quality.approve'] });
  assert.match(approve, /data-approve=/);
  assert.doesNotMatch(approve, /data-inspect=|data-dispose=/);
});
