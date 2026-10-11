import { tryGet, command } from './api.js';
import { esc, $$, fmtDateTime, errorBlock, emptyBlock } from './ui.js';
import { formModal, qty, can } from './forms.js';

// Receiving inspection, supplier dispositions, returns to vendor and customer returns (V3.11).
// A receipt is accepted in part; FAI pending determination is never shown as complete.

const FAI = {
  NotApplicable: ['none', 'n/a'], NotRequired: ['none', 'Not required'], PendingDetermination: ['warn', 'Pending determination'], Required: ['bad', 'Required — not done'], Complete: ['ok', 'Complete'],
};

export async function loadReceiving() {
  const [receipts, rejections, rtvs, returns] = await Promise.all([
    tryGet('/api/inventory/receiving?take=100'), tryGet('/api/inventory/rejections?open=true'), tryGet('/api/inventory/rtvs?open=true'), tryGet('/api/inventory/customer-returns'),
  ]);
  return { receipts, rejections, rtvs, returns };
}

export function receivingCount(rec) {
  return (rec.receipts.data ?? []).filter(r => r.remaining > 0).length + (rec.rejections.data?.length ?? 0) + (rec.rtvs.data?.length ?? 0);
}

export function receivingTab(rec, me) {
  const inspect = can(me, 'Inspector', 'QualityEngineer');
  const disposition = can(me, 'QualityEngineer');
  const approve = can(me, 'QualityManager');
  const move = can(me, 'MaterialHandler');
  const open = (rec.receipts.data ?? []).filter(r => r.remaining > 0 || r.inspections.length === 0);
  const pendingReturns = (rec.returns.data ?? []).filter(r => !r.disposition);
  return `
    <h3>Receipts waiting for inspection</h3>
    ${rec.receipts.error ? errorBlock(rec.receipts.error, 'receipts') : !open.length ? emptyBlock('Nothing to inspect', 'Receipts made into inspection appear here until all of each is accepted or rejected.') : `
    <table class="table"><thead><tr><th>Receipt</th><th>Part</th><th>Supplier / PO</th><th class="num">Received</th><th class="num">Accepted</th><th class="num">Rejected</th><th class="num">To inspect</th><th>FAI</th><th></th></tr></thead><tbody>
    ${open.map(r => { const [cls, label] = FAI[r.faiStatus] ?? ['none', r.faiStatus]; return `<tr><td>${r.receipt.transactionId}<div class="faint">${esc(fmtDateTime(r.receipt.at))}</div></td>
      <td>${esc(r.receipt.partNumber)}${r.receipt.lotNumber ? ` <span class="faint">lot ${esc(r.receipt.lotNumber)}</span>` : ''}</td><td>${esc(r.supplierCode ?? '—')}${r.receipt.poNumber ? ` · PO ${r.receipt.poNumber}` : ''}</td>
      <td class="num">${qty(r.receipt.quantity)}</td><td class="num">${qty(r.accepted)}</td><td class="num">${qty(r.rejected)}</td><td class="num">${qty(r.remaining)}</td>
      <td><span class="pill ${cls}" title="${esc(r.faiSubject)}">${esc(label)}</span></td>
      <td>${inspect && r.remaining > 0 ? `<button class="btn small" data-inspect="${r.receipt.transactionId}" data-left="${r.remaining}">Inspect</button>` : ''}</td></tr>`; }).join('')}
    </tbody></table>`}
    <h3>Rejected material</h3>
    ${!(rec.rejections.data ?? []).length ? '<div class="muted">No rejected material waiting for a decision.</div>' : `
    <table class="table"><thead><tr><th>Rejection</th><th>Part</th><th>Supplier</th><th class="num">Qty</th><th>Defect</th><th>Disposition</th><th></th></tr></thead><tbody>
    ${rec.rejections.data.map(r => `<tr><td>${r.rejectionId}</td><td>${esc(r.partNumber)}${r.lotNumber ? ` <span class="faint">lot ${esc(r.lotNumber)}</span>` : ''}</td><td>${esc(r.supplierCode ?? '—')}</td>
      <td class="num">${qty(r.quantity)}</td><td>${esc(r.defect)}</td><td>${esc(r.disposition)}${r.disposition === 'UseAsIs' ? ' <span class="faint">(waiting for approval)</span>' : ''}</td>
      <td>${disposition && r.disposition === 'Pending' ? `<button class="btn small" data-dispose="${r.rejectionId}">Disposition</button>` : ''}${approve && r.disposition === 'UseAsIs' ? `<button class="btn small" data-approve="${r.rejectionId}">Approve use as is</button>` : ''}</td></tr>`).join('')}
    </tbody></table>`}
    <h3>Returns to vendor</h3>
    ${!(rec.rtvs.data ?? []).length ? '<div class="muted">No open returns to vendor.</div>' : `
    <table class="table"><thead><tr><th>RTV</th><th>Part</th><th>Supplier</th><th class="num">Qty</th><th>Status</th><th></th></tr></thead><tbody>
    ${rec.rtvs.data.map(r => `<tr><td>${r.rtvId}</td><td>${esc(r.partNumber)}${r.lotNumber ? ` <span class="faint">lot ${esc(r.lotNumber)}</span>` : ''}</td><td>${esc(r.supplierCode ?? '—')}</td><td class="num">${qty(r.quantity)}</td>
      <td>${esc(r.status)}${r.forRework ? ' <span class="faint">(rework)</span>' : ''}</td>
      <td>${move && r.status === 'Open' ? `<button class="btn small" data-rtv-ship="${r.rtvId}">Ship back</button>` : ''}${move && r.status === 'Shipped' ? `<button class="btn small" data-rtv-replace="${r.rtvId}" data-qty="${r.quantity}">Receive replacement</button>` : ''}</td></tr>`).join('')}
    </tbody></table>`}
    <h3>Customer returns</h3>
    ${move ? '<div class="toolbar"><button class="btn small" data-rma-add>+ Customer return</button></div>' : ''}
    ${!pendingReturns.length ? '<div class="muted">No customer returns waiting for a decision.</div>' : `
    <table class="table"><thead><tr><th>Return</th><th>Customer</th><th>Shipment</th><th>Part</th><th class="num">Qty</th><th>Why</th><th></th></tr></thead><tbody>
    ${pendingReturns.map(r => `<tr><td>${r.rmaId}</td><td>${esc(r.customer)}</td><td>${r.shipmentId}</td><td>${esc(r.partNumber)}${r.lotNumber ? ` lot ${esc(r.lotNumber)}` : ''}</td><td class="num">${qty(r.quantity)}</td><td>${esc(r.reason)}</td>
      <td>${disposition ? `<button class="btn small" data-rma-dispose="${r.rmaId}">Disposition</button>` : ''}</td></tr>`).join('')}
    </tbody></table>`}`;
}

export function bindReceivingTab(page, locations, reload) {
  $$('[data-inspect]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: `Inspect receipt ${b.dataset.inspect}`, detail: `${b.dataset.left} still to inspect. Accept part, reject part; the rest waits. Material is not accepted until FAI is complete, not required or not applicable.`, confirmLabel: 'Record',
    fields: [
      { name: 'accepted', label: 'Accepted', type: 'number', min: 0, value: 0 }, { name: 'rejected', label: 'Rejected', type: 'number', min: 0, value: 0 },
      { name: 'defect', label: 'Defect (for a rejected quantity)' },
      { name: 'characteristic', label: 'Characteristic checked (optional)' }, { name: 'measured', label: 'Measured (optional)' },
      { name: 'pass', type: 'check', label: 'Result', text: 'Characteristic passed', value: true },
    ],
  }, (v, reason) => command(`/api/inventory/receiving/${b.dataset.inspect}/inspections`, {
    accepted: Number(v.accepted || 0), rejected: Number(v.rejected || 0), defect: v.defect || null,
    results: v.characteristic ? [{ characteristic: v.characteristic, requirement: null, measured: v.measured || null, pass: !!v.pass }] : [],
  }, reason), reload)));
  $$('[data-dispose]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: `Disposition rejection ${b.dataset.dispose}`, confirmLabel: 'Decide', reasonRequired: true,
    fields: [{ name: 'disposition', label: 'Disposition', type: 'select', options: [['ReturnToVendor', 'Return to vendor'], ['ReworkBySupplier', 'Rework by supplier'], ['Scrap', 'Scrap'], ['UseAsIs', 'Use as is (second person approves)']] }],
  }, (v, reason) => command(`/api/inventory/rejections/${b.dataset.dispose}/disposition`, v, reason), reload)));
  $$('[data-approve]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: `Approve use as is for rejection ${b.dataset.approve}`, confirmLabel: 'Approve', reasonRequired: true, fields: [],
  }, (v, reason) => command(`/api/inventory/rejections/${b.dataset.approve}/approve`, {}, reason), reload)));
  $$('[data-rtv-ship]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: `Ship return to vendor ${b.dataset.rtvShip}`, confirmLabel: 'Ship', fields: [{ name: 'carrier', label: 'Carrier (optional)' }],
  }, (v, reason) => command(`/api/inventory/rtvs/${b.dataset.rtvShip}/ship`, { carrier: v.carrier || null }, reason), reload)));
  $$('[data-rtv-replace]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: `Replacement for return to vendor ${b.dataset.rtvReplace}`, detail: 'Replacement material is a new instance; the returned one keeps its history.', confirmLabel: 'Receive',
    fields: [
      { name: 'quantity', label: 'Quantity', type: 'number', min: 0, value: b.dataset.qty }, { name: 'lotNumber', label: 'New lot (lot-tracked parts)', code: true },
      { name: 'locationCode', label: 'Into', type: 'select', options: locations, required: true }, { name: 'inspect', type: 'check', label: 'Inspection', text: 'Receive into inspection', value: true },
    ],
  }, (v, reason) => command(`/api/inventory/rtvs/${b.dataset.rtvReplace}/replacement`, { quantity: Number(v.quantity), lotNumber: v.lotNumber || null, locationCode: v.locationCode, inspect: !!v.inspect }, reason), reload)));
  $$('[data-rma-add]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: 'Customer return', detail: 'Against the shipment it left on; it comes back onto hold.', confirmLabel: 'Receive', reasonRequired: true,
    fields: [
      { name: 'shipmentId', label: 'Shipment', type: 'number', required: true }, { name: 'partNumber', label: 'Part', code: true, required: true }, { name: 'lotNumber', label: 'Lot (if tracked)', code: true },
      { name: 'quantity', label: 'Quantity', type: 'number', min: 0, required: true }, { name: 'locationCode', label: 'Into', type: 'select', options: locations, required: true },
    ],
  }, (v, reason) => command('/api/inventory/customer-returns', { ...v, shipmentId: Number(v.shipmentId), quantity: Number(v.quantity), lotNumber: v.lotNumber || null }, reason), reload)));
  $$('[data-rma-dispose]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: `Disposition return ${b.dataset.rmaDispose}`, confirmLabel: 'Decide', reasonRequired: true,
    fields: [{ name: 'disposition', label: 'Disposition', type: 'select', options: [['Restock', 'Restock (release)'], ['Rework', 'Rework (stays on hold)'], ['Scrap', 'Scrap']] }],
  }, (v, reason) => command(`/api/inventory/customer-returns/${b.dataset.rmaDispose}/disposition`, v, reason), reload)));
}
