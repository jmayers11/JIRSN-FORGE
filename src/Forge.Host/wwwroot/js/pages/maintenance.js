import { tryGet, command } from '../api.js';
import { who } from '../people.js';
import { attachmentsDrawer } from '../attachments.js';
import { esc, $, $$, fmtNumber, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast, drawer } from '../ui.js';
import { formModal, canAction, today, plusDays, qty } from '../forms.js';
import { isDownAt } from '../model.js';
import { serialDrawer } from '../genealogy.js';
import { historyDrawer } from '../history.js';

// Maintenance (spec §47): machine status, preventive-maintenance plans and work orders. Work occupies
// planned downtime, so capacity and the schedule see it. A technician does the work and confirms the
// checklist; a different person signs it off. A breakdown raises a corrective work order, and the
// machine returns to service when that is signed off. An overdue lock-out PM locks its machine out.

const WO_TONE = { Open: 'none', Scheduled: 'info', InProgress: 'warn', Completed: 'info', Closed: 'ok', Cancelled: 'bad' };
const WO_LABEL = { Open: 'Open', Scheduled: 'Scheduled', InProgress: 'In progress', Completed: 'Awaiting sign-off', Closed: 'Closed', Cancelled: 'Cancelled' };
const DOWN_LABEL = { Planned: 'Planned maintenance', Unplanned: 'Down', MaintenanceOverdue: 'Locked out — PM overdue', AssetOutOfService: 'Asset out of service' };
// Due by date and/or by run hours; overdue when either is past.
const isOpen = o => ['Open', 'Scheduled', 'InProgress'].includes(o.status);
const overdueNow = o => isOpen(o) && ((o.dueDate && o.dueDate < today()) || o.dueByRunHours);
const dueText = o => [o.dueDate ? `due ${fmtDate(o.dueDate)}` : null, o.dueRunHours != null ? `due at ${fmtNumber(o.dueRunHours)} run h${o.runHours != null ? ` (${fmtNumber(o.runHours)} so far)` : ''}` : null].filter(Boolean).join(' or ');
const ASSET_TONE = { Planned: 'none', Active: 'ok', Restricted: 'bad', Obsolete: 'warn', Decommissioned: 'none' };
const ASSET_TYPES = ['Production', 'Building', 'Utility', 'TestEquipment', 'Tooling', 'Vehicle', 'Other'];
const CRITICALITY = ['Low', 'Medium', 'High', 'Critical'];
const KINDS = ['Corrective', 'Emergency', 'Predictive', 'Calibration', 'Inspection', 'Facility'];
const OUTAGE_TONE = { Proposed: 'warn', Approved: 'ok', Rejected: 'bad', Withdrawn: 'none' };
const DIM_TONE = { Pass: 'ok', Fail: 'bad', Indeterminate: 'warn' };
const target = o => o.machineCode ?? o.assetCode ?? '—';
const local = iso => { const d = new Date(iso); d.setMinutes(d.getMinutes() - d.getTimezoneOffset()); return d.toISOString().slice(0, 16); };

export async function maintenancePage(page, ctx) {
  const me = ctx.me;
  const report = canAction(me, 'maintenance.downtime.report');
  const tech = canAction(me, 'maintenance.work');
  const spares = canAction(me, 'inventory.spares.issue');
  const planner = canAction(me, 'maintenance.pm.manage');
  const maintenanceApprover = canAction(me, 'maintenance.approve');
  const schedule = canAction(me, 'maintenance.plan');
  const assetsManage = canAction(me, 'maintenance.assets.manage');
  const outageApprover = canAction(me, 'maintenance.outage.approve');
  const requisitioner = canAction(me, 'procurement.requisition.raise');
  let tab = ctx.query.get('tab') ?? 'machines';
  let showClosed = false;
  let data = {};

  async function load() {
    const now = Date.now();
    const iso = ms => new Date(ms).toISOString();
    const [resources, stock, items, downtime, orders, plans, assets, readiness, outages] = await Promise.all([
      tryGet('/api/resources/'), tryGet('/api/inventory/stock'), tryGet('/api/engineering/items'),
      tryGet(`/api/maintenance/downtime?from=${encodeURIComponent(iso(now - 30 * 864e5))}&to=${encodeURIComponent(iso(now + 60 * 864e5))}`),
      tryGet(`/api/maintenance/work-orders?includeClosed=${showClosed}`), tryGet('/api/maintenance/plans'), tryGet('/api/maintenance/assets'),
      tryGet('/api/maintenance/readiness'), tryGet('/api/maintenance/outages?includeDecided=true'),
    ]);
    // Each entry wraps a work order with its run hours (run-hour PM) and the spare parts used on it.
    if (orders.data) orders.data = orders.data.map(v => ({ ...v.order, runHours: v.runHours, dueByRunHours: v.dueByRunHours, parts: v.parts }));
    data = { machines: resources.data?.machines ?? [], workCenters: resources.data?.workCenters ?? [], resources, downtime, orders, plans, assets, stock: stock.data ?? [], items: items.data ?? [],
      readiness: new Map((readiness.data ?? []).map(r => [r.workOrderId, r])), outages };
  }
  const reload = async () => { await load(); render(); };
  const mine = actor => actor === `person:${me.personId}`;
  const machineOptions = () => data.machines.map(m => [m.code, `${m.code} — ${m.name}`]);

  function render() {
    const orders = data.orders.data ?? [];
    const overdue = orders.filter(overdueNow);
    const signOff = orders.filter(o => o.status === 'Completed').length;
    const proposed = (data.outages.data ?? []).filter(o => o.status === 'Proposed');
    const tabs = [['machines', 'Machines'], ['orders', `Work orders${orders.length ? ` (${orders.filter(o => o.status !== 'Closed' && o.status !== 'Cancelled').length})` : ''}`], ['outages', `Outages${proposed.length ? ` (${proposed.length} proposed)` : ''}`], ['plans', 'PM plans'], ['assets', 'Assets'], ['log', 'Downtime log']];
    page.innerHTML = `
      <div class="page-head"><div><h1>Maintenance</h1><div class="sub">Machine status, preventive maintenance and work orders. Work that is not ready is shown as blocked, not planned; an outage takes capacity only once someone else approves it after seeing the schedule preview.</div></div>
        <div class="spacer"></div>
        ${report ? '<button class="btn" id="raise">Raise work order</button>' : ''}
        ${planner ? '<button class="btn primary" id="new-plan">New PM plan</button>' : ''}
        ${assetsManage && tab === 'assets' ? '<button class="btn" id="new-asset">Register asset</button>' : ''}</div>
      ${overdue.length ? `<div class="notice bad">${overdue.length} work order(s) are past due: ${overdue.map(o => `${o.workOrderId} (${esc(target(o))})`).join(', ')}.</div>` : ''}
      ${signOff && maintenanceApprover ? `<div class="notice">${signOff} work order(s) are waiting for your sign-off.</div>` : ''}
      ${proposed.length && outageApprover ? `<div class="notice">${proposed.length} proposed outage(s) are waiting for a decision. Open each to see what it does to the schedule.</div>` : ''}
      <section class="panel">
        <header><div class="seg">${tabs.map(([k, l]) => `<button data-tab="${k}" class="${tab === k ? 'on' : ''}">${esc(l)}</button>`).join('')}</div>
          ${tab === 'orders' ? `<span class="spacer"></span><label class="opt"><input type="checkbox" id="closed" ${showClosed ? 'checked' : ''}> Show closed</label>` : ''}</header>
        ${{ machines: machinesTab, orders: ordersTab, outages: outagesTab, plans: plansTab, assets: assetsTab, log: logTab }[tab]?.() ?? machinesTab()}
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
    $('#new-asset', page)?.addEventListener('click', () => registerAsset());
    $$('[data-asset]', page).forEach(b => b.addEventListener('click', () => assetDrawer(b.dataset.asset)));
    $$('[data-needs]', page).forEach(b => b.addEventListener('click', () => requirements(Number(b.dataset.needs))));
    $$('[data-ready]', page).forEach(b => b.addEventListener('click', () => readinessDrawer(Number(b.dataset.ready))));
    $$('[data-request-parts]', page).forEach(b => b.addEventListener('click', () => requestParts(Number(b.dataset.requestParts))));
    $$('[data-outage]', page).forEach(b => b.addEventListener('click', () => outageDrawer(Number(b.dataset.outage))));
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
            ${schedule ? `<button class="btn small ghost" data-window="${esc(m.code)}">Propose outage</button>` : ''}</td></tr>`;
      }).join('')}</tbody></table>`;
  }

  function ordersTab() {
    if (data.orders.error) return errorBlock(data.orders.error, 'work orders');
    const rows = data.orders.data;
    if (!rows.length) return emptyBlock('No work orders', 'PM plans raise them on schedule; a breakdown raises a corrective one.');
    return rows.map(o => {
      const late = overdueNow(o);
      const ready = data.readiness.get(o.workOrderId);
      const pending = (data.outages.data ?? []).find(x => x.status === 'Proposed' && x.workOrderId === o.workOrderId);
      return `<div class="card-row">
        <div class="grow">
          <div><b>WO ${o.workOrderId}</b> · ${esc(target(o))}${o.assetCode && o.machineCode ? ` <span class="faint">(asset ${esc(o.assetCode)})</span>` : ''} · ${esc(o.title)} <span class="pill ${WO_TONE[o.status]}">${esc(WO_LABEL[o.status])}</span> <span class="pill ${o.kind === 'Emergency' ? 'bad' : 'none'}">${esc(o.kind)}</span>
            ${ready ? `<button class="pill ${ready.status === 'Ready' ? 'ok' : 'bad'} linkish" data-ready="${o.workOrderId}" title="${esc(ready.summary)}">${ready.status === 'Ready' ? 'Ready' : `Blocked · ${esc(ready.blocking)}`}</button>` : ''}</div>
          ${pending ? `<div class="faint">Outage ${pending.outageId} proposed ${esc(fmtDateTime(pending.startsAt))} – ${esc(fmtDateTime(pending.endsAt))}; scheduled when someone else approves it.</div>` : ''}
          ${o.procedureDocument || o.requiredQualifications?.length ? `<div class="faint">${o.procedureDocument ? `Procedure ${esc(o.procedureDocument)} rev ${esc(o.procedureRevision)}` : ''}${o.procedureDocument && o.requiredQualifications?.length ? ' · ' : ''}${o.requiredQualifications?.length ? `Needs ${o.requiredQualifications.map(esc).join(', ')}` : ''}</div>` : ''}
          ${ready?.parts.length ? `<div class="faint">Parts needed: ${ready.parts.map(p => `<span class="${p.covered ? '' : 'c-bad'}">${qty(p.required)} ${esc(p.partNumber)}${p.alternate ? ` (via ${esc(p.alternate)})` : ''}</span>`).join(' · ')}</div>` : ''}
          <div class="faint ${late ? 'c-bad' : ''}">${esc(dueText(o).replace(/^d/, 'D'))}${late ? ' · overdue' : ''}${o.scheduledStart ? ` · window ${esc(fmtDateTime(o.scheduledStart))} – ${esc(fmtDateTime(o.scheduledEnd))}` : ''}</div>
          ${o.description ? `<div>${esc(o.description)}</div>` : ''}
          ${o.tasks.length ? `<div class="faint">Checklist: ${o.tasks.map(esc).join(' · ')}</div>` : ''}
          ${o.parts?.length ? `<div>Parts used: ${o.parts.filter(p => p.netIssued > 0).map(p => `${qty(p.netIssued)} ${esc(p.partNumber)}`).join(' · ') || '<span class="faint">all returned</span>'}</div>` : ''}
          <div class="faint">Raised by ${esc(who(o.createdBy))}${o.startedBy ? ` · started by ${esc(who(o.startedBy))} ${esc(fmtDateTime(o.startedAt))}` : ''}${o.completedBy ? ` · completed by ${esc(who(o.completedBy))}: ${esc(o.findings ?? '')}` : ''}${o.closedBy ? ` · signed off by ${esc(who(o.closedBy))} ${esc(fmtDateTime(o.closedAt))}` : ''}${o.cancelReason ? ` · cancelled: ${esc(o.cancelReason)}` : ''}</div>
        </div>
        <div class="nowrap">
          ${['Open', 'Scheduled'].includes(o.status) && schedule && !pending ? `<button class="btn small" data-schedule="${o.workOrderId}" ${ready?.status === 'Blocked' ? `disabled title="Not ready: ${esc(ready.blocking)}"` : ''}>${o.status === 'Scheduled' ? 'Reschedule' : 'Schedule'}</button>` : ''}
          ${['Open', 'Scheduled'].includes(o.status) && schedule ? `<button class="btn small ghost" data-needs="${o.workOrderId}">Requirements</button>` : ''}
          ${ready?.parts.some(p => !p.covered) && requisitioner ? `<button class="btn small" data-request-parts="${o.workOrderId}">Request parts</button>` : ''}
          ${['Open', 'Scheduled'].includes(o.status) && tech ? `<button class="btn primary small" data-start="${o.workOrderId}">Start</button>` : ''}
          ${o.status === 'InProgress' && tech ? `<button class="btn primary small" data-complete="${o.workOrderId}">Complete</button>` : ''}
          ${o.status === 'Completed' && maintenanceApprover && !mine(o.completedBy) && !mine(o.startedBy) ? `<button class="btn primary small" data-signoff="${o.workOrderId}">Sign off</button> <button class="btn small" data-sendback="${o.workOrderId}">Send back</button>` : ''}
          ${o.status === 'Completed' && (mine(o.completedBy) || mine(o.startedBy)) ? '<span class="faint">You did the work — someone else signs it off</span>' : ''}
          ${isOpen(o) && spares ? `<button class="btn small" data-issue="${o.workOrderId}">Issue part</button>` : ''}
          ${o.parts?.some(p => p.netIssued > 0) && spares ? `<button class="btn small ghost" data-return="${o.workOrderId}">Return part</button>` : ''}
          ${['Open', 'Scheduled'].includes(o.status) && schedule ? `<button class="btn small ghost" data-cancel="${o.workOrderId}">Cancel</button>` : ''}
          <button class="btn small ghost" data-docs-wo="${o.workOrderId}">Documents</button>
        </div></div>`;
    }).join('');
  }

  function outagesTab() {
    if (data.outages.error) return errorBlock(data.outages.error, 'outages');
    const rows = data.outages.data;
    if (!rows.length) return emptyBlock('No outages', 'Plan a window on a machine, or schedule a work order: it is proposed here, and a different person approves it after the schedule preview.');
    return `<table class="table"><thead><tr><th>Outage</th><th>Machine</th><th>Window</th><th>For</th><th>Status</th><th>Proposed</th><th>Decision</th><th></th></tr></thead><tbody>
      ${rows.map(x => `<tr><td><b>${x.outageId}</b></td><td>${esc(x.machineCode)}</td><td>${esc(fmtDateTime(x.startsAt))} – ${esc(fmtDateTime(x.endsAt))}</td>
        <td>${x.workOrderId ? `WO ${x.workOrderId}` : ''} <span class="faint">${esc(x.reason)}</span></td>
        <td><span class="pill ${OUTAGE_TONE[x.status]}">${esc(x.status)}</span></td><td class="faint">${esc(who(x.proposedBy))} ${esc(fmtDateTime(x.proposedAt))}</td>
        <td class="faint">${x.decidedBy ? `${esc(who(x.decidedBy))}: ${esc(x.decisionReason ?? '')}${x.impact ? `<div>${esc(x.impact)}</div>` : ''}` : ''}</td>
        <td><button class="btn small" data-outage="${x.outageId}">${x.status === 'Proposed' ? 'Preview' : 'Open'}</button></td></tr>`).join('')}</tbody></table>`;
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
    return formModal({ title: `Propose an outage on ${machine}`, detail: 'It takes no capacity until a different person approves it after seeing the schedule preview.', confirmLabel: 'Propose',
      fields: [
        { name: 'startsAt', label: 'Starts', type: 'datetime-local', required: true },
        { name: 'endsAt', label: 'Ends', type: 'datetime-local', required: true },
        { name: 'description', label: 'Work to be done', required: true },
      ] },
    (v, reason) => command(`/api/maintenance/machines/${encodeURIComponent(machine)}/planned`, { ...v, startsAt: new Date(v.startsAt).toISOString(), endsAt: new Date(v.endsAt).toISOString() }, reason), reload);
  }

  function raise() {
    return formModal({ title: 'Raise a work order', detail: 'If a machine has stopped, report it down instead: that raises the repair and takes it out of production.', confirmLabel: 'Raise',
      fields: [
        { name: 'kind', label: 'Type of work', type: 'select', options: KINDS.map(k => [k, k]), value: 'Corrective', required: true },
        { name: 'machineCode', label: 'Machine', type: 'select', blank: '— not a machine —', options: machineOptions() },
        { name: 'assetCode', label: 'Asset (buildings, utilities, test equipment — or the machine\'s asset)', type: 'select', blank: '— from the machine —', options: (data.assets.data ?? []).filter(a => a.status !== 'Decommissioned').map(a => [a.assetCode, `${a.assetCode} — ${a.name}`]) },
        { name: 'title', label: 'What needs doing', required: true },
        { name: 'description', label: 'Details', type: 'textarea' },
        { name: 'dueDate', label: 'Due', type: 'date', value: plusDays(7), required: true },
      ] }, (v, reason) => {
        if (!v.machineCode && !v.assetCode) throw new Error('Choose the machine or the asset the work is on.');
        return command('/api/maintenance/work-orders', { ...v, machineCode: v.machineCode || null, assetCode: v.assetCode || null }, reason);
      }, reload);
  }

  function scheduleOrder(id) {
    const o = (data.orders.data ?? []).find(x => x.workOrderId === id);
    const plan = (data.plans.data ?? []).find(p => p.planId === o.planId);
    const start = o.scheduledStart ?? new Date(o.dueDate ? `${o.dueDate}T07:00:00` : Date.now() + 864e5).toISOString();
    const end = o.scheduledEnd ?? new Date(new Date(start).getTime() + (plan?.durationMinutes ?? 120) * 6e4).toISOString();
    return formModal({ title: `Schedule work order ${id} on ${target(o)}`, detail: o.machineCode ? 'Proposes an outage for this window; the work is scheduled when someone else approves it after the schedule preview. A repair of a machine that is already down is scheduled at once.' : 'Work on an asset that is not a machine takes no production capacity.', confirmLabel: 'Schedule',
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
    const available = data.stock.filter(b => b.status === 'Released' && b.quantity > 0);
    const parts = giveBack
      ? used.map(p => [p.partNumber, `${p.partNumber} — ${qty(p.netIssued)} issued`])
      : [...new Set(available.map(b => b.partNumber))].map(pn => [pn, `${pn} — ${data.items.find(i => i.partNumber === pn)?.description ?? ''}`]);
    const locations = [...new Set(data.stock.map(b => b.locationCode))].map(l => [l, l]);
    return formModal({ title: `${giveBack ? 'Return a part from' : 'Issue a part to'} work order ${id}`, detail: giveBack ? 'Unused parts go back into released stock.' : 'From released stock, recorded in the inventory ledger against this work order.', confirmLabel: giveBack ? 'Return' : 'Issue', reasonRequired: giveBack,
      fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: parts, required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', required: true },
        { name: 'locationCode', label: giveBack ? 'Into location' : 'From location', type: 'select', options: locations, required: true },
        { name: 'lotNumber', label: 'Lot (lot-tracked parts only)' },
        { name: 'alternateFor', label: 'In place of (approved alternate only)', type: 'select', blank: '— the part itself —', options: (data.readiness.get(id)?.parts ?? []).map(p => [p.partNumber, p.alternate ? `${p.partNumber} (readiness suggests ${p.alternate})` : p.partNumber]) },
      ] }, (v, reason) => command(`/api/maintenance/work-orders/${id}/parts${giveBack ? '/return' : ''}`, { ...v, workOrderId: id, alternateFor: v.alternateFor || null }, reason), reload);
  }

  // ------------------------------------------------------------ readiness and outages (V2.9)

  function readinessDrawer(id) {
    const r = data.readiness.get(id);
    if (!r) return;
    drawer(`Work order ${id} — ${r.status === 'Ready' ? 'ready' : 'blocked'}`, `
      <p>${esc(r.summary)}</p>
      <table class="table"><thead><tr><th>Dimension</th><th></th><th>Why</th></tr></thead><tbody>
        ${r.dimensions.map(d => `<tr><td><b>${esc(d.name)}</b></td><td><span class="pill ${DIM_TONE[d.outcome] ?? 'none'}">${d.outcome === 'Pass' ? 'Pass' : 'Fail'}</span></td><td>${esc(d.detail)}</td></tr>`).join('')}</tbody></table>
      ${r.parts.length ? `<h3>Parts</h3><table class="table"><thead><tr><th>Part</th><th>Needed</th><th>Issued</th><th>On hand</th><th></th></tr></thead><tbody>
        ${r.parts.map(p => `<tr><td><b>${esc(p.partNumber)}</b></td><td>${qty(p.required)}</td><td>${qty(p.issued)}</td><td>${qty(p.onHand)}</td><td class="${p.covered ? '' : 'c-bad'}">${esc(p.detail)}</td></tr>`).join('')}</tbody></table>` : ''}
      <p class="faint">Stock on hand is not reserved; other work may use it first. Each dimension is checked on its own and none is assumed.</p>`);
  }

  async function requirements(id) {
    const o = (data.orders.data ?? []).find(x => x.workOrderId === id);
    const current = await tryGet(`/api/maintenance/work-orders/${id}/requirements`);
    const parts = current.data ?? [];
    const rows = Array.from({ length: Math.max(6, parts.length + 2) }, (_, i) => i);
    const assets = (data.assets.data ?? []).filter(a => a.status !== 'Decommissioned' && (!o.machineCode || a.machineCode === o.machineCode));
    const itemOptions = data.items.map(i => [i.partNumber, `${i.partNumber} — ${i.description}`]);
    return formModal({ title: `What work order ${id} needs`, detail: 'Readiness checks each of these on its own. Parts with a position are recorded as installed on the asset when they are issued.', confirmLabel: 'Save', fields: [
      { name: 'assetCode', label: 'Asset', type: 'select', blank: '— none —', options: assets.map(a => [a.assetCode, `${a.assetCode} — ${a.name}`]), value: o.assetCode },
      { name: 'procedureDocument', label: 'Procedure document number', value: o.procedureDocument, hint: 'A controlled procedure or work instruction.' },
      { name: 'procedureRevision', label: 'Procedure revision (blank = the one in force)', value: o.procedureRevision },
      { name: 'qualifications', label: 'Qualifications required (comma separated codes)', value: (o.requiredQualifications ?? []).join(', ') },
      ...rows.flatMap(i => [
        { name: `p${i}`, label: `Part ${i + 1}`, type: 'select', blank: '—', options: itemOptions, value: parts[i]?.partNumber },
        { name: `q${i}`, label: `Quantity ${i + 1}`, type: 'number', min: 0, step: 'any', value: parts[i]?.quantity },
        { name: `x${i}`, label: `Position ${i + 1} (optional)`, value: parts[i]?.position },
      ]),
    ] }, (v, reason) => command(`/api/maintenance/work-orders/${id}/requirements`, {
      assetCode: v.assetCode || null, procedureDocument: v.procedureDocument || null, procedureRevision: v.procedureRevision || null,
      qualifications: (v.qualifications ?? '').split(',').map(x => x.trim()).filter(Boolean),
      parts: rows.filter(i => v[`p${i}`]).map(i => ({ partNumber: v[`p${i}`], quantity: Number(v[`q${i}`] || 0), position: v[`x${i}`] || null })),
    }, reason), reload);
  }

  async function requestParts(id) {
    const r = data.readiness.get(id);
    const short = (r?.parts ?? []).filter(p => !p.covered);
    const done = await confirmWithReason({ title: `Request parts for work order ${id}`, detail: `Raises a maintenance requisition for each part that is short (${short.map(p => `${qty(p.stillNeeded - p.onHand)} ${p.partNumber}`).join(', ')}), sourced to this work order, for purchasing to order.`, confirmLabel: 'Request', reasonRequired: false },
      reason => command(`/api/maintenance/work-orders/${id}/request-parts`, {}, reason));
    if (done) { toast(done.lines.map(l => `${l.partNumber}: ${l.outcome}${l.requisitionId ? ` (requisition ${l.requisitionId})` : ''}`).join(' · ')); await reload(); }
  }

  async function outageDrawer(id) {
    const r = await tryGet(`/api/maintenance/outages/${id}`);
    if (r.error) { toast(r.error.message ?? 'Could not load the outage.', true); return; }
    const { outage: x, preview: p } = r.data;
    const mineOutage = x.proposedBy === `person:${me.personId}`;
    const EFFECT = { Moves: 'info', Delayed: 'warn', CannotMove: 'bad', Unchanged: 'none' };
    const d = drawer(`Outage ${id} on ${x.machineCode}`, `
      <p><span class="pill ${OUTAGE_TONE[x.status]}">${esc(x.status)}</span> ${esc(fmtDateTime(x.startsAt))} – ${esc(fmtDateTime(x.endsAt))} · ${esc(x.reason)}</p>
      <p class="faint">Proposed by ${esc(who(x.proposedBy))} ${esc(fmtDateTime(x.proposedAt))}${x.decidedBy ? ` · ${esc(x.status.toLowerCase())} by ${esc(who(x.decidedBy))} ${esc(fmtDateTime(x.decidedAt))}: ${esc(x.decisionReason ?? '')}` : ''}</p>
      ${x.impact ? `<div class="notice">Seen at approval: ${esc(x.impact)}</div>` : ''}
      ${p ? `<h3>Schedule preview — with and without this outage</h3><div class="notice ${p.ordersLate ? 'bad' : p.affected ? 'warn' : ''}">${esc(p.summary)}</div>
        ${p.operations.length ? `<table class="table"><thead><tr><th>Order / op</th><th>Planned</th><th>With the outage</th><th></th></tr></thead><tbody>
          ${p.operations.map(o => `<tr><td>${o.orderNumber} / ${o.sequence} <span class="faint">${esc(o.description)}</span></td><td>${esc(o.machineBefore ?? '')} ${esc(fmtDateTime(o.startBefore))}</td>
            <td>${o.startAfter ? `${esc(o.machineAfter ?? '')} ${esc(fmtDateTime(o.startAfter))}` : '<span class="c-bad">not scheduled</span>'}</td><td><span class="pill ${EFFECT[o.effect]}">${esc(o.effect === 'CannotMove' ? 'Cannot move' : o.effect)}</span></td></tr>`).join('')}</tbody></table>` : ''}
        ${p.orders.length ? `<h3>Orders</h3><ul>${p.orders.map(o => `<li>Order ${o.orderNumber} (${esc(o.partNumber)}, due ${esc(fmtDate(o.dueDate))}): ${esc(o.statusBefore)} → <b class="${o.becomesLate ? 'c-bad' : ''}">${esc(o.statusAfter)}</b>${o.completionAfter ? `, finishes ${esc(fmtDateTime(o.completionAfter))}` : ''}</li>`).join('')}</ul>` : ''}` : ''}
      <div class="row-actions">
        ${x.status === 'Proposed' && outageApprover && !mineOutage ? '<button class="btn primary small" data-o="approve">Approve</button> <button class="btn small" data-o="reject">Reject</button>' : ''}
        ${x.status === 'Proposed' && mineOutage && schedule ? '<span class="faint">You proposed this — someone else decides it.</span> <button class="btn small ghost" data-o="withdraw">Withdraw</button>' : ''}
      </div>`);
    const act = async (verb, title, detail) => {
      const done = await confirmWithReason({ title: `${title} outage ${id}`, detail, confirmLabel: title }, reason => command(`/api/maintenance/outages/${id}/${verb}`, {}, reason));
      if (done) { toast(done.message); d.close(); await reload(); }
    };
    $$('[data-o]', d.element).forEach(b => b.addEventListener('click', () => ({
      approve: () => act('approve', 'Approve', 'The preview is run again at approval and recorded with your decision. The machine leaves capacity in this window.'),
      reject: () => act('reject', 'Reject', 'The machine keeps its capacity.'),
      withdraw: () => act('withdraw', 'Withdraw', 'The proposal is withdrawn.'),
    })[b.dataset.o]()));
  }

  // ------------------------------------------------------------ assets (V2.8)

  function assetsTab() {
    if (data.assets.error) return errorBlock(data.assets.error, 'assets');
    const rows = data.assets.data;
    if (!rows.length) return emptyBlock('No assets yet', assetsManage ? 'Register machines, buildings, utilities and test equipment as assets: lifecycle, components and obsolescence.' : 'A maintenance planner registers them.');
    return `<table class="table"><thead><tr><th>Asset</th><th>Type</th><th>Machine / work center</th><th>Criticality</th><th>Status</th><th>Commissioned</th><th></th></tr></thead><tbody>
      ${rows.map(a => `<tr><td><b>${esc(a.assetCode)}</b> <span class="muted">${esc(a.name)}</span>${a.manufacturer ? `<div class="faint">${esc([a.manufacturer, a.model, a.serialNumber].filter(Boolean).join(' · '))}</div>` : ''}</td>
        <td>${esc(a.type)}</td><td>${esc([a.machineCode, a.workCenterCode].filter(Boolean).join(' / ') || a.location || '—')}</td>
        <td><span class="pill ${a.criticality === 'Critical' ? 'bad' : a.criticality === 'High' ? 'warn' : 'none'}">${esc(a.criticality)}</span></td>
        <td><span class="pill ${ASSET_TONE[a.status]}">${esc(a.status)}</span>${a.decommissionRequestedBy ? ' <span class="pill warn">Decommission requested</span>' : ''}</td>
        <td>${a.commissionedOn ? esc(fmtDate(a.commissionedOn)) : '<span class="faint">—</span>'}</td>
        <td><button class="btn small" data-asset="${esc(a.assetCode)}">Open</button></td></tr>`).join('')}</tbody></table>`;
  }

  async function assetDrawer(code) {
    const r = await tryGet(`/api/maintenance/assets/${encodeURIComponent(code)}`);
    if (r.error) { toast(r.error.message ?? 'Could not load the asset.', true); return; }
    const { asset: a, history, components, obsolescence } = r.data;
    const installed = components.filter(c => !c.removedAt);
    const removed = components.filter(c => c.removedAt);
    const flagged = obsolescence.filter(o => o.replacement !== 'Approved');
    const open = (data.orders.data ?? []).filter(o => isOpen(o) && (o.assetCode === a.assetCode || (a.machineCode && o.machineCode === a.machineCode)));
    const live = a.status !== 'Decommissioned';
    const mineRequest = a.decommissionRequestedBy === `person:${me.personId}`;
    const actions = live ? [
      assetsManage ? '<button class="btn small" data-a="edit">Edit</button>' : '',
      assetsManage && a.status === 'Planned' ? '<button class="btn primary small" data-a="commission">Commission</button>' : '',
      assetsManage && ['Active', 'Obsolete'].includes(a.status) ? '<button class="btn small" data-a="restrict">Restrict</button>' : '',
      assetsManage && ['Restricted', 'Obsolete'].includes(a.status) ? '<button class="btn small" data-a="active">Return to service</button>' : '',
      assetsManage && ['Active', 'Restricted'].includes(a.status) ? '<button class="btn small ghost" data-a="obsolete">Mark obsolete</button>' : '',
      assetsManage && !a.decommissionRequestedBy ? '<button class="btn small ghost" data-a="decommission">Request decommissioning</button>' : '',
      a.decommissionRequestedBy && maintenanceApprover && !mineRequest ? '<button class="btn small" data-a="approve-dec">Approve decommissioning</button> <button class="btn small ghost" data-a="reject-dec">Reject</button>' : '',
      (a.status === 'Planned' ? assetsManage : (tech && open.length)) ? '<button class="btn small" data-a="install">Install component</button>' : '',
    ].filter(Boolean).join(' ') : '';
    const d = drawer(`Asset ${a.assetCode}`, `
      <p><b>${esc(a.name)}</b> <span class="pill ${ASSET_TONE[a.status]}">${esc(a.status)}</span> <span class="pill none">${esc(a.type)}</span> <span class="pill ${a.criticality === 'Critical' ? 'bad' : 'none'}">${esc(a.criticality)}</span></p>
      <dl class="kv">
        <dt>Machine</dt><dd>${esc(a.machineCode ?? '—')}${a.machineCode && a.status === 'Restricted' ? ' <span class="c-bad">out of capacity</span>' : ''}</dd>
        <dt>Work center</dt><dd>${esc(a.workCenterCode ?? '—')}</dd><dt>Location</dt><dd>${esc(a.location ?? '—')}</dd>
        <dt>Make / model / serial</dt><dd>${esc([a.manufacturer, a.model, a.serialNumber].filter(Boolean).join(' · ') || '—')}</dd>
        <dt>Commissioned</dt><dd>${a.commissionedOn ? esc(fmtDate(a.commissionedOn)) : '—'}${a.expectedLifeYears ? ` · expected life ${a.expectedLifeYears} years` : ''}</dd>
        <dt>Replacement cost</dt><dd>${a.replacementCost != null ? fmtNumber(a.replacementCost, 2) : '—'}</dd>
      </dl>
      ${a.decommissionRequestedBy ? `<div class="notice warn">Decommissioning requested by ${esc(who(a.decommissionRequestedBy))}: ${esc(a.decommissionReason ?? '')}. A different person approves it.</div>` : ''}
      ${flagged.length ? `<div class="notice bad">Obsolescence: ${flagged.map(o => `${esc(o.partNumber)} ${esc(o.status)}${o.endOfSupport ? `, end of support ${esc(fmtDate(o.endOfSupport))}` : ''} — replacement ${o.replacementPart && o.replacement === 'Proposed' ? `${esc(o.replacementPart)} proposed` : 'none approved'}`).join('; ')}.</div>` : ''}
      <div class="row-actions">${actions} <button class="btn small ghost" data-genealogy>Genealogy</button> <button class="btn small ghost" data-history>What changed?</button></div>
      <h3>Installed components</h3>
      ${installed.length ? `<table class="table"><thead><tr><th>Part</th><th>Position</th><th>Qty</th><th>Installed</th><th></th></tr></thead><tbody>${installed.map(c => `<tr><td><b>${esc(c.partNumber)}</b>${c.revision ? ` rev ${esc(c.revision)}` : ''}</td><td>${esc(c.position)}</td><td>${qty(c.quantity)}</td>
        <td class="faint">${c.installedByWorkOrder ? `WO ${c.installedByWorkOrder}` : 'as delivered'} · ${esc(who(c.installedBy))} ${esc(fmtDate(c.installedAt))}</td>
        <td>${live && ((a.status === 'Planned' && assetsManage) || (tech && open.length)) ? `<button class="btn small ghost" data-remove="${c.componentId}">Remove</button>` : ''}</td></tr>`).join('')}</tbody></table>` : '<p class="faint">None recorded.</p>'}
      ${removed.length ? `<h3>Removed</h3><ul class="faint">${removed.map(c => `<li>${qty(c.quantity)} ${esc(c.partNumber)} at ${esc(c.position)}: installed ${esc(fmtDate(c.installedAt))}, removed ${esc(fmtDate(c.removedAt))}${c.removedByWorkOrder ? ` by WO ${c.removedByWorkOrder}` : ''} — ${esc(c.removalReason)}</li>`).join('')}</ul>` : ''}
      <h3>History</h3>
      <ul class="timeline">${[...history].reverse().map(e => `<li><span class="faint">${esc(fmtDateTime(e.at))}</span> <b>${esc(e.kind)}</b> ${esc(e.detail)}${e.reason ? ` — <i>${esc(e.reason)}</i>` : ''} <span class="faint">${esc(who(e.actor))}</span></li>`).join('')}</ul>`);
    const after = async () => { d.close(); await reload(); await assetDrawer(code); };
    const status = (target, title, detail, extra = []) => formModal({ title, detail, confirmLabel: title, reasonRequired: true, fields: extra },
      (v, reason) => command(`/api/maintenance/assets/${encodeURIComponent(code)}/status`, { status: target, commissionedOn: v.commissionedOn ?? null }, reason), after);
    const woOptions = open.map(o => [String(o.workOrderId), `WO ${o.workOrderId} — ${o.title}`]);
    const handlers = {
      edit: () => formModal({ title: `Edit asset ${code}`, confirmLabel: 'Save', reasonRequired: true, fields: [
        { name: 'name', label: 'Name', value: a.name, required: true }, { name: 'manufacturer', label: 'Manufacturer', value: a.manufacturer }, { name: 'model', label: 'Model', value: a.model },
        { name: 'serialNumber', label: 'Serial number', value: a.serialNumber }, { name: 'location', label: 'Location', value: a.location },
        { name: 'criticality', label: 'Criticality', type: 'select', options: CRITICALITY.map(c => [c, c]), value: a.criticality },
        { name: 'expectedLifeYears', label: 'Expected life (years)', type: 'number', min: 1, value: a.expectedLifeYears }, { name: 'replacementCost', label: 'Replacement cost', type: 'number', min: 0, step: 'any', value: a.replacementCost },
      ] }, (v, reason) => command(`/api/maintenance/assets/${encodeURIComponent(code)}/change`, v, reason), after),
      commission: () => status('Active', 'Commission', 'The asset goes into service. From now on components are installed and removed by work order.', [{ name: 'commissionedOn', label: 'Commissioned on', type: 'date', value: today() }]),
      restrict: () => status('Restricted', 'Restrict', a.machineCode ? `Machine ${a.machineCode} leaves capacity until the asset is returned to service.` : 'Record why it may not be used.'),
      active: () => status('Active', 'Return to service', a.machineCode ? `Machine ${a.machineCode} returns to capacity.` : ''),
      obsolete: () => status('Obsolete', 'Mark obsolete', 'It still runs, but is no longer supported. Capacity is not reduced.'),
      decommission: async () => { const done = await confirmWithReason({ title: `Request decommissioning of ${code}`, detail: 'A different person with maintenance approval decides it. Open work orders must be finished or cancelled first.', confirmLabel: 'Request' }, reason => command(`/api/maintenance/assets/${encodeURIComponent(code)}/decommission`, {}, reason)); if (done) { toast(done.message); await after(); } },
      'approve-dec': async () => { const done = await confirmWithReason({ title: `Decommission ${code}`, detail: 'The asset leaves service for good; its history stays.', confirmLabel: 'Decommission' }, reason => command(`/api/maintenance/assets/${encodeURIComponent(code)}/decommission/decide`, { approve: true }, reason)); if (done) { toast(done.message); await after(); } },
      'reject-dec': async () => { const done = await confirmWithReason({ title: `Reject decommissioning of ${code}`, confirmLabel: 'Reject' }, reason => command(`/api/maintenance/assets/${encodeURIComponent(code)}/decommission/decide`, { approve: false }, reason)); if (done) { toast(done.message); await after(); } },
      install: () => formModal({ title: `Install a component in ${code}`, detail: a.status === 'Planned' ? 'Recorded as delivered (the asset is not yet commissioned).' : 'Installed under an open work order on this asset.', confirmLabel: 'Install', fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: data.items.map(i => [i.partNumber, `${i.partNumber} — ${i.description}`]), required: true },
        { name: 'revision', label: 'Revision (if known)' }, { name: 'position', label: 'Position', required: true, placeholder: 'e.g. Control cabinet slot 2' },
        { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', value: 1, required: true },
        ...(a.status === 'Planned' ? [] : [{ name: 'workOrderId', label: 'Work order', type: 'select', options: woOptions, required: true }]),
      ] }, (v, reason) => command(`/api/maintenance/assets/${encodeURIComponent(code)}/components`, { ...v, workOrderId: v.workOrderId ? Number(v.workOrderId) : null }, reason), after),
    };
    $$('[data-a]', d.element).forEach(b => b.addEventListener('click', () => handlers[b.dataset.a]()));
    $('[data-genealogy]', d.element)?.addEventListener('click', () => assetGenealogy(code));
    $('[data-history]', d.element)?.addEventListener('click', () => historyDrawer(code, 'asset'));
    $$('[data-remove]', d.element).forEach(b => b.addEventListener('click', () => formModal({ title: `Remove component ${b.dataset.remove}`, detail: 'The installation stays in the history; removal is recorded with its work order and reason.', confirmLabel: 'Remove', reasonRequired: true,
      fields: a.status === 'Planned' ? [] : [{ name: 'workOrderId', label: 'Work order', type: 'select', options: woOptions, required: true }] },
    (v, reason) => command(`/api/maintenance/components/${b.dataset.remove}/remove`, { workOrderId: v.workOrderId ? Number(v.workOrderId) : null }, reason), after)));
  }

  // V2.11: where each component came from, from the issue it was installed from (lot or serial, supplier, PO).
  async function assetGenealogy(code) {
    const r = await tryGet(`/api/maintenance/assets/${encodeURIComponent(code)}/genealogy`);
    const g = drawer(`Genealogy · ${code}`, '');
    if (r.error) { g.element.innerHTML = errorBlock(r.error, 'this asset'); return; }
    g.element.innerHTML = !r.data.components.length ? emptyBlock('No components recorded', 'Components installed on the asset appear here with where they came from.') : `
      <table class="table"><thead><tr><th>Part</th><th>Position</th><th>Rev</th><th>Lot / serial</th><th>Supplier / PO</th><th>Installed</th><th>Removed</th></tr></thead><tbody>
      ${r.data.components.map(x => { const c = x.component; return `<tr class="${c.removedAt ? 'retired' : ''}"><td class="mono">${esc(c.partNumber)}</td><td>${esc(c.position)}</td><td>${esc(x.revision ?? '—')}</td>
        <td>${x.lotNumber ? `<a href="#" data-serial="${esc(x.lotNumber)}" data-part="${esc(c.partNumber)}">${esc(x.lotNumber)}</a>` : '—'}</td>
        <td>${x.supplier ? `${esc(x.supplier)} · PO ${x.poNumber}/${x.poLine}` : x.madeOnOrder ? `made on <a href="#/orders/${x.madeOnOrder}">#${x.madeOnOrder}</a>` : '—'}</td>
        <td class="faint">${c.installedByWorkOrder ? `WO ${c.installedByWorkOrder}` : 'as delivered'} ${esc(fmtDate(c.installedAt))}</td><td class="faint">${c.removedAt ? esc(fmtDate(c.removedAt)) : ''}</td></tr>
        <tr><td colspan="7" class="faint">${esc(x.source)}</td></tr>`; }).join('')}</tbody></table>`;
    $$('[data-serial]', g.element).forEach(a => a.addEventListener('click', ev => { ev.preventDefault(); serialDrawer(a.dataset.serial, a.dataset.part); }));
  }

  function registerAsset() {
    return formModal({ title: 'Register an asset', detail: 'It starts as Planned: record what it was delivered with, then commission it. A production asset points at its machine; capacity stays with the machine.', confirmLabel: 'Register', fields: [
      { name: 'assetCode', label: 'Asset code', code: true, required: true }, { name: 'name', label: 'Name', required: true },
      { name: 'type', label: 'Type', type: 'select', options: ASSET_TYPES.map(t => [t, t]), required: true },
      { name: 'machineCode', label: 'Machine (production assets)', type: 'select', blank: '— none —', options: machineOptions() },
      { name: 'workCenterCode', label: 'Work center', type: 'select', blank: '— from the machine, or none —', options: data.workCenters.map(w => [w.code, `${w.code} — ${w.name}`]) },
      { name: 'location', label: 'Location' }, { name: 'manufacturer', label: 'Manufacturer' }, { name: 'model', label: 'Model' }, { name: 'serialNumber', label: 'Serial number' },
      { name: 'criticality', label: 'Criticality', type: 'select', options: CRITICALITY.map(c => [c, c]), value: 'Medium', required: true },
      { name: 'expectedLifeYears', label: 'Expected life (years)', type: 'number', min: 1 }, { name: 'replacementCost', label: 'Replacement cost', type: 'number', min: 0, step: 'any' },
    ] }, (v, reason) => command('/api/maintenance/assets', v, reason), reload);
  }

  async function retire(id) {
    const done = await confirmWithReason({ title: `Retire PM plan ${id}`, detail: 'Its open work order is cancelled; no more are raised.', confirmLabel: 'Retire' }, reason => command(`/api/maintenance/plans/${id}/retire`, {}, reason));
    if (done) { toast(done.message); await reload(); }
  }

  await load();
  render();
}
