import { tryGet, command } from '../api.js';
import { who } from '../people.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, toast, confirmWithReason } from '../ui.js';
import { formModal, qty, can, today, plusDays } from '../forms.js';

// Programs (spec §7, §72): program → contract → CLIN/SLIN → delivery → demand → production orders.
// Program Horizon counts from records: required = scheduled deliveries, delivered = shipped against
// them, order status = the approved schedule's projection. Programs you have no access to do not appear.

const DELIVERY_TONE = { Delivered: 'ok', Planned: 'info', 'Partly delivered': 'info', 'Not planned': 'warn', 'Contract changed': 'warn', Overdue: 'bad', Cancelled: 'none' };
const ORDER_TONE = { OnTime: 'ok', AtRisk: 'warn', Late: 'bad', Unschedulable: 'bad', NotScheduled: 'none' };
const ORDER_LABEL = { OnTime: 'On schedule', AtRisk: 'At risk', Late: 'Projected late', Unschedulable: 'Cannot be scheduled', NotScheduled: 'Not scheduled' };
const money = n => n == null ? '—' : Number(n).toLocaleString(undefined, { style: 'currency', currency: 'USD', maximumFractionDigits: 0 });

export async function programsPage(page, ctx) {
  const id = ctx.params[0];
  return id ? programDetail(page, ctx, id) : programList(page, ctx);
}

async function programList(page, ctx) {
  const manager = can(ctx.me, 'ProgramManager');
  const list = await tryGet('/api/programs/');
  page.innerHTML = `
    <div class="page-head"><div><h1>Programs</h1><div class="sub">Program Horizon: each program you have access to, from contract deliveries through orders and the approved schedule.</div></div>
      <div class="spacer"></div>${manager ? '<button class="btn primary" id="new">New program</button>' : ''}</div>
    ${list.error ? errorBlock(list.error, 'programs') : !list.data.length
      ? emptyBlock('No programs to show', manager ? 'Create one here. A security administrator then grants people (including you) access to it.' : 'You have access to no programs. A security administrator grants program access.')
      : `<div class="program-grid">${list.data.map(card).join('')}</div>`}`;
  $('#new', page)?.addEventListener('click', () => formModal({
    title: 'New program', detail: 'Program data is segregated: only people a security administrator grants access to can see it — that includes you.', confirmLabel: 'Create program',
    fields: [
      { name: 'code', label: 'Program code', code: true, required: true, placeholder: 'FALCON' },
      { name: 'name', label: 'Name', required: true },
      { name: 'customer', label: 'Customer' },
      { name: 'description', label: 'Description', type: 'textarea' },
    ],
  }, (v, reason) => command('/api/programs/', v, reason), () => programList(page, ctx)));
}

function card(h) {
  const p = h.program;
  const pct = h.unitsRequired ? Math.round((100 * h.unitsDelivered) / h.unitsRequired) : 0;
  return `<a class="panel program-card" href="#/programs/${p.programId}">
    <header><h2>${esc(p.code)}</h2><span class="muted">${esc(p.name)}</span><span class="spacer"></span>${p.status === 'Closed' ? '<span class="pill none">Closed</span>' : ''}</header>
    <dl class="kv">
      <dt>Units required</dt><dd>${qty(h.unitsRequired)}</dd>
      <dt>Delivered</dt><dd>${qty(h.unitsDelivered)} <span class="faint">(${pct}%)</span></dd>
      <dt>On schedule</dt><dd class="c-ok">${h.onSchedule}</dd>
      <dt>At risk</dt><dd class="${h.atRisk ? 'c-warn' : ''}">${h.atRisk}</dd>
      <dt>Projected late</dt><dd class="${h.projectedLate ? 'c-bad' : ''}">${h.projectedLate}</dd>
      <dt>Primary constraint</dt><dd>${h.primaryConstraint ? esc(h.primaryConstraint.constraint) : '<span class="faint">none in the approved schedule</span>'}</dd>
    </dl></a>`;
}

async function programDetail(page, ctx, id) {
  const me = ctx.me;
  const manager = can(me, 'ProgramManager');
  const planner = can(me, 'Planner');
  const [horizon, contracts, items] = await Promise.all([tryGet(`/api/programs/${id}`), tryGet(`/api/programs/${id}/contracts`), tryGet('/api/engineering/items')]);
  const back = '<a href="#/programs">← Programs</a>';
  if (horizon.error) { page.innerHTML = back + errorBlock(horizon.error, 'this program'); return; }
  const h = horizon.data;
  const p = h.program;
  const all = contracts.data ?? [];
  const reload = () => programDetail(page, ctx, id);
  const parts = (items.data ?? []).map(i => [i.partNumber, `${i.partNumber} — ${i.description}`]);
  const active = p.status === 'Active';

  page.innerHTML = `${back}
    <div class="page-head"><div><h1>${esc(p.code)} · ${esc(p.name)}</h1><div class="sub">${esc(p.customer ?? 'No customer recorded')}${p.description ? ` · ${esc(p.description)}` : ''}</div></div>
      <div class="spacer"></div>
      ${manager && active ? '<button class="btn" id="contract">Add contract</button> <button class="btn" id="milestone">Add milestone</button>' : ''}
      ${manager ? `<button class="btn ghost" id="status">${active ? 'Close program' : 'Reopen program'}</button>` : ''}</div>
    <section class="kpis">
      ${kpi('Units required', qty(h.unitsRequired), `${qty(h.unitsPlanned)} planned as demand`)}
      ${kpi('Delivered', qty(h.unitsDelivered), h.unitsRequired ? `${Math.round((100 * h.unitsDelivered) / h.unitsRequired)}% of scheduled deliveries` : 'no deliveries scheduled')}
      ${kpi('On schedule', h.onSchedule, 'orders', 'ok')}
      ${kpi('At risk', h.atRisk, 'orders', h.atRisk ? 'warn' : '')}
      ${kpi('Projected late', h.projectedLate, 'orders', h.projectedLate ? 'bad' : '')}
      ${kpi('Primary constraint', h.primaryConstraint ? esc(h.primaryConstraint.constraint) : '—', h.primaryConstraint ? `${h.primaryConstraint.operations} operation(s) waited on it` : 'from the approved schedule')}
    </section>
    <div class="faint" style="margin:-6px 0 10px">${h.scheduleVersion ? `Order status from approved schedule v${h.scheduleVersion} (${esc(fmtDateTime(h.scheduleApprovedAt))}).` : 'No schedule has been approved yet, so no order has a projected status.'}
      ${h.primaryConstraint ? ` Example: ${esc(h.primaryConstraint.example)}` : ''}</div>

    <section class="panel"><header><h2>Deliveries</h2><span class="spacer"></span><span class="muted">scheduled on contract CLINs; a planner turns each into demand</span></header>
      ${!h.deliveries.length ? emptyBlock('No deliveries scheduled', manager ? 'Add a contract and a CLIN for a part, then schedule deliveries against it.' : 'The program manager schedules contract deliveries.') : `
      <table class="table"><thead><tr><th>#</th><th>Contract / CLIN</th><th>Part</th><th class="num">Qty</th><th>Due</th><th class="num">Shipped</th><th>Status</th><th></th></tr></thead><tbody>
      ${h.deliveries.map(d => `<tr class="${d.cancelled ? 'retired' : ''}"><td>${d.deliveryId}</td><td>${esc(d.contractNumber)} · ${esc(d.clin)}</td><td>${esc(d.partNumber ?? '—')}</td>
        <td class="num">${qty(d.quantity)}</td><td class="${d.state === 'Overdue' ? 'c-bad' : ''}">${esc(fmtDate(d.dueDate))}</td><td class="num">${qty(d.shipped)}</td>
        <td><span class="pill ${DELIVERY_TONE[d.state] ?? 'none'}">${esc(d.state)}</span>${d.demandId ? ` <span class="faint">demand ${d.demandId}</span>` : ''}</td>
        <td class="nowrap">${planner && (d.state === 'Not planned' || d.state === 'Contract changed' || (d.cancelled && d.demandId && !d.demandInStep)) ? `<button class="btn small primary" data-plan="${d.deliveryId}">${d.demandId ? 'Update demand' : 'Plan as demand'}</button>` : ''}
          ${manager && !d.cancelled && d.shipped < d.quantity ? `<button class="btn small" data-change="${d.deliveryId}">Change</button> <button class="btn small ghost" data-cancel="${d.deliveryId}">Cancel</button>` : ''}</td></tr>`).join('')}
      </tbody></table>`}
    </section>

    <section class="panel"><header><h2>Production orders</h2></header>
      ${!h.orders.length ? emptyBlock('No open orders on this program', 'Orders created for this program appear here with their projected delivery.') : `
      <table class="table"><thead><tr><th>Order</th><th>Part</th><th class="num">Qty</th><th>Due</th><th>Status</th><th>Delivery</th><th>Why</th></tr></thead><tbody>
      ${h.orders.map(o => `<tr><td><a href="#/orders/${o.orderNumber}">#${o.orderNumber}</a></td><td>${esc(o.partNumber)}</td><td class="num">${qty(o.quantity)}</td><td>${esc(fmtDate(o.dueDate))}</td><td>${esc(o.orderStatus)}</td>
        <td><span class="pill ${ORDER_TONE[o.delivery] ?? 'none'}">${esc(ORDER_LABEL[o.delivery] ?? o.delivery)}</span>${o.projectedCompletion ? ` <span class="faint">${esc(fmtDateTime(o.projectedCompletion))}</span>` : ''}</td>
        <td class="faint">${esc(o.explanation ?? '')}</td></tr>`).join('')}</tbody></table>`}
    </section>

    ${contracts.error ? errorBlock(contracts.error, 'contracts') : all.map(c => contractPanel(c, h, manager && active)).join('') || `<section class="panel">${emptyBlock('No contracts', manager ? 'Add the contract or award this program works under.' : 'The program manager records contracts.')}</section>`}

    <section class="panel"><header><h2>Milestones</h2></header>
      ${!h.milestones.length ? emptyBlock('No milestones', 'Design reviews, first articles, data deliverables and other dated commitments.') : `
      <table class="table"><thead><tr><th>Milestone</th><th>Due</th><th>Done</th><th></th></tr></thead><tbody>
      ${h.milestones.map(m => {
        const late = !m.completedOn && m.dueDate < h.today;
        return `<tr><td>${esc(m.title)}</td><td class="${late ? 'c-bad' : ''}">${esc(fmtDate(m.dueDate))}${late ? ' · overdue' : ''}</td>
          <td>${m.completedOn ? `${esc(fmtDate(m.completedOn))} <span class="faint">by ${esc(who(m.completedBy))}</span>` : '—'}</td>
          <td>${manager && !m.completedOn ? `<button class="btn small" data-done="${m.milestoneId}">Mark done</button>` : ''}</td></tr>`;
      }).join('')}</tbody></table>`}
    </section>`;

  const contractById = cid => all.find(c => c.contractId === Number(cid));
  $('#contract', page)?.addEventListener('click', () => formModal({ title: `Add a contract to ${p.code}`, confirmLabel: 'Add contract',
    fields: [
      { name: 'number', label: 'Contract number', code: true, required: true, placeholder: 'FA8650-26-C-1234' },
      { name: 'customer', label: 'Customer', value: p.customer, required: true },
      { name: 'title', label: 'Title', required: true },
      { name: 'periodStart', label: 'Period of performance starts', type: 'date', value: today(), required: true },
      { name: 'periodEnd', label: 'Period of performance ends', type: 'date', value: plusDays(365), required: true },
      { name: 'ceiling', label: 'Ceiling value (optional, USD)', type: 'number', min: 0, step: 'any' },
    ] }, (v, reason) => command(`/api/programs/${id}/contracts`, v, reason), reload));
  $('#milestone', page)?.addEventListener('click', () => formModal({ title: `Add a milestone to ${p.code}`, confirmLabel: 'Add milestone',
    fields: [{ name: 'title', label: 'Milestone', required: true }, { name: 'dueDate', label: 'Due', type: 'date', value: plusDays(30), required: true }] },
  (v, reason) => command(`/api/programs/${id}/milestones`, v, reason), reload));
  $('#status', page)?.addEventListener('click', async () => {
    const done = await confirmWithReason({ title: `${active ? 'Close' : 'Reopen'} ${p.code}`, detail: active ? 'A closed program takes no new orders or contracts.' : '', confirmLabel: active ? 'Close program' : 'Reopen' },
      reason => command(`/api/programs/${id}/${active ? 'close' : 'reopen'}`, {}, reason));
    if (done) { toast(done.message); await reload(); }
  });
  $$('[data-done]', page).forEach(b => b.addEventListener('click', () => formModal({ title: 'Milestone done', confirmLabel: 'Mark done',
    fields: [{ name: 'completedOn', label: 'Completed on', type: 'date', value: today(), required: true }] },
  (v, reason) => command(`/api/programs/milestones/${b.dataset.done}/complete`, v, reason), reload)));
  $$('[data-plan]', page).forEach(b => b.addEventListener('click', () => formModal({ title: `Plan delivery ${b.dataset.plan} as demand`, detail: 'Creates contract-delivery demand for MRP and shipping, or brings existing demand back in step with the contract.', confirmLabel: 'Plan',
    fields: [{ name: 'priority', label: 'Priority (1 highest – 9)', type: 'number', min: 1, value: 3, required: true }] },
  (v, reason) => command(`/api/planning/demand/from-delivery/${b.dataset.plan}`, v, reason), reload)));
  $$('[data-change]', page).forEach(b => {
    const d = h.deliveries.find(x => x.deliveryId === Number(b.dataset.change));
    b.addEventListener('click', () => formModal({ title: `Change delivery ${d.deliveryId}`, detail: 'Planned demand shows as out of date until a planner updates it.', confirmLabel: 'Change', reasonRequired: true,
      fields: [{ name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', value: d.quantity, required: true }, { name: 'dueDate', label: 'Due', type: 'date', value: d.dueDate, required: true }] },
    (v, reason) => command(`/api/programs/deliveries/${d.deliveryId}/change`, v, reason), reload));
  });
  $$('[data-cancel]', page).forEach(b => b.addEventListener('click', async () => {
    const done = await confirmWithReason({ title: `Cancel delivery ${b.dataset.cancel}`, detail: 'Its demand is cancelled when a planner updates it.', confirmLabel: 'Cancel delivery' },
      reason => command(`/api/programs/deliveries/${b.dataset.cancel}/cancel`, {}, reason));
    if (done) { toast(done.message); await reload(); }
  }));
  $$('[data-clin]', page).forEach(b => b.addEventListener('click', () => {
    const c = contractById(b.dataset.clin);
    return formModal({ title: `Add a CLIN or SLIN to ${c.number}`, confirmLabel: 'Add',
      fields: [
        { name: 'clin', label: 'CLIN (0001) or SLIN (0001AA)', code: true, required: true },
        { name: 'parentClin', label: 'Under CLIN (SLINs only)', type: 'select', blank: '— none —', options: c.clins.filter(l => !l.parentClin).map(l => [l.clin, `${l.clin} ${l.description}`]) },
        { name: 'description', label: 'Description', required: true },
        { name: 'partNumber', label: 'Part delivered (leave empty for data or services)', type: 'select', blank: '— none —', options: parts },
        { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any' },
        { name: 'unitPrice', label: 'Unit price (USD)', type: 'number', min: 0, step: 'any' },
      ] }, (v, reason) => command(`/api/programs/contracts/${c.contractId}/clins`, v, reason), reload);
  }));
  $$('[data-delivery]', page).forEach(b => b.addEventListener('click', () => {
    const c = contractById(b.dataset.delivery);
    return formModal({ title: `Schedule a delivery on ${c.number}`, detail: `Within the period of performance, ${fmtDate(c.periodStart)} to ${fmtDate(c.periodEnd)}.`, confirmLabel: 'Schedule delivery',
      fields: [
        { name: 'clin', label: 'CLIN', type: 'select', options: c.clins.filter(l => l.partNumber).map(l => [l.clin, `${l.clin} — ${l.partNumber}${l.quantity ? ` × ${qty(l.quantity)}` : ''}`]), required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', required: true },
        { name: 'dueDate', label: 'Due', type: 'date', value: plusDays(60), required: true },
      ] }, (v, reason) => command(`/api/programs/contracts/${c.contractId}/deliveries`, v, reason), reload);
  }));
  $$('[data-requirement]', page).forEach(b => b.addEventListener('click', () => formModal({ title: 'Add a contract requirement', confirmLabel: 'Add',
    fields: [{ name: 'code', label: 'Clause or code', required: true, placeholder: 'FAI per AS9102' }, { name: 'text', label: 'Requirement', type: 'textarea', required: true }] },
  (v, reason) => command(`/api/programs/contracts/${b.dataset.requirement}/requirements`, v, reason), reload)));
  $$('[data-modify]', page).forEach(b => b.addEventListener('click', () => {
    const c = contractById(b.dataset.modify);
    return formModal({ title: `Modify ${c.number}`, detail: 'A contract modification: record the mod number in the reason.', confirmLabel: 'Record modification', reasonRequired: true,
      fields: [
        { name: 'title', label: 'Title', value: c.title, required: true },
        { name: 'periodStart', label: 'Period starts', type: 'date', value: c.periodStart, required: true },
        { name: 'periodEnd', label: 'Period ends', type: 'date', value: c.periodEnd, required: true },
        { name: 'ceiling', label: 'Ceiling (USD)', type: 'number', min: 0, step: 'any', value: c.ceiling },
      ] }, (v, reason) => command(`/api/programs/contracts/${c.contractId}/modify`, v, reason), reload);
  }));
}

function kpi(label, value, sub, tone = '') {
  return `<div class="kpi"><div><div class="t">${esc(label)}</div><div class="v ${tone ? `c-${tone}` : ''}">${value}</div><div class="why">${esc(sub)}</div></div></div>`;
}

function contractPanel(c, h, edit) {
  const summary = h.contracts.find(s => s.contractId === c.contractId);
  return `<section class="panel">
    <header><h2>${esc(c.number)}</h2><span class="muted">${esc(c.title)} · ${esc(c.customer)}</span><span class="pill ${c.status === 'Active' ? 'ok' : 'none'}">${esc(c.status)}</span><span class="spacer"></span>
      ${edit && c.status === 'Active' ? `<button class="btn small" data-clin="${c.contractId}">+ CLIN</button> <button class="btn small" data-delivery="${c.contractId}">+ Delivery</button> <button class="btn small" data-requirement="${c.contractId}">+ Requirement</button> <button class="btn small ghost" data-modify="${c.contractId}">Modify</button>` : ''}</header>
    <div class="faint" style="margin-bottom:6px">Period of performance ${esc(fmtDate(c.periodStart))} – ${esc(fmtDate(c.periodEnd))} · ceiling ${money(c.ceiling)}${summary?.value != null ? ` · CLIN value ${money(summary.value)}` : ''}</div>
    ${c.clins.length ? `<table class="table"><thead><tr><th>CLIN</th><th>Description</th><th>Part</th><th class="num">Qty</th><th class="num">Unit price</th><th class="num">Scheduled</th></tr></thead><tbody>
      ${c.clins.map(l => {
        const scheduled = c.deliveries.filter(d => d.clin === l.clin && !d.cancelled).reduce((s, d) => s + d.quantity, 0);
        return `<tr><td>${l.parentClin ? '&nbsp;&nbsp;↳ ' : ''}<b>${esc(l.clin)}</b></td><td>${esc(l.description)}</td><td>${esc(l.partNumber ?? '—')}</td>
          <td class="num">${l.quantity != null ? qty(l.quantity) : '—'}</td><td class="num">${l.unitPrice != null ? money(l.unitPrice) : '—'}</td>
          <td class="num ${l.quantity != null && scheduled < l.quantity ? 'c-warn' : ''}">${l.partNumber ? qty(scheduled) : '—'}</td></tr>`;
      }).join('')}</tbody></table>` : '<div class="muted">No CLINs yet.</div>'}
    ${c.requirements.length ? `<div style="margin-top:8px"><b>Requirements</b><ul>${c.requirements.map(r => `<li><b>${esc(r.code)}</b> — ${esc(r.text)}</li>`).join('')}</ul></div>` : ''}
  </section>`;
}
