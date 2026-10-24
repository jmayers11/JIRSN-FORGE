import assert from 'node:assert/strict';
import test from 'node:test';
import { applyDemoOffset, demoBannerHtml, demoControlHtml, demoOffsetMs } from '../../src/Forge.Host/wwwroot/js/demo-clock.js';

// Sprint 9: the simulated demo clock is labeled everywhere and only a designated demo database moves it.

const designated = { enabled: true, designated: true, demoCode: 'WW-DEMO', siteCode: 'WW-P01', offsetSeconds: 10800, label: 'SIMULATED DEMO CLOCK',
  realNow: '2026-10-24T12:00:00Z', simulatedNow: '2026-10-24T15:00:00Z', baseline: { baselineId: 2, reason: 'Clean S01', capturedAt: '2026-10-24T12:00:00Z', simulatedAt: '2026-10-24T15:00:00Z' } };

test('the browser clock follows the server offset and returns to real time', () => {
  const real = Date.now();
  applyDemoOffset(3600);
  assert.equal(demoOffsetMs(), 3600000);
  assert.ok(Date.now() - real >= 3600000 - 1000);
  assert.ok(new Date().getTime() - real >= 3600000 - 1000);
  assert.equal(new Date('2026-01-01T00:00:00Z').toISOString(), '2026-01-01T00:00:00.000Z');
  applyDemoOffset(0);
  assert.ok(Math.abs(Date.now() - real) < 5000);
});

test('the banner names the simulated clock; a normal or undesignated database shows none', () => {
  assert.match(demoBannerHtml(designated), /SIMULATED DEMO CLOCK/);
  assert.match(demoBannerHtml(designated), /\+3 h/);
  assert.equal(demoBannerHtml({ enabled: true, designated: false }), '');
  assert.equal(demoBannerHtml(null), '');
});

test('controls follow permissions: planners advance and save, only an administrator resets', () => {
  const planner = demoControlHtml(designated, { permissions: ['demo.control'] });
  assert.match(planner, /data-demo-advance="480"/);
  assert.match(planner, /data-demo-baseline/);
  assert.match(planner, /data-demo-compare/);
  assert.doesNotMatch(planner, /data-demo-reset/);
  assert.match(demoControlHtml(designated, { permissions: ['demo.control', 'demo.reset'] }), /data-demo-reset/);
  assert.match(demoControlHtml(designated, { permissions: [] }), /not change it/);
  assert.equal(demoControlHtml({ enabled: false }, { permissions: ['demo.reset'] }), '');
  assert.match(demoControlHtml({ enabled: true, designated: false }, { permissions: ['demo.reset'] }), /data-demo-designate/);
  const compared = demoControlHtml(designated, { permissions: ['demo.control'] }, { rows: [{ metric: 'Held orders', baseline: 0, current: 2, delta: 2 }], note: 'Counts use your own read permissions.' });
  assert.match(compared, /Held orders<\/td><td class="num">0<\/td><td class="num">2<\/td>/);
  assert.match(compared, /\+2/);
});
