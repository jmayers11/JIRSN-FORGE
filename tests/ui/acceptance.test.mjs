import assert from 'node:assert/strict';
import test from 'node:test';
import { acceptancePage, parseCharacteristics, formatCharacteristics, parseDocuments, criteriaContent, readFindings, resultsSheet } from '../../src/Forge.Host/wwwroot/js/pages/acceptance.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

const me = { personId: '6cf938bf-6c38-481a-a5a4-a3aef8362da4', permissions: ['quality.read', 'quality.plan', 'quality.approve', 'quality.inspect', 'quality.product.release', 'inventory.transact'] };
const characteristics = [{ number: 1, name: 'Bore', kind: 'Variable', nominal: 10, lower: 9.98, upper: 10.02, unit: 'mm' }, { number: 2, name: 'Finish', kind: 'Attribute', nominal: null, lower: null, upper: null, unit: null }];
const effective = { code: 'ACC-BRKT', revision: 2, version: 5, state: 'Effective', facilityCode: 'PLANT1', programId: null, markings: 'None', createdBy: 'person:x', lastEditedBy: 'person:x', approvedBy: 'person:y', effectiveFrom: '2026-10-01T00:00:00Z',
  content: { partNumber: 'BRKT', revision: 'B', operationSequence: null, title: 'Final acceptance', coverage: { kind: 'ApprovedSampling', minimumSampleQuantity: 2, maximumRepresentedQuantity: 500, method: 'Z1.4 S-2', humanApprovalBasis: 'Customer plan' }, characteristics, documents: [{ documentNumber: 'WI-9', revision: 'C' }] } };

test('characteristic lines round-trip and attributes carry no numeric limits', () => {
  const parsed = parseCharacteristics('1 | Bore | Variable | 10 | 9.98 | 10.02 | mm\n2 | Finish | attribute | 5 | 1 | 2 | x');
  assert.deepEqual(parsed, characteristics);
  assert.deepEqual(parseCharacteristics(formatCharacteristics(parsed)), parsed);
  assert.throws(() => parseCharacteristics('Bore | Variable'), /Line 1/);
  assert.throws(() => parseCharacteristics(''), /at least one/);
  assert.deepEqual(parseDocuments('wi-9 c\nDWG-1 A'), [{ documentNumber: 'WI-9', revision: 'C' }, { documentNumber: 'DWG-1', revision: 'A' }]);
  assert.throws(() => parseDocuments('WI-9'), /DOCUMENT-NUMBER REVISION/);
  const c = criteriaContent({ partNumber: 'BRKT', revision: 'B', operationSequence: null, title: 'T', coverageKind: 'FullQuantity', minimumSample: 1, maximumRepresented: 50, method: 'All', approvalBasis: 'Spec', characteristics: '2 | Finish | Attribute', documents: 'WI-9 C' });
  assert.deepEqual(c.coverage, { kind: 'FullQuantity', minimumSampleQuantity: 1, maximumRepresentedQuantity: 50, method: 'All', humanApprovalBasis: 'Spec' });
});

test('the results sheet needs every characteristic for every inspected unit', t => {
  const browser = installBrowser(t), root = browser.document.createElement('div');
  root.innerHTML = resultsSheet(characteristics, 2, ['MIC-7']);
  assert.match(root.innerHTML, /data-cell="1-2-gauge"/); assert.match(root.innerHTML, /data-cell="2-1-accepted"/); assert.doesNotMatch(root.innerHTML, /data-cell="2-1-gauge"/);
  assert.throws(() => readFindings(root, characteristics, 2), /measurement and gauge for Bore, unit 1/);
  for (const s of [1, 2]) { root.querySelector(`[data-cell="1-${s}-measured"]`).value = '10.01'; root.querySelector(`[data-cell="1-${s}-gauge"]`).value = 'mic-7'; }
  root.querySelector('[data-cell="2-1-accepted"]').value = 'true';
  assert.throws(() => readFindings(root, characteristics, 2), /accept or reject for Finish, unit 2/);
  root.querySelector('[data-cell="2-2-accepted"]').value = 'false';
  assert.deepEqual(readFindings(root, characteristics, 2), [
    { characteristicNumber: 1, sampleNumber: 1, measured: 10.01, accepted: null, gaugeCode: 'MIC-7' }, { characteristicNumber: 1, sampleNumber: 2, measured: 10.01, accepted: null, gaugeCode: 'MIC-7' },
    { characteristicNumber: 2, sampleNumber: 1, measured: null, accepted: true, gaugeCode: null }, { characteristicNumber: 2, sampleNumber: 2, measured: null, accepted: false, gaugeCode: null }]);
});

async function open(t, route = () => null, query = '') {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const previous = { subject: session.subject, token: session.token }; session.subject = 'acceptance-test'; session.token = null; t.after(() => Object.assign(session, previous));
  const calls = [];
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body });
    const override = route(path, body); if (override) return Promise.resolve(override);
    if (path.endsWith('/acceptance/criteria')) return Promise.resolve(response([effective, { ...effective, revision: 3, version: 1, state: 'Submitted' }]));
    return Promise.resolve(response([]));
  };
  const controller = await acceptancePage(page, { me, query: new URLSearchParams(query) }); t.after(controller.dispose);
  return { ...browser, page, calls };
}

test('criteria list offers only the next lifecycle step for each revision', async t => {
  const { page } = await open(t);
  assert.match(page.innerHTML, /ACC-BRKT<\/b> rev 2/);
  assert.match(page.innerHTML, /data-crit="ACC-BRKT\/3" data-crit-act="approve"/);
  assert.doesNotMatch(page.innerHTML, /data-crit="ACC-BRKT\/2" data-crit-act/);
  assert.doesNotMatch(page.innerHTML, /data-crit-next="ACC-BRKT\/2"/, 'a new revision waits while revision 3 is open');
});

test('inspection is recorded against the exact subject and effective criteria version, then looked up', async t => {
  const inspection = { inspectionId: 41, version: 1, passed: true, quantity: 100, sampleQuantity: 2, criteriaCode: 'ACC-BRKT', criteriaRevision: 2, inspector: 'person:x', at: '2026-10-15T10:00:00Z', evidence: 'Sheet 7', results: [],
    subject: { key: { partNumber: 'BRKT', lotNumber: 'L9', locationCode: 'FG-1' }, revision: 'B' } };
  const { page, calls, document } = await open(t, (path, body) => body ? response({ succeeded: true, message: 'Inspection 41 passed.', entityId: '41' }) : path.endsWith('/inspections/41') ? response(inspection) : null);
  fire(page.querySelector('[data-tab="inspect"]'), 'click');
  for (const [id, value] of Object.entries({ part: 'brkt', lot: 'l9', loc: 'fg-1', criteria: 'ACC-BRKT/2', qty: '100', sample: '2' })) page.querySelector(`#i-${id}`).value = value;
  fire(page.querySelector('#build-sheet'), 'click');
  for (const s of [1, 2]) { page.querySelector(`[data-cell="1-${s}-measured"]`).value = '10'; page.querySelector(`[data-cell="1-${s}-gauge"]`).value = 'MIC-7'; page.querySelector(`[data-cell="2-${s}-accepted"]`).value = 'true'; }
  page.querySelector('#i-evidence').value = 'Sheet 7';
  fire(page.querySelector('#record-inspection'), 'click'); await flush();
  const modal = document.querySelector('.modal'); modal.querySelector('#reason').value = 'Final inspection';
  fire(modal.querySelector('[data-ok]'), 'click'); await flush(); await flush(); await flush();
  const sent = calls.find(c => c.body && c.path.endsWith('/acceptance/inspections')).body;
  assert.deepEqual(sent.subject, { partNumber: 'BRKT', lotNumber: 'L9', locationCode: 'FG-1' });
  assert.equal(sent.expectedCriteriaVersion, 5); assert.equal(sent.findings.length, 4); assert.equal(sent.sampleQuantity, 2); assert.equal(sent.quarantineLocation, null);
  assert.match(page.innerHTML, /Inspection<\/dt><dd>41/); assert.match(page.innerHTML, /release-this/);
  fire(page.querySelector('#release-this'), 'click');
  assert.equal(page.querySelector('#r-inspections').value, '41'); assert.equal(page.querySelector('#r-part').value, 'BRKT');
});
