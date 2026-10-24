import { tryGet, command } from './api.js';
import { esc, fmtNumber, fmtDateTime, errorBlock, drawer, toast } from './ui.js';
import { formModal, canAction } from './forms.js';

// Cause-adjusted performance (Sprint 10 §16): factual time and quality accounting for one employee. Unadjusted and adjusted figures sit
// side by side, every exclusion is named, and nothing here is a score, a ranking or a personnel recommendation.

export const CODED_TIME_CODES = [
  ['Break', 'Break'], ['Meeting', 'Meeting'], ['IndirectTask', 'Indirect task'], ['Cleaning', 'Cleaning'], ['Training', 'Training'],
  ['MaterialShortage', 'Material shortage'], ['WaitingForInspection', 'Waiting for inspection'], ['QualityHold', 'Quality hold'],
  ['MachineDowntime', 'Machine downtime'], ['Other', 'Other'],
];

export const DEFECT_CAUSES = [
  ['EmployeeControlled', 'Employee-controlled'], ['Machine', 'Machine'], ['IncomingMaterial', 'Incoming material'],
  ['UpstreamProcess', 'Upstream process'], ['Unknown', 'Unknown'], ['Mixed', 'Mixed'],
];

const minutes = value => `${fmtNumber(Number(value ?? 0) / 60, 2)} h`;
const percent = value => value == null ? 'N/A' : `${fmtNumber(value, 2)}%`;

/** The report as HTML: totals, efficiency (unadjusted beside adjusted), FPY (official beside employee-attributable) and each shift. */
export function causePerformanceHtml(report, me) {
  const e = report.efficiency;
  const f = report.firstPass;
  const shiftRows = report.shifts.map(s => {
    const r = s.reconciliation;
    return `<tr><td>${esc(fmtDateTime(s.clockInAt))}${s.openShift ? ' <span class="pill warn">open</span>' : ''}<div class="faint">to ${esc(s.clockOutAt ? fmtDateTime(s.clockOutAt) : 'now')}</div></td>
      <td class="num">${minutes(r.expectedPaidMinutes)}</td><td class="num">${minutes(r.accountedPaidMinutes)}</td>
      <td class="num ${r.gapMinutes > 0 ? 'c-warn' : ''}">${minutes(r.gapMinutes)}</td><td class="num ${r.overlapMinutes > 0 ? 'c-warn' : ''}">${minutes(r.overlapMinutes)}</td>
      <td class="num">${minutes(s.directLaborMinutes)}</td><td class="num">${minutes(s.verifiedExternalDelayMinutes)}</td><td class="num">${minutes(s.reportedUnverifiedDelayMinutes)}</td>
      <td>${s.categories.map(c => `<span class="pill ${c.externalDelay ? 'warn' : 'none'}" title="${esc(c.source)}">${esc(c.label)} ${minutes(c.minutes)}</span>`).join(' ')}
        ${r.dataQualityFlags.map(flag => `<div class="faint">${esc(flag)}</div>`).join('')}${s.pendingReviewNotes.map(note => `<div class="faint">${esc(note)}</div>`).join('')}
        ${s.machineEvents.map(m => `<div class="faint">${esc(m.machineCode)} · ${esc(m.source)}${m.verified ? '' : ' (not verified)'}: event ${minutes(m.fullEventMinutes)}, charged to this shift ${minutes(m.employeeImpactMinutes)}</div>`).join('')}</td></tr>`;
  }).join('');
  const cohortRows = (report.firstPassCohorts ?? []).map(c => `<tr><td>#${c.inspectionId}<div class="faint">WO ${c.orderNumber} · op ${c.sequence}</div></td><td class="num">${fmtNumber(c.quantity, 3)}</td>
    <td>${c.passed ? '<span class="pill ok">Passed</span>' : '<span class="pill bad">Failed</span>'}</td><td>${esc(DEFECT_CAUSES.find(([k]) => k === c.cause)?.[1] ?? c.cause)}</td>
    <td>${c.verified ? '<span class="pill ok">Verified</span>' : `<span class="pill none">${esc(c.attributionState === 'None' ? 'Not attributed' : c.attributionState)}</span>`}</td>
    <td class="nowrap">${canAction(me, 'quality.inspect') && c.attributionState === 'None' ? `<button class="btn small" data-cp-attribute="${c.inspectionId}">Attribute…</button>` : ''}
      ${canAction(me, 'quality.disposition') && c.attributionState === 'Proposed' && c.attributionId ? `<button class="btn small primary" data-cp-verify="${c.attributionId}">Verify</button> <button class="btn small" data-cp-reject="${c.attributionId}">Reject</button>` : ''}
      ${canAction(me, 'quality.inspect') && c.attributionState === 'Verified' && c.attributionId ? `<button class="btn small ghost" data-cp-correct="${c.inspectionId}|${c.attributionId}">Correct…</button>` : ''}</td></tr>`).join('');
  return `<p class="workforce-intelligence-note">${esc(report.basis)}</p>
    <div class="kpis kpis-4">
      <div class="kpi"><div><div class="t">Paid time</div><div class="v">${minutes(report.paidMinutes)}</div><div class="why">${minutes(report.accountedMinutes)} accounted · ${minutes(report.gapMinutes)} unaccounted · ${minutes(report.overlapMinutes)} overlapping</div></div></div>
      <div class="kpi"><div><div class="t">Labor efficiency</div><div class="v">${percent(e.controllableEfficiencyPercent)}</div><div class="why">Unadjusted ${percent(e.unadjustedEfficiencyPercent)} · ${fmtNumber(e.verifiedExternalDelayHours, 2)} h verified external delay excluded</div></div></div>
      <div class="kpi"><div><div class="t">Official process FPY</div><div class="v">${report.qualityRestricted ? '—' : percent(f?.officialProcessFpyPercent)}</div><div class="why">${report.qualityRestricted ? 'You cannot read Quality records.' : `${fmtNumber(f?.inspectedQuantity ?? 0, 3)} first-pass units, every cause`}</div></div></div>
      <div class="kpi"><div><div class="t">Employee-attributable FPY</div><div class="v">${report.qualityRestricted ? '—' : percent(f?.employeeAttributableFpyPercent)}</div><div class="why">${report.qualityRestricted ? '' : `Coverage ${fmtNumber(f?.employeeAttributionCoveragePercent ?? 0, 2)}% · ${fmtNumber(f?.externallyCausedQuantity ?? 0, 3)} externally caused excluded · ${fmtNumber(f?.unassignedQuantity ?? 0, 3)} unassigned`}</div></div></div>
    </div>
    <div class="faint">${esc(e.status)}${report.operationsOutsideEfficiencyScope ? ` ${report.operationsOutsideEfficiencyScope} operation(s) without accepted output are outside the efficiency scope.` : ''}</div>
    ${f ? `<div class="faint">${esc(f.employeeMetricStatus)}</div>` : ''}
    <h3>Shifts</h3>
    ${report.shifts.length ? `<div class="table-wrap"><table class="table"><thead><tr><th>Shift</th><th class="num">Paid</th><th class="num">Accounted</th><th class="num">Gap</th><th class="num">Overlap</th><th class="num">Direct labor</th><th class="num">Verified external delay</th><th class="num">Reported, unverified</th><th>Where the time went</th></tr></thead><tbody>${shiftRows}</tbody></table></div>`
      : '<p class="faint">No attendance was recorded for this employee in the period, so there is no paid time to account for.</p>'}
    ${canAction(me, 'workforce.supervise') ? '<button class="btn small" data-cp-code>Record coded time…</button>' : ''}
    <h3>First-pass inspections</h3>
    ${report.qualityRestricted ? '<p class="faint">Quality records are not readable with your access.</p>'
      : cohortRows ? `<div class="table-wrap"><table class="table"><thead><tr><th>Inspection</th><th class="num">Units</th><th>Result</th><th>Cause</th><th>Attribution</th><th></th></tr></thead><tbody>${cohortRows}</tbody></table></div>`
      : '<p class="faint">No first-pass inspections of this employee’s operations in the period.</p>'}`;
}

/** Opens the employee's cause-adjusted performance in a drawer, with coded-time and attribution actions for those allowed to take them. */
export async function openCausePerformance(personId, displayName, me, days = 7) {
  const d = drawer(`${displayName} · cause-adjusted time and quality`, '<div class="muted">Loading…</div>');
  const to = new Date();
  const from = new Date(to.getTime() - days * 864e5);
  const load = async () => {
    const [report, coded] = await Promise.all([
      tryGet(`/api/workforce/cause-adjusted-performance?personId=${encodeURIComponent(personId)}&from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`),
      tryGet(`/api/workforce/coded-time?personId=${encodeURIComponent(personId)}&from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`),
    ]);
    if (report.error) { d.element.innerHTML = errorBlock(report.error, 'cause-adjusted performance'); return; }
    const pendingCoded = (coded.data ?? []).filter(c => c.state === 'PendingReview');
    d.element.innerHTML = `${causePerformanceHtml(report.data, me)}
      ${pendingCoded.length ? `<h3>Coded time waiting for review</h3><ul>${pendingCoded.map(c => `<li>${esc(CODED_TIME_CODES.find(([k]) => k === c.code)?.[1] ?? c.code)} ${esc(fmtDateTime(c.startsAt))}–${esc(fmtDateTime(c.endsAt))} · ${esc(c.note)}
        ${canAction(me, 'workforce.supervise') && c.recordedBy !== `person:${me?.personId}` ? ` <button class="btn small primary" data-cp-approve="${esc(c.entryId)}">Approve</button> <button class="btn small" data-cp-decline="${esc(c.entryId)}">Reject</button>` : ' <span class="faint">someone else reviews it</span>'}</li>`).join('')}</ul>` : ''}`;
    bind();
  };
  const act = async promise => { try { if (await promise) await load(); } catch (error) { toast(error.message, true); } };
  const bind = () => {
    d.element.querySelector('[data-cp-code]')?.addEventListener('click', () => act(formModal({
      title: `Record coded time for ${displayName}`, detail: 'For paid time no other record covers. Someone other than you approves it before it counts.', confirmLabel: 'Record', reasonRequired: true,
      fields: [
        { name: 'code', label: 'What the time was', type: 'select', options: CODED_TIME_CODES, value: 'Break' },
        { name: 'startsAt', label: 'Start', type: 'datetime-local', required: true },
        { name: 'endsAt', label: 'End', type: 'datetime-local', required: true },
        { name: 'paid', label: 'Paid', type: 'check', text: 'Paid time', value: true },
        { name: 'machineCode', label: 'Machine (optional)', code: true },
        { name: 'note', label: 'What happened', type: 'textarea', required: true },
      ],
    }, (v, reason) => command('/api/workforce/coded-time', { personId, code: v.code, startsAt: new Date(v.startsAt).toISOString(), endsAt: new Date(v.endsAt).toISOString(), paid: v.paid, machineCode: v.machineCode, note: v.note }, reason))));
    d.element.querySelectorAll('[data-cp-approve],[data-cp-decline]').forEach(b => b.addEventListener('click', () => act(formModal({
      title: b.dataset.cpApprove ? 'Approve coded time' : 'Reject coded time', fields: [], confirmLabel: b.dataset.cpApprove ? 'Approve' : 'Reject', reasonRequired: true,
    }, (_, reason) => command(`/api/workforce/coded-time/${encodeURIComponent(b.dataset.cpApprove ?? b.dataset.cpDecline)}/review`, { approve: !!b.dataset.cpApprove }, reason)))));
    const attributionFields = [
      { name: 'cause', label: 'Cause', type: 'select', options: DEFECT_CAUSES, value: 'EmployeeControlled' },
      { name: 'employeeOpportunity', label: 'Opportunity', type: 'check', text: 'The operator had a meaningful, controllable opportunity to affect the result', value: true },
      { name: 'failedQuantity', label: 'Failed units (failed inspections; blank = all inspected)', type: 'number', min: 0 },
      { name: 'evidence', label: 'Evidence (Quality, maintenance or MRB record)', type: 'textarea', required: true },
    ];
    const propose = (inspectionId, supersedesAttributionId = null) => act(formModal({
      title: `Attribute inspection ${inspectionId}`, detail: 'A different person with disposition authority verifies it before it counts. The inspection itself never changes.', fields: attributionFields, confirmLabel: 'Propose', reasonRequired: true,
    }, (v, reason) => command(`/api/quality/inspections/${inspectionId}/attribution`, { ...v, supersedesAttributionId }, reason)));
    d.element.querySelectorAll('[data-cp-attribute]').forEach(b => b.addEventListener('click', () => propose(b.dataset.cpAttribute)));
    d.element.querySelectorAll('[data-cp-correct]').forEach(b => b.addEventListener('click', () => { const [inspection, original] = b.dataset.cpCorrect.split('|'); propose(inspection, Number(original)); }));
    d.element.querySelectorAll('[data-cp-verify],[data-cp-reject]').forEach(b => b.addEventListener('click', () => act(formModal({
      title: b.dataset.cpVerify ? 'Verify attribution' : 'Reject attribution', fields: [], confirmLabel: b.dataset.cpVerify ? 'Verify' : 'Reject', reasonRequired: true,
    }, (_, reason) => command(`/api/quality/attributions/${b.dataset.cpVerify ?? b.dataset.cpReject}/verify`, { verify: !!b.dataset.cpVerify }, reason)))));
  };
  await load();
}
