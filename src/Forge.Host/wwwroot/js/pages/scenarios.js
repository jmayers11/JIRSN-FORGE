import { command, get } from '../api.js';
import { $, $$, esc, fmtDateTime, toast, confirmWithReason } from '../ui.js';
import { formModal } from '../forms.js';
import { scenarioCatalog } from '../data/whiskerworks-scenarios.js';
import { delayedPromiseDate, healthyBaselineAssertions, selectAsm03Callout, selectDayShiftScenarioRoster, selectOpenSensorPromiseLine, sisalQuantityEvidence, sisalSplitSteps } from '../data/whiskerworks-demo.js';
import { armDemoScannerOutage, readDemoScannerOutage } from '../demo-scanner-outage.mjs';
import { deriveMachineCards } from './machine-kanban.js';

const KINDS = ['MachineUnavailable', 'PersonUnavailable', 'PriorityChange', 'OperationMachine', 'Overtime', 'SupplierDelay', 'AddOperator', 'AddTrainer'];
const orderField = { name: 'orderNumber', label: 'Released order number', type: 'number', min: 1, required: true };
const operationField = { name: 'sequence', label: 'Operation sequence', type: 'number', min: 1, required: true };
const codeField = (name, label, required = true, hint) => ({ name, label, required, code: true, hint });
const numberField = (name, label, hint) => ({ name, label, type: 'number', min: 1, step: 'any', required: true, hint });
const stockFields = [codeField('partNumber', 'Stock part'), codeField('lotNumber', 'Lot / serial — optional for untracked stock', false),
  codeField('fromLocation', 'Source location'), numberField('quantity', 'Quantity', 'Use the selected stock part’s units.')];
const workforceFields = [codeField('workCenterCode', 'Work center'), codeField('shiftCode', 'Defined shift'),
  { name: 'personIds', label: 'Workers covering the shift', type: 'textarea', required: true, hint: 'Person IDs from Workforce, separated by commas or new lines. Their actual qualifications and availability remain binding.' }];
const RECOVERY_FORMS = [
  { id: 'overtime', title: 'Overtime', fields: [orderField, ...workforceFields, codeField('proposedShiftCode', 'New extended shift code'),
    numberField('additionalMinutes', 'Additional minutes (1–240)', 'The copied shift must remain at or below 24 hours.')] },
  { id: 'additional-shift', title: 'Additional Shift', fields: [orderField, ...workforceFields] },
  { id: 'alternate-personnel', title: 'Alternate Qualified Personnel', fields: [orderField, operationField,
    { name: 'personId', label: 'Alternate worker’s person ID', required: true }, codeField('machineCode', 'Assignment machine — optional for work-center work', false)] },
  { id: 'alternate-machine', title: 'Alternate Machine', fields: [orderField, operationField, codeField('machineCode', 'Released eligible machine')] },
  { id: 'alternate-work-center', title: 'Alternate Work Center', fields: [orderField, operationField,
    codeField('workCenterCode', 'Proposed work center', true, 'A configured center alone does not authorize a replacement routing. The result identifies the controlled source evidence still required.')] },
  { id: 'approved-alternate', title: 'Approved Alternate Material', fields: [orderField, codeField('primaryPartNumber', 'Primary component on the order'),
    numberField('alternateId', 'Approved alternate relationship number'), ...stockFields] },
  { id: 'existing-wip', title: 'Existing WIP', fields: [orderField, ...stockFields] },
  { id: 'remnant-transformation', title: 'Remnant Transformation', fields: [{ ...orderField, label: 'Released transform order number' },
    ...stockFields.map(f => f.name === 'lotNumber' ? { ...f, label: 'Recorded remnant lot', required: true } : f)] },
  { id: 'split-production', title: 'Split Production', fields: [orderField,
    { name: 'splitQuantities', label: 'Split quantities', required: true, placeholder: '4, 6', hint: 'Positive quantities separated by commas, totaling the released order quantity. A controlled source split plan and commitment traceability are still required.' }] },
  { id: 'resequence', title: 'Resequencing', fields: [orderField, numberField('priority', 'New priority (1 highest, 9 lowest)')] },
  { id: 'expedited-procurement', title: 'Expedited Procurement', fields: [orderField, numberField('purchaseOrder', 'Approved purchase order number'),
    numberField('purchaseOrderLine', 'Open purchase line'), { name: 'proposedDate', label: 'Proposed supplier date', type: 'date', required: true, hint: 'The earlier date is hypothetical until supplier confirmation is recorded at source.' }] },
  { id: 'expedited-material-movement', title: 'Expedited Material Movement', fields: [orderField, ...stockFields, codeField('toLocation', 'Destination point-of-use location')] },
  { id: 'outside-processing', title: 'Outside Processing', fields: [orderField,
    ...stockFields.map(f => f.name === 'lotNumber' ? { ...f, label: 'Lot / serial', required: true } : f), codeField('toLocation', 'Processor location'),
    numberField('purchaseOrder', 'Approved service purchase order number'), numberField('purchaseOrderLine', 'Open service purchase line'),
    { name: 'proposedDate', label: 'Expected return date — optional', type: 'date' }, codeField('specificationDocument', 'Process specification — if applicable', false),
    { name: 'specificationRevision', label: 'Specification revision — optional', hint: 'The applicable revision is resolved from the document when omitted.' }] },
  { id: 'maintenance-timing', title: 'Maintenance Timing Change', fields: [orderField, numberField('workOrderId', 'Open maintenance work order number'),
    { name: 'startsAt', label: 'Proposed start', type: 'datetime-local', required: true }, { name: 'endsAt', label: 'Proposed end', type: 'datetime-local', required: true }] },
  { id: 'partial-shipment', title: 'Partial Shipment', fields: [numberField('shipmentId', 'Packed partial shipment number', 'Must reference a visible open delivery demand. The remaining demand stays outstanding.')] },
];
const INTEGER_FIELDS = new Set(['orderNumber', 'sequence', 'priority', 'additionalMinutes', 'alternateId', 'purchaseOrder', 'purchaseOrderLine', 'workOrderId', 'shipmentId']);
const personIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const localTime = value => {
  if (!value) return '';
  const date = new Date(value);
  date.setMinutes(date.getMinutes() - date.getTimezoneOffset());
  return date.toISOString().slice(0, 16);
};

function recoverySelection(definition, values) {
  const selection = { optionId: definition.id };
  for (const field of definition.fields) {
    let value = values[field.name];
    if (value == null) continue;
    if (field.name === 'personIds') {
      value = Array.isArray(value) ? value : value.split(/[\s,;]+/).filter(Boolean);
      if (!value.length || value.length > 100 || value.some(id => !personIdPattern.test(id)) || new Set(value.map(id => id.toLowerCase())).size !== value.length)
        throw new Error('Select 1 to 100 distinct worker person IDs from Workforce.');
    } else if (field.name === 'personId' && !personIdPattern.test(value)) {
      throw new Error('Enter the worker’s full person ID from Workforce.');
    } else if (field.name === 'splitQuantities') {
      value = value.split(/[\s,;]+/).filter(Boolean).map(Number);
      if (value.length < 2 || value.some(q => !Number.isFinite(q) || q <= 0)) throw new Error('Enter at least two positive split quantities.');
    } else if (field.type === 'number') {
      if (!Number.isFinite(value) || value <= 0 || INTEGER_FIELDS.has(field.name) && !Number.isSafeInteger(value))
        throw new Error(`${field.label} must be a positive ${INTEGER_FIELDS.has(field.name) ? 'whole number' : 'number'}.`);
    } else if (field.type === 'datetime-local') {
      const date = new Date(value);
      if (Number.isNaN(date.getTime())) throw new Error(`${field.label} must be a valid date and time.`);
      value = date.toISOString();
    }
    selection[field.name] = value;
  }
  return selection;
}

const renderRecovery = (o, scenarioId, loading) => {
  const status = { Feasible: 'Projected feasible', Infeasible: 'Blocked', RequiresInputs: 'More inputs needed', NotAuthorized: 'Authority required' }[o.status] || o.status;
  const lines = (title, values) => values?.length ? `<h5>${title}</h5><ul>${values.map(x => `<li>${esc(x)}</li>`).join('')}</ul>` : '';
  const candidates = o.machineAlternatives?.length ? `<h5>Evaluated machines</h5><div class="table-wrap"><table class="table"><thead><tr><th>Order / operation</th><th>Machine</th><th>Completion</th><th>Evidence</th></tr></thead><tbody>${o.machineAlternatives.map(a => `<tr><td>#${a.orderNumber} / ${a.sequence}</td><td>${esc(a.machineCode)}</td><td>${a.feasible ? esc(fmtDateTime(a.projectedCompletion)) : 'Blocked'}</td><td>${[...a.constraints, ...a.evidence].map(esc).join('<br>')}</td></tr>`).join('')}</tbody></table></div>` : '';
  const projections = o.projections?.length ? `<h5>Affected commitments</h5><div class="table-wrap"><table class="table"><thead><tr><th>Order</th><th>Current projection</th><th>Scenario projection</th></tr></thead><tbody>${o.projections.map(p => `<tr><td><a href="#/orders/${p.orderNumber}">#${p.orderNumber}</a></td><td>${esc(fmtDateTime(p.baselineCompletion))}<br>${esc(p.baselineStatus)}</td><td>${esc(fmtDateTime(p.proposedCompletion))}<br>${esc(p.proposedStatus)}</td></tr>`).join('')}</tbody></table></div>` : '';
  const authority = o.sourceAuthority?.length ? `<details><summary>Source commands and authority</summary><ul>${o.sourceAuthority.map(a => `<li><b>${esc(a.sourceContext)} · ${esc(a.command)}</b> — ${a.permitted ? 'Permission available' : 'Authority required'}<br><span class="muted small">${esc(a.resourceType)}${a.resourceId ? ` / ${esc(a.resourceId)}` : ''} · ${esc(a.permission)}</span><p>${esc(a.commitRequirement)}</p></li>`).join('')}</ul></details>` : '';
  return `<article class="card"><div class="card-head"><h4>${esc(o.title)}</h4><span class="pill ${o.feasible ? 'ok' : 'warn'}">${esc(status)}</span><span class="spacer"></span><button class="btn primary" data-evaluate-scenario="${esc(scenarioId)}" data-evaluate-option="${esc(o.id)}" ${loading ? 'disabled' : ''}>Evaluate option</button>${o.applyPath ? `<a class="btn" href="${esc(o.applyPath)}">Open workflow</a>` : ''}</div><p>${esc(o.feasibility)}</p><p class="muted small">Required permission: <code>${esc(o.requiredPermission)}</code></p>${lines('Consequences', o.consequences)}${lines('Required inputs', o.requiredInputs)}${lines('Current blockers', o.constraints)}${projections}${candidates}${authority}<details><summary>Source evidence</summary>${lines('Evidence', o.evidence)}</details></article>`;
};

export async function scenariosPage(root, ctx = { query: new URLSearchParams() }) {
  let scenarios = [];
  let directory = [];
  let items = [];
  let resources = {};
  let result = null;
  let demoWorkers = null;
  let loading = false;
  let profile = null;
  let demoComparison = null;
  let demoBaseline = null;
  let demoEvents = [];
  try { demoBaseline = JSON.parse(sessionStorage.getItem('forge.whiskerworks.baseline.v1') ?? 'null'); } catch { demoBaseline = null; }
  try { demoEvents = JSON.parse(sessionStorage.getItem('forge.whiskerworks.events.v1') ?? '[]'); } catch { demoEvents = []; }
  const read = async () => {
    [scenarios, directory, items, resources, profile] = await Promise.all([
      get('/api/planning/scenarios'), get('/api/kernel/directory'), get('/api/engineering/items'), get('/api/resources/'), get('/api/kernel/me'),
    ]);
    try { demoWorkers = await get('/api/workforce/workers'); } catch { demoWorkers = null; }
  };
  const field = (name, label, type = 'text', required = false, placeholder = '', value = '') => `<label class="field"><span>${label}</span><input class="input" name="${name}" type="${type}" value="${esc(value)}" ${required ? 'required' : ''} ${placeholder ? `placeholder="${placeholder}"` : ''}></label>`;
  const selectField = (name, label, records, value = '', required = false, blank = 'Choose…') => `<label class="field"><span>${label}</span><select class="input" name="${name}" ${required ? 'required' : ''}><option value="">${blank}</option>${records.map(([key, text]) => `<option value="${esc(key)}" ${String(value) === String(key) ? 'selected' : ''}>${esc(text)}</option>`).join('')}</select></label>`;
  const render = () => {
    const prefilled = {
      orderNumber: ctx.query.get('order') ?? '', sequence: ctx.query.get('op') ?? '', workCenterCode: ctx.query.get('wc') ?? '',
      kind: KINDS.includes(ctx.query.get('kind')) ? ctx.query.get('kind') : '', personId: ctx.query.get('personId') ?? '',
      machineCode: ctx.query.get('machineCode') ?? '', priority: ctx.query.get('priority') ?? '', delayDays: ctx.query.get('delayDays') ?? '',
      name: ctx.query.get('scenarioName') ?? '',
      startsAt: ctx.query.get('startsAt') ?? '', durationHours: ctx.query.get('durationHours') ?? '',
    };
    const isGanttLaunch = !!(prefilled.orderNumber || prefilled.sequence || prefilled.workCenterCode);
    const defaultStart = prefilled.kind === 'PersonUnavailable' ? localTime(new Date().toISOString()) : '';
    const whiskerworks = (resources.sites ?? []).some(site => site.code === 'WW-P01');
    root.innerHTML = `<div class="page-head"><div><h1>Scenarios and recovery</h1><p class="muted">Save private what-if cases, compare them against today’s authorized material, capacity and schedule inputs, and review recovery actions. Running a scenario does not change the approved schedule or source records.</p></div></div>
      ${whiskerworks ? renderWhiskerWorksCenter() : ''}
      <section class="panel"><header><h2>New scenario</h2></header><form id="scenario-form" class="form-grid">
        ${field('name', 'Private scenario name', 'text', true, 'e.g. Slitter outage next shift', prefilled.name)}
        <label class="field"><span>Change to test</span><select class="input" name="kind">${KINDS.map((kind, index) => `<option ${prefilled.kind ? kind === prefilled.kind ? 'selected' : '' : isGanttLaunch && kind === 'OperationMachine' || !isGanttLaunch && index === 0 ? 'selected' : ''}>${kind}</option>`).join('')}</select></label>
        ${selectField('machineCode', 'Machine', (resources.machines ?? []).map(machine => [machine.code, `${machine.name ?? machine.code} · ${machine.code}`]), prefilled.machineCode)}
        ${selectField('personId', 'Person', directory.map(person => [person.personId, `${person.displayName} · ${person.personId}`]), prefilled.personId)}
        ${field('orderNumber', 'Released order number', 'number', false, '', prefilled.orderNumber)}${field('sequence', 'Operation sequence', 'number', false, '', prefilled.sequence)}${field('priority', 'Proposed priority (1 highest)', 'number', false, '', prefilled.priority)}
        ${selectField('workCenterCode', 'Work center', (resources.workCenters ?? []).map(center => [center.code, `${center.name ?? center.code} · ${center.code}`]), prefilled.workCenterCode)}
        ${selectField('activityCode', 'Activity', (resources.activityTypes ?? []).map(activity => [activity.code, `${activity.name ?? activity.code} · ${activity.code}`]))}
        ${field('startsAt', 'Starts at', 'datetime-local', false, '', prefilled.startsAt || defaultStart)}${field('durationHours', 'Hours (outage, overtime)', 'number', false, '', prefilled.durationHours || (prefilled.kind === 'PersonUnavailable' ? '8' : ''))}${field('delayDays', 'Supplier delay days', 'number', false, '', prefilled.delayDays)}
        ${field('reason', 'Why save this scenario', 'text', true, 'Required for the audit record')}
        <div class="field"><span>&nbsp;</span><button class="btn primary" type="submit">Save private scenario</button></div>
      </form><p class="muted small">The selected change uses only its relevant fields. Person and order lookups are checked again using your current access when you run the scenario.</p></section>
      <section class="panel"><header><h2>Your private scenarios</h2><span class="spacer"></span><button class="btn" id="refresh-scenarios">Refresh</button></header>
        ${scenarios.length ? `<div class="stack">${scenarios.map(s => `<article class="card"><div class="card-head"><div><h3>${esc(s.name)}</h3><p class="muted">${esc(s.change.kind)} · created ${esc(fmtDateTime(s.createdAt))}${s.change.recoverySelections?.length ? ` · selected ${esc(RECOVERY_FORMS.find(f => f.id === s.change.recoverySelections[0].optionId)?.title ?? s.change.recoverySelections[0].optionId)}` : ''}</p></div><span class="spacer"></span><button class="btn primary" data-run="${esc(s.scenarioId)}" ${loading ? 'disabled' : ''}>Run preview</button><button class="btn danger" data-delete="${esc(s.scenarioId)}">Delete</button></div><div class="actions"><label class="field"><span>Recovery option</span><select class="input" data-option-picker="${esc(s.scenarioId)}"><option value="">Choose an option</option>${RECOVERY_FORMS.map(f => `<option value="${esc(f.id)}">${esc(f.title)}</option>`).join('')}</select></label><button class="btn" data-evaluate-scenario="${esc(s.scenarioId)}" ${loading ? 'disabled' : ''}>Evaluate option</button></div></article>`).join('')}</div>` : '<div class="empty">No private scenarios saved yet.</div>'}</section>
      ${result ? renderResult(result) : ''}`;

    $('#refresh-scenarios', root)?.addEventListener('click', () => scenariosPage(root, ctx).catch(e => toast(e.message, true)));
    $('#ww-capture-baseline', root)?.addEventListener('click', () => captureDemoBaseline());
    $('#ww-compare-baseline', root)?.addEventListener('click', () => compareDemoBaseline());
    $('#ww-export-evidence', root)?.addEventListener('click', () => exportDemoEvidence());
    $$('[data-demo-action]', root).forEach(button => button.addEventListener('click', () => runDemoAction(button.dataset.demoAction)));
    $$('[data-ww-preview]', root).forEach(link => link.addEventListener('click', event => {
      event.preventDefault();
      window.location.hash = link.getAttribute('href');
    }));
    $('#scenario-form', root)?.addEventListener('submit', saveScenario);
    $$('[data-run]', root).forEach(button => button.addEventListener('click', () => runScenario(button.dataset.run)));
    $$('[data-delete]', root).forEach(button => button.addEventListener('click', () => deleteScenario(button.dataset.delete)));
    $$('[data-evaluate-scenario]', root).forEach(button => button.addEventListener('click', () => {
      const id = button.dataset.evaluateScenario;
      const option = button.dataset.evaluateOption || $$('[data-option-picker]', root).find(picker => picker.dataset.optionPicker === id)?.value;
      if (!option) { toast('Choose a recovery option to evaluate.', true); return; }
      evaluateOption(id, option).catch(error => toast(error.message, true));
    }));
  };

  function renderWhiskerWorksCenter() {
    const map = {
      'WW-S07-SHORTAGE': ['SupplierDelay', '4'],
      'WW-S10-RUSH-ORDER': ['PriorityChange', '1'],
    };
    const actions = {
      'WW-S02-ABSENCES': { label: 'Start Day Shift', permission: 'workforce.supervise' },
      'WW-S03-TWO-PERSON': { label: 'Record one callout', permission: 'workforce.supervise', also: ['competency.read'] },
      'WW-S04-CNC-DOWN': { label: 'Report CNC-01 down', permission: 'maintenance.downtime.report' },
      'WW-S06-MRB-HOLD': { label: 'Hold motor demo lot', permission: 'inventory.transact' },
      'WW-S07-SHORTAGE': { label: 'Delay sensor promise by 4 days', permission: 'procurement.manage', also: ['procurement.read', 'material.plan.read'] },
      'WW-S08-LOT-SPLIT': { label: 'Split sisal demo lot', permission: 'inventory.transact' },
      'WW-S11-CAL-EXPIRED': { label: 'Expire torque and optical gauges', permission: 'quality.plan', also: ['quality.read'] },
      'WW-S12-SCAN-OUTAGE': { label: 'Arm scanner / label 503 rehearsal', permission: 'workforce.supervise' },
      'WW-S14-RECOVERY-BOARD': { label: 'Create linked recovery actions', permission: 'workforce.supervise', also: ['execution.shopfloor.record', 'execution.shopfloor.read'] },
      'WW-S15-HANDOFF': { label: 'Capture and review outgoing handoff', permission: 'workforce.supervise', also: ['execution.shopfloor.record', 'execution.shopfloor.read', 'workforce.read'] },
      'WW-S16-CASCADE': { label: 'Apply three source events', permission: 'workforce.supervise', also: ['maintenance.downtime.report', 'inventory.transact'] },
    };
    const can = permission => (profile?.permissions ?? []).includes(permission);
    const demoSiteIsExclusive = (resources.sites ?? []).length === 1 && resources.sites[0]?.code === 'WW-P01';
    const demoCatalogIsExclusive = items.length === 100 && items.every(item => String(item.partNumber ?? '').startsWith('WW-'));
    const demoFoundationShape = (resources.workCenters ?? []).length === 16 && (resources.machines ?? []).length === 24
      && demoWorkers?.length === 60 && demoWorkers.every(worker => (resources.workCenters ?? []).some(center => center.code === worker.homeWorkCenterCode));
    const safeTarget = demoSiteIsExclusive && demoCatalogIsExclusive && demoFoundationShape;
    const s12Rehearsal = readDemoScannerOutage();
    const s12RehearsalActive = !!s12Rehearsal;
    const baselineLabel = demoBaseline ? `Captured ${esc(fmtDateTime(demoBaseline.capturedAt))} in this browser session` : 'No browser-session baseline captured';
    const eventRows = demoEvents.slice(-8).reverse().map(event => `<li><b>${esc(event.code)}</b> · ${esc(event.title)} · ${esc(fmtDateTime(event.recordedAt))}<br><span class="muted small">${esc(event.summary)}</span></li>`).join('');
    return `<section class="panel ww-scenario-center" aria-label="WhiskerWorks demo scenario center">
      <header><div><h2>WhiskerWorks Demo Scenario Center</h2><p class="muted small">FICTIONAL / SAMPLE DATA · WW-P01</p></div><span class="spacer"></span><span class="pill">${scenarioCatalog.length} scenario definitions</span></header>
      <p class="muted">Source-changing demo actions use Forge’s audited commands against named WW-P01 sample records, require your normal permissions and a reason, and use stable command keys for safe retries. S12 is an explicitly local browser-session fault rehearsal; it issues no source-changing API command and contacts no scanner or printer; ordinary read-only kiosk data may still load. A WW-P01 site label alone is not tenant isolation, so source-changing controls stay disabled unless this is the only site and the visible item catalog contains only WhiskerWorks parts. Preview links remain hypothetical.</p>
      <div class="actions"><button class="btn" id="ww-capture-baseline">Capture baseline snapshot</button><button class="btn" id="ww-compare-baseline" ${demoBaseline ? '' : 'disabled'}>Compare with current state</button><button class="btn" id="ww-export-evidence">Export evidence JSON</button><span class="muted small">${baselineLabel}</span></div>
      ${demoBaseline ? renderHealthyBaselineAssertions(demoBaseline) : '<p class="muted small">Capture a read-only snapshot to evaluate the S01 healthy-baseline checks. Capture does not reset or change Forge data.</p>'}
      <div id="ww-evidence-comparison">${demoComparison ? renderDemoComparison(demoComparison) : ''}</div>
      <details open><summary>Canonical disruption catalog</summary><div class="ww-scenario-catalog">${scenarioCatalog.map(scenario => {
        const preview = map[scenario.code];
        const action = actions[scenario.code];
        let href = '#/scenarios';
        if (preview?.[0] === 'SupplierDelay') href = `#/scenarios?kind=${preview[0]}&delayDays=${preview[1]}&scenarioName=${encodeURIComponent(`${scenario.code} ${scenario.title}`)}`;
        if (preview?.[0] === 'PriorityChange') href = `#/scenarios?kind=${preview[0]}&priority=${preview[1]}&scenarioName=${encodeURIComponent(`${scenario.code} ${scenario.title}`)}`;
        const missingPermissions = action ? [action.permission, ...(action.also ?? [])].filter(permission => !can(permission)) : [];
        const s15Events = demoEvents.filter(event => event.code === 'WW-S15-HANDOFF');
        const s15HandoffEvent = s15Events.find(event => Number.isSafeInteger(Number(event.targetGroups?.handoffId)));
        const s15Acknowledgment = [...s15Events].reverse().find(event => Number.isSafeInteger(Number(event.targetGroups?.incomingPersonId)));
        const s15Acknowledged = !!s15Acknowledgment;
        const alreadyRecorded = scenario.code === 'WW-S03-TWO-PERSON' && demoEvents.some(event => event.code === scenario.code
          && Date.parse(event.targetGroups?.endsAt ?? '') > Date.now())
          || scenario.code === 'WW-S07-SHORTAGE' && demoEvents.some(event => event.code === scenario.code)
          || scenario.code === 'WW-S11-CAL-EXPIRED' && demoEvents.some(event => event.code === scenario.code)
          || scenario.code === 'WW-S12-SCAN-OUTAGE' && s12RehearsalActive
          || scenario.code === 'WW-S14-RECOVERY-BOARD' && demoEvents.some(event => event.code === scenario.code)
          || scenario.code === 'WW-S16-CASCADE' && demoEvents.some(event => event.code === scenario.code && event.targetGroups?.phase === 'complete')
          || scenario.code === 'WW-S15-HANDOFF' && s15Acknowledged;
        const sameS15Actor = scenario.code === 'WW-S15-HANDOFF' && s15HandoffEvent
          && s15HandoffEvent.actorPersonId && s15HandoffEvent.actorPersonId === profile?.personId;
        const missingS15Person = scenario.code === 'WW-S15-HANDOFF' && s15HandoffEvent && !profile?.personId;
        const actionBlocked = action && (!safeTarget || missingPermissions.length > 0 || alreadyRecorded || sameS15Actor || missingS15Person);
        const actionRequirement = alreadyRecorded ? scenario.code === 'WW-S03-TWO-PERSON'
          ? `Callout already recorded through ${fmtDateTime(demoEvents.find(event => event.code === scenario.code && Date.parse(event.targetGroups?.endsAt ?? '') > Date.now()).targetGroups.endsAt)}.`
          : scenario.code === 'WW-S11-CAL-EXPIRED' ? 'Gauge-expiry action already recorded in this browser session.'
            : scenario.code === 'WW-S12-SCAN-OUTAGE' ? s12Rehearsal?.scanner?.status === 'failed' && s12Rehearsal?.label?.status === 'reconciled' ? 'Close the completed local rehearsal on Shop Floor before starting again.' : 'Finish the local fault rehearsal and supervisor reconciliation on Shop Floor.'
            : scenario.code === 'WW-S14-RECOVERY-BOARD' ? 'Recovery-board action already recorded in this browser session.'
              : scenario.code === 'WW-S15-HANDOFF' ? 'The independent incoming-shift acknowledgment is recorded.'
                : scenario.code === 'WW-S16-CASCADE' ? 'Cascade source actions were recorded in this browser session.'
                : 'A sensor promise change is already recorded in this browser session.'
          : sameS15Actor ? 'Sign in as a different incoming worker to acknowledge this reviewed handoff.'
          : missingS15Person ? 'A registered incoming worker identity is required for acknowledgment.'
          : !safeTarget ? 'Requires a dedicated WW-P01 demo shape. This UI check is not tenant isolation.' : `Requires ${missingPermissions.join(', ')}`;
        const actionLabel = scenario.code === 'WW-S15-HANDOFF' && s15HandoffEvent ? 'Acknowledge as incoming shift'
          : action?.label;
        const recordedLabel = scenario.code === 'WW-S03-TWO-PERSON' ? 'Recorded for this shift'
          : scenario.code === 'WW-S11-CAL-EXPIRED' ? 'Expiry recorded'
            : scenario.code === 'WW-S14-RECOVERY-BOARD' ? 'Recovery actions recorded'
              : scenario.code === 'WW-S15-HANDOFF' ? 'Handoff acknowledged'
                : scenario.code === 'WW-S12-SCAN-OUTAGE' ? s12Rehearsal?.scanner?.status === 'failed' && s12Rehearsal?.label?.status === 'reconciled' ? 'Rehearsal complete' : 'Rehearsal in progress'
                : scenario.code === 'WW-S16-CASCADE' ? 'Cascade recorded' : 'Delay recorded';
        const actionButton = action ? `<button class="btn small primary" data-demo-action="${esc(scenario.code)}" ${actionBlocked ? 'disabled' : ''}>${esc(alreadyRecorded ? recordedLabel : actionLabel)}</button>${actionBlocked ? `<span class="muted small">${esc(actionRequirement)}</span>` : ''}` : '';
        const workflow = {
          'WW-S01-BASELINE': ['#/machine-kanban', 'Review machine readiness'],
          'WW-S05-INSP-SOON': ['#/work-centers?focus=maintenance', 'Open PM and maintenance'],
          'WW-S09-ECN-REV': ['#/engineering?part=WW-PCB-CTRL-200', 'Open engineering revision'],
          'WW-S11-CAL-EXPIRED': ['#/work-centers?focus=tools', 'Open calibration records'],
          'WW-S12-SCAN-OUTAGE': ['#/shop-floor', 'Open operator kiosk'],
          'WW-S13-IPC-MISMATCH': ['#/workforce', 'Open training and qualifications'],
          'WW-S14-RECOVERY-BOARD': ['#/shift-continuity', 'Open shift actions'],
          'WW-S15-HANDOFF': ['#/shift-continuity', 'Open shift handoff'],
        }[scenario.code];
        const control = actionButton || (preview ? `<a class="btn small" data-ww-preview href="${esc(href)}">Open private preview form</a>` : workflow ? `<a class="btn small" href="${esc(workflow[0])}">${esc(workflow[1])}</a>` : '<span class="muted small">Source workflow required</span>');
        const modeLabel = scenario.code === 'WW-S12-SCAN-OUTAGE' ? 'Local browser fault rehearsal' : action ? 'Audited source command' : preview ? 'Private what-if preview' : workflow ? 'Existing workflow · manual evidence' : 'No executable control';
        const limits = {
          'WW-S02-ABSENCES': 'Creates three audited CallOut windows and leaves two scheduled workers unconfirmed. Any current presence is ended explicitly; no attendance or payroll record is fabricated.',
          'WW-S03-TWO-PERSON': 'Records one demo callout; machine readiness continues to be derived by Forge from the current roster and safety rule.',
          'WW-S04-CNC-DOWN': 'Reports an unplanned machine-down event through Maintenance. Restore remains a separate authorized source action.',
          'WW-S06-MRB-HOLD': 'Moves the seeded motor lot to inventory Hold. It does not create an NCR or claim order pegging unless the source records show that impact.',
          'WW-S07-SHORTAGE': 'Changes the earliest visible approved, open WW-SENS-PROX-05 purchase line by four calendar days through Procurement. The audited date change records the source update; it does not fabricate supplier correspondence or guarantee an MRP shortage.',
          'WW-S08-LOT-SPLIT': 'Splits and reconciles the 250 m parent into 100/100/45 m child lots and 5 m scrap. Parent-hold propagation is not modeled by this control.',
          'WW-S11-CAL-EXPIRED': 'Adds a dated calibration event to the existing gauges; readiness derives their expired status from the recorded due date. No site clock or calibration history is deleted.',
          'WW-S12-SCAN-OUTAGE': 'Arms a browser-session-only rehearsal. Simulated barcode-staging and label-print 503s issue no source-changing API command and contact no printer; typed demo entries stay in session storage, the terminal locks after each failure, and label output requires a supervisor observation signoff.',
          'WW-S14-RECOVERY-BOARD': 'Creates two reviewed shift actions on a completed WW-P01 shift. The second action stores a dependency on the first and a later due time; source issue records remain unchanged.',
          'WW-S15-HANDOFF': 'Creates and reviews a completed-shift handoff only when the shift snapshot can show both CNC-01 downtime and a recorded worker absence. A different incoming worker must sign in and acknowledge it; Forge rejects self-acknowledgment.',
          'WW-S16-CASCADE': 'Runs the three source commands in sequence. If a later command fails, completed steps remain audited and can be safely retried.',
        }[scenario.code];
        return `<article class="ww-scenario-card"><div><code>${esc(scenario.code)}</code><h3>${esc(scenario.title)}</h3><span class="pill">${esc(modeLabel)}</span></div><p class="muted small">${esc(scenario.event)}</p>${control}${limits ? `<p class="muted small">${esc(limits)}</p>` : ''}</article>`;
      }).join('')}</div></details>
      <details><summary>Recent demo source actions (${demoEvents.length})</summary>${eventRows ? `<ol class="ww-scenario-events">${eventRows}</ol>` : '<p class="muted small">No demo source actions recorded in this browser session.</p>'}<p class="muted small">The event list is a browser-session index. Forge’s own audit and domain records remain authoritative.</p></details>
      <p class="notice warn"><b>Still outside this Sprint 9 slice:</b> site-scoped simulated time, a destructive reset, and automatic assertions for every scenario. Forge has no isolated clock or reset boundary for those operations.</p>
    </section>`;
  }

  function renderDemoComparison(comparison) {
    const keys = new Set([...Object.keys(comparison.baseline.metrics), ...Object.keys(comparison.current.metrics)]);
    const rows = [...keys].sort().map(key => {
      const before = comparison.baseline.metrics[key] ?? null;
      const after = comparison.current.metrics[key] ?? null;
      return `<tr><th>${esc(key)}</th><td>${before == null ? '—' : esc(String(before))}</td><td>${after == null ? '—' : esc(String(after))}</td><td>${before == null || after == null ? 'Unavailable' : esc(String(after - before))}</td></tr>`;
    }).join('');
    return `<div class="table-wrap"><table class="table"><caption>Browser-session snapshot comparison · counts only</caption><thead><tr><th>Source count</th><th>Baseline</th><th>Current</th><th>Delta</th></tr></thead><tbody>${rows || '<tr><td colspan="4">No comparable source counts.</td></tr>'}</tbody></table><p class="muted small">Unavailable sources are omitted from counts. This compares two read snapshots; it does not represent a simulated clock or a committed scenario event.</p></div>`;
  }

  function renderHealthyBaselineAssertions(snapshot) {
    const assertions = snapshot.healthyBaselineAssertions ?? healthyBaselineAssertions(snapshot);
    const rows = assertions.map(assertion => {
      const tone = assertion.outcome === 'Pass' ? 'ok' : assertion.outcome === 'Fail' ? 'bad' : 'warn';
      return `<li><span class="pill ${tone}">${esc(assertion.outcome)}</span> <b>${esc(assertion.check)}</b><div class="muted small">${esc(assertion.detail)}</div></li>`;
    }).join('');
    return `<details class="ww-baseline-checks"><summary>S01 healthy baseline · ${assertions.filter(item => item.outcome === 'Pass').length}/${assertions.length} checks passed</summary><ol class="ww-scenario-events">${rows}</ol><p class="muted small">Source checks evaluate the captured snapshot only. Unavailable reads remain Indeterminate; a capture does not establish tenant isolation or reset state.</p></details>`;
  }

  async function captureDemoState() {
    const sources = {
      resources: '/api/resources/', orders: '/api/orders/', workers: '/api/workforce/workers', presence: '/api/readiness/presence', procurement: '/api/procurement/purchase-orders',
      mrp: '/api/planning/mrp?part=WW-SENS-PROX-05',
      operations: '/api/readiness/operations', activeWork: '/api/readiness/active-work', schedule: '/api/planning/schedules/approved',
      cnc01Readiness: '/api/readiness/machines/CNC-01?activity=CNC_MILL', asm03Readiness: '/api/readiness/machines/ASM-03?activity=ASSEMBLE_HEAVY',
      assets: '/api/maintenance/assets', inventory: '/api/inventory/positions', quality: '/api/quality/due', gauges: '/api/quality/gauges', maintenance: '/api/maintenance/work-orders?includeClosed=false',
      downtime: '/api/maintenance/downtime', holds: '/api/holds/?includeReleased=false', unavailability: `/api/workforce/unavailability?from=${encodeURIComponent(new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString())}&to=${encodeURIComponent(new Date(Date.now() + 14 * 24 * 60 * 60 * 1000).toISOString())}`,
      handoffs: '/api/execution/shifts/handoffs?workCenter=WC-CNC-MILL&limit=100',
      sisalLedger: '/api/inventory/transactions?part=WW-SISAL-SPOOL-250M&take=200',
    };
    const settled = await Promise.all(Object.entries(sources).map(async ([name, path]) => {
      try { return [name, { available: true, value: await get(path) }]; }
      catch { return [name, { available: false }]; }
    }));
    const values = Object.fromEntries(settled);
    if (values.workers.available && (profile?.permissions ?? []).includes('competency.read')) {
      try { values.qualifications = { available: true, value: await readDemoQualifications(values.workers.value) }; }
      catch { values.qualifications = { available: false }; }
    } else values.qualifications = { available: false };
    values.recoveryActions = { available: false };
    if (values.handoffs.available && (profile?.permissions ?? []).some(permission => ['execution.shopfloor.read', 'workforce.read'].includes(permission))) {
      try {
        const recoveryRows = [];
        for (const handoff of values.handoffs.value.filter(item => String(item.context ?? '').includes('WW-S14 fictional recovery-board scenario'))) {
          const handoffId = Number(handoff.handoffId);
          if (!Number.isSafeInteger(handoffId) || handoffId <= 0) continue;
          const actions = await get(`/api/execution/shifts/handoffs/${handoffId}/actions`);
          recoveryRows.push(...actions.map(action => ({ ...action, evidenceHandoffId: handoffId })));
        }
        values.recoveryActions = { available: true, value: recoveryRows };
      } catch { values.recoveryActions = { available: false }; }
    }
    values.s15Handoff = { available: false };
    const s15Event = [...demoEvents].reverse().find(event => event.code === 'WW-S15-HANDOFF'
      && Number.isSafeInteger(Number(event.targetGroups?.handoffId)));
    if (s15Event && (profile?.permissions ?? []).some(permission => ['execution.shopfloor.read', 'workforce.read'].includes(permission))) {
      try {
        values.s15Handoff = { available: true, value: await get(`/api/execution/shifts/handoffs/${Number(s15Event.targetGroups.handoffId)}`) };
      } catch { values.s15Handoff = { available: false }; }
    }
    const metrics = {};
    const resource = values.resources.value;
    if (values.resources.available) {
      metrics.sites = resource.sites?.length ?? 0;
      metrics.workCenters = resource.workCenters?.length ?? 0;
      metrics.machines = resource.machines?.length ?? 0;
    }
    if (values.orders.available) {
      const orders = Array.isArray(values.orders.value) ? values.orders.value : [];
      metrics.productionOrders = orders.length;
      for (const status of new Set(orders.map(order => order.status).filter(Boolean))) metrics[`orders.${status}`] = orders.filter(order => order.status === status).length;
    }
    if (values.workers.available) metrics.workers = Array.isArray(values.workers.value) ? values.workers.value.length : 0;
    if (values.presence.available) metrics.presentWorkers = Array.isArray(values.presence.value) ? values.presence.value.filter(person => person.countsAsPresent).length : 0;
    if (values.operations.available) metrics.readinessOperations = Array.isArray(values.operations.value) ? values.operations.value.length : 0;
    const maintenanceOrders = values.maintenance.available ? values.maintenance.value.map(view => ({ ...view.order, runHours: view.runHours, dueByRunHours: view.dueByRunHours, parts: view.parts })) : [];
    const machineCards = values.resources.available ? deriveMachineCards({
      machines: values.resources.value.machines ?? [], workCenters: values.resources.value.workCenters ?? [],
      operations: values.operations.available ? values.operations.value : [],
      activeWork: values.activeWork.available ? values.activeWork.value : [],
      schedule: values.schedule.available ? values.schedule.value : null,
      downtime: values.downtime.available ? values.downtime.value : [],
      assets: values.assets.available ? values.assets.value : [], maintenanceOrders,
      workers: values.workers.available ? values.workers.value : [],
      qualifications: values.qualifications.available ? values.qualifications.value : [],
      permissions: profile?.permissions ?? [], now: Date.now(),
      errors: {
        resources: !values.assets.available || !values.downtime.available || !values.maintenance.available,
        operations: !values.operations.available, activeWork: !values.activeWork.available,
      },
    }) : [];
    const signalCounts = Object.fromEntries(['ready', 'attention', 'blocked', 'unknown'].map(status => [status, machineCards.filter(card => card.status === status).length]));
    for (const [status, count] of Object.entries(signalCounts)) metrics[`machineSignal.${status}`] = count;
    if (values.inventory.available) metrics.inventoryPositions = Array.isArray(values.inventory.value) ? values.inventory.value.length : 0;
    if (values.quality.available) metrics.qualityDue = Array.isArray(values.quality.value) ? values.quality.value.length : 0;
    if (values.maintenance.available) metrics.openMaintenanceOrders = Array.isArray(values.maintenance.value) ? values.maintenance.value.length : 0;
    if (values.downtime.available) metrics.openDowntimeRecords = Array.isArray(values.downtime.value) ? values.downtime.value.filter(item => !item.endsAt).length : 0;
    if (values.procurement.available) metrics.openSensorPurchaseLines = values.procurement.value.flatMap(order => (order.lines ?? [])
      .filter(line => order.status === 'Approved' && line.status === 'Open' && Number(line.open) > 0 && line.partNumber === 'WW-SENS-PROX-05')).length;
    if (values.holds.available) metrics.activeHolds = Array.isArray(values.holds.value) ? values.holds.value.filter(item => item.active !== false).length : 0;
    if (values.unavailability.available) metrics.unavailabilityWindows = Array.isArray(values.unavailability.value) ? values.unavailability.value.length : 0;
    const motor = values.inventory.available ? values.inventory.value.filter(item => item.partNumber === 'WW-MOT-24V-DC' && item.lotNumber === 'LOT-MOT-24V-881') : [];
    const sisal = values.inventory.available ? values.inventory.value.filter(item => item.partNumber === 'WW-SISAL-SPOOL-250M') : [];
    const releasedOrderOperations = values.orders.available && values.operations.available ? values.orders.value.filter(order => order.status === 'Released').map(order => {
      const open = (order.operations ?? []).filter(operation => operation.status !== 'Complete');
      return { orderNumber: order.orderNumber, operations: open.map(operation => {
        const answer = values.operations.value.find(item => item.orderNumber === order.orderNumber && item.sequence === operation.sequence);
        return { sequence: operation.sequence, executable: answer?.executable === true, primaryConstraint: answer?.primaryConstraint ?? (answer ? null : 'No readiness answer was returned') };
      }) };
    }) : null;
    return { siteCode: 'WW-P01', capturedAt: new Date().toISOString(), metrics,
      details: {
        cnc01OpenDowntime: values.downtime.available && values.downtime.value.some(item => item.machineCode === 'CNC-01' && !item.endsAt),
        machineSignals: machineCards.map(card => ({ machineCode: card.machine.code, workCenterCode: card.machine.workCenterCode, status: card.status, summary: card.summary, blockers: card.blockers.map(blocker => blocker.text) })),
        machineAssets: values.resources.available ? (values.resources.value.machines ?? []).map(machine => ({
          machineCode: machine.code, active: machine.active === true,
          assetStatus: values.assets.available ? values.assets.value.find(asset => asset.machineCode === machine.code)?.status ?? null : null,
        })) : null,
        operationBlocks: values.operations.available ? values.operations.value.filter(operation => !operation.executable)
          .flatMap(operation => (operation.dimensions ?? []).filter(dimension => ['Fail', 'Indeterminate'].includes(dimension.outcome))
            .map(dimension => ({ orderNumber: operation.orderNumber, sequence: operation.sequence, dimension: dimension.name }))) : null,
        activeHolds: values.holds.available ? values.holds.value.filter(hold => hold.active !== false).map(({ kind, target, reference }) => ({ kind, target, reference })) : null,
        releasedOrderOperations,
        motorLot: values.inventory.available ? motor.map(({ partNumber, lotNumber, locationCode, qualityStatus, quantity }) => ({ partNumber, lotNumber, locationCode, status: qualityStatus, quantity })) : null,
        sisalLots: values.inventory.available ? sisal.filter(item => ['LOT-SISAL-M01', 'LOT-SISAL-M01-A', 'LOT-SISAL-M01-B', 'LOT-SISAL-M01-C'].includes(item.lotNumber)).map(({ lotNumber, locationCode, qualityStatus, quantity }) => ({ lotNumber, locationCode, status: qualityStatus, quantity })) : null,
        sisalPositions: values.inventory.available ? sisal.filter(item => ['LOT-SISAL-M01', 'LOT-SISAL-M01-A', 'LOT-SISAL-M01-B', 'LOT-SISAL-M01-C'].includes(item.lotNumber)).map(({ lotNumber, locationCode, qualityStatus, quantity }) => ({ lotNumber, locationCode, qualityStatus, quantity })) : null,
        sisalLedger: values.sisalLedger.available ? values.sisalLedger.value.filter(item => ['Split', 'Scrap'].includes(item.kind) && ['LOT-SISAL-M01', 'LOT-SISAL-M01-A', 'LOT-SISAL-M01-B', 'LOT-SISAL-M01-C'].includes(item.lotNumber)).map(({ transactionId, kind, quantity, fromLocation, toLocation, lotNumber, at }) => ({ transactionId, kind, quantity, fromLocation, toLocation, lotNumber, at })) : null,
        workers: values.workers.available ? values.workers.value.map(({ personId, isActive, shiftCode, homeWorkCenterCode, presenceKind, presentUntil }) => ({ personId, isActive, shiftCode, homeWorkCenterCode, presenceKind, presentUntil })) : null,
        workforceAssignments: values.workers.available ? values.workers.value.map(({ personId, displayName, assignment, presenceKind, presentUntil }) => ({ personId, displayName, assignment, presenceKind, presentUntil })) : null,
        qualifications: values.qualifications.available ? values.qualifications.value.map(({ personId, code, status, expiresAt }) => ({ personId, code, status, expiresAt })) : null,
        unavailability: values.unavailability.available ? values.unavailability.value.map(({ personId, kind, startsAt, endsAt }) => ({ personId, kind, startsAt, endsAt })) : null,
        sensorPurchaseLines: values.procurement.available ? values.procurement.value.flatMap(order => (order.lines ?? [])
          .filter(line => line.partNumber === 'WW-SENS-PROX-05')
          .map(line => ({ poNumber: order.poNumber, orderStatus: order.status, lineNumber: line.lineNumber, status: line.status, open: line.open, promisedDate: line.promisedDate }))) : null,
        sensorMrp: values.mrp.available ? {
          item: values.mrp.value.output?.items?.find(item => item.partNumber === 'WW-SENS-PROX-05') ?? null,
          exceptions: (values.mrp.value.output?.exceptions ?? []).filter(item => item.partNumber === 'WW-SENS-PROX-05'),
        } : null,
        gaugeCalibrations: values.gauges.available ? values.gauges.value.filter(gauge => ['GAUGE-TRQ-04', 'GAUGE-OPT-01'].includes(gauge.code))
          .map(({ code, calibrationDue, certificate }) => ({ code, calibrationDue, certificate })) : null,
        recoveryActions: values.recoveryActions.available ? values.recoveryActions.value : null,
        shiftHandoff: values.s15Handoff.available ? values.s15Handoff.value : null,
        cascadeMachineReadiness: {
          CNC01: values.cnc01Readiness.available ? values.cnc01Readiness.value.output : null,
          ASM03: values.asm03Readiness.available ? values.asm03Readiness.value.output : null,
        },
      },
      sources: Object.fromEntries(Object.entries(values).map(([name, result]) => [name, result.available ? 'available' : 'unavailable under current permissions or source state'])) };
  }

  async function captureDemoBaseline() {
    try {
      demoBaseline = await captureDemoState();
      demoBaseline.healthyBaselineAssertions = healthyBaselineAssertions(demoBaseline);
      sessionStorage.setItem('forge.whiskerworks.baseline.v1', JSON.stringify(demoBaseline));
      demoComparison = null;
      render();
      toast('WhiskerWorks baseline snapshot captured in this browser session.');
    } catch (error) { toast(error.message, true); }
  }

  async function compareDemoBaseline() {
    if (!demoBaseline) { toast('Capture a WhiskerWorks baseline snapshot first.', true); return; }
    try {
      demoComparison = { baseline: demoBaseline, current: await captureDemoState() };
      render();
    } catch (error) { toast(error.message, true); }
  }

  async function exportDemoEvidence() {
    try {
      const current = await captureDemoState();
      const assertions = demoEvents.flatMap(event => event.assertionKeys.map(key => {
        let outcome = 'Indeterminate';
        let detail = 'Required source was unavailable under the current permissions or source state.';
        if (key === 'Local scanner failure and pending label reconciliation workflow armed') {
          const rehearsal = readDemoScannerOutage();
          const complete = rehearsal?.scanner?.status === 'failed' && rehearsal?.label?.status === 'reconciled';
          outcome = complete ? 'Pass' : rehearsal ? 'Fail' : 'Indeterminate';
          detail = complete ? `Local rehearsal complete: barcode staging simulated HTTP ${rehearsal.scanner.responseStatus}; label status reconciled as ${rehearsal.label.observedOutcome} by ${rehearsal.label.observedBy}. No source-changing API command or physical-device request was used.`
            : rehearsal ? `Local rehearsal is incomplete: barcode=${rehearsal.scanner.status}, label=${rehearsal.label.status}. No device integration is asserted.`
              : 'The browser-session rehearsal was cleared or is unavailable. No device integration is asserted.';
        }
        if (key === 'CNC-01 downtime is open' && current.sources.downtime === 'available') {
          outcome = current.details.cnc01OpenDowntime ? 'Pass' : 'Fail'; detail = current.details.cnc01OpenDowntime ? 'An open CNC-01 downtime record is present.' : 'No open CNC-01 downtime record was found.';
        } else if (key === 'Sensor purchase promise moved four days' && current.sources.procurement === 'available') {
          const target = event.targetGroups ?? {};
          const line = current.details.sensorPurchaseLines?.find(row => Number(row.poNumber) === Number(target.poNumber)
            && Number(row.lineNumber) === Number(target.lineNumber));
          const pass = line?.orderStatus === 'Approved' && line.status === 'Open' && Number(line.open) > 0 && line.promisedDate === target.promisedTo;
          outcome = pass ? 'Pass' : 'Fail';
          detail = line ? `PO ${line.poNumber}/${line.lineNumber} is ${line.orderStatus}/${line.status}, open ${line.open}, promised ${line.promisedDate}; expected ${target.promisedTo}.`
            : `PO ${target.poNumber}/${target.lineNumber} is no longer visible in the current authorized source read.`;
        } else if (key === 'Delayed sensor receipt is reflected in MRP' && current.sources.procurement === 'available' && current.sources.mrp === 'available') {
          const target = event.targetGroups ?? {};
          const item = current.details.sensorMrp?.item;
          const receipt = item?.receipts?.find(row => row.key === `PO${target.poNumber}/${target.lineNumber}`);
          const exception = (current.details.sensorMrp?.exceptions ?? []).find(row => row.kind === 'RescheduleIn'
            && row.detail.includes(`PO ${target.poNumber} line ${target.lineNumber}`));
          const requirementBeforeReceipt = !!receipt && (item?.requirements ?? []).some(row => row.needDate < receipt.dueDate);
          const pass = receipt?.kind === 'PurchaseOrder' && receipt.dueDate === target.promisedTo && requirementBeforeReceipt && !!exception;
          outcome = pass ? 'Pass' : 'Fail';
          detail = `MRP ${receipt ? `reads PO ${target.poNumber}/${target.lineNumber} for ${receipt.quantity} due ${receipt.dueDate}` : 'does not show the target sensor PO receipt'}; `
            + `${requirementBeforeReceipt ? 'at least one requirement precedes the receipt' : 'no earlier requirement is established'}; `
            + `${exception ? 'a RescheduleIn exception cites that exact PO line' : 'no RescheduleIn exception cites that exact PO line'}.`;
        } else if (key === 'Motor lot is on inventory hold' && current.sources.inventory === 'available') {
          const held = current.details.motorLot?.filter(lot => lot.status === 'Hold').reduce((sum, lot) => sum + Number(lot.quantity), 0) ?? 0;
          outcome = held >= 500 ? 'Pass' : 'Fail'; detail = `${held} units of the named motor lot are on Hold.`;
        } else if (key === 'Sisal child-lot quantity reconciles' && current.sources.inventory === 'available' && current.sources.sisalLedger === 'available') {
          const evidence = sisalQuantityEvidence({ positions: current.details.sisalPositions ?? [], transactions: current.details.sisalLedger ?? [] });
          outcome = evidence.pass ? 'Pass' : 'Fail';
          detail = `Released child quantity ${evidence.childQuantity} m; split ledger ${evidence.splitQuantity} m; parent scrap ledger ${evidence.scrappedQuantity} m; released parent balance ${evidence.parentReleased} m.`;
        } else if (key === 'Three callouts and two workers not present' && current.sources.workers === 'available' && current.sources.unavailability === 'available') {
          const { calloutIds = [], missingIds = [], startsAt, endsAt } = event.targetGroups ?? {};
          const callouts = current.details.unavailability.filter(row => calloutIds.includes(row.personId) && row.kind === 'CallOut'
            && (!startsAt || Date.parse(row.startsAt) < Date.parse(endsAt) && Date.parse(row.endsAt) > Date.parse(startsAt))).length;
          const presentIds = new Set(current.details.workers.filter(worker => worker.presentUntil && Date.parse(worker.presentUntil) > Date.now()).map(worker => worker.personId));
          const missingPresent = missingIds.filter(id => presentIds.has(id)).length;
          outcome = callouts === 3 && missingPresent === 0 ? 'Pass' : 'Fail'; detail = `${callouts}/3 callout windows found; ${missingPresent}/2 expected-missing workers still have current presence.`;
        } else if (key === 'ASM-03 two-person safety coverage is below minimum' && current.sources.workers === 'available'
            && current.sources.qualifications === 'available' && current.sources.unavailability === 'available') {
          const { calloutIds = [], qualifiedCrewIds = [], startsAt, endsAt } = event.targetGroups ?? {};
          const callouts = current.details.unavailability.filter(row => calloutIds.includes(row.personId) && row.kind === 'CallOut'
            && Date.parse(row.startsAt) < Date.parse(endsAt) && Date.parse(row.endsAt) > Date.parse(startsAt)).length;
          const qualified = new Set(current.details.qualifications.filter(row => row.code === 'QUAL-ASM-HVY' && row.status === 'Qualified'
            && (!row.expiresAt || Date.parse(row.expiresAt) > Date.now())).map(row => row.personId));
          const staffed = current.details.workforceAssignments.filter(worker => worker.assignment?.machineCode === 'ASM-03'
            && worker.presentUntil && Date.parse(worker.presentUntil) > Date.now() && qualified.has(worker.personId)).length;
          const targetRemains = calloutIds.some(id => current.details.workforceAssignments.some(worker => worker.personId === id
            && worker.presentUntil && Date.parse(worker.presentUntil) > Date.now()));
          outcome = callouts > 0 && !targetRemains && staffed < 2 && qualifiedCrewIds.length === 2 ? 'Pass' : 'Fail';
          detail = `${callouts}/1 qualifying ASM-03 callout found; ${staffed} currently present independently qualified ASM-03 operators remain.`;
        } else if (key === 'Torque and optical gauges are past due' && current.sources.gauges === 'available') {
          const expected = ['GAUGE-TRQ-04', 'GAUGE-OPT-01'];
          const siteToday = event.targetGroups?.siteToday;
          const gauges = current.details.gaugeCalibrations ?? [];
          const expired = expected.filter(code => gauges.some(gauge => gauge.code === code && siteToday && gauge.calibrationDue < siteToday));
          outcome = expired.length === expected.length ? 'Pass' : 'Fail';
          detail = `${expired.length}/2 target gauges are past due as of ${siteToday ?? 'the recorded site date'}.`;
        } else if (key === 'Recovery action dependency and due dates are recorded' && current.sources.recoveryActions === 'available') {
          const { actionIds = [], blockedByActionId, firstDueAt, secondDueAt } = event.targetGroups ?? {};
          const first = current.details.recoveryActions?.find(action => Number(action.actionId) === Number(actionIds[0]));
          const second = current.details.recoveryActions?.find(action => Number(action.actionId) === Number(actionIds[1]));
          const dependency = (second?.blockedByActionIds ?? []).some(id => Number(id) === Number(blockedByActionId));
          const datesMatch = first?.dueAt && second?.dueAt && Date.parse(first.dueAt) === Date.parse(firstDueAt) && Date.parse(second.dueAt) === Date.parse(secondDueAt);
          const sourceMatch = first?.sourceKind === 'Machine' && first.sourceReference === 'CNC-01'
            && second?.sourceKind === 'Machine' && second.sourceReference === 'CNC-01';
          outcome = dependency && datesMatch && sourceMatch ? 'Pass' : 'Fail';
          detail = `Saved actions ${actionIds.join(' and ')} ${dependency ? 'preserve' : 'do not preserve'} the CNC-01 dependency; due dates ${datesMatch ? 'match' : 'differ from'} the scenario record.`;
        } else if (key === 'Outgoing handoff includes open downtime and recorded worker absences' && current.sources.s15Handoff === 'available') {
          const saved = current.details.shiftHandoff?.handoff;
          const facts = saved?.facts ?? saved?.snapshot?.facts ?? [];
          const downtime = facts.some(fact => fact.kind === 'Downtime' && fact.reference.includes('CNC-01') && fact.status === 'Open');
          const absence = facts.some(fact => fact.kind === 'WorkerAvailability' && ['CallOut', 'ApprovedPto'].includes(fact.status));
          const reviewed = !!saved?.reviewedAt;
          outcome = reviewed && downtime && absence ? 'Pass' : 'Fail';
          detail = `Handoff ${saved?.handoffId ?? 'not found'} is ${reviewed ? 'reviewed' : 'not reviewed'}; CNC-01 downtime ${downtime ? 'is' : 'is not'} present, and a recorded absence ${absence ? 'is' : 'is not'} present in its source facts.`;
        } else if (key === 'Incoming worker independently acknowledged reviewed handoff' && current.sources.s15Handoff === 'available') {
          const { handoffId, incomingPersonId } = event.targetGroups ?? {};
          const saved = current.details.shiftHandoff?.handoff;
          const acknowledgments = current.details.shiftHandoff?.acknowledgments ?? [];
          const ack = acknowledgments.find(item => Number(item.handoffId) === Number(handoffId)
            && String(item.personId).toLowerCase() === String(incomingPersonId).toLowerCase());
          const independent = !!ack && String(incomingPersonId).toLowerCase() !== String(event.actorPersonId ?? '').toLowerCase()
            && !!saved?.reviewedAt;
          outcome = independent ? 'Pass' : 'Fail';
          detail = independent ? `Incoming worker ${incomingPersonId} acknowledged reviewed handoff ${handoffId}.`
            : `No independent acknowledgment by ${incomingPersonId} is recorded for reviewed handoff ${handoffId}.`;
        } else if (key === 'Cascade source events produce the expected blocking constraints'
            && ['resources', 'assets', 'operations', 'activeWork', 'maintenance', 'downtime', 'inventory', 'workers', 'unavailability', 'cnc01Readiness', 'asm03Readiness'].every(source => current.sources[source] === 'available')) {
          const { calloutIds = [], missingIds = [], qualifiedCrewIds = [], startsAt, endsAt } = event.targetGroups ?? {};
          const callouts = current.details.unavailability.filter(row => calloutIds.includes(row.personId) && row.kind === 'CallOut'
            && Date.parse(row.startsAt) < Date.parse(endsAt) && Date.parse(row.endsAt) > Date.parse(startsAt)).length;
          const presentIds = new Set(current.details.workers.filter(worker => worker.presentUntil && Date.parse(worker.presentUntil) > Date.now()).map(worker => worker.personId));
          const missingPresent = missingIds.filter(id => presentIds.has(id)).length;
          const held = current.details.motorLot?.filter(lot => lot.status === 'Hold').reduce((sum, lot) => sum + Number(lot.quantity), 0) ?? 0;
          const cncReadiness = current.details.cascadeMachineReadiness?.CNC01;
          const asm03Readiness = current.details.cascadeMachineReadiness?.ASM03;
          const cncBlocked = cncReadiness?.executable === false && cncReadiness?.primaryConstraint === 'Equipment' && current.details.cnc01OpenDowntime;
          const asm03Safety = (asm03Readiness?.dimensions ?? []).some(dimension => dimension.name === 'Safety' && dimension.outcome === 'Fail');
          const qualifiedCrew = new Set((current.details.qualifications ?? []).filter(row => row.code === 'QUAL-ASM-HVY' && row.status === 'Qualified'
            && (!row.expiresAt || Date.parse(row.expiresAt) > Date.now())).map(row => row.personId));
          const crewStillQualified = qualifiedCrewIds.length === 2 && qualifiedCrewIds.every(id => qualifiedCrew.has(id));
          const oneCrewCallout = calloutIds.filter(id => qualifiedCrewIds.includes(id)).length === 1;
          const asm03Blocked = asm03Readiness?.executable === false && asm03Readiness?.primaryConstraint === 'Safety' && asm03Safety
            && crewStillQualified && oneCrewCallout;
          const pass = callouts === 3 && missingPresent === 0 && cncBlocked && asm03Blocked && held === 500;
          outcome = pass ? 'Pass' : 'Fail';
          detail = `${callouts}/3 recorded callouts; ${missingPresent}/2 expected-missing workers have presence; one of the two independently qualified ASM-03 operators called out: ${oneCrewCallout && crewStillQualified}; CNC-01 ${cncBlocked ? 'blocked' : 'not proven blocked'}, ASM-03 ${asm03Blocked ? 'blocked' : 'not proven blocked'}, and ${held}/500 motor units held.`;
        }
        return { scenarioCode: event.code, recordedAt: event.recordedAt, check: key, outcome, detail };
      }));
      const bundle = { kind: 'WhiskerWorks source-record evidence snapshot', fictional: true, baseline: demoBaseline,
        healthyBaselineAssertions: demoBaseline ? demoBaseline.healthyBaselineAssertions ?? healthyBaselineAssertions(demoBaseline) : null,
        currentHealthyBaselineAssertions: healthyBaselineAssertions(current), current, demoActions: demoEvents, assertions,
        demoScannerOutage: readDemoScannerOutage(),
        notes: ['Forge facts are derived from authorized API responses.', 'Unavailable sources were not inferred.', 'The S12 scanner/label exercise is browser-session-only and does not represent a live scanner, printer, API write, audit, or production transaction.', 'Assertions cover only the explicit source checks listed here.', 'Healthy-baseline checks evaluate captured source facts and are not a complete S01–S16 acceptance suite.', 'The supplier promise-date assertion verifies the current PO line, not external supplier correspondence.', 'This bundle does not contain a simulated clock or demo-wide reset state.'] };
      const blob = new Blob([JSON.stringify(bundle, null, 2)], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url; link.download = `whiskerworks-evidence-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
      document.body.append(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (error) { toast(error.message, true); }
  }

  function saveDemoEvents(events) {
    demoEvents = events;
    try { sessionStorage.setItem('forge.whiskerworks.events.v1', JSON.stringify(events)); } catch { /* audit/source APIs remain authoritative */ }
  }

  function actionKey(code, target) {
    const day = new Date().toISOString().slice(0, 10);
    return `ww-sprint9-${code.toLowerCase()}-${day}-${profile?.personId ?? 'user'}-${target}`;
  }

  function recordDemoEvent(code, summary, assertionKeys = [], targets = [], targetGroups = null) {
    const scenario = scenarioCatalog.find(item => item.code === code);
    const events = [...demoEvents, {
      code, title: scenario?.title ?? code, summary, assertionKeys, targets, targetGroups,
      recordedAt: new Date().toISOString(), actorPersonId: profile?.personId ?? null,
    }].slice(-100);
    saveDemoEvents(events);
  }

  const postDemo = (path, body, reason, key) => command(path, body, reason, { idempotencyKey: key });

  async function readDemoQualifications(workers) {
    if (!(profile?.permissions ?? []).includes('competency.read')) throw new Error('Competency read permission is required for this scenario.');
    const query = new URLSearchParams();
    for (const worker of workers ?? []) if (worker.personId) query.append('personIds', worker.personId);
    if (!query.size) return [];
    return get(`/api/competency/people/qualifications?${query}`);
  }

  async function ensureCallout(worker, reason, code, startsAt, endsAt) {
    const from = new Date(startsAt.getTime() - 60 * 60 * 1000).toISOString();
    const to = new Date(endsAt.getTime() + 60 * 60 * 1000).toISOString();
    const existing = await get(`/api/workforce/unavailability?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`);
    const matching = existing.find(item => item.personId === worker.personId && Date.parse(item.startsAt) < endsAt.getTime() && Date.parse(item.endsAt) > startsAt.getTime());
    if (matching?.kind === 'CallOut') return { alreadyRecorded: true };
    if (matching) throw new Error(`${worker.displayName} already has overlapping ${matching.kind} unavailability; no second window was added.`);
    return postDemo(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/unavailability`, {
      kind: 'CallOut', startsAt: startsAt.toISOString(), endsAt: endsAt.toISOString(),
    }, reason, actionKey(code, `callout-${worker.personId}`));
  }

  async function endPresenceIfNeeded(worker, reason, code) {
    if (!worker.presentUntil || Date.parse(worker.presentUntil) <= Date.now()) return { alreadyAbsent: true };
    return postDemo(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/departure`, {}, reason, actionKey(code, `departure-${worker.personId}`));
  }

  async function startShiftAbsences(reason, code = 'WW-S02-ABSENCES') {
    const { callouts, missing } = selectDayShiftScenarioRoster(await get('/api/workforce/workers'));
    const now = new Date();
    const endsAt = new Date(now.getTime() + 8 * 60 * 60 * 1000);
    for (const worker of callouts) {
      await ensureCallout(worker, reason, code, now, endsAt);
      await endPresenceIfNeeded(worker, reason, code);
    }
    for (const worker of missing) await endPresenceIfNeeded(worker, reason, code);
    return { callouts, missing, startsAt: now, endsAt };
  }

  async function startCascadeAbsences(reason, code = 'WW-S16-CASCADE') {
    const workers = await get('/api/workforce/workers');
    const qualifications = await readDemoQualifications(workers);
    const progress = [...demoEvents].reverse().find(event => event.code === code && event.targetGroups?.phase === 'workforce');
    let callouts, missing, crew, startsAt, endsAt;
    if (progress) {
      const byId = new Map(workers.map(worker => [worker.personId, worker]));
      callouts = (progress.targetGroups.calloutIds ?? []).map(id => byId.get(id)).filter(Boolean);
      missing = (progress.targetGroups.missingIds ?? []).map(id => byId.get(id)).filter(Boolean);
      crew = (progress.targetGroups.qualifiedCrewIds ?? []).map(id => byId.get(id)).filter(Boolean);
      startsAt = new Date(progress.targetGroups.startsAt);
      endsAt = new Date(progress.targetGroups.endsAt);
      const qualified = new Set(qualifications.filter(row => row.code === 'QUAL-ASM-HVY' && row.status === 'Qualified'
        && (!row.expiresAt || Date.parse(row.expiresAt) > Date.now())).map(row => row.personId));
      if (callouts.length !== 3 || missing.length !== 2 || crew.length !== 2 || !Number.isFinite(startsAt.getTime())
        || !Number.isFinite(endsAt.getTime()) || !crew.every(worker => qualified.has(worker.personId)
          && worker.assignment?.machineCode === 'ASM-03') || callouts.filter(worker => crew.some(person => person.personId === worker.personId)).length !== 1) {
        throw new Error('The saved cascade workforce step no longer matches its two qualified ASM-03 operators. No additional events were recorded.');
      }
    } else {
      const { worker: asm03Callout, crew: selectedCrew } = selectAsm03Callout(workers, qualifications);
      const roster = selectDayShiftScenarioRoster(workers);
      const scheduled = workers.filter(worker => worker.isActive && worker.shiftCode === 'SHIFT-DAY')
        .sort((left, right) => String(left.displayName).localeCompare(String(right.displayName)) || String(left.personId).localeCompare(String(right.personId)));
      callouts = [asm03Callout];
      for (const candidate of [...roster.callouts, ...scheduled]) {
        if (callouts.length === 3) break;
        if (!callouts.some(worker => worker.personId === candidate.personId)) callouts.push(candidate);
      }
      missing = [];
      for (const candidate of [...roster.missing, ...scheduled]) {
        if (missing.length === 2) break;
        if (!callouts.some(worker => worker.personId === candidate.personId) && !missing.some(worker => worker.personId === candidate.personId)) missing.push(candidate);
      }
      if (callouts.length !== 3 || missing.length !== 2) throw new Error('The cascade requires three distinct callouts and two other scheduled workers. No workforce event was recorded.');
      crew = selectedCrew;
      startsAt = new Date();
      endsAt = new Date(startsAt.getTime() + 8 * 60 * 60 * 1000);
      recordDemoEvent(code, 'Cascade workforce step reserved; applying the recorded callout and departure commands', [], [], {
        phase: 'workforce', calloutIds: callouts.map(worker => worker.personId), missingIds: missing.map(worker => worker.personId),
        qualifiedCrewIds: crew.map(worker => worker.personId), startsAt: startsAt.toISOString(), endsAt: endsAt.toISOString(),
      });
    }
    for (const worker of callouts) {
      await ensureCallout(worker, reason, code, startsAt, endsAt);
      await endPresenceIfNeeded(worker, reason, code);
    }
    for (const worker of missing) await endPresenceIfNeeded(worker, reason, code);
    return { callouts, missing, crew, startsAt, endsAt };
  }

  async function reportCncDown(reason, code = 'WW-S04-CNC-DOWN') {
    const now = new Date();
    const downtime = await get(`/api/maintenance/downtime?from=${encodeURIComponent(new Date(now.getTime() - 14 * 86400000).toISOString())}&to=${encodeURIComponent(new Date(now.getTime() + 86400000).toISOString())}`);
    const active = downtime.find(item => item.machineCode === 'CNC-01' && !item.endsAt);
    if (active) return active;
    return postDemo('/api/maintenance/machines/CNC-01/down', { description: 'Fictional demo event: unplanned spindle coolant failure' }, reason, actionKey(code, 'CNC-01'));
  }

  async function holdMotorLot(reason, code = 'WW-S06-MRB-HOLD') {
    const positions = await get('/api/inventory/positions');
    const lotPositions = positions.filter(item => item.partNumber === 'WW-MOT-24V-DC' && item.lotNumber === 'LOT-MOT-24V-881');
    const alreadyHeld = lotPositions.filter(item => item.qualityStatus === 'Hold').reduce((sum, item) => sum + Number(item.quantity), 0);
    const available = lotPositions.filter(item => item.qualityStatus === 'Released' && item.locationCode === 'WW-RAW').reduce((sum, item) => sum + Number(item.quantity), 0);
    const remaining = 500 - alreadyHeld;
    if (remaining <= 0) return { alreadyHeld };
    if (available < remaining) throw new Error(`The seeded motor lot has ${available} released units at WW-RAW and ${alreadyHeld} on hold; refusing to invent or over-hold stock.`);
    return postDemo('/api/inventory/holds', { partNumber: 'WW-MOT-24V-DC', quantity: remaining, locationCode: 'WW-RAW', lotNumber: 'LOT-MOT-24V-881', status: 'Released' }, reason, actionKey(code, 'LOT-MOT-24V-881'));
  }

  async function delaySensorPromise(reason, code = 'WW-S07-SHORTAGE') {
    const purchaseOrders = await get('/api/procurement/purchase-orders');
    const prior = demoEvents.find(event => event.code === code);
    if (prior) {
      const target = prior.targetGroups ?? {};
      const line = purchaseOrders.flatMap(order => (order.lines ?? []).map(candidate => ({ order, line: candidate })))
        .find(candidate => Number(candidate.order.poNumber) === Number(target.poNumber) && Number(candidate.line.lineNumber) === Number(target.lineNumber));
      if (line?.line.promisedDate === target.promisedTo) return { ...target, alreadyRecorded: true };
      throw new Error('A sensor promise-date change is already recorded in this browser session. The source line changed since then; refusing to extend another line.');
    }
    const { order, line } = selectOpenSensorPromiseLine(purchaseOrders);
    const promisedTo = delayedPromiseDate(line.promisedDate, 4);
    await postDemo(`/api/procurement/purchase-orders/${order.poNumber}/lines/${line.lineNumber}/date`, { promisedDate: promisedTo }, reason,
      `ww-sprint9-${code.toLowerCase()}-po-${order.poNumber}-line-${line.lineNumber}`);
    return { poNumber: order.poNumber, lineNumber: line.lineNumber, promisedFrom: line.promisedDate, promisedTo };
  }

  async function splitSisalLot(reason, code = 'WW-S08-LOT-SPLIT') {
    const positions = await get('/api/inventory/positions');
    const rows = positions.filter(item => item.partNumber === 'WW-SISAL-SPOOL-250M' && item.locationCode === 'WW-RAW' && item.qualityStatus === 'Released');
    const quantity = lot => rows.filter(item => item.lotNumber === lot).reduce((sum, item) => sum + Number(item.quantity), 0);
    const parent = quantity('LOT-SISAL-M01');
    const children = ['LOT-SISAL-M01-A', 'LOT-SISAL-M01-B', 'LOT-SISAL-M01-C'];
    const childTotal = children.reduce((sum, lot) => sum + quantity(lot), 0);
    const steps = sisalSplitSteps(parent, childTotal);
    if (steps.includes('split')) {
      await postDemo('/api/inventory/splits', { partNumber: 'WW-SISAL-SPOOL-250M', lotNumber: 'LOT-SISAL-M01', locationCode: 'WW-RAW', status: 'Released', results: [
        { lotNumber: children[0], quantity: 100 }, { lotNumber: children[1], quantity: 100 }, { lotNumber: children[2], quantity: 45 },
      ] }, reason, actionKey(code, 'split-parent'));
    }
    if (steps.includes('scrap')) await postDemo('/api/inventory/scrap', { partNumber: 'WW-SISAL-SPOOL-250M', quantity: 5, locationCode: 'WW-RAW', lotNumber: 'LOT-SISAL-M01', status: 'Released' }, reason, actionKey(code, 'scrap-remnant'));
    return { children, childTotal: 245, remainingParent: 0, scrap: 5 };
  }

  async function expireDemoGauges(reason, code = 'WW-S11-CAL-EXPIRED') {
    const targetCodes = ['GAUGE-TRQ-04', 'GAUGE-OPT-01'];
    const gauges = await get('/api/quality/gauges');
    const selected = targetCodes.map(code => gauges.find(gauge => gauge.code === code));
    if (selected.some(gauge => !gauge)) throw new Error('The seeded torque and optical gauges are required. No calibration record was changed.');

    const timeZone = resources.sites?.find(site => site.code === 'WW-P01')?.timeZone ?? 'UTC';
    const dateParts = new Intl.DateTimeFormat('en-CA', {
      timeZone, year: 'numeric', month: '2-digit', day: '2-digit',
    }).formatToParts(new Date());
    const part = type => dateParts.find(value => value.type === type)?.value;
    const siteToday = `${part('year')}-${part('month')}-${part('day')}`;
    const dayOffset = (date, offset) => new Date(Date.parse(`${date}T12:00:00Z`) + offset * 86400000).toISOString().slice(0, 10);
    const calibratedOn = dayOffset(siteToday, -2);
    const dueDate = dayOffset(siteToday, -1);
    const changed = [];
    for (const gauge of selected) {
      if (gauge.calibrationDue < siteToday) continue;
      await postDemo(`/api/quality/gauges/${encodeURIComponent(gauge.code)}/calibrations`, {
        calibratedOn, dueDate, certificate: 'WW-DEMO-EXPIRED-CALIBRATION',
      }, reason, actionKey(code, gauge.code));
      changed.push(gauge.code);
    }
    if (!changed.length) return { alreadyExpired: true, gauges: targetCodes, siteToday };
    return { alreadyExpired: false, gauges: targetCodes, changed, calibratedOn, dueDate, siteToday };
  }

  async function createRecoveryBoardScenario(reason, code = 'WW-S14-RECOVERY-BOARD') {
    const workCenterCode = 'WC-CNC-MILL';
    const shiftCode = 'SHIFT-DAY';
    const site = resources.sites?.find(item => item.code === 'WW-P01');
    const shift = resources.shifts?.find(item => item.code === shiftCode);
    const assignee = demoWorkers?.find(worker => worker.homeWorkCenterCode === workCenterCode && worker.shiftCode === shiftCode);
    if (!site || !shift || !assignee?.personId || !(resources.workCenterShifts ?? []).some(item => item.workCenterCode === workCenterCode && item.shiftCode === shiftCode)) {
      throw new Error('A seeded WW-P01 Day Shift worker and CNC Mill shift assignment are required. No handoff was created.');
    }

    const localParts = (date, timeZone, includeTime = false) => new Intl.DateTimeFormat('en-CA', {
      timeZone, year: 'numeric', month: '2-digit', day: '2-digit',
      ...(includeTime ? { hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' } : {}),
    }).formatToParts(date);
    const partValue = (parts, type) => parts.find(part => part.type === type)?.value;
    const dateString = (parts) => `${partValue(parts, 'year')}-${partValue(parts, 'month')}-${partValue(parts, 'day')}`;
    const today = dateString(localParts(new Date(), site.timeZone));
    const dateOffset = (date, days) => new Date(Date.parse(`${date}T12:00:00Z`) + days * 86400000).toISOString().slice(0, 10);
    const isoWeekday = date => new Date(`${date}T12:00:00Z`).getUTCDay() || 7;
    let completedShiftDate = null;
    for (let offset = 1; offset <= 8; offset++) {
      const candidate = dateOffset(today, -offset);
      if ((shift.days ?? []).includes(isoWeekday(candidate))) { completedShiftDate = candidate; break; }
    }
    if (!completedShiftDate) throw new Error('Could not find a completed configured Day Shift window.');

    const localToUtc = (date, clock) => {
      const [year, month, day] = date.split('-').map(Number);
      const [hour, minute, second = 0] = clock.split(':').map(Number);
      const target = Date.UTC(year, month - 1, day, hour, minute, second);
      let guess = target;
      for (let attempt = 0; attempt < 4; attempt++) {
        const parts = localParts(new Date(guess), site.timeZone, true);
        const represented = Date.UTC(Number(partValue(parts, 'year')), Number(partValue(parts, 'month')) - 1,
          Number(partValue(parts, 'day')), Number(partValue(parts, 'hour')), Number(partValue(parts, 'minute')), Number(partValue(parts, 'second')));
        const correction = target - represented;
        guess += correction;
        if (correction === 0) break;
      }
      return new Date(guess).toISOString();
    };
    const shiftFrom = localToUtc(completedShiftDate, shift.start);
    const shiftUntil = new Date(Date.parse(shiftFrom) + shift.durationMinutes * 60000).toISOString();
    const dueDate = dateOffset(today, 1);
    const firstDueAt = localToUtc(dueDate, '12:00:00');
    const secondDueAt = localToUtc(dueDate, '16:00:00');
    const context = 'WW-S14 fictional recovery-board scenario: inspect the CNC-01 coolant follow-up and verify readiness after the blocking action is complete.';

    const handoffs = await get(`/api/execution/shifts/handoffs?workCenter=${encodeURIComponent(workCenterCode)}&limit=100`);
    let handoff = handoffs.find(item => item.context === context && Date.parse(item.shiftFrom) === Date.parse(shiftFrom));
    if (!handoff) {
      const created = await postDemo('/api/execution/shifts/handoffs', {
        workCenterCode, shiftCode, shiftFrom, shiftUntil, context,
      }, reason, actionKey(code, `handoff-${completedShiftDate}`));
      const handoffId = Number(created.entityId);
      if (!Number.isSafeInteger(handoffId) || handoffId <= 0) throw new Error('The recovery handoff command returned no valid handoff ID.');
      handoff = { handoffId, context, shiftFrom, reviewedAt: null };
    }
    const handoffId = Number(handoff.handoffId);
    if (!handoff.reviewedAt) await postDemo(`/api/execution/shifts/handoffs/${handoffId}/review`, {}, reason, actionKey(code, `review-${handoffId}`));

    const actionsPath = `/api/execution/shifts/handoffs/${handoffId}/actions`;
    let actions = await get(actionsPath);
    const firstDescription = 'WW-S14: Inspect CNC-01 coolant follow-up';
    const secondDescription = 'WW-S14: Verify CNC-01 readiness after inspection';
    let first = actions.find(item => item.description === firstDescription);
    if (!first) {
      const created = await postDemo(actionsPath, {
        description: firstDescription, sourceKind: 'Machine', sourceReference: 'CNC-01', assignee: assignee.personId,
        dueAt: firstDueAt, blockedByActionIds: [],
      }, reason, actionKey(code, `first-${handoffId}`));
      const actionId = Number(created.entityId);
      if (!Number.isSafeInteger(actionId) || actionId <= 0) throw new Error('The first recovery action returned no valid action ID.');
      first = { actionId, description: firstDescription, dueAt: firstDueAt, blockedByActionIds: [] };
    }
    const firstId = Number(first.actionId);
    let second = actions.find(item => item.description === secondDescription);
    if (!second) {
      const created = await postDemo(actionsPath, {
        description: secondDescription, sourceKind: 'Machine', sourceReference: 'CNC-01', assignee: assignee.personId,
        dueAt: secondDueAt, blockedByActionIds: [firstId],
      }, reason, actionKey(code, `second-${handoffId}`));
      const actionId = Number(created.entityId);
      if (!Number.isSafeInteger(actionId) || actionId <= 0) throw new Error('The dependent recovery action returned no valid action ID.');
      second = { actionId, description: secondDescription, dueAt: secondDueAt, blockedByActionIds: [firstId] };
    }

    actions = await get(actionsPath);
    first = actions.find(item => item.actionId === firstId);
    second = actions.find(item => item.actionId === Number(second.actionId));
    const hasDependency = (second?.blockedByActionIds ?? []).some(id => Number(id) === firstId);
    const correctDueDates = first?.dueAt && second?.dueAt
      && Date.parse(first.dueAt) === Date.parse(firstDueAt) && Date.parse(second.dueAt) === Date.parse(secondDueAt);
    if (!first || !second || !hasDependency || !correctDueDates) {
      throw new Error('The saved recovery actions do not match the expected due dates and dependency. Review the handoff before retrying.');
    }
    return { handoffId, actionIds: [firstId, Number(second.actionId)], assigneeId: assignee.personId,
      blockedByActionId: firstId, firstDueAt, secondDueAt, workCenterCode, shiftCode, completedShiftDate };
  }

  async function createS15Handoff(reason, code = 'WW-S15-HANDOFF') {
    const workCenterCode = 'WC-CNC-MILL';
    const site = resources.sites?.find(item => item.code === 'WW-P01');
    const timeZone = site?.timeZone ?? 'UTC';
    const shifts = (resources.shifts ?? []).filter(shift => (resources.workCenterShifts ?? [])
      .some(item => item.workCenterCode === workCenterCode && item.shiftCode === shift.code));
    if (!site || !shifts.length) throw new Error('A WW-P01 CNC Mill shift calendar is required. No handoff was created.');

    const now = new Date();
    const todayParts = new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(now);
    const part = type => todayParts.find(value => value.type === type)?.value;
    const today = `${part('year')}-${part('month')}-${part('day')}`;
    const offsetDate = (date, offset) => new Date(Date.parse(`${date}T12:00:00Z`) + offset * 86400000).toISOString().slice(0, 10);
    const weekday = date => new Date(`${date}T12:00:00Z`).getUTCDay() || 7;
    const localToUtc = (date, clock) => {
      const [year, month, day] = date.split('-').map(Number);
      const [hour, minute, second = 0] = clock.split(':').map(Number);
      const target = Date.UTC(year, month - 1, day, hour, minute, second);
      let guess = target;
      for (let attempt = 0; attempt < 4; attempt++) {
        const parts = new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit',
          hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }).formatToParts(new Date(guess));
        const value = type => parts.find(item => item.type === type)?.value;
        const represented = Date.UTC(Number(value('year')), Number(value('month')) - 1, Number(value('day')),
          Number(value('hour')), Number(value('minute')), Number(value('second')));
        const correction = target - represented;
        guess += correction;
        if (correction === 0) break;
      }
      return new Date(guess);
    };

    const workers = await get('/api/workforce/workers');
    const rangeFrom = new Date(now.getTime() - 21 * 86400000).toISOString();
    const rangeTo = new Date(now.getTime() + 86400000).toISOString();
    let [downtime, unavailability] = await Promise.all([
      get(`/api/maintenance/downtime?from=${encodeURIComponent(rangeFrom)}&to=${encodeURIComponent(rangeTo)}`),
      get(`/api/workforce/unavailability?from=${encodeURIComponent(rangeFrom)}&to=${encodeURIComponent(rangeTo)}`),
    ]);
    const openCncDowntime = downtime.filter(record => record.machineCode === 'CNC-01' && !record.endsAt);
    if (!openCncDowntime.length) throw new Error('S15 requires an open CNC-01 downtime record first. Record the source issue through S04; no workforce change was made.');
    const millWorkerIds = new Set(workers.filter(worker => worker.isActive
      && (worker.assignment?.workCenterCode ?? worker.homeWorkCenterCode) === workCenterCode).map(worker => worker.personId));
    let absenceEvent = [...demoEvents].reverse().find(event => event.code === code && ['absence', 'awaiting'].includes(event.targetGroups?.phase));
    if (!absenceEvent) {
      const currentCallout = unavailability.find(row => row.kind === 'CallOut' && millWorkerIds.has(row.personId)
        && Date.parse(row.startsAt) < now.getTime() && Date.parse(row.endsAt) > now.getTime());
      let worker = currentCallout ? workers.find(item => item.personId === currentCallout.personId) : null;
      let startsAt = currentCallout ? new Date(currentCallout.startsAt) : now;
      let endsAt = currentCallout ? new Date(currentCallout.endsAt) : new Date(now.getTime() + 8 * 60 * 60 * 1000);
      if (currentCallout && worker) await endPresenceIfNeeded(worker, reason, code);
      if (!worker) {
        const eligible = workers.filter(item => item.isActive && item.shiftCode === 'SHIFT-DAY' && millWorkerIds.has(item.personId))
          .sort((left, right) => String(left.displayName).localeCompare(String(right.displayName)) || String(left.personId).localeCompare(String(right.personId)));
        worker = eligible.find(candidate => !unavailability.some(row => row.personId === candidate.personId
          && Date.parse(row.startsAt) < endsAt.getTime() && Date.parse(row.endsAt) > startsAt.getTime()));
        if (!worker) throw new Error('No CNC Mill Day Shift worker has an available absence window. No workforce change was made.');
        recordDemoEvent(code, 'S15 absence step reserved; recording a fictional CNC Mill callout and explicit departure', [], [], {
          phase: 'absence', calloutIds: [worker.personId], startsAt: startsAt.toISOString(), endsAt: endsAt.toISOString(),
        });
        absenceEvent = [...demoEvents].reverse().find(event => event.code === code && ['absence', 'awaiting'].includes(event.targetGroups?.phase));
        await ensureCallout(worker, reason, code, startsAt, endsAt);
        await endPresenceIfNeeded(worker, reason, code);
        [downtime, unavailability] = await Promise.all([
          get(`/api/maintenance/downtime?from=${encodeURIComponent(rangeFrom)}&to=${encodeURIComponent(rangeTo)}`),
          get(`/api/workforce/unavailability?from=${encodeURIComponent(rangeFrom)}&to=${encodeURIComponent(rangeTo)}`),
        ]);
      }
    }
    if (absenceEvent) {
      const worker = workers.find(item => item.personId === absenceEvent.targetGroups?.calloutIds?.[0]);
      if (!worker) throw new Error('The saved S15 worker is no longer registered. No additional workforce event was recorded.');
      const startsAt = new Date(absenceEvent.targetGroups.startsAt);
      const endsAt = new Date(absenceEvent.targetGroups.endsAt);
      if (!Number.isFinite(startsAt.getTime()) || !Number.isFinite(endsAt.getTime()) || endsAt <= startsAt)
        throw new Error('The saved S15 absence window is invalid. No additional workforce event was recorded.');
      await ensureCallout(worker, reason, code, startsAt, endsAt);
      await endPresenceIfNeeded(worker, reason, code);
      unavailability = await get(`/api/workforce/unavailability?from=${encodeURIComponent(rangeFrom)}&to=${encodeURIComponent(rangeTo)}`);
    }
    const cncDowntime = downtime.filter(record => record.machineCode === 'CNC-01' && !record.endsAt);
    const callouts = unavailability.filter(row => row.kind === 'CallOut' && millWorkerIds.has(row.personId));
    const candidates = [];
    for (let offset = 0; offset <= 21; offset++) {
      const date = offsetDate(today, -offset);
      for (const shift of shifts) {
        if (!(shift.days ?? []).includes(weekday(date))) continue;
        const shiftFrom = localToUtc(date, shift.start);
        const shiftUntil = new Date(shiftFrom.getTime() + Number(shift.durationMinutes) * 60000);
        if (shiftUntil > now) continue;
        const hasDowntime = cncDowntime.some(record => Date.parse(record.startsAt) < shiftUntil.getTime());
        const hasCallout = callouts.some(record => Date.parse(record.startsAt) < shiftUntil.getTime()
          && Date.parse(record.endsAt) > shiftFrom.getTime());
        if (hasDowntime && hasCallout) candidates.push({ shift, date, shiftFrom, shiftUntil });
      }
    }
    candidates.sort((left, right) => right.shiftUntil - left.shiftUntil);
    const selected = candidates[0];
    if (!selected) return { awaitingShiftEnd: true, calloutIds: callouts.map(row => row.personId),
      nextReviewAt: callouts.map(row => Date.parse(row.endsAt)).filter(Number.isFinite).sort((a, b) => a - b)[0] ?? null };

    const context = 'WW-S15 fictional cross-shift review: preserve open CNC-01 downtime and recorded worker absence for incoming-shift acknowledgment.';
    const handoffs = await get(`/api/execution/shifts/handoffs?workCenter=${encodeURIComponent(workCenterCode)}&limit=100`);
    let handoff = handoffs.find(item => item.context === context && Date.parse(item.shiftFrom) === selected.shiftFrom.getTime());
    if (!handoff) {
      const created = await postDemo('/api/execution/shifts/handoffs', {
        workCenterCode, shiftCode: selected.shift.code, shiftFrom: selected.shiftFrom.toISOString(),
        shiftUntil: selected.shiftUntil.toISOString(), context,
      }, reason, actionKey(code, `handoff-${selected.shift.code}-${selected.date}`));
      const handoffId = Number(created.entityId);
      if (!Number.isSafeInteger(handoffId) || handoffId <= 0) throw new Error('The cross-shift handoff command returned no valid handoff ID.');
      handoff = { handoffId, context, reviewedAt: null };
    }
    const handoffId = Number(handoff.handoffId);
    if (!handoff.reviewedAt) await postDemo(`/api/execution/shifts/handoffs/${handoffId}/review`, {}, reason, actionKey(code, `review-${handoffId}`));
    const details = await get(`/api/execution/shifts/handoffs/${handoffId}`);
    const facts = details.handoff?.facts ?? details.handoff?.snapshot?.facts ?? [];
    const hasOpenCnc = facts.some(fact => fact.kind === 'Downtime' && fact.reference.includes('CNC-01') && fact.status === 'Open');
    const hasRecordedAbsence = facts.some(fact => fact.kind === 'WorkerAvailability' && ['CallOut', 'ApprovedPto'].includes(fact.status));
    if (!details.handoff?.reviewedAt || !hasOpenCnc || !hasRecordedAbsence) {
      throw new Error('The saved handoff is not yet reviewable with both required source facts. It remains in Forge; review its source snapshot before retrying.');
    }
    return { handoffId, workCenterCode, shiftCode: selected.shift.code, shiftDate: selected.date,
      shiftFrom: selected.shiftFrom.toISOString(), shiftUntil: selected.shiftUntil.toISOString(), factCount: facts.length };
  }

  async function acknowledgeS15Handoff(reason, code = 'WW-S15-HANDOFF') {
    const event = [...demoEvents].reverse().find(item => item.code === code && Number.isSafeInteger(Number(item.targetGroups?.handoffId)));
    if (!event) throw new Error('Capture and review the outgoing S15 handoff before an incoming worker acknowledges it.');
    const incomingPersonId = profile?.personId;
    if (!incomingPersonId) throw new Error('A registered incoming worker identity is required to acknowledge a shift handoff.');
    if (String(incomingPersonId).toLowerCase() === String(event.actorPersonId ?? '').toLowerCase()) {
      throw new Error('The outgoing handoff creator/reviewer cannot acknowledge it. Sign in as another incoming worker.');
    }
    const handoffId = Number(event.targetGroups.handoffId);
    const path = `/api/execution/shifts/handoffs/${handoffId}`;
    let detail = await get(path);
    const hasAcknowledgment = (detail.acknowledgments ?? []).some(item => String(item.personId).toLowerCase() === String(incomingPersonId).toLowerCase());
    if (!hasAcknowledgment) {
      await postDemo(`${path}/acknowledge`, { note: 'Received by incoming WW-P01 shift; downtime and absence facts reviewed.' }, reason,
        actionKey(code, `ack-${handoffId}-${incomingPersonId}`));
      detail = await get(path);
    }
    const confirmed = (detail.acknowledgments ?? []).some(item => String(item.personId).toLowerCase() === String(incomingPersonId).toLowerCase());
    if (!confirmed || !detail.handoff?.reviewedAt) throw new Error('Forge did not return the incoming acknowledgment for this reviewed handoff.');
    recordDemoEvent(code, `Incoming worker ${incomingPersonId} acknowledged handoff ${handoffId}`,
      ['Incoming worker independently acknowledged reviewed handoff'], [handoffId], {
        handoffId, incomingPersonId, outgoingPersonId: event.actorPersonId, shiftCode: detail.handoff.shiftCode,
      });
  }

  async function runDemoAction(code) {
    if (loading) return;
    const scenario = scenarioCatalog.find(item => item.code === code);
    if (!scenario) return;
    const actions = {
      'WW-S02-ABSENCES': {
        detail: 'Uses the first five stable, active Day Shift roster entries: three receive audited CallOut windows; two remain scheduled but without current presence. Any existing presence is ended explicitly. No attendance or payroll record is fabricated.',
        run: async reason => {
          const { callouts, missing, startsAt, endsAt } = await startShiftAbsences(reason);
          recordDemoEvent(code, '3 callouts and 2 unconfirmed scheduled workers', ['Three callouts and two workers not present'], [...callouts, ...missing].map(worker => worker.personId), {
            calloutIds: callouts.map(worker => worker.personId), missingIds: missing.map(worker => worker.personId), startsAt: startsAt.toISOString(), endsAt: endsAt.toISOString(),
          });
        },
      },
      'WW-S03-TWO-PERSON': {
        detail: 'Records one audited Day Shift callout only when Forge reports exactly two currently present, independently qualified workers assigned to ASM-03. Forge continues to derive machine readiness from its current two-person safety rule.',
        run: async reason => {
          const workers = await get('/api/workforce/workers');
          const qualifications = await readDemoQualifications(workers);
          const { worker, crew } = selectAsm03Callout(workers, qualifications);
          const startsAt = new Date(), endsAt = new Date(startsAt.getTime() + 8 * 60 * 60 * 1000);
          await ensureCallout(worker, reason, code, startsAt, endsAt); await endPresenceIfNeeded(worker, reason, code);
          recordDemoEvent(code, `Callout recorded for ${worker.displayName} (${worker.personId})`, ['ASM-03 two-person safety coverage is below minimum'], [worker.personId], {
            calloutIds: [worker.personId], qualifiedCrewIds: crew.map(person => person.personId), startsAt: startsAt.toISOString(), endsAt: endsAt.toISOString(),
          });
        },
      },
      'WW-S04-CNC-DOWN': {
        detail: 'Creates an unplanned CNC-01 downtime record through Forge Maintenance.',
        run: async reason => { const value = await reportCncDown(reason); recordDemoEvent(code, `CNC-01 downtime ${value?.entityId ?? 'recorded or already open'}`, ['CNC-01 downtime is open'], ['CNC-01']); },
      },
      'WW-S06-MRB-HOLD': {
        detail: 'Moves the exact seeded 500-unit motor lot from WW-RAW Released to Hold. This does not fabricate an inspection failure or NCR.',
        run: async reason => { const value = await holdMotorLot(reason); recordDemoEvent(code, `LOT-MOT-24V-881 held (${value?.message ?? 'already held'})`, ['Motor lot is on inventory hold'], ['WW-MOT-24V-DC/LOT-MOT-24V-881']); },
      },
      'WW-S07-SHORTAGE': {
        detail: 'Changes the earliest visible approved, open purchase line for WW-SENS-PROX-05 to a date four calendar days later. The Procurement command is audited and source-backed. It does not fabricate supplier correspondence; the subsequent MRP result is captured separately.',
        run: async reason => {
          const changed = await delaySensorPromise(reason);
          if (!changed.alreadyRecorded) recordDemoEvent(code, `PO ${changed.poNumber}/${changed.lineNumber} promise date moved from ${changed.promisedFrom} to ${changed.promisedTo}`,
            ['Sensor purchase promise moved four days', 'Delayed sensor receipt is reflected in MRP'], [], changed);
        },
      },
      'WW-S08-LOT-SPLIT': {
        detail: 'Uses audited inventory commands: 100 m + 100 m + 45 m become child lots and the remaining 5 m is scrap. Parent-hold propagation is not modeled by this control.',
        run: async reason => { await splitSisalLot(reason); recordDemoEvent(code, 'Three child lots total 245 m; the remaining 5 m is scrapped', ['Sisal child-lot quantity reconciles'], ['WW-SISAL-SPOOL-250M/LOT-SISAL-M01']); },
      },
      'WW-S11-CAL-EXPIRED': {
        detail: 'Records a fictional, audited calibration whose due date is yesterday for the seeded torque and optical gauges. Expiry is derived from the due dates by the existing readiness checks; this does not alter the site clock.',
        run: async reason => {
          const result = await expireDemoGauges(reason);
          const targets = result.changed ?? result.gauges;
          const summary = result.alreadyExpired
            ? `Torque and optical gauges were already expired as of ${result.siteToday}`
            : `Gauge calibration expiry recorded for ${result.changed.join(', ')}; due ${result.dueDate}`;
          recordDemoEvent(code, summary, ['Torque and optical gauges are past due'], targets, result);
        },
      },
      'WW-S12-SCAN-OUTAGE': {
        detail: 'Arms a browser-session-only WW-P01 rehearsal for the scanner / printer outage. It issues no source-changing API command and contacts no physical device. The kiosk preserves the entered demo values, locks after each simulated 503, and requires supervisor observation before closing the print reconciliation.',
        run: async () => {
          const state = armDemoScannerOutage();
          recordDemoEvent(code, 'Browser-session scanner / label 503 rehearsal armed; no source-changing API command or physical device was called',
            ['Local scanner failure and pending label reconciliation workflow armed'], [], { phase: 'armed', siteCode: state.siteCode, storage: 'sessionStorage' });
          window.location.hash = '#/shop-floor';
        },
      },
      'WW-S14-RECOVERY-BOARD': {
        detail: 'Captures a completed CNC Mill shift, reviews it, then creates two owned and dated actions. The second action depends on the first, and the saved action list is read back to verify the dependency and due times.',
        run: async reason => {
          const result = await createRecoveryBoardScenario(reason);
          recordDemoEvent(code, `Handoff ${result.handoffId}: dependent recovery actions ${result.actionIds.join(' → ')} assigned to ${result.assigneeId}`,
            ['Recovery action dependency and due dates are recorded'], result.actionIds, result);
        },
      },
      'WW-S15-HANDOFF': {
        detail: 'Uses the actual outgoing shift snapshot and requires recorded CNC-01 downtime plus worker absence facts. The creator/reviewer cannot acknowledge their own handoff; a different incoming worker must sign in and record receipt.',
        run: async reason => {
          const existing = [...demoEvents].reverse().find(event => event.code === code && Number.isSafeInteger(Number(event.targetGroups?.handoffId)));
          if (existing) {
            await acknowledgeS15Handoff(reason, code);
            return;
          }
          const result = await createS15Handoff(reason, code);
          if (result.awaitingShiftEnd) {
            if (!demoEvents.some(event => event.code === code && event.targetGroups?.phase === 'awaiting')) {
              recordDemoEvent(code, 'CNC Mill callout and departure are recorded; Forge requires the configured shift to finish before its handoff snapshot can be captured', [], result.calloutIds,
                { phase: 'awaiting', calloutIds: result.calloutIds, nextReviewAt: result.nextReviewAt });
            }
            return;
          }
          recordDemoEvent(code, `Reviewed ${result.shiftCode} handoff ${result.handoffId} for ${result.shiftDate}; captured ${result.factCount} source facts`,
            ['Outgoing handoff includes open downtime and recorded worker absences'], [result.handoffId], { ...result, phase: 'handoff' });
        },
      },
      'WW-S16-CASCADE': {
        detail: 'Calls out one of exactly two present, independently qualified ASM-03 operators plus two other Day Shift workers, leaves two additional workers without current presence, reports CNC-01 down, and holds the motor lot. The action then records assertions for the resulting safety, maintenance, and material constraints.',
        run: async reason => {
          const { callouts, missing, crew, startsAt, endsAt } = await startCascadeAbsences(reason, code);
          const absenceEvidence = { calloutIds: callouts.map(worker => worker.personId), missingIds: missing.map(worker => worker.personId),
            qualifiedCrewIds: crew.map(worker => worker.personId), startsAt: startsAt.toISOString(), endsAt: endsAt.toISOString() };
          recordDemoEvent(code, 'Cascade workforce events: one ASM-03 callout, two other callouts, and two unconfirmed scheduled workers',
            ['Three callouts and two workers not present'], [...callouts, ...missing].map(worker => worker.personId), absenceEvidence);
          const downtime = await reportCncDown(reason, code);
          recordDemoEvent(code, `Cascade machine event: CNC-01 downtime ${downtime?.entityId ?? 'recorded or already open'}`, ['CNC-01 downtime is open'], ['CNC-01']);
          const held = await holdMotorLot(reason, code);
          recordDemoEvent(code, `Cascade material event: motor lot held (${held?.message ?? 'already held'})`, ['Motor lot is on inventory hold'], ['WW-MOT-24V-DC/LOT-MOT-24V-881']);
          recordDemoEvent(code, 'Cascade impact evidence: ASM-03, CNC-01, and the motor lot are checked against live source state',
            ['Cascade source events produce the expected blocking constraints'], ['ASM-03', 'CNC-01', 'LOT-MOT-24V-881'], { ...absenceEvidence, phase: 'complete' });
        },
      },
    };
    const action = actions[code];
    if (!action) return;
    await confirmWithReason({ title: `${scenario.code} · ${scenario.title}`, detail: action.detail, confirmLabel: 'Record demo event' }, async reason => {
      loading = true; render();
      try { await action.run(reason); if (code !== 'WW-S12-SCAN-OUTAGE') await read(); render(); toast(code === 'WW-S12-SCAN-OUTAGE' ? 'Local S12 outage rehearsal armed.' : `${scenario.code} source action recorded.`); }
      finally { loading = false; render(); }
    });
  }

  const renderResult = r => `<section class="panel"><header><h2>Scenario preview</h2><span class="spacer"></span><span class="muted">As of ${esc(fmtDateTime(r.asOf))}</span></header>
    <p><b>${esc(r.scenarioDescription)}</b></p><p class="muted">${esc(r.evidenceNote)}</p>
    <h3>Schedule impact</h3>${r.orders.length ? `<div class="table-wrap"><table class="table"><thead><tr><th>Order</th><th>Part</th><th>Change</th><th>Baseline</th><th>Scenario</th><th>Hours</th><th>Evidence</th></tr></thead><tbody>${r.orders.map(o => `<tr><td><a href="#/orders/${o.orderNumber}">#${o.orderNumber}</a></td><td>${esc(o.partNumber)}</td><td>${esc(o.change)}</td><td>${esc(fmtDateTime(o.baselineCompletion))}<br>${esc(o.baselineStatus)}</td><td>${esc(fmtDateTime(o.proposedCompletion))}<br>${esc(o.proposedStatus)}</td><td>${o.shiftHours == null ? '—' : esc(String(o.shiftHours))}</td><td>${o.evidence.map(esc).join('<br>')}</td></tr>`).join('')}</tbody></table></div>` : '<div class="empty">No released order projection changed in this comparison.</div>'}
    <h3>Skills MRP</h3>${r.skillsCapacity.length ? `<div class="table-wrap"><table class="table"><thead><tr><th>Day</th><th>Qualification</th><th>Baseline hours</th><th>Scenario hours</th></tr></thead><tbody>${r.skillsCapacity.map(x => `<tr><td>${esc(x.day)}</td><td>${esc(x.qualification)}</td><td>${esc(String(x.baselineHours))}</td><td>${esc(String(x.proposedHours))}</td></tr>`).join('')}</tbody></table></div>` : '<p class="muted">No qualification-hour difference was projected.</p>'}
    <h3>Recovery options (${r.recoveryOptions.length})</h3><p class="muted">Evaluate a specific selection in a new private trial. Review its source evidence and consequences, then use the owning workflow for any human-approved action.</p>
    <div class="stack">${r.recoveryOptions.map(o => renderRecovery(o, r.scenarioId, loading)).join('')}</div></section>`;

  async function evaluateOption(scenarioId, optionId) {
    const scenario = scenarios.find(s => s.scenarioId === scenarioId);
    const definition = RECOVERY_FORMS.find(f => f.id === optionId);
    if (!scenario || !definition) throw new Error('Refresh your private scenarios before evaluating this option.');
    const existing = scenario.change.recoverySelections?.find(s => s.optionId === optionId);
    const option = result?.scenarioId === scenarioId ? result.recoveryOptions.find(o => o.id === optionId) : null;
    const orderNumber = scenario.change.orderNumber ?? (option?.affectedOrders?.length === 1 ? option.affectedOrders[0] : null);
    const needsCrew = definition.fields.some(field => field.name === 'personIds');
    const needsWorker = definition.fields.some(field => field.name === 'personId');
    let workers = [];
    if (needsCrew || needsWorker) {
      workers = await get('/api/readiness/presence');
      if (!workers.length) throw new Error('Register workers in Workforce before evaluating a staffing option.');
    }
    const defaultName = `${scenario.name} · ${definition.title}`.slice(0, 100);
    const fields = [{ name: 'name', label: 'Private trial name', required: true, value: defaultName }, ...definition.fields.flatMap(field => {
      let value = existing?.[field.name];
      if (value == null && field.name === 'orderNumber') value = orderNumber;
      if (value == null && field.name === 'sequence') value = scenario.change.sequence;
      if (value == null && field.name === 'workCenterCode') value = scenario.change.workCenterCode;
      if (field.name === 'personIds') return [
        { type: 'html', html: '<div class="field"><b>Workers covering the shift</b><p class="muted small">Select the actual crew. Recorded qualifications, presence, availability and safety coverage decide feasibility.</p></div>' },
        ...workers.map((worker, index) => ({ name: `crew-${index}`, type: 'check', label: worker.name,
          text: `${worker.homeWorkCenter ?? worker.workCenter ?? 'No home work center'} · ${worker.countsAsPresent ? 'Present' : 'No current presence evidence'}`,
          value: existing?.personIds?.includes(worker.personId) ?? false })),
      ];
      if (field.name === 'personId') return [{ ...field, label: 'Alternate worker', type: 'lookup',
        options: workers.map(worker => [worker.personId, `${worker.name} · ${worker.personId} · ${worker.homeWorkCenter ?? worker.workCenter ?? 'No home work center'}`]), value }];
      if (field.name === 'partNumber' || field.name === 'primaryPartNumber') return [{ ...field, label: field.name === 'partNumber' ? 'Stock part' : 'Primary component', type: 'lookup',
        options: items.map(item => [item.partNumber, `${item.partNumber} — ${item.description}`]), value }];
      if (field.name === 'workCenterCode') return [{ ...field, type: 'select', blank: field.required ? undefined : 'Choose a work center (optional)',
        options: (resources.workCenters ?? []).filter(center => center.active !== false).map(center => [center.code, `${center.name ?? center.code} · ${center.code}`]), value }];
      if (field.name === 'machineCode') return [{ ...field, type: 'select', blank: field.required ? undefined : 'Choose a machine (optional)',
        options: (resources.machines ?? []).filter(machine => machine.active !== false).map(machine => [machine.code, `${machine.name ?? machine.code} · ${machine.code}`]), value }];
      if (Array.isArray(value)) value = value.join(', ');
      if (field.type === 'datetime-local') value = localTime(value);
      return [{ ...field, value }];
    })];
    await formModal({ title: `Evaluate ${definition.title}`, detail: `Create a private trial from “${scenario.name}”. Current source checks decide feasibility and any remaining evidence needed. Source actions stay in their owning workflows.`,
      fields, confirmLabel: 'Save and evaluate', reasonRequired: true },
    async (values, reason) => {
      if (values.name.length > 100) throw new Error('A private trial name may contain at most 100 characters.');
      if (needsCrew) values.personIds = workers.filter((_, index) => values[`crew-${index}`]).map(worker => worker.personId);
      const selection = recoverySelection(definition, values);
      return command('/api/planning/scenarios', { name: values.name, change: { ...scenario.change, recoverySelections: [selection] } }, reason);
    }, async saved => {
      await read();
      result = null;
      if (saved.entityId) await runScenario(saved.entityId);
      else render();
    });
  }

  async function saveScenario(event) {
    event.preventDefault();
    const f = new FormData(event.currentTarget);
    const kind = String(f.get('kind'));
    const change = { kind };
    for (const name of ['machineCode', 'personId', 'orderNumber', 'sequence', 'priority', 'workCenterCode', 'activityCode', 'startsAt', 'durationHours', 'delayDays']) {
      const value = String(f.get(name) ?? '').trim();
      if (!value) continue;
      if (name === 'personId') change.personId = value;
      else if (name === 'orderNumber' || name === 'sequence' || name === 'priority' || name === 'durationHours' || name === 'delayDays') change[name] = Number(value);
      else if (name === 'startsAt') change.startsAt = new Date(value).toISOString();
      else change[name] = value;
    }
    try {
      const saved = await command('/api/planning/scenarios', { name: f.get('name'), change }, f.get('reason'));
      toast('Private scenario saved.');
      await read(); result = null; render();
      if (saved.entityId) await runScenario(saved.entityId);
    } catch (error) { toast(error.message, true); }
  }

  async function runScenario(id) {
    loading = true; render();
    try { result = await command(`/api/planning/scenarios/${id}/run`, {}); }
    catch (error) { toast(error.message, true); }
    finally { loading = false; render(); }
  }

  async function deleteScenario(id) {
    try {
      const done = await confirmWithReason({ title: 'Delete private scenario', detail: 'Only your saved planning scenario is removed. No production record is changed.', confirmLabel: 'Delete' },
        reason => command(`/api/planning/scenarios/${id}/delete`, {}, reason));
      if (done) { toast('Scenario deleted.'); result = null; await read(); render(); }
    } catch (error) { toast(error.message, true); }
  }

  try { await read(); render(); }
  catch (error) { root.innerHTML = `<section class="panel"><h1>Scenarios and recovery</h1><p class="notice bad">${esc(error.message)}</p></section>`; }
}
