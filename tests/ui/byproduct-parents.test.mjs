import assert from 'node:assert/strict';
import test from 'node:test';
import { byproductParents } from '../../src/Forge.Host/wwwroot/js/genealogy.js';

const issued = [
  { partNumber: 'FOIL-9', lotNumber: 'M-1' },
  { partNumber: 'FOIL-9', lotNumber: 'M-2' },
  { partNumber: 'GLUE', lotNumber: null },
];

test('byproduct parents come only from issued lots with a positive quantity', () => {
  assert.deepEqual(byproductParents(issued, ['20', '', '']), [{ partNumber: 'FOIL-9', lotNumber: 'M-1', quantity: 20 }]);
  assert.deepEqual(byproductParents(issued, ['20', '10', '']), [
    { partNumber: 'FOIL-9', lotNumber: 'M-1', quantity: 20 }, { partNumber: 'FOIL-9', lotNumber: 'M-2', quantity: 10 }]);
  assert.deepEqual(byproductParents(issued, ['', '', '']), []);
  assert.throws(() => byproductParents(issued, ['0', '', '']), /positive quantity of FOIL-9 lot M-1/);
  assert.throws(() => byproductParents(issued, ['', '', '5']), /GLUE has no lot/);
});
