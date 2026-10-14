import { command, tryGet, download, session as signIn } from '../api.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock, toast, manageDialogFocus } from '../ui.js';
import { fieldHtml, readFields, canAction } from '../forms.js';
import { who } from '../people.js';
import { isServiceUnavailable } from '../terminal-state.mjs';

const KINDS = ['Profile', 'Process', 'Policy', 'Requirement'];
const POLICY_KINDS = ['Quality', 'EthicalBehavior', 'Execution', 'ProductRelease', 'LabelCopy', 'AttemptRetention', 'OtherOrganization'];
const REQUIREMENT_CLASSES = ['ISO', 'Industry', 'Customer', 'Organization', 'Optional', 'Statutory'];
const APPLICABILITY = ['Applicable', 'NotApplicable', 'PendingDetermination'];
const VERIFICATION = ['PendingAuthorizedVerification', 'VerifiedAuthorizedCopy'];
const PERMISSION = { manage: 'quality.qms.manage', review: 'quality.qms.review', approve: 'quality.qms.approve', publish: 'quality.qms.publish' };
const labels = value => String(value).replace(/([a-z])([A-Z])/g, '$1 $2');
const text = (name, label, extra = {}) => ({ name, label, required: true, ...extra });
const paragraph = (name, label, extra = {}) => text(name, label, { type: 'textarea', ...extra });
const choice = (name, label, values, extra = {}) => text(name, label, { type: 'select', options: [['', '— choose —'], ...values.map(value => [value, labels(value)])], ...extra });
const determination = (name, label, value = null) => choice(name, label, ['true', 'false'], { options: [['', '— record a determination —'], ['true', 'Yes'], ['false', 'No']], value: value == null ? '' : String(value) });
const lines = value => (value ?? []).join('\n');
const lineValues = value => (value ?? '').split(/\r?\n/).map(line => line.trim()).filter(Boolean);
const booleanValue = value => value === 'true' ? true : value === 'false' ? false : null;
const key = value => encodeURIComponent(value);
const refs = [{ name: 'code', label: 'QMS record code', required: true }, { name: 'revision', label: 'Exact revision', type: 'number', min: 1, required: true }];
const documentFields = [{ name: 'documentNumber', label: 'Engineering document number', required: true }, { name: 'revision', label: 'Exact document revision', required: true }];
const RULE_FIELDS = {
  Execution: ['requireQualification', 'requirePresentOperator', 'requireReleasedDocuments', 'revalidateAtCommit'],
  ProductRelease: ['requireIndependentDecision', 'requireRequiredInspectionEvidence', 'requireDispositionedNonconformities'],
  LabelCopy: ['requireCopyAuthorization', 'retainCopyReason'],
  AttemptRetention: ['preserveDeniedAttempts', 'preserveSourceLink', 'neverRewriteAuditChain'],
};
const ruleNames = { requireQualification: 'Require qualification', requirePresentOperator: 'Require a present operator', requireReleasedDocuments: 'Require released documents', revalidateAtCommit: 'Revalidate when committing', requireIndependentDecision: 'Require an independent release decision', requireRequiredInspectionEvidence: 'Require the specified inspection evidence', requireDispositionedNonconformities: 'Require dispositioned nonconformities', requireCopyAuthorization: 'Require copy authorization', retainCopyReason: 'Retain the reason for a copy', preserveDeniedAttempts: 'Preserve denied attempts', preserveSourceLink: 'Preserve the authoritative source link', neverRewriteAuditChain: 'Preserve the immutable audit chain' };

function ownerField(directory, value) {
  const people = directory?.data;
  if (!people?.length) return text('ownerPersonId', 'Responsible person ID', { value, hint: 'An active Forge person ID. The server checks the responsible person.' });
  const options = [['', '— select a responsible person —'], ...people.map(person => [person.personId, person.displayName])];
  if (value && !people.some(person => person.personId === value)) options.push([value, `Recorded person ${value}`]);
  return text('ownerPersonId', 'Responsible person', { type: 'select', options, value });
}

function contentDefinition(kind, content, directory) {
  const section = content?.[kind.toLowerCase()] ?? {}, fields = [], groups = [];
  const add = (...values) => fields.push(...values);
  const group = (name, label, definitions, values = [], minimum = 0) => groups.push({ name, label, fields: definitions, values, minimum });
  if (kind === 'Profile') {
    add(text('organizationName', 'Organization name', { value: section.organizationName }), paragraph('scope', 'QMS scope', { value: section.scope }),
      paragraph('products', 'Products in scope (one per line)', { required: false, value: lines(section.products) }), paragraph('services', 'Services in scope (one per line)', { required: false, value: lines(section.services) }),
      determination('climateRelevant', 'Is climate change relevant to this organization’s QMS?', section.climateRelevant), paragraph('climateBasis', 'Basis for the climate relevance determination', { value: section.climateBasis }),
      paragraph('customerRequirements', 'Customer requirements (one per line)', { required: false, value: lines(section.customerRequirements) }), paragraph('statutoryRequirements', 'Statutory requirements (one per line)', { required: false, value: lines(section.statutoryRequirements) }));
    group('issues', 'Context issues', [text('kind', 'Kind'), paragraph('description', 'Issue'), paragraph('basis', 'Human basis')], section.issues);
    group('interestedParties', 'Interested parties', [text('name', 'Party'), paragraph('requirements', 'Relevant requirements'), paragraph('basis', 'Human basis')], section.interestedParties);
    group('nonApplicability', 'Justified nonapplicability', [text('requirementCode', 'Requirement record code'), text('revision', 'Exact requirement revision', { type: 'number', min: 1 }), paragraph('basis', 'Justification')], section.nonApplicability);
    group('processes', 'Processes in scope', refs, section.processes); group('requirements', 'Requirement revisions in scope', refs, section.requirements);
  } else if (kind === 'Process') {
    add(text('name', 'Process name', { value: section.name }), ownerField(directory, section.ownerPersonId),
      paragraph('inputs', 'Inputs (one per line)', { value: lines(section.inputs) }), paragraph('outputs', 'Outputs (one per line)', { value: lines(section.outputs) }),
      paragraph('resources', 'Resources (one per line)', { value: lines(section.resources) }), paragraph('riskConsiderations', 'Risk considerations (one per line)', { required: false, value: lines(section.riskConsiderations) }));
    group('dependencies', 'Exact process dependencies', refs, section.dependencies);
    group('monitoring', 'Monitoring definitions', [text('name', 'Measure'), paragraph('method', 'Method'), text('dataSource', 'Authoritative data source'), text('reviewInterval', 'Organization-defined review interval')], section.monitoring, 1);
  } else if (kind === 'Policy') {
    add(choice('policyKind', 'Policy kind', POLICY_KINDS, { value: section.policyKind }), paragraph('statement', 'Policy statement', { value: section.statement }), ownerField(directory, section.ownerPersonId),
      paragraph('communicationMethod', 'Communication method', { value: section.communicationMethod }), text('reviewInterval', 'Organization-defined review interval', { value: section.reviewInterval }), determination('acknowledgmentRequired', 'Require acknowledgment?', section.acknowledgmentRequired));
  } else {
    add(text('title', 'Requirement title', { value: section.title }), choice('classification', 'Requirement classification', REQUIREMENT_CLASSES, { value: section.classification }),
      paragraph('requirementText', 'Organization’s requirement or interpretation', { value: section.requirementText }), choice('applicability', 'Applicability determination', APPLICABILITY, { value: section.applicability }),
      paragraph('applicabilityBasis', 'Human basis for applicability', { value: section.applicabilityBasis }), ownerField(directory, section.ownerPersonId));
    group('standardReferences', 'Edition-qualified standard references', [text('standard', 'Standard identifier'), text('edition', 'Edition'), text('clause', 'Clause reference'), choice('verification', 'Authorized-source verification', VERIFICATION), paragraph('verificationBasis', 'Verification basis'), text('verificationDocumentNumber', 'Verification document number', { required: false }), text('verificationDocumentRevision', 'Exact verification document revision', { required: false })], (section.standardReferences ?? []).map(ref => ({ ...ref, verificationDocumentNumber: ref.verificationDocument?.documentNumber, verificationDocumentRevision: ref.verificationDocument?.revision })));
  }
  group('documents', 'Controlled Engineering document references', documentFields, content?.documents);
  return { fields, groups, section };
}

function policyRuleFields(kind, rules = {}) {
  const values = rules?.[kind.charAt(0).toLowerCase() + kind.slice(1)] ?? {};
  const fields = (RULE_FIELDS[kind] ?? []).map(name => determination(name, ruleNames[name], values[name]));
  if (kind === 'ProductRelease') fields.push(text('releasePermission', 'Product release action', { value: 'quality.product.release', readonly: true, hint: 'Records a quality product-release decision separately from shipping. Existing evidence and source-authority checks remain enforced.' }));
  if (kind === 'LabelCopy') fields.push(text('maximumCopiesPerRequest', 'Maximum copies per request', { type: 'number', min: 1, value: values.maximumCopiesPerRequest }));
  if (kind === 'AttemptRetention') fields.push(text('minimumRetentionDays', 'Minimum evidence retention (days)', { type: 'number', min: 1, value: values.minimumRetentionDays }), text('maximumRawAttemptAgeDays', 'Maximum raw-attempt age (days)', { type: 'number', min: 1, value: values.maximumRawAttemptAgeDays }), text('maximumStoredRawAttempts', 'Maximum stored raw attempts', { type: 'number', min: 1, value: values.maximumStoredRawAttempts }));
  return fields;
}

function contentFrom(kind, values, groups, ruleValues) {
  const documents = groups.documents, section = { ...values };
  if (kind === 'Profile') {
    for (const name of ['products', 'services', 'customerRequirements', 'statutoryRequirements']) section[name] = lineValues(values[name]);
    section.climateRelevant = booleanValue(values.climateRelevant);
    for (const name of ['issues', 'interestedParties', 'nonApplicability', 'processes', 'requirements']) section[name] = groups[name];
  } else if (kind === 'Process') {
    for (const name of ['inputs', 'outputs', 'resources', 'riskConsiderations']) section[name] = lineValues(values[name]);
    section.dependencies = groups.dependencies; section.monitoring = groups.monitoring;
  } else if (kind === 'Policy') {
    section.acknowledgmentRequired = booleanValue(values.acknowledgmentRequired);
    for (const name of RULE_FIELDS[values.policyKind] ?? []) ruleValues[name] = booleanValue(ruleValues[name]);
    if (values.policyKind === 'ProductRelease') ruleValues.releasePermission = 'quality.product.release';
    section.rules = RULE_FIELDS[values.policyKind] ? { [values.policyKind.charAt(0).toLowerCase() + values.policyKind.slice(1)]: ruleValues } : null;
  } else section.standardReferences = groups.standardReferences.map(ref => {
    const { verificationDocumentNumber, verificationDocumentRevision, ...reference } = ref;
    if (!!verificationDocumentNumber !== !!verificationDocumentRevision) throw new Error('A verification document needs both its number and exact revision.');
    return { ...reference, verificationDocument: verificationDocumentNumber ? { documentNumber: verificationDocumentNumber, revision: verificationDocumentRevision } : null };
  });
  return { [kind.toLowerCase()]: section, documents };
}

function sectionHtml(title, values) {
  if (values == null) return '';
  if (Array.isArray(values)) return `<h3>${esc(title)}</h3>${values.length ? `<ul>${values.map(value => `<li>${typeof value === 'object' ? Object.entries(value).filter(([, content]) => content != null).map(([name, content]) => `${esc(labels(name))}: ${typeof content === 'object' ? esc(Object.values(content).join(' · ')) : esc(labels(content))}`).join(' · ') : esc(value)}</li>`).join('')}</ul>` : '<div class="faint">None recorded.</div>'}`;
  return `<div class="field"><b>${esc(title)}</b><div style="white-space:pre-line">${esc(typeof values === 'boolean' ? values ? 'Yes' : 'No' : labels(values))}</div></div>`;
}

function contentHtml(kind, content) {
  const section = content?.[kind.toLowerCase()] ?? {};
  return Object.entries(section).map(([name, value]) => name === 'rules' ? Object.entries(value ?? {}).map(([ruleKind, rules]) => `<h3>${esc(labels(ruleKind))} determinations</h3>${Object.entries(rules).map(([rule, setting]) => sectionHtml(labels(rule), setting)).join('')}`).join('') : sectionHtml(labels(name), value)).join('') + sectionHtml('Controlled document references', content?.documents ?? []);
}

const authored = (revision, actor) => [revision.createdBy, revision.lastEditedBy,
  ...(revision.history ?? []).filter(entry => ['Create', 'Edit'].includes(entry.action)).map(entry => entry.actor)].includes(actor);

export async function qmsPage(page, ctx) {
  const me = ctx.me, auth = { subject: signIn.subject, token: signIn.token };
  let disposed = false, generation = 0, kind = KINDS.includes(ctx.query?.get('kind')) ? ctx.query.get('kind') : 'Profile';
  let data = {}, selectedCode = null, selectedRevision = null;
  const dialogs = new Set();
  const current = token => !disposed && page.isConnected && auth.subject === signIn.subject && auth.token === signIn.token && token === generation;
  const closeDialogs = () => { for (const close of [...dialogs]) close(); };
  const scope = record => ({ facilityId: record.facilityCode, programId: record.programId });
  const allowed = (action, record = null) => canAction(me, PERMISSION[action], record ? scope(record) : null);
  const myActor = `person:${me.personId}`;

  async function load() {
    const token = ++generation; closeDialogs();
    const [status, records, directory] = await Promise.all([tryGet('/api/quality/qms/status'), tryGet(`/api/quality/qms/records?kind=${kind}`), tryGet('/api/kernel/directory')]);
    if (!current(token)) return;
    data = { status, records, directory }; render();
  }

  function dialog(title, detail, html, confirmLabel, send) {
    closeDialogs();
    const token = generation, scrim = document.createElement('div'), modal = document.createElement('div');
    scrim.className = 'scrim'; scrim.setAttribute('aria-hidden', 'true'); modal.className = 'modal';
    modal.innerHTML = `<h2>${esc(title)}</h2><div class="muted">${esc(detail)}</div>${html}
      ${fieldHtml(paragraph('qmsReason', 'Reason recorded in the audit trail'))}<div class="notice bad hidden" data-qms-error role="alert" aria-live="assertive"></div>
      <div class="actions"><button class="btn" data-cancel>Cancel</button><button class="btn primary" data-qms-confirm>${esc(confirmLabel)}</button></div>`;
    let closed = false, busy = false, releaseFocus = () => {};
    const live = () => !closed && modal.isConnected && current(token);
    const close = () => { if (closed) return; closed = true; scrim.remove(); modal.remove(); dialogs.delete(close); releaseFocus(); };
    dialogs.add(close); scrim.onclick = close; document.body.append(scrim, modal);
    $('[data-cancel]', modal).addEventListener('click', close);
    const button = $('[data-qms-confirm]', modal), error = $('[data-qms-error]', modal);
    button.addEventListener('click', async () => {
      if (!live() || busy) return;
      error.classList.add('hidden');
      busy = true; button.disabled = true;
      try {
        const reason = readFields(modal, [paragraph('qmsReason', 'Reason')]).qmsReason;
        const result = await send(modal, reason, live);
        if (!live()) return;
        close(); toast(result.message ?? 'Saved.'); await load();
      } catch (failure) {
        if (!live()) return;
        if (isServiceUnavailable(failure) && Number.isInteger(failure.status)) {
          close(); toast('Forge could not confirm that change. Check the record before retrying.', true); return;
        }
        if (failure instanceof TypeError) { close(); toast('Forge could not confirm that change. Check the record before retrying.', true); return; }
        error.textContent = `${failure.message}${failure.status === 409 ? ' Refresh the record before making another decision.' : ''}`; error.classList.remove('hidden');
      } finally { busy = false; if (live()) button.disabled = false; }
    });
    releaseFocus = manageDialogFocus(modal, close);
    return { modal, close, live };
  }

  function edit(record = null, revision = null, newRevision = false) {
    const selectedKind = record?.kind ?? kind;
    if (!allowed('manage', record)) return;
    const definition = contentDefinition(selectedKind, revision?.content, data.directory);
    const header = record ? [] : [text('code', 'QMS record code', { code: true }), text('facilityCode', 'Actual facility code', { code: true }), text('programId', 'Program ID (optional)', { required: false, hint: 'Use the actual restricted program ID where this record belongs.' }), choice('markings', 'Data markings', ['None', 'ControlledUnclassified', 'ExportControlled', 'Proprietary', 'ControlledUnclassified, ExportControlled', 'ControlledUnclassified, Proprietary', 'ExportControlled, Proprietary', 'ControlledUnclassified, ExportControlled, Proprietary'])];
    const groupHtml = definition.groups.map(group => `<section class="panel" data-qms-group="${group.name}"><h3>${esc(group.label)}</h3><div data-qms-rows></div><button class="btn small" type="button" data-qms-add="${group.name}">Add ${esc(group.label.toLowerCase())}</button></section>`).join('');
    const form = dialog(record ? `${newRevision ? 'New revision of' : 'Edit'} ${record.code} revision ${revision.revision}` : `Create ${selectedKind.toLowerCase()}`, record ? 'The record’s organization, facility, program and markings remain fixed. Only this draft content changes.' : 'Define the organization’s actual scope and content. This draft requires its recorded review and approval before it becomes effective.',
      `<div class="form-grid">${[...header, ...definition.fields].map(fieldHtml).join('')}</div>${selectedKind === 'Policy' ? '<section data-qms-policy-rules></section>' : ''}${groupHtml}`, 'Save draft', async (modal, reason, live) => {
        const headers = readFields(modal, header), values = readFields(modal, definition.fields), groups = {};
        for (const group of definition.groups) {
          const rows = [...modal.querySelector(`[data-qms-group="${group.name}"]`).querySelector('[data-qms-rows]').children];
          if (rows.length < group.minimum) throw new Error(`Record at least ${group.minimum} ${group.label.toLowerCase()}.`);
          groups[group.name] = rows.map(row => Object.fromEntries(Object.entries(readFields(row, row.qmsFields)).map(([name, value]) => [name.slice(row.qmsPrefix.length), value])));
        }
        const policyFields = selectedKind === 'Policy' ? modal.querySelector('[data-qms-policy-rules]').qmsFields : [];
        const content = contentFrom(selectedKind, values, groups, readFields(modal, policyFields));
        if (!live() || !allowed('manage', record)) throw new Error('This editing context is no longer active.');
        const route = record ? `/api/quality/qms/records/${key(record.code)}/revisions${newRevision ? '' : `/${revision.revision}/edit`}` : '/api/quality/qms/records';
        return command(route, record ? { expectedVersion: revision.version, content } : { ...headers, programId: headers.programId || null, kind: selectedKind, content }, reason);
      });
    const modal = form.modal;
    let nextRow = 0;
    for (const group of definition.groups) {
      const section = $(`[data-qms-group="${group.name}"]`, modal), rows = $('[data-qms-rows]', section);
      const add = (value = {}) => {
        if (!form.live()) return;
        const row = document.createElement('div'); row.className = 'form-grid';
        const rowPrefix = `${group.name}-${nextRow++}-`;
        row.qmsPrefix = rowPrefix; row.qmsFields = group.fields.map(field => ({ ...field, name: `${rowPrefix}${field.name}`, value: value[field.name] }));
        row.innerHTML = `${row.qmsFields.map(fieldHtml).join('')}<button class="btn small ghost" type="button" data-qms-remove>Remove this entry</button>`;
        rows.append(row); $('[data-qms-remove]', row).addEventListener('click', () => row.remove());
      };
      for (const value of group.values ?? []) add(value);
      $('[data-qms-add]', section).addEventListener('click', () => add());
    }
    if (selectedKind === 'Policy') {
      const rules = $('[data-qms-policy-rules]', modal), select = $('#f-policyKind', modal);
      const showRules = () => {
        const fields = policyRuleFields(select.value, definition.section.rules); rules.qmsFields = fields;
        rules.innerHTML = fields.length ? `<h3>${esc(labels(select.value))} determinations</h3><div class="faint">Record each organization determination explicitly. Existing manufacturing and audit safeguards remain enforced.</div>${fields.map(fieldHtml).join('')}` : '<div class="faint">This policy kind has no typed operational rules.</div>';
      };
      select.addEventListener('change', showRules); showRules();
    }
  }

  function transition(record, revision, action) {
    const permission = ['effective', 'withdraw'].includes(action) ? 'publish' : action === 'submit' ? 'manage' : action;
    if (!allowed(permission, record) || (['review', 'approve'].includes(action) && authored(revision, myActor))) return;
    const fields = [paragraph('qmsNote', `${labels(action)} decision note`)];
    dialog(`${labels(action)} ${record.code} revision ${revision.revision}`, `This action uses recorded version ${revision.version}. The server rechecks the current record, exact sources, permissions and separation of duties.`, fields.map(fieldHtml).join(''), labels(action), (modal, reason, live) => {
      const note = readFields(modal, fields).qmsNote;
      if (!live() || !allowed(permission, record)) throw new Error('This decision context is no longer active.');
      return command(`/api/quality/qms/records/${key(record.code)}/revisions/${revision.revision}/${action}`, { expectedVersion: revision.version, note }, reason);
    });
  }

  async function openRecord(code) {
    const token = ++generation; closeDialogs();
    const result = await tryGet(`/api/quality/qms/records/${key(code)}`);
    if (!current(token)) return;
    if (result.error) { toast(result.error.message, true); return; }
    const record = { ...result.data.record, revisions: result.data.revisions };
    data.records.data = (data.records.data ?? []).filter(item => item.code !== code).concat(record);
    selectedCode = code; selectedRevision = record.revisions.at(-1)?.revision; render();
  }

  async function openSource(source) {
    const token = ++generation; closeDialogs();
    const scrim = document.createElement('div'), modal = document.createElement('div');
    scrim.className = 'scrim'; scrim.setAttribute('aria-hidden', 'true'); modal.className = 'modal';
    modal.innerHTML = `<h2>${esc(source.key)} — exact revision ${esc(source.revision)}</h2><div data-source-content role="status" aria-live="polite">Reading the current authorized source…</div><div class="actions"><button class="btn" data-source-close>Close</button></div>`;
    let closed = false, releaseFocus = () => {};
    const live = () => !closed && modal.isConnected && current(token);
    const close = () => { if (closed) return; closed = true; scrim.remove(); modal.remove(); dialogs.delete(close); releaseFocus(); };
    dialogs.add(close); scrim.onclick = close; document.body.append(scrim, modal); $('[data-source-close]', modal).addEventListener('click', close);
    releaseFocus = manageDialogFocus(modal, close);
    const route = source.kind === 'QmsRevision' ? `/api/quality/qms/records/${key(source.key)}/revisions/${key(source.revision)}` : `/api/engineering/documents/${key(source.key)}`;
    const result = await tryGet(route);
    if (!live()) return;
    const target = $('[data-source-content]', modal);
    if (result.error) { target.innerHTML = errorBlock(result.error, 'the recorded source revision'); return; }
    if (source.kind === 'QmsRevision') {
      const revision = result.data, kind = KINDS.find(name => revision.content?.[name.toLowerCase()]);
      target.innerHTML = `<div class="notice">${esc(revision.state)} · version ${revision.version}${revision.requiresLicensedVerification ? ' · RequiresLicensedVerification' : ''}</div>${contentHtml(kind, revision.content)}`;
    } else {
      const document = result.data.document, revision = document?.revisions?.find(item => item.revision === source.revision);
      target.innerHTML = revision ? `<div class="notice">${esc(document.title)} · ${esc(document.authority)} · ${esc(revision.status)}</div>${sectionHtml('Effective from', fmtDateTime(revision.effectiveFrom))}${sectionHtml('Effective to', fmtDateTime(revision.effectiveTo))}${sectionHtml('Recorded file hash', revision.fileSha256 ?? 'No file recorded')}${revision.fileSha256 ? '<button class="btn" data-source-file>Open exact revision file</button>' : ''}` : emptyBlock('Revision unavailable', 'The exact recorded revision is not returned by the source.');
      $('[data-source-file]', modal)?.addEventListener('click', async () => {
        if (!live()) return;
        try { await download(`/api/engineering/documents/${key(source.key)}/revisions/${key(source.revision)}/file`, revision.fileName ?? `${source.key}-${source.revision}`); }
        catch (failure) { if (live()) toast(failure.message, true); }
      });
    }
  }

  function revisionHtml(record, revision) {
    const author = authored(revision, myActor), actions = [];
    if (allowed('manage', record) && revision.state === 'Draft') actions.push(['edit', 'Edit draft'], ['submit', 'Submit for review']);
    if (allowed('review', record) && revision.state === 'Submitted' && !author) actions.push(['review', 'Record review']);
    if (allowed('approve', record) && revision.state === 'Reviewed' && !author) actions.push(['approve', 'Approve']);
    if (allowed('publish', record) && revision.state === 'Approved') actions.push(['effective', 'Make effective']);
    if (allowed('publish', record) && !['Superseded', 'Withdrawn'].includes(revision.state)) actions.push(['withdraw', 'Withdraw']);
    if (allowed('manage', record) && revision.revision === Math.max(...record.revisions.map(item => item.revision)) && !record.revisions.some(item => ['Draft', 'Submitted', 'Reviewed', 'Approved'].includes(item.state))) actions.push(['new-revision', 'Create a new draft revision']);
    return `<section class="panel"><header><h2>${esc(record.code)} revision ${revision.revision}</h2><span class="spacer"></span><span class="pill info">${esc(revision.state)}</span></header>
      <div class="muted">Version ${revision.version} · authored by ${esc(who(revision.createdBy))} · last edited by ${esc(who(revision.lastEditedBy))}${revision.reviewedBy ? ` · reviewed by ${esc(who(revision.reviewedBy))}` : ''}${revision.approvedBy ? ` · approved by ${esc(who(revision.approvedBy))}` : ''}</div>
      <div class="muted">Effective from ${esc(fmtDateTime(revision.effectiveFrom)) || '—'} · effective to ${esc(fmtDateTime(revision.effectiveTo)) || '—'}</div>
      ${revision.requiresLicensedVerification ? '<div class="notice warn"><b>RequiresLicensedVerification</b> — organization approval does not establish verified standard wording or conformity.</div>' : ''}
      ${author && ['Submitted', 'Reviewed'].includes(revision.state) ? '<div class="notice">The content author cannot review or approve this revision.</div>' : ''}
      <div class="toolbar">${actions.map(([action, label]) => `<button class="btn" data-qms-action="${action}">${esc(label)}</button>`).join('')}</div>
      ${contentHtml(record.kind, revision.content)}<h3>Recorded authoritative sources</h3>${(revision.sources ?? []).length ? `<ul>${revision.sources.map((source, index) => `<li>${esc(source.kind)} ${esc(source.key)} · exact revision ${esc(source.revision)}${source.facilityCode ? ` · ${esc(source.facilityCode)}` : ''}${source.programId ? ` · program ${esc(source.programId)}` : ''} · ${esc(source.markings)}${['QmsRevision', 'DocumentRevision'].includes(source.kind) ? ` <button class="btn small" data-qms-source="${index}">View exact source revision</button>` : ''}</li>`).join('')}</ul>` : '<div class="faint">No linked sources recorded.</div>'}
      <h3>Revision history</h3>${(revision.history ?? []).length ? `<table class="table"><thead><tr><th>When</th><th>Action and version</th><th>Actor</th><th>State</th><th>Decision note</th><th>Reason</th></tr></thead><tbody>${revision.history.map(entry => `<tr><td>${esc(fmtDateTime(entry.at))}</td><td>${esc(entry.action)} · ${entry.version}</td><td>${esc(who(entry.actor))}</td><td>${esc(entry.fromState ?? 'New')} → ${esc(entry.toState)}</td><td>${esc(entry.note)}</td><td>${esc(entry.reason)}</td></tr>`).join('')}</tbody></table>` : '<div class="faint">No transitions returned.</div>'}</section>`;
  }

  function render() {
    if (!current(generation)) return;
    const records = data.records.data ?? [], record = records.find(item => item.code === selectedCode), revision = record?.revisions.find(item => item.revision === selectedRevision) ?? record?.revisions.at(-1);
    page.innerHTML = `<div class="page-head"><div><h1>QMS governance</h1><div class="sub">Organization scope, controlled processes, policies and requirement determinations.</div></div><span class="spacer"></span><button class="btn" data-qms-refresh>Refresh</button>${allowed('manage') && data.status.data?.configured ? '<button class="btn primary" data-qms-create>Create draft</button>' : ''}</div>
      ${data.status.error ? errorBlock(data.status.error, 'QMS organization configuration') : data.status.data?.configured ? `<div class="notice">Configured organization: <b>${esc(data.status.data.organizationCode)}</b>. Record scope and source permissions are checked separately.</div>` : '<div class="notice warn">QMS organization is not configured. Configure the deployment’s actual organization before creating governed records.</div>'}
      <section class="panel"><header><div class="seg">${KINDS.map(value => `<button data-qms-kind="${value}" class="${kind === value ? 'on' : ''}">${esc(value)}s</button>`).join('')}</div></header>
      ${data.records.error ? errorBlock(data.records.error, 'QMS records') : records.length ? `<table class="table"><thead><tr><th>Record</th><th>Scope</th><th>Latest revision</th><th>State</th><th></th></tr></thead><tbody>${records.map(item => { const latest = item.revisions.at(-1); return `<tr><td><b>${esc(item.code)}</b> · ${esc(item.kind)}</td><td>${esc(item.facilityCode)}${item.programId ? ` · ${esc(item.programId)}` : ''} · ${esc(item.markings)}</td><td>${latest?.revision ?? '—'}</td><td>${esc(latest?.state ?? 'Unknown')}${latest?.requiresLicensedVerification ? ' · RequiresLicensedVerification' : ''}</td><td><button class="btn small" data-qms-open="${esc(item.code)}">Open record</button></td></tr>`; }).join('')}</tbody></table>` : emptyBlock('No visible records', 'Create the organization’s actual definitions or select another kind. Source access can limit which records you see.')}</section>
      ${record && revision ? `<div class="toolbar"><label for="qms-revision">Recorded revision</label><select class="select" id="qms-revision">${record.revisions.map(item => `<option value="${item.revision}" ${item.revision === revision.revision ? 'selected' : ''}>${item.revision} — ${esc(item.state)}</option>`).join('')}</select></div>${revisionHtml(record, revision)}` : ''}`;
    $('[data-qms-refresh]', page).addEventListener('click', load);
    $('[data-qms-create]', page)?.addEventListener('click', () => edit());
    $$('[data-qms-kind]', page).forEach(button => button.addEventListener('click', () => { kind = button.dataset.qmsKind; selectedCode = null; selectedRevision = null; void load(); }));
    $$('[data-qms-open]', page).forEach(button => button.addEventListener('click', () => openRecord(button.dataset.qmsOpen)));
    $('#qms-revision', page)?.addEventListener('change', event => { generation++; closeDialogs(); selectedRevision = Number(event.target.value); render(); });
    $$('[data-qms-action]', page).forEach(button => button.addEventListener('click', () => button.dataset.qmsAction === 'edit' ? edit(record, revision) : button.dataset.qmsAction === 'new-revision' ? edit(record, revision, true) : transition(record, revision, button.dataset.qmsAction)));
    $$('[data-qms-source]', page).forEach(button => button.addEventListener('click', () => openSource(revision.sources[Number(button.dataset.qmsSource)])));
  }

  await load();
  return { dispose: () => { disposed = true; generation++; closeDialogs(); } };
}
