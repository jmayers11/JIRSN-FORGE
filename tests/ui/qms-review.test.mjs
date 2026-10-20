import assert from 'node:assert/strict';
import test from 'node:test';
import { qmsReviewPage, parseMeetingRows } from '../../src/Forge.Host/wwwroot/js/pages/qms-review.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, fire, flush } from './browser-harness.mjs';

const runId = 'a03bd7b0-10d4-48b5-b93e-40b533e5de33';
const run = { id: runId, version: 2, state: 'Draft', programCode: 'REVIEW-PROGRAM', programRevision: 1, periodFrom: '2026-09-01T00:00:00Z', periodTo: '2026-10-01T00:00:00Z', meetingRecord: '', manifest: null, assessments: [], decisions: [], history: [] };
const definition = { code: 'REVIEW-PROGRAM', kind: 'ManagementReviewProgram', organizationCode: 'ACTUAL-ORG', facilityCode: 'PLANT1', markings: 'None', revisions: [{ revision: 1, version: 4, state: 'Effective', content: { program: { name: 'Actual approved management review' } } }] };
async function harness(t, { tab = 'programs', permissions = ['quality.qms.read', 'quality.qms.manage'], records = [definition], route = () => null } = {}) {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const old = { subject: session.subject, token: session.token }; session.subject = 'qms-review-user'; session.token = null; t.after(() => Object.assign(session, old));
  const calls = [];
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body });
    const override = route(path, body); if (override) return Promise.resolve(override);
    if (body) return Promise.resolve(response({ message: 'The actual review decision was recorded.' }));
    if (path.endsWith('/definitions?take=30')) return Promise.resolve(response({ items: records, nextCursor: null }));
    if (path.endsWith('/runs?take=30')) return Promise.resolve(response({ items: records, nextCursor: null }));
    if (path.endsWith('/mappings/assessments?take=30') || path.endsWith('/editions/assessments?take=30')) return Promise.resolve(response({ items: records, nextCursor: null }));
    if (path === '/api/quality/qms/review/definitions/REVIEW-PROGRAM') return Promise.resolve(response(definition));
    if (path.startsWith('/api/quality/qms/review/editions/assessments/')) return Promise.resolve(response(records[0]));
    if (path === '/api/kernel/directory') return Promise.resolve(response([{ personId: 'actual-person', displayName: 'Actual Reviewer' }, { personId: 'owner-person', displayName: 'Action Owner' }]));
    if (path === '/api/quality/qms/review/runs/' + runId) return Promise.resolve(response(records[0]?.id === runId ? records[0] : run));
    if (path === '/api/quality/qms/review/runs/' + runId + '/actions') return Promise.resolve(response([]));
    return Promise.resolve(response({}));
  };
  const controller = await qmsReviewPage(page, { me: { personId: 'actual-person', permissions }, query: new URLSearchParams({ tab }) });
  t.after(controller.dispose);
  return { ...browser, page, calls, open: async id => { fire(page.querySelector('[data-open="' + (id ?? records[0].code ?? records[0].id ?? runId) + '"]'), 'click'); await flush(); } };
}

test('meeting row parser preserves the existing pipe contract and validation', () => {
  assert.deepEqual(parseMeetingRows('AGENDA-1 | Adequate | Source reviewed | None | Continue', 5, 'assessment'),
    [['AGENDA-1', 'Adequate', 'Source reviewed', 'None', 'Continue']]);
  assert.throws(() => parseMeetingRows('AGENDA-1 | Adequate', 5, 'assessment'), /exactly 5 fields/);
});

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

test('management meeting uses structured agenda, participant and decision rows with entity labels', async t => {
  const meetingRun = { ...run, state: 'InputsFrozen', manifest: { inputs: [{ agendaCode: 'QUALITY-PERFORMANCE', title: 'Quality performance' }] } };
  const h = await harness(t, { tab: 'runs', records: [meetingRun] }); await h.open();
  fire(h.page.querySelector('[data-run-action="Record meeting"]'), 'click'); await flush();
  const modal = h.document.querySelector('.modal');
  assert.equal(modal.querySelectorAll('[data-repeater-row]').length, 2);
  modal.querySelector('[data-field="meeting"]').value = 'Actual quarterly quality review meeting';
  modal.querySelector('[data-field="limitations"]').value = 'One source was unavailable';
  const repeaters = [...modal.querySelectorAll('[data-repeater]')];
  const participantRows = repeaters.find(element => element.dataset.repeater === 'participants');
  const assessmentRows = repeaters.find(element => element.dataset.repeater === 'assessments');
  const decisionRows = repeaters.find(element => element.dataset.repeater === 'decisions');
  participantRows.querySelector('[data-repeater-column="personId"]').value = 'actual-person';
  const assessment = assessmentRows.querySelector('[data-repeater-row]');
  assessment.querySelector('[data-repeater-column="assessment"]').value = 'Reviewed measured performance';
  assessment.querySelector('[data-repeater-column="limits"]').value = 'Supplier data lagged one week';
  assessment.querySelector('[data-repeater-column="decision"]').value = 'Continue monitoring';
  const add = decisionRows; fire(add.querySelector('[data-repeater-add]'), 'click');
  const decision = add.querySelector('[data-repeater-row]');
  decision.querySelector('[data-repeater-column="code"]').value = 'Q-1';
  decision.querySelector('[data-repeater-column="decision"]').value = 'Review supplier trend';
  decision.querySelector('[data-repeater-column="basis"]').value = 'Current trend warrants review';
  decision.querySelector('[data-repeater-column="intendedResult"]').value = 'A documented supplier review';
  decision.querySelector('[data-repeater-column="resources"]').value = 'Quality engineer';
  decision.querySelector('[data-repeater-column="owner"]').value = 'owner-person';
  decision.querySelector('[data-repeater-column="due"]').value = '2026-11-01T09:00';
  decision.querySelector('[data-repeater-column="completionCriteria"]').value = 'Review is approved and filed';
  modal.querySelector('[data-reason]').value = 'Record the actual meeting decisions';
  fire(modal.querySelector('[data-confirm]'), 'click'); await flush(); await flush();
  const call = h.calls.find(item => item.body);
  assert.equal(call.path, '/api/quality/qms/review/runs/' + runId + '/meeting');
  assert.deepEqual(call.body.data.participants, ['actual-person']);
  assert.deepEqual(call.body.data.assessments, [{ agendaCode: 'QUALITY-PERFORMANCE', adequacy: 'Adequate', assessment: 'Reviewed measured performance', limits: 'Supplier data lagged one week', decision: 'Continue monitoring', additionalEvidence: [] }]);
  assert.equal(call.body.data.decisions[0].ownerPersonId, 'owner-person');
  // The due time is entered in the browser's local time; compare with the same conversion so any time zone passes.
  assert.equal(call.body.data.decisions[0].dueAt, new Date('2026-11-01T09:00').toISOString());
  assert.equal(call.body.reason, 'Record the actual meeting decisions');
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

test('review dialogs trap focus and preserve entries after an uncertain 5xx without allowing retry', async t => {
  const submittedRun = { ...run, state: 'Submitted' };
  const h = await harness(t, { tab: 'runs', permissions: ['quality.qms.read', 'quality.qms.review'], records: [submittedRun], route: (path, body) => body ? response({ detail: 'Review service unavailable.' }, 503) : null });
  await h.open(); fire(h.page.querySelector('[data-run-action="Review"]'), 'click');
  const modal = h.document.querySelector('.modal');
  modal.querySelector('[data-field="note"]').value = 'Capture the exact approved period and inputs';
  modal.querySelector('[data-reason]').value = 'Freeze the current review cohort';
  modal.querySelector('[data-confirm]').focus(); h.document.dispatchEvent({ type: 'keydown', key: 'Tab' });
  assert.equal(h.document.activeElement, modal.querySelector('[data-field="note"]'));
  fire(modal.querySelector('[data-confirm]'), 'click'); await flush();
  assert.equal(modal.isConnected, true);
  assert.equal(modal.querySelector('[data-field="note"]').value, 'Capture the exact approved period and inputs');
  assert.equal(modal.querySelector('[data-reason]').value, 'Freeze the current review cohort');
  assert.equal(modal.querySelector('[data-confirm]').disabled, true);
  assert.match(modal.querySelector('[data-error]').textContent, /entries are preserved/);
  fire(modal.querySelector('[data-cancel]'), 'click');
  assert.equal(h.page.querySelector('[data-run-action="Review"]'), null);
  assert.match(h.page.textContent, /Refresh the current review records/);
  assert.equal(h.calls.filter(call => call.body).length, 1);
});
