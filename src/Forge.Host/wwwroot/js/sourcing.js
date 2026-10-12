import { tryGet, command, upload } from './api.js';
import { attachmentsDrawer } from './attachments.js';
import { esc, $, $$ } from './ui.js';
import { fmtDate, errorBlock, emptyBlock, drawer, confirmWithReason, toast } from './ui.js';
import { formModal, qty, canAction, plusDays } from './forms.js';

export function sourcingQueue(page, { me, data, tabs, bindTabs, reload }) {
  const buyer = canAction(me, 'procurement.manage'), approver = canAction(me, 'procurement.approve');
  const list = data.rfqs.data ?? [];
  page.innerHTML = `<div class="page-head"><div><h1>Procurement</h1><div class="sub">Request supplier responses, compare factual terms and record a human source decision. RFQs and quotes do not count as supply.</div></div><span class="spacer"></span>${buyer ? '<button class="btn primary" id="new-rfq">New RFQ</button>' : ''}</div>${tabs()}
    ${data.rfqs.error ? errorBlock(data.rfqs.error, 'RFQs') : !list.length ? emptyBlock('No RFQs', 'Create one for an existing open requisition.') : list.map(({ rfq: r, need: n }) => `<section class="panel"><header><h2>RFQ ${r.rfqId} · ${esc(r.title)}</h2><span class="pill">${esc(r.status)}</span><span class="spacer"></span>
      <button class="btn small" data-rfq-docs="${r.rfqId}">Documents</button><button class="btn small" data-rfq-compare="${r.rfqId}">Responses / decision</button>
      ${r.status === 'Draft' && approver && r.createdBy !== 'person:' + me.personId ? `<button class="btn small primary" data-rfq-release="${r.rfqId}">Release</button>` : ''}
      ${['Draft', 'Released'].includes(r.status) && buyer ? `<button class="btn small ghost" data-rfq-cancel="${r.rfqId}">Cancel</button>` : ''}</header>
      <p>Requisition ${n.requisitionId} · <b>${esc(n.partNumber)}</b>${n.requiredRevision ? ' rev ' + esc(n.requiredRevision) : ''} · ${qty(n.quantity)} by ${esc(fmtDate(n.needDate))}</p><div class="faint">${esc(n.sourceLabel)} · invited: ${esc(r.supplierCodes.join(', '))}</div>
      ${r.package ? `<details><summary>Technical package sent ${esc(fmtDate(r.package.recordedAt))}</summary><p>${esc(r.package.summary)}</p><ul>${r.package.documents.map(d => `<li>${esc(d.documentNumber)} · rev ${esc(d.revision ?? '—')} · ${esc(d.state)} · ${esc(d.fileName ?? 'No file')}</li>`).join('')}</ul></details>` : ''}
      ${r.selectedQuoteId ? `<p>Selected response ${r.selectedQuoteId} · ${esc(r.selectedBy)} · ${esc(r.selectionReason)}</p>` : ''}</section>`).join('')}`;
  bindTabs();
  $('#new-rfq', page)?.addEventListener('click', create);
  $$('[data-rfq-docs]', page).forEach(b => b.onclick = () => attachmentsDrawer(`RFQ ${b.dataset.rfqDocs}`, { type: 'Rfq', id: b.dataset.rfqDocs, me }));
  $$('[data-rfq-compare]', page).forEach(b => b.onclick = () => compare(Number(b.dataset.rfqCompare)));
  for (const action of ['release', 'cancel']) $$(`[data-rfq-${action}]`, page).forEach(b => b.onclick = async () => {
    const id = b.dataset[action === 'release' ? 'rfqRelease' : 'rfqCancel'];
    const result = await confirmWithReason({ title: `${action === 'release' ? 'Release' : 'Cancel'} RFQ ${id}`, confirmLabel: action === 'release' ? 'Release' : 'Cancel', reasonRequired: true }, reason => command(`/api/procurement/rfqs/${id}/${action}`, {}, reason));
    if (result) { toast(result.message); await reload(); }
  });
  function create() {
    return formModal({ title: 'New RFQ', reasonRequired: true, fields: [
      { name: 'requisitionId', label: 'Open requisition', type: 'select', required: true, options: (data.requisitions.data ?? []).map(n => [n.requisitionId, `${n.requisitionId}: ${n.partNumber} · ${n.sourceLabel}`]) },
      { name: 'title', label: 'Title', required: true },
      { name: 'suppliers', label: 'Supplier codes, separated by commas', required: true, hint: (data.suppliers.data ?? []).filter(s => s.status === 'Active').map(s => s.code).join(', ') },
    ] }, (v, reason) => command('/api/procurement/rfqs', { requisitionId: Number(v.requisitionId), title: v.title, supplierCodes: v.suppliers.split(',').map(s => s.trim()).filter(Boolean) }, reason), reload);
  }
  async function compare(id) {
    const { rfq: r, need: n } = list.find(x => x.rfq.rfqId === id);
    const response = await tryGet(`/api/procurement/rfqs/${id}/quotes`);
    const quotes = response.data ?? [];
    const { element: el } = drawer(`RFQ ${id} — supplier responses`, `<p>${esc(n.partNumber)} · ${qty(n.quantity)} by ${esc(fmtDate(n.needDate))}. Prices are recorded in the quoted currency; no conversion or automatic ranking is applied.</p>
      ${buyer && r.status === 'Released' ? '<button class="btn primary" id="record-response">Record supplier response</button>' : ''}
      ${response.error ? errorBlock(response.error, 'responses') : !quotes.length ? emptyBlock('No responses', 'Record the original supplier file with its structured terms.') : `<table class="table"><thead><tr><th>Supplier / response</th><th>Qty</th><th>Unit price</th><th>Promised / valid through</th><th>Configuration</th><th>Evidence / decision</th></tr></thead><tbody>${quotes.map(q => `<tr><td>${esc(q.supplierCode)} · ${q.quoteId}</td><td>${qty(q.quantity)}</td><td>${qty(q.unitPrice)} ${esc(q.currency)}</td><td>${esc(fmtDate(q.promisedDate))}<div class="faint">valid through ${esc(fmtDate(q.validThrough))}</div></td><td>${q.proposedAlternatePart ? `Proposes ${esc(q.proposedAlternatePart)} — engineering resolution required` : esc(n.partNumber)}</td><td><button class="btn small" data-response-docs="${q.quoteId}">Evidence</button> <a href="#/engineering/documents/${encodeURIComponent(q.originalDocumentNumber)}">${esc(q.originalDocumentNumber)}</a>
        ${r.selectedQuoteId === q.quoteId ? '<span class="pill ok">Selected</span>' : r.status === 'Released' && approver && q.recordedBy !== 'person:' + me.personId ? `<button class="btn small" data-select-response="${q.quoteId}">Select</button>` : ''}</td></tr>`).join('')}</tbody></table>`}`);
    $('#record-response', el)?.addEventListener('click', () => record(r));
    $$('[data-response-docs]', el).forEach(b => b.onclick = () => attachmentsDrawer(`Response ${b.dataset.responseDocs}`, { type: 'SupplierQuote', id: b.dataset.responseDocs, me }));
    $$('[data-select-response]', el).forEach(b => b.onclick = async () => {
      const result = await confirmWithReason({ title: `Select response ${b.dataset.selectResponse}`, detail: 'Record the human source decision. This creates no purchase order and commits no supply.', confirmLabel: 'Select source', reasonRequired: true }, reason => command(`/api/procurement/rfqs/${id}/select`, { quoteId: Number(b.dataset.selectResponse) }, reason));
      if (result) { toast(result.message); await reload(); await compare(id); }
    });
  }
  function record(r) {
    return formModal({ title: `Record response — RFQ ${r.rfqId}`, reasonRequired: true, fields: [
      { name: 'supplierCode', label: 'Supplier', type: 'select', options: r.supplierCodes.map(s => [s, s]), required: true },
      { name: 'quantity', label: 'Offered quantity', type: 'number', step: 'any', min: 0, required: true },
      { name: 'unitPrice', label: 'Unit price', type: 'number', step: 'any', min: 0, required: true },
      { name: 'currency', label: 'Three-letter currency', value: 'USD', code: true, required: true },
      { name: 'promisedDate', label: 'Promised date', type: 'date', value: plusDays(14), required: true },
      { name: 'validThrough', label: 'Valid through', type: 'date', value: plusDays(7), required: true },
      { name: 'proposedAlternatePart', label: 'Proposed alternate part (optional)', code: true },
      { name: 'original', label: 'Original supplier response', type: 'file', required: true },
      { name: 'note', label: 'Note', type: 'textarea' },
    ] }, async (v, reason) => {
      const file = v.original;
      const uploaded = await upload(file);
      const attached = await command(`/api/engineering/attachments/Rfq/${r.rfqId}/file`, { fileSha256: uploaded, fileName: file.name, type: 'Other' }, reason);
      const links = await tryGet(`/api/engineering/attachments/Rfq/${r.rfqId}`);
      if (links.error) throw new Error(links.error);
      const link = links.data.find(l => String(l.linkId) === String(attached.entityId));
      if (!link) throw new Error('The supplier file was attached but could not be resolved. Review RFQ Documents before retrying.');
      const { original, ...terms } = v;
      return command(`/api/procurement/rfqs/${r.rfqId}/quotes`, { ...terms, originalDocumentNumber: link.documentNumber, originalDocumentRevision: link.revision.revision }, reason);
    }, reload);
  }
}
