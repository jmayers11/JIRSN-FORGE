import { tryGet, command } from '../api.js';
import { who } from '../people.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast } from '../ui.js';
import { formModal, qty, can, plusDays } from '../forms.js';

// Purchase orders (spec §48): a buyer drafts, someone else approves, and only approved open lines
// count as supply for MRP and material readiness. Supplier date changes carry a reason.

const STATUS_TONE = { Draft: 'none', Approved: 'ok', Closed: 'info', Cancelled: 'bad' };

export async function procurementPage(page, ctx) {
  const me = ctx.me;
  const buyer = can(me, 'Buyer');
  const approver = can(me, 'PurchasingManager');
  let showClosed = false;
  let data = {};

  async function load() {
    const [orders, items] = await Promise.all([tryGet('/api/procurement/purchase-orders'), tryGet('/api/engineering/items')]);
    data = { orders, items };
  }
  const reload = async () => { await load(); render(); };
  const buyParts = () => (data.items.data ?? []).filter(i => i.makeBuy === 'Buy').map(i => [i.partNumber, `${i.partNumber} — ${i.description}`]);

  function render() {
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
      ${late.length ? `<div class="notice bad">${late.length} line(s) are past their promised date and not fully received. Receive them or record the supplier's new date so MRP and the schedule stay honest.</div>` : ''}
      ${data.orders.error ? errorBlock(data.orders.error, 'purchase orders') : !list.length ? emptyBlock('No purchase orders', buyer ? 'Draft one here, or from a Buy recommendation on the Material Plan.' : 'A buyer drafts purchase orders.') : list.map(card).join('')}`;

    $('#closed', page).onchange = e => { showClosed = e.target.checked; render(); };
    $('#new-po', page)?.addEventListener('click', newPo);
    $$('[data-approve]', page).forEach(b => b.addEventListener('click', () => act(b.dataset.approve, 'approve', 'Approve', 'Its open lines become supply for MRP and material readiness.')));
    $$('[data-close]', page).forEach(b => b.addEventListener('click', () => act(b.dataset.close, 'close', 'Close', 'Nothing more is expected against it, whatever was received.')));
    $$('[data-cancel]', page).forEach(b => b.addEventListener('click', () => act(b.dataset.cancel, 'cancel', 'Cancel', 'A draft is withdrawn.')));
    $$('[data-line]', page).forEach(b => b.addEventListener('click', () => addLine(Number(b.dataset.line))));
    $$('[data-date]', page).forEach(b => b.addEventListener('click', () => changeDate(...b.dataset.date.split('/').map(Number))));
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
        ${o.status === 'Approved' && buyer ? `<button class="btn small" data-close="${o.poNumber}">Close</button>` : ''}</header>
      <div class="faint" style="margin-bottom:6px">Raised by ${esc(who(o.createdBy))} ${esc(fmtDateTime(o.createdAt))}${o.approvedBy ? ` · approved by ${esc(who(o.approvedBy))} ${esc(fmtDateTime(o.approvedAt))}` : ''}</div>
      <table class="table"><thead><tr><th>Line</th><th>Part</th><th class="num">Ordered</th><th class="num">Received</th><th class="num">Open</th><th>Promised</th><th></th></tr></thead><tbody>
        ${o.lines.map(l => {
          const overdue = o.status === 'Approved' && l.open > 0 && new Date(`${l.promisedDate}T23:59:59`) < new Date();
          return `<tr class="${l.status === 'Cancelled' ? 'retired' : ''}"><td>${l.lineNumber}</td><td><b>${esc(l.partNumber)}</b></td>
            <td class="num">${qty(l.quantity)} <span class="faint">${esc(l.unitOfMeasure)}</span></td><td class="num">${qty(l.received)}</td><td class="num">${o.status === 'Approved' ? qty(l.open) : '—'}</td>
            <td class="${overdue ? 'c-bad' : ''}">${esc(fmtDate(l.promisedDate))}${overdue ? ' · overdue' : ''}${l.status === 'Cancelled' ? ' <span class="pill bad">Cancelled</span>' : ''}</td>
            <td class="nowrap">${open && buyer && l.status === 'Open' ? `<button class="btn small" data-date="${o.poNumber}/${l.lineNumber}">New date</button> <button class="btn small ghost" data-cancel-line="${o.poNumber}/${l.lineNumber}">Cancel</button>` : ''}</td></tr>`;
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
      fields: [{ name: 'supplier', label: 'Supplier', required: true }, ...lineFields] },
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

  function changeDate(po, line) {
    return formModal({ title: `New promised date — PO ${po} line ${line}`, detail: 'MRP, material readiness and the next schedule use the new date.', reasonRequired: true, confirmLabel: 'Record date',
      fields: [{ name: 'promisedDate', label: 'Supplier now promises', type: 'date', required: true }] },
    (v, reason) => command(`/api/procurement/purchase-orders/${po}/lines/${line}/date`, v, reason), reload);
  }

  async function act(po, verb, label, detail) {
    const done = await confirmWithReason({ title: `${label} PO ${po}`, detail, confirmLabel: label }, reason => command(`/api/procurement/purchase-orders/${po}/${verb}`, {}, reason));
    if (done) { toast(done.message); await reload(); }
  }

  await load();
  render();
}
