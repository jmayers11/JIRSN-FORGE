import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { shopFloorPage } from '../../src/Forge.Host/wwwroot/js/pages/shopfloor.js';
import { toast, manageDialogFocus, confirmWithReason } from '../../src/Forge.Host/wwwroot/js/ui.js';
import { installBrowser, response, deferred, flush, fire } from './browser-harness.mjs';

const person = badge => ({ displayName: `Operator ${badge}`, personId: badge, clockedInSince: '2026-10-12T09:00:00Z', hasPin: true, isWorker: true, queue: [], training: [] });

async function terminalPage(t, route = (path, body) => response(person(body?.badge)), idleTimeoutSeconds = undefined) {
  const browser = installBrowser(t), calls = [];
  const page = browser.document.createElement('div');
  browser.document.body.append(page);
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null;
    calls.push({ path, body });
    return path === '/api/execution/reason-codes' ? Promise.resolve(response([])) : Promise.resolve(route(path, body));
  };
  const controller = await shopFloorPage(page, { me: { roles: ['ShopFloorTerminal'], shopFloorIdleTimeoutSeconds: idleTimeoutSeconds } });
  t.after(controller.dispose);
  return { ...browser, page, calls, controller, scan: async badge => {
    page.querySelector('#badge').value = badge;
    fire(page.querySelector('form[data-scan="badge"]'), 'submit');
    await flush();
  } };
}

test('rendered shop-floor controls and persistent operator region expose accessible names', async t => {
  const html = await readFile(new URL('../../src/Forge.Host/wwwroot/index.html', import.meta.url), 'utf8');
  assert.match(html, /<html lang="en">/);
  const h = await terminalPage(t, (path, body) => response({ ...person(body.badge), queue: [{ orderNumber: 17, partNumber: 'PART', orderQuantity: 10, operation: { sequence: 10, description: 'Cut', eligibleMachines: ['M1', 'M2'], goodQuantity: 0 } }] }));
  const status = h.document.querySelector('[aria-label="Current shop-floor operator"]');
  assert.equal(status.getAttribute('role'), 'status');
  assert.equal(status.getAttribute('aria-live'), 'polite');
  await h.scan('B-17');
  assert.equal(h.page.querySelector('[data-machine]').getAttribute('aria-label'), 'Machine for order 17 operation 10');
  assert.equal(status.textContent, 'Current operator: Operator B-17.');
  fire(h.page.querySelector('#done'), 'click');
  assert.equal(h.document.querySelector('[aria-label="Current shop-floor operator"]'), status);
  assert.equal(status.textContent, 'Terminal locked. Scan your badge.');
});

test('shared layout classes exist and critical text/background pairs meet WCAG AA', async () => {
  const css = await readFile(new URL('../../src/Forge.Host/wwwroot/css/forge.css', import.meta.url), 'utf8');
  const rule = selector => css.match(new RegExp(`${selector}\\s*\\{([^}]+)\\}`))?.[1] ?? '';
  const hex = value => value.match(/#[0-9A-F]{6}/i)?.[0];
  const luminance = value => {
    const [r, g, b] = value.slice(1).match(/../g).map(part => parseInt(part, 16) / 255).map(channel => channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4);
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
  };
  const contrast = (foreground, background) => {
    const a = luminance(foreground), b = luminance(background);
    return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
  };
  const backgroundStops = selector => {
    const declaration = rule(selector), gradient = declaration.match(/background:\s*linear-gradient\(180deg,\s*(#[0-9A-F]{6}),\s*(#[0-9A-F]{6})/i);
    if (gradient) return gradient.slice(1);
    const token = declaration.match(/background:\s*var\((--[\w-]+)\)/)?.[1];
    return token ? [hex(css.match(new RegExp(`${token}:\\s*(#[0-9A-F]{6})`, 'i'))?.[1] ?? '')] : [];
  };
  for (const selector of ['\\.section, \\.card', '\\.section-head, \\.card-head, \\.panel-head, \\.row', '\\.stack', '\\.inline', '\\.eyebrow', '\\.notice\\.info', '\\.table tr\\.on td']) {
    assert.notEqual(rule(selector), '', `expected a style rule for ${selector}`);
  }
  const faint = hex(css.match(/--faint:\s*(#[0-9A-F]{6})/i)?.[1] ?? '');
  assert.ok(faint); for (const surface of ['#0F1419', '#171C22', '#1C2229']) assert.ok(contrast(faint, surface) >= 4.5, `${faint} on ${surface}`);
  const white = '#FFFFFF';
  for (const selector of ['\\.btn\\.primary', '\\.seg button\\.on']) {
    const backgrounds = backgroundStops(selector);
    assert.ok(backgrounds.length, `expected an explicit accessible background for ${selector}`);
    for (const background of backgrounds) assert.ok(contrast(white, background) >= 4.5, `${white} on ${background}`);
  }
  for (const selector of ['\\.pill\\.ok', '\\.pill\\.bad']) {
    const backgrounds = backgroundStops(selector);
    assert.ok(backgrounds.length); for (const background of backgrounds) assert.ok(contrast(white, background) >= 4.5, `${white} on ${background}`);
  }
  const orangeBadge = backgroundStops('\\.badge\\.orange');
  assert.ok(orangeBadge.length); for (const background of orangeBadge) assert.ok(contrast(white, background) >= 4.5, `${white} on ${background}`);
  const activeLink = rule('\\.nav a\\.active');
  const activeGradient = activeLink.match(/background:\s*linear-gradient\(90deg,\s*var\(--red-tail\),\s*(#[0-9A-F]{6})\)/i);
  const redTail = hex(css.match(/--red-tail:\s*(#[0-9A-F]{6})/i)?.[1] ?? '');
  assert.ok(activeGradient && redTail, 'the active navigation link uses the explicit accessible red gradient');
  for (const background of [redTail, activeGradient[1]]) assert.ok(contrast(white, background) >= 4.5, `${white} on ${background}`);
  assert.match(activeLink, /border-color:\s*#7C2D12/i);
});

test('modal Tab and Shift+Tab wrap visible enabled controls; Escape restores its opener', t => {
  const { document } = installBrowser(t);
  const opener = document.createElement('button'); document.body.append(opener); opener.focus();
  const modal = document.createElement('div');
  modal.innerHTML = '<h2>Set PIN</h2><input hidden><input disabled><input id="first"><button class="hidden">Hidden</button><button id="last">Save</button>';
  document.body.append(modal);
  let closed = false, release;
  release = manageDialogFocus(modal, () => { closed = true; modal.remove(); release(); });
  assert.equal(modal.getAttribute('role'), 'dialog');
  assert.equal(modal.getAttribute('aria-modal'), 'true');
  assert.equal(modal.getAttribute('aria-labelledby'), modal.querySelector('h2').id);
  const first = modal.querySelector('#first'), last = modal.querySelector('#last');
  assert.equal(document.activeElement, first);
  last.focus();
  assert.equal(fire(last, 'keydown', { key: 'Tab' }).defaultPrevented, true);
  assert.equal(document.activeElement, first);
  assert.equal(fire(first, 'keydown', { key: 'Tab', shiftKey: true }).defaultPrevented, true);
  assert.equal(document.activeElement, last);
  opener.focus();
  assert.equal(document.activeElement, first, 'focus escaping to the background is returned to the dialog');
  fire(first, 'keydown', { key: 'Escape' });
  assert.equal(closed, true);
  assert.equal(document.activeElement, opener);
  assert.equal(document.listeners.get('keydown').size, 0);
  assert.equal(document.listeners.get('focusin').size, 0);
});

test('an empty modal traps focus on its root and a detached modal removes its listeners', t => {
  const { document } = installBrowser(t);
  const modal = document.createElement('div'); modal.innerHTML = '<h2>No controls</h2>';
  document.body.append(modal);
  const release = manageDialogFocus(modal, () => {});
  assert.equal(document.activeElement, modal);
  assert.equal(fire(modal, 'keydown', { key: 'Tab', shiftKey: true }).defaultPrevented, true);
  assert.equal(document.activeElement, modal);
  modal.remove();
  fire(document, 'keydown', { key: 'Tab' });
  assert.equal(document.listeners.get('keydown').size, 0);
  release();
});

test('reason dialogs trap focus, preserve entered reasons on action errors, and close with Escape', async t => {
  const { document } = installBrowser(t);
  const opener = document.createElement('button'); document.body.append(opener); opener.focus();
  const pending = confirmWithReason({ title: 'Mark assignment complete' }, async () => { throw new Error('The decision was rejected.'); });
  const modal = document.querySelector('.modal'), reason = modal.querySelector('#reason'), confirm = modal.querySelector('[data-ok]');
  assert.equal(modal.getAttribute('aria-modal'), 'true'); assert.equal(document.activeElement, reason);
  reason.value = 'The assigned inspection is finished.';
  fire(confirm, 'click'); await flush();
  assert.equal(modal.isConnected, true); assert.equal(reason.value, 'The assigned inspection is finished.');
  assert.match(modal.querySelector('[data-error]').textContent, /decision was rejected/);
  assert.equal(fire(reason, 'keydown', { key: 'Escape' }).defaultPrevented, true);
  assert.equal(await pending, null); assert.equal(document.activeElement, opener);
  assert.equal(document.listeners.get('keydown').size, 0); assert.equal(document.listeners.get('focusin').size, 0);
});

test('toast status and errors expose live roles and expire at their intended times', t => {
  const { document, timers } = installBrowser(t);
  toast('Saved.'); toast('Could not confirm.', true);
  const [status, error] = document.querySelectorAll('.toast');
  assert.equal(status.getAttribute('role'), 'status');
  assert.equal(status.getAttribute('aria-live'), 'polite');
  assert.equal(status.textContent, 'Saved.');
  assert.equal(error.getAttribute('role'), 'alert');
  assert.equal(error.getAttribute('aria-live'), 'assertive');
  assert.equal(error.getAttribute('aria-atomic'), 'true');
  timers.advance(3500);
  assert.equal(status.isConnected, false);
  assert.equal(error.isConnected, true);
  timers.advance(3500);
  assert.equal(error.isConnected, false);
});

test('an identify response arriving after idle expiry cannot restore the expired badge', async t => {
  const pending = deferred();
  const h = await terminalPage(t, () => pending.promise);
  await h.scan('B-17');
  h.timers.advance(60_000);
  pending.resolve(response(person('B-17'))); await flush();
  assert.ok(h.page.querySelector('#badge'));
  assert.equal(h.page.querySelector('#clock-out'), null);
  assert.equal(h.document.querySelector('[aria-label="Current shop-floor operator"]').textContent, 'Terminal locked. Scan your badge.');
});

test('the configured inactivity timeout locks at its boundary and closes an open PIN dialog', async t => {
  const h = await terminalPage(t, undefined, 2);
  await h.scan('B-17'); fire(h.page.querySelector('#pin'), 'click');
  const modal = h.document.querySelector('.modal');
  h.timers.advance(1999);
  assert.equal(modal.isConnected, true);
  assert.equal(h.page.querySelector('#badge'), null);
  h.timers.advance(1);
  assert.equal(modal.isConnected, false);
  assert.ok(h.page.querySelector('#badge'));
  assert.equal(h.document.querySelector('[aria-label="Current shop-floor operator"]').textContent, 'Terminal locked. Scan your badge.');
});

test('invalid inactivity timeout values preserve the one-minute default', async t => {
  for (const value of [0, 3601, '2']) {
    await t.test(`invalid timeout ${JSON.stringify(value)}`, async child => {
      const h = await terminalPage(child, undefined, value);
      await h.scan('B-17');
      h.timers.advance(59_999);
      assert.equal(h.page.querySelector('#badge'), null);
      h.timers.advance(1);
      assert.ok(h.page.querySelector('#badge'));
      assert.equal(h.document.querySelector('[aria-label="Current shop-floor operator"]').textContent, 'Terminal locked. Scan your badge.');
    });
  }
});

test('scans are associated with their responses when identify requests complete out of order', async t => {
  const old = deferred(), current = deferred();
  const h = await terminalPage(t, (path, body) => body.badge === 'B-17' ? old.promise : current.promise);
  await h.scan('B-17'); await h.scan('B-18');
  current.resolve(response(person('B-18'))); await flush();
  old.resolve(response(person('B-17'))); await flush();
  assert.match(h.page.innerHTML, /Operator B-18/);
  assert.doesNotMatch(h.page.innerHTML, /Operator B-17/);
});

test('a late clock-out result cannot refresh or clear the next scanned operator', async t => {
  const pending = deferred();
  const h = await terminalPage(t, (path, body) => path === '/api/shopfloor/clock-out' ? pending.promise : response(person(body.badge)));
  await h.scan('B-17');
  fire(h.page.querySelector('#clock-out'), 'click');
  assert.equal(h.calls.at(-1).body.badge, 'B-17');
  fire(h.page.querySelector('#done'), 'click'); await h.scan('B-18');
  pending.resolve(response({ message: 'Clocked out.' })); await flush();
  assert.match(h.page.innerHTML, /Operator B-18/);
  assert.deepEqual(h.calls.filter(c => c.path === '/api/shopfloor/identify').map(c => c.body.badge), ['B-17', 'B-18']);
  assert.equal(h.document.querySelector('.toast'), null, 'a previous operator\'s completion is not shown as the current operator\'s action');
});

test('idle locking closes PIN dialogs and late saves cannot refresh a different operator', async t => {
  const pending = deferred();
  const h = await terminalPage(t, (path, body) => path === '/api/shopfloor/pin' ? pending.promise : response(person(body.badge)));
  await h.scan('B-17');
  fire(h.page.querySelector('#pin'), 'click');
  const modal = h.document.querySelector('.modal'), save = modal.querySelector('[data-ok]');
  modal.querySelector('#new').value = '1234'; modal.querySelector('#new2').value = '1234';
  fire(save, 'click');
  assert.equal(h.calls.at(-1).body.badge, 'B-17');
  h.timers.advance(60_000);
  assert.equal(h.document.querySelector('.modal'), null);
  assert.equal(h.document.querySelector('.scrim'), null);
  assert.equal(h.document.listeners.get('keydown').size, 0);
  await h.scan('B-18');
  pending.resolve(response({ message: 'PIN saved.' })); await flush();
  assert.match(h.page.innerHTML, /Operator B-18/);
  assert.deepEqual(h.calls.filter(c => c.path === '/api/shopfloor/identify').map(c => c.body.badge), ['B-17', 'B-18']);
  fire(save, 'click'); await flush();
  assert.equal(h.calls.filter(c => c.path === '/api/shopfloor/pin').length, 1, 'a detached dialog cannot issue a new command');
});

test('typing in a modal extends the operator\'s idle timeout; manual lock closes the modal', async t => {
  const h = await terminalPage(t);
  await h.scan('B-17'); fire(h.page.querySelector('#pin'), 'click');
  const modal = h.document.querySelector('.modal');
  h.timers.advance(59_000);
  fire(modal.querySelector('#new'), 'input');
  h.timers.advance(2000);
  assert.equal(modal.isConnected, true);
  fire(h.page.querySelector('#lock-terminal'), 'click');
  assert.equal(modal.isConnected, false);
  assert.ok(h.page.querySelector('#badge'));
});

test('an uncertain action outcome locks the terminal and closes its dialogs', async t => {
  const pending = deferred();
  const h = await terminalPage(t, (path, body) => path === '/api/shopfloor/pin' ? pending.promise : response(person(body.badge)));
  await h.scan('B-17'); fire(h.page.querySelector('#pin'), 'click');
  const modal = h.document.querySelector('.modal');
  modal.querySelector('#new').value = '1234'; modal.querySelector('#new2').value = '1234';
  fire(modal.querySelector('[data-ok]'), 'click');
  pending.reject(new TypeError('Failed to fetch')); await flush();
  assert.ok(h.page.querySelector('#badge'));
  assert.equal(h.document.querySelector('.modal'), null);
  assert.match(h.document.querySelector('.toast').textContent, /supervisor to check the record before retrying/);
  assert.equal(h.document.querySelector('.toast').getAttribute('role'), 'alert');
});

test('overlapping action clicks send one command and do not create an offline queue', async t => {
  const pending = deferred();
  const h = await terminalPage(t, (path, body) => path === '/api/shopfloor/clock-out' ? pending.promise : response(person(body.badge)));
  await h.scan('B-17');
  const button = h.page.querySelector('#clock-out');
  fire(button, 'click'); fire(button, 'click');
  assert.equal(h.calls.filter(c => c.path === '/api/shopfloor/clock-out').length, 1);
  pending.resolve(response({ message: 'Clocked out.' })); await flush();
  assert.ok(h.page.querySelector('#badge'));
  assert.equal(h.calls.filter(c => c.path === '/api/shopfloor/clock-out').length, 1);
});

test('disposing the page closes dialogs, clears its announcer and ignores pending responses', async t => {
  const pending = deferred();
  const h = await terminalPage(t, (path, body) => path === '/api/shopfloor/pin' ? pending.promise : response(person(body.badge)));
  await h.scan('B-17'); fire(h.page.querySelector('#pin'), 'click');
  const modal = h.document.querySelector('.modal');
  modal.querySelector('#new').value = '1234'; modal.querySelector('#new2').value = '1234';
  fire(modal.querySelector('[data-ok]'), 'click');
  h.controller.dispose();
  assert.equal(h.document.querySelector('.modal'), null);
  assert.equal(h.document.querySelector('[aria-label="Current shop-floor operator"]'), null);
  assert.equal(h.document.listeners.get('keydown').size, 0);
  pending.resolve(response({ message: 'Saved.' })); await flush();
  assert.equal(h.calls.filter(c => c.path === '/api/shopfloor/identify').length, 1);
  assert.equal(h.document.querySelector('.toast'), null);
});
