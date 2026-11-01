import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

const entry = (over = {}) => ({ entryId: 'e1', workDate: '2026-11-02', orderNumber: 10002, sequence: 10, chargeCode: null, hours: 6, note: null, state: 'Active',
  recordedBy: 'person:emp', recordedAt: '2026-11-02T20:00:00Z', lateReason: null, supersedesEntryId: null, correctionReason: null, ...over });

test('a week shows one row per charge, day totals, late entries and the corrections kept beneath', async t => {
  installBrowser(t);
  const { weekGridHtml, days, shift, charge } = await import('../../src/Forge.Host/wwwroot/js/pages/timesheet.js');
  assert.deepEqual(days('2026-11-02'), ['2026-11-02', '2026-11-03', '2026-11-04', '2026-11-05', '2026-11-06', '2026-11-07', '2026-11-08']);
  assert.equal(shift('2026-11-02', -1), '2026-10-26');
  assert.equal(charge(entry()), 'Order 10002 op 10');
  assert.equal(charge(entry({ orderNumber: null, sequence: null, chargeCode: 'SHOP' })), 'SHOP');
  const html = weekGridHtml({ entries: [
    entry({ entryId: 'e1', state: 'Superseded' }),
    entry({ entryId: 'e2', hours: 5, supersedesEntryId: 'e1', correctionReason: 'One hour was cleanup' }),
    entry({ entryId: 'e3', orderNumber: null, sequence: null, chargeCode: 'SHOP', hours: 3 }),
    entry({ entryId: 'e4', workDate: '2026-11-03', orderNumber: null, sequence: null, chargeCode: 'ENG-101', hours: 4, lateReason: 'At the customer' }),
  ] }, '2026-11-02', { correctable: true });
  assert.match(html, /Order 10002 op 10/);
  assert.match(html, /data-correct="e2"/);
  assert.doesNotMatch(html, /data-correct="e1"/);
  assert.match(html, /title="At the customer">late/);
  assert.match(html, /<b>8 h<\/b>/);
  assert.match(html, /<b>12 h<\/b>/);
  assert.match(html, /Corrections \(2\)/);
  assert.match(html, /One hour was cleanup/);
  assert.match(weekGridHtml(null, '2026-11-02'), /No time recorded this week/);
});

test('order hours are compared with shop-floor labor and differences of a quarter hour or more are flagged', async t => {
  installBrowser(t);
  const { reconciliationHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/timesheet.js');
  const html = reconciliationHtml([
    { workDate: '2026-11-02', orderNumber: 10002, sequence: 10, timesheetHours: 6, shopFloorHours: 5.5, difference: 0.5 },
    { workDate: '2026-11-03', orderNumber: 10002, sequence: 20, timesheetHours: 2, shopFloorHours: 2, difference: 0 },
  ]);
  assert.match(html, /<span class="pill warn">0.5 h<\/span>/);
  assert.match(html, /10002 op 20/);
  assert.match(reconciliationHtml([]), /No order time this week/);
});

test('the job cost compares each element with the standard of what the order produced and shows charged hours', async t => {
  installBrowser(t);
  const { jobCostHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/costing.js');
  const html = jobCostHtml({
    orderNumber: 10002, partNumber: 'ROLL-100', status: 'Completed', currency: 'USD', orderQuantity: 100, goodReceived: 96, scrapReported: 4, unitStandard: 20,
    elements: [{ element: 'Material', actual: 1000, standard: 960, variance: 40 }, { element: 'Labor', actual: 380, standard: 384, variance: -4 }, { element: 'Overhead', actual: 570, standard: 576, variance: -6 }],
    actualTotal: 1950, standardOfOutput: 1920, variance: 30, workInProcess: null, shopFloorHours: 9.5, timesheetHours: 10, timesheetHoursApproved: 10,
    material: [{ partNumber: 'COIL-AL', issued: 500, returned: 0, credited: 0, value: 1000 }], labor: [], notes: ['The order is finished.'],
  });
  assert.match(html, /\+40\.00 USD/);
  assert.match(html, /c-ok">-4\.00 USD/);
  assert.match(html, /\+30\.00 USD/);
  assert.match(html, /timesheets 10/);
  assert.match(html, /COIL-AL/);
});
