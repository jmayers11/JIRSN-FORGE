import assert from 'node:assert/strict';
import test from 'node:test';
import { commercialQualityPage } from '../../src/Forge.Host/wwwroot/js/pages/commercial-quality.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, fire, flush } from './browser-harness.mjs';

const api = '/api/quality/commercial';
const record = { kind: 'ProviderEvaluation', code: 'SUP-QUAL', revision: 1, version: 2, state: 'Submitted', facilityCode: 'PLANT1',
  createdBy: 'person:author', lastEditedBy: 'person:author', content: { providerEvaluation: { supplierCode: 'ACTUAL-SUPPLIER' } }, history: [] };
async function harness(t, { rows = [record], permissions = ['quality.read'] } = {}) {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const old = { subject: session.subject, token: session.token }; session.subject = 'commercial-user'; session.token = null; t.after(() => Object.assign(session, old));
  const calls = [];
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body });
    if (body) return Promise.resolve(response({ message: 'The governed record was updated.' }));
    if (path === api + '/records?take=50') return Promise.resolve(response({ items: rows, nextCursor: null }));
    if (path === api + '/records/ProviderEvaluation/SUP-QUAL/revisions/1') return Promise.resolve(response(record));
    return Promise.resolve(response({}));
  };
  const controller = await commercialQualityPage(page, { me: { personId: 'reviewer', facilityCode: 'PLANT1', permissions }, query: new URLSearchParams() });
  t.after(controller.dispose);
  return { ...browser, page, calls };
}

test('commercial page lists only returned records and opens the exact authorized owner revision', async t => {
  const h = await harness(t); fire(h.page.querySelector('[data-open="ProviderEvaluation|SUP-QUAL|1"]'), 'click'); await flush();
  assert.match(h.page.innerHTML, /Customer & provider quality/);
  assert.match(h.page.innerHTML, /ACTUAL-SUPPLIER/);
  assert.equal(h.calls.some(call => call.body), false);
});

test('independent reviewer records the current version and human basis', async t => {
  const h = await harness(t, { permissions: ['quality.read', 'quality.approve'] });
  fire(h.page.querySelector('[data-open="ProviderEvaluation|SUP-QUAL|1"]'), 'click'); await flush();
  fire(h.page.querySelector('[data-action="Review"]'), 'click');
  const modal = h.document.querySelector('.modal'); modal.querySelector('[data-basis]').value = 'Actual supplier scope and evidence reviewed';
  modal.querySelector('[data-reason]').value = 'Independent provider review'; fire(modal.querySelector('[data-confirm]'), 'click'); await flush(); await flush();
  const call = h.calls.find(item => item.body);
  assert.equal(call.path, api + '/records/ProviderEvaluation/SUP-QUAL/revisions/1/review');
  assert.deepEqual(call.body, { expectedVersion: 2, basis: 'Actual supplier scope and evidence reviewed', reason: 'Independent provider review' });
});

test('record author can start a typed provider evaluation with its exact supplier source', async t => {
  const h = await harness(t, { rows: [], permissions: ['quality.read', 'quality.plan'] });
  fire(h.page.querySelector('[data-create]'), 'click');
  const modal = h.document.querySelector('.modal'); modal.querySelector('[data-kind]').value = 'ProviderEvaluation'; fire(modal.querySelector('[data-kind]'), 'change');
  modal.querySelector('[data-code]').value = 'SUP-NEW'; modal.querySelector('[data-facility]').value = 'PLANT1';
  modal.querySelector('[data-reason]').value = 'Record actual evaluated provider scope'; fire(modal.querySelector('[data-confirm]'), 'click'); await flush(); await flush();
  const call = h.calls.find(item => item.body);
  assert.equal(call.path, api + '/records'); assert.equal(call.body.kind, 'ProviderEvaluation');
  assert.equal(call.body.content.providerEvaluation.evidence[0].kind, 'Supplier');
  assert.equal(call.body.reason, 'Record actual evaluated provider scope');
});
