import assert from 'node:assert/strict';
import test from 'node:test';
import { operationsVisualizationsPage } from '../../src/Forge.Host/wwwroot/js/pages/operations-visualizations.js';
import { installBrowser } from './browser-harness.mjs';

const allPermissions = [
  'execution.orders.read', 'planning.readiness.read', 'workforce.supervise', 'workforce.read',
  'planning.schedule.read', 'inventory.read',
];

test('operations visualizations hub links to all seven existing views and gives workspace navigation cues', t => {
  const { document } = installBrowser(t);
  const page = document.createElement('main'); document.body.append(page);
  operationsVisualizationsPage(page, { me: { permissions: allPermissions }, operationalVisualizationsEnabled: true });

  assert.equal(page.querySelectorAll('[data-visualization]').length, 7);
  assert.ok(page.querySelector('a[href="#/risk-radar"]'));
  assert.ok(page.querySelector('a[href="#/workforce-kanban"]'));
  assert.ok(page.querySelector('a[href="#/orders"]'));
  assert.ok(page.querySelector('a[href="#/performance?tab=coverage"]'));
  assert.ok(page.querySelector('a[href="#/scenarios"]'));
  assert.ok(page.querySelector('a[href="#/inventory?tab=map"]'));
  assert.match(page.textContent, /Choose an order → Overview → flow map/);
  assert.match(page.textContent, /Run a scenario → select recovery options → compare/);
});

test('hub only lists views the current role can open and honors the visualization feature flag', t => {
  const { document } = installBrowser(t);
  const page = document.createElement('main'); document.body.append(page);
  operationsVisualizationsPage(page, { me: { permissions: allPermissions }, operationalVisualizationsEnabled: false });

  assert.deepEqual(page.querySelectorAll('[data-visualization]').map(card => card.dataset.visualization), ['Interactive plant floor', 'Work-order readiness']);
  assert.match(page.textContent, /Some operational visualizations are disabled/);
  assert.doesNotMatch(page.innerHTML, /risk-radar|tab=map|tab=coverage|#\/scenarios/);
});

test('readiness readers can discover Risk Radar and the existing recovery workspace', t => {
  const { document } = installBrowser(t);
  const page = document.createElement('main'); document.body.append(page);
  operationsVisualizationsPage(page, { me: { permissions: ['planning.readiness.read'] }, operationalVisualizationsEnabled: true });

  assert.deepEqual(page.querySelectorAll('[data-visualization]').map(card => card.dataset.visualization), ['Production risk radar', 'Production recovery comparison']);
});
