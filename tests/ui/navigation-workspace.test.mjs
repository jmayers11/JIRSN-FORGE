import assert from 'node:assert/strict';
import test from 'node:test';
import { filterNavGroups } from '../../src/Forge.Host/wwwroot/js/navigation.mjs';
import { tabifyPanels, enhanceTable } from '../../src/Forge.Host/wwwroot/js/ui.js';
import { installBrowser, fire } from './browser-harness.mjs';

test('navigation defaults to permitted routes and can reveal every module', () => {
  const groups = [['Make', ['shop-floor', 'inventory', 'tools']], ['Quality', ['quality', 'qms']]];
  const access = { 'shop-floor': ['floor.read'], inventory: ['inventory.read'], tools: ['tools.read'], quality: ['quality.read'], qms: ['qms.read'] };
  const filtered = filterNavGroups(groups, access, permission => permission === 'floor.read' || permission === 'quality.read' || permission === 'tools.read');
  assert.deepEqual(filtered, [['Make', ['shop-floor', 'tools']], ['Quality', ['quality']]]);
  assert.deepEqual(filterNavGroups(groups, access, () => false, true), groups);
});

test('tabbed workspaces preserve summary content and switch panels with keyboard navigation', t => {
  const { document } = installBrowser(t);
  const root = document.createElement('main'); document.body.append(root);
  root.innerHTML = '<section class="page-head"><h1>Order 42</h1></section><section class="panel"><h2>Readiness now</h2></section><section class="panel"><h2>What became of the material</h2></section><section class="panel"><h2>Documentation</h2></section>';
  const workspace = tabifyPanels(root, [
    { label: 'Summary', match: ['readiness'] }, { label: 'Material', match: ['material'] }, { label: 'Documents', match: ['documentation'] },
  ]);
  assert.ok(workspace);
  assert.equal(root.querySelector('.page-head').textContent, 'Order 42');
  const tabs = root.querySelectorAll('[data-workspace-tab]'), panels = root.querySelectorAll('[data-workspace-panel]');
  assert.equal(tabs.length, 3); assert.equal(panels.length, 3);
  assert.equal(panels[0].hidden, false); assert.equal(panels[1].hidden, true);
  fire(tabs[0], 'keydown', { key: 'ArrowRight' });
  assert.equal(tabs[1].getAttribute('aria-selected'), 'true');
  assert.equal(panels[1].hidden, false); assert.equal(panels[0].hidden, true);
  fire(tabs[2], 'click');
  assert.equal(panels[2].hidden, false); assert.equal(tabs[2].tabIndex, 0);
});

test('enhanced tables filter rows and sort numerically with keyboard accessible headings', t => {
  const { document } = installBrowser(t);
  const host = document.createElement('section'); document.body.append(host);
  host.innerHTML = '<table class="table"><thead><tr><th>Order</th><th>Part</th></tr></thead><tbody><tr><td>#12</td><td>Beta</td></tr><tr><td>#3</td><td>Alpha</td></tr></tbody></table>';
  const table = host.querySelector('table');
  assert.equal(enhanceTable(table), true);
  assert.equal(enhanceTable(table), false);
  const [orderHead] = table.querySelectorAll('th');
  fire(orderHead, 'keydown', { key: 'Enter' });
  const rows = table.querySelectorAll('tbody tr');
  assert.equal(rows[0].children[0].textContent, '#3');
  assert.equal(orderHead.getAttribute('aria-sort'), 'ascending');
  const filter = host.querySelector('[data-table-filter]');
  filter.value = 'Alpha'; fire(filter, 'input');
  const filteredRows = table.querySelectorAll('tbody tr');
  assert.equal(filteredRows[0].hidden, false); assert.equal(filteredRows[1].hidden, true);
});
