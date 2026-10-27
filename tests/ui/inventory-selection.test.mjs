import assert from 'node:assert/strict';
import test from 'node:test';
import { inventoryPage, receivingPage, parseInventoryShares } from '../../src/Forge.Host/wwwroot/js/pages/inventory.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

test('inventory split and merge row text keeps the existing quantity payload parser', () => {
  assert.deepEqual(parseInventoryShares('lot-a 2.5, lot-b 4'), [{ lotNumber: 'LOT-A', quantity: 2.5 }, { lotNumber: 'LOT-B', quantity: 4 }]);
});

test('inventory custody history shows append-only dispatch, exception and receiver evidence', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const task = { taskId: 31, kind: 'Stage', orderNumber: 1042, partNumber: 'COMP-1', quantity: 8, status: 'InTransit',
    fromLocation: 'STORES', toLocation: 'SLIT-STAGE', transitLocation: 'TRANSIT', requestedBy: 'person:picker', requestedAt: '2026-10-23T09:00:00Z' };
  globalThis.fetch = path => {
    if (path === '/api/inventory/tasks?open=true') return Promise.resolve(response([task]));
    if (path === '/api/inventory/tasks/31/custody') return Promise.resolve(response([
      { eventId: 1, kind: 'DispatchedToTransit', recordedAt: '2026-10-23T10:00:00Z', recordedBy: 'person:picker', locationCode: 'TRANSIT', transactionId: 901, authenticationMethod: 'ForgeSession', reason: null },
      { eventId: 2, kind: 'ExceptionRecorded', recordedAt: '2026-10-23T10:05:00Z', recordedBy: 'person:receiver', locationCode: 'TRANSIT', transactionId: null, authenticationMethod: 'BadgePin', reason: '<script>unexpected count</script>' },
    ]));
    if (path === '/api/resources/') return Promise.resolve(response({ sites: [], workCenters: [], machines: [] }));
    return Promise.resolve(response([]));
  };
  const controller = await inventoryPage(page, { me: { personId: 'viewer', permissions: ['inventory.read'] }, query: new URLSearchParams('tab=tasks'), params: [], refreshBell() {} });
  t.after(controller.dispose);
  fire(page.querySelector('[data-task-custody="31"]'), 'click'); await flush(); await flush();
  const trail = browser.document.querySelector('.drawer .body');
  assert.match(trail.innerHTML, /Dispatched to transit/);
  assert.match(trail.innerHTML, /Exception reported/);
  assert.match(trail.innerHTML, /BadgePin/);
  assert.match(trail.innerHTML, /&lt;script&gt;unexpected count&lt;\/script&gt;/);
  assert.doesNotMatch(trail.innerHTML, /<script>unexpected count/);
});

test('receiver can separately accept and reject a partial handoff into quarantine', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const task = { taskId: 32, kind: 'Stage', orderNumber: 1042, partNumber: 'COMP-1', quantity: 10, pickedQuantity: 10,
    receivedQuantity: 0, rejectedQuantity: 0, status: 'InTransit', fromLocation: 'STORES', toLocation: 'SLIT-STAGE',
    transitLocation: 'TRANSIT', requestedBy: 'person:picker', requestedAt: '2026-10-23T09:00:00Z' };
  globalThis.fetch = path => {
    if (path === '/api/inventory/tasks?open=true') return Promise.resolve(response([task]));
    if (path === '/api/inventory/locations') return Promise.resolve(response([
      { code: 'QUARANTINE', name: 'Quality quarantine', kind: 'Quarantine', active: true, nettable: false, inCompanyCustody: true },
    ]));
    if (path === '/api/resources/') return Promise.resolve(response({ sites: [], workCenters: [], machines: [] }));
    return Promise.resolve(response([]));
  };
  const controller = await inventoryPage(page, { me: { personId: 'receiver', permissions: ['inventory.read', 'inventory.custody.receive'] }, query: new URLSearchParams('tab=tasks'), params: [], refreshBell() {} });
  t.after(controller.dispose);
  assert.ok(page.querySelector('[data-task-receive="32"]'), page.innerHTML);
  fire(page.querySelector('[data-task-receive="32"]'), 'click');
  const modal = browser.document.querySelector('.modal');
  assert.equal(modal.querySelector('#task-accepted-quantity').value, '10');
  assert.equal(modal.querySelector('#task-rejected-quantity').value, '0');
  assert.match(modal.innerHTML, /QUARANTINE/);
  modal.querySelector('#task-accepted-quantity').value = '7';
  modal.querySelector('#task-rejected-quantity').value = '3';
  modal.querySelector('#task-quarantine-location').value = 'QUARANTINE';
  assert.match(modal.textContent, /Reason \(required when rejecting quantity\)/);
});

test('inventory controller can see recent delivered tasks and launch audited return or receipt correction', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const delivered = { taskId: 33, kind: 'Stage', orderNumber: 1042, partNumber: 'COMP-1', quantity: 10, pickedQuantity: 10,
    receivedQuantity: 8, rejectedQuantity: 2, returnedQuantity: 0, reversedQuantity: 0, status: 'Delivered',
    fromLocation: 'STORES', toLocation: 'SLIT-STAGE', requestedBy: 'person:picker', requestedAt: '2026-10-23T09:00:00Z' };
  globalThis.fetch = path => {
    if (path === '/api/inventory/tasks?open=true') return Promise.resolve(response([]));
    if (path === '/api/inventory/tasks?open=false&take=200') return Promise.resolve(response([delivered]));
    if (path === '/api/inventory/locations') return Promise.resolve(response([
      { code: 'STORES', name: 'Main stores', kind: 'Stores', active: true, inCompanyCustody: true },
      { code: 'QUARANTINE', name: 'Quality quarantine', kind: 'Quarantine', active: true, nettable: false, inCompanyCustody: true },
    ]));
    if (path === '/api/resources/') return Promise.resolve(response({ sites: [], workCenters: [], machines: [] }));
    return Promise.resolve(response([]));
  };
  const controller = await inventoryPage(page, { me: { personId: 'controller', permissions: ['inventory.read', 'inventory.manage'] }, query: new URLSearchParams('tab=tasks'), params: [], refreshBell() {} });
  t.after(controller.dispose);
  assert.ok(page.querySelector('[data-task-return="33"]'));
  assert.ok(page.querySelector('[data-task-reverse="33"]'));
  fire(page.querySelector('[data-task-return="33"]'), 'click'); await flush();
  assert.match(browser.document.querySelector('.modal').textContent, /preserves the original receipt and custody events/);
  assert.match(browser.document.querySelector('.modal').innerHTML, /STORES/);
});

test('inventory lot selection shows ranked eligible and excluded candidates without reserving stock', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  const before = { subject: session.subject, token: session.token }; session.subject = 'inventory-selection-user'; session.token = null; t.after(() => Object.assign(session, before));
  globalThis.fetch = path => {
    calls.push(path);
    if (path === '/api/engineering/items') return Promise.resolve(response([{ partNumber: 'PART-A', description: 'Foil' }]));
    if (path === '/api/orders/') return Promise.resolve(response([{ orderNumber: 42, partNumber: 'PART-A', quantity: 10, status: 'Released' }]));
    if (path === '/api/resources/') return Promise.resolve(response({ sites: [], workCenters: [], machines: [] }));
    if (path.startsWith('/api/inventory/selection?')) return Promise.resolve(response({ partNumber: 'PART-A', rule: 'Fefo', requiredRevision: 'B', candidates: [
      { lotNumber: 'LOT-1', locationCode: 'BIN-1', quantity: 6, received: '2026-10-01T00:00:00Z', expires: '2027-01-01', partRevision: 'B', rank: 1, excludedBecause: null },
      { lotNumber: 'LOT-2', locationCode: 'BIN-2', quantity: 4, received: '2026-09-01T00:00:00Z', expires: '2026-12-01', partRevision: 'A', rank: 0, excludedBecause: 'Revision does not match order.' },
    ] }));
    return Promise.resolve(response([]));
  };

  await inventoryPage(page, { me: { personId: 'viewer', permissions: [] }, query: new URLSearchParams('tab=selection'), params: [], refreshBell() {} });
  page.querySelector('#lot-selection-part').value = 'PART-A'; page.querySelector('#lot-selection-order').value = '42';
  fire(page.querySelector('#lot-selection-form'), 'submit'); await flush();

  const lookup = calls.find(path => path.startsWith('/api/inventory/selection?'));
  assert.ok(lookup); assert.match(lookup, /part=PART-A/); assert.match(lookup, /order=42/);
  const result = page.querySelector('#lot-selection-result');
  assert.match(result.innerHTML, /First expired, first out/); assert.match(result.innerHTML, /LOT-1/); assert.match(result.innerHTML, /Eligible candidate/);
  assert.match(result.innerHTML, /LOT-2/); assert.match(result.innerHTML, /Revision does not match order/);
  assert.match(result.innerHTML, /does not reserve or issue stock/);
});

test('inventory defers heavy tab data until selected and exposes a focused Receiving route', async t => {
  const browser = installBrowser(t), calls = [];
  const page = browser.document.createElement('div'); browser.document.body.append(page);
  globalThis.fetch = path => {
    calls.push(path);
    if (path === '/api/resources/') return Promise.resolve(response({ sites: [], workCenters: [], machines: [] }));
    return Promise.resolve(response([]));
  };
  const controller = await inventoryPage(page, { me: { personId: 'viewer', permissions: [] }, query: new URLSearchParams(), params: [], refreshBell() {} });
  t.after(controller.dispose);
  for (const path of ['/api/inventory/transactions?take=200', '/api/inventory/count-programs', '/api/inventory/counts-due', '/api/inventory/shelf-life', '/api/inventory/receiving?take=100', '/api/inventory/outside-processing'])
    assert.equal(calls.includes(path), false, `${path} stays unloaded while Stock is selected`);

  fire(page.querySelector('[data-tab="receiving"]'), 'click'); await flush(); await flush();
  assert.ok(calls.includes('/api/inventory/receiving?take=100'));
  assert.equal(calls.includes('/api/inventory/shelf-life'), false);

  const receivingHost = browser.document.createElement('div'); browser.document.body.append(receivingHost);
  const receivingController = await receivingPage(receivingHost, { me: { personId: 'viewer', permissions: [] }, query: new URLSearchParams(), params: [], refreshBell() {} });
  t.after(receivingController.dispose);
  assert.equal(receivingHost.querySelectorAll('[data-tab]').length, 1);
  assert.match(receivingHost.querySelector('h1').textContent, /Receiving/);
});

test('inventory location map shows ledger quality separately from allocatable stock and resolves work-order movement links on demand', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  globalThis.fetch = path => {
    calls.push(path);
    if (path === '/api/resources/') return Promise.resolve(response({ sites: [{ code: 'WW', name: 'WhiskerWorks' }] }));
    if (path === '/api/inventory/locations') return Promise.resolve(response([
      { code: 'STORES', siteCode: 'WW', name: 'Stores', kind: 'Stores', level: 'Area', active: true, nettable: true },
      { code: 'RACK-1', siteCode: 'WW', parentCode: 'STORES', name: 'Rack 1', kind: 'Stores', level: 'Rack', active: true, nettable: true },
      { code: 'WIP-1', siteCode: 'WW', name: 'WIP 1', kind: 'WipStorage', level: 'Zone', active: true, nettable: false },
    ]));
    if (path === '/api/inventory/positions') return Promise.resolve(response([
      { partNumber: 'MOTOR-1', lotNumber: 'LOT-OK', locationCode: 'RACK-1', locationPath: 'STORES / RACK-1', quantity: 6, qualityStatus: 'Released', custody: 'Company', nettable: true, usable: true, availableForPlanning: true },
      { partNumber: 'MOTOR-1', lotNumber: 'LOT-HOLD', locationCode: 'WIP-1', locationPath: 'WIP-1', quantity: 4, qualityStatus: 'Hold', custody: 'Company', nettable: false, usable: true, availableForPlanning: false },
    ]));
    if (path === '/api/inventory/storage-rules') return Promise.resolve(response([{ rule: { locationCode: 'WIP-1', partNumber: 'MOTOR-1', safetyReference: 'SAFE-22', maxDwellHours: 24 }, availability: { free: null, operationalLimit: 10, occupancy: { onHand: 4, reserved: 2, otherParts: ['PLATE-1'] } } }]));
    if (path === '/api/inventory/transactions?order=1042&take=200') return Promise.resolve(response([
      { at: '2026-10-20T10:00:00Z', kind: 'Issue', partNumber: 'MOTOR-1', lotNumber: 'LOT-OK', fromLocation: 'STORES', toLocation: 'WO-1042', quantity: 6, reason: 'WO 1042' },
    ]));
    return Promise.resolve(response([]));
  };
  const controller = await inventoryPage(page, { me: { personId: 'viewer', permissions: ['inventory.read'] }, query: new URLSearchParams('tab=map'), params: [], refreshBell() {} });
  t.after(controller.dispose);
  assert.ok(calls.includes('/api/inventory/positions'));
  assert.ok(calls.includes('/api/inventory/storage-rules'));
  assert.match(page.innerHTML, /Site: WhiskerWorks/);
  assert.match(page.innerHTML, /STORES \/ RACK-1/);
  assert.match(page.innerHTML, /Available for planning/);
  assert.match(page.innerHTML, /LOT-HOLD/);
  assert.match(page.innerHTML, /mixed with PLATE-1/);
  assert.match(page.innerHTML, /may predate the current location/);

  const search = page.querySelector('[data-inventory-map-search]');
  search.value = 'WO 1042';
  fire(search, 'input');
  fire(page.querySelector('[data-inventory-map-search-form]'), 'submit');
  await flush();
  assert.ok(calls.includes('/api/inventory/transactions?order=1042&take=200'));
  assert.match(page.querySelector('[data-inventory-map-order-results]').innerHTML, /Work order #1042/);
  assert.match(page.querySelector('[data-inventory-map-order-results]').innerHTML, /do not prove that material remains/);
});

test('operational visualization feature flag hides and avoids loading the inventory location map', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  globalThis.fetch = path => { calls.push(path); return Promise.resolve(response([])); };
  const controller = await inventoryPage(page, { me: { personId: 'viewer', permissions: ['inventory.read'] }, query: new URLSearchParams(), params: [], operationalVisualizationsEnabled: false, refreshBell() {} });
  t.after(controller.dispose);
  assert.equal(page.querySelector('[data-tab="map"]'), null);
  assert.equal(calls.includes('/api/inventory/positions'), false);
  assert.equal(calls.includes('/api/inventory/storage-rules'), false);
});
