import assert from 'node:assert/strict';
import test from 'node:test';
import { mrpPage } from '../../src/Forge.Host/wwwroot/js/pages/mrp.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

function setup(t, { output = makeOutput(), suppliers = [], sources = [] } = {}) {
  const browser = installBrowser(t), page = browser.document.createElement('main'); browser.document.body.append(page);
  const calls = [];
  globalThis.fetch = (path, options = {}) => {
    const url = String(path), body = options.body ? JSON.parse(options.body) : null;
    calls.push({ path: url, options, body });
    if (url === '/api/planning/mrp') return Promise.resolve(response({ output, engineName: 'MRP', engineVersion: 'test', inputFingerprint: 'input-fingerprint', outputFingerprint: 'output-fingerprint' }));
    if (url === '/api/planning/demand') return Promise.resolve(response([{ demandId: 9, partNumber: 'BUY-B', quantity: 4, needDate: '2026-10-30', priority: 2, kind: 'Maintenance', sourceKey: 'work-order:17' }]));
    if (url === '/api/engineering/items') return Promise.resolve(response([]));
    if (url === '/api/procurement/suppliers') return Promise.resolve(response(suppliers));
    if (url === '/api/procurement/approved-sources') return Promise.resolve(response(sources));
    if (url === '/api/maintenance/work-orders') return Promise.resolve(response([{ order: { workOrderId: 17, title: 'Replace spindle bearings', machineCode: 'SL-02', status: 'Open' } }]));
    if (url === '/api/projects/') return Promise.resolve(response([{ project: { code: 'CAP-1', title: 'Press upgrade', status: 'Approved' } }]));
    if (url === '/api/projects/CAP-1') return Promise.resolve(response({ project: { code: 'CAP-1', title: 'Press upgrade' }, lines: [{ sourceKey: 'project:CAP-1/3', projectCode: 'CAP-1', lineNumber: 3, partNumber: 'GUARD-1', description: 'Safety enclosure', status: 'Active' }] }));
    if (url === '/api/resources/tools') return Promise.resolve(response([{ tool: { code: 'GAUGE-4', description: 'Bore gauge', status: 'Active' } }]));
    if (options.method === 'POST') return Promise.resolve(response({ succeeded: true, message: 'Saved.' }));
    return Promise.resolve(response([]));
  };
  return { browser, page, calls };
}

function makeOutput() {
  const buckets = [
    { date: '2026-10-21', gross: 3, scheduledReceipts: 0, plannedReceipts: 0, projectedAvailable: 2 },
    { date: '2026-10-22', gross: 2, scheduledReceipts: 0, plannedReceipts: 0, projectedAvailable: 0 },
    ...Array.from({ length: 14 }, (_, index) => ({ date: `2026-10-${String(23 + index).padStart(2, '0')}`, gross: 0, scheduledReceipts: 0, plannedReceipts: 0, projectedAvailable: 4 })),
  ];
  const item = (partNumber, makeBuy, available, inspection, hold) => ({
    partNumber, description: `${partNumber} description`, unitOfMeasure: 'EA', makeBuy, level: 0, leadTimeDays: 2,
    safetyStock: 3, lotPolicy: 'LotForLot', orderQuantity: 0, orderMultiple: 0, available, inspection, hold,
    requirements: [], receipts: [{ description: 'Open PO', quantity: 5 }], buckets,
  });
  const plannedOrder = (partNumber, makeBuy, pastDue, quantity) => ({
    key: `${partNumber}-key`, partNumber, makeBuy, quantity, unitOfMeasure: 'EA', releaseDate: '2026-10-20', dueDate: '2026-10-24',
    pastDue, netRequirement: quantity, revision: 'A', explanation: `${partNumber} is short.`,
    pegging: [{ description: 'Maintenance demand', needDate: '2026-10-24', quantity }],
  });
  return {
    today: '2026-10-20',
    items: [item('MAKE-A', 'Make', 5, 2, 1), item('BUY-B', 'Buy', 1, 0, 0)],
    plannedOrders: [plannedOrder('MAKE-A', 'Make', false, 3), plannedOrder('BUY-B', 'Buy', true, 4)],
    exceptions: [
      { partNumber: 'BUY-B', kind: 'PastDueRelease', detail: 'Release should have happened.' },
      { partNumber: 'BUY-B', kind: 'OverdueReceipt', detail: 'Supplier receipt is late.' },
      { partNumber: 'MISSING-1', kind: 'NoRevision', detail: 'No released revision.' },
    ],
    substitutions: [{ date: '2026-10-24', primaryPart: 'PRIMARY', alternatePart: 'ALT', relationshipId: 1, shortage: 2, alternateAvailable: 2, alternateQuantity: 2, primaryQuantity: 2, explanation: 'Approved alternate.' }],
  };
}

const ctx = permissions => ({ query: new URLSearchParams(), me: { personId: 'mrp-test', permissions } });

test('MRP shows tab counts, searchable tables, and preserves recommendation indexes through filters', async t => {
  const { browser, page } = setup(t);
  await mrpPage(page, ctx(['planning.demand.manage', 'procurement.manage']));
  for (const label of ['Recommendations (2)', 'Substitutions (1)', 'Exceptions (3)', 'Items (2)', 'Demand (1)']) assert.match(page.innerHTML, new RegExp(label.replace(/[()]/g, '\\$&')));
  assert.ok(page.querySelector('[data-table-filter]'), 'two-row tables receive the shared table search');
  fire(page.querySelector('[data-planned-filter="pastDue"]'), 'click');
  assert.ok(page.querySelector('[data-why="1"]'), 'filtered row keeps its original recommendation index');
  assert.equal(page.querySelector('[data-why="0"]'), null);
  fire(page.querySelector('[data-why="1"]'), 'click');
  const body = browser.document.querySelector('.drawer').innerHTML;
  assert.match(body, /MRP netting summary/);
  assert.match(body, /Safety stock threshold/);
  assert.match(body, /time-phased netting applies receipt dates/i);
  assert.match(body, /mrp-bal-fill bad/);
  assert.match(body, /mrp-bal-fill warn/);
});

test('MRP Items offers horizon sparklines, quarantine pills and working item filters', async t => {
  const { page } = setup(t);
  await mrpPage(page, ctx([]));
  fire(page.querySelector('[data-tab="items"]'), 'click');
  assert.match(page.innerHTML, /Horizon balance trajectory for MAKE-A/);
  assert.match(page.innerHTML, /3 in insp\.\/hold/);
  assert.equal((page.innerHTML.match(/mrp-spark-bar /g) ?? []).length, 28, 'sparks are capped at 14 bars for each visible item');
  fire(page.querySelector('[data-item-filter="held"]'), 'click');
  assert.ok(page.querySelector('[data-item="MAKE-A"]'));
  assert.equal(page.querySelector('[data-item="BUY-B"]'), null);
  fire(page.querySelector('[data-item-filter="low"]'), 'click');
  assert.ok(page.querySelector('[data-item="BUY-B"]'));
});

test('MRP exceptions link to repair actions and past-due releases open the matching Why drawer', async t => {
  const { browser, page } = setup(t);
  await mrpPage(page, ctx([]));
  fire(page.querySelector('[data-tab="exceptions"]'), 'click');
  assert.match(page.innerHTML, /href="#\/engineering\/MISSING-1"/);
  assert.match(page.innerHTML, /href="#\/procurement"/);
  fire(page.querySelector('[data-jump-part="BUY-B"]'), 'click');
  assert.equal(page.querySelector('[data-tab="planned"]').getAttribute('class'), 'on');
  assert.match(browser.document.querySelector('.drawer').innerHTML, /Why 4 EA of BUY-B/);
});

test('Draft PO prefers active approved suppliers for the recommended part', async t => {
  const { browser, page } = setup(t, {
    suppliers: [
      { code: 'SUP-A', name: 'General Supply', status: 'Active' },
      { code: 'SUP-B', name: 'Preferred Supply', status: 'Active' },
      { code: 'SUP-X', name: 'Inactive Supply', status: 'Inactive' },
    ],
    sources: [{ partNumber: 'BUY-B', supplierCode: 'SUP-B' }],
  });
  await mrpPage(page, ctx(['procurement.manage']));
  fire(page.querySelector('[data-buy="1"]'), 'click');
  await flush();
  const modal = browser.document.querySelector('.modal'), options = modal.querySelectorAll('#f-supplier option');
  assert.deepEqual(options.map(option => option.getAttribute('value')), ['SUP-B'], 'other active suppliers are excluded when this part has approved-source restrictions');
  assert.match(options[0].textContent, /Approved source/);
  assert.equal(modal.querySelector('#f-supplier').value, 'SUP-B');
  fire(modal.querySelector('[data-cancel]'), 'click');
});

test('Add Demand loads real source records and exposes a custom source-key lookup', async t => {
  const { browser, page, calls } = setup(t);
  await mrpPage(page, ctx(['planning.demand.manage']));
  fire(page.querySelector('[data-tab="demand"]'), 'click');
  fire(page.querySelector('[data-add-demand]'), 'click');
  await flush(); await flush();
  assert.ok(calls.some(call => call.path === '/api/maintenance/work-orders'));
  assert.ok(calls.some(call => call.path === '/api/projects/'));
  assert.ok(calls.some(call => call.path === '/api/projects/CAP-1'));
  assert.ok(calls.some(call => call.path === '/api/resources/tools'), 'tool lookup uses the route exposed by Forge');
  const modal = browser.document.querySelector('.modal');
  const sourceKeys = modal.querySelectorAll('#f-sourceKey-list option').map(option => option.getAttribute('data-key'));
  assert.deepEqual(sourceKeys, ['work-order:17', 'project:CAP-1/3', 'tool:GAUGE-4']);
  modal.querySelector('#f-sourceKey').value = 'work-order:999';
  assert.equal(modal.querySelector('#f-sourceKey').getAttribute('list'), 'f-sourceKey-list');
  fire(modal.querySelector('[data-cancel]'), 'click');
});
