import { tryGet, command } from '../api.js';
import { who } from '../people.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, toast, confirmWithReason } from '../ui.js';
import { formModal, qty, can, today, plusDays } from '../forms.js';
import { historyDrawer } from '../history.js';

// Facility and capital projects (V2.10): approved by a second person; after approval every change to the
// budget, dates, financial reference or material is requested with a reason and approved by someone else.
// Material lines are checked against issues, stock, purchases and requisitions; shortages become project
// demand and requisitions sourced to the line, so "why was this purchased?" answers with the project line.

const STATUS_TONE = { Draft: 'warn', Approved: 'ok', Completed: 'none', Cancelled: 'none' };
const LINE_TONE = { Issued: 'ok', 'In stock': 'ok', 'On order': 'info', Requested: 'info', Short: 'bad', Cancelled: 'none' };
const CHANGE_TONE = { Pending: 'warn', Approved: 'ok', Rejected: 'bad' };
const OUTAGE_TONE = { Proposed: 'warn', Approved: 'ok', Rejected: 'bad', Withdrawn: 'none' };
const money = n => n == null ? '—' : Number(n).toLocaleString(undefined, { style: 'currency', currency: 'USD', maximumFractionDigits: 0 });
const MANAGERS = ['FacilitiesManager', 'MaintenancePlanner', 'ManufacturingEngineer'];

export async function projectsPage(page, ctx) {
  const code = ctx.params[0];
  return code ? projectDetail(page, ctx, decodeURIComponent(code)) : projectList(page, ctx);
}

async function projectList(page, ctx) {
  const manage = can(ctx.me, ...MANAGERS);
  const list = await tryGet('/api/projects/');
  page.innerHTML = `
    <div class="page-head"><div><h1>Projects</h1><div class="sub">Facility and capital projects: approval and budget, material bought and issued against each line, and the outages they need.</div></div>
      <div class="spacer"></div>${manage ? '<button class="btn primary" id="new">New project</button>' : ''}</div>
    ${list.error ? errorBlock(list.error, 'projects') : !list.data.length
      ? emptyBlock('No projects yet', manage ? 'Create a project as a draft, add its material lines, and ask a facilities manager to approve it.' : 'Facility and capital projects appear here once they are created.')
      : `<section class="panel"><table class="table"><thead><tr><th>Project</th><th>Title</th><th>Type</th><th>Funding</th><th>Status</th><th class="num">Budget</th><th>Dates</th><th class="num">Lines</th><th>Waiting</th></tr></thead><tbody>
        ${list.data.map(s => { const p = s.project; return `<tr><td><a href="#/projects/${encodeURIComponent(p.code)}" class="mono">${esc(p.code)}</a></td><td>${esc(p.title)}</td><td>${esc(p.type)}</td>
          <td>${esc(p.funding)}${p.financialReference ? ` <span class="faint mono">${esc(p.financialReference)}</span>` : ''}</td><td><span class="pill ${STATUS_TONE[p.status] ?? 'none'}">${esc(p.status)}</span></td>
          <td class="num">${money(p.budget)}</td><td class="nowrap">${esc(fmtDate(p.startDate))} – ${esc(fmtDate(p.endDate))}</td><td class="num">${s.lines}</td>
          <td>${s.pendingChanges ? `<span class="pill warn">${s.pendingChanges} change(s)</span>` : ''} ${s.openTasks ? `<span class="faint">${s.openTasks} open task(s)</span>` : ''}</td></tr>`; }).join('')}
      </tbody></table></section>`}`;
  $('#new', page)?.addEventListener('click', () => formModal({
    title: 'New project', detail: 'Created as a draft. A different person with project approval authority approves it against a budget; capital funding also needs its financial reference.', confirmLabel: 'Create project',
    fields: [
      { name: 'code', label: 'Project code', code: true, required: true, placeholder: 'FAC-2027-014' },
      { name: 'title', label: 'Title', required: true },
      { name: 'type', label: 'Type', type: 'select', options: [['Facility', 'Facility'], ['Capital', 'Capital']], value: 'Facility' },
      { name: 'funding', label: 'Funding', type: 'select', options: [['Operating', 'Operating (opex)'], ['Capital', 'Capital (capex)']], value: 'Operating' },
      { name: 'budget', label: 'Budget (USD)', type: 'number', step: '0.01', min: 0 },
      { name: 'financialReference', label: 'Financial reference', placeholder: 'CAPEX-2027-014 or cost center', hint: 'Required for capital funding before approval.' },
      { name: 'startDate', label: 'Start', type: 'date', value: today(), required: true },
      { name: 'endDate', label: 'End', type: 'date', value: plusDays(90), required: true },
      { name: 'owner', label: 'Owner' },
      { name: 'description', label: 'Description', type: 'textarea' },
    ],
  }, (v, reason) => command('/api/projects/', v, reason), () => projectList(page, ctx)));
}

async function projectDetail(page, ctx, code) {
  const me = ctx.me;
  const manage = can(me, ...MANAGERS);
  const approve = can(me, 'FacilitiesManager');
  const stores = can(me, 'MaterialHandler', 'InventoryController');
  const back = '<a href="#/projects">← Projects</a>';
  const [res, items, locations, resources] = await Promise.all([tryGet(`/api/projects/${encodeURIComponent(code)}`), tryGet('/api/engineering/items'), tryGet('/api/inventory/locations'), tryGet('/api/resources/')]);
  if (res.error) { page.innerHTML = back + errorBlock(res.error, 'this project'); return; }
  const d = res.data;
  const p = d.project;
  const reload = () => projectDetail(page, ctx, code);
  const draft = p.status === 'Draft';
  const open = p.status === 'Approved';
  const editable = manage && (draft || open);
  const reasonNote = open ? ' The project is approved: this becomes a change request, applied when a different person approves it.' : '';
  const parts = (items.data ?? []).map(i => [i.partNumber, `${i.partNumber} — ${i.description}`]);
  const locs = (locations.data ?? []).filter(l => l.active).map(l => [l.code, `${l.code} — ${l.name}`]);
  const machines = (resources.data?.machines ?? []).filter(m => m.status !== 'Retired' && m.active !== false).map(m => [m.code, `${m.code} — ${m.name}`]);
  const material = d.material.material;
  const pending = d.changes.filter(c => c.status === 'Pending');

  page.innerHTML = `${back}
    <div class="page-head"><div><h1><span class="mono">${esc(p.code)}</span> · ${esc(p.title)}</h1>
      <div class="sub">${esc(p.type)} project · ${esc(p.funding.toLowerCase())} funding${p.owner ? ` · owner ${esc(p.owner)}` : ''} · created by ${esc(who(p.createdBy))}${p.approvedBy ? ` · approved by ${esc(who(p.approvedBy))} ${esc(fmtDateTime(p.approvedAt))}` : ''}</div></div>
      <div class="spacer"></div><span class="pill ${STATUS_TONE[p.status] ?? 'none'}">${esc(p.status)}</span>
      ${editable ? '<button class="btn" id="edit">Change</button>' : ''}
      ${approve && draft && p.createdBy !== `person:${me.personId}` ? '<button class="btn primary" id="approve">Approve</button>' : ''}
      ${manage && open ? '<button class="btn" id="complete">Complete</button>' : ''}
      ${approve && (draft || open) ? '<button class="btn ghost" id="cancel">Cancel project</button>' : ''}
      <button class="btn ghost" id="history">What changed?</button></div>
    ${p.description ? `<p class="muted">${esc(p.description)}</p>` : ''}
    <section class="kpis">
      ${kpi('Budget', money(p.budget), p.funding === 'Capital' ? 'capital' : 'operating')}
      ${kpi('Financial reference', p.financialReference ? `<span class="mono">${esc(p.financialReference)}</span>` : '—', p.funding === 'Capital' ? 'required for capital funding' : 'optional')}
      ${kpi('Dates', `${esc(fmtDate(p.startDate))} – ${esc(fmtDate(p.endDate))}`, '')}
      ${kpi('Material', material.shortLines ? `${material.shortLines} short` : 'covered', `${material.lines.filter(l => l.status !== 'Cancelled').length} line(s)`, material.shortLines ? 'bad' : 'ok')}
      ${kpi('Waiting for a decision', pending.length, 'change request(s)', pending.length ? 'warn' : '')}
    </section>
    ${p.closeReason ? `<div class="faint">${esc(p.status)} by ${esc(who(p.closedBy))} ${esc(fmtDateTime(p.closedAt))}: ${esc(p.closeReason)}</div>` : ''}

    ${d.changes.length ? `<section class="panel"><header><h2>Change requests</h2><span class="spacer"></span><span class="muted">requested with a reason; applied when someone else approves</span></header>
      <table class="table"><thead><tr><th>#</th><th>Change</th><th>Reason</th><th>Requested</th><th>Status</th><th></th></tr></thead><tbody>
      ${d.changes.map(c => `<tr><td>${c.changeId}</td><td>${esc(describe(c))}</td><td>${esc(c.reason)}</td><td class="nowrap">${esc(who(c.requestedBy))} <span class="faint">${esc(fmtDateTime(c.requestedAt))}</span></td>
        <td><span class="pill ${CHANGE_TONE[c.status] ?? 'none'}">${esc(c.status)}</span>${c.decidedBy ? ` <span class="faint">${esc(who(c.decidedBy))}: ${esc(c.decisionReason ?? '')}</span>` : ''}</td>
        <td class="nowrap">${approve && c.status === 'Pending' && c.requestedBy !== `person:${me.personId}` ? `<button class="btn small primary" data-change-ok="${c.changeId}">Approve</button> <button class="btn small ghost" data-change-no="${c.changeId}">Reject</button>` : ''}</td></tr>`).join('')}
      </tbody></table></section>` : ''}

    <section class="panel"><header><h2>Material</h2><span class="spacer"></span><span class="muted">${esc(material.summary)}</span>
      ${editable ? ' <button class="btn small" id="line">+ Line</button>' : ''}
      ${(manage || approve) && open && material.shortLines ? ' <button class="btn small primary" id="request">Request shortages</button>' : ''}</header>
      ${!material.lines.length ? emptyBlock('No material lines', editable ? 'Add the parts this project needs: pipe, valves, anchors…' : 'The project has no material lines.') : `
      <table class="table"><thead><tr><th>Line</th><th>Part</th><th>Need</th><th class="num">Required</th><th class="num">Issued</th><th class="num">From stock</th><th class="num">On order</th><th class="num">Requested</th><th class="num">Short</th><th>Status</th><th></th></tr></thead><tbody>
      ${material.lines.map(l => `<tr class="${l.status === 'Cancelled' ? 'retired' : ''}"><td>${l.lineNumber}</td><td><span class="mono">${esc(l.partNumber)}</span>${l.description ? ` <span class="faint">${esc(l.description)}</span>` : ''}</td>
        <td class="nowrap">${esc(fmtDate(l.needDate))}</td><td class="num">${qty(l.required)}</td><td class="num">${qty(l.issued)}</td><td class="num">${qty(l.fromStock)}</td><td class="num">${qty(l.onOrder)}</td><td class="num">${qty(l.requested)}</td>
        <td class="num ${l.shortage ? 'c-bad' : ''}">${qty(l.shortage)}</td>
        <td><button class="pill linkish ${LINE_TONE[l.status] ?? 'none'}" data-why="${l.lineNumber}" title="Why">${esc(l.status)}</button></td>
        <td class="nowrap">${stores && open && l.status !== 'Cancelled' ? `<button class="btn small" data-issue="${l.lineNumber}">Issue</button> ` : ''}${stores && l.issued > 0 ? `<button class="btn small ghost" data-return="${l.lineNumber}">Return</button> ` : ''}
          ${editable && l.status !== 'Cancelled' ? `<button class="btn small ghost" data-line-change="${l.lineNumber}">Change</button> <button class="btn small ghost" data-line-cancel="${l.lineNumber}">Cancel</button>` : ''}</td></tr>`).join('')}
      </tbody></table>
      <div class="faint" id="why" style="margin-top:6px"></div>`}
    </section>

    <section class="panel"><header><h2>Purchases</h2><span class="spacer"></span><span class="muted">purchase lines bought for this project's lines</span></header>
      ${!d.purchases.length && !d.requisitions.length ? emptyBlock('Nothing bought yet', 'Shortages become requisitions sourced to the project line; the buyer orders them.') : `
      <table class="table"><thead><tr><th>Purchase</th><th>Supplier</th><th>Part</th><th class="num">Qty</th><th>Promised</th><th>For</th><th>Status</th></tr></thead><tbody>
      ${d.purchases.map(x => `<tr><td class="nowrap">PO ${x.poNumber} line ${x.poLine}</td><td>${esc(x.supplier)}</td><td class="mono">${esc(x.partNumber)}</td><td class="num">${qty(x.quantity)}</td><td>${esc(fmtDate(x.promisedDate))}</td>
        <td class="mono">${esc(x.sourceKey)}</td><td>${esc(x.orderStatus)}${x.lineStatus === 'Cancelled' ? ' · line cancelled' : ''}</td></tr>`).join('')}
      ${d.requisitions.filter(r => r.status === 'Open' || r.status === 'Cancelled').map(r => `<tr class="${r.status === 'Cancelled' ? 'retired' : ''}"><td class="nowrap">Requisition ${r.requisitionId}</td><td>${esc(r.supplierCode ?? '—')}</td><td class="mono">${esc(r.partNumber)}</td><td class="num">${qty(r.quantity)}</td>
        <td>${esc(fmtDate(r.needDate))}</td><td class="mono">${esc(r.sourceKey)}</td><td>${esc(r.status)}</td></tr>`).join('')}
      </tbody></table>`}
    </section>

    <section class="panel"><header><h2>Issues and returns</h2><span class="spacer"></span><span class="muted">issues less returns is what each line has used</span></header>
      ${!d.material.movements.length ? emptyBlock('No movements', 'Receipts for the project and issues to its lines appear here.') : `
      <table class="table"><thead><tr><th>When</th><th>Kind</th><th>Line</th><th>Part</th><th class="num">Qty</th><th>Lot</th><th>Location</th><th>By</th></tr></thead><tbody>
      ${d.material.movements.map(t => `<tr><td class="nowrap">${esc(fmtDateTime(t.at))}</td><td>${esc(t.kind)}</td><td>${t.projectLine}</td><td class="mono">${esc(t.partNumber)}</td><td class="num">${qty(t.quantity)}</td>
        <td>${esc(t.lotNumber ?? '—')}</td><td>${esc(t.toLocation ?? t.fromLocation ?? '—')}</td><td>${esc(who(t.actor))}</td></tr>`).join('')}</tbody></table>`}
    </section>

    <section class="panel"><header><h2>Tasks and milestones</h2><span class="spacer"></span>${manage && (draft || open) ? '<button class="btn small" id="task">+ Task</button>' : ''}</header>
      ${!d.tasks.length ? emptyBlock('No tasks', 'Add the steps and milestones the project is tracked by.') : `
      <table class="table"><thead><tr><th>Task</th><th>Due</th><th>Done</th><th></th></tr></thead><tbody>
      ${d.tasks.map(t => `<tr><td>${t.milestone ? '◆ ' : ''}${esc(t.title)}</td><td class="${!t.completedOn && t.dueDate < today() ? 'c-bad' : ''}">${esc(fmtDate(t.dueDate))}</td>
        <td>${t.completedOn ? `${esc(fmtDate(t.completedOn))} <span class="faint">${esc(who(t.completedBy))}</span>` : '<span class="faint">open</span>'}</td>
        <td>${manage && !t.completedOn ? `<button class="btn small" data-task-done="${t.taskId}">Done</button>` : ''}</td></tr>`).join('')}</tbody></table>`}
    </section>

    <section class="panel"><header><h2>Outages</h2><span class="spacer"></span>${manage && open ? '<button class="btn small" id="outage">Propose outage</button>' : ''}</header>
      ${!d.outages.length ? emptyBlock('No outages', 'If the work needs a machine stopped, propose an outage; a planner approves it after seeing the schedule preview.') : `
      <table class="table"><thead><tr><th>#</th><th>Machine</th><th>Window</th><th>Reason</th><th>Status</th><th>Impact</th></tr></thead><tbody>
      ${d.outages.map(o => `<tr><td>${o.outageId}</td><td>${esc(o.machineCode)}</td><td class="nowrap">${esc(fmtDateTime(o.startsAt))} – ${esc(fmtDateTime(o.endsAt))}</td><td>${esc(o.reason)}</td>
        <td><span class="pill ${OUTAGE_TONE[o.status] ?? 'none'}">${esc(o.status)}</span></td><td class="faint">${esc(o.impact ?? (o.status === 'Proposed' ? 'previewed when it is decided (Maintenance → Outages)' : ''))}</td></tr>`).join('')}</tbody></table>`}
    </section>`;

  const lineOf = n => material.lines.find(l => l.lineNumber === Number(n));
  const act = async (title, detail, url, body = {}, reasonRequired = true, confirmLabel = 'Confirm') => {
    const done = await confirmWithReason({ title, detail, confirmLabel, reasonRequired }, reason => command(url, body, reason));
    if (done) { toast(done.message); await reload(); }
  };
  const base = `/api/projects/${encodeURIComponent(p.code)}`;

  $('#history', page)?.addEventListener('click', () => historyDrawer(p.code, 'project'));
  $('#edit', page)?.addEventListener('click', () => formModal({
    title: `Change ${p.code}`, detail: 'Title, description, budget, dates and financial reference.' + reasonNote, confirmLabel: open ? 'Request change' : 'Save', reasonRequired: open,
    fields: [
      { name: 'title', label: 'Title', value: p.title, required: true },
      { name: 'budget', label: 'Budget (USD)', type: 'number', step: '0.01', min: 0, value: p.budget },
      { name: 'financialReference', label: 'Financial reference', value: p.financialReference },
      { name: 'startDate', label: 'Start', type: 'date', value: p.startDate, required: true },
      { name: 'endDate', label: 'End', type: 'date', value: p.endDate, required: true },
      { name: 'description', label: 'Description', type: 'textarea', value: p.description },
    ],
  }, (v, reason) => command(base, v, reason), reload));
  $('#approve', page)?.addEventListener('click', () => act(`Approve ${p.code}`, `Approves the project against its budget (${money(p.budget)})${p.funding === 'Capital' ? ` and capital request ${p.financialReference ?? '(none recorded)'}` : ''}. You cannot approve a project you created. From now on, changes need a reason and a second person.`, `${base}/approve`, {}, true, 'Approve'));
  $('#complete', page)?.addEventListener('click', () => act(`Complete ${p.code}`, 'Nothing more is bought for or issued to a completed project. Pending change requests must be decided first.', `${base}/complete`, {}, true, 'Complete'));
  $('#cancel', page)?.addEventListener('click', () => act(`Cancel ${p.code}`, 'Cancels the project. Open requisitions and purchase lines for it are not changed here: the buyer cancels them.', `${base}/cancel`, {}, true, 'Cancel project'));
  $('#request', page)?.addEventListener('click', () => act(`Request shortages for ${p.code}`, `Records project demand for each short line (for MRP) and raises a requisition for the shortage, sourced to the line (${material.lines.filter(l => l.status === 'Short').map(l => `line ${l.lineNumber}: ${qty(l.shortage)} ${l.partNumber}`).join(', ')}).`, `${base}/request-material`, {}, false, 'Request'));
  $('#line', page)?.addEventListener('click', () => formModal({
    title: `Add a material line to ${p.code}`, detail: 'A part the project needs, how much and by when.' + reasonNote, confirmLabel: open ? 'Request line' : 'Add line', reasonRequired: open,
    fields: [
      { name: 'partNumber', label: 'Part', type: 'select', options: parts, required: true },
      { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, required: true },
      { name: 'needDate', label: 'Needed by', type: 'date', value: p.startDate, required: true },
      { name: 'description', label: 'What it is for', placeholder: 'Gas line to the new slitter' },
    ],
  }, (v, reason) => command(`${base}/lines`, v, reason), reload));
  $('#task', page)?.addEventListener('click', () => formModal({
    title: `Add a task to ${p.code}`, confirmLabel: 'Add', fields: [
      { name: 'title', label: 'Task', required: true },
      { name: 'dueDate', label: 'Due', type: 'date', value: plusDays(14), required: true },
      { name: 'milestone', label: 'Milestone', type: 'check', text: 'This is a milestone' },
    ],
  }, (v, reason) => command(`${base}/tasks`, v, reason), reload));
  $('#outage', page)?.addEventListener('click', () => formModal({
    title: `Propose an outage for ${p.code}`, detail: 'The machine is not taken out of production until a planner approves the outage after seeing the schedule preview.', confirmLabel: 'Propose',
    fields: [
      { name: 'machineCode', label: 'Machine', type: 'select', options: machines, required: true },
      { name: 'startsAt', label: 'From', type: 'datetime-local', required: true },
      { name: 'endsAt', label: 'To', type: 'datetime-local', required: true },
      { name: 'description', label: 'Why', required: true, placeholder: 'Tie-in of the new gas line' },
    ],
  }, (v, reason) => command(`${base}/outages`, { ...v, startsAt: new Date(v.startsAt).toISOString(), endsAt: new Date(v.endsAt).toISOString() }, reason), reload));

  $$('[data-why]', page).forEach(b => b.addEventListener('click', () => { $('#why', page).textContent = lineOf(b.dataset.why)?.detail ?? ''; }));
  $$('[data-line-change]', page).forEach(b => b.addEventListener('click', () => {
    const l = lineOf(b.dataset.lineChange);
    formModal({
      title: `Change ${p.code} line ${l.lineNumber}`, detail: `${l.partNumber}. Never below what is issued (${qty(l.issued)}).` + reasonNote, confirmLabel: open ? 'Request change' : 'Save', reasonRequired: open,
      fields: [{ name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, value: l.required, required: true }, { name: 'needDate', label: 'Needed by', type: 'date', value: l.needDate, required: true }],
    }, (v, reason) => command(`${base}/lines/${l.lineNumber}`, v, reason), reload);
  }));
  $$('[data-line-cancel]', page).forEach(b => b.addEventListener('click', () =>
    act(`Cancel ${p.code} line ${b.dataset.lineCancel}`, 'Material issued to the line is returned first.' + reasonNote, `${base}/lines/${b.dataset.lineCancel}/cancel`, {}, open, open ? 'Request cancel' : 'Cancel line')));
  const move = (verb, n) => {
    const l = lineOf(n);
    formModal({
      title: `${verb === 'issues' ? 'Issue to' : 'Return from'} ${p.code} line ${l.lineNumber}`, detail: verb === 'issues' ? `${qty(l.stillNeeded)} ${l.partNumber} still needed.` : `${qty(l.issued)} issued; return at most that.`,
      confirmLabel: verb === 'issues' ? 'Issue' : 'Return', reasonRequired: verb === 'returns',
      fields: [
        { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, value: verb === 'issues' ? l.stillNeeded : l.issued, required: true },
        { name: 'locationCode', label: 'Location', type: 'select', options: locs, required: true },
        { name: 'lotNumber', label: 'Lot (lot-tracked parts)' },
      ],
    }, (v, reason) => command(`${base}/${verb}`, { ...v, lineNumber: l.lineNumber, partNumber: l.partNumber }, reason), reload);
  };
  $$('[data-issue]', page).forEach(b => b.addEventListener('click', () => move('issues', b.dataset.issue)));
  $$('[data-return]', page).forEach(b => b.addEventListener('click', () => move('returns', b.dataset.return)));
  $$('[data-task-done]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: 'Complete task', confirmLabel: 'Done', fields: [{ name: 'completedOn', label: 'Completed on', type: 'date', value: today(), required: true }],
  }, (v, reason) => command(`/api/projects/tasks/${b.dataset.taskDone}/complete`, v, reason), reload)));
  $$('[data-change-ok]', page).forEach(b => b.addEventListener('click', () => act(`Approve change ${b.dataset.changeOk}`, 'Applies the change to the project.', `/api/projects/changes/${b.dataset.changeOk}/approve`, {}, true, 'Approve')));
  $$('[data-change-no]', page).forEach(b => b.addEventListener('click', () => act(`Reject change ${b.dataset.changeNo}`, 'The project stays as it is.', `/api/projects/changes/${b.dataset.changeNo}/reject`, {}, true, 'Reject')));
}

function describe(c) {
  switch (c.kind) {
    case 'Header': return `Budget ${money(c.budget)}, ${fmtDate(c.startDate)} – ${fmtDate(c.endDate)}, reference ${c.financialReference ?? 'none'}${c.title ? `, title "${c.title}"` : ''}`;
    case 'AddLine': return `Add ${qty(c.quantity)} ${c.partNumber} by ${fmtDate(c.needDate)}`;
    case 'ChangeLine': return `Line ${c.lineNumber}: ${qty(c.quantity)} ${c.partNumber} by ${fmtDate(c.needDate)}`;
    default: return `Cancel line ${c.lineNumber} (${c.partNumber})`;
  }
}

function kpi(label, value, sub, tone = '') {
  return `<div class="kpi"><div><div class="t">${esc(label)}</div><div class="v ${tone ? `c-${tone}` : ''}">${value}</div><div class="why">${esc(sub)}</div></div></div>`;
}
