import assert from 'node:assert/strict';
import test from 'node:test';
import { commercialQualityPage, commercialFields, commercialContent } from '../../src/Forge.Host/wwwroot/js/pages/commercial-quality.js';
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
  const modal = h.document.querySelector('.modal'); modal.querySelector('#f-kind').value = 'ProviderEvaluation'; fire(modal.querySelector('#f-kind'), 'change');
  modal.querySelector('#f-code').value = 'SUP-NEW'; modal.querySelector('#f-facilityCode').value = 'PLANT1';
  modal.querySelector('#f-supplierCode').value = 'ACTUAL-SUPPLIER';
  modal.querySelector('#f-minimumWeightedPassFraction').value = '1';
  modal.querySelector('#f-validFrom').value = '2026-10-01T00:00'; modal.querySelector('#f-validTo').value = '2027-10-01T00:00'; modal.querySelector('#f-reevaluateBy').value = '2027-09-01T00:00';
  modal.querySelector('#f-evaluationBasis').value = 'Actual provider evidence was assessed against the approved criteria.';
  const repeaters = [...modal.querySelectorAll('[data-repeater]')], criteria = repeaters.find(item => item.dataset.repeater === 'criteria'), evidence = repeaters.find(item => item.dataset.repeater === 'evidence');
  const criterion = criteria.querySelector('[data-repeater-row]'); criterion.querySelector('[data-repeater-column="code"]').value = 'QUALITY';
  criterion.querySelector('[data-repeater-column="description"]').value = 'Actual quality performance'; criterion.querySelector('[data-repeater-column="kind"]').value = 'Attribute';
  criterion.querySelector('[data-repeater-column="weight"]').value = '1'; criterion.querySelector('[data-repeater-column="evidenceBasis"]').value = 'Reviewed current inspection evidence.';
  fire(evidence.querySelector('[data-repeater-add]'), 'click');
  const source = evidence.querySelector('[data-repeater-row]'); source.querySelector('[data-repeater-column="kind"]').value = 'Supplier'; source.querySelector('[data-repeater-column="key"]').value = 'ACTUAL-SUPPLIER';
  modal.querySelector('[data-reason]').value = 'Record actual evaluated provider scope'; fire(modal.querySelector('[data-confirm]'), 'click'); await flush(); await flush();
  const call = h.calls.find(item => item.body);
  assert.equal(call.path, api + '/records'); assert.equal(call.body.kind, 'ProviderEvaluation');
  assert.deepEqual(call.body.content.providerEvaluation.evidence, [{ kind: 'Supplier', key: 'ACTUAL-SUPPLIER', lineNumber: null, revision: null }]);
  assert.equal(call.body.content.providerEvaluation.criteria[0].code, 'QUALITY');
  assert.equal(call.body.reason, 'Record actual evaluated provider scope');
});

test('all commercial record types have typed fields and preserve their owner content shape', () => {
  const kinds = ['CustomerRequirements', 'DesignApplicability', 'ProviderEvaluation', 'PropertyAgreement', 'PropertyIncident', 'Complaint', 'CustomerFeedback', 'PostDelivery', 'Communication'];
  const expectedWrapper = ['customerRequirements', 'designApplicability', 'providerEvaluation', 'propertyAgreement', 'propertyIncident', 'complaint', 'customerFeedback', 'postDelivery', 'communication'];
  const date = '2026-10-01T00:00';
  const values = {
    CustomerRequirements: { requirements: [{ code: 'R1', kind: 'Quality', requirement: 'Actual requirement', acceptanceBasis: 'Actual basis' }], applicability: [], enforceAtOrderRelease: true, enforceAtShipment: false, requireIndependentComplaintClosure: true, reviewBasis: 'Reviewed exact contract requirements.', requireCustomerConcessionForNonconformingRelease: false, evidence: [] },
    DesignApplicability: { partNumber: 'PART-A', revision: 'B', applicable: true, determinationBasis: 'Reviewed current design applicability.', controls: [], evidence: [] },
    ProviderEvaluation: { supplierCode: 'SUP-A', partNumber: null, partRevision: null, processCode: null, workCenterCode: null, enforceAtPurchaseApproval: true, enforceAtExternalDispatch: false, purchasingRequirements: 'Actual requirements', criteria: [], minimumWeightedPassFraction: 1, validFrom: date, validTo: '2027-10-01T00:00', reevaluateBy: '2027-09-01T00:00', evaluationBasis: 'Actual evaluation basis', evidence: [] },
    PropertyAgreement: { subjectKind: 'Material', subjectKey: 'LOT-1', titleKind: 'CustomerOwned', legalOwnerKind: 'Contract', legalOwnerKey: '12', agreementBasis: 'Actual agreement basis', validFrom: date, validTo: '2027-10-01T00:00', permittedActivities: [], permittedWorkCenters: [], permittedOrders: [], allowConsumptiveUse: false, requireOwnerReturnAcceptance: true, returnTerms: 'Actual return terms', preservation: [], evidence: [] },
    PropertyIncident: { agreementCode: 'PROP-1', agreementRevision: 2, incidentKind: 'Damage', observedAt: date, description: 'Actual damage', immediateAction: 'Contained item', evidence: [] },
    Complaint: { shipmentId: 12, shipmentLine: null, reportedProblem: 'Actual reported problem', reportedAt: date, receivedChannel: 'Portal', recordedContact: 'Customer contact', investigationOwner: 'owner-1', evidence: [] },
    CustomerFeedback: { customerSourceKind: 'Contract', customerSourceKey: '12', feedbackKind: 'Survey', receivedAt: date, method: 'Recorded survey', measures: [], recordedFeedback: 'Actual feedback', evidence: [] },
    PostDelivery: { shipmentId: 12, postDeliveryKind: 'Warranty', caseBasis: 'Actual warranty report', relatedEvidence: [] },
    Communication: { audience: 'Customer', partySourceKind: 'Contract', partySourceKey: '12', relatedCaseKind: 'CommercialRecord', relatedCaseKey: 'Complaint|CASE-1', recordedRecipient: 'Actual contact', subject: 'Proposed subject', body: 'Proposed message', proposedChannel: 'Portal', evidence: [], hasConcession: false },
  };
  kinds.forEach((kind, index) => {
    const fields = commercialFields(kind, [{ personId: 'owner-1', displayName: 'Quality owner' }]);
    assert.ok(fields.length > 0); assert.equal(new Set(fields.map(field => field.name)).size, fields.length, `${kind} field names must be unique`);
    assert.equal(Object.keys(commercialContent(kind, values[kind]))[0], expectedWrapper[index]);
  });
});
