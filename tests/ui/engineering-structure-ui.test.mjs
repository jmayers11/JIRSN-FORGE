import assert from 'node:assert/strict';
import test from 'node:test';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

function installCalls(t, handler) {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const oldSession = { subject: session.subject, token: session.token };
  session.subject = 'engineering-structure-ui-test'; session.token = null;
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
  modal.querySelector('#reason').value = 'Engineering drawing update';
  fire(modal.querySelector('[data-ok]'), 'click');
  await flush(); await flush();
}

const item = { partNumber: 'ASSY-1', description: 'Assembly one', unitOfMeasure: 'EA', makeBuy: 'Make', leadTimeDays: 2, class: 'Make' };
const component = { partNumber: 'COMP-1', description: 'Component one', unitOfMeasure: 'EA', makeBuy: 'Buy', leadTimeDays: 4, class: 'Unclassified' };
const draft = { partNumber: item.partNumber, revision: 'B', status: 'Draft', effectiveFrom: null, effectiveTo: null };
const detail = {
  item, revision: draft,
  bom: [{ lineNumber: 10, componentPartNumber: component.partNumber, quantityPer: 2, scrapPercent: 1, policy: 'Suggest' }],
  routing: [{ sequence: 10, description: 'Assemble', workCenterCode: 'ASSY-WC', activityCode: 'ASSEMBLE', eligibleMachines: [], requiredQualifications: [], setupMinutes: 12, runMinutesPerUnit: 2, crewSize: 1 }],
};

test('parent-part workspace brings BOM and operations together and submits audited line updates', async t => {
  const { browser, page, calls } = installCalls(t, (path, options) => {
    if (path === '/api/engineering/items') return response([item, component]);
    if (path.endsWith('/revisions')) return response([draft]);
    if (path.endsWith('/revisions/B')) return response(detail);
    if (path === '/api/resources/') return response({ workCenters: [{ code: 'ASSY-WC', name: 'Assembly', active: true }], activityTypes: [{ code: 'ASSEMBLE', name: 'Assemble', active: true }] });
    if (path === '/api/competency/qualification-types') return response([]);
    if (options.method === 'POST') return response({ succeeded: true, message: 'Updated.' });
    return response([]);
  });

  const { engineeringPage } = await import('../../src/Forge.Host/wwwroot/js/pages/engineering.js');
  await engineeringPage(page, { params: ['structure', item.partNumber], query: new URLSearchParams(), me: { personId: 'engineer', permissions: ['engineering.manage', 'engineering.read'] } });
  assert.match(page.innerHTML, /Parent part definition/);
  assert.match(page.innerHTML, /Engineering revision/);
  assert.match(page.innerHTML, /COMP-1/);

  fire(page.querySelector('[data-bom-edit="10"]'), 'click');
  await submit(browser, { componentPartNumber: 'COMP-1', quantityPer: 3, scrapPercent: 2.5 });
  const bomUpdate = calls.find(call => call.path.endsWith('/bom/10/update'));
  assert.deepEqual(bomUpdate.body, { componentPartNumber: 'COMP-1', quantityPer: 3, scrapPercent: 2.5, reason: 'Engineering drawing update' });

  fire(page.querySelector('[data-structure-tab="operations"]'), 'click');
  await flush();
  assert.match(page.innerHTML, /Assemble/);
  fire(page.querySelector('[data-operation-edit="10"]'), 'click');
  await submit(browser, { sequence: 10, description: 'Assemble and inspect', workCenterCode: 'ASSY-WC', activityCode: 'ASSEMBLE', eligibleMachines: 'ASSEMBLER-1', requiredQualifications: 'FIRST-AID', setupMinutes: 15, runMinutesPerUnit: 2.5, crewSize: 2, setupFamily: 'FRAME', familySetupMinutes: 5 });
  const routingUpdate = calls.find(call => call.path.endsWith('/routing/10/update'));
  assert.deepEqual(routingUpdate.body, {
    sequence: 10, description: 'Assemble and inspect', workCenterCode: 'ASSY-WC', activityCode: 'ASSEMBLE', eligibleMachines: ['ASSEMBLER-1'],
    requiredQualifications: ['FIRST-AID'], setupMinutes: 15, runMinutesPerUnit: 2.5, crewSize: 2, setupFamily: 'FRAME', familySetupMinutes: 5,
    reason: 'Engineering drawing update',
  });
});

test('released parent revisions remain read-only in the structure workspace', async t => {
  const released = { ...draft, status: 'Released', effectiveFrom: '2026-10-01T00:00:00Z' };
  const { page } = installCalls(t, path => {
    if (path === '/api/engineering/items') return response([item, component]);
    if (path.endsWith('/revisions')) return response([released]);
    if (path.endsWith('/revisions/B')) return response({ ...detail, revision: released });
    return response([]);
  });
  const { engineeringPage } = await import('../../src/Forge.Host/wwwroot/js/pages/engineering.js');
  await engineeringPage(page, { params: ['structure', item.partNumber], query: new URLSearchParams(), me: { personId: 'reader', permissions: ['engineering.read'] } });
  assert.match(page.innerHTML, /Released · effective/);
  assert.match(page.innerHTML, /definition is read-only/);
  assert.equal(page.querySelector('#parent-add-component'), null);
  assert.equal(page.querySelector('[data-bom-edit="10"]'), null);
  assert.equal(page.querySelector('[data-operation-edit="10"]'), null);
});

test('parent-part index searches part number and description', async t => {
  const { browser, page } = installCalls(t, path => path === '/api/engineering/items' ? response([item, component]) : response([]));
  const { engineeringPage } = await import('../../src/Forge.Host/wwwroot/js/pages/engineering.js');
  await engineeringPage(page, { params: ['structure'], query: new URLSearchParams(), me: { personId: 'reader', permissions: ['engineering.read'] } });
  assert.match(page.innerHTML, /Parent parts/);
  const rows = page.querySelector('#parent-parts-rows');
  assert.match(rows.innerHTML, /ASSY-1/);
  page.querySelector('#parent-search').value = 'component one';
  fire(page.querySelector('#parent-search'), 'input');
  assert.match(rows.innerHTML, /COMP-1/);
  assert.doesNotMatch(rows.innerHTML, /ASSY-1/);
});
