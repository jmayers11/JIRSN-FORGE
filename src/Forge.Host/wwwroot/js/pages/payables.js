import { tryGet, command } from '../api.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast, tabifyPanels } from '../ui.js';
import { formModal, canAction } from '../forms.js';
import { who } from '../people.js';
import { featureOn } from './features.js';

// Paying suppliers (owner decisions 2026-11-01): accounts payable enters each supplier invoice; Forge matches it to the purchase order
// price and what was received. Within the plant's tolerance it is approved for payment; otherwise a purchasing manager decides.

const STATUS = { Approved: 'ok', Exception: 'warn', Rejected: 'bad', Voided: 'none', Paid: 'ok' };
const money = (v, c = 'USD') => `${Number(v ?? 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${esc(c)}`;

export function supplierInvoicesHtml(invoices, { canEnter = false, canDecide = false, me = null } = {}) {
  if (!invoices?.length) return emptyBlock('No supplier invoices', 'Accounts payable enters each supplier invoice against its purchase order.');
  return `<table class="table"><thead><tr><th>Supplier</th><th>Invoice</th><th>PO</th><th>Dates</th><th>Total</th><th>Match</th><th>Status</th><th></th></tr></thead><tbody>
    ${invoices.map(i => `<tr><td>${esc(i.supplierName)}</td><td class="mono">${esc(i.supplierInvoiceNumber)}</td><td class="mono">${i.poNumber}</td>
      <td>${esc(fmtDate(i.invoiceDate))}<div class="faint">due ${esc(fmtDate(i.dueDate))}</div></td>
      <td>${money(i.total, i.currency)}${i.freight || i.tax ? `<div class="faint">freight ${money(i.freight)} · tax ${money(i.tax)}</div>` : ''}</td>
      <td class="muted">${esc(i.matchSummary)}${i.variance ? `<div class="faint">variance ${money(i.variance, i.currency)}</div>` : ''}</td>
      <td><span class="pill ${STATUS[i.status] ?? 'none'}">${esc(i.status)}</span>${i.decidedBy ? `<div class="faint">${esc(who(i.decidedBy))} ${esc(fmtDateTime(i.decidedAt))}</div>` : ''}${i.decisionNote ? `<div class="faint">${esc(i.decisionNote)}</div>` : ''}${i.paidOn ? `<div class="faint">paid ${esc(fmtDate(i.paidOn))} ${esc(i.paymentReference ?? '')}</div>` : ''}</td>
      <td>${canDecide && i.status === 'Exception' && i.enteredBy !== me ? `<button class="btn small primary" data-decide="${i.invoiceId}" data-approve="true">Approve</button> <button class="btn small" data-decide="${i.invoiceId}" data-approve="false">Reject</button>` : ''}
        ${canEnter && i.status === 'Approved' ? `<button class="btn small" data-paid="${i.invoiceId}">Mark paid</button>` : ''}
        ${canEnter && ['Approved', 'Exception', 'Rejected'].includes(i.status) ? ` <button class="btn small ghost" data-void="${i.invoiceId}">Void</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

export async function payablesPage(page, ctx) {
  const me = ctx?.me;
  const can = { enter: canAction(me, 'procurement.invoices.enter'), decide: canAction(me, 'procurement.invoices.approve') };
  const load = async () => {
    if (!(await featureOn('supplier-invoices'))) {
      page.innerHTML = `<div class="page-head"><div><h1>Supplier Invoices</h1></div></div>${emptyBlock('Turned off', 'Supplier invoices are turned off for this plant (Settings → Features).')}`;
      return;
    }
    const [invoices, policy, pos] = await Promise.all([tryGet('/api/payables/invoices'), tryGet('/api/payables/policy'), tryGet('/api/procurement/purchase-orders')]);
    const p = policy.data ?? { tolerancePercent: 2, toleranceAmount: 50 };
    const list = invoices.data ?? [];
    page.innerHTML = `
      <div class="page-head"><div><h1>Supplier Invoices</h1><div class="sub">Three-way match: invoice against the purchase order price and what was received. Within ${esc(p.tolerancePercent)}% and ${money(p.toleranceAmount)} it is approved for payment; otherwise a purchasing manager decides.</div></div>
        <div class="spacer"></div>${can.enter ? '<button class="btn primary" data-enter>Enter invoice</button>' : ''} ${can.decide ? '<button class="btn" data-tolerance>Tolerance</button>' : ''}</div>
      ${invoices.error ? errorBlock(invoices.error, 'supplier invoices') : `
      <section class="panel"><header><h2>Waiting for a decision</h2></header>${supplierInvoicesHtml(list.filter(i => i.status === 'Exception'), { canEnter: can.enter, canDecide: can.decide, me: me ? `person:${me.personId}` : null })}</section>
      <section class="panel"><header><h2>All invoices</h2></header>${supplierInvoicesHtml(list, { canEnter: can.enter, canDecide: can.decide, me: me ? `person:${me.personId}` : null })}</section>`}`;
    tabifyPanels(page, [{ label: 'Waiting for a decision', match: ['waiting for a decision'] }, { label: 'All invoices', match: ['all invoices'] }]);
    const orders = (pos.data ?? []).filter(o => ['Approved', 'Closed'].includes(o.status));
    $('[data-enter]', page)?.addEventListener('click', () => formModal({
      title: 'Enter a supplier invoice', detail: 'One line per purchase order line billed: "line:quantity:unit price", separated by commas (for example 1:100:2.50, 2:40:7.10).', confirmLabel: 'Enter and match',
      fields: [{ name: 'poNumber', label: 'Purchase order', type: 'select', required: true, blank: '— choose —', options: orders.map(o => [String(o.poNumber), `${o.poNumber} — ${o.supplier}`]) },
        { name: 'supplierInvoiceNumber', label: 'Supplier invoice number', required: true }, { name: 'invoiceDate', label: 'Invoice date', type: 'date', required: true, value: new Date().toISOString().slice(0, 10) },
        { name: 'dueDate', label: 'Due date (empty: 30 days)', type: 'date' }, { name: 'lines', label: 'Lines', required: true },
        { name: 'freight', label: 'Freight', type: 'number', min: 0, step: 0.01, value: 0 }, { name: 'tax', label: 'Tax', type: 'number', min: 0, step: 0.01, value: 0 }],
    }, v => command('/api/payables/invoices', {
      poNumber: Number(v.poNumber), supplierInvoiceNumber: v.supplierInvoiceNumber, invoiceDate: v.invoiceDate, dueDate: v.dueDate || null, freight: Number(v.freight || 0), tax: Number(v.tax || 0),
      lines: String(v.lines).split(',').map(s => s.trim()).filter(Boolean).map(s => { const [poLine, quantity, unitPrice] = s.split(':').map(Number); return { poLine, quantity, unitPrice }; }),
    }), load));
    $('[data-tolerance]', page)?.addEventListener('click', () => formModal({
      title: 'Three-way match tolerance', detail: 'An invoice is approved automatically when its variance is within both limits.', confirmLabel: 'Save', reasonRequired: true,
      fields: [{ name: 'tolerancePercent', label: 'Percent', type: 'number', min: 0, max: 25, step: 0.1, value: p.tolerancePercent }, { name: 'toleranceAmount', label: 'Amount', type: 'number', min: 0, step: 0.01, value: p.toleranceAmount }],
    }, (v, reason) => command('/api/payables/policy', { tolerancePercent: Number(v.tolerancePercent), toleranceAmount: Number(v.toleranceAmount) }, reason), load));
    for (const b of $$('[data-decide]', page)) b.addEventListener('click', () => formModal({
      title: `${b.dataset.approve === 'true' ? 'Approve' : 'Reject'} supplier invoice`, confirmLabel: b.dataset.approve === 'true' ? 'Approve' : 'Reject',
      fields: [{ name: 'note', label: 'Why', type: 'textarea', required: true }],
    }, v => command(`/api/payables/invoices/${b.dataset.decide}/decide`, { approve: b.dataset.approve === 'true', note: v.note }, v.note), load));
    for (const b of $$('[data-paid]', page)) b.addEventListener('click', () => formModal({
      title: 'Mark paid', confirmLabel: 'Save', fields: [{ name: 'paidOn', label: 'Paid on', type: 'date', required: true, value: new Date().toISOString().slice(0, 10) }, { name: 'reference', label: 'Check or payment reference' }],
    }, v => command(`/api/payables/invoices/${b.dataset.paid}/paid`, { paidOn: v.paidOn, reference: v.reference || null }), load));
    for (const b of $$('[data-void]', page)) b.addEventListener('click', () => confirmWithReason({ title: 'Void supplier invoice', confirmLabel: 'Void' },
      reason => command(`/api/payables/invoices/${b.dataset.void}/void`, {}, reason)).then(r => { if (r) { toast(r.message ?? 'Voided'); load(); } }));
  };
  await load();
}
