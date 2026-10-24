import assert from 'node:assert/strict';
import test from 'node:test';
import { createIdentificationStage, mountIdentificationInput, materialPickSelection } from '../../src/Forge.Host/wwwroot/js/identification-input.mjs';
import { shopFloorPage } from '../../src/Forge.Host/wwwroot/js/pages/shopfloor.js';
import { inventoryPage } from '../../src/Forge.Host/wwwroot/js/pages/inventory.js';
import { installBrowser, response, deferred, flush, fire } from './browser-harness.mjs';

const config = { namespace: 'configured-plant', supportedKinds: ['order', 'operation', 'material', 'location'], maxCodeLength: 1024, maxKeyLength: 40, version: 'v1' };
const material = (part = 'PART-A', lot = 'LOT-7') => ({ namespace: config.namespace, kind: 'material', partNumber: part, lotNumber: lot, status: 'Recorded', identityKind: 'Roll' });
const location = code => ({ namespace: config.namespace, kind: 'location', locationCode: code, status: 'Active' });
const operation = order => ({ namespace: config.namespace, kind: 'operation', orderNumber: order, sequence: 10, partNumber: 'PART', revision: 'A', status: 'Pending' });
const queue = order => [{ orderNumber: order, partNumber: 'PART', orderQuantity: 10, operation: { sequence: 10, description: 'Cut', workCenterCode: 'CUT', eligibleMachines: ['M1', 'M2'], goodQuantity: 0 }, scheduledMachine: 'M2' }];
const person = badge => ({ displayName: `Operator ${badge}`, personId: badge, clockedInSince: '2026-10-14T09:00:00Z', hasPin: true, isWorker: true, queue: queue(badge === 'B-18' ? 18 : 17), training: [] });

test('resolution is a preview until selected; repeated codes do not execute a transaction', async () => {
  const selected = [], resolved = [];
  const stage = createIdentificationStage({ loadConfig: async () => config, resolve: async code => { resolved.push(code); return material(); }, kinds: ['material'], onSelect: subject => selected.push(subject) });
  await stage.resolve('first-code');
  assert.equal(stage.selected, null); assert.equal(selected.length, 0);
  assert.ok(stage.select()); assert.equal(selected.length, 1);
  await stage.resolve('first-code');
  assert.equal(stage.selected, null); assert.equal(selected.length, 1);
  assert.deepEqual(resolved, ['first-code', 'first-code']);
});

test('new scan or manual input rejects an older in-flight response and error', async () => {
  const old = deferred(), latest = deferred();
  const outages = [];
  const stage = createIdentificationStage({ loadConfig: async () => config, resolve: code => code === 'old' ? old.promise : latest.promise, kinds: ['material'], onUnavailable: error => outages.push(error) });
  const first = stage.resolve('old'); await flush();
  const next = stage.resolve('latest'); await flush();
  latest.resolve(material('PART-B')); await next;
  old.reject(new TypeError('Network failed for the previous request')); await first;
  assert.equal(stage.resolved.partNumber, 'PART-B'); assert.deepEqual(outages, []);
  const pending = deferred();
  const manual = createIdentificationStage({ loadConfig: async () => config, resolve: () => pending.promise, kinds: ['material'] });
  const request = manual.resolve('scan'); await flush(); manual.clear(); pending.resolve(material()); await request;
  assert.equal(manual.resolved, null); assert.equal(manual.select(), null);
});

test('context loss and disposal permanently discard resolutions already in flight', async () => {
  for (const dispose of [false, true]) {
    let active = true; const pending = deferred(), selections = [];
    const stage = createIdentificationStage({ loadConfig: async () => config, resolve: () => pending.promise, kinds: ['material'], isCurrent: () => active, onSelect: subject => selections.push(subject) });
    const result = stage.resolve('code'); await flush();
    if (dispose) stage.dispose(); else active = false;
    pending.resolve(material()); await result;
    assert.equal(stage.resolved, null); assert.equal(stage.selected, null); assert.equal(stage.select(), null); assert.equal(selections.length, 0);
  }
});

test('foreign namespace and wrong subject kind cannot become a selected material', async () => {
  for (const subject of [{ ...material(), namespace: 'other-plant' }, location('STORES')]) {
    const selections = [], outages = [];
    const stage = createIdentificationStage({ loadConfig: async () => config, resolve: async () => subject, kinds: ['material'], onSelect: value => selections.push(value), onUnavailable: error => outages.push(error) });
    await stage.resolve('code');
    assert.equal(stage.resolved, null); assert.equal(stage.select(), null); assert.equal(selections.length, 0); assert.equal(outages.length, 0);
  }
});

test('unconfigured optional scanning leaves manual work available; a transport outage invalidates it', async () => {
  const unavailable = [];
  let configured = false;
  const stage = createIdentificationStage({ loadConfig: async () => {
    if (!configured) throw Object.assign(new Error('Configure identification first.'), { status: 503, problem: { code: 'identification_not_configured' } });
    return config;
  }, resolve: async () => { throw new TypeError('Failed to fetch'); }, kinds: ['material'], onUnavailable: error => unavailable.push(error) });
  await stage.resolve('code'); assert.equal(unavailable.length, 0); assert.equal(stage.selected, null);
  configured = true; await stage.resolve('code'); assert.equal(unavailable.length, 1); assert.equal(stage.selected, null);
});

test('material selection binds both part and lot, including equal lot text on different parts', () => {
  const task = { partNumber: 'PART-A', lotNumber: 'LOT-7' };
  assert.deepEqual(materialPickSelection(material(), task), task);
  assert.throws(() => materialPickSelection(material('PART-B'), task), /part and lot/);
  assert.throws(() => materialPickSelection(material('PART-A', 'LOT-8'), task), /part and lot/);
});

test('keyboard entry resolves a configured namespace and long escaped code without changing it', async t => {
  const { document } = installBrowser(t), codes = [], selected = [];
  const mount = document.createElement('div'); document.body.append(mount);
  const controller = mountIdentificationInput(mount, { id: 'test-code', label: 'Material code', kinds: ['material'], loadConfig: async () => config, resolve: async code => { codes.push(code); return material(); }, onSelect: subject => selected.push(subject) });
  t.after(controller.dispose);
  const code = `forge:v1:${config.namespace}:material:${'%E4%B8%AD'.repeat(40)}:${'%E4%B8%AD'.repeat(40)}`;
  mount.querySelector('input').value = code;
  assert.equal(fire(mount.querySelector('input'), 'keydown', { key: 'Enter' }).defaultPrevented, true); await flush();
  assert.deepEqual(codes, [code]); assert.equal(mount.querySelector('input').getAttribute('maxlength'), '1024');
  assert.equal(mount.querySelector('[data-identify-status]').getAttribute('aria-live'), 'polite');
  assert.match(mount.querySelector('[data-identify-status]').textContent, /PART-A, lot LOT-7/);
  assert.equal(selected.length, 0); fire(mount.querySelector('[data-identify-use]'), 'click'); assert.equal(selected.length, 1);
});

async function terminalPage(t, route = () => null, idleTimeoutSeconds = 60) {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body });
    const override = route(path, body);
    if (override) return Promise.resolve(override);
    if (path === '/api/execution/reason-codes') return Promise.resolve(response([]));
    if (path === '/api/identification/config') return Promise.resolve(response(config));
    if (path.startsWith('/api/identification/scan-sessions/requirements?')) return Promise.resolve(response({ adopted: false, required: false, manualAllowed: true, requiredKinds: [] }));
    if (path === '/api/identification/resolve') return Promise.resolve(response(operation(17)));
    if (path === '/api/shopfloor/identify') return Promise.resolve(response(person(body.badge)));
    return Promise.resolve(response({ message: 'Started.' }));
  };
  const controller = await shopFloorPage(page, { me: { roles: ['ShopFloorTerminal'], shopFloorIdleTimeoutSeconds: idleTimeoutSeconds } }); t.after(controller.dispose);
  return { ...browser, page, calls, controller, scan: async badge => {
    page.querySelector('#badge').value = badge; fire(page.querySelector('form[data-scan="badge"]'), 'submit'); await flush();
  }, resolve: async () => {
    page.querySelector('#work-identification').value = 'operation-code'; fire(page.querySelector('#work-identification'), 'keydown', { key: 'Enter' }); await flush();
  } };
}

test('resolved operation starts only after explicit selection and Start using the current badge and machine', async t => {
  const h = await terminalPage(t); await h.scan('B-17'); await h.resolve();
  assert.equal(h.calls.filter(c => c.path === '/api/shopfloor/start').length, 0);
  fire(h.page.querySelector('[data-identify-use]'), 'click');
  assert.equal(h.calls.filter(c => c.path === '/api/shopfloor/start').length, 0);
  assert.equal(h.page.querySelector('#identified-machine-0').value, 'M2');
  h.page.querySelector('#identified-phase-0').value = 'Inspection';
  const start = h.page.querySelector('[data-identified-start]'); fire(start, 'click'); fire(start, 'click'); await flush();
  assert.deepEqual(h.calls.filter(c => c.path === '/api/shopfloor/start').map(c => c.body), [{ badge: 'B-17', orderNumber: 17, sequence: 10, machineCode: 'M2', phase: 'Inspection', trainerBadge: null }]);
});

test('resolving work outside the badged operator queue never grants a Start action', async t => {
  const h = await terminalPage(t, path => path === '/api/identification/resolve' ? response(operation(99)) : null); await h.scan('B-17'); await h.resolve();
  fire(h.page.querySelector('[data-identify-use]'), 'click');
  assert.match(h.page.querySelector('[data-identify-status]').textContent, /current operator/);
  assert.equal(h.page.querySelector('[data-identified-start]'), null); assert.equal(h.calls.filter(c => c.path === '/api/shopfloor/start').length, 0);
});

test('work resolution arriving after switch, idle or disposal cannot stage under a later context', async t => {
  for (const mode of ['switch', 'idle', 'dispose']) await t.test(mode, async child => {
    const pending = deferred(); const h = await terminalPage(child, path => path === '/api/identification/resolve' ? pending.promise : null, 2);
    await h.scan('B-17'); await h.resolve(); const oldUse = h.page.querySelector('[data-identify-use]');
    if (mode === 'switch') { fire(h.page.querySelector('#done'), 'click'); await h.scan('B-18'); }
    if (mode === 'idle') h.timers.advance(2000);
    if (mode === 'dispose') h.controller.dispose();
    pending.resolve(response(operation(17))); await flush(); fire(oldUse, 'click'); await flush();
    assert.equal(h.page.querySelector('[data-identified-start]'), null); assert.equal(h.calls.filter(c => c.path === '/api/shopfloor/start').length, 0);
    if (mode === 'switch') assert.match(h.page.innerHTML, /Operator B-18/);
    if (mode === 'idle') assert.ok(h.page.querySelector('#badge'));
  });
});

test('transport failure while resolving clears staged work and locks the terminal', async t => {
  const h = await terminalPage(t, path => path === '/api/identification/resolve' ? Promise.reject(new TypeError('Failed to fetch')) : null);
  await h.scan('B-17'); await h.resolve();
  assert.ok(h.page.querySelector('#badge')); assert.equal(h.page.querySelector('[data-identified-start]'), null);
  assert.equal(h.calls.filter(c => c.path === '/api/shopfloor/start').length, 0);
});

async function inventoryTaskPage(t, route = () => null, status = 'Allocated') {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  const task = { taskId: 7, kind: 'Stage', partNumber: 'PART-A', lotNumber: 'LOT-7', quantity: 4, pickedQuantity: 4, fromLocation: 'STORES', toLocation: 'STAGE', status };
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body });
    const override = route(path, body); if (override) return Promise.resolve(override);
    if (path === '/api/identification/config') return Promise.resolve(response(config));
    if (path === '/api/identification/resolve') return Promise.resolve(response(body.code === 'material-code' ? material() : location(status === 'Allocated' ? 'STORES' : 'STAGE')));
    if (path.startsWith('/api/inventory/tasks/7/')) return Promise.resolve(response({ message: 'Saved.' }));
    return Promise.resolve(response(path === '/api/inventory/tasks?open=true' ? [task] : path === '/api/resources/' ? {} : []));
  };
  const controller = await inventoryPage(page, { me: { roles: [], permissions: ['inventory.transact'] }, query: new URLSearchParams('tab=tasks') }); t.after(controller.dispose);
  fire(page.querySelector(status === 'Allocated' ? '[data-task-pick]' : '[data-task-deliver]'), 'click');
  const modal = browser.document.querySelector('.modal');
  return { ...browser, page, calls, modal, controller, resolve: async (kind, code) => {
    const input = modal.querySelector(`#task-${kind}-identification`); input.value = code; fire(input, 'keydown', { key: 'Enter' }); await flush();
  }, use: kind => fire(modal.querySelector(`[data-${kind}-identification]`).querySelector('[data-identify-use]'), 'click') };
}

test('inventory pick stages the exact part/lot tuple and requires the final explicit command', async t => {
  const h = await inventoryTaskPage(t); await h.resolve('location', 'location-code'); h.use('location');
  await h.resolve('material', 'material-code'); h.use('material');
  assert.equal(h.calls.filter(c => c.path === '/api/inventory/tasks/7/pick').length, 0);
  assert.equal(h.modal.getAttribute('aria-modal'), 'true');
  const confirm = h.modal.querySelector('[data-task-confirm]'); fire(confirm, 'click'); fire(confirm, 'click'); await flush();
  assert.deepEqual(h.calls.filter(c => c.path === '/api/inventory/tasks/7/pick').map(c => c.body), [{ scannedLocation: 'STORES', scannedLot: 'LOT-7', scannedMaterial: { partNumber: 'PART-A', lotNumber: 'LOT-7' }, quantity: 4 }]);
  assert.equal(h.modal.isConnected, false); assert.equal(h.document.listeners.get('keydown').size, 0);
});

test('same-lot material from another part cannot populate a task; editing a staged lot clears its typed tuple', async t => {
  let part = 'PART-B';
  const h = await inventoryTaskPage(t, (path, body) => path === '/api/identification/resolve' && body.code === 'material-code' ? response(material(part)) : null);
  await h.resolve('material', 'material-code'); h.use('material');
  assert.equal(h.modal.querySelector('#task-scanned-lot').value, '');
  assert.match(h.modal.querySelector('[data-material-identification]').querySelector('[data-identify-status]').textContent, /part and lot/);
  part = 'PART-A'; await h.resolve('material', 'material-code'); h.use('material');
  const lot = h.modal.querySelector('#task-scanned-lot'); lot.value = 'LOT-7'; fire(lot, 'input');
  h.modal.querySelector('#task-scanned-location').value = 'STORES'; fire(h.modal.querySelector('[data-task-confirm]'), 'click'); await flush();
  const pick = h.calls.find(c => c.path === '/api/inventory/tasks/7/pick');
  assert.deepEqual(pick.body, { scannedLocation: 'STORES', scannedLot: 'LOT-7', quantity: 4 });
});

test('delivery uses existing manual location input when optional identification is unconfigured', async t => {
  const h = await inventoryTaskPage(t, path => path === '/api/identification/config' ? response({ code: 'identification_not_configured', detail: 'Identification is not configured.' }, 503) : null, 'Picked');
  await h.resolve('location', 'location-code'); assert.equal(h.modal.isConnected, true);
  h.modal.querySelector('#task-scanned-location').value = 'stage'; fire(h.modal.querySelector('[data-task-confirm]'), 'click'); await flush();
  assert.deepEqual(h.calls.filter(c => c.path === '/api/inventory/tasks/7/deliver').map(c => c.body), [{ scannedLocation: 'STAGE' }]);
});

test('closing or disposing the inventory task discards late scan results and cannot send a detached command', async t => {
  for (const mode of ['escape', 'dispose']) await t.test(mode, async child => {
    const pending = deferred(); const h = await inventoryTaskPage(child, path => path === '/api/identification/resolve' ? pending.promise : null);
    await h.resolve('material', 'material-code'); const confirm = h.modal.querySelector('[data-task-confirm]');
    if (mode === 'escape') fire(h.modal, 'keydown', { key: 'Escape' }); else h.controller.dispose();
    pending.resolve(response(material())); await flush(); fire(confirm, 'click'); await flush();
    assert.equal(h.modal.isConnected, false); assert.equal(h.document.querySelector('.scrim'), null); assert.equal(h.document.listeners.get('keydown').size, 0);
    assert.equal(h.calls.filter(c => c.path === '/api/inventory/tasks/7/pick').length, 0);
    assert.equal(h.modal.querySelector('#task-scanned-lot').value, '');
  });
});
