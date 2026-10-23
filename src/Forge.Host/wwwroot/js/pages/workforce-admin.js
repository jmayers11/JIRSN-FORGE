import { tryGet, command, readPost, removeVisualImage, uploadVisualImage } from '../api.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast } from '../ui.js';
import { formModal, canAction } from '../forms.js';

// Workforce administration (V3.9 commands that had no screen): register a worker at a home work center, set their
// shift, assign and end work, record presence, departure and unavailability, issue or revoke a badge, and manage
// qualifications (training, certification, suspension, revocation, prerequisites).

const STATUS_TONE = { InTraining: 'info', Qualified: 'ok', Suspended: 'warn', Revoked: 'bad' };
const local = value => value ? new Date(value).toISOString() : null;

/** The actions a signed-in person may take on a selected person, by permission. */
export function personActions(me) {
  const has = p => canAction(me, p);
  return [
    has('workforce.manage') && ['register', 'Register as worker'], has('workforce.manage') && ['shift', 'Set shift'],
    has('workforce.supervise') && ['assign', 'Assign work'], has('workforce.supervise') && ['end-assignment', 'End assignment'],
    has('workforce.supervise') && ['presence', 'Record presence'], has('workforce.supervise') && ['departure', 'Record departure'],
    has('workforce.supervise') && ['unavailable', 'Record unavailability'],
    has('workforce.badges.manage') && ['badge', 'Issue badge'], has('workforce.badges.manage') && ['revoke-badge', 'Revoke badge'],
    has('competency.manage') && ['training', 'Start training'], has('competency.certify') && ['certify', 'Certify'],
  ].filter(Boolean);
}

export function personActionsForWorker(me, worker, personId = me?.personId) {
  const actions = personActions(me);
  if (worker && canAction(me, 'workforce.manage')) actions.push(['job-title', 'Set official title']);
  if (!worker?.isActive) return actions;
  if (canAction(me, 'workforce.supervise')) actions.push(['move', 'Temporary move']);
  if (canAction(me, 'workforce.manage') && personId !== me?.personId) actions.push(['transfer', 'Permanent transfer']);
  return actions;
}

export function siteDate(now, timeZone) {
  const values = Object.fromEntries(new Intl.DateTimeFormat('en-US', {
    timeZone, year: 'numeric', month: '2-digit', day: '2-digit',
  }).formatToParts(now).map(part => [part.type, part.value]));
  return `${values.year}-${values.month}-${values.day}`;
}

const addDays = (date, days) => {
  const value = new Date(`${date}T00:00:00Z`);
  value.setUTCDate(value.getUTCDate() + days);
  return value.toISOString().slice(0, 10);
};

export function unavailabilityBody(v) {
  const startsAt = local(v.startsAt), endsAt = local(v.endsAt);
  if (!startsAt || !endsAt || endsAt <= startsAt) throw new Error('Enter a start and a later end.');
  return { kind: v.kind, startsAt, endsAt };
}

export function assignmentBody(v) {
  if (!v.safetyAreaCode && !v.workCenterCode && !v.machineCode) throw new Error('Name the safety area, work center or machine.');
  return { safetyAreaCode: v.safetyAreaCode, workCenterCode: v.workCenterCode, machineCode: v.machineCode, activityCode: v.activityCode, asTrainee: !!v.asTrainee };
}

export async function appendWorkforceAdmin(page, me, initialPersonId = null) {
  const section = document.createElement('section');
  section.className = 'panel';
  section.setAttribute('aria-label', 'Manage people');
  page.append(section);
  const canManagePeople = ['workforce.manage', 'workforce.supervise', 'workforce.badges.manage', 'competency.manage', 'competency.certify']
    .some(permission => canAction(me, permission));
  if (!canManagePeople) return section;

  const [people, typesResult, plant, workerResult] = await Promise.all([
    tryGet('/api/kernel/directory'), tryGet('/api/competency/qualification-types'), tryGet('/api/resources/'),
    canAction(me, 'workforce.read') ? tryGet('/api/workforce/workers') : Promise.resolve({ data: [] }),
  ]);
  let types = typesResult;
  const codes = key => (plant.data?.[key] ?? []).map(x => x.code);
  let workers = workerResult.data ?? [];
  const siteTimeZone = worker => {
    const home = plant.data?.workCenters?.find(center => center.code === worker?.homeWorkCenterCode);
    const area = plant.data?.safetyAreas?.find(candidate => candidate.code === home?.safetyAreaCode);
    return plant.data?.sites?.find(site => site.code === area?.siteCode)?.timeZone
      ?? plant.data?.sites?.[0]?.timeZone ?? 'UTC';
  };
  let selected = null, quals = null, moves = null, transfers = null, previewResult = null, previewError = null, previewBusy = false, previewSequence = 0;

  async function select(personId) {
    previewSequence++; previewResult = null; previewError = null; previewBusy = false;
    selected = (people.data ?? []).find(p => p.personId === personId) ?? null;
    const worker = workers.find(p => p.personId === selected?.personId);
    const [qualificationResult, moveResult, transferResult] = await Promise.all([
      selected ? tryGet(`/api/competency/people/${selected.personId}`) : Promise.resolve(null),
      selected && worker ? tryGet(`/api/workforce/moves?personId=${encodeURIComponent(selected.personId)}&includeClosed=true`) : Promise.resolve(null),
      selected && worker ? tryGet(`/api/workforce/transfers?personId=${encodeURIComponent(selected.personId)}`) : Promise.resolve(null),
    ]);
    quals = qualificationResult;
    moves = moveResult;
    transfers = transferResult;
    render();
  }

  function render() {
    const list = people.data ?? [];
    const worker = workers.find(p => p.personId === selected?.personId) ?? null;
    const actions = personActionsForWorker(me, worker, selected?.personId);
    section.innerHTML = `<header><h2>Manage people</h2><div class="spacer"></div>
        ${canAction(me, 'competency.manage') ? '<button class="btn small ghost" id="prerequisite">Add qualification prerequisite</button>' : ''}</header>
      ${people.error ? errorBlock(people.error, 'people') : !list.length ? emptyBlock('Nobody registered', 'Register people under Access Control.') : `
      <div class="toolbar"><label for="wf-person">Person</label><select class="select" id="wf-person"><option value="">— choose —</option>
        ${list.map(p => `<option value="${p.personId}" ${selected?.personId === p.personId ? 'selected' : ''}>${esc(p.displayName)}</option>`).join('')}</select></div>
      ${selected ? `<div class="actions">${actions.map(([a, text]) => `<button class="btn small" data-wf="${a}">${esc(text)}</button>`).join('')}</div>
        ${worker ? workerPhotoEditor(worker) : ''}
        ${worker ? movementHistory(worker) : ''}
        ${canAction(me, 'readiness.read') ? `<section class="panel" aria-label="Assignment impact preview">
          <h3>Assignment impact preview</h3><p class="muted">Compare active work before and after a proposed assignment. This preview never changes the assignment.</p>
          <div class="form-grid">
            <label class="field"><span>Safety area</span><select class="input" data-preview-field="safetyAreaCode"><option value="">— none —</option>${codes('safetyAreas').map(code => `<option value="${esc(code)}">${esc(code)}</option>`).join('')}</select></label>
            <label class="field"><span>Work center</span><select class="input" data-preview-field="workCenterCode"><option value="">— none —</option>${codes('workCenters').map(code => `<option value="${esc(code)}">${esc(code)}</option>`).join('')}</select></label>
            <label class="field"><span>Machine</span><select class="input" data-preview-field="machineCode"><option value="">— none —</option>${codes('machines').map(code => `<option value="${esc(code)}">${esc(code)}</option>`).join('')}</select></label>
            <label class="field"><span>Activity</span><select class="input" data-preview-field="activityCode"><option value="">— choose —</option>${codes('activityTypes').map(code => `<option value="${esc(code)}">${esc(code)}</option>`).join('')}</select></label>
            <label class="field"><span><input type="checkbox" data-preview-field="asTrainee"> Proposed as trainee</span></label>
          </div>
          <button class="btn small" data-reallocation-preview ${previewBusy ? 'disabled' : ''}>${previewBusy ? 'Evaluating…' : 'Preview impact'}</button>
          ${previewError ? errorBlock(previewError, 'assignment impact preview') : previewResult ? previewHtml(previewResult) : ''}
        </section>` : ''}
        <h3>Qualifications</h3>${quals?.error ? errorBlock(quals.error, 'qualifications') : !(quals?.data ?? []).length ? '<div class="muted">No qualifications recorded.</div>' : `
        <table class="table"><thead><tr><th>Qualification</th><th>Status</th><th>Certified</th><th>Expires</th><th></th></tr></thead><tbody>
        ${quals.data.map(q => `<tr><td>${esc(typeName(q.code))}</td><td><span class="pill ${STATUS_TONE[q.status] ?? 'none'}">${esc(q.status.replace(/([a-z])([A-Z])/g, '$1 $2'))}</span></td>
          <td>${q.certifiedAt ? esc(fmtDateTime(q.certifiedAt)) : '—'}</td><td>${q.expiresAt ? esc(fmtDateTime(q.expiresAt)) : '—'}</td>
          <td class="row-actions">${canAction(me, 'competency.certify') && q.status === 'InTraining' ? `<button class="btn small" data-qual="certify" data-code="${esc(q.code)}">Certify</button>` : ''}
            ${canAction(me, 'competency.certify') && q.status === 'Qualified' ? `<button class="btn small ghost" data-qual="suspensions" data-code="${esc(q.code)}">Suspend</button>` : ''}
            ${canAction(me, 'competency.certify') && (q.status === 'Qualified' || q.status === 'Suspended') ? `<button class="btn small ghost" data-qual="revocations" data-code="${esc(q.code)}">Revoke</button>` : ''}</td></tr>`).join('')}
        </tbody></table>`}` : '<div class="muted">Choose a person to manage their work, presence, badge and qualifications.</div>'}`}`;
    $('#wf-person', section)?.addEventListener('change', e => select(e.target.value));
    $('#prerequisite', section)?.addEventListener('click', prerequisite);
    $('[data-reallocation-preview]', section)?.addEventListener('click', previewAssignment);
    $$('[data-wf]', section).forEach(b => b.addEventListener('click', () => run(b.dataset.wf)));
    $('#wf-photo-file', section)?.addEventListener('change', async event => {
      const file = event.target.files?.[0];
      if (!file || !selected) return;
      const personId = selected.personId;
      try {
        const result = await confirmWithReason({ title: `Upload photo for ${selected.displayName}`, detail: 'The photo is visible to people who can read the workforce board. Use a clear, current headshot.', confirmLabel: 'Upload photo', reasonRequired: true }, reason => uploadVisualImage(`/api/workforce/workers/${encodeURIComponent(personId)}/photo`, file, reason));
        if (result) await after();
      } catch (error) { toast(error.message || 'Photo upload failed.', true); }
      finally { event.target.value = ''; }
    });
    $('[data-remove-worker-photo]', section)?.addEventListener('click', async () => {
      if (!selected) return;
      const personId = selected.personId;
      try {
        const result = await confirmWithReason({ title: `Remove photo for ${selected.displayName}`, detail: 'The worker will show initials on the Kanban.', confirmLabel: 'Remove photo', reasonRequired: true }, reason => removeVisualImage(`/api/workforce/workers/${encodeURIComponent(personId)}/photo`, reason));
        if (result) await after();
      } catch (error) { toast(error.message || 'Photo removal failed.', true); }
    });
    $$('[data-move-action]', section).forEach(b => b.addEventListener('click', () => changeMove(b.dataset.moveAction, Number(b.dataset.moveId))));
    $$('[data-qual]', section).forEach(b => b.addEventListener('click', () => b.dataset.qual === 'certify' ? certify(b.dataset.code) : qualification(b.dataset.qual, b.dataset.code)));
  }

  function movementHistory(worker) {
    const moveRows = moves?.data ?? [];
    const transferRows = transfers?.data ?? [];
    return `<section class="panel" aria-label="Moves and transfers">
      <header><h3>Moves and transfers</h3><span class="spacer"></span><span class="muted">Home work center: ${esc(worker.homeWorkCenterCode)}</span></header>
      ${moves?.error ? errorBlock(moves.error, 'temporary moves') : moveRows.length ? `<div class="table-wrap"><table class="table"><thead><tr><th>Temporary move</th><th>Dates</th><th>Status and changes</th><th></th></tr></thead><tbody>
        ${moveRows.map(move => `<tr><td><b>${esc(move.fromWorkCenter)} → ${esc(move.toWorkCenter)}</b><div class="faint">Move ${move.moveId}</div></td>
          <td>${esc(move.startsOn)} – ${esc(move.endsOn)}</td><td><span class="pill ${move.status === 'Active' ? 'info' : 'none'}">${esc(move.status)}</span>${(move.changes ?? []).map(change => `<div class="faint">${esc(change.kind)}: ${esc(change.previousEndsOn)} → ${esc(change.endsOn)} · ${esc(fmtDateTime(change.changedAt))}</div>`).join('')}</td>
          <td class="row-actions">${move.status === 'Active' && canAction(me, 'workforce.supervise') ? `<button class="btn small" data-move-action="extend" data-move-id="${move.moveId}">Extend</button><button class="btn small ghost" data-move-action="end" data-move-id="${move.moveId}">End early</button>` : ''}
            ${move.status !== 'Active' && canAction(me, 'workforce.supervise') ? `<button class="btn small" data-move-action="renew" data-move-id="${move.moveId}">Renew</button>` : ''}</td></tr>`).join('')}</tbody></table></div>` : emptyBlock('No temporary moves', 'Temporary moves count the worker at another work center through the estimated end date; they do not change the worker’s shift.')}
      ${transfers?.error ? errorBlock(transfers.error, 'transfers') : transferRows.length ? `<h4>Permanent transfer history</h4><div class="table-wrap"><table class="table"><thead><tr><th>From</th><th>To</th><th>Effective from</th><th>Recorded</th></tr></thead><tbody>
        ${transferRows.map(transfer => `<tr><td>${esc(transfer.fromWorkCenter)}</td><td>${esc(transfer.workCenter)}</td><td>${esc(transfer.effectiveFrom)}</td><td>${esc(fmtDateTime(transfer.recordedAt))} · ${esc(transfer.recordedBy)}</td></tr>`).join('')}</tbody></table></div>` : '<div class="muted" style="margin-top:8px">No permanent transfers recorded.</div>'}
      <div class="faint" style="margin-top:8px">Each change requires a reason and is recorded in the audit history. Temporary-move reminders and returns are handled automatically.</div>
    </section>`;
  }

  function workerPhotoEditor(worker) {
    if (!canAction(me, 'workforce.photos.manage')) return '';
    return `<section class="workforce-photo-editor" aria-label="Worker photo"><div><b>Worker photo</b><div class="muted small">${worker.hasPhoto ? 'A profile photo is shown on the plant floor.' : 'No photo yet; the floor board shows initials.'}</div></div><div class="workforce-photo-actions"><label class="btn small">${worker.hasPhoto ? 'Replace photo' : 'Add photo'}<input id="wf-photo-file" type="file" accept="image/png,image/jpeg,image/webp" aria-label="Choose a worker photo"></label>${worker.hasPhoto ? '<button class="btn small" type="button" data-remove-worker-photo>Remove photo</button>' : ''}</div></section>`;
  }

  const typeName = code => (types.data ?? []).find(t => t.code === code)?.name ?? code;
  const typeOptions = () => (types.data ?? []).map(t => [t.code, `${t.name} (${t.code})`]);
  const workCenterOptions = () => (plant.data?.workCenters ?? []).filter(w => w.active !== false).map(w => [w.code, `${w.name} (${w.code})`]);
  const base = () => `/api/workforce/workers/${selected.personId}`;
  const after = async () => {
    if (canAction(me, 'workforce.read')) {
      const currentWorkers = await tryGet('/api/workforce/workers');
      if (!currentWorkers.error) workers = currentWorkers.data ?? [];
    }
    return select(selected.personId);
  };
  const simple = (title, detail, path, body = {}) => formModal({ title, detail, fields: [], confirmLabel: title, reasonRequired: true }, (_, reason) => command(path, body, reason), after);

  function previewHtml(run) {
    const result = run.output, impacts = result.impacts ?? [];
    const workName = work => [work.safetyAreaCode, work.workCenterCode, work.machineCode, work.activityCode].filter(Boolean).join(' · ') || 'Unspecified work';
    return `<div class="notice info" data-preview-result>Preview only: no assignment was changed. ${esc(result.personName)} has ${impacts.length} projected active-work impact${impacts.length === 1 ? '' : 's'}.</div>
      ${impacts.length ? `<table class="table"><thead><tr><th>Impact</th><th>Work</th><th>Constraint</th><th>Effect</th></tr></thead><tbody>${impacts.map(i => `<tr><td>${esc(i.kind)}</td><td>${esc(workName(i.work))}</td><td>${esc(i.constraint ?? '—')}</td><td>${esc(i.detail)}</td></tr>`).join('')}</tbody></table>` : '<div class="muted">No active work changes in this preview.</div>'}
      ${(result.options ?? []).length ? `<h4>Possible responses</h4><ul>${result.options.map(o => `<li>${esc(o.description)}${o.consequences?.length ? ` — ${esc(o.consequences.join('; '))}` : ''}</li>`).join('')}</ul>` : ''}`;
  }

  async function previewAssignment() {
    if (!selected || previewBusy) return;
    const value = name => $('[data-preview-field="' + name + '"]', section);
    let body;
    try {
      const activityCode = value('activityCode').value;
      if (!activityCode) throw new Error('Choose an activity for the preview.');
      body = assignmentBody({ safetyAreaCode: value('safetyAreaCode').value, workCenterCode: value('workCenterCode').value,
        machineCode: value('machineCode').value, activityCode, asTrainee: value('asTrainee').checked });
    } catch (error) { previewError = error; render(); return; }
    const personId = selected.personId, sequence = ++previewSequence;
    previewBusy = true; previewError = null; previewResult = null; render();
    try {
      const result = await readPost(`/api/readiness/reallocation-preview/${personId}`, body);
      if (sequence !== previewSequence || selected?.personId !== personId) return;
      previewResult = result; previewBusy = false;
    } catch (error) {
      if (sequence !== previewSequence || selected?.personId !== personId) return;
      previewError = error; previewBusy = false;
    }
    render();
  }

  function run(action) {
    const name = selected.displayName;
    switch (action) {
      case 'register': return formModal({ title: `Register ${name} as a worker`, fields: [{ name: 'homeWorkCenterCode', label: 'Home work center', code: true, required: true, list: codes('workCenters') }], confirmLabel: 'Register', reasonRequired: true },
        (v, reason) => command(base(), v, reason), after);
      case 'shift': return formModal({ title: `Set ${name}'s shift`, fields: [{ name: 'shiftCode', label: 'Shift (blank: none)', code: true, list: codes('shifts') }], confirmLabel: 'Save', reasonRequired: true },
        (v, reason) => command(`${base()}/shift`, { shiftCode: v.shiftCode }, reason), after);
      case 'job-title': {
        const worker = workers.find(person => person.personId === selected.personId);
        return formModal({ title: `Set ${name}'s official title`, detail: 'This title is displayed beneath their name on the Workforce Kanban. Leave it blank to clear it.',
          fields: [{ name: 'jobTitle', label: 'Official title', value: worker?.jobTitle ?? '', placeholder: 'Production Operator' }], confirmLabel: 'Save title', reasonRequired: true },
        (v, reason) => command(`${base()}/job-title`, { jobTitle: v.jobTitle || null }, reason), after);
      }
      case 'assign': return formModal({ title: `Assign work to ${name}`, detail: 'Readiness checks the assignment against qualifications and safety rules.',
        fields: [{ name: 'safetyAreaCode', label: 'Safety area', code: true, list: codes('safetyAreas') }, { name: 'workCenterCode', label: 'Work center', code: true, list: codes('workCenters') },
          { name: 'machineCode', label: 'Machine', code: true, list: codes('machines') }, { name: 'activityCode', label: 'Activity', code: true, required: true, list: codes('activityTypes') },
          { name: 'asTrainee', label: 'Trainee', type: 'check', text: 'Assigned as a trainee' }], confirmLabel: 'Assign', reasonRequired: true },
        (v, reason) => command(`${base()}/assignment`, assignmentBody(v), reason), after);
      case 'end-assignment': return simple('End assignment', `${name} stops the assigned work.`, `${base()}/assignment/end`);
      case 'presence': return formModal({ title: `Record ${name}'s presence`, detail: 'Only clocked-in or supervisor-confirmed presence counts. A projection is never presence.',
        fields: [{ name: 'kind', label: 'Basis', type: 'select', options: [['SupervisorConfirmed', 'Supervisor confirmed on site'], ['ClockedIn', 'Clocked in'], ['Projected', 'Projected (planning only)']] },
          { name: 'validUntil', label: 'Valid until (optional)', type: 'datetime-local' }], confirmLabel: 'Record', reasonRequired: true },
        (v, reason) => command(`${base()}/presence`, { kind: v.kind, validUntil: local(v.validUntil) }, reason), after);
      case 'departure': return simple('Record departure', `${name} has left the site.`, `${base()}/departure`);
      case 'unavailable': return formModal({ title: `Record ${name} unavailable`, fields: [{ name: 'kind', label: 'Kind', type: 'select', options: [['CallOut', 'Call-out'], ['Unavailable', 'Unavailable'], ['ApprovedPto', 'Approved time off'], ['Training', 'Training']] },
          { name: 'startsAt', label: 'From', type: 'datetime-local', required: true }, { name: 'endsAt', label: 'To', type: 'datetime-local', required: true }], confirmLabel: 'Record', reasonRequired: true },
        (v, reason) => command(`${base()}/unavailability`, unavailabilityBody(v), reason), after);
      case 'move': {
        const worker = workers.find(person => person.personId === selected.personId);
        const timeZone = siteTimeZone(worker);
        const today = siteDate(new Date(), timeZone);
        return formModal({ title: `Temporarily move ${name}`, detail: 'This is a time-limited loan to another work center. The estimated end date is required; shift assignment stays separate.', confirmLabel: 'Start move', reasonRequired: true,
          fields: [
            { name: 'workCenterCode', label: 'Destination work center', type: 'select', options: workCenterOptions(), required: true },
            { name: 'startsOn', label: 'Starts on (site date)', type: 'date', value: today, required: true },
            { name: 'estimatedEndOn', label: 'Estimated end (site date)', type: 'date', value: addDays(today, 14), required: true },
          ] }, (v, reason) => command(`${base()}/moves`, v, reason), after);
      }
      case 'transfer': {
        const worker = workers.find(person => person.personId === selected.personId);
        const timeZone = siteTimeZone(worker);
        const today = siteDate(new Date(), timeZone);
        return formModal({ title: `Permanently transfer ${name}`, detail: `Changes the home work center from the effective site date. Past capacity and labor history stay in the original work center. Current home: ${worker?.homeWorkCenterCode ?? 'unknown'}.`, confirmLabel: 'Transfer', reasonRequired: true,
          fields: [
            { name: 'workCenterCode', label: 'New home work center', type: 'select', options: workCenterOptions(), required: true },
            { name: 'effectiveFrom', label: 'Effective from (site date)', type: 'date', value: today, required: true },
          ] }, (v, reason) => command(`${base()}/transfer`, v, reason), after);
      }
      case 'badge': return formModal({ title: `Issue a badge to ${name}`, detail: 'The badge number is a secret; it is not shown again.', fields: [{ name: 'badge', label: 'Badge number', type: 'password', required: true }], confirmLabel: 'Issue', reasonRequired: true },
        (v, reason) => command(`/api/workforce/people/${selected.personId}/badge`, { badge: v.badge }, reason), after);
      case 'revoke-badge': return simple('Revoke badge', `${name}'s badge stops working at terminals.`, `/api/workforce/people/${selected.personId}/badge/revoke`);
      case 'training': return formModal({ title: `Start ${name}'s training`, fields: [{ name: 'code', label: 'Qualification', type: 'select', required: true, blank: '— choose —', options: typeOptions() }], confirmLabel: 'Start training', reasonRequired: true },
        (v, reason) => command(`/api/competency/people/${selected.personId}/training`, { code: v.code }, reason), after);
      case 'certify': return certify(null);
      default: return null;
    }
  }

  function changeMove(action, moveId) {
    const move = (moves?.data ?? []).find(item => item.moveId === moveId);
    if (!move) return;
    const today = siteDate(new Date(), siteTimeZone(workers.find(person => person.personId === selected.personId)));
    if (action === 'end') return simple(`End move ${moveId} early`, 'The worker returns to their home work center from today.', `/api/workforce/moves/${moveId}/end`);
    const renew = action === 'renew';
    const defaultEnd = renew ? addDays(today, 14) : addDays(move.endsOn, 7);
    return formModal({ title: `${renew ? 'Renew' : 'Extend'} ${move.displayName}'s move`, detail: renew
      ? `Starts a new move to ${move.toWorkCenter} from today; the days between moves remain at home.`
      : `The move to ${move.toWorkCenter} remains active through its new inclusive end date.`,
    confirmLabel: renew ? 'Renew' : 'Extend', reasonRequired: true,
    fields: [{ name: 'newEndOn', label: 'New estimated end (site date)', type: 'date', value: defaultEnd, required: true }] },
    (v, reason) => command(`/api/workforce/moves/${moveId}/${renew ? 'renew' : 'extend'}`, v, reason), after);
  }

  function certify(code) {
    formModal({ title: `Certify ${selected.displayName}`, detail: 'Certification needs evidence. Prerequisites must already be held.',
      fields: [{ name: 'code', label: 'Qualification', type: 'select', required: true, blank: '— choose —', value: code ?? '', options: typeOptions() }, { name: 'evidence', label: 'Evidence', type: 'textarea', required: true }], confirmLabel: 'Certify', reasonRequired: true },
    (v, reason) => command(`/api/competency/people/${selected.personId}/certifications`, { code: v.code, evidence: v.evidence }, reason), after);
  }

  function qualification(kind, code) {
    const word = kind === 'suspensions' ? 'Suspend' : 'Revoke';
    formModal({ title: `${word} ${typeName(code)} for ${selected.displayName}`, detail: kind === 'suspensions' ? 'They cannot do work needing it until it is reinstated by certification.' : 'Revocation is final; a new certification is needed.',
      fields: [], confirmLabel: word, reasonRequired: true },
    (_, reason) => command(`/api/competency/people/${selected.personId}/${kind}`, { code }, reason), after);
  }

  function prerequisite() {
    formModal({ title: 'Add qualification prerequisite', detail: 'A person must hold the prerequisite before being certified for the qualification.',
      fields: [{ name: 'code', label: 'Qualification', type: 'select', required: true, blank: '— choose —', options: typeOptions() }, { name: 'prerequisiteCode', label: 'Requires', type: 'select', required: true, blank: '— choose —', options: typeOptions() }],
      confirmLabel: 'Add', reasonRequired: true },
    (v, reason) => command(`/api/competency/qualification-types/${encodeURIComponent(v.code)}/prerequisites`, { prerequisiteCode: v.prerequisiteCode }, reason), async () => { types = await tryGet('/api/competency/qualification-types'); render(); });
  }

  if (initialPersonId && (people.data ?? []).some(person => person.personId === initialPersonId)) {
    await select(initialPersonId);
    section.scrollIntoView?.({ behavior: 'smooth', block: 'start' });
  } else {
    render();
  }
  return section;
}
