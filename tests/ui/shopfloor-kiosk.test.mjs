import assert from 'node:assert/strict';
import test from 'node:test';
import { shopFloorPage } from '../../src/Forge.Host/wwwroot/js/pages/shopfloor.js';
import { isKioskMode } from '../../src/Forge.Host/wwwroot/js/shell-mode.mjs';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

const operator = (badge, clockedIn = true) => ({
  personId: 'operator-1', displayName: `Operator ${badge}`, clockedInSince: clockedIn ? '2026-10-18T08:00:00Z' : null,
  hasPin: true, isWorker: true, homeWorkCenter: 'CUT', labor: {
    orderNumber: 17, sequence: 10, role: 'Operator', startedAt: '2026-10-18T08:10:00Z', machineCode: 'M1',
  }, training: [], queue: [{
    orderNumber: 17, partNumber: 'PART-1', orderQuantity: 10,
    operation: { sequence: 10, description: 'Cut', workCenterCode: 'CUT', goodQuantity: 3 },
  }],
});

async function mount(t, roles = ['ShopFloorTerminal'], permissions = [], idle = 20) {
  const browser = installBrowser(t), calls = [];
  const page = browser.document.createElement('main'); browser.document.body.append(page);
  globalThis.fetch = async (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null;
    calls.push({ path, body });
    if (path === '/api/execution/reason-codes') return response([]);
    if (path === '/api/shopfloor/identify') return response(operator(body.badge));
    if (path.startsWith('/api/execution/shifts/handoffs?')) return response([{
      handoffId: 22, workCenterCode: 'CUT', shiftCode: 'DAY', shiftUntil: '2026-10-18T07:00:00Z',
      reviewedAt: '2026-10-18T07:20:00Z', context: 'Verify the first piece before the second run.',
    }]);
    return response({ message: 'Saved.', entityId: '22' });
  };
  let kiosk = false;
  const controller = await shopFloorPage(page, { me: { roles, permissions, shopFloorIdleTimeoutSeconds: idle }, kioskMode: false, setKioskMode: value => { kiosk = value; } });
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
