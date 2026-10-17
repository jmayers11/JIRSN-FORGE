import assert from 'node:assert/strict';
import test from 'node:test';
import { appendGoals } from '../../src/Forge.Host/wwwroot/js/goals.js';
import { installBrowser, response } from './browser-harness.mjs';

test('Goals panel reads and distinguishes released-order goal risk from behind-plan status', async t => {
  const browser = installBrowser(t), calls = [], host = { appendChild: element => browser.document.body.append(element) };
  globalThis.fetch = path => {
    calls.push(path);
    if (path === '/api/planning/goals') return Promise.resolve(response([]));
    if (path === '/api/orders/') return Promise.resolve(response([]));
    if (path === '/api/planning/demand') return Promise.resolve(response([]));
    if (path === '/api/planning/goal-risk') return Promise.resolve(response([{ orderNumber: 44, partNumber: 'PART-A', dueDate: '2026-10-20', projectedCompletion: null, state: 'Unschedulable', atRisk: true, behindPlan: true, evidence: ['No executable schedule found.'] }]));
    return Promise.resolve(response({ detail: 'Unexpected request.' }, 404));
  };

  await appendGoals(host, { personId: 'planner' });
  const section = browser.document.querySelector('.panel');
  assert.ok(calls.includes('/api/planning/goal-risk'));
  assert.match(section.innerHTML, /Released-order goal risk/);
  assert.match(section.innerHTML, /Unschedulable/);
  assert.match(section.innerHTML, /No executable schedule found/);
  assert.match(section.innerHTML, /Behind plan/);
});
