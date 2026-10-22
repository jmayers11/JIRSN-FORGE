import { readPost, tryGet } from '../api.js';
import { esc, $, $$, errorBlock, emptyBlock, fmtDateTime, toast } from '../ui.js';

const DRAFT_VERSION = 1;
const ACTIVE_QUALIFICATION = 'Qualified';

export async function workforceKanbanPage(page, ctx) {
  const permissions = ctx.me?.permissions ?? [];
  if (!permissions.includes('workforce.supervise') && !permissions.includes('workforce.manage')) {
    page.innerHTML = `<div class="page-head"><div><h1>Workforce Kanban</h1></div></div>${emptyBlock('Supervisor access required', 'A supervisor or workforce manager role is required to open the staffing scenario board.')}`;
    return { dispose() {} };
  }
  const draftKey = `forge.workforce-kanban.${ctx.me?.personId ?? 'unknown'}`;
  const state = {
    resources: null, workers: [], qualifications: [], operations: [], schedule: null,
    errors: {}, refreshedAt: null, department: '', shift: '', query: '', moves: new Map(),
    preview: null, previewError: null, previewBusy: false, staleSchedule: false,
  };

  restoreDraft();
  await load();

  async function load() {
    const now = Date.now(), iso = ms => new Date(ms).toISOString();
    const [resources, workers, operations, schedule, downtime] = await Promise.all([
      tryGet('/api/resources/'),
      tryGet('/api/workforce/workers'),
      tryGet('/api/readiness/operations'),
      tryGet('/api/planning/schedules/approved'),
      tryGet(`/api/maintenance/downtime?from=${encodeURIComponent(iso(now - 864e5))}&to=${encodeURIComponent(iso(now + 7 * 864e5))}`),
    ]);
    state.resources = resources.data ?? null;
    state.workers = workers.data ?? [];
    state.operations = operations.data ?? [];
    state.schedule = schedule.data ?? null;
    state.downtime = downtime.data ?? [];
    state.errors = { resources: resources.error, workers: workers.error, operations: operations.error, schedule: schedule.error, downtime: downtime.error };
    state.refreshedAt = new Date().toISOString();
    await loadQualifications();
    render();
  }

  async function loadQualifications() {
    const ids = state.workers.map(worker => worker.personId).filter(Boolean);
    const results = [];
    for (let offset = 0; offset < ids.length; offset += 100) {
      const query = new URLSearchParams();
      ids.slice(offset, offset + 100).forEach(id => query.append('personIds', id));
      const result = await tryGet(`/api/competency/people/qualifications?${query}`);
      if (result.error) { state.errors.qualifications = result.error; return; }
      results.push(...(result.data ?? []));
    }
    state.qualifications = results;
  }

  function restoreDraft() {
    try {
      const saved = JSON.parse(window.sessionStorage?.getItem(draftKey) ?? 'null');
      if (saved?.version === DRAFT_VERSION && Array.isArray(saved.moves))
        state.moves = new Map(saved.moves.filter(move => move && typeof move.personId === 'string').map(move => [move.personId, move]));
    } catch { /* storage may be blocked or contain an old draft */ }
  }

  function saveDraft() {
    try {
      window.sessionStorage?.setItem(draftKey, JSON.stringify({ version: DRAFT_VERSION, savedAt: new Date().toISOString(), moves: [...state.moves.values()] }));
      toast('Scenario draft saved in this browser tab. No live records were changed.');
    } catch { toast('This browser could not save the draft. It remains open until you leave the page.', true); }
  }

  function resetDraft() {
    state.moves.clear(); state.preview = null; state.previewError = null; state.staleSchedule = false;
    try { window.sessionStorage?.removeItem(draftKey); } catch { /* in-memory reset still works */ }
    render();
  }

  function centers() { return state.resources?.workCenters ?? []; }
  function centerByCode(code) { return centers().find(center => center.code === code); }
  function activitiesAt(code) {
    const centerActivities = (state.resources?.workCenterActivities ?? []).filter(activity => activity.workCenterCode === code).map(activity => activity.activityCode);
    const machineActivities = (state.resources?.machineActivities ?? []).filter(activity => state.resources?.machines?.some(machine => machine.code === activity.machineCode && machine.workCenterCode === code)).map(activity => activity.activityCode);
    return [...new Set([...centerActivities, ...machineActivities])].sort((a, b) => a.localeCompare(b));
  }
  function machinesFor(code, activity) {
    return (state.resources?.machineActivities ?? []).filter(item => item.activityCode === activity)
      .map(item => state.resources?.machines?.find(machine => machine.code === item.machineCode))
      .filter(machine => machine?.workCenterCode === code);
  }
  function hasCenterActivity(code, activity) {
    return (state.resources?.workCenterActivities ?? []).some(item => item.workCenterCode === code && item.activityCode === activity);
  }
  function moveIncomplete(move) {
    if (move.unassign) return false;
    if (!move.activityCode || !move.workCenterCode) return true;
    const machines = machinesFor(move.workCenterCode, move.activityCode);
    if (machines.length && !hasCenterActivity(move.workCenterCode, move.activityCode) && !move.machineCode) return true;
    return !!move.machineCode && !machines.some(machine => machine.code === move.machineCode);
  }
  function currentCenter(worker) { return worker.assignment?.workCenterCode ?? null; }
  function destination(worker) {
    const move = state.moves.get(worker.personId);
    return move ? (move.unassign ? null : move.workCenterCode) : currentCenter(worker);
  }
  function available(worker) { return worker.isActive && !worker.unavailability && !!worker.presenceKind; }
  function laneFor(worker) {
    const target = destination(worker);
    if (target) return target;
    return available(worker) ? '__available' : '__unavailable';
  }
  function validQualifications(personId) {
    const now = Date.now();
    return state.qualifications.filter(record => record.personId === personId && record.status === ACTIVE_QUALIFICATION
      && (!record.expiresAt || new Date(record.expiresAt).getTime() > now)).map(record => record.code);
  }
  function assignmentText(assignment) {
    if (!assignment) return 'No current assignment';
    return [assignment.activityCode, assignment.machineCode, assignment.workCenterCode].filter(Boolean).join(' · ');
  }
  function presenceText(worker) {
    if (worker.unavailability) return `Unavailable: ${worker.unavailability}`;
    if (!worker.isActive) return 'Inactive';
    if (!worker.presenceKind) return 'No current presence';
    return `Present: ${worker.presenceKind}${worker.presentUntil ? ` until ${fmtDateTime(worker.presentUntil)}` : ''}`;
  }
  function scheduleOperations(code) {
    return (state.schedule?.result?.operations ?? []).filter(operation => (operation.workCenterCode ?? operation.workCenter) === code);
  }
  function atRiskOrders() {
    return (state.schedule?.result?.orders ?? []).filter(order => ['AtRisk', 'Late', 'Unschedulable'].includes(order.status));
  }

  function render() {
    if (!state.resources) {
      page.innerHTML = `<div class="page-head"><div><h1>Workforce Kanban</h1></div></div>${errorBlock(state.errors.resources, 'plant resources')}`;
      return;
    }
    const areaOptions = state.resources.safetyAreas ?? [];
    const shiftOptions = state.resources.shifts ?? [];
    const moves = [...state.moves.values()];
    const incomplete = moves.some(moveIncomplete);
    const currentStaff = state.workers.filter(worker => !!worker.assignment).length;
    const qualified = state.qualifications.filter(record => record.status === ACTIVE_QUALIFICATION && (!record.expiresAt || new Date(record.expiresAt) > new Date())).length;
    const riskOrders = atRiskOrders();
    const scheduleVersion = state.schedule?.versionId;
    const scheduleStatus = scheduleVersion ? `Approved production schedule #${scheduleVersion}` : 'No approved production schedule';
    const freshness = state.refreshedAt ? `Forge records refreshed ${fmtDateTime(state.refreshedAt)}` : 'Refresh time unavailable';
    const error = state.errors.workers || state.errors.operations || state.errors.schedule || state.errors.downtime || state.errors.qualifications;

    page.innerHTML = `
      <div class="page-head">
        <div><h1>Workforce Kanban</h1><div class="sub">Review current Forge assignments and stage scenario-only moves. Live workforce and attendance records are never changed here.</div></div>
        <div class="spacer"></div><div class="head-controls">
          <a class="btn" href="#/supervisor">Plant Overview</a>
          <a class="btn" href="#/machine-kanban">Machine Readiness</a>
          <button class="btn" id="refresh-board" type="button">Refresh</button>
        </div>
      </div>
      <div class="notice warn"><b>Current-state board.</b> Forge currently has an approved production-operation schedule, but no approved person-by-person weekly roster. Cards show current worker assignments and actual presence. The schedule context below is production-only.</div>
      ${error ? `<section class="panel">${errorBlock(error, 'one or more staffing data sources')}</section>` : ''}
      <section class="kpis workforce-board-kpis" aria-label="Staffing summary">
        <div class="kpi"><div><div class="t">Workers in current assignments</div><div class="v">${currentStaff}</div><div class="why">Actual assignments only</div></div></div>
        <div class="kpi"><div><div class="t">Current qualified records</div><div class="v">${qualified}</div><div class="why">Qualification records; not a staffing count</div></div></div>
        <div class="kpi"><div><div class="t">Orders at risk</div><div class="v">${scheduleVersion ? riskOrders.length : '—'}</div><div class="why">From ${esc(scheduleStatus)}</div></div></div>
        <div class="kpi"><div><div class="t">Data freshness</div><div class="v">${esc(freshness)}</div><div class="why">Not a real-time guarantee</div></div></div>
      </section>
      <section class="panel workforce-kanban-filters" aria-label="Board filters">
        <label class="field"><span>Department / safety area</span><select class="input" id="kanban-area"><option value="">All areas</option>${areaOptions.map(area => `<option value="${esc(area.code)}" ${area.code === state.department ? 'selected' : ''}>${esc(area.name)}</option>`).join('')}</select></label>
        <label class="field"><span>Shift</span><select class="input" id="kanban-shift"><option value="">All shifts</option>${shiftOptions.map(shift => `<option value="${esc(shift.code)}" ${shift.code === state.shift ? 'selected' : ''}>${esc(shift.name)}</option>`).join('')}</select></label>
        <label class="field"><span>Find a person</span><input class="input" id="kanban-search" value="${esc(state.query)}" placeholder="Name or employee ID" autocomplete="off"></label>
      </section>
      <section class="panel workforce-scenario-controls" aria-label="Scenario controls">
        <div><b>Scenario draft · ${moves.length} proposed move${moves.length === 1 ? '' : 's'}</b><div class="muted small">Saved drafts stay in this browser tab. Analyze checks successive moves against one current Forge snapshot.</div></div>
        <div class="toolbar">
          <button class="btn" id="save-draft" type="button" ${moves.length ? '' : 'disabled'}>Save draft</button>
          <button class="btn" id="analyze-draft" type="button" ${!moves.length || incomplete || state.previewBusy ? 'disabled' : ''}>${state.previewBusy ? 'Checking safety…' : 'Analyze safety impact'}</button>
          <button class="btn" id="reset-draft" type="button" ${moves.length ? '' : 'disabled'}>Reset scenario</button>
        </div>
      </section>
      ${state.previewError ? `<section class="panel">${errorBlock(state.previewError, 'the safety scenario preview')}</section>` : ''}
      ${state.staleSchedule ? '<div class="notice warn"><b>Schedule changed while this scenario was open.</b> Refresh the board before relying on schedule context.</div>' : ''}
      ${renderPreview()}
      <section class="panel workforce-schedule-context"><header><h2>Production schedule context</h2><span class="spacer"></span><a href="#/scheduling">Open Scheduling</a></header>
        <p class="muted">${esc(scheduleStatus)}. These are order operations, not employee assignments.</p>
        ${scheduleVersion ? renderProductionContext() : emptyBlock('No approved production schedule', state.errors.schedule ? 'Schedule context is unavailable.' : 'An approved schedule will appear here when a planner approves one.')}
      </section>
      ${renderBoard()}`;

    $('#refresh-board', page).addEventListener('click', () => { state.preview = null; state.previewError = null; void load(); });
    $('#kanban-area', page).addEventListener('change', event => { state.department = event.target.value; render(); });
    $('#kanban-shift', page).addEventListener('change', event => { state.shift = event.target.value; render(); });
    $('#kanban-search', page).addEventListener('input', event => { state.query = event.target.value; renderBoardOnly(); });
    $('#save-draft', page).addEventListener('click', saveDraft);
    $('#reset-draft', page).addEventListener('click', resetDraft);
    $('#analyze-draft', page).addEventListener('click', () => { void analyze(); });
    wireCards();
  }

  function renderBoardOnly() {
    const board = $('.workforce-lanes', page);
    const status = $('.workforce-lanes-status', page);
    if (!board) return;
    const fresh = renderLanes();
    board.outerHTML = fresh;
    wireCards();
    if (status) status.textContent = `${visibleWorkers().length} workers shown`;
  }

  function visibleWorkers() {
    const query = state.query.trim().toLocaleLowerCase();
    return state.workers.filter(worker => {
      const area = worker.assignment?.safetyAreaCode ?? state.resources?.workCenters?.find(center => center.code === worker.homeWorkCenterCode)?.safetyAreaCode;
      if (state.department && area !== state.department) return false;
      if (state.shift && worker.shiftCode !== state.shift) return false;
      if (query && !`${worker.displayName} ${worker.personId}`.toLocaleLowerCase().includes(query)) return false;
      return true;
    });
  }

  function renderBoard() {
    return `<section class="panel workforce-board-panel"><header><div><h2>Current staffing by work center</h2><div class="muted small">Drag a card to stage a move, or use the keyboard-accessible destination controls on each card.</div></div><span class="spacer"></span><span class="pill info">Scenario only</span></header>
      ${state.errors.workers ? errorBlock(state.errors.workers, 'current workforce') : state.workers.length ? `<div class="muted small workforce-lanes-status" role="status">${visibleWorkers().length} workers shown</div>${renderLanes()}` : emptyBlock('No registered workers', 'Register workers and set up work centers before building staffing plans.')}
    </section>`;
  }

  function renderLanes() {
    const shown = visibleWorkers();
    const laneDefs = [
      { code: '__available', name: 'Unassigned / Available', safetyAreaCode: null },
      { code: '__unavailable', name: 'Unavailable / no current presence', safetyAreaCode: null },
      ...centers().filter(center => !state.department || center.safetyAreaCode === state.department).map(center => ({ code: center.code, name: center.name, safetyAreaCode: center.safetyAreaCode })),
    ];
    return `<div class="workforce-lanes" aria-label="Workforce staffing lanes">${laneDefs.map(lane => {
      const people = shown.filter(worker => laneFor(worker) === lane.code);
      const ops = lane.safetyAreaCode ? scheduleOperations(lane.code) : [];
      const currentOps = lane.safetyAreaCode ? state.operations.filter(operation => operation.workCenter === lane.code) : [];
      const notReady = currentOps.filter(operation => !operation.executable).length;
      const presentPeople = people.filter(available).length;
      const downMachines = lane.safetyAreaCode ? (state.resources.machines ?? []).filter(machine => machine.workCenterCode === lane.code
        && state.downtime.some(window => window.machineCode === machine.code && new Date(window.startsAt).getTime() <= Date.now()
          && (!window.endsAt || new Date(window.endsAt).getTime() > Date.now()))).length : 0;
      return `<section class="workforce-lane" data-drop-lane="${esc(lane.code)}" aria-label="${esc(lane.name)}">
        <header><div><b>${esc(lane.name)}</b>${lane.safetyAreaCode ? `<div class="muted small">${esc(lane.safetyAreaCode)}</div>` : ''}</div><span class="pill ${notReady || downMachines ? 'warn' : 'info'}">${people.length} people</span></header>
        ${lane.safetyAreaCode ? `<div class="lane-metrics"><span>${presentPeople} with current presence</span><span>${ops.length} approved production operation${ops.length === 1 ? '' : 's'}</span><span>${currentOps.length - notReady}/${currentOps.length} current operation${currentOps.length === 1 ? '' : 's'} executable</span>${notReady ? `<span class="pill bad">${notReady} blocked</span>` : ''}${downMachines ? `<span class="pill bad">${downMachines} machine${downMachines === 1 ? '' : 's'} down</span>` : ''}</div>` : ''}
        <div class="workforce-lane-cards">${people.length ? people.map(worker => cardHtml(worker, lane)).join('') : '<div class="empty small">No workers in this lane.</div>'}</div>
      </section>`;
    }).join('')}</div>`;
  }

  function cardHtml(worker, lane) {
    const move = state.moves.get(worker.personId);
    const current = worker.assignment;
    const targetCenter = move && !move.unassign ? centerByCode(move.workCenterCode) : null;
    const currentSafetyArea = current?.safetyAreaCode ?? worker.assignment?.safetyAreaCode ?? worker.safetyAreaCode;
    const destinationOptions = [
      '<option value="__available">Unassigned / Available</option>',
      ...centers().map(center => `<option value="${esc(center.code)}">${esc(center.name)}</option>`),
    ].join('');
    const q = validQualifications(worker.personId);
    const draftPanel = move ? move.unassign ? '<div class="notice warn small">Proposed: unassign for the affected period. No machine output is assigned.</div>' : assignmentEditor(move, targetCenter) : '';
    return `<article class="staff-card ${move ? 'is-proposed' : ''}" draggable="true" data-worker-card="${esc(worker.personId)}" aria-label="${esc(worker.displayName)}${move ? ', proposed move' : ''}">
      <div class="staff-card-head"><b>${esc(worker.displayName)}</b>${move ? '<span class="pill warn">Proposed</span>' : ''}</div>
      <div class="staff-card-status"><span class="pill ${available(worker) ? 'ok' : worker.unavailability ? 'bad' : 'none'}">${esc(presenceText(worker))}</span></div>
      <div class="staff-card-details"><span><b>Shift</b> ${esc(worker.shiftCode ?? 'Not assigned')}</span>
        <span><b>Current</b> ${esc(assignmentText(current))}</span>
        <span><b>Home</b> ${esc(worker.homeWorkCenterCode ?? '—')}</span>
        <span><b>Qualifications</b> ${q.length ? q.map(code => `<span class="pill info">${esc(code)}</span>`).join(' ') : state.errors.qualifications ? '<span class="muted">Unavailable</span>' : '<span class="muted">None currently valid</span>'}</span>
        ${current?.asTrainee ? '<span class="pill warn">Trainee assignment</span>' : ''}
      </div>
      <div class="staff-card-actions"><a class="btn small" href="#/scenarios?kind=PersonUnavailable&amp;personId=${encodeURIComponent(worker.personId)}&amp;scenarioName=${encodeURIComponent(`Model absence · ${worker.displayName}`)}">Model absence (scenario only)</a></div>
      <div class="staff-card-move"><label for="dest-${esc(worker.personId)}">Proposed destination</label><div class="staff-card-move-row">
        <select class="input" id="dest-${esc(worker.personId)}" data-destination="${esc(worker.personId)}">${destinationOptions.replace(`value="${esc(destination(worker) ?? '__available')}"`, `value="${esc(destination(worker) ?? '__available')}" selected`)}</select>
        <button class="btn small" type="button" data-stage-worker="${esc(worker.personId)}">Stage move</button>
      </div></div>
      ${draftPanel}
    </article>`;
  }

  function assignmentEditor(move, center) {
    if (!center) return '';
    const activities = activitiesAt(center.code);
    const selected = move.activityCode ?? '';
    const activityOptions = ['<option value="">Choose activity</option>', ...activities.map(code => `<option value="${esc(code)}" ${code === selected ? 'selected' : ''}>${esc(code)}</option>`)].join('');
    const machines = move.activityCode ? machinesFor(center.code, move.activityCode) : [];
    const machineRequired = move.activityCode && machines.length > 0 && !hasCenterActivity(center.code, move.activityCode);
    const machineOptions = [
      ...(hasCenterActivity(center.code, move.activityCode) ? ['<option value="">Support / no machine</option>'] : []),
      ...machines.map(machine => `<option value="${esc(machine.code)}" ${machine.code === move.machineCode ? 'selected' : ''}>${esc(machine.code)} · ${esc(machine.name)}</option>`),
    ].join('');
    return `<div class="staff-card-editor">
      <label>Proposed activity<select class="input" data-draft-activity="${esc(move.personId)}">${activityOptions}</select></label>
      ${move.activityCode && (machines.length || hasCenterActivity(center.code, move.activityCode)) ? `<label>Machine / duty<select class="input" data-draft-machine="${esc(move.personId)}" ${machineRequired ? 'required' : ''}>${machineOptions}</select></label>` : ''}
      ${!activities.length ? '<div class="notice bad small">No activities are configured at this work center. Choose another destination.</div>' : machineRequired && !move.machineCode ? '<div class="notice warn small">Choose an eligible machine before analyzing this move.</div>' : ''}
      <button class="btn small" type="button" data-remove-move="${esc(move.personId)}">Remove proposed move</button>
    </div>`;
  }

  function renderProductionContext() {
    const operations = state.schedule?.result?.operations ?? [];
    const orders = state.schedule?.result?.orders ?? [];
    if (!operations.length) return emptyBlock('No operation rows in the approved schedule', 'The approved schedule contains no visible production operations for this view.');
    const rows = operations.slice(0, 24).map(operation => `<tr>
      <td><a href="#/orders/${esc(operation.orderNumber)}">#${esc(operation.orderNumber)}</a></td><td>${esc(operation.sequence)}</td>
      <td>${esc(operation.workCenterCode ?? operation.workCenter)}</td><td>${esc(operation.machineCode ?? operation.machine ?? '—')}</td>
      <td>${esc(fmtDateTime(operation.start))}</td><td>${esc(operation.scheduled === false ? 'Not scheduled' : 'Scheduled')}</td></tr>`).join('');
    return `<div class="table-wrap"><table class="table"><thead><tr><th>Order</th><th>Op</th><th>Work center</th><th>Machine</th><th>Start</th><th>Plan state</th></tr></thead><tbody>${rows}</tbody></table></div>
      ${orders.length > 24 ? `<div class="muted small">Showing 24 of ${operations.length} operations. Open Scheduling for the full production plan.</div>` : ''}`;
  }

  function renderPreview() {
    if (!state.preview && !state.previewError) return '';
    const output = state.preview?.output;
    if (!output) return '';
    return `<section class="panel workforce-preview" aria-live="polite"><header><div><h2>Scenario safety preview</h2><div class="muted small">Read-only · snapshot ${esc(fmtDateTime(output.asOf))}</div></div><span class="spacer"></span><span class="pill ${state.preview.evidence?.outcome === 'Pass' ? 'ok' : 'bad'}">${state.preview.evidence?.outcome === 'Pass' ? 'No active-work safety loss found' : 'Safety impact found'}</span></header>
      <p class="muted">This evaluates active work and staffing safety only. It does not calculate order due dates, material/MRP, overtime costs, or a future roster. A safety finding is not an override.</p>
      ${output.steps.map(step => {
        const preview = step.preview;
        const findings = preview.impacts ?? [];
        const readiness = (preview.after ?? []).filter(result => !result.executable);
        return `<article class="workforce-preview-step"><h3>Move ${step.number}: ${esc(preview.personName)}</h3><div class="muted">${esc(assignmentText(preview.current))} → ${esc(assignmentText(preview.proposed))}</div>
          ${findings.length ? `<ul class="evidence">${findings.map(item => `<li class="${['WouldStop', 'WouldBeUnstaffed'].includes(item.kind) ? 'c-bad' : 'c-info'}"><b>${esc(item.kind)}:</b> ${esc(item.detail)}</li>`).join('')}</ul>` : '<div class="notice ok small">This move changes no currently executable work.</div>'}
          ${readiness.length ? `<div class="muted small">Work still needing attention after this step:</div><ul class="evidence">${readiness.slice(0, 8).map(item => `<li><b>${esc(item.work?.activityCode)} · ${esc(item.work?.machineCode ?? item.work?.workCenterCode)}</b> — ${esc(item.primaryConstraint ?? 'Not executable')}${item.dimensions?.length ? `: ${esc(item.dimensions.filter(d => d.outcome === 'Fail' || d.outcome === 'Indeterminate').map(d => d.detail).join(' ') )}` : ''}</li>`).join('')}</ul>` : ''}
          ${(preview.options ?? []).length ? `<div class="muted small"><b>Possible safe responses to review:</b> ${(preview.options ?? []).map(option => esc(option.description)).join(' · ')}</div>` : ''}
        </article>`;
      }).join('')}
    </section>`;
  }

  async function analyze() {
    const moves = [...state.moves.values()];
    if (!moves.length || moves.some(moveIncomplete)) {
      toast('Choose a destination activity for every proposed move first.', true); return;
    }
    const proposed = moves.map(move => ({
      personId: move.personId,
      safetyAreaCode: move.safetyAreaCode ?? null,
      workCenterCode: move.workCenterCode ?? null,
      machineCode: move.machineCode || null,
      activityCode: move.activityCode ?? null,
      asTrainee: false,
      unassign: !!move.unassign,
    }));
    state.previewBusy = true; state.previewError = null; render();
    try {
      state.preview = await readPost('/api/readiness/reallocation-plan-preview', { moves: proposed });
      const latest = await tryGet('/api/planning/schedules/approved');
      state.staleSchedule = !latest.error && (latest.data?.versionId ?? null) !== (state.schedule?.versionId ?? null);
      if (state.staleSchedule) state.preview = null;
    } catch (error) {
      state.preview = null; state.previewError = error;
    }
    state.previewBusy = false; render();
  }

  function stage(worker, targetCode) {
    const previous = state.moves.get(worker.personId);
    if (targetCode === '__available') {
      state.moves.set(worker.personId, { personId: worker.personId, unassign: true });
    } else {
      const center = centerByCode(targetCode);
      if (!center) return;
      const activities = activitiesAt(center.code);
      const existingActivity = worker.assignment?.activityCode;
      const activityCode = previous?.workCenterCode === center.code ? previous.activityCode
        : activities.includes(existingActivity) ? existingActivity : activities.length === 1 ? activities[0] : '';
      const machines = activityCode ? machinesFor(center.code, activityCode) : [];
      const machineCode = previous?.workCenterCode === center.code ? previous.machineCode
        : machines.some(machine => machine.code === worker.assignment?.machineCode) ? worker.assignment.machineCode : '';
      state.moves.set(worker.personId, { personId: worker.personId, unassign: false, safetyAreaCode: center.safetyAreaCode, workCenterCode: center.code, activityCode, machineCode });
    }
    state.preview = null; state.previewError = null; render();
  }

  function wireCards() {
    $$('[data-stage-worker]', page).forEach(button => button.addEventListener('click', () => {
      const worker = state.workers.find(item => item.personId === button.dataset.stageWorker);
      const target = $$('[data-destination]', page).find(select => select.dataset.destination === button.dataset.stageWorker)?.value;
      if (worker) stage(worker, target);
    }));
    $$('[data-draft-activity]', page).forEach(select => select.addEventListener('change', () => {
      const move = state.moves.get(select.dataset.draftActivity);
      if (!move) return;
      move.activityCode = select.value;
      const allowed = machinesFor(move.workCenterCode, move.activityCode).some(machine => machine.code === move.machineCode);
      if (!allowed) move.machineCode = '';
      state.preview = null; state.previewError = null; render();
    }));
    $$('[data-draft-machine]', page).forEach(select => select.addEventListener('change', () => {
      const move = state.moves.get(select.dataset.draftMachine);
      if (!move) return;
      move.machineCode = select.value;
      state.preview = null; state.previewError = null; render();
    }));
    $$('[data-remove-move]', page).forEach(button => button.addEventListener('click', () => {
      state.moves.delete(button.dataset.removeMove); state.preview = null; state.previewError = null; render();
    }));
    $$('[data-worker-card]', page).forEach(card => {
      card.addEventListener('dragstart', event => event.dataTransfer?.setData('text/plain', card.dataset.workerCard));
      card.addEventListener('dragend', () => $$('.workforce-lane.is-drop-target', page).forEach(lane => lane.classList.remove('is-drop-target')));
    });
    $$('[data-drop-lane]', page).forEach(lane => {
      lane.addEventListener('dragover', event => { event.preventDefault(); lane.classList.add('is-drop-target'); });
      lane.addEventListener('dragleave', event => { if (!lane.contains(event.relatedTarget)) lane.classList.remove('is-drop-target'); });
      lane.addEventListener('drop', event => {
        event.preventDefault(); lane.classList.remove('is-drop-target');
        const id = event.dataTransfer?.getData('text/plain');
        const worker = state.workers.find(item => item.personId === id);
        if (worker) stage(worker, lane.dataset.dropLane === '__unavailable' ? '__available' : lane.dataset.dropLane);
      });
    });
  }

  render();
  return { dispose() {} };
}
