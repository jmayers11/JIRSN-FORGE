import { tryGet, command } from '../api.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast, tabifyPanels } from '../ui.js';
import { formModal, canAction } from '../forms.js';
import { who } from '../people.js';

// Demand forecasting and the V5 allocation optimizer (owner decisions 2026-11-02). Both propose; a planner (or inventory controller, for
// allocations) approves. Simulated history is marked everywhere it is used.

const METHOD = { Naive: 'Last month', SeasonalNaive: 'Same month last year', MovingAverage3: '3-month average', ExponentialSmoothing: 'Smoothed average', Croston: 'Sparse demand (Croston)' };
const MODE = { Primary: ['ok', 'Stock of the part'], AutomaticAlternate: ['warn', 'Approved alternate (automatic)'], ReviewAlternate: ['bad', 'Approved alternate (review first)'] };
const RULES = [['DueDate', 'Earliest due date'], ['Priority', 'Highest priority'], ['SmallestShortage', 'Smallest shortage'], ['ReleasedFirst', 'Released first']];
const n = v => Number(v ?? 0).toLocaleString(undefined, { maximumFractionDigits: 2 });

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

export function forecastHtml(view, history, { canApprove = false } = {}) {
  const lines = view?.lines ?? [];
  if (!view?.run) return emptyBlock('No forecast yet', 'Generate or import history, then run a forecast.');
  const byPart = new Map((history ?? []).map(h => [h.partNumber, h]));
  return `${view.run.simulated ? '<p><span class="pill bad">Simulated history</span> This forecast is built on simulated history, not real demand. Use it to learn how forecasting works; clear it once real history exists.</p>' : ''}
    <p class="muted">Forecast ${view.run.runId}, ${esc(fmtDateTime(view.run.runAt))} by ${esc(who(view.run.runBy))}: ${view.run.horizon} months from ${esc(String(view.run.firstMonth).slice(0, 7))}, each method tested on the last ${view.run.holdout} months, ${view.run.intervalPercent} % range.</p>
    <table class="table"><thead><tr><th>Part</th><th>History and forecast</th><th>Method (tested best)</th><th>Test error</th><th>Next months</th><th>Watch out</th><th></th></tr></thead><tbody>
    ${lines.map(l => { const f = l.forecast; const best = (f.scores ?? []).find(s => s.method === f.method); return `<tr>
      <td class="mono">${esc(f.partNumber)}${byPart.get(f.partNumber)?.description ? `<div class="faint">${esc(byPart.get(f.partNumber).description)}</div>` : ''}${l.simulated ? ' <span class="pill bad">Simulated</span>' : ''}</td>
      <td>${sparkline(byPart.get(f.partNumber)?.series ?? [], f.months ?? [])}</td>
      <td>${esc(METHOD[f.method] ?? f.method)}<div class="faint">${(f.scores ?? []).filter(s => s.method !== f.method).map(s => `${esc(METHOD[s.method] ?? s.method)} ${n(s.meanAbsoluteError)}`).join(' · ')}</div></td>
      <td>${best ? `±${n(best.meanAbsoluteError)} a month${best.weightedPercentError != null ? ` (${n(best.weightedPercentError)} %)` : ''}<div class="faint">${best.bias > 0 ? 'runs high' : best.bias < 0 ? 'runs low' : 'no bias'} by ${n(Math.abs(best.bias))}</div>` : '—'}</td>
      <td>${(f.months ?? []).map(m => `<div>${esc(String(m.month).slice(0, 7))}: <b>${n(m.forecast)}</b> <span class="faint">${n(m.low)}–${n(m.high)}</span></div>`).join('')}</td>
      <td>${(f.flags ?? []).map(x => `<div class="faint">${esc(x.message)}</div>`).join('') || '—'}</td>
      <td>${l.approvedAt ? `<span class="pill ok">Approved</span><div class="faint">${esc(who(l.approvedBy))}</div>` : canApprove ? `<button class="btn small primary" data-approve="${esc(f.partNumber)}">Approve as demand</button>` : ''}</td></tr>`; }).join('')}</tbody></table>`;
}

export function proposalsHtml(proposals, { canApprove = false } = {}) {
  if (!proposals?.length) return emptyBlock('Nothing to allocate', 'No released order is short of material that free stock or an approved alternate could cover.');
  return proposals.map(p => `<section class="panel"><header><h3 class="mono">${esc(p.partNumber)}</h3><span class="spacer"></span>
      <span class="faint">on hand ${n(p.onHand)} · not usable ${n(p.unavailable)} · reserved for others ${n(p.reservedForOthers)} · free ${n(p.free)}</span></header>
    ${p.lines.length ? `<table class="table"><thead><tr><th>Rank</th><th>Order</th><th>Short</th><th>Proposed</th><th>Why</th><th></th></tr></thead><tbody>
      ${p.lines.map(l => `<tr><td>${l.rank}</td><td class="mono">${l.orderNumber}<div class="faint">due ${esc(l.dueDate)} · priority ${l.priority}</div></td><td>${n(l.shortage)} ${esc(l.neededPart)}</td>
        <td><b>${n(l.quantity)} ${esc(l.offeredPart)}</b>${l.offeredPart !== l.neededPart ? `<div class="faint">covers ${n(l.primaryQuantity)} ${esc(l.neededPart)}</div>` : ''} <span class="pill ${MODE[l.mode]?.[0] ?? 'none'}">${esc(MODE[l.mode]?.[1] ?? l.mode)}</span></td>
        <td class="muted">${esc(l.why)}</td>
        <td>${canApprove ? `<button class="btn small primary" data-allocate="${l.orderNumber}" data-part="${esc(l.offeredPart)}" data-needed="${esc(l.neededPart)}" data-quantity="${l.quantity}" data-mode="${esc(l.mode)}">Approve</button>` : ''}</td></tr>`).join('')}</tbody></table>` : ''}
    ${p.stillShort.length ? `<p><b>Still short:</b> ${p.stillShort.map(g => `order ${g.orderNumber} ${n(g.missing)} (${esc(g.why)})`).join('; ')}</p>` : ''}
    ${p.ruledOut.length ? `<details><summary class="faint">Ruled out (${p.ruledOut.length})</summary>${p.ruledOut.map(r => `<div class="faint">${esc(r)}</div>`).join('')}</details>` : ''}</section>`).join('');
}

export function historyHtml(history) {
  if (!history?.length) return emptyBlock('No demand history', 'Shipments count automatically. Import months from another system, or generate simulated history to try forecasting.');
  return `<table class="table"><thead><tr><th>Part</th><th>Months</th><th>Total</th><th>From</th><th>Checks</th></tr></thead><tbody>
    ${history.map(h => `<tr><td class="mono">${esc(h.partNumber)}</td><td>${h.months}</td><td>${n(h.total)}</td>
      <td>${h.simulatedMonths ? `<span class="pill bad">${h.simulatedMonths} simulated</span> ` : ''}${h.importedMonths ? `${h.importedMonths} imported ` : ''}${h.shippedMonths ? `${h.shippedMonths} shipped` : ''}</td>
      <td>${(h.flags ?? []).map(f => `<div class="faint">${esc(f.message)}</div>`).join('') || '<span class="pill ok">OK</span>'}</td></tr>`).join('')}</tbody></table>`;
}

export async function forecastPage(page, ctx) {
  const me = ctx?.me;
  const can = { manage: canAction(me, 'planning.forecast.manage'), approve: canAction(me, 'planning.demand.manage'), allocate: canAction(me, 'planning.allocation.approve') };
  const load = async () => {
    const [view, history, proposals, policy] = await Promise.all([tryGet('/api/forecasting/latest'), tryGet('/api/forecasting/history'), tryGet('/api/allocation/proposals'), tryGet('/api/allocation/policy')]);
    const fp = view.data?.policy ?? {};
    const rules = (policy.data?.rules ?? []).map(r => RULES.find(x => x[0] === r)?.[1] ?? r).join(', then ');
    page.innerHTML = `
      <div class="page-head"><div><h1>Forecast &amp; Allocation</h1><div class="sub">Demand forecasts and material allocation proposals. Both are suggestions: nothing is planned or reserved until a person approves it.</div></div>
        <div class="spacer"></div>${can.manage ? '<button class="btn primary" data-run>Run forecast</button>' : ''}</div>
      <section class="panel"><header><h2>Forecast</h2></header>${view.error ? errorBlock(view.error, 'the forecast') : forecastHtml(view.data, history.data, { canApprove: can.approve })}</section>
      <section class="panel"><header><h2>Allocation</h2>${can.allocate ? '<span class="spacer"></span><button class="btn small" data-rules>Ranking rules</button>' : ''}</header>
        <p class="muted">Free stock goes to released orders that are short, ranked by ${esc(rules || 'earliest due date, then highest priority, then smallest shortage')}. Stock reserved for other work is never taken. Approving a line reserves it.</p>
        ${proposals.error ? errorBlock(proposals.error, 'allocation proposals') : proposalsHtml(proposals.data, { canApprove: can.allocate })}</section>
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
      .map(([partNumber, month, quantity]) => ({ partNumber, month: `${month}-01`, quantity: Number(quantity) })) }, reason), load));
    $('[data-policy]', page)?.addEventListener('click', () => formModal({
      title: 'Forecast settings', confirmLabel: 'Save', reasonRequired: true,
      fields: [{ name: 'horizon', label: 'Months ahead', type: 'number', min: 1, max: 24, value: fp.horizon ?? 6 }, { name: 'holdout', label: 'Months held out to test', type: 'number', min: 1, max: 24, value: fp.holdout ?? 6 },
        { name: 'intervalPercent', label: 'Range', type: 'select', options: [['80', '80 %'], ['90', '90 %'], ['95', '95 %']], value: String(fp.intervalPercent ?? 80) },
        { name: 'useSimulated', label: 'Simulated history', type: 'check', text: 'Count simulated history (turn off once real history exists)', value: fp.useSimulated ?? true }],
    }, (v, reason) => command('/api/forecasting/policy', { horizon: Number(v.horizon), holdout: Number(v.holdout), intervalPercent: Number(v.intervalPercent), useSimulated: !!v.useSimulated }, reason), load));
    for (const b of $$('[data-approve]', page)) b.addEventListener('click', () => confirmWithReason({
      title: `Approve the ${b.dataset.approve} forecast as demand`, detail: 'Each month becomes forecast demand, less customer orders already due that month. MRP then plans it like any demand.', confirmLabel: 'Approve', reasonRequired: false,
    }, reason => command('/api/forecasting/approve', { runId: view.data.run.runId, partNumber: b.dataset.approve }, reason)).then(done));
    for (const b of $$('[data-allocate]', page)) b.addEventListener('click', () => confirmWithReason({
      title: `Reserve ${b.dataset.quantity} ${b.dataset.part} for order ${b.dataset.allocate}`,
      detail: b.dataset.mode === 'ReviewAlternate' ? 'This alternate is only suggested by the BOM line: confirm engineering\'s conditions hold for this order.' : 'The stock is reserved for the order.', confirmLabel: 'Approve', reasonRequired: b.dataset.mode === 'ReviewAlternate',
    }, reason => command('/api/allocation/approve', { orderNumber: Number(b.dataset.allocate), partNumber: b.dataset.part, quantity: Number(b.dataset.quantity),
      alternateFor: b.dataset.part !== b.dataset.needed ? b.dataset.needed : null }, reason)).then(done));
    $('[data-rules]', page)?.addEventListener('click', () => formModal({
      title: 'Allocation ranking rules', detail: 'Short orders are ranked by the first rule, ties by the next.', confirmLabel: 'Save', reasonRequired: true,
      fields: [1, 2, 3].map(i => ({ name: `r${i}`, label: `Rule ${i}`, type: 'select', blank: i === 1 ? undefined : '— none —', options: RULES, value: policy.data?.rules?.[i - 1] ?? '' })),
    }, (v, reason) => command('/api/allocation/policy', { rules: [v.r1, v.r2, v.r3].filter(Boolean) }, reason), load));
  };
  await load();
}
