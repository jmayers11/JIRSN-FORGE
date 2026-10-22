import { tryGet, readPost, command } from '../api.js';
import { esc, $, $$, fmtDateTime, timeZone, errorBlock, emptyBlock, drawer, toast } from '../ui.js';
import { futureEventsHtml } from '../history.js';
import { WINDOWS } from '../model.js';
import { canAction } from '../ui-authority.mjs';

const H = 36e5;
const TONE = { OnTime: '#22C55E', AtRisk: '#F97316', Late: '#EF4444', Unschedulable: '#EF4444' };

export async function timelinePage(page, ctx) {
  if (ctx.query.get('source') === 'proposed') return proposalTimelinePage(page, ctx);
  const lenses = ['Factory', 'Program', 'Contract', 'WorkCenter', 'Machine', 'Order', 'Workforce'];
  const names = { WorkCenter: 'Work center' };
  const fields = { Program: 'programId', Contract: 'contractId', WorkCenter: 'workCenter', Machine: 'machine', Order: 'orderNumber', Workforce: 'personId' };
  const choices = { Program: 'programs', Contract: 'contracts', WorkCenter: 'workCenters', Machine: 'machines', Order: 'orders', Workforce: 'people' };
  const state = { lens: ctx.query.get('lens') ?? 'Factory', window: ctx.query.get('t') ?? '+2w', anchor: ctx.query.get('anchor') ?? '', onlyCommitted: ctx.query.get('onlyCommitted') === 'true' };
  const scope = Object.fromEntries(Object.values(fields).map(key => [key, ctx.query.get(key) ?? '']));
  let data = null;
  let generation = 0;
  if (!lenses.includes(state.lens)) state.lens = 'Factory';
  if (!WINDOWS[state.window]) state.window = '+2w';
  if (state.lens === 'Contract') state.onlyCommitted = true;

  async function load() {
    const current = ++generation;
    const anchor = state.anchor ? new Date(state.anchor).getTime() : Date.now();
    if (!Number.isFinite(anchor)) { page.innerHTML = emptyBlock('Choose a valid date and time', 'The window anchor sets the past, current or future projection window.'); return; }
    const window = WINDOWS[state.window];
    const from = window.from(anchor), to = window.to(anchor);
    const at = state.window.startsWith('-') ? from : anchor;
    const query = new URLSearchParams({ lens: state.lens, at: new Date(at).toISOString(), from: new Date(from).toISOString(), to: new Date(to).toISOString(), onlyCommitted: String(state.onlyCommitted) });
    for (const [key, value] of Object.entries(scope)) if (value) query.set(key, value);
    const result = await tryGet(`/api/horizon/lenses?${query}`);
    if (current !== generation) return;
    data = result.data;
    render(result.error);
  }

  function render(error) {
    const field = fields[state.lens];
    const options = data?.choices[choices[state.lens]] ?? [];
    page.innerHTML = `<div class="page-head"><div><h1>Horizon</h1><div class="sub">The recorded approved plan at one instant, across the same manufacturing scope.</div></div><span class="spacer"></span><a class="btn small" href="#/horizon?source=proposed">Current proposal</a><a class="btn small" href="#/reports?view=changes">What changed?</a></div>
      <nav class="toolbar horizon-lenses" aria-label="Horizon lenses">${lenses.map(lens => `<button class="btn small ${lens === state.lens ? 'primary' : ''}" data-lens="${lens}">${names[lens] ?? lens}</button>`).join('')}</nav>
      <section class="panel"><div class="toolbar"><div class="seg" role="group" aria-label="Time window">${Object.entries(WINDOWS).map(([key, window]) => `<button data-window="${key}" class="${key === state.window ? 'on' : ''}">${esc(window.label)}</button>`).join('')}</div>
        <label for="horizon-anchor">Window anchor</label><input class="input" id="horizon-anchor" type="datetime-local" value="${esc(state.anchor)}">
        ${field ? `<label for="horizon-scope">${names[state.lens] ?? state.lens}</label><select class="select" id="horizon-scope"><option value="">All visible</option>${options.map(option => `<option value="${esc(option.key)}" ${scope[field] === option.key ? 'selected' : ''}>${esc(option.label)}</option>`).join('')}</select>` : ''}
        <label class="check"><input id="horizon-committed" type="checkbox" ${state.onlyCommitted ? 'checked' : ''}> Recorded contract commitments only</label><button class="btn small" id="clear-horizon-scope">Clear scope</button></div>
        <div class="faint">${Object.entries(scope).filter(([, value]) => value).map(([key, value]) => `${esc(key)}: ${esc(value)}`).join(' · ') || 'All visible manufacturing records'}${state.onlyCommitted ? ' · only explicitly committed orders' : ''}</div></section>
      ${error ? errorBlock(error, 'this Horizon snapshot') : data ? snapshotHtml(data) : ''}`;
    $$('[data-lens]', page).forEach(button => button.onclick = () => { state.lens = button.dataset.lens; if (state.lens === 'Contract') state.onlyCommitted = true; load(); });
    $$('[data-window]', page).forEach(button => button.onclick = () => { state.window = button.dataset.window; load(); });
    $('#horizon-anchor', page).onchange = event => { state.anchor = event.target.value; load(); };
    $('#horizon-scope', page)?.addEventListener('change', event => { scope[field] = event.target.value; load(); });
    $('#horizon-committed', page).onchange = event => { state.onlyCommitted = event.target.checked; load(); };
    $('#clear-horizon-scope', page).onclick = () => { for (const key of Object.keys(scope)) scope[key] = ''; state.onlyCommitted = state.lens === 'Contract'; load(); };
  }

  function snapshotHtml(snapshot) {
    const counts = snapshot.counts;
    const tile = (label, count) => `<div class="kpi"><div><div class="t">${label}</div><div class="v">${count}</div></div></div>`;
    return `<section class="kpis">${tile('Distinct orders', counts.orders)}${tile('Operations', counts.operations)}${tile('Scheduled', counts.scheduled)}${tile('Not scheduled', counts.unscheduled)}${tile('Late', counts.late)}${tile('At risk', counts.atRisk)}</section>
      <section class="panel"><header><h2>${names[state.lens] ?? state.lens} lens</h2><span class="spacer"></span><span class="muted">Projection as of ${esc(fmtDateTime(snapshot.at))}</span></header>
        <p class="muted">${esc(snapshot.source)}</p><div class="faint">Window ${esc(fmtDateTime(snapshot.from))} – ${esc(fmtDateTime(snapshot.to))}. ${snapshot.excludedUnlinkedOperations ? `${snapshot.excludedUnlinkedOperations} operation(s) excluded because no contract commitment was recorded at this instant.` : ''}</div>
        ${snapshot.groups.length ? `<table class="table"><thead><tr><th>Scope</th><th>Distinct orders</th><th>Operations</th><th>Scheduled</th><th>Not scheduled</th><th>Late</th><th>At risk</th></tr></thead><tbody>${snapshot.groups.map(group => `<tr><td>${esc(group.label)}</td><td>${group.counts.orders}</td><td>${group.counts.operations}</td><td>${group.counts.scheduled}</td><td>${group.counts.unscheduled}</td><td>${group.counts.late}</td><td>${group.counts.atRisk}</td></tr>`).join('')}</tbody></table>` : emptyBlock('No recorded work in this scope', 'Choose another window or scope. A missing past projection stays unknown.')}
      </section>${ganttHtml(snapshot)}<section class="panel"><header><h2>Recorded operations</h2><span class="spacer"></span><span class="muted">Schedule #${esc(snapshot.scheduleVersion ?? 'unknown')}</span></header>
        ${snapshot.operations.length ? `<table class="table"><thead><tr><th>Order/op</th><th>Work center</th><th>Machine</th><th>Start</th><th>Finish</th><th>Recorded constraint</th><th>Crew in this plan</th></tr></thead><tbody>${snapshot.operations.map(operation => `<tr><td><a href="#/orders/${operation.orderNumber}">#${operation.orderNumber}/${operation.sequence}</a></td><td>${esc(operation.workCenterCode)}</td><td>${esc(operation.machineCode ?? 'No machine')}</td><td>${esc(fmtDateTime(operation.start))}</td><td>${esc(fmtDateTime(operation.end))}</td><td>${esc(operation.bindingConstraint)}<div class="faint">${esc(operation.bindingDetail)}</div></td><td>${esc(operation.operators.join(', ') || 'Not assigned')}</td></tr>`).join('')}</tbody></table>` : ''}
      </section>`;
  }

  function ganttHtml(snapshot) {
    const operations = snapshot.operations.filter(operation => operation.scheduled);
    if (!operations.length) return '';
    const from = Date.parse(snapshot.from), to = Date.parse(snapshot.to);
    const width = 1100, left = 165, rowHeight = 31, top = 32, height = top + operations.length * rowHeight + 10;
    const x = instant => left + (instant - from) / (to - from) * (width - left - 8);
    const status = new Map(snapshot.orders.map(order => [order.orderNumber, order.status]));
    const rows = operations.map((operation, index) => `<rect x="0" y="${top + index * rowHeight}" width="${width}" height="${rowHeight}" fill="${index % 2 ? '#161B21' : '#1A2027'}"/><text x="8" y="${top + index * rowHeight + 20}">#${operation.orderNumber}/${operation.sequence} · ${esc(operation.machineCode ?? operation.workCenterCode)}</text>`).join('');
    const bars = operations.flatMap((operation, index) => operation.segments.map(segment => {
      const a = Math.max(from, Date.parse(segment.start)), b = Math.min(to, Date.parse(segment.end));
      if (b <= a) return '';
      return `<rect x="${x(a)}" y="${top + index * rowHeight + 7}" width="${Math.max(2, x(b) - x(a))}" height="17" rx="2" fill="${TONE[status.get(operation.orderNumber)] ?? '#6B7280'}"><title>${esc(`#${operation.orderNumber}/${operation.sequence} · ${fmtDateTime(segment.start)} – ${fmtDateTime(segment.end)} · ${operation.bindingDetail}`)}</title></rect>`;
    })).join('');
    const ticks = Array.from({ length: 6 }, (_, index) => from + (to - from) * index / 5).map(instant => `<line x1="${x(instant)}" x2="${x(instant)}" y1="${top}" y2="${height}" stroke="#2A3038"/><text x="${x(instant) + 3}" y="18">${esc(new Date(instant).toLocaleDateString(undefined, { month: 'short', day: 'numeric', timeZone: timeZone() }))}</text>`).join('');
    return `<section class="panel"><header><h2>Recorded timeline</h2><span class="muted">Approved schedule #${esc(snapshot.scheduleVersion)}</span></header><div class="gantt"><svg viewBox="0 0 ${width} ${height}" width="${width}" role="img" aria-label="Recorded schedule timeline">${rows}${ticks}${bars}</svg></div></section>`;
  }
  await load();
  return { dispose: () => { generation++; } };
}

async function proposalTimelinePage(page, ctx) {
  let span = Number(ctx.query.get('days') ?? 3);
  let which = 'proposed';
  let farAhead = false;
  let filterStatus = 'All';
  const now = Date.now();
  const iso = ms => new Date(ms).toISOString();
  const [approved, proposed, downtime, resources, future, constraints] = await Promise.all([
    tryGet('/api/planning/schedules/approved'), tryGet('/api/planning/schedules/proposed'),
    tryGet(`/api/maintenance/downtime?from=${encodeURIComponent(iso(now - 864e5))}&to=${encodeURIComponent(iso(now + 15 * 864e5))}`),
    tryGet('/api/resources/'),
    tryGet('/api/horizon/events?days=90'),
    tryGet('/api/planning/constraints'),
  ]);

  // V2.12: what the records say lies ahead — effective dates, reviews, outages, due dates, milestones.
  function comingUp() {
    const days = farAhead ? 90 : span;
    const events = (future.data ?? []).filter(e => new Date(e.at).getTime() < now + days * 24 * H);
    return `<section class="panel"><header><h2>Coming up</h2><span class="spacer"></span>
        <div class="seg"><button data-far="0" class="${farAhead ? '' : 'on'}">This window</button><button data-far="1" class="${farAhead ? 'on' : ''}">90 days</button></div></header>
      ${future.error ? errorBlock(future.error, 'future events') : futureEventsHtml(events, days)}
      <div class="faint" style="font-size:12px">Only dates recorded in Forge: item and document effective dates and reviews, engineering changes, alternates, obsolescence dates, outages, work-order due dates and project tasks.</div></section>`;
  }

  function render() {
    const s = (which === 'approved' ? approved : proposed).data;
    const head = `
      <div class="page-head">
        <div><h1>Horizon</h1><div class="sub">The schedule over time: every operation on its machine, with downtime and the reason each one starts when it does.</div></div>
        <div class="spacer"></div>
        <nav class="toolbar horizon-lenses" aria-label="Horizon lenses">
          <a class="btn small" href="#/home">Factory</a><a class="btn small" href="#/programs">Program</a><a class="btn small" href="#/programs">Contract</a>
          <a class="btn small" href="#/work-centers">Work center</a><a class="btn small" href="#/maintenance">Machine</a>
          <a class="btn small" href="#/orders">Order</a><a class="btn small" href="#/workforce">Workforce</a>
          <a class="btn small" href="#/reports?view=changes">What changed?</a>
        </nav>
        <div class="head-controls">
          <div class="seg">${['approved', 'proposed'].map(k => `<button data-which="${k}" class="${k === which ? 'on' : ''}">${k === 'approved' ? 'Approved' : 'Proposal'}</button>`).join('')}</div>
          <div class="seg" aria-label="Timeline window">${[1, 3, 7, 14].map(d => `<button data-days="${d}" class="${d === span ? 'on' : ''}">${d === 1 ? '24h' : `${d}d`}</button>`).join('')}</div>
          <div class="seg" aria-label="Filter by status">${['All', 'Late', 'AtRisk', 'Setup'].map(status => `<button data-status="${status}" class="${status === filterStatus ? 'on' : ''}">${status === 'AtRisk' ? 'At risk' : status}</button>`).join('')}</div>
        </div>
      </div>`;
    if (!s) {
      const r = which === 'approved' ? approved : proposed;
      page.innerHTML = head + (r.error && r.error.status !== 404 ? errorBlock(r.error, 'the schedule') : emptyBlock(which === 'approved' ? 'No approved schedule' : 'No proposal', 'A planner proposes a schedule under Scheduling; another approves it.')) + comingUp();
      wire();
      return;
    }
    const ops = s.result.operations.filter(o => o.scheduled);
    const orderStatus = new Map(s.result.orders.map(o => [o.orderNumber, o.status]));
    const from = now - 2 * H, to = from + span * 24 * H;
    const rows = [...new Set(ops.map(o => o.machineCode ?? `${o.workCenterCode} (no machine)`))]
      .sort((a, b) => a.localeCompare(b));
    for (const m of resources.data?.machines ?? []) if (!rows.includes(m.code)) rows.push(m.code);
    if (!rows.length) { page.innerHTML = head + emptyBlock('Nothing scheduled', 'This schedule has no scheduled operations.') + comingUp(); wire(); return; }

    const W = 1100, L = 130, RH = 30, T = 28, Hh = T + rows.length * RH + 8;
    const x = ms => L + ((ms - from) / (to - from)) * (W - L - 8);
    const tickStep = span <= 1 ? 3 * H : span <= 3 ? 12 * H : 24 * H;
    const ticks = [];
    for (let tk = Math.ceil(from / tickStep) * tickStep; tk <= to; tk += tickStep) ticks.push(tk);
    const fmtTick = ms => new Date(ms).toLocaleString(undefined, tickStep < 24 * H ? { weekday: 'short', hour: 'numeric', timeZone: timeZone() } : { weekday: 'short', month: 'short', day: 'numeric', timeZone: timeZone() });
    const clip = (a, b) => [Math.max(a, from), Math.min(b, to)];

    const grid = ticks.map(tk => `<line x1="${x(tk)}" x2="${x(tk)}" y1="${T - 6}" y2="${Hh}" stroke="#222830"/><text x="${x(tk) + 3}" y="${T - 10}">${esc(fmtTick(tk))}</text>`).join('');
    const lanes = rows.map((r, i) => `<rect x="0" y="${T + i * RH}" width="${W}" height="${RH}" fill="${i % 2 ? '#161B21' : '#1A2027'}"/><text x="8" y="${T + i * RH + 19}">${esc(r)}</text>`).join('');
    const downs = (downtime.data ?? []).filter(w => rows.includes(w.machineCode)).map(w => {
      const [a, b] = clip(new Date(w.startsAt).getTime(), w.endsAt ? new Date(w.endsAt).getTime() : now);
      if (b <= a) return '';
      const i = rows.indexOf(w.machineCode);
      return `<rect x="${x(a)}" y="${T + i * RH + 2}" width="${x(b) - x(a)}" height="${RH - 4}" fill="url(#hatch)" stroke="#EF4444" stroke-width=".8"><title>${esc(w.machineCode)} ${esc(w.kind)} downtime: ${esc(w.reason)}</title></rect>`;
    }).join('');
    const visibleOps = ops.filter(operation => {
      if (filterStatus === 'All') return true;
      if (filterStatus === 'Setup') return Number(operation.setupMinutes ?? 0) > 0 || !!operation.setupFamily || (operation.segments ?? []).some(segment => segment.kind === 'Setup');
      return orderStatus.get(operation.orderNumber) === filterStatus;
    });
    const bars = visibleOps.flatMap(o => (o.segments ?? []).map(g => {
      const [a, b] = clip(new Date(g.start).getTime(), new Date(g.end).getTime());
      if (b <= a) return '';
      const i = rows.indexOf(o.machineCode ?? `${o.workCenterCode} (no machine)`);
      const tip = `Order #${o.orderNumber} op ${o.sequence} ${o.description}\n${fmtDateTime(g.start)} – ${fmtDateTime(g.end)}\nStarts because: ${o.bindingConstraint} — ${o.bindingDetail}\nCrew: ${(o.operators ?? []).join(', ') || '—'}`;
      const w = x(b) - x(a);
      return `<g><rect class="gantt-bar" tabindex="0" role="button" aria-label="Inspect order ${o.orderNumber}, operation ${o.sequence}" data-gantt-order="${o.orderNumber}" data-gantt-op="${o.sequence}" x="${x(a)}" y="${T + i * RH + 6}" width="${Math.max(2, w)}" height="${RH - 12}" rx="2" fill="${TONE[orderStatus.get(o.orderNumber)] ?? '#6B7280'}" fill-opacity=".85"><title>${esc(tip)}</title></rect>
        ${w > 74 ? `<text x="${x(a) + 4}" y="${T + i * RH + 19}" fill="#0F1419" style="fill:#0F1419;font-weight:600">#${o.orderNumber}/${o.sequence}</text>` : ''}</g>`;
    })).join('');
    const marks = (future.data ?? []).filter(e => e.link.startsWith('machine:') && rows.includes(e.link.slice(8))).map(e => {
      const t = new Date(e.at).getTime();
      if (t < from || t > to) return '';
      const i = rows.indexOf(e.link.slice(8));
      const cx = x(t), cy = T + i * RH + RH / 2;
      return `<path d="M${cx} ${cy - 7} L${cx + 7} ${cy} L${cx} ${cy + 7} L${cx - 7} ${cy} Z" fill="#60A5FA" stroke="#0F1419"><title>${esc(fmtDateTime(e.at))} ${esc(e.kind)}: ${esc(e.title)}</title></path>`;
    }).join('');
    const nowLine = `<line x1="${x(now)}" x2="${x(now)}" y1="${T - 6}" y2="${Hh}" stroke="#F97316" stroke-width="2"/><text x="${x(now) + 4}" y="${Hh - 4}" style="fill:#F97316">now</text>`;

    page.innerHTML = `${head}
      <section class="panel">
        <div class="toolbar"><label class="muted">Zoom / window</label><div class="seg" aria-label="Timeline window">${[1, 3, 7, 14].map(d => `<button data-days="${d}" class="${d === span ? 'on' : ''}">${d === 1 ? '24h' : `${d}d`}</button>`).join('')}</div><label class="muted">Filter by status</label><div class="seg" aria-label="Filter by status">${['All', 'Late', 'AtRisk', 'Setup'].map(status => `<button data-status="${status}" class="${status === filterStatus ? 'on' : ''}">${status === 'AtRisk' ? 'At risk' : status}</button>`).join('')}</div></div>
        <div class="legend" style="justify-content:flex-start"><span><i style="background:#22C55E"></i>On time</span><span><i style="background:#F97316"></i>At risk</span><span><i style="background:#EF4444"></i>Late</span><span><i style="background:repeating-linear-gradient(45deg,#EF4444 0 2px,transparent 2px 5px)"></i>Downtime</span><span><i style="background:#60A5FA;transform:rotate(45deg)"></i>Recorded future event</span>
          <span class="faint">Schedule #${s.versionId} · ${esc(s.status)} · click or focus a bar to inspect the operation</span></div>
        <div class="gantt"><svg viewBox="0 0 ${W} ${Hh}" width="${W}" style="min-width:${W}px" role="img" aria-label="Schedule timeline">
          <defs><pattern id="hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="6" height="6" fill="rgba(239,68,68,.12)"/><line x1="0" y1="0" x2="0" y2="6" stroke="#EF4444" stroke-width="1.5"/></pattern></defs>
          ${lanes}${grid}${downs}${bars}${marks}${nowLine}</svg></div>
      </section>
      ${comingUp()}`;
    wire(s, orderStatus);
  }

  function wire(schedule = null, orderStatus = new Map()) {
    $$('[data-days]', page).forEach(b => b.addEventListener('click', () => { span = Number(b.dataset.days); render(); }));
    $$('[data-which]', page).forEach(b => b.addEventListener('click', () => { which = b.dataset.which; render(); }));
    $$('[data-status]', page).forEach(b => b.addEventListener('click', () => { filterStatus = b.dataset.status; render(); }));
    $$('[data-far]', page).forEach(b => b.addEventListener('click', () => { farAhead = b.dataset.far === '1'; render(); }));
    $$('[data-gantt-order]', page).forEach(bar => {
      const inspect = () => {
        const selected = schedule?.result?.operations.find(item => item.orderNumber === Number(bar.dataset.ganttOrder) && item.sequence === Number(bar.dataset.ganttOp));
        if (selected) inspectOperation(selected, orderStatus).catch(error => toast(error.message, true));
      };
      bar.addEventListener('click', inspect);
      bar.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); inspect(); } });
    });
  }

  async function inspectOperation(operation, orderStatus) {
    const [orderResult, constraintResult] = await Promise.all([tryGet(`/api/orders/${operation.orderNumber}`), Promise.resolve(constraints)]);
    const order = orderResult.data;
    const orderOperation = order?.operations?.find(candidate => candidate.sequence === operation.sequence);
    const segments = operation.segments ?? [];
    const start = segments.length ? segments.map(segment => segment.start).sort()[0] : orderOperation?.plannedStart;
    const end = segments.length ? segments.map(segment => segment.end).sort().at(-1) : orderOperation?.plannedEnd;
    const status = orderStatus.get(operation.orderNumber) ?? 'Unknown';
    const matching = (constraintResult.data ?? []).filter(constraint =>
      (constraint.affectedOrders ?? []).includes(operation.orderNumber)
      || (constraint.affectedOperations ?? []).includes(`${operation.orderNumber}/${operation.sequence}`)
      || [operation.machineCode, operation.workCenterCode].includes(constraint.subject));
    const setupMinutes = orderOperation?.setupMinutes ?? operation.setupMinutes ?? 0;
    const setupFamily = orderOperation?.setupFamily ?? operation.setupFamily ?? 'Standard';
    const canChangePriority = canAction(ctx.me, 'orders.priority');
    const panel = drawer(`Order #${operation.orderNumber} · operation ${operation.sequence}`, `
      <h3>Order & operation summary</h3><dl class="kv"><dt>Part</dt><dd>${esc(order?.partNumber ?? '—')}</dd><dt>Operation</dt><dd>${esc(operation.description ?? orderOperation?.description ?? '—')}</dd>
        <dt>Machine</dt><dd>${esc(operation.machineCode ?? 'No machine')}</dd><dt>Work center</dt><dd>${esc(operation.workCenterCode ?? '—')}</dd>
        <dt>Planned window</dt><dd>${esc(fmtDateTime(start))} → ${esc(fmtDateTime(end))}</dd><dt>Set-up</dt><dd>${esc(String(setupMinutes))} min · ${esc(setupFamily)}</dd><dt>Delivery status</dt><dd><span class="pill ${status === 'Late' ? 'bad' : status === 'AtRisk' ? 'warn' : 'ok'}">${esc(status)}</span></dd></dl>
      <h3>Why it starts here</h3>${operation.bindingConstraint ? `<p><b>${esc(operation.bindingConstraint)}</b> — ${esc(operation.bindingDetail ?? '')}</p>` : ''}${matching.length ? `<ul class="evidence">${matching.map(constraint => `<li><b>${esc(constraint.category ?? 'Constraint')}: ${esc(constraint.subject ?? '')}</b> — ${esc(constraint.finding ?? constraint.description ?? constraint.status ?? '')}</li>`).join('')}</ul>` : '<p class="muted">No matching open constraint is visible for this operation.</p>'}
      ${canChangePriority ? `<h3>Preview & change order priority</h3><div class="field"><label for="dispatch-priority">Priority (1 highest, 9 lowest)</label><select class="select" id="dispatch-priority">${Array.from({ length: 9 }, (_, index) => index + 1).map(value => `<option value="${value}" ${value === (order?.priority ?? 5) ? 'selected' : ''}>${value} ${value === 1 ? '— Expedite' : value === 5 ? '— Normal' : value === 9 ? '— Low' : ''}</option>`).join('')}</select></div><button class="btn small" data-priority-preview>Preview impact</button><div data-priority-impact class="muted" style="margin-top:8px">Preview the schedule before saving.</div><div class="field"><label for="dispatch-reason">Reason (recorded in the audit trail)</label><textarea id="dispatch-reason" class="input" rows="2"></textarea></div><button class="btn primary" data-priority-save disabled>Confirm priority change</button>` : ''}
      <div class="actions" style="margin-top:14px"><a class="btn" href="#/scenarios?order=${operation.orderNumber}&op=${operation.sequence}&wc=${encodeURIComponent(operation.workCenterCode ?? '')}">Run What-If Recovery Trial →</a><a class="btn" href="#/orders/${operation.orderNumber}">Open Order #${operation.orderNumber} →</a></div>`);
    const previewButton = $('[data-priority-preview]', panel.element);
    const saveButton = $('[data-priority-save]', panel.element);
    const select = $('#dispatch-priority', panel.element);
    const impact = $('[data-priority-impact]', panel.element);
    let preview = null;
    previewButton?.addEventListener('click', async () => {
      saveButton.disabled = true; impact.textContent = 'Recalculating schedule impact…';
      try {
        preview = await readPost(`/api/orders/${operation.orderNumber}/priority-preview`, { priority: Number(select.value) });
        const changes = preview.orders ?? [];
        const affected = changes.filter(change => change.orderNumber === operation.orderNumber || change.delayMinutes != null);
        impact.innerHTML = affected.length ? `<table class="table"><thead><tr><th>Order</th><th>Before</th><th>After</th><th>Ripple (min)</th></tr></thead><tbody>${affected.map(change => `<tr><td>#${change.orderNumber}</td><td>${esc(change.statusBefore ?? '—')}<div class="faint">${esc(fmtDateTime(change.completionBefore))}</div></td><td>${esc(change.statusAfter ?? '—')}<div class="faint">${esc(fmtDateTime(change.completionAfter))}</div></td><td>${change.delayMinutes == null ? '—' : esc(String(change.delayMinutes))}</td></tr>`).join('')}</tbody></table>` : '<p class="muted">No schedule ripple was returned.</p>';
        saveButton.disabled = Number(select.value) === Number(order?.priority ?? 5);
      } catch (error) { preview = null; impact.innerHTML = `<span class="c-bad">${esc(error.message)}</span>`; }
    });
    select?.addEventListener('change', () => { preview = null; saveButton.disabled = true; impact.textContent = 'Preview again after changing priority.'; });
    saveButton?.addEventListener('click', async () => {
      const reason = $('#dispatch-reason', panel.element).value.trim();
      if (!reason) { toast('Give a reason for the priority change.', true); return; }
      if (!preview) { toast('Preview the schedule impact before saving.', true); return; }
      saveButton.disabled = true;
      try {
        const saved = await command(`/api/orders/${operation.orderNumber}/priority`, { priority: preview.proposedPriority, expectedPriority: preview.currentPriority, previewAsOf: preview.asOf, previewFingerprint: preview.fingerprint }, reason);
        panel.close(); toast(saved.message ?? 'Priority updated.'); render();
      } catch (error) { toast(error.message, true); saveButton.disabled = false; }
    });
  }

  if (!approved.data && proposed.data) which = 'proposed';
  render();
}
