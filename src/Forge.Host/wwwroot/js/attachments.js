import { tryGet, command, upload, download } from './api.js';
import { who } from './people.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, toast, drawer } from './ui.js';
import { formModal, canAction } from './forms.js';

// Documents attached to a record (V2.2): an order, purchase order, work order, NCR, supplier, item or
// program. A file is attached as a reference document — kept, never production authority. A controlled
// document is attached to follow or for information. Detaching needs a reason and keeps the history.

export const TYPE_LABEL = {
  Drawing: 'Drawing', Specification: 'Specification', Procedure: 'Procedure', WorkInstruction: 'Work instruction', InspectionPlan: 'Inspection plan',
  Standard: 'Standard', Datasheet: 'Datasheet', Certificate: 'Certificate', PurchaseDocument: 'Purchase document', Contract: 'Contract', Photo: 'Photo', Other: 'Other',
  PackingSlip: 'Packing slip', CertificateOfConformance: 'Certificate of conformance', CertificateOfAnalysis: 'Certificate of analysis', MeasurementRecord: 'Measurements', FaiReport: 'Signed FAI report',
};
export const MARKING_LABEL = { None: 'No marking', ControlledUnclassified: 'CUI', ExportControlled: 'Export controlled', Proprietary: 'Proprietary' };
const STATE_TONE = { Current: 'ok', Reference: 'none', 'Not released': 'warn', Superseded: 'warn', Obsolete: 'bad' };

export const markings = m => (!m || m === 'None' ? [] : String(m).split(',').map(s => s.trim()));
export const markingPills = m => markings(m).map(x => `<span class="pill bad">${esc(MARKING_LABEL[x] ?? x)}</span>`).join(' ');
export const fileSize = n => (n == null ? '' : n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(0)} KB` : `${(n / 1048576).toFixed(1)} MB`);
export const fileUrl = (number, revision) => `/api/engineering/documents/${encodeURIComponent(number)}/revisions/${encodeURIComponent(revision)}/file`;

/**
 * Renders the Documents panel for one record into `section` (a <section class="panel">).
 * `type` is the record kind (ProductionOrder, PurchaseOrder, WorkOrder, Ncr, Supplier, Item, Program, WorkCenter).
 */
export async function attachmentsPanel(section, { type, id, me, title = 'Documents', note }) {
  let history = false;
  const base = `/api/engineering/attachments/${type}/${encodeURIComponent(id)}`;
  const mayAttach = canAction(me, 'engineering.documents.attach');
  const evidence = ['Receipt', 'ReceivingInspection', 'Inspection', 'Shipment', 'OutsideProcess', 'Fai', 'SupplierQuote'].includes(type);

  async function render() {
    const res = await tryGet(`${base}${history ? '?history=true' : ''}`);
    section.classList.add('panel');
    if (res.error) {
      section.innerHTML = `<header><h2>${esc(title)}</h2></header>${errorBlock(res.error, 'documents')}`;
      return;
    }

    const list = res.data;
    section.innerHTML = `
      <header><h2>${esc(title)}</h2><span class="spacer"></span>
        <label class="opt faint"><input type="checkbox" data-history ${history ? 'checked' : ''}> Show detached</label>
        ${mayAttach ? `${evidence ? '' : '<button class="btn small" data-attach-doc>Attach a document</button>'}<button class="btn small primary" data-attach-file>Attach a file</button>` : ''}</header>
      ${note ? `<div class="muted" style="margin-bottom:8px">${esc(note)}</div>` : ''}
      ${!list.length ? emptyBlock('Nothing attached', mayAttach ? 'Attach a file (kept as reference) or a controlled document this record must follow.' : 'No documents are attached to this record.') : `
      <div class="table-wrap"><table class="table"><thead><tr><th>Document</th><th>Use</th><th>Revision</th><th>File</th><th>Attached</th><th></th></tr></thead><tbody>
      ${list.map(a => {
        const r = a.revision;
        const detached = !!a.detachedAt;
        return `<tr class="${detached ? 'retired' : ''}">
          <td><a href="#/engineering/documents/${encodeURIComponent(a.documentNumber)}"><b>${esc(a.documentNumber)}</b></a> ${esc(a.title)}
            <div class="faint">${esc(TYPE_LABEL[a.type] ?? a.type)} · ${a.authority === 'Reference' ? 'Reference — not production authority' : esc(a.authority)} ${markingPills(a.markings)}</div>
            ${a.note ? `<div class="faint">${esc(a.note)}</div>` : ''}</td>
          <td>${a.purpose === 'Follow' ? '<span class="pill info">Follow</span>' : '<span class="pill none">Information</span>'}</td>
          <td>${r ? esc(r.revision) : '—'}${a.pinnedRevision ? ' <span class="faint">(pinned)</span>' : ''} <span class="pill ${STATE_TONE[a.state] ?? 'none'}">${esc(a.state)}</span>
            ${r?.effectiveFrom ? `<div class="faint">from ${esc(fmtDate(r.effectiveFrom))}</div>` : ''}</td>
          <td>${r?.fileName ? `<button class="btn small ghost" data-file="${esc(a.documentNumber)}|${esc(r.revision)}|${esc(r.fileName)}">${esc(r.fileName)}</button><div class="faint">${esc(fileSize(r.fileSize))}</div>` : '<span class="faint">—</span>'}</td>
          <td>${esc(who(a.linkedBy))}<div class="faint">${esc(fmtDateTime(a.linkedAt))}</div>
            ${detached ? `<div class="faint">Detached by ${esc(who(a.detachedBy))} ${esc(fmtDateTime(a.detachedAt))}: ${esc(a.detachReason)}</div>` : ''}</td>
          <td>${!detached && mayAttach ? `<button class="btn small ghost" data-detach="${a.linkId}">Detach</button>` : ''}${!detached && a.authority === 'Reference' && r?.fileSha256 && canAction(me, 'quality.inspect') ? `<button class="btn small ghost" data-verify-evidence="${esc(a.documentNumber)}" data-revision="${esc(r.revision)}">Verify source signature</button>` : ''}</td></tr>`;
      }).join('')}
      </tbody></table></div>`}`;

    $('[data-history]', section).onchange = e => { history = e.target.checked; render(); };
    $$('[data-file]', section).forEach(b => b.addEventListener('click', () => {
      const [number, revision, name] = b.dataset.file.split('|');
      download(fileUrl(number, revision), name).catch(e => toast(e.message, true));
    }));
    $$('[data-detach]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: 'Detach document', detail: 'It stops applying to this record. The attachment stays in the record\'s history with your reason.', reasonRequired: true, confirmLabel: 'Detach', fields: [],
    }, (_, reason) => command(`/api/engineering/attachment-links/${b.dataset.detach}/detach`, {}, reason), render)));
    $$('[data-verify-evidence]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: 'Verify source signature', detail: 'Review the original signed file first. This records your verification evidence; structured inspection results remain required.', reasonRequired: true, confirmLabel: 'Record verification',
      fields: [
        { name: 'sourceSigner', label: 'Source signer', required: true },
        { name: 'sourceSignedAt', label: 'Source signature date and time', type: 'datetime-local', required: true },
        { name: 'meaning', label: 'Meaning of source signature', type: 'textarea', required: true },
      ],
    }, (v, reason) => command(`/api/engineering/documents/${encodeURIComponent(b.dataset.verifyEvidence)}/verify-signature`, { ...v, revision: b.dataset.revision, sourceSignedAt: new Date(v.sourceSignedAt).toISOString() }, reason), render)));
    $('[data-attach-file]', section)?.addEventListener('click', () => formModal({
      title: 'Attach a file', detail: 'Kept as a reference document (customer PO, certificate, photo, note). It is never production authority; controlled documents are released in Engineering.', confirmLabel: 'Attach',
      fields: [
        { name: 'file', label: 'File (at most 50 MB)', type: 'file', required: true },
        { name: 'title', label: 'Title', placeholder: 'Defaults to the file name' },
        { name: 'type', label: 'Kind', type: 'select', value: 'Other', options: Object.entries(TYPE_LABEL) },
        { name: 'markings', label: 'Data marking', type: 'select', value: 'None', options: Object.entries(MARKING_LABEL), hint: 'Only people holding the marking will see it. You must hold it yourself.' },
        { name: 'note', label: 'Note', type: 'textarea' },
      ],
    }, async (v, reason) => {
      const sha = await upload(v.file);
      return command(`${base}/file`, { fileSha256: sha, fileName: v.file.name, title: v.title, type: v.type, markings: v.markings, note: v.note }, reason);
    }, render));
    $('[data-attach-doc]', section)?.addEventListener('click', async () => {
      const docs = await tryGet('/api/engineering/documents');
      if (docs.error) { toast(docs.error.message, true); return; }
      const controlled = docs.data.filter(d => d.authority !== 'Reference');
      if (!controlled.length) { toast('There are no controlled documents yet. Create them in Engineering → Documents.', true); return; }
      formModal({
        title: 'Attach a document', detail: 'Follow: this record must also follow the document. Leave the revision empty to always show the revision in force; pin one only when this record needs that exact revision.', confirmLabel: 'Attach',
        fields: [
          { name: 'documentNumber', label: 'Document', type: 'select', blank: 'Choose…', required: true, options: controlled.map(d => [d.number, `${d.number} — ${d.title}`]) },
          { name: 'purpose', label: 'Use', type: 'select', value: 'Follow', options: [['Follow', 'Follow (this record must follow it)'], ['Information', 'For information']] },
          { name: 'revision', label: 'Pin a revision (optional)', code: true, placeholder: 'e.g. B — must be released to follow' },
          { name: 'note', label: 'Note', type: 'textarea' },
        ],
      }, (v, reason) => command(base, v, reason), render);
    });
  }

  await render();
}

/** Appends a Documents panel to a page. */
export function appendAttachments(page, options) {
  const section = document.createElement('section');
  section.className = 'panel';
  page.append(section);
  return attachmentsPanel(section, options);
}

/** Opens a record's documents in a side drawer (for records shown as cards or table rows). */
export function attachmentsDrawer(title, options) {
  const { element } = drawer(title, '<section class="panel"></section>');
  return attachmentsPanel(element.querySelector('section'), { ...options, title: 'Documents' });
}

const STATE_LABEL = {
  Current: 'Current', Pinned: 'Pinned', LegitimatelySuperseded: 'Superseded after release', Mismatch: 'Mismatch', Missing: 'Missing',
  NotReleased: 'Not released', Obsolete: 'Obsolete', RequiresReview: 'Requires review',
};
const STATE_PILL = { Current: 'ok', Pinned: 'ok', LegitimatelySuperseded: 'info', Mismatch: 'bad', Missing: 'bad', NotReleased: 'bad', Obsolete: 'bad', RequiresReview: 'warn' };

/** Which revision of each document applies to an order and whether the order matches it (V2.3). */
export async function appendApplicability(page, orderNumber, me, supplied, onChanged) {
  const section = document.createElement('section');
  section.className = 'panel';
  page.append(section);
  return renderApplicability(section, orderNumber, me, supplied, onChanged);
}

async function renderApplicability(section, orderNumber, me, supplied, onChanged) {
  const res = supplied ?? await tryGet(`/api/planning/applicability?order=${orderNumber}`);
  if (res.error) { section.innerHTML = `<header><h2>Applicable documentation</h2></header>${errorBlock(res.error, 'applicability')}`; return null; }
  const d = res.data;
  section.innerHTML = `
    <header><h2>Applicable documentation</h2><span class="spacer"></span>
      <span class="pill ${d.enforced ? 'info' : 'none'}">${d.enforced ? 'Blocks readiness' : `Not enforced for ${esc(d.itemClass)} items`}</span>
      ${d.documents.some(x => x.state === 'Mismatch') && canAction(me, 'execution.orders.release') ? '<button class="btn small primary" data-reissue>Reissue documentation</button>' : ''}</header>
    <div class="muted" style="margin-bottom:8px">${esc(d.summary)} Released orders are compared with the revision that applied when they were released.</div>
    ${!d.documents.length ? emptyBlock('No documents required', `${d.partNumber} rev ${d.revision} lists no documents and nothing is attached to follow.`) : `
    <div class="table-wrap"><table class="table"><thead><tr><th>Document</th><th>Required by</th><th>Released with</th><th>Applies now</th><th>State</th><th>Why</th></tr></thead><tbody>
    ${d.documents.map(x => `<tr><td>${x.title ? `<a href="#/engineering/documents/${encodeURIComponent(x.documentNumber)}"><b>${esc(x.documentNumber)}</b></a> ${esc(x.title)}` : `<i>${esc(x.documentNumber)}</i>`}</td>
      <td>${esc(x.source)}</td><td>${esc(x.atRelease ?? '—')}</td><td>${esc(x.applicable ?? '—')}${x.pinnedRevision ? ' <span class="faint">(pinned)</span>' : ''}</td>
      <td><span class="pill ${STATE_PILL[x.state] ?? 'none'}">${esc(STATE_LABEL[x.state] ?? x.state)}</span></td><td class="muted">${esc(x.explanation)}</td></tr>`).join('')}
    </tbody></table></div>`}`;
  section.querySelector('[data-reissue]')?.addEventListener('click', () => formModal({
    title: `Reissue documentation for order ${orderNumber}`,
    detail: 'Do this when the floor has the revisions that apply now (traveler reprinted, old copies withdrawn). The order then references them.',
    reasonRequired: true, confirmLabel: 'Reissue', fields: [],
  }, (_, reason) => command(`/api/engineering/orders/${orderNumber}/reissue-documentation`, {}, reason), () => (onChanged ? onChanged() : renderApplicability(section, orderNumber, me))));
  return d;
}
