import { tryGet, session } from '../api.js';
import { icon, img, stagePhoto } from '../icons.js';
import { esc, $, $$, fmtNumber, fmtTime, fmtDateTime, timeZone, errorBlock, emptyBlock, drawer } from '../ui.js';
import { who } from '../people.js';
import { WINDOWS, buildHorizon, categoryInfo, plannedOutput, actualOutput, localDays, myAssignments } from '../model.js';
import { timelinePage } from './timeline.js';

const H = 36e5;
const STATUS = {
  ready: { label: 'Ready', tone: 'ok' },
  constrained: { label: 'Constrained', tone: 'warn' },
  stopped: { label: 'Stopped', tone: 'bad' },
  idle: { label: 'No work', tone: 'none' },
};

export async function horizonPage(page, ctx) {
  const requested = ctx.query.get('t');
  if (requested?.startsWith('-') || requested === '+2w') return timelinePage(page, ctx);
  const state = {
    windowKey: ctx.query.get('t') ?? 'now',
    safetyArea: ctx.query.get('area') ?? '',
    hideIdle: false,
    selected: null,
    outputRange: 'next',
  };
  let data = null;
  let timer = null;

  const iso = ms => new Date(ms).toISOString();

  async function load() {
    const now = Date.now();
    const win = WINDOWS[state.windowKey];
    const from = win.from(now), to = win.to(now);
    const [resources, approved, proposed, constraints, downtime, capacity, orders, activeWork, attainment, activity, material, inbox] = await Promise.all([
      tryGet('/api/resources/'),
      tryGet('/api/planning/schedules/approved'),
      tryGet('/api/planning/schedules/proposed'),
      tryGet('/api/planning/constraints'),
      tryGet(`/api/maintenance/downtime?from=${encodeURIComponent(iso(now - 8 * 24 * H))}&to=${encodeURIComponent(iso(now + 8 * 24 * H))}`),
      tryGet(`/api/planning/capacity?from=${encodeURIComponent(iso(from))}&to=${encodeURIComponent(iso(to))}`),
      tryGet('/api/orders/'),
      tryGet('/api/readiness/active-work'),
      tryGet(`/api/planning/attainment?from=${encodeURIComponent(iso(now - 24 * H))}&to=${encodeURIComponent(iso(now))}`),
      tryGet(`/api/shopfloor/activity?from=${encodeURIComponent(iso(now - 7 * 24 * H))}&to=${encodeURIComponent(iso(now))}`),
      tryGet('/api/planning/material'),
      tryGet('/api/work-inbox'),
    ]);
    // Prefer the approved schedule; fall back to the latest proposal and say so.
    const schedule = approved.data ?? proposed.data ?? null;
    data = { now, resources, approved, proposed, schedule, constraints, downtime, capacity, orders, activeWork, attainment, activity, material, inbox };
    render();
  }

  function view() {
    return buildHorizon({
      resources: data.resources.data, schedule: data.schedule, constraints: data.constraints.data,
      downtime: data.downtime.data, capacity: data.capacity.data, orders: data.orders.data,
      activeWork: data.activeWork.data, now: data.now, windowKey: state.windowKey, safetyArea: state.safetyArea || null,
      attainment: data.attainment,
      material: data.material,
    });
  }

  function render() {
    if (data.resources.error) {
      page.innerHTML = header() + errorBlock(data.resources.error, 'the plant layout');
      wireHeader();
      return;
    }
    const v = view();
    const centers = state.hideIdle ? v.centers.filter(c => c.status !== 'idle') : v.centers;
    page.innerHTML = `
      ${header()}
      ${mobileFocus(v)}
      <section class="panel flow-wrap" aria-label="Factory flow">${flow(centers, v)}</section>
      ${roleActionBar()}
      <section class="kpis">${kpis(v)}</section>
      <section class="grid-3">
        <div class="panel">${wcTable(centers, v)}</div>
        <div class="panel">${outputChart()}</div>
        <div class="panel">${topIssues(v)}</div>
      </section>
      <div class="faint" style="font-size:12px">${sourceLine(v)}</div>`;
    wireHeader();
    $$('[data-wc]', page).forEach(el => el.addEventListener('click', () => openCenter(v.centers.find(c => c.code === el.dataset.wc), v)));
    $$('[data-range]', page).forEach(b => b.addEventListener('click', () => { state.outputRange = b.dataset.range; render(); }));
    $$('[data-issue]', page).forEach(el => el.addEventListener('click', () => openIssue(v.issues[Number(el.dataset.issue)])));
  }

  function roleActionBar() {
    const actions = [];
    const proposal = data.proposed.data;
    const actor = session.subject ?? `person:${ctx.me.personId}`;
    if (proposal && proposal.proposedBy !== actor && proposal.status === 'Proposed')
      actions.push(['Proposed Schedule Awaiting Approval', '#/scheduling', 'info']);
    const down = new Set((data.downtime.data ?? []).filter(record => !record.endsAt && new Date(record.startsAt).getTime() <= data.now).map(record => record.machineCode));
    if (down.size) actions.push([`${down.size} Machine${down.size === 1 ? '' : 's'} Down`, '#/maintenance', 'bad']);
    const openConstraints = (data.constraints.data ?? []).filter(record => ['Open', 'Investigating'].includes(record.status));
    if (openConstraints.length) actions.push([`${openConstraints.length} Constraint${openConstraints.length === 1 ? '' : 's'} Open`, '#/scheduling', 'warn']);
    const shortfalls = (data.material.data?.output ?? []).filter(record => record.state === 'Shortfall');
    if (shortfalls.length) actions.push([`${shortfalls.length} Material Shortfall${shortfalls.length === 1 ? '' : 's'}`, '#/material-plan?tab=exceptions', 'bad']);
    const approvals = data.inbox.data?.approvals ?? [];
    const assignments = (data.inbox.data?.assignments ?? []).filter(record => !['Completed', 'Cancelled'].includes(record.status));
    const due = approvals.length + assignments.length;
    if (due) actions.push([`${due} Inbox Item${due === 1 ? '' : 's'} Due`, '#/my-work', 'info']);
    return `<nav class="role-action-bar" aria-label="My shift action bar"><b>My shift</b>${actions.length ? actions.map(([label, href, tone]) => `<a class="role-action-chip ${tone}" href="${href}">${esc(label)} →</a>`).join('') : '<span class="muted">No immediate bottlenecks are recorded.</span>'}</nav>`;
  }

  function header() {
    const res = data?.resources.data;
    const areas = res?.safetyAreas ?? [];
    const site = res?.sites?.[0];
    const now = new Date(data?.now ?? Date.now());
    const date = now.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric', year: 'numeric', timeZone: timeZone() });
    return `
      <div class="page-head">
        <div><h1>${ctx.route === 'supervisor' ? 'Plant Overview' : 'Factory Horizon'}</h1><div class="sub">Live operational view. Past. Present. Future.</div></div>
        <div class="spacer"></div>
        <div class="head-controls">
          ${ctx.route === 'supervisor' ? '<a class="btn primary" href="#/machine-kanban">Machine Readiness</a><a class="btn" href="#/workforce-kanban">Workforce Kanban / Staffing Planner</a><a class="btn" href="#/workforce">Report Callout</a><a class="btn" href="#/scheduling">Production Schedule</a>' : ''}
          <label for="view">View</label>
          <select class="select" id="view" style="width:auto;min-width:150px;padding:7px 10px">
            <option value="">Factory</option>
            ${areas.map(a => `<option value="${esc(a.code)}" ${a.code === state.safetyArea ? 'selected' : ''}>${esc(a.name)}</option>`).join('')}
          </select>
          <div class="seg" role="group" aria-label="Time window">
            ${Object.entries(WINDOWS).map(([k, w]) => `<button data-window="${k}" class="${k === state.windowKey ? 'on' : ''}" title="${esc(w.describe)}">${esc(w.label)}</button>`).join('')}
          </div>
        </div>
        <div class="head-controls" style="flex-direction:column;align-items:flex-end;gap:6px">
          <div class="clock">${esc(date)} &nbsp; ${esc(fmtTime(now.toISOString()))}<small>${esc(site ? `${site.name} · ${site.timeZone}` : 'No site set up yet')}</small></div>
          <button class="btn" id="filters">${icon('filter')} ${state.hideIdle ? 'Showing active only' : 'Filters'}</button>
        </div>
      </div>`;
  }

  function wireHeader() {
    $('#view', page)?.addEventListener('change', e => { state.safetyArea = e.target.value; render(); });
    $$('[data-window]', page).forEach(b => b.addEventListener('click', async () => {
      if (b.dataset.window.startsWith('-') || b.dataset.window === '+2w') { location.hash = `#/horizon?t=${b.dataset.window}`; return; }
      state.windowKey = b.dataset.window; await load();
    }));
    $('#filters', page)?.addEventListener('click', () => { state.hideIdle = !state.hideIdle; render(); });
  }

  function flow(centers, v) {
    if (!centers.length) {
      return (data.resources.data?.workCenters ?? []).length
        ? emptyBlock('Nothing to show with this filter', 'Every work center in this view has no scheduled work. Click Filters to show all.')
        : emptyBlock('No work centers yet', 'An engineer sets up sites, safety areas, work centers and machines under Plant Setup. They appear here in routing order.');
    }
    const arrow = `<div class="flow-arrow">${icon('arrow')}</div>`;
    return `<div class="flow">${centers.map(c => {
      const pct = c.load == null ? null : Math.round(c.load * 100);
      const barTone = c.status === 'stopped' ? 'bad' : c.status === 'constrained' ? 'warn' : c.status === 'idle' ? 'none' : '';
      return `<button class="stage st-${c.status} ${c.status === 'constrained' || c.status === 'stopped' ? 'sel' : ''}" data-wc="${esc(c.code)}" title="${esc(c.name)} — open for evidence">
        <div class="art"><img src="${esc(stagePhoto(c.code, c.name, c.picture))}" alt="" loading="lazy"><span class="machines">${c.machines.length} machine${c.machines.length === 1 ? '' : 's'}</span></div>
        <h3>${esc(c.name)} <span title="Scheduled load: booked hours ÷ working hours, ${esc(v.window.describe)}">${pct != null ? `${pct}%` : c.bookedHours > 0 ? `${fmtNumber(c.bookedHours)} h` : '—'}</span></h3>
        <div class="bar ${barTone}"><i style="width:${Math.min(100, pct ?? 0)}%"></i></div>
        <div class="state c-${c.state.tone}">${esc(c.state.text)}</div>
      </button>`;
    }).join(arrow)}</div>`;
  }

  function kpis(v) {
    const k = v.kpis;
    const tile = (ic, title, m, unit, href = null) => {
      const tag = href ? 'a' : 'div', linkAttrs = href ? ` href="${href}" aria-label="${esc(`${title}: ${m.value == null ? m.why : `${m.value} ${m.why}`}. Open material exceptions`)}"` : '';
      const content = m.value == null
        ? `<div class="v">Not yet measured</div><div class="why">${esc(m.why)}</div>`
        : `<div class="v">${fmtNumber(m.value)}${unit ? `<small>${unit}</small>` : ''}</div><div class="why">${esc(m.why)}</div>`;
      return `<${tag} class="kpi ${m.value == null ? 'na' : ''}${href ? ' kpi-link' : ''}" title="${esc(m.why)}"${linkAttrs}>${img(ic)}<div><div class="t">${esc(title)}</div>${content}</div></${tag}>`;
    };
    const canOpenMaterialPlan = (ctx.me?.permissions ?? []).some(permission => ['planning.mrp.read', 'planning.demand.manage'].includes(permission));
    return [
      tile('trend', 'Production Attainment', k.attainment, '%'),
      tile('alert', 'Orders At Risk', k.ordersAtRisk, ''),
      tile('maintenance', 'Machine Downtime', k.downtime, 'hr'),
      tile('qualification', 'Qualified Labor Constraint', k.laborConstraint, 'hr'),
      tile('material', 'Material Constraint', k.materialConstraint, '', canOpenMaterialPlan ? '#/material-plan?tab=exceptions' : null),
      tile('quality', 'Safety Constraint', k.safetyConstraint, 'hr'),
    ].join('');
  }

  function wcTable(centers, v) {
    const head = `<header><h2>Work Center Status</h2><span class="spacer"></span><a href="#/work-centers">View all</a></header>`;
    if (!centers.length) return head + emptyBlock('No work centers', 'Nothing to list yet.');
    return `${head}
      <div class="table-wrap"><table class="table">
        <thead><tr><th>Work Center</th><th>Status</th><th title="Booked hours ÷ working hours, ${esc(v.window.describe)}">Scheduled load</th><th>Primary Constraint</th><th></th></tr></thead>
        <tbody>${centers.map(c => {
          const s = STATUS[c.status];
          const pc = c.primary ? categoryInfo(c.primary.category) : null;
          return `<tr class="click" data-wc="${esc(c.code)}">
            <td>${esc(c.name)}</td>
            <td class="nowrap"><span class="dot ${s.tone}"></span><span class="c-${s.tone}">${s.label}</span></td>
            <td>${c.load != null ? `${Math.round(c.load * 100)}%` : c.bookedHours > 0 ? `<span title="No machines here, so there is no machine capacity to divide by; this is the booked crew time.">${fmtNumber(c.bookedHours)} h booked</span>` : '<span class="faint">—</span>'}</td>
            <td>${pc ? `<span class="c-${pc.tone}">${esc(pc.label)}</span>` : '<span class="faint">—</span>'}</td>
            <td class="chev">${icon('chevron')}</td></tr>`;
        }).join('')}</tbody>
      </table></div>`;
  }

  function outputChart() {
    const head = `<header><h2>Production Output</h2><span class="spacer"></span>
      <div class="seg"><button data-range="last" class="${state.outputRange === 'last' ? 'on' : ''}">Last 7 Days</button><button data-range="next" class="${state.outputRange === 'next' ? 'on' : ''}">Next 7 Days</button></div></header>`;
    const start = state.outputRange === 'next' ? data.now : data.now - 6 * 24 * H;
    const days = localDays(start, 7, timeZone()).slice(0, 7);
    const plan = data.schedule ? plannedOutput(data.schedule, data.orders.data, days, timeZone()).map(s => s.plan) : days.map(() => 0);
    const actual = data.activity.data ? actualOutput(data.activity.data.reports, days, timeZone()) : null;
    if (!data.schedule && !(actual ?? []).some(v => v > 0)) {
      return head + emptyBlock('Nothing planned or reported yet', 'Planned output comes from the approved schedule; actual output from quantities reported on final operations.');
    }

    const max = Math.max(1, ...plan, ...(actual ?? []));
    const nice = niceMax(max);
    const W = 360, Hh = 190, L = 34, B = 22, T = 8;
    const bw = (W - L - 8) / days.length;
    const y = val => T + (Hh - T - B) * (1 - val / nice);
    const grid = [0, 0.5, 1].map(f => `<line x1="${L}" x2="${W}" y1="${y(nice * f)}" y2="${y(nice * f)}" stroke="#2A3038"/><text x="${L - 6}" y="${y(nice * f) + 4}" text-anchor="end" fill="#9CA3AF" font-size="10">${fmtNumber(nice * f, 0)}</text>`).join('');
    const bar = (x, v, fill, title) => `<rect x="${x}" y="${y(v)}" width="${bw * 0.3}" height="${Math.max(0, Hh - B - y(v))}" fill="${fill}" rx="1"><title>${esc(title)}</title></rect>`;
    const bars = days.map((d, i) => {
      const x = L + i * bw + bw * 0.18;
      const label = new Date(`${d}T12:00:00Z`).toLocaleDateString(undefined, { weekday: 'short', timeZone: 'UTC' });
      return bar(x, plan[i], '#6B7280', `${d}: ${fmtNumber(plan[i])} planned`)
        + (actual ? bar(x + bw * 0.34, actual[i], '#F97316', `${d}: ${fmtNumber(actual[i])} completed`) : '')
        + `<text x="${x + bw * 0.32}" y="${Hh - 6}" text-anchor="middle" fill="#9CA3AF" font-size="10">${esc(label)}</text>`;
    }).join('');
    return `${head}
      <div class="legend"><span><i style="background:#6B7280"></i>Plan (scheduled completions)</span>${actual
        ? '<span><i style="background:#F97316"></i>Actual (good units, final operation)</span>'
        : `<span class="faint">Actual — ${esc(data.activity.error?.status === 403 ? 'not visible with your roles' : 'could not load')}</span>`}</div>
      <div class="chart"><svg viewBox="0 0 ${W} ${Hh}" role="img" aria-label="Planned and actual units per day">${grid}${bars}</svg></div>`;
  }

  function topIssues(v) {
    const head = `<header><h2>Top Issues (Current)</h2><span class="spacer"></span><a href="#/scheduling">View all</a></header>`;
    if (data.constraints.error && !v.issues.length) return head + errorBlock(data.constraints.error, 'constraints');
    if (!v.issues.length) return head + emptyBlock('No open issues', `No machine is down and the schedule found no constraints in ${v.window.describe}.`);
    return `${head}<ul class="issues">${v.issues.slice(0, 6).map((i, n) => `
      <li class="click" data-issue="${n}" style="cursor:pointer">
        <span class="ic img">${img(i.kind === 'down' ? 'stop' : i.tone === 'bad' ? 'alert' : i.tone === 'info' ? 'info' : 'schedule')}</span>
        <div><b>${esc(i.title)}</b><span>${i.kind === 'down' ? `Since ${esc(fmtDateTime(i.at))} · ${esc(i.reason)}` : `${new Date(i.at).getTime() > data.now ? 'From' : 'Since'} ${esc(fmtDateTime(i.at))} · Affects ${i.affects} order${i.affects === 1 ? '' : 's'}`}</span></div>
      </li>`).join('')}</ul>`;
  }

  function mobileFocus(v) {
    const focus = [...v.centers].sort((a, b) => rank(a) - rank(b)).slice(0, 4);
    const mine = myAssignments(data.schedule, ctx.me.personId, data.now, data.now + 24 * H);
    const ic = { bad: 'alert', warn: 'schedule', ok: 'complete', none: 'pause' };
    return `<div class="mobile-only"><div class="grid-2">
      <section class="panel"><header><h2>Today's Focus</h2></header>
        ${focus.length ? `<ul class="focus-list">${focus.map(c => `<li data-wc="${esc(c.code)}">
          <span class="ic img">${img(ic[c.state.tone])}</span>
          <div class="txt"><b>${esc(c.name)}</b><span class="c-${c.state.tone}">${esc(c.state.text)}</span></div>
          <span class="num">${c.load == null ? '' : `${Math.round(c.load * 100)}%`}</span></li>`).join('')}</ul>` : emptyBlock('Nothing yet', 'No work centers set up.')}
      </section>
      <section class="panel"><header><h2>My Assignments</h2></header>
        ${mine.length ? `<ul class="focus-list">${mine.slice(0, 5).map(o => `<li><span class="ic img">${img('workorder')}</span>
          <div class="txt"><b>#${o.orderNumber} · ${esc(o.description)}</b><span>${esc(o.machineCode ?? o.workCenterCode)} · ${esc(fmtDateTime(o.start))}</span></div></li>`).join('')}</ul>`
        : emptyBlock('No scheduled work for you', 'In the next 24 hours the schedule has not crewed you on any operation.')}
      </section></div></div>`;
  }

  function sourceLine(v) {
    const s = data.schedule;
    const parts = [];
    if (s) {
      parts.push(`Schedule #${s.versionId} (${s.status.toLowerCase()}${s.status === 'Proposed' ? ', not yet approved' : ''}), engine ${s.engineVersion}, output ${String(s.outputFingerprint).slice(0, 12)}…`);
    } else {
      parts.push('No schedule yet: load, at-risk orders and output need a proposed schedule.');
    }
    if (data.capacity.data) parts.push(`capacity ${data.capacity.data.engineVersion} as of ${fmtDateTime(data.capacity.data.asOf)} — ${data.capacity.data.output.confidence}`);
    else if (data.capacity.error) parts.push(`capacity unavailable: ${data.capacity.error.message}`);
    parts.push(`window: ${v.window.describe}`);
    return esc(parts.join(' · '));
  }

  function openCenter(c, v) {
    if (!c) return;
    const s = STATUS[c.status];
    const machines = c.machines.map(m => {
      const down = c.downNow.some(d => d.code === m.code);
      return `<tr><td>${esc(m.code)}</td><td>${esc(m.name)}</td><td>${down ? '<span class="pill bad">Down</span>' : '<span class="pill ok">Up</span>'}</td></tr>`;
    }).join('');
    const readiness = c.readiness.map(r => `
      <div class="panel" style="padding:10px 12px">
        <div><b>${esc(r.output.work.activityCode)}</b> on ${esc(r.output.work.machineCode ?? r.output.work.workCenterCode)} —
          ${r.output.executable ? '<span class="pill ok">Executable</span>' : `<span class="pill bad">Blocked: ${esc(r.output.primaryConstraint)}</span>`}</div>
        <ul class="evidence">${(r.output.dimensions ?? []).map(dm => `<li><span class="${dm.outcome === 'Pass' ? 'c-ok' : dm.outcome === 'Fail' ? 'c-bad' : 'c-warn'}">${esc(dm.outcome)}</span> <b>${esc(dm.name)}</b> — ${esc(dm.detail)}</li>`).join('')}
          ${(r.output.notEvaluated ?? []).length ? `<li class="faint">Not evaluated yet: ${esc(r.output.notEvaluated.join(', '))}</li>` : ''}</ul>
      </div>`).join('');
    const cons = c.constraints.map(k => `<li><b>${esc(categoryInfo(k.category).label)}</b> — ${esc(k.finding)}<br><span class="muted">${esc(k.evidence)}</span>
      ${(k.alternatives ?? []).length ? `<br><span class="c-info">Could help: ${esc(k.alternatives.join('; '))}</span>` : ''}</li>`).join('');
    const ops = c.operations.filter(o => o.scheduled).slice(0, 12).map(o => `<tr><td>#${o.orderNumber}/${o.sequence}</td><td>${esc(o.machineCode ?? '—')}</td><td>${esc(fmtDateTime(o.start))}</td><td>${esc(fmtDateTime(o.end))}</td><td title="${esc(o.bindingDetail)}">${esc(o.bindingConstraint)}</td></tr>`).join('');
    drawer(`${c.name} (${c.code})`, `
      <div><span class="pill ${s.tone}">${s.label}</span> <span class="c-${c.state.tone}" style="margin-left:6px">${esc(c.state.text)}</span></div>
      <dl class="kv">
        <dt>Window</dt><dd>${esc(v.window.describe)}</dd>
        <dt>Working hours</dt><dd>${c.workingHours == null ? 'Capacity not available' : `${fmtNumber(c.workingHours)} h across machines (shift calendars)`}</dd>
        <dt>Executable hours</dt><dd>${c.executableHours == null ? '—' : `${fmtNumber(c.executableHours)} h (staffed, safe and up)`}</dd>
        <dt>Booked hours</dt><dd>${fmtNumber(c.bookedHours)} h from the schedule</dd>
        <dt>Scheduled load</dt><dd>${c.load == null ? '—' : `${fmtNumber(c.bookedHours)} ÷ ${fmtNumber(c.workingHours)} = ${Math.round(c.load * 100)}%`}</dd>
        <dt>Downtime</dt><dd>${fmtNumber(c.downHours)} h in window</dd>
        <dt>Orders</dt><dd>${c.orders.length ? c.orders.map(n => `<a href="#/orders/${n}">#${n}</a>`).join(', ') : 'None scheduled here'}${c.late.length ? ` · <span class="c-bad">${c.late.length} late</span>` : ''}${c.atRisk.length ? ` · <span class="c-warn">${c.atRisk.length} at risk</span>` : ''}</dd>
      </dl>
      <div><h3 style="margin:0 0 6px;font-size:14px">Machines</h3>${machines ? `<table class="table"><tbody>${machines}</tbody></table>` : '<span class="muted">No machines; work here is assigned to the work center.</span>'}</div>
      <div><h3 style="margin:0 0 6px;font-size:14px">Readiness of active work now</h3>${readiness || '<span class="muted">Nobody is assigned to work here right now.</span>'}</div>
      <div><h3 style="margin:0 0 6px;font-size:14px">Constraints in window</h3>${cons ? `<ul class="evidence">${cons}</ul>` : '<span class="muted">None found by the schedule.</span>'}</div>
      <div><h3 style="margin:0 0 6px;font-size:14px">Scheduled operations</h3>${ops ? `<table class="table"><thead><tr><th>Op</th><th>Machine</th><th>Start</th><th>End</th><th>Starts because</th></tr></thead><tbody>${ops}</tbody></table>` : '<span class="muted">None.</span>'}</div>`);
  }

  function openIssue(i) {
    if (!i) return;
    if (i.kind === 'down') { location.hash = '#/maintenance'; return; }
    const c = i.constraint;
    drawer(i.title, `
      <div><span class="pill ${i.tone}">${esc(c.status)}</span></div>
      <p>${esc(c.finding)}</p>
      <dl class="kv">
        <dt>Evidence</dt><dd>${esc(c.evidence)}</dd>
        <dt>Window</dt><dd>${esc(fmtDateTime(c.windowFrom))} – ${esc(fmtDateTime(c.windowUntil))}</dd>
        <dt>Affected orders</dt><dd>${(c.affectedOrders ?? []).map(n => `<a href="#/orders/${n}">#${n}</a>`).join(', ') || '—'}</dd>
        <dt>Operations</dt><dd>${esc((c.affectedOperations ?? []).join(', ') || '—')}</dd>
        <dt>Could help</dt><dd>${esc((c.alternatives ?? []).join('; ') || 'No qualified alternative found')}</dd>
        <dt>First seen</dt><dd>${esc(fmtDateTime(c.firstSeen))}</dd>
        <dt>Owner</dt><dd>${esc(c.owner ? who(c.owner) : 'Nobody yet')}</dd>
      </dl>
      <a class="btn" href="#/scheduling">Work this constraint in Scheduling</a>`);
  }

  await load();
  // Refresh every minute while the page is open; numbers always come from the API.
  timer = setInterval(() => { if (document.visibilityState === 'visible' && !document.querySelector('.drawer, .modal')) load(); }, 60000);
  return { dispose: () => clearInterval(timer) };
}

function rank(c) { return { bad: 0, warn: 1, ok: 2, none: 3 }[c.state.tone] * 10 + { stopped: 0, constrained: 1, ready: 2, idle: 3 }[c.status]; }

function niceMax(v) {
  const p = 10 ** Math.floor(Math.log10(v));
  return [1, 2, 2.5, 5, 10].map(m => m * p).find(m => m >= v) ?? v;
}

