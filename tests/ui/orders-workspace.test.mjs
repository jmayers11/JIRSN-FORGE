import assert from 'node:assert/strict';
import test from 'node:test';
import { fetchFile } from '../../src/Forge.Host/wwwroot/js/api.js';
import { bindOrderSearch, instructionRevision, instructionsForOperation, isPrintableInstruction } from '../../src/Forge.Host/wwwroot/js/pages/orders.js';
import { enhanceTable, tabifyPanels } from '../../src/Forge.Host/wwwroot/js/ui.js';
import { fire, installBrowser } from './browser-harness.mjs';

test('production order search works with one row and announces its result count', t => {
  const { document } = installBrowser(t);
  const root = document.createElement('main'); document.body.append(root);
  root.innerHTML = `<label>Search<input data-order-search-input></label><span data-order-search-count aria-live="polite"></span>
    <table><tbody><tr data-order-search-row data-order-search="#42 bracket rev c released"><td>#42 Bracket C</td></tr></tbody></table>
    <div data-order-search-empty hidden>No matching orders</div>`;
  assert.equal(bindOrderSearch(root), 1);
  const row = root.querySelector('[data-order-search-row]');
  const input = root.querySelector('[data-order-search-input]');
  assert.equal(root.querySelector('[data-order-search-count]').textContent, '1 order');
  input.value = 'bracket'; fire(input, 'input');
  assert.equal(row.hidden, false);
  assert.equal(root.querySelector('[data-order-search-count]').textContent, '1 of 1 orders');
  input.value = 'cancelled'; fire(input, 'input');
  assert.equal(row.hidden, true);
  assert.equal(root.querySelector('[data-order-search-empty]').hidden, false);
  input.value = ''; fire(input, 'input');
  assert.equal(row.hidden, false);
  assert.equal(root.querySelector('[data-order-search-empty]').hidden, true);
});

test('operation work-instruction selection includes item-wide and matching operation documents only', () => {
  const docs = [
    { documentNumber: 'WI-GLOBAL', operationSequence: null },
    { documentNumber: 'WI-10', operationSequence: 10 },
    { documentNumber: 'WI-20', operationSequence: 20 },
  ];
  assert.deepEqual(instructionsForOperation(docs, 10).map(doc => doc.documentNumber), ['WI-GLOBAL', 'WI-10']);
});

test('released order work-instruction revision takes precedence over current and pinned revisions', () => {
  assert.equal(instructionRevision({ atRelease: 'B', applicable: 'C', pinnedRevision: 'A' }), 'B');
  assert.equal(instructionRevision({ applicable: 'C', pinnedRevision: 'A' }), 'C');
  assert.equal(instructionRevision({ pinnedRevision: 'A' }), 'A');
});

test('order workspace keeps Operations and Bill of materials in separate accessible tabs', t => {
  const { document } = installBrowser(t);
  const root = document.createElement('main'); document.body.append(root);
  root.innerHTML = '<section class="page-head"><h1>Order 42</h1></section><section class="panel"><h2>Readiness now</h2></section><section class="panel"><h2>Operations</h2></section><section class="panel"><h2>Bill of materials</h2></section><section class="panel"><h2>Applicable documentation</h2></section>';
  const tabs = tabifyPanels(root, [
    { label: 'Summary', match: ['readiness'] }, { label: 'Operations', match: ['operations'] },
    { label: 'Bill of materials', match: ['bill of materials'] }, { label: 'Documents', match: ['documentation'] },
  ]);
  assert.ok(tabs);
  assert.deepEqual(tabs.querySelectorAll('[data-workspace-tab]').map(tab => tab.textContent), ['Summary', 'Operations', 'Bill of materials', 'Documents']);
});

test('shared table sorting can be enabled without adding a duplicate filter to the order search', t => {
  const { document } = installBrowser(t);
  const host = document.createElement('section'); document.body.append(host);
  host.innerHTML = '<table class="table"><thead><tr><th>Order</th><th>Part</th></tr></thead><tbody><tr><td>#12</td><td>Beta</td></tr><tr><td>#3</td><td>Alpha</td></tr></tbody></table>';
  const table = host.querySelector('table');
  assert.equal(enhanceTable(table, { filter: false }), true);
  assert.equal(host.querySelector('[data-table-filter]'), null);
  assert.equal(table.querySelector('th').tabIndex, 0);
});

test('protected-file reader preserves the server filename for print and preview callers', async t => {
  const { } = installBrowser(t);
  globalThis.fetch = async () => ({
    ok: true, status: 200, blob: async () => new Blob(['pdf-bytes']),
    headers: { get: name => name.toLowerCase() === 'content-disposition' ? "attachment; filename*=UTF-8''WI-42%20Rev%20B.pdf" : null },
  });
  const file = await fetchFile('/api/engineering/documents/WI-42/revisions/B/file', 'fallback.pdf');
  assert.equal(file.name, 'WI-42 Rev B.pdf');
  assert.equal(await file.blob.text(), 'pdf-bytes');
});

test('only current, pinned or legitimately superseded work-instruction revisions can be printed for the floor', () => {
  for (const state of ['Current', 'Pinned', 'LegitimatelySuperseded']) assert.equal(isPrintableInstruction({ state }), true, state);
  for (const state of ['Mismatch', 'Obsolete', 'NotReleased', 'Missing', 'RequiresReview', undefined]) assert.equal(isPrintableInstruction({ state }), false, String(state));
});
