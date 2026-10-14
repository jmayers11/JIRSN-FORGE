import assert from 'node:assert/strict';
import test from 'node:test';
import { shopFloorPage } from '../../src/Forge.Host/wwwroot/js/pages/shopfloor.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, deferred, flush, fire } from './browser-harness.mjs';

const operatorPersonId = 'bd97b4ea-b83a-43b0-aa65-377d614ee66a';
const operationCode = 'forge:v1:configured-plant:operation:17:10';
const config = { namespace: 'configured-plant', maxCodeLength: 1024, maxKeyLength: 40, supportedKinds: ['order', 'operation', 'material', 'location'], version: 'v1' };
const queue = [{ orderNumber: 17, partNumber: 'PART-A', orderQuantity: 10, operation: { sequence: 10, description: 'Cut', workCenterCode: 'CUT', eligibleMachines: ['M1'], goodQuantity: 0 } }];
const person = (labor, queueView = queue) => ({ personId: operatorPersonId, displayName: 'Current verified operator', hasPin: true, isWorker: true, clockedInSince: '2026-10-14T13:00:00Z', queue: queueView, training: [], labor });
const initialSession = () => ({ id: '1d8f8421-28e4-4f7d-88ad-4890313c6aa9', version: 3, state: 'Active', operatorPersonId, operationCode, machineCode: 'M1', selections: [{ kind: 'Operation', code: operationCode, subjectCode: operationCode, inputMode: 'KeyboardWedge' }], requiredKinds: ['Operation'], manualAllowed: true, expiresAt: new Date(Date.now() + 120000).toISOString(), policy: { code: 'EXECUTION', revision: 2 }, accepted: [] });

async function harness(t, { adopted = true, required = true, route = () => null, queueView = queue } = {}) {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  let labor = null, scan = initialSession();
  const before = { subject: session.subject, token: session.token }; session.subject = 'shopfloor-session-user'; session.token = null; t.after(() => Object.assign(session, before));
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body, headers: options.headers });
    const override = route(path, body); if (override) return Promise.resolve(override);
    if (path === '/api/execution/reason-codes') return Promise.resolve(response([]));
    if (path === '/api/shopfloor/identify') return Promise.resolve(response(person(labor, queueView)));
    if (path === '/api/identification/config') return Promise.resolve(response(config));
    if (path === '/api/identification/scanners/config') return Promise.resolve(response({ configured: false, adapters: [], supportedInputModes: ['Manual', 'KeyboardWedge'] }));
    if (path.startsWith('/api/identification/scan-sessions/requirements?')) return Promise.resolve(response({ adopted, required, manualAllowed: true, requiredKinds: required ? ['Operation'] : [] }));
    if (path === '/api/identification/scan-sessions/current') return Promise.resolve(response({ id: null }));
    if (path === '/api/identification/scan-sessions' && body) { scan = { ...scan, machineCode: body.machineCode }; return Promise.resolve(response({ entityId: '1d8f8421-28e4-4f7d-88ad-4890313c6aa9', message: 'Staging begun.' })); }
    if (path === '/api/identification/scan-sessions/1d8f8421-28e4-4f7d-88ad-4890313c6aa9') return Promise.resolve(response(scan));
    if (path === '/api/shopfloor/start') { labor = { orderNumber: 17, sequence: 10, role: 'Operator', startedAt: '2026-10-14T14:00:00Z', machineCode: 'M1' }; scan = { ...scan, accepted: [{ commandType: 'StartOperation', entityType: 'Operation', entityId: '17/10', auditId: 'audit-start', correlationId: 'correlation-start', recordedAt: '2026-10-14T14:00:00Z' }] }; return Promise.resolve(response({ message: 'Started.' })); }
    if (path === '/api/shopfloor/report') { scan = { ...scan, accepted: [...scan.accepted, { commandType: 'ReportProduction', entityType: 'Operation', entityId: '17/10', auditId: 'audit-report', correlationId: 'correlation-report', recordedAt: '2026-10-14T14:10:00Z' }] }; return Promise.resolve(response({ message: 'Reported.' })); }
    return Promise.resolve(response({ message: 'Recorded.' }));
  };
  const controller = await shopFloorPage(page, { me: { roles: ['ShopFloorTerminal'], shopFloorIdleTimeoutSeconds: 60 } }); t.after(controller.dispose);
  page.querySelector('#badge').value = 'B-17'; fire(page.querySelector('form[data-scan="badge"]'), 'submit'); await flush();
  return { ...browser, calls, page, controller, stage: async () => { fire(page.querySelector('[data-stage-work]'), 'click'); await flush(); page.querySelector('[data-session-reason]').value = 'Stage this verified operation'; fire(page.querySelector('[data-session-start]'), 'click'); await flush(); } };
}

test('mandatory staging uses a captured current header for Start and a refreshed explicit header for Report', async t => {
  const h = await harness(t); await h.stage(); fire(h.page.querySelector('[data-start]'), 'click'); await flush();
  const start = h.calls.find(call => call.path === '/api/shopfloor/start'); assert.equal(start.headers['X-Forge-Scan-Session'], '1d8f8421-28e4-4f7d-88ad-4890313c6aa9'); assert.equal(String(start.headers['X-Forge-Scan-Version']), '3'); assert.equal(start.body.badge, 'B-17'); assert.equal(start.body.machineCode, 'M1');
  assert.match(h.page.querySelector('[data-scan-session-host]').textContent, /audit-start/);
  h.page.querySelector('#good').value = 2; fire(h.page.querySelector('#report'), 'submit'); await flush();
  const report = h.calls.find(call => call.path === '/api/shopfloor/report'); assert.equal(report.headers['X-Forge-Scan-Session'], '1d8f8421-28e4-4f7d-88ad-4890313c6aa9'); assert.equal(String(report.headers['X-Forge-Scan-Version']), '3'); assert.equal(report.body.badge, 'B-17'); assert.equal(report.body.good, 2);
  assert.match(h.page.querySelector('[data-scan-session-host]').textContent, /audit-report/);
});

test('a required operation cannot dispatch Start with an omitted or unsatisfied session', async t => {
  const h = await harness(t); fire(h.page.querySelector('[data-start]'), 'click'); await flush();
  assert.equal(h.calls.filter(call => call.path === '/api/shopfloor/start').length, 0); assert.match(h.document.querySelector('.toast').textContent, /Stage this operation/);
  assert.ok(h.page.querySelector('#clock-out'));
});

test('explicit no-adoption summary preserves legacy Start without namespace configuration or scan headers', async t => {
  const h = await harness(t, { adopted: false, required: false, route: path => path === '/api/identification/config' ? response({ detail: 'Identification is not configured.', code: 'identification_not_configured' }, 503) : null });
  fire(h.page.querySelector('[data-start]'), 'click'); await flush(); const start = h.calls.find(call => call.path === '/api/shopfloor/start');
  assert.ok(start); assert.equal(start.headers['X-Forge-Scan-Session'], undefined); assert.equal(h.calls.some(call => call.path === '/api/identification/config'), false);
  assert.ok(h.calls.some(call => call.path.includes('/requirements?orderNumber=17&sequence=10')));
});

test('withdrawn or inaccessible requirements never turn into legacy fallback', async t => {
  const h = await harness(t, { route: path => path.includes('/requirements?') ? response({ detail: 'The current adopted policy is unavailable.', code: 'qms_policy_unavailable' }, 409) : null }); fire(h.page.querySelector('[data-start]'), 'click'); await flush();
  assert.equal(h.calls.filter(call => call.path === '/api/shopfloor/start').length, 0); assert.match(h.document.querySelector('.toast').textContent, /policy is unavailable/);
});

test('a requirements response arriving after switch cannot dispatch the earlier operator command', async t => {
  const pending = deferred(); const h = await harness(t, { route: path => path.includes('/requirements?') ? pending.promise : null }); fire(h.page.querySelector('[data-start]'), 'click'); await flush();
  fire(h.page.querySelector('#done'), 'click'); pending.resolve(response({ adopted: false, required: false, requiredKinds: [] })); await flush();
  assert.equal(h.calls.filter(call => call.path === '/api/shopfloor/start').length, 0); assert.ok(h.page.querySelector('#badge'));
});

test('changing the displayed eligible machine invalidates staged references and requires a new current session', async t => {
  const q = { ...queue[0], operation: { ...queue[0].operation, eligibleMachines: ['M1', 'M2'] } };
  const h = await harness(t, { queueView: [q] }); await h.stage(); const machine = h.page.querySelector('[data-machine]'); machine.value = 'M2'; fire(machine, 'change'); await flush();
  assert.equal(h.page.querySelector('[data-session-stage]'), null); assert.ok(h.calls.some(call => call.path.endsWith('/invalidate')));
  fire(h.page.querySelector('[data-start]'), 'click'); await flush(); assert.equal(h.calls.filter(call => call.path === '/api/shopfloor/start').length, 0); assert.ok(h.calls.some(call => call.path.includes('/requirements?orderNumber=17&sequence=10&machineCode=M2')));
});
