import { tryGet, command } from '../api.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast, tabifyPanels } from '../ui.js';
import { formModal, canAction } from '../forms.js';
import { who } from '../people.js';
import { featureOn } from './features.js';

// Getting paid (owner decisions 2026-11-01): billing clerks record payments; customers over their limit or past due are on credit hold, and new
// orders and shipments wait until a sales manager or credit manager releases them with a reason.

const money = (v, currency = 'USD') => `${Number(v ?? 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${esc(currency)}`;

export function customersHtml(customers, { canManage = false } = {}) {
  if (!customers?.length) return emptyBlock('No customers', 'Customers are kept under Sales & Invoicing.');
  return `<table class="table"><thead><tr><th>Customer</th><th>Open invoices</th><th>Current</th><th>1–30</th><th>31–60</th><th>61–90</th><th>90+</th><th>On order</th><th>Credit limit</th><th>Credit</th><th></th></tr></thead><tbody>
    ${customers.map(c => `<tr><td><b>${esc(c.customerName)}</b> <span class="mono faint">${esc(c.customerCode)}</span></td><td>${money(c.openInvoices, c.currency)}${c.onAccount ? `<div class="faint">${money(c.onAccount, c.currency)} on account</div>` : ''}</td>
      <td>${money(c.aging.current)}</td><td>${money(c.aging.days1To30)}</td><td>${money(c.aging.days31To60)}</td><td>${money(c.aging.days61To90)}</td><td>${money(c.aging.over90)}</td>
      <td>${money(c.openOrders, c.currency)}</td><td>${c.creditLimit == null ? '<span class="faint">No limit</span>' : money(c.creditLimit, c.currency)}</td>
      <td>${c.onHold ? `<span class="pill bad">Credit hold</span><div class="faint">${(c.reasons ?? []).map(esc).join('; ')}</div>` : '<span class="pill ok">Good</span>'}</td>
      <td>${canManage ? `<button class="btn small" data-limit="${esc(c.customerCode)}" data-current="${c.creditLimit ?? ''}">Credit limit</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

export function waitingHtml(items, { canRelease = false } = {}) {
  if (!items?.length) return emptyBlock('Nothing waiting', 'No draft order or packed shipment is held for credit.');
  return `<table class="table"><thead><tr><th>Customer</th><th>Waiting</th><th>Why</th><th></th></tr></thead><tbody>
    ${items.map(i => `<tr><td>${esc(i.customerName)}</td><td>${i.kind === 'SalesOrder' ? `Sales order <b>${i.number}</b>${i.value != null ? ` (${money(i.value)})` : ''}` : `Shipment <b>${i.number}</b>`}</td>
      <td class="muted">${(i.reasons ?? []).map(esc).join('; ')}</td>
      <td>${canRelease ? `<button class="btn small primary" data-release="${esc(i.customerCode)}" data-kind="${esc(i.kind)}" data-number="${i.number}">Release</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

export function invoicesHtml(invoices) {
  if (!invoices?.length) return emptyBlock('No open invoices', 'Everything invoiced is paid.');
  return `<table class="table"><thead><tr><th>Invoice</th><th>Customer</th><th>Date</th><th>Due</th><th>Total</th><th>Paid</th><th>Balance</th><th></th></tr></thead><tbody>
    ${invoices.map(b => `<tr><td class="mono">${b.invoiceNumber}</td><td>${esc(b.customerCode)}</td><td>${esc(fmtDate(b.invoiceDate))}</td><td>${esc(fmtDate(b.dueDate))}</td>
      <td>${money(b.total, b.currency)}</td><td>${money(b.paid, b.currency)}</td><td><b>${money(b.balance, b.currency)}</b></td>
      <td>${b.daysPastDue > 0 ? `<span class="pill ${b.daysPastDue > 30 ? 'bad' : 'warn'}">${b.daysPastDue} days past due</span>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

export function paymentsHtml(payments, { canRecord = false } = {}) {
  if (!payments?.length) return emptyBlock('No payments yet', 'Billing clerks record customer payments here.');
  return `<table class="table"><thead><tr><th>#</th><th>Customer</th><th>Received</th><th>Amount</th><th>Method</th><th>Applied to</th><th>Recorded</th><th></th></tr></thead><tbody>
    ${payments.map(p => `<tr><td class="mono">${p.paymentId}</td><td>${esc(p.customerCode)}</td><td>${esc(fmtDate(p.receivedOn))}</td><td>${money(p.amount, p.currency)}</td>
      <td>${esc(p.method)}${p.reference ? ` <span class="faint">${esc(p.reference)}</span>` : ''}</td>
      <td>${(p.applications ?? []).map(a => `${a.invoiceNumber} (${money(a.amount)})`).join(', ') || '—'}${p.unapplied > 0 ? `<div class="faint">${money(p.unapplied)} on account</div>` : ''}</td>
      <td>${esc(who(p.recordedBy))} ${esc(fmtDateTime(p.recordedAt))}</td>
      <td>${p.status === 'Voided' ? `<span class="pill none">Voided</span>` : canRecord ? `<button class="btn small" data-void="${p.paymentId}">Void</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

export async function receivablesPage(page, ctx) {
  const me = ctx?.me;
  const can = { record: canAction(me, 'sales.payments.record'), manage: canAction(me, 'sales.credit.manage'), release: canAction(me, 'sales.credit.release') };
  const load = async () => {
    if (!(await featureOn('receivables'))) {
      page.innerHTML = `<div class="page-head"><div><h1>Payments &amp; Credit</h1></div></div>${emptyBlock('Turned off', 'Customer payments and credit holds are turned off for this plant (Settings → Features).')}`;
      return;
    }
    const view = await tryGet('/api/receivables/');
    const v = view.data ?? {};
    const policy = v.policy ?? {};
    page.innerHTML = `
      <div class="page-head"><div><h1>Payments &amp; Credit</h1><div class="sub">What customers owe, how late, and who is on credit hold.
        ${policy.pastDueDays != null ? `Customers more than ${policy.pastDueDays} days past due go on hold.` : 'Lateness alone does not put a customer on hold.'}
        ${policy.includeOpenOrders ? 'Open orders count toward credit limits.' : ''}</div></div>
        <div class="spacer"></div>${can.record ? '<button class="btn primary" data-payment>Record payment</button>' : ''} ${can.manage ? '<button class="btn" data-policy>Credit policy</button>' : ''}</div>
      ${view.error ? errorBlock(view.error, 'receivables') : `
      <section class="panel"><header><h2>Customers</h2></header>${customersHtml(v.customers, { canManage: can.manage })}</section>
      <section class="panel"><header><h2>Waiting on credit</h2></header>${waitingHtml(v.waiting, { canRelease: can.release })}</section>
      <section class="panel"><header><h2>Open invoices</h2></header>${invoicesHtml(v.openInvoices)}</section>
      <section class="panel"><header><h2>Payments</h2></header>${paymentsHtml(v.payments, { canRecord: can.record })}</section>`}`;
    tabifyPanels(page, [{ label: 'Customers', match: ['customers'] }, { label: 'Waiting on credit', match: ['waiting on credit'] }, { label: 'Open invoices', match: ['open invoices'] }, { label: 'Payments', match: ['payments'] }]);
    const customers = (v.customers ?? []).map(c => [c.customerCode, `${c.customerName} (${c.customerCode})`]);
    $('[data-payment]', page)?.addEventListener('click', () => formModal({
      title: 'Record a customer payment', detail: 'Applied to the oldest open invoices first, unless you name the invoices. Anything left stays on account.', confirmLabel: 'Record',
      fields: [{ name: 'customerCode', label: 'Customer', type: 'select', required: true, blank: '— choose —', options: customers },
        { name: 'amount', label: 'Amount', type: 'number', required: true, min: 0.01, step: 0.01 }, { name: 'receivedOn', label: 'Received on', type: 'date', required: true, value: new Date().toISOString().slice(0, 10) },
        { name: 'method', label: 'Method', type: 'select', options: [['Check', 'Check'], ['Ach', 'ACH'], ['Wire', 'Wire'], ['Card', 'Card'], ['Other', 'Other']], value: 'Check' },
        { name: 'reference', label: 'Check or reference number' }, { name: 'invoices', label: 'Apply to invoices (optional, comma separated)' }],
    }, v2 => {
      const invoices = String(v2.invoices ?? '').split(',').map(s => s.trim()).filter(Boolean);
      const open = (v.openInvoices ?? []);
      let left = Number(v2.amount);
      const applications = invoices.map(n => { const b = open.find(x => String(x.invoiceNumber) === n); const amount = Math.min(left, b?.balance ?? 0); left -= amount; return { invoiceNumber: Number(n), amount }; });
      return command('/api/receivables/payments', { customerCode: v2.customerCode, amount: Number(v2.amount), receivedOn: v2.receivedOn, method: v2.method, reference: v2.reference || null, applications: applications.length ? applications : null });
    }, load));
    $('[data-policy]', page)?.addEventListener('click', () => formModal({
      title: 'Credit policy', confirmLabel: 'Save', reasonRequired: true,
      fields: [{ name: 'pastDueDays', label: 'Hold when an invoice is more than this many days past due (empty: never)', type: 'number', min: 0, max: 365, value: policy.pastDueDays ?? '' },
        { name: 'includeOpenOrders', label: 'Open orders', type: 'check', text: 'Count approved, not yet invoiced orders toward credit limits', value: policy.includeOpenOrders ?? true }],
    }, (v2, reason) => command('/api/receivables/policy', { pastDueDays: v2.pastDueDays === '' || v2.pastDueDays == null ? null : Number(v2.pastDueDays), includeOpenOrders: !!v2.includeOpenOrders }, reason), load));
    for (const b of $$('[data-limit]', page)) b.addEventListener('click', () => formModal({
      title: `Credit limit for ${b.dataset.limit}`, confirmLabel: 'Save', reasonRequired: true,
      fields: [{ name: 'creditLimit', label: 'Credit limit (empty: no limit)', type: 'number', min: 0, step: 0.01, value: b.dataset.current }],
    }, (v2, reason) => command(`/api/receivables/customers/${encodeURIComponent(b.dataset.limit)}/credit-limit`, { creditLimit: v2.creditLimit === '' || v2.creditLimit == null ? null : Number(v2.creditLimit) }, reason), load));
    for (const b of $$('[data-release]', page)) b.addEventListener('click', () => confirmWithReason({
      title: `Release ${b.dataset.kind === 'SalesOrder' ? 'sales order' : 'shipment'} ${b.dataset.number} from credit hold`, detail: 'Say why (payment promised, prepaid, manager decision …). The customer stays on hold for anything else.', confirmLabel: 'Release',
    }, reason => command('/api/receivables/releases', { customerCode: b.dataset.release, salesOrderNumber: b.dataset.kind === 'SalesOrder' ? Number(b.dataset.number) : null, shipmentId: b.dataset.kind === 'Shipment' ? Number(b.dataset.number) : null }, reason))
      .then(r => { if (r) { toast(r.message ?? 'Released'); load(); } }));
    for (const b of $$('[data-void]', page)) b.addEventListener('click', () => confirmWithReason({ title: `Void payment ${b.dataset.void}`, detail: 'The invoices it paid open again.', confirmLabel: 'Void' },
      reason => command(`/api/receivables/payments/${b.dataset.void}/void`, {}, reason)).then(r => { if (r) { toast(r.message ?? 'Voided'); load(); } }));
  };
  await load();
}
