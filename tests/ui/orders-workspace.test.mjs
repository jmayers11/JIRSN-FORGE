import assert from 'node:assert/strict';
import test from 'node:test';
import { fetchFile } from '../../src/Forge.Host/wwwroot/js/api.js';
import { bindOrderSearch, instructionRevision, instructionsForOperation, isPrintableInstruction, manufacturingFlowState, manufacturingFlowTracks, orderReadinessRows, orderSiteName, renderManufacturingFlow } from '../../src/Forge.Host/wwwroot/js/pages/orders.js';
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

test('manufacturing flow separates the released route from approved rework tracks', () => {
  const order = { operations: [
    { sequence: 30, reworkId: 7 }, { sequence: 20, reworkId: null },
    { sequence: 10, reworkId: null }, { sequence: 40, reworkId: 7 }, { sequence: 50, reworkId: 8 },
  ] };
  const tracks = manufacturingFlowTracks(order);
  assert.deepEqual(tracks.map(track => [track.label, track.operations.map(operation => operation.sequence)]), [
    ['Released routing', [10, 20]], ['Approved rework plan 7', [30, 40]], ['Approved rework plan 8', [50]],
  ]);
});

test('manufacturing flow resolves site through the configured work-center safety area', () => {
  const order = { operations: [{ workCenterCode: 'SLIT' }] };
  const resources = { data: {
    sites: [{ code: 'WW', name: 'WhiskerWorks' }],
    safetyAreas: [{ code: 'PRODUCTION', siteCode: 'WW' }],
    workCenters: [{ code: 'SLIT', safetyAreaCode: 'PRODUCTION' }],
  } };
  assert.equal(orderSiteName(order, resources), 'WhiskerWorks');
  assert.equal(orderSiteName(order, { error: new Error('forbidden') }), 'Unknown — site records unavailable');
  assert.equal(orderSiteName(order, { data: { sites: [], safetyAreas: [], workCenters: [] } }), 'Unknown — no site mapping');
});

test('manufacturing flow states keep blockers, waiting predecessors, and missing evidence distinct', () => {
  assert.deepEqual(manufacturingFlowState({ status: 'Complete' }, null), { key: 'complete', label: 'Complete' });
  assert.deepEqual(manufacturingFlowState({ status: 'Pending' }, { executable: false, primaryConstraint: 'Predecessor' }), { key: 'waiting', label: 'Waiting' });
  assert.deepEqual(manufacturingFlowState({ status: 'Pending' }, { executable: false, primaryConstraint: 'Quality' }), { key: 'blocked', label: 'Blocked' });
  assert.deepEqual(manufacturingFlowState({ status: 'Pending' }, { executable: true, notEvaluated: ['Quality'] }), { key: 'unknown', label: 'Unknown' });
  assert.deepEqual(manufacturingFlowState({ status: 'Pending' }, null), { key: 'unknown', label: 'Unknown' });
});

test('order readiness table does not show unevaluated inputs as executable passes', () => {
  const order = { operations: [
    { sequence: 10, status: 'Pending' }, { sequence: 20, status: 'Pending' },
    { sequence: 30, status: 'Complete' }, { sequence: 40, status: 'InProgress' },
  ] };
  const html = orderReadinessRows(order, { operations: [
    { sequence: 10, description: 'Cut', executable: true, notEvaluated: ['Quality'], dimensions: [] },
    { sequence: 20, description: 'Inspect', executable: false, primaryConstraint: 'Predecessor', notEvaluated: [], dimensions: [] },
    { sequence: 30, description: 'Pack', executable: true, notEvaluated: [], dimensions: [] },
    { sequence: 40, description: 'Run', executable: false, primaryConstraint: 'Equipment', notEvaluated: [], dimensions: [] },
  ] });
  assert.match(html, /Unknown/);
  assert.match(html, /Unknown, not evaluated: Quality/);
  assert.match(html, /Waiting/);
  assert.match(html, /Complete/);
  assert.match(html, /Running/);
  assert.doesNotMatch(html, /Executable/);
});

test('manufacturing flow explains a blocker and identifies downstream operations', () => {
  const order = { orderNumber: 42, partNumber: 'BRACKET', revision: 'B', status: 'Released', quantity: 12, operations: [
    { sequence: 10, description: 'Cut', workCenterCode: 'CUT', activityCode: 'SAW', eligibleMachines: ['SAW-1'], status: 'Pending', goodQuantity: 0, scrapQuantity: 0, plannedMinutes: 60 },
    { sequence: 20, description: 'Inspect', workCenterCode: 'QC', activityCode: 'INSPECT', eligibleMachines: ['QC-1'], status: 'Pending', goodQuantity: 0, scrapQuantity: 0, plannedMinutes: 30 },
  ] };
  const readiness = { operations: [
    { sequence: 10, executable: false, primaryConstraint: 'Equipment', bestMachine: 'SAW-1', notEvaluated: [], dimensions: [{ name: 'Equipment', outcome: 'Fail', detail: 'SAW-1 has a recorded downtime window.' }] },
    { sequence: 20, executable: false, primaryConstraint: 'Predecessor', notEvaluated: [], dimensions: [{ name: 'Predecessor', outcome: 'Fail', detail: 'Operation 10 is incomplete.' }] },
  ] };
  const html = renderManufacturingFlow(order, readiness, null, '2026-10-27T13:00:00Z', null, 'WhiskerWorks');
  assert.match(html, /SAW-1 has a recorded downtime window/);
  assert.match(html, /Downstream impact:.*OP 20/);
  assert.match(html, /Review machine readiness/);
  assert.match(html, /Oct 27, 9:00 AM/);
  assert.match(html, /Site: WhiskerWorks · Current horizon · Shift scope: not filtered/);
});

test('manufacturing flow exposes the current crew and does not draw a branch from rework to the released route', () => {
  const order = { orderNumber: 5, partNumber: 'PART', revision: 'A', status: 'Released', quantity: 2, operations: [
    { sequence: 10, description: 'Build', workCenterCode: 'ASM', activityCode: 'BUILD', eligibleMachines: ['ASM-1'], status: 'InProgress', goodQuantity: 1, scrapQuantity: 0, plannedMinutes: 40 },
    { sequence: 20, description: 'Rework', workCenterCode: 'ASM', activityCode: 'REPAIR', eligibleMachines: ['ASM-1'], status: 'Pending', goodQuantity: 0, scrapQuantity: 0, plannedMinutes: 15, reworkId: 4 },
  ] };
  const html = renderManufacturingFlow(order, { operations: [
    { sequence: 10, executable: true, notEvaluated: [], dimensions: [] },
    { sequence: 20, executable: true, notEvaluated: [], dimensions: [] },
  ] }, null, null, { labor: [{ sequence: 10, personId: '00000000-0000-0000-0000-000000000001', role: 'Operator', machineCode: 'ASM-1' }] });
  assert.match(html, /Active crew/);
  assert.match(html, /Approved rework plan 4/);
  assert.match(html, /does not provide a cross-track link/);
});

test('order workspace keeps Flow, Operations and Bill of materials in separate accessible tabs', t => {
  const { document } = installBrowser(t);
  const root = document.createElement('main'); document.body.append(root);
  root.innerHTML = '<section class="page-head"><h1>Order 42</h1></section><section class="panel"><h2>Readiness now</h2></section><section class="panel"><h2>Manufacturing flow</h2></section><section class="panel"><h2>Operations</h2></section><section class="panel"><h2>Bill of materials</h2></section><section class="panel"><h2>Applicable documentation</h2></section>';
  const tabs = tabifyPanels(root, [
    { label: 'Flow', match: ['manufacturing flow'] }, { label: 'Summary', match: ['readiness'] }, { label: 'Operations', match: ['operations'] },
    { label: 'Bill of materials', match: ['bill of materials'] }, { label: 'Documents', match: ['documentation'] },
  ]);
  assert.ok(tabs);
  assert.deepEqual(tabs.querySelectorAll('[data-workspace-tab]').map(tab => tab.textContent), ['Flow', 'Summary', 'Operations', 'Bill of materials', 'Documents']);
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
