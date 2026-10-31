import assert from 'node:assert/strict';
import test from 'node:test';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, fire } from './browser-harness.mjs';

function setup(t, routes) {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const before = { subject: session.subject, token: session.token }; session.subject = 'density-user'; session.token = null; t.after(() => Object.assign(session, before));
  globalThis.fetch = path => Promise.resolve(response(routes(path) ?? []));
  return page;
}

test('integrity findings show their kinds beside the selected kind\'s findings', async t => {
  const page = setup(t, path => path === '/api/integrity/exceptions' ? [
    { kind: 'NegativeStock', summary: 'COIL-AL below zero', source: 'Inventory', observedAt: '2026-10-31T12:00:00Z', evidence: 'Balance -4 KG at STORES' },
    { kind: 'NegativeStock', summary: 'CORE-3 below zero', source: 'Inventory', observedAt: '2026-10-31T12:00:00Z', evidence: 'Balance -1 EA' },
    { kind: 'ExpiredCalibrationInUse', summary: 'TQ-7 used after due date', source: 'Tools', observedAt: '2026-10-31T12:00:00Z', evidence: 'Used 2026-10-30' },
  ] : null);
  const { integrityPage } = await import('../../src/Forge.Host/wwwroot/js/pages/integrity.js');
  await integrityPage(page, {});
  assert.deepEqual([...page.querySelectorAll('[data-kind]')].map(b => b.dataset.kind), ['NegativeStock', 'ExpiredCalibrationInUse']);
  const detail = () => page.querySelector('.list-detail-detail').textContent;
  assert.match(detail(), /COIL-AL below zero/);
  assert.doesNotMatch(detail(), /TQ-7/);
  fire(page.querySelector('[data-kind="ExpiredCalibrationInUse"]'), 'click');
  assert.match(detail(), /TQ-7 used after due date/);
  assert.doesNotMatch(detail(), /COIL-AL/);
});

test('My Work puts each queue in its own tab', async t => {
  const page = setup(t, path => path === '/api/work-inbox' ? { approvals: [{ workType: 'NCR', workId: '5', title: 'Use as is NCR 5', requester: 'qe', permission: 'quality.approve', requestedAt: '2026-10-31T12:00:00Z' }], assignments: [] } : null);
  const { workInboxPage } = await import('../../src/Forge.Host/wwwroot/js/pages/work-inbox.js');
  await workInboxPage(page, { me: { personId: 'p', permissions: [] } });
  const tabs = [...page.querySelectorAll('[data-workspace-tab]')].map(tab => tab.textContent);
  assert.deepEqual(tabs, ['Waiting for my decision (1)', 'Assigned to me (0)']);
  assert.equal([...page.querySelectorAll('[data-workspace-panel]')].filter(panel => !panel.hidden).length, 1);
});
