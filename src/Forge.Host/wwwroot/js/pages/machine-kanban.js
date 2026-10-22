import { tryGet } from '../api.js';
import { esc, $, fmtDate, fmtDateTime, errorBlock, emptyBlock } from '../ui.js';
import { isDownAt } from '../model.js';
import { today } from '../forms.js';

const LANE_ORDER = ['blocked', 'attention', 'ready', 'unknown'];
const LANE = {
  blocked: { title: 'Blocked / Down', icon: '■', tone: 'bad' },
  attention: { title: 'Attention', icon: '!', tone: 'warn' },
  ready: { title: 'Ready', icon: '✓', tone: 'ok' },
  unknown: { title: 'Unknown / Not verified', icon: '?', tone: 'none' },
};
const DOWN_LABEL = { Planned: 'Planned maintenance', Unplanned: 'Reported down', MaintenanceOverdue: 'Locked out — maintenance overdue', AssetOutOfService: 'Asset out of service' };
const OPEN_MAINTENANCE = new Set(['Open', 'Scheduled', 'InProgress']);
const PM_KINDS = new Set(['Preventive', 'Inspection', 'Calibration']);

const has = (permissions, permission) => permissions.includes(permission);
const operationKey = value => `${value?.orderNumber ?? ''}/${value?.sequence ?? ''}`;
const isFailure = dimension => ['Fail', 'Indeterminate'].includes(dimension?.outcome);
const failureDetails = result => (result?.dimensions ?? []).filter(isFailure).map(dimension => `${dimension.name}: ${dimension.detail}`);
const dueNow = order => OPEN_MAINTENANCE.has(order.status)
  && ((order.dueDate && order.dueDate < today()) || order.dueByRunHours);
const timeInWindow = (record, now) => {
  const start = Date.parse(record.start ?? record.startsAt ?? '');
  const end = Date.parse(record.end ?? record.endsAt ?? '');
  return Number.isFinite(start) && Number.isFinite(end) && start <= now && now < end && record.scheduled !== false;
};

function normalizeWorkOrders(views) {
  return (views ?? []).map(view => ({ ...view.order, runHours: view.runHours, dueByRunHours: view.dueByRunHours, parts: view.parts }));
}

/** Pure status derivation over Forge results. It never invents a running state or a PM warning interval. */
export function deriveMachineCards({
  machines = [], workCenters = [], operations = [], activeWork = [], schedule = null,
  downtime = [], assets = [], maintenanceOrders = [], workers = [], qualifications = [],
  errors = {}, permissions = [], now = Date.now(),
}) {
  const scheduledOperations = schedule?.result?.operations ?? [];
  const orderOperationByKey = new Map(operations.map(operation => [operationKey(operation), operation]));
  const readableWorkers = has(permissions, 'workforce.read');
  const readableQualifications = has(permissions, 'competency.read');

  return machines.map(machine => {
    const wc = workCenters.find(center => center.code === machine.workCenterCode);
    const asset = assets.find(item => item.machineCode === machine.code);
    const downNow = downtime.filter(window => window.machineCode === machine.code && isDownAt(window, now));
    const futureWindows = downtime.filter(window => window.machineCode === machine.code && window.kind === 'Planned' && Date.parse(window.startsAt) > now)
      .sort((a, b) => Date.parse(a.startsAt) - Date.parse(b.startsAt));
    const openMaintenance = maintenanceOrders.filter(order => order.machineCode === machine.code && OPEN_MAINTENANCE.has(order.status));
    const overdueMaintenance = openMaintenance.filter(order => PM_KINDS.has(order.kind) && dueNow(order));
    const nextMaintenance = openMaintenance.filter(order => PM_KINDS.has(order.kind))
      .sort((a, b) => String(a.dueDate ?? '9999').localeCompare(String(b.dueDate ?? '9999')))[0] ?? null;
    const operationsForMachine = operations.filter(operation => operation.machine === machine.code)
      .sort((a, b) => a.priority - b.priority || String(a.dueDate).localeCompare(String(b.dueDate)) || a.orderNumber - b.orderNumber || a.sequence - b.sequence);
    const assignedWork = activeWork.map(run => run.output).filter(output => output?.work?.machineCode === machine.code);
    const scheduledNow = scheduledOperations.filter(operation => (operation.machineCode ?? operation.machine) === machine.code && timeInWindow(operation, now));
    const scheduledEvaluations = scheduledNow.map(operation => ({ plan: operation, evaluation: orderOperationByKey.get(operationKey(operation)) ?? null }));
    const staff = readableWorkers ? workers.filter(worker => worker.assignment?.machineCode === machine.code) : [];
    const blockers = [];

    for (const window of downNow) blockers.push({ source: 'Recorded downtime', text: `${DOWN_LABEL[window.kind] ?? window.kind}: ${window.reason || 'No reason recorded'}${window.startsAt ? ` · since ${fmtDateTime(window.startsAt)}` : ''}` });
    if (asset?.status === 'Restricted') blockers.push({ source: 'Production asset', text: `${asset.assetCode ?? machine.code} is Restricted${asset.statusReason ? `: ${asset.statusReason}` : ''}.` });
    for (const output of assignedWork.filter(work => !work.executable)) {
      blockers.push({ source: 'Assigned-work readiness', text: failureDetails(output).join(' ') || `Assigned ${output.work?.activityCode ?? 'work'} is not executable.` });
    }
    for (const { plan, evaluation } of scheduledEvaluations) {
      if (!evaluation) {
        blockers.push({ source: 'Approved schedule', text: `Order ${plan.orderNumber}/${plan.sequence} is scheduled now, but no matching operation-readiness result was returned.` });
      } else if (!evaluation.executable) {
        blockers.push({ source: 'Scheduled-operation readiness', text: `Order ${evaluation.orderNumber}/${evaluation.sequence}: ${evaluation.why || evaluation.primaryConstraint || 'Not executable.'}` });
        blockers.push(...(evaluation.dimensions ?? []).filter(isFailure).map(dimension => ({ source: dimension.name, text: dimension.detail })));
      }
    }

    const coreSourceError = errors.resources || errors.operations || errors.activeWork;
    const failedAssignedWork = assignedWork.some(work => !work.executable);
    const failedScheduledOperation = scheduledEvaluations.some(({ evaluation }) => evaluation && !evaluation.executable);
    const hasReadyScheduled = scheduledEvaluations.length > 0 && scheduledEvaluations.every(({ evaluation }) => evaluation?.executable === true);
    const hasReadyOpenOperation = !scheduledEvaluations.length && operationsForMachine.some(operation => operation.executable);
    const hasCurrentEvaluation = assignedWork.length > 0 || scheduledEvaluations.length > 0;
    const hardBlocked = downNow.length > 0 || asset?.status === 'Restricted' || failedAssignedWork || failedScheduledOperation;

    let status, summary;
    if (asset?.status === 'Decommissioned') {
      status = 'unknown'; summary = 'Decommissioned';
    } else if (hardBlocked) {
      status = 'blocked';
      summary = downNow.length ? (DOWN_LABEL[downNow[0].kind] ?? 'Reported down')
      : asset?.status === 'Restricted' ? 'Production asset restricted'
          : 'Operation blocked by readiness';
    } else if (coreSourceError) {
      status = 'unknown'; summary = 'Readiness data unavailable';
    } else if (overdueMaintenance.length || futureWindows.length) {
      status = 'attention';
      summary = overdueMaintenance.length ? 'Overdue maintenance work' : 'Planned downtime coming up';
    } else if (hasReadyScheduled || hasReadyOpenOperation) {
      status = 'ready'; summary = hasReadyScheduled ? 'Scheduled operation passes readiness' : 'Eligible open operation passes readiness';
    } else {
      status = 'unknown';
      summary = scheduledEvaluations.some(({ evaluation }) => !evaluation) ? 'Scheduled operation readiness unavailable'
        : hasCurrentEvaluation ? 'No linked order operation readiness result' : 'No current operation evaluated';
    }

    const currentEvaluation = scheduledEvaluations.find(item => item.evaluation)?.evaluation
      ?? operationsForMachine.find(operation => operation.executable)
      ?? operationsForMachine[0]
      ?? null;
    const nextScheduled = scheduledOperations.filter(operation => (operation.machineCode ?? operation.machine) === machine.code
      && operation.scheduled !== false && Date.parse(operation.start ?? '') > now)
      .sort((a, b) => Date.parse(a.start) - Date.parse(b.start))[0] ?? null;
    const candidateBlockers = operationsForMachine.filter(operation => !operation.executable).slice(0, 4);
    const workerNames = staff.map(worker => {
      const quals = readableQualifications ? qualifications.filter(record => record.personId === worker.personId && record.status === 'Qualified'
        && (!record.expiresAt || Date.parse(record.expiresAt) > now)).map(record => record.code) : [];
      return { name: worker.displayName, qualifications: quals, presence: worker.presenceKind ? `Presence: ${worker.presenceKind}` : worker.unavailability ? 'Unavailable' : 'No current presence' };
    });

    return {
      machine, workCenter: wc, asset, status, summary, blockers, currentEvaluation, nextScheduled,
      downNow, futureWindow: futureWindows[0] ?? null, overdueMaintenance, nextMaintenance,
      candidateBlockers, operationsForMachine, workerNames,
      maintenanceHidden: !has(permissions, 'maintenance.read'),
      workforceHidden: !readableWorkers,
      qualificationHidden: readableWorkers && !readableQualifications,
      error: errors.operations || errors.activeWork || errors.resources || null,
    };
  });
}

export async function machineKanbanPage(page, ctx) {
  const permissions = ctx.me?.permissions ?? [];
  const canSeeAny = ['planning.readiness.read', 'maintenance.read', 'resources.read'].some(permission => has(permissions, permission));
  if (!canSeeAny) {
    page.innerHTML = `<div class="page-head"><div><h1>Machine Readiness</h1></div></div>${emptyBlock('Plant access required', 'Resources, readiness or maintenance read permission is required to view machine status.')}`;
    return { dispose() {} };
  }

  const state = { machines: [], workCenters: [], operations: [], activeWork: [], schedule: null, downtime: [], assets: [], maintenanceOrders: [], workers: [], qualifications: [], errors: {},
    refreshedAt: null, loading: false, center: '', signal: '', query: '' };
  await load();

  async function load() {
    const time = Date.now();
    const iso = ms => new Date(ms).toISOString();
    state.loading = true;
    if (state.machines.length) render();
    const permittedGet = (permission, path) => has(permissions, permission)
      ? tryGet(path)
      : Promise.resolve({ data: null, error: null, denied: true });
    const [resources, operations, activeWork, downtime, assets, workOrders, schedule, workers] = await Promise.all([
      permittedGet('resources.read', '/api/resources/'),
      permittedGet('planning.readiness.read', '/api/readiness/operations'),
      permittedGet('planning.readiness.read', '/api/readiness/active-work'),
      permittedGet('maintenance.read', `/api/maintenance/downtime?from=${encodeURIComponent(iso(time - 864e5))}&to=${encodeURIComponent(iso(time + 14 * 864e5))}`),
      permittedGet('maintenance.read', '/api/maintenance/assets'),
      permittedGet('maintenance.read', '/api/maintenance/work-orders?includeClosed=false'),
      permittedGet('planning.schedule.read', '/api/planning/schedules/approved'),
      permittedGet('workforce.read', '/api/workforce/workers'),
    ]);
    const source = (name, result) => {
      if (result.denied) return;
      if (result.error) state.errors[name] = result.error;
      else delete state.errors[name];
    };
    for (const [name, result] of Object.entries({ resources, operations, activeWork, downtime, assets, workOrders, schedule, workers })) source(name, result);
    if (resources.data) {
      state.machines = resources.data.machines ?? [];
      state.workCenters = resources.data.workCenters ?? [];
    }
    if (operations.data) state.operations = operations.data;
    if (activeWork.data) state.activeWork = activeWork.data;
    if (downtime.data) state.downtime = downtime.data;
    if (assets.data) state.assets = assets.data;
    if (workOrders.data) state.maintenanceOrders = normalizeWorkOrders(workOrders.data);
    if (schedule.data) state.schedule = schedule.data;
    if (workers.data) state.workers = workers.data;
    else if (workers.denied) state.workers = [];

    if (operations.error) state.operations = [];
    if (activeWork.error) state.activeWork = [];
    if (schedule.error) state.schedule = null;

    state.qualifications = [];
    if (workers.data && has(permissions, 'competency.read')) {
      const ids = workers.data.map(worker => worker.personId).filter(Boolean);
      let failed = null;
      for (let offset = 0; offset < ids.length; offset += 100) {
        const query = new URLSearchParams();
        ids.slice(offset, offset + 100).forEach(id => query.append('personIds', id));
        const qualifications = await tryGet(`/api/competency/people/qualifications?${query}`);
        if (qualifications.error) { failed = qualifications.error; break; }
        state.qualifications.push(...(qualifications.data ?? []));
      }
      if (failed) state.errors.qualifications = failed;
      else delete state.errors.qualifications;
    }
    state.refreshedAt = new Date().toISOString();
    state.loading = false;
    render();
  }

  function cards() {
    return deriveMachineCards({
      machines: state.machines, workCenters: state.workCenters, operations: state.operations,
      activeWork: state.activeWork, schedule: state.schedule, downtime: state.downtime,
      assets: state.assets, maintenanceOrders: state.maintenanceOrders, workers: state.workers,
      qualifications: state.qualifications, errors: state.errors, permissions, now: Date.now(),
    });
  }

  function visibleCards() {
    const query = state.query.trim().toLocaleLowerCase();
    return cards().filter(card => (!state.center || card.machine.workCenterCode === state.center)
      && (!state.signal || card.status === state.signal)
      && (!query || `${card.machine.code} ${card.machine.name} ${card.machine.workCenterCode} ${card.currentEvaluation?.orderNumber ?? ''} ${card.summary}`.toLocaleLowerCase().includes(query)));
  }

  function render() {
    const all = cards();
    const shown = visibleCards();
    const centerOptions = state.workCenters.map(center => `<option value="${esc(center.code)}" ${center.code === state.center ? 'selected' : ''}>${esc(center.name)} (${esc(center.code)})</option>`).join('');
    const errors = Object.entries(state.errors).filter(([name]) => !['workers', 'qualifications', 'schedule'].includes(name));
    const readAt = state.refreshedAt ? fmtDateTime(state.refreshedAt) : 'not yet read';
    page.innerHTML = `
      <div class="page-head"><div><h1>Machine Readiness</h1><div class="sub">Exceptions first. Signals come from Forge readiness and maintenance records; this plant has no live machine telemetry feed.</div></div>
        <div class="spacer"></div><div class="head-controls"><a class="btn" href="#/supervisor">Plant Overview</a>${has(permissions, 'workforce.supervise') || has(permissions, 'workforce.manage') ? '<a class="btn" href="#/workforce-kanban">Workforce Kanban</a>' : ''}<button class="btn primary" id="refresh-machine-board" type="button" ${state.loading ? 'disabled' : ''}>${state.loading ? 'Refreshing…' : 'Refresh status'}</button></div>
      </div>
      <div class="notice warn"><b>Readiness is not telemetry.</b> “Ready” means an evaluated Forge operation passed at the last read time; it does not mean the machine is running. Missing or failed readiness reads remain Unknown. A status here cannot clear a safety, maintenance, quality or material blocker.</div>
      ${!has(permissions, 'planning.readiness.read') ? '<div class="notice warn">Your role cannot read operation readiness; machine readiness stays Unknown unless a separate Forge record confirms a blocker.</div>' : ''}
      ${errors.length ? `<section class="panel machine-source-errors"><b>Some source reads failed.</b><span> Failed readiness/resource reads prevent a Ready signal; maintenance and workforce details may be incomplete.</span>${errors.map(([name, error]) => `<div>${errorBlock(error, name)}</div>`).join('')}</section>` : ''}
      <section class="kpis machine-board-kpis" aria-label="Machine status summary">${LANE_ORDER.map(key => `<div class="kpi machine-kpi ${key}"><div><div class="t">${esc(LANE[key].title)}</div><div class="v">${all.filter(card => card.status === key).length}</div></div></div>`).join('')}</section>
      <section class="panel machine-board-filters" aria-label="Machine board filters">
        <label class="field"><span>Work center</span><select class="input" id="machine-center"><option value="">All work centers</option>${centerOptions}</select></label>
        <label class="field"><span>Signal</span><select class="input" id="machine-signal"><option value="">All signals</option>${LANE_ORDER.map(key => `<option value="${key}" ${state.signal === key ? 'selected' : ''}>${esc(LANE[key].title)}</option>`).join('')}</select></label>
        <label class="field"><span>Find a machine, order or state</span><input class="input" id="machine-search" type="search" value="${esc(state.query)}" placeholder="Machine ID, name, order" autocomplete="off"></label>
      </section>
      <div class="machine-board-meta" role="status">Forge records read ${esc(readAt)} · ${state.schedule?.versionId ? `approved production schedule ${esc(state.schedule.versionId)}` : 'schedule context unavailable or not authorized'} · separate reads, not a real-time or atomic snapshot</div>
      ${!state.machines.length ? emptyBlock(state.errors.resources || !has(permissions, 'resources.read') ? 'Machine data is not available' : 'No machines available', state.errors.resources ? 'Machine resource data could not be read.' : !has(permissions, 'resources.read') ? 'Your role does not include resource read access.' : 'Set up active machines under Plant Setup.') : renderLanes(shown)}
      <section class="panel machine-board-legend"><b>Signal guide</b><span><i class="machine-signal-dot blocked" aria-hidden="true">■</i> Blocked / Down — a recorded outage, restriction, or assigned/scheduled operation failure</span><span><i class="machine-signal-dot attention" aria-hidden="true">!</i> Attention — overdue PM/inspection/calibration work or upcoming recorded downtime</span><span><i class="machine-signal-dot ready" aria-hidden="true">✓</i> Ready — an eligible operation passes Forge readiness</span><span><i class="machine-signal-dot unknown" aria-hidden="true">?</i> Unknown / Not verified — no current eligible operation or required read failed</span></section>`;

    $('#refresh-machine-board', page)?.addEventListener('click', () => { void load(); });
    $('#machine-center', page)?.addEventListener('change', event => { state.center = event.target.value; render(); });
    $('#machine-signal', page)?.addEventListener('change', event => { state.signal = event.target.value; render(); });
    $('#machine-search', page)?.addEventListener('input', event => { state.query = event.target.value; renderBoardOnly(); });
  }

  function renderBoardOnly() {
    const host = $('.machine-kanban-host', page);
    if (!host) return;
    host.outerHTML = renderLanes(visibleCards());
  }

  function renderLanes(list) {
    return `<section class="machine-kanban-host" aria-label="Machine readiness Kanban">${LANE_ORDER.map(key => {
      const items = list.filter(card => card.status === key);
      const lane = LANE[key];
      return `<section class="machine-lane ${key}" aria-labelledby="machine-lane-${key}">
        <header><h2 id="machine-lane-${key}"><span class="machine-signal-dot ${key}" aria-hidden="true">${lane.icon}</span>${esc(lane.title)}</h2><span class="pill ${lane.tone}">${items.length}</span></header>
        <div class="machine-lane-cards">${items.length ? items.map(card => cardHtml(card, lane)).join('') : '<div class="empty small">No machines in this signal group.</div>'}</div>
      </section>`;
    }).join('')}</section>`;
  }

  function cardHtml(card, lane) {
    const { machine, workCenter, currentEvaluation, nextScheduled, nextMaintenance } = card;
    const operation = currentEvaluation;
    const orderLink = operation ? `<a class="btn small" href="#/orders/${encodeURIComponent(operation.orderNumber)}">Order #${esc(operation.orderNumber)}</a>` : '';
    const staff = card.workforceHidden ? '<span class="muted">Staff details are restricted by your role.</span>'
      : card.workerNames.length ? card.workerNames.map(worker => `<span class="machine-staff-chip">${esc(worker.name)} · ${esc(worker.presence)}${worker.qualifications.length ? ` · ${worker.qualifications.map(esc).join(', ')}` : card.qualificationHidden ? ' · qualification details restricted' : ''}</span>`).join('')
        : '<span class="muted">No worker currently assigned directly to this machine.</span>';
    const due = nextMaintenance ? `${esc(nextMaintenance.title)}${nextMaintenance.dueDate ? ` · due ${esc(fmtDate(nextMaintenance.dueDate))}` : nextMaintenance.dueByRunHours ? ' · run-hour limit reached' : ''}` : 'No open PM/inspection/calibration work order found.';
    const operationDetail = operation ? `Order #${operation.orderNumber} · op ${operation.sequence} · ${operation.description ?? 'Operation'}${operation.partNumber ? ` · ${operation.partNumber}` : ''}` : 'No linked operation readiness result.';
    const upcoming = card.futureWindow ? `<div><b>Upcoming downtime</b><br>${esc(DOWN_LABEL[card.futureWindow.kind] ?? card.futureWindow.kind)} · ${esc(fmtDateTime(card.futureWindow.startsAt))} · ${esc(card.futureWindow.reason ?? '')}</div>` : '';
    return `<article class="machine-kanban-card ${card.status}" aria-label="${esc(machine.code)} ${esc(lane.title)}">
      <header><div><span class="machine-code">${esc(machine.code)}</span><h3>${esc(machine.name)}</h3></div><span class="machine-status ${lane.tone}"><span class="machine-signal-dot ${card.status}" aria-hidden="true">${lane.icon}</span><span>${esc(lane.title)}</span></span></header>
      <div class="machine-card-summary">${esc(card.summary)}</div>
      <dl class="machine-card-facts"><div><dt>Work center</dt><dd>${esc(workCenter?.name ?? machine.workCenterCode)} <span class="muted">${esc(machine.workCenterCode)}</span></dd></div>
        <div><dt>Current readiness context</dt><dd>${esc(operationDetail)}</dd></div>
        <div><dt>Assigned staff</dt><dd>${staff}</dd></div>
        <div><dt>Next maintenance record</dt><dd>${due}</dd></div></dl>
      ${card.blockers.length ? `<ul class="machine-blocker-list" aria-label="Current blockers">${card.blockers.map(blocker => `<li><b>${esc(blocker.source)}:</b> ${esc(blocker.text)}</li>`).join('')}</ul>` : ''}
      <details class="machine-card-more"><summary>Details and related work</summary><div class="machine-card-detail-body">
        ${operation?.dimensions?.length ? `<div><b>Readiness evidence</b><ul>${operation.dimensions.map(d => `<li><span class="pill ${d.outcome === 'Pass' ? 'ok' : ['Fail', 'Indeterminate'].includes(d.outcome) ? 'bad' : 'none'}">${esc(d.outcome)}</span> ${esc(d.name)} — ${esc(d.detail)}</li>`).join('')}</ul></div>` : ''}
        ${card.candidateBlockers.length ? `<div><b>Other open operation blockers</b><ul>${card.candidateBlockers.map(item => `<li><a href="#/orders/${encodeURIComponent(item.orderNumber)}">Order #${esc(item.orderNumber)} / op ${esc(item.sequence)}</a> — ${esc(item.why || item.primaryConstraint || 'Not executable')}</li>`).join('')}</ul></div>` : ''}
        ${nextScheduled ? `<div><b>Next approved operation</b><br><a href="#/orders/${encodeURIComponent(nextScheduled.orderNumber)}">Order #${esc(nextScheduled.orderNumber)} / op ${esc(nextScheduled.sequence)}</a> · ${esc(fmtDateTime(nextScheduled.start))}–${esc(fmtDateTime(nextScheduled.end))}</div>` : ''}
        ${upcoming}${card.overdueMaintenance.length ? `<div><b>Overdue maintenance</b><ul>${card.overdueMaintenance.map(item => `<li>WO ${esc(item.workOrderId)} · ${esc(item.title)}${item.dueDate ? ` · due ${esc(fmtDate(item.dueDate))}` : item.dueByRunHours ? ' · run-hour limit reached' : ''}</li>`).join('')}</ul><span class="muted">An overdue record is an attention signal. A hard lockout is shown only when Forge records it.</span></div>` : ''}
        ${card.maintenanceHidden ? '<div class="muted">Maintenance detail is hidden by your role; readiness checks still show Forge-evaluated blockers.</div>' : ''}
        ${card.asset?.status ? `<div><b>Production asset</b> ${esc(card.asset.assetCode)} · ${esc(card.asset.status)}</div>` : ''}
        ${card.error ? `<div>${errorBlock(card.error, 'machine readiness')}</div>` : ''}
        <div><b>Read at</b> ${esc(fmtDateTime(state.refreshedAt))}</div>
      </div></details>
      <footer class="machine-card-actions">${orderLink}<a class="btn small" href="#/work-centers/${encodeURIComponent(machine.workCenterCode)}">Work center</a>${!card.maintenanceHidden ? '<a class="btn small" href="#/maintenance?tab=machines">Maintenance</a>' : ''}</footer>
    </article>`;
  }

  render();
  return { dispose() {} };
}
