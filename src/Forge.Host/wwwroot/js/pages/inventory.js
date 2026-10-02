import { tryGet, command } from '../api.js';
import { img } from '../icons.js';
import { who } from '../people.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock, drawer, confirmWithReason, toast } from '../ui.js';
import { formModal, qty, can } from '../forms.js';

// Inventory (spec §11, §49): every quantity is the sum of ledger entries, and every entry says who,
// when, why and what it relates to. Adjustments, count variances and quality releases need a second person.

const KIND_LABEL = {
  Receipt: 'Receipt', Issue: 'Issue to order', Return: 'Return from order', Transfer: 'Move', Adjustment: 'Adjustment',
  ProductionReceipt: 'From production', Scrap: 'Scrap', CycleCount: 'Count variance', QualityHold: 'Hold', QualityRelease: 'Release', Shipment: 'Shipped',
};
const STATUS_TONE = { Available: 'ok', Inspection: 'info', Hold: 'warn' };

export async function inventoryPage(page, ctx) {
  const me = ctx.me;
  const handler = can(me, 'MaterialHandler');
  const controller = can(me, 'InventoryController');
  let tab = ctx.query.get('tab') ?? 'stock';
  let data = {};

  async function load() {
    const [summary, locations, requests, ledger, items, orders, resources] = await Promise.all([
      tryGet('/api/inventory/summary'), tryGet('/api/inventory/locations'), tryGet('/api/inventory/requests?pending=true'),
      tryGet('/api/inventory/transactions?take=200'), tryGet('/api/engineering/items'), tryGet('/api/orders/'), tryGet('/api/resources/'),
    ]);
    data = { summary, locations, requests, ledger, items, orders, sites: resources.data?.sites ?? [] };
  }

  const locs = (active = true) => (data.locations.data ?? []).filter(l => !active || l.active).map(l => [l.code, `${l.code} — ${l.name} (${l.kind})`]);
  const parts = () => (data.items.data ?? []).map(i => [i.partNumber, `${i.partNumber} — ${i.description}`]);
  const item = part => (data.items.data ?? []).find(i => i.partNumber === part);
  const released = () => (data.orders.data ?? []).filter(o => o.status === 'Released').map(o => [o.orderNumber, `#${o.orderNumber} — ${o.partNumber} × ${qty(o.quantity)}`]);
  const lotField = { name: 'lotNumber', label: 'Lot (lot-tracked parts only)', hint: 'Leave empty for parts that are not lot-tracked.' };
  const statusField = (value = 'Available') => ({ name: 'status', label: 'Stock status', type: 'select', value, options: [['Available', 'Available'], ['Inspection', 'Inspection'], ['Hold', 'Hold']] });
  const reload = async () => { await load(); render(); };

  const actions = {
    receive: () => formModal({
      title: 'Receive material', detail: 'Against a purchase order line, or with a reason (opening balance, customer-supplied).', confirmLabel: 'Receive',
      fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: parts(), required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, required: true },
        { name: 'locationCode', label: 'Into location', type: 'select', options: locs(), required: true },
        { name: 'poNumber', label: 'Purchase order (optional)', type: 'number' },
        { name: 'poLine', label: 'PO line (if the order has several lines for this part)', type: 'number' },
        lotField,
        { name: 'supplierLot', label: 'Supplier lot (optional)' },
        { name: 'inspect', type: 'check', label: 'Incoming inspection', text: 'Hold in Inspection until released' },
      ],
    }, (v, reason) => command('/api/inventory/receipts', v, reason), reload),

    issue: () => formModal({
      title: 'Issue material to an order', detail: 'Only to released orders, and never more than the order needs.', confirmLabel: 'Issue',
      fields: [
        { name: 'orderNumber', label: 'Order', type: 'select', options: released(), required: true },
        { name: 'partNumber', label: 'Component', type: 'select', options: parts(), required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, required: true },
        { name: 'locationCode', label: 'From location', type: 'select', options: locs(), required: true },
        lotField,
      ],
    }, (v, reason) => command('/api/inventory/issues', { ...v, orderNumber: Number(v.orderNumber) }, reason), reload),

    return: () => formModal({
      title: 'Return material from an order', confirmLabel: 'Return', reasonRequired: true,
      fields: [
        { name: 'orderNumber', label: 'Order number', type: 'number', required: true },
        { name: 'partNumber', label: 'Component', type: 'select', options: parts(), required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, required: true },
        { name: 'locationCode', label: 'Into location', type: 'select', options: locs(), required: true },
        lotField,
      ],
    }, (v, reason) => command('/api/inventory/returns', v, reason), reload),

    move: () => formModal({
      title: 'Move stock', confirmLabel: 'Move',
      fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: parts(), required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, required: true },
        { name: 'fromLocation', label: 'From', type: 'select', options: locs(), required: true },
        { name: 'toLocation', label: 'To', type: 'select', options: locs(), required: true },
        lotField, statusField(),
      ],
    }, (v, reason) => command('/api/inventory/transfers', v, reason), reload),

    scrap: () => formModal({
      title: 'Scrap stock', confirmLabel: 'Scrap', reasonRequired: true,
      fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: parts(), required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, required: true },
        { name: 'locationCode', label: 'Location', type: 'select', options: locs(false), required: true },
        lotField, statusField(),
      ],
    }, (v, reason) => command('/api/inventory/scrap', v, reason), reload),

    hold: () => formModal({
      title: 'Put stock on quality hold', detail: 'Held stock cannot be issued and does not count for MRP or readiness. Someone else releases it.', confirmLabel: 'Hold', reasonRequired: true,
      fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: parts(), required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, required: true },
        { name: 'locationCode', label: 'Location', type: 'select', options: locs(false), required: true },
        lotField,
      ],
    }, (v, reason) => command('/api/inventory/holds', v, reason), reload),

    release: () => formModal({
      title: 'Release stock to Available', detail: 'From quality hold or incoming inspection. You cannot release stock you put there yourself.', confirmLabel: 'Release', reasonRequired: true,
      fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: parts(), required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, required: true },
        { name: 'locationCode', label: 'Location', type: 'select', options: locs(false), required: true },
        lotField, statusField('Inspection'),
      ],
    }, (v, reason) => command('/api/inventory/releases', v, reason), reload),

    produced: () => formModal({
      title: 'Receive finished goods from production', detail: 'Up to the good quantity reported on the order\'s last operation. Lot-tracked parts take the order number as their lot unless you give one.', confirmLabel: 'Receive',
      fields: [
        { name: 'orderNumber', label: 'Order number', type: 'number', required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, required: true },
        { name: 'locationCode', label: 'Into location', type: 'select', options: locs(), required: true },
        lotField,
      ],
    }, (v, reason) => command('/api/inventory/production-receipts', v, reason), reload),

    adjust: () => formModal({
      title: 'Request an adjustment', detail: 'Nothing changes until a different person approves it.', confirmLabel: 'Request', reasonRequired: true,
      fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: parts(), required: true },
        { name: 'locationCode', label: 'Location', type: 'select', options: locs(false), required: true },
        { name: 'change', label: 'Change (+ adds, − removes)', type: 'number', step: 'any', required: true },
        lotField, statusField(),
      ],
    }, (v, reason) => command('/api/inventory/adjustments', v, reason), reload),

    count: () => formModal({
      title: 'Record a count', detail: 'Count what is physically there. A variance waits for a second person; if stock moves before then, count again.', confirmLabel: 'Record count',
      fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: parts(), required: true },
        { name: 'locationCode', label: 'Location', type: 'select', options: locs(false), required: true },
        { name: 'counted', label: 'Counted quantity', type: 'number', step: 'any', min: 0, required: true },
        lotField, statusField(),
      ],
    }, (v, reason) => command('/api/inventory/counts', v, reason), reload),

    location: () => formModal({
      title: 'Add a location', confirmLabel: 'Add',
      fields: [
        { name: 'code', label: 'Code (permanent)', code: true, required: true, placeholder: 'STORES-A' },
        { name: 'name', label: 'Name', required: true, placeholder: 'Main stores, aisle A' },
        { name: 'kind', label: 'Kind', type: 'select', options: ['Stores', 'Receiving', 'Inspection', 'Quarantine', 'Production', 'Shipping'].map(k => [k, k]) },
        { name: 'siteCode', label: 'Site', type: 'select', options: data.sites.map(s => [s.code, s.name]), required: true },
      ],
    }, (v, reason) => command('/api/inventory/locations', v, reason), reload),
  };

  function kpis() {
    const s = data.summary.data ?? [];
    const pending = data.requests.data ?? [];
    const tile = (icon, title, value, why) => `<div class="kpi">${img(icon)}<div><div class="t">${esc(title)}</div><div class="v">${value}</div><div class="why">${esc(why)}</div></div></div>`;
    return `<div class="kpis kpis-4">
      ${tile('inventory', 'Items with stock or supply', s.length, 'Parts with stock, allocations, open supply or safety stock.')}
      ${tile('alert', 'Below safety stock', s.filter(x => x.belowSafetyStock).length, 'Available (on hand less allocated) under safety stock.')}
      ${tile('quality', 'Inspection or hold', s.filter(x => x.inspection > 0 || x.hold > 0).length, 'Parts with stock that cannot be used yet.')}
      ${tile('complete', 'Waiting for approval', pending.length, 'Adjustments and count variances for a second person.')}
    </div>`;
  }

  function stockTab() {
    const s = data.summary;
    if (s.error) return errorBlock(s.error, 'stock');
    if (!s.data.length) return emptyBlock('No stock yet', 'Add a location, then receive material. Every quantity here will be explained by its receipts, issues and moves.');
    return `<table class="table"><thead><tr><th>Part</th><th class="num">On hand</th><th class="num">Inspection</th><th class="num">Hold</th><th class="num">Allocated</th><th class="num">Available</th><th class="num">On order</th><th class="num">In production</th><th class="num">Safety stock</th></tr></thead><tbody>
      ${s.data.map(x => `<tr class="click" data-part="${esc(x.partNumber)}">
        <td><b>${esc(x.partNumber)}</b> <span class="faint">${esc(x.unitOfMeasure)} · ${esc(x.makeBuy)}</span><br><span class="muted">${esc(x.description)}</span></td>
        <td class="num">${qty(x.onHand)}</td><td class="num">${x.inspection ? `<span class="c-info">${qty(x.inspection)}</span>` : '—'}</td><td class="num">${x.hold ? `<span class="c-warn">${qty(x.hold)}</span>` : '—'}</td>
        <td class="num" title="Required by released orders and not yet issued">${qty(x.allocated)}</td>
        <td class="num ${x.available < 0 ? 'c-bad' : x.belowSafetyStock ? 'c-warn' : ''}"><b>${qty(x.available)}</b></td>
        <td class="num">${qty(x.onOrder)}</td><td class="num">${qty(x.inProduction)}</td>
        <td class="num">${x.safetyStock ? qty(x.safetyStock) : '—'}${x.belowSafetyStock ? ' <span class="pill warn">below</span>' : ''}</td></tr>`).join('')}
      </tbody></table>`;
  }

  const ledgerRow = t => `<tr>
    <td class="nowrap">${esc(fmtDateTime(t.at))}</td><td>${esc(KIND_LABEL[t.kind] ?? t.kind)}</td><td>${esc(t.partNumber)}${t.lotNumber ? ` <span class="faint">lot ${esc(t.lotNumber)}</span>` : ''}</td>
    <td class="num">${qty(t.quantity)}</td>
    <td>${t.fromLocation ? `${esc(t.fromLocation)} <span class="faint">${esc(t.fromStatus)}</span>` : '<span class="faint">—</span>'} → ${t.toLocation ? `${esc(t.toLocation)} <span class="faint">${esc(t.toStatus)}</span>` : '<span class="faint">out</span>'}</td>
    <td>${t.orderNumber ? `<a href="#/orders/${t.orderNumber}">#${t.orderNumber}</a>` : ''}${t.poNumber ? `PO ${t.poNumber}/${t.poLine}` : ''}</td>
    <td class="muted">${esc(t.reason ?? '')}</td>
    <td>${esc(who(t.actor))}${t.approvedBy ? `<br><span class="faint">approved by ${esc(who(t.approvedBy))}</span>` : ''}</td></tr>`;
  const ledgerTable = rows => `<table class="table"><thead><tr><th>When</th><th>What</th><th>Part</th><th class="num">Qty</th><th>From → To</th><th>For</th><th>Why</th><th>Who</th></tr></thead><tbody>${rows.map(ledgerRow).join('')}</tbody></table>`;

  function ledgerTab() {
    const l = data.ledger;
    if (l.error) return errorBlock(l.error, 'the inventory ledger');
    return l.data.length ? `${ledgerTable(l.data)}<div class="faint" style="margin-top:6px">Latest 200 entries. Entries are never edited or deleted; corrections are new entries.</div>` : emptyBlock('No transactions yet', 'Receipts, issues and moves appear here as they happen.');
  }

  function approvalsTab() {
    const r = data.requests;
    if (r.error) return errorBlock(r.error, 'approvals');
    if (!r.data.length) return emptyBlock('Nothing waiting', 'Adjustments and count variances appear here until a second person approves or rejects them.');
    return `<table class="table"><thead><tr><th>#</th><th>What</th><th>Part</th><th>Where</th><th class="num">On record</th><th class="num">Counted / change</th><th class="num">Variance</th><th>Why</th><th>Asked by</th><th></th></tr></thead><tbody>
      ${r.data.map(x => {
        const mine = x.requestedBy === `person:${me.personId}`;
        const variance = x.kind === 'Count' ? x.quantity - x.systemQuantity : x.quantity;
        return `<tr><td>${x.requestId}</td><td>${esc(x.kind === 'Count' ? 'Count' : 'Adjustment')}</td><td>${esc(x.partNumber)}${x.lotNumber ? ` <span class="faint">lot ${esc(x.lotNumber)}</span>` : ''}</td>
          <td>${esc(x.locationCode)} <span class="pill ${STATUS_TONE[x.status]}">${esc(x.status)}</span></td>
          <td class="num">${qty(x.systemQuantity)}</td><td class="num">${qty(x.quantity)}</td>
          <td class="num ${variance < 0 ? 'c-bad' : 'c-ok'}"><b>${variance > 0 ? '+' : ''}${qty(variance)}</b></td>
          <td class="muted">${esc(x.reason)}</td><td>${esc(who(x.requestedBy))}<br><span class="faint">${esc(fmtDateTime(x.requestedAt))}</span></td>
          <td class="nowrap">${controller && !mine ? `<button class="btn primary small" data-approve="${x.requestId}">Approve</button> <button class="btn small" data-reject="${x.requestId}">Reject</button>` : mine ? '<span class="faint">yours — someone else decides</span>' : ''}</td></tr>`;
      }).join('')}</tbody></table>`;
  }

  function locationsTab() {
    const l = data.locations;
    if (l.error) return errorBlock(l.error, 'locations');
    if (!l.data.length) return emptyBlock('No locations yet', controller ? 'Add the places stock is kept: stores, receiving, inspection, quarantine.' : 'An inventory controller adds locations.');
    return `<table class="table"><thead><tr><th>Code</th><th>Name</th><th>Kind</th><th>Site</th><th></th></tr></thead><tbody>
      ${l.data.map(x => `<tr class="${x.active ? '' : 'retired'}"><td class="mono">${esc(x.code)}</td><td>${esc(x.name)}</td><td>${esc(x.kind)}</td><td>${esc(x.siteCode)}</td>
        <td>${!x.active ? '<span class="pill none">Retired</span>' : controller ? `<button class="btn small ghost" data-retire="${esc(x.code)}">Retire</button>` : ''}</td></tr>`).join('')}
      </tbody></table>`;
  }

  function render() {
    const pending = data.requests.data?.length ?? 0;
    const buttons = [
      handler && ['receive', 'Receive'], handler && ['issue', 'Issue to order'], handler && ['return', 'Return'], handler && ['move', 'Move'],
      handler && ['produced', 'From production'], handler && ['hold', 'Hold'], controller && ['release', 'Release'], handler && ['scrap', 'Scrap'],
      handler && ['count', 'Count'], handler && ['adjust', 'Adjust'], controller && ['location', '+ Location'],
    ].filter(Boolean);
    page.innerHTML = `
      <div class="page-head">
        <div><h1>Inventory</h1><div class="sub">Every quantity is the sum of its ledger entries. Adjustments, count variances and quality releases need a second person.</div></div>
      </div>
      ${buttons.length ? `<div class="toolbar">${buttons.map(([k, l]) => `<button class="btn ${k === 'receive' ? 'primary' : ''}" data-act="${k}">${esc(l)}</button>`).join('')}</div>` : '<div class="notice">You can look at stock. Material handlers move it; inventory controllers approve changes.</div>'}
      ${kpis()}
      <section class="panel">
        <header><div class="seg" id="tabs">${[['stock', 'Stock'], ['ledger', 'Ledger'], ['approvals', `Approvals${pending ? ` (${pending})` : ''}`], ['locations', 'Locations']].map(([k, l]) => `<button data-tab="${k}" class="${tab === k ? 'on' : ''}">${esc(l)}</button>`).join('')}</div></header>
        ${tab === 'ledger' ? ledgerTab() : tab === 'approvals' ? approvalsTab() : tab === 'locations' ? locationsTab() : stockTab()}
      </section>`;

    $$('[data-tab]', page).forEach(b => b.addEventListener('click', () => { tab = b.dataset.tab; render(); }));
    $$('[data-act]', page).forEach(b => b.addEventListener('click', () => {
      if (!(data.locations.data ?? []).length && b.dataset.act !== 'location') { toast('Add a location first (inventory controller).', true); return; }
      actions[b.dataset.act]();
    }));
    $$('[data-part]', page).forEach(r => r.addEventListener('click', () => partDrawer(r.dataset.part)));
    $$('[data-approve]', page).forEach(b => b.addEventListener('click', () => decide(b.dataset.approve, 'approve')));
    $$('[data-reject]', page).forEach(b => b.addEventListener('click', () => decide(b.dataset.reject, 'reject')));
    $$('[data-retire]', page).forEach(b => b.addEventListener('click', async () => {
      const done = await confirmWithReason({ title: `Retire location ${b.dataset.retire}`, detail: 'Only an empty location can be retired.', confirmLabel: 'Retire' },
        reason => command(`/api/inventory/locations/${encodeURIComponent(b.dataset.retire)}/retire`, {}, reason));
      if (done) { toast(done.message); await reload(); }
    }));
  }

  async function decide(id, verb) {
    const done = await confirmWithReason({ title: `${verb === 'approve' ? 'Approve' : 'Reject'} request #${id}`, detail: verb === 'approve' ? 'The change is posted to the ledger with you as approver.' : 'Stock stays as it is.', confirmLabel: verb === 'approve' ? 'Approve' : 'Reject' },
      reason => command(`/api/inventory/requests/${id}/${verb}`, {}, reason));
    if (done) { toast(done.message); await reload(); }
  }

  async function partDrawer(part) {
    const d = drawer(`${part}${item(part) ? ` — ${item(part).description}` : ''}`, '<div class="muted">Loading…</div>');
    const [stock, ledger] = await Promise.all([tryGet(`/api/inventory/stock?part=${encodeURIComponent(part)}`), tryGet(`/api/inventory/transactions?part=${encodeURIComponent(part)}&take=100`)]);
    d.element.innerHTML = `
      <h3 style="margin:0">Where it is</h3>
      ${stock.error ? errorBlock(stock.error, 'stock') : stock.data.length ? `<table class="table"><thead><tr><th>Location</th><th>Lot</th><th>Status</th><th class="num">Qty</th></tr></thead><tbody>
        ${stock.data.map(b => `<tr><td>${esc(b.locationCode)}</td><td>${esc(b.lotNumber ?? '—')}</td><td><span class="pill ${STATUS_TONE[b.status]}">${esc(b.status)}</span></td><td class="num">${qty(b.quantity)}</td></tr>`).join('')}</tbody></table>` : '<div class="muted">None on hand.</div>'}
      <h3 style="margin:0">How it got there</h3>
      ${ledger.error ? errorBlock(ledger.error, 'ledger') : ledger.data.length ? ledgerTable(ledger.data) : '<div class="muted">No transactions.</div>'}
      <div class="faint"><a href="#/material-plan?part=${encodeURIComponent(part)}">Open the material plan for ${esc(part)} →</a></div>`;
  }

  await load();
  render();
}
