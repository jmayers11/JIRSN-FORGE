import { command, fetchFile, readPost, removeVisualImage, tryGet, uploadVisualImage } from '../api.js';
import { esc, $, $$, errorBlock, emptyBlock, fmtDateTime, toast, confirmWithReason, drawer } from '../ui.js';
import { canAction, formModal } from '../forms.js';
import { stagePhoto } from '../icons.js';
import { deriveMachineCards } from './machine-kanban.js';

const DRAFT_VERSION = 1;

export async function workforceKanbanPage(page, ctx) {
  const permissions = ctx.me?.permissions ?? [];
  if (!permissions.includes('workforce.supervise') && !permissions.includes('workforce.manage') && !permissions.includes('resources.floor-map.manage')) {
    page.innerHTML = `<div class="page-head"><div><h1>Workforce Kanban</h1></div></div>${emptyBlock('Supervisor access required', 'A supervisor, workforce manager or authorized plant-map manager role is required to open this board.')}`;
    return { dispose() {} };
  }
  const draftKey = `forge.workforce-kanban.${ctx.me?.personId ?? 'unknown'}`;
  const state = {
    resources: null, workers: [], schedule: null, unavailability: [],
    qualificationTypes: [], qualifications: [], machineAuthorizations: [], qualificationCoverage: 'not-required',
    errors: {}, refreshedAt: null, department: '', shift: '', query: '', moves: new Map(),
    preview: null, previewError: null, previewBusy: false, staleSchedule: false, expandedPersonId: null,
    layoutEdit: false, layoutDraft: new Map(), layoutBaseline: new Map(), viewMode: 'board', machineCards: [], machineReadAt: null,
    mapZoom: 1, mapLayers: { workers: true, readiness: true, machines: true, orders: false, materials: false, quality: false, safety: false, maintenance: false, bottlenecks: false }, mapCoverageQuery: '',
    machineEvidenceErrors: {},
    expandedLanes: new Set(), expandedMapCenter: null, openFloorDetails: new Set(), visualUrls: new Map(), pendingVisuals: new Set(), disposed: false,
  };

  restoreDraft();
  await load();

  async function load() {
    const from = new Date();
    const to = new Date(from.getTime() + 30 * 24 * 60 * 60 * 1000);
    const [resources, workers, schedule, unavailability] = await Promise.all([
      tryGet('/api/resources/'),
      tryGet('/api/workforce/workers'),
      tryGet('/api/planning/schedules/approved'),
      tryGet(`/api/workforce/unavailability?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`),
    ]);
    state.resources = resources.data ?? null;
    state.workers = workers.data ?? [];
    state.schedule = schedule.data ?? null;
    state.unavailability = Array.isArray(unavailability.data) ? unavailability.data : [];
    state.errors = { resources: resources.error, workers: workers.error, schedule: schedule.error, unavailability: unavailability.error };
    await loadQualificationCoverage();
    await loadMachineReadiness();
    state.refreshedAt = new Date().toISOString();
    render();
    void loadVisuals();
  }

  async function loadQualificationCoverage() {
    const requiredCodes = [...new Set((state.resources?.machineActivities ?? []).flatMap(profile => profile.operatorQualifications ?? []))];
    state.qualificationTypes = [];
    state.qualifications = [];
    state.machineAuthorizations = [];
    delete state.errors.qualifications;
    if (!requiredCodes.length) { state.qualificationCoverage = 'not-required'; return; }
    if (!(ctx.me?.permissions ?? []).includes('competency.read')) {
      state.qualificationCoverage = 'unavailable';
      state.errors.qualifications = new Error('Qualification records are not available to verify specialized work-center placements.');
      return;
    }

    state.qualificationCoverage = 'loading';
    const typesResult = await tryGet('/api/competency/qualification-types');
    if (typesResult.error) {
      state.qualificationCoverage = 'unavailable';
      state.errors.qualifications = typesResult.error;
      return;
    }
    state.qualificationTypes = typesResult.data ?? [];
    const people = state.workers.map(worker => worker.personId).filter(Boolean);
    for (let offset = 0; offset < people.length; offset += 100) {
      const query = new URLSearchParams();
      people.slice(offset, offset + 100).forEach(personId => query.append('personIds', personId));
      const [result, authorizationResult] = await Promise.all([
        tryGet(`/api/competency/people/qualifications?${query}`),
        tryGet(`/api/competency/machine-authorizations?${query}`),
      ]);
      if (result.error || authorizationResult.error) {
        state.qualificationTypes = [];
        state.qualifications = [];
        state.machineAuthorizations = [];
        state.qualificationCoverage = 'unavailable';
        state.errors.qualifications = result.error ?? authorizationResult.error;
        return;
      }
      state.qualifications.push(...(Array.isArray(result.data) ? result.data : []));
      state.machineAuthorizations.push(...(Array.isArray(authorizationResult.data) ? authorizationResult.data : []));
    }
    state.qualificationCoverage = 'ready';
  }

  async function loadMachineReadiness() {
    const permissions = ctx.me?.permissions ?? [];
    const permittedGet = (permission, path) => permissions.includes(permission)
      ? tryGet(path)
      : Promise.resolve({ denied: true, data: null });
    const now = Date.now();
    const iso = value => new Date(value).toISOString();
    const [operations, activeWork, downtime, assets, workOrders] = await Promise.all([
      permittedGet('planning.readiness.read', '/api/readiness/operations'),
      permittedGet('planning.readiness.read', '/api/readiness/active-work'),
      permittedGet('maintenance.read', `/api/maintenance/downtime?from=${encodeURIComponent(iso(now - 864e5))}&to=${encodeURIComponent(iso(now + 14 * 864e5))}`),
      permittedGet('maintenance.read', '/api/maintenance/assets'),
      permittedGet('maintenance.read', '/api/maintenance/work-orders?includeClosed=false'),
    ]);
    const errors = { resources: state.errors.resources, operations: operations.error, activeWork: activeWork.error };
    for (const [name, result] of Object.entries({ downtime, assets, workOrders })) {
      if (!result.denied && result.error) errors[name] = result.error;
    }
    state.machineEvidenceErrors = errors;
    const maintenanceOrders = (workOrders.data ?? []).map(view => ({ ...(view.order ?? {}), runHours: view.runHours, dueByRunHours: view.dueByRunHours, parts: view.parts }));
    state.machineCards = deriveMachineCards({
      machines: state.resources?.machines ?? [], workCenters: state.resources?.workCenters ?? [],
      operations: operations.data ?? [], activeWork: activeWork.data ?? [], schedule: state.schedule,
      downtime: downtime.data ?? [], assets: assets.data ?? [], maintenanceOrders,
      workers: state.workers, qualifications: state.qualifications, errors, permissions, now,
    });
    state.machineReadAt = new Date(now).toISOString();
  }

  async function loadVisuals() {
    const images = [
      ...state.workers.filter(worker => worker.hasPhoto).map(worker => [`worker:${worker.personId}`, `/api/workforce/workers/${encodeURIComponent(worker.personId)}/photo`]),
      ...(state.resources?.sites ?? []).filter(site => site.hasCustomMap).map(site => [`site:${site.code}`, `/api/resources/sites/${encodeURIComponent(site.code)}/map/image`]),
    ];
    const keys = new Set(images.map(([key]) => key));
    for (const [key, url] of state.visualUrls) if (!keys.has(key)) clearVisual(key);
    await Promise.all(images.map(async ([key, path]) => {
      if (state.visualUrls.has(key) || state.pendingVisuals.has(key)) return;
      state.pendingVisuals.add(key);
      try {
        const { blob } = await fetchFile(path);
        if (state.disposed) return;
        if (globalThis.URL?.createObjectURL) state.visualUrls.set(key, URL.createObjectURL(blob));
        renderBoardOnly();
      } catch { /* an inaccessible/stale photo falls back to initials or the reference map */ }
      finally { state.pendingVisuals.delete(key); }
    }));
  }

  function clearVisual(key) {
    const url = state.visualUrls.get(key);
    if (url) globalThis.URL?.revokeObjectURL?.(url);
    state.visualUrls.delete(key);
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

  function centers() { return (state.resources?.workCenters ?? []).filter(center => center.active !== false); }
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
  function qualificationRequirements(move) {
    if (!move?.workCenterCode || !move.activityCode) return [];
    const profiles = state.resources?.machineActivities ?? [];
    if (move.machineCode) return profiles.find(profile => profile.machineCode === move.machineCode && profile.activityCode === move.activityCode)?.operatorQualifications ?? [];
    return machinesFor(move.workCenterCode, move.activityCode)
      .flatMap(machine => profiles.find(profile => profile.machineCode === machine.code && profile.activityCode === move.activityCode)?.operatorQualifications ?? []);
  }
  function machineProfileForMove(move) {
    if (!move?.machineCode) return null;
    return (state.resources?.machineActivities ?? []).find(profile => profile.machineCode === move.machineCode && profile.activityCode === move.activityCode) ?? null;
  }
  function qualificationRecord(personId, code) {
    return state.qualifications.find(record => record.personId === personId && record.code === code);
  }
  function currentQualified(personId, code) {
    const record = qualificationRecord(personId, code);
    return record?.status === 'Qualified' && (!record.expiresAt || new Date(record.expiresAt).getTime() > Date.now());
  }
  function currentMachineAuthorized(personId, move) {
    const profile = machineProfileForMove(move);
    if (!profile?.requiresMachineAuthorization) return true;
    return state.machineAuthorizations.some(record => record.personId === personId && record.machineCode === profile.machineCode
      && record.activityCode === profile.activityCode && record.status === 'Authorized'
      && record.independentOperationAllowed === true
      && (!record.expiresAt || Date.parse(record.expiresAt) > Date.now())
      && (!record.courseExpiresAt || Date.parse(record.courseExpiresAt) > Date.now())
      && currentQualified(personId, record.qualificationCode));
  }
  function trainerFor(worker, move, qualificationCode) {
    const trainerTypes = state.qualificationTypes.filter(type => type.kind === 'Trainer' && (type.prerequisites ?? []).includes(qualificationCode));
    return state.workers.find(candidate => candidate.personId !== worker.personId
      && candidate.isActive && !candidate.unavailability && confirmedOnSite(candidate)
      && candidate.assignment?.asTrainee !== true && currentCenter(candidate) === move.workCenterCode
      && destination(candidate) === move.workCenterCode
      && (!worker.shiftCode || candidate.shiftCode === worker.shiftCode)
      && currentQualified(candidate.personId, qualificationCode)
      && currentMachineAuthorized(candidate.personId, move)
      && trainerTypes.some(type => currentQualified(candidate.personId, type.code)));
  }
  function moveQualificationGuard(worker, move) {
    if (!move || move.unassign) return { blocked: false, message: '' };
    const required = qualificationRequirements(move);
    if (!required.length) return { blocked: false, message: '' };
    const center = centerByCode(move.workCenterCode);
    const names = [...new Set(required)].map(code => state.qualificationTypes.find(type => type.code === code)?.name ?? code);
    const areaName = center?.name ?? move.workCenterCode;
    if (state.qualificationCoverage !== 'ready') return {
      blocked: true,
      message: `Cannot verify ${areaName} qualifications. Qualification records are unavailable, so this specialized placement is blocked until coverage can be checked.`,
    };
    const hasQualification = required.some(code => currentQualified(worker.personId, code));
    const profile = machineProfileForMove(move);
    if (hasQualification && currentMachineAuthorized(worker.personId, move)) return { blocked: false, message: '' };
    if (hasQualification && profile?.requiresMachineAuthorization && !move.machineCode) return {
      blocked: true,
      message: `Choose the exact machine before staging ${worker.displayName}; this activity requires a current machine-specific authorization.`,
    };

    const trainingCodes = required.filter(code => qualificationRecord(worker.personId, code)?.status === 'InTraining'
      || (profile?.requiresMachineAuthorization && currentQualified(worker.personId, code)));
    if (!trainingCodes.length) return {
      blocked: true,
      message: `${worker.displayName} needs a current ${names.join(' or ')} qualification${profile?.requiresMachineAuthorization ? ' and machine-specific practical authorization' : ''} or a recorded training plan before placement at ${areaName}.`,
    };

    for (const code of trainingCodes) {
      const trainer = trainerFor(worker, move, code);
      if (trainer) return {
        blocked: false,
        supervised: true,
        message: `Supervised training only: ${trainer.displayName} is a qualified trainer already on site at ${areaName}. Keep the trainer there while this trainee works; the placement does not grant a qualification.`,
      };
    }
    return {
      blocked: true,
      message: `${worker.displayName} needs supervised training or practical assessment for ${trainingCodes.join(', ')}. A currently qualified trainer${profile?.requiresMachineAuthorization ? ` with authorization for ${profile.machineCode} ${profile.activityCode}` : ''} must already be clocked in and assigned to ${areaName} before this placement can proceed.`,
    };
  }
  function currentCenter(worker) { return worker.assignment?.workCenterCode ?? null; }
  function destination(worker) {
    const move = state.moves.get(worker.personId);
    return move ? (move.unassign ? null : move.workCenterCode) : currentCenter(worker);
  }
  function confirmedOnSite(worker) {
    return ['ClockedIn', 'SupervisorConfirmed'].includes(worker.presenceKind)
      && (!worker.presentUntil || new Date(worker.presentUntil).getTime() > Date.now());
  }
  function available(worker) { return worker.isActive && !worker.unavailability && confirmedOnSite(worker); }
  function laneFor(worker) {
    const target = destination(worker);
    if (target) return target;
    return available(worker) ? '__available' : '__unavailable';
  }
  function assignmentText(assignment) {
    if (!assignment) return 'No current assignment';
    return [assignment.activityCode, assignment.machineCode, assignment.workCenterCode].filter(Boolean).join(' · ');
  }
  function render() {
    if (!state.resources) {
      page.innerHTML = `<div class="page-head"><div><h1>Workforce Kanban</h1></div></div>${errorBlock(state.errors.resources, 'plant resources')}`;
      return;
    }
    const moves = [...state.moves.values()];
    const incomplete = moves.some(moveIncomplete);
    const blockedMoves = moves.map(move => ({ move, worker: state.workers.find(worker => worker.personId === move.personId) }))
      .filter(item => item.worker && moveQualificationGuard(item.worker, item.move).blocked);
    const scheduleVersion = state.schedule?.versionId;
    const scheduleStatus = scheduleVersion ? `Approved production schedule #${scheduleVersion}` : 'No approved production schedule';
    const error = state.errors.workers;

    page.innerHTML = `
      <div class="page-head">
        <div><h1>Workforce Kanban</h1><div class="sub">Plan staffing before a shift starts, then track who is confirmed on site.</div></div>
        <div class="spacer"></div><div class="head-controls workforce-kanban-head-controls">
          <label class="field workforce-shift-switcher"><span>Shift</span><select class="input" id="kanban-shift" aria-label="Show workforce by shift"><option value="">All shifts</option>${(state.resources.shifts ?? []).map(shift => `<option value="${esc(shift.code)}" ${shift.code === state.shift ? 'selected' : ''}>${esc(shift.name)}</option>`).join('')}</select></label>
          <a class="btn" href="#/supervisor">Plant Overview</a>
          <a class="btn" href="#/machine-kanban">Machine Readiness</a>
          <button class="btn" id="refresh-board" type="button">Refresh</button>
        </div>
      </div>
      ${error ? `<section class="panel">${errorBlock(error, 'one or more staffing data sources')}</section>` : ''}
      <div class="workforce-kanban-toolbar" aria-label="Board filters">
        <label class="field workforce-search-field"><span>Find an employee</span><input class="input" id="kanban-search" value="${esc(state.query)}" placeholder="Name, title or employee ID" autocomplete="off"></label>
        <label class="field"><span>Safety area</span><select class="input" id="kanban-area"><option value="">All areas</option>${(state.resources.safetyAreas ?? []).map(area => `<option value="${esc(area.code)}" ${area.code === state.department ? 'selected' : ''}>${esc(area.name)}</option>`).join('')}</select></label>
      </div>
      ${state.previewError ? `<section class="panel">${errorBlock(state.previewError, 'the safety scenario preview')}</section>` : ''}
      ${blockedMoves.length ? `<div class="notice bad workforce-qualification-blocks" role="alert"><b>Specialized placement blocked.</b>${blockedMoves.map(({ move, worker }) => `<div>${esc(moveQualificationGuard(worker, move).message)}</div>`).join('')}</div>` : ''}
      ${state.staleSchedule ? '<div class="notice warn"><b>Schedule changed while this scenario was open.</b> Refresh before reviewing this proposal again.</div>' : ''}
      ${renderBoard()}
      ${renderPreview()}
      <details class="panel workforce-schedule-context"><summary><b>Production schedule context</b><span class="muted">${esc(scheduleStatus)} · order operations only</span></summary>
        <div class="workforce-schedule-body"><header><span class="spacer"></span><a href="#/scheduling">Open Scheduling</a></header>
          <p class="muted">This is production context, not an employee roster.</p>
          ${scheduleVersion ? renderProductionContext() : emptyBlock('No approved production schedule', state.errors.schedule ? 'Schedule context is unavailable.' : 'An approved schedule will appear here when a planner approves one.')}
        </div>
      </details>
      <section class="panel workforce-scenario-controls" aria-label="Scenario controls">
        <div class="workforce-scenario-label"><b>Proposed moves · ${moves.length}</b><span class="muted small">Live assignments stay unchanged.</span></div>
        <div class="toolbar">
          <button class="btn" id="save-draft" type="button" ${moves.length ? '' : 'disabled'}>Save draft</button>
          <button class="btn primary" id="analyze-draft" type="button" ${!moves.length || incomplete || blockedMoves.length || state.previewBusy ? 'disabled' : ''}>${state.previewBusy ? 'Checking safety…' : 'Analyze impact'}</button>
          <button class="btn" id="reset-draft" type="button" ${moves.length ? '' : 'disabled'}>Reset</button>
        </div>
      </section>`;

    $('#refresh-board', page).addEventListener('click', () => { state.preview = null; state.previewError = null; void load(); });
    $('#kanban-area', page)?.addEventListener('change', event => { state.department = event.target.value; renderBoardOnly(); });
    $('#kanban-shift', page)?.addEventListener('change', event => { state.shift = event.target.value; renderBoardOnly(); });
    $('#kanban-search', page).addEventListener('input', event => { state.query = event.target.value; renderBoardOnly(); });
    $('#save-draft', page).addEventListener('click', saveDraft);
    $('#reset-draft', page).addEventListener('click', resetDraft);
    $('#analyze-draft', page).addEventListener('click', () => { void analyze(); });
    wireBoard();
  }

  function renderBoardOnly() {
    const board = $('.workforce-board-panel', page);
    if (!board) return;
    board.outerHTML = renderBoard();
    wireBoard();
  }

  function visibleWorkers() {
    const query = state.query.trim().toLocaleLowerCase();
    return state.workers.filter(worker => {
      const area = centerByCode(destination(worker))?.safetyAreaCode ?? worker.assignment?.safetyAreaCode ?? state.resources?.workCenters?.find(center => center.code === worker.homeWorkCenterCode)?.safetyAreaCode;
      if (state.department && area !== state.department) return false;
      if (state.shift && worker.shiftCode !== state.shift) return false;
      if (query && !`${worker.displayName} ${worker.jobTitle ?? ''} ${worker.personId}`.toLocaleLowerCase().includes(query)) return false;
      return true;
    });
  }

  function renderBoard() {
    const canEditLayout = canAction(ctx.me, 'resources.floor-map.manage');
    const dirty = layoutHasChanges();
    const boardMode = state.viewMode === 'board';
    return `<section class="panel workforce-board-panel"><header class="workforce-board-header"><div><h2>${boardMode ? 'Plant floor' : 'Plant map'}</h2><div class="muted small">${boardMode ? 'On-site staff and pre-shift placements. Planned workers are not shown as present.' : 'Work centers and on-site employees positioned over the site floor plan.'}</div></div><span class="spacer"></span>
      <div class="workforce-board-view-toggle" role="group" aria-label="Plant view"><button class="btn small ${boardMode ? 'primary' : ''}" type="button" data-board-view="board" aria-pressed="${boardMode}" ${state.layoutEdit ? 'disabled' : ''}>4 × 4 staffing</button><button class="btn small ${!boardMode ? 'primary' : ''}" type="button" data-board-view="map" aria-pressed="${!boardMode}">Plant map</button></div>
      ${canEditLayout && !boardMode ? state.layoutEdit ? `<button class="btn small primary" id="save-floor-layout" type="button" ${dirty ? '' : 'disabled'}>Save positions</button><button class="btn small" id="cancel-floor-layout" type="button">Cancel</button>` : '<button class="btn small" id="edit-floor-layout" type="button">Arrange work centers</button>' : ''}
      ${boardMode ? '<span class="pill info">Proposal only</span>' : ''}</header>
      ${state.layoutEdit ? '<div class="notice info small">Drag a work-center marker to its new location on the map, or use its arrow controls. Save records the full layout with an audit reason.</div>' : ''}
      ${state.errors.workers ? errorBlock(state.errors.workers, 'current workforce') : `<div class="muted small workforce-lanes-status" role="status">${visibleWorkers().filter(confirmedOnSite).length} on site · ${visibleWorkers().filter(worker => worker.isActive && !confirmedOnSite(worker) && !worker.unavailability).length} not clocked in · ${centers().filter(center => !state.department || center.safetyAreaCode === state.department).length} work centers</div>${boardMode ? renderStaffingBoard() : renderFloorMap()}${state.workers.length ? '' : emptyBlock('No registered workers', 'Work-center locations are shown above. Register workers to see names on the floor.')}`}
    </section>`;
  }

  function renderStaffingBoard() {
    const shown = visibleWorkers();
    const positions = layoutForCenters();
    const boardCenters = centers().filter(center => !state.department || center.safetyAreaCode === state.department).slice().sort((a, b) => {
      const areaA = state.resources.safetyAreas?.find(area => area.code === a.safetyAreaCode)?.name ?? '';
      const areaB = state.resources.safetyAreas?.find(area => area.code === b.safetyAreaCode)?.name ?? '';
      if (areaA !== areaB) return areaA.localeCompare(areaB);
      const pa = positions.get(a.code) ?? { layoutY: 0, layoutX: 0 };
      const pb = positions.get(b.code) ?? { layoutY: 0, layoutX: 0 };
      return pa.layoutY - pb.layoutY || pa.layoutX - pb.layoutX || a.name.localeCompare(b.name);
    });
    const centerLanes = boardCenters.map(center => renderStaffingLane(center, shown)).join('');
    return `<div class="workforce-compact-board">
      <section class="workforce-floor-zone workforce-compact-zone workforce-centers-zone"><header class="workforce-compact-zone-head"><span>Work centers</span><span class="muted small">${boardCenters.length} total</span></header>${boardCenters.length ? `<div class="workforce-kanban-lanes" aria-label="Plant work centers">${centerLanes}</div>` : emptyBlock('No work centers in this area', 'Set up work centers under Plant Setup.')}</section>
      ${renderPlanningRail(shown)}
    </div>`;
  }

  function renderPlanningRail(shown) {
    const unassigned = shown.filter(worker => available(worker) && (!destination(worker) || !centerByCode(destination(worker))));
    const plannedToCenter = worker => { const move = state.moves.get(worker.personId); return !!move && !move.unassign && !!centerByCode(move.workCenterCode); };
    const preShift = shown.filter(worker => worker.isActive && !confirmedOnSite(worker) && !worker.unavailability && !plannedToCenter(worker));
    const byId = new Map(shown.map(worker => [worker.personId, worker]));
    const absences = state.unavailability.filter(window => byId.has(window.personId))
      .slice().sort((a, b) => new Date(a.startsAt) - new Date(b.startsAt));
    const now = Date.now();
    const hasCurrentWindow = new Set(absences.filter(window => new Date(window.startsAt).getTime() <= now && new Date(window.endsAt).getTime() > now).map(window => window.personId));
    const currentAway = shown.filter(worker => worker.unavailability && !hasCurrentWindow.has(worker.personId));
    const absenceCount = absences.length + currentAway.length;
    return `<aside class="workforce-planning-rail" aria-label="Workforce planning roster">
      <section class="workforce-rail-section workforce-available-section" data-drop-lane="__available">
        <header class="workforce-rail-heading"><span class="workforce-rail-status status-ready"></span><div><h3>Available to assign</h3><small>${unassigned.length} on site · no work center</small></div></header>
        <div class="workforce-rail-people">${unassigned.length ? unassigned.map(worker => workerChip(worker, null)).join('') : '<p class="workforce-rail-empty">No unassigned people on this shift.</p>'}</div>
        <p class="workforce-rail-hint">Drag a person to a work center, or select them to stage a move.</p>
      </section>
      <section class="workforce-rail-section workforce-preshift-section" data-drop-lane="__available">
        <header class="workforce-rail-heading"><span class="workforce-rail-status status-preshift"></span><div><h3>Pre-shift roster</h3><small>${preShift.length} active · not clocked in</small></div></header>
        <div class="workforce-rail-people">${preShift.length ? preShift.map(worker => workerChip(worker, null)).join('') : '<p class="workforce-rail-empty">No one waiting to be planned for this shift.</p>'}</div>
        <p class="workforce-rail-hint">Plan their work center now. They remain marked as not clocked in.</p>
      </section>
      <section class="workforce-rail-section workforce-absence-section">
        <header class="workforce-rail-heading"><span class="workforce-rail-status status-away"></span><div><h3>Callouts & upcoming time away</h3><small>${absenceCount} record${absenceCount === 1 ? '' : 's'} · next 30 days</small></div></header>
        ${state.errors.unavailability ? `<div class="workforce-rail-warning">${esc(state.errors.unavailability.message ?? 'Time-away information is unavailable.')}</div>` : ''}
        <div class="workforce-absence-list">${[...currentAway.map(worker => ({ personId: worker.personId, kind: worker.unavailability, startsAt: new Date().toISOString(), endsAt: new Date().toISOString(), current: true })), ...absences].map(window => absenceRow(window, byId.get(window.personId))).join('') || '<p class="workforce-rail-empty">No callouts or scheduled time away.</p>'}</div>
      </section>
    </aside>`;
  }

  function absenceRow(window, worker) {
    if (!worker) return '';
    const labels = { ApprovedPto: 'PTO / vacation', CallOut: 'Callout', Unavailable: 'Unavailable', Training: 'Training' };
    const start = new Date(window.startsAt), end = new Date(window.endsAt);
    const date = value => value.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    const isCurrent = window.current || (start.getTime() <= Date.now() && end.getTime() > Date.now());
    const dateText = isCurrent ? 'Current' : `${date(start)}${date(start) === date(end) ? '' : ` – ${date(end)}`}`;
    return `<article class="workforce-absence-row"><span class="workforce-avatar-wrap">${workerAvatar(worker)}</span><span class="workforce-absence-person"><b>${esc(worker.displayName)}</b><small>${esc(worker.jobTitle || 'Title not set')}</small></span><span class="workforce-absence-detail"><b class="absence-kind absence-${esc(window.kind)}">${esc(labels[window.kind] ?? window.kind)}</b><small>${esc(dateText)}</small></span></article>`;
  }

  function renderStaffingLane(center, shown) {
    const onSite = shown.filter(worker => confirmedOnSite(worker) && laneFor(worker) === center.code);
    const planned = shown.filter(worker => !confirmedOnSite(worker) && state.moves.has(worker.personId) && !state.moves.get(worker.personId).unassign && laneFor(worker) === center.code);
    const people = [...onSite, ...planned];
    const areaName = state.resources.safetyAreas?.find(area => area.code === center.safetyAreaCode)?.name ?? 'Area not set';
    const expanded = state.expandedLanes.has(center.code);
    const displayed = expanded ? people : people.slice(0, 3);
    return `<article class="workforce-kanban-lane" data-drop-lane="${esc(center.code)}">
      <header class="workforce-kanban-lane-head"><img src="${esc(stagePhoto(center.code, center.name, center.picture))}" alt="" loading="lazy"><div class="workforce-kanban-lane-name"><small class="workforce-lane-area">${esc(areaName)}</small><h3>${esc(center.name)}</h3><span class="mono faint">${esc(center.code)}</span></div><span class="pill info workforce-staff-count" aria-label="${onSite.length} on site">${onSite.length} here</span>${planned.length ? `<span class="pill warn workforce-planned-count" aria-label="${planned.length} planned, not clocked in">${planned.length} planned</span>` : ''}</header>
      <div class="workforce-lane-cards">${people.length ? displayed.map(worker => workerChip(worker, center)).join('') : '<div class="workforce-empty-lane">No one assigned</div>'}${people.length > 3 ? `<button class="workforce-more-people" type="button" data-expand-lane="${esc(center.code)}">${expanded ? 'Show fewer' : `+${people.length - 3} more`}</button>` : ''}</div>
    </article>`;
  }

  function workerChip(worker, center) {
    if (state.expandedPersonId === worker.personId) return cardHtml(worker, center);
    const move = state.moves.get(worker.personId);
    const proposed = !!move;
    const qualificationGuard = moveQualificationGuard(worker, move);
    const location = centerByCode(destination(worker))?.name;
    const workerStatus = qualificationGuard.blocked ? 'Placement blocked' : qualificationGuard.supervised ? 'Trainer required throughout' : confirmedOnSite(worker) ? 'On site' : proposed ? 'Planned · not clocked in' : `Not clocked in · ${location ?? 'unassigned'}`;
    return `<article class="staff-card workforce-person-chip ${proposed ? 'is-proposed' : ''} ${confirmedOnSite(worker) ? '' : 'is-preshift'}" draggable="true" data-worker-card="${esc(worker.personId)}">
      <button class="staff-person-toggle" type="button" data-toggle-person="${esc(worker.personId)}" aria-expanded="false">${workerAvatar(worker)}<span><b>${esc(worker.displayName)}</b><small class="staff-job-title">${esc(worker.jobTitle || 'Title not set')}</small></span></button>
      <span class="staff-presence-tag ${qualificationGuard.blocked || qualificationGuard.supervised ? 'is-not-present' : confirmedOnSite(worker) ? '' : 'is-not-present'}"${qualificationGuard.message ? ` title="${esc(qualificationGuard.message)}"` : ''}>${esc(workerStatus)}</span>${proposed ? `<span class="pill ${qualificationGuard.blocked ? 'bad' : 'warn'}">${qualificationGuard.blocked ? 'Blocked' : 'Proposed'}</span>` : ''}</article>`;
  }

  function workerAvatar(worker) {
    const source = state.visualUrls.get(`worker:${worker.personId}`);
    const initials = worker.displayName.trim().split(/\s+/).slice(0, 2).map(part => part[0] ?? '').join('').toUpperCase();
    return source ? `<img class="workforce-avatar" src="${esc(source)}" alt="Photo of ${esc(worker.displayName)}" loading="lazy">` : `<span class="workforce-avatar workforce-avatar-initials" aria-hidden="true">${esc(initials || '?')}</span>`;
  }

  function layoutForCenters() {
    const output = new Map();
    const byArea = new Map();
    for (const center of centers().slice().sort((a, b) => a.code.localeCompare(b.code))) {
      if (!byArea.has(center.safetyAreaCode)) byArea.set(center.safetyAreaCode, []);
      byArea.get(center.safetyAreaCode).push(center);
    }
    for (const areaCenters of byArea.values()) {
      const occupied = new Set();
      for (const center of areaCenters) {
        if (Number.isInteger(center.layoutX) && Number.isInteger(center.layoutY)) {
          const position = `${center.layoutX}:${center.layoutY}`;
          if (occupied.has(position)) continue;
          output.set(center.code, { layoutX: center.layoutX, layoutY: center.layoutY });
          occupied.add(position);
        }
      }
      for (const center of areaCenters) {
        if (output.has(center.code)) continue;
        let index = 0, position;
        do {
          position = { layoutX: index % 4, layoutY: Math.floor(index / 4) };
          index++;
        } while (occupied.has(`${position.layoutX}:${position.layoutY}`));
        output.set(center.code, position);
        occupied.add(`${position.layoutX}:${position.layoutY}`);
      }
    }
    return output;
  }

  function layoutHasChanges() {
    if (!state.layoutEdit) return false;
    for (const [code, position] of state.layoutDraft) {
      const baseline = state.layoutBaseline.get(code);
      if (!baseline || baseline.mapX !== position.mapX || baseline.mapY !== position.mapY) return true;
    }
    return false;
  }

  function mapPositionsForCenters() {
    const result = new Map();
    const bySite = new Map();
    for (const center of centers().slice().sort((a, b) => a.code.localeCompare(b.code))) {
      const area = state.resources.safetyAreas?.find(item => item.code === center.safetyAreaCode);
      const siteCode = area?.siteCode ?? 'UNKNOWN';
      if (!bySite.has(siteCode)) bySite.set(siteCode, []);
      bySite.get(siteCode).push(center);
    }
    const defaults = center => {
      const code = String(center.code ?? '').toUpperCase();
      const name = `${center.code} ${center.name}`.toLowerCase();
      const whiskerWorks = {
        'WC-RCV': { mapX: 110, mapY: 180 }, 'WC-RAW': { mapX: 280, mapY: 180 },
        'WC-CNC-MILL': { mapX: 480, mapY: 180 }, 'WC-CNC-LATHE': { mapX: 650, mapY: 180 },
        'WC-FAB': { mapX: 830, mapY: 180 }, 'WC-ELE-PCB': { mapX: 110, mapY: 480 },
        'WC-ELE-HARN': { mapX: 280, mapY: 480 }, 'WC-SFT': { mapX: 460, mapY: 480 },
        'WC-KIT': { mapX: 640, mapY: 480 }, 'WC-ASM-STD': { mapX: 810, mapY: 480 },
        'WC-ASM-HVY': { mapX: 915, mapY: 480 }, 'WC-QAL': { mapX: 110, mapY: 780 },
        'WC-TST': { mapX: 290, mapY: 780 }, 'WC-PKG': { mapX: 470, mapY: 780 },
        'WC-SHP': { mapX: 640, mapY: 780 }, 'WC-MNT': { mapX: 800, mapY: 780 },
        'WC-FAC': { mapX: 915, mapY: 780 }, 'WC-OFF': { mapX: 500, mapY: 910 },
      };
      if (whiskerWorks[code]) return whiskerWorks[code];
      if (/receiv|raw/.test(name)) return { mapX: 100, mapY: 120 };
      if (/slit/.test(name)) return { mapX: 350, mapY: 230 };
      if (/wind|paint/.test(name)) return { mapX: 700, mapY: 180 };
      if (/warehouse|kit|stag/.test(name)) return { mapX: 120, mapY: 510 };
      if (/assembl|fabric|fab/.test(name)) return { mapX: 350, mapY: 570 };
      if (/inspect|quality|qc/.test(name)) return { mapX: 560, mapY: 570 };
      if (/maint/.test(name)) return { mapX: 740, mapY: 570 };
      if (/tool/.test(name)) return { mapX: 875, mapY: 570 };
      if (/ship/.test(name)) return { mapX: 100, mapY: 850 };
      if (/office/.test(name)) return { mapX: 600, mapY: 850 };
      return null;
    };
    for (const siteCenters of bySite.values()) {
      const occupied = new Set();
      for (const center of siteCenters) {
        if (Number.isInteger(center.mapX) && Number.isInteger(center.mapY)) {
          const key = `${center.mapX}:${center.mapY}`;
          if (!occupied.has(key)) { result.set(center.code, { mapX: center.mapX, mapY: center.mapY }); occupied.add(key); }
        }
      }
      siteCenters.forEach((center, index) => {
        if (result.has(center.code)) return;
        let position = defaults(center);
        if (position && occupied.has(`${position.mapX}:${position.mapY}`)) position = null;
        let slot = index;
        while (!position) {
          position = { mapX: 125 + (slot % 4) * 250, mapY: 160 + Math.floor(slot / 4) * 220 };
          slot++;
          if (occupied.has(`${position.mapX}:${position.mapY}`)) position = null;
        }
        result.set(center.code, position); occupied.add(`${position.mapX}:${position.mapY}`);
      });
    }
    return result;
  }

  function mapLayerEvidence(card, layer) {
    const unavailable = message => ({ state: 'unknown', items: [message] });
    if (!card) return unavailable('Machine source is unavailable.');
    if (layer === 'orders') {
      const operations = (card.operationsForMachine ?? []).slice(0, 3);
      if (operations.length) return { state: 'reported', items: operations.map(operation =>
        `Order #${operation.orderNumber}/Op ${operation.sequence} · ${operation.partNumber ?? 'part not returned'} · ${operation.executable === true ? 'readiness passes' : operation.executable === false ? operation.why ?? operation.primaryConstraint ?? 'readiness blocked' : 'readiness unknown'}`) };
      if (card.nextScheduled) return { state: 'reported', items: [`Next scheduled order #${card.nextScheduled.orderNumber}/Op ${card.nextScheduled.sequence}`] };
      if (state.machineEvidenceErrors.operations || state.machineEvidenceErrors.activeWork) return unavailable('Order readiness read is unavailable.');
      return { state: 'clear', items: ['No open machine operation was returned.'] };
    }
    if (layer === 'maintenance') {
      if (card.maintenanceHidden) return { state: 'restricted', items: ['Maintenance details are restricted for this role.'] };
      if (state.machineEvidenceErrors.downtime || state.machineEvidenceErrors.assets || state.machineEvidenceErrors.workOrders) return unavailable('Maintenance source read is unavailable.');
      const items = [
        ...(card.downNow ?? []).map(item => `Down · ${item.reason || item.kind || 'reason not recorded'}`),
        ...(card.overdueMaintenance ?? []).map(item => `Overdue ${item.kind ?? 'maintenance'} · ${item.workOrderNumber ?? item.workOrderId ?? 'work order'}`),
        ...(card.futureWindow ? [`Planned downtime · ${fmtDateTime(card.futureWindow.startsAt)}`] : []),
        ...(!card.downNow?.length && !card.overdueMaintenance?.length && !card.futureWindow && card.nextMaintenance
          ? [`Next ${card.nextMaintenance.kind ?? 'PM'} · due ${card.nextMaintenance.dueDate ?? 'date not returned'}`] : []),
      ];
      return items.length ? { state: 'reported', items } : { state: 'clear', items: ['No active downtime or open maintenance item was returned.'] };
    }
    if (layer === 'bottlenecks') {
      if (['blocked', 'attention'].includes(card.status)) return { state: 'issue', items: [card.summary, ...(card.blockers ?? []).slice(0, 2).map(item => `${item.source}: ${item.text}`)] };
      if (card.status === 'unknown' || card.error) return unavailable('Readiness is unknown; no clear bottleneck result can be claimed.');
      return { state: 'clear', items: ['No blocked or attention signal was reported at the last read.'] };
    }

    const patterns = {
      materials: /material|inventory|stock|shortage|receipt|lot|part|supply|subassembly/i,
      quality: /quality|inspection|ncr|mrb|disposition|calibration|gauge/i,
      safety: /safety|safe-|qualification|authorization|authorized|trainer|two-person|2-person|competenc/i,
    };
    const evidence = [
      ...(card.blockers ?? []).map(item => ({ label: item.source, detail: item.text, outcome: 'Fail' })),
      ...(card.candidateBlockers ?? []).map(item => ({ label: item.constraint ?? 'Operation readiness', detail: item.why ?? item.primaryConstraint ?? 'Operation is not executable.', outcome: 'Fail' })),
      ...(card.currentEvaluation?.dimensions ?? []).map(item => ({ label: item.name ?? 'Readiness', detail: item.detail ?? item.outcome ?? 'No detail returned.', outcome: item.outcome })),
    ];
    const matching = evidence.filter(item => patterns[layer].test(`${item.label} ${item.detail}`));
    if (matching.length) {
      const unique = [...new Map(matching.map(item => [`${item.label}|${item.detail}`, item])).values()];
      const failing = unique.filter(item => ['Fail', 'Indeterminate'].includes(item.outcome));
      if (failing.some(item => item.outcome === 'Fail')) return { state: 'issue', items: failing.slice(0, 3).map(item => `${item.label}: ${item.detail}`) };
      if (failing.length || unique.some(item => !item.outcome || !['Pass', 'Fail', 'Indeterminate'].includes(item.outcome))) {
        return unavailable(unique.slice(0, 3).map(item => `${item.label}: ${item.detail}`).join(' · '));
      }
      if (unique.every(item => item.outcome === 'Pass')) return { state: 'clear', items: unique.slice(0, 3).map(item => `Pass · ${item.label}: ${item.detail}`) };
    }
    if (card.error || !card.currentEvaluation) return unavailable(`${layer[0].toUpperCase()}${layer.slice(1)} evidence is not confirmed by a current operation read.`);
    return { state: 'clear', items: [`No ${layer} blocker surfaced in the evaluated operation.`] };
  }

  function renderMapLayerBadges(machines) {
    const layers = [
      ['orders', 'ORD'], ['materials', 'MAT'], ['quality', 'QLT'], ['safety', 'SAFE'], ['maintenance', 'MNT'], ['bottlenecks', 'BOT'],
    ].filter(([layer]) => state.mapLayers[layer]);
    return layers.map(([layer, label]) => {
      const evidence = machines.map(machine => mapLayerEvidence(state.machineCards.find(card => card.machine.code === machine.code), layer));
      if (evidence.some(item => item.state === 'restricted')) return '';
      const unknown = evidence.some(item => item.state === 'unknown');
      const count = evidence.reduce((total, item) => total + (['issue', 'reported'].includes(item.state) ? item.items.length : 0), 0);
      const value = unknown && !count ? '?' : String(count);
      const tone = unknown ? 'unknown' : count ? (layer === 'orders' ? 'info' : 'warn') : 'clear';
      return `<span class="plant-map-layer-badge ${tone}" title="${esc(`${label} layer · ${unknown ? 'some source facts unavailable' : `${count} reported item(s)`}`)}">${label} ${value}</span>`;
    }).join('');
  }

  function renderMapLayerPanels(machines) {
    const layers = [
      ['orders', 'Orders'], ['materials', 'Materials'], ['quality', 'Quality'], ['safety', 'Safety'], ['maintenance', 'Maintenance'], ['bottlenecks', 'Bottlenecks'],
    ].filter(([layer]) => state.mapLayers[layer]);
    return layers.map(([layer, label]) => {
      const items = machines.flatMap(machine => {
        const evidence = mapLayerEvidence(state.machineCards.find(card => card.machine.code === machine.code), layer);
        return evidence.items.map(detail => ({ machine, detail, state: evidence.state }));
      });
      return `<section class="plant-map-layer-panel" data-map-layer-panel="${layer}"><h5>${label}</h5>${items.length ? items.map(item => `<div class="plant-map-layer-item ${item.state}"><b>${esc(item.machine.code)}</b><span>${esc(item.detail)}</span></div>`).join('') : '<p class="muted small">No machine records are configured for this work center.</p>'}</section>`;
    }).join('');
  }

  function renderFloorMap() {
    const shown = visibleWorkers();
    const positions = state.layoutEdit ? state.layoutDraft : mapPositionsForCenters();
    const sites = state.resources.sites ?? [];
    const siteCards = sites.map(site => {
      const areaCodes = new Set((state.resources.safetyAreas ?? []).filter(area => area.siteCode === site.code && (!state.department || area.code === state.department)).map(area => area.code));
      const siteCenters = centers().filter(center => areaCodes.has(center.safetyAreaCode));
      if (!siteCenters.length) return '';
      const image = site.hasCustomMap ? (state.visualUrls.get(`site:${site.code}`) ?? '') : '/img/plants/sample-plant-floor.svg';
      const pins = siteCenters.map(center => {
        const position = positions.get(center.code) ?? { mapX: 500, mapY: 500 };
        const people = shown.filter(worker => confirmedOnSite(worker) && laneFor(worker) === center.code);
        const machines = (state.resources?.machines ?? []).filter(machine => machine.workCenterCode === center.code);
        const signals = machines.map(machine => state.machineCards.find(card => card.machine.code === machine.code)).filter(Boolean);
        const statusOrder = ['blocked', 'attention', 'ready', 'unknown'];
        const signal = statusOrder.find(status => signals.some(card => card.status === status)) ?? 'unknown';
        const signalLabel = { blocked: 'Blocked', attention: 'Attention', ready: 'Ready', unknown: 'Unknown' }[signal];
        const expanded = state.expandedMapCenter === center.code;
        const layerBadges = renderMapLayerBadges(machines);
        return `<article class="plant-map-pin ${expanded ? 'is-expanded' : ''} ${state.layoutEdit ? 'is-draggable' : ''}" data-map-pin="${esc(center.code)}" data-drop-lane="${esc(center.code)}" style="--map-x:${position.mapX / 10}%;--map-y:${position.mapY / 10}%" ${state.layoutEdit ? 'draggable="true"' : ''}>
          <button class="plant-map-pin-toggle" type="button" data-map-toggle="${esc(center.code)}" aria-expanded="${expanded}"><span class="plant-map-pin-heading"><b>${esc(center.name)}</b><small>${esc(center.code)}${state.mapLayers.workers ? ` · ${people.length} on site` : ''}</small></span>${state.mapLayers.readiness ? `<span class="plant-map-pin-status"><i class="machine-signal-dot ${signal}" aria-hidden="true">${signal === 'ready' ? '✓' : signal === 'attention' ? '!' : signal === 'blocked' ? '■' : '?'}</i><span>${signalLabel}</span></span>` : ''}${state.mapLayers.workers ? `<span class="plant-map-pin-avatars">${people.slice(0, 4).map(workerAvatar).join('')}${people.length > 4 ? `<i>+${people.length - 4}</i>` : ''}</span>` : ''}${layerBadges ? `<span class="plant-map-pin-layer-badges" aria-label="Selected map layer facts">${layerBadges}</span>` : ''}</button>
          ${expanded ? `<div class="plant-map-pin-people">${state.mapLayers.workers ? (people.length ? people.map(worker => workerChip(worker, center)).join('') : '<span class="muted small">No one currently here</span>') : ''}
            ${state.mapLayers.machines && machines.length ? `<div class="plant-map-machine-list" aria-label="Machines in ${esc(center.name)}">${machines.map(machine => { const card = state.machineCards.find(item => item.machine.code === machine.code); const status = card?.status ?? 'unknown'; const icon = status === 'ready' ? '✓' : status === 'attention' ? '!' : status === 'blocked' ? '■' : '?'; return `<button type="button" class="plant-map-machine" data-map-machine="${esc(machine.code)}"><i class="machine-signal-dot ${state.mapLayers.readiness ? status : 'unknown'}" aria-hidden="true">${state.mapLayers.readiness ? icon : '?'}</i><span><b>${esc(machine.code)}</b><small>${state.mapLayers.readiness ? esc(card?.summary ?? 'Readiness not verified') : 'Readiness hidden'}</small></span></button>`; }).join('')}</div>` : ''}
            ${renderMapLayerPanels(machines)}
            </div>` : ''}
          ${state.layoutEdit ? `<div class="plant-map-move-controls" aria-label="Move ${esc(center.name)} marker"><button type="button" data-map-step="${esc(center.code)}" data-dx="-1" aria-label="Move left">←</button><button type="button" data-map-step="${esc(center.code)}" data-dy="-1" aria-label="Move up">↑</button><button type="button" data-map-step="${esc(center.code)}" data-dy="1" aria-label="Move down">↓</button><button type="button" data-map-step="${esc(center.code)}" data-dx="1" aria-label="Move right">→</button></div>` : ''}
        </article>`;
      }).join('');
      return `<section class="workforce-floor-zone plant-map-site"><header class="workforce-floor-zone-head"><div><span class="faint">${site.hasCustomMap ? 'SITE FLOOR PLAN' : 'REFERENCE FLOOR PLAN'}</span><h3>${esc(site.name)}</h3></div><div class="plant-map-actions">${canAction(ctx.me, 'resources.floor-map.manage') ? `<label class="btn small plant-map-upload">${site.hasCustomMap ? 'Replace map' : 'Upload new map'}<input type="file" accept="image/png,image/jpeg,image/webp" data-map-upload="${esc(site.code)}" aria-label="Upload a new ${esc(site.name)} plant map"></label>${site.hasCustomMap ? `<button class="btn small" type="button" data-map-remove="${esc(site.code)}">Use reference map</button>` : ''}` : ''}<span class="pill info">${siteCenters.length} work centers</span></div></header>
        ${site.hasCustomMap ? '' : '<div class="muted small">Example layout · upload your site floor plan to align centers to your facility.</div>'}<div class="plant-map-viewport" aria-label="Scrollable plant floor map"><div class="plant-map-canvas" data-map-canvas="${esc(site.code)}" style="--map-zoom:${Math.round(state.mapZoom * 100)}%"><img class="plant-map-background" src="${esc(image || '/img/plants/sample-plant-floor.png')}" alt="${esc(site.hasCustomMap ? `${site.name} plant floor map` : 'Reference plant floor layout')}" draggable="false">${pins}</div></div></section>`;
    }).join('');
    const presentUnassigned = shown.filter(worker => confirmedOnSite(worker) && (!destination(worker) || !centerByCode(destination(worker))));
    const notPresent = shown.filter(worker => !confirmedOnSite(worker));
    const unassignedOpen = state.openFloorDetails.has('unassigned') || presentUnassigned.some(worker => worker.personId === state.expandedPersonId);
    const offsiteOpen = state.openFloorDetails.has('offsite') || notPresent.some(worker => worker.personId === state.expandedPersonId);
    const coverageMarkup = renderMapCoverageSearch();
    const layerLabel = { workers: 'Staffing', readiness: 'Readiness', machines: 'Machines', orders: 'Orders', materials: 'Materials', quality: 'Quality', safety: 'Safety', maintenance: 'Maintenance', bottlenecks: 'Bottlenecks' };
    return `<div class="workforce-floor-map"><section class="plant-map-toolbar panel" aria-label="Plant map controls"><div class="plant-map-zoom-controls" role="group" aria-label="Map zoom"><button class="btn small" type="button" data-map-zoom="out" aria-label="Zoom map out" ${state.mapZoom <= .75 ? 'disabled' : ''}>−</button><span data-map-zoom-label>${Math.round(state.mapZoom * 100)}%</span><button class="btn small" type="button" data-map-zoom="in" aria-label="Zoom map in" ${state.mapZoom >= 1.75 ? 'disabled' : ''}>+</button><button class="btn small" type="button" data-map-zoom="fit" ${state.mapZoom === 1 ? 'disabled' : ''}>Fit plant</button><button class="btn small" type="button" data-map-zoom="reset-view">Reset view</button></div><div class="plant-map-layer-controls" role="group" aria-label="Map layers">${Object.entries(layerLabel).map(([key, label]) => `<button type="button" class="btn small ${state.mapLayers[key] ? 'primary' : ''}" data-map-layer="${key}" aria-pressed="${state.mapLayers[key]}">${label}</button>`).join('')}</div></section>${coverageMarkup}${siteCards || emptyBlock('No work centers in this area', 'Set up work centers under Plant Setup.')}
      ${presentUnassigned.length ? `<details class="workforce-unassigned-zone" data-floor-details="unassigned" ${unassignedOpen ? 'open' : ''}><summary>On site with no assigned work center · ${presentUnassigned.length}</summary><div class="workforce-floor-people">${presentUnassigned.map(worker => workerChip(worker, null)).join('')}</div></details>` : ''}
      ${notPresent.length ? `<details class="workforce-offsite" data-floor-details="offsite" ${offsiteOpen ? 'open' : ''}><summary>Not currently confirmed on site · ${notPresent.length}</summary><div class="workforce-offsite-list">${notPresent.map(worker => `<div class="workforce-offsite-person">${cardHtml(worker, centerByCode(destination(worker))) }<span class="muted small">${esc(centerByCode(destination(worker))?.name ?? 'No work center recorded')}</span></div>`).join('')}</div></details>` : ''}
    </div>`;
  }

  function renderMapCoverageSearch() {
    const query = state.mapCoverageQuery.trim().toLowerCase();
    const machines = state.resources?.machines ?? [];
    const profiles = state.resources?.machineActivities ?? [];
    const rows = machines.map(machine => {
      const profile = profiles.find(item => item.machineCode === machine.code);
      const required = profile?.operatorQualifications ?? [];
      const center = centerByCode(machine.workCenterCode);
      const terms = [machine.code, machine.name, machine.workCenterCode, center?.name, profile?.activityCode, ...required].filter(Boolean).join(' ').toLowerCase();
      const qualified = state.qualificationCoverage === 'ready' && required.length
        ? visibleWorkers().filter(worker => available(worker) && confirmedOnSite(worker)
          && required.some(code => currentQualified(worker.personId, code))
          && currentMachineAuthorized(worker.personId, { machineCode: machine.code, activityCode: profile?.activityCode }))
        : null;
      return { machine, profile, required, terms, qualified };
    }).filter(row => row.required.length > 0 && (query && row.terms.includes(query)));
    return `<section class="plant-map-coverage panel" aria-label="Qualified machine coverage search"><form data-map-coverage-search><label for="map-coverage-query"><b>Qualified coverage search</b><span class="muted small">Search a machine, work center, activity or qualification to see current on-site coverage within the selected workforce filters.</span></label><div class="plant-map-search-row"><input class="input" id="map-coverage-query" name="query" type="search" value="${esc(state.mapCoverageQuery)}" placeholder="e.g. CNC-02, mill, QUAL-CNC-MILL"><button class="btn primary" type="submit">Search</button>${query ? '<button class="btn" type="button" data-map-coverage-clear>Clear</button>' : ''}</div></form>${state.qualificationCoverage !== 'ready' ? '<p class="muted small" role="status">Qualification coverage is unavailable. Search will not infer worker eligibility.</p>' : query ? rows.length ? `<div class="plant-map-coverage-results" role="list">${rows.map(row => `<button class="plant-map-coverage-result" type="button" role="listitem" data-map-machine="${esc(row.machine.code)}"><span><b>${esc(row.machine.code)} · ${esc(row.machine.name)}</b><small>${esc(row.machine.workCenterCode)}${row.profile?.activityCode ? ` · ${esc(row.profile.activityCode)}` : ''} · Requires ${esc(row.required.join(', '))}</small></span><span class="pill ${row.qualified?.length ? 'ok' : 'warn'}">${row.qualified?.length ?? 0} present · qualified</span></button>`).join('')}</div>` : '<p class="muted small" role="status">No configured machine qualification requirements matched that search.</p>' : '<p class="muted small" role="status">Enter a search to find machines with configured qualification requirements.</p>'}</section>`;
  }

  function openMachineReadiness(machineCode) {
    const card = state.machineCards.find(item => item.machine.code === machineCode);
    if (!card) return;
    const activityProfile = (state.resources?.machineActivities ?? []).find(profile => profile.machineCode === machineCode);
    const requiredQualifications = activityProfile?.operatorQualifications ?? [];
    const machineMove = { machineCode, activityCode: activityProfile?.activityCode };
    const isQualified = worker => requiredQualifications.some(code => state.qualifications.some(record => record.personId === worker.personId
      && record.code === code && record.status === 'Qualified' && (!record.expiresAt || Date.parse(record.expiresAt) > Date.now())))
      && currentMachineAuthorized(worker.personId, machineMove);
    const qualifiedPeople = state.qualificationCoverage === 'ready' && requiredQualifications.length
      ? state.workers.filter(worker => available(worker) && confirmedOnSite(worker) && isQualified(worker)) : [];
    const signal = card.status;
    const icon = signal === 'ready' ? '✓' : signal === 'attention' ? '!' : signal === 'blocked' ? '■' : '?';
    const evaluation = card.currentEvaluation;
    const blockers = [...(card.blockers ?? []), ...(card.candidateBlockers ?? []).map(item => ({ source: item.constraint ?? 'Operation readiness', text: item.why ?? item.primaryConstraint ?? 'This operation is not executable.' }))];
    const dimensions = evaluation?.dimensions ?? [];
    const maintenance = card.maintenanceHidden ? 'Maintenance details are hidden for this role.'
      : card.overdueMaintenance?.length ? `${card.overdueMaintenance.length} overdue maintenance item(s)`
        : card.nextMaintenance ? `Next ${card.nextMaintenance.kind} · due ${card.nextMaintenance.dueDate ?? 'date not available'}` : 'No open maintenance item was returned.';
    const detailDrawer = drawer(`Machine readiness · ${machineCode}`, `
      <div class="stack machine-readiness-drawer">
        <div class="machine-readiness-status"><i class="machine-signal-dot ${signal}" aria-hidden="true">${icon}</i><div><b>${esc(card.summary)}</b><small>${esc(card.workCenter?.name ?? card.machine.workCenterCode)} · Forge read ${esc(fmtDateTime(state.machineReadAt))}</small></div></div>
        <section class="panel"><h3>Current readiness</h3><p>${evaluation ? `Order ${esc(evaluation.orderNumber)}/${esc(evaluation.sequence)} · ${esc(evaluation.executable ? 'passes current readiness' : evaluation.why ?? evaluation.primaryConstraint ?? 'blocked')}` : 'No current executable operation was evaluated for this machine.'}</p>
          ${dimensions.length ? `<div class="machine-requirement-list"><b>Readiness checks</b>${dimensions.map(item => `<div><span class="pill ${item.outcome === 'Pass' ? 'ok' : ['Fail', 'Indeterminate'].includes(item.outcome) ? 'bad' : 'none'}">${esc(item.outcome ?? 'Unknown')}</span><span><b>${esc(item.name)}</b><small>${esc(item.detail ?? 'No additional detail returned.')}</small></span></div>`).join('')}</div>` : ''}
          ${blockers.length ? `<ul class="evidence">${blockers.slice(0, 8).map(item => `<li><b>${esc(item.source)}:</b> ${esc(item.text)}</li>`).join('')}</ul>` : '<div class="muted">No current blocking condition was returned by the readiness sources.</div>'}</section>
        <section class="panel"><h3>Required qualifications</h3>${requiredQualifications.length ? `<p>${requiredQualifications.map(code => `<span class="pill info">${esc(code)}</span>`).join(' ')}</p>` : '<p class="muted">No machine-specific operator qualifications are configured for this asset.</p>'}${activityProfile?.requiresMachineAuthorization ? '<p class="notice warn">This machine also requires a current practical authorization for this exact activity. Training or a general qualification alone does not satisfy the gate.</p>' : ''}${state.qualificationCoverage !== 'ready' && requiredQualifications.length ? '<p class="muted">Qualification coverage is unavailable; Forge will not suggest a worker.</p>' : ''}</section>
        <section class="panel"><h3>Maintenance &amp; staffing</h3><p>${esc(maintenance)}</p>${card.workforceHidden ? '<p class="muted">Operator assignments are hidden for this role.</p>' : card.workerNames?.length ? `<p>${card.workerNames.map(worker => esc(worker.name)).join(' · ')}</p>` : '<p class="muted">No machine-assigned operators were returned.</p>'}
          ${qualifiedPeople.length ? `<div class="machine-coverage-list"><b>Qualified people on site</b>${qualifiedPeople.map(worker => `<button type="button" class="btn small" data-machine-stage-person="${esc(worker.personId)}">Stage ${esc(worker.displayName)} to ${esc(machineCode)}</button>`).join('')}</div>` : requiredQualifications.length ? '<p class="muted">No currently confirmed, available worker with all required qualifications was found.</p>' : ''}</section>
        <div class="inline"><a class="btn primary" href="#/machine-kanban">Open Machine Readiness</a><button class="btn" type="button" data-map-plan-center="${esc(card.machine.workCenterCode)}">Review staffing plan</button>${state.moves.size ? '<button class="btn" type="button" data-map-analyze>Analyze staged moves</button>' : ''}</div>
        <p class="muted small">Status reflects separate Forge records at the read time above. Unknown means the required readiness source or a current eligible operation was not confirmed.</p>
      </div>`);
    $$('[data-map-plan-center]', detailDrawer.element).forEach(button => button.addEventListener('click', () => {
      const code = button.dataset.mapPlanCenter;
      state.viewMode = 'board';
      state.expandedLanes.add(code);
      renderBoardOnly();
      detailDrawer.close();
      $('.workforce-board-panel', page)?.scrollIntoView?.({ behavior: 'smooth', block: 'start' });
    }, { once: true }));
    $$('[data-machine-stage-person]', detailDrawer.element).forEach(button => button.addEventListener('click', () => {
      const worker = state.workers.find(person => person.personId === button.dataset.machineStagePerson);
      if (!worker || !activityProfile) return;
      detailDrawer.close();
      stage(worker, card.machine.workCenterCode, { activityCode: activityProfile.activityCode, machineCode });
    }));
    $('[data-map-analyze]', detailDrawer.element)?.addEventListener('click', () => {
      detailDrawer.close();
      state.viewMode = 'board';
      renderBoardOnly();
      $('#analyze-draft', page)?.click();
    });
  }

  function renderQuickManageBar(worker, move) {
    const canTitle = canAction(ctx.me, 'workforce.manage');
    const canPhoto = canAction(ctx.me, 'workforce.photos.manage');
    const canSupervise = canAction(ctx.me, 'workforce.supervise');
    const canTraining = ['competency.read', 'competency.manage', 'competency.certify'].some(permission => canAction(ctx.me, permission));
    const guard = move ? moveQualificationGuard(worker, move) : { blocked: false };
    const canApplyMove = canSupervise && move && !moveIncomplete(move) && !guard.blocked;

    return `<div class="staff-card-manage" role="group" aria-label="Manage ${esc(worker.displayName)}">
      <div class="staff-card-manage-head">Manage employee</div>
      <div class="staff-card-manage-actions">
        ${canTraining ? `<button class="btn small" type="button" data-emp-training="${esc(worker.personId)}">Training &amp; quals</button>` : ''}
        ${canTitle ? `<button class="btn small" type="button" data-emp-title="${esc(worker.personId)}">Edit title</button>` : ''}
        ${canTitle ? `<button class="btn small" type="button" data-emp-department="${esc(worker.personId)}">Edit department</button>` : ''}
        ${canPhoto ? `<label class="btn small staff-photo-upload-btn">${worker.hasPhoto ? 'Replace photo' : 'Upload photo'}<input type="file" accept="image/png,image/jpeg,image/webp" data-emp-photo-upload="${esc(worker.personId)}" aria-label="Upload photo for ${esc(worker.displayName)}"></label>` : ''}
        ${canPhoto && worker.hasPhoto ? `<button class="btn small ghost" type="button" data-emp-photo-remove="${esc(worker.personId)}">Remove photo</button>` : ''}
        ${canSupervise && !confirmedOnSite(worker) ? `<button class="btn small" type="button" data-emp-presence="${esc(worker.personId)}">Confirm on site</button>` : ''}
        ${canSupervise ? `<button class="btn small" type="button" data-emp-unavailable="${esc(worker.personId)}">Record callout / PTO</button>` : ''}
        ${canApplyMove ? `<button class="btn small primary" type="button" data-emp-apply-move="${esc(worker.personId)}">Apply live assignment</button>` : ''}
        <a class="btn small ghost" href="#/workforce?personId=${encodeURIComponent(worker.personId)}">Open full profile →</a>
      </div>
    </div>`;
  }

  function cardHtml(worker, lane) {
    const move = state.moves.get(worker.personId);
    const qualificationGuard = moveQualificationGuard(worker, move);
    const targetCenter = move && !move.unassign ? centerByCode(move.workCenterCode) : null;
    const destinationOptions = [
      '<option value="__available">Unassigned / Available</option>',
      ...centers().map(center => `<option value="${esc(center.code)}">${esc(center.name)}</option>`),
    ].join('');
    const expanded = state.expandedPersonId === worker.personId;
    const machineCode = move && !move.unassign ? move.machineCode : worker.assignment?.machineCode;
    const assignedMachine = machineCode ? state.resources?.machines?.find(machine => machine.code === machineCode) : null;
    const currentWorkCenter = centerByCode(currentCenter(worker));
    const cardStatus = confirmedOnSite(worker) ? 'On site' : move ? 'Planned · not clocked in' : worker.presenceKind === 'Projected' ? 'Expected · not clocked in' : 'Not clocked in';
    const controls = move
      ? move.unassign ? `<div class="staff-card-editor"><div class="muted small">Proposed to be unassigned.</div><button class="btn small" type="button" data-remove-move="${esc(worker.personId)}">Remove proposal</button></div>` : assignmentEditor(move, targetCenter)
      : `<div class="staff-card-move"><label for="dest-${esc(worker.personId)}">Proposed destination</label><div class="staff-card-move-row">
        <select class="input" id="dest-${esc(worker.personId)}" data-destination="${esc(worker.personId)}">${destinationOptions.replace(`value="${esc(destination(worker) ?? '__available')}"`, `value="${esc(destination(worker) ?? '__available')}" selected`)}</select>
        <button class="btn small" type="button" data-stage-worker="${esc(worker.personId)}">Stage move</button>
      </div></div>`;
    return `<article class="staff-card ${move ? 'is-proposed' : ''} ${confirmedOnSite(worker) ? '' : 'is-preshift'}" draggable="true" data-worker-card="${esc(worker.personId)}" aria-label="${esc(worker.displayName)}${move ? ', proposed move' : ''}">
      <div class="staff-card-head"><button class="staff-person-toggle" type="button" data-toggle-person="${esc(worker.personId)}" aria-expanded="${expanded}">${workerAvatar(worker)}<span>${esc(worker.displayName)}<small class="staff-job-title">${esc(worker.jobTitle || 'Official title not set')}${worker.department ? ` · ${esc(worker.department)}` : ''}</small></span></button>${move ? '<span class="pill warn">Proposed</span>' : ''}</div>
      <div class="staff-card-meta"><span class="staff-presence-tag ${confirmedOnSite(worker) ? '' : 'is-not-present'}">${esc(cardStatus)}</span>${worker.shiftCode ? `<span class="staff-shift-tag">${esc(worker.shiftCode)}</span>` : ''}${move ? `<span class="staff-origin-tag">From ${esc(currentWorkCenter?.name ?? 'unassigned')}</span>` : ''}</div>
      ${machineCode ? `<div class="staff-location">${esc(assignedMachine?.name ?? 'Machine')} · ${esc(machineCode)}</div>` : ''}
      ${qualificationGuard.message ? `<div class="notice ${qualificationGuard.blocked ? 'bad' : 'info'} small workforce-qualification-guard" role="${qualificationGuard.blocked ? 'alert' : 'status'}">${esc(qualificationGuard.message)}</div>` : ''}
      ${expanded ? `<div class="staff-card-proposal-controls">${controls}<div class="staff-card-scenario-row"><a class="btn small" href="#/scenarios?kind=PersonUnavailable&amp;personId=${encodeURIComponent(worker.personId)}&amp;scenarioName=${encodeURIComponent(`Model absence · ${worker.displayName}`)}">Model absence scenario</a></div>${renderQuickManageBar(worker, move)}</div>` : ''}
    </article>`;
  }

  function editWorkerTitle(worker) {
    formModal({
      title: `Set ${worker.displayName}'s official title`,
      detail: 'Displayed beneath their name across Workforce Kanban and the plant floor map. Leave blank to clear.',
      confirmLabel: 'Save title', reasonRequired: true,
      fields: [{ name: 'jobTitle', label: 'Official title', value: worker.jobTitle ?? '', placeholder: 'Production Operator', maxLength: 100 }],
    }, (values, reason) => command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/job-title`, { jobTitle: values.jobTitle || null }, reason), () => load());
  }

  function editWorkerDepartment(worker) {
    formModal({
      title: `Set ${worker.displayName}'s department`,
      detail: 'Departments support Workforce filters and employee context. Leave blank to mark the department unassigned.',
      confirmLabel: 'Save department', reasonRequired: true,
      fields: [{ name: 'department', label: 'Department', value: worker.department ?? '', placeholder: 'Production', maxLength: 100 }],
    }, (values, reason) => command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/department`, { department: values.department || null }, reason), () => load());
  }

  function confirmWorkerPresence(worker) {
    formModal({
      title: `Record ${worker.displayName}'s presence`,
      detail: 'Only clocked-in or supervisor-confirmed presence counts as on site. Use pre-shift planning for people who have not arrived.',
      confirmLabel: 'Record presence', reasonRequired: true,
      fields: [
        { name: 'kind', label: 'Basis', type: 'select', value: 'SupervisorConfirmed', options: [['SupervisorConfirmed', 'Supervisor confirmed on site'], ['ClockedIn', 'Clocked in']] },
        { name: 'validUntil', label: 'Valid until (optional)', type: 'datetime-local' },
      ],
    }, (values, reason) => command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/presence`, {
      kind: values.kind,
      validUntil: values.validUntil ? new Date(values.validUntil).toISOString() : null,
    }, reason), () => load());
  }

  function recordWorkerUnavailable(worker) {
    const now = new Date();
    const end = new Date(now.getTime() + 8 * 3600 * 1000);
    const toLocalInput = date => new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
    formModal({
      title: `Record ${worker.displayName} unavailable`,
      detail: 'Records a call-out, PTO, training window, or unavailability for workforce planning.',
      confirmLabel: 'Record unavailability', reasonRequired: true,
      fields: [
        { name: 'kind', label: 'Kind', type: 'select', value: 'CallOut', options: [['CallOut', 'Call-out'], ['Unavailable', 'Unavailable'], ['ApprovedPto', 'Approved time off'], ['Training', 'Training']] },
        { name: 'startsAt', label: 'From', type: 'datetime-local', value: toLocalInput(now), required: true },
        { name: 'endsAt', label: 'To', type: 'datetime-local', value: toLocalInput(end), required: true },
      ],
    }, (values, reason) => {
      const startsAt = values.startsAt ? new Date(values.startsAt).toISOString() : null;
      const endsAt = values.endsAt ? new Date(values.endsAt).toISOString() : null;
      if (!startsAt || !endsAt || endsAt <= startsAt) throw new Error('Enter a start and a later end.');
      if (new Date(endsAt).getTime() - new Date(startsAt).getTime() > 60 * 864e5) throw new Error('Unavailability must be within 60 days.');
      return command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/unavailability`, { kind: values.kind, startsAt, endsAt }, reason);
    }, () => load());
  }

  async function applyLiveMove(worker) {
    const proposed = state.moves.get(worker.personId);
    if (!proposed || moveIncomplete(proposed)) return;
    const unassign = proposed.unassign === true;
    const target = [proposed.activityCode, proposed.machineCode, proposed.workCenterCode].filter(Boolean).join(' · ');
    const ok = await confirmWithReason({
      title: unassign ? `End assignment for ${worker.displayName}` : `Assign ${worker.displayName} to ${proposed.workCenterCode}`,
      detail: unassign ? `${worker.displayName} will be unassigned from ${worker.assignment?.workCenterCode ?? 'their current work'}.` : `Commits live assignment: ${target}. Specialized assignments are checked again before saving.`,
      confirmLabel: unassign ? 'End live assignment' : 'Apply live assignment', reasonRequired: true,
    }, async reason => {
      if (unassign) return command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/assignment/end`, {}, reason);

      // Refresh presence, assignment and qualification evidence after the reason dialog and immediately before committing.
      await load();
      if (!state.resources || state.errors.resources) throw new Error('Plant setup could not be read, so the live assignment was not applied.');
      const currentWorker = state.workers.find(item => item.personId === worker.personId);
      const currentMove = state.moves.get(worker.personId);
      if (!currentWorker || !currentMove || moveIncomplete(currentMove)) throw new Error('The proposed assignment changed or is incomplete. Review it again before applying.');
      const guard = moveQualificationGuard(currentWorker, currentMove);
      if (guard.blocked) throw new Error(guard.message);
      return command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/assignment`, {
        safetyAreaCode: currentMove.safetyAreaCode || null,
        workCenterCode: currentMove.workCenterCode || null,
        machineCode: currentMove.machineCode || null,
        activityCode: currentMove.activityCode,
        asTrainee: guard.supervised === true,
      }, reason);
    });
    if (ok) { state.moves.delete(worker.personId); await load(); }
  }

  async function openEmployeeTrainingDrawer(worker) {
    const canRead = canAction(ctx.me, 'competency.read');
    const canManage = canAction(ctx.me, 'competency.manage');
    const canCertify = canAction(ctx.me, 'competency.certify');
    const canAuthorize = canAction(ctx.me, 'competency.machine.authorize');
    const [qualsRes, typesRes, docTrainRes, coursesRes, assessmentsRes, authorizationsRes] = await Promise.all([
      canRead ? tryGet(`/api/competency/people/${encodeURIComponent(worker.personId)}`) : Promise.resolve({ data: null }),
      canRead ? tryGet('/api/competency/qualification-types') : Promise.resolve({ data: [] }),
      canRead ? tryGet(`/api/engineering/document-training?personId=${encodeURIComponent(worker.personId)}`) : Promise.resolve({ data: null }),
      canRead ? tryGet(`/api/competency/training-course-completions?personIds=${encodeURIComponent(worker.personId)}`) : Promise.resolve({ data: [] }),
      canRead ? tryGet(`/api/competency/practical-assessments?personIds=${encodeURIComponent(worker.personId)}`) : Promise.resolve({ data: [] }),
      canRead ? tryGet(`/api/competency/machine-authorizations?personIds=${encodeURIComponent(worker.personId)}`) : Promise.resolve({ data: [] }),
    ]);
    const types = typesRes.data ?? [];
    const typeName = code => types.find(type => type.code === code)?.name ?? code;
    const typeOptions = () => types.map(type => [type.code, `${type.name} (${type.code})`]);
    const quals = qualsRes.data ?? [];
    const courseCompletions = coursesRes.data ?? [];
    const assessments = assessmentsRes.data ?? [];
    const authorizations = authorizationsRes.data ?? [];
    const docRecords = (docTrainRes.data?.records ?? []).filter(record => !record.personId || record.personId === worker.personId);
    const statusTone = { InTraining: 'info', Qualified: 'ok', Suspended: 'warn', Revoked: 'bad' };
    const machineProfiles = (state.resources?.machineActivities ?? []).filter(profile => profile.operatorQualifications?.length);
    const machineActivityOptions = () => machineProfiles.map(profile => [`${profile.machineCode}|${profile.activityCode}|${profile.operatorQualifications.join('|')}`, `${profile.machineCode} · ${profile.activityCode}`]);
    const passingAssessmentOptions = () => assessments.filter(item => item.passed).map(item => [item.assessmentId, `${item.qualificationCode} · ${item.machineCode} / ${item.activityCode} · ${item.assessmentVersion}`]);

    const box = drawer(`Training & qualifications · ${worker.displayName}`, `
      <div class="stack" id="emp-training-drawer">
        <div class="row"><div><b>${esc(worker.displayName)}</b> <span class="muted">· ${esc(worker.jobTitle || 'Official title not set')}</span><div class="faint">Home work center: ${esc(worker.homeWorkCenterCode ?? '—')} · Shift: ${esc(worker.shiftCode ?? '—')}</div></div>
          <div class="inline">${canManage ? '<button class="btn small primary" type="button" id="drawer-start-training"' + (!types.length ? ' disabled' : '') + '>+ Start training</button>' : ''}${canCertify ? '<button class="btn small" type="button" id="drawer-certify"' + (!types.length ? ' disabled' : '') + '>+ Certify</button>' : ''}${canCertify ? '<button class="btn small" type="button" id="drawer-course-completion"' + (!types.length ? ' disabled' : '') + '>+ Course completion</button>' : ''}${canCertify ? '<button class="btn small" type="button" id="drawer-practical-assessment"' + (!machineProfiles.length ? ' disabled' : '') + '>+ Practical assessment</button>' : ''}${canManage ? '<button class="btn small" type="button" id="drawer-doc-training"' + (!canRead ? ' disabled' : '') + '>+ Document training</button>' : ''}</div>
        </div>
        ${!canRead ? '<div class="notice warn">Qualification and document-training records are not readable with your current permissions.</div>' : ''}
        ${typesRes.error ? errorBlock(typesRes.error, 'qualification types') : ''}
        <section class="panel"><header><h3>Qualifications (${quals.length})</h3></header>
          ${qualsRes.error ? errorBlock(qualsRes.error, 'qualifications') : !canRead ? '<div class="muted">No read permission.</div>' : !quals.length ? '<div class="muted">No qualifications recorded for this employee yet.</div>' : `
          <table class="table"><thead><tr><th>Qualification</th><th>Status</th><th>Certified</th><th>Expires</th><th></th></tr></thead><tbody>
            ${quals.map(qualification => `<tr><td><b>${esc(typeName(qualification.code))}</b> <span class="mono faint">${esc(qualification.code)}</span></td><td><span class="pill ${statusTone[qualification.status] ?? 'none'}">${esc(qualification.status)}</span></td><td>${qualification.certifiedAt ? esc(fmtDateTime(qualification.certifiedAt)) : '—'}</td><td>${qualification.expiresAt ? esc(fmtDateTime(qualification.expiresAt)) : '—'}</td><td class="row-actions">
              ${canCertify && qualification.status === 'InTraining' ? `<button class="btn small" type="button" data-drawer-qual="certify" data-code="${esc(qualification.code)}">Certify</button>` : ''}
              ${canCertify && qualification.status === 'Qualified' ? `<button class="btn small ghost" type="button" data-drawer-qual="suspensions" data-code="${esc(qualification.code)}">Suspend</button>` : ''}
              ${canCertify && ['Qualified', 'Suspended'].includes(qualification.status) ? `<button class="btn small ghost" type="button" data-drawer-qual="revocations" data-code="${esc(qualification.code)}">Revoke</button>` : ''}
            </td></tr>`).join('')}
          </tbody></table>`}
        </section>
        <section class="panel"><header><h3>Training course completions (${courseCompletions.length})</h3></header>
          ${coursesRes.error ? errorBlock(coursesRes.error, 'course completion records') : !courseCompletions.length ? '<p class="muted">No course completion evidence recorded. Course completion does not grant a qualification.</p>' : `<div class="table-wrap"><table class="table"><thead><tr><th>Course</th><th>Version</th><th>Completed</th><th>Expires</th><th>Trainer</th><th>Evidence</th></tr></thead><tbody>${courseCompletions.map(item => `<tr><td>${esc(item.courseCode)}</td><td>${esc(item.courseVersion)}</td><td>${esc(fmtDateTime(item.completedAt))}</td><td>${item.expiresAt ? esc(fmtDateTime(item.expiresAt)) : 'No expiry recorded'}</td><td>${esc(state.workers.find(candidate => candidate.personId === item.trainerPersonId)?.displayName ?? `person ${String(item.trainerPersonId ?? '').slice(0, 8)}`)}</td><td>${esc(item.evidence)}</td></tr>`).join('')}</tbody></table></div>`}
        </section>
        <section class="panel"><header><h3>Machine practical assessments (${assessments.length})</h3></header>
          ${assessmentsRes.error ? errorBlock(assessmentsRes.error, 'practical assessments') : !assessments.length ? '<p class="muted">No machine-specific practical assessment recorded.</p>' : `<div class="table-wrap"><table class="table"><thead><tr><th>Qualification</th><th>Machine / activity</th><th>Result</th><th>Version</th><th>Assessed</th><th>Evidence / restriction</th></tr></thead><tbody>${assessments.map(item => `<tr><td>${esc(item.qualificationCode)}</td><td>${esc(item.machineCode)} · ${esc(item.activityCode)}</td><td><span class="pill ${item.passed ? 'ok' : 'bad'}">${item.passed ? 'Passed' : 'Not passed'}</span></td><td>${esc(item.assessmentVersion)}</td><td>${esc(fmtDateTime(item.assessedAt))}</td><td>${esc(item.evidence)}${item.restrictions ? `<div class="faint">${esc(item.restrictions)}</div>` : ''}</td></tr>`).join('')}</tbody></table></div>`}
        </section>
        <section class="panel"><header><h3>Machine-specific authorizations (${authorizations.length})</h3>${canAuthorize && assessments.some(item => item.passed) ? '<button class="btn small" type="button" id="drawer-machine-authorize">+ Approve authorization</button>' : ''}</header>
          ${authorizationsRes.error ? errorBlock(authorizationsRes.error, 'machine authorizations') : !authorizations.length ? '<p class="muted">No machine authorization recorded. A general qualification alone does not authorize machine-specific work when the plant requires it.</p>' : `<div class="table-wrap"><table class="table"><thead><tr><th>Machine / activity</th><th>Qualification</th><th>Status</th><th>Revision / expiry</th><th>Restrictions</th><th>Action</th></tr></thead><tbody>${authorizations.map(item => `<tr><td>${esc(item.machineCode)} · ${esc(item.activityCode)}</td><td>${esc(item.qualificationCode)}</td><td><span class="pill ${item.status === 'Authorized' && item.independentOperationAllowed && (!item.expiresAt || Date.parse(item.expiresAt) > Date.now()) && (!item.courseExpiresAt || Date.parse(item.courseExpiresAt) > Date.now()) ? 'ok' : item.status === 'Suspended' ? 'warn' : 'bad'}">${esc(item.status)}${item.status === 'Authorized' && !item.independentOperationAllowed ? ' · supervised only' : ''}</span></td><td>r${item.revision}${item.expiresAt ? ` · auth ${esc(fmtDateTime(item.expiresAt))}` : ''}${item.courseExpiresAt ? ` · course ${esc(fmtDateTime(item.courseExpiresAt))}` : ''}</td><td>${esc(item.restrictions ?? '—')}</td><td class="row-actions">${canAuthorize && item.status === 'Authorized' ? `<button class="btn small ghost" type="button" data-auth-status="Suspended" data-auth-machine="${esc(item.machineCode)}" data-auth-activity="${esc(item.activityCode)}" data-auth-qualification="${esc(item.qualificationCode)}">Suspend</button>` : ''}${canAuthorize && item.status !== 'Revoked' ? `<button class="btn small ghost" type="button" data-auth-status="Revoked" data-auth-machine="${esc(item.machineCode)}" data-auth-activity="${esc(item.activityCode)}" data-auth-qualification="${esc(item.qualificationCode)}">Revoke</button>` : ''}</td></tr>`).join('')}</tbody></table></div>`}
        </section>
        <section class="panel"><header><h3>Controlled document training (${docRecords.length})</h3></header>
          ${docTrainRes.error ? errorBlock(docTrainRes.error, 'document training') : !canRead ? '<div class="muted">No read permission.</div>' : !docRecords.length ? '<div class="muted">No controlled document training recorded for this employee.</div>' : `
          <table class="table"><thead><tr><th>Document</th><th>Revision</th><th>Trained on</th></tr></thead><tbody>${docRecords.map(record => `<tr><td class="mono">${esc(record.documentNumber)}</td><td class="mono">${esc(record.revision)}</td><td>${esc(record.trainedOn ?? '—')}</td></tr>`).join('')}</tbody></table>`}
        </section>
      </div>`);

    const refreshDrawer = async () => {
      await load();
      const currentWorker = state.workers.find(person => person.personId === worker.personId) ?? worker;
      await openEmployeeTrainingDrawer(currentWorker);
    };
    $('#drawer-start-training', box.element)?.addEventListener('click', () => formModal({
      title: `Start ${worker.displayName}'s training`, fields: [{ name: 'code', label: 'Qualification', type: 'select', required: true, blank: '— choose —', options: typeOptions() }],
      confirmLabel: 'Start training', reasonRequired: true,
    }, (values, reason) => command(`/api/competency/people/${encodeURIComponent(worker.personId)}/training`, { code: values.code }, reason), refreshDrawer));
    const certify = code => formModal({
      title: `Certify ${worker.displayName}`, detail: 'Certification requires evidence. Prerequisites must already be held.',
      fields: [{ name: 'code', label: 'Qualification', type: 'select', required: true, blank: '— choose —', value: code ?? '', options: typeOptions() }, { name: 'evidence', label: 'Evidence', type: 'textarea', required: true }],
      confirmLabel: 'Certify', reasonRequired: true,
    }, (values, reason) => command(`/api/competency/people/${encodeURIComponent(worker.personId)}/certifications`, { code: values.code, evidence: values.evidence }, reason), refreshDrawer);
    $('#drawer-certify', box.element)?.addEventListener('click', () => certify(null));
    $('#drawer-course-completion', box.element)?.addEventListener('click', () => formModal({
      title: `Record course completion for ${worker.displayName}`, detail: 'Requires a qualified trainer. This course record is separate from practical assessment and does not grant machine authorization.',
      confirmLabel: 'Record completion', reasonRequired: true,
      fields: [{ name: 'courseCode', label: 'Course / qualification code', type: 'select', required: true, blank: '— choose —', options: typeOptions() }, { name: 'courseVersion', label: 'Course revision', required: true }, { name: 'expiresAt', label: 'Expires (optional)', type: 'date' }, { name: 'evidence', label: 'Evidence reference', required: true }],
    }, (values, reason) => command(`/api/competency/people/${encodeURIComponent(worker.personId)}/training-course-completions`, {
      courseCode: values.courseCode, courseVersion: values.courseVersion, expiresAt: values.expiresAt ? new Date(`${values.expiresAt}T23:59:59`).toISOString() : null, evidence: values.evidence,
    }, reason), refreshDrawer));
    $('#drawer-practical-assessment', box.element)?.addEventListener('click', () => formModal({
      title: `Record machine practical assessment for ${worker.displayName}`, detail: 'The operator must hold the underlying qualification and a current course completion. A passing assessment still needs separate authorization approval.',
      confirmLabel: 'Record assessment', reasonRequired: true,
      fields: [{ name: 'machineActivity', label: 'Machine / activity', type: 'select', required: true, blank: '— choose —', options: machineActivityOptions() }, { name: 'qualificationCode', label: 'Qualification', type: 'select', required: true, blank: '— choose —', options: typeOptions() }, { name: 'passed', label: 'Assessment result', type: 'check', text: 'Practical assessment passed', value: false }, { name: 'assessmentVersion', label: 'Assessment version', required: true }, { name: 'evidence', label: 'Evidence reference', required: true }, { name: 'restrictions', label: 'Restrictions (optional)' }],
    }, (values, reason) => {
      const [machineCode, activityCode] = String(values.machineActivity).split('|');
      if (!(state.resources?.machineActivities ?? []).some(profile => profile.machineCode === machineCode && profile.activityCode === activityCode && profile.operatorQualifications.includes(values.qualificationCode))) throw new Error('Choose a qualification required by the selected machine and activity.');
      return command(`/api/competency/people/${encodeURIComponent(worker.personId)}/practical-assessments`, {
        qualificationCode: values.qualificationCode, machineCode, activityCode, passed: values.passed, assessmentVersion: values.assessmentVersion, evidence: values.evidence, restrictions: values.restrictions || null,
      }, reason);
    }, refreshDrawer));
    $('#drawer-machine-authorize', box.element)?.addEventListener('click', () => formModal({
      title: `Approve machine authorization for ${worker.displayName}`, detail: 'Approval requires the underlying qualification, current course completion, and a matching passing practical assessment. The approver must be a different person.',
      confirmLabel: 'Approve authorization', reasonRequired: true,
      fields: [{ name: 'assessmentId', label: 'Passing practical assessment', type: 'select', required: true, blank: '— choose —', options: passingAssessmentOptions() }, { name: 'independentOperationAllowed', label: 'Independent operation', type: 'check', text: 'Authorize independent operation on this exact machine and activity', value: true }, { name: 'expiresAt', label: 'Expires (optional)', type: 'date' }, { name: 'evidence', label: 'Approval evidence', required: true }, { name: 'restrictions', label: 'Restrictions (optional)' }],
    }, (values, reason) => {
      const assessment = assessments.find(item => item.assessmentId === values.assessmentId);
      if (!assessment) throw new Error('Choose an available passing assessment.');
      return command(`/api/competency/people/${encodeURIComponent(worker.personId)}/machine-authorizations`, {
        machineCode: assessment.machineCode, activityCode: assessment.activityCode, qualificationCode: assessment.qualificationCode,
        assessmentId: assessment.assessmentId, expiresAt: values.expiresAt ? new Date(`${values.expiresAt}T23:59:59`).toISOString() : null,
        evidence: values.evidence, restrictions: values.restrictions || null, independentOperationAllowed: values.independentOperationAllowed,
      }, reason);
    }, refreshDrawer));
    $('#drawer-doc-training', box.element)?.addEventListener('click', () => formModal({
      title: `Record document training for ${worker.displayName}`, confirmLabel: 'Record training', reasonRequired: true,
      fields: [{ name: 'documentNumber', label: 'Document number', code: true, required: true, placeholder: 'WI-SLIT-01' }, { name: 'revision', label: 'Revision', required: true, placeholder: 'A' }, { name: 'trainedOn', label: 'Trained on', type: 'date' }],
    }, (values, reason) => command('/api/engineering/document-training', { personId: worker.personId, documentNumber: values.documentNumber, revision: values.revision, trainedOn: values.trainedOn || null }, reason), refreshDrawer));
    $$('[data-drawer-qual]', box.element).forEach(button => button.addEventListener('click', () => {
      const kind = button.dataset.drawerQual, code = button.dataset.code;
      if (kind === 'certify') return certify(code);
      const word = kind === 'suspensions' ? 'Suspend' : 'Revoke';
      formModal({ title: `${word} ${typeName(code)} for ${worker.displayName}`, detail: kind === 'suspensions' ? 'They cannot perform work requiring this qualification until reinstated.' : 'Revocation is final; a new certification is needed.', fields: [], confirmLabel: word, reasonRequired: true },
        (_, reason) => command(`/api/competency/people/${encodeURIComponent(worker.personId)}/${kind}`, { code }, reason), refreshDrawer);
    }));
    $$('[data-auth-status]', box.element).forEach(button => button.addEventListener('click', () => {
      const status = button.dataset.authStatus;
      formModal({ title: `${status} machine authorization`, detail: 'This changes the authoritative machine gate immediately and appends a revisioned record.', fields: [], confirmLabel: status, reasonRequired: true },
        (_, reason) => command(`/api/competency/people/${encodeURIComponent(worker.personId)}/machine-authorizations/${encodeURIComponent(button.dataset.authMachine)}/${encodeURIComponent(button.dataset.authActivity)}/${encodeURIComponent(button.dataset.authQualification)}/status`, { status }, reason), refreshDrawer);
    }));
  }

  function assignmentEditor(move, center) {
    if (!center) return '';
    const worker = state.workers.find(person => person.personId === move.personId);
    const qualificationGuard = worker ? moveQualificationGuard(worker, move) : { blocked: false, message: '' };
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
      ${qualificationGuard.message ? `<div class="notice ${qualificationGuard.blocked ? 'bad' : 'info'} small" role="${qualificationGuard.blocked ? 'alert' : 'status'}">${esc(qualificationGuard.message)}</div>` : ''}
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
      <p class="muted">This evaluates active work and staffing safety at the current snapshot. It does not certify future shift coverage or calculate order due dates, material/MRP, overtime costs, or a future roster. Review pre-shift plans again when attendance and qualifications are current. A safety finding is not an override.</p>
      ${output.steps.map(step => {
        const preview = step.preview;
        const findings = preview.impacts ?? [];
        const sections = new Map();
        for (const item of findings) {
          const code = item.work?.workCenterCode ?? item.work?.safetyAreaCode ?? 'Other';
          if (!sections.has(code)) sections.set(code, []);
          sections.get(code).push(item);
        }
        return `<article class="workforce-preview-step"><h3>Move ${step.number}: ${esc(preview.personName)}</h3><div class="muted">${esc(assignmentText(preview.current))} → ${esc(assignmentText(preview.proposed))}</div>
          ${sections.size ? [...sections].map(([code, impacts]) => `<section class="workforce-section-impact"><h4>${esc(centerByCode(code)?.name ?? code)}</h4><ul class="evidence">${impacts.map(item => `<li class="${['WouldStop', 'WouldBeUnstaffed'].includes(item.kind) ? 'c-bad' : 'c-info'}"><b>${esc(item.kind)}:</b> ${esc(item.detail)}</li>`).join('')}</ul></section>`).join('') : '<div class="notice ok small">This move changes no currently executable work.</div>'}
          ${(preview.options ?? []).length ? `<div class="muted small"><b>Possible safe responses to review:</b> ${(preview.options ?? []).map(option => esc(option.description)).join(' · ')}</div>` : ''}
        </article>`;
      }).join('')}
    </section>`;
  }

  async function analyze() {
    const moves = [...state.moves.values()];
    const blocked = moves.map(move => ({ move, worker: state.workers.find(person => person.personId === move.personId) }))
      .find(item => item.worker && moveQualificationGuard(item.worker, item.move).blocked);
    if (blocked) { toast(moveQualificationGuard(blocked.worker, blocked.move).message, true); return; }
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

  function stage(worker, targetCode, selection = null) {
    const previous = state.moves.get(worker.personId);
    if (targetCode === '__available') {
      state.moves.set(worker.personId, { personId: worker.personId, unassign: true });
    } else {
      const center = centerByCode(targetCode);
      if (!center) return;
      const activities = activitiesAt(center.code);
      const existingActivity = worker.assignment?.activityCode;
      const inferredActivityCode = previous?.workCenterCode === center.code
        ? previous.activityCode
        : activities.includes(existingActivity) ? existingActivity : activities.length === 1 ? activities[0] : '';
      const activityCode = selection?.activityCode ?? inferredActivityCode;
      const machines = activityCode ? machinesFor(center.code, activityCode) : [];
      const inferredMachineCode = previous?.workCenterCode === center.code
        ? previous.machineCode
        : machines.some(machine => machine.code === worker.assignment?.machineCode) ? worker.assignment.machineCode : '';
      const machineCode = selection?.machineCode ?? inferredMachineCode;
      state.moves.set(worker.personId, { personId: worker.personId, unassign: false, safetyAreaCode: center.safetyAreaCode, workCenterCode: center.code, activityCode, machineCode });
    }
    state.preview = null; state.previewError = null; render();
  }

  function wireBoard() {
    wireCards();
    $$('[data-board-view]', page).forEach(button => button.addEventListener('click', () => {
      if (state.layoutEdit || state.viewMode === button.dataset.boardView) return;
      state.viewMode = button.dataset.boardView;
      renderBoardOnly();
    }));
    $('#edit-floor-layout', page)?.addEventListener('click', () => {
      state.layoutBaseline = mapPositionsForCenters();
      state.layoutDraft = new Map([...state.layoutBaseline].map(([code, position]) => [code, { ...position }]));
      state.layoutEdit = true;
      renderBoardOnly();
    });
    $('#cancel-floor-layout', page)?.addEventListener('click', () => {
      state.layoutEdit = false; state.layoutDraft.clear(); state.layoutBaseline.clear(); renderBoardOnly();
    });
    $('#save-floor-layout', page)?.addEventListener('click', () => { void saveFloorLayout(); });
    $$('[data-map-step]', page).forEach(button => button.addEventListener('click', () => {
      const current = state.layoutDraft.get(button.dataset.mapStep);
      if (!current) return;
      moveMapCenter(button.dataset.mapStep, current.mapX + Number(button.dataset.dx ?? 0) * 35, current.mapY + Number(button.dataset.dy ?? 0) * 35);
    }));
    $$('[data-map-toggle]', page).forEach(button => button.addEventListener('click', () => {
      state.expandedMapCenter = state.expandedMapCenter === button.dataset.mapToggle ? null : button.dataset.mapToggle;
      renderBoardOnly();
    }));
    $$('[data-map-machine]', page).forEach(button => button.addEventListener('click', () => openMachineReadiness(button.dataset.mapMachine)));
    $$('[data-map-layer]', page).forEach(button => button.addEventListener('click', () => {
      const layer = button.dataset.mapLayer;
      if (!Object.hasOwn(state.mapLayers, layer)) return;
      state.mapLayers[layer] = !state.mapLayers[layer];
      renderBoardOnly();
    }));
    $$('[data-map-zoom]', page).forEach(button => button.addEventListener('click', () => {
      const action = button.dataset.mapZoom;
      if (action === 'reset-view') {
        state.mapZoom = 1;
        state.expandedMapCenter = null;
        state.mapCoverageQuery = '';
        state.mapLayers = { workers: true, readiness: true, machines: true, orders: false, materials: false, quality: false, safety: false, maintenance: false, bottlenecks: false };
      } else if (action === 'fit') state.mapZoom = 1;
      else state.mapZoom = Math.min(1.75, Math.max(.75, state.mapZoom + (action === 'in' ? .25 : -.25)));
      renderBoardOnly();
      if (action === 'reset-view') page.querySelectorAll('.plant-map-viewport').forEach(viewport => { viewport.scrollLeft = 0; viewport.scrollTop = 0; });
    }));
    const coverageForm = $('[data-map-coverage-search]', page);
    coverageForm?.addEventListener('submit', event => {
      event.preventDefault();
      state.mapCoverageQuery = $('[name="query"]', coverageForm)?.value?.trim() ?? '';
      renderBoardOnly();
    });
    $('[data-map-coverage-clear]', page)?.addEventListener('click', () => {
      state.mapCoverageQuery = '';
      renderBoardOnly();
    });
    $$('[data-map-pin]', page).forEach(pin => pin.addEventListener('dragstart', event => {
      if (event.target.closest('[data-worker-card]')) return;
      if (!state.layoutEdit) { event.preventDefault(); return; }
      event.dataTransfer?.setData('text/plain', `work-center:${pin.dataset.mapPin}`);
    }));
    $$('[data-map-canvas]', page).forEach(canvas => {
      canvas.addEventListener('dragover', event => { if (state.layoutEdit && event.dataTransfer?.types?.includes('text/plain')) { event.preventDefault(); canvas.classList.add('is-drop-target'); } });
      canvas.addEventListener('dragleave', event => { if (!canvas.contains(event.relatedTarget)) canvas.classList.remove('is-drop-target'); });
      canvas.addEventListener('drop', event => {
        const transfer = event.dataTransfer?.getData('text/plain') ?? '';
        if (!state.layoutEdit || !transfer.startsWith('work-center:')) return;
        event.preventDefault(); event.stopPropagation(); canvas.classList.remove('is-drop-target');
        const bounds = canvas.getBoundingClientRect();
        const x = Math.round(Math.min(1, Math.max(0, (event.clientX - bounds.left) / Math.max(1, bounds.width))) * 1000);
        const y = Math.round(Math.min(1, Math.max(0, (event.clientY - bounds.top) / Math.max(1, bounds.height))) * 1000);
        moveMapCenter(transfer.slice('work-center:'.length), x, y);
      });
    });
    $$('[data-map-upload]', page).forEach(input => input.addEventListener('change', async () => {
      const file = input.files?.[0];
      if (!file) return;
      const siteCode = input.dataset.mapUpload;
      try {
        const result = await confirmWithReason({ title: 'Upload plant map', detail: `Replace the floor-plan image for ${siteCode}. Work-center positions will stay in place.`, confirmLabel: 'Upload map', reasonRequired: true }, reason => uploadVisualImage(`/api/resources/sites/${encodeURIComponent(siteCode)}/map`, file, reason));
        if (result) { clearVisual(`site:${siteCode}`); await load(); }
      } catch (error) { toast(error.message || 'Map upload failed.', true); }
      finally { input.value = ''; }
    }));
    $$('[data-map-remove]', page).forEach(button => button.addEventListener('click', async () => {
      const siteCode = button.dataset.mapRemove;
      try {
        const result = await confirmWithReason({ title: 'Use reference map', detail: `Remove the uploaded map for ${siteCode} and return to the supplied reference floor plan.`, confirmLabel: 'Use reference map', reasonRequired: true }, reason => removeVisualImage(`/api/resources/sites/${encodeURIComponent(siteCode)}/map`, reason));
        if (result) { clearVisual(`site:${siteCode}`); await load(); }
      } catch (error) { toast(error.message || 'Map reset failed.', true); }
    }));
  }

  function moveMapCenter(code, x, y) {
    const current = state.layoutDraft.get(code);
    if (!current || !centerByCode(code)) return;
    const next = { mapX: Math.min(1000, Math.max(0, Math.round(x))), mapY: Math.min(1000, Math.max(0, Math.round(y))) };
    const other = centers().find(item => item.code !== code && state.layoutDraft.get(item.code)?.mapX === next.mapX && state.layoutDraft.get(item.code)?.mapY === next.mapY);
    if (other) state.layoutDraft.set(other.code, { ...current });
    state.layoutDraft.set(code, next);
    renderBoardOnly();
  }

  async function saveFloorLayout() {
    if (!layoutHasChanges()) return;
    const placements = [...state.layoutDraft].map(([code, position]) => ({ code, ...position }));
    const result = await confirmWithReason({
      title: 'Save plant map positions',
      detail: `Save positions for ${placements.length} active work centers. The change is recorded in the plant audit history.`,
      confirmLabel: 'Save positions', reasonRequired: true,
    }, reason => command('/api/resources/work-centers/map-layout', { placements }, reason));
    if (!result) return;
    state.layoutEdit = false; state.layoutDraft.clear(); state.layoutBaseline.clear();
    await load();
  }

  function wireCards() {
    $$('[data-floor-details]', page).forEach(details => details.addEventListener('toggle', () => {
      if (details.open) state.openFloorDetails.add(details.dataset.floorDetails);
      else state.openFloorDetails.delete(details.dataset.floorDetails);
    }));
    $$('[data-expand-lane]', page).forEach(button => button.addEventListener('click', () => {
      const code = button.dataset.expandLane;
      if (state.expandedLanes.has(code)) state.expandedLanes.delete(code); else state.expandedLanes.add(code);
      renderBoardOnly();
    }));
    $$('[data-stage-worker]', page).forEach(button => button.addEventListener('click', () => {
      const worker = state.workers.find(item => item.personId === button.dataset.stageWorker);
      const target = $$('[data-destination]', page).find(select => select.dataset.destination === button.dataset.stageWorker)?.value;
      if (worker) stage(worker, target);
    }));
    $$('[data-toggle-person]', page).forEach(button => button.addEventListener('click', () => {
      state.expandedPersonId = state.expandedPersonId === button.dataset.togglePerson ? null : button.dataset.togglePerson;
      render();
    }));
    $$('[data-emp-training]', page).forEach(button => button.addEventListener('click', () => {
      const worker = state.workers.find(person => person.personId === button.dataset.empTraining);
      if (worker) void openEmployeeTrainingDrawer(worker);
    }));
    $$('[data-emp-title]', page).forEach(button => button.addEventListener('click', () => {
      const worker = state.workers.find(person => person.personId === button.dataset.empTitle);
      if (worker) editWorkerTitle(worker);
    }));
    $$('[data-emp-department]', page).forEach(button => button.addEventListener('click', () => {
      const worker = state.workers.find(person => person.personId === button.dataset.empDepartment);
      if (worker) editWorkerDepartment(worker);
    }));
    $$('[data-emp-photo-upload]', page).forEach(input => input.addEventListener('change', async () => {
      const file = input.files?.[0];
      const worker = state.workers.find(person => person.personId === input.dataset.empPhotoUpload);
      if (!file || !worker) return;
      try {
        const result = await confirmWithReason({ title: `Upload photo for ${worker.displayName}`, detail: 'This photo appears on the workforce board and plant floor map. Use a clear, current headshot.', confirmLabel: 'Upload photo', reasonRequired: true }, reason => uploadVisualImage(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/photo`, file, reason));
        if (result) { clearVisual(`worker:${worker.personId}`); await load(); }
      } catch (error) { toast(error.message || 'Photo upload failed.', true); }
      finally { input.value = ''; }
    }));
    $$('[data-emp-photo-remove]', page).forEach(button => button.addEventListener('click', async () => {
      const worker = state.workers.find(person => person.personId === button.dataset.empPhotoRemove);
      if (!worker) return;
      try {
        const result = await confirmWithReason({ title: `Remove photo for ${worker.displayName}`, detail: 'The employee will show initials on the Kanban and plant map.', confirmLabel: 'Remove photo', reasonRequired: true }, reason => removeVisualImage(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/photo`, reason));
        if (result) { clearVisual(`worker:${worker.personId}`); await load(); }
      } catch (error) { toast(error.message || 'Photo removal failed.', true); }
    }));
    $$('[data-emp-presence]', page).forEach(button => button.addEventListener('click', () => {
      const worker = state.workers.find(person => person.personId === button.dataset.empPresence);
      if (worker) confirmWorkerPresence(worker);
    }));
    $$('[data-emp-unavailable]', page).forEach(button => button.addEventListener('click', () => {
      const worker = state.workers.find(person => person.personId === button.dataset.empUnavailable);
      if (worker) recordWorkerUnavailable(worker);
    }));
    $$('[data-emp-apply-move]', page).forEach(button => button.addEventListener('click', () => {
      const worker = state.workers.find(person => person.personId === button.dataset.empApplyMove);
      if (worker) void applyLiveMove(worker);
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
      card.addEventListener('dragstart', event => event.dataTransfer?.setData('text/plain', `person:${card.dataset.workerCard}`));
      card.addEventListener('dragend', () => $$('[data-drop-lane].is-drop-target', page).forEach(lane => lane.classList.remove('is-drop-target')));
    });
    $$('[data-drop-lane]', page).forEach(lane => {
      lane.addEventListener('dragover', event => { if (event.dataTransfer?.types?.includes('text/plain')) { event.preventDefault(); lane.classList.add('is-drop-target'); } });
      lane.addEventListener('dragleave', event => { if (!lane.contains(event.relatedTarget)) lane.classList.remove('is-drop-target'); });
      lane.addEventListener('drop', event => {
        const transfer = event.dataTransfer?.getData('text/plain') ?? '';
        if (!transfer.startsWith('person:')) return;
        event.preventDefault(); lane.classList.remove('is-drop-target');
        const id = transfer.slice('person:'.length);
        const worker = state.workers.find(item => item.personId === id);
        if (worker) stage(worker, lane.dataset.dropLane);
      });
    });
  }

  render();
  return { dispose() { state.disposed = true; for (const key of [...state.visualUrls.keys()]) clearVisual(key); } };
}
