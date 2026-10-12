import { command } from './api.js';
import { esc, $$, errorBlock, emptyBlock, fmtDateTime } from './ui.js';
import { formModal, canAction } from './forms.js';
import { attachmentsDrawer } from './attachments.js';

export function outsideProcessingTab(result, me) {
  if (result.error) return errorBlock(result.error, 'outside processing');
  const records = result.data?.dispatches ?? [];
  const actions = canAction(me, 'inventory.transact') ? '<div class="toolbar"><button class="btn" data-process-dispatch>Send to processor</button><button class="btn" data-process-return>Receive from processor</button></div>' : '';
  return `${actions}<p class="muted">Company ownership and material identity stay with each lot. Returned material waits in Receiving for inspection, results and its required processor certificate.</p>
    ${!records.length ? emptyBlock('No sent process configurations', 'Outside-processor balances remain visible in Stock. New dispatches record the material revision and process specification sent with the approved service order.') : `<table class="table"><thead><tr><th>Material / lot</th><th>Processor / service order</th><th>Sent configuration</th><th>Expected return</th><th>Sent</th><th>Evidence</th></tr></thead><tbody>
    ${records.map(d => `<tr><td>${esc(d.partNumber)} · ${esc(d.lotNumber)}<div class="faint">Company owned · identity preserved</div></td><td>${esc(d.processorLocation)} · PO ${d.purchaseOrder}/${d.purchaseOrderLine}</td><td>Material ${esc(d.materialRevision ?? 'revision not recorded')}<br>${esc(d.specificationDocument ?? 'Specification not recorded')}${d.specificationRevision ? ` rev ${esc(d.specificationRevision)}` : ''}<div class="faint">${esc(d.technicalPackage.summary)}</div></td><td>${esc(d.expectedReturnDate ?? 'Not recorded')}</td><td>${esc(fmtDateTime(d.dispatchedAt))}<div class="faint">Ledger transaction ${d.transactionId}</div></td><td><button class="btn small" data-process-evidence="${esc(d.key)}">${d.requireCertificate ? 'Required certificate' : 'Evidence'}</button></td></tr>`).join('')}
    </tbody></table>`}`;
}

export function bindOutsideProcessingTab(page, locations, reload, me) {
  const company = locations.filter(l => l.active && l.inCompanyCustody).map(l => [l.code, l.name]);
  const processors = locations.filter(l => l.active && l.kind === 'ExternalProcessor').map(l => [l.code, `${l.name} (${l.custodianSupplier})`]);
  const common = [
    { name: 'partNumber', label: 'Part', code: true, required: true }, { name: 'lotNumber', label: 'Lot or serial', code: true, required: true },
    { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', required: true },
    { name: 'processorLocation', label: 'Processor location', type: 'select', options: processors, required: true },
    { name: 'purchaseOrder', label: 'Approved service purchase order', type: 'number', min: 1, required: true },
    { name: 'purchaseOrderLine', label: 'Service line', type: 'number', min: 1, required: true },
  ];
  const body = v => ({ ...v, quantity: Number(v.quantity), purchaseOrder: Number(v.purchaseOrder), purchaseOrderLine: Number(v.purchaseOrderLine) });
  $$('[data-process-dispatch]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: 'Send company material to a processor', confirmLabel: 'Dispatch', reasonRequired: true,
    detail: 'Released, usable material and an approved processor service order are required. Active shipping holds stop dispatch.',
    fields: [...common, { name: 'fromLocation', label: 'From company location', type: 'select', options: company, required: true },
      { name: 'materialRevision', label: 'Material revision (if recorded on the lot)' },
      { name: 'specificationDocument', label: 'Controlled process specification' }, { name: 'specificationRevision', label: 'Specification revision' },
      { name: 'expectedReturnDate', label: 'Expected return', type: 'date' },
      { name: 'requireCertificate', label: 'Acceptance evidence', type: 'check', value: true, text: 'Require processor certificate before acceptance' }],
  }, (v, reason) => command('/api/inventory/outside-processing/dispatch', { ...body(v), materialRevision: v.materialRevision || null,
    specificationDocument: v.specificationDocument || null, specificationRevision: v.specificationRevision || null, expectedReturnDate: v.expectedReturnDate || null, requireCertificate: !!v.requireCertificate }, reason), reload)));
  $$('[data-process-return]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: 'Receive from outside processing', confirmLabel: 'Receive for inspection', reasonRequired: true,
    detail: 'Use the lot, processor and service order it was sent under. Partial returns keep the remaining quantity with the processor.',
    fields: [...common, { name: 'toLocation', label: 'Into company location', type: 'select', options: company, required: true }],
  }, (v, reason) => command('/api/inventory/outside-processing/return', body(v), reason), reload)));
  $$('[data-process-evidence]', page).forEach(b => b.addEventListener('click', () => attachmentsDrawer('Processor evidence', {
    type: 'OutsideProcess', id: b.dataset.processEvidence, me, note: 'Attach the processor certificate here. Acceptance is recorded separately in Receiving.',
  })));
}
