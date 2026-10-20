import assert from 'node:assert/strict';
import test from 'node:test';
import { materialConstraintKpi } from '../../src/Forge.Host/wwwroot/js/model.js';

test('material constraint KPI counts released orders awaiting supply or in shortfall', () => {
  assert.deepEqual(materialConstraintKpi({ data: { output: [
    { orderNumber: 1, state: 'Ready' }, { orderNumber: 2, state: 'Expected' },
    { orderNumber: 3, state: 'Shortfall' }, { orderNumber: 4, state: 'Expected' },
  ] } }), { value: 3, why: '1 short, 2 awaiting recorded receipts, of 4 released orders' });
  assert.equal(materialConstraintKpi({ data: { output: [] } }).value, 0);
  assert.equal(materialConstraintKpi({ error: { message: 'Not authorized.' } }).value, null);
});
