import { command, tryGet, session, uuid } from '../api.js';
import { esc, $, $$, errorBlock, toast } from '../ui.js';

const ROOT = '/api/quality/qms/review', enc = encodeURIComponent;
const TABS = ['programs', 'runs', 'mappings', 'editions'];
const TAB_LABEL = { programs: 'Review programs', runs: 'Management reviews', mappings: 'Evidence assessments', editions: 'Edition assessments' };
const LABEL = value => String(value ?? '').replace(/([a-z])([A-Z])/g, '$1 $2');
const permission = (me, name) => (me?.permissions ?? []).includes(name);
const rowsFor = (tab, data) => data?.items ?? [];
const idFor = (tab, row) => tab === 'programs' ? row.code : row.id;
const stateFor = (tab, row) => tab === 'programs' ? row.revisions?.at(-1)?.state : row.state;
const detailHtml = value => {
  if (value == null || value === '') return '<span class="faint">Not recorded</span>';
  if (Array.isArray(value)) return value.length ? '<ul>' + value.map(detailHtml).map(x => '<li>' + x + '</li>').join('') + '</ul>' : '<span class="faint">No entries</span>';
  if (typeof value === 'object') return '<dl>' + Object.entries(value).map(([name, item]) => '<dt>' + esc(LABEL(name)) + '</dt><dd>' + detailHtml(item) + '</dd>').join('') + '</dl>';
  return '<span>' + esc(value) + '</span>';
};
const detailPath = (tab, id) => tab === 'programs' ? ROOT + '/definitions/' + enc(id)
  : tab === 'runs' ? ROOT + '/runs/' + enc(id)
    : ROOT + (tab === 'mappings' ? '/mappings/assessments/' : '/editions/assessments/') + enc(id);
const listPath = tab => ROOT + ({ programs: '/definitions', runs: '/runs', mappings: '/mappings/assessments', editions: '/editions/assessments' }[tab]);

export async function qmsReviewPage(page, ctx) {
  const auth = { subject: session.subject, token: session.token };
  let disposed = false, generation = 0;
  let tab = TABS.includes(ctx.query?.get('tab')) ? ctx.query.get('tab') : 'programs';
  let data = {}, error = null, selected = null, actions = [], cursor = null;
  const live = token => !disposed && page.isConnected && generation === token && session.subject === auth.subject && session.token === auth.token;
  async function load(after = null) {
    const token = ++generation; selected = null; actions = []; error = null; cursor = after;
    page.innerHTML = '<div class="muted">Loading authorized quality review records…</div>';
    const response = await tryGet(listPath(tab) + '?take=30' + (after ? '&after=' + enc(after) : ''));
    if (!live(token)) return;
    if (response.error) { error = response.error; data = null; }
    else { data = response.data; error = null; }
    render();
  }
  async function open(id) {
    const token = ++generation; selected = null; actions = []; render();
    const result = await tryGet(detailPath(tab, id));
    if (!live(token)) return;
    if (result.error) { error = result.error; selected = null; render(); return; }
    selected = result.data; error = null;
    if (tab === 'runs') {
      const resultActions = await tryGet(ROOT + '/runs/' + enc(id) + '/actions');
      if (!live(token)) return;
      if (!resultActions.error) actions = resultActions.data;
    }
    render();
  }
  function modal(title, content, onSubmit) {
    const scrim = document.createElement('div'), box = document.createElement('div');
    scrim.className = 'scrim'; scrim.setAttribute('aria-hidden', 'true'); box.className = 'modal';
    box.innerHTML = '<h2>' + esc(title) + '</h2>' + content + '<label class="field"><span>Reason recorded in the audit trail</span><textarea class="input" data-reason required></textarea></label><div class="notice bad hidden" role="alert" data-error></div><div class="actions"><button class="btn" data-cancel>Cancel</button><button class="btn primary" data-confirm>Record decision</button></div>';
    document.body.append(scrim, box); let closed = false, busy = false;
    const close = () => { if (closed) return; closed = true; scrim.remove(); box.remove(); };
    scrim.onclick = close; $('[data-cancel]', box).onclick = close;
    $('[data-confirm]', box).onclick = async () => {
      if (closed || busy) return; busy = true; const btn = $('[data-confirm]', box), err = $('[data-error]', box); btn.disabled = true; err.classList.add('hidden');
      try { await onSubmit(box, live(generation)); close(); toast('The review record was updated.'); await load(); }
      catch (e) { err.textContent = e.message; err.classList.remove('hidden'); btn.disabled = false; busy = false; }
    };
    $('[data-reason]', box).focus();
  }
  const formField = (name, label, type = 'text', value = '') => '<label class="field"><span>' + esc(label) + '</span>' + (type === 'textarea'
    ? '<textarea class="input" data-field="' + name + '">' + esc(value) + '</textarea>'
    : '<input class="input" data-field="' + name + '" type="' + type + '" value="' + esc(value) + '">') + '</label>';
  const value = (box, name) => $('[data-field="' + name + '"]', box)?.value?.trim() ?? '';
  const reason = box => $('[data-reason]', box).value.trim();
  const runAction = (action, run) => modal(LABEL(action) + ' management review',
    '<p class="muted">Current version ' + run.version + '. The server rechecks source access and approval authority when the command is recorded.</p>' + formField('note', 'Human decision basis', 'textarea'),
    async (box, current) => { if (!current || !reason(box) || !value(box, 'note')) throw new Error('A current session, reason and human decision basis are required.');
      await command(ROOT + '/runs/' + enc(run.id) + '/' + action.toLowerCase(), { data: { expectedVersion: run.version, note: value(box, 'note') } }, reason(box)); });
  const freeze = run => modal('Freeze actual review inputs',
    '<p class="muted">This captures the exact approved program, owner values, metric cohort, database snapshot and cutoff once. Later changes require a new review.</p>',
    async (box, current) => { if (!current || !reason(box)) throw new Error('A current session and reason are required.'); await command(ROOT + '/runs/' + enc(run.id) + '/freeze', { data: { expectedVersion: run.version } }, reason(box)); });
  const createRun = definition => modal('Start a management review',
    '<p class="muted">Uses the currently selected approved program revision. Period end must be a closed period.</p>' + formField('from', 'Period starts (UTC)', 'datetime-local') + formField('to', 'Period ends (UTC)', 'datetime-local'),
    async (box, current) => { const from = value(box, 'from'), to = value(box, 'to');
      if (!current || !from || !to || !reason(box)) throw new Error('Choose a closed period and provide a reason.');
      const revision = definition.revisions?.find(x => x.state === 'Effective') ?? definition.revisions?.at(-1);
      if (!revision) throw new Error('This program has no controlled revision.');
      await command(ROOT + '/runs', { data: { id: uuid(), programCode: definition.code, programRevision: revision.revision, periodFrom: new Date(from + ':00Z').toISOString(), periodTo: new Date(to + ':00Z').toISOString() } }, reason(box)); });
  const createEdition = () => modal('Record an edition mapping proposal',
    '<p class="muted">Record the organization’s human assessment of exact criteria. Licensed text remains pending until an authorized copy is verified; this workflow does not declare conformity.</p>' +
      formField('facility', 'Facility code') + formField('standard', 'Standard') + formField('fromEdition', 'From edition') + formField('toEdition', 'To edition') +
      formField('fromCriteria', 'Exact source criteria, one per line (blank only for a new requirement)', 'textarea') +
      formField('toCriteria', 'Exact target criteria, one per line (blank only for a removed requirement)', 'textarea') +
      formField('relationship', 'Relationship: Direct, Split, Combined, Changed, New, Removed, or Unresolved') +
      formField('basis', 'Human basis', 'textarea') + formField('changes', 'Observed changes', 'textarea') +
      formField('applicability', 'Applicability impact', 'textarea') + formField('requiredActions', 'Required actions', 'textarea') +
      formField('auditProgram', 'Effective audit program code') + formField('auditProgramRevision', 'Audit program revision', 'number', '1') +
      formField('impartiality', 'Auditor impartiality basis', 'textarea'),
    async (box, current) => {
      const fields = ['facility', 'standard', 'fromEdition', 'toEdition', 'relationship', 'basis', 'changes', 'applicability', 'requiredActions', 'auditProgram', 'auditProgramRevision', 'impartiality'];
      if (!current || !ctx.me?.personId || !reason(box) || fields.some(name => !value(box, name))) throw new Error('Complete the exact editions, human basis, required actions, qualified audit program and impartiality basis.');
      const clauses = name => value(box, name).split(/\r?\n|;/).map(item => item.trim()).filter(Boolean);
      const criterion = (edition, clause) => ({ standard: value(box, 'standard'), edition, clause, verification: 'PendingAuthorizedVerification', verificationBasis: 'Authorized licensed source has not been verified.', verificationDocument: null });
      const relationship = value(box, 'relationship');
      if (!['Direct', 'Split', 'Combined', 'Changed', 'New', 'Removed', 'Unresolved'].includes(relationship)) throw new Error('Choose one of the listed relationship values.');
      const fromEdition = value(box, 'fromEdition'), toEdition = value(box, 'toEdition');
      await command(ROOT + '/editions/assessments', { data: {
        id: uuid(), facilityCode: value(box, 'facility'), programId: null, markings: 'None', standard: value(box, 'standard'), fromEdition, toEdition,
        fromCriteria: clauses('fromCriteria').map(clause => criterion(fromEdition, clause)),
        toCriteria: clauses('toCriteria').map(clause => criterion(toEdition, clause)), relationship,
        basis: value(box, 'basis'), changes: value(box, 'changes'), applicabilityImpact: value(box, 'applicability'), requiredActions: value(box, 'requiredActions'),
        auditorValidation: { personId: ctx.me.personId, asOf: new Date().toISOString(), auditProgram: { code: value(box, 'auditProgram'), revision: Number(value(box, 'auditProgramRevision')) }, competenceAssessmentId: null, impartialityBasis: value(box, 'impartiality') },
        evidence: []
      } }, reason(box));
    });
  const editionAction = (action, assessment) => modal(LABEL(action) + ' edition assessment',
    '<p class="muted">Current version ' + assessment.version + '. The server rechecks auditor competence, independence and current source access when recording this decision.</p>' +
      formField('note', 'Human decision basis', 'textarea') + formField('impartiality', 'Current auditor impartiality basis', 'textarea', assessment.auditorValidation?.impartialityBasis ?? ''),
    async (box, current) => {
      if (!current || !ctx.me?.personId || !assessment.auditorValidation?.auditProgram || !reason(box) || !value(box, 'note') || !value(box, 'impartiality')) throw new Error('A current qualified auditor, effective audit program, impartiality basis and reason are required.');
      await command(ROOT + '/editions/assessments/' + enc(assessment.id) + '/' + action.toLowerCase(), { data: {
        expectedVersion: assessment.version, note: value(box, 'note'),
        auditorValidation: { personId: ctx.me.personId, asOf: new Date().toISOString(), auditProgram: assessment.auditorValidation.auditProgram, competenceAssessmentId: null, impartialityBasis: value(box, 'impartiality') }
      } }, reason(box));
    });
  const recordMeeting = run => {
    const agenda = run.manifest?.inputs ?? [];
    const assessmentFormat = agenda.map(item => item.agendaCode + '|Adequate| | |').join('\n');
    modal('Record actual meeting judgments',
      '<p class="muted">Assess each frozen agenda item. Use one line per item: agenda code | Adequate, Inadequate, Unavailable | actual assessment | limits | decision. Separate human judgments from captured source linkage.</p>' +
      formField('meeting', 'Meeting record', 'textarea') + formField('participants', 'Actual participant person IDs, one per line', 'textarea') +
      formField('assessments', 'Agenda assessments', 'textarea', assessmentFormat) +
      formField('decisions', 'Assigned decisions: code | decision | basis | intended result | resources | owner person ID or blank | due UTC or blank | completion criteria', 'textarea') +
      formField('limitations', 'Limitations', 'textarea'),
      async (box, current) => {
        const parse = (text, count, name) => text.split(/\r?\n/).map(line => line.trim()).filter(Boolean).map(line => { const pieces = line.split('|').map(x => x.trim()); if (pieces.length !== count || pieces.some((part, index) => name === 'decision' ? (index < 5 || index === 7) && !part : index < 2 && !part)) throw new Error('Each ' + name + ' line needs exactly ' + count + ' fields separated by |.'); return pieces; });
        const assessments = parse(value(box, 'assessments'), 5, 'assessment').map(([agendaCode, adequacy, assessment, limits, decision]) => ({ agendaCode, adequacy, assessment, limits, decision, additionalEvidence: [] }));
        const decisions = parse(value(box, 'decisions'), 8, 'decision').map(([code, decision, basis, intendedResult, resources, owner, due, completionCriteria]) => ({ code, decision, basis, intendedResult, resources, ownerPersonId: owner || null, dueAt: due ? new Date(due).toISOString() : null, completionCriteria, sourceActionReferences: [] }));
        const participants = value(box, 'participants').split(/\r?\n/).map(x => x.trim()).filter(Boolean);
        if (!current || !reason(box) || !value(box, 'meeting') || !value(box, 'limitations') || assessments.length !== agenda.length || !participants.length) throw new Error('Record the meeting, every frozen agenda assessment, active participants and limitations.');
        await command(ROOT + '/runs/' + enc(run.id) + '/meeting', { data: { expectedVersion: run.version, meetingRecord: value(box, 'meeting'), participants, assessments, decisions, limitations: value(box, 'limitations') } }, reason(box));
      });
  };
  function actionsFor(run) {
    const result = [];
    if (permission(ctx.me, 'quality.qms.manage')) {
      if (run.state === 'Draft') result.push(['Freeze inputs', () => freeze(run)]);
      if (run.state === 'InputsFrozen' && !run.meetingRecord) result.push(['Record meeting', () => recordMeeting(run)]);
      if (run.state === 'InputsFrozen' && run.meetingRecord) result.push(['Submit', () => runAction('Submit', run)]);
      if (run.state === 'InputsFrozen' || run.state === 'Submitted') result.push(['Withdraw', () => runAction('Withdraw', run)]);
    }
    if (run.state === 'Submitted' && permission(ctx.me, 'quality.qms.review')) result.push(['Review', () => runAction('Review', run)]);
    if (run.state === 'Reviewed' && permission(ctx.me, 'quality.qms.approve')) result.push(['Approve', () => runAction('Approve', run)]);
    if (run.state === 'Approved' && permission(ctx.me, 'quality.qms.approve')) result.push(['Close', () => runAction('Close', run)]);
    return result;
  }
  function editionActions(assessment) {
    const result = [];
    if (assessment.state === 'Draft' && permission(ctx.me, 'quality.qms.manage')) result.push('Submit');
    if (assessment.state === 'Submitted' && permission(ctx.me, 'quality.qms.review')) result.push('Review');
    if (assessment.state === 'Reviewed' && permission(ctx.me, 'quality.qms.approve')) result.push('Approve');
    return result;
  }
  function render() {
    if (disposed || !page.isConnected) return;
    const rows = rowsFor(tab, data), selectedId = selected && idFor(tab, selected);
    const sourceText = error ? errorBlock(error, 'current quality review records') : rows.length
      ? '<table class="table"><thead><tr><th>Record</th><th>State</th><th>Scope</th><th></th></tr></thead><tbody>' + rows.map(item => '<tr><td>' + esc(idFor(tab, item)) + '</td><td>' + esc(stateFor(tab, item) ?? '—') + '</td><td>' + esc(item.organizationCode ?? item.facilityCode ?? item.mappingCode ?? '') + '</td><td><button class="btn small" data-open="' + esc(idFor(tab, item)) + '">Open</button></td></tr>').join('') + '</tbody></table>'
      : '<div class="empty">No authorized records are available in this page.</div>';
    const createButton = selected?.kind === 'ManagementReviewProgram' && permission(ctx.me, 'quality.qms.manage')
      ? '<button class="btn primary" data-create-run>Start review</button>' : '';
    const toolbarCreate = tab === 'editions' && permission(ctx.me, 'quality.qms.manage')
      ? '<button class="btn primary" data-create-edition>New edition assessment</button>' : '';
    const actionButtons = tab === 'runs' && selected ? actionsFor(selected).map(([name]) => '<button class="btn" data-run-action="' + esc(name) + '">' + esc(name) + '</button>').join('')
      : tab === 'editions' && selected ? editionActions(selected).map(name => '<button class="btn" data-edition-action="' + esc(name) + '">' + esc(name) + '</button>').join('') : '';
    page.innerHTML = '<div class="page-head"><div><h1>QMS reviews and evidence</h1><p class="sub">Review actual owner records, frozen inputs and human decisions. Evidence links do not declare conformity.</p></div><button class="btn" data-refresh>Refresh</button></div>' +
      '<section class="panel"><div class="seg">' + TABS.map(name => '<button class="' + (tab === name ? 'on' : '') + '" data-tab="' + name + '">' + esc(TAB_LABEL[name]) + '</button>').join('') + '</div>' + sourceText +
      (data?.nextCursor ? '<div class="toolbar"><button class="btn" data-next>Next page</button></div>' : '') + '</section>' +
      '<div class="toolbar">' + toolbarCreate + '</div>' +
      (selected ? '<section class="panel"><div class="page-head"><div><h2>' + esc(selectedId) + '</h2><p class="sub">' + esc(stateFor(tab, selected) ?? '') + '</p></div><div class="toolbar">' + createButton + actionButtons + '</div></div>' + detailHtml(selected) +
        (tab === 'runs' ? '<h3>Assigned actions</h3>' + detailHtml(actions) : '') + '</section>' : '');
    $$('[data-tab]', page).forEach(button => button.onclick = () => { tab = button.dataset.tab; load(); });
    $('[data-refresh]', page)?.addEventListener('click', () => load());
    $('[data-next]', page)?.addEventListener('click', () => load(data.nextCursor));
    $$('[data-open]', page).forEach(button => button.onclick = () => open(button.dataset.open));
    $('[data-create-run]', page)?.addEventListener('click', () => createRun(selected));
    $('[data-create-edition]', page)?.addEventListener('click', () => createEdition());
    $$('[data-run-action]', page).forEach(button => { const action = actionsFor(selected).find(([name]) => name === button.dataset.runAction); if (action) button.onclick = action[1]; });
    $$('[data-edition-action]', page).forEach(button => button.onclick = () => editionAction(button.dataset.editionAction, selected));
  }
  await load();
  return { dispose() { disposed = true; generation++; page.innerHTML = ''; } };
}
