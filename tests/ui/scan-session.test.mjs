import assert from 'node:assert/strict';
import test from 'node:test';
import { mountScanSession, operationIdentificationCode } from '../../src/Forge.Host/wwwroot/js/scan-session.mjs';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, deferred, flush, fire } from './browser-harness.mjs';

const operatorPersonId = 'bd97b4ea-b83a-43b0-aa65-377d614ee66a';
const config = { namespace: 'configured-plant', maxCodeLength: 1024, maxKeyLength: 40, supportedKinds: ['order', 'operation', 'material', 'location'], version: 'v1' };
const operationCode = 'forge:v1:configured-plant:operation:17:10';
const materialCode = 'forge:v1:configured-plant:material:PART-A:LOT-7';
const material = { code: materialCode, namespace: config.namespace, kind: 'material', partNumber: 'PART-A', lotNumber: 'LOT-7', revision: 'A', status: 'Recorded' };
const initial = () => ({ id: 'session-1', version: 3, state: 'Active', operatorPersonId, operationCode, machineCode: null, selections: [], requiredKinds: ['Operation', 'Material'], manualAllowed: true, expiresAt: new Date(Date.now() + 120000).toISOString(), policy: { code: 'EXECUTION-POLICY', revision: 2, adoptionId: 'ADOPT-1', fingerprint: 'actual-policy-fingerprint' }, accepted: [] });

async function harness(t, { route = () => null, manualAllowed = true, machineCode = null, drivers = {}, scanners = { configured: false, adapters: [], supportedInputModes: ['Manual', 'KeyboardWedge'] } } = {}) {
  const browser = installBrowser(t), calls = [], outages = [], element = browser.document.createElement('section'); browser.document.body.append(element);
  let live = true, view = { ...initial(), manualAllowed, machineCode };
  const before = { subject: session.subject, token: session.token }; session.subject = 'scan-test-user'; session.token = null; t.after(() => Object.assign(session, before));
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body, headers: options.headers });
    const override = route(path, body); if (override) return Promise.resolve(override);
    if (path === '/api/identification/config') return Promise.resolve(response(config));
    if (path === '/api/identification/scanners/config') return Promise.resolve(response(scanners));
    if (path.startsWith('/api/identification/scan-sessions/requirements?')) return Promise.resolve(response({ adopted: true, required: true, manualAllowed, requiredKinds: ['Operation', 'Material'], policy: { code: 'EXECUTION-POLICY', revision: 2 } }));
    if (path === '/api/identification/resolve') return Promise.resolve(response(material));
    if (path === '/api/identification/scan-sessions/current') return Promise.resolve(response({ id: null }));
    if (path === '/api/identification/scan-sessions' && body) return Promise.resolve(response({ message: 'Staging begun.', entityId: 'session-1' }));
    if (path === '/api/identification/scan-sessions/session-1/stages') { view = { ...view, version: view.version + 1, selections: [{ kind: 'Material', code: body.code, subjectCode: materialCode, inputMode: body.inputMode, adapterId: body.adapterId, symbology: body.symbology, recordedAt: new Date().toISOString() }] }; return Promise.resolve(response({ message: 'Reference staged.', entityId: 'session-1' })); }
    if (path === '/api/identification/scan-sessions/session-1') return Promise.resolve(response(view));
    return Promise.resolve(response({ message: 'Invalidated.' }));
  };
  const controller = await mountScanSession(element, { operationCode, operatorPersonId, machineCode, badge: 'B-17', isCurrent: () => live, onUnavailable: error => outages.push(error), drivers }); t.after(controller.dispose);
  return { ...browser, element, calls, outages, controller, contextLost: () => { live = false; }, start: async () => { element.querySelector('[data-session-reason]').value = 'Stage the selected operation'; fire(element.querySelector('[data-session-start]'), 'click'); await flush(); }, resolve: async (code = materialCode) => {
    const input = element.querySelector('#session-reference-code'); input.value = code; fire(input, 'keydown', { key: 'Enter' }); await flush(); fire(element.querySelector('[data-identify-use]'), 'click');
  } };
}

test('operation staging code uses configured namespace and canonical positive order/sequence values', () => {
  assert.equal(operationIdentificationCode(config, 17, 10), operationCode);
  assert.throws(() => operationIdentificationCode({ ...config, namespace: '' }, 17, 10), /configured canonical/);
  assert.throws(() => operationIdentificationCode(config, '017', 10), /configured canonical/);
  assert.equal(operationIdentificationCode(config, '9223372036854775807', '2147483647'), 'forge:v1:configured-plant:operation:9223372036854775807:2147483647');
  assert.throws(() => operationIdentificationCode(config, '9223372036854775808', 10), /configured canonical/);
  assert.throws(() => operationIdentificationCode(config, 17, '2147483648'), /configured canonical/);
  assert.throws(() => operationIdentificationCode(config, Number.MAX_SAFE_INTEGER + 1, 10), /configured canonical/);
});

test('server session is begun explicitly and offers an immutable version snapshot without dispatching production', async t => {
  const h = await harness(t); assert.equal(h.controller.snapshot, null); await h.start();
  assert.deepEqual(h.controller.snapshot, { id: 'session-1', version: 3 }); assert.equal(Object.isFrozen(h.controller.snapshot), true);
  assert.deepEqual(h.calls.find(call => call.path === '/api/identification/scan-sessions').body, { operationCode, machineCode: null, badge: 'B-17', previousSessionId: null, reason: 'Stage the selected operation' });
  assert.equal(h.calls.some(call => call.path.startsWith('/api/shopfloor/')), false);
  assert.match(h.element.innerHTML, /not authentication or physical capture proof/);
});

test('staging records the reviewed original label code with captured server version and refreshes its actual selection', async t => {
  const h = await harness(t); await h.start(); const before = h.controller.snapshot;
  const originalLabelCode = 'forge:v1:configured-plant:label:tracked-copy'; await h.resolve(originalLabelCode);
  h.element.querySelector('[data-stage-mode]').value = 'KeyboardWedge'; h.element.querySelector('[data-stage-symbology]').value = 'QR'; h.element.querySelector('[data-stage-reason]').value = 'Record this reviewed material label';
  fire(h.element.querySelector('[data-session-stage]'), 'click'); fire(h.element.querySelector('[data-session-stage]'), 'click'); await flush();
  assert.deepEqual(h.calls.filter(call => call.path.endsWith('/stages')).map(call => call.body), [{ expectedVersion: 3, code: originalLabelCode, inputMode: 'KeyboardWedge', adapterId: null, symbology: 'QR', reason: 'Record this reviewed material label' }]);
  assert.deepEqual(before, { id: 'session-1', version: 3 }); assert.deepEqual(h.controller.snapshot, { id: 'session-1', version: 4 }); assert.match(h.element.innerHTML, /declared Keyboard Wedge/);
  assert.equal(h.calls.some(call => call.path.startsWith('/api/shopfloor/')), false);
});

test('manual selection forbidden by the actual operation rule cannot be recorded', async t => {
  const h = await harness(t, { manualAllowed: false }); await h.start(); await h.resolve();
  h.element.querySelector('[data-stage-mode]').value = 'Manual'; h.element.querySelector('[data-stage-reason]').value = 'Attempt manual selection'; fire(h.element.querySelector('[data-session-stage]'), 'click'); await flush();
  assert.equal(h.calls.filter(call => call.path.endsWith('/stages')).length, 0); assert.match(h.document.querySelector('.toast').textContent, /does not allow manual selection/);
});

test('missing identification configuration cannot create a server session even when adoption is required', async t => {
  const h = await harness(t, { route: path => path === '/api/identification/config' ? response({ detail: 'Identification is not configured.', code: 'identification_not_configured' }, 503) : null }); await h.start();
  assert.equal(h.calls.filter(call => call.path === '/api/identification/scan-sessions' && call.body).length, 0); assert.equal(h.controller.snapshot, null); assert.match(h.document.querySelector('.toast').textContent, /Identification must be configured/);
});

test('configured adapter capture needs a whitelisted driver and late callbacks cannot populate a later context', async t => {
  let callback, stopped = false;
  const h = await harness(t, { scanners: { configured: true, adapters: [{ id: 'reader-1', displayName: 'Configured reader', enabled: true, availability: 'Unknown', symbologies: ['QR'] }], supportedInputModes: ['Manual', 'KeyboardWedge', 'ConfiguredAdapter'] }, drivers: { 'reader-1': { subscribe: value => { callback = value; return () => { stopped = true; }; } } } });
  await h.start(); h.element.querySelector('[data-stage-adapter]').value = 'reader-1'; fire(h.element.querySelector('[data-stage-use-adapter]'), 'click');
  callback({ code: materialCode, symbology: 'QR' }); assert.equal(h.element.querySelector('#session-reference-code').value, materialCode); assert.equal(h.element.querySelector('[data-stage-mode]').value, 'ConfiguredAdapter');
  await h.resolve(materialCode); h.element.querySelector('[data-stage-reason]').value = 'Declared configured-reader capture'; fire(h.element.querySelector('[data-session-stage]'), 'click'); await flush();
  assert.equal(h.calls.find(call => call.path.endsWith('/stages')).body.adapterId, 'reader-1'); assert.equal(stopped, true);
  h.contextLost(); callback({ code: 'STALE-CODE', symbology: 'QR' }); assert.notEqual(h.element.querySelector('#session-reference-code').value, 'STALE-CODE'); assert.equal(h.controller.snapshot, null);
});

test('selecting a configured adapter name without a capture cannot fabricate its input provenance', async t => {
  const h = await harness(t, { scanners: { configured: true, adapters: [{ id: 'reader-1', displayName: 'Configured reader', enabled: true, availability: 'Configured' }] }, drivers: { 'reader-1': { subscribe: () => () => {} } } });
  await h.start(); await h.resolve(); h.element.querySelector('[data-stage-mode]').value = 'ConfiguredAdapter'; h.element.querySelector('[data-stage-adapter]').value = 'reader-1'; h.element.querySelector('[data-stage-reason]').value = 'Attempt declared adapter'; fire(h.element.querySelector('[data-session-stage]'), 'click'); await flush();
  assert.equal(h.calls.filter(call => call.path.endsWith('/stages')).length, 0); assert.match(h.document.querySelector('.toast').textContent, /Capture this code through the configured adapter/);
});

test('context loss or disposal discards a session creation response before fetching or using it', async t => {
  for (const dispose of [false, true]) await t.test(dispose ? 'dispose' : 'context loss', async child => {
    const pending = deferred(); const h = await harness(child, { route: (path, body) => path === '/api/identification/scan-sessions' && body ? pending.promise : null });
    await h.start(); if (dispose) h.controller.dispose(); else h.contextLost();
    pending.resolve(response({ entityId: 'old-session' })); await flush();
    assert.equal(h.controller.snapshot, null); assert.equal(h.calls.some(call => call.path.includes('old-session')), false);
  });
});

test('staging failure caused by outage clears the snapshot and invokes terminal lock handling without retry', async t => {
  const h = await harness(t, { route: (path, body) => path.endsWith('/stages') && body ? Promise.reject(new TypeError('Failed to fetch')) : null }); await h.start(); await h.resolve();
  h.element.querySelector('[data-stage-mode]').value = 'Manual'; h.element.querySelector('[data-stage-reason]').value = 'Record reviewed material'; fire(h.element.querySelector('[data-session-stage]'), 'click'); await flush();
  assert.equal(h.controller.snapshot, null); assert.equal(h.outages.length, 1); assert.equal(h.calls.filter(call => call.path.endsWith('/stages')).length, 1);
});

test('server-reported accepted provenance is displayed as recorded evidence rather than a UI-generated success', async t => {
  const accepted = { ...initial(), version: 4, accepted: [{ commandType: 'StartOperation', entityType: 'Operation', entityId: '17/10', correlationId: 'correlation-1', auditId: 'audit-1', eventIds: ['event-1'], operatorPersonId, recordedAt: '2026-10-14T15:00:00Z' }] };
  const h = await harness(t, { route: path => path === '/api/identification/scan-sessions/session-1' ? response(accepted) : null }); await h.start();
  assert.match(h.element.innerHTML, /StartOperation/); assert.match(h.element.innerHTML, /audit-1/); assert.match(h.element.innerHTML, /correlation-1/);
  assert.equal(h.calls.some(call => call.path.startsWith('/api/shopfloor/')), false);
});

test('a pending stage cannot supply a business-command snapshot before the current server version is read', async t => {
  const pending = deferred(); let staged = false;
  const h = await harness(t, { route: (path, body) => path.endsWith('/stages') && body ? (staged = true, pending.promise) : path === '/api/identification/scan-sessions/session-1' && staged ? response({ ...initial(), version: 4, selections: [{ kind: 'Material', code: materialCode, subjectCode: materialCode, inputMode: 'Manual' }] }) : null });
  await h.start(); await h.resolve(); h.element.querySelector('[data-stage-mode]').value = 'Manual'; h.element.querySelector('[data-stage-reason]').value = 'Record the reviewed material'; fire(h.element.querySelector('[data-session-stage]'), 'click'); await flush();
  assert.equal(h.controller.snapshot, null); pending.resolve(response({ message: 'Recorded.' })); await flush(); assert.deepEqual(h.controller.snapshot, { id: 'session-1', version: 4 });
});

test('a rejected stage requires an explicit current server refresh before another business-command snapshot', async t => {
  const h = await harness(t, { route: (path, body) => path.endsWith('/stages') && body ? response({ detail: 'The recorded session version changed.', code: 'scan_session_version' }, 409) : null }); await h.start(); await h.resolve();
  h.element.querySelector('[data-stage-mode]').value = 'Manual'; h.element.querySelector('[data-stage-reason]').value = 'Record the reviewed material'; fire(h.element.querySelector('[data-session-stage]'), 'click'); await flush();
  assert.equal(h.controller.snapshot, null); assert.match(h.element.textContent, /Refresh this recorded session before further use/); assert.equal(h.calls.filter(call => call.path.endsWith('/stages')).length, 1);
  await h.controller.refresh(); assert.deepEqual(h.controller.snapshot, { id: 'session-1', version: 3 });
});

test('local expiry clears the command snapshot and recorded-reference controls without a mutation or retry', async t => {
  const h = await harness(t); await h.start(); const before = h.calls.length; h.timers.advance(120001);
  assert.equal(h.controller.snapshot, null); assert.equal(h.element.querySelector('[data-session-stage]'), null); assert.match(h.element.textContent, /Expired/); assert.equal(h.calls.length, before);
});

function installCamera(t, getUserMedia, detect) {
  const previous = Object.fromEntries(['navigator', 'BarcodeDetector'].map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { mediaDevices: { getUserMedia } } });
  Object.defineProperty(globalThis, 'BarcodeDetector', { configurable: true, value: class { static async getSupportedFormats() { return ['qr_code']; } async detect(video) { return detect(video); } } });
  t.after(() => { for (const [name, descriptor] of Object.entries(previous)) if (descriptor) Object.defineProperty(globalThis, name, descriptor); else delete globalThis[name]; });
}

test('camera capture requires an explicit user command and stages the captured reference without proving device trust', async t => {
  let requested = 0, stopped = false;
  installCamera(t, async () => { requested++; return { getTracks: () => [{ stop: () => { stopped = true; } }] }; }, () => [{ rawValue: materialCode, format: 'qr_code' }]);
  const h = await harness(t); await h.start(); assert.equal(requested, 0); h.element.querySelector('[data-stage-camera]').play = async () => {};
  fire(h.element.querySelector('[data-stage-use-camera]'), 'click'); await flush(); assert.equal(requested, 1); assert.equal(h.element.querySelector('#session-reference-code').value, materialCode); assert.equal(h.element.querySelector('[data-stage-mode]').value, 'Camera');
  await h.resolve(); h.element.querySelector('[data-stage-reason]').value = 'Review the captured material reference'; fire(h.element.querySelector('[data-session-stage]'), 'click'); await flush();
  assert.equal(h.calls.find(call => call.path.endsWith('/stages')).body.inputMode, 'Camera'); assert.equal(h.calls.find(call => call.path.endsWith('/stages')).body.symbology, 'QR'); assert.equal(stopped, true);
  assert.match(h.element.textContent, /not authentication or physical capture proof/);
});

test('a camera stream arriving after operator-context loss is stopped and cannot populate the later context', async t => {
  const pending = deferred(); let stopped = false;
  installCamera(t, () => pending.promise, () => [{ rawValue: 'STALE-CODE', format: 'qr_code' }]);
  const h = await harness(t); await h.start(); fire(h.element.querySelector('[data-stage-use-camera]'), 'click'); h.contextLost(); pending.resolve({ getTracks: () => [{ stop: () => { stopped = true; } }] }); await flush();
  assert.equal(stopped, true); assert.notEqual(h.element.querySelector('#session-reference-code').value, 'STALE-CODE'); assert.equal(h.controller.snapshot, null); assert.equal(h.calls.filter(call => call.path.endsWith('/stages')).length, 0);
});

test('new staging captures its authenticated current pointer before POST and handles compare-and-swap rejection without retry', async t => {
  const h = await harness(t, { machineCode: 'M1', route: (path, body) => path === '/api/identification/scan-sessions/current' ? response({ id: 'previous-current-session' }) : path === '/api/identification/scan-sessions' && body ? response({ detail: 'The current work context changed.', code: 'scan_session_current_changed' }, 409) : null }); await h.start();
  const creates = h.calls.filter(call => call.path === '/api/identification/scan-sessions' && call.body); assert.equal(creates.length, 1); assert.equal(creates[0].body.previousSessionId, 'previous-current-session'); assert.equal(creates[0].body.machineCode, 'M1');
  assert.ok(h.calls.some(call => call.path.includes('/requirements?') && call.path.includes('&machineCode=M1'))); assert.equal(h.controller.snapshot, null); assert.match(h.element.textContent, /current work context changed/);
});

test('a current-pointer response after context loss cannot dispatch an earlier session create', async t => {
  const pending = deferred(), h = await harness(t, { route: path => path === '/api/identification/scan-sessions/current' ? pending.promise : null }); await h.start(); h.contextLost(); pending.resolve(response({ id: 'later-session' })); await flush();
  assert.equal(h.calls.filter(call => call.path === '/api/identification/scan-sessions' && call.body).length, 0); assert.equal(h.controller.snapshot, null);
});

test('a server session for another selected machine cannot be used in the current context', async t => {
  const h = await harness(t, { machineCode: 'M1', route: path => path === '/api/identification/scan-sessions/session-1' ? response({ ...initial(), machineCode: 'M2' }) : null }); await h.start();
  assert.equal(h.controller.snapshot, null); assert.match(h.element.textContent, /selected machine/); assert.equal(h.calls.filter(call => call.path.endsWith('/stages')).length, 0);
});

test('switching configured readers clears the prior reference and rejects old callbacks within the same operation', async t => {
  const callbacks = {}, stopped = [];
  const h = await harness(t, { scanners: { configured: true, adapters: ['A', 'B'].map(id => ({ id, displayName: `Reader ${id}`, enabled: true, availability: 'Unknown' })) }, drivers: Object.fromEntries(['A', 'B'].map(id => [id, { subscribe: callback => { callbacks[id] = callback; return () => stopped.push(id); } }])) }); await h.start();
  h.element.querySelector('[data-stage-adapter]').value = 'A'; fire(h.element.querySelector('[data-stage-use-adapter]'), 'click'); callbacks.A({ code: materialCode, symbology: 'QR' }); await h.resolve();
  h.element.querySelector('[data-stage-adapter]').value = 'B'; fire(h.element.querySelector('[data-stage-use-adapter]'), 'click'); assert.equal(h.element.querySelector('#session-reference-code').value, ''); assert.equal(h.element.querySelector('[data-stage-preview]').textContent, ''); assert.ok(stopped.includes('A'));
  callbacks.A({ code: 'LATE-READER-A', symbology: 'QR' }); assert.equal(h.element.querySelector('#session-reference-code').value, ''); callbacks.B({ code: materialCode, symbology: 'QR' }); assert.equal(h.element.querySelector('#session-reference-code').value, materialCode);
  assert.equal(h.calls.filter(call => call.path.endsWith('/stages')).length, 0);
});

test('a superseded camera startup stops only its own late stream and cannot replace a newer capture', async t => {
  const first = deferred(), second = deferred(); let requested = 0, oldStopped = false, currentStopped = false;
  installCamera(t, () => ++requested === 1 ? first.promise : second.promise, () => [{ rawValue: materialCode, format: 'qr_code' }]);
  const h = await harness(t); await h.start(); h.element.querySelector('[data-stage-camera]').play = async () => {};
  fire(h.element.querySelector('[data-stage-use-camera]'), 'click'); fire(h.element.querySelector('[data-stage-use-camera]'), 'click'); second.resolve({ getTracks: () => [{ stop: () => { currentStopped = true; } }] }); await flush();
  assert.equal(h.element.querySelector('#session-reference-code').value, materialCode); first.resolve({ getTracks: () => [{ stop: () => { oldStopped = true; } }] }); await flush();
  assert.equal(oldStopped, true); assert.equal(currentStopped, false); assert.equal(h.element.querySelector('#session-reference-code').value, materialCode); h.controller.dispose(); assert.equal(currentStopped, true);
});
