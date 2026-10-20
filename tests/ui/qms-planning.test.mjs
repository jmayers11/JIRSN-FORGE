import assert from 'node:assert/strict';
import test from 'node:test';
import { qmsPlanningPage, planningDefinition, planningContentFrom, assessmentContentFrom, definitionActions, workActions, retentionActions } from '../../src/Forge.Host/wwwroot/js/pages/qms-planning.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, deferred, flush, fire } from './browser-harness.mjs';

const author = '71656ec5-98b3-4899-9da1-26c7cbe660d0', reviewer = '6cf938bf-6c38-481a-a5a4-a3aef8362da4';
const permissions = ['quality.qms.read', 'quality.qms.manage', 'quality.qms.review', 'quality.qms.approve', 'quality.qms.publish'];
const me = { personId: reviewer, permissions };
const definition = (state = 'Reviewed', extras = {}) => ({ code: 'METHOD', revision: 1, version: 8, state, createdBy: `person:${author}`, lastEditedBy: `person:${author}`, requiresLicensedVerification: true,
  content: { evaluationMethod: { name: 'Actual qualitative evaluation', kind: 'Qualitative', method: 'Owner assessment of actual process evidence', decisionCriteria: 'Record the basis and affected decisions', scale: [], reviewInterval: 'On change' }, documents: [{ documentNumber: 'METHOD-WI', revision: 'B' }] }, sources: [], history: [], ...extras });
const record = (state, extras = {}) => ({ code: 'METHOD', kind: 'EvaluationMethod', organizationCode: 'ACTUAL-ORG', facilityCode: 'PLANT1', programId: null, markings: 'None', revisions: [definition(state)], ...extras });
async function harness(t, { records = [record('Reviewed')], kind = 'EvaluationMethod', person = me, configured = true, work = { assessments: [], tasks: [], cases: [] }, route = () => null } = {}) {
  const browser = installBrowser(t), page = browser.document.createElement('div'), calls = []; browser.document.body.append(page);
  const previous = { subject: session.subject, token: session.token }; session.subject = 'qms-planning-test'; session.token = null; t.after(() => Object.assign(session, previous));
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body }); const override = route(path, body); if (override) return Promise.resolve(override);
    if (path.endsWith('/status')) return Promise.resolve(response({ configured, organizationCode: configured ? 'ACTUAL-ORG' : null }));
    if (path.includes('/records?')) return Promise.resolve(response({ items: records, nextCursor: null }));
    if (path.includes('/retention/') && path.includes('?')) return Promise.resolve(response({ items: [], nextCursor: null }));
    if (path.endsWith('/work')) return Promise.resolve(response(work));
    if (!body && path.endsWith('/records/METHOD')) return Promise.resolve(response(records[0]));
    return Promise.resolve(response({ succeeded: true, message: 'Decision recorded.' }));
  };
  const controller = await qmsPlanningPage(page, { me: person, query: new URLSearchParams({ kind }) }); t.after(controller.dispose);
  return { ...browser, page, calls, controller, open: async () => { fire(page.querySelector('[data-open]'), 'click'); await flush(); }, create: () => { fire(page.querySelector('[data-create]'), 'click'); return browser.document.querySelector('.modal'); } };
}
const fill = (modal, values) => { for (const [name, value] of Object.entries(values)) modal.querySelector(`#f-${name}`).value = value; };

test('metric form preserves explicit empty decisions and exact operation cohort; no metric/policy defaults', () => {
  const d = planningDefinition('MetricDefinition');
  assert.equal(d.fields.find(field => field.name === 'includeAllOperations').value, '');
  assert.equal(d.fields.find(field => field.name === 'maximumRows').value, '');
  const c = planningContentFrom('MetricDefinition', { name: 'Recorded scrap fraction', sourceKind: 'RecordedProductionReports', productionMeasure: 'ScrapFraction', unit: 'fraction', orderNumbers: '101\n102', operationSequences: '20', includeAllOperations: 'false', periodFrom: '2026-10-01T00:00', periodTo: '2026-10-02T00:00' }, {});
  assert.deepEqual(c.metricDefinition.orderNumbers, [101, 102]); assert.deepEqual(c.metricDefinition.operationSequences, [20]); assert.equal(c.metricDefinition.includeAllOperations, false);
  assert.equal(c.metricDefinition.periodTo, '2026-10-02T00:00:00.000Z'); assert.equal(c.metricDefinition.unit, 'fraction');
});
test('unknown objective baseline remains null and actual baseline evidence retains exact event/audit references', () => {
  const baseline = { context: 'Execution', type: 'ProductionReport', key: '99', eventId: 'actual-event', auditId: 'actual-audit' };
  const c = planningContentFrom('Objective', { title: 'Actual objective', metricCode: 'METRIC', metricRevision: 3, baselineStatus: 'Unknown', baseline: null, periodFrom: '2026-10-01T00:00', periodTo: '2026-10-02T00:00', dueAt: '2026-11-01T00:00' }, { baselineEvidence: [baseline] });
  assert.equal(c.objective.baseline, null); assert.deepEqual(c.objective.metric, { code: 'METRIC', revision: 3 }); assert.deepEqual(c.objective.baselineEvidence, [baseline]);
});
test('retention definitions preserve explicit finite/indefinite choice and exact allowed owner category', () => {
  const d = planningDefinition('RetentionSchedule'); assert.equal(d.fields.find(field => field.name === 'indefinite').value, '');
  const c = planningContentFrom('RetentionSchedule', { indefinite: 'true', durationDays: null, allowedOwnerTypes: 'Engineering/DocumentRevision\nProcurement/TechnicalPackage' }, {});
  assert.equal(c.retentionSchedule.indefinite, true); assert.equal(c.retentionSchedule.durationDays, null); assert.deepEqual(c.retentionSchedule.allowedOwnerTypes, ['Engineering/DocumentRevision', 'Procurement/TechnicalPackage']);
});
test('assessment forms keep failed/unknown effectiveness explicit and do not invent a human actual', () => {
  const result = assessmentContentFrom('EffectivenessReview', { from: '2026-10-01T00:00', to: '2026-10-02T00:00', effective: 'false', results: 'Observed recurrence', limitations: 'Full period reviewed' });
  assert.equal(result.effectivenessReview.effective, false);
  const evaluation = assessmentContentFrom('ObjectiveEvaluation', { from: '2026-10-01T00:00', to: '2026-10-02T00:00', humanActual: null }); assert.equal(evaluation.objectiveEvaluation.humanActual, null);
});
test('earlier authors and evidence contributors cannot approve; independent reviewer can approve own review', () => {
  const saved = record('Reviewed'); assert.ok(definitionActions(me, saved, saved.revisions[0]).includes('Approve'));
  assert.ok(!definitionActions(me, saved, definition('Reviewed', { history: [{ action: 'Edit', actor: `person:${reviewer}` }] })).includes('Approve'));
  assert.ok(!definitionActions(me, saved, definition('Reviewed', { sources: [{ contributors: [`person:${reviewer}`] }] })).includes('Approve'));
});
test('open Approved proposals forbid new revision and Draft/Submitted proposals never enable actual task completion', () => {
  const saved = record('Approved'); assert.ok(!definitionActions(me, saved, saved.revisions[0]).includes('NewRevision'));
  assert.deepEqual(workActions(me, saved, 'task', { state: 'Open', ownerPersonId: author, createdBy: `person:${author}` }), ['Withdraw']);
});
test('task verifier excludes assigned owner/completor and case closure follows actual effectiveness state', () => {
  const saved = record('Effective'), task = { state: 'Completed', ownerPersonId: reviewer, createdBy: `person:${author}`, completedBy: `person:${reviewer}` };
  assert.ok(!workActions(me, saved, 'task', task).includes('Verify'));
  assert.ok(!workActions(me, saved, 'case', { state: 'Implementing', createdBy: `person:${author}` }).includes('Close'));
  assert.ok(workActions(me, saved, 'case', { state: 'EffectivenessPending', createdBy: `person:${author}` }).includes('Accept'));
});
test('retention UI never offers active binding withdrawal, blocked manifest approval, self-release or any physical delete', () => {
  const scope = { facilityCode: 'PLANT1', programId: null, createdBy: `person:${author}`, proposedBy: `person:${author}` };
  assert.deepEqual(retentionActions(me, 'bindings', { ...scope, state: 'Active' }), []);
  assert.ok(!retentionActions(me, 'dispositions', { ...scope, state: 'DryRunFrozen', manifest: { eligible: false } }).includes('Approve'));
  assert.ok(!retentionActions(me, 'holds', { ...scope, state: 'ReleaseProposed', placedBy: `person:${author}`, releaseProposedBy: `person:${reviewer}` }).includes('Release'));
  assert.ok(retentionActions(me, 'dispositions', { ...scope, state: 'Approved' }).includes('Record'));
});
test('actual page displays exact revision, pending licensed verification and retention byte preservation without writes', async t => {
  const h = await harness(t); await h.open(); assert.match(h.page.innerHTML, /ACTUAL-ORG/); assert.match(h.page.innerHTML, /version 8/); assert.match(h.page.innerHTML, /METHOD-WI/); assert.match(h.page.innerHTML, /RequiresLicensedVerification/); assert.match(h.page.innerHTML, /bytes remain preserved/); assert.equal(h.calls.filter(item => item.body).length, 0);
});
test('unconfigured organization disables definition creation without an inferred organization', async t => {
  const h = await harness(t, { configured: false, records: [] }); assert.equal(h.page.querySelector('[data-create]'), null); assert.doesNotMatch(h.page.innerHTML, /ACTUAL-ORG/); assert.equal(h.calls.filter(item => item.body).length, 0);
});
test('approval freezes expected version/note/reason and duplicate click dispatches once; focus cleanup remains intact', async t => {
  const pending = deferred(), h = await harness(t, { route: (path, body) => body && path.endsWith('/approve') ? pending.promise : null }); await h.open();
  fire(h.page.querySelector('[data-definition-action="Approve"]'), 'click'); const modal = h.document.querySelector('.modal'); assert.equal(modal.getAttribute('aria-modal'), 'true');
  fill(modal, { note: 'Reviewed exact method and evidence', reason: 'Independent decision' }); const button = modal.querySelector('[data-confirm]'); fire(button, 'click'); fire(button, 'click');
  assert.deepEqual(h.calls.filter(item => item.path.endsWith('/approve')).map(item => item.body), [{ expectedVersion: 8, note: 'Reviewed exact method and evidence', reason: 'Independent decision' }]);
  pending.resolve(response({ message: 'Decision recorded.' })); await flush(); assert.equal(modal.isConnected, false); assert.equal(h.document.listeners.get('keydown').size, 0);
});
test('a detached or sign-out context cannot dispatch a staged command', async t => {
  const h = await harness(t); await h.open(); fire(h.page.querySelector('[data-definition-action="Approve"]'), 'click'); const modal = h.document.querySelector('.modal');
  fill(modal, { note: 'Actual decision', reason: 'Reason' }); session.subject = 'another-authenticated-person'; fire(modal.querySelector('[data-confirm]'), 'click'); await flush(); assert.equal(h.calls.filter(item => item.body).length, 0);
});
test('source selection invalidates an open decision and delayed A read cannot repaint under B', async t => {
  const pending = deferred(); let reads = 0; const h = await harness(t, { route: path => path.endsWith('/records/METHOD') && ++reads === 1 ? pending.promise : null });
  fire(h.page.querySelector('[data-open]'), 'click'); fire(h.page.querySelector('[data-kind="Objective"]'), 'click'); await flush(); pending.resolve(response(record('Reviewed'))); await flush(); assert.equal(h.page.querySelector('[data-definition-action="Approve"]'), null); assert.equal(h.calls.filter(item => item.body).length, 0);
});
test('409 retains captured version and asks for current read without fabricated success', async t => {
  const h = await harness(t, { route: (path, body) => body && path.endsWith('/approve') ? response({ detail: 'Version changed', code: 'stale_version' }, 409) : null }); await h.open();
  fire(h.page.querySelector('[data-definition-action="Approve"]'), 'click'); const modal = h.document.querySelector('.modal'); fill(modal, { note: 'Decision', reason: 'Reason' }); fire(modal.querySelector('[data-confirm]'), 'click'); await flush();
  assert.equal(modal.isConnected, true); assert.match(modal.querySelector('[data-error]').textContent, /Read the current version/); assert.equal(h.calls.find(item => item.path.endsWith('/approve')).body.expectedVersion, 8); assert.equal(h.document.querySelector('.toast'), null);
});
test('network uncertainty preserves staged decisions, blocks retry and requires current-record read', async t => {
  const h = await harness(t, { route: (path, body) => body && path.endsWith('/approve') ? Promise.reject(new TypeError('Connection lost')) : null }); await h.open();
  fire(h.page.querySelector('[data-definition-action="Approve"]'), 'click'); const modal = h.document.querySelector('.modal'); fill(modal, { note: 'Actual decision', reason: 'Reason' }); const submit = modal.querySelector('[data-confirm]'); fire(submit, 'click'); await flush();
  assert.equal(modal.isConnected, true); assert.equal(modal.querySelector('#f-note').value, 'Actual decision'); assert.equal(modal.querySelector('#f-reason').value, 'Reason');
  assert.equal(submit.disabled, true); assert.equal(h.calls.filter(item => item.body).length, 1); assert.match(modal.querySelector('[data-error]').textContent, /entries are preserved/);
  fire(modal.querySelector('[data-cancel]'), 'click'); assert.equal(h.page.querySelector('[data-definition-action="Approve"]'), null);
  fire(h.page.querySelector('[data-refresh]'), 'click'); await flush(); await flush(); assert.ok(h.page.querySelector('[data-definition-action="Approve"]'));
});
test('typed method form sends real fields instead of raw JSON and preserves explicit scope', async t => {
  const h = await harness(t, { records: [] }), modal = h.create(); fill(modal, { code: 'METHOD-NEW', facilityCode: 'PLANT1', markings: 'None', name: 'Owner qualitative criteria', kind: 'Qualitative', method: 'Assess actual observed consequences', decisionCriteria: 'Record explicit decision and action', reviewInterval: 'On change', reason: 'Actual organization choices' });
  fire(modal.querySelector('[data-confirm]'), 'click'); await flush(); const sent = h.calls.find(item => item.path.endsWith('/records') && item.body);
  assert.ok(sent, `Expected a typed record command; calls were ${JSON.stringify(h.calls)}; form error was ${modal.querySelector('[data-error]').textContent}`);
  assert.equal(sent.body.kind, 'EvaluationMethod'); assert.equal(sent.body.facilityCode, 'PLANT1'); assert.equal(sent.body.content.evaluationMethod.kind, 'Qualitative'); assert.deepEqual(sent.body.content.evaluationMethod.scale, []); assert.deepEqual(sent.body.content.documents, []); assert.equal(sent.body.programId, null);
});
