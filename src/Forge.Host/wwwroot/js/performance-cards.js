// Employee review cards (Training & Performance Command Center → Employees). Every figure comes from /api/workforce/performance-cards,
// which gathers the records Forge already keeps: cause-adjusted labor and first-pass yield, verified attendance, coverage goals and the
// competency register. The colors only say where a figure sits against the published thresholds below and why; they never rank people,
// and a figure without enough verified evidence stays gray instead of turning red.
import { esc, fmtNumber } from './ui.js';

/** Published grading thresholds. Green at or above `good`, yellow at or above `watch`, red below. */
export const THRESHOLDS = Object.freeze({
  efficiency: { good: 95, watch: 85, minimumHours: 8, label: 'Labor efficiency' },
  firstPass: { good: 98, watch: 95, label: 'First-pass yield' },
  attendance: { good: 95, watch: 90, minimumArrivals: 5, label: 'On-time attendance' },
  coverage: { good: 90, watch: 75, label: 'Coverage goals met' },
});

export const GRADE_LABEL = { good: 'On track', watch: 'Watch', concern: 'Needs review', na: 'Not enough evidence' };
const GRADE_ORDER = { concern: 0, watch: 1, good: 2, na: 3 };
const DAY = 86_400_000;

const pct = value => value == null ? 'N/A' : `${fmtNumber(Math.round(Number(value) * 10) / 10)}%`;
const hours = value => `${fmtNumber(Math.round(Number(value ?? 0) * 10) / 10)} h`;
const plural = (count, word, many = `${word}s`) => `${fmtNumber(count)} ${count === 1 ? word : many}`;
const day = value => {
  const date = new Date(value);
  return Number.isFinite(date.getTime()) ? date.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }) : '—';
};

function band(value, threshold) {
  if (value >= threshold.good) return 'good';
  return value >= threshold.watch ? 'watch' : 'concern';
}

function bandReason(grade, value, threshold, unit = '%') {
  if (grade === 'good') return `At or above the ${threshold.good}${unit} target.`;
  if (grade === 'watch') return `Between ${threshold.watch}${unit} and the ${threshold.good}${unit} target.`;
  return `Below ${threshold.watch}${unit}; review the context before drawing any conclusion.`;
}

function efficiencyMetric(e) {
  const t = THRESHOLDS.efficiency;
  if (!e) return { key: 'efficiency', label: t.label, display: 'Hidden', grade: 'na', reason: 'Labor evidence is not visible to this account.' };
  const context = `${hours(e.eligibleLaborHours)} eligible of ${hours(e.directLaborHours)} direct labor; ${hours(e.verifiedExternalDelayHours)} verified external delay excluded; ${plural(e.operationsInScope, 'operation')} in scope, ${fmtNumber(e.operationsOutsideScope)} outside (no accepted output or standard).`;
  if (e.controllablePercent == null)
    return { key: 'efficiency', label: t.label, display: 'N/A', grade: 'na', reason: e.status || 'No operation with accepted output and a released standard in this period.', context };
  if (Number(e.eligibleLaborHours) < t.minimumHours)
    return { key: 'efficiency', label: t.label, value: e.controllablePercent, display: pct(e.controllablePercent), grade: 'na',
      reason: `Only ${hours(e.eligibleLaborHours)} of eligible labor; at least ${t.minimumHours} h are needed before the figure is graded.`, context };
  const grade = band(Number(e.controllablePercent), t);
  return { key: 'efficiency', label: t.label, value: e.controllablePercent, display: pct(e.controllablePercent), grade,
    reason: `${bandReason(grade, e.controllablePercent, t)} Unadjusted ${pct(e.unadjustedPercent)}.`, context };
}

function firstPassMetric(f) {
  const t = THRESHOLDS.firstPass;
  if (!f) return { key: 'firstPass', label: t.label, display: 'Hidden', grade: 'na', reason: 'First-pass results need quality read access.' };
  const context = `Official process FPY ${pct(f.officialProcessPercent)} on ${fmtNumber(f.inspectedQuantity)} inspected unit(s); verified attribution covers ${pct(f.coveragePercent)} (minimum ${pct(f.minimumCoveragePercent)}); ${fmtNumber(f.externallyCausedQuantity)} unit(s) failed for verified outside causes, ${fmtNumber(f.unassignedQuantity)} not yet attributed.`;
  if (f.employeeAttributablePercent == null)
    return { key: 'firstPass', label: t.label, display: 'N/A', grade: 'na', reason: f.status || 'Too little Quality-verified attribution to state an employee figure.', context };
  const grade = band(Number(f.employeeAttributablePercent), t);
  return { key: 'firstPass', label: t.label, value: f.employeeAttributablePercent, display: pct(f.employeeAttributablePercent), grade,
    reason: `${bandReason(grade, f.employeeAttributablePercent, t)} Employee-attributable figure; only Quality-verified causes count.`, context };
}

function attendanceMetric(a) {
  const t = THRESHOLDS.attendance;
  if (!a) return { key: 'attendance', label: t.label, display: 'Hidden', grade: 'na', reason: 'Attendance rates are visible to Human Resources only.' };
  const context = `${fmtNumber(a.onTime)} on time of ${fmtNumber(a.eligible)} eligible arrival(s) · ${fmtNumber(a.verifiedLate)} verified late · ${fmtNumber(a.verifiedCallOuts)} call-out(s) · ${fmtNumber(a.verifiedNoShows)} no-show(s) · ${fmtNumber(a.excluded)} excluded (approved leave) · ${fmtNumber(a.pendingReview)} pending review.`;
  if (a.onTimePercent == null || a.eligible < t.minimumArrivals)
    return { key: 'attendance', label: t.label, value: a.onTimePercent, display: pct(a.onTimePercent), grade: 'na',
      reason: a.onTimePercent == null ? (a.explanation || 'No eligible scheduled arrivals in this period.')
        : `Only ${plural(a.eligible, 'eligible arrival')}; at least ${t.minimumArrivals} are needed before the figure is graded.`, context };
  const grade = band(Number(a.onTimePercent), t);
  const pending = a.pendingReview ? ` ${plural(a.pendingReview, 'record')} still awaiting review are not counted.` : '';
  return { key: 'attendance', label: t.label, value: a.onTimePercent, display: pct(a.onTimePercent), grade,
    reason: `${bandReason(grade, a.onTimePercent, t)} Verified events only.${pending}`, context };
}

function coverageMetric(c) {
  const t = THRESHOLDS.coverage;
  if (!c) return { key: 'coverage', label: t.label, display: 'Hidden', grade: 'na', reason: 'Coverage records are not visible to this account.' };
  const context = `${plural(c.completed, 'completed assignment')} · ${hours(c.verifiedHours)} verified · ${plural(c.distinctOperations, 'operation')} covered · ${fmtNumber(c.goalSuccesses)} of ${fmtNumber(c.goalEligible)} goal-eligible met · average attainment ${pct(c.averageAttainmentPercent)} · ${fmtNumber(c.excluded)} excluded.`;
  if (c.goalSuccessPercent == null || !c.goalEligible)
    return { key: 'coverage', label: t.label, display: c.completed ? 'N/A' : '—', grade: 'na',
      reason: c.completed ? 'Completed coverage had no comparable goal, so no rate is stated.' : 'No cross-functional coverage in this period (not a shortfall).', context };
  const grade = band(Number(c.goalSuccessPercent), t);
  return { key: 'coverage', label: t.label, value: c.goalSuccessPercent, display: pct(c.goalSuccessPercent), grade,
    reason: `${bandReason(grade, c.goalSuccessPercent, t)} Each assignment is judged against its own goal in effect.`, context };
}

function qualificationMetric(q, expiringDays) {
  const label = 'Qualifications';
  if (!q) return { key: 'qualifications', label, display: 'Hidden', grade: 'na', reason: 'Qualification records need competency read access.' };
  const context = `${plural(q.current, 'current qualification')} · ${fmtNumber(q.inTraining)} in training · ${fmtNumber(q.machineAuthorizationsValid)} valid machine authorization(s)${q.machineAuthorizationsInvalid ? `, ${fmtNumber(q.machineAuthorizationsInvalid)} not valid` : ''}.`;
  const expiredCodes = (q.qualifications ?? []).filter(row => row.status === 'Expired').map(row => row.code);
  const soon = (q.qualifications ?? []).filter(row => row.status === 'Qualified' && row.expiresAt
    && new Date(row.expiresAt).getTime() - Date.now() <= expiringDays * DAY && new Date(row.expiresAt).getTime() > Date.now()).map(row => row.code);
  if (q.expired > 0)
    return { key: 'qualifications', label, value: q.current, display: fmtNumber(q.current), grade: 'concern',
      reason: `${plural(q.expired, 'qualification')} expired (${expiredCodes.join(', ') || 'see list'}); expired records do not authorize work until renewed.`, context };
  if (q.expiringSoon > 0)
    return { key: 'qualifications', label, value: q.current, display: fmtNumber(q.current), grade: 'watch',
      reason: `${plural(q.expiringSoon, 'qualification')} expire within ${expiringDays} days (${soon.join(', ') || 'see list'}); schedule reassessment.`, context };
  if (!q.current)
    return { key: 'qualifications', label, value: 0, display: '0', grade: 'na',
      reason: q.inTraining ? `In training for ${plural(q.inTraining, 'qualification')}; nothing certified yet.` : 'No certified qualifications recorded.', context };
  return { key: 'qualifications', label, value: q.current, display: fmtNumber(q.current), grade: 'good', reason: 'All certified qualifications are current.', context };
}

/** Grades one card. `overall` is the most serious graded figure; gray only when nothing could be graded. */
export function gradeCard(card, expiringDays = 30) {
  const metrics = [efficiencyMetric(card.efficiency), firstPassMetric(card.firstPass), attendanceMetric(card.attendance),
    coverageMetric(card.coverage), qualificationMetric(card.competency, expiringDays)];
  const graded = metrics.filter(metric => metric.grade !== 'na');
  const overall = graded.length ? graded.reduce((worst, metric) => GRADE_ORDER[metric.grade] < GRADE_ORDER[worst] ? metric.grade : worst, 'good') : 'na';
  const flagged = metrics.filter(metric => metric.grade === 'concern' || metric.grade === 'watch');
  const summary = overall === 'na' ? 'No figure has enough verified evidence to grade in this period.'
    : flagged.length ? flagged.map(metric => `${metric.label}: ${metric.reason}`).join(' ')
      : 'Every graded figure meets its target.';
  return { metrics, overall, summary };
}

/** Review periods: each quarter of this and last year that has started, plus both years. */
export function reviewPeriods(now = new Date()) {
  const periods = [];
  for (const year of [now.getFullYear(), now.getFullYear() - 1]) {
    for (let quarter = 4; quarter >= 1; quarter--) {
      const from = new Date(year, (quarter - 1) * 3, 1);
      if (from > now) continue;
      const end = new Date(year, quarter * 3, 1);
      periods.push({ key: `${year}-Q${quarter}`, label: `Q${quarter} ${year}${end > now ? ' (to date)' : ''}`, kind: 'Quarterly', from, to: end > now ? now : end });
    }
    const end = new Date(year + 1, 0, 1);
    periods.push({ key: `${year}`, label: `${year} ${end > now ? 'year to date' : 'full year'}`, kind: 'Annual', from: new Date(year, 0, 1), to: end > now ? now : end });
  }
  return periods.sort((a, b) => b.from - a.from || (a.kind === 'Annual' ? 1 : -1));
}

function tile(metric) {
  return `<div class="perf-tile ${metric.grade}" title="${esc(metric.reason)}">
    <div class="perf-tile-value">${esc(metric.display)}</div><div class="perf-tile-label">${esc(metric.label)}</div>
    <div class="perf-tile-why"><span class="perf-dot ${metric.grade}" aria-hidden="true"></span>${esc(metric.reason)}</div></div>`;
}

function evidenceHtml(card, grading) {
  const q = card.competency;
  const qualificationRows = q?.qualifications?.length
    ? `<table class="table perf-table"><thead><tr><th>Qualification</th><th>Status</th><th>Certified</th><th>Expires</th></tr></thead><tbody>${q.qualifications.map(row => `<tr><td>${esc(row.code)}<div class="faint">${esc(row.name)}</div></td><td><span class="pill ${row.status === 'Qualified' ? 'ok' : row.status === 'Expired' ? 'bad' : row.status === 'InTraining' ? 'warn' : 'none'}">${esc(row.status === 'InTraining' ? 'In training' : row.status)}</span></td><td>${row.certifiedAt ? esc(day(row.certifiedAt)) : '—'}</td><td>${row.expiresAt ? esc(day(row.expiresAt)) : 'No expiry'}</td></tr>`).join('')}</tbody></table>` : '';
  const authorizationRows = q?.machineAuthorizations?.length
    ? `<table class="table perf-table"><thead><tr><th>Machine / activity</th><th>Qualification</th><th>Status</th><th>Expires</th></tr></thead><tbody>${q.machineAuthorizations.map(row => `<tr><td>${esc(row.machineCode)} · ${esc(row.activityCode)}${row.restrictions ? `<div class="faint">${esc(row.restrictions)}</div>` : ''}</td><td>${esc(row.qualificationCode)}</td><td><span class="pill ${row.valid ? 'ok' : 'bad'}">${row.valid ? 'Valid' : esc(row.status)}</span></td><td>${row.expiresAt ? esc(day(row.expiresAt)) : 'No expiry'}</td></tr>`).join('')}</tbody></table>` : '';
  const courseRows = q?.coursesInPeriod?.length
    ? `<ul class="perf-list">${q.coursesInPeriod.map(row => `<li>${esc(row.courseCode)} v${esc(row.courseVersion)} · completed ${esc(day(row.completedAt))}${row.trainerName ? ` · trainer ${esc(row.trainerName)}` : ''}</li>`).join('')}</ul>` : '';
  return `<ul class="perf-list perf-context">${grading.metrics.map(metric => `<li><b>${esc(metric.label)}</b> <span class="pill ${metric.grade === 'good' ? 'ok' : metric.grade === 'watch' ? 'warn' : metric.grade === 'concern' ? 'bad' : 'none'}">${esc(GRADE_LABEL[metric.grade])}</span><div>${esc(metric.reason)}</div>${metric.context ? `<div class="faint">${esc(metric.context)}</div>` : ''}</li>`).join('')}
    <li><b>Paid time</b><div class="faint">${hours(card.paidHours)} recorded across ${plural(card.shiftsRecorded, 'shift')}; ${hours(card.unaccountedHours)} not yet accounted for by labor, verified downtime or approved coded time.</div></li></ul>
    ${qualificationRows ? `<h4>Qualifications</h4>${qualificationRows}` : ''}${authorizationRows ? `<h4>Machine authorizations</h4>${authorizationRows}` : ''}${courseRows ? `<h4>Training completed in this period</h4>${courseRows}` : ''}`;
}

function contributionsHtml(card) {
  return card.contributions?.length
    ? `<ul class="perf-list">${card.contributions.map(line => `<li>${esc(line)}</li>`).join('')}</ul>`
    : '<p class="faint">No completed operations, coverage or training were recorded in this period.</p>';
}

const PILL = { good: 'ok', watch: 'warn', concern: 'bad', na: 'none' };
const SHORT = { efficiency: 'Eff', firstPass: 'FPY', attendance: 'On-time', coverage: 'Cover', qualifications: 'Quals' };

/** One employee: a collapsed row (name, status, every figure as a colored chip) that opens into the full review card. */
export function cardHtml(card, grading, period, open = false) {
  const subtitle = [card.jobTitle, card.department, card.homeWorkCenterCode, card.shiftCode].filter(Boolean).map(esc).join(' · ');
  const chips = grading.metrics.map(metric => `<span class="perf-chip ${metric.grade}" title="${esc(`${metric.label}: ${metric.reason}`)}"><span class="perf-dot ${metric.grade}" aria-hidden="true"></span>${esc(SHORT[metric.key] ?? metric.label)} <b>${esc(metric.display)}</b></span>`).join('');
  return `<details class="perf-card ${grading.overall}" data-perf-card="${esc(card.personId)}" ${open ? 'open' : ''}>
    <summary><span class="perf-card-head"><span class="perf-card-name"><b>${esc(card.displayName)}</b><span class="faint">${subtitle}</span></span><span class="pill ${PILL[grading.overall]}">${esc(GRADE_LABEL[grading.overall])}</span></span>
      <span class="perf-chips">${chips}</span></summary>
    <div class="perf-card-body">
    <div class="perf-period faint">${esc(period?.kind ?? 'Review')} performance summary · ${esc(period?.label ?? '')} · <a href="#/performance?personId=${encodeURIComponent(card.personId)}">operation history</a></div>
    <div class="perf-tiles">${grading.metrics.map(tile).join('')}</div>
    <div class="perf-contributions"><b>Documented contributions</b>${contributionsHtml(card)}</div>
    <details class="perf-evidence"><summary>Why these colors · evidence and context</summary>${evidenceHtml(card, grading)}</details>
    <footer><span class="faint">Every figure comes from recorded source data and is for a reviewer to discuss, not a decision.</span><button class="btn small" type="button" data-perf-print="${esc(card.personId)}">Print</button></footer>
    </div>
  </details>`;
}

const GROUPS = {
  status: { label: 'Status', columns: [['concern', GRADE_LABEL.concern], ['watch', GRADE_LABEL.watch], ['good', GRADE_LABEL.good], ['na', GRADE_LABEL.na]], of: g => g.grading.overall },
  workCenter: { label: 'Work center', of: g => g.card.homeWorkCenterCode || 'Unassigned' },
  shift: { label: 'Shift', of: g => g.card.shiftCode || 'No shift' },
};

/** The board: one column per status (or per work center / shift), one card per employee. */
export function boardHtml(data, state, periods) {
  const period = periods.find(item => item.key === state.periodKey) ?? periods[0];
  const graded = (data?.cards ?? []).map(card => ({ card, grading: gradeCard(card, data.expiringWithinDays ?? 30) }))
    .filter(item => (!state.visibleIds || state.visibleIds.has(item.card.personId))
      && (!state.search || item.card.displayName.toLowerCase().includes(state.search.toLowerCase())));
  const group = GROUPS[state.groupBy] ?? GROUPS.status;
  const columns = group.columns ?? [...new Set(graded.map(group.of))].sort((a, b) => a.localeCompare(b)).map(key => [key, key]);
  const counts = { concern: 0, watch: 0, good: 0, na: 0 };
  for (const item of graded) counts[item.grading.overall]++;
  const byGrade = (a, b) => GRADE_ORDER[a.grading.overall] - GRADE_ORDER[b.grading.overall] || a.card.displayName.localeCompare(b.card.displayName);
  const legend = Object.entries(THRESHOLDS).map(([, t]) => `<span><b>${esc(t.label)}</b> green ≥ ${t.good}% · yellow ≥ ${t.watch}% · red below</span>`).join('')
    + `<span><b>Qualifications</b> yellow when one expires within ${data?.expiringWithinDays ?? 30} days · red when one has expired</span><span><b>Gray</b> not enough verified evidence; never treated as a shortfall</span>`;
  return `<div class="perf-summary"><span class="perf-count concern">${counts.concern} needs review</span><span class="perf-count watch">${counts.watch} watch</span><span class="perf-count good">${counts.good} on track</span><span class="perf-count na">${counts.na} not enough evidence</span></div>
    ${(data?.restrictions ?? []).map(line => `<p class="notice warn">${esc(line)}</p>`).join('')}
    <div class="perf-board">${columns.map(([key, label]) => {
      const items = graded.filter(item => group.of(item) === key).sort(byGrade);
      return `<section class="perf-column ${state.groupBy === 'status' || !state.groupBy ? key : ''}"><h3>${esc(label)} <span class="badge gray">${items.length}</span></h3>${items.map(item => cardHtml(item.card, item.grading, period, state.open?.has(item.card.personId))).join('') || '<p class="faint">No employees.</p>'}</section>`;
    }).join('')}</div>
    <details class="perf-legend"><summary>How the colors are set</summary><div>${legend}</div><p class="faint">${(data?.basis ?? []).map(esc).join(' ')}</p></details>`;
}

export function controlsHtml(state, periods) {
  return `<div class="perf-controls no-print">
    <label>Review period<select class="input" data-perf-control="period">${periods.map(period => `<option value="${esc(period.key)}" ${period.key === state.periodKey ? 'selected' : ''}>${esc(period.label)}</option>`).join('')}</select></label>
    <label>Group by<select class="input" data-perf-control="group">${Object.entries(GROUPS).map(([key, group]) => `<option value="${key}" ${key === (state.groupBy ?? 'status') ? 'selected' : ''}>${esc(group.label)}</option>`).join('')}</select></label>
    <label>Find employee<input class="input" type="search" data-perf-control="search" value="${esc(state.search ?? '')}" placeholder="Name"></label>
    <span class="spacer"></span><button class="btn small" type="button" data-perf-expand="open">Expand all</button><button class="btn small" type="button" data-perf-expand="close">Collapse all</button><button class="btn small primary" type="button" data-perf-print="all">Print full summary</button></div>`;
}

const PRINT_CSS = `body{font:12px/1.4 "Segoe UI",system-ui,sans-serif;color:#111;margin:18px}h1{font-size:20px;margin:0 0 4px}h2{font-size:15px;margin:18px 0 6px}h4{margin:10px 0 4px}
.meta{color:#444;margin-bottom:12px}table{border-collapse:collapse;width:100%;margin:6px 0}th,td{border:1px solid #bbb;padding:3px 5px;text-align:left;vertical-align:top}th{background:#f2f2f2}
.g-good{background:#dcfce7}.g-watch{background:#fef3c7}.g-concern{background:#fee2e2}.g-na{background:#f3f4f6;color:#555}
.emp{page-break-inside:avoid;border:1px solid #999;border-radius:6px;padding:10px 12px;margin:12px 0}.emp header{display:flex;justify-content:space-between}
.tiles{display:grid;grid-template-columns:repeat(5,1fr);gap:6px;margin:8px 0}.tile{border:1px solid #bbb;border-top-width:5px;border-radius:4px;padding:6px}
.tile.good{border-top-color:#16a34a}.tile.watch{border-top-color:#d97706}.tile.concern{border-top-color:#dc2626}.tile.na{border-top-color:#9ca3af}
.v{font-size:18px;font-weight:700}.why{font-size:10.5px;color:#333}.faint{color:#555}ul{margin:4px 0 4px 18px;padding:0}.sign{margin-top:22px;display:flex;gap:40px}.sign div{flex:1;border-top:1px solid #333;padding-top:3px}
@media print{.emp{page-break-inside:avoid}}`;

/** A self-contained, printable summary for one employee or all of them. */
export function printDocument(data, period, personId = null) {
  const graded = (data?.cards ?? []).filter(card => !personId || card.personId === personId)
    .map(card => ({ card, grading: gradeCard(card, data.expiringWithinDays ?? 30) }));
  const cell = metric => `<td class="g-${metric.grade}">${esc(metric.display)}<div class="why">${esc(GRADE_LABEL[metric.grade])}</div></td>`;
  const table = graded.length > 1 ? `<h2>Summary</h2><table><thead><tr><th>Employee</th>${graded[0].grading.metrics.map(metric => `<th>${esc(metric.label)}</th>`).join('')}<th>Overall</th></tr></thead><tbody>${graded.map(({ card, grading }) => `<tr><td>${esc(card.displayName)}<div class="faint">${esc([card.jobTitle, card.homeWorkCenterCode, card.shiftCode].filter(Boolean).join(' · '))}</div></td>${grading.metrics.map(cell).join('')}<td class="g-${grading.overall}">${esc(GRADE_LABEL[grading.overall])}</td></tr>`).join('')}</tbody></table>` : '';
  const employees = graded.map(({ card, grading }) => `<section class="emp"><header><div><b style="font-size:15px">${esc(card.displayName)}</b><div class="faint">${esc([card.jobTitle, card.department, card.homeWorkCenterCode, card.shiftCode].filter(Boolean).join(' · '))}</div></div><div>${esc(GRADE_LABEL[grading.overall])}</div></header>
    <div class="tiles">${grading.metrics.map(metric => `<div class="tile ${metric.grade}"><div class="v">${esc(metric.display)}</div><b>${esc(metric.label)}</b><div class="why">${esc(metric.reason)}</div></div>`).join('')}</div>
    <h4>Documented contributions</h4>${contributionsHtml(card)}<h4>Evidence and context</h4>${evidenceHtml(card, grading)}
    ${personId ? '<div class="sign"><div>Employee</div><div>Reviewer</div><div>Date</div></div>' : ''}</section>`).join('');
  const thresholds = Object.values(THRESHOLDS).map(t => `${t.label}: green ≥ ${t.good}%, yellow ≥ ${t.watch}%, red below`).join(' · ');
  return `<!doctype html><html><head><meta charset="utf-8"><title>${esc(period?.kind ?? 'Review')} performance summary · ${esc(period?.label ?? '')}</title><style>${PRINT_CSS}</style></head><body>
    <h1>${esc(period?.kind ?? 'Review')} performance summary · ${esc(period?.label ?? '')}</h1>
    <div class="meta">${esc(day(data?.from))} – ${esc(day(data?.to))} · prepared ${esc(day(data?.asOf ?? new Date()))} · ${plural(graded.length, 'employee')}</div>
    <p class="faint">Figures come from Forge source records. Colors show where a figure sits against published thresholds (${esc(thresholds)}; gray means not enough verified evidence). They are prompts for a review conversation; Forge makes no ranking, pay, promotion or disciplinary decision from them.</p>
    ${(data?.restrictions ?? []).map(line => `<p class="faint">${esc(line)}</p>`).join('')}
    ${table}${employees}<h2>Basis</h2>${(data?.basis ?? []).map(line => `<p class="faint">${esc(line)}</p>`).join('')}</body></html>`;
}

/** Fills `root` with the board, loading the selected period on demand. Returns a reload function. */
export function mountPerformanceCards(root, { get, personIds = null, now = new Date(), state = {}, cache = new Map() }) {
  const periods = reviewPeriods(now);
  state.periodKey ??= periods[0].key;
  state.groupBy ??= 'status';
  let data = null;
  const period = () => periods.find(item => item.key === state.periodKey) ?? periods[0];
  const isOpen = card => card.open === true || (card.open === undefined && !!card.attributes?.has?.('open'));
  const remember = () => {
    const board = root.querySelector('[data-perf-board]');
    if (board) state.open = new Set(Array.from(board.querySelectorAll('details.perf-card')).filter(isOpen).map(card => card.dataset.perfCard));
  };
  const draw = () => {
    const board = root.querySelector('[data-perf-board]');
    if (board) board.innerHTML = data?.error ? `<p class="notice warn">${esc(data.error)}</p>` : data ? boardHtml(data, state, periods) : '<p class="faint">Gathering verified evidence for every employee…</p>';
  };
  const load = async () => {
    const selected = period();
    data = cache.get(selected.key) ?? null;
    draw();
    if (data) return;
    const query = new URLSearchParams({ from: selected.from.toISOString(), to: selected.to.toISOString() });
    (personIds ?? []).forEach(id => query.append('personIds', id));
    const result = await get(`/api/workforce/performance-cards?${query}`);
    data = result?.error ? { error: result.error } : (result?.data && !Array.isArray(result.data) ? result.data : { cards: [] });
    if (!data.error) cache.set(selected.key, data);
    if (state.periodKey === selected.key) draw();
  };
  root.innerHTML = `${controlsHtml(state, periods)}<div data-perf-board></div>`;
  root.addEventListener('change', event => {
    const control = event.target?.dataset?.perfControl;
    if (control === 'period') { remember(); state.periodKey = event.target.value; load(); }
    if (control === 'group') { remember(); state.groupBy = event.target.value; draw(); }
  });
  root.addEventListener('input', event => {
    if (event.target?.dataset?.perfControl !== 'search') return;
    remember();
    state.search = event.target.value;
    draw();
  });
  root.addEventListener('click', event => {
    const expand = event.target.closest?.('[data-perf-expand]');
    if (expand) {
      const open = expand.dataset.perfExpand === 'open';
      for (const card of Array.from(root.querySelectorAll('details.perf-card'))) {
        card.open = open;
      }
      remember();
      return;
    }
    const print = event.target.closest?.('[data-perf-print]');
    if (!print || !data || data.error) return;
    const visible = { ...data, cards: (data.cards ?? []).filter(card => !state.visibleIds || state.visibleIds.has(card.personId)) };
    const html = printDocument(visible, period(), print.dataset.perfPrint === 'all' ? null : print.dataset.perfPrint);
    const view = typeof window.open === 'function' ? window.open('', '_blank') : null;
    if (!view) {
      const note = root.querySelector('[data-perf-board]');
      note?.insertAdjacentHTML?.('afterbegin', '<p class="notice warn">The browser blocked the print window. Allow pop-ups for Forge and try again.</p>');
      return;
    }
    view.document.open(); view.document.write(html); view.document.close();
    view.focus(); setTimeout(() => view.print(), 250);
  });
  load();
  return { reload: () => { cache.clear(); return load(); }, periods, get data() { return data; } };
}
