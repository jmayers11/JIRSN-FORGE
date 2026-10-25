import { tryGet, command } from './api.js';
import { who } from './people.js';
import { esc, $$, fmtDateTime, errorBlock, confirmWithReason, toast } from './ui.js';
import { formModal, qty, canAction } from './forms.js';

// Production actuals (V3.5): stoppages with one category and one reason code each, setups and changeovers against
// the minutes the routing expected, root causes proven only by a second person; an order's quantity through its
// states; rework and repair that take good output back without rewriting what was reported.

const CATEGORIES = ['Planned', 'Unplanned', 'Setup', 'Changeover', 'Maintenance', 'Waiting', 'Starved', 'Blocked'];
const CATEGORY_TONE = { Planned: 'info', Unplanned: 'bad', Setup: 'info', Changeover: 'info', Maintenance: 'warn', Waiting: 'warn', Starved: 'warn', Blocked: 'warn' };
const reporter = me => canAction(me, 'maintenance.downtime.report');
const confirmer = me => canAction(me, 'execution.rootcause.confirm');
const mine = (me, actor) => actor === `person:${me.personId}`;

function rootCauseText(v) {
  const proven = v.rootCauses.find(r => r.confirmedAt);
  if (proven) return `<span class="pill ok">Proven</span> ${esc(proven.cause)} <span class="faint">· confirmed by ${esc(who(proven.confirmedBy))}</span>`;
  const last = v.rootCauses[v.rootCauses.length - 1];
  return last ? `<span class="pill warn">Proposed</span> ${esc(last.cause)} <span class="faint">· ${esc(last.evidence)}</span>` : '<span class="faint">No root cause yet — the reason code is what was reported</span>';
}

/** Stoppages of a work center's machines, last 7 days and open ones, with stop, end and root-cause actions. */
export async function appendStoppages(container, machines, me) {
  const section = document.createElement('section');
  section.className = 'panel';
  container.appendChild(section);
  const load = async () => {
    const [stoppages, reasons] = await Promise.all([tryGet('/api/execution/stoppages?days=7'), tryGet('/api/execution/reason-codes')]);
    const codes = machines.map(m => m.code);
    const list = (stoppages.data ?? []).filter(v => codes.includes(v.stoppage.machineCode)).reverse();
    const downtime = (reasons.data ?? []).filter(r => r.kind === 'Downtime' && r.active);
    section.innerHTML = `<header><h2>Stoppages</h2><span class="spacer"></span>${reporter(me) && machines.length ? '<button class="btn small" data-stop>Machine stopped</button>' : ''}</header>
      ${stoppages.error ? errorBlock(stoppages.error, 'stoppages') : !list.length ? '<div class="muted">No stoppages reported on these machines in the last 7 days.</div>' : `
      <table class="table"><thead><tr><th>Machine</th><th>Category</th><th>Reason</th><th>From</th><th class="num">Minutes</th><th class="num">Expected</th><th>Root cause</th><th></th></tr></thead><tbody>
      ${list.map(v => { const s = v.stoppage; return `<tr><td class="mono">${esc(s.machineCode)}${s.orderNumber ? `<br><a class="faint" href="#/orders/${s.orderNumber}">${s.orderNumber}/${s.sequence}</a>` : ''}</td>
        <td><span class="pill ${CATEGORY_TONE[s.category] ?? 'none'}">${esc(s.category)}</span></td><td>${esc(s.reasonCode)} <span class="faint">${esc(v.reasonDescription)}</span>${s.note ? `<div class="faint">${esc(s.note)}</div>` : ''}</td>
        <td class="nowrap">${esc(fmtDateTime(s.startedAt))}<br><span class="faint">${s.endedAt ? `to ${esc(fmtDateTime(s.endedAt))}` : 'still stopped'} · ${esc(who(s.startedBy))}</span></td>
        <td class="num">${qty(v.minutes, 1)}</td><td class="num ${v.variance > 0 ? 'c-warn' : ''}">${s.expectedMinutes != null ? `${qty(s.expectedMinutes, 1)}${v.variance != null ? `<br><span class="faint">${v.variance > 0 ? '+' : ''}${qty(v.variance, 1)}</span>` : ''}` : '—'}</td>
        <td>${rootCauseText(v)}</td>
        <td class="nowrap">${!s.endedAt && reporter(me) ? `<button class="btn small primary" data-end="${s.stoppageId}">Running again</button> ` : ''}${!v.rootCauses.some(r => r.confirmedAt) && reporter(me) ? `<button class="btn small ghost" data-cause="${s.stoppageId}">Propose cause</button> ` : ''}${v.rootCauses.filter(r => !r.confirmedAt && confirmer(me) && !mine(me, r.proposedBy)).map(r => `<button class="btn small" data-confirm="${r.rootCauseId}">Confirm cause</button>`).join('')}</td></tr>`; }).join('')}
      </tbody></table>`}
      <div class="faint" style="margin-top:6px">A reason code is what was reported. A root cause is proposed with its evidence and proven only when a different person confirms it.</div>`;
    $$('[data-stop]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: 'Machine stopped', detail: 'One category and one reason code. Setups and changeovers name the order operation they prepare; the routing\'s setup minutes become the expected time.', confirmLabel: 'Record',
      fields: [
        { name: 'machineCode', label: 'Machine', type: 'select', options: machines.map(m => [m.code, `${m.code} — ${m.name}`]), required: true },
        { name: 'category', label: 'Category', type: 'select', options: CATEGORIES.map(c => [c, c]), required: true },
        { name: 'reasonCode', label: 'Reason code', type: 'select', options: downtime.map(r => [r.code, `${r.code} — ${r.description} (${r.category})`]), required: true },
        { name: 'orderNumber', label: 'Order (setups and changeovers)', type: 'number' },
        { name: 'sequence', label: 'Operation', type: 'number' },
        { name: 'note', label: 'Note (optional)' },
      ],
    }, (v, reason) => command('/api/execution/stoppages', v, reason), load)));
    $$('[data-end]', section).forEach(b => b.addEventListener('click', async () => {
      const done = await confirmWithReason({ title: `Stoppage ${b.dataset.end} — running again`, detail: 'Ends the stoppage now.', confirmLabel: 'Running again', reasonRequired: false },
        reason => command(`/api/execution/stoppages/${b.dataset.end}/end`, {}, reason));
      if (done) { toast(done.message); await load(); }
    }));
    $$('[data-cause]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: `Propose a root cause for stoppage ${b.dataset.cause}`, detail: 'It stays a proposal until someone else confirms it from the evidence.', confirmLabel: 'Propose',
      fields: [{ name: 'cause', label: 'Root cause', required: true }, { name: 'evidence', label: 'Evidence', type: 'textarea', required: true }],
    }, (v, reason) => command(`/api/execution/stoppages/${b.dataset.cause}/root-causes`, v, reason), load)));
    $$('[data-confirm]', section).forEach(b => b.addEventListener('click', async () => {
      const done = await confirmWithReason({ title: `Confirm root cause ${b.dataset.confirm}`, detail: 'You confirm the evidence proves it. You cannot confirm a cause you proposed.', confirmLabel: 'Confirm' },
        reason => command(`/api/execution/root-causes/${b.dataset.confirm}/confirm`, {}, reason));
      if (done) { toast(done.message); await load(); }
    }));
  };
  await load();
}

/** The plant's reason codes; manufacturing engineers define, change and retire them. */
export async function appendReasonCodes(container, me) {
  const manage = canAction(me, 'execution.reasons.manage');
  const section = document.createElement('section');
  section.className = 'panel';
  container.appendChild(section);
  const load = async () => {
    const res = await tryGet('/api/execution/reason-codes');
    section.innerHTML = `<header><h2>Reason codes</h2><span class="spacer"></span>${manage ? '<button class="btn small" data-add-reason>+ Reason code</button>' : ''}</header>
      ${res.error ? errorBlock(res.error, 'reason codes') : !res.data.length ? `<div class="muted">No reason codes yet.${manage ? ' Add the plant\'s own words for downtime, scrap and rework.' : ' A manufacturing engineer adds them.'}</div>` : `
      <table class="table"><thead><tr><th>Code</th><th>Kind</th><th>Category</th><th>Description</th><th></th></tr></thead><tbody>
      ${res.data.map(r => `<tr class="${r.active ? '' : 'retired'}"><td class="mono">${esc(r.code)}</td><td>${esc(r.kind)}</td><td>${esc(r.category ?? '—')}</td><td>${esc(r.description)}</td>
        <td>${!r.active ? '<span class="pill none">Retired</span>' : manage ? `<button class="btn small ghost" data-retire-reason="${esc(r.code)}">Retire</button>` : ''}</td></tr>`).join('')}</tbody></table>`}`;
    $$('[data-add-reason]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: 'Add a reason code', detail: 'Downtime reasons belong to one stoppage category.', confirmLabel: 'Add',
      fields: [
        { name: 'code', label: 'Code (permanent)', code: true, required: true, placeholder: 'NO-COIL' },
        { name: 'kind', label: 'Kind', type: 'select', options: [['Downtime', 'Downtime'], ['Scrap', 'Scrap'], ['Rework', 'Rework']] },
        { name: 'category', label: 'Stoppage category (downtime only)', type: 'select', options: [['', '—'], ...CATEGORIES.map(c => [c, c])] },
        { name: 'description', label: 'Description', required: true },
      ],
    }, (v, reason) => command('/api/execution/reason-codes', v, reason), load)));
    $$('[data-retire-reason]', section).forEach(b => b.addEventListener('click', async () => {
      const r = res.data.find(x => x.code === b.dataset.retireReason);
      const done = await confirmWithReason({ title: `Retire ${r.code}`, detail: 'It can no longer be chosen; what was recorded with it keeps it.', confirmLabel: 'Retire' },
        reason => command(`/api/execution/reason-codes/${encodeURIComponent(r.code)}`, { description: r.description, active: false }, reason));
      if (done) { toast(done.message); await load(); }
    }));
  };
  await load();
}

/** An order's quantity states and its rework and repair, on the order page. */
export async function appendOrderQuantityStates(page, order, me) {
  const section = document.createElement('section');
  section.className = 'panel';
  page.appendChild(section);
  const supervisor = canAction(me, 'execution.shopfloor.record');
  const load = async () => {
    const [states, rework, reasons, planResult] = await Promise.all([tryGet(`/api/orders/${order.orderNumber}/quantity-states`), tryGet(`/api/quality/rework?order=${order.orderNumber}`),
      tryGet('/api/execution/reason-codes'), tryGet(`/api/quality/rework-plans?order=${order.orderNumber}`)]);
    const plans = Array.isArray(planResult.data) ? planResult.data : [];
    if (states.error) { section.innerHTML = `<header><h2>Quantity states</h2></header>${errorBlock(states.error, 'quantity states')}`; return; }
    const q = states.data;
    const row = s => `<tr><td>${esc(s.state)}</td><td class="num"><b>${qty(s.quantity)}</b></td><td class="faint">${esc(s.explanation)}</td></tr>`;
    const list = rework.data ?? [];
    section.innerHTML = `<header><h2>Quantity states</h2><span class="spacer"></span>${supervisor && order.status !== 'Cancelled' ? '<button class="btn small" data-rework>Take back for rework or repair</button>' : ''}</header>
      <div class="grid-2" style="display:grid;grid-template-columns:1fr 1fr;gap:12px">
        <div><h3 style="margin:0">So far</h3><table class="table"><tbody>${q.states.filter(s => s.kind === 'SoFar').map(row).join('')}</tbody></table></div>
        <div><h3 style="margin:0">Now</h3><table class="table"><tbody>${q.states.filter(s => s.kind === 'Now').map(row).join('')}</tbody></table></div>
      </div>
      ${q.notes.map(n => `<div class="faint">${esc(n)}</div>`).join('')}
      ${list.length ? `<h3 style="margin:8px 0 0">Rework and repair</h3><table class="table"><thead><tr><th>#</th><th>Kind</th><th>Op</th><th class="num">Qty</th><th>Why</th><th>Opened</th><th>Added work</th><th>Outcome</th><th></th></tr></thead><tbody>
        ${list.map(r => `<tr><td>${r.reworkId}</td><td><span class="pill ${r.kind === 'Repair' ? 'warn' : 'info'}">${esc(r.kind)}</span></td><td>${r.sequence}</td><td class="num">${qty(r.quantity)}</td>
          <td>${r.ncrId ? `NCR ${r.ncrId}` : esc(r.reasonCode ?? '')}<div class="faint">${esc(r.instructions)}</div></td><td class="nowrap">${esc(fmtDateTime(r.openedAt))}<br><span class="faint">${esc(who(r.openedBy))}</span></td>
          <td>${reworkPlanCell(r, plans.filter(p => p.reworkId === r.reworkId), me)}</td>
          <td>${r.closedAt ? `${qty(r.goodQuantity)} good · ${qty(r.scrapQuantity)} scrap<div class="faint">${esc(r.closeNote ?? '')}</div>` : '<span class="pill warn">Open</span>'}</td>
          <td>${!r.closedAt && supervisor ? `<button class="btn small" data-close-rework="${r.reworkId}">Close</button>` : ''}</td></tr>`).join('')}</tbody></table>` : ''}
      <div class="faint" style="margin-top:6px">Production reports are never changed: rework and repair take good output back, and closing says what came out good and what was scrapped.</div>`;
    $$('[data-rework]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: `Rework or repair on order ${order.orderNumber}`, detail: 'Rework brings it back to the drawing. Repair follows an approved repair disposition on an NCR and leaves it usable, not to the drawing.', confirmLabel: 'Take back', reasonRequired: true,
      fields: [
        { name: 'kind', label: 'Kind', type: 'select', options: [['Rework', 'Rework'], ['Repair', 'Repair']] },
        { name: 'sequence', label: 'Operation whose good output goes back', type: 'select', options: order.operations.map(o => [o.sequence, `${o.sequence} — ${o.description} (${qty(o.goodQuantity)} good)`]), required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, required: true },
        { name: 'ncrId', label: 'NCR (required for repair)', type: 'number' },
        { name: 'reasonCode', label: 'Rework reason code (without an NCR)', type: 'select', options: [['', '—'], ...(reasons.data ?? []).filter(r => r.kind === 'Rework' && r.active).map(r => [r.code, `${r.code} — ${r.description}`])] },
        { name: 'instructions', label: 'Instructions', type: 'textarea', required: true },
      ],
    }, (v, reason) => command('/api/quality/rework', { ...v, orderNumber: order.orderNumber, sequence: Number(v.sequence) }, reason), load)));
    $$('[data-plan-rework]', section).forEach(b => b.addEventListener('click', () => proposeReworkPlan(order, list.find(x => x.reworkId === Number(b.dataset.planRework)), load)));
    $$('[data-decide-plan]', section).forEach(b => b.addEventListener('click', () => {
      const plan = plans.find(p => p.planId === b.dataset.decidePlan);
      const approve = b.dataset.approve === 'true';
      formModal({
        title: `${approve ? 'Approve' : 'Reject'} the rework plan for rework ${plan.reworkId}`,
        detail: approve ? 'Approval adds these operations and materials to the order, opens a completed order again and asks the technical trainers to train the people named. The NCR disposition and the rework record stay as they are.' : 'The order stays as it is; the plan stays in the history.',
        confirmLabel: approve ? 'Approve' : 'Reject', reasonRequired: true,
        fields: [{ name: 'note', label: 'Decision note', type: 'textarea', required: true }],
      }, (v, reason) => command(`/api/quality/rework-plans/${plan.planId}/decide`, { approve, note: v.note }, reason), load);
    }));
    $$('[data-close-rework]', section).forEach(b => b.addEventListener('click', () => {
      const r = list.find(x => x.reworkId === Number(b.dataset.closeRework));
      formModal({
        title: `Close ${r.kind.toLowerCase()} ${r.reworkId}`, detail: `${qty(r.quantity)} went back; good and scrap add up to that.`, confirmLabel: 'Close',
        fields: [
          { name: 'goodQuantity', label: 'Good', type: 'number', step: 'any', min: 0, value: r.quantity, required: true },
          { name: 'scrapQuantity', label: 'Scrapped', type: 'number', step: 'any', min: 0, value: 0, required: true },
          { name: 'note', label: 'Note', required: true },
        ],
      }, (v, reason) => command(`/api/quality/rework/${r.reworkId}/close`, v, reason), load);
    }));
  };
  await load();
}

const PLAN_TONE = { Proposed: 'warn', Approved: 'ok', Rejected: 'bad' };

/** What a rework's plans added (or would add) to the order, and the actions the viewer may take. */
export function reworkPlanCell(rework, plans, me) {
  const proposed = plans.find(p => p.status === 'Proposed');
  const rows = plans.map(p => `<div><span class="pill ${PLAN_TONE[p.status] ?? 'info'}">${esc(p.status)}</span>
    ${p.operations.map(o => `${o.sequence} ${esc(o.description)}${o.inspection ? ' (inspection)' : ''}`).join(' · ')}
    ${p.materials.length ? `<div class="faint">${p.materials.map(m => `${qty(m.quantity)} ${esc(m.partNumber)} at ${m.consumedAtSequence}`).join(' · ')}</div>` : ''}
    <div class="faint">${esc(p.rationale)}${p.decisionNote ? ` — ${esc(p.decisionNote)}` : ''}</div>
    ${p.status === 'Proposed' && canAction(me, 'quality.approve') ? `<button class="btn small" data-decide-plan="${esc(p.planId)}" data-approve="true">Approve</button> <button class="btn small" data-decide-plan="${esc(p.planId)}" data-approve="false">Reject</button>` : ''}</div>`).join('');
  const add = !rework.closedAt && !proposed && canAction(me, 'execution.orders.manage') ? `<button class="btn small" data-plan-rework="${rework.reworkId}">Plan added work</button>` : '';
  return rows || add ? `${rows}${add}` : '<span class="faint">None</span>';
}

const csv = value => (value ?? '').split(',').map(x => x.trim()).filter(Boolean);

/** The engineer's form for work, inspections, material and trainees a rework adds to its order. */
async function proposeReworkPlan(order, rework, reload) {
  const [workers, resources] = await Promise.all([tryGet('/api/workforce/workers'), tryGet('/api/resources/')]);
  const people = (Array.isArray(workers.data) ? workers.data : []).filter(w => w.isActive !== false).sort((a, b) => a.displayName.localeCompare(b.displayName));
  const centers = (resources.data?.workCenters ?? []).map(c => [c.code, `${c.code} — ${c.name}`]);
  const next = Math.ceil((Math.max(0, ...order.operations.map(o => o.sequence)) + 1) / 10) * 10;
  formModal({
    title: `Plan the work rework ${rework.reworkId} adds to order ${order.orderNumber}`,
    detail: `${qty(rework.quantity)} went back from operation ${rework.sequence}. Added steps come after everything on the order and carry their own labor standard and qualifications. Someone with Quality approval authority decides the plan; nothing changes until then.`,
    confirmLabel: 'Propose', reasonRequired: true,
    fields: [
      { name: 'operations', label: 'Operations', type: 'repeater', required: true, minRows: 1, rowLabel: 'operation', value: [{ sequence: next, setupMinutes: 0, runMinutesPerUnit: 0, crewSize: 1 }],
        columns: [
          { name: 'sequence', label: 'Seq', type: 'number', min: 1, max: 9999, required: true },
          { name: 'description', label: 'Description', required: true },
          { name: 'workCenterCode', label: 'Work center', type: 'select', options: centers, required: true },
          { name: 'activityCode', label: 'Activity', code: true, required: true },
          { name: 'requiredQualifications', label: 'Qualifications (comma)', code: true },
          { name: 'setupMinutes', label: 'Setup min', type: 'number', min: 0, step: 'any', required: true },
          { name: 'runMinutesPerUnit', label: 'Run min/unit', type: 'number', min: 0, step: 'any', required: true },
          { name: 'crewSize', label: 'Crew', type: 'number', min: 1, max: 20, required: true },
          { name: 'inspection', label: 'Inspection', type: 'check' },
        ] },
      { name: 'materials', label: 'Material', type: 'repeater', minRows: 0, rowLabel: 'material line',
        columns: [
          { name: 'partNumber', label: 'Part', code: true, required: true },
          { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', required: true },
          { name: 'consumedAtSequence', label: 'Used at seq', type: 'number', min: 1, required: true },
        ] },
      { name: 'trainees', label: 'People to train', type: 'repeater', minRows: 0, rowLabel: 'person',
        columns: [{ name: 'personId', label: 'Person', type: 'select', blank: '—', options: people.map(w => [w.personId, w.displayName]), required: true }] },
      { name: 'rationale', label: 'Why this work', type: 'textarea', required: true },
    ],
  }, (v, reason) => command(`/api/quality/rework/${rework.reworkId}/plans`, {
    operations: v.operations.map(o => ({ ...o, eligibleMachines: [], requiredQualifications: csv(o.requiredQualifications), inspection: !!o.inspection })),
    materials: v.materials, trainees: v.trainees.map(t => t.personId), rationale: v.rationale,
  }, reason), reload);
}

