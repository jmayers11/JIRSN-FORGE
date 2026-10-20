import { command, tryGet, session as signIn } from '../api.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock, toast, manageDialogFocus } from '../ui.js';
import { fieldHtml, readFields, canAction } from '../forms.js';
import { isServiceUnavailable } from '../terminal-state.mjs';

const API = '/api/quality/qms/planning';
export const PLANNING_KINDS = ['EvaluationMethod', 'RiskOpportunity', 'MetricDefinition', 'Objective', 'Change', 'RetentionSchedule', 'Improvement'];
const permission = { manage: 'quality.qms.manage', review: 'quality.qms.review', approve: 'quality.qms.approve', publish: 'quality.qms.publish' };
const key = encodeURIComponent, label = value => String(value).replace(/([a-z])([A-Z])/g, '$1 $2');
const text = (name, title, value = '', extra = {}) => ({ name, label: title, value, required: true, ...extra });
const paragraph = (name, title, value = '', extra = {}) => text(name, title, value, { type: 'textarea', ...extra });
const number = (name, title, value, extra = {}) => text(name, title, value, { type: 'number', step: 'any', ...extra });
const choose = (name, title, values, value = '') => text(name, title, value, { type: 'select', options: [['', '— choose —'], ...values.map(item => [item, label(item)])] });
const decide = (name, title, value) => choose(name, title, ['true', 'false'], value == null ? '' : String(value));
const date = (name, title, value) => text(name, title, value ? new Date(value).toISOString().slice(0, 16) : '', { type: 'datetime-local', hint: 'UTC date and time.' });
const dates = value => { if (!value) throw new Error('Record an exact UTC date and time.'); return new Date(`${value.length === 16 ? value + ':00' : value}Z`).toISOString(); };
const lines = value => Array.isArray(value) ? value.join('\n') : value ?? '';
const values = value => String(value ?? '').split(/\r?\n/).map(item => item.trim()).filter(Boolean);
const bool = value => value === 'true' ? true : value === 'false' ? false : null;
const numbers = value => values(value).map(item => { const result = Number(item); if (!Number.isSafeInteger(result) || result <= 0) throw new Error('Enter distinct positive order or operation numbers, one per line.'); return result; });
const referenceFields = [text('code', 'Record code'), number('revision', 'Exact revision', null, { min: 1 })];
const documentFields = [text('documentNumber', 'Engineering document number'), text('revision', 'Exact document revision')];
const evidenceFields = [text('context', 'Actual owner context'), text('type', 'Actual owner record type'), text('key', 'Actual owner key'), text('revision', 'Exact owner revision, if versioned', '', { required: false }), text('eventId', 'Actual event ID, if cited', '', { required: false }), text('auditId', 'Actual audit ID, if cited', '', { required: false })];
const sectionKey = kind => kind[0].toLowerCase() + kind.slice(1);
const owner = value => text('ownerPersonId', 'Responsible registered person ID', value, { hint: 'Choose the actual owner. The server verifies active registration and source access.' });

export function planningDefinition(kind, content = {}) {
  const c = content[sectionKey(kind)] ?? {}, fields = [], groups = [];
  const add = (...items) => fields.push(...items);
  const group = (name, title, columns, rows = [], minimum = 0) => groups.push({ name, title, columns, rows, minimum });
  const str = (name, title = label(name)) => paragraph(name, title, c[name]);
  const ref = (name, title) => { add(text(`${name}Code`, `${title} code`, c[name]?.code), number(`${name}Revision`, `${title} exact revision`, c[name]?.revision, { min: 1 })); };
  if (kind === 'EvaluationMethod') {
    add(text('name', 'Evaluation method name', c.name), choose('kind', 'Method kind', ['Qualitative', 'Ordinal', 'Numeric', 'OtherExplicit'], c.kind), str('method'), str('decisionCriteria'), str('reviewInterval'));
    group('scale', 'Explicit evaluation scale', [text('code', 'Scale code'), paragraph('description', 'Scale meaning'), number('numericValue', 'Numeric value, if declared', null, { required: false })], c.scale);
  } else if (kind === 'RiskOpportunity') {
    add(choose('kind', 'Register kind', ['Risk', 'Opportunity'], c.kind), str('context')); ref('process', 'Controlled QMS process');
    add(str('description'), str('consequenceOrBenefit')); ref('evaluationMethod', 'Approved planning evaluation method');
    add(owner(c.ownerPersonId), str('plannedAction'), date('dueAt', 'Action due (UTC)', c.dueAt), str('resources'), str('reviewInterval'), str('effectivenessCriteria'));
  } else if (kind === 'MetricDefinition') {
    add(text('name', 'Metric name', c.name), choose('sourceKind', 'Actual data source', ['RecordedProductionReports', 'AttributedHumanObservation'], c.sourceKind),
      choose('productionMeasure', 'Recorded production measure (blank for human measurement)', ['GoodQuantity', 'ScrapQuantity', 'TotalReportedQuantity', 'ScrapFraction', 'ReportCount'], c.productionMeasure));
    fields.at(-1).required = false;
    add(text('unit', 'Approved unit', c.unit), str('numeratorDefinition'), str('denominatorDefinition'), str('measurementMethod'), str('cohortBasis'),
      paragraph('orderNumbers', 'Exact order cohort (one number per line)', lines(c.orderNumbers)), decide('includeAllOperations', 'Include all recorded operations in each order?', c.includeAllOperations),
      paragraph('operationSequences', 'Exact operation sequences (blank only when all are explicitly included)', lines(c.operationSequences), { required: false }),
      date('periodFrom', 'Period from, inclusive (UTC)', c.periodFrom), date('periodTo', 'Period to, exclusive (UTC)', c.periodTo), number('maximumRows', 'Maximum evidence rows; excess returns incomplete', c.maximumRows, { min: 1 }), str('completenessCriteria'), str('reviewInterval'));
  } else if (kind === 'Objective') {
    add(text('title', 'Quality objective', c.title)); ref('metric', 'Approved metric definition');
    add(owner(c.ownerPersonId), choose('baselineStatus', 'Baseline knowledge', ['Recorded', 'Unknown'], c.baselineStatus), number('baseline', 'Recorded baseline (blank if unknown)', c.baseline, { required: false }), str('baselineBasis'),
      choose('comparison', 'Target comparison', ['AtLeast', 'AtMost', 'Equal', 'WithinRange'], c.comparison), number('target', 'Target', c.target), number('upperTarget', 'Upper target (range only)', c.upperTarget, { required: false }), number('tolerance', 'Explicit tolerance', c.tolerance, { min: 0 }),
      date('periodFrom', 'Objective period from (UTC)', c.periodFrom), date('periodTo', 'Objective period to (UTC)', c.periodTo), str('reviewInterval'), str('plannedActions'), str('resources'), date('dueAt', 'Action due (UTC)', c.dueAt));
    group('baselineEvidence', 'Actual baseline evidence', evidenceFields, c.baselineEvidence);
  } else if (kind === 'Change') {
    add(str('proposedChange'), str('reasonForChange'), str('intendedResult'), str('resourceImplications'), str('responsibilityChanges'), str('requiredApprovals'), str('implementationPlan'), str('verificationCriteria'), str('effectivenessCriteria'), owner(c.ownerPersonId), date('dueAt', 'Implementation due (UTC)', c.dueAt));
    group('affectedProcesses', 'Actual affected controlled processes', referenceFields, c.affectedProcesses, 1);
    group('risksAndOpportunities', 'Linked planning risk/opportunity definitions', referenceFields, c.risksAndOpportunities);
    group('engineeringChanges', 'Actual separately governed Engineering change evidence', evidenceFields, c.engineeringChanges);
  } else if (kind === 'RetentionSchedule') {
    add(str('recordCategory'), str('retentionRequirement'), choose('trigger', 'Actual retention trigger', ['SourceCreatedAt', 'SourceApprovedAt', 'SourceClosedAt', 'HumanVerifiedExternalOccurrence'], c.trigger),
      decide('indefinite', 'Retain indefinitely?', c.indefinite), number('durationDays', 'Explicit duration days (blank for indefinite)', c.durationDays, { required: false, min: 0 }), owner(c.ownerPersonId), str('contractCustomerLegalBasis'), str('dispositionAuthority'), str('dispositionEvidenceCriteria'),
      paragraph('allowedOwnerTypes', 'Exact allowed owner context/type (one per line)', lines(c.allowedOwnerTypes), { hint: 'For example Engineering/DocumentRevision. No universal duration or record category is inferred.' }));
  } else if (kind === 'Improvement') {
    add(text('title', 'Improvement opportunity', c.title), str('context'), str('intendedBenefit'), owner(c.ownerPersonId), str('evaluationMethod'), str('selectionCriteria'), str('plannedActions'), str('resources'), date('dueAt', 'Action due (UTC)', c.dueAt), str('beforeAfterMethod'));
    group('origins', 'Actual audit, CAPA, feedback, objective or performance origins', evidenceFields, c.origins, 1);
  } else throw new Error('Choose an actual planning record kind.');
  group('documents', 'Exact controlled Engineering documents', documentFields, content.documents);
  group('requirements', 'Exact QMS requirement decisions', referenceFields, content.requirements);
  group('evidence', 'Actual owner evidence', evidenceFields, content.evidence);
  return { fields, groups };
}

export function planningContentFrom(kind, v, g) {
  const section = { ...v };
  const ref = name => { section[name] = { code: v[`${name}Code`], revision: v[`${name}Revision`] }; delete section[`${name}Code`]; delete section[`${name}Revision`]; };
  if (kind === 'EvaluationMethod') section.scale = g.scale ?? [];
  if (kind === 'RiskOpportunity') { ref('process'); ref('evaluationMethod'); section.dueAt = dates(v.dueAt); }
  if (kind === 'MetricDefinition') { section.orderNumbers = numbers(v.orderNumbers); section.operationSequences = numbers(v.operationSequences); section.includeAllOperations = bool(v.includeAllOperations); section.periodFrom = dates(v.periodFrom); section.periodTo = dates(v.periodTo); }
  if (kind === 'Objective') { ref('metric'); section.periodFrom = dates(v.periodFrom); section.periodTo = dates(v.periodTo); section.dueAt = dates(v.dueAt); section.baselineEvidence = g.baselineEvidence ?? []; }
  if (kind === 'Change') { section.dueAt = dates(v.dueAt); section.affectedProcesses = g.affectedProcesses ?? []; section.risksAndOpportunities = g.risksAndOpportunities ?? []; section.engineeringChanges = g.engineeringChanges ?? []; }
  if (kind === 'RetentionSchedule') { section.indefinite = bool(v.indefinite); section.allowedOwnerTypes = values(v.allowedOwnerTypes); }
  if (kind === 'Improvement') { section.dueAt = dates(v.dueAt); section.origins = g.origins ?? []; }
  return { [sectionKey(kind)]: section, documents: g.documents ?? [], requirements: g.requirements ?? [], evidence: g.evidence ?? [] };
}

export function assessmentDefinition(kind) {
  const fields = [], add = (...items) => fields.push(...items);
  if (kind === 'RiskEvaluation') add(text('methodCode', 'Exact approved method code'), number('methodRevision', 'Exact method revision', null, { min: 1 }), paragraph('evaluation', 'Actual evaluation'), number('numericResult', 'Numeric result (numeric method)', null, { required: false }), text('scaleCode', 'Declared scale code (ordinal method)', '', { required: false }), paragraph('decision', 'Human risk/opportunity decision'), paragraph('basis', 'Actual decision basis'), paragraph('actionPlan', 'Actual action plan'), date('nextReviewAt', 'Next review (UTC)'), paragraph('effectivenessCriteria', 'Effectiveness criteria'));
  else if (kind === 'ObjectiveEvaluation') add(date('from', 'Measurement from, inclusive (UTC)'), date('to', 'Measurement to, exclusive (UTC)'), number('humanActual', 'Actual (human source only; server derives recorded measures)', null, { required: false }), paragraph('humanMeasurementBasis', 'Human measurement basis (human source only)', '', { required: false }), paragraph('completenessAssessment', 'Cohort and evidence completeness assessment'), paragraph('actionDecision', 'Actual action decision'));
  else if (kind === 'ChangeImpact') add(...['processImpact', 'riskOpportunityImpact', 'resourceImpact', 'responsibilityImpact', 'integrityAssessment', 'approvalBasis'].map(name => paragraph(name, label(name))));
  else if (kind === 'ImplementationVerification') add(paragraph('taskIds', 'Every actual verified implementation task ID (one per line)'), paragraph('criteria', 'Verification criteria'), paragraph('result', 'Actual verification result'), decide('satisfied', 'Were the declared implementation criteria satisfied?'), paragraph('limitations', 'Observed limitations or explicit none'));
  else if (kind === 'EffectivenessReview') add(date('from', 'Effectiveness review from (UTC)'), date('to', 'Effectiveness review to (UTC)'), ...['method', 'criteria', 'results', 'limitations', 'followUpDecision'].map(name => paragraph(name, label(name))), decide('effective', 'Was the implemented action effective by these criteria?'));
  else if (kind === 'ImprovementEvaluation') add(paragraph('decision', 'Improvement selection/evaluation decision'), paragraph('basis', 'Actual basis'), number('before', 'Measured before (optional)', null, { required: false }), number('after', 'Measured after (optional)', null, { required: false }), text('unit', 'Measurement unit or explicit unmeasured basis'), paragraph('resultInterpretation', 'Human interpretation'), paragraph('limitations', 'Limitations or explicit none'));
  else throw new Error('Choose an assessment kind.');
  return { fields, groups: [{ name: 'evidence', title: 'Actual assessment source evidence', columns: evidenceFields, rows: [] }] };
}
export function assessmentContentFrom(kind, values_) {
  const section = { ...values_ };
  for (const field of ['from', 'to', 'nextReviewAt']) if (field in section) section[field] = dates(section[field]);
  for (const field of ['effective', 'satisfied']) if (field in section) section[field] = bool(section[field]);
  if ('taskIds' in section) section.taskIds = values(section.taskIds);
  return { [sectionKey(kind)]: section };
}
export function contributors(record) { return [...new Set([record.createdBy, record.lastEditedBy, ...(record.history ?? []).filter(item => ['Create', 'Edit', 'Complete'].includes(item.action)).map(item => item.actor), ...(record.sources ?? record.evidence ?? []).flatMap(item => item.contributors ?? [])].filter(Boolean))]; }
export function definitionActions(me, record, revision) {
  const scoped = { facilityId: record.facilityCode, programId: record.programId }, actor = `person:${me?.personId}`;
  const can = action => canAction(me, permission[action], scoped), result = [];
  if (can('manage') && revision.state === 'Draft') result.push('Edit', 'Submit');
  if (can('review') && revision.state === 'Submitted' && !contributors(revision).includes(actor)) result.push('Review');
  if (can('approve') && revision.state === 'Reviewed' && !contributors(revision).includes(actor)) result.push('Approve');
  if (can('publish') && revision.state === 'Approved') result.push('Effective');
  if (can('publish') && !['Superseded', 'Withdrawn'].includes(revision.state)) result.push('Withdraw');
  if (can('manage') && revision.revision === record.revisions.at(-1)?.revision && !record.revisions.some(item => ['Draft', 'Submitted', 'Reviewed', 'Approved'].includes(item.state))) result.push('NewRevision');
  return result;
}
export function workActions(me, scope, type, record) {
  const resource = { facilityId: scope.facilityCode, programId: scope.programId }, can = action => canAction(me, permission[action], resource), actor = `person:${me?.personId}`;
  const independent = !contributors(record).includes(actor), result = [];
  if (type === 'assessment') {
    if (record.state === 'Draft' && can('manage')) result.push('Submit');
    if (record.state === 'Submitted' && can('review') && independent) result.push('Review');
    if (record.state === 'Reviewed' && can('approve') && independent) result.push('Approve');
    if (record.state !== 'Withdrawn' && can('publish')) result.push('Withdraw');
  } else if (type === 'task') {
    if (['Open', 'Reopened'].includes(record.state) && me?.personId === record.ownerPersonId && can('manage')) result.push('Complete');
    if (record.state === 'Completed' && can('approve') && independent && me?.personId !== record.ownerPersonId && actor !== record.completedBy) result.push('Verify');
    if (['Completed', 'Verified'].includes(record.state) && can('manage')) result.push('Reopen');
    if (record.state !== 'Withdrawn' && can('publish')) result.push('Withdraw');
  } else {
    const next = { Identified: 'Assess', Reopened: 'Assess', AssessmentPending: 'Plan', ActionPlanned: 'Implement', Implementing: 'RequestVerification', VerificationPending: 'Verify', EffectivenessPending: 'Accept', Accepted: 'Close', Closed: 'Reopen' }[record.state];
    if (next && can(['Accept', 'Close'].includes(next) ? 'approve' : 'manage') && (!['Accept', 'Close'].includes(next) || independent)) result.push(next);
    if (['Accepted', 'Closed'].includes(record.state) && can('manage') && !result.includes('Reopen')) result.push('Reopen');
    if (record.state !== 'Withdrawn' && can('publish')) result.push('Withdraw');
  }
  return result;
}
export function retentionActions(me, type, record) {
  const resource = { facilityId: record.facilityCode, programId: record.programId }, can = action => canAction(me, permission[action], resource), actor = `person:${me?.personId}`, result = [];
  if (type === 'bindings') {
    if (record.state === 'Proposed' && can('approve') && ![record.createdBy, ...(record.subject?.contributors ?? []), ...(record.triggerEvidence ?? []).flatMap(item => item.contributors ?? [])].includes(actor)) result.push('Approve');
    if (record.state === 'Proposed' && can('publish')) result.push('Withdraw');
  } else if (type === 'holds') {
    if (record.state === 'Active' && can('publish')) result.push('ProposeRelease');
    if (record.state === 'ReleaseProposed' && can('approve') && ![record.placedBy, record.releaseProposedBy, ...(record.evidence ?? []).flatMap(item => item.contributors ?? [])].includes(actor)) result.push('Release');
  } else {
    if (['Proposed', 'DryRunFrozen'].includes(record.state) && can('manage')) result.push('DryRun');
    const authors = [record.proposedBy, ...(record.history ?? []).filter(item => item.action === 'DryRun').map(item => item.actor), ...(record.manifest?.candidates ?? []).flatMap(item => item.subject?.contributors ?? [])];
    if (record.state === 'DryRunFrozen' && record.manifest?.eligible === true && can('approve') && !authors.includes(actor)) result.push('Approve');
    if (record.state === 'Approved' && can('publish')) result.push('Record');
    if (['Proposed', 'DryRunFrozen', 'Approved'].includes(record.state) && can('publish')) result.push('Withdraw');
  }
  return result;
}

const historyHtml = history => (history ?? []).length ? `<table class="table"><thead><tr><th>When</th><th>Actor/action/version</th><th>State</th><th>Decision and reason</th></tr></thead><tbody>${history.map(item => `<tr><td>${esc(fmtDateTime(item.at))}</td><td>${esc(item.actor)} · ${esc(item.action)} · ${item.version}</td><td>${esc(item.fromState)} → ${esc(item.toState)}</td><td>${esc(item.note)}<br>${esc(item.reason)}</td></tr>`).join('')}</tbody></table>` : '<p class="faint">No history returned.</p>';
const detailsHtml = object => `<dl>${Object.entries(object ?? {}).filter(([, value]) => value != null).map(([name, value]) => `<dt><b>${esc(label(name))}</b></dt><dd style="white-space:pre-line">${typeof value === 'object' ? Array.isArray(value) ? value.map(item => typeof item === 'object' ? detailsHtml(item) : esc(item)).join('<br>') : detailsHtml(value) : esc(value)}</dd>`).join('')}</dl>`;

export async function qmsPlanningPage(page, ctx) {
  const me = ctx.me, auth = { subject: signIn.subject, token: signIn.token }, dialogs = new Set();
  let disposed = false, generation = 0, blocked = false, kind = PLANNING_KINDS.includes(ctx.query?.get('kind')) ? ctx.query.get('kind') : 'RiskOpportunity';
  let data = {}, selected = null, selectedRevision = null, work = {}, retention = null, cursor = null;
  const live = token => !disposed && page.isConnected && token === generation && signIn.subject === auth.subject && signIn.token === auth.token;
  const closeDialogs = () => { for (const close of [...dialogs]) close(); };
  const allowed = (action, record = selected) => !blocked && canAction(me, permission[action], record ? { facilityId: record.facilityCode, programId: record.programId } : null);
  const currentRevision = () => selected?.revisions.find(item => item.revision === selectedRevision) ?? selected?.revisions.at(-1);

  async function load(after = null) {
    const token = ++generation; blocked = true; closeDialogs();
    const [status, records, bindings, dispositions] = await Promise.all([tryGet(`${API}/status`), tryGet(`${API}/records?kind=${kind}&take=30${after ? `&after=${key(after)}` : ''}`), tryGet(`${API}/retention/bindings?take=30`), tryGet(`${API}/retention/dispositions?take=30`)]);
    const directory = await tryGet('/api/kernel/directory');
    if (!live(token)) return; blocked = !!status.error || !!records.error; data = { status, records, bindings, dispositions, directory }; cursor = after; render();
    if (selected) await openRecord(selected.code, selectedRevision);
  }
  async function openRecord(code, revision = null) {
    const token = ++generation; closeDialogs(); const result = await tryGet(`${API}/records/${key(code)}`);
    if (!live(token)) return; if (result.error) { blocked = true; toast(result.error.message, true); render(); return; }
    selected = result.data; selectedRevision = revision ?? selected.revisions.at(-1)?.revision; retention = null;
    const children = await tryGet(`${API}/records/${key(code)}/revisions/${selectedRevision}/work`);
    if (!live(token)) return; work = children; blocked = !!children.error; render();
  }
  async function openRetention(type, id) {
    const token = ++generation; closeDialogs(); const result = await tryGet(`${API}/retention/${type}/${key(id)}`);
    if (!live(token)) return; if (result.error) { toast(result.error.message, true); return; } retention = { type, record: result.data }; render();
  }

  function form(title, detail, definition, send) {
    closeDialogs(); const token = generation, scrim = document.createElement('div'), modal = document.createElement('div');
    scrim.className = 'scrim'; scrim.setAttribute('aria-hidden', 'true'); modal.className = 'modal';
    modal.innerHTML = `<h2>${esc(title)}</h2><p class="muted">${esc(detail)}</p><div class="form-grid">${definition.fields.map(fieldHtml).join('')}</div>${definition.groups.map(group => `<section data-group="${group.name}"><h3>${esc(group.title)}</h3><div data-rows></div><button class="btn small" data-add type="button">Add entry</button></section>`).join('')}${fieldHtml(paragraph('reason', 'Reason recorded in the audit trail'))}<div class="notice bad hidden" role="alert" aria-live="assertive" data-error></div><div class="actions"><button class="btn" data-cancel>Cancel</button><button class="btn primary" data-confirm>Record decision</button></div>`;
    let closed = false, busy = false, uncertain = false, focus = () => {}, rowId = 0;
    const current = () => !closed && modal.isConnected && live(token) && !blocked;
    const close = () => {
      if (closed) return;
      closed = true; scrim.remove(); modal.remove(); dialogs.delete(close); focus();
      if (uncertain && page.isConnected) { blocked = true; render(); }
    };
    dialogs.add(close); scrim.onclick = close; document.body.append(scrim, modal); $('[data-cancel]', modal).onclick = close; focus = manageDialogFocus(modal, close);
    for (const group of definition.groups) {
      const area = $(`[data-group="${group.name}"]`, modal), rows = $('[data-rows]', area);
      const add = (value = {}) => {
        if (!current()) return; const row = document.createElement('div'); row.className = 'form-grid'; row.prefix = `entry-${rowId++}-`;
        row.fields = group.columns.map(field => ({ ...field, name: row.prefix + field.name, value: value[field.name] ?? field.value }));
        row.innerHTML = row.fields.map(fieldHtml).join('') + '<button class="btn small" data-remove type="button">Remove entry</button>'; rows.append(row); $('[data-remove]', row).onclick = () => row.remove();
      };
      for (const item of group.rows ?? []) add(item); $('[data-add]', area).onclick = () => add();
    }
    $('[data-confirm]', modal).onclick = async () => {
      if (!current() || busy) return; const button = $('[data-confirm]', modal), error = $('[data-error]', modal); busy = true; button.disabled = true; error.classList.add('hidden');
      try {
        const v = readFields(modal, definition.fields), reason = readFields(modal, [paragraph('reason', 'Reason')]).reason, groups = {};
        for (const group of definition.groups) { const rows = [...$(`[data-group="${group.name}"] [data-rows]`, modal).children]; if (rows.length < (group.minimum ?? 0)) throw new Error(`Record the required ${group.title.toLowerCase()}.`); groups[group.name] = rows.map(row => Object.fromEntries(Object.entries(readFields(row, row.fields)).map(([name, value]) => [name.slice(row.prefix.length), value]))); }
        if (!current()) return; const result = await send(v, groups, reason, current);
        if (!current()) return; close(); toast(result.message ?? 'Decision recorded.'); await load(cursor);
      } catch (failure) {
        if (!current()) return;
        if (failure instanceof TypeError || isServiceUnavailable(failure) && Number.isInteger(failure.status)) {
          uncertain = true;
          error.textContent = 'Forge could not confirm that change. Your entries are preserved. Close this dialog and refresh the record before deciding whether to retry.';
          error.classList.remove('hidden');
          return;
        }
        error.textContent = `${failure.message}${failure.status === 409 ? ' Read the current version before deciding again.' : ''}`; error.classList.remove('hidden');
      } finally { busy = false; if (current()) button.disabled = uncertain; }
    };
  }
  const guard = (current, action, record) => { if (!current() || !allowed(action, record)) throw new Error('This authenticated decision context is no longer active.'); };
  function withOwners(definition) {
    const people = data.directory?.data;
    if (!Array.isArray(people) || !people.length) return definition;
    return { ...definition, fields: definition.fields.map(field => {
      if (field.name !== 'ownerPersonId') return field;
      const options = [['', '— choose actual responsible person —'], ...people.map(person => [person.personId, person.displayName])];
      if (field.value && !people.some(person => person.personId === field.value)) options.push([field.value, 'Recorded person ' + field.value]);
      return { ...field, type: 'select', options };
    }) };
  }
  function edit(mode = 'Create') {
    const record = mode === 'Create' ? null : selected, revision = currentRevision(), selectedKind = record?.kind ?? kind;
    const definition = withOwners(planningDefinition(selectedKind, record ? revision.content : {}));
    const headers = record ? [] : [text('code', 'Record code'), text('facilityCode', 'Actual facility code'), text('programId', 'Owning program ID (if applicable)', '', { required: false }), choose('markings', 'Actual handling markings', ['None', 'Proprietary', 'ControlledUnclassified', 'ExportControlled', 'Proprietary, ControlledUnclassified', 'Proprietary, ExportControlled', 'ControlledUnclassified, ExportControlled', 'Proprietary, ControlledUnclassified, ExportControlled'])];
    form(`${label(mode)} ${label(selectedKind)}`, 'Record the organization’s actual definition and exact sources. Approval does not claim licensed standards verification or completed physical work.', { fields: [...headers, ...definition.fields], groups: definition.groups }, (v, groups, reason, current) => {
      guard(current, 'manage', record); const contentValues = Object.fromEntries(definition.fields.map(field => [field.name, v[field.name]])), content = planningContentFrom(selectedKind, contentValues, groups);
      const body = record ? { expectedVersion: revision.version, content } : { code: v.code, kind: selectedKind, facilityCode: v.facilityCode, programId: v.programId || null, markings: v.markings, content };
      return command(record ? `${API}/records/${key(record.code)}/revisions${mode === 'Edit' ? `/${revision.revision}/edit` : ''}` : `${API}/records`, body, reason);
    });
  }
  function transition(route, action, record, extra = {}) {
    const version = record.version, scope = record.facilityCode ? record : selected;
    const access = extra.case && action === 'Verify' ? 'manage' : ['Effective', 'Withdraw', 'Record', 'ProposeRelease'].includes(action) ? 'publish' : ['Review'].includes(action) ? 'review' : ['Approve', 'Verify', 'Accept', 'Close', 'Release'].includes(action) ? 'approve' : 'manage';
    const fields = [paragraph('note', 'Human decision, observed result and limitations')];
    if (extra.observation) fields.push(choose('observationKind', 'Observation type', ['AdministrativeRecordOnly', 'AttributedExternalDispositionObservation']));
    if (extra.case) fields.push(text('assessmentId', 'Exact independently approved assessment ID (required for plan/verify/accept/close)', '', { required: false }));
    const groups = extra.evidence ? [{ name: 'evidence', title: 'Actual decision evidence', columns: evidenceFields, rows: [] }] : [];
    form(`${label(action)} ${record.id ?? record.code}`, `This decision uses recorded version ${version}. The server rechecks current source access, governing revisions and independence.`, { fields, groups }, (v, g, reason, current) => {
      guard(current, access, scope); const body = { expectedVersion: version, note: v.note, ...(extra.case ? { assessmentId: v.assessmentId || null } : {}), ...(extra.evidence ? { evidence: g.evidence } : {}), ...(extra.observation ? { observationKind: v.observationKind } : {}) };
      if (extra.task) { body.result = body.note; delete body.note; }
      return command(`${route}/${action.toLowerCase()}`, body, reason);
    });
  }
  function createWork(type, assessmentKind = null) {
    const record = selected, revision = currentRevision(); if (!record || revision?.state !== 'Effective') return;
    let definition;
    if (type === 'assessments') definition = assessmentDefinition(assessmentKind);
    else if (type === 'tasks') definition = withOwners({ fields: [text('title', 'Actual action task'), owner(), date('dueAt', 'Task due (UTC)'), paragraph('resources', 'Actual resources'), paragraph('completionCriteria', 'Observable completion criteria')], groups: [] });
    else definition = { fields: [], groups: [] };
    const caseChoices = type === 'assessments' ? (work.data?.cases ?? []).filter(item => item.definitionRevision === revision.revision && item.state === (assessmentKind === 'ImplementationVerification' ? 'VerificationPending' : assessmentKind === 'EffectivenessReview' ? 'EffectivenessPending' : 'AssessmentPending')) : [];
    if (type === 'assessments') definition.fields.unshift(text('caseId', 'Actual operational case (blank for a definition review only)', '', { required: false, type: 'select', options: [['', '— definition review only —'], ...caseChoices.map(item => [item.id, item.id + ' · ' + label(item.state) + ' · version ' + item.version])] }));
    form(`Create ${label(assessmentKind ?? type)}`, 'This is an actual owned work record for the selected exact definition revision. Definition approval alone does not claim completion.', definition, (v, g, reason, current) => {
      guard(current, 'manage', record); const body = { code: record.code, definitionRevision: revision.revision };
      if (type === 'assessments') {
        const { caseId, ...assessmentValues } = v, boundCase = caseChoices.find(item => item.id === caseId);
        if (caseId && !boundCase) throw new Error('Select the actual current case decision context.');
        Object.assign(body, { kind: assessmentKind, content: assessmentContentFrom(assessmentKind, assessmentValues), evidence: g.evidence, caseId: boundCase?.id ?? null, expectedCaseVersion: boundCase?.version ?? null });
      }
      if (type === 'tasks') Object.assign(body, v, { dueAt: dates(v.dueAt) });
      return command(`${API}/${type}`, body, reason);
    });
  }
  function createRetention(type) {
    const fields = [], groups = [{ name: 'evidence', title: 'Actual supporting owner evidence', columns: evidenceFields, rows: [] }];
    if (type !== 'bindings') fields.push(text('facilityCode', 'Actual facility code'), text('programId', 'Owning program ID', '', { required: false }), choose('markings', 'Actual handling markings', ['None', 'Proprietary', 'ControlledUnclassified', 'ExportControlled', 'Proprietary, ControlledUnclassified', 'Proprietary, ExportControlled', 'ControlledUnclassified, ExportControlled', 'Proprietary, ControlledUnclassified, ExportControlled']));
    if (type === 'bindings') fields.push(text('scheduleCode', 'Exact schedule code', selected?.kind === 'RetentionSchedule' ? selected.code : ''), number('scheduleRevision', 'Exact effective schedule revision', selected?.kind === 'RetentionSchedule' ? currentRevision()?.revision : null, { min: 1 }), date('externalTriggerAt', 'External occurrence time (only for human-verified external trigger)'), paragraph('triggerBasis', 'Actual trigger or explicit unknown basis'));
    if (type === 'bindings') fields.find(field => field.name === 'externalTriggerAt').required = false;
    if (type === 'bindings') fields.push(text('previousBindingId', 'Prior active binding ID (only for a revised classification)', '', { required: false, hint: 'An independent approval preserves the old binding and every earlier retention protection.' }));
    if (['bindings', 'holds'].includes(type)) groups.unshift({ name: 'subject', title: 'Exact actual owner subject (one entry)', columns: evidenceFields, rows: [], minimum: 1 });
    if (type === 'holds') fields.push(choose('kind', 'Hold kind', ['Legal', 'Quality']), paragraph('basis', 'Actual hold basis'), paragraph('authorityBasis', 'Actual legal/quality authority basis'));
    if (type === 'dispositions') fields.push(paragraph('bindingIds', 'Exact retention binding IDs (one per line)'), paragraph('purpose', 'Actual disposition purpose and scope'));
    form(`Propose retention ${type}`, 'All Forge source, file and audit bytes are preserved. A physical disposition observation is separately attributed evidence.', { fields, groups }, (v, g, reason, current) => {
      guard(current, type === 'holds' ? 'publish' : 'manage', type === 'bindings' && selected?.kind === 'RetentionSchedule' ? selected : null);
      const body = { ...v, evidence: g.evidence };
      if (type === 'bindings') { body.schedule = { code: v.scheduleCode, revision: v.scheduleRevision }; delete body.scheduleCode; delete body.scheduleRevision; body.externalTriggerAt = v.externalTriggerAt ? dates(v.externalTriggerAt) : null; }
      if (['bindings', 'holds'].includes(type)) { if (g.subject.length !== 1) throw new Error('Choose exactly one actual owner subject.'); body.subject = g.subject[0]; }
      if (type === 'dispositions') { body.bindingIds = values(v.bindingIds); delete body.evidence; }
      return command(`${API}/retention/${type}`, body, reason);
    });
  }
  function workHtml() {
    if (!selected) return ''; const revision = currentRevision();
    const primary = { RiskOpportunity: 'RiskEvaluation', Objective: 'ObjectiveEvaluation', Change: 'ChangeImpact', Improvement: 'ImprovementEvaluation' }[selected.kind];
    const controls = revision.state === 'Effective' && primary && allowed('manage') ? `<div class="toolbar"><button class="btn" data-work-create="${primary}">Record ${esc(label(primary))}</button><button class="btn" data-task-create>Create owned task</button><button class="btn" data-case-create>Create operational case</button><button class="btn" data-work-create="ImplementationVerification">Record implementation verification</button><button class="btn" data-work-create="EffectivenessReview">Record effectiveness review</button></div>` : '';
    if (work.error) return errorBlock(work.error, 'actual governed work');
    const assessments = work.data?.assessments ?? [], tasks = work.data?.tasks ?? [], cases = work.data?.cases ?? [];
    return `<section class="panel"><h2>Actual work and decisions</h2>${controls}${assessments.map(item => `<article><h3>${esc(label(item.kind))} · ${esc(item.id)}</h3><p>${esc(item.state)} · version ${item.version}</p>${detailsHtml(item.content)}${item.measurement ? `<div class="notice"><b>${esc(item.measurement.availability)}</b> · ${esc(item.measurement.basis)}${item.measurement.availability === 'Complete' ? ` · actual ${esc(item.measurement.actual)} ${esc(item.measurement.unit)}` : ' · complete actual unavailable'}</div>${detailsHtml({ evidence: item.measurement.evidence, manifestFingerprint: item.measurement.manifestFingerprint })}` : ''}<div class="toolbar">${(blocked ? [] : workActions(me, selected, 'assessment', item)).map(action => `<button class="btn small" data-assessment="${item.id}" data-action="${action}">${label(action)}</button>`).join('')}</div>${historyHtml(item.history)}</article>`).join('')}${tasks.map(item => `<article><h3>${esc(item.title)} · ${esc(item.id)}</h3><p>${esc(item.state)} · version ${item.version} · owner ${esc(item.ownerPersonId)} · due ${esc(fmtDateTime(item.dueAt))}</p>${detailsHtml({ resources: item.resources, completionCriteria: item.completionCriteria, result: item.result, evidence: item.evidence })}<div class="toolbar">${(blocked ? [] : workActions(me, selected, 'task', item)).map(action => `<button class="btn small" data-task="${item.id}" data-action="${action}">${label(action)}</button>`).join('')}</div>${historyHtml(item.history)}</article>`).join('')}${cases.map(item => `<article><h3>Operational case ${esc(item.id)}</h3><p>${esc(item.state)} · version ${item.version}</p><div class="toolbar">${(blocked ? [] : workActions(me, selected, 'case', item)).map(action => `<button class="btn small" data-case="${item.id}" data-action="${action}">${label(action)}</button>`).join('')}</div>${historyHtml(item.history)}</article>`).join('')}${!assessments.length && !tasks.length && !cases.length ? '<p class="faint">No actual assessment, task or operational case returned.</p>' : ''}</section>`;
  }
  function render() {
    if (!live(generation)) return; const records = data.records.data?.items ?? [], revision = currentRevision();
    page.innerHTML = `<div class="page-head"><div><h1>QMS planning and retention</h1><p class="sub">Risks, opportunities, objectives, controlled changes, improvements and attributed retention decisions.</p></div><button class="btn" data-refresh>Refresh</button>${allowed('manage', null) && data.status.data?.configured ? '<button class="btn primary" data-create>Create typed definition</button>' : ''}</div>${data.status.error ? errorBlock(data.status.error, 'QMS organization') : `<div class="notice">${data.status.data?.configured ? `Configured organization: ${esc(data.status.data.organizationCode)}` : 'The actual QMS organization must be configured.'} · Local approval does not establish licensed standards verification.</div>`}<section class="panel"><div class="seg">${PLANNING_KINDS.map(item => `<button data-kind="${item}" class="${kind === item ? 'on' : ''}">${esc(label(item))}</button>`).join('')}</div>${data.records.error ? errorBlock(data.records.error, 'planning definitions') : records.length ? `<table class="table"><thead><tr><th>Definition</th><th>Scope</th><th>Revision/state</th><th></th></tr></thead><tbody>${records.map(item => `<tr><td>${esc(item.code)}</td><td>${esc(item.facilityCode)} · ${esc(item.programId ?? 'No program')} · ${esc(item.markings)}</td><td>${item.revisions.at(-1)?.revision} · ${esc(item.revisions.at(-1)?.state)}</td><td><button class="btn small" data-open="${esc(item.code)}">Open</button></td></tr>`).join('')}</tbody></table>` : emptyBlock('No visible definitions', 'Choose the actual organization definitions and linked owner sources.')}<div class="toolbar">${cursor ? '<button class="btn" data-first>First page</button>' : ''}${data.records.data?.nextCursor ? '<button class="btn" data-next>Next visible page</button>' : ''}</div></section>${selected && revision ? `<section class="panel"><h2>${esc(selected.code)} — exact revision <select class="select" data-revision>${selected.revisions.map(item => `<option value="${item.revision}" ${item.revision === revision.revision ? 'selected' : ''}>${item.revision} · ${esc(item.state)}</option>`).join('')}</select></h2><p>${esc(revision.state)} · version ${revision.version} · authored by ${esc(revision.createdBy)} · effective ${esc(fmtDateTime(revision.effectiveFrom))} to ${esc(fmtDateTime(revision.effectiveTo))}</p>${revision.requiresLicensedVerification ? '<div class="notice warn">RequiresLicensedVerification — actual authorized normative verification remains pending.</div>' : ''}<div class="toolbar">${(blocked ? [] : definitionActions(me, selected, revision)).map(action => `<button class="btn" data-definition-action="${action}">${esc(label(action))}</button>`).join('')}</div>${detailsHtml(revision.content)}<h3>Recorded owner sources</h3>${detailsHtml({ sources: revision.sources })}<h3>Definition history</h3>${historyHtml(revision.history)}</section>${workHtml()}` : ''}<section class="panel"><h2>Retention governance</h2><p class="muted">Exact sources and schedules, active holds, immutable dry-run manifests and attributed observations. Forge source, file and audit bytes remain preserved.</p><div class="toolbar">${allowed('manage', null) ? '<button class="btn" data-retention-create="bindings">Propose binding</button><button class="btn" data-retention-create="dispositions">Propose exact disposition</button>' : ''}${allowed('publish', null) ? '<button class="btn" data-retention-create="holds">Place legal/quality hold</button>' : ''}<button class="btn" data-hold-open>Open recorded hold ID</button></div>${['bindings', 'dispositions'].map(type => data[type].error ? errorBlock(data[type].error, `retention ${type}`) : `<h3>${label(type)}</h3>${(data[type].data?.items ?? []).map(item => `<p><button class="btn small" data-retention-type="${type}" data-retention-id="${item.id}">Open ${esc(item.id)}</button> ${esc(item.state)} · version ${item.version}</p>`).join('')}`).join('')}${retention ? `<article><h3>${label(retention.type)} ${esc(retention.record.id)}</h3>${detailsHtml(retention.record)}<div class="toolbar">${(blocked ? [] : retentionActions(me, retention.type, retention.record)).map(action => `<button class="btn" data-retention-action="${action}">${label(action)}</button>`).join('')}</div></article>` : ''}</section>`;
    $('[data-refresh]', page).onclick = () => load(cursor); $('[data-create]', page)?.addEventListener('click', () => edit());
    $$('[data-kind]', page).forEach(button => button.onclick = () => { kind = button.dataset.kind; selected = null; selectedRevision = null; work = {}; void load(); });
    $$('[data-open]', page).forEach(button => button.onclick = () => openRecord(button.dataset.open)); $('[data-first]', page)?.addEventListener('click', () => load()); $('[data-next]', page)?.addEventListener('click', () => load(data.records.data.nextCursor));
    $('[data-revision]', page)?.addEventListener('change', event => openRecord(selected.code, Number(event.target.value)));
    $$('[data-definition-action]', page).forEach(button => button.onclick = () => ['Edit', 'NewRevision'].includes(button.dataset.definitionAction) ? edit(button.dataset.definitionAction) : transition(`${API}/records/${key(selected.code)}/revisions/${revision.revision}`, button.dataset.definitionAction, revision));
    $$('[data-work-create]', page).forEach(button => button.onclick = () => createWork('assessments', button.dataset.workCreate)); $('[data-task-create]', page)?.addEventListener('click', () => createWork('tasks')); $('[data-case-create]', page)?.addEventListener('click', () => createWork('cases'));
    for (const type of ['assessment', 'task', 'case']) $$(`[data-${type}]`, page).forEach(button => button.onclick = () => { const plural = `${type}s`, record = (work.data?.[plural] ?? []).find(item => item.id === button.dataset[type]); transition(`${API}/${plural}/${key(record.id)}`, button.dataset.action, record, { task: type === 'task', case: type === 'case', evidence: type === 'task' }); });
    $$('[data-retention-create]', page).forEach(button => button.onclick = () => createRetention(button.dataset.retentionCreate)); $$('[data-retention-id]', page).forEach(button => button.onclick = () => openRetention(button.dataset.retentionType, button.dataset.retentionId));
    $('[data-hold-open]', page).onclick = () => form('Open actual retained hold', 'Enter the ID from an actual recorded legal/quality hold.', { fields: [text('id', 'Recorded hold ID')], groups: [] }, async (v, g, reason, current) => { if (!current()) return; await openRetention('holds', v.id); return { message: 'Current retained hold read.' }; });
    $$('[data-retention-action]', page).forEach(button => button.onclick = () => transition(`${API}/retention/${retention.type}/${key(retention.record.id)}`, button.dataset.retentionAction, retention.record, { evidence: ['holds', 'dispositions'].includes(retention.type), observation: button.dataset.retentionAction === 'Record' }));
  }
  await load(); return { dispose: () => { disposed = true; generation++; closeDialogs(); selected = null; retention = null; work = {}; } };
}
