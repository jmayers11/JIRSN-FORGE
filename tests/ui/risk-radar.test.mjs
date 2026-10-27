import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';

const radar = view => ({
  view, today: '2026-10-27', timeZone: 'America/New_York', outsideView: view === 'current' ? 2 : 0,
  bands: [{ band: 'High', count: 1, rule: 'Impact today up to 6 days out' }, { band: 'Medium', count: view === 'current' ? 0 : 1, rule: 'Impact 7 to 29 days out' }, { band: 'Low', count: 0, rule: 'Impact 30 days out or later' }],
  items: [
    { band: 'High', daysOut: 0, impactAt: '2026-10-27T15:00:00Z', impactBasis: 'Open NCR holds the order now',
      signal: { category: 'QualityNcr', state: 'Open', causeState: 'Unattributed', links: [{ targetType: 'Order', targetId: '7' }, { targetType: 'Operation', targetId: '7/10' }, { targetType: 'Part', targetId: 'ROLL-100' }] } },
    ...(view === 'current' ? [] : [{ band: 'Medium', daysOut: 9, impactAt: '2026-11-05T12:00:00Z', impactBasis: 'Approved schedule start of the affected operation',
      signal: { category: 'ReadinessBlocker', state: 'Blocked', causeState: 'Unattributed', links: [{ targetType: 'Operation', targetId: '8/20' }] } }]),
  ],
  evidence: { read: { readCompletedAt: '2026-10-27T15:00:01Z' }, limitations: ['A shared order does not prove a shared cause.'],
    sources: [{ code: 'readiness', status: 'Included' }, { code: 'quality', status: 'NotAuthorized' }] },
});

test('the risk radar shows High, Medium and Low lanes, switches between today, 7 and 30 days, and links each card to its source', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  const before = { subject: session.subject, token: session.token }; session.subject = 'radar-user'; session.token = null; t.after(() => Object.assign(session, before));
  globalThis.fetch = path => { calls.push(path); const view = new URL(path, 'http://x').searchParams.get('view'); return Promise.resolve(response(radar(view))); };
  const { riskRadarPage } = await import('../../src/Forge.Host/wwwroot/js/pages/risk-radar.js');

  await riskRadarPage(page, { query: new URLSearchParams(), operationalVisualizationsEnabled: true });
  assert.equal(calls.at(-1), '/api/planning/risk-radar?view=current');
  assert.deepEqual([...page.querySelectorAll('.risk-lane')].map(lane => lane.dataset.band), ['High', 'Medium', 'Low']);
  const high = page.querySelector('.risk-lane[data-band="High"]');
  assert.match(high.textContent, /Open NCR/);
  assert.match(high.textContent, /Today/);
  assert.ok(high.querySelector('a[href="#/orders/7"]'));
  assert.match(page.textContent, /2 later signal\(s\) are outside this view/);
  assert.match(page.textContent, /cannot read them: quality/);

  fire(page.querySelector('[data-view="30d"]'), 'click'); await flush(); await flush();
  assert.equal(calls.at(-1), '/api/planning/risk-radar?view=30d');
  const medium = page.querySelector('.risk-lane[data-band="Medium"]');
  assert.match(medium.textContent, /Operation blocked/);
  assert.match(medium.textContent, /9 days/);
  assert.ok(medium.querySelector('a[href="#/orders/8"]'));
});
