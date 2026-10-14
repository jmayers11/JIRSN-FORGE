import { get, command, tryGet, session } from './api.js';
import { esc, $, $$, fmtDateTime, emptyBlock, errorBlock, toast, manageDialogFocus } from './ui.js';
import { fieldHtml, readFields, canAction } from './forms.js';
import { who } from './people.js';
import { describeIdentification } from './identification-input.mjs';
import { isServiceUnavailable } from './terminal-state.mjs';

const API = '/api/identification';
const PERMISSIONS = { manage: 'identification.labels.manage', approve: 'identification.labels.approve', print: 'identification.labels.print', aliases: 'identification.aliases.manage' };
const key = value => encodeURIComponent(value);
const words = value => String(value).replace(/([a-z])([A-Z])/g, '$1 $2');
const text = (name, label, options = {}) => ({ name, label, required: true, ...options });
// A physical presentation keeps its captured part revision even when the current owner has changed.
function describeLabelSubject(subject) {
  if (subject?.kind === 'material' && subject.revision) {
    return 'Material ' + subject.partNumber + ' revision ' + subject.revision + ', lot ' + subject.lotNumber
      + (subject.identityKind ? ' (' + subject.identityKind + ')' : '')
      + '. Recorded identity; stock eligibility is checked when you confirm.';
  }
  return describeIdentification(subject);
}
const reasonField = text('labelReason', 'Reason recorded in the audit trail', { type: 'textarea' });
const SYMBOLOGIES = [['Code128', 'Code 128'], ['QR', 'QR Code'], ['DataMatrix', 'Data Matrix']];
const encodeKey = value => encodeURIComponent(value).replace(/[!'()*]/g, character => `%${character.charCodeAt(0).toString(16).toUpperCase()}`);

export function materialIdentificationCode(config, partNumber, lotNumber) {
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(config?.namespace ?? '')) throw new Error('Identification is not configured.');
  for (const value of [partNumber, lotNumber]) if (typeof value !== 'string' || !value || value.length > config.maxKeyLength || /[\p{White_Space}\p{Cc}]/u.test(value)) throw new Error('The material keys are not valid canonical identifiers.');
  const code = `forge:v1:${config.namespace}:material:${encodeKey(partNumber)}:${encodeKey(lotNumber)}`;
  if (code.length > config.maxCodeLength) throw new Error('The material identification exceeds the configured limit.');
  return code;
}

function openBrowserPrint(svgUrl) {
  const view = globalThis.window?.open('', '_blank');
  if (!view) throw new Error('Allow a print window, or download the artifact and print it with your approved process.');
  view.opener = null;
  view.document.write(`<html lang="en"><head><title>Forge label artifact</title></head><body><img src="${esc(svgUrl)}" alt="The recorded Forge label artifact"></body></html>`);
  view.document.close();
  const image = view.document.querySelector('img');
  image.addEventListener('load', () => { view.focus(); view.print(); });
}

// Presentation records never own inventory. Every write uses the existing authenticated
// command path; source, adopted policy, scope, version and duty checks remain on the server.
export async function mountLabelsPanel(element, { me, partNumber, lotNumber, facilityCode = null,
  isCurrent = () => true, printArtifact = openBrowserPrint }) {
  let disposed = false, generation = 0, data = {}, selected = null, subject = null, subjectCode = null;
  const auth = { subject: session.subject, token: session.token }, dialogs = new Set(), blobs = new Set();
  const current = token => !disposed && element.isConnected && isCurrent() && token === generation && session.subject === auth.subject && session.token === auth.token;
  const closeDialogs = () => { for (const close of [...dialogs]) close(); };
  const dispose = () => { disposed = true; generation++; closeDialogs(); for (const blob of blobs) URL.revokeObjectURL(blob); blobs.clear(); observer?.disconnect(); };
  const observer = typeof MutationObserver === 'function' ? new MutationObserver(() => { if (!element.isConnected || !isCurrent()) dispose(); }) : null;
  observer?.observe(element.ownerDocument.body, { childList: true, subtree: true });
  const resource = item => ({ facilityId: item?.facilityCode ?? facilityCode, programId: item?.subject?.programId ?? null });
  const allowed = (action, item = null) => canAction(me, PERMISSIONS[action], resource(item));
  const mine = actor => actor === `person:${me.personId}`;
  const labelAllowed = (action, label) => selected?.label.labelId === label.labelId && (selected.allowedActions ?? []).includes(action);
  const canReconcileAttempt = (attempt, label) => allowed('print', label) && (['Requested', 'Uncertain'].includes(attempt.state) || (attempt.state === 'Confirmed' && attempt.observedPrinted === true && attempt.observedAttached === false));

  function modal(title, description, fields, confirmLabel, send, extraHtml = '', onRecorded = null) {
    closeDialogs();
    const token = generation, scrim = document.createElement('div'), dialog = document.createElement('div');
    scrim.className = 'scrim'; scrim.setAttribute('aria-hidden', 'true'); dialog.className = 'modal';
    dialog.innerHTML = `<h2>${esc(title)}</h2><div class="muted">${esc(description)}</div>${fields.map(fieldHtml).join('')}${extraHtml}${fieldHtml(reasonField)}
      <div class="notice bad hidden" data-label-error role="alert" aria-live="assertive"></div><div class="actions"><button class="btn" data-label-cancel>Cancel</button><button class="btn primary" data-label-confirm>${esc(confirmLabel)}</button></div>`;
    let closed = false, busy = false, releaseFocus = () => {};
    const live = () => !closed && dialog.isConnected && current(token);
    const close = () => { if (closed) return; closed = true; scrim.remove(); dialog.remove(); dialogs.delete(close); releaseFocus(); };
    dialogs.add(close); scrim.onclick = close; document.body.append(scrim, dialog);
    $('[data-label-cancel]', dialog).addEventListener('click', close);
    const button = $('[data-label-confirm]', dialog), error = $('[data-label-error]', dialog);
    button.addEventListener('click', async () => {
      if (!live() || busy) return;
      busy = true; button.disabled = true; error.classList.add('hidden');
      try {
        const values = readFields(dialog, fields), reason = readFields(dialog, [reasonField]).labelReason;
        const result = await send(values, reason, dialog, live);
        if (!live()) return;
        close(); toast(result.message ?? 'Recorded.');
        const refreshed = await refresh();
        if (onRecorded && current(refreshed)) await onRecorded(result);
      } catch (failure) {
        if (!live()) return;
        if (isServiceUnavailable(failure) && (Number.isInteger(failure.status) || failure instanceof TypeError)) {
          close(); toast('Forge could not confirm that change. Check the label record before retrying; no print or stock outcome is inferred.', true); return;
        }
        error.textContent = `${failure.message}${failure.status === 409 ? ' Refresh the record before another action.' : ''}`; error.classList.remove('hidden');
      } finally { busy = false; if (live()) button.disabled = false; }
    });
    releaseFocus = manageDialogFocus(dialog, close);
    return { dialog, live, close };
  }

  async function refresh() {
    const token = ++generation; closeDialogs();
    if (!subjectCode) {
      const config = await tryGet(`${API}/config`);
      if (!current(token)) return;
      if (config.error) { element.innerHTML = errorBlock(config.error, 'label identification'); return; }
      try { subjectCode = materialIdentificationCode(config.data, partNumber, lotNumber); }
      catch (error) { element.innerHTML = errorBlock(error, 'label identification'); return; }
    }
    try { subject = await command(`${API}/resolve`, { code: subjectCode }); }
    catch (error) { if (current(token)) element.innerHTML = errorBlock(error, 'the material subject'); return; }
    if (!current(token)) return;
    const [labels, aliases, templates, policy] = await Promise.all([tryGet(`${API}/labels?subjectCode=${key(subjectCode)}`), tryGet(`${API}/aliases?subjectCode=${key(subjectCode)}`), tryGet(`${API}/templates`), facilityCode ? tryGet(`${API}/labels/config?subjectCode=${key(subjectCode)}&facilityCode=${key(facilityCode)}`) : Promise.resolve({})]);
    if (!current(token)) return;
    data = { labels: labels.error ? labels : { data: labels.data.items, nextCursor: labels.data.nextCursor }, aliases: aliases.error ? aliases : { data: aliases.data.items, nextCursor: aliases.data.nextCursor }, templates: templates.error ? templates : { data: templates.data.items, nextCursor: templates.data.nextCursor }, policy };
    if (selected) {
      const detail = await tryGet(`${API}/labels/${key(selected.label.labelId)}`);
      if (!current(token)) return;
      selected = detail.error ? null : detail.data;
    }
    render();
    return token;
  }

  function issue() {
    if (!allowed('manage')) return;
    const templates = (data.templates.data ?? []).filter(template => template.state === 'Approved' && template.allowedKinds.includes(subject.kind));
    if (!templates.length) { toast('No currently visible approved template is available for this subject.', true); return; }
    const fields = [text('facilityCode', 'Actual facility code', { value: facilityCode, hint: 'The server requires a current adopted label policy for this actual facility.' }), text('template', 'Approved template revision', { type: 'select', options: [['', '— select an exact revision —'], ...templates.map((template, index) => [index, `${template.code} revision ${template.revision} · ${template.facilityCode}`])] }), text('symbology', 'Barcode symbology', { type: 'select', options: [['', '— choose —'], ...SYMBOLOGIES] }), text('copies', 'Physical label copies requested', { type: 'number', min: 1, hint: 'The adopted policy controls the allowed quantity and copy approval.' })];
    modal('Issue labels', `${describeLabelSubject(subject)} Labels identify this same physical subject and do not create inventory.`, fields, 'Issue presentation records', (values, reason, dialog, live) => {
      const template = templates[Number(values.template)];
      if (!template || !template.allowedSymbologies.includes(values.symbology)) throw new Error('The chosen symbology is not allowed by this template revision.');
      if (!live() || !allowed('manage')) throw new Error('This label context is no longer active.');
      return command(`${API}/labels`, { subjectCode, facilityCode: values.facilityCode, templateCode: template.code, templateVersion: template.revision, symbology: values.symbology, copies: values.copies }, reason);
    }, '', openIssuance);
  }

  function labelAction(label, action) {
    if (!labelAllowed(words(action).replace(/^./, letter => letter.toUpperCase()), label)) return;
    modal(`${words(action)} label`, `Printed subject: ${describeLabelSubject(label.subject)}${selected.currentSubject ? ` Current owner: ${describeLabelSubject(selected.currentSubject)}` : ''} Recorded label version ${label.version}. ${action === 'replace' ? 'A replacement uses the current owner configuration, preserves the physical subject and keeps the old label history.' : action === 'void' ? 'Voiding the presentation label leaves its physical subject and inventory history intact.' : 'The server rechecks source access and the currently adopted policy.'}`, [], words(action), (values, reason, dialog, live) => {
      if (!live() || !labelAllowed(words(action).replace(/^./, letter => letter.toUpperCase()), label)) throw new Error('This label context is no longer active.');
      return command(`${API}/labels/${key(label.labelId)}/${action}`, { expectedVersion: label.version }, reason);
    }, '', ['reprint', 'replace'].includes(action) ? openIssuance : null);
  }

  function requestPrint(label) {
    if (!labelAllowed('RequestPrint', label)) return;
    modal('Request a print artifact', `${describeLabelSubject(label.subject)} This creates an audited print attempt, not proof of printing or attachment.`, [text('printerCode', 'Configured printer code (optional)', { required: false, hint: 'Leave empty for a manual/browser print. A named printer is not an acknowledgement.' })], 'Record print attempt', (values, reason, dialog, live) => {
      if (!live() || !labelAllowed('RequestPrint', label)) throw new Error('This print context is no longer active.');
      return command(`${API}/labels/${key(label.labelId)}/print-attempts`, { expectedVersion: label.version, printerCode: values.printerCode || null }, reason);
    });
  }

  function reconcile(attempt, label) {
    if (!allowed('print', label)) return;
    const attachmentOnly = attempt.state === 'Confirmed' && attempt.observedPrinted === true && attempt.observedAttached === false;
    const fields = [text('outcome', 'Your observed print outcome', { type: 'select', options: [['', '— record your observation —'], ['Confirmed', attachmentOnly ? 'I observed attachment of the recorded print' : 'I observed the physical print'], ...(attachmentOnly ? [] : [['Failed', 'I observed failure'], ['Uncertain', 'The outcome is uncertain']])] }), text('observedPrinted', 'Did you observe the physical print?', { type: 'select', options: [['', '— record explicitly —'], ['true', 'Yes'], ['false', 'No']] }), text('observedAttached', 'Did you observe attachment to this physical subject?', { type: 'select', options: [['', '— record explicitly —'], ['true', 'Yes'], ['false', 'No']] }), text('observation', 'Observation and basis', { type: 'textarea', hint: 'Describe what you observed. These are attributed human observations; printer-device success and custody are not inferred.' })];
    modal('Reconcile print attempt', `Attempt ${attempt.attemptId}, recorded version ${attempt.version}. Your decision is separately attributed; provider acknowledgements are not offered as human outcomes.`, fields, 'Record human observation', (values, reason, dialog, live) => {
      if (!live() || !allowed('print', label)) throw new Error('This print context is no longer active.');
      return command(`${API}/print-attempts/${key(attempt.attemptId)}/outcome`, { expectedVersion: attempt.version, outcome: values.outcome, observedPrinted: values.observedPrinted === 'true', observedAttached: values.observedAttached === 'true', observation: values.observation }, reason);
    });
  }

  async function artifact(attempt) {
    closeDialogs(); const token = generation;
    const result = await tryGet(`${API}/print-attempts/${key(attempt.attemptId)}/artifact`);
    if (!current(token)) return;
    if (result.error) { toast(result.error.message, true); return; }
    const svg = result.data?.detail;
    if (typeof svg !== 'string' || !svg.includes('<svg')) { toast('The recorded artifact is unavailable.', true); return; }
    const blob = URL.createObjectURL(new Blob([svg], { type: 'image/svg+xml' })); blobs.add(blob);
    const scrim = document.createElement('div'), dialog = document.createElement('div');
    scrim.className = 'scrim'; scrim.setAttribute('aria-hidden', 'true'); dialog.className = 'modal';
    dialog.innerHTML = `<h2>Recorded label artifact</h2><div class="muted">Attempt ${esc(attempt.attemptId)} · SHA-256 ${esc(attempt.artifactSha256)}</div><img src="${esc(blob)}" alt="Recorded barcode and human-readable subject" style="max-width:100%"><div class="notice" data-artifact-observation>Opening the browser print dialog does not confirm printing or attachment. Record an observation separately.</div><div class="actions"><button class="btn" data-artifact-close>Close</button><button class="btn primary" data-artifact-print>Open browser print dialog</button></div>`;
    let closed = false, releaseFocus = () => {};
    const live = () => !closed && current(token) && dialog.isConnected;
    const close = () => { if (closed) return; closed = true; dialog.remove(); scrim.remove(); dialogs.delete(close); releaseFocus(); URL.revokeObjectURL(blob); blobs.delete(blob); };
    dialogs.add(close); scrim.onclick = close; document.body.append(scrim, dialog); $('[data-artifact-close]', dialog).addEventListener('click', close);
    $('[data-artifact-print]', dialog).addEventListener('click', () => {
      if (!live()) return;
      try { printArtifact(blob); $('[data-artifact-observation]', dialog).textContent = 'The browser print view was requested. The recorded outcome remains unchanged; record what you observed separately.'; }
      catch (error) { if (live()) toast(error.message, true); }
    });
    releaseFocus = manageDialogFocus(dialog, close);
  }

  function alias() {
    if (!allowed('aliases')) return;
    const fields = [text('domain', 'Organization-defined alias domain'), text('alias', 'Exact alias text', { hint: 'Alias text maps to the same recorded subject. It does not create material or authenticate a person.' }), text('facilityCode', 'Actual facility code', { value: facilityCode })];
    modal('Record an identification alias', describeLabelSubject(subject), fields, 'Record mapping', (values, reason, dialog, live) => {
      if (!live() || !allowed('aliases')) throw new Error('This alias context is no longer active.');
      return command(`${API}/aliases`, { ...values, subjectCode }, reason);
    });
  }

  function voidAlias(mapping) {
    if (!allowed('aliases', mapping)) return;
    modal('Void alias mapping', `Alias ${mapping.domain}: ${mapping.alias}. The historical mapping and physical subject remain recorded.`, [], 'Void mapping', (values, reason, dialog, live) => {
      if (!live() || !allowed('aliases', mapping)) throw new Error('This alias context is no longer active.');
      return command(`${API}/aliases/${key(mapping.aliasId)}/void`, { expectedVersion: mapping.version }, reason);
    });
  }

  function replaceAlias(mapping) {
    if (!allowed('aliases', mapping)) return;
    modal('Replace alias mapping', `Alias ${mapping.domain}: ${mapping.alias} will keep the same physical subject ${describeLabelSubject(mapping.subject)}. The old mapping is retained and voided.`, [], 'Record replacement mapping', (values, reason, dialog, live) => {
      if (!live() || !allowed('aliases', mapping)) throw new Error('This alias context is no longer active.');
      return command(`${API}/aliases/${key(mapping.aliasId)}/replace`, { expectedVersion: mapping.version, subjectCode: mapping.subjectCode, facilityCode: mapping.facilityCode }, reason);
    });
  }

  function template(existing = null) {
    if (!allowed('manage')) return;
    const document = existing?.documents?.[0];
    const fields = [...(existing ? [] : [text('code', 'Template code', { code: true })]), text('facilityCode', 'Actual facility code', { value: existing?.facilityCode ?? facilityCode, readonly: !!existing }), text('width', 'Width (pixels, 96–2048)', { type: 'number', min: 96, value: existing?.width }), text('height', 'Height (pixels, 64–2048)', { type: 'number', min: 64, value: existing?.height }), text('documentNumber', 'Controlled template document number', { value: document?.documentNumber }), text('documentRevision', 'Exact controlled document revision', { value: document?.revision })];
    const extra = `<h3>Allowed subject kinds</h3>${['order', 'operation', 'material', 'location'].map(kind => `<label class="opt"><input type="checkbox" data-template-kind="${kind}" ${existing?.allowedKinds.includes(kind) ? 'checked' : ''}> ${words(kind)}</label>`).join('')}<h3>Allowed symbologies</h3>${SYMBOLOGIES.map(([value, label]) => `<label class="opt"><input type="checkbox" data-template-symbology="${value}" ${existing?.allowedSymbologies.includes(value) ? 'checked' : ''}> ${esc(label)}</label>`).join('')}`;
    modal(existing ? `New draft revision of ${existing.code}` : 'Create a label template draft', 'The source document, allowed kinds and barcode formats are explicit. A separately approved exact template revision is required for issuance.', fields, 'Create template draft', (values, reason, dialog, live) => {
      const allowedKinds = $$('[data-template-kind]', dialog).filter(input => input.checked).map(input => input.dataset.templateKind), allowedSymbologies = $$('[data-template-symbology]', dialog).filter(input => input.checked).map(input => input.dataset.templateSymbology);
      if (!allowedKinds.length || !allowedSymbologies.length) throw new Error('Choose the allowed subject kinds and barcode symbologies.');
      if (!live() || !allowed('manage')) throw new Error('This template context is no longer active.');
      const { documentNumber, documentRevision, ...body } = values;
      return command(existing ? `${API}/templates/${key(existing.code)}/revisions` : `${API}/templates`, { ...body, ...(existing ? { expectedVersion: existing.version } : {}), allowedKinds, allowedSymbologies, documents: [{ documentNumber, revision: documentRevision }, ...(existing?.documents?.slice(1) ?? [])] }, reason);
    }, extra);
  }

  function templateAction(item, action) {
    const permission = action === 'approve' ? 'approve' : 'manage';
    if (!allowed(permission, item) || (action === 'approve' && [item.createdBy, item.lastEditedBy].some(mine))) return;
    modal(`${words(action)} ${item.code} revision ${item.revision}`, `Exact template revision ${item.revision}, current version ${item.version}. The server rechecks its controlled source document and independent approval.`, [], words(action), (values, reason, dialog, live) => {
      if (!live() || !allowed(permission, item)) throw new Error('This template context is no longer active.');
      return command(`${API}/templates/${key(item.code)}/revisions/${item.revision}/${action}`, { expectedVersion: item.version }, reason);
    });
  }

  async function openLabel(labelId) {
    const token = ++generation; closeDialogs();
    const result = await tryGet(`${API}/labels/${key(labelId)}`);
    if (!current(token)) return;
    if (result.error) { toast(result.error.message, true); return; }
    selected = result.data; render();
  }

  async function openIssuance(result) {
    if (!result.entityId) return;
    const token = ++generation;
    const issuance = await tryGet(`${API}/labels/issuances/${key(result.entityId)}`);
    if (!current(token)) return;
    if (issuance.error) { toast('The label command was recorded. Refresh the issuance before another action.', true); return; }
    const copy = issuance.data.copies?.[0];
    if (copy) await openLabel(copy.labelId);
  }

  async function more(name) {
    const collection = data[name];
    if (!collection?.nextCursor) return;
    const token = ++generation; closeDialogs();
    const query = name === 'templates' ? 'templates?' : `${name}?subjectCode=${key(subjectCode)}&`;
    const result = await tryGet(`${API}/${query}after=${key(collection.nextCursor)}`);
    if (!current(token)) return;
    if (result.error) { toast(result.error.message, true); return; }
    data[name] = { data: [...collection.data, ...result.data.items], nextCursor: result.data.nextCursor }; render();
  }

  function render() {
    if (!current(generation)) return;
    const label = selected?.label;
    element.innerHTML = `<h3>Labels for ${esc(partNumber)} / ${esc(lotNumber)}</h3><div class="muted">${esc(describeLabelSubject(subject))}</div>
      ${data.policy.error ? errorBlock(data.policy.error, 'the current adopted label policy') : data.policy.data ? `<div class="notice">Current adopted policy ${esc(data.policy.data.policy.code)} revision ${data.policy.data.policy.revision} · active copy mode ${esc(data.policy.data.policy.activeCopyMode)} · maximum ${data.policy.data.policy.maximumActiveCopies} active copies · required confirmation ${esc(words(data.policy.data.policy.requiredConfirmation))}. Printer mode ${esc(data.policy.data.printerMode)}${data.policy.data.trustedHardware ? '' : ' · hardware is not attested'}.</div>` : '<div class="faint">The actual facility and adopted policy are rechecked before label actions. No policy or device default is assumed.</div>'}
      <div class="toolbar"><button class="btn small" data-label-refresh>Refresh labels</button>${allowed('manage') ? '<button class="btn small" data-label-issue>Issue labels</button><button class="btn small" data-label-template>Create template draft</button>' : ''}${allowed('aliases') ? '<button class="btn small" data-label-alias>Record alias</button>' : ''}</div>
      ${data.labels.error ? errorBlock(data.labels.error, 'labels') : (data.labels.data ?? []).length ? `<table class="table"><thead><tr><th>Label and subject</th><th>Presentation</th><th>State/version</th><th>Adopted policy</th><th></th></tr></thead><tbody>${data.labels.data.map(item => `<tr><td>${esc(item.code)}<div class="faint">${esc(describeLabelSubject(item.subject))}</div></td><td>${esc(item.templateCode)} revision ${item.templateVersion} · ${esc(words(item.symbology))} · ${esc(item.relationship)}</td><td>${esc(item.state)} · ${item.version}</td><td>${esc(item.policyCode)} revision ${item.policyRevision}</td><td><button class="btn small" data-label-open="${item.labelId}">Open label</button></td></tr>`).join('')}</tbody></table>` : emptyBlock('No visible labels', 'Label issuance is a presentation record. Inventory and genealogy remain authoritative.')}
      ${label ? `<section class="panel"><h3>Label ${esc(label.code)}</h3><div>Recorded printed subject: ${esc(describeLabelSubject(label.subject))}</div>${selected.currentSubject ? `<div>Current owner: ${esc(describeLabelSubject(selected.currentSubject))}</div>` : ''}<div class="faint">${esc(label.state)} · version ${label.version} · ${esc(label.relationship)}${label.previousLabelId ? ` · previous label ${esc(label.previousLabelId)}` : ''} · created by ${esc(who(label.createdBy))} ${esc(fmtDateTime(label.createdAt))}</div><div class="toolbar">
        ${selected.policy?.available === false ? '<div class="notice warn">The adopted policy is unavailable for current use. The historical label remains readable.</div>' : ''}
        ${labelAllowed('Approve', label) ? '<button class="btn small" data-label-action="approve">Authorize label copy</button>' : ''}
        ${labelAllowed('Reprint', label) ? '<button class="btn small" data-label-action="reprint">Reprint copy</button>' : ''}${labelAllowed('Replace', label) ? '<button class="btn small" data-label-action="replace">Replace label</button>' : ''}
        ${labelAllowed('Void', label) ? '<button class="btn small" data-label-action="void">Void label</button>' : ''}
        ${labelAllowed('RequestPrint', label) ? '<button class="btn small" data-label-request-print>Record print attempt</button>' : ''}</div>
        <h4>Label transitions</h4>${(selected.transitions ?? []).length ? `<ul>${selected.transitions.map(entry => `<li>${esc(entry.action)} · ${entry.version} · ${esc(entry.fromState ?? 'New')} → ${esc(entry.toState)} · ${esc(who(entry.actor))} ${esc(fmtDateTime(entry.at))} · ${esc(entry.reason)}${entry.observation ? ` · ${esc(entry.observation)}` : ''}</li>`).join('')}</ul>` : '<div class="faint">No transitions returned.</div>'}
        <h4>Print attempts</h4><div class="faint">Printer acknowledgement, human printing observation and physical attachment are distinct. Physical confirmation requires a separately attributed observation.</div>${(selected.printAttempts ?? []).length ? `<table class="table"><thead><tr><th>Attempt/state</th><th>Requested</th><th>Human observation</th><th></th></tr></thead><tbody>${selected.printAttempts.map((attempt, index) => `<tr><td>${esc(attempt.attemptId)} · ${esc(attempt.state)} · version ${attempt.version}${attempt.printerCode ? `<div>${esc(attempt.printerCode)}</div>` : ''}</td><td>${esc(who(attempt.requestedBy))} ${esc(fmtDateTime(attempt.requestedAt))}</td><td>Printed observed: ${attempt.observedPrinted === true ? 'Yes' : attempt.observedPrinted === false ? 'No' : 'not recorded'} · attachment observed: ${attempt.observedAttached === true ? 'Yes' : attempt.observedAttached === false ? 'No' : 'not recorded'}<div>${esc(attempt.observation ?? 'No human observation recorded')}</div>${attempt.outcomeBy ? `<div>${esc(who(attempt.outcomeBy))} ${esc(fmtDateTime(attempt.outcomeAt))}</div>` : ''}</td><td><button class="btn small" data-label-artifact="${index}">View recorded artifact</button>${canReconcileAttempt(attempt, label) ? ` <button class="btn small" data-label-reconcile="${index}">Record observation</button>` : ''}</td></tr>`).join('')}</tbody></table>` : '<div class="faint">No print attempts recorded.</div>'}</section>` : ''}
      <h4>Alias mappings</h4>${data.aliases.error ? errorBlock(data.aliases.error, 'aliases') : (data.aliases.data ?? []).length ? `<ul>${data.aliases.data.map((mapping, index) => `<li>${esc(mapping.domain)}: ${esc(mapping.alias)} → ${esc(mapping.code)} · ${esc(mapping.state)} · version ${mapping.version}${mapping.previousAliasId ? ` · previous mapping ${esc(mapping.previousAliasId)}` : ''}${mapping.state === 'Active' && allowed('aliases', mapping) ? ` <button class="btn small" data-alias-replace="${index}">Replace mapping</button> <button class="btn small" data-alias-void="${index}">Void mapping</button>` : ''}</li>`).join('')}</ul>` : '<div class="faint">No visible alias mappings.</div>'}
      <h4>Label template revisions</h4>${data.templates.error ? errorBlock(data.templates.error, 'label templates') : (data.templates.data ?? []).length ? `<table class="table"><thead><tr><th>Template revision</th><th>Geometry/formats</th><th>Controlled sources</th><th></th></tr></thead><tbody>${data.templates.data.map((item, index) => `<tr><td>${esc(item.code)} revision ${item.revision} · version ${item.version} · ${esc(item.state)}</td><td>${item.width} × ${item.height} px · ${esc(item.allowedSymbologies.join(', '))}</td><td>${item.documents.map(document => `${esc(document.documentNumber)} revision ${esc(document.revision)}`).join(', ')}</td><td>${item.state === 'Draft' && allowed('approve', item) && ![item.createdBy, item.lastEditedBy].some(mine) ? `<button class="btn small" data-template-approve="${index}">Approve exact revision</button>` : ''}${item.state !== 'Withdrawn' && allowed('manage', item) ? `<button class="btn small" data-template-withdraw="${index}">Withdraw</button>` : ''}${item.state !== 'Draft' && allowed('manage', item) ? `<button class="btn small" data-template-revise="${index}">New draft revision</button>` : ''}</td></tr>`).join('')}</tbody></table>` : '<div class="faint">No visible templates.</div>'}`;
    if (data.labels.nextCursor) { const button = document.createElement('button'); button.className = 'btn small'; button.textContent = 'Load more labels'; button.addEventListener('click', () => more('labels')); element.append(button); }
    if (data.templates.nextCursor) { const button = document.createElement('button'); button.className = 'btn small'; button.textContent = 'Load more template revisions'; button.addEventListener('click', () => more('templates')); element.append(button); }
    if (data.aliases.nextCursor) { const button = document.createElement('button'); button.className = 'btn small'; button.textContent = 'Load more alias mappings'; button.addEventListener('click', () => more('aliases')); element.append(button); }
    $('[data-label-refresh]', element).addEventListener('click', refresh);
    $('[data-label-issue]', element)?.addEventListener('click', issue); $('[data-label-template]', element)?.addEventListener('click', template); $('[data-label-alias]', element)?.addEventListener('click', alias);
    $$('[data-label-open]', element).forEach(button => button.addEventListener('click', () => openLabel(button.dataset.labelOpen)));
    $$('[data-label-action]', element).forEach(button => button.addEventListener('click', () => labelAction(label, button.dataset.labelAction)));
    $('[data-label-request-print]', element)?.addEventListener('click', () => requestPrint(label));
    $$('[data-label-reconcile]', element).forEach(button => button.addEventListener('click', () => reconcile(selected.printAttempts[Number(button.dataset.labelReconcile)], label)));
    $$('[data-label-artifact]', element).forEach(button => button.addEventListener('click', () => artifact(selected.printAttempts[Number(button.dataset.labelArtifact)])));
    $$('[data-alias-void]', element).forEach(button => button.addEventListener('click', () => voidAlias(data.aliases.data[Number(button.dataset.aliasVoid)])));
    $$('[data-alias-replace]', element).forEach(button => button.addEventListener('click', () => replaceAlias(data.aliases.data[Number(button.dataset.aliasReplace)])));
    $$('[data-template-approve]', element).forEach(button => button.addEventListener('click', () => templateAction(data.templates.data[Number(button.dataset.templateApprove)], 'approve')));
    $$('[data-template-withdraw]', element).forEach(button => button.addEventListener('click', () => templateAction(data.templates.data[Number(button.dataset.templateWithdraw)], 'withdraw')));
    $$('[data-template-revise]', element).forEach(button => button.addEventListener('click', () => template(data.templates.data[Number(button.dataset.templateRevise)])));
  }

  await refresh();
  return { dispose, refresh };
}
