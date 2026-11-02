import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

test('family lines, borrowed patterns and planner adjustments are shown and explained', async t => {
  installBrowser(t);
  const { forecastHtml, setupHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/forecast.js');
  const view = { run: { runId: 4, runAt: '2026-11-02T06:00:00Z', runBy: 'person:p', simulated: false, horizon: 1, holdout: 6, intervalPercent: 80, firstMonth: '2026-11-01' },
    lines: [
      { runId: 4, simulated: false, forecast: { partNumber: 'family:FAM', method: 'HoltWinters', scores: [], months: [{ month: '2026-11-01', forecast: 45, low: 40, high: 50 }], flags: [], basis: 'Family Brackets: the total of its 2 parts' } },
      { runId: 4, simulated: false, forecast: { partNumber: 'VAR-A', method: 'HoltWinters', scores: [], basis: '60 % of family Brackets (share of the family\'s last 12 months)',
        months: [{ month: '2026-11-01', forecast: 77, low: 70, high: 80, statistical: 27, adjusted: '+50 units (Spring promotion)' }], flags: [] } }] };
  const html = forecastHtml(view, [], { canApprove: true });
  assert.match(html, /Trend and season \(Holt-Winters\)/);
  assert.match(html, /pill none">Family<\/span> FAM/);
  assert.doesNotMatch(html, /data-approve="family:FAM"/);
  assert.match(html, /data-approve="VAR-A"/);
  assert.match(html, /60 % of family Brackets/);
  assert.match(html, /method 27 · \+50 units \(Spring promotion\)/);
  const setup = setupHtml({ analogs: [{ partNumber: 'NEW', likePart: 'OLD', scale: 0.5, launchMonth: '2027-01-01' }],
    families: [{ code: 'CFG', name: 'Configured unit', driverPart: 'CONFIG', members: [{ partNumber: 'OPT-KIT', share: 2 }, { partNumber: 'OPT-HEATER', share: null }] }],
    adjustments: [{ adjustmentId: 9, partNumber: 'P', fromMonth: '2027-03-01', toMonth: null, kind: 'EndOfLife', amount: 0, reason: 'Replaced by P2', setBy: 'person:p' }] }, { canManage: true });
  assert.match(setup, /× 0.5/);
  assert.match(setup, /2 per unit/);
  assert.match(setup, /recent mix/);
  assert.match(setup, /End of life/);
  assert.match(setup, /data-cancel-adjustment="9"/);
});

test('work in progress and leftover pieces show where they come from and what they still need', async t => {
  installBrowser(t);
  const { proposalsHtml, sourcesHtml, size } = await import('../../src/Forge.Host/wwwroot/js/pages/forecast.js');
  const html = proposalsHtml([{ partNumber: 'SUB', onHand: 0, unavailable: 0, reservedForOthers: 0, free: 0, stillShort: [], ruledOut: [],
    promised: [{ wipOrderNumber: 7, consumerOrderNumber: 9, quantity: 6, fulfilled: 0 }],
    lines: [{ orderNumber: 9, dueDate: '2026-11-06', priority: 3, rank: 1, neededPart: 'SUB', shortage: 8, offeredPart: 'SUB', quantity: 2, primaryQuantity: 2, mode: 'WorkInProgress', wipOrderNumber: 7, why: 'still runs 20 Final inspection' },
      { orderNumber: 10, dueDate: '2026-11-07', priority: 3, rank: 2, neededPart: 'PLATE', shortage: 1, offeredPart: 'PLATE', quantity: 1, primaryQuantity: 1, mode: 'Primary', lotNumber: 'R-FIT', why: 'smallest that fits' }] }], { canApprove: true });
  assert.match(html, /Work in progress/);
  assert.match(html, /data-wip="7"/);
  assert.match(html, />Promise</);
  assert.match(html, /data-lot="R-FIT"/);
  assert.match(html, /6 from order 7 to order 9/);
  const sources = sourcesHtml({ workInProgress: [{ orderNumber: 7, partNumber: 'SUB', revision: 'A', status: 'Released', dueDate: '2026-11-08', projected: 10, pegged: 4, promised: 6, unpegged: 0,
    peggedTo: ['4 for ACME PO 77 (due 2026-11-12)'], remainingOperations: ['10 Assemble', '20 Final inspection'] }],
    promised: [{ allocationId: 3, wipOrderNumber: 7, consumerOrderNumber: 9, partNumber: 'SUB', quantity: 6, fulfilled: 0 }],
    setAside: [{ earmarkId: 5, orderNumber: 10, partNumber: 'PLATE', lotNumber: 'R-FIT' }],
    measured: [{ partNumber: 'PLATE', lotNumber: 'R-FIT', size: { length: 18, width: 12, thickness: null, unit: 'Inch' } }],
    pieceSizes: [{ parentPart: 'BRACKET', componentPart: 'PLATE', minimum: { length: 16, width: 10, unit: 'Inch' } }] }, { canApprove: true });
  assert.match(sources, /ACME PO 77/);
  assert.match(sources, /20 Final inspection/);
  assert.match(sources, /data-release-promise="3"/);
  assert.match(sources, /data-release-setaside="5"/);
  assert.match(sources, /18 × 12 in/);
  assert.match(sources, /16 × 10 in/);
  assert.equal(size({ length: 500, width: null, thickness: 3, unit: 'Millimetre' }), '500 × 3 mm');
});
