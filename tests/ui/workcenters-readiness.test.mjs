import assert from 'node:assert/strict';
import test from 'node:test';
import { workCentersPage } from '../../src/Forge.Host/wwwroot/js/pages/workcenters.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

test('work center capacity links to a current per-machine readiness evaluation and its evidence', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); page.appendChild = element => page.append(element); browser.document.body.append(page);
  const before = { subject: session.subject, token: session.token }; session.subject = 'machine-readiness-user'; session.token = null; t.after(() => Object.assign(session, before));
  globalThis.fetch = path => {
    calls.push(path);
    if (path === '/api/resources/') return Promise.resolve(response({ sites: [{ code: 'PLANT', name: 'Plant' }], safetyAreas: [{ code: 'AREA', name: 'Area' }], workCenters: [{ code: 'WC1', name: 'Cutting', safetyAreaCode: 'AREA' }], machines: [{ code: 'M1', name: 'Slitter', workCenterCode: 'WC1' }], workCenterShifts: [], shifts: [], activityTypes: [] }));
    if (path.startsWith('/api/planning/capacity?')) return Promise.resolve(response({ confidence: 'Recorded setup and presence evidence.', output: { confidence: 'Recorded setup and presence evidence.', machines: [{ machineCode: 'M1', workCenterCode: 'WC1', activityCode: 'SLIT', workingHours: 8, executableHours: 6, intervals: [] }] } }));
    if (path.startsWith('/api/readiness/machines/M1?activity=SLIT')) return Promise.resolve(response({ output: { executable: false, primaryConstraint: 'Workforce', dimensions: [{ name: 'Equipment', outcome: 'Pass', detail: 'No recorded downtime on M1.' }, { name: 'Workforce', outcome: 'Fail', detail: 'No qualified operator is present.' }], notEvaluated: ['Material', 'Quality'] } }));
    return Promise.resolve(response([]));
  };

  await workCentersPage(page, { params: ['WC1'], me: { personId: 'viewer', permissions: [] } });
  fire(page.querySelector('[data-readiness-check="0"]'), 'click'); await flush();
  assert.ok(calls.some(path => path.startsWith('/api/readiness/machines/M1?activity=SLIT')));
  const result = page.querySelector('[data-readiness-result="0"]');
  assert.match(result.innerHTML, /Blocked: Workforce/);
  assert.match(result.innerHTML, /No qualified operator is present/);
  assert.match(result.innerHTML, /Not evaluated: Material, Quality/);
});

test('work centers open as tabs: the list first, each other view in its own tab instead of one long page', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); page.appendChild = element => page.append(element); browser.document.body.append(page);
  const before = { subject: session.subject, token: session.token }; session.subject = 'wc-tabs-user'; session.token = null; t.after(() => Object.assign(session, before));
  globalThis.fetch = path => {
    if (path === '/api/resources/') return Promise.resolve(response({ sites: [{ code: 'PLANT', name: 'Plant', timeZone: 'UTC' }], safetyAreas: [{ code: 'AREA', name: 'Area' }],
      workCenters: [{ code: 'WC1', name: 'Cutting', safetyAreaCode: 'AREA' }, { code: 'WC2', name: 'Packing', safetyAreaCode: 'AREA' }], machines: [{ code: 'M1', name: 'Slitter', workCenterCode: 'WC1' }], workCenterShifts: [], shifts: [], activityTypes: [] }));
    return Promise.resolve(response([]));
  };

  await workCentersPage(page, { params: [], query: new URLSearchParams(), me: { personId: 'viewer', permissions: [] } });
  const tabs = [...page.querySelectorAll('[data-workspace-tab]')].map(b => b.dataset.workspaceTab);
  assert.deepEqual(tabs, ['list', 'readiness', 'support', 'tools', 'holds']);
  const shown = [...page.querySelectorAll('[data-workspace-panel]')].filter(p => !p.hidden).map(p => p.dataset.workspacePanel);
  assert.deepEqual(shown, ['list']);
  assert.equal(page.querySelectorAll('.wc-card-grid .wc-card[data-wc]').length, 2);
  assert.equal(page.querySelector('table'), null, 'the list is a compact card grid, not a long table');
  assert.equal(page.querySelectorAll('[data-wc-picker] option').length, 3);
  fire(page.querySelector('[data-workspace-tab="tools"]'), 'click');
  assert.deepEqual([...page.querySelectorAll('[data-workspace-panel]')].filter(p => !p.hidden).map(p => p.dataset.workspacePanel), ['tools']);

  page.innerHTML = '';
  await workCentersPage(page, { params: ['WC1'], query: new URLSearchParams('focus=holds'), me: { personId: 'viewer', permissions: [] } });
  assert.match(page.querySelector('h1').textContent, /Cutting/);
  assert.equal(page.querySelector('[data-workspace-tab]').dataset.workspaceTab, 'overview');
  assert.deepEqual([...page.querySelectorAll('[data-workspace-panel]')].filter(p => !p.hidden).map(p => p.dataset.workspacePanel), ['holds']);
});
