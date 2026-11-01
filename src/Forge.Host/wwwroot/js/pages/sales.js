import { tryGet, command, download } from '../api.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast, tabifyPanels, enhanceTables } from '../ui.js';
import { formModal, canAction, qty, today, plusDays } from '../forms.js';
import { who } from '../people.js';

// Quote to cash (owner decisions 2026-10-31): Sales Representatives keep customers and draft quotes and sales orders, a Sales Manager
// who did not write them approves them, and a Billing Clerk invoices confirmed shipments only. Each customer carries a sales-tax
// rate or a tax-exemption certificate. Forge keeps no general ledger; the accounting system imports Forge's CSV export.

export const money = (amount, currency) => amount == null ? '—' : `${Number(amount).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${currency}`;

const STATUS_PILL = { Draft: 'none', Approved: 'ok', Accepted: 'ok', Issued: 'ok', Active: 'ok', Closed: 'none', Declined: 'warn', OnHold: 'warn', Cancelled: 'bad', Voided: 'bad', Inactive: 'none' };
const pill = status => `<span class="pill ${STATUS_PILL[status] ?? 'none'}">${esc(status === 'OnHold' ? 'On hold' : status)}</span>`;

export const taxText = c => c.taxExemptCertificate ? `Tax exempt (${c.taxExemptCertificate})` : `${Number(c.taxRatePercent)} % sales tax`;

/** One sales order as a card: lines with shipped and invoiced quantities, and the actions the reader may take. */
export function salesOrderCard(o, can = {}) {
  const open = o.lines.filter(l => l.status === 'Open');
  const actions = [
    can.manage && o.status === 'Draft' ? `<button class="btn small" data-order-line="${o.orderNumber}">+ Line</button>` : '',
    can.approve && o.status === 'Draft' ? `<button class="btn small primary" data-order-approve="${o.orderNumber}">Approve</button>` : '',
    can.bill && o.uninvoicedShipments.length && (o.status === 'Approved' || o.status === 'Closed') ? `<button class="btn small primary" data-order-invoice="${o.orderNumber}">Invoice ${o.uninvoicedShipments.length} shipment(s)</button>` : '',
    can.manage && o.status === 'Approved' ? `<button class="btn small" data-order-close="${o.orderNumber}">Close</button>` : '',
    can.manage && (o.status === 'Draft' || o.status === 'Approved') ? `<button class="btn small danger" data-order-cancel="${o.orderNumber}">Cancel</button>` : '',
  ].join(' ');
  return `<article class="card sales-order" data-order="${o.orderNumber}">
    <header><b>SO ${o.orderNumber}</b> ${pill(o.status)} <span>${esc(o.customerName)}</span>${o.customerPo ? ` <span class="faint">PO ${esc(o.customerPo)}</span>` : ''}${o.quoteNumber ? ` <span class="faint">from quote ${o.quoteNumber}</span>` : ''}
      <span class="spacer"></span><b>${esc(money(o.total, o.currency))}</b>${o.invoiced ? ` <span class="faint">invoiced ${esc(money(o.invoiced, o.currency))}</span>` : ''}</header>
    <table class="table"><thead><tr><th>#</th><th>Part</th><th class="num">Qty</th><th class="num">Unit price</th><th class="num">Amount</th><th>Due</th><th class="num">Shipped</th><th class="num">Invoiced</th><th></th></tr></thead><tbody>
    ${o.lines.map(l => `<tr class="${l.status === 'Cancelled' ? 'faint' : ''}"><td>${l.lineNumber}</td><td>${esc(l.partNumber)}</td><td class="num">${qty(l.quantity)} ${esc(l.unitOfMeasure)}</td>
      <td class="num">${esc(money(l.unitPrice, o.currency))}</td><td class="num">${esc(money(l.amount, o.currency))}</td><td class="nowrap">${esc(fmtDate(l.dueDate))}</td>
      <td class="num">${qty(l.shipped)}</td><td class="num">${qty(l.invoiced)}</td>
      <td class="nowrap">${l.status === 'Cancelled' ? pill('Cancelled') : can.manage && (o.status === 'Draft' || o.status === 'Approved')
        ? `<button class="btn small" data-line-date="${o.orderNumber}:${l.lineNumber}" data-due="${esc(l.dueDate)}">Date</button>${open.length > 1 ? ` <button class="btn small" data-line-cancel="${o.orderNumber}:${l.lineNumber}">Cancel</button>` : ''}` : ''}</td></tr>`).join('')}
    </tbody></table>
    <div class="faint">Drafted by ${esc(who(o.createdBy))} ${esc(fmtDateTime(o.createdAt))}${o.approvedBy ? ` · approved by ${esc(who(o.approvedBy))} ${esc(fmtDateTime(o.approvedAt))}` : ''}${o.shipTo ? ` · ship to ${esc(o.shipTo)}` : ''}
      ${o.status === 'Approved' ? ' · open lines are customer-order demand in Material Plan; ship them from Shipping against that demand' : ''}</div>
    ${actions.trim() ? `<div class="actions">${actions}</div>` : ''}
  </article>`;
}

/** A promise-date answer in one line. */
export const promiseText = p => `${p.quantity} ${p.partNumber}: earliest ${fmtDate(p.earliest)}. ${p.basis} Free now: ${p.freeNow}.`;

/** The accounting export's summary: what the period contains, whether every entry balances, and what was left out. */
export function exportSummaryHtml(s) {
  const ok = (flag, yes, no) => flag == null ? '<span class="pill none">Not included</span>' : flag ? `<span class="pill ok">${esc(yes)}</span>` : `<span class="pill bad">${esc(no)}</span>`;
  return `<p><b>${s.invoices}</b> invoice(s) ${esc(fmtDate(s.from))} – ${esc(fmtDate(s.to))}: ${esc(money(s.invoicedSubtotal, ''))} before tax, tax ${esc(money(s.invoicedTax, ''))}, total ${esc(money(s.invoicedTotal, ''))}${s.voidedInPeriod ? ` · ${s.voidedInPeriod} void(s) reversed` : ''}</p>
    <p>Sales journal ${ok(s.salesJournalBalances, 'balances', 'does not balance')} · Inventory journal ${ok(s.inventoryJournalBalances, 'balances', 'does not balance')}</p>
    ${s.inventoryKinds.length ? `<table class="table"><thead><tr><th>Movement</th><th class="num">Movements</th><th class="num">Valued</th><th class="num">Value</th><th>Treatment</th></tr></thead><tbody>
      ${s.inventoryKinds.map(k => `<tr><td>${esc(k.kind)}</td><td class="num">${k.movements}</td><td class="num">${k.valued}</td><td class="num">${esc(money(k.value, ''))}</td><td>${esc(k.treatment)}</td></tr>`).join('')}</tbody></table>` : ''}
    ${s.partsWithoutStandardCost.length ? `<div class="notice warn">No standard cost for ${s.partsWithoutStandardCost.map(esc).join(', ')}; their movements are left out until costed.</div>` : ''}
    <details><summary>Account codes</summary><div class="faint">${Object.entries(s.accounts).map(([k, v]) => `${esc(k)} ${esc(v)}`).join(' · ')}</div></details>
    ${s.notes.map(n => `<div class="faint">${esc(n)}</div>`).join('')}`;
}

export async function salesPage(page, ctx) {
  const me = ctx?.me;
  const can = { manage: canAction(me, 'sales.manage'), approve: canAction(me, 'sales.approve'), bill: canAction(me, 'sales.billing.manage'), export: canAction(me, 'accounting.export') };
  const load = async () => {
    const [customers, quotes, orders, invoices, items] = await Promise.all([
      tryGet('/api/sales/customers'), tryGet('/api/sales/quotes'), tryGet('/api/sales/orders'), tryGet('/api/sales/invoices'), tryGet('/api/engineering/items')]);
    if (orders.error) { page.innerHTML = errorBlock(orders.error, 'sales orders'); return; }
    const customerList = customers.data ?? [];
    const activeCustomers = customerList.filter(c => c.status === 'Active').map(c => [c.code, `${c.name} (${c.code})`]);
    const parts = (items.data ?? []).map(i => i.partNumber);
    const orderList = orders.data ?? [];
    const quoteList = quotes.data ?? [];
    const invoiceList = invoices.data ?? [];
    const awaitingInvoice = orderList.reduce((n, o) => n + (o.uninvoicedShipments?.length ?? 0), 0);
    page.innerHTML = `
      <div class="page-head"><div><h1>Sales</h1><div class="sub">Quotes, sales orders and invoices. A different person approves what a representative writes; invoices bill confirmed shipments only.</div></div><div class="spacer"></div>
        <button class="btn" data-promise>Promise date</button> ${can.manage ? '<button class="btn" data-new-customer>+ Customer</button> <button class="btn" data-new-quote>+ Quote</button> <button class="btn primary" data-new-order>+ Sales order</button>' : ''}</div>
      <section class="panel"><header><h2>Sales orders (${orderList.filter(o => o.status === 'Draft' || o.status === 'Approved').length} open)</h2></header>
        ${orderList.length ? orderList.map(o => salesOrderCard(o, can)).join('') : emptyBlock('No sales orders yet', 'Draft one, or turn an approved quote into one.')}</section>
      <section class="panel"><header><h2>Quotes (${quoteList.filter(q => q.quote.status === 'Draft' || q.quote.status === 'Approved').length} open)</h2></header>
        ${quoteList.length ? `<table class="table"><thead><tr><th>Quote</th><th>Customer</th><th>Status</th><th>Valid until</th><th class="num">Lines</th><th class="num">Total</th><th></th></tr></thead><tbody>
        ${quoteList.map(({ quote: q, total }) => `<tr><td>${q.quoteNumber}</td><td>${esc(q.customerCode)}</td><td>${pill(q.status)}${q.salesOrderNumber ? ` <span class="faint">SO ${q.salesOrderNumber}</span>` : ''}</td><td class="nowrap">${esc(fmtDate(q.validUntil))}</td>
          <td class="num">${q.lines.length}</td><td class="num">${esc(money(total, q.currency))}</td><td class="nowrap">
          ${can.approve && q.status === 'Draft' ? `<button class="btn small primary" data-quote-approve="${q.quoteNumber}">Approve</button>` : ''}
          ${can.manage && q.status === 'Approved' ? `<button class="btn small primary" data-quote-order="${q.quoteNumber}">Make sales order</button>` : ''}
          ${can.manage && (q.status === 'Draft' || q.status === 'Approved') ? `<button class="btn small" data-quote-decline="${q.quoteNumber}">Declined</button>` : ''}
          ${can.manage && q.status === 'Draft' ? `<button class="btn small danger" data-quote-cancel="${q.quoteNumber}">Cancel</button>` : ''}</td></tr>`).join('')}</tbody></table>`
        : emptyBlock('No quotes yet', 'A quote names the customer, parts, prices and lead times, and how long it is valid.')}</section>
      <section class="panel"><header><h2>Customers (${customerList.length})</h2></header>
        ${customers.error ? errorBlock(customers.error, 'customers') : customerList.length ? `<table class="table"><thead><tr><th>Code</th><th>Name</th><th>Status</th><th>Terms</th><th>Tax</th><th>Currency</th><th></th></tr></thead><tbody>
        ${customerList.map(c => `<tr><td class="mono">${esc(c.code)}</td><td>${esc(c.name)}${c.billTo ? `<div class="faint">${esc(c.billTo)}</div>` : ''}</td><td>${pill(c.status)}</td><td>Net ${c.paymentTermsDays}</td><td>${esc(taxText(c))}</td><td>${esc(c.currency)}</td>
          <td class="nowrap">${can.manage ? `<button class="btn small" data-customer-edit="${esc(c.code)}">Edit</button>` : ''} ${can.approve ? `<button class="btn small" data-customer-status="${esc(c.code)}">Status</button>` : ''}</td></tr>`).join('')}</tbody></table>`
        : emptyBlock('No customers yet', 'Add a customer with its payment terms and sales-tax rate or exemption certificate.')}</section>
      <section class="panel"><header><h2>Invoices (${invoiceList.filter(i => i.status === 'Issued').length} issued${awaitingInvoice ? `, ${awaitingInvoice} shipment(s) to invoice` : ''})</h2></header>
        ${invoices.error ? errorBlock(invoices.error, 'invoices') : invoiceList.length ? `<table class="table"><thead><tr><th>Invoice</th><th>Date</th><th>Customer</th><th>SO</th><th>Status</th><th class="num">Subtotal</th><th class="num">Tax</th><th class="num">Total</th><th>Due</th><th></th></tr></thead><tbody>
        ${invoiceList.map(i => `<tr><td>${i.invoiceNumber}</td><td class="nowrap">${esc(fmtDate(i.invoiceDate))}</td><td>${esc(i.customerName)}${i.customerPo ? ` <span class="faint">PO ${esc(i.customerPo)}</span>` : ''}</td><td>${i.orderNumber}</td>
          <td>${pill(i.status)}${i.voidReason ? `<div class="faint">${esc(i.voidReason)}</div>` : ''}</td><td class="num">${esc(money(i.subtotal, i.currency))}</td><td class="num">${i.taxExemptCertificate ? `<span class="faint">exempt</span>` : esc(money(i.tax, i.currency))}</td>
          <td class="num"><b>${esc(money(i.total, i.currency))}</b></td><td class="nowrap">${esc(fmtDate(i.dueDate))}</td><td>${can.bill && i.status === 'Issued' ? `<button class="btn small danger" data-invoice-void="${i.invoiceNumber}">Void</button>` : ''}</td></tr>`).join('')}</tbody></table>`
        : emptyBlock('No invoices yet', 'A billing clerk invoices an approved order\'s confirmed shipments.')}</section>
      ${can.export ? `<section class="panel"><header><h2>Accounting export</h2></header>
        <form id="acct-form" class="inline"><label>From <input class="input" type="date" id="acct-from" value="${esc(plusDays(-30))}"></label> <label>To <input class="input" type="date" id="acct-to" value="${esc(today())}"></label> <button class="btn">Preview</button></form>
        <div id="acct-summary" class="faint">CSV files for the accounting system: invoices, invoice lines, the sales journal and, with cost access, the inventory journal at standard cost.</div>
        <div class="actions">${['invoices', 'invoice-lines', 'sales-journal', 'inventory-journal'].map(f => `<button class="btn small" data-export="${f}">${esc(f.replace('-', ' '))}.csv</button>`).join(' ')}</div></section>` : ''}`;
    tabifyPanels(page, [
      { label: 'Sales orders', match: ['sales orders'] },
      { label: 'Quotes', match: ['quotes'] },
      { label: 'Customers', match: ['customers'] },
      { label: 'Invoices', match: ['invoices'] },
      { label: 'Accounting export', match: ['accounting export'] },
    ]);
    enhanceTables(page);
    wire({ customerList, activeCustomers, parts, orderList });
  };

  const ask = (title, detail, path, reasonRequired = true) => confirmWithReason({ title, detail, reasonRequired }, reason => command(path, {}, reason)).then(r => { if (r) { toast(r.message ?? 'Done'); load(); } });
  const customerFields = (c = {}) => [
    ...(c.code ? [] : [{ name: 'code', label: 'Customer code', code: true, required: true, placeholder: 'ACME' }]),
    { name: 'name', label: 'Name', required: true, value: c.name },
    { name: 'billTo', label: 'Bill to', type: 'textarea', value: c.billTo },
    { name: 'shipTo', label: 'Ship to', type: 'textarea', value: c.shipTo },
    { name: 'paymentTermsDays', label: 'Payment terms (net days)', type: 'number', min: 0, max: 365, required: true, value: c.paymentTermsDays ?? 30 },
    { name: 'taxRatePercent', label: 'Sales-tax rate (%)', type: 'number', min: 0, max: 25, step: 'any', required: true, value: c.taxRatePercent ?? 0 },
    { name: 'taxExemptCertificate', label: 'Tax-exemption certificate (rate must be 0)', value: c.taxExemptCertificate },
    ...(c.code ? [] : [{ name: 'currency', label: 'Currency', type: 'select', value: 'USD', options: [['USD', 'USD'], ['EUR', 'EUR'], ['GBP', 'GBP'], ['CAD', 'CAD']] }]),
    { name: 'notes', label: 'Notes', type: 'textarea', value: c.notes },
  ];

  function wire({ customerList, activeCustomers, parts, orderList }) {
    $('[data-promise]', page)?.addEventListener('click', () => formModal({
      title: 'Earliest promise date', detail: 'Free stock (available less reservations), then open purchase and production orders, then the part\'s lead time. Advice for the quote, not a commitment.', confirmLabel: 'Check',
      fields: [{ name: 'part', label: 'Part', code: true, required: true, list: parts }, { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', required: true }],
    }, async v => { const r = await tryGet(`/api/sales/promise?part=${encodeURIComponent(v.part)}&quantity=${encodeURIComponent(v.quantity)}`); if (r.error) throw r.error; return { message: promiseText(r.data) }; }));
    const customerField = { name: 'customer', label: 'Customer', type: 'select', required: true, options: activeCustomers };
    $('[data-new-customer]', page)?.addEventListener('click', () => formModal({ title: 'Add a customer', detail: 'Invoices take the customer\'s terms and sales-tax rate, or no tax with an exemption certificate.', confirmLabel: 'Add customer', fields: customerFields() },
      (v, reason) => command('/api/sales/customers', v, reason), load));
    $('[data-new-quote]', page)?.addEventListener('click', () => formModal({
      title: 'Draft a quote', detail: 'A Sales Manager who did not write it approves it before it goes to the customer.', confirmLabel: 'Draft quote',
      fields: [customerField, { name: 'validUntil', label: 'Valid until', type: 'date', required: true, value: plusDays(30) },
        { name: 'lines', label: 'Lines', type: 'repeater', required: true, minRows: 1, rowLabel: 'line', columns: [
          { name: 'partNumber', label: 'Part', code: true, required: true }, { name: 'quantity', label: 'Qty', type: 'number', min: 0, step: 'any', required: true },
          { name: 'unitPrice', label: 'Unit price', type: 'number', min: 0, step: 'any', required: true }, { name: 'leadTimeDays', label: 'Lead time (days)', type: 'number', min: 0, max: 730, required: true }] },
        { name: 'notes', label: 'Notes', type: 'textarea' }],
    }, (v, reason) => command('/api/sales/quotes', { ...v, lines: v.lines.map(l => ({ ...l, partNumber: String(l.partNumber).toUpperCase() })) }, reason), load));
    $('[data-new-order]', page)?.addEventListener('click', () => formModal({
      title: 'Draft a sales order', detail: 'Once a Sales Manager who did not write it approves it, its lines become customer-order demand.', confirmLabel: 'Draft order',
      fields: [customerField, { name: 'customerPo', label: 'Customer PO' }, { name: 'shipTo', label: 'Ship to (blank: the customer\'s)', type: 'textarea' },
        { name: 'lines', label: 'Lines', type: 'repeater', required: true, minRows: 1, rowLabel: 'line', columns: [
          { name: 'partNumber', label: 'Part', code: true, required: true }, { name: 'quantity', label: 'Qty', type: 'number', min: 0, step: 'any', required: true },
          { name: 'unitPrice', label: 'Unit price', type: 'number', min: 0, step: 'any', required: true }, { name: 'dueDate', label: 'Due', type: 'date', required: true }] }],
    }, (v, reason) => command('/api/sales/orders', { ...v, lines: v.lines.map(l => ({ ...l, partNumber: String(l.partNumber).toUpperCase() })) }, reason), load));

    for (const b of $$('[data-customer-edit]', page)) b.addEventListener('click', () => {
      const c = customerList.find(x => x.code === b.dataset.customerEdit);
      formModal({ title: `Edit ${c.name}`, detail: 'Changes apply to invoices issued from now on.', confirmLabel: 'Save', reasonRequired: true, fields: customerFields(c) },
        (v, reason) => command(`/api/sales/customers/${encodeURIComponent(c.code)}`, v, reason), load);
    });
    for (const b of $$('[data-customer-status]', page)) b.addEventListener('click', () => {
      const c = customerList.find(x => x.code === b.dataset.customerStatus);
      formModal({ title: `${c.name}: status`, detail: 'A customer on hold or inactive takes no new quotes, orders or approvals. Shipped work can still be invoiced.', confirmLabel: 'Set status', reasonRequired: true,
        fields: [{ name: 'status', label: 'Status', type: 'select', value: c.status, options: [['Active', 'Active'], ['OnHold', 'On hold'], ['Inactive', 'Inactive']] }] },
      (v, reason) => command(`/api/sales/customers/${encodeURIComponent(c.code)}/status`, v, reason), load);
    });

    for (const b of $$('[data-quote-approve]', page)) b.addEventListener('click', () => ask(`Approve quote ${b.dataset.quoteApprove}`, 'You cannot approve a quote you wrote.', `/api/sales/quotes/${b.dataset.quoteApprove}/approve`, false));
    for (const b of $$('[data-quote-decline]', page)) b.addEventListener('click', () => ask(`Quote ${b.dataset.quoteDecline} declined`, 'Record that the customer declined, or that the quote lapsed.', `/api/sales/quotes/${b.dataset.quoteDecline}/decline`));
    for (const b of $$('[data-quote-cancel]', page)) b.addEventListener('click', () => ask(`Cancel quote ${b.dataset.quoteCancel}`, 'Withdraws the draft.', `/api/sales/quotes/${b.dataset.quoteCancel}/cancel`));
    for (const b of $$('[data-quote-order]', page)) b.addEventListener('click', () => formModal({
      title: `Sales order from quote ${b.dataset.quoteOrder}`, detail: 'Copies the quote\'s prices; each line is due its quoted lead time from today. A Sales Manager approves the order.', confirmLabel: 'Make sales order',
      fields: [{ name: 'customerPo', label: 'Customer PO' }, { name: 'shipTo', label: 'Ship to (blank: the customer\'s)', type: 'textarea' }],
    }, (v, reason) => command(`/api/sales/quotes/${b.dataset.quoteOrder}/order`, v, reason), load));

    for (const b of $$('[data-order-approve]', page)) b.addEventListener('click', () => ask(`Approve sales order ${b.dataset.orderApprove}`, 'Its open lines become customer-order demand. You cannot approve an order you wrote.', `/api/sales/orders/${b.dataset.orderApprove}/approve`, false));
    for (const b of $$('[data-order-close]', page)) b.addEventListener('click', () => ask(`Close sales order ${b.dataset.orderClose}`, 'Nothing more will ship; its demand closes. Shipped work can still be invoiced.', `/api/sales/orders/${b.dataset.orderClose}/close`));
    for (const b of $$('[data-order-cancel]', page)) b.addEventListener('click', () => ask(`Cancel sales order ${b.dataset.orderCancel}`, 'Only an order with nothing shipped can be cancelled; its demand is cancelled.', `/api/sales/orders/${b.dataset.orderCancel}/cancel`));
    for (const b of $$('[data-order-line]', page)) b.addEventListener('click', () => formModal({
      title: `Add a line to SO ${b.dataset.orderLine}`, confirmLabel: 'Add line',
      fields: [{ name: 'partNumber', label: 'Part', code: true, required: true, list: parts }, { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', required: true },
        { name: 'unitPrice', label: 'Unit price', type: 'number', min: 0, step: 'any', required: true }, { name: 'dueDate', label: 'Due', type: 'date', required: true, value: plusDays(14) }],
    }, (v, reason) => command(`/api/sales/orders/${b.dataset.orderLine}/lines`, v, reason), load));
    for (const b of $$('[data-line-date]', page)) b.addEventListener('click', () => {
      const [order, line] = b.dataset.lineDate.split(':');
      formModal({ title: `SO ${order} line ${line}: due date`, detail: 'Planning moves the line\'s demand to the new date.', confirmLabel: 'Change date', reasonRequired: true,
        fields: [{ name: 'dueDate', label: 'Due', type: 'date', required: true, value: b.dataset.due }] }, (v, reason) => command(`/api/sales/orders/${order}/lines/${line}/date`, v, reason), load);
    });
    for (const b of $$('[data-line-cancel]', page)) b.addEventListener('click', () => {
      const [order, line] = b.dataset.lineCancel.split(':');
      ask(`Cancel SO ${order} line ${line}`, 'Only a line with nothing shipped can be cancelled; its demand is cancelled.', `/api/sales/orders/${order}/lines/${line}/cancel`);
    });
    for (const b of $$('[data-order-invoice]', page)) b.addEventListener('click', () => {
      const o = orderList.find(x => String(x.orderNumber) === b.dataset.orderInvoice);
      formModal({
        title: `Invoice SO ${o.orderNumber} (${o.customerName})`, detail: 'Each shipment is billed whole, at its order line\'s price. Tax follows the customer\'s rate or exemption.', confirmLabel: 'Issue invoice',
        fields: o.uninvoicedShipments.map(s => ({ name: `s${s.shipmentId}`, label: `Shipment ${s.shipmentId}`, type: 'check', value: true,
          text: `line ${s.orderLine}: ${qty(s.quantity)} ${s.partNumber} shipped ${fmtDate(s.shippedAt)}${s.tracking ? ` (${s.tracking})` : ''}` })),
      }, (v, reason) => command('/api/sales/invoices', { orderNumber: o.orderNumber, shipmentIds: o.uninvoicedShipments.filter(s => v[`s${s.shipmentId}`]).map(s => s.shipmentId) }, reason), load);
    });
    for (const b of $$('[data-invoice-void]', page)) b.addEventListener('click', () => ask(`Void invoice ${b.dataset.invoiceVoid}`, 'The invoice keeps its number; its shipments can be invoiced again. The export reverses it on today\'s date.', `/api/sales/invoices/${b.dataset.invoiceVoid}/void`));

    const period = () => `from=${encodeURIComponent($('#acct-from', page).value)}&to=${encodeURIComponent($('#acct-to', page).value)}`;
    $('#acct-form', page)?.addEventListener('submit', async event => {
      event.preventDefault();
      const host = $('#acct-summary', page);
      const result = await tryGet(`/api/accounting/export?${period()}`);
      host.className = '';
      host.innerHTML = result.error ? errorBlock(result.error, 'accounting export') : exportSummaryHtml(result.data);
    });
    for (const b of $$('[data-export]', page)) b.addEventListener('click', () => download(`/api/accounting/export/${b.dataset.export}?${period()}`, `forge-${b.dataset.export}.csv`).catch(e => toast(e.message, true)));
  }

  await load();
}
