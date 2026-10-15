import assert from 'node:assert/strict';
import test from 'node:test';
import { qmsReviewPage } from '../../src/Forge.Host/wwwroot/js/pages/qms-review.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, fire, flush } from './browser-harness.mjs';

const runId = 'a03bd7b0-10d4-48b5-b93e-40b533e5de33';
const run = { id: runId, version: 2, state: 'Draft', programCode: 'REVIEW-PROGRAM', programRevision: 1, periodFrom: '2026-09-01T00:00:00Z', periodTo: '2026-10-01T00:00:00Z', meetingRecord: '', manifest: null, assessments: [], decisions: [], history: [] };
const definition = { code: 'REVIEW-PROGRAM', kind: 'ManagementReviewProgram', organizationCode: 'ACTUAL-ORG', facilityCode: 'PLANT1', markings: 'None', revisions: [{ revision: 1, version: 4, state: 'Effective', content: { program: { name: 'Actual approved management review' } } }] };
async function harness(t, { tab = 'programs', permissions = ['quality.qms.read', 'quality.qms.manage'], records = [definition] } = {}) {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const old = { subject: session.subject, token: session.token }; session.subject = 'qms-review-user'; session.token = null; t.after(() => Object.assign(session, old));
  const calls = [];
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body });
    if (body) return Promise.resolve(response({ message: 'The actual review decision was recorded.' }));
    if (path.endsWith('/definitions?take=30')) return Promise.resolve(response({ items: records, nextCursor: null }));
    if (path.endsWith('/runs?take=30')) return Promise.resolve(response({ items: records, nextCursor: null }));
    if (path.endsWith('/mappings/assessments?take=30') || path.endsWith('/editions/assessments?take=30')) return Promise.resolve(response({ items: records, nextCursor: null }));
    if (path === '/api/quality/qms/review/definitions/REVIEW-PROGRAM') return Promise.resolve(response(definition));
    if (path.startsWith('/api/quality/qms/review/editions/assessments/')) return Promise.resolve(response(records[0]));
    if (path === '/api/quality/qms/review/runs/' + runId) return Promise.resolve(response(run));
    if (path === '/api/quality/qms/review/runs/' + runId + '/actions') return Promise.resolve(response([]));
    return Promise.resolve(response({}));
  };
  const controller = await qmsReviewPage(page, { me: { personId: 'actual-person', permissions }, query: new URLSearchParams({ tab }) });
  t.after(controller.dispose);
  return { ...browser, page, calls, open: async id => { fire(page.querySelector('[data-open="' + (id ?? records[0].code ?? records[0].id ?? runId) + '"]'), 'click'); await flush(); } };
}

test('review programs show authorized records and the exact effective definition', async t => {
  const h = await harness(t); await h.open('REVIEW-PROGRAM');
  assert.match(h.page.innerHTML, /QMS reviews and evidence/);
  assert.match(h.page.innerHTML, /Actual approved management review/);
  assert.equal(h.calls.some(call => call.body), false);
});

test('draft review action captures current version and audit reason', async t => {
  const h = await harness(t, { tab: 'runs', records: [run] }); await h.open();
  fire(h.page.querySelector('[data-run-action="Freeze inputs"]'), 'click');
  const modal = h.document.querySelector('.modal'); modal.querySelector('[data-reason]').value = 'Capture the actual approved cohort';
  fire(modal.querySelector('[data-confirm]'), 'click'); await flush(); await flush();
  const call = h.calls.find(item => item.body); assert.equal(call.path, '/api/quality/qms/review/runs/' + runId + '/freeze');
  assert.equal(call.body.data.expectedVersion, 2); assert.equal(call.body.reason, 'Capture the actual approved cohort');
});

test('user without QMS read authority sees no records and no write controls', async t => {
  const h = await harness(t, { permissions: [], records: [] });
  assert.match(h.page.innerHTML, /No authorized records/); assert.equal(h.page.querySelector('[data-create-run]'), null);
  assert.equal(h.calls.some(call => call.body), false);
});

test('edition assessment form records exact criteria, unresolved relationship and pending licensed verification', async t => {
  const h = await harness(t, { tab: 'editions', records: [] });
  fire(h.page.querySelector('[data-create-edition]'), 'click');
  const modal = h.document.querySelector('.modal');
  const set = (name, text) => { modal.querySelector('[data-field="' + name + '"]').value = text; };
  set('facility', 'PLANT1'); set('standard', 'ISO 9001'); set('fromEdition', '2015'); set('toEdition', '2026');
  set('fromCriteria', '8.5.1\n8.5.2'); set('toCriteria', '8.5.1'); set('relationship', 'Unresolved');
  set('basis', 'Human basis recorded'); set('changes', 'Authorized source comparison is pending'); set('applicability', 'No conclusion until assessed');
  set('requiredActions', 'Review the licensed source and applicability'); set('auditProgram', 'INTERNAL-AUDIT'); set('auditProgramRevision', '1');
  set('impartiality', 'No process ownership or evidence contribution');
  modal.querySelector('[data-reason]').value = 'Record the qualified proposal';
  fire(modal.querySelector('[data-confirm]'), 'click'); await flush(); await flush();
  const call = h.calls.find(item => item.body);
  assert.equal(call.path, '/api/quality/qms/review/editions/assessments');
  assert.equal(call.body.data.relationship, 'Unresolved');
  assert.deepEqual(call.body.data.fromCriteria.map(item => item.clause), ['8.5.1', '8.5.2']);
  assert.equal(call.body.data.toCriteria[0].verification, 'PendingAuthorizedVerification');
  assert.equal(call.body.data.auditorValidation.personId, 'actual-person');
  assert.equal(call.body.reason, 'Record the qualified proposal');
});

test('edition transition sends the current human auditor and exact assessment version', async t => {
  const assessment = { id: runId, version: 2, state: 'Submitted', auditorValidation: { auditProgram: { code: 'INTERNAL-AUDIT', revision: 1 }, impartialityBasis: 'Current independent reviewer' } };
  const h = await harness(t, { tab: 'editions', permissions: ['quality.qms.read', 'quality.qms.review'], records: [assessment] }); await h.open(runId);
  fire(h.page.querySelector('[data-edition-action="Review"]'), 'click');
  const modal = h.document.querySelector('.modal'); modal.querySelector('[data-field="note"]').value = 'Independent assessment';
  modal.querySelector('[data-field="impartiality"]').value = 'No ownership or contribution'; modal.querySelector('[data-reason]').value = 'Record independent review';
  fire(modal.querySelector('[data-confirm]'), 'click'); await flush(); await flush();
  const call = h.calls.find(item => item.body);
  assert.equal(call.path, '/api/quality/qms/review/editions/assessments/' + runId + '/review');
  assert.equal(call.body.data.expectedVersion, 2); assert.equal(call.body.data.auditorValidation.personId, 'actual-person');
  assert.equal(call.body.data.auditorValidation.auditProgram.code, 'INTERNAL-AUDIT');
});
