import { tryGet, command, readPost } from '../api.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock } from '../ui.js';
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

export function unavailabilityBody(v) {
  const startsAt = local(v.startsAt), endsAt = local(v.endsAt);
  if (!startsAt || !endsAt || endsAt <= startsAt) throw new Error('Enter a start and a later end.');
  return { kind: v.kind, startsAt, endsAt };
}

export function assignmentBody(v) {
  if (!v.safetyAreaCode && !v.workCenterCode && !v.machineCode) throw new Error('Name the safety area, work center or machine.');
  return { safetyAreaCode: v.safetyAreaCode, workCenterCode: v.workCenterCode, machineCode: v.machineCode, activityCode: v.activityCode, asTrainee: !!v.asTrainee };
}

export async function appendWorkforceAdmin(page, me) {
  const actions = personActions(me);
  const section = document.createElement('section');
  section.className = 'panel';
  section.setAttribute('aria-label', 'Manage people');
  page.append(section);
  if (!actions.length && !canAction(me, 'competency.certify')) return section;

  const [people, typesResult, plant] = await Promise.all([tryGet('/api/kernel/directory'), tryGet('/api/competency/qualification-types'), tryGet('/api/resources/')]);
  let types = typesResult;
  const codes = key => (plant.data?.[key] ?? []).map(x => x.code);
  let selected = null, quals = null, previewResult = null, previewError = null, previewBusy = false, previewSequence = 0;

  async function select(personId) {
    previewSequence++; previewResult = null; previewError = null; previewBusy = false;
    selected = (people.data ?? []).find(p => p.personId === personId) ?? null;
    quals = selected ? await tryGet(`/api/competency/people/${selected.personId}`) : null;
    render();
  }

  function render() {
    const list = people.data ?? [];
    section.innerHTML = `<header><h2>Manage people</h2><div class="spacer"></div>
        ${canAction(me, 'competency.manage') ? '<button class="btn small ghost" id="prerequisite">Add qualification prerequisite</button>' : ''}</header>
      ${people.error ? errorBlock(people.error, 'people') : !list.length ? emptyBlock('Nobody registered', 'Register people under Access Control.') : `
      <div class="toolbar"><label for="wf-person">Person</label><select class="select" id="wf-person"><option value="">— choose —</option>
        ${list.map(p => `<option value="${p.personId}" ${selected?.personId === p.personId ? 'selected' : ''}>${esc(p.displayName)}</option>`).join('')}</select></div>
      ${selected ? `<div class="actions">${actions.map(([a, text]) => `<button class="btn small" data-wf="${a}">${esc(text)}</button>`).join('')}</div>
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
    $$('[data-qual]', section).forEach(b => b.addEventListener('click', () => b.dataset.qual === 'certify' ? certify(b.dataset.code) : qualification(b.dataset.qual, b.dataset.code)));
  }

  const typeName = code => (types.data ?? []).find(t => t.code === code)?.name ?? code;
  const typeOptions = () => (types.data ?? []).map(t => [t.code, `${t.name} (${t.code})`]);
  const base = () => `/api/workforce/workers/${selected.personId}`;
  const after = () => select(selected.personId);
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
      case 'badge': return formModal({ title: `Issue a badge to ${name}`, detail: 'The badge number is a secret; it is not shown again.', fields: [{ name: 'badge', label: 'Badge number', type: 'password', required: true }], confirmLabel: 'Issue', reasonRequired: true },
        (v, reason) => command(`/api/workforce/people/${selected.personId}/badge`, { badge: v.badge }, reason), after);
      case 'revoke-badge': return simple('Revoke badge', `${name}'s badge stops working at terminals.`, `/api/workforce/people/${selected.personId}/badge/revoke`);
      case 'training': return formModal({ title: `Start ${name}'s training`, fields: [{ name: 'code', label: 'Qualification', type: 'select', required: true, blank: '— choose —', options: typeOptions() }], confirmLabel: 'Start training', reasonRequired: true },
        (v, reason) => command(`/api/competency/people/${selected.personId}/training`, { code: v.code }, reason), after);
      case 'certify': return certify(null);
      default: return null;
    }
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

  render();
  return section;
}
