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
