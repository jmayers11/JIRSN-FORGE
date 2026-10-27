import { tryGet, command, readPost } from '../api.js';
import { esc, fmtNumber, errorBlock } from '../ui.js';
import { buildWorkforceIntelligence, filterWorkforceIntelligenceInputs, renderEvidenceReference } from '../workforce-intelligence.mjs';
import { openCausePerformance } from '../cause-performance.js';
import { candidateCapacityHtml, capacityByPerson } from '../skill-capacity.js';
import { mountPerformanceCards } from '../performance-cards.js';

const DAY = 86_400_000;
const formatDate = value => {
  const date = new Date(value);
  return Number.isFinite(date.getTime()) ? date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) : 'date unavailable';
};
const formatMoment = value => {
  const date = new Date(value);
  return Number.isFinite(date.getTime()) ? date.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : 'not scheduled in the current horizon';
};

function renderMaterialReceiptImpact(preview) {
  const completion = value => value ? formatMoment(value) : 'not scheduled in the current horizon';
  const impact = row => `<li><b>WO ${fmtNumber(row.orderNumber)} · due ${esc(formatDate(row.dueDate))}</b><span>${esc(row.beforeStatus)} → ${esc(row.proposedStatus)} · ${esc(completion(row.beforeCompletion))} → ${esc(completion(row.proposedCompletion))}${row.completionShiftHours == null ? '' : ` · ${row.completionShiftHours > 0 ? '+' : ''}${fmtNumber(row.completionShiftHours)} schedule hours`}</span></li>`;
  return `<div class="workforce-intelligence-recovery-list"><b>WO ${fmtNumber(preview.targetOrder.orderNumber)} · due ${esc(formatDate(preview.targetOrder.dueDate))}</b><span>${esc(preview.targetOrder.beforeStatus)} → ${esc(preview.targetOrder.proposedStatus)} · projected completion ${esc(completion(preview.targetOrder.beforeCompletion))} → ${esc(completion(preview.targetOrder.proposedCompletion))}${preview.targetOrder.completionShiftHours == null ? '' : ` · ${preview.targetOrder.completionShiftHours > 0 ? '+' : ''}${fmtNumber(preview.targetOrder.completionShiftHours)} schedule hours`}</span><span>Material: ${esc(preview.baselineMaterialState)} → ${esc(preview.proposedMaterialState)}</span><span>${esc(preview.baselineMaterialSummary)} ${esc(preview.proposedMaterialSummary)}</span>${preview.otherOrderImpacts?.length ? `<b>Other visible orders affected</b><ol>${preview.otherOrderImpacts.map(impact).join('')}</ol>` : '<span>No other visible order schedule changed in this comparison.</span>'}<p class="faint">${esc(preview.assumption)}</p></div>`;
}

async function qualificationRecords(workers) {
  const ids = workers.map(worker => worker.personId).filter(Boolean);
  if (!ids.length) return { data: [] };
  const batches = [];
  for (let i = 0; i < ids.length; i += 500) {
    const query = new URLSearchParams();
    ids.slice(i, i + 500).forEach(id => query.append('personIds', id));
    batches.push(tryGet(`/api/competency/people/qualifications?${query}`));
  }
  const results = await Promise.all(batches);
  const failed = results.find(result => result.error);
  return failed ? { error: failed.error } : { data: results.flatMap(result => result.data ?? []) };
}

async function machineAuthorizationRecords(workers) {
  const ids = workers.map(worker => worker.personId).filter(Boolean);
  if (!ids.length) return { data: [] };
  const batches = [];
  for (let i = 0; i < ids.length; i += 500) {
    const query = new URLSearchParams();
    ids.slice(i, i + 500).forEach(id => query.append('personIds', id));
    batches.push(tryGet(`/api/competency/machine-authorizations?${query}`));
  }
  const results = await Promise.all(batches);
  const failed = results.find(result => result.error);
  return failed ? { error: failed.error } : { data: results.flatMap(result => result.data ?? []) };
}

function escapeAttr(value) { return esc(value).replaceAll('"', '&quot;'); }
function localDateTime(value) {
  const date = value instanceof Date ? value : new Date(value);
  if (!Number.isFinite(date.getTime())) return '';
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}

async function loadPersonOperationHistory(personId, activityResult, me, asOf) {
  if (!personId) return null;
  if (activityResult?.error || !Array.isArray(activityResult?.data?.labor)) return { error: activityResult?.error ?? 'Recorded shop-floor labor is unavailable.' };
  const permissions = new Set(me?.permissions ?? []);
  if (!permissions.has('execution.shopfloor.read') || !permissions.has('execution.orders.read'))
    return { error: 'Shop-floor and work-order read permissions are required to view operation history.' };

  const labor = activityResult.data.labor.filter(entry => entry.personId === personId)
    .sort((a, b) => new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime());
  if (!labor.length) return { rows: [], omittedOrders: 0 };
  const orderNumbers = [...new Set(labor.map(entry => entry.orderNumber))];
  const visibleOrders = orderNumbers.slice(0, 8);
  const details = await Promise.all(visibleOrders.map(async orderNumber => [orderNumber, await tryGet(`/api/orders/${encodeURIComponent(orderNumber)}/shopfloor`)]));
  const byOrder = new Map(details.map(([orderNumber, result]) => [orderNumber, result]));
  const groups = new Map();
  for (const entry of labor) {
    if (!visibleOrders.includes(entry.orderNumber)) continue;
    const key = `${entry.orderNumber}:${entry.sequence}`;
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(entry);
  }
  const rows = [...groups.entries()].map(([key, entries]) => {
    const [orderNumber, sequence] = key.split(':').map(Number);
    const result = byOrder.get(orderNumber);
    const detail = result?.data;
    const metric = detail?.laborMetrics?.find(item => item.sequence === sequence) ?? null;
    const duration = entry => {
      const start = new Date(entry.startedAt).getTime();
      const end = entry.endedAt ? new Date(entry.endedAt).getTime() : asOf.getTime();
      return Number.isFinite(start) && Number.isFinite(end) ? Math.max(0, (end - start) / 3_600_000) : 0;
    };
    const round = value => Math.round(value * 1000) / 1000;
    const personOperatorHours = entries.filter(entry => entry.role === 'Operator').reduce((sum, entry) => sum + duration(entry), 0);
    const personTrainingHours = entries.filter(entry => entry.role === 'Trainer' || entry.role === 'Trainee').reduce((sum, entry) => sum + duration(entry), 0);
    const phases = [...new Set(entries.map(entry => entry.phase ?? 'Run'))].sort().map(phase => [phase,
      round(entries.filter(entry => (entry.phase ?? 'Run') === phase).reduce((sum, entry) => sum + duration(entry), 0))]);
    return { orderNumber, sequence, workCenterCode: entries[0]?.workCenterCode ?? metric?.workCenterCode ?? '—',
      machineCodes: [...new Set(entries.map(entry => entry.machineCode).filter(Boolean))], employeeHours: round(personOperatorHours),
      trainingHours: round(personTrainingHours), phases, metric,
      latest: Math.max(...entries.map(entry => new Date(entry.startedAt).getTime()).filter(Number.isFinite)),
      detailError: result?.error };
  }).sort((a, b) => b.latest - a.latest);
  const failed = details.find(([, result]) => result.error)?.[1]?.error;
  return { rows, error: failed, omittedOrders: Math.max(0, orderNumbers.length - visibleOrders.length) };
}

function personHistoryHtml(selectedPersonId, selectedWorker, personOperationHistory) {
  return !selectedPersonId ? '' : !selectedWorker
    ? `<section class="workforce-intelligence-person-history"><h3>Operation history</h3><p class="faint">The selected worker is not available in the current Workforce scope.</p></section>`
    : personOperationHistory?.error
      ? `<section class="workforce-intelligence-person-history"><h3>${esc(selectedWorker.displayName)} · operation history</h3>${errorBlock(personOperationHistory.error, 'operation history')}</section>`
      : !personOperationHistory?.rows?.length
        ? `<section class="workforce-intelligence-person-history"><h3>${esc(selectedWorker.displayName)} · operation history</h3><p class="faint">No direct labor entries for this employee were recorded in the last 30 days.</p></section>`
        : `<section class="workforce-intelligence-person-history"><header><h3>${esc(selectedWorker.displayName)} · operation history</h3><span class="spacer"></span><span class="muted">Last 30 days · recorded labor</span></header>
          <p class="workforce-intelligence-note">Employee hours come from this person’s recorded intervals. Routing targets and crew actuals describe the whole operation; they are not an individual score. Efficiency stays N/A without reviewed accepted-output evidence.</p>
          ${personOperationHistory.error ? `<p class="notice warn">Some work-order details could not be read: ${esc(personOperationHistory.error)}</p>` : ''}
          <div class="table-wrap"><table class="table"><thead><tr><th>Work order / operation</th><th>Employee hours</th><th>Operation target / crew actual</th><th>Reported output</th><th>Machine stops</th></tr></thead><tbody>
          ${personOperationHistory.rows.map(row => `<tr><td><a href="#/orders?q=${encodeURIComponent(String(row.orderNumber))}">WO ${row.orderNumber} · op ${row.sequence}</a><div class="faint">${esc(row.workCenterCode)}${row.machineCodes.length ? ` · ${row.machineCodes.map(esc).join(', ')}` : ''}</div></td>
            <td>${fmtNumber(row.employeeHours)} h direct${row.trainingHours ? `<div class="faint">${fmtNumber(row.trainingHours)} h training</div>` : ''}<div class="faint">${row.phases.map(([phase, hours]) => `${esc(phase)} ${fmtNumber(hours)} h`).join(' · ') || 'No phase detail'}</div></td>
            <td>${row.metric?.plannedTotalLaborHours == null ? 'N/A' : `${fmtNumber(row.metric.plannedTotalLaborHours)} h target`}<div class="faint">${row.metric ? `${fmtNumber(row.metric.recordedOperatorHours)} h crew actual · crew ${row.metric.crewSize}` : 'Operation target unavailable'}</div></td>
            <td>${row.metric ? `${fmtNumber(row.metric.reportedGoodQuantity, 3)} good / ${fmtNumber(row.metric.reportedScrapQuantity, 3)} scrap` : 'N/A'}<div class="faint">${row.metric?.earnedStandardStatus ? esc(row.metric.earnedStandardStatus) : 'Operation details unavailable'}</div></td>
            <td>${row.metric ? `${fmtNumber(row.metric.recordedStoppageMinutes)} min` : 'N/A'}<div class="faint">${esc(row.metric?.stoppageContext ?? 'Operation details unavailable')}</div></td></tr>`).join('')}
          </tbody></table></div>
          ${personOperationHistory.omittedOrders ? `<p class="faint">Showing recent operations from ${personOperationHistory.rows.length} orders; ${personOperationHistory.omittedOrders} older orders are outside this profile view.</p>` : ''}
        </section>`;
}

/** Recent operation labor for one person (last 30 days), for pages outside the Command Center. */
export async function personOperationHistoryHtml(person, me) {
  const now = new Date();
  const from = new Date(now.getTime() - 30 * DAY);
  const activity = await tryGet(`/api/shopfloor/activity?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(now.toISOString())}`);
  return personHistoryHtml(person.personId, person, await loadPersonOperationHistory(person.personId, activity, me, now));
}

function renderOwnAttendanceCorrections(eventsResult) {
  const events = Array.isArray(eventsResult?.data) ? eventsResult.data : [];
  const superseded = new Set(events.map(item => item.supersedesEventId).filter(Boolean));
  const correctionKinds = ['Arrival', 'CallOut', 'NoShow', 'ApprovedPto', 'ProtectedLeave', 'ExcusedAbsence', 'EarlyDeparture', 'ScheduleChange'];
  const rows = events.length ? events.map(item => {
    const replaced = superseded.has(item.eventId);
    const pending = item.reviewState === 'PendingReview';
    return `<article class="workforce-intelligence-review-item" data-own-attendance-event="${escapeAttr(item.eventId)}"><div><b>${esc(item.kind)}</b><span>${esc(item.reviewState.replaceAll(/([a-z])([A-Z])/g, '$1 $2'))} · ${esc(item.source)} · recorded ${formatDate(item.recordedAt)} · ${item.observedAt ? `observed ${formatMoment(item.observedAt)}` : 'no observed time'}</span></div>
      ${replaced ? '<p class="faint">A later correction or review superseded this entry.</p>' : pending ? '<p class="faint">This entry is awaiting independent review.</p>' : `<form class="workforce-intelligence-form" data-own-attendance-correction="${escapeAttr(item.eventId)}"><label>Correct classification<select class="input" name="kind">${correctionKinds.map(kind => `<option value="${kind}" ${kind === item.kind ? 'selected' : ''}>${kind}</option>`).join('')}</select></label><label>Observed at<input class="input" name="observedAt" type="datetime-local" value="${localDateTime(item.observedAt ?? item.recordedAt)}"></label><label>Why does this record need correction?<input class="input" name="reason" maxlength="240" required placeholder="Describe the source or time correction"></label><button class="btn small" type="submit">Request correction</button><span data-own-attendance-result aria-live="polite"></span></form>`}
    </article>`;
  }).join('') : '<p class="faint">No attendance records were found for your account in the past year.</p>';
  return `<section class="panel workforce-intelligence-own-attendance"><header><h2>My attendance records</h2></header><p class="faint">Review records linked to your account. A correction request is appended to the record and sent to another authorized reviewer; it does not change the original entry.</p>${eventsResult?.error ? errorBlock(eventsResult.error, 'your attendance records') : rows}<div data-own-attendance-message aria-live="polite"></div></section>`;
}

export async function appendOwnAttendanceCorrections(page, me) {
  if (!new Set(me?.permissions ?? []).has('workforce.attendance-correction.request')) return;
  const now = new Date();
  const from = new Date(now.getTime() - 366 * DAY);
  const query = `from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(new Date(now.getTime() + DAY).toISOString())}`;
  const readOwnEvents = () => tryGet(`/api/workforce/my-attendance-events?${query}`);
  let result = await readOwnEvents();
  page.innerHTML += renderOwnAttendanceCorrections(result);
  page.addEventListener('submit', async event => {
    const form = event.target.closest('[data-own-attendance-correction]');
    if (!form) return;
    event.preventDefault();
    const eventId = form.dataset.ownAttendanceCorrection;
    const kind = form.querySelector('[name="kind"]')?.value;
    const observedAt = form.querySelector('[name="observedAt"]')?.value ?? '';
    const reason = form.querySelector('[name="reason"]')?.value?.trim() ?? '';
    const resultNode = form.querySelector('[data-own-attendance-result]');
    const submit = form.querySelector('button[type="submit"]');
    if (!reason) { if (resultNode) resultNode.textContent = 'Add a reason for the request.'; return; }
    if (submit) submit.disabled = true;
    if (resultNode) resultNode.textContent = 'Submitting for independent review…';
    try {
      await command(`/api/workforce/my-attendance-events/${encodeURIComponent(eventId)}/correction`, {
        kind, observedAt: observedAt ? new Date(observedAt).toISOString() : null, reasonCode: 'employee-correction-request',
      }, reason);
      result = await readOwnEvents();
      const section = page.querySelector('.workforce-intelligence-own-attendance');
      if (section) section.outerHTML = renderOwnAttendanceCorrections(result);
    } catch (error) {
      if (submit) submit.disabled = false;
      if (resultNode) resultNode.textContent = error?.message ?? 'The correction request could not be submitted.';
    }
  });
}

/** Prerequisites and today's coverage of a target qualification, shown in the nomination form before anyone is nominated. */
export function readinessHtml(r) {
  const rows = r.prerequisites.map(p => `<li>${p.held ? '✓' : '✗'} ${esc(p.name)} (${esc(p.code)})${p.group ? ` <span class="faint">one of ${esc(p.group)}</span>` : ''}</li>`).join('');
  return `<div><b>${esc(r.qualificationName)}</b>: ${r.alreadyHeld ? 'already held' : r.prerequisitesMet ? 'prerequisites met' : 'prerequisites missing'}</div>
    ${rows ? `<ul class="workforce-intelligence-list">${rows}</ul>` : '<div class="faint">No prerequisites are configured for this qualification.</div>'}
    <div>${esc(r.benefit)}</div>`;
}

/** Current qualification evidence by configured machine/activity; this is not a staffing approval. */
export function workforceCoverageMatrixHtml(sources, options, me, at = options.asOf ?? new Date()) {
  sources = sources.matrixSources ?? sources;
  const permissions = new Set(me?.permissions ?? []);
  const workers = sources.workers?.data ?? [];
  const resources = sources.resources?.data;
  const allProfiles = (resources?.machineActivities ?? []).map(profile => ({
    ...profile,
    machine: (resources.machines ?? []).find(machine => machine.code === profile.machineCode),
  })).filter(profile => profile.machine && (!options.workCenterCode || profile.machine.workCenterCode === options.workCenterCode)
    && (!options.machineCode || profile.machineCode === options.machineCode)
    && (!options.qualificationCode || (profile.operatorQualifications ?? []).includes(options.qualificationCode)))
    .sort((a, b) => a.machine.workCenterCode.localeCompare(b.machine.workCenterCode)
      || a.machineCode.localeCompare(b.machineCode) || a.activityCode.localeCompare(b.activityCode));
  const maxRows = 2500;
  const profileLimit = Math.max(1, Math.floor(maxRows / Math.max(1, workers.length)));
  const profiles = allProfiles.slice(0, profileLimit);
  const qualificationsKnown = Array.isArray(sources.qualifications?.data) && !sources.qualifications?.error;
  const authorizationsKnown = Array.isArray(sources.machineAuthorizations?.data) && !sources.machineAuthorizations?.error;
  const availabilityKnown = !sources.unavailability?.error && Array.isArray(sources.unavailability?.data);
  const now = at.getTime();
  const siteName = options.siteCode ? options.sites.find(site => site.code === options.siteCode)?.name ?? options.siteCode : 'All authorized sites';
  const shiftName = options.shiftCode ? options.shifts.find(shift => shift.code === options.shiftCode)?.name ?? options.shiftCode : 'All shifts';
  const centerName = options.workCenterCode ? options.workCenters.find(center => center.code === options.workCenterCode)?.name ?? options.workCenterCode : 'All work centers';
  const qualifies = (record, code) => record.code === code && record.status === 'Qualified'
    && (!record.expiresAt || new Date(record.expiresAt).getTime() > now);
  const currentAuthorization = (personId, profile, qualificationCode) => (sources.machineAuthorizations?.data ?? []).some(record =>
    record.personId === personId && record.machineCode === profile.machineCode && record.activityCode === profile.activityCode
      && record.qualificationCode === qualificationCode && record.status === 'Authorized' && record.independentOperationAllowed === true
      && (!record.expiresAt || new Date(record.expiresAt).getTime() > now)
      && (!record.courseExpiresAt || new Date(record.courseExpiresAt).getTime() > now));
  const profileHtml = profiles.map((profile, index) => {
    const required = profile.operatorQualifications ?? [];
    const rows = workers.map(worker => {
      const records = (sources.qualifications?.data ?? []).filter(record => record.personId === worker.personId && required.includes(record.code));
      const activeQualification = records.find(record => qualifies(record, record.code));
      const expired = records.filter(record => record.status === 'Qualified' && record.expiresAt && new Date(record.expiresAt).getTime() <= now);
      const inTraining = records.filter(record => record.status === 'InTraining');
      const requirementsReadable = qualificationsKnown && (!profile.requiresMachineAuthorization || authorizationsKnown);
      const unavailableWindow = (sources.unavailability?.data ?? []).some(window => window.personId === worker.personId
        && new Date(window.startsAt).getTime() <= now && now < new Date(window.endsAt).getTime());
      const unavailable = worker.isActive === false || !!worker.unavailability || (availabilityKnown && unavailableWindow);
      let gate = !requirementsReadable ? 'Unknown'
        : unavailable ? 'Unavailable'
          : !required.length ? 'No qualification requirement listed'
          : activeQualification ? profile.requiresMachineAuthorization
              ? currentAuthorization(worker.personId, profile, activeQualification.code) ? (availabilityKnown ? 'Qualified · safe preview required' : 'Availability unknown') : 'Machine authorization required'
              : availabilityKnown ? 'Qualified · safe preview required' : 'Availability unknown'
              : inTraining.length ? 'Supervised training'
                : expired.length ? 'Certification expired'
                  : records.some(record => ['Suspended', 'Revoked'].includes(record.status)) ? 'Suspended / revoked' : 'Unqualified';
      const tone = gate.startsWith('Qualified') ? 'ok' : ['Unavailable', 'Certification expired', 'Suspended / revoked', 'Machine authorization required'].includes(gate) ? 'warn' : 'none';
      const qualificationText = !qualificationsKnown ? 'Qualification source unavailable'
        : activeQualification ? `${activeQualification.code}${activeQualification.expiresAt ? ` · expires ${formatDate(activeQualification.expiresAt)}` : ' · no expiry recorded'}`
          : inTraining.length ? inTraining.map(record => `${record.code} · in training`).join(', ')
            : expired.length ? expired.map(record => `${record.code} · expired ${formatDate(record.expiresAt)}`).join(', ')
              : records.length ? records.map(record => `${record.code} · ${record.status}`).join(', ') : 'No matching qualification record';
      const authText = !profile.requiresMachineAuthorization ? 'Not required by this profile'
        : !authorizationsKnown ? 'Authorization source unavailable'
          : !activeQualification ? 'Requires current matching qualification'
            : currentAuthorization(worker.personId, profile, activeQualification.code) ? 'Authorized for independent operation'
              : 'No current independent authorization found';
      const presenceText = unavailable ? 'Unavailable now'
        : worker.presenceKind && worker.presentUntil && new Date(worker.presentUntil).getTime() > now ? `Presence recorded · ${esc(worker.presenceKind)}`
          : worker.presenceKind ? 'Presence evidence expired' : 'Presence not confirmed';
      return `<tr><td>${esc(worker.displayName)}<div class="faint">${esc(worker.jobTitle ?? worker.homeWorkCenterCode ?? '')}</div></td><td>${esc(worker.shiftCode ?? 'Shift not recorded')}</td><td>${esc(qualificationText)}</td><td>${esc(authText)}</td><td>${presenceText}</td><td><span class="pill ${tone}">${esc(gate)}</span></td></tr>`;
    }).join('');
    const requirementText = required.length ? required.join(' or ') : 'No operator qualification configured';
    const preview = permissions.has('planning.readiness.read') && (permissions.has('workforce.supervise') || permissions.has('workforce.manage'))
      ? `<button class="btn small" type="button" data-wi-matrix-preview data-safety-area="${escapeAttr(profile.machine.safetyAreaCode ?? '')}" data-work-center="${escapeAttr(profile.machine.workCenterCode)}" data-machine="${escapeAttr(profile.machineCode)}" data-activity="${escapeAttr(profile.activityCode)}" data-shift="${escapeAttr(options.shiftCode ?? '')}">Preview safe candidates &amp; capacity</button><div data-wi-matrix-preview-result aria-live="polite"></div>`
      : '';
    return `<details class="workforce-intelligence-matrix-profile"><summary><b>${esc(profile.machine.workCenterCode)} · ${esc(profile.machineCode)} / ${esc(profile.activityCode)}</b><span>Requires ${esc(requirementText)}${profile.requiresMachineAuthorization ? ' · machine authorization' : ''}</span><span>${workers.length} employee record${workers.length === 1 ? '' : 's'} · open for evidence</span></summary>
      <div class="workforce-intelligence-matrix-content"><p class="faint">${esc(profile.machine.name)} · qualification and current authorization evidence only. Assignment safety, required crew, active work and recency-based capacity are checked separately by the safe preview.</p>
        ${preview}<div class="table-wrap"><table class="table"><thead><tr><th>Employee</th><th>Shift</th><th>Qualification</th><th>Machine authorization</th><th>Presence / availability</th><th>Qualification gate</th></tr></thead><tbody>${rows || '<tr><td colspan="6">No employees in the current filters.</td></tr>'}</tbody></table></div>
      </div></details>`;
  }).join('');
  const capNote = allProfiles.length > profiles.length ? `<p class="notice warn small">Showing ${profiles.length} of ${allProfiles.length} configured machine/activity profiles to keep this view responsive. Filter by work center or machine to inspect the rest.</p>` : '';
  const sourceMissing = !resources || !Array.isArray(sources.workers?.data)
    ? 'Workforce or resource records are unavailable; coverage is unknown.'
    : !profiles.length ? 'No configured machine/activity profiles match these filters.' : '';
  return `<section class="workforce-intelligence-matrix"><header><h3>Operation coverage matrix</h3><span class="spacer"></span><span class="muted">Site: ${esc(siteName)} · Shift: ${esc(shiftName)} · Scope: ${esc(centerName)} · Page data reads completed ${esc(formatMoment(at))}</span></header>
    <p class="faint">Expand an operation to compare each employee's recorded qualifications and machine authorization. “Qualified” is not a placement approval; presence, required crew, safety, current workload and capacity recency need a separate safe preview. No employee score is calculated.</p>
    <div class="workforce-intelligence-matrix-legend" aria-label="Coverage matrix status legend"><span class="pill ok">Qualified · safe preview required</span><span class="pill warn">Training / expired / unavailable / authorization gap</span><span class="pill none">Unknown / unqualified</span></div>
    ${sourceMissing ? `<p class="notice warn">${esc(sourceMissing)}</p>` : `${capNote}${profileHtml}`}
  </section>`;
}

/** Calendar quarters are resolved by the server in the plant's time zone; the other periods are chosen here. */
export function attendanceSummaryUrl(period, now) {
  if (period === 'quarter' || period === 'previous-quarter') return `/api/workforce/attendance-intelligence?period=${period}`;
  const iso = date => encodeURIComponent(date.toISOString());
  return `/api/workforce/attendance-intelligence?from=${iso(attendancePeriodStart(period, now))}&to=${iso(now)}`;
}

function attendancePeriodStart(period, now) {
  const start = new Date(now);
  start.setHours(0, 0, 0, 0);
  if (period === 'month') start.setDate(1);
  else if (period === 'quarter') {
    start.setMonth(Math.floor(start.getMonth() / 3) * 3, 1);
  } else if (period === 'year') start.setMonth(0, 1);
  else start.setTime(now.getTime() - 90 * DAY);
  return start;
}

function render(summary, sources, options, me, selectedPersonId = null, personOperationHistory = null) {
  const permissions = new Set(me?.permissions ?? []);
  const canPreviewMaterialImpact = permissions.has('inventory.read') && permissions.has('planning.schedule.read');
  const issues = [];
  if (summary.expiring.length) {
    issues.push(...summary.expiring.slice(0, 8).map(row => ({
      severity: row.daysRemaining <= 7 ? 'urgent' : 'attention',
      title: `${row.code} expires in ${row.daysRemaining} day${row.daysRemaining === 1 ? '' : 's'}`,
      detail: `${row.workerName} · review the evidence and schedule reassessment if required.`,
      evidenceReferences: [`PersonQualification/${row.personId}/${row.code}`],
    })));
  }
  if (summary.attendanceRisks?.length) issues.push(...summary.attendanceRisks.slice(0, 8));
  if (summary.capabilityGaps.length) {
    issues.push(...summary.capabilityGaps.slice(0, 8).map(row => ({
      severity: 'urgent',
      title: `${row.machineCode} · ${row.activityCode} has no listed qualified worker`,
      detail: `${row.workCenterCode} · ${row.shiftName} · requires ${row.requiredQualifications.join(' or ')}${row.requiresMachineAuthorization ? ' plus a current machine-specific authorization' : ''}.`,
      evidenceReferences: [`MachineActivity/${row.machineCode}/${row.activityCode}`],
    })));
  }
  if (summary.trainingRecommendations?.length) {
    issues.push(...summary.trainingRecommendations.slice(0, 8).map(row => ({
      severity: 'attention',
      title: `${row.workerName} may be ready for a ${row.qualificationCode} practical assessment`,
      detail: `${row.rationale} ${row.expectedPlantBenefit}`,
      evidenceReferences: row.evidenceReferences,
    })));
  }
  if (summary.blockers.length) {
    issues.push(...summary.blockers.filter(row => ['Workforce', 'Safety'].includes(row.primaryConstraint)).slice(0, 8).map(row => ({
      severity: row.primaryConstraint === 'Safety' ? 'urgent' : 'attention',
      title: `WO ${row.orderNumber} · operation ${row.sequence} is blocked by ${row.primaryConstraint}`,
      detail: row.why,
      evidenceReferences: row.evidenceReferences,
    })));
  }
  issues.push(...(summary.materialDisruptions ?? []));
  const byUrgency = (a, b) => (a.severity === 'urgent' ? 0 : 1) - (b.severity === 'urgent' ? 0 : 1) || a.title.localeCompare(b.title);
  issues.sort(byUrgency);
  const coverageKnown = summary.coverage !== null;
  const workersKnown = summary.sources.workers;
  const qualificationsKnown = summary.sources.qualifications;
  const materialSourcesKnown = summary.sources.materialTasks && summary.sources.suspectReports;
  const expiringRows = summary.expiring.length
    ? `<ul class="workforce-intelligence-list">${summary.expiring.slice(0, 6).map(row => `<li><a href="#/performance?personId=${encodeURIComponent(row.personId)}"><b>${esc(row.workerName)}</b></a><span>${esc(row.code)} · expires ${formatDate(row.expiresAt)} (${row.daysRemaining}d)</span></li>`).join('')}</ul>`
    : `<p class="faint">${qualificationsKnown ? 'No qualifications expire in this window.' : 'Qualification records are not visible to this account.'}</p>`;
  const trainingRecommendationRows = (summary.trainingRecommendations ?? []).filter(row => !options.qualificationCode || row.qualificationCode === options.qualificationCode).slice(0, 8).map(row => `<li><a href="#/performance?personId=${encodeURIComponent(row.personId)}"><b>${esc(row.workerName)}</b></a><span>${esc(row.qualificationCode)} · ${esc(row.machineCode)} / ${esc(row.activityCode)} · ${esc(row.workCenterCode)} / ${esc(row.shiftCode)}</span><span>${esc(row.rationale)}</span>${row.evidenceReferences?.length ? `<span>Evidence: ${row.evidenceReferences.map(renderEvidenceReference).join(' · ')}</span>` : ''}</li>`).join('');
  const trainingForecastRows = summary.trainingForecast?.length
    ? `<ul class="workforce-intelligence-list">${summary.trainingForecast.slice(0, 10).map(row => `<li><b>${esc(row.date)} · ${esc(row.shiftCode)} · ${esc(row.machineCode)} / ${esc(row.activityCode)}</b><span>${esc(row.workCenterCode)} · ${esc(row.qualificationCode)} · ${row.traineeCount} scheduled learner(s) · ${row.trainerCount} qualified trainer(s) · ${row.qualifiedCount} independently qualified worker(s)</span>${row.canReviewWindow ? `<span>Largest shared production-free block: ${fmtNumber(row.largestSharedFreeMinutes)} min · ${esc(formatMoment(row.availableFrom))}–${esc(formatMoment(row.availableThrough))}</span>` : ''}<span>${esc(row.detail)}</span></li>`).join('')}</ul>`
    : summary.trainingForecast ? '<p class="faint">No scheduled learner-and-qualification gap pairs were found in the next 14 days.</p>'
      : '<p class="faint">Training windows are unknown because the approved production schedule, shift schedule, availability, or competency sources are not readable.</p>';
  const qualificationMatrixRows = qualificationsKnown
    ? (sources.workers.data ?? []).map(worker => {
      const rows = (sources.qualifications.data ?? []).filter(record => record.personId === worker.personId);
      return `<tr><td><a href="#/performance?personId=${encodeURIComponent(worker.personId)}">${esc(worker.displayName)}</a><div class="faint">${esc(worker.jobTitle ?? worker.homeWorkCenterCode ?? '')}</div></td><td>${rows.length ? rows.map(record => `<span class="pill ${record.status === 'Qualified' ? (record.expiresAt && new Date(record.expiresAt).getTime() <= Date.now() ? 'bad' : 'ok') : record.status === 'InTraining' ? 'warn' : 'none'}">${esc(record.code)} · ${esc(record.status)}${record.expiresAt ? ` · to ${formatDate(record.expiresAt)}` : ''}</span>`).join(' ') : '<span class="faint">No recorded qualifications</span>'}</td></tr>`;
    }).join('')
    : '';
  const alertRows = issues.length
    ? `<ul class="workforce-intelligence-alerts" aria-live="polite">${issues.slice(0, 10).map(item => `<li class="${item.severity === 'urgent' ? 'urgent' : ''}"><span class="workforce-intelligence-mark" aria-hidden="true">!</span><div><b>${esc(item.title)}</b><span>${esc(item.detail)}</span>${item.evidenceReferences?.length ? `<span>Evidence: ${item.evidenceReferences.map(renderEvidenceReference).join(' · ')}</span>` : ''}${canPreviewMaterialImpact && item.canPreviewReceiptImpact ? `<button class="btn small" type="button" data-wi-material-recovery="${escapeAttr(item.taskId)}">Preview receipt schedule impact</button><div data-wi-material-recovery-result="${escapeAttr(item.taskId)}" aria-live="polite"></div>` : ''}</div></li>`).join('')}</ul>`
    : `<p class="faint">${workersKnown && qualificationsKnown ? 'No current qualification or readiness alerts from the available sources.' : 'Alerts are limited until Workforce and Competency records are readable.'}${materialSourcesKnown ? '' : ' Material and suspect-report sources are not visible to this account, so those disruptions are not included.'}</p>`;
  const laborRows = summary.labor.slice(0, 6);
  const laborTable = laborRows.length
    ? `<div class="table-wrap"><table class="table"><thead><tr><th>Work order / op</th><th>Work center</th><th>Operator hours</th><th>Training hours</th><th>Good / scrap qty</th><th>Target</th></tr></thead><tbody>${laborRows.map(row => `<tr><td>WO ${row.orderNumber} / ${row.sequence}</td><td>${esc(row.workCenterCode)}</td><td>${fmtNumber(row.operatorHours)}</td><td>${fmtNumber(row.trainingHours)}</td><td>${fmtNumber(row.goodQuantity)} / ${fmtNumber(row.scrapQuantity)}</td><td><span class="pill none">N/A</span></td></tr>`).join('')}</tbody></table></div>`
    : `<p class="faint">${sources.activity ? 'No operation labor was recorded in the last 30 days.' : 'Shop-floor activity is unavailable to this account.'}</p>`;
  const selectedWorker = (sources.workers.data ?? []).find(worker => worker.personId === selectedPersonId);
  const personHistory = personHistoryHtml(selectedPersonId, selectedWorker, personOperationHistory);
  const shiftChoices = options.shifts.map(shift => `<option value="${escapeAttr(shift.code)}" ${options.shiftCode === shift.code ? 'selected' : ''}>${esc(shift.name)}</option>`).join('');
  const centerChoices = options.workCenters.map(center => `<option value="${escapeAttr(center.code)}" ${options.workCenterCode === center.code ? 'selected' : ''}>${esc(center.name)}</option>`).join('');
  const siteChoices = (options.sites ?? []).map(site => `<option value="${escapeAttr(site.code)}" ${options.siteCode === site.code ? 'selected' : ''}>${esc(site.name)}</option>`).join('');
  const areaChoices = (options.areas ?? []).map(area => `<option value="${escapeAttr(area.code)}" ${options.areaCode === area.code ? 'selected' : ''}>${esc(area.name)}</option>`).join('');
  const machineChoices = (options.machines ?? []).map(machine => `<option value="${escapeAttr(machine.code)}" ${options.machineCode === machine.code ? 'selected' : ''}>${esc(machine.code)} · ${esc(machine.name)}</option>`).join('');
  const roleChoices = (options.jobTitles ?? []).map(title => `<option value="${escapeAttr(title)}" ${options.roleTitle === title ? 'selected' : ''}>${esc(title)}</option>`).join('');
  const qualificationChoices = (options.qualificationTypes ?? []).map(type => `<option value="${escapeAttr(type.code)}" ${options.qualificationCode === type.code ? 'selected' : ''}>${esc(type.code)}${type.name ? ` · ${esc(type.name)}` : ''}</option>`).join('');
  const departmentChoices = (options.departments ?? []).map(department => `<option value="${escapeAttr(department.code)}" ${options.department === department.code ? 'selected' : ''}>${esc(department.name)}</option>`).join('');
  const attendanceCount = value => value == null ? '—' : fmtNumber(value);
  const canSchedule = permissions.has('workforce.attendance-schedule.manage');
  const canPolicy = permissions.has('workforce.attendance-policy.manage');
  const canReview = permissions.has('workforce.attendance-classification.review');
  const canNominate = permissions.has('workforce.development.nominate');
  const canReviewNominations = permissions.has('workforce.development.review');
  const canReadNominations = permissions.has('workforce.development.read');
  const canReadCompensation = permissions.has('workforce.compensation.read');
  const canRecoverCoverage = permissions.has('planning.readiness.read')
    && (permissions.has('workforce.supervise') || permissions.has('workforce.manage'));
  const recoveryRows = canRecoverCoverage && coverageKnown && summary.capabilityGaps.length
    ? summary.capabilityGaps.slice(0, 8).map(row => {
      const machine = (sources.resources?.data?.machines ?? []).find(item => item.code === row.machineCode);
      const scheduledShiftChoices = options.shifts.map(shift => `<option value="${escapeAttr(shift.code)}" ${shift.code === row.shiftCode ? 'selected' : ''}>${esc(shift.name)}</option>`).join('');
      return `<article class="workforce-intelligence-recovery-item">
        <div><b>${esc(row.machineCode)} · ${esc(row.activityCode)}</b><span>${esc(row.workCenterCode)} · ${esc(row.shiftName)} · ${esc(row.requiredQualifications.join(' or '))}${row.requiresMachineAuthorization ? ' · machine authorization required' : ''}</span></div>
        <button class="btn small" type="button" data-wi-recovery data-safety-area="${escapeAttr(machine?.safetyAreaCode ?? '')}" data-work-center="${escapeAttr(row.workCenterCode)}" data-machine="${escapeAttr(row.machineCode)}" data-activity="${escapeAttr(row.activityCode)}">Find safe candidates</button>
        <details class="workforce-intelligence-recovery-scheduled"><summary>Plan for an upcoming shift</summary><div><label>Plant shift date<input class="input" type="date" data-wi-recovery-date required></label><label>Shift<select class="input" data-wi-recovery-shift required>${scheduledShiftChoices}</select></label><button class="btn small" type="button" data-wi-recovery data-projected="true" data-safety-area="${escapeAttr(machine?.safetyAreaCode ?? '')}" data-work-center="${escapeAttr(row.workCenterCode)}" data-machine="${escapeAttr(row.machineCode)}" data-activity="${escapeAttr(row.activityCode)}">Preview scheduled candidates</button></div></details>
        <div data-wi-recovery-result aria-live="polite"></div></article>`;
    }).join('')
    : '';
  const allEvents = sources.pending?.data ?? [];
  const pendingEvents = allEvents.filter(item => options.reviewState === 'all'
    ? item.reviewState === 'PendingReview' || item.reviewState === 'Disputed'
    : item.reviewState === options.reviewState);
  const supersededEventIds = new Set(allEvents.map(item => item.supersedesEventId).filter(Boolean));
  const currentEventIds = new Set(allEvents.filter(item => !supersededEventIds.has(item.eventId)).map(item => item.opportunityId));
  const missedOpportunities = (sources.opportunities?.data ?? []).filter(item => item.state === 'Scheduled'
    && new Date(item.scheduledEnd).getTime() < Date.now() && !currentEventIds.has(item.opportunityId)).slice(0, 12);
  const eventKinds = ['CallOut', 'NoShow', 'ApprovedPto', 'ProtectedLeave', 'ExcusedAbsence', 'EarlyDeparture'];
  const correctionKinds = [...eventKinds, 'Arrival', 'ScheduleChange'];
  const pendingRows = pendingEvents.map(item => {
    const name = sources.workerNames.get(item.personId) ?? `Worker ${item.personId.slice(0, 8)}`;
    return `<article class="workforce-intelligence-review-item"><div><b>${esc(name)} · ${esc(item.kind)}</b><span>${esc(item.source)} · recorded ${formatDate(item.recordedAt)} · ${item.observedAt ? `observed ${formatDate(item.observedAt)}` : 'time needs verification'}</span></div>
      <div class="workforce-intelligence-review-actions">${canReview && ['PendingReview', 'Disputed'].includes(item.reviewState) ? `<button class="btn small primary" type="button" data-wi-review="${escapeAttr(item.eventId)}" data-decision="Verified">Verify</button><button class="btn small" type="button" data-wi-review="${escapeAttr(item.eventId)}" data-decision="Disputed">Dispute</button>` : item.reviewState === 'Verified' ? '<span class="pill ok">Verified</span>' : ''}</div>
      <details><summary>Submit a correction</summary><div class="workforce-intelligence-correction"><label>Correct classification<select class="input" data-wi-correct-kind="${escapeAttr(item.eventId)}">${correctionKinds.map(kind => `<option value="${kind}" ${kind === item.kind ? 'selected' : ''}>${kind}</option>`).join('')}</select></label><label>Observed at<input class="input" type="datetime-local" data-wi-correct-time="${escapeAttr(item.eventId)}" value="${localDateTime(item.observedAt ?? item.recordedAt)}"></label><label>Audit reason<input class="input" data-wi-correct-reason="${escapeAttr(item.eventId)}" value="Correct source classification" maxlength="240"></label><button class="btn small" type="button" data-wi-correct="${escapeAttr(item.eventId)}">Submit correction</button></div></details>
    </article>`;
  }).join('');
  const nominationRows = (sources.nominations?.data ?? []).map(item => {
    const reviewChoices = ({ Draft: ['Submitted'], Submitted: ['InReview'], InReview: ['Approved', 'Declined', 'Deferred'], Deferred: ['InReview'],
      Approved: ['TrainingScheduled'], TrainingScheduled: ['Completed'], Completed: ['PracticalAssessment'], PracticalAssessment: ['Authorized'] })[item.status] ?? [];
    const canAdvance = item.status === 'Draft' || canReviewNominations;
    return `<article class="workforce-intelligence-review-item"><div><b>${esc(sources.workerNames.get(item.personId) ?? 'Worker record unavailable')} · ${esc(item.kind.replaceAll(/([a-z])([A-Z])/g, '$1 $2'))}</b><span>${esc(item.pathway)} · ${esc(item.status)} · updated ${formatDate(item.updatedAt)}</span>
      <p>${esc(item.rationale)}</p><p class="faint">Plant benefit: ${esc(item.expectedPlantBenefit)}${item.estimatedTrainingHours != null ? ` · ${fmtNumber(item.estimatedTrainingHours)} training hours` : ''}${item.estimatedCost != null ? ` · ${esc(item.currency ?? '')} ${fmtNumber(item.estimatedCost)}` : ''}</p>
      ${item.evidenceReferences?.length ? `<div class="faint">Evidence references: ${item.evidenceReferences.map(renderEvidenceReference).join(' · ')}</div>` : '<div class="faint">No source evidence references attached.</div>'}</div>
      ${canAdvance && reviewChoices.length ? `<div class="workforce-intelligence-review-actions"><select class="input" data-wi-nom-status="${escapeAttr(item.nominationId)}" aria-label="Next development packet status">${reviewChoices.map(status => `<option value="${status}">${status.replaceAll(/([a-z])([A-Z])/g, '$1 $2')}</option>`).join('')}</select><textarea class="input" data-wi-nom-note="${escapeAttr(item.nominationId)}" rows="2" maxlength="800" placeholder="Human review note" aria-label="Human review note" required></textarea><button class="btn small primary" type="button" data-wi-nom-advance="${escapeAttr(item.nominationId)}">Record status</button></div>` : ''}
    </article>`;
  }).join('');
  const nextRevision = Math.max(0, ...(sources.policies?.data ?? []).map(policy => policy.revision)) + 1;
  const policyRows = (sources.policies?.data ?? []).map(policy => `<li><b>${esc(policy.policyCode)} · r${policy.revision}</b><span>Effective ${formatDate(policy.effectiveFrom)} · ${policy.graceMinutes} minute grace${policy.effectiveThrough ? ` · through ${formatDate(policy.effectiveThrough)}` : ''}</span></li>`).join('');
  const scheduleFrom = new Date(); scheduleFrom.setHours(0, 0, 0, 0);
  const scheduleThrough = new Date(scheduleFrom.getTime() + 14 * DAY);
  const filters = `<div class="workforce-intelligence-filters"><label>Plant<select class="input" data-wi-filter="site"><option value="">All plants</option>${siteChoices}</select></label>
        <label>Shift<select class="input" data-wi-filter="shift"><option value="">All shifts</option>${shiftChoices}</select></label>
        <label>Work center<select class="input" data-wi-filter="center"><option value="">All centers</option>${centerChoices}</select></label>
        <label>Machine<select class="input" data-wi-filter="machine"><option value="">All machines</option>${machineChoices}</select></label>
        <label>Qualification window<select class="input" data-wi-filter="expiry"><option value="30" ${options.expiryDays === 30 ? 'selected' : ''}>30 days</option><option value="60" ${options.expiryDays === 60 ? 'selected' : ''}>60 days</option><option value="90" ${options.expiryDays === 90 ? 'selected' : ''}>90 days</option></select></label>
        <details class="workforce-intelligence-filter-more"><summary>More filters</summary><div>
          <label>Department<select class="input" data-wi-filter="department"><option value="">All departments</option>${departmentChoices}</select></label>
          <label>Safety area<select class="input" data-wi-filter="area"><option value="">All areas</option>${areaChoices}</select></label>
          <label>Role / job title<select class="input" data-wi-filter="role"><option value="">All roles</option>${roleChoices}</select></label>
          <label>Training type<select class="input" data-wi-filter="qualification"><option value="">All training</option>${qualificationChoices}</select></label>
          <label>Worker status<select class="input" data-wi-filter="workerStatus"><option value="all" ${options.workerStatus === 'all' ? 'selected' : ''}>All workers</option><option value="active" ${options.workerStatus === 'active' ? 'selected' : ''}>Active</option><option value="inactive" ${options.workerStatus === 'inactive' ? 'selected' : ''}>Inactive</option></select></label>
          <label>Review status<select class="input" data-wi-filter="reviewState"><option value="all" ${options.reviewState === 'all' ? 'selected' : ''}>Pending and disputed</option><option value="PendingReview" ${options.reviewState === 'PendingReview' ? 'selected' : ''}>Pending review</option><option value="Verified" ${options.reviewState === 'Verified' ? 'selected' : ''}>Verified</option><option value="Disputed" ${options.reviewState === 'Disputed' ? 'selected' : ''}>Disputed</option></select></label>
        </div></details></div>`;
  const overview = `
      <div class="kpis kpis-4 workforce-intelligence-kpis">
        <div class="kpi"><div><div class="t">Current qualifications</div><div class="v">${qualificationsKnown ? fmtNumber(summary.qualificationCounts.current) : 'N/A'}</div><div class="why">Verified, unexpired Competency records</div></div></div>
        <div class="kpi ${summary.expiring.length ? 'attention' : ''}"><div><div class="t">Expiring in ${summary.expiryDays} days</div><div class="v">${qualificationsKnown ? fmtNumber(summary.qualificationCounts.expiring) : 'N/A'}</div><div class="why">Review evidence before scheduling reassessment</div></div></div>
        <div class="kpi ${summary.qualificationCounts.expired ? 'blocked' : ''}"><div><div class="t">Expired qualifications</div><div class="v">${qualificationsKnown ? fmtNumber(summary.qualificationCounts.expired) : 'N/A'}</div><div class="why">Expired records do not count as authorization</div></div></div>
        <div class="kpi ${summary.capabilityGaps.length ? 'blocked' : ''}"><div><div class="t">Uncovered machine / shift capabilities</div><div class="v">${coverageKnown ? fmtNumber(summary.capabilityGaps.length) : 'N/A'}</div><div class="why">A listed machine profile with no current qualified worker</div></div></div>
      </div>
      <div class="workforce-intelligence-columns">
        <section><h3>Daily action alerts <span class="badge ${issues.length ? 'orange' : 'gray'}">${issues.length}</span></h3>${alertRows}${materialSourcesKnown ? '' : '<p class="faint">Material task and suspect-report signals are unavailable to this account.</p>'}</section>
        <section><h3>Qualification review</h3>${expiringRows}<p class="faint workforce-intelligence-note">${fmtNumber(summary.qualificationCounts.inTraining)} record(s) are marked In Training. In Training does not grant independent authorization.</p></section>
      </div>`;
  const training = `
      <section class="workforce-intelligence-forecast"><h3>Upcoming training windows · next 14 days</h3><p class="faint">Forecasts compare scheduled workers and trainer credentials with the approved production schedule to find shared time clear of assigned work on the pair and training machine. A forecast is a review opportunity; it does not book training, estimate course duration, or assume employee interest.</p>${trainingForecastRows}</section>
      ${trainingRecommendationRows ? `<section><h3>Human-reviewed training opportunities</h3><ul class="workforce-intelligence-list">${trainingRecommendationRows}</ul></section>` : ''}
      <section><h3>Employee qualification matrix</h3><p class="faint workforce-intelligence-note">${fmtNumber(summary.qualificationCounts.inTraining)} record(s) are In Training; they grant no capability until certified in Competency.</p>${qualificationMatrixRows ? `<div class="table-wrap"><table class="table"><thead><tr><th>Employee / profile</th><th>Recorded qualification status</th></tr></thead><tbody>${qualificationMatrixRows}</tbody></table></div>` : '<p class="faint">Qualification records are not available to this account.</p>'}</section>`;
  const coverageMatrix = workforceCoverageMatrixHtml(sources, options, me);
  const coverage = recoveryRows
    ? `<section class="workforce-intelligence-recovery"><h3>Safe coverage options</h3><p class="faint">Ranked from current visible workers. The preview checks qualification, machine authorization, active source work, equipment/tooling, and safety composition; it does not commit assignments.</p>${recoveryRows}</section>`
    : `<p class="faint">${!canRecoverCoverage ? 'Safe candidate previews need readiness read and Workforce supervise or manage permissions.' : coverageKnown ? 'Every listed machine and shift capability has a current qualified worker in this view.' : 'Coverage is unknown until resources and qualifications are readable.'}</p>`;
  const attendanceTab = `
        <div class="workforce-intelligence-attendance"><b>Attendance reliability${summary.attendance.period ? ` · ${esc(summary.attendance.period)}` : ''}: ${esc(summary.attendance.status)}</b><span>${esc(summary.attendance.detail)}</span></div>
        ${sources.attendance?.error ? errorBlock(sources.attendance.error, 'attendance summary') : ''}
        <div class="workforce-intelligence-attendance-stats" aria-label="Attendance calculation counts">
          <span>Scheduled: <b>${attendanceCount(summary.attendance.scheduledOpportunities)}</b></span><span>Eligible arrivals: <b>${attendanceCount(summary.attendance.eligibleOpportunities)}</b></span>
          <span>On time: <b>${attendanceCount(summary.attendance.onTimeArrivals)}</b></span><span>Verified late: <b>${attendanceCount(summary.attendance.verifiedLateArrivals)}</b></span>
          <span>Call-outs: <b>${attendanceCount(summary.attendance.verifiedCallOuts)}</b></span><span>No-shows: <b>${attendanceCount(summary.attendance.verifiedNoShows)}</b></span>
          <span>Excluded: <b>${attendanceCount(summary.attendance.excludedOpportunities)}</b></span><span>Pending review: <b>${attendanceCount(summary.attendance.pendingReviewOpportunities)}</b></span>
        </div>
        ${summary.upcomingAbsence.length ? `<p class="faint">${summary.upcomingAbsence.length} planned availability window(s) remain in the next 30 days. The source records contain no private absence reasons.</p>` : ''}
        <div class="workforce-intelligence-attendance-tools"><h3>Attendance schedule and policy</h3>
          <label>Review period<select class="input" data-wi-filter="period"><option value="quarter" ${options.attendancePeriod === 'quarter' ? 'selected' : ''}>Calendar quarter to date</option><option value="previous-quarter" ${options.attendancePeriod === 'previous-quarter' ? 'selected' : ''}>Previous calendar quarter</option><option value="month" ${options.attendancePeriod === 'month' ? 'selected' : ''}>Month to date</option><option value="rolling90" ${options.attendancePeriod === 'rolling90' ? 'selected' : ''}>Rolling 90 days</option><option value="year" ${options.attendancePeriod === 'year' ? 'selected' : ''}>Year to date</option></select></label>
          <p class="faint">The metric uses dated opportunities and the policy revision effective on each scheduled date. A missed punch remains pending review; no-show classifications require a completed shift and independent verification.</p>
          ${policyRows ? `<ul class="workforce-intelligence-list">${policyRows}</ul>` : '<p class="faint">No attendance policy is recorded yet.</p>'}
          ${canSchedule ? `<form data-wi-schedule-form class="workforce-intelligence-form"><label>From<input class="input" name="from" type="datetime-local" required value="${localDateTime(scheduleFrom)}"></label><label>Through<input class="input" name="through" type="datetime-local" required value="${localDateTime(scheduleThrough)}"></label><label>Audit reason<input class="input" name="reason" required value="Generate dated shift schedule" maxlength="240"></label><button class="btn small primary" type="submit">Generate shift opportunities</button></form>` : ''}
          ${canPolicy ? `<form data-wi-policy-form class="workforce-intelligence-form"><label>Policy code<input class="input" name="policyCode" required value="DEFAULT" maxlength="48"></label><label>Revision<input class="input" name="revision" type="number" min="1" required value="${nextRevision}"></label><label>Effective from<input class="input" name="effectiveFrom" type="datetime-local" required value="${localDateTime(new Date())}"></label><label>Grace minutes<input class="input" name="graceMinutes" type="number" min="0" max="240" required value="5"></label><label class="check"><input type="checkbox" name="includeCallouts"> Include verified call-outs in arrival denominator</label><label>Audit reason<input class="input" name="reason" required value="Publish attendance policy revision" maxlength="240"></label><button class="btn small" type="submit">Publish policy revision</button></form>` : ''}
        </div>
        ${canReview ? `<div class="workforce-intelligence-review"><h3>Attendance classifications · ${esc(options.reviewState === 'all' ? 'pending and disputed' : options.reviewState.replaceAll(/([a-z])([A-Z])/g, '$1 $2'))} <span class="badge ${pendingEvents.length ? 'orange' : 'gray'}">${pendingEvents.length}</span></h3>${sources.pending?.error ? errorBlock(sources.pending.error, 'attendance review queue') : pendingRows || '<p class="faint">No matching attendance classifications were found in this period.</p>'}</div>` : ''}
        ${permissions.has('workforce.attendance-classification.record') ? `<div class="workforce-intelligence-review"><h3>Unclassified completed shifts</h3><p class="faint">A missing punch remains unknown. A supervisor must choose a classification and submit it for independent review.</p>${sources.opportunities?.error ? errorBlock(sources.opportunities.error, 'attendance schedule') : missedOpportunities.length ? missedOpportunities.map(item => {
          const name = sources.workerNames.get(item.personId) ?? `Worker ${item.personId.slice(0, 8)}`;
          return `<article class="workforce-intelligence-review-item"><b>${esc(name)} · ${esc(item.shiftCode)} · ${formatDate(item.scheduledStart)}</b><div class="workforce-intelligence-correction"><label>Classification<select class="input" data-wi-classification-kind="${escapeAttr(item.opportunityId)}">${eventKinds.map(kind => `<option value="${kind}" ${kind === 'NoShow' ? 'selected' : ''}>${kind}</option>`).join('')}</select></label><label>Observed at<input class="input" type="datetime-local" data-wi-classification-time="${escapeAttr(item.opportunityId)}" value="${localDateTime(item.scheduledEnd)}"></label><label>Audit reason<input class="input" data-wi-classification-reason="${escapeAttr(item.opportunityId)}" value="Review completed shift record" maxlength="240"></label><button class="btn small" type="button" data-wi-classify="${escapeAttr(item.opportunityId)}">Submit for review</button></div></article>`;
        }).join('') : '<p class="faint">No completed, unclassified shifts in the current window.</p>'}</div>` : ''}`;
  const labor = `
      <section><h3>Recorded operation labor · last 30 days</h3>${laborTable}
      <p class="faint workforce-intelligence-note">Target hours and efficiency are N/A until an approved operation labor standard and reviewed quantity basis exist. Downtime and training time stay visible; no employee score is inferred.</p></section>
      ${personHistory}
      ${selectedWorker ? `<section class="workforce-intelligence-person-history"><header><h3>${esc(selectedWorker.displayName)} · cause-adjusted time and quality</h3><span class="spacer"></span><button class="btn small" data-wi-cause-performance="${escapeAttr(selectedWorker.personId)}" data-wi-cause-name="${escapeAttr(selectedWorker.displayName)}">Open</button></header><p class="faint">Paid time accounted for by labor, verified downtime and approved coded time; official and employee-attributable first-pass yield side by side. Factual reporting only.</p></section>` : '<p class="faint">Choose an employee card or a name in the qualification matrix to see that person’s operation history and cause-adjusted time.</p>'}`;
  const development = canReadNominations || canNominate ? `<section class="workforce-intelligence-development"><h3>Training and advancement packets</h3><p class="faint">Recommendations require documented human review. Approval does not grant a qualification or machine authorization; those remain in Competency.</p>
        ${canNominate ? `<form class="workforce-intelligence-form" data-wi-nomination-form><label>Employee<select class="input" name="personId" required><option value="">Choose employee</option>${(sources.workers.data ?? []).filter(worker => worker.isActive).map(worker => `<option value="${escapeAttr(worker.personId)}">${esc(worker.displayName)} · ${esc(worker.jobTitle ?? worker.homeWorkCenterCode ?? 'Worker')}</option>`).join('')}</select></label>
          <label>Packet type<select class="input" name="kind" required><option value="AdvancedTraining">Advanced training</option><option value="LeadReadiness">Lead responsibilities</option><option value="PromotionReadiness">Promotion readiness</option>${canReadCompensation ? '<option value="CompensationConsideration">Compensation consideration</option>' : ''}</select></label>
          <label>Target qualification<select class="input" name="qualificationCode" data-wi-readiness><option value="">None (general development)</option>${(sources.types?.data ?? []).map(type => `<option value="${escapeAttr(type.code)}">${esc(type.name)} (${esc(type.code)})</option>`).join('')}</select></label>
          <div class="faint" data-wi-readiness-result>Choose an employee and a target qualification to see prerequisites and today's coverage before nominating.</div>
          <label>Pathway<input class="input" name="pathway" maxlength="120" required placeholder="e.g. CNC setup practical assessment"></label>
          <label>Evidence based rationale<textarea class="input" name="rationale" maxlength="500" required></textarea></label>
          <label>Expected plant benefit<textarea class="input" name="benefit" maxlength="500" required placeholder="Coverage, quality, trainer capacity or delivery benefit"></textarea></label>
          <label>Evidence record references<input class="input" name="evidence" maxlength="1200" placeholder="Qualification/CNC-01, OrderOperation/1042/10"></label>
          <label>Estimated training hours<input class="input" name="hours" type="number" min="0" max="10000" step="0.25"></label>
          <label>Estimated cost<input class="input" name="cost" type="number" min="0" max="10000000" step="0.01"></label>
          <label>Currency<input class="input" name="currency" maxlength="3" placeholder="USD"></label>
          <label>Audit reason<input class="input" name="reason" maxlength="240" required value="Create development packet"></label>
          <button class="btn small primary" type="submit">Save draft packet</button></form>` : ''}
        ${sources.nominations?.error ? errorBlock(sources.nominations.error, 'development packets') : nominationRows || (canReadNominations ? '<p class="faint">No development packets are recorded.</p>' : '')}
      </section>` : '';
  const tabs = [
    { key: 'overview', label: 'Overview', badge: issues.length, html: overview },
    ...(options.extraTabs ?? []).map(tab => ({ key: tab.key, label: tab.label, badge: tab.badge ?? null, html: `<div data-wi-extra="${escapeAttr(tab.key)}"></div>` })),
    ...(options.employeesTab === false ? [] : [{ key: 'employees', label: 'Employees', badge: null, html: '<div data-wi-employee-cards><p class="faint">Open this tab to gather each employee’s review card.</p></div>' }]),
    { key: 'training', label: 'Training & qualifications', badge: summary.trainingRecommendations?.length || null, html: training },
    { key: 'coverage', label: 'Coverage', badge: canRecoverCoverage && coverageKnown ? summary.capabilityGaps.length : null, html: `${coverage}${options.operationalVisualizationsEnabled === false ? '' : coverageMatrix}` },
    { key: 'attendance', label: 'Attendance', badge: canReview ? pendingEvents.length : null, html: attendanceTab },
    { key: 'labor', label: 'Labor evidence', badge: null, html: labor },
    ...(development ? [{ key: 'development', label: 'Development', badge: (sources.nominations?.data ?? []).length || null, html: development }] : []),
  ];
  const active = tabs.some(tab => tab.key === options.tab) ? options.tab : 'overview';
  return `
    <section class="panel workforce-intelligence" aria-labelledby="workforce-intelligence-title">
      <header class="workforce-intelligence-head"><div><h2 id="workforce-intelligence-title"><span aria-hidden="true">!</span> Training &amp; Performance Command Center</h2><p class="faint">Source-backed workforce signals · recommendations require supervisor review</p></div>
        ${filters}
        <p class="faint workforce-intelligence-filter-note">Roster, coverage, and evidence views follow the selected filters. Attendance totals follow the review period and remain permission-scoped. Departments are maintained on employee profiles by Workforce managers.</p>
      </header>
      <div class="workspace-tabs workforce-intelligence-tabs">
        <div class="workspace-tablist" role="tablist" aria-label="Command Center sections">${tabs.map(tab => `<button class="workspace-tab" type="button" role="tab" id="wi-tab-${tab.key}" data-wi-tab="${tab.key}" aria-controls="wi-panel-${tab.key}" aria-selected="${tab.key === active}" tabindex="${tab.key === active ? 0 : -1}">${esc(tab.label)}${tab.badge ? ` <span class="badge ${tab.key === 'overview' || tab.key === 'attendance' ? 'orange' : 'gray'}">${fmtNumber(tab.badge)}</span>` : ''}</button>`).join('')}</div>
        ${tabs.map(tab => `<div class="workspace-panel workforce-intelligence-panel" role="tabpanel" id="wi-panel-${tab.key}" data-wi-panel="${tab.key}" aria-labelledby="wi-tab-${tab.key}" ${tab.key === active ? '' : 'hidden'}>${tab.html}</div>`).join('')}
      </div>
      <p class="workforce-intelligence-note faint">No automatic promotion, pay, or disciplinary decision is made from these signals.</p>
      ${sources.workers.error ? errorBlock(sources.workers.error, 'workforce intelligence') : ''}
    </section>`;
}

export async function appendWorkforceIntelligence(page, me, selectedPersonId = null, { after = false, employeesTab = true, extraTabs = [], tab = null, operationalVisualizationsEnabled = true } = {}) {
  const now = new Date();
  const activityFrom = new Date(now.getTime() - 30 * DAY);
  const through = new Date(now.getTime() + 30 * DAY);
  const current = { tab: tab ?? (selectedPersonId ? 'labor' : 'overview'), expiryDays: 60, siteCode: '', areaCode: '', department: '', shiftCode: '', workCenterCode: '', machineCode: '', roleTitle: '', qualificationCode: '', workerStatus: 'all', reviewState: 'all', attendancePeriod: 'quarter' };
  const reviewFrom = new Date(now.getTime() - 90 * DAY);
  const scheduleFrom = new Date(now.getTime() - 60 * DAY);
  const iso = date => encodeURIComponent(date.toISOString());
  const mePermissions = new Set(me?.permissions ?? []);
  const nominationPermission = mePermissions.has('workforce.development.read');
  const canReadSchedule = mePermissions.has('planning.schedule.read');
  const [workersResult, resourcesResult, typesResult, unavailabilityResult, readinessResult, activityResult, attendanceResult, pendingResult, policiesResult, opportunitiesResult, nominationsResult, materialTasksResult, suspectReportsResult, scheduleResult] = await Promise.all([
    tryGet('/api/workforce/workers'),
    tryGet('/api/resources/'),
    tryGet('/api/competency/qualification-types'),
    tryGet(`/api/workforce/unavailability?from=${iso(now)}&to=${iso(through)}`),
    tryGet('/api/readiness/operations'),
    tryGet(`/api/shopfloor/activity?from=${iso(activityFrom)}&to=${iso(now)}`),
    tryGet(attendanceSummaryUrl(current.attendancePeriod, now)),
    tryGet(`/api/workforce/attendance-intelligence/events?from=${iso(reviewFrom)}&to=${iso(now)}`),
    tryGet('/api/workforce/attendance-policies'),
    tryGet(`/api/workforce/attendance-opportunities?from=${iso(scheduleFrom)}&to=${iso(through)}`),
    nominationPermission ? tryGet('/api/workforce/development-nominations') : Promise.resolve({ data: [] }),
    tryGet('/api/inventory/tasks?open=true'),
    tryGet('/api/quality/suspect-scrap'),
    canReadSchedule ? tryGet('/api/planning/schedules/approved') : Promise.resolve({ error: 'Approved schedule access is unavailable to this account.' }),
  ]);
  const workers = Array.isArray(workersResult.data) ? workersResult.data : undefined;
  const qualificationsResult = workers ? await qualificationRecords(workers) : { error: workersResult.error };
  const machineAuthorizationsResult = workers ? await machineAuthorizationRecords(workers) : { error: workersResult.error };
  const sourceReadCompletedAt = new Date();
  const resources = resourcesResult.data ?? undefined;
  const personOperationHistory = await loadPersonOperationHistory(selectedPersonId, activityResult, me, now);
  const inputs = {
    now,
    workers,
    qualifications: qualificationsResult.data,
    machineAuthorizations: machineAuthorizationsResult.data,
    qualificationTypes: typesResult.data,
    resources,
    unavailability: unavailabilityResult.data,
    attendanceOpportunities: opportunitiesResult.data,
    approvedSchedule: Array.isArray(scheduleResult.data?.result?.operations) ? scheduleResult.data.result : null,
    attendanceEvents: pendingResult.data,
    readiness: readinessResult.data,
    activity: activityResult.data,
    attendanceSummary: attendanceResult.data,
    materialTasks: materialTasksResult.data,
    suspectReports: suspectReportsResult.data,
  };
  const shifts = resources?.shifts ?? [];
  const workCenters = resources?.workCenters ?? [];
  const sites = resources?.sites ?? [];
  const safetyAreas = resources?.safetyAreas ?? [];
  const machines = resources?.machines ?? [];
  const jobTitles = [...new Set((workers ?? []).map(worker => worker.jobTitle?.trim()).filter(Boolean))].sort((a, b) => a.localeCompare(b));
  const qualificationTypes = typesResult.data ?? [];
  const departments = [...new Set((workers ?? []).map(worker => worker.department?.trim()).filter(Boolean))]
    .sort((a, b) => a.localeCompare(b)).map(code => ({ code, name: code }));
  if ((workers ?? []).some(worker => !worker.department?.trim())) departments.push({ code: '__unassigned', name: 'Unassigned' });
  const workerNames = new Map((workers ?? []).map(worker => [worker.personId, worker.displayName]));
  const sources = { workers: workersResult, types: typesResult, qualifications: qualificationsResult, machineAuthorizations: machineAuthorizationsResult,
    resources: resourcesResult, unavailability: unavailabilityResult, activity: activityResult, attendance: attendanceResult, pending: pendingResult,
    policies: policiesResult, opportunities: opportunitiesResult, nominations: nominationsResult, materialTasks: materialTasksResult,
    suspectReports: suspectReportsResult, schedule: scheduleResult, workerNames };
  const filteredView = () => {
    const scoped = filterWorkforceIntelligenceInputs(inputs, current);
    const ids = new Set((scoped.workers ?? []).map(worker => worker.personId));
    // Matrix columns are the selected operations, while its employee rows must still include
    // qualified people from other work centers at the same authorized plant. The regular
    // roster's work-center filter intentionally narrows employees to their home center.
    const resourceData = resources ?? {};
    const centerByCode = new Map((resourceData.workCenters ?? []).map(center => [center.code, center]));
    const areaByCode = new Map((resourceData.safetyAreas ?? []).map(area => [area.code, area]));
    const targetCenter = current.workCenterCode || (current.machineCode
      ? resourceData.machines?.find(machine => machine.code === current.machineCode)?.workCenterCode : null);
    const targetArea = centerByCode.get(targetCenter)?.safetyAreaCode;
    const matrixSiteCode = current.siteCode || areaByCode.get(targetArea)?.siteCode || '';
    const matrixWorkers = (workers ?? []).filter(worker => {
      const homeCenter = worker.assignment?.workCenterCode || worker.homeWorkCenterCode;
      const homeArea = centerByCode.get(homeCenter)?.safetyAreaCode;
      return (!current.shiftCode || worker.shiftCode === current.shiftCode)
        && (!matrixSiteCode || areaByCode.get(homeArea)?.siteCode === matrixSiteCode)
        && (!current.areaCode || homeArea === current.areaCode)
        && (!current.department || (current.department === '__unassigned' ? !worker.department : worker.department === current.department))
        && (!current.roleTitle || (worker.jobTitle ?? '').trim() === current.roleTitle)
        && (current.workerStatus === 'all' || !current.workerStatus || (current.workerStatus === 'active' ? worker.isActive : !worker.isActive));
    });
    const matrixIds = new Set(matrixWorkers.map(worker => worker.personId));
    const matrixSources = {
      ...sources,
      workers: { ...sources.workers, data: matrixWorkers },
      qualifications: { ...sources.qualifications, data: (sources.qualifications.data ?? []).filter(row => matrixIds.has(row.personId)) },
      machineAuthorizations: { ...sources.machineAuthorizations, data: (sources.machineAuthorizations.data ?? []).filter(row => matrixIds.has(row.personId)) },
      unavailability: { ...sources.unavailability, data: (sources.unavailability.data ?? []).filter(row => matrixIds.has(row.personId)) },
      resources: { ...sources.resources, data: scoped.resources },
    };
    const filteredSources = {
      ...sources,
      workers: { ...sources.workers, data: scoped.workers },
      qualifications: { ...sources.qualifications, data: scoped.qualificationRecords },
      machineAuthorizations: { ...sources.machineAuthorizations, data: scoped.machineAuthorizations },
      unavailability: { ...sources.unavailability, data: scoped.unavailability },
      activity: { ...sources.activity, data: scoped.activity },
      resources: { ...sources.resources, data: scoped.resources },
      pending: { ...sources.pending, data: (sources.pending.data ?? []).filter(row => ids.has(row.personId)) },
      opportunities: { ...sources.opportunities, data: (sources.opportunities.data ?? []).filter(row => ids.has(row.personId)) },
      nominations: { ...sources.nominations, data: (sources.nominations.data ?? []).filter(row => ids.has(row.personId)) },
      materialTasks: { ...sources.materialTasks, data: scoped.materialTasks },
      suspectReports: { ...sources.suspectReports, data: scoped.suspectReports },
      workerNames: new Map(scoped.workers.map(worker => [worker.personId, worker.displayName])),
      matrixSources,
    };
    return { scoped, filteredSources };
  };
  const renderPanel = () => {
    const { scoped, filteredSources } = filteredView();
    const options = { ...current, asOf: sourceReadCompletedAt, operationalVisualizationsEnabled, shifts: scoped.resources?.shifts ?? shifts, workCenters: scoped.resources?.workCenters ?? workCenters,
      sites: sites.filter(site => !current.siteCode || site.code === current.siteCode),
      areas: safetyAreas.filter(area => (!current.siteCode || area.siteCode === current.siteCode) && (!current.areaCode || area.code === current.areaCode)),
      machines: machines.filter(machine => (!current.workCenterCode || machine.workCenterCode === current.workCenterCode)
        && (!current.siteCode || scoped.resources?.machines?.some(item => item.code === machine.code))),
      jobTitles, qualificationTypes, departments };
    return render(buildWorkforceIntelligence({ ...scoped, ...current }), filteredSources, { ...options, employeesTab, extraTabs }, me, selectedPersonId, personOperationHistory);
  };
  // Employees tab: review cards are gathered only when the tab is opened (one read per review period, cached across filter changes).
  const cardState = {}, cardCache = new Map();
  // Tabs a host page adds (e.g. the training queue) render their own content when opened.
  const mountExtra = () => {
    const extra = extraTabs.find(item => item.key === current.tab);
    const host = extra && page.querySelector(`[data-wi-extra="${extra.key}"]`);
    if (host) extra.mount(host);
  };
  const mountCards = () => {
    mountExtra();
    const host = page.querySelector('[data-wi-employee-cards]');
    if (!host || current.tab !== 'employees') return;
    cardState.visibleIds = new Set((filteredView().scoped.workers ?? []).map(worker => worker.personId));
    host.setAttribute('data-mounted', 'true');
    mountPerformanceCards(host, { get: tryGet, state: cardState, cache: cardCache });
  };
  const selectTab = key => {
    const panel = page.querySelector('.workforce-intelligence');
    if (!panel) return;
    current.tab = key;
    for (const button of panel.querySelectorAll('[data-wi-tab]')) {
      const on = button.dataset.wiTab === key;
      button.setAttribute('aria-selected', String(on)); button.tabIndex = on ? 0 : -1;
    }
    for (const section of panel.querySelectorAll('[data-wi-panel]')) section.hidden = section.dataset.wiPanel !== key;
    if (key === 'employees' && panel.querySelector('[data-wi-employee-cards]')?.getAttribute('data-mounted') !== 'true') mountCards();
    if (extraTabs.some(item => item.key === key)) mountExtra();
  };
  page.innerHTML = after ? page.innerHTML + renderPanel() : renderPanel() + page.innerHTML;
  mountCards();
  page.addEventListener('keydown', event => {
    const tab = event.target?.closest?.('[data-wi-tab]');
    if (!tab || !['ArrowRight', 'ArrowLeft'].includes(event.key)) return;
    const buttons = [...page.querySelectorAll('[data-wi-tab]')];
    const next = buttons[(buttons.indexOf(tab) + (event.key === 'ArrowRight' ? 1 : buttons.length - 1)) % buttons.length];
    event.preventDefault(); next.focus(); selectTab(next.dataset.wiTab);
  });
  page.addEventListener('change', async event => {
    const nominationForm = event.target?.closest?.('[data-wi-nomination-form]');
    if (nominationForm && (event.target.name === 'personId' || event.target.name === 'qualificationCode')) {
      const host = nominationForm.querySelector('[data-wi-readiness-result]');
      const personId = nominationForm.querySelector('[name="personId"]').value, code = nominationForm.querySelector('[name="qualificationCode"]').value;
      if (host && personId && code) {
        const result = await tryGet(`/api/workforce/development-nominations/readiness?personId=${encodeURIComponent(personId)}&qualificationCode=${encodeURIComponent(code)}`);
        host.innerHTML = result.error ? errorBlock(result.error, 'prerequisites') : readinessHtml(result.data);
      }
      return;
    }
    const filter = event.target?.dataset?.wiFilter;
    if (!filter) return;
    if (filter === 'expiry') current.expiryDays = Number(event.target.value);
    if (filter === 'shift') current.shiftCode = event.target.value;
    if (filter === 'center') { current.workCenterCode = event.target.value; current.machineCode = ''; }
    if (filter === 'site') { current.siteCode = event.target.value; current.areaCode = ''; current.workCenterCode = ''; current.machineCode = ''; }
    if (filter === 'area') { current.areaCode = event.target.value; current.workCenterCode = ''; current.machineCode = ''; }
    if (filter === 'department') current.department = event.target.value;
    if (filter === 'machine') current.machineCode = event.target.value;
    if (filter === 'role') current.roleTitle = event.target.value;
    if (filter === 'qualification') current.qualificationCode = event.target.value;
    if (filter === 'workerStatus') current.workerStatus = event.target.value;
    if (filter === 'reviewState') current.reviewState = event.target.value;
    const panel = event.target.closest('.workforce-intelligence');
    if (filter === 'period') {
      current.attendancePeriod = event.target.value;
      const end = new Date();
      const result = await tryGet(attendanceSummaryUrl(current.attendancePeriod, end));
      sources.attendance = result;
      inputs.attendanceSummary = result.data ?? null;
    }
    if (panel) { panel.outerHTML = renderPanel(); mountCards(); }
  });
  page.addEventListener('submit', async event => {
    const form = event.target.closest('[data-wi-schedule-form], [data-wi-policy-form], [data-wi-nomination-form]');
    if (!form) return;
    event.preventDefault();
    const values = new FormData(form);
    const reason = String(values.get('reason') ?? '').trim();
    try {
      if (form.matches('[data-wi-nomination-form]')) {
        const evidence = String(values.get('evidence') ?? '').split(',').map(value => value.trim()).filter(Boolean);
        const target = String(values.get('qualificationCode') ?? '');
        if (target && !evidence.includes(`Qualification/${target}`)) evidence.unshift(`Qualification/${target}`);
        const hours = String(values.get('hours') ?? '');
        const cost = String(values.get('cost') ?? '');
        await command('/api/workforce/development-nominations', {
          personId: String(values.get('personId')), kind: String(values.get('kind')), pathway: String(values.get('pathway')).trim(),
          rationale: String(values.get('rationale')).trim(), expectedPlantBenefit: String(values.get('benefit')).trim(),
          evidenceReferences: evidence, estimatedTrainingHours: hours === '' ? null : Number(hours), estimatedCost: cost === '' ? null : Number(cost),
          currency: String(values.get('currency') ?? '').trim() || null,
        }, reason);
      } else if (form.matches('[data-wi-schedule-form]')) {
        const fromValue = new Date(String(values.get('from')));
        const throughValue = new Date(String(values.get('through')));
        if (!Number.isFinite(fromValue.getTime()) || !Number.isFinite(throughValue.getTime())) throw new Error('Choose valid schedule dates.');
        await command('/api/workforce/attendance-schedule/generate', { from: fromValue.toISOString(), through: throughValue.toISOString() }, reason);
      } else {
        const effectiveFrom = new Date(String(values.get('effectiveFrom')));
        if (!Number.isFinite(effectiveFrom.getTime())) throw new Error('Choose a valid policy start date.');
        await command('/api/workforce/attendance-policies', {
          policyCode: String(values.get('policyCode')).trim(), revision: Number(values.get('revision')),
          effectiveFrom: effectiveFrom.toISOString(), effectiveThrough: null,
          graceMinutes: Number(values.get('graceMinutes')), includeCallOutsInArrivalDenominator: values.get('includeCallouts') === 'on',
        }, reason);
      }
      window.location.reload();
    } catch (error) {
      const alert = document.createElement('p');
      alert.className = 'notice warn';
      alert.textContent = error?.message ?? 'The attendance command could not be completed.';
      form.append(alert);
    }
  });
  page.addEventListener('click', async event => {
    const tab = event.target.closest('[data-wi-tab]');
    if (tab) { selectTab(tab.dataset.wiTab); return; }
    const cover = event.target.closest('[data-wi-cover]');
    if (cover) {
      const plan = JSON.parse(cover.dataset.wiCover);
      const f = plan.forecast;
      const start = new Date(), end = new Date(start.getTime() + 8 * 3600e3);
      const { formModal } = await import('../forms.js');
      await formModal({
        title: 'Plan coverage', detail: `Forecast: ${f.expectedGoodUnits == null ? 'no estimate' : `${f.conservativeGoodUnits}–${f.optimisticGoodUnits} good units (expected ${f.expectedGoodUnits}, ${f.confidence} confidence)`}. Set a separate, achievable goal; share it and the safe-work instructions with the worker.`,
        fields: [
          { name: 'destinationWorkCenter', label: 'Destination work center', value: plan.workCenterCode ?? '', code: true, required: true },
          { name: 'goalGoodUnits', label: 'Goal (good units)', type: 'number', min: 0, value: f.expectedGoodUnits ?? '', required: true },
          { name: 'goalProductiveMinutes', label: 'Planned productive minutes', type: 'number', min: 1, value: f.productiveMinutes || 420, required: true },
          { name: 'goalRationale', label: 'Why this goal', type: 'textarea', required: true },
          { name: 'coverageReason', label: 'Why coverage is needed', type: 'textarea', required: true },
        ],
        confirmLabel: 'Approve coverage', reasonRequired: true,
      }, (v, reason) => command('/api/workforce/coverage', {
        personId: plan.personId, destinationWorkCenter: v.destinationWorkCenter, machineCode: plan.machineCode, activityCode: plan.activityCode,
        plannedStart: start.toISOString(), plannedEnd: end.toISOString(), coverageReason: v.coverageReason, goalGoodUnits: v.goalGoodUnits,
        goalProductiveMinutes: v.goalProductiveMinutes, goalRationale: v.goalRationale, forecastConservative: f.conservativeGoodUnits,
        forecastExpected: f.expectedGoodUnits, forecastOptimistic: f.optimisticGoodUnits, forecastConfidence: f.confidence, forecastBasis: f.estimateBasis,
      }, reason));
      return;
    }
    const causePerformance = event.target.closest('[data-wi-cause-performance]');
    if (causePerformance) { await openCausePerformance(causePerformance.dataset.wiCausePerformance, causePerformance.dataset.wiCauseName, me); return; }
    const recovery = event.target.closest('[data-wi-recovery]');
    const matrixPreview = event.target.closest('[data-wi-matrix-preview]');
    const materialRecovery = event.target.closest('[data-wi-material-recovery]');
    const review = event.target.closest('[data-wi-review]');
    const correction = event.target.closest('[data-wi-correct]');
    const classification = event.target.closest('[data-wi-classify]');
    const nominationAdvance = event.target.closest('[data-wi-nom-advance]');
    if (!recovery && !matrixPreview && !materialRecovery && !review && !correction && !classification && !nominationAdvance) return;
    try {
      if (recovery) {
        const output = recovery.closest('.workforce-intelligence-recovery-item')?.querySelector('[data-wi-recovery-result]');
        const projectedShift = recovery.dataset.projected === 'true';
        recovery.disabled = true;
        if (output) output.innerHTML = projectedShift
          ? '<p class="faint">Checking scheduled workers, qualifications, safety, and the selected shift…</p>'
          : '<p class="faint">Evaluating current source and target work…</p>';
        const request = {
          safetyAreaCode: recovery.dataset.safetyArea || null,
          workCenterCode: recovery.dataset.workCenter || null,
          machineCode: recovery.dataset.machine || null,
          activityCode: recovery.dataset.activity,
        };
        if (projectedShift) {
          request.shiftDate = recovery.closest('.workforce-intelligence-recovery-item')?.querySelector('[data-wi-recovery-date]')?.value || null;
          request.shiftCode = recovery.closest('.workforce-intelligence-recovery-item')?.querySelector('[data-wi-recovery-shift]')?.value || null;
          if (!request.shiftDate || !request.shiftCode) throw new Error('Choose the plant shift date and shift before previewing scheduled candidates.');
        }
        // Section 21: the same ranking with each candidate's capacity forecast; fall back to the plain ranking if forecasts are unavailable.
        let result, capacity = new Map();
        try {
          const withCapacity = await readPost('/api/readiness/workforce-recovery-capacity', { ...request, scheduledMinutes: 480 });
          if (withCapacity?.recovery?.output) {
            result = withCapacity.recovery;
            capacity = capacityByPerson(withCapacity);
          }
        } catch {
          // Forecasts are optional context; the ranking below still answers the question.
        }
        result ??= await readPost('/api/readiness/workforce-recovery-preview', request);
        const summary = result.output;
        if (!summary || !output) throw new Error('The recovery preview returned no result.');
        const sourceImpact = candidate => {
          const known = candidate.sourceImpacts?.length
            ? `<span>Currently open source-work impact: ${candidate.sourceImpacts.map(item => esc(item.detail)).join(' · ')}</span>`
            : '<span>No currently open source operation is made unready using current assignments carried forward.</span>';
          return summary.projectedShift
            ? `${known}<span>Future source dispatch and workload remain unknown.</span>`
            : candidate.sourceImpacts?.length ? `<span>Source impact: ${candidate.sourceImpacts.map(item => esc(item.detail)).join(' · ')}</span>` : '<span>No currently executable source work is disrupted.</span>';
        };
        output.innerHTML = summary.candidates?.length
          ? `<ol class="workforce-intelligence-recovery-list">${summary.candidates.map(candidate => `<li><b>#${candidate.rank} · ${esc(candidate.personName)}</b><span>${summary.projectedShift ? 'Scheduled candidate' : candidate.requiresSourceBackfill ? 'Two-step plan' : 'Direct move'} · ${candidate.moves.map(move => `${esc(move.personName)} → ${esc(move.assignment.machineCode || move.assignment.workCenterCode || move.assignment.safetyAreaCode)} / ${esc(move.assignment.activityCode)}`).join(' · ')}</span>${sourceImpact(candidate)}<span>${candidate.evidence.map(esc).join(' ')}</span>${candidateCapacityHtml(capacity.get(candidate.personId))}${capacity.get(candidate.personId)?.atTarget.forecast.isEligible && (me?.permissions ?? []).includes('workforce.supervise') && !summary.projectedShift ? `<button class="btn small" data-wi-cover="${escapeAttr(JSON.stringify({ personId: candidate.personId, workCenterCode: request.workCenterCode, machineCode: request.machineCode, activityCode: request.activityCode, forecast: capacity.get(candidate.personId).atTarget.forecast }))}">Plan coverage with a goal…</button>` : ''}</li>`).join('')}</ol><p class="faint">${summary.limitations.map(esc).join(' ')}</p>`
          : `<p class="faint">No ${summary.projectedShift ? 'qualified workers scheduled' : 'safe candidates among visible, currently present workers'} found (${fmtNumber(summary.evaluatedCandidates)} evaluated). ${summary.currentConstraint ? `Current target constraint: ${esc(summary.currentConstraint)}.` : ''}</p><p class="faint">${summary.limitations.map(esc).join(' ')}</p>`;
        recovery.disabled = false;
        return;
      } else if (matrixPreview) {
        const profile = matrixPreview.closest('.workforce-intelligence-matrix-profile');
        const output = profile?.querySelector('[data-wi-matrix-preview-result]');
        if (!output) throw new Error('The coverage preview area is unavailable.');
        matrixPreview.disabled = true;
        output.innerHTML = '<p class="faint">Checking current qualified workers, safety and skill-capacity evidence…</p>';
        const request = {
          safetyAreaCode: matrixPreview.dataset.safetyArea || null,
          workCenterCode: matrixPreview.dataset.workCenter || null,
          machineCode: matrixPreview.dataset.machine || null,
          activityCode: matrixPreview.dataset.activity,
          ...(matrixPreview.dataset.shift ? { shiftCode: matrixPreview.dataset.shift } : {}),
        };
        const result = await readPost('/api/readiness/workforce-recovery-capacity', { ...request, scheduledMinutes: 480 });
        const summary = result?.recovery?.output;
        if (!summary) throw new Error('The safe capacity preview returned no result.');
        const capacities = capacityByPerson(result);
        output.innerHTML = `<div class="workforce-intelligence-matrix-preview"><b>${esc(matrixPreview.dataset.machine)} · ${esc(matrixPreview.dataset.activity)} · current evidence</b>
          ${summary.candidates?.length ? `<ul class="workforce-intelligence-recovery-list">${summary.candidates.map(candidate => `<li><b>${esc(candidate.personName)}</b><span>${candidate.moves.map(move => `${esc(move.personName)} → ${esc(move.assignment.machineCode || move.assignment.workCenterCode || move.assignment.safetyAreaCode)}`).join(' · ')}</span>${candidate.sourceImpacts?.length ? `<span>Origin work: ${candidate.sourceImpacts.map(item => esc(item.detail)).join(' · ')}</span>` : '<span>No currently executable source operation is made unready using current assignments carried forward.</span>'}${candidateCapacityHtml(capacities.get(candidate.personId))}</li>`).join('')}</ul>` : `<p>No safe, currently present candidates passed the recorded qualification, authorization and safety gates (${fmtNumber(summary.evaluatedCandidates)} evaluated). ${summary.currentConstraint ? `Current constraint: ${esc(summary.currentConstraint)}.` : ''}</p>`}
          <p class="faint">${(summary.limitations ?? []).map(esc).join(' ')} Preview only; no employee is moved or assigned. Skill-capacity estimates show their evidence and confidence.</p></div>`;
        matrixPreview.disabled = false;
        return;
      } else if (materialRecovery) {
        const output = materialRecovery.closest('li')?.querySelector('[data-wi-material-recovery-result]');
        materialRecovery.disabled = true;
        if (output) output.innerHTML = '<p class="faint">Comparing current schedule with hypothetical receipt…</p>';
        const preview = await readPost('/api/readiness/material-receipt-impact-preview', { taskId: Number(materialRecovery.dataset.wiMaterialRecovery) });
        if (!output || !preview?.targetOrder) throw new Error('The material receipt impact preview returned no result.');
        output.innerHTML = renderMaterialReceiptImpact(preview);
        materialRecovery.disabled = false;
        return;
      } else if (nominationAdvance) {
        const id = nominationAdvance.dataset.wiNomAdvance;
        const status = page.querySelector(`[data-wi-nom-status="${CSS.escape(id)}"]`)?.value;
        const note = page.querySelector(`[data-wi-nom-note="${CSS.escape(id)}"]`)?.value?.trim();
        if (!note) throw new Error('Add the human review note before recording this transition.');
        await command(`/api/workforce/development-nominations/${encodeURIComponent(id)}/advance`, { status, note }, note);
      } else if (review) {
        await command(`/api/workforce/attendance-intelligence/events/${encodeURIComponent(review.dataset.wiReview)}/review`, {
          decision: review.dataset.decision, reasonCode: 'reviewed-source-record',
        }, 'Independent review of the recorded attendance evidence');
      } else {
        if (classification) {
          const id = classification.dataset.wiClassify;
          const kind = page.querySelector(`[data-wi-classification-kind="${CSS.escape(id)}"]`)?.value;
          const time = page.querySelector(`[data-wi-classification-time="${CSS.escape(id)}"]`)?.value;
          const reason = page.querySelector(`[data-wi-classification-reason="${CSS.escape(id)}"]`)?.value?.trim();
          await command(`/api/workforce/attendance-opportunities/${encodeURIComponent(id)}/classifications`, {
            kind, observedAt: time ? new Date(time).toISOString() : null, reasonCode: 'supervisor-reviewed-source',
          }, reason);
          window.location.reload();
          return;
        }
        const id = correction.dataset.wiCorrect;
        const kind = page.querySelector(`[data-wi-correct-kind="${CSS.escape(id)}"]`)?.value;
        const time = page.querySelector(`[data-wi-correct-time="${CSS.escape(id)}"]`)?.value;
        const reason = page.querySelector(`[data-wi-correct-reason="${CSS.escape(id)}"]`)?.value?.trim();
        await command(`/api/workforce/attendance-intelligence/events/${encodeURIComponent(id)}/correction`, {
          kind, observedAt: time ? new Date(time).toISOString() : null, reasonCode: 'source-record-correction',
        }, reason);
      }
      window.location.reload();
    } catch (error) {
      if (recovery) {
        recovery.disabled = false;
        const output = recovery.closest('.workforce-intelligence-recovery-item')?.querySelector('[data-wi-recovery-result]');
        if (output) output.innerHTML = `<p class="notice warn">${esc(error?.message ?? 'The recovery preview could not be evaluated.')}</p>`;
        return;
      }
      if (matrixPreview) {
        matrixPreview.disabled = false;
        const output = matrixPreview.closest('.workforce-intelligence-matrix-profile')?.querySelector('[data-wi-matrix-preview-result]');
        if (output) output.innerHTML = `<p class="notice warn">${esc(error?.message ?? 'The safe coverage preview could not be evaluated.')}</p>`;
        return;
      }
      if (materialRecovery) {
        materialRecovery.disabled = false;
        const output = materialRecovery.closest('li')?.querySelector('[data-wi-material-recovery-result]');
        if (output) output.innerHTML = `<p class="notice warn">${esc(error?.message ?? 'The material receipt preview could not be evaluated.')}</p>`;
        return;
      }
      const item = (review ?? correction ?? classification ?? nominationAdvance).closest('.workforce-intelligence-review-item');
      const alert = document.createElement('p');
      alert.className = 'notice warn';
      alert.textContent = error?.message ?? 'The attendance action could not be completed.';
      item?.append(alert);
    }
  });
  return { selectTab, refreshExtra: mountExtra };
}
