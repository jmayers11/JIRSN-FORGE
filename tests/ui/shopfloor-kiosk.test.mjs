import assert from 'node:assert/strict';
import test from 'node:test';
import { shopFloorPage } from '../../src/Forge.Host/wwwroot/js/pages/shopfloor.js';
import { isKioskMode } from '../../src/Forge.Host/wwwroot/js/shell-mode.mjs';
import { armDemoScannerOutage, readDemoScannerOutage } from '../../src/Forge.Host/wwwroot/js/demo-scanner-outage.mjs';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

const operator = (badge, clockedIn = true) => ({
  personId: 'operator-1', displayName: `Operator ${badge}`, clockedInSince: clockedIn ? '2026-10-18T08:00:00Z' : null,
  hasPin: true, isWorker: true, homeWorkCenter: 'CUT', labor: {
    orderNumber: 17, sequence: 10, role: 'Operator', startedAt: '2026-10-18T08:10:00Z', machineCode: 'M1',
  }, training: [], queue: [{
    orderNumber: 17, partNumber: 'PART-1', orderQuantity: 10,
    operation: { sequence: 10, description: 'Cut', workCenterCode: 'CUT', eligibleMachines: ['M1'], goodQuantity: 3 },
  }],
});

async function mount(t, roles = ['ShopFloorTerminal'], permissions = [], idle = 20, hasLabor = true, clockedIn = true, photoAvailable = false) {
  const browser = installBrowser(t), calls = [];
  const page = browser.document.createElement('main'); browser.document.body.append(page);
  globalThis.fetch = async (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null;
    calls.push({ path, body });
    if (path === '/api/shopfloor/worker-photo') return photoAvailable
      ? new Response(new Blob(['fictional photo'], { type: 'image/png' }), { status: 200 })
      : response({ title: 'Not found' }, 404);
    if (path === '/api/execution/reason-codes') return response([]);
    if (path === '/api/shopfloor/identify') return response({ ...operator(body.badge, clockedIn), labor: hasLabor ? operator(body.badge, clockedIn).labor : null });
    if (path.startsWith('/api/execution/shifts/handoffs?')) return response([{
      handoffId: 22, workCenterCode: 'CUT', shiftCode: 'DAY', shiftUntil: '2026-10-18T07:00:00Z',
      reviewedAt: '2026-10-18T07:20:00Z', context: 'Verify the first piece before the second run.',
    }]);
    return response({ message: 'Saved.', entityId: '22' });
  };
  let kiosk = false;
  const controller = await shopFloorPage(page, { me: { personId: roles.includes('Supervisor') ? 'supervisor-id' : null, roles, permissions, shopFloorIdleTimeoutSeconds: idle }, kioskMode: false, setKioskMode: value => { kiosk = value; } });
  t.after(controller.dispose);
  const scan = async () => {
    page.querySelector('#badge').value = 'B-17';
    fire(page.querySelector('form[data-scan="badge"]'), 'submit');
    await flush(); await flush();
  };
  return { ...browser, page, calls, controller, scan, get kiosk() { return kiosk; } };
}

test('shop-floor terminal accounts enter kiosk mode automatically; supervisors may toggle it', () => {
  assert.equal(isKioskMode('shop-floor', ['ShopFloorTerminal']), true);
  assert.equal(isKioskMode('shop-floor', ['Supervisor']), false);
  assert.equal(isKioskMode('shop-floor', ['Supervisor'], true), true);
  assert.equal(isKioskMode('shop-floor', ['Supervisor', 'ShopFloorTerminal']), true);
  assert.equal(isKioskMode('shop-floor', ['Supervisor', 'ShopFloorTerminal'], false), false);
  assert.equal(isKioskMode('orders', ['ShopFloorTerminal']), false);
});

test('supervisor kiosk toggle, idle countdown, stay-signed-in and pass-on banner work', async t => {
  const h = await mount(t, ['Supervisor', 'ShopFloorTerminal'], ['execution.shopfloor.read'], 20);
  await h.scan();
  fire(h.page.querySelector('#toggle-kiosk'), 'click');
  assert.equal(h.kiosk, true);
  assert.equal(h.page.querySelector('#toggle-kiosk').getAttribute('aria-pressed'), 'true');
  assert.equal(h.page.querySelector('#idle-countdown').textContent, '20s');
  assert.match(h.page.querySelector('#pass-on-banner').textContent, /Verify the first piece/);
  h.timers.advance(5000);
  assert.equal(h.page.querySelector('#idle-countdown').textContent, '15s');
  assert.equal(h.page.querySelector('#idle-indicator').classList.contains('warn'), true);
  fire(h.page.querySelector('#stay-signed-in'), 'click');
  assert.equal(h.page.querySelector('#idle-countdown').textContent, '20s');
});

test('locked kiosk shows a live station clock and reports whether scanner input is armed', async t => {
  const h = await mount(t);
  assert.ok(h.page.querySelector('#kiosk-site-clock'));
  assert.match(h.page.querySelector('#badge-focus-status').textContent, /Scanner armed/);
  fire(h.page.querySelector('#badge-focus-status'), 'click');
  assert.equal(h.document.activeElement, h.page.querySelector('#badge'));
});

test('S12 kiosk rehearsal preserves fields, locks locally, and requires supervisor print reconciliation without API writes', async t => {
  const previousStorage = globalThis.sessionStorage;
  const values = new Map();
  globalThis.sessionStorage = { getItem: key => values.get(key) ?? null, setItem: (key, value) => values.set(key, String(value)), removeItem: key => values.delete(key) };
  t.after(() => { if (previousStorage === undefined) delete globalThis.sessionStorage; else globalThis.sessionStorage = previousStorage; });
  armDemoScannerOutage(globalThis.sessionStorage);
  const h = await mount(t, ['Supervisor', 'ShopFloorTerminal'], ['workforce.supervise'], 20);
  h.page.querySelector('[data-s12-scanner-reference]').value = 'WW-REF-92';
  h.page.querySelector('[data-s12-scanner-operation]').value = 'Order 750 / op 20';
  fire(h.page.querySelector('[data-s12-fail-scanner]'), 'click');
  assert.equal(readDemoScannerOutage().scanner.status, 'failed');
  assert.match(h.page.querySelector('[data-s12-fail-scanner]')?.parentElement?.textContent ?? h.page.innerHTML, /WW-REF-92/);
  assert.match(h.page.querySelector('[data-s12-fail-scanner]')?.parentElement?.textContent ?? h.page.innerHTML, /Order 750 \/ op 20/);

  h.page.querySelector('[data-s12-label-part]').value = 'WW-PCB-CTRL-200';
  h.page.querySelector('[data-s12-label-quantity]').value = '4';
  h.page.querySelector('[data-s12-label-text]').value = 'Order 750 / Rev B';
  fire(h.page.querySelector('[data-s12-fail-label]'), 'click');
  assert.equal(readDemoScannerOutage().label.status, 'pending-reconciliation');
  h.page.querySelector('[data-s12-label-outcome]').value = 'not-printed';
  h.page.querySelector('[data-s12-label-reason]').value = 'Checked tray and take-up bin';
  fire(h.page.querySelector('[data-s12-reconcile-label]'), 'click');
  assert.equal(readDemoScannerOutage().label.status, 'reconciled');
  assert.equal(readDemoScannerOutage().label.observedBy, 'supervisor-id');
  assert.equal(h.calls.some(call => call.path.startsWith('/api/shopfloor/')), false);
});

test('touch numeric and PIN keypads update their associated controls', async t => {
  const h = await mount(t);
  await h.scan();
  fire(h.page.querySelector('[data-numpad="good"] [data-numpad-key="3"]'), 'click');
  fire(h.page.querySelector('[data-numpad="good"] [data-numpad-key="."]'), 'click');
  fire(h.page.querySelector('[data-numpad="good"] [data-numpad-key="5"]'), 'click');
  assert.equal(h.page.querySelector('#good').value, '3.5');
  fire(h.page.querySelector('#pin'), 'click');
  const modal = h.document.querySelector('.modal');
  fire(modal.querySelector('[data-numpad="new"] [data-numpad-key="1"]'), 'click');
  fire(modal.querySelector('[data-numpad="new"] [data-numpad-key="2"]'), 'click');
  fire(modal.querySelector('[data-numpad="new"] [data-numpad-key="backspace"]'), 'click');
  assert.equal(modal.querySelector('#new').value, '1');
});

test('active kiosk shows quantity progress and uses one touch keypad for both report fields', async t => {
  const h = await mount(t);
  await h.scan();
  assert.equal(h.page.querySelectorAll('.kiosk-shared-keypad').length, 1);
  assert.equal(h.page.querySelectorAll('.kiosk-progress-strip .kiosk-metric').length, 4);
  assert.match(h.page.querySelector('.kiosk-progress-strip').textContent, /Target[\s\S]*Good so far[\s\S]*Scrap so far[\s\S]*Remaining/);
  fire(h.page.querySelector('[data-focus-qty="scrap"]'), 'click');
  fire(h.page.querySelector('.kiosk-shared-keypad [data-numpad-key="5"]'), 'click');
  assert.equal(h.page.querySelector('#scrap').value, '5');
  fire(h.page.querySelector('[data-qty-add="1"]'), 'click');
  assert.equal(h.page.querySelector('#scrap').value, '6');
  fire(h.page.querySelector('[data-scrap-tile="Edge wave"]'), 'click');
  assert.equal(h.page.querySelector('#reason').value, 'Edge wave');
  fire(h.page.querySelector('[data-focus-qty="good"]'), 'click');
  fire(h.page.querySelector('[data-qty-fill]'), 'click');
  assert.equal(h.page.querySelector('#good').value, '7');
});

test('kiosk loads the current badge holder photo through a badge-scoped endpoint', async t => {
  const h = await mount(t, ['ShopFloorTerminal'], [], 20, false, true, true);
  await h.scan(); await flush(); await flush();
  const photo = h.page.querySelector('.kiosk-operator-photo');
  assert.ok(photo);
  assert.equal(photo.getAttribute('alt'), 'Operator B-17 employee photo');
  assert.match(h.page.querySelector('.kiosk-identified-operator').textContent, /Safety and qualification checks at start/);
  assert.deepEqual(h.calls.find(call => call.path === '/api/shopfloor/worker-photo').body, { badge: 'B-17' });
});

test('kiosk retains the initials avatar when the current badge holder has no photo', async t => {
  const h = await mount(t);
  await h.scan(); await flush(); await flush();
  assert.ok(h.page.querySelector('.kiosk-operator-avatar'));
  assert.equal(h.page.querySelector('.kiosk-operator-photo'), null);
});

test('machine-down quick action uses the authorized audited maintenance endpoint', async t => {
  const h = await mount(t, ['ShopFloorTerminal'], ['maintenance.downtime.report']);
  await h.scan();
  const button = h.page.querySelector('#kiosk-machine-down');
  assert.ok(button);
  assert.match(button.textContent, /M1/);
  fire(button, 'click');
  const modal = h.document.querySelector('.modal');
  assert.ok(modal);
  modal.querySelector('#f-description').value = 'Coolant pressure fault';
  modal.querySelector('#reason').value = 'Operator observed machine stop';
  fire(modal.querySelector('[data-ok]'), 'click');
  await flush(); await flush();
  const report = h.calls.find(call => call.path === '/api/maintenance/machines/M1/down');
  assert.deepEqual(report.body, { description: 'Coolant pressure fault', reason: 'Operator observed machine stop' });
});

test('machine-down action stays hidden when the current role cannot report downtime', async t => {
  const h = await mount(t);
  await h.scan();
  assert.equal(h.page.querySelector('#kiosk-machine-down'), null);
});

test('available work uses touch-friendly dispatch cards and keeps the clock-in gate visible', async t => {
  const h = await mount(t, ['ShopFloorTerminal'], [], 20, false, false);
  await h.scan();
  assert.equal(h.page.querySelectorAll('.kiosk-dispatch-card').length, 1);
  assert.match(h.page.querySelector('.kiosk-dispatch-card').textContent, /Order #17[\s\S]*Op 10 · Cut[\s\S]*M1/);
  assert.equal(h.page.querySelector('[data-start="0"]').disabled, true, 'work cannot start before clock-in');
  assert.ok(h.page.querySelector('[data-stage-work="0"]'));
});

test('active operation exposes existing document, inventory, NCR and quality-hold actions by permission', async t => {
  const h = await mount(t, ['Supervisor'], ['engineering.read', 'inventory.read', 'inventory.transact', 'quality.inspect', 'holds.place']);
  await h.scan();
  for (const id of ['work-instructions', 'material-reconciliation', 'flag-ncr', 'quality-hold']) assert.ok(h.page.querySelector(`#${id}`), `${id} is visible`);
  fire(h.page.querySelector('#quality-hold'), 'click');
  const modal = h.document.querySelector('.modal');
  assert.match(modal.textContent, /order #17, op 10/);
  assert.ok(modal.querySelector('#f-evidence'));
  assert.ok(modal.querySelector('#f-requiredResolution'));
});
