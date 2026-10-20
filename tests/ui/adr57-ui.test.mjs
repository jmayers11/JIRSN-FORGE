import assert from 'node:assert/strict';
import test from 'node:test';
import { appendWorkforceAdmin, personActionsForWorker, siteDate } from '../../src/Forge.Host/wwwroot/js/pages/workforce-admin.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

const personId = '1f9a60c2-0ff8-4276-8125-f17efb69570a';
  const worker = { personId, displayName: 'Operator One', homeWorkCenterCode: 'WC2', isActive: true };

function installCalls(t, handler) {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const oldSession = { subject: session.subject, token: session.token };
  session.subject = 'adr57-ui-test'; session.token = null;
  t.after(() => Object.assign(session, oldSession));
  const calls = [];
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null;
    calls.push({ path: String(path), options, body });
    return Promise.resolve(handler(String(path), options, body) ?? response([]));
  };
  return { browser, page, calls };
}

async function submit(browser, values = {}) {
  await flush();
  const modal = browser.document.querySelector('.modal');
  assert.ok(modal, 'expected an open form dialog');
  for (const [name, value] of Object.entries(values)) {
    const field = modal.querySelector(`#f-${name}`);
    if (field.type === 'checkbox') field.checked = value;
    else field.value = String(value);
  }
  modal.querySelector('#reason').value = 'Approved for the production plan';
  fire(modal.querySelector('[data-ok]'), 'click');
  await flush(); await flush();
}

test('worker moves and transfers use the separate audited commands and site dates', async t => {
  const { browser, page, calls } = installCalls(t, (path, options) => {
    if (path === '/api/kernel/directory') return response([{ personId, displayName: worker.displayName }]);
    if (path === '/api/competency/qualification-types') return response([]);
    if (path === '/api/resources/') return response({ sites: [
      { code: 'TOKYO', timeZone: 'Asia/Tokyo' }, { code: 'PLANT', timeZone: 'America/Los_Angeles' },
    ], safetyAreas: [{ code: 'AREA-2', siteCode: 'PLANT' }], workCenters: [
      { code: 'WC1', name: 'Cutting', safetyAreaCode: 'AREA-1', active: true },
      { code: 'WC2', name: 'Packing', safetyAreaCode: 'AREA-2', active: true },
    ] });
    if (path === '/api/workforce/workers') return response([worker]);
    if (path === `/api/competency/people/${personId}`) return response([]);
    if (path.startsWith('/api/workforce/moves?')) return response([]);
    if (path.startsWith('/api/workforce/transfers?')) return response([]);
    if (options.method === 'POST') return response({ succeeded: true, message: 'Recorded.' });
    return response([]);
  });
  const me = { personId: 'supervisor-id', permissions: ['workforce.read', 'workforce.supervise', 'workforce.manage'] };
  await appendWorkforceAdmin(page, me);
  const select = page.querySelector('#wf-person'); select.value = personId; fire(select, 'change'); await flush();
  const movementPanel = page.querySelector('[aria-label="Manage people"]');
  assert.match(movementPanel.innerHTML, /Moves and transfers/);
  assert.ok(personActionsForWorker(me, worker, personId).some(([action]) => action === 'move'));
  assert.ok(personActionsForWorker(me, worker, personId).some(([action]) => action === 'transfer'));
  assert.ok(!personActionsForWorker({ ...me, personId }, worker, personId).some(([action]) => action === 'transfer'), 'a person cannot transfer themselves');

  fire(page.querySelector('[data-wf="move"]'), 'click');
  assert.equal(browser.document.querySelector('#f-startsOn').value, siteDate(new Date(), 'America/Los_Angeles'));
  await submit(browser, { workCenterCode: 'WC2', startsOn: '2026-10-20', estimatedEndOn: '2026-11-02' });
  const move = calls.find(call => call.path === `/api/workforce/workers/${personId}/moves`);
  assert.deepEqual(move.body, { workCenterCode: 'WC2', startsOn: '2026-10-20', estimatedEndOn: '2026-11-02', reason: 'Approved for the production plan' });

  fire(page.querySelector('[data-wf="transfer"]'), 'click');
  await submit(browser, { workCenterCode: 'WC2', effectiveFrom: '2026-11-03' });
  const transfer = calls.find(call => call.path === `/api/workforce/workers/${personId}/transfer`);
  assert.deepEqual(transfer.body, { workCenterCode: 'WC2', effectiveFrom: '2026-11-03', reason: 'Approved for the production plan' });
  assert.equal(siteDate(new Date('2026-10-20T02:00:00Z'), 'America/New_York'), '2026-10-19');
});

test('routing operation family fields validate together and serialize to the existing API contract', async t => {
  installBrowser(t);
  const { routingOperationBody } = await import('../../src/Forge.Host/wwwroot/js/pages/engineering.js');
  const operation = routingOperationBody({ sequence: 10, description: 'Wind', workCenterCode: 'WIND', activityCode: 'WIND',
    eligibleMachines: 'winder-1, WINDER-2\nwinder-1', requiredQualifications: 'WIND, Q-CHECK', setupMinutes: 20, runMinutesPerUnit: 3, crewSize: 2,
    setupFamily: 'resin-a', familySetupMinutes: 5 });
  assert.deepEqual(operation, { sequence: 10, description: 'Wind', workCenterCode: 'WIND', activityCode: 'WIND',
    eligibleMachines: ['WINDER-1', 'WINDER-2'], requiredQualifications: ['WIND', 'Q-CHECK'], setupMinutes: 20, runMinutesPerUnit: 3, crewSize: 2,
    setupFamily: 'RESIN-A', familySetupMinutes: 5 });
  assert.throws(() => routingOperationBody({ ...operation, setupFamily: null }), /both a set-up family/);
  assert.throws(() => routingOperationBody({ ...operation, familySetupMinutes: 21 }), /full set-up time/);
});

test('engineering item routing editor submits and displays set-up family values', async t => {
  const item = { partNumber: 'PART-1', description: 'Test part', unitOfMeasure: 'EA', makeBuy: 'Make', leadTimeDays: 1, class: 'Make' };
  const revision = { partNumber: item.partNumber, revision: 'A', status: 'Draft', effectiveFrom: null, effectiveTo: null };
  const operations = [];
  const { browser, page, calls } = installCalls(t, (path, options, body) => {
    if (path === '/api/engineering/items') return response([item]);
    if (path.startsWith('/api/engineering/manufacturer-parts')) return response([]);
    if (path.startsWith('/api/engineering/alternates/where-used/')) return response({ usedIn: [], alternates: [], alternateFor: [] });
    if (path.endsWith('/revisions')) return response([revision]);
    if (path.endsWith('/revisions/A')) return response({ item, revision, bom: [], routing: operations });
    if (path === '/api/resources/') return response({ workCenters: [{ code: 'WIND', name: 'Winding', active: true }], activityTypes: [{ code: 'WIND', name: 'Wind', active: true }] });
    if (path === '/api/competency/qualification-types') return response([{ code: 'WIND', name: 'Winding' }]);
    if (options.method === 'POST' && path.endsWith('/routing')) {
      const { reason, ...operation } = body; operations.push(operation);
      return response({ succeeded: true, message: 'Operation recorded.' });
    }
    if (path.includes('/documents')) return response([]);
    if (options.method === 'POST') return response({ succeeded: true, message: 'Recorded.' });
    return response([]);
  });
  const { engineeringPage } = await import('../../src/Forge.Host/wwwroot/js/pages/engineering.js');
  await engineeringPage(page, { params: [item.partNumber], query: new URLSearchParams(), me: { personId: 'engineer', permissions: ['engineering.manage', 'engineering.read'] } });
  const routingPanel = page.querySelector('[aria-label="Routing operations"]');
  assert.match(routingPanel.innerHTML, /Routing operations/);
  fire(page.querySelector('#routing-add-operation'), 'click');
  await submit(browser, { sequence: 10, description: 'Wind', workCenterCode: 'WIND', activityCode: 'WIND', eligibleMachines: 'W1, W2', requiredQualifications: 'WIND',
    setupMinutes: 20, runMinutesPerUnit: 3, crewSize: 2, setupFamily: 'RESIN-A', familySetupMinutes: 5 });
  const posted = calls.find(call => call.options.method === 'POST' && call.path.endsWith('/routing'));
  assert.equal(posted.body.setupFamily, 'RESIN-A');
  assert.equal(posted.body.familySetupMinutes, 5);
  assert.deepEqual(posted.body.eligibleMachines, ['W1', 'W2']);
  assert.match(routingPanel.innerHTML, /RESIN-A · 5 min same family/);
});
