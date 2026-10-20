import assert from 'node:assert/strict';
import test from 'node:test';
import { qmsAuditPage, auditProgramActions, auditRunActions, capaActions, auditFormDefinition, auditFormData, evidenceLines, parsedRefs } from '../../src/Forge.Host/wwwroot/js/pages/qms-audit.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, fire, flush, deferred } from './browser-harness.mjs';

const author = '2ee3b2cd-0231-49e9-af51-12f1011d0c01', observer = 'bb2c099c-62d1-4b13-ba80-d3f8fd7a6e19', reviewer = 'b4b86282-8f80-4a0f-8fa2-dbd6b08e3d5c';
const permission = ['quality.read', 'quality.audit.manage', 'quality.audit.conduct', 'quality.audit.approve', 'quality.disposition', 'quality.approve'];
const me = id => ({ personId: id, permissions: permission });
const source = { reference: { context: 'Engineering', type: 'DocumentRevision', key: 'AUDIT-BASIS', revision: 'B' }, ownerContext: 'Engineering', ownerType: 'DocumentRevision', ownerId: 'AUDIT-BASIS', ownerRevision: 'B', fingerprint: 'actual-owner-fingerprint', contributors: ['person:' + author], sources: [{ permission: 'engineering.read', resource: { type: 'DocumentRevision', id: 'AUDIT-BASIS/B', facilityId: 'ACTUAL-PLANT' } }] };
function program(state = 'Reviewed', extra = {}) {
  return { code: 'AUDIT-PROGRAM', organizationCode: 'ACTUAL-ORG', facilityCode: 'ACTUAL-PLANT', programId: null, markings: 'None', revisions: [{ code: 'AUDIT-PROGRAM', revision: 2, version: 7, state, createdBy: 'person:' + author, lastEditedBy: 'person:' + author, sources: [source], requiresLicensedVerification: true, content: { scope: 'Actual source control', impartiality: { requireIndependentReportApproval: true } }, history: [{ action: 'Create', actor: 'person:' + author, note: 'Actual human definition', reason: 'Owner chose exact criteria', version: 1 }], ...extra }] };
}
function run(state = 'InProgress', id = 'adba810e-f6b6-4855-a154-0e2bf772317f') {
  return { id, version: 4, generation: 2, state, organizationCode: 'ACTUAL-ORG', facilityCode: 'ACTUAL-PLANT', markings: 'None', auditProgram: { code: 'AUDIT-PROGRAM', revision: 2 }, auditors: [{ personId: observer, responsibility: 'Actual evidence judgment' }], competence: [{ personId: observer, asOf: '2026-10-14T15:00:00Z', qualifications: [{ code: 'AUDITOR', status: 'Qualified', qualifiedAsOf: true }] }], report: state === 'ReportSubmitted' ? { authoredBy: 'person:' + observer, evidence: [source], humanAssessment: 'Qualified human evaluates partial scope' } : null, sources: [source], history: [] };
}
function capa(state = 'Investigating') { return { capa: { capaId: 501, version: 6, generation: 2, state, organizationCode: 'ACTUAL-ORG', facilityCode: 'ACTUAL-PLANT', markings: 'None', createdBy: 'person:' + author, causeKnowledge: 'Unknown', confirmedCause: null, sources: [source] }, core: { capaId: 501, rootCause: null, action: null, status: 'Open', governed: true }, investigations: [], actions: [], effectiveness: [] }; }
async function harness(t, { tab = 'programs', actor = reviewer, configured = true, records = [program()], detail = null, route = () => null } = {}) {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const old = { subject: session.subject, token: session.token }; session.subject = 'audit-user'; session.token = null; t.after(() => Object.assign(session, old));
  const calls = [];
  globalThis.fetch = (path, opts = {}) => {
    const body = opts.body ? JSON.parse(opts.body) : null; calls.push({ path, body });
    const override = route(path, body); if (override) return Promise.resolve(override);
    if (path === '/api/quality/audits/status') return Promise.resolve(response({ configured, organizationCode: configured ? 'ACTUAL-ORG' : null }));
    if (path.includes('?take=')) return Promise.resolve(response({ items: records, nextCursor: null }));
    if (path === '/api/quality/audits/programs/AUDIT-PROGRAM') return Promise.resolve(response(program()));
    if (!body && detail) return Promise.resolve(response(detail));
    if (body) return Promise.resolve(response({ message: 'Actual human decision recorded.' }));
    return Promise.resolve(response({}));
  };
  const controller = await qmsAuditPage(page, { me: me(actor), query: new URLSearchParams({ tab }) }); t.after(controller.dispose);
  return { ...browser, page, controller, calls, open: async id => { fire(page.querySelector('[data-open="' + (id ?? records[0].code ?? records[0].id ?? records[0].capaId) + '"]'), 'click'); await flush(); }, dialog: () => browser.document.querySelector('.modal') };
}
function fill(m, vals) { for (const [name, value] of Object.entries(vals)) m.querySelector('#f-' + name).value = value; }
function submit(m) { fire(m.querySelector('[data-confirm]'), 'click'); }

test('program displays actual organization, exact retained sources and pending licensed verification', async t => {
  const h = await harness(t); await h.open();
  assert.match(h.page.innerHTML, /ACTUAL-ORG/); assert.match(h.page.innerHTML, /AUDIT-BASIS/); assert.match(h.page.innerHTML, /Actual human definition/); assert.match(h.page.innerHTML, /requires Licensed Verification|Requires Licensed Verification/i);
  assert.equal(h.calls.filter(x => x.body).length, 0);
});
test('independent program approval keeps expected version and human reason in actual handler', async t => {
  const h = await harness(t); await h.open(); fire(h.page.querySelector('[data-owner-action="Approve"]'), 'click');
  const m = h.dialog(); fill(m, { note: 'Reviewed exact source and human criteria', reason: 'Actual independent approval' }); submit(m); await flush();
  const call = h.calls.find(x => x.body); assert.equal(call.path, '/api/quality/audits/programs/AUDIT-PROGRAM/revisions/2/approve'); assert.equal(call.body.data.expectedVersion, 7); assert.equal(call.body.data.note, 'Reviewed exact source and human criteria'); assert.equal(call.body.reason, 'Actual independent approval');
});
test('all earlier content contributors remain excluded from independent approval', () => {
  const p = program('Reviewed', { history: [{ action: 'Edit', actor: 'person:' + reviewer }] }); assert.equal(auditProgramActions(me(reviewer), p, p.revisions[0]).includes('Approve'), false);
  const q = program(); assert.equal(auditProgramActions(me(author), q, q.revisions[0]).includes('Approve'), false);
});
test('unconfigured organization never offers creation or inferred scope', async t => {
  const h = await harness(t, { configured: false, records: [] }); assert.equal(h.page.querySelector('[data-create]'), null); assert.match(h.page.innerHTML, /must be configured/); assert.equal(h.calls.filter(x => x.body).length, 0);
});
test('program form has explicit blank competence and impartiality choices and no universal questions', () => {
  const definition = auditFormDefinition('Program');
  for (const name of ['requireFormalQualifications', 'requireIndependentCompetenceAssessment', 'prohibitAuditingOwnedProcesses', 'prohibitAuditingContributedEvidence', 'requireIndependentReportApproval']) assert.equal(definition.fields.find(x => x.name === name).value, '');
  assert.equal(definition.groups.find(x => x.name === 'checklist').rows?.length ?? 0, 0);
  assert.equal(definition.groups.find(x => x.name === 'criteria').minimum, 1);
});
test('human typed criteria preserve exact controlled revisions and explicit decisions', () => {
  const v = { title: 'Actual audit', scope: 'Actual source scope', scheduleBasis: 'Owner schedule', frequency: 'On source change', ownerPersonId: author, findingFollowUpCriteria: 'Actual independent verified actions', closureCriteria: 'Approved report and verified follow-up', requireFormalQualifications: 'true', qualificationCodes: 'AUDITOR', requireIndependentCompetenceAssessment: 'false', competenceCriteria: 'Demonstrated actual evidence judgment', reassessmentInterval: 'Chosen review interval', prohibitAuditingOwnedProcesses: 'true', prohibitAuditingContributedEvidence: 'true', requireIndependentReportApproval: 'true', conflictAssessmentCriteria: 'Actual ownership and contributions' };
  const g = { processes: [{ code: 'PROCESS', revision: 4 }], criteria: [{ code: 'C1', requirements: 'REQ | 3', documents: 'PROC | B', requiredEvidenceKinds: 'ProductionReport', evidenceAdequacyCriteria: 'Actual human assessment' }], checklist: [{ code: 'Q1', criterionCode: 'C1', question: 'Actual organization question' }], documents: [], evidence: [] };
  const result = auditFormData('Program', v, g); assert.deepEqual(result.criteria[0].requirements, [{ code: 'REQ', revision: 3 }]); assert.deepEqual(result.criteria[0].documents, [{ documentNumber: 'PROC', revision: 'B' }]); assert.equal(result.competence.requireIndependentCompetenceAssessment, false); assert.equal(result.competence.competenceCriteria, v.competenceCriteria);
  assert.throws(() => auditFormData('Program', v, { ...g, criteria: [{ ...g.criteria[0], requirements: 'REQ | 0' }] }), /positive whole/);
});
test('unknown cause stays null and cause proposal remains explicitly evidence supported', () => {
  const definition = auditFormDefinition('Investigation'); assert.equal(definition.fields.find(x => x.name === 'humanDeterminedCause').required, false);
  const data = auditFormData('Investigation', { humanDeterminedCause: null, actualInvestigation: 'Recorded human investigation' }, { proposals: [{ description: 'Possible missing validation', supportingBasis: 'Recorded observation', contraryEvidence: 'Alternatives assessed', evidenceContext: 'Engineering', evidenceType: 'DocumentRevision', evidenceKey: 'ACTUAL', evidenceRevision: 'B' }], evidence: [] });
  assert.equal(data.humanDeterminedCause, null); assert.equal(data.proposals[0].evidence[0].key, 'ACTUAL'); assert.equal(data.proposals[0].description, 'Possible missing validation');
});
test('actual approved checklist questions have no inferred answers and retain separate human adequacy/evidence', () => {
  const p = { content: { checklist: [{ code: 'QUESTION-7', question: 'Actual approved question?' }] } }, definition = auditFormDefinition('Report', p), group = definition.groups.find(x => x.name === 'checklistAnswers');
  assert.deepEqual(group.rows, [{ checklistCode: 'QUESTION-7', question: 'Actual approved question?' }]);
  assert.equal(group.columns.find(x => x.name === 'adequacy').value, '');
  const data = auditFormData('Report', { summary: 'Actual report' }, { evidence: [], checklistAnswers: [{ checklistCode: 'QUESTION-7', actualObservation: 'Selected actual source observed', adequacy: 'Partial', adequacyBasis: 'Qualified human finds limited scope', limitations: 'Missing required cohort', evidenceReferences: 'Engineering | DocumentRevision | PROCEDURE | B' }] });
  assert.equal(data.checklistAnswers[0].adequacy, 'Partial'); assert.equal(data.checklistAnswers[0].evidence[0].revision, 'B'); assert.equal(data.checklistAnswers[0].question, undefined);
});
test('assigned auditor records explicit human evidence adequacy with captured run version and generation', async t => {
  const a = run(), h = await harness(t, { tab: 'runs', actor: observer, records: [a], detail: { audit: a, findings: [] } }); await h.open();
  fire(h.page.querySelector('[data-owner-action="Finding"]'), 'click'); const m = h.dialog();
  fill(m, { criterionCode: 'C1', kind: 'Observation', statement: 'Actual observed evidence', actualObservation: 'Selected original owner records examined', adequacy: 'Partial', adequacyBasis: 'Qualified human finds incomplete required scope', limitations: 'Other required sources are unavailable', followUpRequired: 'false', reason: 'Record actual limited judgment' }); submit(m); await flush();
  const call = h.calls.find(x => x.body); assert.equal(call.body.data.expectedAuditVersion, 4); assert.equal(call.body.data.generation, 2); assert.equal(call.body.data.adequacy, 'Partial'); assert.deepEqual(call.body.data.evidence, []);
});
test('report approval requires explicit governed independence, assignment role alone does not prove competence', () => {
  const a = run('ReportSubmitted'); assert.equal(auditRunActions(me(observer), a, program().revisions[0]).includes('ApproveReport'), false);
  assert.equal(auditRunActions(me(reviewer), a, program().revisions[0]).includes('ApproveReport'), true);
  assert.equal(auditRunActions(me(reviewer), a, null).includes('ApproveReport'), false);
});
test('governed CAPA displays unknown cause and existing owner ID without legacy implementation bypass', async t => {
  const detail = capa(), h = await harness(t, { tab: 'capas', actor: author, records: [detail.capa], detail }); await h.open();
  assert.match(h.page.innerHTML, /Existing CAPA ID 501/); assert.match(h.page.innerHTML, /Cause remains unknown/); assert.equal(h.page.querySelector('[data-implement]'), null); assert.equal(h.page.querySelector('[data-owner-action="Close"]'), null);
});
test('inconclusive independent effectiveness permits controlled reopen and never closure', () => {
  const detail = capa('EffectivenessSubmitted'); detail.capa.latestEffectivenessId = 'result'; detail.effectiveness.push({ id: 'result', state: 'Approved', result: 'Inconclusive', createdBy: 'person:' + author, evidence: [] });
  assert.equal(capaActions(me(reviewer), detail).includes('Close'), false); assert.equal(capaActions(me(reviewer), detail).includes('Reopen'), true);
});
test('sign-out or disposed context blocks detached modal commands', async t => {
  const h = await harness(t); await h.open(); fire(h.page.querySelector('[data-owner-action="Approve"]'), 'click'); const m = h.dialog();
  fill(m, { note: 'Actual judgment', reason: 'Actual reason' }); session.subject = 'different-person'; submit(m); await flush(); assert.equal(h.calls.filter(x => x.body).length, 0);
  h.controller.dispose(); assert.equal(m.isConnected, false);
});
test('refresh closes and invalidates a stale decision modal while keeping focus semantics', async t => {
  const h = await harness(t); await h.open(); const opener = h.page.querySelector('[data-owner-action="Approve"]'); opener.focus(); fire(opener, 'click'); const m = h.dialog();
  assert.equal(m.getAttribute('role'), 'dialog'); assert.equal(m.getAttribute('aria-modal'), 'true'); fill(m, { note: 'Actual review', reason: 'Actual reason' }); fire(h.page.querySelector('[data-refresh]'), 'click'); await flush(); submit(m); await flush();
  assert.equal(m.isConnected, false); assert.equal(h.calls.filter(x => x.body).length, 0);
});
test('delayed audit detail A cannot replace currently opened B', async t => {
  const a = run(), b = run('Scheduled', '972484ec-b99e-4cb4-8d60-786c1c603e64'), waiting = deferred();
  const h = await harness(t, { tab: 'runs', records: [a, b], route: path => path === '/api/quality/audits/runs/' + a.id ? waiting.promise : path === '/api/quality/audits/runs/' + b.id ? response({ audit: b, findings: [] }) : null });
  fire(h.page.querySelector('[data-open="' + a.id + '"]'), 'click'); await flush(); await h.open(b.id); waiting.resolve(response({ audit: a, findings: [] })); await flush();
  assert.match(h.page.innerHTML, new RegExp(b.id)); assert.equal(h.page.querySelector('[data-owner-action="Finding"]'), null); assert.equal(h.calls.filter(x => x.body).length, 0);
});
test('uncertain server outcome preserves the proposal, blocks retry and requires owner refresh', async t => {
  const h = await harness(t, { route: (path, body) => body ? response({ code: 'service_unavailable', detail: 'Outcome unavailable' }, 503) : null }); await h.open(); fire(h.page.querySelector('[data-owner-action="Approve"]'), 'click'); const m = h.dialog(); fill(m, { note: 'Actual assessment', reason: 'Actual decision' }); submit(m); await flush();
  assert.equal(m.isConnected, true); assert.equal(m.querySelector('#f-note').value, 'Actual assessment'); assert.equal(m.querySelector('#f-reason').value, 'Actual decision');
  assert.equal(m.querySelector('[data-confirm]').disabled, true); assert.match(m.querySelector('[data-error]').textContent, /entries are preserved/); assert.equal(h.calls.filter(x => x.body).length, 1);
  fire(m.querySelector('[data-cancel]'), 'click'); assert.equal(h.page.querySelector('[data-owner-action="Approve"]'), null);
  fire(h.page.querySelector('[data-refresh]'), 'click'); await flush(); await flush(); assert.ok(h.page.querySelector('[data-owner-action="Approve"]'));
});

test('audit criteria and checklist evidence use structured rows while preserving source parsers', () => {
  const program = auditFormDefinition('Program', { content: { criteria: [{ code: 'C1', name: 'Traceability', requirements: [{ code: 'QMS-8.5', revision: 2 }], documents: [{ documentNumber: 'WI-10', revision: 'C' }] }] } });
  const criteria = program.groups.find(group => group.name === 'criteria');
  assert.equal(criteria.columns.find(field => field.name === 'requirements').type, 'repeater');
  assert.equal(criteria.rows[0].requirements[0].code, 'QMS-8.5');
  assert.equal(criteria.columns.find(field => field.name === 'documents').type, 'repeater');
  const report = auditFormDefinition('Report', { content: { checklist: [{ code: 'Q1', question: 'Was the control followed?' }] } });
  const references = report.groups.find(group => group.name === 'checklistAnswers').columns.find(field => field.name === 'evidenceReferences');
  assert.equal(references.type, 'repeater');
  const payload = auditFormData('Report', {}, { evidence: [], checklistAnswers: [{ checklistCode: 'Q1', evidenceReferences: 'Inventory | Lot | LOT-9 | 2 | EVT-4 | AUD-8' }] });
  assert.deepEqual(payload.checklistAnswers[0].evidence, [{ context: 'Inventory', type: 'Lot', key: 'LOT-9', revision: '2', eventId: 'EVT-4', auditId: 'AUD-8' }]);
  assert.deepEqual(parsedRefs('QMS-8.5 | 2'), [{ code: 'QMS-8.5', revision: 2 }]);
  assert.deepEqual(evidenceLines('Inventory | Lot | LOT-9 | 2 | EVT-4 | AUD-8'), payload.checklistAnswers[0].evidence);
});
