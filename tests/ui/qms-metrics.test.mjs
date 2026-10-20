import assert from 'node:assert/strict';
import test from 'node:test';
import { metricDashboardRows, metricValueFor, qmsMetricsPage } from '../../src/Forge.Host/wwwroot/js/pages/qms-metrics.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, fire, flush } from './browser-harness.mjs';

test('dashboard withholds partial and human-assessment values from calculated KPI display', () => {
  assert.equal(metricValueFor({ calculated: true, status: 'Complete', unit: 'EA', measurement: { availability: 'Complete', actual: 12 } }), '12 EA');
  assert.equal(metricValueFor({ calculated: true, status: 'Partial', unit: 'EA', measurement: { availability: 'Partial', actual: 12 } }), 'Not available');
  assert.equal(metricValueFor({ calculated: false, status: 'AssessmentRequired', unit: 'score', measurement: null }), 'Human assessment required');
  const html = metricDashboardRows({ items: [
    { code: 'PARTIAL', revision: 1, name: 'Partial cohort', calculated: true, status: 'Partial', unit: 'EA', measurement: { availability: 'Partial', actual: 99, basis: 'Cohort unavailable' }, numeratorDefinition: 'Good', denominatorDefinition: 'Not a ratio', cohortBasis: 'Exact order', objectives: [{ code: 'OBJECTIVE', revision: 2, title: '<img src=x onerror=alert(1)>', comparison: 'AtLeast', target: 100, upperTarget: null, tolerance: 0, status: 'NotEvaluated' }] },
    { code: 'MANUAL', revision: 1, name: 'Manual review', calculated: false, status: 'AssessmentRequired', unit: 'score', measurement: null, numeratorDefinition: 'Human assessed', denominatorDefinition: 'Human defined', cohortBasis: 'Approved review' },
  ] });
  assert.match(html, /Not available/); assert.match(html, /Human assessment required/); assert.doesNotMatch(html, /99/);
  assert.match(html, /NotEvaluated/); assert.match(html, /&lt;img src=x onerror=alert\(1\)&gt;/); assert.doesNotMatch(html, /<img src=x/);
});

test('metrics page requests an explicit inclusive/exclusive period and renders returned authorized results', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  browser.document.body.append(page);
  const old = { subject: session.subject, token: session.token }; session.subject = 'metrics-user'; session.token = null; t.after(() => Object.assign(session, old));
  const calls = [];
  globalThis.fetch = (path, options = {}) => {
    calls.push({ path, options });
    return Promise.resolve(response({
      from: '2026-10-01T00:00:00Z', to: '2026-10-02T00:00:00Z', capturedAt: '2026-10-02T00:00:01Z',
      definitionsComplete: true, calculatedMetricsComplete: true, nextCursor: null,
      items: [{ code: 'GOOD.1', revision: 2, name: 'Recorded good', sourceKind: 'RecordedProductionReports', unit: 'EA', numeratorDefinition: 'Recorded good', denominatorDefinition: 'No ratio', measurementMethod: 'Exact reports', cohortBasis: 'Order 17, op 10', status: 'Complete', calculated: true, measurement: { availability: 'Complete', actual: 24, from: '2026-10-01T00:00:00Z', to: '2026-10-02T00:00:00Z', basis: 'Complete declared cohort' }, objectives: [{ code: 'OBJ.1', revision: 1, title: 'Good target', comparison: 'AtLeast', target: 20, upperTarget: null, tolerance: 0, status: 'OnTarget' }] }],
    }));
  };
  const controller = await qmsMetricsPage(page); t.after(controller.dispose);
  assert.match(page.innerHTML, /From, inclusive/); assert.match(page.innerHTML, /To, exclusive/);
  assert.match(page.querySelector('[data-from]').value, /^\d{4}-\d{2}-\d{2}T/);
  assert.match(page.querySelector('[data-to]').value, /^\d{4}-\d{2}-\d{2}T/);
  await flush(); await flush();
  assert.equal(calls.length, 1, 'the default 30-day window loads on entry');
  assert.match(calls[0].path, /metrics\/dashboard/);
  page.querySelector('[data-from]').value = '2026-10-01T00:00'; page.querySelector('[data-to]').value = '2026-10-02T00:00';
  fire(page.querySelector('[data-metric-period]'), 'submit'); await flush();
  assert.equal(calls.length, 2); assert.match(calls[1].path, /metrics\/dashboard/);
  assert.ok(calls[1].path.includes('from=' + encodeURIComponent(new Date('2026-10-01T00:00').toISOString())));
  assert.ok(calls[1].path.includes('to=' + encodeURIComponent(new Date('2026-10-02T00:00').toISOString())));
  assert.match(page.innerHTML, /Recorded good/); assert.match(page.innerHTML, /24 EA/); assert.match(page.innerHTML, /Complete declared cohort/); assert.match(page.innerHTML, /OnTarget/);
});
