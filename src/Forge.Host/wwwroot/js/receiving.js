import { tryGet, command } from './api.js';
import { esc, $$, fmtDateTime, errorBlock, emptyBlock } from './ui.js';
import { formModal, qty, canAction } from './forms.js';
import { attachmentsDrawer } from './attachments.js';

// Receiving inspection, supplier dispositions, returns to vendor and customer returns (V3.11).
// A receipt is accepted in part; FAI pending determination is never shown as complete.

const FAI = {
  NotApplicable: ['none', 'n/a'], NotRequired: ['none', 'Not required'], PendingDetermination: ['warn', 'Pending determination'], Required: ['bad', 'Required — not done'], Complete: ['ok', 'Complete'],
};

/** "PART:REV@SUPPLIER" into its parts; null when the receipt has no supplier subject. */
export function faiParts(subject) {
  const m = /^([^:]+):([^@]+)@(.+)$/.exec(subject ?? '');
  return m ? { partNumber: m[1], revision: m[2], supplierCode: m[3] } : null;
}

/** "characteristic | requirement | measured | pass/fail", one result per line. */
export function faiResults(text) {
  const lines = String(text ?? '').split(/\r?\n/).map(l => l.trim()).filter(Boolean);
  if (!lines.length) throw new Error('Record the result of each characteristic checked.');
  return lines.map((line, i) => {
    const [characteristic, requirement, measured, result] = line.split('|').map(p => p.trim());
    if (!characteristic || !/^(pass|fail|ok|ng|yes|no)$/i.test(result ?? '')) throw new Error(`Result line ${i + 1}: use "characteristic | requirement | measured | pass or fail".`);
    return { characteristic, requirement: requirement || null, measured: measured || null, pass: /^(pass|ok|yes)$/i.test(result) };
  });
}

/** The disposition body (ADR 0055): blank or the whole quantity decides the rejection; less decides only that part. */
export function dispositionPayload(values, rejected) {
  const body = { disposition: values.disposition };
  const raw = String(values.quantity ?? '').trim();
  if (raw === '') return body;
  const quantity = Number(raw);
  if (!Number.isFinite(quantity) || quantity <= 0) throw new Error('Enter a quantity above zero, or leave it blank for the whole rejection.');
  if (quantity > Number(rejected)) throw new Error(`The rejection is for ${rejected}; a disposition cannot cover more.`);
  if (quantity !== Number(rejected)) body.quantity = quantity;
  return body;
}

/** The reduce body (ADR 0055): an open return to vendor only goes down; the rest stays on hold as a pending rejection. */
export function rtvReducePayload(values, current) {
  const quantity = Number(String(values.quantity ?? '').trim());
  if (!Number.isFinite(quantity) || quantity <= 0) throw new Error('Enter the quantity still going back, above zero.');
  if (quantity >= Number(current)) throw new Error(`This return is for ${current}; it can only be reduced.`);
  return { quantity };
}

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
  const inspect = canAction(me, 'quality.inspect');
  const disposition = canAction(me, 'quality.disposition');
  const approve = canAction(me, 'quality.approve');
  const move = canAction(me, 'inventory.transact');
  const plan = canAction(me, 'quality.plan');
  const credit = canAction(me, 'purchasing.approve');
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
      <td><button class="btn small ghost" data-receipt-evidence="${r.receipt.transactionId}">Evidence</button>${r.inspections.map(i => `<button class="btn small ghost" data-inspection-evidence="${i.inspectionId}">Inspection ${i.inspectionId} evidence</button>`).join('')}${plan && r.faiStatus === 'PendingDetermination' && faiParts(r.faiSubject) ? `<button class="btn small" data-fai-decide="${esc(r.faiSubject)}">Decide FAI</button>` : ''}${inspect && r.faiStatus === 'Required' && faiParts(r.faiSubject) ? `<button class="btn small" data-fai-record="${esc(r.faiSubject)}">Record FAI</button>` : ''}${inspect && r.remaining > 0 ? `<button class="btn small" data-inspect="${r.receipt.transactionId}" data-left="${r.remaining}">Inspect</button>` : ''}</td></tr>`; }).join('')}
    </tbody></table>`}
    <h3>Rejected material</h3>
    ${!(rec.rejections.data ?? []).length ? '<div class="muted">No rejected material waiting for a decision.</div>' : `
    <table class="table"><thead><tr><th>Rejection</th><th>Part</th><th>Supplier</th><th class="num">Qty</th><th>Defect</th><th>Disposition</th><th></th></tr></thead><tbody>
    ${rec.rejections.data.map(r => `<tr><td>${r.rejectionId}${r.splitFrom ? ` <span class="faint">(split from ${r.splitFrom})</span>` : ''}</td><td>${esc(r.partNumber)}${r.lotNumber ? ` <span class="faint">lot ${esc(r.lotNumber)}</span>` : ''}</td><td>${esc(r.supplierCode ?? '—')}</td>
      <td class="num">${qty(r.quantity)}</td><td>${esc(r.defect)}</td><td>${esc(r.disposition)}${r.disposition === 'UseAsIs' ? ' <span class="faint">(waiting for approval)</span>' : ''}</td>
      <td>${disposition && r.disposition === 'Pending' ? `<button class="btn small" data-dispose="${r.rejectionId}" data-qty="${r.quantity}">Disposition</button>` : ''}${approve && r.disposition === 'UseAsIs' ? `<button class="btn small" data-approve="${r.rejectionId}">Approve use as is</button>` : ''}</td></tr>`).join('')}
    </tbody></table>`}
    <h3>Returns to vendor</h3>
    ${!(rec.rtvs.data ?? []).length ? '<div class="muted">No open returns to vendor.</div>' : `
    <table class="table"><thead><tr><th>RTV</th><th>Part</th><th>Supplier</th><th class="num">Qty</th><th>Status</th><th></th></tr></thead><tbody>
    ${rec.rtvs.data.map(r => `<tr><td>${r.rtvId}</td><td>${esc(r.partNumber)}${r.lotNumber ? ` <span class="faint">lot ${esc(r.lotNumber)}</span>` : ''}</td><td>${esc(r.supplierCode ?? '—')}</td><td class="num">${qty(r.quantity)}</td>
      <td>${esc(r.status)}${r.forRework ? ' <span class="faint">(rework)</span>' : ''}</td>
      <td>${move && r.status === 'Open' ? `<button class="btn small" data-rtv-ship="${r.rtvId}">Ship back</button>` : ''}${disposition && r.status === 'Open' ? `<button class="btn small ghost" data-rtv-reduce="${r.rtvId}" data-qty="${r.quantity}">Reduce</button>` : ''}${move && r.status === 'Shipped' ? `<button class="btn small" data-rtv-replace="${r.rtvId}" data-qty="${r.quantity}">Receive replacement</button>` : ''}${credit && r.status === 'Shipped' ? `<button class="btn small ghost" data-rtv-credit="${r.rtvId}">Record credit</button>` : ''}</td></tr>`).join('')}
    </tbody></table>`}
    <h3>Customer returns</h3>
    ${move ? '<div class="toolbar"><button class="btn small" data-rma-add>+ Customer return</button></div>' : ''}
    ${!pendingReturns.length ? '<div class="muted">No customer returns waiting for a decision.</div>' : `
    <table class="table"><thead><tr><th>Return</th><th>Customer</th><th>Shipment</th><th>Part</th><th class="num">Qty</th><th>Why</th><th></th></tr></thead><tbody>
    ${pendingReturns.map(r => `<tr><td>${r.rmaId}</td><td>${esc(r.customer)}</td><td>${r.shipmentId}</td><td>${esc(r.partNumber)}${r.lotNumber ? ` lot ${esc(r.lotNumber)}` : ''}</td><td class="num">${qty(r.quantity)}</td><td>${esc(r.reason)}</td>
      <td>${move ? `<button class="btn small ghost" data-rma-prepare="${r.rmaId}" data-qty="${r.quantity}">Prepare inspection</button>` : ''}${disposition ? `<button class="btn small" data-rma-dispose="${r.rmaId}" data-qty="${r.quantity}">Disposition</button>` : ''}</td></tr>`).join('')}
    </tbody></table>`}`;
}

export function bindReceivingTab(page, locations, reload, me) {
  $$('[data-receipt-evidence]', page).forEach(b => b.addEventListener('click', () => attachmentsDrawer(`Receipt ${b.dataset.receiptEvidence} evidence`, { type: 'Receipt', id: b.dataset.receiptEvidence, me, note: 'Reference evidence does not complete the structured inspection.' })));
  $$('[data-inspection-evidence]', page).forEach(b => b.addEventListener('click', () => attachmentsDrawer(`Inspection ${b.dataset.inspectionEvidence} evidence`, { type: 'ReceivingInspection', id: b.dataset.inspectionEvidence, me })));
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
    detail: `${b.dataset.qty} rejected. Decide all of it, or part of it: the rest stays on hold as a new pending rejection that needs its own decision.`,
    fields: [{ name: 'disposition', label: 'Disposition', type: 'select', options: [['ReturnToVendor', 'Return to vendor'], ['ReworkBySupplier', 'Rework by supplier'], ['Scrap', 'Scrap'], ['UseAsIs', 'Use as is (second person approves)']] },
      { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', value: b.dataset.qty }],
  }, (v, reason) => command(`/api/inventory/rejections/${b.dataset.dispose}/disposition`, dispositionPayload(v, b.dataset.qty), reason), reload)));
  $$('[data-rtv-reduce]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: `Reduce return to vendor ${b.dataset.rtvReduce}`, confirmLabel: 'Reduce', reasonRequired: true,
    detail: `${b.dataset.qty} is going back. Enter what is still going back; the rest stays on hold as a pending rejection, for example to use as is with a second person's approval.`,
    fields: [{ name: 'quantity', label: 'Quantity still going back', type: 'number', min: 0, step: 'any', value: b.dataset.qty }],
  }, (v, reason) => command(`/api/inventory/rtvs/${b.dataset.rtvReduce}/reduce`, rtvReducePayload(v, b.dataset.qty), reason), reload)));
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
    detail: 'Where governed return acceptance is configured, a restock names the independent product release for the prepared return cohort.',
    fields: [{ name: 'disposition', label: 'Disposition', type: 'select', options: [['Restock', 'Restock (release)'], ['Rework', 'Rework (stays on hold)'], ['Scrap', 'Scrap']] },
      { name: 'decisionId', label: 'Product release decision (governed restock)', type: 'number', min: 1 }, { name: 'expectedDecisionVersion', label: 'Release decision version', type: 'number', min: 1 },
      { name: 'quantity', label: 'Quantity (governed disposition)', type: 'number', min: 0, step: 'any' }],
  }, (v, reason) => command(`/api/inventory/customer-returns/${b.dataset.rmaDispose}/disposition`, { disposition: v.disposition, decisionId: v.decisionId, expectedDecisionVersion: v.expectedDecisionVersion, quantity: v.quantity }, reason), reload)));
  $$('[data-rma-prepare]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: `Prepare return ${b.dataset.rmaPrepare} for inspection`, detail: 'Identifies the exact returned quantity as a cohort that product acceptance inspects and releases.', confirmLabel: 'Prepare', reasonRequired: true,
    fields: [{ name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', value: b.dataset.qty, required: true }, { name: 'locationCode', label: 'Inspection location (optional)', type: 'select', blank: '— where it is now —', options: locations }],
  }, (v, reason) => command(`/api/inventory/customer-returns/${b.dataset.rmaPrepare}/prepare-inspection`, { quantity: v.quantity, locationCode: v.locationCode }, reason), reload)));
  $$('[data-rtv-credit]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: `Record supplier credit for return ${b.dataset.rtvCredit}`, detail: 'Closes the return to vendor: the supplier credited it instead of replacing it.', confirmLabel: 'Record credit', reasonRequired: true, fields: [],
  }, (v, reason) => command(`/api/inventory/rtvs/${b.dataset.rtvCredit}/credit`, {}, reason), reload)));
  $$('[data-fai-decide]', page).forEach(b => b.addEventListener('click', () => { const s = faiParts(b.dataset.faiDecide); formModal({
    title: `FAI for ${s.partNumber} rev ${s.revision} from ${s.supplierCode}`, detail: 'Decide whether first article inspection applies. The reason is kept as the basis.', confirmLabel: 'Decide', reasonRequired: true,
    fields: [{ name: 'state', label: 'First article inspection', type: 'select', options: [['Required', 'Required'], ['NotRequired', 'Not required'], ['NotApplicable', 'Not applicable']] }],
  }, (v, reason) => command('/api/quality/fai-determinations', { ...s, state: v.state }, reason), reload); }));
  $$('[data-fai-record]', page).forEach(b => b.addEventListener('click', () => { const s = faiParts(b.dataset.faiRecord); formModal({
    title: `Record FAI: ${s.partNumber} rev ${s.revision} from ${s.supplierCode}`, detail: 'Backed by a released (signed) FAI report in document control. A failed characteristic means the FAI has not passed.', confirmLabel: 'Record FAI', reasonRequired: true,
    fields: [{ name: 'passed', label: 'Outcome', type: 'check', text: 'FAI passed', value: true },
      { name: 'results', label: 'Results', type: 'textarea', required: true, hint: 'One per line: characteristic | requirement | measured | pass or fail.' },
      { name: 'documentNumber', label: 'FAI report document', code: true, required: true }, { name: 'documentRevision', label: 'Report revision', code: true, required: true }],
  }, (v, reason) => command('/api/quality/fai', { ...s, passed: !!v.passed, results: faiResults(v.results), documentNumber: v.documentNumber, documentRevision: v.documentRevision }, reason), reload); }));
}
