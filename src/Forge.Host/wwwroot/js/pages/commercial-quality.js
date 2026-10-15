import { command, tryGet, session } from '../api.js';
import { esc, $, $$, errorBlock, toast } from '../ui.js';

const ROOT = '/api/quality/commercial', enc = encodeURIComponent;
const KINDS = ['CustomerRequirements', 'DesignApplicability', 'ProviderEvaluation', 'PropertyAgreement', 'PropertyIncident', 'Complaint', 'CustomerFeedback', 'PostDelivery', 'Communication'];
const LABEL = value => String(value ?? '').replace(/([a-z])([A-Z])/g, '$1 $2');
const can = (me, permission) => (me?.permissions ?? []).includes(permission);
const templates = {
  CustomerRequirements: { customerRequirements: { contractId: 1, deliveryId: null, partNumber: null, partRevision: null, requirements: [{ code: 'REQ-1', kind: 'Quality', requirement: 'Record the actual customer requirement', acceptanceBasis: 'State the verified acceptance basis', evidence: [] }], applicability: [], enforceAtOrderRelease: true, enforceAtShipment: true, requireIndependentComplaintClosure: true, reviewBasis: 'Record the actual contract review basis', requireCustomerConcessionForNonconformingRelease: false, customerSource: null } },
  DesignApplicability: { designApplicability: { partNumber: 'PART', revision: 'A', applicable: false, determinationBasis: 'Record the actual design applicability decision', controls: [], evidence: [] } },
  ProviderEvaluation: { providerEvaluation: { supplierCode: 'SUPPLIER', scope: { partNumber: null, partRevision: null, processCode: null, workCenterCode: null, enforceAtPurchaseApproval: true, enforceAtExternalDispatch: false, purchasingRequirements: 'Record exact scoped purchasing requirements' }, criteria: [{ code: 'QUALITY', description: 'Actual evaluated requirement', kind: 'Attribute', mandatory: true, weight: 1, measured: null, minimum: null, maximum: null, accepted: true, evidenceBasis: 'Record actual supplier evidence and evaluation' }], minimumWeightedPassFraction: 1, validFrom: '2026-01-01T00:00:00Z', validTo: '2027-01-01T00:00:00Z', reevaluateBy: '2026-12-01T00:00:00Z', evaluationBasis: 'Record the human evaluation', evidence: [{ kind: 'Supplier', key: 'SUPPLIER' }] } },
  PropertyAgreement: { propertyAgreement: { subject: { kind: 'Material', key: 'LOT', partNumber: 'PART' }, titleKind: 'CustomerOwned', legalOwner: { kind: 'Contract', key: '1' }, agreementBasis: 'Record the actual agreement', validFrom: '2026-01-01T00:00:00Z', validTo: '2027-01-01T00:00:00Z', permittedActivities: ['ProductionIssue'], permittedWorkCenters: [], permittedOrders: [], allowConsumptiveUse: true, requireOwnerReturnAcceptance: false, returnTerms: 'Record actual return terms', preservation: [], evidence: [{ kind: 'Contract', key: '1' }] } },
  PropertyIncident: { propertyIncident: { agreementCode: 'PROPERTY', agreementRevision: 1, kind: 'Damage', observedAt: '2026-10-15T00:00:00Z', description: 'Record the actual incident', immediateAction: 'Record the actual containment', evidence: [{ kind: 'Tool', key: 'TOOL' }] } },
  Complaint: { complaint: { shipmentId: 1, shipmentLine: null, reportedProblem: 'Record the actual reported problem', reportedAt: '2026-10-15T00:00:00Z', receivedChannel: 'Recorded channel', recordedContact: 'Recorded contact', investigationOwner: 'Actual Forge person ID', evidence: [] } },
  CustomerFeedback: { customerFeedback: { customerSource: { kind: 'Contract', key: '1' }, kind: 'Other', receivedAt: '2026-10-15T00:00:00Z', method: 'Recorded method', measures: [{ code: 'FEEDBACK', description: 'Actual customer measure', value: null, minimum: null, maximum: null, unit: null, accepted: true, customerProvidedBasis: 'Record the actual customer-provided result' }], recordedFeedback: 'Record the actual feedback', evidence: [] } },
  PostDelivery: { postDelivery: { shipmentId: 1, kind: 'Warranty', caseBasis: 'Record the actual post-delivery case', relatedEvidence: [] } },
  Communication: { communication: { audience: 'Customer', partySource: { kind: 'Contract', key: '1' }, relatedCase: { kind: 'CommercialRecord', key: 'Complaint|CASE', revision: null }, recordedRecipient: 'Recorded intended recipient', subject: 'Proposed communication', body: 'Proposed text for independent approval', proposedChannel: 'Recorded channel', evidence: [], concession: null } },
};
const recordPath = record => ROOT + '/records/' + enc(record.kind) + '/' + enc(record.code) + '/revisions/' + record.revision;
const detailHtml = value => {
  if (value == null || value === '') return '<span class="faint">Not recorded</span>';
  if (Array.isArray(value)) return value.length ? '<ul>' + value.map(detailHtml).map(item => '<li>' + item + '</li>').join('') + '</ul>' : '<span class="faint">No entries</span>';
  if (typeof value === 'object') return '<dl>' + Object.entries(value).map(([name, item]) => '<dt>' + esc(LABEL(name)) + '</dt><dd>' + detailHtml(item) + '</dd>').join('') + '</dl>';
  return '<span>' + esc(value) + '</span>';
};

export async function commercialQualityPage(page, ctx) {
  const auth = { subject: session.subject, token: session.token };
  let disposed = false, generation = 0, data = null, selected = null, error = null, cursor = null;
  const live = token => !disposed && page.isConnected && generation === token && session.subject === auth.subject && session.token === auth.token;
  async function load(after = null) {
    const token = ++generation; cursor = after; selected = null; error = null;
    page.innerHTML = '<div class="muted">Loading authorized customer and provider quality records…</div>';
    const result = await tryGet(ROOT + '/records?take=50' + (after ? '&after=' + enc(after) : ''));
    if (!live(token)) return;
    if (result.error) { error = result.error; data = null; } else data = result.data;
    render();
  }
  async function open(row) {
    const token = ++generation; selected = null; render();
    const result = await tryGet(recordPath(row));
    if (!live(token)) return;
    if (result.error) error = result.error; else { selected = result.data; error = null; }
    render();
  }
  function modal(title, content, onSubmit) {
    const scrim = document.createElement('div'), box = document.createElement('div');
    scrim.className = 'scrim'; box.className = 'modal';
    box.innerHTML = '<h2>' + esc(title) + '</h2>' + content + '<label class="field"><span>Reason recorded in the audit trail</span><textarea class="input" data-reason required></textarea></label><div class="notice bad hidden" role="alert" data-error></div><div class="actions"><button class="btn" data-cancel>Cancel</button><button class="btn primary" data-confirm>Record</button></div>';
    document.body.append(scrim, box); let closed = false, busy = false;
    const close = () => { if (closed) return; closed = true; scrim.remove(); box.remove(); };
    scrim.onclick = close; $('[data-cancel]', box).onclick = close;
    $('[data-confirm]', box).onclick = async () => {
      if (closed || busy) return; busy = true; const button = $('[data-confirm]', box), problem = $('[data-error]', box);
      button.disabled = true; problem.classList.add('hidden');
      try { await onSubmit(box, live(generation)); close(); toast('The commercial quality record was updated.'); await load(); }
      catch (failure) { problem.textContent = failure.message; problem.classList.remove('hidden'); button.disabled = false; busy = false; }
    };
    $('[data-reason]', box).focus();
  }
  const create = () => {
    const kind = KINDS[0];
    modal('Create controlled commercial quality record', '<p class="muted">Use the exact typed owner record and actual source references. JSON is validated by the domain rules; cited evidence must resolve to current owner records.</p>' +
      '<label class="field"><span>Record type</span><select class="input" data-kind>' + KINDS.map(value => '<option value="' + value + '">' + esc(LABEL(value)) + '</option>').join('') + '</select></label>' +
      '<label class="field"><span>Record code</span><input class="input" data-code required maxlength="40"></label>' +
      '<label class="field"><span>Facility code</span><input class="input" data-facility required value="' + esc(ctx.me?.facilityCode ?? '') + '"></label>' +
      '<label class="field"><span>Typed content JSON</span><textarea class="input" data-content rows="16">' + esc(JSON.stringify(templates[kind], null, 2)) + '</textarea></label>',
      async (box, current) => {
        if (!current || !can(ctx.me, 'quality.plan')) throw new Error('A current session and quality planning authority are required.');
        let content; try { content = JSON.parse($('[data-content]', box).value); } catch { throw new Error('Enter valid typed JSON content.'); }
        const code = $('[data-code]', box).value.trim(), facilityCode = $('[data-facility]', box).value.trim();
        if (!code || !facilityCode || !content || typeof content !== 'object') throw new Error('Record code, facility and typed content are required.');
        await command(ROOT + '/records', { kind: $('[data-kind]', box).value, code, facilityCode, content }, $('[data-reason]', box).value.trim());
      });
    const box = document.querySelector('.modal'); $('[data-kind]', box).onchange = () => { const type = $('[data-kind]', box).value; $('[data-content]', box).value = JSON.stringify(templates[type], null, 2); };
  };
  const transition = action => modal(LABEL(action) + ' commercial quality record', '<p class="muted">Current record version ' + selected.version + '. The server rechecks current sources, independence and authority.</p><label class="field"><span>Human decision basis</span><textarea class="input" data-basis required></textarea></label>',
    async (box, current) => {
      if (!current || !selected) throw new Error('Reload the current record before recording a decision.');
      const basis = $('[data-basis]', box).value.trim(), reason = $('[data-reason]', box).value.trim();
      if (!basis || !reason) throw new Error('Record the actual decision basis and audit reason.');
      await command(recordPath(selected) + '/' + action.toLowerCase(), { expectedVersion: selected.version, basis }, reason);
    });
  const observe = () => { modal('Record actual commercial observation', '<p class="muted">Observations are append-only and require exact current evidence references.</p>' +
    '<label class="field"><span>Observation kind</span><select class="input" data-observation><option>Monitoring</option><option>Reassessment</option><option>CorrectiveAction</option><option>Investigation</option><option>Correction</option><option>ComplaintDisposition</option><option>PropertyReceipt</option><option>PropertyUse</option><option>PropertyReturn</option><option>OwnerAcceptance</option><option>PreservationObservation</option><option>CommunicationObserved</option><option>CustomerApprovalReceived</option></select></label>' +
    '<label class="field"><span>Observed at (UTC)</span><input class="input" data-at type="datetime-local" required></label>' +
    '<label class="field"><span>Actual evidence references JSON array</span><textarea class="input" data-evidence rows="5">[]</textarea></label>' +
    '<label class="field"><span>Recorded external person, where applicable</span><input class="input" data-external></label>' +
    '<label class="field"><span>Human observation basis</span><textarea class="input" data-basis required></textarea></label>',
    async (box, current) => {
      if (!current || !selected || !can(ctx.me, 'quality.plan')) throw new Error('A current session and quality planning authority are required.');
      const at = $('[data-at]', box).value, basis = $('[data-basis]', box).value.trim(); let evidence;
      try { evidence = JSON.parse($('[data-evidence]', box).value); } catch { throw new Error('Enter a valid JSON array of evidence references.'); }
      if (!at || !basis || !Array.isArray(evidence)) throw new Error('Observed time, actual evidence and human basis are required.');
      await command(recordPath(selected) + '/observations', { expectedVersion: selected.version, observation: $('[data-observation]', box).value,
        observedAt: new Date(at + ':00Z').toISOString(), basis, evidence, recordedExternalPerson: $('[data-external]', box).value.trim() || null }, $('[data-reason]', box).value.trim());
    });
  $('[data-at]', document.querySelector('.modal')).value = new Date().toISOString().slice(0, 16);
  };
  const actions = record => {
    const result = [], state = record.state, independent = record.createdBy !== 'person:' + ctx.me?.personId && record.lastEditedBy !== 'person:' + ctx.me?.personId;
    if (can(ctx.me, 'quality.plan') && state === 'Draft') result.push('Submit');
    if (can(ctx.me, 'quality.approve') && independent && state === 'Submitted') result.push('Review');
    if (can(ctx.me, record.kind === 'Communication' ? 'quality.communications.approve' : 'quality.approve') && independent && state === 'Reviewed') result.push('Approve');
    if (can(ctx.me, record.kind === 'Communication' ? 'quality.communications.approve' : 'quality.approve') && state === 'Approved') result.push('Effective');
    if (can(ctx.me, 'quality.plan') && !['Withdrawn', 'Superseded', 'Closed'].includes(state)) result.push('Withdraw');
    if (can(ctx.me, 'quality.plan') && state === 'Effective' && ['Complaint', 'PropertyIncident', 'PostDelivery'].includes(record.kind)) result.push('Close');
    if (can(ctx.me, 'quality.plan') && state === 'Effective') result.push('Observation');
    return result;
  };
  function render() {
    if (disposed || !page.isConnected) return;
    const rows = data?.items ?? [];
    const body = error ? errorBlock(error, 'current commercial quality records') : rows.length
      ? '<table class="table"><thead><tr><th>Record</th><th>State</th><th>Revision</th><th>Facility</th><th></th></tr></thead><tbody>' + rows.map(row => '<tr><td>' + esc(LABEL(row.kind)) + ' · ' + esc(row.code) + '</td><td>' + esc(row.state) + '</td><td>' + esc(row.revision) + '</td><td>' + esc(row.facilityCode) + '</td><td><button class="btn small" data-open="' + esc(row.kind) + '|' + esc(row.code) + '|' + row.revision + '">Open</button></td></tr>').join('') + '</tbody></table>'
      : '<div class="empty">No authorized commercial quality records are available in this page.</div>';
    const buttons = selected ? actions(selected).map(action => '<button class="btn" data-action="' + esc(action) + '">' + esc(action) + '</button>').join('') : '';
    page.innerHTML = '<div class="page-head"><div><h1>Customer & provider quality</h1><p class="sub">Control actual customer, provider, property, complaint and post-delivery evidence. Source links retain their owner permissions.</p></div><div class="toolbar">' + (can(ctx.me, 'quality.plan') ? '<button class="btn primary" data-create>Create record</button>' : '') + '<button class="btn" data-refresh>Refresh</button></div></div>' +
      '<section class="panel">' + body + (data?.nextCursor ? '<div class="toolbar"><button class="btn" data-next>Next page</button></div>' : '') + '</section>' +
      (selected ? '<section class="panel"><div class="page-head"><div><h2>' + esc(LABEL(selected.kind)) + ' · ' + esc(selected.code) + '</h2><p class="sub">Revision ' + esc(selected.revision) + ' · version ' + esc(selected.version) + ' · ' + esc(selected.state) + '</p></div><div class="toolbar">' + buttons + '</div></div>' + detailHtml(selected) + '</section>' : '');
    $('[data-create]', page)?.addEventListener('click', create);
    $('[data-refresh]', page)?.addEventListener('click', () => load());
    $('[data-next]', page)?.addEventListener('click', () => load(data.nextCursor));
    $$('[data-open]', page).forEach(button => button.onclick = () => { const [kind, code, revision] = button.dataset.open.split('|'); open({ kind, code, revision }); });
    $$('[data-action]', page).forEach(button => button.onclick = () => button.dataset.action === 'Observation' ? observe() : transition(button.dataset.action));
  }
  await load();
  return { dispose() { disposed = true; generation++; page.innerHTML = ''; } };
}
