import { sourcingQueue } from '../sourcing.js';
import { tryGet, command } from '../api.js';
import { who } from '../people.js';
import { attachmentsDrawer } from '../attachments.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast, drawer, enhanceTables } from '../ui.js';
import { formModal, qty, canAction, plusDays } from '../forms.js';

// Purchase orders (spec §48): a buyer drafts, someone else approves, and only approved open lines
// count as supply for MRP and material readiness. Supplier date changes carry a reason.

const STATUS_TONE = { Draft: 'none', Approved: 'ok', Closed: 'info', Cancelled: 'bad' };
const SOURCE_LABEL = {
  Production: 'Production', Maintenance: 'Maintenance', MroReplenishment: 'MRO replenishment', FacilityProject: 'Facility project', CapitalProject: 'Capital project',
  Quality: 'Quality', Tooling: 'Tooling', Engineering: 'Engineering', CustomerOrder: 'Customer order', Forecast: 'Forecast', Service: 'Service', Internal: 'Internal',
  ContractDelivery: 'Contract delivery', SafetyStock: 'Safety stock', Mixed: 'Several sources',
};
const SUPPLIER_TONE = { Active: 'ok', OnHold: 'warn', Inactive: 'none' };
const SUPPLIER_LABEL = { Active: 'Active', OnHold: 'On hold', Inactive: 'Inactive' };

export async function procurementPage(page, ctx) {
  const me = ctx.me;
  const buyer = canAction(me, 'procurement.manage');
  const approver = canAction(me, 'procurement.approve');
  let showClosed = false;
  let tab = ctx.query.get('tab') ?? 'orders';
  let data = {};

  async function load() {
    const [orders, items, suppliers, sources, requisitions, rfqs] = await Promise.all([
      tryGet('/api/procurement/purchase-orders'), tryGet('/api/engineering/items'),
      tryGet('/api/procurement/suppliers'), tryGet('/api/procurement/approved-sources'), tryGet('/api/procurement/requisitions'), tryGet('/api/procurement/rfqs')]);
    data = { orders, items, suppliers, sources, requisitions, rfqs };
  }
  const supplierOptions = () => (data.suppliers.data ?? []).filter(x => x.status === 'Active').map(x => [x.code, `${x.name} (${x.code})`]);
  const reload = async () => { await load(); render(); };
  const buyParts = () => (data.items.data ?? []).filter(i => i.makeBuy === 'Buy').map(i => [i.partNumber, `${i.partNumber} — ${i.description}`]);

  function render() {
    if (tab === 'rfqs') return sourcingQueue(page, { me, data, tabs, bindTabs, reload });
    if (tab === 'suppliers') return renderSuppliers();
    if (tab === 'requisitions') return renderRequisitions();
    const all = data.orders.data ?? [];
    const list = all.filter(o => showClosed || ['Draft', 'Approved'].includes(o.status));
    const late = all.filter(o => o.status === 'Approved').flatMap(o => o.lines.filter(l => l.open > 0 && new Date(`${l.promisedDate}T23:59:59`) < new Date()));
    page.innerHTML = `
      <div class="page-head">
        <div><h1>Procurement</h1><div class="sub">Purchase orders and supplier promised dates. A buyer drafts; someone else approves. Only approved open lines count as supply.</div></div>
        <div class="spacer"></div>
        <label class="opt"><input type="checkbox" id="closed" ${showClosed ? 'checked' : ''}> Show closed and cancelled</label>
        ${buyer ? '<button class="btn primary" id="new-po">New purchase order</button>' : ''}
      </div>
      ${tabs()}
      ${late.length ? `<div class="notice bad">${late.length} line(s) are past their promised date and not fully received. Receive them or record the supplier's new date so MRP and the schedule stay honest.</div>` : ''}
      ${data.orders.error ? errorBlock(data.orders.error, 'purchase orders') : !list.length ? emptyBlock('No purchase orders', buyer ? 'Draft one here, or from a Buy recommendation on the Material Plan.' : 'A buyer drafts purchase orders.') : list.map(card).join('')}`;

    enhanceTables(page);
    bindTabs();
    $('#closed', page).onchange = e => { showClosed = e.target.checked; render(); };
    $('#new-po', page)?.addEventListener('click', newPo);
    $$('[data-approve]', page).forEach(b => b.addEventListener('click', () => act(b.dataset.approve, 'approve', 'Approve', 'Its open lines become supply for MRP and material readiness.')));
    $$('[data-close]', page).forEach(b => b.addEventListener('click', () => act(b.dataset.close, 'close', 'Close', 'Nothing more is expected against it, whatever was received.')));
    $$('[data-cancel]', page).forEach(b => b.addEventListener('click', () => act(b.dataset.cancel, 'cancel', 'Cancel', 'A draft is withdrawn.')));
    $$('[data-line]', page).forEach(b => b.addEventListener('click', () => addLine(Number(b.dataset.line))));
    $$('[data-docs-po]', page).forEach(b => b.addEventListener('click', () => attachmentsDrawer(`PO ${b.dataset.docsPo}`, { type: 'PurchaseOrder', id: b.dataset.docsPo, me })));
    $$('[data-date]', page).forEach(b => b.addEventListener('click', () => changeDate(...b.dataset.date.split('/').map(Number))));
    $$('[data-package]', page).forEach(b => b.addEventListener('click', () => showPackage(...b.dataset.package.split('/'))));
    $$('[data-why]', page).forEach(b => b.addEventListener('click', () => whyLine(...b.dataset.why.split('/'))));
    $$('[data-cancel-line]', page).forEach(b => b.addEventListener('click', async () => {
      const [po, line] = b.dataset.cancelLine.split('/');
      const done = await confirmWithReason({ title: `Cancel PO ${po} line ${line}`, confirmLabel: 'Cancel line' }, reason => command(`/api/procurement/purchase-orders/${po}/lines/${line}/cancel`, {}, reason));
      if (done) { toast(done.message); await reload(); }
    }));
  }

  function card(o) {
    const mine = o.createdBy === `person:${me.personId}`;
    const open = ['Draft', 'Approved'].includes(o.status);
    return `<section class="panel">
      <header><h2>PO ${o.poNumber} · ${esc(o.supplier)}</h2><span class="pill ${STATUS_TONE[o.status]}">${esc(o.status)}</span><span class="spacer"></span>
        ${o.status === 'Draft' && approver && !mine ? `<button class="btn primary small" data-approve="${o.poNumber}">Approve</button>` : ''}
        ${o.status === 'Draft' && approver && mine ? '<span class="faint">You raised it — someone else approves</span>' : ''}
        ${o.status === 'Draft' && buyer ? `<button class="btn small" data-line="${o.poNumber}">+ Line</button> <button class="btn small ghost" data-cancel="${o.poNumber}">Cancel</button>` : ''}
        ${o.status === 'Approved' && buyer ? `<button class="btn small" data-close="${o.poNumber}">Close</button>` : ''}
        <button class="btn small ghost" data-docs-po="${o.poNumber}">Documents</button></header>
      <div class="faint" style="margin-bottom:6px">Raised by ${esc(who(o.createdBy))} ${esc(fmtDateTime(o.createdAt))}${o.approvedBy ? ` · approved by ${esc(who(o.approvedBy))} ${esc(fmtDateTime(o.approvedAt))}` : ''}</div>
      <table class="table"><thead><tr><th>Line</th><th>Part</th><th class="num">Ordered</th><th class="num">Received</th><th class="num">Open</th><th>Promised</th><th></th></tr></thead><tbody>
        ${o.lines.map(l => {
          const overdue = o.status === 'Approved' && l.open > 0 && new Date(`${l.promisedDate}T23:59:59`) < new Date();
          return `<tr class="${l.status === 'Cancelled' ? 'retired' : ''}"><td>${l.lineNumber}</td><td><b>${esc(l.partNumber)}</b>${l.sourceKind ? `<div class="faint" title="${esc(l.sourceLabel)}">for ${esc(SOURCE_LABEL[l.sourceKind] ?? l.sourceKind)} ${esc(l.sourceKey)}</div>` : ''}</td>
            <td class="num">${qty(l.quantity)} <span class="faint">${esc(l.unitOfMeasure)}</span></td><td class="num">${qty(l.received)}</td><td class="num">${o.status === 'Approved' ? qty(l.open) : '—'}</td>
            <td class="${overdue ? 'c-bad' : ''}">${esc(fmtDate(l.promisedDate))}${overdue ? ' · overdue' : ''}${l.status === 'Cancelled' ? ' <span class="pill bad">Cancelled</span>' : ''}</td>
            <td class="nowrap">${open && buyer && l.status === 'Open' ? `<button class="btn small" data-date="${o.poNumber}/${l.lineNumber}">New date</button> <button class="btn small ghost" data-cancel-line="${o.poNumber}/${l.lineNumber}">Cancel</button>` : ''}
              <button class="btn small ghost" data-why="${o.poNumber}/${l.lineNumber}" title="Why was this purchased, what arrived and where it went">Why?</button>
              ${o.status !== 'Draft' ? ` <button class="btn small ghost" data-package="${o.poNumber}/${l.lineNumber}" title="What this line was bought to when the order was approved">Package</button>` : ''}</td></tr>`;
        }).join('')}
      </tbody></table></section>`;
  }

  function newPo() {
    const parts = buyParts();
    const lineFields = [1, 2, 3].flatMap(n => [
      { name: `part${n}`, label: `Line ${n} part${n > 1 ? ' (optional)' : ''}`, type: 'select', options: parts, blank: n > 1 ? '—' : undefined, required: n === 1 },
      { name: `qty${n}`, label: `Line ${n} quantity`, type: 'number', min: 0, step: 'any' },
      { name: `date${n}`, label: `Line ${n} promised date`, type: 'date', value: plusDays(14) },
    ]);
    return formModal({ title: 'New purchase order', detail: 'Saved as a draft. A purchasing manager other than you approves it.', confirmLabel: 'Draft purchase order',
      fields: [{ name: 'supplier', label: 'Supplier', type: 'select', options: supplierOptions(), required: true, hint: 'Only active suppliers in the supplier master. Add a supplier on the Suppliers tab.' }, ...lineFields] },
    (v, reason) => {
      const lines = [1, 2, 3].filter(n => v[`part${n}`]).map(n => {
        if (!v[`qty${n}`]) throw new Error(`Line ${n}: give a quantity.`);
        return { partNumber: v[`part${n}`], quantity: v[`qty${n}`], promisedDate: v[`date${n}`] };
      });
      return command('/api/procurement/purchase-orders', { supplier: v.supplier, lines }, reason);
    }, reload);
  }

  function addLine(po) {
    return formModal({ title: `Add a line to PO ${po}`, confirmLabel: 'Add line',
      fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: buyParts(), required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', required: true },
        { name: 'promisedDate', label: 'Promised date', type: 'date', value: plusDays(14), required: true },
      ] }, (v, reason) => command(`/api/procurement/purchase-orders/${po}/lines`, v, reason), reload);
  }

  // V2.5: the technical package recorded at approval — never changed afterwards, whatever engineering releases.
  async function showPackage(po, line) {
    const { data: p, error } = await tryGet(`/api/procurement/purchase-orders/${po}/lines/${line}/package`);
    drawer(`PO ${po} line ${line} — technical package`, error ? errorBlock(error, 'technical package') : `
      <p><b>${esc(p.partNumber)}</b> rev <b>${esc(p.itemRevision ?? '—')}</b></p>
      <div class="faint" style="margin-bottom:8px">Recorded when the order was approved, by ${esc(who(p.recordedBy))} ${esc(fmtDateTime(p.recordedAt))}. ${esc(p.summary)}</div>
      ${!p.documents.length ? emptyBlock('No documents', 'The item revision lists no documents and none were attached to follow.') : `
      <table class="table"><thead><tr><th>Document</th><th>Revision sent</th><th>File</th></tr></thead><tbody>
        ${p.documents.map(d => `<tr><td><b>${esc(d.documentNumber)}</b><div class="faint">${esc(d.title)} · ${esc(d.source)}</div></td><td>${esc(d.revision ?? '—')} <span class="faint">${esc(d.state)}</span></td>
          <td class="faint">${d.fileName ? `${esc(d.fileName)}<div title="SHA-256">${esc((d.fileSha256 ?? '').slice(0, 12))}…</div>` : '—'}</td></tr>`).join('')}
      </tbody></table>`}`);
  }

  // V2.7: why a line was bought, what arrived and where it went.
  async function whyLine(po, line) {
    const { data: w, error } = await tryGet(`/api/procurement/purchase-orders/${po}/lines/${line}/why`);
    const rows = list => list.map(t => `<tr><td>${esc(fmtDateTime(t.at))}</td><td>${esc(t.kind)}</td><td class="num">${qty(t.quantity)}</td><td>${esc(t.lotNumber ?? '—')}</td><td>${t.orderNumber ? `<a href="#/orders/${t.orderNumber}">order ${t.orderNumber}</a>` : t.workOrderId ? `work order ${t.workOrderId}` : esc(t.location ?? '')}</td></tr>`).join('');
    drawer(`PO ${po} line ${line} — why was this purchased?`, error ? errorBlock(error, 'the purchase') : `
      <p>${esc(w.answer)}</p>
      ${w.sourceKind ? `<dl class="kv"><dt>For</dt><dd>${esc(SOURCE_LABEL[w.sourceKind] ?? w.sourceKind)} <b>${esc(w.sourceKey ?? '')}</b></dd><dt>Why</dt><dd>${esc(w.sourceLabel ?? '')}</dd>
        ${w.requisition ? `<dt>Requisition</dt><dd>${w.requisition.requisitionId}, raised by ${esc(who(w.requisition.requestedBy))} ${esc(fmtDateTime(w.requisition.requestedAt))}${w.requisition.requiredRevision ? ` · rev ${esc(w.requisition.requiredRevision)} required` : ''}</dd>` : ''}
        ${w.demand ? `<dt>Demand</dt><dd>${w.demand.demandId}: ${qty(w.demand.quantity)} by ${esc(fmtDate(w.demand.needDate))}${w.demand.reference ? ` · ${esc(w.demand.reference)}` : ''}</dd>` : ''}</dl>` : ''}
      <h3 style="margin:12px 0 4px">Received</h3>${w.receipts.length ? `<table class="table"><tbody>${rows(w.receipts)}</tbody></table>` : '<div class="muted">Nothing yet.</div>'}
      <h3 style="margin:12px 0 4px">Where it went</h3>${w.uses.length ? `<table class="table"><tbody>${rows(w.uses)}</tbody></table>` : '<div class="muted">Nothing traced.</div>'}`);
  }

  // V2.7: requisitions from every source, grouped by what they are for.
  function renderRequisitions() {
    const res = data.requisitions;
    const list = res.data ?? [];
    const groups = Object.keys(SOURCE_LABEL).map(k => [k, list.filter(r => r.sourceKind === k)]).filter(([, rs]) => rs.length);
    page.innerHTML = `
      <div class="page-head">
        <div><h1>Procurement</h1><div class="sub">Requisitions from every source in one queue. Each says what it is for; the purchase order line keeps that, so every receipt and issue can answer "why was this purchased?".</div></div>
        <div class="spacer"></div>${canAction(me, 'procurement.requisition.raise') ? '<button class="btn primary" id="raise">Raise a requisition</button>' : ''}
      </div>
      ${tabs()}
      ${res.error ? errorBlock(res.error, 'requisitions') : !groups.length ? emptyBlock('No open requisitions', 'Raise one here, or from a Buy recommendation on the Material Plan.') : groups.map(([k, rs]) => `
        <section class="panel"><header><h2>${esc(SOURCE_LABEL[k])} (${rs.length})</h2></header>
          <table class="table"><thead><tr><th>#</th><th>Part</th><th class="num">Qty</th><th>Need by</th><th>For</th><th>Config / supplier</th><th>Raised</th><th></th></tr></thead><tbody>
          ${rs.map(r => `<tr><td>${r.requisitionId}</td><td><b>${esc(r.partNumber)}</b></td><td class="num">${qty(r.quantity)}</td><td class="nowrap">${esc(fmtDate(r.needDate))}</td>
            <td><b>${esc(r.sourceKey)}</b><div class="muted">${esc(r.sourceLabel)}</div></td>
            <td>${r.requiredRevision ? `rev ${esc(r.requiredRevision)}` : '<span class="faint">any released rev</span>'}${r.supplierCode ? `<div class="faint">prefers ${esc(r.supplierCode)}</div>` : ''}</td>
            <td>${esc(who(r.requestedBy))}<div class="faint">${esc(fmtDateTime(r.requestedAt))}</div></td>
            <td class="nowrap">${buyer ? `<button class="btn small primary" data-order-req="${r.requisitionId}">Order</button> <button class="btn small ghost" data-alts-req="${esc(r.partNumber)}">Alternates</button>` : ''}
              <button class="btn small ghost" data-cancel-req="${r.requisitionId}">Cancel</button></td></tr>`).join('')}
          </tbody></table></section>`).join('')}`;
    enhanceTables(page);
    bindTabs();
    $('#raise', page)?.addEventListener('click', raise);
    $$('[data-order-req]', page).forEach(b => b.addEventListener('click', () => orderRequisition(list.find(r => String(r.requisitionId) === b.dataset.orderReq))));
    $$('[data-alts-req]', page).forEach(b => b.addEventListener('click', () => alternatesFor(b.dataset.altsReq)));
    $$('[data-cancel-req]', page).forEach(b => b.addEventListener('click', async () => {
      const done = await confirmWithReason({ title: `Cancel requisition ${b.dataset.cancelReq}`, confirmLabel: 'Cancel requisition' }, reason => command(`/api/procurement/requisitions/${b.dataset.cancelReq}/cancel`, {}, reason));
      if (done) { toast(done.message); await reload(); }
    }));
  }

  function raise() {
    return formModal({ title: 'Raise a requisition', detail: 'Say what it is for: the source stays with the purchase order line, its receipts and where the stock goes.', confirmLabel: 'Raise',
      fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: buyParts(), required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', required: true },
        { name: 'needDate', label: 'Needed by', type: 'date', value: plusDays(14), required: true },
        { name: 'sourceKind', label: 'For', type: 'select', value: 'Maintenance', options: Object.entries(SOURCE_LABEL).filter(([k]) => !['Mixed', 'SafetyStock', 'ContractDelivery'].includes(k)) },
        { name: 'sourceKey', label: 'Reference (kind:key)', required: true, placeholder: 'work-order:57' },
        { name: 'sourceLabel', label: 'What it is for', required: true, placeholder: 'Replace spindle bearings on SL-02' },
        { name: 'requiredRevision', label: 'Required revision (optional)', code: true },
        { name: 'supplierCode', label: 'Preferred supplier (optional)', type: 'select', blank: '—', options: supplierOptions() },
      ] }, (v, reason) => command('/api/procurement/requisitions', v, reason), reload);
  }

  function orderRequisition(r) {
    const drafts = (data.orders.data ?? []).filter(o => o.status === 'Draft');
    return formModal({ title: `Order requisition ${r.requisitionId}: ${r.partNumber}`, detail: `For ${r.sourceKey}: ${r.sourceLabel}. The purchase order line keeps this source.`, confirmLabel: 'Order',
      fields: [
        { name: 'po', label: 'Add to', type: 'select', value: '', options: [['', 'A new purchase order'], ...drafts.map(o => [String(o.poNumber), `Draft PO ${o.poNumber} · ${o.supplier}`])] },
        { name: 'supplier', label: 'Supplier (new order)', type: 'select', value: r.supplierCode ?? '', blank: '—', options: supplierOptions() },
        { name: 'quantity', label: 'Quantity', type: 'number', value: r.quantity, min: 0, step: 'any', required: true },
        { name: 'promisedDate', label: 'Promised date', type: 'date', value: r.needDate, required: true },
      ] }, (v, reason) => {
        const line = { partNumber: r.partNumber, quantity: v.quantity, promisedDate: v.promisedDate, requisitionId: r.requisitionId };
        if (v.po) return command(`/api/procurement/purchase-orders/${v.po}/lines`, line, reason);
        if (!v.supplier) throw new Error('Choose a supplier for the new order.');
        return command('/api/procurement/purchase-orders', { supplier: v.supplier, lines: [line] }, reason);
      }, reload);
  }

  async function alternatesFor(part) {
    const { data: c, error } = await tryGet(`/api/engineering/alternates/candidates/${encodeURIComponent(part)}`);
    const row = x => `<tr><td><b>${esc(x.offeredPart)}</b></td><td>${esc(String(x.factor))} per 1</td><td class="muted">${esc(x.reasons.join(' '))}</td></tr>`;
    drawer(`${part} — alternates`, error ? errorBlock(error, 'alternates') : `
      <h3 style="margin:0 0 6px">Approved (${c.approved.length})</h3>${c.approved.length ? `<table class="table"><tbody>${c.approved.map(row).join('')}</tbody></table>` : '<div class="muted">None.</div>'}
      <h3 style="margin:14px 0 6px" class="c-warn">Requires approval (${c.requiresApproval.length})</h3>${c.requiresApproval.length ? `<table class="table"><tbody>${c.requiresApproval.map(row).join('')}</tbody></table>` : '<div class="muted">None.</div>'}`);
  }

  function changeDate(po, line) {
    return formModal({ title: `New promised date — PO ${po} line ${line}`, detail: 'MRP, material readiness and the next schedule use the new date.', reasonRequired: true, confirmLabel: 'Record date',
      fields: [{ name: 'promisedDate', label: 'Supplier now promises', type: 'date', required: true }] },
    (v, reason) => command(`/api/procurement/purchase-orders/${po}/lines/${line}/date`, v, reason), reload);
  }

  function tabs() {
    const openReqs = (data.requisitions?.data ?? []).length;
    return `<div class="seg" style="margin-bottom:12px">${[['orders', 'Purchase orders'], ['rfqs', 'RFQs / sourcing'], ['requisitions', `Requisitions${openReqs ? ` (${openReqs})` : ''}`], ['suppliers', `Suppliers${data.suppliers.data ? ` (${data.suppliers.data.length})` : ''}`]]
      .map(([k, l]) => `<button data-tab="${k}" class="${tab === k ? 'on' : ''}">${esc(l)}</button>`).join('')}</div>`;
  }

  function bindTabs() {
    $$('[data-tab]', page).forEach(b => b.addEventListener('click', () => { tab = b.dataset.tab; render(); }));
  }

  // Supplier master (V2.1): new orders name a supplier record; on hold or inactive suppliers take no new orders.
  function renderSuppliers() {
    const list = data.suppliers.data ?? [];
    const sources = data.sources.data ?? [];
    const items = new Map((data.items.data ?? []).map(i => [i.partNumber, i]));
    page.innerHTML = `
      <div class="page-head">
        <div><h1>Procurement</h1><div class="sub">The supplier master. New purchase orders name a supplier from this list; an item with approved sources is bought only from them.</div></div>
        <div class="spacer"></div>
        ${approver ? '<button class="btn" id="new-source">Approve a source</button>' : ''}
        ${buyer ? '<button class="btn primary" id="new-supplier">New supplier</button>' : ''}
      </div>
      ${tabs()}
      ${data.suppliers.error ? errorBlock(data.suppliers.error, 'suppliers') : !list.length ? emptyBlock('No suppliers yet', buyer ? 'Add the suppliers you buy from; purchase orders choose from this list.' : 'A buyer adds suppliers.') : `
      <section class="panel"><div class="table-wrap"><table class="table">
        <thead><tr><th>Code</th><th>Name</th><th>Status</th><th>Approved source for</th><th>Notes</th><th></th></tr></thead><tbody>
        ${list.map(x => {
          const mine = sources.filter(s => s.supplierCode === x.code);
          return `<tr><td><b>${esc(x.code)}</b></td><td>${esc(x.name)}</td><td><span class="pill ${SUPPLIER_TONE[x.status]}">${esc(SUPPLIER_LABEL[x.status])}</span></td>
            <td>${mine.length ? mine.map(s => `<span class="chip" title="${esc(items.get(s.partNumber)?.description ?? '')}">${esc(s.partNumber)}${approver ? ` <a href="#" data-unsource="${esc(s.partNumber)}/${esc(x.code)}" title="Remove">×</a>` : ''}</span>`).join(' ') : '<span class="faint">—</span>'}</td>
            <td class="faint">${esc(x.notes ?? '')}</td>
            <td class="nowrap">${buyer ? `<button class="btn small" data-edit-supplier="${esc(x.code)}">Edit</button>` : ''}
              ${approver ? `<button class="btn small ghost" data-status="${esc(x.code)}">Status</button>` : ''}
              <button class="btn small ghost" data-docs-supplier="${esc(x.code)}">Documents</button></td></tr>`;
        }).join('')}
      </tbody></table></div></section>`}`;
    enhanceTables(page);
    bindTabs();
    $('#new-supplier', page)?.addEventListener('click', () => formModal({ title: 'New supplier', confirmLabel: 'Add supplier',
      fields: [
        { name: 'code', label: 'Supplier code', code: true, required: true, placeholder: 'ALCOA' },
        { name: 'name', label: 'Name', required: true },
        { name: 'notes', label: 'Notes', type: 'textarea' },
      ] }, (v, reason) => command('/api/procurement/suppliers', v, reason), reload));
    $$('[data-docs-supplier]', page).forEach(b => b.addEventListener('click', () => attachmentsDrawer(`Supplier ${b.dataset.docsSupplier}`, { type: 'Supplier', id: b.dataset.docsSupplier, me })));
    $$('[data-edit-supplier]', page).forEach(b => b.addEventListener('click', () => {
      const x = list.find(y => y.code === b.dataset.editSupplier);
      formModal({ title: `Edit ${x.code}`, confirmLabel: 'Save',
        fields: [{ name: 'name', label: 'Name', required: true, value: x.name }, { name: 'notes', label: 'Notes', type: 'textarea', value: x.notes ?? '' }] },
      (v, reason) => command(`/api/procurement/suppliers/${encodeURIComponent(x.code)}`, v, reason), reload);
    }));
    $$('[data-status]', page).forEach(b => b.addEventListener('click', () => {
      const x = list.find(y => y.code === b.dataset.status);
      formModal({ title: `Status of ${x.name}`, detail: 'A supplier on hold or inactive takes no new purchase orders; open orders stay open.', reasonRequired: true, confirmLabel: 'Set status',
        fields: [{ name: 'status', label: 'Status', type: 'select', value: x.status, options: Object.entries(SUPPLIER_LABEL) }] },
      (v, reason) => command(`/api/procurement/suppliers/${encodeURIComponent(x.code)}/status`, v, reason), reload);
    }));
    $('#new-source', page)?.addEventListener('click', () => formModal({ title: 'Approve a source', detail: 'Once an item has any approved source, purchase orders for it must go to one of them.', reasonRequired: true, confirmLabel: 'Approve source',
      fields: [
        { name: 'part', label: 'Part', type: 'select', options: buyParts(), required: true },
        { name: 'supplierCode', label: 'Supplier', type: 'select', options: supplierOptions(), required: true },
        { name: 'note', label: 'Note' },
      ] }, (v, reason) => command(`/api/procurement/items/${encodeURIComponent(v.part)}/approved-sources`, { supplierCode: v.supplierCode, note: v.note }, reason), reload));
    $$('[data-unsource]', page).forEach(a => a.addEventListener('click', async e => {
      e.preventDefault();
      const [part, code] = a.dataset.unsource.split('/');
      const done = await confirmWithReason({ title: `Remove ${code} as a source of ${part}`, confirmLabel: 'Remove' }, reason => command(`/api/procurement/items/${encodeURIComponent(part)}/approved-sources/${encodeURIComponent(code)}/remove`, {}, reason));
      if (done) { toast(done.message); await reload(); }
    }));
  }

  async function act(po, verb, label, detail) {
    const done = await confirmWithReason({ title: `${label} PO ${po}`, detail, confirmLabel: label }, reason => command(`/api/procurement/purchase-orders/${po}/${verb}`, {}, reason));
    if (done) { toast(done.message); await reload(); }
  }

  await load();
  render();
}
