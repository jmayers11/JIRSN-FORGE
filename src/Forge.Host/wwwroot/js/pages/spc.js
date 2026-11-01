import { tryGet, command } from '../api.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast, tabifyPanels, drawer } from '../ui.js';
import { formModal, canAction } from '../forms.js';
import { who } from '../people.js';

// Statistical process control (owner decisions 2026-11-01): an individuals chart of every measured characteristic with the Western
// Electric rules, capability against the drawing limits (Cpk 1.33 required, 1.67 for critical characteristics, set by a Quality Manager),
// and a notice to quality engineers when a new measurement signals. Nothing is held automatically; a person decides.

const RULE = {
  BeyondThreeSigma: 'Beyond 3σ', TwoOfThreeBeyondTwoSigma: '2 of 3 beyond 2σ', FourOfFiveBeyondOneSigma: '4 of 5 beyond 1σ', EightOnOneSide: '8 on one side',
};
const num = (n, digits = 4) => n === null || n === undefined ? '—' : Number(n).toLocaleString('en-US', { maximumFractionDigits: digits });
const key = k => `${k.partNumber} op ${k.sequence} #${k.number}`;

/** Capable, not capable, or not yet known, as a pill. */
export function capabilityPill(capable, cpk, required) {
  if (capable === null || capable === undefined) return '<span class="pill none">Too few points</span>';
  return capable ? `<span class="pill ok">Cpk ${num(cpk, 2)} ≥ ${num(required, 2)}</span>` : `<span class="pill bad">Cpk ${num(cpk, 2)} &lt; ${num(required, 2)}</span>`;
}

/** The overview: one row per measured characteristic, open signals first. */
export function capabilityHtml(rows, { canManage = false } = {}) {
  if (!rows?.length) return emptyBlock('No measurements yet', 'Charts appear once inspections record measured (variable) characteristics.');
  return `<table class="table"><thead><tr><th>Part</th><th class="num">Op</th><th>Characteristic</th><th class="num">Points</th><th>Capability</th><th class="num">Rule breaks</th><th class="num">Open signals</th><th>Last</th><th></th></tr></thead><tbody>
    ${rows.map(r => `<tr><td class="mono">${esc(r.key.partNumber)}</td><td class="num">${r.key.sequence}</td><td>${esc(r.name)} <span class="faint">#${r.key.number}</span>${r.critical ? ' <span class="pill warn">Critical</span>' : ''}</td>
      <td class="num">${r.points}</td><td>${capabilityPill(r.capable, r.cpk, r.requiredCpk)}</td><td class="num">${r.violations}</td>
      <td class="num">${r.openSignals ? `<span class="pill bad">${r.openSignals}</span>` : '0'}</td><td>${r.last ? esc(fmtDate(r.last)) : '—'}</td>
      <td><button class="btn small" data-chart="${esc(r.key.partNumber)}|${r.key.sequence}|${r.key.number}">Chart</button>${canManage ? ` <button class="btn small" data-critical="${esc(r.key.partNumber)}|${r.key.sequence}|${r.key.number}" data-is="${r.critical}">${r.critical ? 'Not critical' : 'Mark critical'}</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

/** An individuals chart as SVG: the points in time order, centre line, control limits and drawing limits, rule breaks in red. */
export function iChartSvg(chart, { width = 720, height = 260 } = {}) {
  const pts = chart?.points ?? [];
  if (!pts.length) return '<div class="faint">No measurements.</div>';
  const lines = [
    ['UCL', chart.upperControlLimit, 'var(--bad, #c0392b)', '6 4'], ['CL', chart.mean, 'var(--muted, #888)', ''], ['LCL', chart.lowerControlLimit, 'var(--bad, #c0392b)', '6 4'],
    ['USL', chart.upperSpec, 'var(--warn, #b7791f)', '2 3'], ['LSL', chart.lowerSpec, 'var(--warn, #b7791f)', '2 3'],
  ].filter(([, v]) => v !== null && v !== undefined);
  const values = [...pts.map(p => Number(p.value)), ...lines.map(([, v]) => Number(v))];
  let lo = Math.min(...values), hi = Math.max(...values);
  if (hi === lo) { hi += 1; lo -= 1; }
  const pad = (hi - lo) * 0.08; lo -= pad; hi += pad;
  const left = 56, right = 44, top = 12, bottom = 24;
  const x = i => left + (pts.length === 1 ? (width - left - right) / 2 : i * (width - left - right) / (pts.length - 1));
  const y = v => top + (hi - Number(v)) * (height - top - bottom) / (hi - lo);
  const broken = new Set((chart.violations ?? []).map(v => v.pointIndex));
  const path = pts.map((p, i) => `${i ? 'L' : 'M'}${x(i).toFixed(1)},${y(p.value).toFixed(1)}`).join(' ');
  return `<svg class="spc-chart" viewBox="0 0 ${width} ${height}" role="img" aria-label="Individuals chart of ${esc(chart.name)}" style="width:100%;max-width:${width}px">
    ${lines.map(([label, v, color, dash]) => `<line x1="${left}" x2="${width - right}" y1="${y(v).toFixed(1)}" y2="${y(v).toFixed(1)}" stroke="${color}" stroke-dasharray="${dash}"/>
      <text x="${width - right + 4}" y="${(y(v) + 4).toFixed(1)}" font-size="11" fill="currentColor">${label}</text><text x="${left - 4}" y="${(y(v) + 4).toFixed(1)}" font-size="10" text-anchor="end" fill="currentColor">${esc(num(v))}</text>`).join('')}
    <path d="${path}" fill="none" stroke="currentColor" stroke-width="1.5"/>
    ${pts.map((p, i) => `<circle cx="${x(i).toFixed(1)}" cy="${y(p.value).toFixed(1)}" r="${broken.has(i) ? 4.5 : 3}" fill="${broken.has(i) ? 'var(--bad, #c0392b)' : 'currentColor'}" data-point="${i}"><title>${esc(fmtDateTime(p.at))} · order ${p.orderNumber} · ${esc(num(p.value))}</title></circle>`).join('')}
  </svg>`;
}

/** The chart's statistics, rule breaks and notes. */
export function chartDetailHtml(chart) {
  const stats = [['Mean', chart.mean], ['σ (MR̄/1.128)', chart.sigma], ['MR̄', chart.averageMovingRange], ['UCL', chart.upperControlLimit], ['LCL', chart.lowerControlLimit],
    ['MR UCL', chart.movingRangeUpperLimit], ['Cp', chart.cp], ['Cpk', chart.cpk]];
  return `${iChartSvg(chart)}
    <div class="inline">${capabilityPill(chart.capable, chart.cpk, chart.requiredCpk)} ${chart.critical ? '<span class="pill warn">Critical</span>' : ''}
      <span class="faint">Drawing ${esc(num(chart.lowerSpec))} – ${esc(num(chart.upperSpec))}${chart.unit ? ` ${esc(chart.unit)}` : ''}${chart.nominal !== null && chart.nominal !== undefined ? `, nominal ${esc(num(chart.nominal))}` : ''}</span></div>
    <table class="table"><tbody><tr>${stats.map(([l]) => `<th class="num">${esc(l)}</th>`).join('')}</tr><tr>${stats.map(([, v]) => `<td class="num">${esc(num(v))}</td>`).join('')}</tr></tbody></table>
    ${(chart.notes ?? []).map(n => `<div class="notice">${esc(n)}</div>`).join('')}
    ${(chart.violations ?? []).length ? `<h3>Rule breaks</h3><ul>${chart.violations.map(v => `<li>${esc(fmtDateTime(chart.points[v.pointIndex]?.at))} — ${esc(RULE[v.rule] ?? v.rule)}: ${esc(v.description)} <span class="faint">inspection ${v.inspectionId}</span></li>`).join('')}</ul>` : ''}`;
}

/** Signals waiting for a quality engineer, or the history. */
export function signalsHtml(signals, { canAcknowledge = false } = {}) {
  if (!signals?.length) return emptyBlock('No open signals', 'When a new measurement breaks a control-chart rule, quality engineers are told and it appears here.');
  return `<table class="table"><thead><tr><th>Detected</th><th>Characteristic</th><th>Rule</th><th class="num">Value</th><th>Order</th><th>Status</th><th></th></tr></thead><tbody>
    ${signals.map(s => `<tr><td>${esc(fmtDateTime(s.detectedAt))}</td><td>${esc(key(s.key))} ${esc(s.characteristicName)}</td><td>${esc(RULE[s.rule] ?? s.rule)}</td><td class="num">${esc(num(s.value))}</td>
      <td>${s.orderNumber}</td><td>${s.status === 'Open' ? '<span class="pill bad">Open</span>' : `<span class="pill ok">Acknowledged</span> <span class="faint">${esc(who(s.acknowledgedBy))}: ${esc(s.note)}</span>`}</td>
      <td>${canAcknowledge && s.status === 'Open' ? `<button class="btn small" data-ack="${s.signalId}">Acknowledge</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

export async function spcPage(page, ctx) {
  const me = ctx?.me;
  const can = { manage: canAction(me, 'quality.spc.manage'), acknowledge: canAction(me, 'quality.plan') };
  let showAll = false;
  const load = async () => {
    const [charts, signals, policy] = await Promise.all([tryGet('/api/quality/spc/charts'), tryGet(`/api/quality/spc/signals?open=${!showAll}`), tryGet('/api/quality/spc/policy')]);
    const p = policy.data?.policy;
    page.innerHTML = `
      <div class="page-head"><div><h1>Statistical Process Control</h1><div class="sub">Individuals charts of every measured characteristic, capability against the drawing, and signals when a new measurement breaks a rule. A signal tells quality engineers; it holds nothing.</div></div></div>
      <section class="panel"><header><h2>Signals</h2><span class="spacer"></span><label class="inline"><input type="checkbox" data-all ${showAll ? 'checked' : ''}> Show acknowledged</label></header>
        ${signals.error ? errorBlock(signals.error, 'signals') : signalsHtml(signals.data, { canAcknowledge: can.acknowledge })}</section>
      <section class="panel"><header><h2>Capability</h2></header>${charts.error ? errorBlock(charts.error, 'charts') : capabilityHtml(charts.data, { canManage: can.manage })}</section>
      <section class="panel"><header><h2>Policy</h2></header>
        ${policy.error ? errorBlock(policy.error, 'the SPC policy') : `<p>Required Cpk <b>${num(p.minimumCpk, 2)}</b>, <b>${num(p.criticalMinimumCpk, 2)}</b> for critical characteristics. Limits and capability after <b>${p.minimumPoints}</b> measurements. Notices to quality engineers are <b>${p.notifyOnSignal ? 'on' : 'off'}</b>.</p>
          <p class="faint">Cpk compares the process spread with the drawing tolerance: 1.33 means the nearest limit is four standard deviations from the average. ${p.setBy && p.setBy !== 'system:default' ? `Set by ${esc(who(p.setBy))} ${esc(fmtDateTime(p.effectiveFrom))}: ${esc(p.reason)}` : 'The default policy.'}</p>
          ${can.manage ? '<div class="actions"><button class="btn" data-policy>Change policy</button></div>' : ''}`}</section>`;
    tabifyPanels(page, [{ label: 'Signals', match: ['signals'] }, { label: 'Capability', match: ['capability'] }, { label: 'Policy', match: ['policy'] }]);

    $('[data-all]', page)?.addEventListener('change', e => { showAll = e.target.checked; load(); });
    for (const b of $$('[data-chart]', page)) b.addEventListener('click', async () => {
      const [part, sequence, number] = b.dataset.chart.split('|');
      const r = await tryGet(`/api/quality/spc/chart?part=${encodeURIComponent(part)}&sequence=${sequence}&number=${number}`);
      drawer(`${part} op ${sequence} — ${r.data?.name ?? `#${number}`}`, r.error ? errorBlock(r.error, 'the chart') : chartDetailHtml(r.data));
    });
    for (const b of $$('[data-critical]', page)) b.addEventListener('click', () => {
      const [partNumber, sequence, number] = b.dataset.critical.split('|');
      const critical = b.dataset.is !== 'true';
      confirmWithReason({ title: `${critical ? 'Mark' : 'Unmark'} ${partNumber} op ${sequence} #${number} critical`, detail: critical ? 'A critical characteristic must reach the higher Cpk.' : 'It goes back to the general Cpk requirement.' },
        reason => command('/api/quality/spc/critical', { partNumber, sequence: Number(sequence), number: Number(number), critical }, reason)).then(r => { if (r) { toast(r.message ?? 'Done'); load(); } });
    });
    for (const b of $$('[data-ack]', page)) b.addEventListener('click', () => formModal({
      title: `Acknowledge signal ${b.dataset.ack}`, detail: 'Say what you found and what was done. Whether to hold anything is your decision, made separately.', confirmLabel: 'Acknowledge',
      fields: [{ name: 'note', label: 'What was found and done', required: true }],
    }, v => command(`/api/quality/spc/signals/${b.dataset.ack}/acknowledge`, v), load));
    $('[data-policy]', page)?.addEventListener('click', () => formModal({
      title: 'SPC policy', confirmLabel: 'Save', reasonRequired: true,
      fields: [{ name: 'minimumCpk', label: 'Required Cpk', type: 'number', min: 0.5, max: 3, step: 0.01, required: true, value: p.minimumCpk },
        { name: 'criticalMinimumCpk', label: 'Required Cpk, critical characteristics', type: 'number', min: 0.5, max: 3, step: 0.01, required: true, value: p.criticalMinimumCpk },
        { name: 'minimumPoints', label: 'Measurements before limits are calculated', type: 'number', min: 5, max: 100, step: 1, required: true, value: p.minimumPoints },
        { name: 'notifyOnSignal', label: 'Tell quality engineers about signals', type: 'select', options: [['true', 'Yes'], ['false', 'No']], value: String(p.notifyOnSignal) }],
    }, (v, reason) => command('/api/quality/spc/policy', { ...v, minimumCpk: Number(v.minimumCpk), criticalMinimumCpk: Number(v.criticalMinimumCpk), minimumPoints: Number(v.minimumPoints), notifyOnSignal: String(v.notifyOnSignal) === 'true' }, reason), load));
  };
  await load();
}
