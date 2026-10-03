import { tryGet, command, session } from '../api.js';
import { who } from '../people.js';
import { esc, $, $$, fmtDate, fmtDateTime, fmtNumber, errorBlock, emptyBlock, toast, confirmWithReason, timeZone } from '../ui.js';
import { formModal } from '../forms.js';

// Reports (spec §54) and Horizon history (§63–65). Every report row keeps the records it came from;
// they show as links. Exports are made by the server from the same result, so CSV/XLSX match the screen.

const FILTER_KEYS = ['from', 'to', 'lastDays', 'programId', 'workCenter', 'machine', 'partNumber', 'shift', 'status', 'search', 'groupBy'];
const TONE = {
  'On plan': 'ok', Completed: 'ok', 'On time': 'ok', Delivered: 'ok', Pass: 'ok', Closed: 'none', Verified: 'ok', Resolved: 'ok',
  Short: 'warn', CompletedLate: 'warn', Late: 'warn', 'Partly delivered': 'warn', AtRisk: 'warn', Investigating: 'warn', Open: 'info',
  'Not reported': 'bad', Overdue: 'bad', Fail: 'bad', Unplanned: 'warn', Cancelled: 'none', 'Not planned': 'warn',
};
const STATUS_COLUMNS = new Set(['status', 'state', 'result']);

/** Where a source record opens in Forge. */
export function traceLink(t) {
  const [kind, ...rest] = t.split(':');
  const id = rest.join(':');
  const route = {
    order: `#/orders/${id}`, item: `#/engineering/${encodeURIComponent(id)}`, document: `#/engineering/documents/${encodeURIComponent(id)}`, supplier: '#/procurement?tab=suppliers', schedule: '#/scheduling', constraint: '#/scheduling?closed=1', report: `#/orders`,
    machine: '#/maintenance', downtime: '#/maintenance?tab=log', 'work-order': '#/maintenance?tab=orders',
    demand: '#/material-plan?tab=demand', delivery: '#/programs', contract: '#/programs', program: `#/programs/${id}`,
    shipment: '#/shipping', ncr: '#/quality?tab=ncr', inspection: '#/quality', transaction: '#/inventory?tab=ledger',
    po: '#/procurement', request: '#/inventory', audit: '#/settings', person: '#/workforce', qualification: '#/workforce',
  }[kind];
  const label = kind === 'program' ? 'program' : kind === 'person' ? 'person' : `${kind.replace('-', ' ')} ${id}`;
  return route ? `<a href="${route}" title="${esc(t)}">${esc(label)}</a>` : esc(t);
}

export function cellHtml(col, v) {
  if (v === null || v === undefined || v === '') return '<span class="muted">—</span>';
  switch (col.kind) {
    case 'Date': return esc(fmtDate(v));
    case 'DateTime': return esc(fmtDateTime(v));
    case 'Number': return fmtNumber(v, 2);
    case 'Hours': return fmtNumber(v, 1);
    default:
      if (typeof v === 'string' && v.startsWith('person:')) return esc(who(v));
      if (STATUS_COLUMNS.has(col.key)) return `<span class="pill ${TONE[v] ?? 'none'}">${esc(v)}</span>`;
      return esc(v);
  }
}
const isNum = col => col.kind === 'Number' || col.kind === 'Hours';

function queryOf(params) {
  const q = new URLSearchParams();
  for (const k of FILTER_KEYS) if (params[k] !== undefined && params[k] !== null && params[k] !== '') q.set(k, params[k]);
  return q;
}

export async function reportsPage(page, ctx) {
  const view = ctx.query.get('view') ?? (ctx.query.get('report') || ctx.query.get('definition') ? 'run' : 'catalog');
  const [catalog, definitions, programs, resources] = await Promise.all([tryGet('/api/reports/catalog'), tryGet('/api/reports/definitions'), tryGet('/api/programs/'), tryGet('/api/resources/')]);
  const state = { catalog: catalog.data ?? [], definitions: definitions.data ?? [], programs: programs.data?.map(p => p.program ?? p) ?? [], shifts: resources.data?.shifts ?? [] };
  const canHistory = ctx.me.roles.some(r => ['Planner', 'Supervisor', 'ManufacturingEngineer', 'ProgramManager', 'MaintenancePlanner'].includes(r));

  const tabs = [['catalog', 'Standard reports'], ['saved', `Saved${state.definitions.length ? ` (${state.definitions.length})` : ''}`]];
  if (canHistory) tabs.push(['changes', 'What changed?']);
  const head = active => `
    <div class="page-head no-print"><div><h1>Reports</h1><div class="sub">Standard reports over Forge's records, traceable to each source record, with saved definitions and CSV, Excel and print output. “What changed?” compares the approved schedules in force at two moments.</div></div></div>
    <div class="toolbar no-print">${tabs.map(([k, l]) => `<a class="btn small ${active === k ? 'primary' : ''}" href="#/reports?view=${k}">${esc(l)}</a>`).join(' ')}</div>`;

  if (catalog.error) { page.innerHTML = head('catalog') + errorBlock(catalog.error, 'reports'); return; }

  if (view === 'saved') return savedView(page, ctx, state, head);
  if (view === 'changes' && canHistory) return changesView(page, ctx, head);
  if (view === 'run') {
    let reportId = ctx.query.get('report');
    let params = Object.fromEntries(FILTER_KEYS.map(k => [k, ctx.query.get(k) ?? '']));
    let definition = null;
    if (ctx.query.get('definition')) {
      definition = state.definitions.find(d => String(d.definitionId) === ctx.query.get('definition'));
      if (!definition) { page.innerHTML = head('saved') + emptyBlock('Saved report not found', 'It may have been deleted, or it is for a report you cannot run.'); return; }
      reportId = definition.reportId;
      params = { ...Object.fromEntries(FILTER_KEYS.map(k => [k, ''])), ...Object.fromEntries(Object.entries(definition.parameters).filter(([, v]) => v !== null)) };
    }
    const entry = state.catalog.find(e => e.id === reportId);
    if (!entry) { page.innerHTML = head('catalog') + emptyBlock('Report not available', 'This report does not exist or your roles do not include what it reports on.'); return; }
    return runView(page, ctx, state, head, entry, params, definition);
  }
  return catalogView(page, state, head);
}

// ------------------------------------------------------------------ catalog and saved

function catalogView(page, state, head) {
  const byCategory = Object.groupBy ? Object.groupBy(state.catalog, e => e.category) : state.catalog.reduce((a, e) => ((a[e.category] ??= []).push(e), a), {});
  page.innerHTML = head('catalog') + (!state.catalog.length
    ? emptyBlock('No reports for your roles', 'Each report needs read access to what it reports on.')
    : Object.entries(byCategory).map(([cat, list]) => `
      <section class="panel"><header><h2>${esc(cat)}</h2></header>
        <div class="report-grid">${list.map(e => `
          <a class="report-card" href="#/reports?report=${encodeURIComponent(e.id)}&groupBy=${encodeURIComponent(e.defaultGroupBy ?? '')}">
            <b>${esc(e.title)}</b><span>${esc(e.description)}</span>
            <small class="muted">Up to ${e.maxDays} days · group by ${e.groupBy.map(g => esc(e.columns.find(c => c.key === g)?.title ?? g)).join(', ')}</small>
          </a>`).join('')}</div>
      </section>`).join(''));
}

function savedView(page, ctx, state, head) {
  const list = state.definitions;
  page.innerHTML = head('saved') + `<section class="panel">
    ${!list.length ? emptyBlock('No saved reports', 'Run a standard report, set its filters, then choose Save to keep it here. Shared reports are listed for everyone; each person still sees only rows their access allows.')
      : `<div class="table-wrap"><table class="table"><thead><tr><th>Name</th><th>Report</th><th>Period</th><th>Filters</th><th>Owner</th><th></th></tr></thead><tbody>
        ${list.map(d => `<tr>
          <td><a href="#/reports?definition=${d.definitionId}"><b>${esc(d.name)}</b></a>${d.shared ? ' <span class="pill info">Shared</span>' : ''}</td>
          <td>${esc(d.reportTitle)}</td>
          <td>${d.parameters.lastDays ? `Last ${d.parameters.lastDays} days` : d.parameters.from ? `${esc(fmtDate(d.parameters.from))} – ${esc(fmtDate(d.parameters.to ?? d.parameters.from))}` : 'Last 30 days'}</td>
          <td class="muted">${esc(describe(d.parameters, state)) || '—'}</td>
          <td>${esc(who(d.owner))}</td>
          <td>${d.mine ? `<button class="btn small" data-delete="${d.definitionId}">Delete</button>` : ''}</td></tr>`).join('')}
        </tbody></table></div>`}</section>`;
  $$('[data-delete]', page).forEach(b => b.addEventListener('click', () => {
    const d = list.find(x => x.definitionId === Number(b.dataset.delete));
    confirmWithReason({ title: `Delete “${d.name}”?`, detail: 'Only the definition is deleted; no data changes.', confirmLabel: 'Delete', reasonRequired: false },
      async reason => { await command(`/api/reports/definitions/${d.definitionId}/delete`, {}, reason); toast('Deleted.'); location.hash = '#/reports?view=saved&r=' + Date.now(); });
  }));
}

function describe(p, state) {
  const parts = [];
  if (p.programId) parts.push(`program ${state.programs.find(x => x.programId === p.programId)?.code ?? '…'}`);
  for (const [k, l] of [['workCenter', 'work center'], ['machine', 'machine'], ['partNumber', 'part'], ['shift', 'shift'], ['status', 'status'], ['search', 'contains']]) if (p[k]) parts.push(`${l} ${p[k]}`);
  if (p.groupBy) parts.push(`by ${p.groupBy}`);
  return parts.join(' · ');
}

// ------------------------------------------------------------------ run a report

async function runView(page, ctx, state, head, entry, params, definition) {
  const has = key => entry.filters.some(f => f.key === key);
  const statusFilter = entry.filters.find(f => f.key === 'status');
  const statuses = statusFilter ? statusFilter.kind.replace(/^choice:/, '').split('|') : [];
  const q = queryOf(params);
  const result = await tryGet(`/api/reports/${encodeURIComponent(entry.id)}?${q}`);
  const r = result.data;
  const col = key => entry.columns.find(c => c.key === key);
  const input = (key, label, attrs = '') => `<div class="field"><label>${label}</label><input class="input" name="${key}" value="${esc(params[key] ?? '')}" ${attrs}></div>`;

  page.innerHTML = head('catalog') + `
    <section class="panel no-print">
      <header><h2>${esc(entry.title)}</h2>${definition ? `<span class="pill info">${esc(definition.name)}</span>` : ''}<span class="spacer"></span>
        <button class="btn small" id="csv" ${r ? '' : 'disabled'}>CSV</button>
        <button class="btn small" id="xlsx" ${r ? '' : 'disabled'}>Excel</button>
        <button class="btn small" id="print" ${r ? '' : 'disabled'}>Print / PDF</button>
        <button class="btn small primary" id="save">${definition?.mine ? 'Save changes' : 'Save…'}</button></header>
      <p class="muted" style="margin:0 0 10px">${esc(entry.description)}</p>
      <form id="filters" class="report-filters">
        ${input('from', 'From', 'type="date"')}${input('to', 'To', 'type="date"')}
        ${input('lastDays', entry.forward ? 'Or next N days' : 'Or last N days', `type="number" min="1" max="${entry.maxDays}" placeholder="—"`)}
        ${has('programId') ? `<div class="field"><label>Program</label><select class="input" name="programId"><option value="">All you can see</option>${state.programs.map(p => `<option value="${p.programId}" ${params.programId === p.programId ? 'selected' : ''}>${esc(p.code)}</option>`).join('')}</select></div>` : ''}
        ${has('workCenter') ? input('workCenter', 'Work center') : ''}
        ${has('machine') ? input('machine', 'Machine') : ''}
        ${has('partNumber') ? input('partNumber', 'Part') : ''}
        ${has('shift') ? `<div class="field"><label>Shift</label><select class="input" name="shift"><option value="">Any</option>${state.shifts.map(x => `<option value="${esc(x.code)}" ${params.shift === x.code ? 'selected' : ''}>${esc(x.name)}</option>`).join('')}</select></div>` : ''}
        ${statuses.length ? `<div class="field"><label>Status</label><select class="input" name="status"><option value="">Any</option>${statuses.map(s => `<option ${params.status === s ? 'selected' : ''}>${esc(s)}</option>`).join('')}</select></div>` : ''}
        ${input('search', 'Contains')}
        <div class="field"><label>Group by</label><select class="input" name="groupBy"><option value="">No grouping</option>${entry.groupBy.map(g => `<option value="${g}" ${params.groupBy === g ? 'selected' : ''}>${esc(col(g)?.title ?? g)}</option>`).join('')}</select></div>
        <div class="field"><label>&nbsp;</label><button class="btn primary" type="submit">Run</button></div>
      </form>
    </section>
    ${result.error ? errorBlock(result.error, 'this report') : resultHtml(r, entry)}`;

  const read = () => Object.fromEntries(FILTER_KEYS.map(k => [k, $(`[name="${k}"]`, page)?.value?.trim() ?? '']));
  $('#filters', page).addEventListener('submit', e => {
    e.preventDefault();
    const next = queryOf(read());
    next.set('report', entry.id);
    if (definition) next.set('definition', definition.definitionId);
    // Running a saved definition with new filters shows them without changing it until Save.
    location.hash = definition ? `#/reports?view=run&report=${encodeURIComponent(entry.id)}&${queryOf(read())}` : `#/reports?${next}`;
  });
  const download = async format => {
    const res = await fetch(`/api/reports/${encodeURIComponent(entry.id)}/export?format=${format}&${q}`, { headers: authHeaders() });
    if (!res.ok) { toast(`Export failed (${res.status}).`, true); return; }
    const blob = await res.blob();
    const name = /filename="?([^";]+)"?/.exec(res.headers.get('Content-Disposition') ?? '')?.[1] ?? `${entry.id}.${format}`;
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob); a.download = name; document.body.append(a); a.click();
    setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
  };
  $('#csv', page).addEventListener('click', () => download('csv'));
  $('#xlsx', page).addEventListener('click', () => download('xlsx'));
  $('#print', page).addEventListener('click', () => window.print());
  $('#save', page).addEventListener('click', () => {
    const p = read();
    const parameters = Object.fromEntries(Object.entries(p).map(([k, v]) => [k, v === '' ? null : k === 'lastDays' ? Number(v) : v]));
    formModal({
      title: definition?.mine ? `Save “${definition.name}”` : 'Save report',
      detail: 'Saves the report with these filters and grouping. A fixed From/To period stays fixed; use “last N days” for a rolling period.',
      confirmLabel: 'Save',
      fields: [
        { name: 'name', label: 'Name', required: true, value: definition?.mine ? definition.name : '' },
        { name: 'shared', label: 'Sharing', type: 'check', text: 'Shared with everyone who can run this report', value: definition?.shared ?? false },
      ],
    }, (v, reason) => command('/api/reports/definitions', { definitionId: definition?.mine ? definition.definitionId : null, name: v.name, reportId: entry.id, parameters, shared: !!v.shared }, reason),
    res => { location.hash = `#/reports?definition=${res?.entityId ?? definition?.definitionId}`; });
  });
}

export function authHeaders() {
  const h = {};
  if (session.config?.authMode === 'Development' && session.subject) h['X-Forge-Dev-Subject'] = session.subject;
  if (session.token) h.Authorization = `Bearer ${session.token}`;
  return h;
}

function resultHtml(r, entry) {
  const cols = r.columns;
  const ratio = g => r.ratio && g.ratio !== null && g.ratio !== undefined ? `${fmtNumber(g.ratio, 1)}%` : '—';
  const sums = cols.filter(isNum);
  const groups = r.groups.length ? `
    <section class="panel"><header><h2>By ${esc(cols.find(c => c.key === r.groupBy)?.title ?? r.groupBy)}</h2></header>
      <div class="table-wrap"><table class="table"><thead><tr><th>${esc(cols.find(c => c.key === r.groupBy)?.title ?? '')}</th><th class="num">Rows</th>${sums.map(c => `<th class="num">${esc(c.title)}</th>`).join('')}${r.ratio ? `<th class="num">${esc(r.ratio.title)}</th>` : ''}</tr></thead>
      <tbody>${[...r.groups, r.total].map(g => `<tr class="${g === r.total ? 'total' : ''}"><td>${g === r.total ? '<b>Total</b>' : esc(g.key)}</td><td class="num">${g.rows}</td>${sums.map(c => `<td class="num">${fmtNumber(g.sums[c.key] ?? 0, c.kind === 'Hours' ? 1 : 2)}</td>`).join('')}${r.ratio ? `<td class="num"><b>${ratio(g)}</b></td>` : ''}</tr>`).join('')}</tbody></table></div>
    </section>` : '';
  return `
    <section class="panel report-print">
      <div class="print-only"><h1>${esc(r.title)}</h1></div>
      <div class="report-meta">${esc(fmtDate(r.from))} – ${esc(fmtDate(r.to))} (${esc(r.timeZone)}) · generated ${esc(fmtDateTime(r.generatedAt))} by ${esc(r.generatedBy)} · ${r.rows.length} row(s)${r.ratio && r.total.ratio != null ? ` · <b>${esc(r.ratio.title)}: ${ratio(r.total)}</b>` : ''}</div>
      ${r.summary.length ? `<ul class="report-summary">${r.summary.map(s => `<li>${esc(s)}</li>`).join('')}</ul>` : ''}
      <div class="muted small">Basis: ${esc(r.basis)}${r.sources.length ? ` Sources: ${esc(r.sources.join(', '))}.` : ''}</div>
    </section>
    ${groups}
    <section class="panel">
      ${!r.rows.length ? emptyBlock('No rows', 'Nothing in this period matches the filters.') : `
      <div class="table-wrap"><table class="table report-table"><thead><tr>${cols.map(c => `<th class="${isNum(c) ? 'num' : ''}">${esc(c.title)}</th>`).join('')}<th class="no-print">Source records</th></tr></thead>
      <tbody>${r.rows.map(row => `<tr>${cols.map((c, i) => `<td class="${isNum(c) ? 'num' : ''}">${cellHtml(c, row.cells[i])}</td>`).join('')}<td class="trace no-print">${row.trace.map(traceLink).join(' ')}</td></tr>`).join('')}</tbody>
      <tfoot><tr class="total"><td><b>Total</b></td>${cols.slice(1).map(c => `<td class="num">${isNum(c) ? `<b>${fmtNumber(r.total.sums[c.key] ?? 0, c.kind === 'Hours' ? 1 : 2)}</b>` : ''}</td>`).join('')}<td class="no-print"></td></tr></tfoot></table></div>`}
    </section>`;
}

// ------------------------------------------------------------------ what changed?

function localInput(ms) {
  const d = new Date(ms);
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

const CHANGE_TONE = { Later: 'bad', Earlier: 'ok', 'Status changed': 'warn', Added: 'info', Removed: 'none', Rescheduled: 'info', Unchanged: 'none' };

async function changesView(page, ctx, head) {
  const now = Date.now();
  const at1 = ctx.query.get('at1') ?? localInput(now - 7 * 864e5);
  const at2 = ctx.query.get('at2') ?? localInput(now);
  const all = ctx.query.get('all') === '1';
  const iso = v => new Date(v).toISOString();
  const res = await tryGet(`/api/planning/history/compare?at1=${encodeURIComponent(iso(at1))}&at2=${encodeURIComponent(iso(at2))}&includeUnchanged=${all}`);
  const c = res.data;
  const version = v => v ? `<a href="#/scheduling">version ${v.versionId}</a>, approved ${esc(fmtDateTime(v.approvedAt))}${v.approvedBy ? ` by ${esc(who(v.approvedBy))}` : ''}` : '<span class="muted">none</span>';
  const evidence = list => list.length ? `<ul class="evidence-list">${list.map(e => `<li><span class="muted">${e.at ? esc(fmtDateTime(e.at)) : ''}</span> <b>${esc(e.kind)}</b> — ${esc(e.text)} ${traceLink(e.trace)}</li>`).join('')}</ul>` : '';
  page.innerHTML = head('changes') + `
    <section class="panel no-print">
      <header><h2>What changed?</h2></header>
      <p class="muted" style="margin:0 0 10px">Compares the approved schedule in force at each moment, order by order, and lists what was recorded in between that bears on each change: downtime, constraint findings, shop-floor reports, NCRs${c?.auditIncluded ? ' and audit entries' : ''}.</p>
      <form id="cmp" class="report-filters">
        <div class="field"><label>From (${esc(timeZone() ?? 'local')})</label><input class="input" type="datetime-local" name="at1" value="${esc(at1)}"></div>
        <div class="field"><label>To</label><input class="input" type="datetime-local" name="at2" value="${esc(at2)}"></div>
        <label class="opt"><input type="checkbox" name="all" ${all ? 'checked' : ''}> Include unchanged orders</label>
        <div class="field"><label>&nbsp;</label><button class="btn primary" type="submit">Compare</button></div>
        <div class="field"><label>&nbsp;</label><button class="btn" type="button" id="print">Print / PDF</button></div>
      </form>
    </section>
    ${res.error ? errorBlock(res.error, 'the comparison') : `
    <section class="panel report-print">
      <div class="print-only"><h1>What changed? ${esc(fmtDateTime(c.at1))} → ${esc(fmtDateTime(c.at2))}</h1></div>
      <div class="kpis">
        ${[['Later', c.later, 'bad'], ['Earlier', c.earlier, 'ok'], ['Delivery status changed', c.statusChanged, 'warn'], ['Added', c.added, 'info'], ['Removed', c.removed, 'none'], ['Unchanged', c.unchanged, 'none']]
          .map(([l, n]) => `<div class="kpi"><div><div class="t">${l}</div><div class="v">${n}</div></div></div>`).join('')}
      </div>
      <p><b>${esc(c.headline)}</b></p>
      <div class="report-meta">Before: ${version(c.before)} · After: ${version(c.after)}</div>
      ${c.siteEvidence.length ? `<h3>Recorded in between</h3>${evidence(c.siteEvidence)}` : ''}
    </section>
    <section class="panel">
      ${!c.orders.length ? emptyBlock('No order changed', c.before?.versionId === c.after?.versionId ? 'The same schedule was in force at both moments.' : 'Every order kept its projection.') : c.orders.map(o => `
        <div class="change-card">
          <div class="change-head"><a href="#/orders/${o.orderNumber}"><b>Order ${o.orderNumber}</b></a> <span class="muted">${esc(o.partNumber)}</span>
            <span class="pill ${CHANGE_TONE[o.change] ?? 'none'}">${esc(o.change)}${o.shiftHours ? ` ${o.shiftHours > 0 ? '+' : ''}${fmtNumber(o.shiftHours, 1)} h` : ''}</span>
            <span class="spacer"></span><span class="muted small">${o.projectedBefore ? esc(fmtDateTime(o.projectedBefore)) : '—'} → ${o.projectedAfter ? esc(fmtDateTime(o.projectedAfter)) : '—'}</span></div>
          <div>${esc(o.explanation)}</div>
          ${o.operations.length ? `<ul class="op-changes">${o.operations.map(op => `<li>${esc(op.explanation)}</li>`).join('')}</ul>` : ''}
          ${o.evidence.length ? `<details ${o.evidence.length <= 3 ? 'open' : ''}><summary>Evidence (${o.evidence.length})</summary>${evidence(o.evidence)}</details>` : ''}
        </div>`).join('')}
    </section>`}`;
  $('#cmp', page).addEventListener('submit', e => {
    e.preventDefault();
    const f = e.target;
    location.hash = `#/reports?view=changes&at1=${encodeURIComponent(f.at1.value)}&at2=${encodeURIComponent(f.at2.value)}${f.all.checked ? '&all=1' : ''}`;
  });
  $('#print', page)?.addEventListener('click', () => window.print());
}
