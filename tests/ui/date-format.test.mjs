import assert from 'node:assert/strict';
import test from 'node:test';
import { fmtDateTime } from '../../src/Forge.Host/wwwroot/js/ui.js';

// A date in another year shows its year, so a qualification expiring next year never reads like today.
test('date-times outside the current year carry their year', () => {
  const nextYear = new Date().getUTCFullYear() + 1;
  assert.match(fmtDateTime(`${nextYear}-06-15T12:00:00Z`), new RegExp(String(nextYear)));
  assert.doesNotMatch(fmtDateTime(new Date().toISOString()), new RegExp(String(new Date().getUTCFullYear())));
  assert.equal(fmtDateTime(null), '—');
});
