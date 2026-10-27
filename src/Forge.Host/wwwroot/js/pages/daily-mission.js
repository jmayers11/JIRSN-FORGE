import { get, statePost, put, command } from '../api.js';
import { esc, errorBlock, confirmWithReason, toast } from '../ui.js';

const GROUPS = ['Requires Decision', 'Ready to Execute', 'Requires Attention', 'Assigned Work', 'Watch/Upcoming'];

const dateTime = (value, timeZone) => {
  if (!value) return 'Not recorded';
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? 'Not recorded' : date.toLocaleString(undefined, timeZone ? { timeZone } : undefined);
};

const dateOnly = value => {
  if (!value) return null;
  const date = new Date(`${value}T12:00:00`);
  return Number.isNaN(date.valueOf()) ? null : date.toLocaleDateString();
};

const scrollOptions = () => ({ behavior: globalThis.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches ? 'auto' : 'smooth', block: 'start' });
function focusSection(root, selector) {
  const section = root.querySelector(selector);
  if (!section) return;
  section.scrollIntoView?.(scrollOptions());
  section.setAttribute('tabindex', '-1');
  section.focus?.({ preventScroll: true });
}

const sourceLabel = status => ({
  Available: 'Current', Limited: 'Partial coverage', PendingIntegration: 'Not connected',
  NotAvailableToRole: 'Not available to this role', NoWorkerRecord: 'No linked worker record',
  Unavailable: 'Refresh needed',
}[status] ?? status);

function itemCard(item, priorities, timeZone, stale = false, canReviewRecovery = false) {
  const deadline = dateOnly(item.deadline);
  const assignmentId = item.sourceLifecycle === 'WorkAssignment' && item.id?.startsWith('assignment:')
    ? item.id.slice('assignment:'.length) : '';
  const validAssignmentId = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(assignmentId);
  const nextAssignmentStatus = { Assigned: 'Acknowledged', Acknowledged: 'InProgress', InProgress: 'Completed' }[item.sourceStatus];
  const assignmentAction = validAssignmentId && nextAssignmentStatus
    ? `<button class="btn small primary" data-assignment-id="${esc(assignmentId)}" data-assignment-status="${esc(nextAssignmentStatus)}" ${stale ? 'disabled title="Refresh this mission before advancing an assignment."' : ''}>${nextAssignmentStatus === 'Completed' ? 'Complete assignment' : `Mark ${esc(nextAssignmentStatus)}`}</button><small class="daily-mission-assignment-note">Updates assignment tracking only; the linked source record stays in its workflow.</small>`
    : item.sourceLifecycle === 'WorkAssignment' && item.sourceStatus === 'Completed'
      ? '<small class="daily-mission-assignment-note">Assignment tracking is complete. This does not certify completion of the linked source record.</small>' : '';
  const blockedOperation = item.sourceLifecycle === 'WorkforceOrderReadinessProjection' && item.sourceStatus === 'WorkforceBlocked'
    ? item.sourceId?.match(/^(\d+):(\d+)$/) : null;
  const recoveryAction = canReviewRecovery && blockedOperation
    ? `<button class="btn small" type="button" data-recovery-order="${esc(blockedOperation[1])}" data-recovery-sequence="${esc(blockedOperation[2])}">Review safe coverage</button><div class="daily-mission-recovery" data-recovery-panel="${esc(blockedOperation[1])}:${esc(blockedOperation[2])}" hidden></div>` : '';
  return `<article class="daily-mission-card ${priorities.has(item.id) ? 'priority' : ''}">
    <div class="daily-mission-card-main">
      <div class="daily-mission-card-meta"><span class="daily-mission-group-tag">${esc(item.group)}</span><span class="badge gray">${esc(item.sourceLifecycle)} · ${esc(item.sourceStatus)}</span></div>
      <h3>${esc(item.title)}</h3>
      <p>${esc(item.reason)}</p>
      <div class="daily-mission-card-foot"><span>Updated ${esc(dateTime(item.sourceTimestamp, timeZone))}</span>${deadline ? `<span>Expected / due ${esc(deadline)}</span>` : ''}</div>
    </div>
    <div class="daily-mission-card-actions"><a class="btn small" href="${esc(item.deepLink)}" aria-label="Open ${esc(item.title)} in its source workflow">Open source</a>${recoveryAction}${assignmentAction}</div>
  </article>`;
}

function renderRecoveryPreview(result) {
  const run = result?.recovery;
  const output = run?.output;
  const forecasts = new Map((result?.candidates ?? []).map(candidate => [candidate.rank, candidate]));
  const candidates = output?.candidates ?? [];
  const candidateList = candidates.length ? `<ol>${candidates.map(candidate => {
    const candidateCapacity = forecasts.get(candidate.rank);
    const capacity = candidateCapacity?.atTarget;
    const forecast = capacity?.forecast;
    const range = forecast?.expectedGoodUnits == null ? 'No capacity estimate is available.'
      : `Expected ${Number(forecast.expectedGoodUnits).toLocaleString()} good units (${Number(forecast.conservativeGoodUnits).toLocaleString()}–${Number(forecast.optimisticGoodUnits).toLocaleString()} range; ${esc(forecast.confidence ?? 'unrated')} confidence).`;
    const recency = capacity?.lastOperatedAt ? ` Last verified work: ${esc(dateTime(capacity.lastOperatedAt))}.` : ' No verified recent operation was found.';
    const origin = forecasts.get(candidate.rank)?.atOrigin?.forecast?.expectedGoodUnits;
    const originImpact = origin == null ? '' : ` Origin work estimate: ${Number(origin).toLocaleString()} good units before backfill.`;
    const explanation = candidateCapacity?.explanation ? ` ${esc(candidateCapacity.explanation)}` : '';
    return `<li><b>${esc(candidate.personName)}</b> — ${esc((candidate.evidence ?? []).join(' '))} ${range}${recency}${originImpact}${explanation}${candidate.requiresSourceBackfill ? ' A qualified backfill is required before this move.' : ''}</li>`;
  }).join('')}</ol>` : `<p>No safe source-aware candidate was found among ${Number(output?.evaluatedCandidates ?? 0)} evaluated workers.</p>`;
  const limitations = output?.limitations?.length ? `<small>${esc(output.limitations.join(' '))}</small>` : '';
  return `<h4>Read-only coverage preview</h4><p>Fresh order-readiness and supervisor-scope checks completed at ${esc(dateTime(run?.asOf ?? output?.asOf))}. No staffing change was made.</p>${candidateList}${limitations}`;
}

const LANE = { 'Requires Decision': 'decision', 'Ready to Execute': 'ready', 'Requires Attention': 'attention', 'Assigned Work': 'assigned', 'Watch/Upcoming': 'watch' };

/** The mission as a kanban board: one lane per group, side by side, each scrolling on its own. */
export function missionBoardHtml(items, priorities, timeZone, stale = false, canReviewRecovery = false) {
  return `<div class="daily-mission-board" aria-label="Daily Mission board">${GROUPS.map(group => {
    const inGroup = items.filter(item => item.group === group);
    const id = `mission-lane-${LANE[group]}`;
    return `<section class="daily-mission-section daily-mission-lane" data-lane="${LANE[group]}" data-mission-group="${esc(group)}" aria-labelledby="${id}">
      <header class="daily-mission-lane-head"><h2 id="${id}">${esc(group)}</h2><span class="badge gray">${inGroup.length}</span></header>
      <div class="daily-mission-list">${inGroup.length ? inGroup.map(item => itemCard(item, priorities, timeZone, stale, canReviewRecovery)).join('') : '<p class="daily-mission-empty">Nothing from the connected sources needs attention here.</p>'}</div></section>`;
  }).join('')}</div>`;
}

export async function dailyMissionPage(root, { me }) {
  const renderHeader = (preferences = null) => `<section class="page-head daily-mission-head"><div><p class="eyebrow">SHIFT BRIEFING · LIVE SOURCE LINKS</p><h1>Daily Mission</h1><p class="sub">A role-aware view of decisions, assigned work and operational risks. Source workflows remain authoritative.</p></div><div class="daily-mission-header-actions"><button class="btn" data-reopen-briefing type="button" aria-controls="daily-mission-intro" aria-expanded="false">View shift briefing</button><button class="btn" data-refresh type="button">Refresh mission</button></div></section>
    ${preferences ? `<details class="daily-mission-preferences"><summary>Personalize your workspace</summary><div class="daily-mission-preference-fields"><label>Default workspace on sign-in<select class="input" data-landing-route>${preferences.options.map(option => `<option value="${esc(option.route)}" ${option.route === preferences.landingRoute ? 'selected' : ''}>${esc(option.label)}</option>`).join('')}</select></label><label>Program view<select class="input" data-program-scope>${(preferences.programOptions ?? [{ route: 'all', label: 'All authorized programs' }]).map(option => `<option value="${esc(option.route)}" ${option.route === (preferences.programScope ?? 'all') ? 'selected' : ''}>${esc(option.label)}</option>`).join('')}</select></label><label>Hawk accents<select class="input" data-hawk-accents><option value="off" ${preferences.hawkAccents === 'off' ? 'selected' : ''}>Off</option><option value="subtle" ${preferences.hawkAccents === 'subtle' ? 'selected' : ''}>Subtle</option><option value="expressive" ${preferences.hawkAccents === 'expressive' ? 'selected' : ''}>Expressive</option></select></label><button class="btn small" data-save-preferences type="button">Save preferences</button><small>The program view can narrow your authorized scope; it cannot grant access. Shared site context stays governed by its own permissions. The sidebar remains available as your Classic Workspace.</small></div></details>` : ''}`;
  root.innerHTML = `${renderHeader()}<div class="muted">Loading authorized mission sources…</div>`;

  let data, briefing = { shouldShow: false };
  let preferences = null;
  try {
    let currentSession;
    if (me?.dailyMissionSession) {
      currentSession = me.dailyMissionSession;
      delete me.dailyMissionSession;
    }
    else currentSession = await statePost('/api/daily-mission/session');
    briefing = currentSession.briefing;
    data = currentSession.mission ?? await get('/api/daily-mission');
  }
  catch (error) {
    root.innerHTML = `${renderHeader()}${errorBlock(error, 'Daily Mission')}`;
    root.querySelector('[data-refresh]')?.addEventListener('click', () => dailyMissionPage(root, { me }));
    return;
  }
  try {
    preferences = await get('/api/daily-mission/preferences');
  } catch { /* a mission stays usable when personal state is temporarily unavailable */ }

  const context = data.operationalContext;
  const timeZone = context?.timeZone;
  const generatedAt = Date.parse(data.generatedAt ?? '');
  const stale = !Number.isFinite(generatedAt) || Date.now() - generatedAt > 5 * 60_000;
  const items = data.items ?? [];
  const sources = data.sources ?? [];
  const permissions = me?.permissions ?? [];
  const canReviewRecovery = permissions.includes('workforce.supervise') || permissions.includes('workforce.manage');
  const priorities = new Set(items.slice(0, 5).map(item => item.id));
  const sections = missionBoardHtml(items, priorities, timeZone, stale, canReviewRecovery);

  root.innerHTML = `${renderHeader(preferences)}
    ${briefing.scopeChanged ? `<div class="notice info daily-mission-scope-notice" role="status">Your role, site, shift, or program access changed. This mission now reflects your current authorized scope. Your briefing has not been repeated.</div>` : ''}
    <section class="daily-mission-welcome" id="daily-mission-intro" data-first-briefing role="region" aria-labelledby="daily-welcome-title" ${briefing.shouldShow ? '' : 'hidden'}><div><span class="eyebrow">${briefing.shouldShow ? 'FIRST BRIEFING THIS OPERATIONAL DAY' : 'SHIFT BRIEFING'}</span><h2 id="daily-welcome-title">Welcome, ${esc(me.displayName)}</h2><p>Your briefing is ready. Dismissal only closes this introduction; it does not approve or execute any work.</p></div><div class="daily-mission-welcome-actions"><button class="btn primary" data-open-plan type="button">Open Daily Plan</button><button class="btn" data-review-recommendations type="button">Review Recommendations</button><button class="btn ghost" data-dismiss-briefing type="button">Dismiss for Now</button></div></section>
    <section class="daily-mission-brief" data-hawk-accents="${esc(preferences?.hawkAccents ?? 'subtle')}" aria-label="Current shift briefing">
      <div class="daily-mission-brief-mark"><img src="/img/brand/forge-logo-icon.png" alt=""></div>
      <div class="daily-mission-brief-copy"><div class="eyebrow">${esc(me.displayName)} · ${esc((me.roles ?? []).join(' / ') || 'Forge user')}</div>
        <h2>${context ? `${esc(context.siteName)} · ${esc(context.shiftName)}` : 'Your Forge workspaces'}</h2>
        <p>${context ? `Operational day ${esc(context.operationalDate)} · ${esc(context.relation)} shift · ${esc(dateTime(context.shiftStartsAt, timeZone))} – ${esc(dateTime(context.shiftEndsAt, timeZone))}` : 'Your personal work and any source areas available to your role are gathered below.'}</p>
      </div>
      <div class="daily-mission-freshness"><span>${data.isPartial ? 'PARTIAL MISSION' : 'SOURCE STATUS'}</span><b>${esc(dateTime(data.generatedAt, timeZone))}</b><small>Mission generated</small></div>
    </section>
    ${data.isPartial ? `<div class="notice warn daily-mission-warning" role="status">This mission is partial. Missing or restricted sources are listed below; an empty section does not confirm an all-clear.${(data.warnings ?? []).length ? ` ${esc(data.warnings.join(' '))}` : ''}</div>` : ''}
    <div class="notice warn daily-mission-warning" data-mission-stale role="alert" ${stale ? '' : 'hidden'}>This mission snapshot is older than five minutes or has no valid generation time. Refresh it before using assignment actions.</div>
    ${sections}
    <details class="section daily-mission-sources"><summary><b>Source coverage</b><span>${sources.length} sources · ${data.isPartial ? 'some coverage is missing or limited' : 'available'}</span></summary>
      <div class="daily-mission-source-list">${sources.map(source => `<div><span class="daily-source-state ${source.status === 'Available' ? 'ok' : source.status === 'PendingIntegration' || source.status === 'Limited' ? 'warn' : ''}">${esc(sourceLabel(source.status))}</span><b>${esc(source.code.replaceAll('-', ' '))}</b><span>${esc(source.scope)}</span>${source.asOf ? `<time>${esc(dateTime(source.asOf, timeZone))}</time>` : ''}${source.note ? `<small>${esc(source.note)}</small>` : ''}</div>`).join('')}</div>
    </details>`;
  root.querySelector('[data-refresh]')?.addEventListener('click', () => dailyMissionPage(root, { me }));
  root.querySelectorAll('[data-assignment-id]').forEach(button => button.addEventListener('click', () => {
    if (!Number.isFinite(generatedAt) || Date.now() - generatedAt > 5 * 60_000) {
      button.disabled = true;
      const notice = root.querySelector('[data-mission-stale]');
      if (notice) notice.hidden = false;
      return;
    }
    const nextStatus = button.dataset.assignmentStatus;
    return confirmWithReason({
      title: nextStatus === 'Completed' ? 'Complete work assignment' : `Mark assignment ${nextStatus.toLowerCase()}`,
      detail: 'Record why this assignment is moving forward. This changes assignment tracking only; the linked order, inspection, or other source record must still be completed in its own workflow.',
      confirmLabel: nextStatus === 'Completed' ? 'Complete assignment' : `Mark ${nextStatus}`,
    }, async reason => {
      await command(`/api/work-inbox/assignments/${button.dataset.assignmentId}/status`, { status: nextStatus }, reason);
      toast(`Assignment ${nextStatus.toLowerCase()}.`);
      await dailyMissionPage(root, { me });
    });
  }));
  root.querySelectorAll('[data-recovery-order]').forEach(button => button.addEventListener('click', async () => {
    const key = `${button.dataset.recoveryOrder}:${button.dataset.recoverySequence}`;
    const panel = root.querySelector(`[data-recovery-panel="${key}"]`);
    if (!panel || button.disabled) return;
    button.disabled = true;
    button.textContent = 'Checking current coverage…';
    panel.hidden = false;
    panel.innerHTML = '<p>Rechecking the operation, candidate qualifications, safety rules, and source impact…</p>';
    try {
      const preview = await get(`/api/daily-mission/recovery/${encodeURIComponent(button.dataset.recoveryOrder)}/${encodeURIComponent(button.dataset.recoverySequence)}`);
      panel.innerHTML = renderRecoveryPreview(preview);
      button.textContent = 'Refresh coverage preview';
    } catch (error) {
      panel.innerHTML = errorBlock(error, 'Coverage preview');
      button.textContent = 'Retry coverage preview';
    } finally { button.disabled = false; }
  }));
  const welcome = root.querySelector('[data-first-briefing]');
  const welcomeToggle = root.querySelector('[data-reopen-briefing]');
  welcomeToggle?.setAttribute('aria-expanded', String(briefing.shouldShow));
  const closeWelcome = () => {
    if (welcome) welcome.hidden = true;
    welcomeToggle?.setAttribute('aria-expanded', 'false');
  };
  root.querySelector('[data-open-plan]')?.addEventListener('click', () => {
    closeWelcome();
    focusSection(root, '.daily-mission-section');
  });
  root.querySelector('[data-review-recommendations]')?.addEventListener('click', () => {
    closeWelcome();
    focusSection(root, '[data-mission-group="Requires Attention"]');
  });
  root.querySelector('[data-dismiss-briefing]')?.addEventListener('click', async event => {
    const button = event.currentTarget ?? event.target;
    button.disabled = true;
    try { await statePost('/api/daily-mission/briefing/dismiss'); closeWelcome(); focusSection(root, '.daily-mission-brief'); }
    catch (error) { button.disabled = false; button.title = error.message; }
  });
  root.querySelector('[data-reopen-briefing]')?.addEventListener('click', () => {
    if (welcome) {
      welcome.hidden = false;
      welcomeToggle?.setAttribute('aria-expanded', 'true');
      welcome.scrollIntoView?.(scrollOptions());
      return;
    }
    const briefingPanel = root.querySelector('.daily-mission-brief');
    briefingPanel?.scrollIntoView?.(scrollOptions());
  });
  root.querySelector('[data-save-preferences]')?.addEventListener('click', async event => {
    const button = event.currentTarget ?? event.target;
    button.disabled = true;
    try {
      const saved = await put('/api/daily-mission/preferences', {
        landingRoute: root.querySelector('[data-landing-route]')?.value,
        hawkAccents: root.querySelector('[data-hawk-accents]')?.value,
        programScope: root.querySelector('[data-program-scope]')?.value ?? 'all',
      });
      const programScopeChanged = (preferences?.programScope ?? 'all') !== saved.programScope;
      preferences = saved;
      button.textContent = 'Saved';
      if (programScopeChanged) await dailyMissionPage(root, { me });
    } catch (error) { button.title = error.message; }
    finally { button.disabled = false; }
  });
}
