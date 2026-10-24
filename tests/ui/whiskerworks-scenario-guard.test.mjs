import assert from 'node:assert/strict';
import test from 'node:test';
import { scenariosPage } from '../../src/Forge.Host/wwwroot/js/pages/scenarios.js';
import { installBrowser, response } from './browser-harness.mjs';

async function renderWithRoster(t, count, permissions = []) {
  const browser = installBrowser(t), root = browser.document.createElement('div');
  browser.document.body.append(root);
  const centers = Array.from({ length: 16 }, (_, index) => ({ code: `WC-${index + 1}`, name: `Center ${index + 1}` }));
  const items = Array.from({ length: 100 }, (_, index) => ({ partNumber: `WW-${String(index + 1).padStart(3, '0')}` }));
  const workers = Array.from({ length: count }, (_, index) => ({ personId: `worker-${index + 1}`, homeWorkCenterCode: centers[index % centers.length].code, shiftCode: 'SHIFT-DAY' }));
  globalThis.fetch = path => {
    if (path === '/api/planning/scenarios' || path === '/api/kernel/directory') return Promise.resolve(response([]));
    if (path === '/api/engineering/items') return Promise.resolve(response(items));
    if (path === '/api/resources/') return Promise.resolve(response({
      sites: [{ code: 'WW-P01' }], workCenters: centers,
      machines: Array.from({ length: 24 }, (_, index) => ({ code: `M-${index + 1}`, name: `Machine ${index + 1}` })),
    }));
    if (path === '/api/kernel/me') return Promise.resolve(response({ permissions }));
    if (path === '/api/workforce/workers') return Promise.resolve(response(workers));
    return Promise.resolve(response([]));
  };
  await scenariosPage(root);
  return root.innerHTML;
}

test('WhiskerWorks Scenario Center requires the 60-person Sprint 10 roster shape', async t => {
  const html = await renderWithRoster(t, 60);
  assert.match(html, /WhiskerWorks Demo Scenario Center/);
  assert.match(html, /Capture baseline snapshot/);
  assert.doesNotMatch(html, /Requires a dedicated WW-P01 demo shape/);
});

test('the former 52-person Sprint 9 roster cannot enable the expanded Sprint 10 scenario guard', async t => {
  const html = await renderWithRoster(t, 52);
  assert.match(html, /WhiskerWorks Demo Scenario Center/);
  assert.match(html, /Requires a dedicated WW-P01 demo shape/);
});

test('S05 offers its CNC-02 PM proposal only to a maintenance planner on the guarded demo shape', async t => {
  const allowed = await renderWithRoster(t, 60, ['maintenance.plan', 'maintenance.read', 'competency.read']);
  assert.match(allowed, /Propose CNC-02 PM in 4 hours/);
  assert.match(allowed, /records a current CNC-02 qualification expiring within five days/);

  const denied = await renderWithRoster(t, 60);
  assert.match(denied, /Requires maintenance.plan, maintenance.read, competency.read/);
});

test('S07 exposes the audited sensor and bearing promise action only with procurement and MRP access', async t => {
  const allowed = await renderWithRoster(t, 60, ['procurement.manage', 'procurement.read', 'material.plan.read']);
  assert.match(allowed, /Delay sensor and bearing POs by 4 days/);
  assert.match(allowed, /sensor and 608-bearing purchase lines four calendar days later/);
  assert.match(allowed, /exact PO receipts and live MRP/);

  const denied = await renderWithRoster(t, 60);
  assert.match(denied, /Requires procurement.manage, procurement.read, material.plan.read/);
});
