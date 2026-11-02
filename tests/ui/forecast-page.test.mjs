import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

test('the forecast marks simulated history and offers approval per part', async t => {
  installBrowser(t);
  const { forecastHtml, historyHtml, sparkline } = await import('../../src/Forge.Host/wwwroot/js/pages/forecast.js');
  const view = { run: { runId: 3, runAt: '2026-11-02T06:00:00Z', runBy: 'person:p', simulated: true, horizon: 2, holdout: 6, intervalPercent: 80, firstMonth: '2026-11-01' },
    lines: [{ runId: 3, simulated: true, forecast: { partNumber: 'ROLL-100', method: 'SeasonalNaive', scores: [{ method: 'SeasonalNaive', meanAbsoluteError: 4, weightedPercentError: 6, bias: 1 }, { method: 'Naive', meanAbsoluteError: 9, bias: 0 }],
      months: [{ month: '2026-11-01', forecast: 100, low: 90, high: 110 }, { month: '2026-12-01', forecast: 120, low: 105, high: 135 }], flags: [{ code: 'simulated', message: '24 month(s) are simulated history, not real demand.' }] } }] };
  const html = forecastHtml(view, [{ partNumber: 'ROLL-100', description: 'Slit roll', series: [{ month: '2026-10-01', quantity: 95 }] }], { canApprove: true });
  assert.match(html, /pill bad">Simulated history/);
  assert.match(html, /Same month last year/);
  assert.match(html, /2026-11: <b>100<\/b>/);
  assert.match(html, /runs high/);
  assert.match(html, /data-approve="ROLL-100"/);
  assert.doesNotMatch(forecastHtml({ ...view, lines: [{ ...view.lines[0], approvedAt: '2026-11-02T07:00:00Z', approvedBy: 'person:p' }] }, []), /data-approve/);
  assert.match(sparkline([{ quantity: 1 }, { quantity: 2 }], [{ forecast: 3, low: 2, high: 4 }]), /polygon/);
  assert.match(historyHtml([{ partNumber: 'ROLL-100', months: 24, total: 2400, simulatedMonths: 24, importedMonths: 0, shippedMonths: 0, flags: [] }]), /24 simulated/);
});

test('allocation proposals show rank, mode, why, and approval', async t => {
  installBrowser(t);
  const { proposalsHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/forecast.js');
  const html = proposalsHtml([{ partNumber: 'CORE-1', onHand: 9, unavailable: 0, reservedForOthers: 5, free: 4, stillShort: [], ruledOut: ['5 CORE-1 is reserved for other work and is never taken from it.'],
    lines: [{ orderNumber: 12, dueDate: '2026-11-07', priority: 3, rank: 1, neededPart: 'CORE-1', shortage: 6, offeredPart: 'CORE-EQ', quantity: 2, primaryQuantity: 2, mode: 'ReviewAlternate', why: 'Ranked 1 of 2' }] }], { canApprove: true });
  assert.match(html, /review first/);
  assert.match(html, /data-allocate="12" data-part="CORE-EQ" data-needed="CORE-1"/);
  assert.match(html, /never taken/);
});
