import assert from 'node:assert/strict';
import test from 'node:test';
import { investigationsPage, findingContent, traceRoot, candidateRows, completenessNotes } from '../../src/Forge.Host/wwwroot/js/pages/investigations.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

const owner = '71656ec5-98b3-4899-9da1-26c7cbe660d0';
const quality = { personId: owner, permissions: ['quality.read', 'quality.plan', 'quality.disposition', 'quality.approve'] };
const candidate = { key: 'lot:BAR-1:L7', category: 'ConsumingOrder', recordType: 'Order', recordId: '501', partNumber: 'BRKT', lotNumber: null, orderNumber: 501, sequence: 20,
  quantity: 4, unit: 'EA', status: 'Released', paths: [{ relationship: 'Consumed', basis: 'Issue transaction 77' }], holdTargets: [{ type: 'Order', id: '501', part: null }] };
const header = (state = 'Open') => ({ investigationId: 9, version: 3, state, root: { kind: 'SupplierLot', partNumber: 'BAR-1', lotNumber: 'L7', findingId: null },
  title: 'Supplier bar L7', ownerPersonId: owner, createdBy: `person:${owner}`, createdAt: '2026-10-14T12:00:00Z', triggerEvidence: 'Supplier notice SN-4', latestSnapshot: 2 });
const view = (assessments = [], extra = {}) => ({ header: header(), narrativeRedacted: false, holdLinks: [], assessments,
  snapshot: { investigationId: 9, number: 2, recordedBy: `person:${owner}`, recordedAt: '2026-10-14T12:05:00Z',
    capture: { dataCutoff: '2026-10-14T12:05:00Z', candidates: [candidate], completeness: { recordedTraversalComplete: true, intervalKnown: false, physicalCoverageKnown: false, restrictedSources: false, concurrentSnapshotUncertain: false, gaps: [{ code: 'interval_unknown', description: 'No suspected start.' }] } } }, ...extra });

test('finding content keeps unknown bounds null and declares a range only with its basis', () => {
  const base = { toolCode: 'MIC-7', calibrationEventId: null, discoveredAt: '2026-10-14T08:00', asFoundObservation: 'Reads 0.02 high', suspectedFrom: '', suspectedTo: null, intervalBasis: 'Last good calibration unknown', evidence: 'Cal cert C-9' };
  const c = findingContent(base);
  assert.equal(c.suspectedFrom, null); assert.equal(c.suspectedTo, null); assert.equal(c.affectedRange, null);
  assert.equal(c.discoveredAt, new Date('2026-10-14T08:00').toISOString());
  assert.throws(() => findingContent({ ...base, rangeFrom: 1, unit: 'mm' }), /basis for the affected range/);
  const ranged = findingContent({ ...base, characteristicName: 'Bore', rangeFrom: 10, rangeTo: 12, unit: 'mm', rangeBasis: 'Bore spec' });
  assert.deepEqual(ranged.affectedRange, { characteristicName: 'Bore', unit: 'mm', from: 10, to: 12, basis: 'Bore spec' });
  assert.throws(() => findingContent({ ...base, discoveredAt: null }), /exact date/);
});

test('a trace root is an exact supplier lot or an approved finding, never both', () => {
  assert.deepEqual(traceRoot({ kind: 'SupplierLot', partNumber: 'BAR-1', lotNumber: 'L7', findingId: '4' }), { kind: 'SupplierLot', partNumber: 'BAR-1', lotNumber: 'L7', findingId: null });
  assert.deepEqual(traceRoot({ kind: 'MeasurementFinding', partNumber: 'BAR-1', findingId: '4' }), { kind: 'MeasurementFinding', partNumber: null, lotNumber: null, findingId: 4 });
  assert.throws(() => traceRoot({ kind: 'SupplierLot', partNumber: 'BAR-1' }), /exact supplier part and lot/);
  assert.throws(() => traceRoot({ kind: 'MeasurementFinding' }), /approved measurement finding/);
});

test('completeness lists unknowns and gaps instead of implying a complete trace', () => {
  const html = completenessNotes(view().snapshot.capture.completeness);
  assert.match(html, /suspect interval is not fully known/); assert.match(html, /Physical coverage beyond the records is unknown/); assert.match(html, /interval_unknown/);
});

test('candidate actions follow state and permission: assess, approve a proposal, link a hold only after approval', () => {
  const proposed = { assessmentId: 31, candidateKey: candidate.key, version: 1, state: 'Proposed', decision: 'PotentiallyAffected', basis: 'Used before notice', plannedDisposition: 'Inspect', proposedBy: `person:${owner}` };
  assert.match(candidateRows(view(), { disposition: true }), /data-propose="lot:BAR-1:L7"/);
  assert.doesNotMatch(candidateRows(view(), { disposition: false }), /data-propose/);
  const pending = candidateRows(view([proposed]), { disposition: true, approve: true });
  assert.match(pending, /data-assess-approve="31"/); assert.doesNotMatch(pending, /data-propose|data-link/);
  const approved = candidateRows(view([{ ...proposed, state: 'Approved', version: 2 }]), { disposition: true });
  assert.match(approved, /data-link="31" data-version="2"/);
  const closed = candidateRows({ ...view([{ ...proposed, state: 'Approved' }]), header: header('Closed') }, { disposition: true, approve: true });
  assert.doesNotMatch(closed, /data-link|data-propose|data-assess/);
});

test('page lists cases, opens one and records an assessment against its latest snapshot', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const previous = { subject: session.subject, token: session.token }; session.subject = 'investigations-test'; session.token = null; t.after(() => Object.assign(session, previous));
  const calls = [];
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body });
    if (body) return Promise.resolve(response({ succeeded: true, message: 'Assessment proposed.', entityId: '31' }));
    if (path.endsWith('/investigations/trace')) return Promise.resolve(response([header()]));
    if (path.includes('/investigations/trace/9')) return Promise.resolve(response(view()));
    if (path.endsWith('/measurement-findings')) return Promise.resolve(response([]));
    if (path.endsWith('/kernel/directory')) return Promise.resolve(response([{ personId: owner, displayName: 'Quinn Quality' }]));
    return Promise.resolve(response([]));
  };
  const controller = await investigationsPage(page, { me: quality, query: new URLSearchParams() }); t.after(controller.dispose);
  assert.match(page.innerHTML, /Supplier bar L7/); assert.match(page.innerHTML, /Supplier lot BAR-1 L7/);
  fire(page.querySelector('[data-case="9"]'), 'click'); await flush(); await flush();
  assert.match(page.innerHTML, /What this snapshot knows/); assert.match(page.innerHTML, /order 501\/20/);
  fire(page.querySelector('[data-propose]'), 'click'); await flush();
  const modal = browser.document.querySelector('.modal');
  for (const [name, value] of Object.entries({ decision: 'NotAffected', basis: 'Lot used after rework', evidence: 'Issue 77', plannedDisposition: 'None' })) modal.querySelector(`#f-${name}`).value = value;
  modal.querySelector('#reason').value = 'Assessed in MRB';
  fire(modal.querySelector('[data-ok]'), 'click'); await flush(); await flush();
  const sent = calls.find(c => c.body && c.path.endsWith('/trace/9/assessments'));
  assert.deepEqual(sent.body, { expectedVersion: 3, snapshotNumber: 2, candidateKey: candidate.key, decision: 'NotAffected', basis: 'Lot used after rework', evidence: 'Issue 77', plannedDisposition: 'None', reason: 'Assessed in MRB' });
});
