import { tryGet, command } from './api.js';
import { who } from './people.js';
import { esc, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast, drawer } from './ui.js';
import { formModal, qty, can } from './forms.js';

// Tools (V3.8): identity, type, place and custodian, condition, eligibility, uses against limits, calibration and
// service cycles. Whether a tool may be used is judged at the moment of use from its history.

const KINDS = ['Gauge', 'Cutting', 'Fixture', 'Die', 'Mold', 'Other'];
const CONDITIONS = ['Good', 'Worn', 'Damaged'];

/** The Tools panel on the Work Centers page. */
export async function appendTools(container, machines, me) {
  const section = document.createElement('section');
  section.className = 'panel';
  container.appendChild(section);
  const manage = can(me, 'ManufacturingEngineer', 'QualityEngineer', 'MaintenancePlanner');
  const calibrate = can(me, 'QualityEngineer', 'MaintenanceTechnician');
  const handle = can(me, 'MaterialHandler', 'Supervisor', 'MaintenanceTechnician', 'MaintenancePlanner', 'QualityEngineer');
  const load = async () => {
    const [tools, types, activities] = await Promise.all([tryGet('/api/resources/tools'), tryGet('/api/resources/tool-types'), tryGet('/api/resources/')]);
    const list = tools.data ?? [];
    section.innerHTML = `<header><h2>Tools and gauges</h2><span class="spacer"></span>${manage ? '<button class="btn small" data-tool-type>+ Tool type</button> <button class="btn small" data-tool-add>+ Tool</button>' : ''}</header>
      ${tools.error ? errorBlock(tools.error, 'tools') : !list.length ? emptyBlock('No tools yet', 'Register blades, dies, fixtures and gauges with their limits; their use is then judged at the moment of use.') : `
      <table class="table"><thead><tr><th>Tool</th><th>Where / with</th><th>Condition</th><th>Calibration due</th><th class="num">Uses</th><th>Usable now</th><th></th></tr></thead><tbody>
      ${list.map(v => { const t = v.tool; return `<tr><td><b>${esc(t.code)}</b> <span class="faint">${esc(v.type.name)}${t.serial ? ` · s/n ${esc(t.serial)}` : ''}</span><div class="faint">${esc(t.description)}${t.eligibleMachines.length ? ` · for ${esc(t.eligibleMachines.join(', '))}` : ''}</div></td>
        <td>${t.custodian ? `<span class="pill warn">${esc(t.custody === 'AtVendor' ? 'At' : 'With')} ${esc(t.custodian)}</span>` : esc(t.location ?? '—')}</td>
        <td>${esc(t.condition)}${t.status === 'Retired' ? ' <span class="pill none">Retired</span>' : ''}</td>
        <td>${t.calibrationDue ? esc(fmtDate(t.calibrationDue)) : '—'}</td>
        <td class="num">${qty(t.usageCount)}${v.validity.usesLeft != null ? `<div class="faint">${qty(v.validity.usesLeft)} left</div>` : ''}${v.type.serviceUses ? `<div class="faint">${qty(t.usesSinceService)} / ${qty(v.type.serviceUses)} since service</div>` : ''}</td>
        <td>${v.validity.valid ? '<span class="pill ok">Yes</span>' : `<span class="pill bad">No</span><div class="faint">${esc(v.validity.problems.join(' '))}</div>`}</td>
        <td class="nowrap">${t.status === 'Retired' ? '' : `<button class="btn small ghost" data-tool-history="${esc(t.code)}">History</button>
          ${calibrate && t.status === 'Available' ? ` <button class="btn small ghost" data-tool-cal="${esc(t.code)}">Calibrate</button>` : ''}
          ${handle && t.status === 'Available' ? ` <button class="btn small ghost" data-tool-service="${esc(t.code)}">Send for service</button> <button class="btn small ghost" data-tool-condition="${esc(t.code)}">Condition</button>` : ''}
          ${handle && t.status === 'OutForService' ? ` <button class="btn small" data-tool-return="${esc(t.code)}">Back from service</button>` : ''}`}</td></tr>`; }).join('')}</tbody></table>`}`;
    const typeOptions = (types.data ?? []).filter(t => t.active).map(t => [t.code, `${t.code} — ${t.name} (${t.kind})`]);
    $$('[data-tool-type]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: 'Add a tool type', detail: 'Gauges and anything with a calibration interval need a calibration in force to be used.', confirmLabel: 'Add',
      fields: [
        { name: 'code', label: 'Code', code: true, required: true, placeholder: 'SLIT-KNIFE' }, { name: 'name', label: 'Name', required: true },
        { name: 'kind', label: 'Kind', type: 'select', options: KINDS.map(k => [k, k]) },
        { name: 'calibrationDays', label: 'Calibration every (days, optional)', type: 'number', min: 1 },
        { name: 'usageLimit', label: 'Usage limit over its life (optional)', type: 'number', min: 1 },
        { name: 'serviceUses', label: 'Service (sharpen) every N uses (optional)', type: 'number', min: 1 },
      ],
    }, (v, reason) => command('/api/resources/tool-types', v, reason), load)));
    $$('[data-tool-add]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: 'Register a tool', confirmLabel: 'Register',
      fields: [
        { name: 'code', label: 'Tool code', code: true, required: true }, { name: 'typeCode', label: 'Type', type: 'select', options: typeOptions, required: true },
        { name: 'description', label: 'Description', required: true }, { name: 'serial', label: 'Serial (optional)' }, { name: 'location', label: 'Kept at (optional)' },
        { name: 'eligibleMachines', label: 'For machines (comma separated, empty for any)', placeholder: machines.map(m => m.code).slice(0, 3).join(', ') },
        { name: 'calibratedOn', label: 'Calibrated on (gauges)', type: 'date' }, { name: 'calibrationDue', label: 'Calibration due (if not from the interval)', type: 'date' },
        { name: 'certificate', label: 'Certificate (optional)' },
      ],
    }, (v, reason) => command('/api/resources/tools', { ...v, eligibleMachines: (v.eligibleMachines ?? '').split(/[,\s]+/).filter(Boolean).map(x => x.toUpperCase()), calibratedOn: v.calibratedOn || null, calibrationDue: v.calibrationDue || null }, reason), load)));
    $$('[data-tool-history]', section).forEach(b => b.addEventListener('click', async () => {
      const { data, error } = await tryGet(`/api/resources/tools/${encodeURIComponent(b.dataset.toolHistory)}/history`);
      drawer(`${b.dataset.toolHistory} — history`, error ? errorBlock(error, 'history') : `<table class="table"><thead><tr><th>When</th><th>What</th><th>Detail</th><th>Who</th></tr></thead><tbody>
        ${data.slice().reverse().map(e => `<tr><td class="nowrap">${esc(fmtDateTime(e.at))}</td><td>${esc(e.kind)}</td>
          <td>${[e.location && `at ${e.location}`, e.custodian && `with ${e.custodian}`, e.condition, e.calibratedOn && `calibrated ${e.calibratedOn}`, e.dueOn && `due ${e.dueOn}`, e.certificate && `cert ${e.certificate}`, e.uses && `${e.uses} use(s)`, e.reference, e.note].filter(Boolean).map(esc).join(' · ')}</td>
          <td>${esc(who(e.actor))}</td></tr>`).join('')}</tbody></table>`);
    }));
    $$('[data-tool-cal]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: `Calibrate ${b.dataset.toolCal}`, confirmLabel: 'Record',
      fields: [
        { name: 'calibratedOn', label: 'Calibrated on', type: 'date', required: true }, { name: 'passed', type: 'check', label: 'Result', text: 'Passed', value: true },
        { name: 'dueOn', label: 'Next due (empty: from the type\'s interval)', type: 'date' }, { name: 'certificate', label: 'Certificate' },
      ],
    }, (v, reason) => command(`/api/resources/tools/${encodeURIComponent(b.dataset.toolCal)}/calibrations`, { ...v, dueOn: v.dueOn || null }, reason), load)));
    $$('[data-tool-service]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: `Send ${b.dataset.toolService} for service`, detail: 'It is not used until it is back.', confirmLabel: 'Send', reasonRequired: true,
      fields: [{ name: 'vendor', label: 'To', required: true, placeholder: 'Sharpening Co.' }, { name: 'note', label: 'Note (optional)' }],
    }, (v, reason) => command(`/api/resources/tools/${encodeURIComponent(b.dataset.toolService)}/service`, v, reason), load)));
    $$('[data-tool-return]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: `${b.dataset.toolReturn} back from service`, detail: 'Uses since service start again.', confirmLabel: 'Record',
      fields: [
        { name: 'condition', label: 'Condition', type: 'select', options: CONDITIONS.map(c => [c, c]) }, { name: 'location', label: 'Kept at (optional)' },
        { name: 'calibratedOn', label: 'Calibrated on (if it was)', type: 'date' }, { name: 'certificate', label: 'Certificate (optional)' },
      ],
    }, (v, reason) => command(`/api/resources/tools/${encodeURIComponent(b.dataset.toolReturn)}/return`, { ...v, calibratedOn: v.calibratedOn || null }, reason), load)));
    $$('[data-tool-condition]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: `Condition of ${b.dataset.toolCondition}`, confirmLabel: 'Record', reasonRequired: true,
      fields: [{ name: 'condition', label: 'Condition', type: 'select', options: CONDITIONS.map(c => [c, c]) }],
    }, (v, reason) => command(`/api/resources/tools/${encodeURIComponent(b.dataset.toolCondition)}/condition`, v, reason), load)));
  };
  await load();
}

/** What tools activities need (V3.9): readiness checks that enough of them are valid for the machine and activity. */
export async function appendToolRequirements(container, me) {
  const section = document.createElement('section');
  section.className = 'panel';
  container.appendChild(section);
  const manage = can(me, 'ManufacturingEngineer', 'QualityEngineer', 'MaintenancePlanner');
  const load = async () => {
    const [reqs, types] = await Promise.all([tryGet('/api/resources/tool-requirements'), tryGet('/api/resources/tool-types')]);
    const list = reqs.data ?? [];
    section.innerHTML = `<header><h2>Tooling required</h2><span class="spacer"></span>${manage ? '<button class="btn small" data-req-add>+ Requirement</button>' : ''}</header>
      ${reqs.error ? errorBlock(reqs.error, 'tool requirements') : !list.length ? emptyBlock('No tooling required', 'Say which tools an activity needs (on a machine, or anywhere); readiness then checks that enough are valid at that moment.') : `
      <table class="table"><thead><tr><th>Activity</th><th>Machine</th><th>Tool type</th><th class="num">How many</th><th>Set</th><th></th></tr></thead><tbody>
      ${list.map(r => `<tr><td>${esc(r.activityCode)}</td><td>${esc(r.machineCode ?? 'Any')}</td><td>${esc(r.toolTypeCode)}</td><td class="num">${r.quantity}</td><td>${esc(fmtDateTime(r.setAt))} <span class="faint">${esc(who(r.setBy))}</span></td>
        <td>${manage ? `<button class="btn small ghost" data-req-remove="${r.requirementId}">Remove</button>` : ''}</td></tr>`).join('')}</tbody></table>`}`;
    $$('[data-req-add]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: 'Require tooling', detail: 'A machine-specific requirement replaces the general one for the same tool type.', confirmLabel: 'Save',
      fields: [
        { name: 'activityCode', label: 'Activity', code: true, required: true, placeholder: 'RUN_SLITTER' }, { name: 'machineCode', label: 'Machine (empty for any)', code: true },
        { name: 'toolTypeCode', label: 'Tool type', type: 'select', options: (types.data ?? []).filter(t => t.active).map(t => [t.code, `${t.code} — ${t.name}`]), required: true },
        { name: 'quantity', label: 'How many', type: 'number', min: 1, value: 1 },
      ],
    }, (v, reason) => command('/api/resources/tool-requirements', { ...v, machineCode: v.machineCode || null, quantity: Number(v.quantity || 1) }, reason), load)));
    $$('[data-req-remove]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: 'Remove this tooling requirement', confirmLabel: 'Remove', reasonRequired: true, fields: [],
    }, (v, reason) => command(`/api/resources/tool-requirements/${b.dataset.reqRemove}/remove`, {}, reason), load)));
  };
  await load();
}
