import assert from 'node:assert/strict';
import test from 'node:test';
import { mountLabelsPanel, materialIdentificationCode } from '../../src/Forge.Host/wwwroot/js/identification-labels.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, deferred, flush, fire } from './browser-harness.mjs';

const personId = 'a7a35f21-3b44-42fd-935d-1c1461669520';
const permissions = ['identification.read', 'identification.labels.manage', 'identification.labels.approve', 'identification.labels.print', 'identification.aliases.manage'];
const config = { namespace: 'configured-plant', maxKeyLength: 40, maxCodeLength: 1024, supportedKinds: ['order', 'operation', 'material', 'location'], version: 'v1' };
const subjectCode = 'forge:v1:configured-plant:material:PART-A:LOT-7';
const subject = { code: subjectCode, namespace: config.namespace, kind: 'material', partNumber: 'PART-A', lotNumber: 'LOT-7', revision: 'A', status: 'Recorded', identityKind: 'Roll' };
const template = { code: 'ROLL-LABEL', revision: 2, version: 9, state: 'Approved', width: 400, height: 180, allowedKinds: ['material'], allowedSymbologies: ['Code128', 'QR'], facilityCode: 'PLANT', documents: [{ documentNumber: 'LABEL-PROCEDURE', revision: 'B' }], createdBy: 'person:another-author', lastEditedBy: 'person:another-author' };
const label = { labelId: 'label-1', issuanceId: 'issuance-1', code: 'forge:v1:configured-plant:label:label-1', subjectCode, subject, templateCode: template.code, templateVersion: 2, symbology: 'Code128', facilityCode: 'PLANT', state: 'Active', version: 3, createdBy: `person:${personId}`, createdAt: '2026-10-14T13:00:00Z', relationship: 'Original', policyCode: 'COPY-POLICY', policyRevision: 4 };
const attempt = { attemptId: 'attempt-1', labelId: label.labelId, version: 1, state: 'Requested', requestedBy: `person:${personId}`, requestedAt: '2026-10-14T13:01:00Z', artifactSha256: 'recorded-artifact-hash' };
const alias = { aliasId: 'alias-1', domain: 'Supplier', alias: 'supplier/raw-value', code: 'forge:v1:configured-plant:alias:alias-1', subjectCode, subject, facilityCode: 'PLANT', state: 'Active', version: 5, createdBy: `person:${personId}` };
const policy = { namespace: config.namespace, geometryUnit: 'pixels', supportedSymbologies: ['Code128', 'QR', 'DataMatrix'], printerMode: 'BrowserObserved', trustedHardware: false, policy: { code: 'COPY-POLICY', revision: 4, fingerprint: 'policy-fingerprint', activeCopyMode: 'MultipleActive', maximumActiveCopies: 3, requiredConfirmation: 'OperatorObservedPrinting', requireCopyAuthorization: true, requireIndependentCopyAuthorization: false, allowReprint: true, allowReplacement: true, allowVoid: true, allowAliasCreate: true, allowAliasReplacement: true, allowAliasVoid: true, allowedTemplates: [{ code: template.code, version: 2 }], allowedSymbologies: ['Code128', 'QR'], allowedSubjectKinds: ['material'] } };

async function harness(t, { route = () => null, labelView = label, attempts = [attempt], currentSubject = null, actions = ['Reprint', 'Replace', 'Void', 'RequestPrint'], policyAvailable = true } = {}) {
  const browser = installBrowser(t), calls = [], printed = [], element = browser.document.createElement('section'); browser.document.body.append(element);
  const previous = { subject: session.subject, token: session.token }; session.subject = 'label-test-user'; session.token = null; t.after(() => Object.assign(session, previous));
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body });
    const override = route(path, body); if (override) return Promise.resolve(override);
    if (path === '/api/identification/config') return Promise.resolve(response(config));
    if (path === '/api/identification/resolve') return Promise.resolve(response(subject));
    if (path.startsWith('/api/identification/labels/config?')) return Promise.resolve(response(policy));
    if (path.startsWith('/api/identification/labels?')) return Promise.resolve(response({ items: [labelView], nextCursor: null }));
    if (path.startsWith('/api/identification/aliases?')) return Promise.resolve(response({ items: [alias], nextCursor: null }));
    if (path === '/api/identification/templates') return Promise.resolve(response({ items: [template], nextCursor: null }));
    if (path === '/api/identification/labels/label-1') return Promise.resolve(response({ label: labelView, currentSubject, transitions: [{ action: 'Create', version: 1, actor: label.createdBy, at: label.createdAt, toState: 'Active', reason: 'Recorded issuance' }], printAttempts: attempts, allowedActions: actions, policy: { available: policyAvailable } }));
    if (path.endsWith('/artifact')) return Promise.resolve({ status: 200, ok: true, text: async () => '<svg xmlns="http://www.w3.org/2000/svg"><rect width="100" height="30"/></svg>' });
    return Promise.resolve(response({ message: 'Recorded.', entityId: 'new-presentation-record' }));
  };
  const controller = await mountLabelsPanel(element, { me: { personId, permissions }, partNumber: 'PART-A', lotNumber: 'LOT-7', facilityCode: 'PLANT', printArtifact: url => printed.push(url) }); t.after(controller.dispose);
  return { ...browser, element, calls, printed, controller, open: async () => { fire(element.querySelector('[data-label-open]'), 'click'); await flush(); } };
}
function fill(modal, values) { for (const [name, value] of Object.entries(values)) modal.querySelector(`#f-${name}`).value = value; }
const changes = calls => calls.filter(call => call.body && call.path !== '/api/identification/resolve');

test('material label identity uses configured namespace and strict escaping without changing either key', () => {
  assert.equal(materialIdentificationCode(config, 'PART:(A)/100%+', "LOT'7"), 'forge:v1:configured-plant:material:PART%3A%28A%29%2F100%25%2B:LOT%277');
  const long = materialIdentificationCode(config, '中'.repeat(40), '中'.repeat(40)); assert.ok(long.length > 256); assert.ok(long.length <= 1024);
  assert.throws(() => materialIdentificationCode({ ...config, namespace: '' }, 'PART', 'LOT'), /not configured/);
  assert.throws(() => materialIdentificationCode(config, 'PART A', 'LOT'), /not valid/);
  assert.throws(() => materialIdentificationCode(config, 'PART\u0081', 'LOT'), /not valid/);
  assert.equal(materialIdentificationCode(config, 'PART\ufeff', 'LOT'), 'forge:v1:configured-plant:material:PART%EF%BB%BF:LOT');
});

test('label panel displays the recorded source revision, exact template and current adopted policy without a stock transaction', async t => {
  const h = await harness(t, { route: path => path === '/api/identification/resolve' ? response({ ...subject, revision: 'B' }) : null });
  assert.match(h.element.innerHTML, /revision A/); assert.match(h.element.innerHTML, /ROLL-LABEL revision 2/); assert.match(h.element.innerHTML, /COPY-POLICY revision 4/);
  assert.match(h.element.innerHTML, /hardware is not attested/); assert.equal(changes(h.calls).length, 0); assert.equal(h.calls.some(call => call.path.startsWith('/api/inventory/')), false);
});

test('issuance submits the physical subject and governed template revision rather than its optimistic version', async t => {
  const h = await harness(t); fire(h.element.querySelector('[data-label-issue]'), 'click'); const modal = h.document.querySelector('.modal');
  fill(modal, { facilityCode: 'PLANT', template: '0', symbology: 'QR', copies: 2, labelReason: 'Two approved copies for the existing roll' });
  fire(modal.querySelector('[data-label-confirm]'), 'click'); fire(modal.querySelector('[data-label-confirm]'), 'click'); await flush();
  assert.deepEqual(changes(h.calls).map(call => ({ path: call.path, body: call.body })), [{ path: '/api/identification/labels', body: { subjectCode, facilityCode: 'PLANT', templateCode: 'ROLL-LABEL', templateVersion: 2, symbology: 'QR', copies: 2, reason: 'Two approved copies for the existing roll' } }]);
  assert.equal(modal.isConnected, false); assert.equal(h.document.listeners.get('keydown').size, 0);
});

test('reprint, replacement and void submit the existing label version and never create inventory', async t => {
  for (const action of ['reprint', 'replace', 'void']) await t.test(action, async child => {
    const h = await harness(child); await h.open(); fire(h.element.querySelector(`[data-label-action="${action}"]`), 'click');
    const modal = h.document.querySelector('.modal'); fill(modal, { labelReason: `Controlled ${action} reason` }); fire(modal.querySelector('[data-label-confirm]'), 'click'); await flush();
    assert.deepEqual(changes(h.calls).map(call => call.body), [{ expectedVersion: 3, reason: `Controlled ${action} reason` }]);
    assert.equal(h.calls.some(call => call.path.startsWith('/api/inventory/')), false);
  });
});

test('copy approval follows actual server capabilities and can permit the issuer when independence is not required', async t => {
  const h = await harness(t, { labelView: { ...label, state: 'PendingApproval' }, actions: ['Approve', 'Void'] }); await h.open();
  assert.ok(h.element.querySelector('[data-label-action="approve"]')); fire(h.element.querySelector('[data-label-action="approve"]'), 'click');
  const modal = h.document.querySelector('.modal'); fill(modal, { labelReason: 'Current policy permits authorized issuer approval' }); fire(modal.querySelector('[data-label-confirm]'), 'click'); await flush();
  assert.equal(changes(h.calls)[0].path, '/api/identification/labels/label-1/approve');
});

test('historical labels remain readable with no consequential controls when current policy is unavailable', async t => {
  const h = await harness(t, { actions: [], policyAvailable: false }); await h.open();
  assert.match(h.element.innerHTML, /historical label remains readable/); assert.equal(h.element.querySelector('[data-label-action]'), null); assert.equal(h.element.querySelector('[data-label-request-print]'), null);
  assert.equal(changes(h.calls).length, 0);
});

test('recording a browser print attempt requires an explicit reason and does not confirm its outcome', async t => {
  const h = await harness(t); await h.open(); fire(h.element.querySelector('[data-label-request-print]'), 'click');
  const modal = h.document.querySelector('.modal'); fill(modal, { labelReason: 'Request printable artifact for this recorded copy' }); fire(modal.querySelector('[data-label-confirm]'), 'click'); await flush();
  assert.deepEqual(changes(h.calls).map(call => call.body), [{ expectedVersion: 3, printerCode: null, reason: 'Request printable artifact for this recorded copy' }]);
  assert.equal(h.calls.some(call => call.path.endsWith('/outcome')), false);
});

test('artifact viewing and browser printing never record a fabricated confirmation or provider acknowledgement', async t => {
  const h = await harness(t); await h.open(); fire(h.element.querySelector('[data-label-artifact]'), 'click'); await flush();
  const modal = h.document.querySelector('.modal'); assert.match(modal.innerHTML, /recorded-artifact-hash/); assert.equal(modal.querySelector('img').getAttribute('alt'), 'Recorded barcode and human-readable subject');
  fire(modal.querySelector('[data-artifact-print]'), 'click'); assert.equal(h.printed.length, 1);
  assert.match(modal.querySelector('[data-artifact-observation]').textContent, /recorded outcome remains unchanged/); assert.equal(changes(h.calls).length, 0);
});

test('human print reconciliation requires separate explicit printed and attached observations', async t => {
  const h = await harness(t); await h.open(); fire(h.element.querySelector('[data-label-reconcile]'), 'click');
  const modal = h.document.querySelector('.modal'); assert.equal(modal.querySelector('#f-outcome').value, ''); assert.equal(modal.querySelector('#f-observedPrinted').value, ''); assert.equal(modal.querySelector('#f-observedAttached').value, '');
  assert.doesNotMatch(modal.innerHTML, /value="SpoolAccepted"|value="Sent"/);
  fill(modal, { outcome: 'Confirmed', observedPrinted: 'true', observedAttached: 'false', observation: 'Observed the paper output; it has not been attached', labelReason: 'Reconcile the manual print attempt' }); fire(modal.querySelector('[data-label-confirm]'), 'click'); await flush();
  assert.deepEqual(changes(h.calls).map(call => call.body), [{ expectedVersion: 1, outcome: 'Confirmed', observedPrinted: true, observedAttached: false, observation: 'Observed the paper output; it has not been attached', reason: 'Reconcile the manual print attempt' }]);
});

test('alias replacement and void preserve the old exact mapping and capture its current version', async t => {
  for (const action of ['replace', 'void']) await t.test(action, async child => {
    const h = await harness(child); fire(h.element.querySelector(`[data-alias-${action}]`), 'click'); const modal = h.document.querySelector('.modal'); fill(modal, { labelReason: `Controlled alias ${action}` }); fire(modal.querySelector('[data-label-confirm]'), 'click'); await flush();
    assert.deepEqual(changes(h.calls).map(call => call.body), [{ expectedVersion: 5, ...(action === 'replace' ? { subjectCode, facilityCode: 'PLANT' } : {}), reason: `Controlled alias ${action}` }]);
    assert.equal(h.calls.some(call => call.path.startsWith('/api/inventory/')), false);
  });
});

test('confirmed printing can receive a later explicit attachment observation on the same attempt', async t => {
  const h = await harness(t, { attempts: [{ ...attempt, state: 'Confirmed', version: 2, observedPrinted: true, observedAttached: false }] }); await h.open();
  fire(h.element.querySelector('[data-label-reconcile]'), 'click'); const modal = h.document.querySelector('.modal');
  assert.match(modal.innerHTML, /observed attachment of the recorded print/); assert.doesNotMatch(modal.innerHTML, /value="Failed"|value="Uncertain"/);
  assert.equal(modal.querySelector('#f-observedAttached').value, '');
  fill(modal, { outcome: 'Confirmed', observedPrinted: 'true', observedAttached: 'true', observation: 'Observed this printed copy attached to the recorded roll', labelReason: 'Record the separate attachment fact' }); fire(modal.querySelector('[data-label-confirm]'), 'click'); await flush();
  assert.deepEqual(changes(h.calls).map(call => ({ path: call.path, body: call.body })), [{ path: '/api/identification/print-attempts/attempt-1/outcome', body: { expectedVersion: 2, outcome: 'Confirmed', observedPrinted: true, observedAttached: true, observation: 'Observed this printed copy attached to the recorded roll', reason: 'Record the separate attachment fact' } }]);
  assert.equal(h.calls.some(call => call.path.endsWith('/print-attempts')), false);
});

test('final confirmed attachment and failed print attempts cannot offer another reconciliation command', async t => {
  const h = await harness(t, { attempts: [{ ...attempt, state: 'Confirmed', observedPrinted: true, observedAttached: true }, { ...attempt, attemptId: 'failed-attempt', state: 'Failed', observedPrinted: false, observedAttached: false }] }); await h.open();
  assert.equal(h.element.querySelector('[data-label-reconcile]'), null); assert.equal(changes(h.calls).length, 0);
});

test('a retained printed subject and changed current owner are shown separately with server-controlled replacement', async t => {
  const h = await harness(t, { currentSubject: { ...subject, revision: 'B' }, actions: ['Replace', 'Void'] }); await h.open();
  assert.match(h.element.textContent, /Recorded printed subject:.*revision A/); assert.match(h.element.textContent, /Current owner:.*revision B/);
  assert.equal(h.element.querySelector('[data-label-action="reprint"]'), null); assert.equal(h.element.querySelector('[data-label-request-print]'), null);
  fire(h.element.querySelector('[data-label-action="replace"]'), 'click'); assert.match(h.document.querySelector('.modal').textContent, /current owner configuration/); assert.equal(changes(h.calls).length, 0);
});

test('bounded lists send the returned visible cursor as after and keep the earlier visible records', async t => {
  const secondAlias = { ...alias, aliasId: 'alias-2', alias: 'second visible mapping' };
  const h = await harness(t, { route: path => path.startsWith('/api/identification/aliases?') ? response({ items: path.includes('&after=alias-1') ? [secondAlias] : [alias], nextCursor: path.includes('&after=alias-1') ? null : 'alias-1' }) : null });
  const button = [...h.element.querySelectorAll('button')].find(button => button.textContent === 'Load more alias mappings'); assert.ok(button); fire(button, 'click'); await flush();
  assert.ok(h.calls.some(call => call.path === `/api/identification/aliases?subjectCode=${encodeURIComponent(subjectCode)}&after=alias-1`));
  assert.match(h.element.textContent, /supplier\/raw-value/); assert.match(h.element.textContent, /second visible mapping/); assert.equal(changes(h.calls).length, 0);
});

test('a recorded replacement opens the newly issued copy using the returned issuance id', async t => {
  const fresh = { ...label, labelId: 'label-2', issuanceId: 'issuance-2', code: 'NEW-COPY', previousLabelId: label.labelId, relationship: 'Replacement', version: 1 };
  const h = await harness(t, { route: (path, body) => path.endsWith('/replace') && body ? response({ message: 'Replacement issued.', entityId: 'issuance-2' }) : path === '/api/identification/labels/issuances/issuance-2' ? response({ issuance: { issuanceId: 'issuance-2' }, copies: [fresh] }) : path === '/api/identification/labels/label-2' ? response({ label: fresh, transitions: [], printAttempts: [], allowedActions: ['RequestPrint'], policy: { available: true } }) : null });
  await h.open(); fire(h.element.querySelector('[data-label-action="replace"]'), 'click'); const modal = h.document.querySelector('.modal'); fill(modal, { labelReason: 'Replace damaged existing presentation' }); fire(modal.querySelector('[data-label-confirm]'), 'click'); await flush();
  assert.ok(h.calls.some(call => call.path === '/api/identification/labels/issuances/issuance-2')); assert.match(h.element.textContent, /Label NEW-COPY/); assert.equal(changes(h.calls).length, 1);
});

test('uncertain command outcome closes the dialog and requires record reconciliation without an automatic retry', async t => {
  const h = await harness(t, { route: (path, body) => body && path.endsWith('/replace') ? Promise.reject(new TypeError('Failed to fetch')) : null }); await h.open(); fire(h.element.querySelector('[data-label-action="replace"]'), 'click');
  const modal = h.document.querySelector('.modal'); fill(modal, { labelReason: 'Replace damaged label' }); fire(modal.querySelector('[data-label-confirm]'), 'click'); await flush();
  assert.equal(modal.isConnected, false); assert.equal(changes(h.calls).length, 1); assert.match(h.document.querySelector('.toast').textContent, /Check the label record before retrying/); assert.equal(h.document.listeners.get('keydown').size, 0);
});

test('refresh, signout and disposal make detached label commands ineffective', async t => {
  for (const mode of ['refresh', 'signout', 'dispose']) await t.test(mode, async child => {
    const h = await harness(child); await h.open(); fire(h.element.querySelector('[data-label-action="void"]'), 'click'); const modal = h.document.querySelector('.modal'); fill(modal, { labelReason: 'Void with recorded reason' });
    if (mode === 'refresh') await h.controller.refresh(); if (mode === 'signout') session.subject = null; if (mode === 'dispose') h.controller.dispose();
    fire(modal.querySelector('[data-label-confirm]'), 'click'); await flush(); assert.equal(changes(h.calls).length, 0);
    if (mode !== 'signout') assert.equal(h.document.listeners.get('keydown').size, 0);
  });
});

test('a late label detail cannot overwrite a newer selection', async t => {
  const pending = deferred(); let oldRead = true;
  const h = await harness(t, { route: path => path === '/api/identification/labels/label-1' && oldRead ? (oldRead = false, pending.promise) : null });
  fire(h.element.querySelector('[data-label-open]'), 'click'); await flush(); await h.controller.refresh();
  pending.resolve(response({ label: { ...label, code: 'STALE-LABEL' }, transitions: [], printAttempts: [], allowedActions: ['Void'], policy: { available: true } })); await flush();
  assert.doesNotMatch(h.element.innerHTML, /STALE-LABEL/); assert.equal(changes(h.calls).length, 0);
});
