import { command, tryGet, session } from '../api.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock, toast, manageDialogFocus } from '../ui.js';
import { fieldHtml, readFields, canAction } from '../forms.js';
import { isServiceUnavailable } from '../terminal-state.mjs';

const AUDITS = '/api/quality/audits', CAPAS = '/api/quality/capa-governance', key = encodeURIComponent;
const PERM = { manage: 'quality.audit.manage', conduct: 'quality.audit.conduct', approve: 'quality.audit.approve', capa: 'quality.disposition', capaApprove: 'quality.approve' };
const label = x => String(x).replace(/([a-z])([A-Z])/g, '$1 $2');
const text = (name, title, value = '', extra = {}) => ({ name, label: title, value, required: true, ...extra });
const paragraph = (name, title, value = '', extra = {}) => text(name, title, value, { type: 'textarea', ...extra });
const number = (name, title, value = '', extra = {}) => text(name, title, value, { type: 'number', min: 1, ...extra });
const choose = (name, title, values, value = '') => text(name, title, value, { type: 'select', options: [['', '— choose —'], ...values.map(x => [x, label(x)])] });
const determination = (name, title, value) => choose(name, title, ['true', 'false'], value == null ? '' : String(value));
const date = (name, title, value = '') => text(name, title, value ? new Date(value).toISOString().slice(0, 16) : '', { type: 'datetime-local', hint: 'UTC date and time.' });
const lines = value => (value ?? []).join('\n');
const values = value => String(value ?? '').split(/\r?\n/).map(x => x.trim()).filter(Boolean);
const bool = value => value === 'true' ? true : value === 'false' ? false : null;
const instant = value => { if (!value) throw new Error('Record an actual UTC date and time.'); return new Date(value + (value.length === 16 ? ':00Z' : 'Z')).toISOString(); };
const refs = [{ name: 'code', label: 'Controlled QMS code', required: true }, number('revision', 'Exact revision')];
const docs = [text('documentNumber', 'Engineering document number'), text('revision', 'Exact document revision')];
const evidence = [text('context', 'Actual owning context'), text('type', 'Actual owner record type'), text('key', 'Actual owner ID'), text('revision', 'Exact revision, if applicable', '', { required: false }), text('eventId', 'Actual event ID, if cited', '', { required: false }), text('auditId', 'Actual audit ID, if cited', '', { required: false })];
const refFields = (prefix, title, value = {}) => [text(prefix + 'Code', title + ' code', value.code), number(prefix + 'Revision', title + ' exact revision', value.revision)];
function parsedRefs(value, documents = false) {
  return values(value).map(line => { const parts = line.split('|').map(x => x.trim()); if (parts.length !== 2 || parts.some(x => !x)) throw new Error('Use one exact code | revision per line.'); if (documents) return { documentNumber: parts[0], revision: parts[1] }; const revision = Number(parts[1]); if (!Number.isSafeInteger(revision) || revision <= 0) throw new Error('QMS revisions must be positive whole numbers.'); return { code: parts[0], revision }; });
}
const formatRefs = (items, documents = false) => (items ?? []).map(x => (documents ? x.documentNumber : x.code) + ' | ' + x.revision).join('\n');
const evidenceLines = input => values(input).map(line => { const parts = line.split('|').map(x => x.trim()); if (![3, 4, 6].includes(parts.length) || parts.slice(0, 3).some(x => !x)) throw new Error('Use actual context | owner type | owner ID | exact revision per evidence line; event and audit IDs may follow as two optional fields.'); return { context: parts[0], type: parts[1], key: parts[2], revision: parts[3] || null, eventId: parts[4] || null, auditId: parts[5] || null }; });
function positiveIds(value) { return values(value).map(x => { const n = Number(x); if (!Number.isSafeInteger(n) || n <= 0) throw new Error('Use actual positive owner IDs, one per line.'); return n; }); }
const meActor = me => 'person:' + me.personId;
const sourceActors = items => (items ?? []).flatMap(x => x.contributors ?? []);
const authors = record => [record?.createdBy, record?.lastEditedBy, ...(record?.history ?? []).filter(x => ['Create', 'Edit'].includes(x.action)).map(x => x.actor)];
const independent = (me, actors, sources = []) => ![...actors, ...sourceActors(sources)].includes(meActor(me));
const allowed = (me, permission, record) => canAction(me, PERM[permission] ?? permission, record ? { programId: record.programId, facilityId: record.facilityCode, markings: record.markings } : null);

export function auditProgramActions(me, program, revision) {
  const result = [];
  if (allowed(me, 'manage', program)) { if (revision.state === 'Draft') result.push('Edit', 'Submit'); if (!program.revisions.some(x => ['Draft', 'Submitted', 'Reviewed', 'Approved'].includes(x.state))) result.push('NewRevision'); }
  if (allowed(me, 'approve', program)) {
    if (independent(me, authors(revision), revision.sources)) { if (revision.state === 'Submitted') result.push('Review'); if (revision.state === 'Reviewed') result.push('Approve'); }
    if (revision.state === 'Approved') result.push('Effective');
    if (!['Superseded', 'Withdrawn'].includes(revision.state)) result.push('Withdraw');
  }
  return result;
}
export function auditRunActions(me, run, program) {
  const actions = [], assigned = run.auditors.some(x => x.personId === me.personId);
  if (allowed(me, 'conduct', run)) {
    if (assigned && ['Scheduled', 'Reopened'].includes(run.state)) actions.push('Start');
    if (assigned && run.state === 'InProgress') actions.push('Finding', 'Report');
    if (assigned && run.state === 'ReportDraft') actions.push('Report', 'SubmitReport');
    if (run.state === 'ReportApproved') actions.push('FollowUp');
    if (run.state === 'Closed') actions.push('Reopen');
    if (['Scheduled', 'Reopened'].includes(run.state)) actions.push('Cancel');
  }
  if (allowed(me, 'approve', run)) {
    const requireIndependent = program?.content?.impartiality?.requireIndependentReportApproval;
    if (run.state === 'ReportSubmitted' && (requireIndependent === false || (requireIndependent === true && independent(me, [...run.auditors.map(x => 'person:' + x.personId), run.report?.authoredBy], run.report?.evidence)))) actions.push('ApproveReport');
    if (['ReportApproved', 'FollowUp'].includes(run.state)) actions.push('Close');
  }
  return actions;
}
export function capaActions(me, detail) {
  const c = detail.capa, actions = [];
  if (allowed(me, 'capa', c)) {
    if (['Investigating', 'Reopened'].includes(c.state)) actions.push('Investigation');
    if (!['Closed', 'Withdrawn', 'EffectivenessSubmitted'].includes(c.state)) actions.push('Action');
    if (['PlanApproved', 'Implementing'].includes(c.state)) actions.push('Implemented');
    if (c.state === 'EffectivenessPending') actions.push('Effectiveness');
  }
  if (allowed(me, 'capaApprove', c)) {
    const result = detail.effectiveness.find(x => x.id === c.latestEffectivenessId);
    if (c.state === 'EffectivenessSubmitted' && result?.state === 'Approved' && result.result === 'Effective' && detail.core?.implementedBy !== meActor(me) && independent(me, [c.createdBy, result.createdBy, ...sourceActors(c.sources), ...detail.actions.flatMap(x => [x.createdBy, 'person:' + x.ownerPersonId, x.completedBy])], result.evidence)) actions.push('Close');
    if (c.state === 'Closed' || (['EffectivenessPending', 'EffectivenessSubmitted'].includes(c.state) && result?.state === 'Approved' && result.result !== 'Effective')) actions.push('Reopen');
    if (!['Closed', 'Withdrawn'].includes(c.state)) actions.push('Withdraw');
  }
  return actions;
}
export function auditFormDefinition(type, record = {}) {
  const fields = [], groups = [];
  const add = (...items) => fields.push(...items), group = (name, title, columns, rows = [], minimum = 0) => groups.push({ name, title, columns, rows, minimum });
  const ev = rows => group('evidence', 'Actual source evidence; links do not themselves establish adequacy', evidence, rows);
  if (type === 'Program') {
    const c = record.content ?? {}, competence = c.competence ?? {}, impartiality = c.impartiality ?? {};
    for (const name of ['title', 'scope', 'scheduleBasis', 'frequency', 'findingFollowUpCriteria', 'closureCriteria']) add(paragraph(name, label(name), c[name]));
    add(text('ownerPersonId', 'Actual responsible person ID', c.ownerPersonId), determination('requireFormalQualifications', 'Require formal qualifications?', competence.requireFormalQualifications), paragraph('qualificationCodes', 'Actual qualification codes, one per line', lines(competence.qualificationCodes), { required: false }), determination('requireIndependentCompetenceAssessment', 'Require independently approved competence assessment?', competence.requireIndependentCompetenceAssessment), paragraph('competenceCriteria', 'Actual competence criteria', competence.competenceCriteria), paragraph('reassessmentInterval', 'Organization-defined reassessment interval', competence.reassessmentInterval),
      determination('prohibitAuditingOwnedProcesses', 'Prohibit auditing a process this person owns?', impartiality.prohibitAuditingOwnedProcesses), determination('prohibitAuditingContributedEvidence', 'Prohibit assessing evidence this auditor contributed?', impartiality.prohibitAuditingContributedEvidence), determination('requireIndependentReportApproval', 'Require independent report approval?', impartiality.requireIndependentReportApproval), paragraph('conflictAssessmentCriteria', 'Actual conflict assessment criteria', impartiality.conflictAssessmentCriteria));
    group('processes', 'Exact controlled processes in scope', refs, c.processes, 1);
    group('criteria', 'Organization-defined audit criteria', [text('code', 'Criterion code'), text('name', 'Criterion name'), paragraph('evaluationMethod', 'Evaluation method'), paragraph('requirements', 'Exact QMS requirement code | revision, one per line', '', { required: false }), paragraph('documents', 'Exact Engineering document number | revision, one per line', '', { required: false }), paragraph('requiredEvidenceKinds', 'Required owner evidence kinds, one per line', '', { required: false }), paragraph('evidenceAdequacyCriteria', 'Qualified human adequacy criteria')], (c.criteria ?? []).map(x => ({ ...x, requirements: formatRefs(x.requirements), documents: formatRefs(x.documents, true), requiredEvidenceKinds: lines(x.requiredEvidenceKinds) })), 1);
    group('checklist', 'Controlled questions; no universal checklist is supplied', [text('code', 'Checklist code'), text('criterionCode', 'Declared criterion code'), paragraph('question', 'Actual question'), paragraph('requiredEvidenceBasis', 'Required evidence basis')], c.checklist, 1);
    group('documents', 'Exact controlled supporting document revisions', docs, c.documents); ev(c.evidence);
  } else if (type === 'Competence') {
    add(text('personId', 'Actual assessed person ID'), ...refFields('auditProgram', 'Approved audit program'), choose('decision', 'Human competence determination', ['Competent', 'NotCompetent', 'Pending']), date('validFrom', 'Validity from (UTC)'), date('validTo', 'Validity to, exclusive (UTC)'), paragraph('criteria', 'Exact approved competence criteria'), paragraph('actualAssessment', 'Actual competence assessment'), paragraph('limitations', 'Limits and uncertainty')); ev();
  } else if (type === 'Run') {
    add(...refFields('auditProgram', 'Currently effective audit program'), paragraph('scope', 'Actual audit scope'), date('from', 'Scheduled from (UTC)'), date('to', 'Scheduled to (UTC)'));
    group('auditors', 'Actual assignments and competence references', [text('personId', 'Actual auditor person ID'), paragraph('responsibility', 'Assigned responsibility'), text('competenceAssessmentId', 'Approved competence assessment ID, when required', '', { required: false }), paragraph('impartialityBasis', 'Actual conflict and impartiality basis')], [], 1);
  } else if (type === 'Finding') {
    add(text('criterionCode', 'Approved program criterion code'), choose('kind', 'Finding type', ['Nonconformity', 'Observation', 'OpportunityForImprovement']), paragraph('statement', 'Finding statement'), paragraph('actualObservation', 'Actual observed evidence'), choose('adequacy', 'Qualified human adequacy assessment', ['Adequate', 'Inadequate', 'Partial', 'Unavailable']), paragraph('adequacyBasis', 'Adequacy assessment basis'), paragraph('limitations', 'Evidence limitations'), determination('followUpRequired', 'Require follow-up?'), paragraph('capaIds', 'Actual existing CAPA IDs, one per line', '', { required: false }), paragraph('planningTaskIds', 'Actual owned planning task IDs, one per line', '', { required: false })); ev();
  } else if (type === 'Report') {
    add(paragraph('summary', 'Report summary'), paragraph('scopePerformed', 'Actual scope performed'), paragraph('evidenceLimitations', 'Evidence limits and omissions'), paragraph('humanAssessment', 'Qualified human assessment')); ev();
    group('checklistAnswers', 'Actual answers to the approved checklist; gaps stay explicit', [text('checklistCode', 'Exact approved checklist code'), paragraph('question', 'Approved question (reference)', '', { readonly: true, required: false }), paragraph('actualObservation', 'Actual observation for this question'), choose('adequacy', 'Human evidence adequacy', ['Adequate', 'Inadequate', 'Partial', 'Unavailable']), paragraph('adequacyBasis', 'Qualified human adequacy basis'), paragraph('limitations', 'Actual limits and gaps'), paragraph('evidenceReferences', 'Actual context | owner type | owner ID | exact revision, one source per line', '', { required: false })], (record.content?.checklist ?? []).map(item => ({ checklistCode: item.code, question: item.question })), 1);
  } else if (type === 'Capa') {
    add(text('title', 'CAPA title'), ...refFields('process', 'Currently effective source-control process'), paragraph('problemStatement', 'Actual problem statement'), text('ownerPersonId', 'Actual responsible person ID'), text('dueDate', 'Due date', '', { type: 'date' }));
    for (const name of ['investigationMethod', 'investigationCriteria', 'effectivenessCriteria', 'recurrenceMonitoring']) add(paragraph(name, label(name)));
    add(paragraph('relatedNcrIds', 'Actual related NCR IDs, one per line', '', { required: false }), number('existingCapaId', 'Existing CAPA ID for controlled governed reopening (optional)', '', { required: false }), text('originFindingId', 'Actual original audit finding ID (optional)', '', { required: false, hint: 'Freezes the original finding observation; later reciprocal follow-up links do not rewrite that origin.' })); ev();
  } else if (type === 'Investigation') {
    const c = record.content ?? {};
    for (const name of ['actualInvestigation', 'method', 'causeDecisionBasis', 'actionPlan', 'systemicScope', 'riskOpportunityImpact', 'verificationCriteria']) add(paragraph(name, label(name), c[name]));
    add(paragraph('humanDeterminedCause', 'Human determined cause; leave blank while unknown', c.humanDeterminedCause, { required: false }));
    group('proposals', 'Evidence-supported cause proposals; a proposal is not a confirmed cause', [paragraph('description', 'Proposed cause'), paragraph('supportingBasis', 'Supporting basis'), paragraph('contraryEvidence', 'Contrary evidence reviewed'), text('evidenceContext', 'Actual evidence context'), text('evidenceType', 'Actual evidence type'), text('evidenceKey', 'Actual evidence ID'), text('evidenceRevision', 'Exact evidence revision (optional)', '', { required: false })], (c.proposals ?? []).map(x => ({ ...x, evidenceContext: x.evidence?.[0]?.context, evidenceType: x.evidence?.[0]?.type, evidenceKey: x.evidence?.[0]?.key, evidenceRevision: x.evidence?.[0]?.revision }))); ev(c.evidence);
  } else if (type === 'Action') {
    add(choose('kind', 'Action type', ['Correction', 'Containment', 'CorrectiveAction']), paragraph('description', 'Actual action'), text('ownerPersonId', 'Actual assigned owner ID'), date('dueAt', 'Due (UTC)'), paragraph('resources', 'Required resources'), paragraph('completionCriteria', 'Actual completion criteria'));
  } else if (type === 'Effectiveness') {
    add(date('from', 'Actual evaluation window from (UTC)'), date('to', 'Actual evaluation window to, exclusive (UTC)'), paragraph('method', 'Actual evaluation method'), paragraph('criteria', 'Exact approved case effectiveness criteria', record.effectivenessCriteria), paragraph('actualResults', 'Actual observed results'), choose('result', 'Human effectiveness result', ['Effective', 'Ineffective', 'Inconclusive']), paragraph('limitations', 'Evidence limits and uncertainty'), paragraph('recurrenceReview', 'Actual recurrence monitoring'), paragraph('followUpDecision', 'Human follow-up decision')); ev();
  } else if (type === 'FollowUp') {
    add(paragraph('result', 'Actual follow-up result'), paragraph('capaIds', 'Actual existing CAPA IDs, one per line', lines(record.capaIds), { required: false }), paragraph('planningTaskIds', 'Actual owned planning task IDs, one per line', lines(record.planningTaskIds), { required: false })); ev();
  } else if (type === 'ActionDecision') { add(paragraph('result', 'Actual attributed action result')); ev(); }
  else { add(paragraph('note', 'Human decision basis')); if (type === 'CaseDecision') ev(); }
  return { fields, groups };
}
export function auditFormData(type, v, g) {
  const reference = prefix => ({ code: v[prefix + 'Code'], revision: v[prefix + 'Revision'] });
  if (type === 'Program') return { ...Object.fromEntries(['title', 'scope', 'scheduleBasis', 'frequency', 'ownerPersonId', 'findingFollowUpCriteria', 'closureCriteria'].map(name => [name, v[name]])), processes: g.processes, criteria: g.criteria.map(x => ({ ...x, requirements: parsedRefs(x.requirements), documents: parsedRefs(x.documents, true), requiredEvidenceKinds: values(x.requiredEvidenceKinds) })), checklist: g.checklist, competence: { requireFormalQualifications: bool(v.requireFormalQualifications), qualificationCodes: values(v.qualificationCodes), requireIndependentCompetenceAssessment: bool(v.requireIndependentCompetenceAssessment), competenceCriteria: v.competenceCriteria, reassessmentInterval: v.reassessmentInterval }, impartiality: { prohibitAuditingOwnedProcesses: bool(v.prohibitAuditingOwnedProcesses), prohibitAuditingContributedEvidence: bool(v.prohibitAuditingContributedEvidence), requireIndependentReportApproval: bool(v.requireIndependentReportApproval), conflictAssessmentCriteria: v.conflictAssessmentCriteria }, documents: g.documents, evidence: g.evidence };
  if (type === 'Competence') return { ...v, auditProgram: reference('auditProgram'), validFrom: instant(v.validFrom), validTo: instant(v.validTo), evidence: g.evidence };
  if (type === 'Run') { const auditProgram = reference('auditProgram'); return { auditProgram, scope: v.scope, from: instant(v.from), to: instant(v.to), auditors: g.auditors.map(x => ({ personId: x.personId, responsibility: x.responsibility })), competence: g.auditors.map(x => ({ personId: x.personId, asOf: new Date().toISOString(), auditProgram, competenceAssessmentId: x.competenceAssessmentId ?? null, impartialityBasis: x.impartialityBasis })) }; }
  if (type === 'Finding') return { ...v, followUpRequired: bool(v.followUpRequired), capaIds: positiveIds(v.capaIds), planningTaskIds: values(v.planningTaskIds), evidence: g.evidence };
  if (type === 'Report') return { ...v, evidence: g.evidence, checklistAnswers: g.checklistAnswers.map(x => ({ checklistCode: x.checklistCode, actualObservation: x.actualObservation, adequacy: x.adequacy, adequacyBasis: x.adequacyBasis, limitations: x.limitations, evidence: evidenceLines(x.evidenceReferences) })) };
  if (type === 'Capa') return { ...v, process: reference('process'), relatedNcrIds: positiveIds(v.relatedNcrIds), existingCapaId: v.existingCapaId ?? null, evidence: g.evidence };
  if (type === 'Investigation') return { ...v, humanDeterminedCause: v.humanDeterminedCause ?? null, proposals: g.proposals.map(x => ({ description: x.description, supportingBasis: x.supportingBasis, contraryEvidence: x.contraryEvidence, evidence: [{ context: x.evidenceContext, type: x.evidenceType, key: x.evidenceKey, revision: x.evidenceRevision ?? null }] })), evidence: g.evidence };
  if (type === 'Action') return { ...v, dueAt: instant(v.dueAt) };
  if (type === 'Effectiveness') return { ...v, from: instant(v.from), to: instant(v.to), evidence: g.evidence };
  if (type === 'FollowUp') return { ...v, capaIds: positiveIds(v.capaIds), planningTaskIds: values(v.planningTaskIds), evidence: g.evidence };
  return { ...v, ...(g.evidence ? { evidence: g.evidence } : {}) };
}

const headers = [text('facilityCode', 'Actual owning facility code'), text('programId', 'Owning program ID, if applicable', '', { required: false }), choose('markings', 'Actual handling markings', ['None', 'Proprietary', 'ControlledUnclassified', 'ExportControlled', 'Proprietary, ControlledUnclassified', 'Proprietary, ExportControlled', 'ControlledUnclassified, ExportControlled', 'Proprietary, ControlledUnclassified, ExportControlled'])];
const detailsHtml = value => {
  if (value == null) return '<span class="faint">Not recorded</span>';
  if (Array.isArray(value)) return value.length ? '<ul>' + value.map(x => '<li>' + detailsHtml(x) + '</li>').join('') + '</ul>' : '<span class="faint">No entries</span>';
  if (typeof value === 'object') return '<dl>' + Object.entries(value).map(([name, item]) => '<dt>' + esc(label(name)) + '</dt><dd>' + detailsHtml(item) + '</dd>').join('') + '</dl>';
  return '<span>' + esc(value) + '</span>';
};
export async function qmsAuditPage(page, ctx) {
  const me = ctx.me, auth = { subject: session.subject, token: session.token }, dialogs = new Set();
  let disposed = false, generation = 0, tab = ['programs', 'runs', 'capas'].includes(ctx.query?.get('tab')) ? ctx.query.get('tab') : 'programs', cursor = null, status = {}, listing = {}, selected = null, selectedProgram = null, competence = null, blocked = false;
  const live = token => !disposed && page.isConnected && token === generation && session.subject === auth.subject && session.token === auth.token;
  const closeDialogs = () => { for (const close of [...dialogs]) close(); };
  const routeFor = type => type === 'capas' ? CAPAS : AUDITS + '/' + type;
  async function load(after = null, reopen = true) {
    const token = ++generation, oldId = selected?.capa?.capaId ?? selected?.audit?.id ?? selected?.code; closeDialogs();
    blocked = true; selected = null; selectedProgram = null; render();
    const result = await Promise.all([tryGet(AUDITS + '/status'), tryGet(routeFor(tab) + '?take=30' + (after ? '&after=' + key(after) : ''))]);
    if (!live(token)) return; [status, listing] = result; cursor = after; blocked = !!status.error || !!listing.error; render();
    if (oldId && reopen && !blocked) await open(oldId);
  }
  async function open(id) {
    const token = ++generation; closeDialogs(); selected = null; selectedProgram = null;
    blocked = true; listing.error = null; render();
    const result = await tryGet(routeFor(tab) + '/' + key(id));
    if (!live(token)) return;
    if (result.error) { blocked = true; listing.error = result.error; render(); return; }
    selected = result.data;
    if (selected.audit) {
      const program = await tryGet(AUDITS + '/programs/' + key(selected.audit.auditProgram.code));
      if (!live(token)) return;
      if (program.error) { blocked = true; listing.error = program.error; }
      else { selectedProgram = program.data.revisions.find(x => x.revision === selected.audit.auditProgram.revision); blocked = !selectedProgram; }
    }
    else blocked = false;
    render();
  }
  function form(title, detail, definition, send) {
    closeDialogs(); const token = generation, scrim = document.createElement('div'), modal = document.createElement('div');
    scrim.className = 'scrim'; scrim.setAttribute('aria-hidden', 'true'); modal.className = 'modal';
    modal.innerHTML = '<h2>' + esc(title) + '</h2><p class="muted">' + esc(detail) + '</p><div class="form-grid">' + definition.fields.map(fieldHtml).join('') + '</div>' + definition.groups.map(g => '<section data-group="' + g.name + '"><h3>' + esc(g.title) + '</h3><div data-rows></div><button class="btn small" data-add type="button">Add entry</button></section>').join('') + fieldHtml(paragraph('reason', 'Reason recorded in the audit trail')) + '<div class="notice bad hidden" role="alert" aria-live="assertive" data-error></div><div class="actions"><button class="btn" data-cancel>Cancel</button><button class="btn primary" data-confirm>Record human decision</button></div>';
    let closed = false, busy = false, focus = () => {}, rowId = 0;
    const current = () => !closed && modal.isConnected && live(token) && !blocked;
    const close = () => { if (closed) return; closed = true; scrim.remove(); modal.remove(); dialogs.delete(close); focus(); };
    dialogs.add(close); scrim.onclick = close; document.body.append(scrim, modal); $('[data-cancel]', modal).onclick = close; focus = manageDialogFocus(modal, close);
    for (const g of definition.groups) {
      const area = $('[data-group="' + g.name + '"]', modal), rows = $('[data-rows]', area);
      const add = (value = {}) => {
        if (!current()) return; const row = document.createElement('div'); row.className = 'form-grid'; row.prefix = 'audit-entry-' + rowId++ + '-';
        row.fields = g.columns.map(f => ({ ...f, name: row.prefix + f.name, value: value[f.name] ?? f.value }));
        row.innerHTML = row.fields.map(fieldHtml).join('') + '<button class="btn small" data-remove type="button">Remove entry</button>';
        rows.append(row); $('[data-remove]', row).onclick = () => row.remove();
      };
      for (const row of g.rows ?? []) add(row); $('[data-add]', area).onclick = () => add();
    }
    $('[data-confirm]', modal).onclick = async () => {
      if (!current() || busy) return; busy = true; const button = $('[data-confirm]', modal), error = $('[data-error]', modal); button.disabled = true; error.classList.add('hidden');
      try {
        const v = readFields(modal, definition.fields), reason = readFields(modal, [paragraph('reason', 'Reason')]).reason, groups = {};
        for (const g of definition.groups) { const rows = [...$('[data-group="' + g.name + '"] [data-rows]', modal).children]; if (rows.length < (g.minimum ?? 0)) throw new Error('Record the required ' + g.title.toLowerCase() + '.'); groups[g.name] = rows.map(row => Object.fromEntries(Object.entries(readFields(row, row.fields)).map(([name, value]) => [name.slice(row.prefix.length), value]))); }
        if (!current()) return; const result = await send(v, groups, reason, current);
        if (!current()) return; close(); toast(result.message ?? 'Human decision recorded.'); await load(cursor);
      } catch (failure) {
        if (!current()) return;
        if (failure instanceof TypeError || Number.isInteger(failure.status) && isServiceUnavailable(failure)) { blocked = true; close(); toast('Forge could not confirm this change. Refresh the current owner before retrying.', true); render(); return; }
        error.textContent = failure.message + (failure.status === 409 ? ' Refresh the actual current version before deciding again.' : ''); error.classList.remove('hidden');
      } finally { busy = false; if (current()) button.disabled = false; }
    };
  }
  function guard(current, access, scope) { if (!current() || !allowed(me, access, scope)) throw new Error('This authenticated decision context is no longer active.'); }
  function create(type) {
    const definition = auditFormDefinition(type), access = type === 'Program' || type === 'Run' ? 'manage' : type === 'Competence' ? 'conduct' : 'capa';
    if (type === 'Program') definition.fields.unshift(text('code', 'Audit program code'), ...headers);
    if (type === 'Capa') definition.fields.unshift(...headers);
    form('Create ' + label(type), 'Record actual owner decisions and source evidence. Linked evidence does not establish conformity or competence by itself.', definition, (v, g, reason, current) => {
      guard(current, access, null); const data = auditFormData(type, v, g);
      if (type === 'Program') return command(AUDITS + '/programs', { data: { code: v.code, facilityCode: v.facilityCode, programId: v.programId || null, markings: v.markings, content: data } }, reason);
      if (type === 'Capa') data.programId = v.programId || null;
      return command(type === 'Capa' ? CAPAS : AUDITS + '/' + (type === 'Competence' ? 'competence' : 'runs'), { data }, reason);
    });
  }
  function editProgram(action) {
    const program = selected, revision = program.revisions.at(-1), definition = auditFormDefinition('Program', revision);
    form(label(action) + ' audit program', 'Exact controlled content revision. Previous authors remain contributors.', definition, (v, g, reason, current) => { guard(current, 'manage', program); return command(AUDITS + '/programs/' + key(program.code) + '/revisions' + (action === 'Edit' ? '/' + revision.revision + '/edit' : ''), { data: { expectedVersion: revision.version, content: auditFormData('Program', v, g) } }, reason); });
  }
  function decision(route, action, record, scope, access, extra = {}) {
    const definition = auditFormDefinition(extra.kind ?? 'Decision', record), version = record.version, generationSnapshot = extra.generation ?? record.generation;
    form(label(action) + ' ' + (record.id ?? record.code ?? record.capaId), 'Uses current owner version ' + version + '. The server rechecks current sources, competence and independence before replay.', definition, (v, g, reason, current) => {
      guard(current, access, scope); return command(route + '/' + action.toLowerCase(), { data: { expectedVersion: version, generation: generationSnapshot, ...auditFormData(extra.kind ?? 'Decision', v, g), ...(extra.data ?? {}) } }, reason);
    });
  }
  function work(action) {
    const a = selected.audit, c = selected.capa, target = a ?? c, definition = auditFormDefinition(action, action === 'Effectiveness' ? c : action === 'Report' ? selectedProgram : {}), access = a ? 'conduct' : 'capa';
    let route = a ? AUDITS + '/runs/' + key(a.id) : CAPAS + '/' + key(c.capaId);
    route += '/' + { Finding: 'findings', Report: 'report', Investigation: 'investigations', Action: 'actions', Effectiveness: 'effectiveness' }[action];
    form('Record ' + label(action), 'Record actual observations, human criteria and owner references in generation ' + target.generation + '.', definition, (v, g, reason, current) => {
      guard(current, access, target); let data = auditFormData(action, v, g); const epoch = { generation: target.generation };
      if (action === 'Finding') data = { ...data, ...epoch, expectedAuditVersion: target.version };
      else if (action === 'Report') data = { ...data, ...epoch, expectedVersion: target.version, findingIds: selected.findings.filter(x => x.auditGeneration === target.generation).map(x => x.id) };
      else if (action === 'Investigation') data = { ...epoch, expectedCaseVersion: target.version, revision: null, expectedVersion: null, content: data };
      else data = { ...data, ...epoch, expectedCaseVersion: target.version, ...(action === 'Effectiveness' ? { verifiedActionIds: selected.actions.filter(x => x.state !== 'Withdrawn').map(x => x.id) } : {}) };
      return command(route, { data }, reason);
    });
  }
  function editInvestigation(item) {
    const c = selected.capa, definition = auditFormDefinition('Investigation', item);
    form('Edit actual investigation draft', 'An unknown cause stays blank until the organization determines it from actual evidence.', definition, (v, g, reason, current) => { guard(current, 'capa', c); return command(CAPAS + '/' + c.capaId + '/investigations/' + item.revision + '/edit', { data: { expectedCaseVersion: c.version, generation: c.generation, expectedVersion: item.version, content: auditFormData('Investigation', v, g) } }, reason); });
  }
  function openCompetence() {
    form('Open actual competence assessment', 'Use an ID from a recorded assessment.', { fields: [text('id', 'Recorded competence assessment ID')], groups: [] }, async (v, g, reason, current) => { if (!current()) return; const result = await tryGet(AUDITS + '/competence/' + key(v.id)); if (!current()) return; if (result.error) throw result.error; competence = result.data; render(); return { message: 'Actual assessment read.' }; });
  }
  function render() {
    if (!live(generation)) return;
    const items = listing.data?.items ?? [], actions = selected?.revisions ? auditProgramActions(me, selected, selected.revisions.at(-1)) : selected?.audit ? auditRunActions(me, selected.audit, selectedProgram) : selected?.capa ? capaActions(me, selected) : [];
    const selectedTitle = selected?.code ?? selected?.audit?.id ?? selected?.capa?.capaId;
    page.innerHTML = '<div class="page-head"><div><h1>Internal audit and corrective action</h1><p class="sub">Actual competence, explicit human evidence adequacy, investigations and verified effectiveness.</p></div><button class="btn" data-refresh>Refresh</button></div>' + (status.error ? errorBlock(status.error, 'QMS organization') : '<div class="notice">' + (status.data?.configured ? 'Configured organization: ' + esc(status.data.organizationCode) : 'The actual QMS organization must be configured.') + ' · Local approval retains pending licensed verification.</div>') +
      '<section class="panel"><div class="seg">' + ['programs', 'runs', 'capas'].map(type => '<button data-tab="' + type + '" class="' + (tab === type ? 'on' : '') + '">' + label(type) + '</button>').join('') + '</div><div class="toolbar">' + (!blocked && status.data?.configured && allowed(me, tab === 'capas' ? 'capa' : 'manage', null) ? '<button class="btn primary" data-create="' + { programs: 'Program', runs: 'Run', capas: 'Capa' }[tab] + '">Create ' + { programs: 'audit program', runs: 'actual scheduled audit', capas: 'governed CAPA' }[tab] + '</button>' : '') + (!blocked && allowed(me, 'conduct', null) ? '<button class="btn" data-competence-create>Record competence assessment</button>' : '') + '<button class="btn" data-competence-open>Open competence ID</button></div>' +
      (listing.error ? errorBlock(listing.error, 'actual audit/CAPA records') : items.length ? '<table class="table"><thead><tr><th>Owner</th><th>Scope</th><th>State</th><th></th></tr></thead><tbody>' + items.map(item => '<tr><td>' + esc(item.code ?? item.capaId ?? item.id) + '</td><td>' + esc(item.facilityCode) + ' · ' + esc(item.programId ?? 'No program') + ' · ' + esc(item.markings) + '</td><td>' + esc(item.state ?? item.revisions?.at(-1)?.state) + '</td><td><button class="btn small" data-open="' + esc(item.code ?? item.capaId ?? item.id) + '">Open</button></td></tr>').join('') + '</tbody></table>' : emptyBlock('No visible actual records')) + '<div class="toolbar">' + (cursor ? '<button class="btn" data-first>First visible page</button>' : '') + (listing.data?.nextCursor ? '<button class="btn" data-next>Next visible page</button>' : '') + '</div></section>' +
      (selected ? '<section class="panel"><h2>' + esc(selectedTitle) + '</h2>' + (selected.capa ? '<p>Existing CAPA ID ' + selected.capa.capaId + ' · ' + esc(selected.capa.state) + ' · generation ' + selected.capa.generation + ' · version ' + selected.capa.version + '</p><p>Cause knowledge: ' + esc(selected.capa.causeKnowledge) + '. ' + esc(selected.capa.confirmedCause ?? 'Cause remains unknown or proposed; no confirmed cause recorded.') + '</p>' : '') + '<div class="toolbar">' + (!blocked ? actions.map(action => '<button class="btn" data-owner-action="' + action + '">' + label(action) + '</button>').join('') : '') + '</div>' + detailsHtml(selected.revisions ? selected : selected.audit ?? selected.capa) + '</section>' + childrenHtml() : '') +
      (competence ? '<section class="panel"><h2>Actual auditor competence ' + esc(competence.id) + '</h2>' + detailsHtml(competence) + (!blocked && !competence.approvedBy && allowed(me, 'approve', null) && independent(me, [competence.assessedBy, 'person:' + competence.personId], competence.evidence) ? '<button class="btn" data-competence-approve>Independently approve competence</button>' : '') + '</section>' : '');
    $('[data-refresh]', page).onclick = () => load(cursor);
    $$('[data-tab]', page).forEach(b => b.onclick = () => { tab = b.dataset.tab; selected = null; competence = null; void load(); });
    $$('[data-open]', page).forEach(b => b.onclick = () => open(b.dataset.open));
    const createButton = $('[data-create]', page); createButton?.addEventListener('click', () => create(createButton.dataset.create));
    $('[data-first]', page)?.addEventListener('click', () => load(null, false)); $('[data-next]', page)?.addEventListener('click', () => load(listing.data.nextCursor, false));
    $('[data-competence-create]', page)?.addEventListener('click', () => create('Competence')); $('[data-competence-open]', page).onclick = openCompetence;
    $('[data-competence-approve]', page)?.addEventListener('click', () => decision(AUDITS + '/competence/' + competence.id, 'Approve', competence, null, 'approve'));
    $$('[data-owner-action]', page).forEach(b => b.onclick = () => {
      const action = b.dataset.ownerAction;
      if (selected.revisions) { const r = selected.revisions.at(-1); if (['Edit', 'NewRevision'].includes(action)) editProgram(action); else decision(AUDITS + '/programs/' + key(selected.code) + '/revisions/' + r.revision, action, r, selected, action === 'Submit' ? 'manage' : 'approve'); }
      else if (['Finding', 'Report', 'Investigation', 'Action', 'Effectiveness'].includes(action)) work(action);
      else if (selected.audit) decision(AUDITS + '/runs/' + selected.audit.id, action, selected.audit, selected.audit, ['ApproveReport', 'Close'].includes(action) ? 'approve' : 'conduct');
      else decision(CAPAS + '/' + selected.capa.capaId, action, selected.capa, selected.capa, action === 'Implemented' ? 'capa' : 'capaApprove', { kind: 'CaseDecision', data: { effectivenessId: selected.capa.latestEffectivenessId } });
    });
    $$('[data-finding-action]', page).forEach(b => b.onclick = () => { const f = selected.findings.find(x => x.id === b.dataset.id); decision(AUDITS + '/findings/' + f.id, b.dataset.findingAction, f, selected.audit, b.dataset.findingAction === 'Verify' ? 'approve' : 'conduct', { kind: 'FollowUp', generation: selected.audit.generation }); });
    for (const family of ['investigation', 'action', 'effectiveness']) $$('[data-' + family + '-action]', page).forEach(b => b.onclick = () => {
      const item = selected[family === 'investigation' ? 'investigations' : family === 'action' ? 'actions' : 'effectiveness'].find(x => String(family === 'investigation' ? x.revision : x.id) === b.dataset.id), action = b.dataset[family + 'Action'];
      if (action === 'Edit') { editInvestigation(item); return; }
      const route = family === 'investigation' ? CAPAS + '/' + selected.capa.capaId + '/investigations/' + item.revision : CAPAS + '/' + (family === 'action' ? 'actions' : 'effectiveness') + '/' + item.id;
      decision(route, action, item, selected.capa, ['Review', 'Approve', 'Verify'].includes(action) ? 'capaApprove' : 'capa', { kind: family === 'action' ? 'ActionDecision' : 'Decision', generation: selected.capa.generation, data: family === 'action' ? { actionGeneration: item.actionGeneration } : {} });
    });
  }
  function childrenHtml() {
    if (selected.audit) return selected.findings.map(f => '<article class="panel"><h3>Finding ' + esc(f.id) + '</h3><p>Human adequacy: ' + esc(f.adequacy) + ' · ' + esc(f.adequacyBasis) + '</p>' + detailsHtml(f) + (!blocked && f.auditGeneration === selected.audit.generation && f.followUpRequired ? '<div class="toolbar">' + (allowed(me, 'conduct', selected.audit) ? '<button class="btn" data-finding-action="Record" data-id="' + f.id + '">Record actual follow-up</button>' : '') + (allowed(me, 'approve', selected.audit) && f.followUpResult && independent(me, [f.recordedBy, ...f.history.filter(x => x.action === 'FollowUp').map(x => x.actor)], f.evidence) ? '<button class="btn" data-finding-action="Verify" data-id="' + f.id + '">Independently verify follow-up</button>' : '') + '</div>' : '') + '</article>').join('');
    if (!selected.capa) return '';
    return ['investigation', 'action', 'effectiveness'].map(family => selected[family === 'investigation' ? 'investigations' : family === 'action' ? 'actions' : 'effectiveness'].map(item => {
      const actions = [];
      if (!blocked && allowed(me, 'capa', selected.capa)) { if (family === 'investigation' && item.state === 'Draft') actions.push('Edit', 'Submit'); if (family === 'effectiveness' && item.state === 'Draft') actions.push('Submit'); if (family === 'action' && ['Open', 'Reopened'].includes(item.state) && item.ownerPersonId === me.personId) actions.push('Complete'); if (family === 'action' && ['Completed', 'Verified'].includes(item.state)) actions.push('Reopen'); }
      const contribution = family === 'action' ? [item.createdBy, 'person:' + item.ownerPersonId, item.completedBy] : [...authors(item), selected.capa.createdBy, ...sourceActors(selected.capa.sources), ...(family === 'effectiveness' ? selected.actions.flatMap(x => [x.createdBy, 'person:' + x.ownerPersonId, x.completedBy]) : [])];
      if (!blocked && allowed(me, 'capaApprove', selected.capa) && independent(me, contribution, item.sources ?? item.evidence)) { if (family !== 'action' && item.state === 'Submitted') actions.push('Review'); if (family !== 'action' && item.state === 'Reviewed') actions.push('Approve'); if (family === 'action' && item.state === 'Completed') actions.push('Verify'); }
      return '<article class="panel"><h3>' + label(family) + ' ' + esc(item.id ?? item.revision) + '</h3>' + detailsHtml(item) + '<div class="toolbar">' + actions.map(action => '<button class="btn" data-' + family + '-action="' + action + '" data-id="' + esc(item.id ?? item.revision) + '">' + label(action) + '</button>').join('') + '</div></article>';
    }).join('')).join('');
  }
  await load();
  if (tab === 'capas' && ctx.query?.get('capaId') && !blocked) await open(ctx.query.get('capaId'));
  return { dispose: () => { disposed = true; generation++; closeDialogs(); selected = null; selectedProgram = null; competence = null; } };
}
