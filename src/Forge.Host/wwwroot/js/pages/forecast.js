import { tryGet, command } from '../api.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast, tabifyPanels } from '../ui.js';
import { formModal, canAction } from '../forms.js';
import { who } from '../people.js';

// Demand forecasting and the V5 allocation optimizer (owner decisions 2026-11-02). Both propose; a planner (or inventory controller, for
// allocations) approves. Simulated history is marked everywhere it is used. Complex items: families and configurable items split by share,
// new items borrow a like item, planner adjustments sit on top. Allocation also uses leftover pieces (smallest that fits) and work in
// progress no demand is counting on.

const METHOD = { Naive: 'Last month', SeasonalNaive: 'Same month last year', MovingAverage3: '3-month average', ExponentialSmoothing: 'Smoothed average', Croston: 'Sparse demand (Croston)',
  SyntetosBoylan: 'Sparse demand (Syntetos-Boylan)', DampedTrend: 'Trend, flattening', HoltWinters: 'Trend and season (Holt-Winters)' };
const MODE = { Primary: ['ok', 'Stock of the part'], AutomaticAlternate: ['warn', 'Approved alternate (automatic)'], ReviewAlternate: ['bad', 'Approved alternate (review first)'], WorkInProgress: ['warn', 'Work in progress'] };
const RULES = [['DueDate', 'Earliest due date'], ['Priority', 'Highest priority'], ['SmallestShortage', 'Smallest shortage'], ['ReleasedFirst', 'Released first']];
const UNITS = [['Millimetre', 'mm'], ['Centimetre', 'cm'], ['Metre', 'm'], ['Inch', 'in'], ['Foot', 'ft']];
const ADJUST = { AddUnits: 'Add units', Percent: 'Change by %', EndOfLife: 'End of life' };
const n = v => Number(v ?? 0).toLocaleString(undefined, { maximumFractionDigits: 2 });
const month = v => esc(String(v ?? '').slice(0, 7));
const unit = u => UNITS.find(x => x[0] === u)?.[1] ?? u ?? '';
export const size = s => s ? `${[s.length, s.width, s.thickness].filter(v => v != null).map(n).join(' × ')} ${unit(s.unit)}` : '';
const isFamily = p => String(p ?? '').startsWith('family:');

/** A small line chart of history then forecast with its range. */
export function sparkline(history = [], months = []) {
  const values = [...history.map(h => h.quantity), ...months.flatMap(m => [m.high, m.low, m.forecast])];
  const max = Math.max(1, ...values);
  const total = history.length + months.length;
  if (!total) return '';
  const x = i => (i / Math.max(1, total - 1)) * 220;
  const y = v => 46 - (v / max) * 42;
  const hist = history.map((h, i) => `${x(i).toFixed(1)},${y(h.quantity).toFixed(1)}`).join(' ');
  const start = history.length;
  const fc = months.map((m, i) => `${x(start + i).toFixed(1)},${y(m.forecast).toFixed(1)}`).join(' ');
  const band = months.length ? `${months.map((m, i) => `${x(start + i).toFixed(1)},${y(m.high).toFixed(1)}`).join(' ')} ${[...months].reverse().map((m, i) => `${x(start + months.length - 1 - i).toFixed(1)},${y(m.low).toFixed(1)}`).join(' ')}` : '';
  return `<svg class="sparkline" viewBox="0 0 220 48" width="220" height="48" role="img" aria-label="History and forecast">
    ${band ? `<polygon points="${band}" fill="currentColor" opacity="0.12"></polygon>` : ''}<polyline points="${hist}" fill="none" stroke="currentColor" stroke-width="1.5"></polyline>
    ${fc ? `<polyline points="${fc}" fill="none" stroke="currentColor" stroke-width="1.5" stroke-dasharray="3 2"></polyline>` : ''}</svg>`;
}

const monthCell = m => `<div>${month(m.month)}: <b>${n(m.forecast)}</b> <span class="faint">${n(m.low)}–${n(m.high)}</span>${m.adjusted
  ? `<div class="faint">method ${n(m.statistical ?? m.forecast)} · ${esc(m.adjusted)}</div>` : ''}</div>`;

export function forecastHtml(view, history, { canApprove = false } = {}) {
  const lines = view?.lines ?? [];
  if (!view?.run) return emptyBlock('No forecast yet', 'Generate or import history, then run a forecast.');
  const byPart = new Map((history ?? []).map(h => [h.partNumber, h]));
  return `${view.run.simulated ? '<p><span class="pill bad">Simulated history</span> This forecast is built on simulated history, not real demand. Use it to learn how forecasting works; clear it once real history exists.</p>' : ''}
    <p class="muted">Forecast ${view.run.runId}, ${esc(fmtDateTime(view.run.runAt))} by ${esc(who(view.run.runBy))}: ${view.run.horizon} months from ${month(view.run.firstMonth)}, each method tested on the last ${view.run.holdout} months, ${view.run.intervalPercent} % range.</p>
    <table class="table"><thead><tr><th>Part</th><th>History and forecast</th><th>Method (tested best)</th><th>Test error</th><th>Next months</th><th>Watch out</th><th></th></tr></thead><tbody>
    ${lines.map(l => { const f = l.forecast; const best = (f.scores ?? []).find(s => s.method === f.method); const family = isFamily(f.partNumber); return `<tr${family ? ' class="muted"' : ''}>
      <td class="mono">${family ? `<span class="pill none">Family</span> ${esc(f.partNumber.slice(7))}` : esc(f.partNumber)}${byPart.get(f.partNumber)?.description ? `<div class="faint">${esc(byPart.get(f.partNumber).description)}</div>` : ''}${l.simulated ? ' <span class="pill bad">Simulated</span>' : ''}</td>
      <td>${sparkline(byPart.get(f.partNumber)?.series ?? [], f.months ?? [])}</td>
      <td>${esc(METHOD[f.method] ?? f.method)}${f.basis ? `<div><span class="pill warn">${esc(f.basis)}</span></div>` : ''}<div class="faint">${(f.scores ?? []).filter(s => s.method !== f.method).map(s => `${esc(METHOD[s.method] ?? s.method)} ${n(s.meanAbsoluteError)}`).join(' · ')}</div></td>
      <td>${best ? `±${n(best.meanAbsoluteError)} a month${best.weightedPercentError != null ? ` (${n(best.weightedPercentError)} %)` : ''}<div class="faint">${best.bias > 0 ? 'runs high' : best.bias < 0 ? 'runs low' : 'no bias'} by ${n(Math.abs(best.bias))}</div>` : '—'}</td>
      <td>${(f.months ?? []).map(monthCell).join('')}</td>
      <td>${(f.flags ?? []).map(x => `<div class="faint">${esc(x.message)}</div>`).join('') || '—'}</td>
      <td>${family ? '<span class="faint">Approve its parts</span>' : l.approvedAt ? `<span class="pill ok">Approved</span><div class="faint">${esc(who(l.approvedBy))}</div>` : canApprove ? `<button class="btn small primary" data-approve="${esc(f.partNumber)}">Approve as demand</button>` : ''}</td></tr>`; }).join('')}</tbody></table>`;
}

/** Like items, families and planner adjustments: what makes a forecast more than the part's own history. */
export function setupHtml(setup, { canManage = false } = {}) {
  const { analogs = [], families = [], adjustments = [] } = setup ?? {};
  const adj = a => a.kind === 'EndOfLife' ? 'nothing forecast' : a.kind === 'Percent' ? `${a.amount > 0 ? '+' : ''}${n(a.amount)} %` : `${a.amount > 0 ? '+' : ''}${n(a.amount)} units`;
  return `<h3>New items</h3>${analogs.length ? `<table class="table"><thead><tr><th>New part</th><th>Like</th><th>Scale</th><th>Launch</th></tr></thead><tbody>
      ${analogs.map(a => `<tr><td class="mono">${esc(a.partNumber)}</td><td class="mono">${esc(a.likePart)}</td><td>${a.scale != null ? `× ${n(a.scale)}` : '<span class="faint">from shared months</span>'}</td><td>${a.launchMonth ? month(a.launchMonth) : '—'}</td></tr>`).join('')}</tbody></table>`
      : '<p class="faint">No new items borrow a like item’s history.</p>'}
    <h3>Families and options</h3>${families.length ? `<table class="table"><thead><tr><th>Family</th><th>Follows</th><th>Members</th></tr></thead><tbody>
      ${families.map(f => `<tr><td><b>${esc(f.name)}</b> <span class="mono faint">${esc(f.code)}</span></td><td class="mono">${f.driverPart ? esc(f.driverPart) : '<span class="faint">its own total</span>'}</td>
        <td>${f.members.map(m => `<span class="mono">${esc(m.partNumber)}</span>${m.share != null ? ` ${f.driverPart ? `${n(m.share)} per unit` : `${n(m.share * 100)} %`}` : ' <span class="faint">recent mix</span>'}`).join(' · ')}</td></tr>`).join('')}</tbody></table>`
      : '<p class="faint">No families: every part is forecast on its own.</p>'}
    <h3>Planner adjustments</h3>${adjustments.length ? `<table class="table"><thead><tr><th>Part</th><th>Months</th><th>Change</th><th>Why</th><th></th></tr></thead><tbody>
      ${adjustments.map(a => `<tr><td class="mono">${esc(a.partNumber)}</td><td>${month(a.fromMonth)}${a.kind === 'EndOfLife' ? ' on' : a.toMonth && a.toMonth !== a.fromMonth ? ` to ${month(a.toMonth)}` : ''}</td>
        <td><span class="pill warn">${esc(ADJUST[a.kind] ?? a.kind)}</span> ${esc(adj(a))}</td><td class="muted">${esc(a.reason)}<div class="faint">${esc(who(a.setBy))}</div></td>
        <td>${canManage ? `<button class="btn small ghost" data-cancel-adjustment="${a.adjustmentId}">Cancel</button>` : ''}</td></tr>`).join('')}</tbody></table>`
      : '<p class="faint">No adjustments. Add one for a promotion, a known large order or a part that is ending.</p>'}`;
}

export function proposalsHtml(proposals, { canApprove = false } = {}) {
  if (!proposals?.length) return emptyBlock('Nothing to allocate', 'No released order is short of material that free stock, a leftover piece, work in progress or an approved alternate could cover.');
  return proposals.map(p => `<section class="panel"><header><h3 class="mono">${esc(p.partNumber)}</h3><span class="spacer"></span>
      <span class="faint">on hand ${n(p.onHand)} · not usable ${n(p.unavailable)} · reserved for others ${n(p.reservedForOthers)} · free ${n(p.free)}</span></header>
    ${p.lines.length ? `<table class="table"><thead><tr><th>Rank</th><th>Order</th><th>Short</th><th>Proposed</th><th>Why</th><th></th></tr></thead><tbody>
      ${p.lines.map(l => `<tr><td>${l.rank}</td><td class="mono">${l.orderNumber}<div class="faint">due ${esc(l.dueDate)} · priority ${l.priority}</div></td><td>${n(l.shortage)} ${esc(l.neededPart)}</td>
        <td><b>${n(l.quantity)} ${esc(l.offeredPart)}</b>${l.offeredPart !== l.neededPart ? `<div class="faint">covers ${n(l.primaryQuantity)} ${esc(l.neededPart)}</div>` : ''} <span class="pill ${MODE[l.mode]?.[0] ?? 'none'}">${esc(MODE[l.mode]?.[1] ?? l.mode)}</span>
          ${l.wipOrderNumber ? `<div class="faint">from order ${l.wipOrderNumber}</div>` : ''}${l.lotNumber ? `<div class="faint">lot ${esc(l.lotNumber)}</div>` : ''}</td>
        <td class="muted">${esc(l.why)}</td>
        <td>${canApprove ? `<button class="btn small primary" data-allocate="${l.orderNumber}" data-part="${esc(l.offeredPart)}" data-needed="${esc(l.neededPart)}" data-quantity="${l.quantity}" data-mode="${esc(l.mode)}"${l.wipOrderNumber ? ` data-wip="${l.wipOrderNumber}"` : ''}${l.lotNumber ? ` data-lot="${esc(l.lotNumber)}"` : ''}>${l.mode === 'WorkInProgress' ? 'Promise' : 'Approve'}</button>` : ''}</td></tr>`).join('')}</tbody></table>` : ''}
    ${p.promised?.length ? `<p class="faint">Already promised from work in progress: ${p.promised.map(w => `${n(w.quantity - w.fulfilled)} from order ${w.wipOrderNumber} to order ${w.consumerOrderNumber}`).join('; ')}.</p>` : ''}
    ${p.stillShort.length ? `<p><b>Still short:</b> ${p.stillShort.map(g => `order ${g.orderNumber} ${n(g.missing)} (${esc(g.why)})`).join('; ')}</p>` : ''}
    ${p.ruledOut.length ? `<details><summary class="faint">Ruled out (${p.ruledOut.length})</summary>${p.ruledOut.map(r => `<div class="faint">${esc(r)}</div>`).join('')}</details>` : ''}</section>`).join('');
}

/** Work in progress, leftover pieces and minimum piece sizes the optimizer uses. */
export function sourcesHtml(sources, { canApprove = false } = {}) {
  const { workInProgress = [], promised = [], setAside = [], measured = [], pieceSizes = [] } = sources ?? {};
  return `<h3>Work in progress</h3>${workInProgress.length ? `<table class="table"><thead><tr><th>Order</th><th>Part</th><th>Will make</th><th>Counted on by</th><th>Promised</th><th>Unpegged</th><th>Still to run</th></tr></thead><tbody>
      ${workInProgress.map(w => `<tr><td class="mono">${w.orderNumber}<div class="faint">${esc(w.status)} · due ${esc(w.dueDate)}</div></td><td class="mono">${esc(w.partNumber)} <span class="faint">rev ${esc(w.revision)}</span></td><td>${n(w.projected)}</td>
        <td>${w.peggedTo.length ? w.peggedTo.map(x => `<div class="faint">${esc(x)}</div>`).join('') : '—'}</td><td>${n(w.promised)}</td><td>${w.unpegged > 0 ? `<span class="pill ok">${n(w.unpegged)}</span>` : '0'}</td>
        <td class="faint">${w.remainingOperations.length ? w.remainingOperations.map(esc).join('; ') : 'done, waiting to be received'}</td></tr>`).join('')}</tbody></table>`
      : '<p class="faint">No open production orders.</p>'}
    ${promised.length ? `<h3>Promised output</h3><table class="table"><thead><tr><th>From order</th><th>To order</th><th>Part</th><th>Still to come</th><th></th></tr></thead><tbody>
      ${promised.map(w => `<tr><td class="mono">${w.wipOrderNumber}</td><td class="mono">${w.consumerOrderNumber}</td><td class="mono">${esc(w.partNumber)}</td><td>${n(w.quantity - w.fulfilled)} of ${n(w.quantity)}</td>
        <td>${canApprove ? `<button class="btn small ghost" data-release-promise="${w.allocationId}">Release</button>` : ''}</td></tr>`).join('')}</tbody></table>` : ''}
    <h3>Leftover pieces</h3>${measured.length ? `<table class="table"><thead><tr><th>Part</th><th>Lot</th><th>Size</th><th>Set aside for</th></tr></thead><tbody>
      ${measured.map(d => { const held = setAside.filter(e => e.partNumber === d.partNumber && e.lotNumber === d.lotNumber); return `<tr><td class="mono">${esc(d.partNumber)}</td><td class="mono">${esc(d.lotNumber)}</td><td>${esc(size(d.size))}</td>
        <td>${held.map(e => `order ${e.orderNumber} ${canApprove ? `<button class="btn small ghost" data-release-setaside="${e.earmarkId}">Release</button>` : ''}`).join(' ') || '—'}</td></tr>`; }).join('')}</tbody></table>`
      : '<p class="faint">No lots measured yet. Measure leftover pieces so jobs that need a minimum size can use them.</p>'}
    <h3>Minimum piece sizes</h3>${pieceSizes.length ? `<table class="table"><thead><tr><th>Job (parent part)</th><th>Component</th><th>At least</th></tr></thead><tbody>
      ${pieceSizes.map(r => `<tr><td class="mono">${esc(r.parentPart)}</td><td class="mono">${esc(r.componentPart)}</td><td>${esc(size(r.minimum))}</td></tr>`).join('')}</tbody></table>`
      : '<p class="faint">No minimum piece sizes: any free stock is offered by quantity.</p>'}`;
}

export function historyHtml(history) {
  if (!history?.length) return emptyBlock('No demand history', 'Shipments count automatically. Import months from another system, or generate simulated history to try forecasting.');
  return `<table class="table"><thead><tr><th>Part</th><th>Months</th><th>Total</th><th>From</th><th>Checks</th></tr></thead><tbody>
    ${history.map(h => `<tr><td class="mono">${esc(h.partNumber)}</td><td>${h.months}</td><td>${n(h.total)}</td>
      <td>${h.simulatedMonths ? `<span class="pill bad">${h.simulatedMonths} simulated</span> ` : ''}${h.importedMonths ? `${h.importedMonths} imported ` : ''}${h.shippedMonths ? `${h.shippedMonths} shipped` : ''}</td>
      <td>${(h.flags ?? []).map(f => `<div class="faint">${esc(f.message)}</div>`).join('') || '<span class="pill ok">OK</span>'}</td></tr>`).join('')}</tbody></table>`;
}

const num = v => v === '' || v == null ? null : Number(v);
const sizeFields = (label) => [{ name: 'length', label: `${label} length`, type: 'number' }, { name: 'width', label: `${label} width`, type: 'number' }, { name: 'thickness', label: `${label} thickness`, type: 'number' },
  { name: 'unit', label: 'Unit', type: 'select', options: UNITS, value: 'Inch' }];

export async function forecastPage(page, ctx) {
  const me = ctx?.me;
  const can = { manage: canAction(me, 'planning.forecast.manage'), approve: canAction(me, 'planning.demand.manage'), allocate: canAction(me, 'planning.allocation.approve'),
    measure: canAction(me, 'inventory.transact'), sizes: canAction(me, 'inventory.piece-sizes.manage') };
  const load = async () => {
    const [view, history, proposals, policy, setup, sources] = await Promise.all([tryGet('/api/forecasting/latest'), tryGet('/api/forecasting/history'), tryGet('/api/allocation/proposals'),
      tryGet('/api/allocation/policy'), tryGet('/api/forecasting/setup'), tryGet('/api/allocation/sources')]);
    const fp = view.data?.policy ?? {};
    const rules = (policy.data?.rules ?? []).map(r => RULES.find(x => x[0] === r)?.[1] ?? r).join(', then ');
    page.innerHTML = `
      <div class="page-head"><div><h1>Forecast &amp; Allocation</h1><div class="sub">Demand forecasts and material allocation proposals. Both are suggestions: nothing is planned, reserved or promised until a person approves it.</div></div>
        <div class="spacer"></div>${can.manage ? '<button class="btn primary" data-run>Run forecast</button>' : ''}</div>
      <section class="panel"><header><h2>Forecast</h2><span class="spacer"></span>
        ${can.manage ? '<button class="btn small" data-analog>New item like…</button> <button class="btn small" data-family>Family or options</button> <button class="btn small" data-adjust>Planner adjustment</button>' : ''}</header>
        ${view.error ? errorBlock(view.error, 'the forecast') : forecastHtml(view.data, history.data, { canApprove: can.approve })}
        ${setup.error ? errorBlock(setup.error, 'forecast setup') : setupHtml(setup.data, { canManage: can.manage })}</section>
      <section class="panel"><header><h2>Allocation</h2><span class="spacer"></span>${can.allocate ? '<button class="btn small" data-rules>Ranking rules</button> ' : ''}${can.measure ? '<button class="btn small" data-measure>Measure a lot</button> ' : ''}${can.sizes ? '<button class="btn small" data-piece>Minimum piece size</button>' : ''}</header>
        <p class="muted">Released orders that are short are ranked by ${esc(rules || 'earliest due date, then highest priority, then smallest shortage')}. Each gets, in turn: free stock (where the job needs a minimum piece size, the smallest lot that fits, leftover pieces first); output of open orders that no customer or forecast demand is counting on, which still runs every remaining operation and inspection; then approved alternates. Stock reserved or set aside for other work is never taken.</p>
        ${proposals.error ? errorBlock(proposals.error, 'allocation proposals') : proposalsHtml(proposals.data, { canApprove: can.allocate })}
        ${sources.error ? errorBlock(sources.error, 'work in progress and leftover pieces') : sourcesHtml(sources.data, { canApprove: can.allocate })}</section>
      <section class="panel"><header><h2>History &amp; settings</h2><span class="spacer"></span>
        ${can.manage ? '<button class="btn small" data-simulate>Generate simulated history</button> <button class="btn small ghost" data-clear>Clear simulated</button> <button class="btn small" data-import>Import history</button> <button class="btn small" data-policy>Forecast settings</button>' : ''}</header>
        <p class="muted">${fp.horizon ?? 6} months ahead, tested on the last ${fp.holdout ?? 6} months, ${fp.intervalPercent ?? 80} % range; simulated history ${fp.useSimulated === false ? 'is ignored' : 'counts'}.</p>
        ${history.error ? errorBlock(history.error, 'demand history') : historyHtml(history.data)}</section>`;
    tabifyPanels(page, [{ label: 'Forecast', match: ['forecast'] }, { label: 'Allocation', match: ['allocation'] }, { label: 'History & settings', match: ['history & settings', 'history &amp; settings'] }]);
    const done = r => { if (r) { toast(r.message ?? 'Done'); load(); } };
    $('[data-run]', page)?.addEventListener('click', () => command('/api/forecasting/run', {}).then(done).catch(e => toast(e.message, true)));
    $('[data-simulate]', page)?.addEventListener('click', () => formModal({
      title: 'Generate simulated history', detail: 'Made-up monthly demand for each finished part, so forecasting can be tried before real history exists. It is marked simulated everywhere and replaces any earlier simulated history.', confirmLabel: 'Generate',
      fields: [{ name: 'months', label: 'Months', type: 'number', min: 12, max: 60, value: 24 }, { name: 'seed', label: 'Seed (same seed, same history)', type: 'number', value: 2026 }],
    }, v => command('/api/forecasting/simulate', { months: Number(v.months || 24), seed: Number(v.seed || 2026) }), load));
    $('[data-clear]', page)?.addEventListener('click', () => confirmWithReason({ title: 'Clear simulated history', detail: 'Forecast demand already approved stays until a planner changes it.', confirmLabel: 'Clear' },
      reason => command('/api/forecasting/simulated/clear', {}, reason)).then(done));
    $('[data-import]', page)?.addEventListener('click', () => formModal({
      title: 'Import demand history', detail: 'One row per line: part, month (YYYY-MM), quantity. For example: ROLL-100, 2025-01, 120', confirmLabel: 'Import', reasonRequired: true,
      fields: [{ name: 'rows', label: 'Rows', type: 'textarea', required: true }],
    }, (v, reason) => command('/api/forecasting/import', { rows: String(v.rows).split(/\r?\n/).map(l => l.split(',').map(s => s.trim())).filter(c => c.length >= 3 && c[0])
      .map(([partNumber, m, quantity]) => ({ partNumber, month: `${m}-01`, quantity: Number(quantity) })) }, reason), load));
    $('[data-policy]', page)?.addEventListener('click', () => formModal({
      title: 'Forecast settings', confirmLabel: 'Save', reasonRequired: true,
      fields: [{ name: 'horizon', label: 'Months ahead', type: 'number', min: 1, max: 24, value: fp.horizon ?? 6 }, { name: 'holdout', label: 'Months held out to test', type: 'number', min: 1, max: 24, value: fp.holdout ?? 6 },
        { name: 'intervalPercent', label: 'Range', type: 'select', options: [['80', '80 %'], ['90', '90 %'], ['95', '95 %']], value: String(fp.intervalPercent ?? 80) },
        { name: 'useSimulated', label: 'Simulated history', type: 'check', text: 'Count simulated history (turn off once real history exists)', value: fp.useSimulated ?? true }],
    }, (v, reason) => command('/api/forecasting/policy', { horizon: Number(v.horizon), holdout: Number(v.holdout), intervalPercent: Number(v.intervalPercent), useSimulated: !!v.useSimulated }, reason), load));
    $('[data-analog]', page)?.addEventListener('click', () => formModal({
      title: 'New item like an existing one', detail: 'A new part with little history borrows the demand pattern of a like part until it has a year of its own. Leave "Like part" empty to stop borrowing.', confirmLabel: 'Save',
      fields: [{ name: 'partNumber', label: 'New part', required: true }, { name: 'likePart', label: 'Like part' }, { name: 'scale', label: 'Scale (1 = sells the same; empty works it out once both have sold)', type: 'number' },
        { name: 'launch', label: 'Launch month (YYYY-MM, optional)' }],
    }, v => command('/api/forecasting/analogs', { partNumber: v.partNumber, likePart: v.likePart || null, scale: num(v.scale), launchMonth: v.launch ? `${v.launch}-01` : null }), load));
    $('[data-family]', page)?.addEventListener('click', () => formModal({
      title: 'Family or options', detail: 'Forecast variants together and split by share, or set a driver part (the configurable item that sells) and each option follows it by its attach rate. One member per line: part, then optional share (percent of the family, or units per driver unit). No members removes the family.', confirmLabel: 'Save',
      fields: [{ name: 'code', label: 'Family code', required: true }, { name: 'name', label: 'Name' }, { name: 'driver', label: 'Driver part (optional)' }, { name: 'members', label: 'Members', type: 'textarea' }],
    }, v => command('/api/forecasting/families', { code: v.code, name: v.name || null, driverPart: v.driver || null,
      members: String(v.members ?? '').split(/\r?\n/).map(l => l.split(',').map(s => s.trim())).filter(c => c[0])
        .map(([partNumber, share]) => ({ partNumber, share: share ? (v.driver ? Number(share) : Number(share) / 100) : null })) }), load));
    $('[data-adjust]', page)?.addEventListener('click', () => formModal({
      title: 'Planner adjustment', detail: 'Something history cannot know: a promotion, a known large order, a part that is ending. It applies from the next run and is shown beside the method’s figure. If the large order is already entered as a customer order, it is counted already.', confirmLabel: 'Add', reasonRequired: true,
      fields: [{ name: 'partNumber', label: 'Part', required: true }, { name: 'kind', label: 'Change', type: 'select', options: Object.entries(ADJUST), value: 'AddUnits' },
        { name: 'amount', label: 'Units a month, or percent (+ or -)', type: 'number' }, { name: 'from', label: 'First month (YYYY-MM)', required: true }, { name: 'to', label: 'Last month (YYYY-MM, optional)' }],
    }, (v, reason) => command('/api/forecasting/adjustments', { partNumber: v.partNumber, kind: v.kind, amount: Number(v.amount || 0), fromMonth: `${v.from}-01`, toMonth: v.to ? `${v.to}-01` : null }, reason), load));
    for (const b of $$('[data-cancel-adjustment]', page)) b.addEventListener('click', () => confirmWithReason({ title: `Cancel adjustment ${b.dataset.cancelAdjustment}`, detail: 'It stops applying from the next forecast run.', confirmLabel: 'Cancel adjustment' },
      reason => command(`/api/forecasting/adjustments/${b.dataset.cancelAdjustment}/cancel`, {}, reason)).then(done));
    for (const b of $$('[data-approve]', page)) b.addEventListener('click', () => confirmWithReason({
      title: `Approve the ${b.dataset.approve} forecast as demand`, detail: 'Each month becomes forecast demand, less customer orders already due that month. MRP then plans it like any demand.', confirmLabel: 'Approve', reasonRequired: false,
    }, reason => command('/api/forecasting/approve', { runId: view.data.run.runId, partNumber: b.dataset.approve }, reason)).then(done));
    for (const b of $$('[data-allocate]', page)) b.addEventListener('click', () => confirmWithReason({
      title: b.dataset.wip ? `Promise ${b.dataset.quantity} ${b.dataset.part} from order ${b.dataset.wip} to order ${b.dataset.allocate}` : `Reserve ${b.dataset.quantity} ${b.dataset.part} for order ${b.dataset.allocate}${b.dataset.lot ? ` from lot ${b.dataset.lot}` : ''}`,
      detail: b.dataset.mode === 'ReviewAlternate' ? 'This alternate is only suggested by the BOM line: confirm engineering\'s conditions hold for this order.'
        : b.dataset.wip ? `Order ${b.dataset.wip} still runs every remaining operation and inspection; when its output is received, this much is reserved for order ${b.dataset.allocate}.`
          : b.dataset.lot ? `The stock is reserved for the order and lot ${b.dataset.lot} is set aside so no one else cuts it.` : 'The stock is reserved for the order.',
      confirmLabel: b.dataset.wip ? 'Promise' : 'Approve', reasonRequired: b.dataset.mode === 'ReviewAlternate' || !!b.dataset.wip,
    }, reason => command('/api/allocation/approve', { orderNumber: Number(b.dataset.allocate), partNumber: b.dataset.part, quantity: Number(b.dataset.quantity),
      alternateFor: b.dataset.part !== b.dataset.needed ? b.dataset.needed : null, wipOrderNumber: b.dataset.wip ? Number(b.dataset.wip) : null, lotNumber: b.dataset.lot || null }, reason)).then(done));
    for (const b of $$('[data-release-promise]', page)) b.addEventListener('click', () => confirmWithReason({ title: 'Release the promise', detail: 'The output is free for others again.', confirmLabel: 'Release' },
      reason => command(`/api/allocation/promises/${b.dataset.releasePromise}/release`, {}, reason)).then(done));
    for (const b of $$('[data-release-setaside]', page)) b.addEventListener('click', () => confirmWithReason({ title: 'Release the set-aside lot', detail: 'The order keeps its reservation; only this specific lot is freed.', confirmLabel: 'Release' },
      reason => command(`/api/allocation/set-asides/${b.dataset.releaseSetaside}/release`, {}, reason)).then(done));
    $('[data-measure]', page)?.addEventListener('click', () => formModal({
      title: 'Measure a lot', detail: 'Record the size of a leftover piece (or any lot) so jobs that need a minimum size can use it. Give the sizes that matter.', confirmLabel: 'Save',
      fields: [{ name: 'partNumber', label: 'Part', required: true }, { name: 'lotNumber', label: 'Lot', required: true }, ...sizeFields('Piece')],
    }, v => command('/api/allocation/lot-dimensions', { partNumber: v.partNumber, lotNumber: v.lotNumber, length: num(v.length), width: num(v.width), thickness: num(v.thickness), unit: v.unit }), load));
    $('[data-piece]', page)?.addEventListener('click', () => formModal({
      title: 'Minimum piece size', detail: 'The smallest piece of a component a job (parent part) can use. Length and width may be turned. Leave all sizes empty to clear.', confirmLabel: 'Save', reasonRequired: true,
      fields: [{ name: 'parentPart', label: 'Job (parent part)', required: true }, { name: 'componentPart', label: 'Component', required: true }, ...sizeFields('Minimum')],
    }, (v, reason) => command('/api/allocation/piece-sizes', { parentPart: v.parentPart, componentPart: v.componentPart, length: num(v.length), width: num(v.width), thickness: num(v.thickness), unit: v.unit }, reason), load));
    $('[data-rules]', page)?.addEventListener('click', () => formModal({
      title: 'Allocation ranking rules', detail: 'Short orders are ranked by the first rule, ties by the next.', confirmLabel: 'Save', reasonRequired: true,
      fields: [1, 2, 3].map(i => ({ name: `r${i}`, label: `Rule ${i}`, type: 'select', blank: i === 1 ? undefined : '— none —', options: RULES, value: policy.data?.rules?.[i - 1] ?? '' })),
    }, (v, reason) => command('/api/allocation/policy', { rules: [v.r1, v.r2, v.r3].filter(Boolean) }, reason), load));
  };
  await load();
}
