import { tryGet, command } from '../api.js';
import { img } from '../icons.js';
import { who } from '../people.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock, drawer, confirmWithReason, toast } from '../ui.js';
import { formModal, qty, canAction } from '../forms.js';
import { serialDrawer, traceDrawer } from '../genealogy.js';
import { loadShelfLife, shelfLifeTab, bindShelfLifeTab, appendPartShelfLife } from '../shelflife.js';
import { loadReceiving, receivingTab, bindReceivingTab, receivingCount } from '../receiving.js';
import { outsideProcessingTab, bindOutsideProcessingTab } from '../outside-processing.js';

// Inventory (spec §11, §49): every quantity is the sum of ledger entries, and every entry says who,
// when, why and what it relates to. Adjustments, count variances and quality releases need a second person.

const KIND_LABEL = {
  Receipt: 'Receipt', Issue: 'Issue to order', Return: 'Return from order', Transfer: 'Move', Adjustment: 'Adjustment',
  ProductionReceipt: 'From production', Scrap: 'Scrap', CycleCount: 'Count variance', QualityHold: 'Hold', QualityRelease: 'Release', Shipment: 'Shipped',
  MaintenanceIssue: 'Issue to work order', MaintenanceReturn: 'Return from work order', ProjectIssue: 'Issue to project', ProjectReturn: 'Return from project', Split: 'Split', Merge: 'Merge', Remnant: 'Remnant from order', Byproduct: 'Byproduct from order',
};
const IDENTITY_KINDS = ['Lot', 'Batch', 'Roll', 'Reel', 'Sheet', 'Piece'];
const STATUS_TONE = { Released: 'ok', Inspection: 'info', Hold: 'warn' };
const LOCATION_KINDS = ['Stores', 'Receiving', 'Inspection', 'Quarantine', 'Production', 'Shipping', 'Staging', 'MachineSide', 'WipStorage', 'ExternalProcessor', 'InTransit'];
const KIND_NAME = { MachineSide: 'Machine side', WipStorage: 'WIP storage', ExternalProcessor: 'Outside processor', InTransit: 'In transit' };
const LEVELS = ['Area', 'Zone', 'Rack', 'Shelf', 'Bin', 'Position'];
const custodyText = l => l.custody === 'Supplier' ? `Outside processor ${l.custodianSupplier}` : l.custody === 'InTransit' ? 'In transit' : 'Company';

export async function inventoryPage(page, ctx) {
  const me = ctx.me;
  const handler = canAction(me, 'inventory.transact');
  const controller = canAction(me, 'inventory.approve');
  const inventoryManager = canAction(me, 'inventory.manage');
  const mroManager = canAction(me, 'inventory.mro.manage');
  let tab = ctx.query.get('tab') ?? 'stock';
  let data = {};

  async function load() {
    const [summary, locations, requests, ledger, items, orders, resources, mro, suppliers, tasks, programs, due] = await Promise.all([
      tryGet('/api/inventory/summary'), tryGet('/api/inventory/locations'), tryGet('/api/inventory/requests?pending=true'),
      tryGet('/api/inventory/transactions?take=200'), tryGet('/api/engineering/items'), tryGet('/api/orders/'), tryGet('/api/resources/'), tryGet('/api/inventory/mro'),
      tryGet('/api/procurement/suppliers'), tryGet('/api/inventory/tasks?open=true'), tryGet('/api/inventory/count-programs'), tryGet('/api/inventory/counts-due'),
    ]);
    const shelf = await loadShelfLife();
    const receiving = await loadReceiving();
    const outside = await tryGet('/api/inventory/outside-processing');
    data = { shelf, receiving, outside, summary, locations, requests, ledger, items, orders, mro, tasks, programs, due, sites: resources.data?.sites ?? [], workCenters: resources.data?.workCenters ?? [], machines: resources.data?.machines ?? [], suppliers: suppliers.data ?? [] };
  }

  const locs = (active = true, ours = true) => (data.locations.data ?? []).filter(l => (!active || l.active) && (!ours || l.inCompanyCustody)).map(l => [l.code, `${l.code} — ${l.name} (${l.kind})`]);
  const parts = () => (data.items.data ?? []).map(i => [i.partNumber, `${i.partNumber} — ${i.description}`]);
  const item = part => (data.items.data ?? []).find(i => i.partNumber === part);
  const released = () => (data.orders.data ?? []).filter(o => o.status === 'Released').map(o => [o.orderNumber, `#${o.orderNumber} — ${o.partNumber} × ${qty(o.quantity)}`]);
  const lotField = { name: 'lotNumber', label: 'Lot (lot-tracked parts only)', hint: 'Leave empty for parts that are not lot-tracked.' };
  const statusField = (value = 'Released') => ({ name: 'status', label: 'Quality status', type: 'select', value, options: [['Released', 'Released'], ['Inspection', 'Inspection'], ['Hold', 'Hold']] });
  const reload = async () => { await load(); render(); };
  const shares = text => (text ?? '').split(/[,;\n]+/).map(x => x.trim()).filter(Boolean).map(x => { const [lotNumber, q] = x.split(/\s+/); return { lotNumber: lotNumber.toUpperCase(), quantity: Number(q) }; });

  // V3.1: where a location sits, what it serves, and who holds stock there.
  const placeFields = l => [
    { name: 'parentCode', label: 'Sits in (optional)', type: 'select', value: l.parentCode ?? '', options: [['', '— top level —'], ...locs(true, false).filter(([c]) => c !== l.code)] },
    { name: 'level', label: 'Level', type: 'select', value: l.level ?? '', options: [['', l.code ? l.level : 'One narrower than where it sits'], ...LEVELS.map(x => [x, x])] },
    { name: 'workCenterCode', label: 'Serves work center (production, staging, WIP storage)', type: 'select', value: l.workCenterCode ?? '', options: [['', '—'], ...data.workCenters.map(w => [w.code, `${w.code} — ${w.name}`])] },
    { name: 'machineCode', label: 'Beside machine (machine-side only)', type: 'select', value: l.machineCode ?? '', options: [['', '—'], ...data.machines.map(m => [m.code, `${m.code} — ${m.name}`])] },
    { name: 'custodianSupplier', label: 'Outside processor (outside-processor locations only)', type: 'select', value: l.custodianSupplier ?? '', options: [['', '—'], ...data.suppliers.map(x => [x.code, `${x.code} — ${x.name}`])] },
  ];
  const locationBody = v => ({
    ...v, parentCode: v.parentCode || null, level: v.level || null, workCenterCode: v.workCenterCode || null, machineCode: v.machineCode || null,
    custodianSupplier: v.custodianSupplier || null, nettable: v.nettable === '' || v.nettable === undefined ? null : v.nettable === true || v.nettable === 'true',
  });
  const changeLocation = l => formModal({
    title: `Change location ${l.code}`, detail: `${KIND_NAME[l.kind] ?? l.kind} at ${l.siteCode}. The kind and site never change; retire it and add another instead.`, confirmLabel: 'Save', reasonRequired: true,
    fields: [
      { name: 'name', label: 'Name', value: l.name, required: true },
      ...placeFields(l),
      { name: 'nettable', label: 'Counts for planning', type: 'select', value: String(l.nettable), options: [['true', 'Yes'], ['false', 'No']] },
    ],
  }, (v, reason) => { const b = locationBody(v); return command(`/api/inventory/locations/${encodeURIComponent(l.code)}`, { ...b, level: b.level ?? l.level, nettable: b.nettable ?? l.nettable }, reason); }, reload);

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
        { name: 'revision', label: 'Part revision received (optional)', code: true, hint: 'The released revision on the paperwork. Without it the stock shows as revision not recorded. One lot holds one revision.' },
        { name: 'identityKind', label: 'What the lot physically is', type: 'select', options: [['', 'Lot (serialized parts: serial)'], ...IDENTITY_KINDS.map(k => [k, k])] },
        { name: 'manufacturedOn', label: 'Manufactured on (from the certificate, optional)', type: 'date' },
        { name: 'expiresOn', label: 'Expires on (from the paperwork, optional)', type: 'date', hint: 'The lot keeps the earlier of this and the part\'s shelf-life rule.' },
        { name: 'inspect', type: 'check', label: 'Incoming inspection', text: 'Hold in Inspection until released' },
      ],
    }, (v, reason) => command('/api/inventory/receipts', { ...v, identityKind: v.identityKind || null, manufacturedOn: v.manufacturedOn || null, expiresOn: v.expiresOn || null }, reason), reload),

    // V3.2: dividing an instance creates new instances whose quantities add up to what left it.
    split: (preset = {}) => formModal({
      title: 'Split a lot, roll or batch', detail: 'Each new instance gets its own number and keeps the source\'s revision and supplier lot. What you do not divide off stays in the source. Serials are never split.', confirmLabel: 'Split', reasonRequired: true,
      fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: parts(), value: preset.partNumber, required: true },
        { name: 'lotNumber', label: 'Lot to split', value: preset.lotNumber, code: true, required: true },
        { name: 'locationCode', label: 'Where it is', type: 'select', options: locs(), value: preset.locationCode, required: true },
        statusField(preset.status),
        { name: 'results', label: 'New instances', required: true, placeholder: 'C-1001-A 900, C-1001-B 70', hint: 'Number and quantity for each, separated by commas.' },
      ],
    }, (v, reason) => command('/api/inventory/splits', { ...v, results: shares(v.results) }, reason), reload),

    merge: () => formModal({
      title: 'Merge lots', detail: 'Only for parts whose merge policy allows it. The sources stop being separately traceable in the result, so every merge is recorded.', confirmLabel: 'Merge', reasonRequired: true,
      fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: parts(), required: true },
        { name: 'sources', label: 'From lots', required: true, placeholder: 'R-12 40, R-13 25', hint: 'Lot and quantity for each, separated by commas.' },
        { name: 'resultLot', label: 'Into lot (new or existing)', code: true, required: true },
        { name: 'locationCode', label: 'Where they are', type: 'select', options: locs(), required: true },
        statusField(),
      ],
    }, (v, reason) => command('/api/inventory/merges', { ...v, sources: shares(v.sources) }, reason), reload),

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
        { name: 'fromLocation', label: 'From', type: 'select', options: locs(true, false), required: true },
        { name: 'toLocation', label: 'To', type: 'select', options: locs(true, false), required: true, hint: 'Moving stock to an outside processor or in transit puts it out of the company\'s custody until it is moved back.' },
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
      title: 'Release stock', detail: 'From quality hold or incoming inspection. You cannot release stock you put there yourself.', confirmLabel: 'Release', reasonRequired: true,
      fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: parts(), required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, required: true },
        { name: 'locationCode', label: 'Location', type: 'select', options: locs(false), required: true },
        lotField, statusField('Inspection'),
      ],
    }, (v, reason) => command('/api/inventory/releases', v, reason), reload),

    produced: () => formModal({
      title: 'Receive finished goods from production', detail: 'Up to the good quantity reported on the order\'s last operation. Lot-tracked parts take the order number as their lot unless you give one. Serialized parts get one new serial number per unit here.', confirmLabel: 'Receive',
      fields: [
        { name: 'orderNumber', label: 'Order number', type: 'number', required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, required: true },
        { name: 'locationCode', label: 'Into location', type: 'select', options: locs(), required: true },
        lotField,
        { name: 'serials', label: 'Serial numbers (serialized parts)', placeholder: '000481, 000482', hint: 'One per unit, separated by commas or spaces. Never reused.' },
      ],
    }, (v, reason) => command('/api/inventory/production-receipts', { ...v, serials: v.serials ? v.serials.split(/[\s,;]+/).filter(Boolean) : null }, reason), reload),

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

    // V3.4: material to the machine, as tasks: requested, allocated, picked (scanned), delivered (scanned).
    request: (preset = {}) => formModal({
      title: 'Request material', detail: 'Staging and kitting are for a released order and go to its staging or point of use. Put-away goes to stores; replenishment tops up a point of use. Give a source to allocate it now.', confirmLabel: 'Request',
      fields: [
        { name: 'kind', label: 'Task', type: 'select', value: preset.kind ?? 'Stage', options: [['Stage', 'Stage for an order'], ['Kit', 'Kit for an order'], ['PutAway', 'Put away'], ['Replenish', 'Replenish a point of use']] },
        { name: 'orderNumber', label: 'Order (staging and kitting)', type: 'number', value: preset.orderNumber },
        { name: 'kitCode', label: 'Kit code (kitting)', code: true },
        { name: 'partNumber', label: 'Part', type: 'select', options: parts(), value: preset.partNumber, required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, value: preset.quantity, required: true },
        { name: 'toLocation', label: 'To', type: 'select', options: locs(), value: preset.toLocation, required: true },
        { name: 'fromLocation', label: 'From (optional: allocates now)', type: 'select', options: [['', '— allocate later —'], ...locs()] },
        lotField,
      ],
    }, (v, reason) => command('/api/inventory/tasks', { ...v, orderNumber: v.orderNumber ? Number(v.orderNumber) : null, fromLocation: v.fromLocation || null, kitCode: v.kitCode || null, lotNumber: v.lotNumber || null }, reason), reload),

    program: () => formModal({
      title: 'Add a cycle-count program', detail: 'Every balance in the location (and the locations in it), of the part, or both, is due for counting this often.', confirmLabel: 'Add',
      fields: [
        { name: 'programCode', label: 'Code', code: true, required: true, placeholder: 'CC-STORES' },
        { name: 'name', label: 'Name', required: true, placeholder: 'Main stores every 30 days' },
        { name: 'locationCode', label: 'Location (optional)', type: 'select', options: [['', '— every location —'], ...locs(true, false)] },
        { name: 'partNumber', label: 'Part (optional)', type: 'select', options: [['', '— every part —'], ...parts()] },
        { name: 'frequencyDays', label: 'Every (days)', type: 'number', min: 1, max: 366, value: 30, required: true },
      ],
    }, (v, reason) => command('/api/inventory/count-programs', { ...v, locationCode: v.locationCode || null, partNumber: v.partNumber || null, frequencyDays: Number(v.frequencyDays) }, reason), reload),

    location: () => formModal({
      title: 'Add a location', detail: 'Where it sits, what it serves and who has custody. Stock at an outside processor or in transit never counts for planning.', confirmLabel: 'Add',
      fields: [
        { name: 'code', label: 'Code (permanent)', code: true, required: true, placeholder: 'STORES-A' },
        { name: 'name', label: 'Name', required: true, placeholder: 'Main stores, aisle A' },
        { name: 'kind', label: 'Kind', type: 'select', options: LOCATION_KINDS.map(k => [k, KIND_NAME[k] ?? k]) },
        { name: 'siteCode', label: 'Site', type: 'select', options: data.sites.map(s => [s.code, s.name]), required: true },
        ...placeFields({}),
        { name: 'nettable', label: 'Counts for planning', type: 'select', options: [['', 'Default for the kind'], ['true', 'Yes'], ['false', 'No']] },
      ],
    }, (v, reason) => command('/api/inventory/locations', locationBody(v), reason), reload),
  };

  function kpis() {
    const s = data.summary.data ?? [];
    const pending = data.requests.data ?? [];
    const tile = (icon, title, value, why) => `<div class="kpi">${img(icon)}<div><div class="t">${esc(title)}</div><div class="v">${value}</div><div class="why">${esc(why)}</div></div></div>`;
    return `<div class="kpis kpis-4">
      ${tile('inventory', 'Items with stock or supply', s.length, 'Parts with stock, allocations, open supply or safety stock.')}
      ${tile('alert', 'Below safety stock', s.filter(x => x.belowSafetyStock).length, 'Available for planning (released, in a nettable location, less allocated) under safety stock.')}
      ${tile('quality', 'Inspection or hold', s.filter(x => x.inspection > 0 || x.hold > 0).length, 'Parts with stock that cannot be used yet.')}
      ${tile('complete', 'Waiting for approval', pending.length, 'Adjustments and count variances for a second person.')}
    </div>`;
  }

  function stockTab() {
    const s = data.summary;
    if (s.error) return errorBlock(s.error, 'stock');
    if (!s.data.length) return emptyBlock('No stock yet', 'Add a location, then receive material. Every quantity here will be explained by its receipts, issues and moves.');
    return `<table class="table"><thead><tr><th>Part</th><th class="num">On hand</th><th class="num">Released</th><th class="num">Inspection</th><th class="num">Hold</th>
      <th class="num" title="Released, but at an outside processor, in transit or in quarantine">Not counted</th><th class="num" title="Released stock at a work center's point of use">At point of use</th>
      <th class="num">Allocated</th><th class="num">Available for planning</th><th class="num">On order</th><th class="num">In production</th><th class="num">Safety stock</th></tr></thead><tbody>
      ${s.data.map(x => `<tr class="click" data-part="${esc(x.partNumber)}">
        <td><b>${esc(x.partNumber)}</b> <span class="faint">${esc(x.unitOfMeasure)} · ${esc(x.makeBuy)}</span><br><span class="muted">${esc(x.description)}</span></td>
        <td class="num">${qty(x.onHand)}</td><td class="num">${qty(x.released)}</td>
        <td class="num">${x.inspection ? `<span class="c-info">${qty(x.inspection)}</span>` : '—'}</td><td class="num">${x.hold ? `<span class="c-warn">${qty(x.hold)}</span>` : '—'}</td>
        <td class="num">${x.notNettable ? qty(x.notNettable) : '—'}</td><td class="num">${x.atPointOfUse ? qty(x.atPointOfUse) : '—'}</td>
        <td class="num" title="Required by released orders and not yet issued">${qty(x.allocated)}</td>
        <td class="num ${x.availableForPlanning < 0 ? 'c-bad' : x.belowSafetyStock ? 'c-warn' : ''}"><b>${qty(x.availableForPlanning)}</b></td>
        <td class="num">${qty(x.onOrder)}</td><td class="num">${qty(x.inProduction)}</td>
        <td class="num">${x.safetyStock ? qty(x.safetyStock) : '—'}${x.belowSafetyStock ? ' <span class="pill warn">below</span>' : ''}</td></tr>`).join('')}
      </tbody></table>
      <div class="faint" style="margin-top:6px">On hand is everything in the ledger. Released is what quality has released. Available for planning is released stock in locations that count for planning, less what released orders still need. At point of use is released stock beside a machine or staged at its work center.</div>`;
  }

  const ledgerRow = t => `<tr>
    <td class="nowrap">${esc(fmtDateTime(t.at))}</td><td>${esc(KIND_LABEL[t.kind] ?? t.kind)}</td><td>${esc(t.partNumber)}${t.partRevision ? ` <span class="faint">rev ${esc(t.partRevision)}</span>` : ''}${t.lotNumber ? ` <span class="faint">lot ${esc(t.lotNumber)}</span>` : ''}</td>
    <td class="num">${qty(t.quantity)}</td>
    <td>${t.fromLocation ? `${esc(t.fromLocation)} <span class="faint">${esc(t.fromStatus)}</span>` : '<span class="faint">—</span>'} → ${t.toLocation ? `${esc(t.toLocation)} <span class="faint">${esc(t.toStatus)}</span>` : '<span class="faint">out</span>'}</td>
    <td>${t.orderNumber ? `<a href="#/orders/${t.orderNumber}">#${t.orderNumber}</a>` : ''}${t.poNumber ? `PO ${t.poNumber}/${t.poLine}` : ''}${t.substitutesFor ? ` <span class="faint">for ${esc(t.substitutesFor)}</span>` : ''}${t.workOrderId ? ` WO ${t.workOrderId}` : ''}${t.projectCode ? ` <a href="#/projects/${encodeURIComponent(t.projectCode)}">${esc(t.projectCode)}</a> line ${t.projectLine}` : ''}
      ${t.poNumber || t.lotNumber ? ` <button class="btn small ghost" data-why-txn="${t.transactionId}|${esc(t.partNumber)}" title="Why was this stock purchased?">Why bought?</button>` : ''}</td>
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
    if (!l.data.length) return emptyBlock('No locations yet', inventoryManager ? 'Add the places stock is kept: stores, receiving, inspection, quarantine, staging, beside machines, outside processors.' : 'An inventory controller adds locations.');
    const byParent = new Map();
    for (const x of l.data) { const k = x.parentCode ?? ''; byParent.set(k, [...(byParent.get(k) ?? []), x]); }
    const known = new Set(l.data.map(x => x.code));
    const ordered = [];
    const walk = (parent, depth) => (byParent.get(parent) ?? []).forEach(x => { ordered.push([x, depth]); if (depth < 8) walk(x.code, depth + 1); });
    walk('', 0);
    l.data.filter(x => x.parentCode && !known.has(x.parentCode)).forEach(x => ordered.push([x, 0]));
    return `<table class="table"><thead><tr><th>Code</th><th>Name</th><th>Kind</th><th>Level</th><th>Counts for planning</th><th>Point of use</th><th>Custody</th><th>Site</th><th></th></tr></thead><tbody>
      ${ordered.map(([x, depth]) => `<tr class="${x.active ? '' : 'retired'}"><td class="mono" style="padding-left:${8 + depth * 18}px">${depth ? '└ ' : ''}${esc(x.code)}</td><td>${esc(x.name)}</td><td>${esc(KIND_NAME[x.kind] ?? x.kind)}</td><td>${esc(x.level)}</td>
        <td>${x.nettable ? 'Yes' : '<span class="pill none">No</span>'}</td>
        <td>${x.pointOfUse ? `${esc(x.workCenterCode)}${x.machineCode ? ` · ${esc(x.machineCode)}` : ''}` : x.workCenterCode ? `<span class="faint">${esc(x.workCenterCode)}</span>` : '—'}</td>
        <td>${x.custody === 'Company' ? 'Company' : `<span class="pill warn">${esc(custodyText(x))}</span>`}</td><td>${esc(x.siteCode)}</td>
        <td class="nowrap">${!x.active ? '<span class="pill none">Retired</span>' : inventoryManager ? `<button class="btn small" data-change-loc="${esc(x.code)}">Change</button> <button class="btn small ghost" data-retire="${esc(x.code)}">Retire</button>` : ''}</td></tr>`).join('')}
      </tbody></table>`;
  }

  const TASK_NAME = { Stage: 'Stage', Kit: 'Kit', PutAway: 'Put away', Replenish: 'Replenish' };
  function tasksTab() {
    const t = data.tasks;
    if (t.error) return errorBlock(t.error, 'tasks');
    if (!t.data.length) return emptyBlock('No open tasks', 'Request material for an order (or from the order page) and it appears here to allocate, pick and deliver.');
    const step = x => x.status === 'Requested' ? `<button class="btn small primary" data-task-allocate="${x.taskId}">Allocate</button>`
      : x.status === 'Allocated' ? `<button class="btn small primary" data-task-pick="${x.taskId}">Pick (scan)</button>`
      : x.status === 'Picked' ? `<button class="btn small primary" data-task-deliver="${x.taskId}">Deliver (scan)</button>` : '';
    return `<table class="table"><thead><tr><th>#</th><th>Task</th><th>For</th><th>Part</th><th class="num">Qty</th><th>From → To</th><th>Status</th><th>Requested</th><th></th></tr></thead><tbody>
      ${t.data.map(x => `<tr><td>${x.taskId}</td><td>${esc(TASK_NAME[x.kind] ?? x.kind)}${x.kitCode ? ` <span class="faint">kit ${esc(x.kitCode)}</span>` : ''}</td>
        <td>${x.orderNumber ? `<a href="#/orders/${x.orderNumber}">#${x.orderNumber}</a>` : '—'}</td><td>${esc(x.partNumber)}${x.lotNumber ? ` <span class="faint">lot ${esc(x.lotNumber)}</span>` : ''}</td>
        <td class="num">${qty(x.quantity)}${x.pickedQuantity != null && x.pickedQuantity < x.quantity ? ` <span class="c-warn">picked ${qty(x.pickedQuantity)}</span>` : ''}</td>
        <td>${esc(x.fromLocation ?? '?')} → ${esc(x.toLocation)}</td><td><span class="pill info">${esc(x.status)}</span></td>
        <td>${esc(who(x.requestedBy))}<br><span class="faint">${esc(fmtDateTime(x.requestedAt))}</span></td>
        <td class="nowrap">${handler ? `${step(x)} <button class="btn small ghost" data-task-cancel="${x.taskId}">Cancel</button>` : ''}</td></tr>`).join('')}
      </tbody></table>
      <div class="faint" style="margin-top:6px">Allocated stock is held back from other tasks. Picking and delivering are confirmed by scanning the location (and lot); only delivery moves stock in the ledger.</div>`;
  }

  function countsTab() {
    const p = data.programs, d = data.due;
    if (p.error) return errorBlock(p.error, 'count programs');
    const add = inventoryManager ? '<div class="toolbar"><button class="btn small" data-act="program">+ Count program</button></div>' : '';
    const programs = p.data.length ? `<table class="table"><thead><tr><th>Program</th><th>Location</th><th>Part</th><th class="num">Every (days)</th><th>Set up by</th><th></th></tr></thead><tbody>
      ${p.data.map(x => `<tr class="${x.active ? '' : 'retired'}"><td><b>${esc(x.programCode)}</b> ${esc(x.name)}</td><td>${esc(x.locationCode ?? 'every')}</td><td>${esc(x.partNumber ?? 'every')}</td><td class="num">${x.frequencyDays}</td>
        <td class="faint">${esc(who(x.createdBy))} · ${esc(fmtDateTime(x.createdAt))}</td><td>${!x.active ? '<span class="pill none">Retired</span>' : inventoryManager ? `<button class="btn small ghost" data-retire-program="${esc(x.programCode)}">Retire</button>` : ''}</td></tr>`).join('')}</tbody></table>`
      : emptyBlock('No count programs', 'Say what is counted how often; due counts then appear here.');
    const dueRows = d.error ? errorBlock(d.error, 'counts due') : !d.data.length ? '<p class="muted">Nothing is due for counting.</p>' : `<table class="table"><thead><tr><th>Due</th><th>Program</th><th>Location</th><th>Part</th><th>Lot</th><th>Status</th><th class="num">On record</th><th>Last counted</th><th></th></tr></thead><tbody>
      ${d.data.map((x, i) => `<tr><td>${esc(x.dueOn)}</td><td>${esc(x.programCode)}</td><td>${esc(x.locationCode)}</td><td>${esc(x.partNumber)}</td><td>${esc(x.lotNumber ?? '—')}</td><td>${esc(x.status)}</td><td class="num">${qty(x.quantity)}</td>
        <td>${x.lastCountedAt ? esc(fmtDateTime(x.lastCountedAt)) : '<span class="faint">never</span>'}</td><td>${handler ? `<button class="btn small" data-count-due="${i}">Count</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
    return `${add}${programs}<h3>Due for counting</h3>${dueRows}`;
  }

  // MRO stocking levels (V2.8): at or below the reorder point MRP plans a purchase up to the maximum.
  function mroTab() {
    if (data.mro.error) return errorBlock(data.mro.error, 'MRO levels');
    const rows = data.mro.data;
    const add = mroManager ? '<div class="toolbar"><button class="btn small" data-mro-set="">Set MRO levels for an item</button></div>' : '';
    if (!rows.length) return add + emptyBlock('No MRO levels set', 'Give spares and consumables a reorder point and maximum; MRP then plans their purchases.');
    return `${add}<table class="table"><thead><tr><th>Part</th><th class="num">Available</th><th class="num">Minimum</th><th class="num">Reorder point</th><th class="num">Maximum</th><th>Critical</th><th>Shelf life</th><th>Set by</th><th></th></tr></thead><tbody>
      ${rows.map(r => { const p = r.policy; return `<tr><td><b>${esc(p.partNumber)}</b> <span class="muted">${esc(r.description)}</span></td>
        <td class="num ${r.belowMinimum ? 'c-bad' : r.atOrBelowReorderPoint ? 'c-warn' : ''}">${qty(r.available)} ${esc(r.unitOfMeasure)}</td>
        <td class="num">${qty(p.minQuantity)}</td><td class="num">${qty(p.reorderPoint)}</td><td class="num">${qty(p.maxQuantity)}</td>
        <td>${p.criticalSpare ? '<span class="pill bad">Critical</span>' : ''}${r.belowMinimum ? ' <span class="pill bad">Below minimum</span>' : r.atOrBelowReorderPoint ? ' <span class="pill warn">Reorder</span>' : ''}</td>
        <td>${p.shelfLifeDays ? `${p.shelfLifeDays} d` : '—'}</td><td class="faint">${esc(who(p.setBy))} · ${esc(fmtDateTime(p.setAt))}<br>${esc(p.reason)}</td>
        <td class="nowrap">${mroManager ? `<button class="btn small" data-mro-set="${esc(p.partNumber)}">Change</button> <button class="btn small ghost" data-mro-clear="${esc(p.partNumber)}">Clear</button>` : ''}</td></tr>`; }).join('')}
      </tbody></table>`;
  }

  function setMro(part) {
    const current = (data.mro.data ?? []).find(r => r.policy.partNumber === part)?.policy;
    const buy = (data.items.data ?? []).filter(i => i.makeBuy === 'Buy').map(i => [i.partNumber, `${i.partNumber} — ${i.description}`]);
    return formModal({ title: part ? `MRO levels for ${part}` : 'Set MRO levels', detail: 'At or below the reorder point (on hand plus on order, less what is needed), MRP plans a purchase up to the maximum. Every setting is kept.', confirmLabel: 'Save', reasonRequired: true,
      fields: [
        part ? { name: 'part', label: 'Part', value: part, readonly: true } : { name: 'part', label: 'Part', type: 'select', options: buy, required: true },
        { name: 'minQuantity', label: 'Minimum', type: 'number', min: 0, step: 'any', value: current?.minQuantity ?? 0, required: true },
        { name: 'reorderPoint', label: 'Reorder point', type: 'number', min: 0, step: 'any', value: current?.reorderPoint, required: true },
        { name: 'maxQuantity', label: 'Maximum', type: 'number', min: 0, step: 'any', value: current?.maxQuantity, required: true },
        { name: 'criticalSpare', type: 'check', label: 'Critical spare', text: 'Report when projected below the minimum', value: current?.criticalSpare ?? false },
        { name: 'shelfLifeDays', label: 'Shelf life (days, optional)', type: 'number', min: 1, value: current?.shelfLifeDays },
      ] },
    (v, reason) => command(`/api/inventory/mro/${encodeURIComponent(v.part)}`, v, reason), reload);
  }

  function render() {
    const pending = data.requests.data?.length ?? 0;
    const buttons = [
      handler && ['receive', 'Receive'], handler && ['issue', 'Issue to order'], handler && ['return', 'Return'], handler && ['move', 'Move'],
      handler && ['produced', 'From production'], handler && ['split', 'Split'], handler && ['merge', 'Merge'], handler && ['request', 'Request material'], handler && ['hold', 'Hold'], controller && ['release', 'Release'], handler && ['scrap', 'Scrap'],
      handler && ['count', 'Count'], handler && ['adjust', 'Adjust'], inventoryManager && ['location', '+ Location'],
    ].filter(Boolean);
    page.innerHTML = `
      <div class="page-head">
        <div><h1>Inventory</h1><div class="sub">Every quantity is the sum of its ledger entries. Adjustments, count variances and quality releases need a second person.</div></div>
        <div class="spacer"></div><form id="serial-find" class="inline"><input class="input mono" id="serial-q" placeholder="Serial or lot" style="width:160px"> <button class="btn">Genealogy</button> <button class="btn" type="button" id="trace-q">Trace</button></form>
      </div>
      ${buttons.length ? `<div class="toolbar">${buttons.map(([k, l]) => `<button class="btn ${k === 'receive' ? 'primary' : ''}" data-act="${k}">${esc(l)}</button>`).join('')}</div>` : '<div class="notice">You can look at stock. Material handlers move it; inventory controllers approve changes.</div>'}
      ${kpis()}
      <section class="panel">
        <header><div class="seg" id="tabs">${[['stock', 'Stock'], ['tasks', `Tasks${data.tasks.data?.length ? ` (${data.tasks.data.length})` : ''}`], ['ledger', 'Ledger'], ['approvals', `Approvals${pending ? ` (${pending})` : ''}`], ['locations', 'Locations'], ['counts', `Counts${data.due.data?.length ? ` (${data.due.data.length} due)` : ''}`], ['shelf', `Shelf life${data.shelf.pending.data?.length ? ` (${data.shelf.pending.data.length})` : ''}`], ['receiving', `Receiving${receivingCount(data.receiving) ? ` (${receivingCount(data.receiving)})` : ''}`], ['outside', 'Outside processing'], ['mro', 'MRO levels']].map(([k, l]) => `<button data-tab="${k}" class="${tab === k ? 'on' : ''}">${esc(l)}</button>`).join('')}</div></header>
        ${tab === 'outside' ? outsideProcessingTab(data.outside, me) : tab === 'tasks' ? tasksTab() : tab === 'counts' ? countsTab() : tab === 'ledger' ? ledgerTab() : tab === 'approvals' ? approvalsTab() : tab === 'locations' ? locationsTab() : tab === 'mro' ? mroTab() : tab === 'shelf' ? shelfLifeTab(data.shelf, me) : tab === 'receiving' ? receivingTab(data.receiving, me) : stockTab()}
      </section>`;
    $('#trace-q', page)?.addEventListener('click', () => { const q = $('#serial-q', page).value.trim(); if (q) traceDrawer({ lot: q.toUpperCase() }); });
    $('#serial-find', page)?.addEventListener('submit', ev => { ev.preventDefault(); const q = $('#serial-q', page).value.trim(); if (q) serialDrawer(q.toUpperCase()); });

    $$('[data-tab]', page).forEach(b => b.addEventListener('click', () => { tab = b.dataset.tab; render(); }));
    $$('[data-act]', page).forEach(b => b.addEventListener('click', () => {
      if (!(data.locations.data ?? []).length && b.dataset.act !== 'location') { toast('Add a location first (inventory controller).', true); return; }
      actions[b.dataset.act]({});
    }));
    $$('[data-part]', page).forEach(r => r.addEventListener('click', () => partDrawer(r.dataset.part)));
    if (tab === 'shelf') bindShelfLifeTab(page, locs(true, false), reload);
    if (tab === 'receiving') bindReceivingTab(page, locs(), reload, me);
    if (tab === 'outside') bindOutsideProcessingTab(page, data.locations.data ?? [], reload, me);
    $$('[data-mro-set]', page).forEach(b => b.addEventListener('click', () => setMro(b.dataset.mroSet)));
    $$('[data-mro-clear]', page).forEach(b => b.addEventListener('click', async () => {
      const done = await confirmWithReason({ title: `Clear MRO levels for ${b.dataset.mroClear}`, detail: 'MRP stops replenishing it to a maximum. The old levels stay in the history.', confirmLabel: 'Clear' },
        reason => command(`/api/inventory/mro/${encodeURIComponent(b.dataset.mroClear)}/clear`, {}, reason));
      if (done) { toast(done.message); await reload(); }
    }));
    $$('[data-why-txn]', page).forEach(b => b.addEventListener('click', async () => {
      const [id, part] = b.dataset.whyTxn.split('|');
      const { data: w, error } = await tryGet(`/api/inventory/transactions/${id}/why?part=${encodeURIComponent(part)}`);
      drawer(`Why was this ${part} purchased?`, error ? errorBlock(error, 'the purchase') : `<p>${esc(w.answer)}</p>
        ${w.purchases.map(p => `<div class="notice" style="margin-top:8px">PO ${p.poNumber} line ${p.lineNumber} · ${esc(p.supplier)}${p.sourceKey ? ` · for <b>${esc(p.sourceKey)}</b>` : ''}</div>`).join('')}`);
    }));
    const task = id => data.tasks.data.find(x => x.taskId === Number(id));
    $$('[data-task-allocate]', page).forEach(b => b.addEventListener('click', () => { const x = task(b.dataset.taskAllocate); formModal({
      title: `Allocate task ${x.taskId}`, detail: `${qty(x.quantity)} ${x.partNumber} to ${x.toLocation}. The stock is held for this task until it is delivered or cancelled.`, confirmLabel: 'Allocate',
      fields: [{ name: 'fromLocation', label: 'From', type: 'select', options: locs(), required: true }, { ...lotField, value: x.lotNumber ?? '' }],
    }, (v, reason) => command(`/api/inventory/tasks/${x.taskId}/allocate`, { ...v, lotNumber: v.lotNumber || null }, reason), reload); }));
    $$('[data-task-pick]', page).forEach(b => b.addEventListener('click', () => { const x = task(b.dataset.taskPick); formModal({
      title: `Pick task ${x.taskId}`, detail: `Go to ${x.fromLocation} and scan it${x.lotNumber ? `, then scan lot ${x.lotNumber}` : ''}. Pick less if the location is short.`, confirmLabel: 'Picked',
      fields: [{ name: 'scannedLocation', label: 'Scan the location', code: true, required: true }, ...(x.lotNumber ? [{ name: 'scannedLot', label: 'Scan the lot', code: true, required: true }] : []),
        { name: 'quantity', label: 'Quantity picked', type: 'number', step: 'any', min: 0, value: x.quantity, required: true }],
    }, (v, reason) => command(`/api/inventory/tasks/${x.taskId}/pick`, { ...v, quantity: Number(v.quantity) }, reason), reload); }));
    $$('[data-task-deliver]', page).forEach(b => b.addEventListener('click', () => { const x = task(b.dataset.taskDeliver); formModal({
      title: `Deliver task ${x.taskId}`, detail: `Take ${qty(x.pickedQuantity)} ${x.partNumber} to ${x.toLocation} and scan it there.`, confirmLabel: 'Delivered',
      fields: [{ name: 'scannedLocation', label: 'Scan the destination', code: true, required: true }],
    }, (v, reason) => command(`/api/inventory/tasks/${x.taskId}/deliver`, v, reason), reload); }));
    $$('[data-task-cancel]', page).forEach(b => b.addEventListener('click', async () => {
      const done = await confirmWithReason({ title: `Cancel task ${b.dataset.taskCancel}`, detail: 'Anything allocated is free again. Nothing moves.', confirmLabel: 'Cancel task' },
        reason => command(`/api/inventory/tasks/${b.dataset.taskCancel}/cancel`, {}, reason));
      if (done) { toast(done.message); await reload(); }
    }));
    $$('[data-retire-program]', page).forEach(b => b.addEventListener('click', async () => {
      const done = await confirmWithReason({ title: `Retire count program ${b.dataset.retireProgram}`, detail: 'Its balances are no longer due under it. Counts already recorded stay.', confirmLabel: 'Retire' },
        reason => command(`/api/inventory/count-programs/${encodeURIComponent(b.dataset.retireProgram)}/retire`, {}, reason));
      if (done) { toast(done.message); await reload(); }
    }));
    $$('[data-count-due]', page).forEach(b => b.addEventListener('click', () => { const x = data.due.data[Number(b.dataset.countDue)]; formModal({
      title: `Count ${x.partNumber} at ${x.locationCode}`, detail: 'Count what is physically there. A variance waits for a second person.', confirmLabel: 'Record count',
      fields: [{ name: 'counted', label: 'Counted quantity', type: 'number', step: 'any', min: 0, required: true }],
    }, (v, reason) => command('/api/inventory/counts', { partNumber: x.partNumber, locationCode: x.locationCode, lotNumber: x.lotNumber, status: x.status, counted: Number(v.counted) }, reason), reload); }));
    $$('[data-approve]', page).forEach(b => b.addEventListener('click', () => decide(b.dataset.approve, 'approve')));
    $$('[data-reject]', page).forEach(b => b.addEventListener('click', () => decide(b.dataset.reject, 'reject')));
    $$('[data-change-loc]', page).forEach(b => b.addEventListener('click', () => changeLocation(data.locations.data.find(l => l.code === b.dataset.changeLoc))));
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
    const [stock, ledger, lots, policy] = await Promise.all([tryGet(`/api/inventory/positions?part=${encodeURIComponent(part)}`), tryGet(`/api/inventory/transactions?part=${encodeURIComponent(part)}&take=100`),
      tryGet(`/api/inventory/lots?part=${encodeURIComponent(part)}`), tryGet(`/api/inventory/merge-policy/${encodeURIComponent(part)}`)]);
    const tolerance = ((await tryGet('/api/inventory/yield-tolerances')).data ?? []).find(t => t.partNumber === part);
    const kindOf = lot => (lots.data ?? []).find(l => l.lotNumber === lot)?.identityKind;
    const divisible = item(part)?.lotTracked && !item(part)?.serialized;
    const p = policy.data;
    d.element.innerHTML = `
      <h3 style="margin:0">Where it is</h3>
      ${stock.error ? errorBlock(stock.error, 'stock') : stock.data.length ? `<table class="table"><thead><tr><th>Location</th><th>Instance</th><th>Quality</th><th>Custody</th><th>Point of use</th><th>Planning</th><th class="num">Qty</th></tr></thead><tbody>
        ${stock.data.map(b => `<tr><td>${esc(b.locationPath)}</td><td>${b.lotNumber ? `${esc(b.lotNumber)} <span class="faint">${esc(kindOf(b.lotNumber) ?? '')}</span> <button class="btn small ghost" data-trace="${esc(b.lotNumber)}">Trace</button> <button class="btn small ghost" data-lineage="${esc(b.lotNumber)}">Splits</button>${handler && divisible && b.custody === 'Company' ? ` <button class="btn small ghost" data-split='${esc(JSON.stringify({ partNumber: part, lotNumber: b.lotNumber, locationCode: b.locationCode, status: b.qualityStatus }))}'>Split</button>` : ''}` : '—'}</td><td><span class="pill ${STATUS_TONE[b.qualityStatus]}">${esc(b.qualityStatus)}</span></td>
          <td>${b.custody === 'Company' ? 'Company' : `<span class="pill warn">${esc(custodyText(b))}</span>`}</td><td>${esc(b.pointOfUseWorkCenter ? `${b.pointOfUseWorkCenter}${b.machineCode ? ` · ${b.machineCode}` : ''}` : '—')}</td>
          <td>${b.availableForPlanning ? 'Counts' : '<span class="faint">Not counted</span>'}</td><td class="num">${qty(b.quantity)}</td></tr>`).join('')}</tbody></table>` : '<div class="muted">None on hand.</div>'}
      ${divisible ? `<div class="notice" style="margin-top:8px">Merging lots: ${p?.allowed ? `allowed${p.sameRevisionRequired ? ', within one revision' : ''}${p.sameSupplierLotRequired ? ', within one supplier lot' : ''}` : 'never'}${p?.setBy && p.setBy !== 'system' ? ` <span class="faint">· set by ${esc(who(p.setBy))}: ${esc(p.reason)}</span>` : ''}
        ${controller ? ' <button class="btn small" data-merge-policy>Change</button>' : ''}</div>` : ''}
      <div class="notice" style="margin-top:8px">Yield tolerance: ${tolerance ? `±${qty(tolerance.percent)}% of what is issued <span class="faint">· set by ${esc(who(tolerance.setBy))}: ${esc(tolerance.reason)}</span>` : 'none — issued and accounted must match exactly'}
        ${controller ? ' <button class="btn small" data-yield>Change</button>' : ''}</div>
      <div id="shelf-life"></div>
      <div id="lineage"></div>
      <h3 style="margin:0">How it got there</h3>
      ${ledger.error ? errorBlock(ledger.error, 'ledger') : ledger.data.length ? ledgerTable(ledger.data) : '<div class="muted">No transactions.</div>'}
      <div class="faint"><a href="#/material-plan?part=${encodeURIComponent(part)}">Open the material plan for ${esc(part)} →</a></div>`;
    $$('[data-trace]', d.element).forEach(b => b.addEventListener('click', () => traceDrawer({ part, lot: b.dataset.trace })));
    $$('[data-split]', d.element).forEach(b => b.addEventListener('click', () => actions.split(JSON.parse(b.dataset.split))));
    appendPartShelfLife(d.element.querySelector('#shelf-life'), part, item(part), me, async () => { await reload(); partDrawer(part); });
    $$('[data-lineage]', d.element).forEach(b => b.addEventListener('click', async () => {
      const { data: g, error } = await tryGet(`/api/inventory/lots/${encodeURIComponent(part)}/${encodeURIComponent(b.dataset.lineage)}/lineage`);
      const link = (l, other) => `<li>${esc(l.relation === 'Split' ? 'Split' : 'Merged')} ${qty(l.quantity)} ${esc(other)} <span class="faint">${esc(fmtDateTime(l.at))} · ${esc(who(l.actor))}${l.reason ? ` · ${esc(l.reason)}` : ''}</span></li>`;
      $('#lineage', d.element).innerHTML = error ? errorBlock(error, 'the lineage') : `<h3 style="margin:8px 0 0">Lineage of ${esc(g.lotNumber)}</h3>
        ${g.sources.length ? `<div>Came from:</div><ul>${g.sources.map(l => link(l, `from ${l.sourceLot}`)).join('')}</ul>` : '<div class="muted">Not split or merged from another instance.</div>'}
        ${g.results.length ? `<div>Became:</div><ul>${g.results.map(l => link(l, `into ${l.resultLot}`)).join('')}</ul>` : ''}
        ${g.origins.length ? `<div class="faint">First instances: ${g.origins.map(esc).join(', ')}</div>` : ''}`;
    }));
    $('[data-yield]', d.element)?.addEventListener('click', () => formModal({
      title: `Yield tolerance for ${part}`, detail: 'How far issued and accounted for (consumed, returned, scrap, loss, remnant, remaining) may differ before the difference is unexplained. Every setting is kept.', confirmLabel: 'Save', reasonRequired: true,
      fields: [{ name: 'percent', label: 'Percent of what is issued (0 to 50)', type: 'number', min: 0, max: 50, step: 'any', value: tolerance?.percent ?? 0, required: true }],
    }, (v, reason) => command(`/api/inventory/yield-tolerance/${encodeURIComponent(part)}`, { percent: Number(v.percent) }, reason), async () => { await reload(); partDrawer(part); }));
    $('[data-merge-policy]', d.element)?.addEventListener('click', () => formModal({
      title: `Merge policy for ${part}`, detail: 'Merged lots stop being separately traceable. Every setting is kept with who set it and why.', confirmLabel: 'Save', reasonRequired: true,
      fields: [
        { name: 'allowed', type: 'check', label: 'Merging', text: 'Lots of this part may be merged', value: p?.allowed ?? false },
        { name: 'sameRevisionRequired', type: 'check', label: 'Revision', text: 'Only lots of the same revision', value: p?.sameRevisionRequired ?? true },
        { name: 'sameSupplierLotRequired', type: 'check', label: 'Supplier lot', text: 'Only lots from the same supplier lot', value: p?.sameSupplierLotRequired ?? true },
      ],
    }, (v, reason) => command(`/api/inventory/merge-policy/${encodeURIComponent(part)}`, v, reason), async () => { await reload(); partDrawer(part); }));
  }

  await load();
  render();
}

