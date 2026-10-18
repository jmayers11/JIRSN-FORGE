import assert from 'node:assert/strict';
import test from 'node:test';
import { dispositionPayload, rtvReducePayload, receivingTab } from '../../src/Forge.Host/wwwroot/js/receiving.js';

test('a disposition decides the whole rejection unless a smaller quantity is given (ADR 0055)', () => {
  assert.deepEqual(dispositionPayload({ disposition: 'Scrap', quantity: '' }, '30'), { disposition: 'Scrap' });
  assert.deepEqual(dispositionPayload({ disposition: 'Scrap', quantity: '30' }, '30'), { disposition: 'Scrap' });
  assert.deepEqual(dispositionPayload({ disposition: 'ReturnToVendor', quantity: '20' }, '30'), { disposition: 'ReturnToVendor', quantity: 20 });
  assert.throws(() => dispositionPayload({ disposition: 'Scrap', quantity: '31' }, '30'), /cannot cover more/);
  assert.throws(() => dispositionPayload({ disposition: 'Scrap', quantity: '0' }, '30'), /above zero/);
});

test('a return to vendor can only be reduced', () => {
  assert.deepEqual(rtvReducePayload({ quantity: '12' }, '15'), { quantity: 12 });
  assert.throws(() => rtvReducePayload({ quantity: '15' }, '15'), /only be reduced/);
  assert.throws(() => rtvReducePayload({ quantity: '16' }, '15'), /only be reduced/);
  assert.throws(() => rtvReducePayload({ quantity: '' }, '15'), /above zero/);
});

test('split rejections name their source and open returns offer Reduce only with disposition authority', () => {
  const rec = {
    receipts: { data: [] }, returns: { data: [] },
    rejections: { data: [{ rejectionId: 8002, splitFrom: 8001, partNumber: 'COIL-AL', supplierCode: null, quantity: 10, defect: 'Dent', disposition: 'Pending' }] },
    rtvs: { data: [{ rtvId: 9001, partNumber: 'COIL-AL', supplierCode: null, quantity: 20, status: 'Open', forRework: false }] },
  };
  const quality = { permissions: ['quality.disposition', 'inventory.transact'] };
  const handler = { permissions: ['inventory.transact'] };
  const withAuthority = receivingTab(rec, quality);
  assert.match(withAuthority, /split from 8001/);
  assert.match(withAuthority, /data-rtv-reduce="9001"/);
  assert.match(withAuthority, /data-dispose="8002" data-qty="10"/);
  assert.doesNotMatch(receivingTab(rec, handler), /data-rtv-reduce/);
});
