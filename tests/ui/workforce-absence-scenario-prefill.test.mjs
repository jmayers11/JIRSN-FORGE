import assert from 'node:assert/strict';
import test from 'node:test';
import { scenariosPage } from '../../src/Forge.Host/wwwroot/js/pages/scenarios.js';
import { installBrowser, response } from './browser-harness.mjs';

const personId = '1f9a60c2-0ff8-4276-8125-f17efb69570a';

test('Kanban absence link pre-fills a private scenario without writing attendance', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div');
  browser.document.body.append(page);
  globalThis.fetch = path => {
    calls.push(path);
    if (path === '/api/planning/scenarios') return Promise.resolve(response([]));
    if (path === '/api/kernel/directory') return Promise.resolve(response([{ personId, displayName: 'Operator One' }]));
    if (path === '/api/engineering/items') return Promise.resolve(response([]));
    if (path === '/api/resources/') return Promise.resolve(response({ workCenters: [], machines: [], activityTypes: [] }));
    return Promise.resolve(response([]));
  };

  await scenariosPage(page, { query: new URLSearchParams(`kind=PersonUnavailable&personId=${personId}&scenarioName=Model+absence`), me: { personId: 'supervisor' } });

  assert.match(page.innerHTML, /Save private scenario/);
  assert.equal(page.querySelector('[name="kind"]').value, 'PersonUnavailable');
  assert.equal(page.querySelector('[name="personId"]').value, personId);
  assert.equal(page.querySelector('[name="name"]').value, 'Model absence');
  assert.equal(page.querySelector('[name="durationHours"]').value, '8');
  assert.equal(calls.some(path => path.includes('/unavailability') || path.includes('/attendance')), false);
  // A pre-filled launch opens the New scenario tab; saved scenarios and results live in their own tab.
  const shown = [...page.querySelectorAll('[data-workspace-panel]')].filter(panel => !panel.hidden).map(panel => panel.dataset.workspacePanel);
  assert.deepEqual(shown, ['new']);
  assert.deepEqual([...page.querySelectorAll('[data-workspace-tab]')].map(tab => tab.dataset.workspaceTab), ['mine', 'new']);
  assert.ok(page.querySelector('[data-workspace-panel="mine"] .list-detail-detail'));
});
