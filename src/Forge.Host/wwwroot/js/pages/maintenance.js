import { tryGet, command } from '../api.js';
import { who } from '../people.js';
import { attachmentsDrawer } from '../attachments.js';
import { esc, $, $$, fmtNumber, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast } from '../ui.js';
import { formModal, can, today, plusDays, qty } from '../forms.js';
import { isDownAt } from '../model.js';

// Maintenance (spec §47): machine status, preventive-maintenance plans and work orders. Work occupies
// planned downtime, so capacity and the schedule see it. A technician does the work and confirms the
// checklist; a different person signs it off. A breakdown raises a corrective work order, and the
// machine returns to service when that is signed off. An overdue lock-out PM locks its machine out.

const WO_TONE = { Open: 'none', Scheduled: 'info', InProgress: 'warn', Completed: 'info', Closed: 'ok', Cancelled: 'bad' };
const WO_LABEL = { Open: 'Open', Scheduled: 'Scheduled', InProgress: 'In progress', Completed: 'Awaiting sign-off', Closed: 'Closed', Cancelled: 'Cancelled' };
const DOWN_LABEL = { Planned: 'Planned maintenance', Unplanned: 'Down', MaintenanceOverdue: 'Locked out — PM overdue' };
// Due by date and/or by run hours; overdue when either is past.
const isOpen = o => ['Open', 'Scheduled', 'InProgress'].includes(o.status);
const overdueNow = o => isOpen(o) && ((o.dueDate && o.dueDate < today()) || o.dueByRunHours);
const dueText = o => [o.dueDate ? `due ${fmtDate(o.dueDate)}` : null, o.dueRunHours != null ? `due at ${fmtNumber(o.dueRunHours)} run h${o.runHours != null ? ` (${fmtNumber(o.runHours)} so far)` : ''}` : null].filter(Boolean).join(' or ');
const local = iso => { const d = new Date(iso); d.setMinutes(d.getMinutes() - d.getTimezoneOffset()); return d.toISOString().slice(0, 16); };

export async function maintenancePage(page, ctx) {
  const me = ctx.me;
  const report = can(me, 'MaintenanceTechnician', 'Supervisor', 'MaintenancePlanner');
  const tech = can(me, 'MaintenanceTechnician');
  const spares = can(me, 'MaintenanceTechnician', 'MaterialHandler');
  const planner = can(me, 'MaintenancePlanner');
  const schedule = can(me, 'MaintenanceTechnician', 'MaintenancePlanner');
  let tab = ctx.query.get('tab') ?? 'machines';
  let showClosed = false;
  let data = {};

  async function load() {
    const now = Date.now();
    const iso = ms => new Date(ms).toISOString();
    const [resources, stock, items, downtime, orders, plans] = await Promise.all([
      tryGet('/api/resources/'), tryGet('/api/inventory/stock'), tryGet('/api/engineering/items'),
      tryGet(`/api/maintenance/downtime?from=${encodeURIComponent(iso(now - 30 * 864e5))}&to=${encodeURIComponent(iso(now + 60 * 864e5))}`),
      tryGet(`/api/maintenance/work-orders?includeClosed=${showClosed}`), tryGet('/api/maintenance/plans'),
    ]);
    // Each entry wraps a work order with its run hours (run-hour PM) and the spare parts used on it.
    if (orders.data) orders.data = orders.data.map(v => ({ ...v.order, runHours: v.runHours, dueByRunHours: v.dueByRunHours, parts: v.parts }));
    data = { machines: resources.data?.machines ?? [], resources, downtime, orders, plans, stock: stock.data ?? [], items: items.data ?? [] };
  }
  const reload = async () => { await load(); render(); };
  const mine = actor => actor === `person:${me.personId}`;
  const machineOptions = () => data.machines.map(m => [m.code, `${m.code} — ${m.name}`]);

  function render() {
    const orders = data.orders.data ?? [];
    const overdue = orders.filter(overdueNow);
    const signOff = orders.filter(o => o.status === 'Completed').length;
    const tabs = [['machines', 'Machines'], ['orders', `Work orders${orders.length ? ` (${orders.filter(o => o.status !== 'Closed' && o.status !== 'Cancelled').length})` : ''}`], ['plans', 'PM plans'], ['log', 'Downtime log']];
    page.innerHTML = `
      <div class="page-head"><div><h1>Maintenance</h1><div class="sub">Machine status, preventive maintenance and work orders. Maintenance work is planned downtime: it removes capacity and moves scheduled work.</div></div>
        <div class="spacer"></div>
        ${report ? '<button class="btn" id="raise">Raise work order</button>' : ''}
        ${planner ? '<button class="btn primary" id="new-plan">New PM plan</button>' : ''}</div>
      ${overdue.length ? `<div class="notice bad">${overdue.length} work order(s) are past due: ${overdue.map(o => `${o.workOrderId} (${esc(o.machineCode)})`).join(', ')}.</div>` : ''}
      ${signOff && planner ? `<div class="notice">${signOff} work order(s) are waiting for your sign-off.</div>` : ''}
      <section class="panel">
        <header><div class="seg">${tabs.map(([k, l]) => `<button data-tab="${k}" class="${tab === k ? 'on' : ''}">${esc(l)}</button>`).join('')}</div>
          ${tab === 'orders' ? `<span class="spacer"></span><label class="opt"><input type="checkbox" id="closed" ${showClosed ? 'checked' : ''}> Show closed</label>` : ''}</header>
        ${{ machines: machinesTab, orders: ordersTab, plans: plansTab, log: logTab }[tab]?.() ?? machinesTab()}
      </section>`;

    $$('[data-tab]', page).forEach(b => b.addEventListener('click', () => { tab = b.dataset.tab; render(); }));
    $('#closed', page)?.addEventListener('change', async e => { showClosed = e.target.checked; await reload(); });
    $('#raise', page)?.addEventListener('click', () => raise());
    $('#new-plan', page)?.addEventListener('click', () => editPlan(null));
    $$('[data-down]', page).forEach(b => b.addEventListener('click', () => reportDown(b.dataset.down)));
    $$('[data-restore]', page).forEach(b => b.addEventListener('click', () => restore(b.dataset.restore)));
    $$('[data-window]', page).forEach(b => b.addEventListener('click', () => planWindow(b.dataset.window)));
    $$('[data-schedule]', page).forEach(b => b.addEventListener('click', () => scheduleOrder(Number(b.dataset.schedule))));
    $$('[data-start]', page).forEach(b => b.addEventListener('click', () => step(Number(b.dataset.start), 'start', 'Start', 'The machine is taken out of production now.', false)));
    $$('[data-docs-wo]', page).forEach(b => b.addEventListener('click', () => attachmentsDrawer(`Work order ${b.dataset.docsWo}`, { type: 'WorkOrder', id: b.dataset.docsWo, me, note: 'Procedures to follow, manuals, photos and service reports for this work.' })));
    $$('[data-complete]', page).forEach(b => b.addEventListener('click', () => complete(Number(b.dataset.complete))));
    $$('[data-signoff]', page).forEach(b => b.addEventListener('click', () => step(Number(b.dataset.signoff), 'sign-off', 'Sign off', 'You confirm the work was done properly. A preventive order raises the next one; a repair returns the machine to service.', false)));
    $$('[data-sendback]', page).forEach(b => b.addEventListener('click', () => step(Number(b.dataset.sendback), 'send-back', 'Send back', 'The order goes back to the technician as in progress.', true)));
    $$('[data-cancel]', page).forEach(b => b.addEventListener('click', () => step(Number(b.dataset.cancel), 'cancel', 'Cancel', 'A skipped PM is due again one interval later.', true)));
    $$('[data-edit-plan]', page).forEach(b => b.addEventListener('click', () => editPlan((data.plans.data ?? []).find(p => p.planId === Number(b.dataset.editPlan)))));
    $$('[data-retire]', page).forEach(b => b.addEventListener('click', () => retire(Number(b.dataset.retire))));
    $$('[data-issue]', page).forEach(b => b.addEventListener('click', () => sparePart(Number(b.dataset.issue), false)));
    $$('[data-return]', page).forEach(b => b.addEventListener('click', () => sparePart(Number(b.dataset.return), true)));
  }

  // ------------------------------------------------------------ tabs

  function machinesTab() {
    if (data.resources.error) return errorBlock(data.resources.error, 'machines');
    if (!data.machines.length) return emptyBlock('No machines yet', 'Machines are set up in Plant Setup.');
    const now = Date.now();
    const windows = data.downtime.data ?? [];
    const orders = data.orders.data ?? [];
    return `<table class="table"><thead><tr><th>Machine</th><th>Work center</th><th>Status</th><th>Next PM</th><th></th></tr></thead><tbody>
      ${data.machines.map(m => {
        const down = windows.find(w => w.machineCode === m.code && isDownAt(w, now));
        const nextPm = orders.filter(o => o.machineCode === m.code && o.kind === 'Preventive' && ['Open', 'Scheduled', 'InProgress'].includes(o.status)).sort((a, b) => (a.dueDate ?? '9999').localeCompare(b.dueDate ?? '9999'))[0];
        return `<tr><td><b>${esc(m.code)}</b> <span class="muted">${esc(m.name)}</span></td><td>${esc(m.workCenterCode)}</td>
          <td>${down ? `<span class="pill bad">${esc(DOWN_LABEL[down.kind] ?? down.kind)}</span> <span class="muted">since ${esc(fmtDateTime(down.startsAt))} · ${esc(down.reason)}</span>` : '<span class="pill ok">Up</span>'}</td>
          <td>${nextPm ? `${esc(nextPm.title)} <span class="${overdueNow(nextPm) ? 'c-bad' : 'faint'}">${esc(dueText(nextPm))}</span>` : '<span class="faint">—</span>'}</td>
          <td class="nowrap">${down && down.kind === 'Unplanned' && report ? `<button class="btn small" data-restore="${esc(m.code)}">Restore</button>` : ''}
            ${!down && report ? `<button class="btn small" data-down="${esc(m.code)}">Report down</button>` : ''}
            ${schedule ? `<button class="btn small ghost" data-window="${esc(m.code)}">Plan window</button>` : ''}</td></tr>`;
      }).join('')}</tbody></table>`;
  }

  function ordersTab() {
    if (data.orders.error) return errorBlock(data.orders.error, 'work orders');
    const rows = data.orders.data;
    if (!rows.length) return emptyBlock('No work orders', 'PM plans raise them on schedule; a breakdown raises a corrective one.');
    return rows.map(o => {
      const late = overdueNow(o);
      return `<div class="card-row">
        <div class="grow">
          <div><b>WO ${o.workOrderId}</b> · ${esc(o.machineCode)} · ${esc(o.title)} <span class="pill ${WO_TONE[o.status]}">${esc(WO_LABEL[o.status])}</span> <span class="pill none">${esc(o.kind)}</span></div>
          <div class="faint ${late ? 'c-bad' : ''}">${esc(dueText(o).replace(/^d/, 'D'))}${late ? ' · overdue' : ''}${o.scheduledStart ? ` · window ${esc(fmtDateTime(o.scheduledStart))} – ${esc(fmtDateTime(o.scheduledEnd))}` : ''}</div>
          ${o.description ? `<div>${esc(o.description)}</div>` : ''}
          ${o.tasks.length ? `<div class="faint">Checklist: ${o.tasks.map(esc).join(' · ')}</div>` : ''}
          ${o.parts?.length ? `<div>Parts used: ${o.parts.filter(p => p.netIssued > 0).map(p => `${qty(p.netIssued)} ${esc(p.partNumber)}`).join(' · ') || '<span class="faint">all returned</span>'}</div>` : ''}
          <div class="faint">Raised by ${esc(who(o.createdBy))}${o.startedBy ? ` · started by ${esc(who(o.startedBy))} ${esc(fmtDateTime(o.startedAt))}` : ''}${o.completedBy ? ` · completed by ${esc(who(o.completedBy))}: ${esc(o.findings ?? '')}` : ''}${o.closedBy ? ` · signed off by ${esc(who(o.closedBy))} ${esc(fmtDateTime(o.closedAt))}` : ''}${o.cancelReason ? ` · cancelled: ${esc(o.cancelReason)}` : ''}</div>
        </div>
        <div class="nowrap">
          ${['Open', 'Scheduled'].includes(o.status) && schedule ? `<button class="btn small" data-schedule="${o.workOrderId}">${o.status === 'Scheduled' ? 'Reschedule' : 'Schedule'}</button>` : ''}
          ${['Open', 'Scheduled'].includes(o.status) && tech ? `<button class="btn primary small" data-start="${o.workOrderId}">Start</button>` : ''}
          ${o.status === 'InProgress' && tech ? `<button class="btn primary small" data-complete="${o.workOrderId}">Complete</button>` : ''}
          ${o.status === 'Completed' && planner && !mine(o.completedBy) && !mine(o.startedBy) ? `<button class="btn primary small" data-signoff="${o.workOrderId}">Sign off</button> <button class="btn small" data-sendback="${o.workOrderId}">Send back</button>` : ''}
          ${o.status === 'Completed' && (mine(o.completedBy) || mine(o.startedBy)) ? '<span class="faint">You did the work — someone else signs it off</span>' : ''}
          ${isOpen(o) && spares ? `<button class="btn small" data-issue="${o.workOrderId}">Issue part</button>` : ''}
          ${o.parts?.some(p => p.netIssued > 0) && spares ? `<button class="btn small ghost" data-return="${o.workOrderId}">Return part</button>` : ''}
          ${['Open', 'Scheduled'].includes(o.status) && schedule ? `<button class="btn small ghost" data-cancel="${o.workOrderId}">Cancel</button>` : ''}
          <button class="btn small ghost" data-docs-wo="${o.workOrderId}">Documents</button>
        </div></div>`;
    }).join('');
  }

  function plansTab() {
    if (data.plans.error) return errorBlock(data.plans.error, 'PM plans');
    const rows = data.plans.data;
    if (!rows.length) return emptyBlock('No PM plans', planner ? 'Add one: a machine, an interval and a checklist.' : 'A maintenance planner sets them up.');
    return `<table class="table"><thead><tr><th>Plan</th><th>Machine</th><th>Every</th><th>Takes</th><th>Checklist</th><th>If overdue</th><th></th></tr></thead><tbody>
      ${rows.map(p => `<tr class="${p.active ? '' : 'retired'}"><td><b>${p.planId}</b> ${esc(p.title)}${p.active ? '' : ' <span class="pill none">Retired</span>'}</td><td>${esc(p.machineCode)}</td>
        <td>${[p.intervalDays ? `${p.intervalDays} day(s)` : null, p.intervalRunHours ? `${fmtNumber(p.intervalRunHours)} run h` : null].filter(Boolean).join(' or ')}</td><td>${fmtNumber(p.durationMinutes / 60)} h</td><td class="faint">${p.tasks.map(esc).join('<br>')}</td>
        <td>${p.lockOutWhenOverdue ? `<span class="pill warn">Lock out</span>${p.graceDays ? ` after ${p.graceDays} day(s) grace` : ''}${p.graceRunHours ? ` / ${fmtNumber(p.graceRunHours)} run h` : ''}` : '<span class="faint">Warn only</span>'}</td>
        <td class="nowrap">${planner && p.active ? `<button class="btn small" data-edit-plan="${p.planId}">Edit</button> <button class="btn small ghost" data-retire="${p.planId}">Retire</button>` : ''}</td></tr>`).join('')}
      </tbody></table>`;
  }

  function logTab() {
    if (data.downtime.error) return errorBlock(data.downtime.error, 'downtime');
    const now = Date.now();
    const windows = data.downtime.data;
    if (!windows.length) return emptyBlock('No downtime recorded', 'Nothing in the last 30 days or the next 60.');
    return `<table class="table"><thead><tr><th>Machine</th><th>Kind</th><th>From</th><th>Until</th><th>Hours</th><th>Reason</th></tr></thead><tbody>
      ${[...windows].sort((a, b) => new Date(b.startsAt) - new Date(a.startsAt)).map(w => `<tr><td>${esc(w.machineCode)}</td>
        <td><span class="pill ${w.kind === 'Planned' ? 'info' : 'bad'}">${esc(DOWN_LABEL[w.kind] ?? w.kind)}</span></td><td>${esc(fmtDateTime(w.startsAt))}</td>
        <td>${w.endsAt ? esc(fmtDateTime(w.endsAt)) : '<span class="c-bad">open</span>'}</td>
        <td>${w.endsAt ? fmtNumber((new Date(w.endsAt) - new Date(w.startsAt)) / 36e5) : `${fmtNumber(Math.max(0, now - new Date(w.startsAt)) / 36e5)} so far`}</td>
        <td>${esc(w.reason)}</td></tr>`).join('')}</tbody></table>`;
  }

  // ------------------------------------------------------------ actions

  async function step(id, verb, label, detail, reasonRequired) {
    const done = await confirmWithReason({ title: `${label} work order ${id}`, detail, confirmLabel: label, reasonRequired }, reason => command(`/api/maintenance/work-orders/${id}/${verb}`, {}, reason));
    if (done) { toast(done.message); await reload(); }
  }

  function reportDown(machine) {
    return formModal({ title: `Report ${machine} down`, detail: 'Raises a corrective work order. The machine returns to service when the repair is signed off.', confirmLabel: 'Report down',
      fields: [{ name: 'description', label: 'What is wrong', type: 'textarea', required: true }] },
    (v, reason) => command(`/api/maintenance/machines/${encodeURIComponent(machine)}/down`, v, reason), reload);
  }

  async function restore(machine) {
    const done = await confirmWithReason({ title: `Restore ${machine}`, detail: 'For a fault that needed no repair. A repair already under way is returned to service by signing off its work order.', confirmLabel: 'Restore' },
      reason => command(`/api/maintenance/machines/${encodeURIComponent(machine)}/restore`, {}, reason));
    if (done) { toast(done.message); await reload(); }
  }

  function planWindow(machine) {
    return formModal({ title: `Plan a maintenance window on ${machine}`, confirmLabel: 'Plan window',
      fields: [
        { name: 'startsAt', label: 'Starts', type: 'datetime-local', required: true },
        { name: 'endsAt', label: 'Ends', type: 'datetime-local', required: true },
        { name: 'description', label: 'Work to be done', required: true },
      ] },
    (v, reason) => command(`/api/maintenance/machines/${encodeURIComponent(machine)}/planned`, { ...v, startsAt: new Date(v.startsAt).toISOString(), endsAt: new Date(v.endsAt).toISOString() }, reason), reload);
  }

  function raise() {
    return formModal({ title: 'Raise a corrective work order', detail: 'For a fault that has not stopped the machine. If it has, report the machine down instead.', confirmLabel: 'Raise',
      fields: [
        { name: 'machineCode', label: 'Machine', type: 'select', options: machineOptions(), required: true },
        { name: 'title', label: 'What needs doing', required: true },
        { name: 'description', label: 'Details', type: 'textarea' },
        { name: 'dueDate', label: 'Due', type: 'date', value: plusDays(7), required: true },
      ] }, (v, reason) => command('/api/maintenance/work-orders', v, reason), reload);
  }

  function scheduleOrder(id) {
    const o = (data.orders.data ?? []).find(x => x.workOrderId === id);
    const plan = (data.plans.data ?? []).find(p => p.planId === o.planId);
    const start = o.scheduledStart ?? new Date(o.dueDate ? `${o.dueDate}T07:00:00` : Date.now() + 864e5).toISOString();
    const end = o.scheduledEnd ?? new Date(new Date(start).getTime() + (plan?.durationMinutes ?? 120) * 6e4).toISOString();
    return formModal({ title: `Schedule work order ${id} on ${o.machineCode}`, detail: 'The machine is unavailable to production in this window.', confirmLabel: 'Schedule',
      fields: [
        { name: 'startsAt', label: 'Starts', type: 'datetime-local', value: local(start), required: true },
        { name: 'endsAt', label: 'Ends', type: 'datetime-local', value: local(end), required: true },
      ] },
    (v, reason) => command(`/api/maintenance/work-orders/${id}/schedule`, { startsAt: new Date(v.startsAt).toISOString(), endsAt: new Date(v.endsAt).toISOString() }, reason), reload);
  }

  function complete(id) {
    const o = (data.orders.data ?? []).find(x => x.workOrderId === id);
    const fields = [
      ...o.tasks.map((t, i) => ({ name: `t${i}`, label: `Task ${i + 1}`, type: 'check', text: t })),
      { name: 'findings', label: 'What was done and found', type: 'textarea', required: true },
    ];
    return formModal({ title: `Complete work order ${id}`, detail: 'Confirm every checklist task. A different person signs the work off.', confirmLabel: 'Complete', fields },
      (v, reason) => command(`/api/maintenance/work-orders/${id}/complete`, { findings: v.findings, tasksDone: o.tasks.map((_, i) => v[`t${i}`]) }, reason), reload);
  }

  function editPlan(plan) {
    const rows = Array.from({ length: 8 }, (_, i) => i);
    const fields = [
      plan ? { name: 'machineCode', label: 'Machine', value: plan.machineCode, readonly: true } : { name: 'machineCode', label: 'Machine', type: 'select', options: machineOptions(), required: true },
      { name: 'title', label: 'PM title', value: plan?.title, required: true },
      { name: 'intervalDays', label: 'Every (days) — optional if run hours given', type: 'number', min: 1, value: plan ? plan.intervalDays : 30 },
      { name: 'intervalRunHours', label: 'Every (machine run hours) — optional', type: 'number', min: 1, step: 'any', value: plan?.intervalRunHours, hint: 'Counted from shop-floor labor on the machine. With both, whichever comes first.' },
      { name: 'durationMinutes', label: 'Takes (minutes)', type: 'number', min: 1, value: plan?.durationMinutes ?? 60, required: true },
      ...(plan ? [] : [{ name: 'firstDue', label: 'First due (calendar plans)', type: 'date', value: today() }]),
      { name: 'lockOutWhenOverdue', type: 'check', label: 'Safety-critical', text: 'Lock the machine out when this PM is overdue', value: plan?.lockOutWhenOverdue ?? false },
      { name: 'graceDays', label: 'Grace days before lock-out', type: 'number', min: 0, value: plan?.graceDays ?? 0 },
      { name: 'graceRunHours', label: 'Grace run hours before lock-out', type: 'number', min: 0, step: 'any', value: plan?.graceRunHours ?? 0 },
      ...rows.map(i => ({ name: `task${i}`, label: `Checklist task ${i + 1}${i ? ' (optional)' : ''}`, value: plan?.tasks[i], required: i === 0 })),
    ];
    return formModal({ title: plan ? `Edit PM plan ${plan.planId}` : 'New PM plan', detail: plan ? 'Work not yet started follows the new checklist.' : 'The first work order is raised now; each sign-off raises the next.', confirmLabel: 'Save plan', reasonRequired: !!plan, fields },
      (v, reason) => {
        if (!v.intervalDays && !v.intervalRunHours) throw new Error('Give an interval in days, in run hours, or both.');
        const body = { title: v.title, intervalDays: v.intervalDays || null, intervalRunHours: v.intervalRunHours || null, graceRunHours: v.graceRunHours ?? 0, durationMinutes: v.durationMinutes, lockOutWhenOverdue: v.lockOutWhenOverdue, graceDays: v.graceDays ?? 0, tasks: rows.map(i => v[`task${i}`]).filter(Boolean) };
        return plan ? command(`/api/maintenance/plans/${plan.planId}`, body, reason) : command('/api/maintenance/plans', { ...body, machineCode: v.machineCode, firstDue: v.intervalDays ? v.firstDue : null }, reason);
      }, reload);
  }

  function sparePart(id, giveBack) {
    const o = (data.orders.data ?? []).find(x => x.workOrderId === id);
    const used = (o.parts ?? []).filter(p => p.netIssued > 0);
    const available = data.stock.filter(b => b.status === 'Available' && b.quantity > 0);
    const parts = giveBack
      ? used.map(p => [p.partNumber, `${p.partNumber} — ${qty(p.netIssued)} issued`])
      : [...new Set(available.map(b => b.partNumber))].map(pn => [pn, `${pn} — ${data.items.find(i => i.partNumber === pn)?.description ?? ''}`]);
    const locations = [...new Set(data.stock.map(b => b.locationCode))].map(l => [l, l]);
    return formModal({ title: `${giveBack ? 'Return a part from' : 'Issue a part to'} work order ${id}`, detail: giveBack ? 'Unused parts go back into Available stock.' : 'From Available stock, recorded in the inventory ledger against this work order.', confirmLabel: giveBack ? 'Return' : 'Issue', reasonRequired: giveBack,
      fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: parts, required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', required: true },
        { name: 'locationCode', label: giveBack ? 'Into location' : 'From location', type: 'select', options: locations, required: true },
        { name: 'lotNumber', label: 'Lot (lot-tracked parts only)' },
      ] }, (v, reason) => command(`/api/maintenance/work-orders/${id}/parts${giveBack ? '/return' : ''}`, { ...v, workOrderId: id }, reason), reload);
  }

  async function retire(id) {
    const done = await confirmWithReason({ title: `Retire PM plan ${id}`, detail: 'Its open work order is cancelled; no more are raised.', confirmLabel: 'Retire' }, reason => command(`/api/maintenance/plans/${id}/retire`, {}, reason));
    if (done) { toast(done.message); await reload(); }
  }

  await load();
  render();
}
