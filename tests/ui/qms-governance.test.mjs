import assert from 'node:assert/strict';
import test from 'node:test';
import { qmsPage } from '../../src/Forge.Host/wwwroot/js/pages/qms.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, deferred, flush, fire } from './browser-harness.mjs';

const authorId = 'b93f8c16-fc50-4f73-ad28-e48c44ef2b66', reviewerId = 'd664fbd0-2f82-4f53-9f81-dab9c8a60a78';
const permissions = ['quality.qms.read', 'quality.qms.manage', 'quality.qms.review', 'quality.qms.approve', 'quality.qms.publish'];
const policyContent = { policy: { policyKind: 'Execution', statement: 'Our approved execution policy', ownerPersonId: authorId, communicationMethod: 'Controlled briefing', reviewInterval: 'Annual organization review', acknowledgmentRequired: true, rules: { execution: { requireQualification: true, requirePresentOperator: true, requireReleasedDocuments: true, revalidateAtCommit: true } } }, documents: [{ documentNumber: 'PROCEDURE-7', revision: 'B' }] };
const revision = (state = 'Reviewed', extra = {}) => ({ code: 'EXECUTION', revision: 2, version: 7, state, content: policyContent, createdBy: `person:${authorId}`, lastEditedBy: `person:${authorId}`, reviewedBy: `person:${reviewerId}`, approvedBy: null, effectiveFrom: null, effectiveTo: null, requiresLicensedVerification: false, sources: [{ kind: 'DocumentRevision', key: 'PROCEDURE-7', revision: 'B', facilityCode: 'PLANT', programId: null, markings: 'None' }], history: [{ action: 'Review', actor: `person:${reviewerId}`, at: '2026-10-14T13:00:00Z', note: 'Reviewed against process needs', reason: 'Scheduled review', fromState: 'Submitted', toState: 'Reviewed', version: 7 }], ...extra });
const record = (state = 'Reviewed', extra = {}) => ({ code: 'EXECUTION', kind: 'Policy', organizationCode: 'ACTUAL-ORG', facilityCode: 'PLANT', programId: null, markings: 'None', createdBy: `person:${authorId}`, createdAt: '2026-10-14T12:00:00Z', revisions: [revision(state)], ...extra });

async function pageHarness(t, { records = [record()], kind = 'Policy', me = { personId: reviewerId, permissions }, configured = true, route = () => null } = {}) {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  const before = { subject: session.subject, token: session.token }; session.subject = 'qms-test-user'; session.token = null;
  t.after(() => Object.assign(session, before));
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body });
    const override = route(path, body); if (override) return Promise.resolve(override);
    if (path === '/api/quality/qms/status') return Promise.resolve(response({ configured, organizationCode: configured ? 'ACTUAL-ORG' : null }));
    if (path.startsWith('/api/quality/qms/records?')) return Promise.resolve(response(records));
    if (path === '/api/kernel/directory') return Promise.resolve(response([{ personId: authorId, displayName: 'Responsible author' }, { personId: reviewerId, displayName: 'Independent reviewer' }]));
    if (!body) { const found = records.find(item => path === `/api/quality/qms/records/${item.code}`); if (found) return Promise.resolve(response({ record: found, revisions: found.revisions })); }
    return Promise.resolve(response({ message: 'Saved.' }));
  };
  const controller = await qmsPage(page, { me, query: new URLSearchParams({ kind }) }); t.after(controller.dispose);
  return { ...browser, page, calls, controller, open: async (code = records[0]?.code) => { fire(page.querySelector(`[data-qms-open="${code}"]`), 'click'); await flush(); }, create: () => { fire(page.querySelector('[data-qms-create]'), 'click'); return browser.document.querySelector('.modal'); } };
}

function fill(modal, values) { for (const [name, value] of Object.entries(values)) modal.querySelector(`#f-${name}`).value = value; }
function header(modal, code = 'NEW-RECORD') { fill(modal, { code, facilityCode: 'PLANT', markings: 'None', qmsReason: 'Record the organization’s actual decision' }); }
function row(modal, group, values) {
  const section = modal.querySelector(`[data-qms-group="${group}"]`); fire(section.querySelector('[data-qms-add]'), 'click');
  const added = section.querySelector('[data-qms-rows]').children.at(-1);
  fill(added, Object.fromEntries(Object.entries(values).map(([name, value]) => [`${added.qmsPrefix}${name}`, value])));
  return added;
}

test('QMS page displays actual organization, exact source revisions and recorded lifecycle history', async t => {
  const h = await pageHarness(t); await h.open();
  assert.match(h.page.innerHTML, /ACTUAL-ORG/); assert.match(h.page.innerHTML, /Version 7/);
  assert.match(h.page.innerHTML, /PROCEDURE-7/); assert.match(h.page.innerHTML, /exact revision B/);
  assert.match(h.page.innerHTML, /Reviewed against process needs/); assert.match(h.page.innerHTML, /Scheduled review/);
  assert.equal(h.calls.filter(call => call.body).length, 0);
});

test('unconfigured organization offers no creation or inferred organization identity', async t => {
  const h = await pageHarness(t, { records: [], configured: false });
  assert.match(h.page.innerHTML, /QMS organization is not configured/); assert.equal(h.page.querySelector('[data-qms-create]'), null);
  assert.doesNotMatch(h.page.innerHTML, /ACTUAL-ORG/); assert.equal(h.calls.filter(call => call.body).length, 0);
});

test('content author cannot review or approve while a permitted reviewer can approve their own review', async t => {
  for (const state of ['Submitted', 'Reviewed']) await t.test(`author ${state}`, async child => {
    const h = await pageHarness(child, { records: [record(state)], me: { personId: authorId, permissions } }); await h.open();
    assert.equal(h.page.querySelector('[data-qms-action="review"]'), null); assert.equal(h.page.querySelector('[data-qms-action="approve"]'), null);
    assert.match(h.page.innerHTML, /content author cannot review or approve/);
  });
  const h = await pageHarness(t); await h.open(); assert.ok(h.page.querySelector('[data-qms-action="approve"]'));
});

test('an earlier content editor cannot approve after a later person edits the draft', async t => {
  const priorEditor = record('Reviewed', { revisions: [revision('Reviewed', { history: [{ action: 'Edit', actor: `person:${reviewerId}`, at: '2026-10-14T12:30:00Z', fromState: 'Draft', toState: 'Draft', note: 'Earlier edit', reason: 'Correct scope', version: 3 }, ...revision().history] })] });
  const h = await pageHarness(t, { records: [priorEditor] }); await h.open();
  assert.equal(h.page.querySelector('[data-qms-action="approve"]'), null); assert.match(h.page.innerHTML, /content author cannot review or approve/);
});

test('approved content must become effective or withdrawn before a new draft revision is offered', async t => {
  const h = await pageHarness(t, { records: [record('Approved')] }); await h.open();
  assert.ok(h.page.querySelector('[data-qms-action="effective"]')); assert.ok(h.page.querySelector('[data-qms-action="withdraw"]')); assert.equal(h.page.querySelector('[data-qms-action="new-revision"]'), null);
});

test('exact document source opens the owning revision without substituting its newest revision', async t => {
  const h = await pageHarness(t, { route: path => path === '/api/engineering/documents/PROCEDURE-7' ? response({ document: { title: 'Controlled execution procedure', authority: 'Internal', revisions: [{ revision: 'B', status: 'Superseded', fileSha256: 'recorded-B-hash' }, { revision: 'C', status: 'Released', fileSha256: 'new-C-hash' }] } }) : null });
  await h.open(); fire(h.page.querySelector('[data-qms-source]'), 'click'); await flush();
  const modal = h.document.querySelector('.modal'); assert.match(modal.innerHTML, /exact revision B/); assert.match(modal.querySelector('[data-source-content]').innerHTML, /recorded-B-hash/); assert.doesNotMatch(modal.querySelector('[data-source-content]').innerHTML, /new-C-hash/);
  assert.equal(h.calls.filter(call => call.body).length, 0);
});

test('approval captures expectedVersion, independent actor note and audit reason once despite duplicate clicks', async t => {
  const pending = deferred(); const h = await pageHarness(t, { route: (path, body) => body && path.endsWith('/approve') ? pending.promise : null });
  await h.open(); fire(h.page.querySelector('[data-qms-action="approve"]'), 'click');
  const modal = h.document.querySelector('.modal'); assert.equal(modal.getAttribute('aria-modal'), 'true');
  fill(modal, { qmsNote: 'Review confirmed the applicable procedure', qmsReason: 'Independent organization approval' });
  const confirm = modal.querySelector('[data-qms-confirm]'); fire(confirm, 'click'); fire(confirm, 'click');
  assert.deepEqual(h.calls.filter(call => call.path.endsWith('/approve')).map(call => call.body), [{ expectedVersion: 7, note: 'Review confirmed the applicable procedure', reason: 'Independent organization approval' }]);
  pending.resolve(response({ message: 'Approved.' })); await flush(); assert.equal(modal.isConnected, false); assert.equal(h.document.listeners.get('keydown').size, 0);
});

test('stale version reports refresh requirement without updating the captured version or fabricating success', async t => {
  const h = await pageHarness(t, { route: (path, body) => body && path.endsWith('/approve') ? response({ detail: 'The record version changed.', code: 'qms_version_conflict' }, 409) : null });
  await h.open(); fire(h.page.querySelector('[data-qms-action="approve"]'), 'click');
  const modal = h.document.querySelector('.modal'); fill(modal, { qmsNote: 'Decision basis', qmsReason: 'Approval reason' }); fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  assert.equal(modal.isConnected, true); assert.match(modal.querySelector('[data-qms-error]').textContent, /Refresh the record/);
  assert.equal(h.calls.find(call => call.path.endsWith('/approve')).body.expectedVersion, 7); assert.equal(h.document.querySelector('.toast'), null);
});

test('uncertain QMS command outcome preserves the form and blocks another decision until refresh', async t => {
  const h = await pageHarness(t, { route: (path, body) => body && path.endsWith('/approve') ? response({ detail: 'The service is temporarily unavailable.' }, 503) : null });
  await h.open(); fire(h.page.querySelector('[data-qms-action="approve"]'), 'click');
  const modal = h.document.querySelector('.modal'); fill(modal, { qmsNote: 'Approval based on the current controlled scope', qmsReason: 'Independent review completed' });
  const submit = modal.querySelector('[data-qms-confirm]'); fire(submit, 'click'); await flush();
  assert.equal(modal.isConnected, true); assert.equal(modal.querySelector('#f-qmsNote').value, 'Approval based on the current controlled scope');
  assert.equal(modal.querySelector('#f-qmsReason').value, 'Independent review completed'); assert.equal(submit.disabled, true);
  assert.match(modal.querySelector('[data-qms-error]').textContent, /entries are preserved/); assert.equal(h.document.querySelector('.toast'), null);
  fire(modal.querySelector('[data-cancel]'), 'click');
  assert.equal(h.page.querySelector('[data-qms-action="approve"]'), null);
  fire(h.page.querySelector('[data-qms-refresh]'), 'click'); await flush();
  assert.ok(h.page.querySelector('[data-qms-action="approve"]'));
});

test('refresh, signout and disposal invalidate detached decision contexts before a command can be sent', async t => {
  for (const mode of ['refresh', 'signout', 'dispose']) await t.test(mode, async child => {
    const h = await pageHarness(child); await h.open(); fire(h.page.querySelector('[data-qms-action="approve"]'), 'click');
    const modal = h.document.querySelector('.modal'); fill(modal, { qmsNote: 'Decision basis', qmsReason: 'Approval reason' });
    if (mode === 'refresh') { fire(h.page.querySelector('[data-qms-refresh]'), 'click'); await flush(); }
    if (mode === 'signout') session.subject = null;
    if (mode === 'dispose') h.controller.dispose();
    fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
    assert.equal(h.calls.filter(call => call.path.endsWith('/approve')).length, 0);
    if (mode !== 'signout') { assert.equal(modal.isConnected, false); assert.equal(h.document.listeners.get('keydown').size, 0); }
  });
});

test('profile form records human scope, explicit relevance and exact governed/document references', async t => {
  const h = await pageHarness(t, { records: [], kind: 'Profile' }), modal = h.create(); header(modal, 'ORG-SCOPE');
  assert.equal(modal.querySelector('#f-climateRelevant').value, '');
  fill(modal, { organizationName: 'Actual organization', scope: 'Precision parts at PLANT', products: 'Machined assemblies\nTest fixtures', climateRelevant: 'false', climateBasis: 'Recorded context assessment' });
  row(modal, 'processes', { code: 'MACHINING', revision: 3 }); row(modal, 'requirements', { code: 'CUSTOMER-REQ', revision: 2 });
  row(modal, 'nonApplicability', { requirementCode: 'DESIGN', revision: 4, basis: 'Build to customer drawings within this scope' });
  row(modal, 'documents', { documentNumber: 'QMS-MANUAL', revision: 'C' });
  fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  const body = h.calls.find(call => call.path === '/api/quality/qms/records' && call.body)?.body;
  assert.equal(body.kind, 'Profile'); assert.equal(body.facilityCode, 'PLANT'); assert.equal(body.programId, null); assert.equal(body.content.profile.climateRelevant, false);
  assert.deepEqual(body.content.profile.products, ['Machined assemblies', 'Test fixtures']); assert.deepEqual(body.content.profile.services, []);
  assert.deepEqual(body.content.profile.processes, [{ code: 'MACHINING', revision: 3 }]); assert.deepEqual(body.content.profile.nonApplicability, [{ requirementCode: 'DESIGN', revision: 4, basis: 'Build to customer drawings within this scope' }]);
  assert.deepEqual(body.content.documents, [{ documentNumber: 'QMS-MANUAL', revision: 'C' }]); assert.equal(body.content.policy, undefined);
});

test('process form requires monitoring and preserves actual owner, inputs, outputs and exact dependencies', async t => {
  const h = await pageHarness(t, { records: [], kind: 'Process' }), modal = h.create(); header(modal, 'MACHINING');
  fill(modal, { name: 'Controlled machining', ownerPersonId: authorId, inputs: 'Released drawing\nReleased stock', outputs: 'Inspected parts', resources: 'Qualified worker\nEligible machine' });
  fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  assert.match(modal.querySelector('[data-qms-error]').textContent, /monitoring definitions/); assert.equal(h.calls.filter(call => call.body).length, 0);
  row(modal, 'monitoring', { name: 'Accepted output', method: 'Review recorded inspection outcomes', dataSource: 'Quality inspection records', reviewInterval: 'Weekly' });
  row(modal, 'dependencies', { code: 'RECEIVING', revision: 2 }); fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  const content = h.calls.find(call => call.body)?.body.content.process;
  assert.equal(content.ownerPersonId, authorId); assert.deepEqual(content.inputs, ['Released drawing', 'Released stock']); assert.deepEqual(content.dependencies, [{ code: 'RECEIVING', revision: 2 }]);
  assert.equal(content.monitoring[0].dataSource, 'Quality inspection records');
});

test('execution policy form never supplies default determinations and sends only selected typed rules', async t => {
  const h = await pageHarness(t, { records: [], kind: 'Policy' }), modal = h.create(); header(modal, 'WORK-POLICY');
  fill(modal, { policyKind: 'Execution', statement: 'Require controlled execution', ownerPersonId: authorId, communicationMethod: 'Shift briefing', reviewInterval: 'Annual', acknowledgmentRequired: 'true' });
  fire(modal.querySelector('#f-policyKind'), 'change');
  assert.equal(modal.querySelector('#f-requireQualification').value, ''); fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  assert.equal(h.calls.filter(call => call.body).length, 0); assert.match(modal.querySelector('[data-qms-error]').textContent, /Require qualification is required/);
  fill(modal, { requireQualification: 'true', requirePresentOperator: 'true', requireReleasedDocuments: 'true', revalidateAtCommit: 'true', extensionEnabled: 'false' }); fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  const policy = h.calls.find(call => call.body)?.body.content.policy;
  assert.deepEqual(policy.rules, { execution: { requireQualification: true, requirePresentOperator: true, requireReleasedDocuments: true, revalidateAtCommit: true } }); assert.equal(policy.acknowledgmentRequired, true);
});

test('retention policy captures explicit evidence retention and bounded raw-attempt limits', async t => {
  const h = await pageHarness(t, { records: [], kind: 'Policy' }), modal = h.create(); header(modal, 'RETENTION');
  fill(modal, { policyKind: 'AttemptRetention', statement: 'Our retention policy', ownerPersonId: authorId, communicationMethod: 'Published procedure', reviewInterval: 'Annual', acknowledgmentRequired: 'false' }); fire(modal.querySelector('#f-policyKind'), 'change');
  fill(modal, { preserveDeniedAttempts: 'true', preserveSourceLink: 'true', neverRewriteAuditChain: 'true', minimumRetentionDays: 365, maximumRawAttemptAgeDays: 14, maximumStoredRawAttempts: 5000, extensionEnabled: 'false' }); fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  assert.deepEqual(h.calls.find(call => call.body)?.body.content.policy.rules, { attemptRetention: { preserveDeniedAttempts: true, preserveSourceLink: true, neverRewriteAuditChain: true, minimumRetentionDays: 365, maximumRawAttemptAgeDays: 14, maximumStoredRawAttempts: 5000 } });
});

test('product release policy keeps the quality release action fixed and its organization determinations explicit', async t => {
  const h = await pageHarness(t, { records: [], kind: 'Policy' }), modal = h.create(); header(modal, 'PRODUCT-RELEASE');
  fill(modal, { policyKind: 'ProductRelease', statement: 'Our product-release policy', ownerPersonId: authorId, communicationMethod: 'Controlled release procedure', reviewInterval: 'Annual', acknowledgmentRequired: 'true' }); fire(modal.querySelector('#f-policyKind'), 'change');
  const action = modal.querySelector('#f-releasePermission');
  assert.equal(action.value, 'quality.product.release'); assert.equal(action.getAttribute('readonly'), '');
  assert.equal(modal.querySelector('#f-requireIndependentDecision').value, '');
  assert.equal(modal.querySelector('#f-requireRequiredInspectionEvidence').value, '');
  assert.equal(modal.querySelector('#f-requireDispositionedNonconformities').value, '');
  fill(modal, { requireIndependentDecision: 'true', requireRequiredInspectionEvidence: 'true', requireDispositionedNonconformities: 'true' });
  action.value = 'inventory.shipping.ship'; fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  assert.deepEqual(h.calls.find(call => call.body)?.body.content.policy.rules, { productRelease: { requireIndependentDecision: true, requireRequiredInspectionEvidence: true, requireDispositionedNonconformities: true, releasePermission: 'quality.product.release' } });
});

test('requirement form separates applicability from licensed verification with exact authorized document revision', async t => {
  const h = await pageHarness(t, { records: [], kind: 'Requirement' }), modal = h.create(); header(modal, 'REQ-1');
  fill(modal, { title: 'Organization requirement', classification: 'ISO', requirementText: 'Human organization interpretation', applicability: 'PendingDetermination', applicabilityBasis: 'Scope review remains pending', ownerPersonId: authorId });
  row(modal, 'standardReferences', { standard: 'ISO 9001', edition: '2026', clause: 'Source reference awaiting authorized verification', verification: 'PendingAuthorizedVerification', verificationBasis: 'Authorized copy not yet verified' });
  fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  const requirement = h.calls.find(call => call.body)?.body.content.requirement;
  assert.equal(requirement.applicability, 'PendingDetermination'); assert.equal(requirement.standardReferences[0].verification, 'PendingAuthorizedVerification'); assert.equal(requirement.standardReferences[0].verificationDocument, null);
  const h2 = await pageHarness(t, { records: [], kind: 'Requirement' }), modal2 = h2.create(); header(modal2, 'REQ-2');
  fill(modal2, { title: 'Customer requirement', classification: 'Customer', requirementText: 'Customer requirement interpretation', applicability: 'Applicable', applicabilityBasis: 'Agreed scope', ownerPersonId: authorId });
  row(modal2, 'standardReferences', { standard: 'Customer specification', edition: '3', clause: 'Customer-specific reference', verification: 'VerifiedAuthorizedCopy', verificationBasis: 'Reviewed licensed controlled source', verificationDocumentNumber: 'CUSTOMER-SPEC', verificationDocumentRevision: 'D' });
  fire(modal2.querySelector('[data-qms-confirm]'), 'click'); await flush();
  assert.deepEqual(h2.calls.find(call => call.body)?.body.content.requirement.standardReferences[0].verificationDocument, { documentNumber: 'CUSTOMER-SPEC', revision: 'D' });
});

test('an ISO revision retains RequiresLicensedVerification beside local approval and effective dates', async t => {
  const r = record('Effective', { kind: 'Requirement', revisions: [revision('Effective', { requiresLicensedVerification: true, effectiveFrom: '2026-10-14T14:00:00Z', content: { requirement: { title: 'Provisional mapping', classification: 'ISO', applicability: 'Applicable', applicabilityBasis: 'Human scope decision', standardReferences: [] }, documents: [] } })] });
  const h = await pageHarness(t, { records: [r], kind: 'Requirement' }); await h.open();
  assert.match(h.page.innerHTML, /RequiresLicensedVerification/); assert.match(h.page.innerHTML, /organization approval does not establish verified standard wording or conformity/);
  assert.match(h.page.innerHTML, /Effective from/); assert.ok(h.page.querySelector('[data-qms-action="new-revision"]'));
});

test('editing a draft uses its captured revision/version and never resubmits immutable scope headers', async t => {
  const h = await pageHarness(t, { records: [record('Draft')] }); await h.open(); fire(h.page.querySelector('[data-qms-action="edit"]'), 'click');
  const modal = h.document.querySelector('.modal'); assert.equal(modal.querySelector('#f-facilityCode'), null); assert.equal(modal.querySelector('#f-programId'), null);
  fill(modal, { statement: 'Updated actual policy', ownerPersonId: authorId, communicationMethod: 'Shift briefing', reviewInterval: 'Annual', acknowledgmentRequired: 'true', requireQualification: 'true', requirePresentOperator: 'true', requireReleasedDocuments: 'true', revalidateAtCommit: 'true', qmsReason: 'Correct the draft definition' });
  // The minimal DOM harness does not initialize textarea values from their text nodes.
  const docRow = modal.querySelector('[data-qms-group="documents"]').querySelector('[data-qms-rows]').children[0]; fill(docRow, { [`${docRow.qmsPrefix}documentNumber`]: 'PROCEDURE-7', [`${docRow.qmsPrefix}revision`]: 'B' });
  fire(modal.querySelector('[data-qms-confirm]'), 'click'); await flush();
  const body = h.calls.find(call => call.path.endsWith('/revisions/2/edit'))?.body;
  assert.equal(body.expectedVersion, 7); assert.equal(body.content.policy.statement, 'Updated actual policy'); assert.equal(body.facilityCode, undefined); assert.equal(body.programId, undefined);
});

test('scope-specific permission hides a decision outside the granted actual facility', async t => {
  const h = await pageHarness(t, { me: { personId: reviewerId, permissions: ['quality.qms.read'], scopedPermissions: [{ permission: 'quality.qms.approve', scopeType: 'facility', scopeId: 'OTHER' }] } }); await h.open();
  assert.equal(h.page.querySelector('[data-qms-action="approve"]'), null); assert.equal(h.page.querySelector('[data-qms-create]'), null);
});

test('an uncertain approval outcome preserves entries, blocks retry and requires record refresh', async t => {
  const h = await pageHarness(t, { route: (path, body) => body && path.endsWith('/approve') ? Promise.reject(new TypeError('Failed to fetch')) : null }); await h.open(); fire(h.page.querySelector('[data-qms-action="approve"]'), 'click');
  const modal = h.document.querySelector('.modal'); fill(modal, { qmsNote: 'Approval basis', qmsReason: 'Recorded decision' }); const submit = modal.querySelector('[data-qms-confirm]'); fire(submit, 'click'); await flush();
  assert.equal(modal.isConnected, true); assert.equal(modal.querySelector('#f-qmsNote').value, 'Approval basis'); assert.equal(modal.querySelector('#f-qmsReason').value, 'Recorded decision');
  assert.equal(submit.disabled, true); assert.match(modal.querySelector('[data-qms-error]').textContent, /entries are preserved/);
  assert.equal(h.calls.filter(call => call.path.endsWith('/approve')).length, 1); assert.equal(h.document.querySelector('.toast'), null);
  fire(modal.querySelector('[data-cancel]'), 'click'); assert.equal(h.page.querySelector('[data-qms-action="approve"]'), null);
  fire(h.page.querySelector('[data-qms-refresh]'), 'click'); await flush(); assert.ok(h.page.querySelector('[data-qms-action="approve"]'));
});
