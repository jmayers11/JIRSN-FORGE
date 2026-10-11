import { tryGet, command } from '../api.js';
import { who } from '../people.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast } from '../ui.js';
import { formModal, qty, can } from '../forms.js';
import { attachmentsDrawer } from '../attachments.js';

// Shipping (spec §47): a shipment is put together and packed by one person, who confirms the stock is
// there; a different person with shipping authority ships it, and only then does stock leave the ledger.
// Nothing on an undecided nonconformance ships.

const TONE = { Open: 'none', Packed: 'info', Shipped: 'ok', Cancelled: 'bad' };

export async function shippingPage(page, ctx) {
  const me = ctx.me;
  const handler = can(me, 'MaterialHandler');
  const shipper = can(me, 'Shipper');
  const planner = can(me, 'Planner');
  let showClosed = false;
  let data = {};

  async function load() {
    const [shipments, demand, stock, items] = await Promise.all([
      tryGet(`/api/inventory/shipments?includeClosed=${showClosed}`), tryGet('/api/planning/demand'), tryGet('/api/inventory/stock'), tryGet('/api/engineering/items'),
    ]);
    data = { shipments, demand, stock, items };
  }
  const reload = async () => { await load(); render(); };
  const mine = actor => actor === `person:${me.personId}`;
  const available = () => (data.stock.data ?? []).filter(b => b.status === 'Released' && b.quantity > 0);

  function render() {
    const list = data.shipments.data ?? [];
    const toShip = (data.demand.data ?? []).filter(d => d.status === 'Open' && d.remaining > 0 && d.kind !== 'Forecast');
    const late = toShip.filter(d => d.needDate < new Date().toISOString().slice(0, 10));
    page.innerHTML = `
      <div class="page-head">
        <div><h1>Shipping</h1><div class="sub">Customer shipments. One person packs and confirms the stock; a different person ships it, and only then does stock leave inventory.</div></div>
        <div class="spacer"></div>
        <label class="opt"><input type="checkbox" id="closed" ${showClosed ? 'checked' : ''}> Show shipped and cancelled</label>
        ${handler ? '<button class="btn primary" id="new">New shipment</button>' : ''}
      </div>
      ${late.length ? `<div class="notice bad">${late.length} customer demand(s) are past their need date and not fully shipped.</div>` : ''}
      <section class="panel"><header><h2>Shipments</h2></header>
        ${data.shipments.error ? errorBlock(data.shipments.error, 'shipments') : !list.length ? emptyBlock('No open shipments', handler ? 'Start one from a demand below, or for any customer.' : 'A material handler puts shipments together.') : list.map(card).join('')}
      </section>
      <section class="panel"><header><h2>Demand still to ship</h2></header>
        ${data.demand.error ? errorBlock(data.demand.error, 'demand') : !toShip.length ? emptyBlock('Nothing waiting to ship', 'Customer orders and contract deliveries recorded on the Material Plan appear here until they have shipped.') : `
        <table class="table"><thead><tr><th>Demand</th><th>Customer</th><th>Part</th><th class="num">Ordered</th><th class="num">Shipped</th><th class="num">To ship</th><th>Need date</th><th class="num">Available now</th><th></th></tr></thead><tbody>
          ${toShip.map(d => {
            const onHand = available().filter(b => b.partNumber === d.partNumber).reduce((s, b) => s + b.quantity, 0);
            const overdue = d.needDate < new Date().toISOString().slice(0, 10);
            return `<tr><td>${d.demandId}${d.reference ? ` <span class="faint">${esc(d.reference)}</span>` : ''}</td><td>${esc(d.customer ?? '—')}</td><td><b>${esc(d.partNumber)}</b></td>
              <td class="num">${qty(d.quantity)}</td><td class="num">${qty(d.shipped)}</td><td class="num">${qty(d.remaining)}</td>
              <td class="${overdue ? 'c-bad' : ''}">${esc(fmtDate(d.needDate))}${overdue ? ' · late' : ''}</td><td class="num ${onHand < d.remaining ? 'c-warn' : ''}">${qty(onHand)}</td>
              <td>${handler && onHand > 0 ? `<button class="btn small" data-from-demand="${d.demandId}">Ship this</button>` : ''}</td></tr>`;
          }).join('')}</tbody></table>`}
        ${planner ? '' : '<div class="faint" style="margin-top:6px">Demand is recorded by planners on the Material Plan.</div>'}
      </section>`;

    $('#closed', page).onchange = async e => { showClosed = e.target.checked; await reload(); };
    $('#new', page)?.addEventListener('click', () => create(null));
    $$('[data-from-demand]', page).forEach(b => b.addEventListener('click', () => create((data.demand.data ?? []).find(d => d.demandId === Number(b.dataset.fromDemand)))));
    $$('[data-pack]', page).forEach(b => b.addEventListener('click', () => step(b.dataset.pack, 'pack', 'Pack', 'Confirms the stock is there and nothing on it is on an open NCR. Someone else ships it.', false)));
    $$('[data-ship]', page).forEach(b => b.addEventListener('click', () => ship(Number(b.dataset.ship))));
    $$('[data-shipment-evidence]', page).forEach(b => b.addEventListener('click', () => attachmentsDrawer(`Shipment ${b.dataset.shipmentEvidence} evidence`, { type: 'Shipment', id: b.dataset.shipmentEvidence, me, title: 'Evidence', note: 'Packing slips, certificates and photos are reference evidence; attaching a file does not release stock or complete inspection.' })));
    $$('[data-cancel]', page).forEach(b => b.addEventListener('click', () => step(b.dataset.cancel, 'cancel', 'Cancel', 'Nothing has left the building; stock is not touched.', true)));
  }

  function card(s) {
    return `<div class="card-row">
      <div class="grow">
        <div><b>Shipment ${s.shipmentId}</b> · ${esc(s.customer)}${s.reference ? ` · ${esc(s.reference)}` : ''}${s.demandId ? ` · demand ${s.demandId}` : ''} <span class="pill ${TONE[s.status]}">${esc(s.status)}</span></div>
        <div>${s.lines.map(l => `${qty(l.quantity)} <b>${esc(l.partNumber)}</b> from ${esc(l.locationCode)}${l.lotNumber ? ` lot ${esc(l.lotNumber)}` : ''}`).join(' · ')}${s.shipTo ? ` <span class="faint">→ ${esc(s.shipTo)}</span>` : ''}</div>
        <div class="faint">Created by ${esc(who(s.createdBy))} ${esc(fmtDateTime(s.createdAt))}${s.packedBy ? ` · packed by ${esc(who(s.packedBy))} ${esc(fmtDateTime(s.packedAt))}` : ''}${s.shippedBy ? ` · shipped by ${esc(who(s.shippedBy))} ${esc(fmtDateTime(s.shippedAt))}${s.carrier ? ` via ${esc(s.carrier)}` : ''}${s.tracking ? ` (${esc(s.tracking)})` : ''}` : ''}${s.cancelReason ? ` · cancelled: ${esc(s.cancelReason)}` : ''}</div>
      </div>
      <div class="nowrap">
        <button class="btn small ghost" data-shipment-evidence="${s.shipmentId}">Evidence</button>
        ${s.status === 'Open' && handler ? `<button class="btn primary small" data-pack="${s.shipmentId}">Pack</button>` : ''}
        ${s.status === 'Packed' && shipper && !mine(s.packedBy) ? `<button class="btn primary small" data-ship="${s.shipmentId}">Ship</button>` : ''}
        ${s.status === 'Packed' && mine(s.packedBy) ? '<span class="faint">You packed it — someone else ships it</span>' : ''}
        ${['Open', 'Packed'].includes(s.status) && handler ? `<button class="btn small ghost" data-cancel="${s.shipmentId}">Cancel</button>` : ''}
      </div></div>`;
  }

  function create(demand) {
    const stock = available().filter(b => !demand || b.partNumber === demand.partNumber);
    const options = stock.map(b => [`${b.partNumber}|${b.locationCode}|${b.lotNumber ?? ''}`, `${b.partNumber} at ${b.locationCode}${b.lotNumber ? ` lot ${b.lotNumber}` : ''} — ${qty(b.quantity)} available`]);
    if (!options.length) { toast(demand ? `No ${demand.partNumber} is available to ship.` : 'No stock is available to ship.', true); return null; }
    const lines = [1, 2, 3];
    return formModal({ title: demand ? `Ship demand ${demand.demandId} — ${demand.partNumber}` : 'New shipment', detail: 'Pick the stock to send. Packing checks it is still there.', confirmLabel: 'Create shipment',
      fields: [
        ...(demand ? [] : [{ name: 'customer', label: 'Customer', required: true }]),
        { name: 'reference', label: 'Customer reference (optional)', value: demand?.reference },
        { name: 'shipTo', label: 'Ship to (optional)' },
        ...lines.flatMap(n => [
          { name: `stock${n}`, label: `Line ${n} stock${n > 1 ? ' (optional)' : ''}`, type: 'select', options, blank: n > 1 ? '—' : undefined, required: n === 1 },
          { name: `qty${n}`, label: `Line ${n} quantity`, type: 'number', min: 0, step: 'any', value: n === 1 && demand ? demand.remaining : undefined },
        ]),
      ] },
    (v, reason) => command('/api/inventory/shipments', {
      customer: v.customer ?? null, reference: v.reference, shipTo: v.shipTo, demandId: demand?.demandId ?? null,
      lines: lines.filter(n => v[`stock${n}`]).map(n => {
        if (!v[`qty${n}`]) throw new Error(`Line ${n}: give a quantity.`);
        const [partNumber, locationCode, lot] = v[`stock${n}`].split('|');
        return { partNumber, locationCode, lotNumber: lot || null, quantity: v[`qty${n}`] };
      }),
    }, reason), reload);
  }

  function ship(id) {
    return formModal({ title: `Ship shipment ${id}`, detail: 'Stock leaves inventory now, recorded against this shipment.', confirmLabel: 'Ship',
      fields: [{ name: 'carrier', label: 'Carrier (optional)' }, { name: 'tracking', label: 'Tracking number (optional)' }] },
    (v, reason) => command(`/api/inventory/shipments/${id}/ship`, v, reason), reload);
  }

  async function step(id, verb, label, detail, reasonRequired) {
    const done = await confirmWithReason({ title: `${label} shipment ${id}`, detail, confirmLabel: label, reasonRequired }, reason => command(`/api/inventory/shipments/${id}/${verb}`, {}, reason));
    if (done) { toast(done.message); await reload(); }
  }

  await load();
  render();
}
