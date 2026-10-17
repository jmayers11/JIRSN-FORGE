import assert from 'node:assert/strict';
import test from 'node:test';
import { qmsPage } from '../../src/Forge.Host/wwwroot/js/pages/qms.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

const authorId = 'b93f8c16-fc50-4f73-ad28-e48c44ef2b66', reviewerId = 'd664fbd0-2f82-4f53-9f81-dab9c8a60a78';
const permissions = ['quality.qms.read', 'quality.qms.manage', 'quality.qms.review', 'quality.qms.approve', 'quality.qms.publish'];
const adoption = { code: 'CUT-EXECUTION', organizationCode: 'ACTUAL-ORG', facilityCode: 'PLANT', programId: null, markings: 'None', createdBy: `person:${authorId}`, revisions: [{ code: 'CUT-EXECUTION', revision: 2, version: 8, state: 'Reviewed', content: { purpose: 'Execution', policyCode: 'EXECUTION-POLICY', policyRevision: 3, selector: { workCenterCode: 'CUT', operationSequence: 10, partNumber: 'PART-A', partRevision: 'A' }, process: { code: 'MACHINING', revision: 4 }, riskClassCode: 'HIGH', riskBasis: 'Approved process risk assessment', basis: 'Use this exact policy for cutting' }, createdBy: `person:${authorId}`, lastEditedBy: `person:${authorId}`, reviewedBy: `person:${reviewerId}`, effectiveFrom: null, effectiveTo: null, sources: [{ kind: 'QmsRevision', key: 'EXECUTION-POLICY', revision: '3', markings: 'None', facilityCode: 'PLANT' }], history: [{ action: 'Review', actor: `person:${reviewerId}`, at: '2026-10-14T15:00:00Z', version: 8, toState: 'Reviewed', note: 'Reviewed binding scope', reason: 'Process assessment' }] }] };

async function harness(t, kind, records = []) {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  const before = { subject: session.subject, token: session.token }; session.subject = 'qms-policy-user'; session.token = null; t.after(() => Object.assign(session, before));
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body });
    if (path === '/api/quality/qms/status') return Promise.resolve(response({ configured: true, organizationCode: 'ACTUAL-ORG' }));
    if (path.startsWith('/api/quality/qms/records?') || path === '/api/quality/qms/adoptions') return Promise.resolve(response(records));
    if (path === '/api/quality/qms/adoptions/CUT-EXECUTION') return Promise.resolve(response(adoption));
    if (path === '/api/kernel/directory') return Promise.resolve(response([{ personId: authorId, displayName: 'Policy owner' }]));
    if (path === '/api/resources/') return Promise.resolve(response({ sites: [{ code: 'PLANT', name: 'Plant One' }] }));
    if (path.startsWith('/api/quality/qms/policy-current/')) return Promise.resolve(response({ adoptionCode: 'CUT-EXECUTION', adoptionRevision: 2, policyCode: 'EXECUTION-POLICY', policyRevision: 3, organizationCode: 'ACTUAL-ORG', facilityCode: 'PLANT', effectiveFrom: '2026-10-15T00:00:00Z', effectiveTo: null, selector: { workCenterCode: 'CUT', activityCode: 'SLIT' }, process: null, riskClassCode: 'HIGH', riskBasis: 'Approved risk review' }));
    return Promise.resolve(response({ message: 'Recorded.' }));
  };
  const controller = await qmsPage(page, { me: { personId: reviewerId, permissions }, query: new URLSearchParams({ kind }) }); t.after(controller.dispose);
  return { ...browser, page, calls, controller, create: () => { fire(page.querySelector('[data-qms-create]'), 'click'); return browser.document.querySelector('.modal'); } };
}
function fill(modal, values) { for (const [name, value] of Object.entries(values)) modal.querySelector(`#f-${name}`).value = value; }
function header(modal, code) { fill(modal, { code, facilityCode: 'PLANT', markings: 'None', qmsReason: 'Explicit human governance definition' }); }
function policy(modal, kind) { fill(modal, { policyKind: kind, statement: 'Organization-defined policy', ownerPersonId: authorId, communicationMethod: 'Controlled procedure', reviewInterval: 'Annual organization review', acknowledgmentRequired: 'true' }); fire(modal.querySelector('#f-policyKind'), 'change'); }
function extension(modal) { fill(modal, { extensionEnabled: 'true' }); fire(modal.querySelector('#f-extensionEnabled'), 'change'); }
function row(modal, name, values) { const group = modal.querySelector(`[data-qms-extension-group="${name}"]`); fire(group.querySelector('[data-qms-add]'), 'click'); const added = group.querySelector('[data-qms-rows]').children.at(-1); fill(added, Object.fromEntries(Object.entries(values).map(([name, value]) => [`${added.qmsPrefix}${name}`, value]))); }

test('adoption form binds an exact policy to actual selectors and records process/risk as human governance', async t => {
  const h = await harness(t, 'Adoption'), modal = h.create(); header(modal, 'CUT-EXECUTION');
  assert.equal(modal.querySelector('#f-purpose').value, '');
  fill(modal, { purpose: 'Execution', policyCode: 'EXECUTION-POLICY', policyRevision: 3, workCenterCode: 'CUT', operationSequence: 10, partNumber: 'PART-A', partRevision: 'A', processCode: 'MACHINING', processRevision: 4, riskClassCode: 'HIGH', riskBasis: 'Approved process risk assessment', basis: 'Use this policy for cutting' });
  fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  const body = h.calls.find(call => call.path === '/api/quality/qms/adoptions' && call.body)?.body;
  assert.equal(body.kind, undefined); assert.equal(body.facilityCode, 'PLANT'); assert.equal(body.content.policyRevision, 3);
  assert.deepEqual(body.content.selector, { workCenterCode: 'CUT', activityCode: null, machineCode: null, operationSequence: 10, partNumber: 'PART-A', partRevision: 'A' });
  assert.deepEqual(body.content.process, { code: 'MACHINING', revision: 4 }); assert.equal(body.content.riskBasis, 'Approved process risk assessment');
  assert.equal(h.calls.some(call => call.path.startsWith('/api/shopfloor/')), false);
});

test('adoption review and approval use actual recorded authors and expectedVersion without implicit effectivity', async t => {
  const h = await harness(t, 'Adoption', [adoption]); fire(h.page.querySelector('[data-qms-open]'), 'click'); await flush();
  assert.match(h.page.innerHTML, /EXECUTION-POLICY/); assert.match(h.page.innerHTML, /Reviewed binding scope/); assert.ok(h.page.querySelector('[data-qms-action="approve"]'));
  fire(h.page.querySelector('[data-qms-action="approve"]'), 'click'); const modal = h.document.querySelector('.modal'); fill(modal, { qmsNote: 'Independent review confirms the binding', qmsReason: 'Approval of actual process scope' }); fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  assert.deepEqual(h.calls.filter(call => call.body).map(call => ({ path: call.path, body: call.body })), [{ path: '/api/quality/qms/adoptions/CUT-EXECUTION/revisions/2/approve', body: { expectedVersion: 8, note: 'Independent review confirms the binding', reason: 'Approval of actual process scope' } }]);
  assert.equal(h.calls.some(call => call.path.endsWith('/effective')), false);
});

test('incomplete process/risk declarations cannot be submitted as an adoption', async t => {
  const h = await harness(t, 'Adoption'), modal = h.create(); header(modal, 'INCOMPLETE');
  fill(modal, { purpose: 'Execution', policyCode: 'EXECUTION-POLICY', policyRevision: 3, processCode: 'MACHINING', basis: 'Human scope basis' }); fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  assert.match(modal.querySelector('[data-qms-error]').textContent, /both its code and exact revision/); assert.equal(h.calls.filter(call => call.body).length, 0);
  fill(modal, { processRevision: 4, riskClassCode: 'HIGH' }); fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  assert.match(modal.querySelector('[data-qms-error]').textContent, /human basis/); assert.equal(h.calls.filter(call => call.body).length, 0);
});

test('current policy lookup labels itself as a governance preview and sends the selected actual scope', async t => {
  const h = await harness(t, 'Adoption');
  h.page.querySelector('[data-policy-current="facilityCode"]').value = 'PLANT';
  h.page.querySelector('[data-policy-current="workCenterCode"]').value = 'CUT';
  h.page.querySelector('[data-policy-current="activityCode"]').value = 'SLIT';
  fire(h.page.querySelector('[data-policy-current-check]'), 'click'); await flush();
  const call = h.calls.find(item => item.path.startsWith('/api/quality/qms/policy-current/'));
  assert.ok(call); assert.ok(call.path.startsWith('/api/quality/qms/policy-current/Execution?')); assert.match(call.path, /facilityCode=PLANT/); assert.match(call.path, /workCenterCode=CUT/);
  const result = h.page.querySelector('[data-policy-current-result]');
  assert.match(result.innerHTML, /Preview only · effective adoption CUT-EXECUTION/);
  assert.match(result.innerHTML, /Approved risk review/);
});

test('execution presentation captures explicit assurance/session policy and exact operation/process scan rules', async t => {
  const h = await harness(t, 'Policy'), modal = h.create(); header(modal, 'EXECUTION-POLICY'); policy(modal, 'Execution'); extension(modal);
  assert.equal(modal.querySelector('#f-providerAssurance').value, ''); assert.equal(modal.querySelector('#f-requireTrustedWorkstation').value, '');
  fill(modal, { requireQualification: 'true', requirePresentOperator: 'true', requireReleasedDocuments: 'true', revalidateAtCommit: 'true', sessionExpirySeconds: 120, requireServerSession: 'true', requireCurrentAuthenticatedOperator: 'true', requireTrustedWorkstation: 'true', providerAssurance: 'StepUp' });
  row(modal, 'operationRules', { code: 'CUT-1', workCenterCode: 'CUT', operationSequence: 10, partNumber: 'PART-A', partRevision: 'A', processCode: 'MACHINING', processRevision: 4, riskClassCode: 'HIGH', requiredScans: 'Order\nOperation\nMaterial\nControlledInstruction', allowManualSelection: 'false', requireInstructionRevision: 'true', requireTrustedTerminalContext: 'true' });
  fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  const rules = h.calls.find(call => call.body)?.body.content.policy.rules.execution.presentation;
  assert.equal(rules.providerAssurance, 'StepUp'); assert.equal(rules.requireTrustedWorkstation, true); assert.equal(rules.sessionExpirySeconds, 120);
  assert.deepEqual(rules.operationRules[0].requiredScans, ['Order', 'Operation', 'Material', 'ControlledInstruction']); assert.equal(rules.operationRules[0].allowManualSelection, false); assert.deepEqual(rules.operationRules[0].process, { code: 'MACHINING', revision: 4 }); assert.equal(rules.operationRules[0].selector.operationSequence, 10);
  assert.equal(h.calls.some(call => call.path.startsWith('/api/identification/scan-sessions')), false);
});

test('label lifecycle form records copy/alias/confirmation decisions and exact template/symbology restrictions', async t => {
  const h = await harness(t, 'Policy'), modal = h.create(); header(modal, 'COPY-POLICY'); policy(modal, 'LabelCopy'); extension(modal);
  assert.equal(modal.querySelector('#f-requiredConfirmation').value, '');
  fill(modal, { requireCopyAuthorization: 'true', retainCopyReason: 'true', maximumCopiesPerRequest: 2, activeCopyMode: 'MultipleActive', maximumActiveCopies: 3, allowReprint: 'true', allowReplacement: 'true', allowVoid: 'true', replacementVoidsPriorCopy: 'true', requireIndependentCopyAuthorization: 'false', allowAliasCreate: 'true', allowAliasReplacement: 'false', allowAliasVoid: 'true', requiredConfirmation: 'OperatorObservedPrinting', allowedSubjectKinds: 'material\nlocation', allowedSymbologies: 'Code128\nQR' });
  row(modal, 'allowedTemplates', { code: 'ROLL-LABEL', version: 2 }); fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  const rules = h.calls.find(call => call.body)?.body.content.policy.rules.labelCopy.lifecycle;
  assert.equal(rules.requireIndependentCopyAuthorization, false); assert.equal(rules.allowAliasReplacement, false); assert.equal(rules.requiredConfirmation, 'OperatorObservedPrinting'); assert.equal(rules.maximumActiveCopies, 3); assert.deepEqual(rules.allowedTemplates, [{ code: 'ROLL-LABEL', version: 2 }]); assert.deepEqual(rules.allowedSymbologies, ['Code128', 'QR']);
});

test('attempt telemetry uses explicit normalized-reference limits and declared metadata without raw credentials', async t => {
  const h = await harness(t, 'Policy'), modal = h.create(); header(modal, 'ATTEMPTS'); policy(modal, 'AttemptRetention'); extension(modal);
  assert.equal(modal.querySelector('#f-payloadMode').value, '');
  fill(modal, { preserveDeniedAttempts: 'true', preserveSourceLink: 'true', neverRewriteAuditChain: 'true', minimumRetentionDays: 365, maximumRawAttemptAgeDays: 14, maximumStoredRawAttempts: 5000, payloadMode: 'NormalizedReferencesOnly', maximumCapturedReferenceCharacters: 1024, retainDeclaredAdapter: 'true', retainDeclaredDevice: 'false', retainCaptureMethod: 'true' }); fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  assert.deepEqual(h.calls.find(call => call.body)?.body.content.policy.rules.attemptRetention.telemetry, { payloadMode: 'NormalizedReferencesOnly', maximumCapturedReferenceCharacters: 1024, retainDeclaredAdapter: true, retainDeclaredDevice: false, retainCaptureMethod: true });
  assert.equal(h.calls.find(call => call.body)?.body.content.policy.rules.attemptRetention.extensionEnabled, undefined);
});
