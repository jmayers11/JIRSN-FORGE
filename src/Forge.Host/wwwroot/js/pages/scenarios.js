import { command, get } from '../api.js';
import { $, $$, esc, fmtDateTime, toast, confirmWithReason } from '../ui.js';
import { formModal } from '../forms.js';

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

export async function scenariosPage(root) {
  let scenarios = [];
  let result = null;
  let loading = false;
  const read = async () => { scenarios = await get('/api/planning/scenarios'); };
  const field = (name, label, type = 'text', required = false, placeholder = '') => `<label class="field"><span>${label}</span><input class="input" name="${name}" type="${type}" ${required ? 'required' : ''} ${placeholder ? `placeholder="${placeholder}"` : ''}></label>`;
  const render = () => {
    root.innerHTML = `<div class="page-head"><div><h1>Scenarios and recovery</h1><p class="muted">Save private what-if cases, compare them against today’s authorized material, capacity and schedule inputs, and review recovery actions. Running a scenario does not change the approved schedule or source records.</p></div></div>
      <section class="panel"><header><h2>New scenario</h2></header><form id="scenario-form" class="form-grid">
        ${field('name', 'Private scenario name', 'text', true, 'e.g. Slitter outage next shift')}
        <label class="field"><span>Change to test</span><select class="input" name="kind">${KINDS.map(k => `<option>${k}</option>`).join('')}</select></label>
        ${field('machineCode', 'Machine code')}${field('personId', 'Person ID')}${field('orderNumber', 'Released order number', 'number')}${field('sequence', 'Operation sequence', 'number')}${field('priority', 'Proposed priority (1 highest)', 'number')}
        ${field('workCenterCode', 'Work center code')}${field('activityCode', 'Activity code')}${field('startsAt', 'Starts at', 'datetime-local')}${field('durationHours', 'Hours (outage, overtime)', 'number')}${field('delayDays', 'Supplier delay days', 'number')}
        ${field('reason', 'Why save this scenario', 'text', true, 'Required for the audit record')}
        <div class="field"><span>&nbsp;</span><button class="btn primary" type="submit">Save private scenario</button></div>
      </form><p class="muted small">The selected change uses only its relevant fields. Person and order lookups are checked again using your current access when you run the scenario.</p></section>
      <section class="panel"><header><h2>Your private scenarios</h2><span class="spacer"></span><button class="btn" id="refresh-scenarios">Refresh</button></header>
        ${scenarios.length ? `<div class="stack">${scenarios.map(s => `<article class="card"><div class="card-head"><div><h3>${esc(s.name)}</h3><p class="muted">${esc(s.change.kind)} · created ${esc(fmtDateTime(s.createdAt))}${s.change.recoverySelections?.length ? ` · selected ${esc(RECOVERY_FORMS.find(f => f.id === s.change.recoverySelections[0].optionId)?.title ?? s.change.recoverySelections[0].optionId)}` : ''}</p></div><span class="spacer"></span><button class="btn primary" data-run="${esc(s.scenarioId)}" ${loading ? 'disabled' : ''}>Run preview</button><button class="btn danger" data-delete="${esc(s.scenarioId)}">Delete</button></div><div class="actions"><label class="field"><span>Recovery option</span><select class="input" data-option-picker="${esc(s.scenarioId)}"><option value="">Choose an option</option>${RECOVERY_FORMS.map(f => `<option value="${esc(f.id)}">${esc(f.title)}</option>`).join('')}</select></label><button class="btn" data-evaluate-scenario="${esc(s.scenarioId)}" ${loading ? 'disabled' : ''}>Evaluate option</button></div></article>`).join('')}</div>` : '<div class="empty">No private scenarios saved yet.</div>'}</section>
      ${result ? renderResult(result) : ''}`;

    $('#refresh-scenarios', root)?.addEventListener('click', () => scenariosPage(root).catch(e => toast(e.message, true)));
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
      if (field.name === 'personIds') return [
        { type: 'html', html: '<div class="field"><b>Workers covering the shift</b><p class="muted small">Select the actual crew. Recorded qualifications, presence, availability and safety coverage decide feasibility.</p></div>' },
        ...workers.map((worker, index) => ({ name: `crew-${index}`, type: 'check', label: worker.name,
          text: `${worker.homeWorkCenter ?? worker.workCenter ?? 'No home work center'} · ${worker.countsAsPresent ? 'Present' : 'No current presence evidence'}`,
          value: existing?.personIds?.includes(worker.personId) ?? false })),
      ];
      if (field.name === 'personId') return [{ ...field, label: 'Alternate worker', type: 'select', blank: 'Choose a worker',
        options: workers.map(worker => [worker.personId, `${worker.name} · ${worker.homeWorkCenter ?? worker.workCenter ?? 'No home work center'}`]), value }];
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
