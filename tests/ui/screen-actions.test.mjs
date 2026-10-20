import assert from 'node:assert/strict';
import test from 'node:test';
import { faiParts, faiResults, receivingTab, bindReceivingTab } from '../../src/Forge.Host/wwwroot/js/receiving.js';
import { containerRows } from '../../src/Forge.Host/wwwroot/js/pages/shipping.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

const receipt = (faiStatus) => ({ receipt: { transactionId: 7, at: '2026-10-15T10:00:00Z', partNumber: 'BAR-1', lotNumber: null, poNumber: 88, quantity: 10 }, supplierCode: 'ACME',
  accepted: 0, rejected: 0, remaining: 10, faiStatus, faiSubject: 'BAR-1:B@ACME', inspections: [] });
const rec = (status, extra = {}) => ({ receipts: { data: [receipt(status)] }, rejections: { data: [] }, rtvs: { data: [{ rtvId: 3, partNumber: 'BAR-1', supplierCode: 'ACME', quantity: 2, status: 'Shipped', forRework: false }] },
  returns: { data: [{ rmaId: 5, customer: 'Globex', shipmentId: 12, partNumber: 'BRKT', lotNumber: null, quantity: 4, reason: 'Damaged', disposition: null }] }, ...extra });

test('FAI subjects and results are read exactly', () => {
  assert.deepEqual(faiParts('BAR-1:B@ACME'), { partNumber: 'BAR-1', revision: 'B', supplierCode: 'ACME' });
  assert.equal(faiParts('BAR-1'), null);
  assert.deepEqual(faiResults('Length | 100 ±0.1 | 100.02 | pass\nBurrs | none | | fail'), [
    { characteristic: 'Length', requirement: '100 ±0.1', measured: '100.02', pass: true }, { characteristic: 'Burrs', requirement: 'none', measured: null, pass: false }]);
  assert.throws(() => faiResults('Length | 100 | 100'), /Result line 1/); assert.throws(() => faiResults(''), /each characteristic/);
});

test('receiving offers FAI decisions, FAI records, RTV credit and return preparation by permission', () => {
  const all = { permissions: ['quality.plan', 'quality.inspect', 'quality.disposition', 'inventory.transact', 'purchasing.approve'] };
  assert.match(receivingTab(rec('PendingDetermination'), all), /data-fai-decide="BAR-1:B@ACME"/);
  assert.doesNotMatch(receivingTab(rec('PendingDetermination'), all), /data-fai-record/);
  assert.match(receivingTab(rec('Required'), all), /data-fai-record="BAR-1:B@ACME"/);
  assert.doesNotMatch(receivingTab(rec('Complete'), all), /data-fai-/);
  assert.match(receivingTab(rec('Complete'), all), /data-rtv-credit="3"/); assert.match(receivingTab(rec('Complete'), all), /data-rma-prepare="5"/);
  const handler = { permissions: ['inventory.transact'] };
  assert.doesNotMatch(receivingTab(rec('PendingDetermination'), handler), /data-fai-decide|data-rtv-credit/);
});

test('recording an FAI sends the exact subject, results and signed report', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const previous = { subject: session.subject, token: session.token }; session.subject = 'receiving-test'; session.token = null; t.after(() => Object.assign(session, previous));
  const calls = []; globalThis.fetch = (path, options = {}) => { calls.push({ path, body: options.body ? JSON.parse(options.body) : null }); return Promise.resolve(response({ succeeded: true, message: 'FAI recorded.' })); };
  const me = { permissions: ['quality.inspect'] };
  page.innerHTML = receivingTab(rec('Required'), me); bindReceivingTab(page, [['RECV', 'Receiving']], async () => {}, me);
  fire(page.querySelector('[data-fai-record]'), 'click'); await flush();
  const modal = browser.document.querySelector('.modal');
  const resultRow = modal.querySelector('[data-repeater-row]');
  resultRow.querySelector('[data-repeater-column="characteristic"]').value = 'Length';
  resultRow.querySelector('[data-repeater-column="requirement"]').value = '100';
  resultRow.querySelector('[data-repeater-column="measured"]').value = '100.02';
  resultRow.querySelector('[data-repeater-column="result"]').value = 'pass';
  modal.querySelector('#f-documentNumber').value = 'fai-bar1'; modal.querySelector('#f-documentRevision').value = 'a';
  modal.querySelector('#reason').value = 'First shipment from ACME'; fire(modal.querySelector('[data-ok]'), 'click'); await flush(); await flush();
  assert.deepEqual(calls.find(c => c.path === '/api/quality/fai').body, { partNumber: 'BAR-1', revision: 'B', supplierCode: 'ACME', passed: true,
    results: [{ characteristic: 'Length', requirement: '100', measured: '100.02', pass: true }], documentNumber: 'FAI-BAR1', documentRevision: 'A', reason: 'First shipment from ACME' });
});

test('customer return restock lists only releases tied to its exact cohort and fills version and quantity', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const previous = { subject: session.subject, token: session.token }; session.subject = 'return-release-test'; session.token = null; t.after(() => Object.assign(session, previous));
  const calls = [], returned = { rmaId: 5, customer: 'Globex', shipmentId: 12, partNumber: 'BRKT', lotNumber: 'LOT-9', locationCode: 'RECV', quantity: 4, reason: 'Damaged', disposition: null };
  const release = (decisionId, cohortId, quantity) => ({ decisionId, version: 3, quantity, subject: { partNumber: 'BRKT', lotNumber: 'LOT-9', locationCode: 'RECV', returnCohortId: cohortId } });
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body });
    if (path === '/api/quality/acceptance/product-releases?take=100') return Promise.resolve(response([release(10, 9, 3), release(11, 10, 2), release(12, 11, 4)]));
    if (path === '/api/quality/acceptance/return-cohorts/9') return Promise.resolve(response({ rmaId: 5, subject: { partNumber: 'BRKT', lotNumber: 'LOT-9', locationCode: 'RECV', returnCohortId: 9 } }));
    if (path === '/api/quality/acceptance/return-cohorts/10') return Promise.resolve(response({ rmaId: 5, subject: { partNumber: 'BRKT', lotNumber: 'LOT-9', locationCode: 'RECV', returnCohortId: 10 } }));
    if (path === '/api/quality/acceptance/return-cohorts/11') return Promise.resolve(response({ rmaId: 6, subject: { partNumber: 'BRKT', lotNumber: 'LOT-9', locationCode: 'RECV', returnCohortId: 11 } }));
    return Promise.resolve(response({ message: 'Disposition recorded.' }));
  };
  const me = { permissions: ['quality.disposition'] }, data = rec('Complete', { returns: { data: [returned] } });
  page.innerHTML = receivingTab(data, me); bindReceivingTab(page, [], async () => {}, me, data);
  fire(page.querySelector('[data-rma-dispose="5"]'), 'click');
  for (let i = 0; i < 6 && !browser.document.querySelector('.modal'); i++) await flush();
  const modal = browser.document.querySelector('.modal');
  assert.ok(modal, `modal did not open; requests: ${calls.map(call => call.path).join(', ')}; notice: ${browser.document.querySelector('.toast')?.textContent}`);
  const releaseField = modal.querySelector('#f-release');
  assert.deepEqual(releaseField.querySelectorAll('option').map(option => option.value), ['', '10|3|3', '11|3|2']);
  releaseField.value = '10|3|3'; fire(releaseField, 'change');
  assert.equal(modal.querySelector('#f-expectedDecisionVersion').value, '3'); assert.equal(modal.querySelector('#f-quantity').value, '3');
  modal.querySelector('#f-disposition').value = 'Restock'; modal.querySelector('#reason').value = 'Restock the exact inspected return cohort.';
  fire(modal.querySelector('[data-ok]'), 'click'); await flush(); await flush();
  const command = calls.find(call => call.path === '/api/inventory/customer-returns/5/disposition');
  assert.deepEqual(command.body, { disposition: 'Restock', decisionId: 10, expectedDecisionVersion: 3, quantity: 3, reason: 'Restock the exact inspected return cohort.' });
});

test('containers can be packed only while open and sealed only once they hold something', () => {
  const open = { code: 'PLT-1', kind: 'Pallet', status: 'Open', contents: [] }, packed = { ...open, contents: [{ lineNumber: 1, partNumber: 'BRKT', quantity: 4, locationCode: 'FG-1', lotNumber: null }] };
  assert.match(containerRows([open], true), /data-container-add="PLT-1"/); assert.doesNotMatch(containerRows([open], true), /data-container-seal/);
  assert.match(containerRows([packed], true), /data-container-seal="PLT-1"/); assert.match(containerRows([packed], true), /4 × BRKT/);
  assert.doesNotMatch(containerRows([{ ...packed, status: 'Sealed' }], true), /data-container-/); assert.doesNotMatch(containerRows([packed], false), /data-container-/);
});
