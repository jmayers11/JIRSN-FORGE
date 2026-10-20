import { command, tryGet, session } from '../api.js';
import { esc, $, $$, errorBlock, toast, manageDialogFocus } from '../ui.js';
import { fieldHtml, readFields, bindRepeaters } from '../forms.js';

const ROOT = '/api/quality/commercial', enc = encodeURIComponent;
const KINDS = ['CustomerRequirements', 'DesignApplicability', 'ProviderEvaluation', 'PropertyAgreement', 'PropertyIncident', 'Complaint', 'CustomerFeedback', 'PostDelivery', 'Communication'];
const LABEL = value => String(value ?? '').replace(/([a-z])([A-Z])/g, '$1 $2');
const can = (me, permission) => (me?.permissions ?? []).includes(permission);
const REFERENCE_KINDS = ['Supplier', 'Contract', 'Delivery', 'PartRevision', 'DocumentRevision', 'EngineeringChange', 'PurchaseOrder', 'Receipt', 'Shipment', 'CustomerReturn', 'Order', 'Lot', 'Tool', 'Machine', 'AcceptanceInspection', 'ProductReleaseDecision', 'Ncr', 'Capa', 'CommercialRecord', 'Location', 'InventoryTransaction', 'ToolEvent', 'Item', 'WorkCenter', 'QmsRevision', 'Inspection', 'Hold', 'Program'];
const referenceOptions = REFERENCE_KINDS.map(value => [value, LABEL(value)]);
const selects = (name, label, options, extra = {}) => ({ name, label, type: 'select', options: options.map(value => Array.isArray(value) ? value : [value, LABEL(value)]), ...extra });
const text = (name, label, extra = {}) => ({ name, label, ...extra });
const number = (name, label, extra = {}) => ({ name, label, type: 'number', min: 0, ...extra });
const referenceFields = (prefix, label, required = false) => [selects(prefix + 'Kind', label + ' source type', referenceOptions, { blank: 'Choose a source type', required }), text(prefix + 'Key', label + ' source key', { required }), number(prefix + 'LineNumber', label + ' line number', { min: 1 }), text(prefix + 'Revision', label + ' source revision')];
const referenceValue = (values, prefix) => {
  const kind = values[prefix + 'Kind'], key = values[prefix + 'Key'];
  if (!kind && !key) return null;
  if (!kind || !key) throw new Error(`Complete both the ${LABEL(prefix)} source type and source key.`);
  return { kind, key, lineNumber: values[prefix + 'LineNumber'] ?? null, revision: values[prefix + 'Revision'] ?? null };
};
const referenceRows = (name, label, required = false) => ({ name, label, type: 'repeater', required, minRows: required ? 1 : 0, rowLabel: 'source reference',
  columns: [{ name: 'kind', label: 'Source type', type: 'select', required: true, blank: 'Choose type', options: referenceOptions }, { name: 'key', label: 'Source key', required: true },
    { name: 'lineNumber', label: 'Line number', type: 'number', min: 1 }, { name: 'revision', label: 'Revision' }] });
const stringRows = (name, label, required = false) => ({ name, label, type: 'repeater', required, minRows: required ? 1 : 0, rowLabel: 'entry', columns: [{ name: 'value', label, required: true }] });
const dateField = (name, label) => text(name, label, { type: 'datetime-local', required: true, hint: 'Enter the actual UTC date and time.' });
const instant = value => { if (!value) throw new Error('Enter the actual date and time.'); return new Date(value.length === 16 ? `${value}:00Z` : `${value}Z`).toISOString(); };
const mountStructuredFields = (root, fields) => {
  fields.filter(field => field.type === 'repeater').forEach(field => { const repeater = $('#f-' + field.name, root); if (repeater) repeater.repeaterField = field; });
  bindRepeaters(root);
};

export function commercialFields(kind, people = []) {
  const refs = [referenceRows('evidence', 'Actual source evidence')];
  if (kind === 'CustomerRequirements') return [number('contractId', 'Actual contract ID', { required: false }), number('deliveryId', 'Actual delivery ID', { required: false }), text('partNumber', 'Part number'), text('partRevision', 'Part revision'),
    { name: 'requirements', label: 'Customer requirements', type: 'repeater', required: true, minRows: 1, rowLabel: 'requirement', columns: [text('code', 'Requirement code', { required: true }), selects('kind', 'Requirement kind', ['Product', 'Contract', 'Delivery', 'Quality', 'Regulatory', 'Configuration', 'SpecialCharacteristic', 'Acceptance', 'Preservation', 'Communication'], { required: true }), text('requirement', 'Actual requirement', { required: true }), text('acceptanceBasis', 'Acceptance basis', { required: true })] },
    referenceRows('applicability', 'Applicability reference'), ...referenceFields('customerSource', 'Customer source'),
    { name: 'enforceAtOrderRelease', type: 'check', label: 'Enforce at order release', text: 'Require these controls before order release' },
    { name: 'enforceAtShipment', type: 'check', label: 'Enforce at shipment', text: 'Require these controls before shipment' },
    { name: 'requireIndependentComplaintClosure', type: 'check', label: 'Independent complaint closure', text: 'Require independent complaint closure' },
    text('reviewBasis', 'Actual contract review basis', { type: 'textarea', required: true }),
    { name: 'requireCustomerConcessionForNonconformingRelease', type: 'check', label: 'Customer concession', text: 'Require customer concession for nonconforming release' }, ...refs];
  if (kind === 'DesignApplicability') return [text('partNumber', 'Part number', { required: true }), text('revision', 'Exact part revision', { required: true }),
    { name: 'applicable', type: 'check', label: 'Applicability', text: 'This design applies to the stated part revision' }, text('determinationBasis', 'Actual determination basis', { type: 'textarea', required: true }),
    { name: 'controls', label: 'Design control assessments', type: 'repeater', rowLabel: 'control assessment', columns: [selects('stage', 'Control stage', ['Planning', 'Inputs', 'Review', 'Verification', 'Validation', 'Outputs', 'Changes'], { required: true }), text('basis', 'Actual assessment basis', { required: true })] }, ...refs];
  if (kind === 'ProviderEvaluation') return [text('supplierCode', 'Supplier code', { required: true }), text('partNumber', 'Scoped part number'), text('partRevision', 'Scoped part revision'), text('processCode', 'Scoped process code'), text('workCenterCode', 'Scoped work center'),
    { name: 'enforceAtPurchaseApproval', type: 'check', label: 'Purchase approval', text: 'Enforce at purchase approval' }, { name: 'enforceAtExternalDispatch', type: 'check', label: 'External dispatch', text: 'Enforce at external dispatch' },
    text('purchasingRequirements', 'Exact purchasing requirements', { type: 'textarea' }),
    { name: 'criteria', label: 'Provider evaluation criteria', type: 'repeater', required: true, minRows: 1, rowLabel: 'criterion', columns: [text('code', 'Criterion code', { required: true }), text('description', 'Actual requirement', { required: true }), selects('kind', 'Criterion type', ['Numeric', 'Attribute'], { required: true }), { name: 'mandatory', label: 'Mandatory', type: 'check' }, number('weight', 'Weight', { min: 0, required: true }), number('measured', 'Measured result'), number('minimum', 'Minimum'), number('maximum', 'Maximum'), selects('accepted', 'Attribute result', [['', 'Not applicable'], ['true', 'Accepted'], ['false', 'Not accepted']]), text('evidenceBasis', 'Actual evidence basis', { required: true })] },
    number('minimumWeightedPassFraction', 'Minimum weighted pass fraction', { min: 0, max: 1, step: 'any', required: true }), dateField('validFrom', 'Valid from (UTC)'), dateField('validTo', 'Valid to (UTC)'), dateField('reevaluateBy', 'Reevaluate by (UTC)'), text('evaluationBasis', 'Human evaluation basis', { type: 'textarea', required: true }), ...refs];
  if (kind === 'PropertyAgreement') return [selects('subjectKind', 'Property subject type', ['Tool', 'Machine', 'Material'], { required: true }), text('subjectKey', 'Property subject key', { required: true }), text('subjectPartNumber', 'Part number, if material'), selects('titleKind', 'Legal title', ['CustomerOwned', 'SupplierConsigned', 'Loaned'], { required: true }), ...referenceFields('legalOwner', 'Legal owner', true),
    text('agreementBasis', 'Actual agreement basis', { type: 'textarea', required: true }), dateField('validFrom', 'Valid from (UTC)'), dateField('validTo', 'Valid to (UTC)'),
    stringRows('permittedActivities', 'Permitted activity', true), stringRows('permittedWorkCenters', 'Permitted work center'), { name: 'permittedOrders', label: 'Permitted orders', type: 'repeater', rowLabel: 'order', columns: [number('orderNumber', 'Order number', { min: 1, required: true })] },
    { name: 'allowConsumptiveUse', type: 'check', label: 'Consumptive use', text: 'Allow consumptive use' }, { name: 'requireOwnerReturnAcceptance', type: 'check', label: 'Owner return acceptance', text: 'Require owner return acceptance' }, text('returnTerms', 'Actual return terms', { type: 'textarea', required: true }),
    { name: 'preservation', label: 'Preservation controls', type: 'repeater', rowLabel: 'preservation control', columns: [text('code', 'Control code', { required: true }), selects('kind', 'Control type', ['Handling', 'Storage', 'Packaging', 'Contamination', 'ShelfLife', 'Environment', 'Transportation'], { required: true }), text('instructions', 'Actual instructions', { required: true }), number('minimum', 'Minimum'), number('maximum', 'Maximum'), text('unit', 'Unit')] }, ...refs];
  if (kind === 'PropertyIncident') return [text('agreementCode', 'Property agreement code', { required: true }), number('agreementRevision', 'Exact agreement revision', { min: 1, required: true }), selects('incidentKind', 'Incident kind', ['Loss', 'Damage', 'Unsuitability', 'PreservationFailure'], { required: true }), dateField('observedAt', 'Observed at (UTC)'), text('description', 'Actual incident', { type: 'textarea', required: true }), text('immediateAction', 'Actual containment action', { type: 'textarea', required: true }), ...refs];
  if (kind === 'Complaint') return [number('shipmentId', 'Actual shipment ID', { min: 1, required: true }), number('shipmentLine', 'Shipment line, if applicable', { min: 1 }), text('reportedProblem', 'Reported problem', { type: 'textarea', required: true }), dateField('reportedAt', 'Reported at (UTC)'), text('receivedChannel', 'Recorded channel', { required: true }), text('recordedContact', 'Recorded contact', { required: true }),
    { name: 'investigationOwner', label: 'Investigation owner', type: 'lookup', required: true, options: people.map(person => [person.personId, `${person.displayName} · ${person.personId}`]), hint: 'Choose an active Forge person from the directory.' }, ...refs];
  if (kind === 'CustomerFeedback') return [...referenceFields('customerSource', 'Customer source', true), selects('feedbackKind', 'Feedback kind', ['Scorecard', 'Survey', 'SupplierRating', 'DeliveryEvaluation', 'ProductAcceptance', 'Other'], { required: true }), dateField('receivedAt', 'Received at (UTC)'), text('method', 'Recorded method', { required: true }),
    { name: 'measures', label: 'Customer measures', type: 'repeater', required: true, minRows: 1, rowLabel: 'measure', columns: [text('code', 'Measure code', { required: true }), text('description', 'Actual measure', { required: true }), number('value', 'Recorded value'), number('minimum', 'Minimum'), number('maximum', 'Maximum'), text('unit', 'Unit'), selects('accepted', 'Acceptance', [['', 'Not assessed'], ['true', 'Accepted'], ['false', 'Not accepted']]), text('customerProvidedBasis', 'Customer-provided basis', { required: true })] },
    text('recordedFeedback', 'Actual recorded feedback', { type: 'textarea', required: true }), ...refs];
  if (kind === 'PostDelivery') return [number('shipmentId', 'Actual shipment ID', { min: 1, required: true }), selects('postDeliveryKind', 'Post-delivery case kind', ['Warranty', 'FieldFailure', 'ProductInvestigation', 'RecallReview', 'ReturnReview', 'CorrectiveActionReview'], { required: true }), text('caseBasis', 'Actual case basis', { type: 'textarea', required: true }), referenceRows('relatedEvidence', 'Related source evidence')];
  if (kind === 'Communication') return [selects('audience', 'Audience', ['Customer', 'Supplier'], { required: true }), ...referenceFields('partySource', 'Customer or supplier source', true), ...referenceFields('relatedCase', 'Related case', true),
    text('recordedRecipient', 'Recorded intended recipient', { required: true }), text('subject', 'Proposed subject', { required: true }), text('body', 'Proposed message', { type: 'textarea', required: true }), text('proposedChannel', 'Proposed channel', { required: true }), ...refs,
    { name: 'hasConcession', type: 'check', label: 'Customer concession', text: 'This communication includes a customer concession request' },
    text('concessionPartNumber', 'Concession part number'), text('concessionLotNumber', 'Concession lot number'), text('concessionLocationCode', 'Concession location code'), number('concessionReturnCohortId', 'Return cohort ID, if applicable', { min: 1 }), text('concessionRevision', 'Part revision'), number('concessionNcrId', 'Actual NCR ID', { min: 1 }), number('concessionQuantity', 'Concession quantity', { min: 0, step: 'any' }), selects('concessionDisposition', 'Proposed disposition', ['UseAsIs', 'Rework', 'Scrap', 'ReturnToSupplier', 'Repair'], { blank: 'Choose disposition' })];
  throw new Error('Choose a supported commercial quality record type.');
}

export function commercialContent(kind, v) {
  const evidence = v.evidence ?? [], ref = prefix => referenceValue(v, prefix);
  if (kind === 'CustomerRequirements') return { customerRequirements: { contractId: v.contractId, deliveryId: v.deliveryId, partNumber: v.partNumber, partRevision: v.partRevision,
    requirements: v.requirements.map(item => ({ ...item, evidence: [] })), applicability: v.applicability, enforceAtOrderRelease: v.enforceAtOrderRelease, enforceAtShipment: v.enforceAtShipment,
    requireIndependentComplaintClosure: v.requireIndependentComplaintClosure, reviewBasis: v.reviewBasis, requireCustomerConcessionForNonconformingRelease: v.requireCustomerConcessionForNonconformingRelease, customerSource: ref('customerSource'), evidence } };
  if (kind === 'DesignApplicability') return { designApplicability: { partNumber: v.partNumber, revision: v.revision, applicable: v.applicable, determinationBasis: v.determinationBasis, controls: v.controls.map(control => ({ ...control, evidence: [] })), evidence } };
  if (kind === 'ProviderEvaluation') return { providerEvaluation: { supplierCode: v.supplierCode, scope: { partNumber: v.partNumber, partRevision: v.partRevision, processCode: v.processCode, workCenterCode: v.workCenterCode,
    enforceAtPurchaseApproval: v.enforceAtPurchaseApproval, enforceAtExternalDispatch: v.enforceAtExternalDispatch, purchasingRequirements: v.purchasingRequirements }, criteria: v.criteria.map(criterion => ({ ...criterion, accepted: criterion.accepted == null ? null : criterion.accepted === 'true' })),
    minimumWeightedPassFraction: v.minimumWeightedPassFraction, validFrom: instant(v.validFrom), validTo: instant(v.validTo), reevaluateBy: instant(v.reevaluateBy), evaluationBasis: v.evaluationBasis, evidence } };
  if (kind === 'PropertyAgreement') return { propertyAgreement: { subject: { kind: v.subjectKind, key: v.subjectKey, partNumber: v.subjectPartNumber }, titleKind: v.titleKind, legalOwner: ref('legalOwner'), agreementBasis: v.agreementBasis,
    validFrom: instant(v.validFrom), validTo: instant(v.validTo), permittedActivities: v.permittedActivities.map(item => item.value), permittedWorkCenters: v.permittedWorkCenters.map(item => item.value), permittedOrders: v.permittedOrders.map(item => item.orderNumber),
    allowConsumptiveUse: v.allowConsumptiveUse, requireOwnerReturnAcceptance: v.requireOwnerReturnAcceptance, returnTerms: v.returnTerms, preservation: v.preservation.map(item => ({ ...item, evidence: [] })), evidence } };
  if (kind === 'PropertyIncident') return { propertyIncident: { agreementCode: v.agreementCode, agreementRevision: v.agreementRevision, kind: v.incidentKind, observedAt: instant(v.observedAt), description: v.description, immediateAction: v.immediateAction, evidence } };
  if (kind === 'Complaint') return { complaint: { shipmentId: v.shipmentId, shipmentLine: v.shipmentLine, reportedProblem: v.reportedProblem, reportedAt: instant(v.reportedAt), receivedChannel: v.receivedChannel, recordedContact: v.recordedContact, investigationOwner: v.investigationOwner, evidence } };
  if (kind === 'CustomerFeedback') return { customerFeedback: { customerSource: ref('customerSource'), kind: v.feedbackKind, receivedAt: instant(v.receivedAt), method: v.method, measures: v.measures.map(measure => ({ ...measure, accepted: measure.accepted == null ? null : measure.accepted === 'true' })), recordedFeedback: v.recordedFeedback, evidence } };
  if (kind === 'PostDelivery') return { postDelivery: { shipmentId: v.shipmentId, kind: v.postDeliveryKind, caseBasis: v.caseBasis, relatedEvidence: v.relatedEvidence } };
  if (kind === 'Communication') {
    if (v.hasConcession && (!v.concessionPartNumber || !v.concessionLotNumber || !v.concessionLocationCode || !v.concessionRevision || !v.concessionNcrId || !v.concessionQuantity || !v.concessionDisposition))
      throw new Error('Complete the exact part, lot, location, revision, NCR, quantity and proposed disposition for the concession.');
    const concession = v.hasConcession ? { subject: { partNumber: v.concessionPartNumber, lotNumber: v.concessionLotNumber, locationCode: v.concessionLocationCode, returnCohortId: v.concessionReturnCohortId }, revision: v.concessionRevision,
      ncrId: v.concessionNcrId, quantity: v.concessionQuantity, disposition: v.concessionDisposition } : null;
    return { communication: { audience: v.audience, partySource: ref('partySource'), relatedCase: ref('relatedCase'), recordedRecipient: v.recordedRecipient, subject: v.subject, body: v.body, proposedChannel: v.proposedChannel, evidence, concession } };
  }
  throw new Error('Choose a supported commercial quality record type.');
}
const recordPath = record => ROOT + '/records/' + enc(record.kind) + '/' + enc(record.code) + '/revisions/' + record.revision;
const detailHtml = value => {
  if (value == null || value === '') return '<span class="faint">Not recorded</span>';
  if (Array.isArray(value)) return value.length ? '<ul>' + value.map(detailHtml).map(item => '<li>' + item + '</li>').join('') + '</ul>' : '<span class="faint">No entries</span>';
  if (typeof value === 'object') return '<dl>' + Object.entries(value).map(([name, item]) => '<dt>' + esc(LABEL(name)) + '</dt><dd>' + detailHtml(item) + '</dd>').join('') + '</dl>';
  return '<span>' + esc(value) + '</span>';
};

export async function commercialQualityPage(page, ctx) {
  const auth = { subject: session.subject, token: session.token };
  let disposed = false, generation = 0, data = null, selected = null, error = null, cursor = null, directory = [];
  const live = token => !disposed && page.isConnected && generation === token && session.subject === auth.subject && session.token === auth.token;
  async function load(after = null) {
    const token = ++generation; cursor = after; selected = null; error = null;
    page.innerHTML = '<div class="muted">Loading authorized customer and provider quality records…</div>';
    const [result, people] = await Promise.all([tryGet(ROOT + '/records?take=50' + (after ? '&after=' + enc(after) : '')), tryGet('/api/kernel/directory')]);
    if (!live(token)) return;
    if (result.error) { error = result.error; data = null; } else data = result.data;
    if (!people.error) directory = Array.isArray(people.data) ? people.data : [];
    render();
  }
  async function open(row) {
    const token = ++generation; selected = null; render();
    const result = await tryGet(recordPath(row));
    if (!live(token)) return;
    if (result.error) error = result.error; else { selected = result.data; error = null; }
    render();
  }
  function modal(title, content, onSubmit, onOpen) {
    const scrim = document.createElement('div'), box = document.createElement('div');
    scrim.className = 'scrim'; scrim.setAttribute('aria-hidden', 'true'); box.className = 'modal';
    box.innerHTML = '<h2>' + esc(title) + '</h2>' + content + '<label class="field"><span>Reason recorded in the audit trail</span><textarea class="input" data-reason required></textarea></label><div class="notice bad hidden" role="alert" data-error></div><div class="actions"><button class="btn" data-cancel>Cancel</button><button class="btn primary" data-confirm>Record</button></div>';
    document.body.append(scrim, box); let closed = false, busy = false, releaseFocus = () => {};
    const close = () => { if (closed) return; closed = true; scrim.remove(); box.remove(); releaseFocus(); };
    scrim.onclick = close; $('[data-cancel]', box).onclick = close;
    $('[data-confirm]', box).onclick = async () => {
      if (closed || busy) return; busy = true; const button = $('[data-confirm]', box), problem = $('[data-error]', box);
      button.disabled = true; problem.classList.add('hidden');
      try { await onSubmit(box, live(generation)); close(); toast('The commercial quality record was updated.'); await load(); }
      catch (failure) { problem.textContent = failure.message; problem.classList.remove('hidden'); button.disabled = false; busy = false; }
    };
    releaseFocus = manageDialogFocus(box, close); onOpen?.(box);
  }
  const create = () => {
    const baseFields = [selects('kind', 'Record type', KINDS, { value: KINDS[0], required: true }), text('code', 'Record code', { required: true, maxLength: 40 }), text('facilityCode', 'Facility code', { required: true, value: ctx.me?.facilityCode ?? '' })];
    let activeFields = [];
    modal('Create controlled commercial quality record', '<p class="muted">Enter the actual owner record and exact source references. Each record type uses fields for its current governed content.</p>' +
      '<div class="form-grid">' + baseFields.map(fieldHtml).join('') + '</div><div class="form-grid" data-content-fields></div>',
      async (box, current) => {
        if (!current || !can(ctx.me, 'quality.plan')) throw new Error('A current session and quality planning authority are required.');
        const values = readFields(box, [...baseFields, ...activeFields]);
        await command(ROOT + '/records', { kind: values.kind, code: values.code, facilityCode: values.facilityCode, content: commercialContent(values.kind, values) }, $('[data-reason]', box).value.trim());
      }, box => {
        const host = $('[data-content-fields]', box), type = $('#f-kind', box);
        const renderFields = () => { activeFields = commercialFields(type.value, directory); host.innerHTML = activeFields.map(fieldHtml).join(''); mountStructuredFields(box, activeFields); };
        type.addEventListener('change', renderFields); renderFields();
      });
  };
  const transition = action => modal(LABEL(action) + ' commercial quality record', '<p class="muted">Current record version ' + selected.version + '. The server rechecks current sources, independence and authority.</p><label class="field"><span>Human decision basis</span><textarea class="input" data-basis required></textarea></label>',
    async (box, current) => {
      if (!current || !selected) throw new Error('Reload the current record before recording a decision.');
      const basis = $('[data-basis]', box).value.trim(), reason = $('[data-reason]', box).value.trim();
      if (!basis || !reason) throw new Error('Record the actual decision basis and audit reason.');
      await command(recordPath(selected) + '/' + action.toLowerCase(), { expectedVersion: selected.version, basis }, reason);
    });
  const observe = () => {
    const fields = [selects('observation', 'Observation kind', ['Monitoring', 'Reassessment', 'CorrectiveAction', 'Investigation', 'Correction', 'ComplaintDisposition', 'PropertyReceipt', 'PropertyUse', 'PropertyReturn', 'OwnerAcceptance', 'PreservationObservation', 'CommunicationObserved', 'CustomerApprovalReceived'], { required: true }),
      dateField('at', 'Observed at (UTC)'), referenceRows('evidence', 'Actual source evidence'), text('external', 'Recorded external person, where applicable'), text('basis', 'Human observation basis', { type: 'textarea', required: true })];
    modal('Record actual commercial observation', '<p class="muted">Observations are append-only and require exact current evidence references.</p><div class="form-grid">' + fields.map(fieldHtml).join('') + '</div>',
    async (box, current) => {
      if (!current || !selected || !can(ctx.me, 'quality.plan')) throw new Error('A current session and quality planning authority are required.');
      const values = readFields(box, fields);
      await command(recordPath(selected) + '/observations', { expectedVersion: selected.version, observation: values.observation,
        observedAt: instant(values.at), basis: values.basis, evidence: values.evidence, recordedExternalPerson: values.external || null }, $('[data-reason]', box).value.trim());
    }, box => { mountStructuredFields(box, fields); $('#f-at', box).value = new Date().toISOString().slice(0, 16); });
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
