import { tryGet, command } from '../api.js';
import { esc, $, $$, fmtDate, errorBlock, emptyBlock, drawer } from '../ui.js';
import { formModal, canAction } from '../forms.js';

// Supplier scorecards (owner decisions 2026-11-01): raw measures with an A/B/C grade. On time is on or before the promised date unless the
// company's policy allows days late or limits how early counts. A grade never holds a supplier; a person decides.

const GRADE = { A: 'ok', B: 'warn', C: 'bad' };
const pct = n => n === null || n === undefined ? '—' : `${Number(n).toLocaleString('en-US', { maximumFractionDigits: 2 })}%`;
const n0 = n => n === null || n === undefined ? '—' : Number(n).toLocaleString('en-US', { maximumFractionDigits: 3 });

export const gradePill = g => g ? `<span class="pill ${GRADE[g] ?? 'none'}">${esc(g)}</span>` : '<span class="pill none">No receipts</span>';

/** In words, what counts as on time under a policy. */
export function onTimeRule(p) {
  const late = p.lateToleranceDays ? `up to ${p.lateToleranceDays} day(s) after the promised date` : 'on or before the promised date';
  const early = p.earlyLimitDays === null || p.earlyLimitDays === undefined ? '' : `, and no more than ${p.earlyLimitDays} day(s) early`;
  return `On time: received ${late}${early}.`;
}

export function scorecardHtml(report) {
  if (!report?.suppliers?.length) return emptyBlock('No supplier activity in this period', 'Purchase receipts, nonconformances on received lots and past-due lines appear here.');
  return `<table class="table"><thead><tr><th>Supplier</th><th>Grade</th><th class="num">Receipts</th><th class="num">On time</th><th class="num">Avg days late</th><th class="num">Received</th><th class="num">Rejected</th><th class="num">PPM</th><th class="num">Quality</th><th class="num">NCRs</th><th class="num">Past due lines</th><th></th></tr></thead><tbody>
    ${report.suppliers.map(s => `<tr><td><b>${esc(s.supplierName)}</b> <span class="faint mono">${esc(s.supplierCode)}</span></td><td>${gradePill(s.grade)}</td>
      <td class="num">${s.receipts}</td><td class="num">${pct(s.onTimePercent)} <span class="faint">(${s.onTimeReceipts}/${s.receipts})</span></td><td class="num">${n0(s.averageDaysLate)}</td>
      <td class="num">${n0(s.quantityReceived)}</td><td class="num">${n0(s.quantityRejected)}</td><td class="num">${n0(s.ppm)}</td><td class="num">${pct(s.qualityPercent)}</td><td class="num">${s.nonconformances}</td>
      <td class="num">${s.pastDueLines ? `<span class="pill warn">${s.pastDueLines}</span>` : '0'}</td>
      <td>${s.late.length || s.rejections.length ? `<button class="btn small" data-detail="${esc(s.supplierCode)}">Details</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

export function supplierDetailHtml(s) {
  return `${s.late.length ? `<h3>Not on time</h3><table class="table"><thead><tr><th>PO line</th><th>Part</th><th class="num">Qty</th><th>Promised</th><th>Received</th><th class="num">Days</th></tr></thead><tbody>
      ${s.late.map(r => `<tr><td>${r.poNumber}/${r.lineNumber}</td><td class="mono">${esc(r.partNumber)}</td><td class="num">${n0(r.quantity)}</td><td>${esc(fmtDate(r.promised))}</td><td>${esc(fmtDate(r.received))}</td>
        <td class="num">${r.daysLate > 0 ? `${r.daysLate} late` : `${-r.daysLate} early`}</td></tr>`).join('')}</tbody></table>` : ''}
    ${s.rejections.length ? `<h3>Nonconformances on received lots</h3><table class="table"><thead><tr><th>NCR</th><th>Part</th><th>Lot</th><th class="num">Qty</th><th>Defect</th><th>Raised</th></tr></thead><tbody>
      ${s.rejections.map(r => `<tr><td>${r.ncrId}</td><td class="mono">${esc(r.partNumber)}</td><td class="mono">${esc(r.lotNumber)}</td><td class="num">${n0(r.quantity)}</td><td>${esc(r.defect)}</td><td>${esc(fmtDate(r.raisedAt))}</td></tr>`).join('')}</tbody></table>` : ''}`;
}

export async function supplierScorecardPage(page, ctx) {
  const canManage = canAction(ctx?.me, 'procurement.approve');
  let days = null;
  const load = async () => {
    const r = await tryGet(`/api/procurement/scorecard${days ? `?days=${days}` : ''}`);
    const report = r.data;
    const p = report?.policy;
    page.innerHTML = `
      <div class="page-head"><div><h1>Supplier Scorecards</h1><div class="sub">Delivery and quality by supplier with an A/B/C grade. The grade informs a decision; holding a supplier is a person's call.</div></div></div>
      <section class="panel"><header><h2>Scorecard</h2><span class="spacer"></span>
        <label class="inline">Period <select data-days>${[30, 90, 180, 365].map(d => `<option value="${d}" ${(days ?? p?.periodDays) === d ? 'selected' : ''}>${d} days</option>`).join('')}</select></label></header>
        ${r.error ? errorBlock(r.error, 'supplier scorecards') : `<div class="faint">${esc(fmtDate(report.from))} – ${esc(fmtDate(report.to))}. ${esc(onTimeRule(p))} A: on time ≥ ${pct(p.gradeAOnTimePercent)} and quality ≥ ${pct(p.gradeAQualityPercent)}; B: ≥ ${pct(p.gradeBOnTimePercent)} and ≥ ${pct(p.gradeBQualityPercent)}; otherwise C.</div>
          ${scorecardHtml(report)}${(report.notes ?? []).map(n => `<div class="faint">${esc(n)}</div>`).join('')}
          ${canManage ? '<div class="actions"><button class="btn" data-policy>Change scorecard policy</button></div>' : ''}`}</section>`;
    $('[data-days]', page)?.addEventListener('change', e => { days = Number(e.target.value); load(); });
    for (const b of $$('[data-detail]', page)) b.addEventListener('click', () => {
      const s = report.suppliers.find(x => x.supplierCode === b.dataset.detail);
      drawer(`${s.supplierName} — details`, supplierDetailHtml(s));
    });
    $('[data-policy]', page)?.addEventListener('click', () => formModal({
      title: 'Scorecard policy', detail: 'What counts as on time, and where grades fall. Earlier versions are kept.', confirmLabel: 'Save', reasonRequired: true,
      fields: [{ name: 'lateToleranceDays', label: 'Days late still on time', type: 'number', min: 0, max: 30, step: 1, required: true, value: p.lateToleranceDays },
        { name: 'earlyLimitDays', label: 'Days early still on time (blank: any)', type: 'number', min: 0, max: 365, step: 1, value: p.earlyLimitDays ?? '' },
        { name: 'gradeAOnTimePercent', label: 'A: on time at least (%)', type: 'number', min: 0, max: 100, step: 0.1, required: true, value: p.gradeAOnTimePercent },
        { name: 'gradeAQualityPercent', label: 'A: quality at least (%)', type: 'number', min: 0, max: 100, step: 0.01, required: true, value: p.gradeAQualityPercent },
        { name: 'gradeBOnTimePercent', label: 'B: on time at least (%)', type: 'number', min: 0, max: 100, step: 0.1, required: true, value: p.gradeBOnTimePercent },
        { name: 'gradeBQualityPercent', label: 'B: quality at least (%)', type: 'number', min: 0, max: 100, step: 0.01, required: true, value: p.gradeBQualityPercent },
        { name: 'periodDays', label: 'Default period (days)', type: 'number', min: 7, max: 730, step: 1, required: true, value: p.periodDays }],
    }, (v, reason) => command('/api/procurement/scorecard/policy', {
      lateToleranceDays: Number(v.lateToleranceDays), earlyLimitDays: v.earlyLimitDays === '' || v.earlyLimitDays === null || v.earlyLimitDays === undefined ? null : Number(v.earlyLimitDays),
      gradeAOnTimePercent: Number(v.gradeAOnTimePercent), gradeAQualityPercent: Number(v.gradeAQualityPercent), gradeBOnTimePercent: Number(v.gradeBOnTimePercent),
      gradeBQualityPercent: Number(v.gradeBQualityPercent), periodDays: Number(v.periodDays) }, reason), load));
  };
  await load();
}
