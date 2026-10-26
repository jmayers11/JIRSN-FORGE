import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

test('the quality trend shows equipment and material context beside each week and names no operator', async t => {
  installBrowser(t);
  const { trendHtml } = await import('../../src/Forge.Host/wwwroot/js/quality-trend.js');
  const html = trendHtml({
    partNumber: 'ROLL-100', from: '2026-08-03', to: '2026-10-25', attribution: 'Operator fault is not assigned here.',
    context: ['SLITTER-A had 1 recorded stoppage(s) (20 min) in weeks with nonconformance on this part; check whether they are related before drawing a conclusion.'],
    limitations: ['Equipment and material context shows what coincided with each week, not a confirmed cause.'],
    weeks: [
      { weekStart: '2026-10-12', good: 0, scrap: 0, yieldPercent: null, inspected: 0, failedInspections: 0, nonconformingQuantity: 0, scrapReasons: [], machines: [], materials: [], gauges: [] },
      { weekStart: '2026-10-19', good: 38, scrap: 2, yieldPercent: 95, inspected: 10, failedInspections: 1, nonconformingQuantity: 2, scrapReasons: ['EDGE: 2'], gauges: ['CAL-1'],
        machines: [{ machineCode: 'SLITTER-A', orders: 1, stoppages: 1, stoppageMinutes: 20, stoppageCategories: ['Unplanned'], maintenanceWindows: 0 }, { machineCode: 'SLITTER-B', orders: 1, stoppages: 0, stoppageMinutes: 0, stoppageCategories: [], maintenanceWindows: 1 }],
        materials: [{ componentPart: 'COIL-AL', lotNumber: 'L-17', supplierCode: 'ALCO', orders: 2, ordersWithNonconformance: 1 }, { componentPart: 'CORE-3', lotNumber: null, supplierCode: null, orders: 2, ordersWithNonconformance: 0 }] },
    ],
  });
  assert.match(html, /Operator fault is not assigned/);
  assert.match(html, /95%/);
  assert.match(html, /EDGE: 2/);
  assert.match(html, /1 stoppage\(s\), 20 min · Unplanned/);
  assert.match(html, /1 maintenance window/);
  assert.match(html, /lot L-17 · ALCO/);
  assert.match(html, /1\/2 orders with nonconformance/);
  assert.match(html, /lot not tracked/);
  assert.match(html, /before drawing a conclusion/);
  assert.doesNotMatch(html, /2026-10-12|Oct 12/, 'empty weeks are left out');
  assert.doesNotMatch(html, /operator\b(?! fault)/i, 'no operator column or score');
});
