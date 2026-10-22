import { tryGet, command } from '../api.js';
import { img } from '../icons.js';
import { esc, $, $$, fmtDate, errorBlock, emptyBlock, drawer, confirmWithReason, toast, enhanceTables } from '../ui.js';
import { formModal, qty, canAction, plusDays, csvImportModal } from '../forms.js';

// Material Plan (spec §12-13): independent demand, item planning parameters and a deterministic MRP
// whose every recommendation shows its arithmetic and the requirements it is pegged to.

const EXCEPTION_LABEL = {
  PastDueRelease: 'Release past due', RescheduleIn: 'Bring receipt in', OverdueReceipt: 'Receipt overdue', PastDueRequirement: 'Requirement past due',
  NoRevision: 'No released revision', BomLoop: 'BOM loop', UnknownItem: 'Unknown item',
};
const DEMAND_KINDS = [['CustomerOrder', 'Customer order'], ['Forecast', 'Forecast'], ['ContractDelivery', 'Contract delivery'], ['Service', 'Service / spares'], ['Internal', 'Internal'],
  ['Maintenance', 'Maintenance'], ['MroReplenishment', 'MRO replenishment'], ['FacilityProject', 'Facility project'], ['CapitalProject', 'Capital project'], ['Quality', 'Quality'], ['Tooling', 'Tooling'], ['Engineering', 'Engineering']];
const POLICIES = [['LotForLot', 'Lot-for-lot (exactly what is needed)'], ['FixedQuantity', 'Fixed quantity (whole lots)'], ['MinimumQuantity', 'Minimum quantity']];

export async function mrpPage(page, ctx) {
  const me = ctx.me;
  const planner = canAction(me, 'planning.demand.manage');
  const engineer = canAction(me, 'execution.orders.manage');
  const buyer = canAction(me, 'procurement.manage');
  let tab = ctx.query.get('tab') ?? 'planned';
  let plannedFilter = 'all';
  let itemFilter = 'all';
  const focus = ctx.query.get('part');
  let data = {};

  async function load() {
    const [mrp, demand, items, suppliers, sources] = await Promise.all([
      tryGet('/api/planning/mrp'), tryGet('/api/planning/demand'), tryGet('/api/engineering/items'),
      tryGet('/api/procurement/suppliers'), tryGet('/api/procurement/approved-sources'),
    ]);
    data = { mrp, demand, items, suppliers, sources };
  }
  const reload = async () => { await load(); render(); };
  const out = () => data.mrp.data?.output;
  const itemPlan = part => out()?.items.find(i => i.partNumber === part);
  const itemInfo = part => (data.items.data ?? []).find(i => i.partNumber === part);

  function kpis() {
    const o = out();
    const tile = (icon, title, value, why) => `<div class="kpi">${img(icon)}<div><div class="t">${esc(title)}</div><div class="v">${value}</div><div class="why">${esc(why)}</div></div></div>`;
    return `<div class="kpis kpis-4">
      ${tile('planning', 'Planned orders', o?.plannedOrders.length ?? '—', 'Recommended make and buy orders that cover every requirement.')}
      ${tile('alert', 'Past-due releases', o?.plannedOrders.filter(p => p.pastDue).length ?? '—', 'Should already have been released to meet the need date.')}
      ${tile('trend', 'Exceptions', o?.exceptions.length ?? '—', 'Receipts to bring in, overdue receipts and data problems.')}
      ${tile('orders', 'Open demand', data.demand.data?.length ?? '—', 'Customer orders, forecasts and other independent demand.')}
    </div>`;
  }

  function plannedTab() {
    if (data.mrp.error) return errorBlock(data.mrp.error, 'the material plan');
    const list = out().plannedOrders ?? [];
    if (!list.length) return emptyBlock('Nothing to order', 'Stock, open purchase orders and production orders cover every requirement and safety stock.');
    const pastDueCount = list.filter(p => p.pastDue).length;
    const makeCount = list.filter(p => p.makeBuy === 'Make').length;
    const buyCount = list.filter(p => p.makeBuy === 'Buy').length;
    const filters = [['all', `All (${list.length})`], ['pastDue', `Past due (${pastDueCount})`], ['Make', `Make (${makeCount})`], ['Buy', `Buy (${buyCount})`]];
    const indexed = list.map((p, index) => ({ p, index })).filter(({ p }) =>
      plannedFilter === 'pastDue' ? p.pastDue
        : plannedFilter === 'Make' ? p.makeBuy === 'Make'
          : plannedFilter === 'Buy' ? p.makeBuy === 'Buy' : true);
    const chips = `<div class="mrp-filters" role="group" aria-label="Filter recommendations">${filters.map(([key, label]) =>
      `<button class="mrp-filter ${plannedFilter === key ? 'on' : ''}" type="button" data-planned-filter="${key}" aria-pressed="${plannedFilter === key}">${esc(label)}</button>`).join('')}</div>`;
    if (!indexed.length) return `${chips}${emptyBlock('No recommendations match', 'Choose another recommendation filter.')}`;
    return `${chips}<table class="table"><thead><tr><th>Release by</th><th>Need by</th><th>Part</th><th></th><th class="num">Quantity</th><th>Covers</th><th></th></tr></thead><tbody>
      ${indexed.map(({ p, index: i }) => `<tr>
        <td class="nowrap">${p.pastDue ? '<span class="pill bad">Past due</span> ' : ''}${esc(fmtDate(p.releaseDate))}</td><td class="nowrap">${esc(fmtDate(p.dueDate))}</td>
        <td><b>${esc(p.partNumber)}</b>${p.revision ? ` <span class="faint">rev ${esc(p.revision)}</span>` : ''}</td>
        <td><span class="pill ${p.makeBuy === 'Make' ? 'info' : 'none'}">${p.makeBuy === 'Make' ? 'Make' : 'Buy'}</span></td>
        <td class="num"><b>${qty(p.quantity)}</b> <span class="faint">${esc(p.unitOfMeasure)}</span></td>
        <td class="muted">${esc(p.pegging.map(g => g.description).filter((v, j, a) => a.indexOf(v) === j).slice(0, 2).join('; '))}${p.pegging.length > 2 ? '…' : ''}</td>
        <td class="nowrap"><button class="btn small" data-why="${i}">Why?</button>
          ${p.makeBuy === 'Buy' ? `<button class="btn small ghost" data-alts="${esc(p.partNumber)}" title="Approved alternates and those requiring approval, kept apart">Alternates</button>` : ''}
          ${p.makeBuy === 'Buy' && canAction(me, 'procurement.requisition.raise') ? `<button class="btn small" data-req="${i}" title="A requisition that keeps what this purchase is for">Requisition</button>` : ''}
          ${p.makeBuy === 'Buy' && buyer ? `<button class="btn small primary" data-buy="${i}">Draft PO</button>` : ''}
          ${p.makeBuy === 'Make' && engineer ? `<button class="btn small primary" data-make="${i}">Create order</button>` : ''}</td></tr>`).join('')}
      </tbody></table>
      <div class="faint" style="margin-top:6px">Recommendations only: nothing is ordered until a buyer drafts a purchase order (approved by someone else) or an engineer sets up a production order (released by a planner).</div>`;
  }

  // V2.6: what MRP covered with approved alternates, and why (the substitution peg).
  function substitutionsTab() {
    if (data.mrp.error) return errorBlock(data.mrp.error, 'the material plan');
    const list = out().substitutions ?? [];
    if (!list.length) return emptyBlock('No substitutions', 'MRP substitutes only approved equivalents, only on BOM lines whose policy is automatic, and only up to the shortage. Lines that suggest show their alternates under Exceptions.');
    return `<table class="table"><thead><tr><th>Need by</th><th>Short</th><th>Used instead</th><th class="num">Shortage</th><th class="num">Alternate free</th><th class="num">Allocated</th><th>Why</th></tr></thead><tbody>
      ${list.map(x => `<tr><td class="nowrap">${esc(fmtDate(x.date))}</td><td><b>${esc(x.primaryPart)}</b></td><td><b>${esc(x.alternatePart)}</b> <span class="faint">alternate ${x.relationshipId}</span></td>
        <td class="num">${qty(x.shortage)}</td><td class="num">${qty(x.alternateAvailable)}</td><td class="num"><b>${qty(x.alternateQuantity)}</b>${x.alternateQuantity !== x.primaryQuantity ? ` <span class="faint">= ${qty(x.primaryQuantity)} ${esc(x.primaryPart)}</span>` : ''}</td>
        <td class="muted">${esc(x.explanation)}</td></tr>`).join('')}
      </tbody></table>`;
  }

  async function alternates(part) {
    const { data: c, error } = await tryGet(`/api/engineering/alternates/candidates/${encodeURIComponent(part)}`);
    const row = x => `<tr><td><b>${esc(x.offeredPart)}</b> <span class="faint">alternate ${x.relationship.alternateId}</span></td><td>${qty(x.factor)} per 1</td><td class="muted">${esc(x.reasons.join(' '))}</td></tr>`;
    drawer(`${part} — alternates`, error ? errorBlock(error, 'alternates') : `
      <h3 style="margin:0 0 6px">Approved here (${c.approved.length})</h3>
      ${c.approved.length ? `<table class="table"><tbody>${c.approved.map(row).join('')}</tbody></table>` : '<div class="muted">None.</div>'}
      <h3 style="margin:14px 0 6px" class="c-warn">Requires approval (${c.requiresApproval.length})</h3>
      <div class="faint" style="margin-bottom:6px">Never bought or used in place of ${esc(part)} until engineering approves them for this use.</div>
      ${c.requiresApproval.length ? `<table class="table"><tbody>${c.requiresApproval.map(row).join('')}</tbody></table>` : '<div class="muted">None.</div>'}
      <div style="margin-top:12px"><a href="#/engineering/${encodeURIComponent(part)}">Where-used and alternates of ${esc(part)} →</a></div>`);
  }

  function exceptionsTab() {
    if (data.mrp.error) return errorBlock(data.mrp.error, 'the material plan');
    const list = out().exceptions;
    if (!list.length) return emptyBlock('No exceptions', 'Every receipt is on time for its need and the data is complete.');
    return `<table class="table"><thead><tr><th>Part</th><th>What</th><th>Detail</th><th>Action</th></tr></thead><tbody>
      ${list.map(e => `<tr><td><b>${esc(e.partNumber)}</b></td><td><span class="pill ${['PastDueRelease', 'OverdueReceipt', 'BomLoop', 'NoRevision'].includes(e.kind) ? 'bad' : 'warn'}">${esc(EXCEPTION_LABEL[e.kind] ?? e.kind)}</span></td><td>${esc(e.detail)}</td><td class="nowrap">${exceptionActionHtml(e)}</td></tr>`).join('')}
      </tbody></table>`;
  }

  function exceptionActionHtml(e) {
    if (['NoRevision', 'BomLoop', 'UnknownItem'].includes(e.kind)) {
      return `<a class="btn small" href="#/engineering/${encodeURIComponent(e.partNumber)}">Open in Engineering</a>`;
    }
    if (['RescheduleIn', 'OverdueReceipt'].includes(e.kind)) {
      return `<button class="btn small ghost" type="button" data-alts="${esc(e.partNumber)}">Alternates</button> <a class="btn small" href="#/procurement">Open POs</a>`;
    }
    if (e.kind === 'PastDueRelease') {
      return `<button class="btn small primary" type="button" data-jump-part="${esc(e.partNumber)}">View recommendation</button>`;
    }
    return `<button class="btn small ghost" type="button" data-alts="${esc(e.partNumber)}">Alternates</button>`;
  }

  function itemsTab() {
    if (data.mrp.error) return errorBlock(data.mrp.error, 'the material plan');
    const allItems = out().items ?? [];
    const list = allItems.filter(i => {
      const held = (i.inspection ?? 0) + (i.hold ?? 0) > 0;
      const low = (i.buckets ?? []).some(b => (b.projectedAvailable ?? 0) < (i.safetyStock ?? 0)) || (i.available ?? 0) < (i.safetyStock ?? 0);
      return itemFilter === 'low' ? low : itemFilter === 'held' ? held
        : itemFilter === 'make' ? i.makeBuy === 'Make' : itemFilter === 'buy' ? i.makeBuy === 'Buy' : true;
    });
    const canEdit = canAction(me, 'engineering.planning.manage');
    if (!allItems.length) return emptyBlock('Nothing to plan yet', 'Items appear here once they have demand, stock, supply or safety stock.');
    const filters = [['all', `All (${allItems.length})`], ['low', `Below safety stock (${allItems.filter(i => (i.buckets ?? []).some(b => (b.projectedAvailable ?? 0) < (i.safetyStock ?? 0)) || (i.available ?? 0) < (i.safetyStock ?? 0)).length})`],
      ['held', `In inspection/hold (${allItems.filter(i => (i.inspection ?? 0) + (i.hold ?? 0) > 0).length})`], ['make', `Make (${allItems.filter(i => i.makeBuy === 'Make').length})`], ['buy', `Buy (${allItems.filter(i => i.makeBuy === 'Buy').length})`]];
    const chips = `<div class="mrp-filters" role="group" aria-label="Filter planned items">${filters.map(([key, label]) =>
      `<button class="mrp-filter ${itemFilter === key ? 'on' : ''}" type="button" data-item-filter="${key}" aria-pressed="${itemFilter === key}">${esc(label)}</button>`).join('')}</div>`;
    if (!list.length) return `${chips}${emptyBlock('No items match', 'Choose another item filter.')}`;
    return `${chips}<table class="table"><thead><tr><th>Part</th><th>Level</th><th class="num">Available</th><th>Insp./hold</th><th class="num">Safety stock</th><th>Lot policy</th><th class="num">Lead time</th><th>Horizon</th><th></th></tr></thead><tbody>
      ${list.map(i => `<tr class="click" data-item="${esc(i.partNumber)}">
        <td><b>${esc(i.partNumber)}</b> <span class="faint">${esc(i.makeBuy)}</span><br><span class="muted">${esc(i.description)}</span></td><td>${i.level}</td>
        <td class="num">${qty(i.available)}</td><td>${(i.inspection ?? 0) + (i.hold ?? 0) ? `<span class="pill warn" title="Stock in inspection and hold is excluded from nettable availability">${qty((i.inspection ?? 0) + (i.hold ?? 0))} in insp./hold</span>` : '—'}</td><td class="num">${i.safetyStock ? qty(i.safetyStock) : '—'}</td>
        <td>${esc(policyText(i))}</td><td class="num">${i.leadTimeDays} d</td>
        <td><span class="num">${qty(i.buckets?.at(-1)?.projectedAvailable ?? i.available)}</span> ${horizonSparkHtml(i)}</td>
        <td>${canEdit ? `<button class="btn small" data-params="${esc(i.partNumber)}">Planning</button>` : ''}</td></tr>`).join('')}
      </tbody></table>
      ${canEdit ? '<div class="toolbar" style="margin-top:8px"><button class="btn small" data-params-any="1">Set planning for another part…</button></div>' : ''}`;
  }

  function horizonSparkHtml(i) {
    const buckets = (i.buckets ?? []).slice(0, 14);
    if (!buckets.length) return '<span class="faint">—</span>';
    const peak = Math.max(1, i.available ?? 0, i.safetyStock ?? 0, ...buckets.map(b => Math.max(0, b.projectedAvailable ?? 0)));
    const bars = buckets.map(b => {
      const projected = b.projectedAvailable ?? 0;
      const tone = projected <= 0 && (b.gross > 0 || (i.safetyStock ?? 0) > 0) ? 'bad' : projected < (i.safetyStock ?? 0) ? 'warn' : '';
      const height = Math.max(3, Math.min(14, Math.round((Math.max(0, projected) / peak) * 14)));
      return `<span class="mrp-spark-bar ${tone}" style="height:${height}px" title="${esc(fmtDate(b.date))}: ${qty(projected)} ${esc(i.unitOfMeasure)}"></span>`;
    }).join('');
    return `<span class="mrp-spark" role="img" aria-label="Horizon balance trajectory for ${esc(i.partNumber)}">${bars}</span>`;
  }

  function demandTab() {
    if (data.demand.error) return errorBlock(data.demand.error, 'demand');
    const list = data.demand.data;
    return `${planner ? '<div class="toolbar"><button class="btn" data-import-demand-csv>Import demand CSV</button><button class="btn primary" data-add-demand="1">+ Demand</button></div>' : ''}
      ${!list.length ? emptyBlock('No open demand', 'Record customer orders, forecasts and contract deliveries here; MRP plans to cover them.') : `
      <table class="table"><thead><tr><th>#</th><th>Part</th><th class="num">Qty</th><th>Need by</th><th>Priority</th><th>Kind</th><th>Customer / reference</th><th></th></tr></thead><tbody>
      ${list.map(d => `<tr><td>D${d.demandId}</td><td><b>${esc(d.partNumber)}</b></td><td class="num">${qty(d.quantity)}</td><td>${esc(fmtDate(d.needDate))}</td><td>${d.priority}</td>
        <td>${esc((DEMAND_KINDS.find(k => k[0] === d.kind) ?? [0, d.kind])[1])}</td><td>${esc([d.customer, d.reference].filter(Boolean).join(' · ') || '—')}${d.shipped ? ` <span class="faint">· ${qty(d.shipped)} shipped</span>` : ''}${d.contractNote ? `<div class="c-warn">${esc(d.contractNote)}</div>` : ''}</td>
        <td class="nowrap">${planner && d.contractNote ? `<button class="btn small primary" data-resync="${d.deliveryId}">Update from contract</button> ` : ''}${planner ? `<button class="btn small" data-change="${d.demandId}">Change</button> <button class="btn small" data-close="${d.demandId}">Close</button> <button class="btn small ghost" data-cancel="${d.demandId}">Cancel</button>` : ''}</td></tr>`).join('')}
      </tbody></table>`}`;
  }

  function render() {
    const run = data.mrp.data;
    page.innerHTML = `
      <div class="page-head">
        <div><h1>Material Plan</h1><div class="sub">MRP nets demand and order material against stock and open supply, item by item down the bill of materials. Click <b>Why?</b> on any recommendation to see the arithmetic.</div></div>
        <div class="spacer"></div><button class="btn" id="rerun">Run again</button>
      </div>
      ${kpis()}
      <section class="panel">
        <header><div class="seg">${[
          ['planned', `Recommendations${out()?.plannedOrders?.length ? ` (${out().plannedOrders.length})` : ''}`],
          ['substitutions', `Substitutions${out()?.substitutions?.length ? ` (${out().substitutions.length})` : ''}`],
          ['exceptions', `Exceptions${out()?.exceptions?.length ? ` (${out().exceptions.length})` : ''}`],
          ['items', `Items${out()?.items?.length ? ` (${out().items.length})` : ''}`],
          ['demand', `Demand${data.demand.data?.length ? ` (${data.demand.data.length})` : ''}`],
        ].map(([k, l]) => `<button data-tab="${k}" class="${tab === k ? 'on' : ''}">${esc(l)}</button>`).join('')}</div></header>
        ${tab === 'exceptions' ? exceptionsTab() : tab === 'items' ? itemsTab() : tab === 'demand' ? demandTab() : tab === 'substitutions' ? substitutionsTab() : plannedTab()}
      </section>
      ${run ? `<div class="faint mono" style="font-size:11px">${esc(run.engineName)} v${esc(run.engineVersion)} · as of ${esc(fmtDate(run.output.today))} · input ${esc(run.inputFingerprint.slice(0, 12))} · output ${esc(run.outputFingerprint.slice(0, 12))} — the same inputs always give the same plan.</div>` : ''}`;

    enhanceTables(page);
    $('#rerun', page)?.addEventListener('click', reload);
    $$('[data-tab]', page).forEach(b => b.addEventListener('click', () => { tab = b.dataset.tab; render(); }));
    $$('[data-planned-filter]', page).forEach(b => b.addEventListener('click', () => { plannedFilter = b.dataset.plannedFilter; render(); }));
    $$('[data-item-filter]', page).forEach(b => b.addEventListener('click', () => { itemFilter = b.dataset.itemFilter; render(); }));
    $$('[data-why]', page).forEach(b => b.addEventListener('click', () => why(out().plannedOrders[Number(b.dataset.why)])));
    $$('[data-alts]', page).forEach(b => b.addEventListener('click', () => alternates(b.dataset.alts)));
    $$('[data-jump-part]', page).forEach(b => b.addEventListener('click', () => {
      const part = b.dataset.jumpPart;
      const planned = out().plannedOrders.find(p => p.partNumber === part && p.pastDue) ?? out().plannedOrders.find(p => p.partNumber === part);
      tab = 'planned'; plannedFilter = 'all'; render();
      if (planned) why(planned);
    }));
    $$('[data-req]', page).forEach(b => b.addEventListener('click', async () => {
      const p = out().plannedOrders[Number(b.dataset.req)];
      const done = await confirmWithReason({ title: `Requisition for ${p.partNumber}`, detail: `${qty(p.quantity)} ${p.unitOfMeasure} by ${fmtDate(p.dueDate)}. Its source comes from what the recommendation covers: ${p.pegging.map(g => g.description).filter((v, j, a) => a.indexOf(v) === j).join('; ')}.`, confirmLabel: 'Raise requisition', reasonRequired: false },
        reason => command('/api/planning/mrp/requisitions', { plannedOrder: p.key }, reason));
      if (done) { toast(done.message); location.hash = '#/procurement?tab=requisitions'; }
    }));
    $$('[data-buy]', page).forEach(b => b.addEventListener('click', () => draftPo(out().plannedOrders[Number(b.dataset.buy)])));
    $$('[data-make]', page).forEach(b => b.addEventListener('click', () => createOrder(out().plannedOrders[Number(b.dataset.make)])));
    $$('[data-item]', page).forEach(r => r.addEventListener('click', e => { if (!e.target.closest('button')) itemDrawer(r.dataset.item); }));
    $$('[data-params]', page).forEach(b => b.addEventListener('click', () => planning(b.dataset.params)));
    $$('[data-params-any]', page).forEach(b => b.addEventListener('click', () => planning(null)));
    $$('[data-add-demand]', page).forEach(b => b.addEventListener('click', addDemand));
    $('[data-import-demand-csv]', page)?.addEventListener('click', () => csvImportModal({
      title: 'Import MRP demand', detail: 'Each row adds one customer, forecast, contract, service or internal demand record. Forge validates and records every row separately.',
      columns: [
        { name: 'partNumber', label: 'Part number', required: true }, { name: 'quantity', label: 'Quantity', type: 'number', required: true, min: 0.000001 },
        { name: 'needDate', label: 'Need date', type: 'date', required: true }, { name: 'priority', label: 'Priority', type: 'number', required: true, min: 1, max: 9 },
        { name: 'kind', label: 'Demand kind', required: true, options: DEMAND_KINDS.map(([value]) => value) },
        { name: 'customer', label: 'Customer' }, { name: 'reference', label: 'Reference' }, { name: 'sourceKey', label: 'Source key' },
      ], sampleRows: [{ partNumber: 'FG-100', quantity: 25, needDate: plusDays(14), priority: 3, kind: 'CustomerOrder', customer: 'Example customer', reference: 'PO-1042', sourceKey: 'customer-order:PO-1042' }],
      onRow: (row, reason) => command('/api/planning/demand', row, reason),
    }, async () => { await reload(); }));
    $$('[data-resync]', page).forEach(b => b.addEventListener('click', async () => {
      const done = await confirmWithReason({ title: `Update demand from contract delivery ${b.dataset.resync}`, detail: 'The demand takes the contract delivery\'s current quantity and date, or is cancelled if the delivery was.', confirmLabel: 'Update', reasonRequired: false },
        reason => command(`/api/planning/demand/from-delivery/${b.dataset.resync}`, {}, reason));
      if (done) { toast(done.message); await reload(); }
    }));
    $$('[data-change]', page).forEach(b => b.addEventListener('click', () => changeDemand(Number(b.dataset.change))));
    $$('[data-close]', page).forEach(b => b.addEventListener('click', () => closeDemand(Number(b.dataset.close), 'close')));
    $$('[data-cancel]', page).forEach(b => b.addEventListener('click', () => closeDemand(Number(b.dataset.cancel), 'cancel')));
    if (focus && itemPlan(focus)) { const f = focus; setTimeout(() => itemDrawer(f), 0); }
  }

  function bucketsTable(i) {
    const buckets = i.buckets ?? [];
    const peak = Math.max(1, i.available ?? 0, i.safetyStock ?? 0, ...buckets.map(b => Math.max(0, b.projectedAvailable ?? 0)));
    return `<table class="table"><thead><tr><th>Date</th><th class="num">Needed</th><th class="num">Arriving</th><th class="num">Planned</th><th>Projected available</th></tr></thead><tbody>
      ${buckets.map(b => {
        const projected = b.projectedAvailable ?? 0;
        const safety = i.safetyStock ?? 0;
        const tone = projected <= 0 && (b.gross > 0 || safety > 0) ? 'bad' : projected < safety ? 'warn' : 'ok';
        const width = Math.max(4, Math.min(100, Math.round((Math.max(0, projected) / peak) * 100)));
        return `<tr><td>${esc(fmtDate(b.date))}</td><td class="num">${b.gross ? qty(b.gross) : ''}</td><td class="num">${b.scheduledReceipts ? qty(b.scheduledReceipts) : ''}</td>
          <td class="num">${b.plannedReceipts ? `<b class="c-warn">${qty(b.plannedReceipts)}</b>` : ''}</td><td class="mrp-bal-cell"><div class="mrp-bal-value ${tone === 'bad' ? 'c-bad' : tone === 'warn' ? 'c-warn' : ''}">${qty(projected)}${projected < safety ? ` <span class="faint">(below ${qty(safety)})</span>` : ''}</div><div class="mrp-bal-bar" aria-hidden="true"><div class="mrp-bal-fill ${tone}" style="width:${width}%"></div></div></td></tr>`;
      }).join('')}
      </tbody></table>`;
  }

  function nettingFormulaHtml(p, i) {
    if (!i) return '';
    const buckets = i.buckets ?? [];
    const gross = buckets.reduce((sum, b) => sum + (b.gross ?? 0), 0);
    const scheduled = buckets.reduce((sum, b) => sum + (b.scheduledReceipts ?? 0), 0);
    const pegged = (p.pegging ?? []).reduce((sum, peg) => sum + (peg.quantity ?? 0), 0);
    const locked = (i.inspection ?? 0) + (i.hold ?? 0);
    return `<div class="mrp-formula" role="region" aria-label="MRP netting summary">
      <div class="mrp-formula-step"><div class="k">Nettable stock</div><div class="v">${qty(i.available)} ${esc(i.unitOfMeasure)}</div><div class="faint">${locked ? `${qty(locked)} in inspection/hold excluded` : 'Ready now'}</div></div>
      <div class="mrp-formula-step"><div class="k">Scheduled in</div><div class="v">+${qty(scheduled)}</div><div class="faint">${i.receipts?.length ?? 0} open receipt(s)</div></div>
      <div class="mrp-formula-step"><div class="k">Gross need</div><div class="v">−${qty(gross)} ${esc(i.unitOfMeasure)}</div><div class="faint">${qty(pegged)} pegged to this recommendation · need by ${esc(fmtDate(p.dueDate))}</div></div>
      <div class="mrp-formula-step"><div class="k">Safety stock threshold</div><div class="v">${qty(i.safetyStock ?? 0)} ${esc(i.unitOfMeasure)}</div><div class="faint">${esc(policyText(i))}</div></div>
      <div class="mrp-formula-step highlight"><div class="k">Recommended ${esc(p.makeBuy)}</div><div class="v">${qty(p.quantity)} ${esc(p.unitOfMeasure)}</div><div class="faint">Release ${esc(fmtDate(p.releaseDate))}</div></div>
    </div><div class="faint mrp-netting-note">Time-phased netting applies receipt dates, safety stock and the lot policy shown above; this summary is not a single-period arithmetic total.</div>`;
  }

  function why(p) {
    const i = itemPlan(p.partNumber);
    drawer(`Why ${qty(p.quantity)} ${p.unitOfMeasure} of ${p.partNumber}?`, `
      ${nettingFormulaHtml(p, i)}
      <p>${esc(p.explanation)}</p>
      <h3 style="margin:0">What it covers</h3>
      <table class="table"><thead><tr><th>Requirement</th><th>Needed</th><th class="num">Qty</th></tr></thead><tbody>
        ${p.pegging.map(g => `<tr><td>${esc(g.description)}</td><td>${esc(fmtDate(g.needDate))}</td><td class="num">${qty(g.quantity)}</td></tr>`).join('')}</tbody></table>
      ${i ? `<h3 style="margin:0">${esc(p.partNumber)} day by day</h3>${bucketsTable(i)}
        <dl class="kv"><dt>Available for planning now</dt><dd>${qty(i.available)}${i.inspection + i.hold ? ` (plus ${qty(i.inspection)} in inspection, ${qty(i.hold)} on hold — not counted)` : ''}</dd>
          <dt>Planning</dt><dd>${esc(policyText(i))}; safety stock ${qty(i.safetyStock)}; lead time ${i.leadTimeDays} days</dd></dl>` : ''}`);
  }

  function itemDrawer(part) {
    const i = itemPlan(part);
    if (!i) return;
    drawer(`${part} — ${i.description}`, `
      <dl class="kv"><dt>Make or buy</dt><dd>${esc(i.makeBuy)} · level ${i.level}</dd><dt>Planning</dt><dd>${esc(policyText(i))}; safety stock ${qty(i.safetyStock)}; lead time ${i.leadTimeDays} days</dd>
        <dt>Available for planning now</dt><dd>${qty(i.available)} ${esc(i.unitOfMeasure)}${i.inspection + i.hold ? ` (${qty(i.inspection)} in inspection, ${qty(i.hold)} on hold — not counted)` : ''}</dd></dl>
      <h3 style="margin:0">Day by day</h3>${bucketsTable(i)}
      <h3 style="margin:0">Requirements</h3>
      ${i.requirements.length ? `<table class="table"><tbody>${i.requirements.map(r => `<tr><td>${esc(r.description)}</td><td>${esc(fmtDate(r.needDate))}</td><td class="num">${qty(r.quantity)}</td></tr>`).join('')}</tbody></table>` : '<div class="muted">None.</div>'}
      <h3 style="margin:0">Scheduled receipts</h3>
      ${i.receipts.length ? `<table class="table"><tbody>${i.receipts.map(r => `<tr><td>${esc(r.description)}</td><td>${esc(fmtDate(r.dueDate))}</td><td class="num">${qty(r.quantity)}</td></tr>`).join('')}</tbody></table>` : '<div class="muted">None.</div>'}
      <div class="faint"><a href="#/inventory">Stock and ledger in Inventory →</a></div>`);
  }

  function planning(part) {
    const info = part ? itemInfo(part) : null;
    const fields = [
      part ? { name: 'part', label: 'Part', value: part, readonly: true } : { name: 'part', label: 'Part', type: 'select', options: (data.items.data ?? []).map(x => [x.partNumber, `${x.partNumber} — ${x.description}`]), required: true },
      { name: 'leadTimeDays', label: 'Lead time (calendar days)', type: 'number', min: 0, value: info?.leadTimeDays ?? 0, required: true },
      { name: 'safetyStock', label: 'Safety stock', type: 'number', min: 0, step: 'any', value: info?.safetyStock ?? 0 },
      { name: 'lotPolicy', label: 'Lot policy', type: 'select', options: POLICIES, value: info?.lotPolicy ?? 'LotForLot' },
      { name: 'orderQuantity', label: 'Order quantity (fixed lot or minimum)', type: 'number', min: 0, step: 'any', value: info?.orderQuantity ?? 0 },
      { name: 'orderMultiple', label: 'Round up to multiples of (0 = none)', type: 'number', min: 0, step: 'any', value: info?.orderMultiple ?? 0 },
    ];
    return formModal({ title: part ? `Planning for ${part}` : 'Planning parameters', detail: 'MRP uses these on its next run. The change is audited.', fields },
      (v, reason) => command(`/api/engineering/items/${encodeURIComponent(v.part)}/planning`, { ...v, safetyStock: v.safetyStock ?? 0, orderQuantity: v.orderQuantity ?? 0, orderMultiple: v.orderMultiple ?? 0 }, reason),
      async () => { const items = await tryGet('/api/engineering/items'); data.items = items; await reload(); });
  }

  function addDemand() {
    return Promise.all([
      tryGet('/api/maintenance/work-orders'), tryGet('/api/projects/'), tryGet('/api/resources/tools'),
    ]).then(async ([workOrders, projects, tools]) => {
      const sourceOptions = [];
      for (const view of workOrders.data ?? []) {
        const order = view.order ?? view;
        if (['Closed', 'Cancelled'].includes(order.status)) continue;
        const key = `work-order:${order.workOrderId}`;
        sourceOptions.push([key, `${key} — ${order.title}${order.machineCode ? ` (${order.machineCode})` : ''}`]);
      }
      const openProjects = (projects.data ?? []).map(summary => summary.project ?? summary).filter(project => project.status === 'Approved');
      const projectDetails = await Promise.all(openProjects.map(project => tryGet(`/api/projects/${encodeURIComponent(project.code)}`)));
      for (const result of projectDetails) {
        for (const line of result.data?.lines ?? []) {
          if (line.status !== 'Active') continue;
          const key = line.sourceKey ?? `project:${line.projectCode}/${line.lineNumber}`;
          sourceOptions.push([key, `${key} — ${line.partNumber}${line.description ? ` · ${line.description}` : ''}`]);
        }
      }
      for (const view of tools.data ?? []) {
        const tool = view.tool ?? view;
        if (tool.status === 'Retired') continue;
        const key = `tool:${tool.code}`;
        sourceOptions.push([key, `${key} — ${tool.description || tool.code}`]);
      }
      const distinctSources = [...new Map(sourceOptions.map(option => [option[0], option])).values()];
      return formModal({
        title: 'Record demand', confirmLabel: 'Record',
        fields: [
          { name: 'partNumber', label: 'Part', type: 'select', options: (data.items.data ?? []).map(x => [x.partNumber, `${x.partNumber} — ${x.description}`]), required: true },
          { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', required: true },
          { name: 'needDate', label: 'Needed by', type: 'date', value: plusDays(14), required: true },
          { name: 'priority', label: 'Priority (1 = highest)', type: 'number', min: 1, value: 5 },
          { name: 'kind', label: 'Kind', type: 'select', options: DEMAND_KINDS.filter(([k]) => k !== 'ContractDelivery') },
          { name: 'sourceKey', label: 'What it is for (kind:key)', type: 'lookup', allowCustom: true, options: distinctSources,
            placeholder: 'Search records or type e.g. work-order:57', hint: 'Required for Maintenance and later demand kinds. Work orders, approved project lines and tools are listed when available.' },
          { name: 'customer', label: 'Customer (optional)' },
          { name: 'reference', label: 'Reference — customer PO, contract line (optional)' },
        ],
      }, (v, reason) => command('/api/planning/demand', v, reason), reload);
    });
  }

  function changeDemand(id) {
    const d = data.demand.data.find(x => x.demandId === id);
    return formModal({
      title: `Change demand D${id}`, detail: `${d.partNumber}. MRP results move with this change.`, reasonRequired: true,
      fields: [
        { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', value: d.quantity, required: true },
        { name: 'needDate', label: 'Needed by', type: 'date', value: d.needDate, required: true },
        { name: 'priority', label: 'Priority (1 = highest)', type: 'number', min: 1, value: d.priority, required: true },
      ],
    }, (v, reason) => command(`/api/planning/demand/${id}/change`, v, reason), reload);
  }

  async function closeDemand(id, verb) {
    const done = await confirmWithReason({ title: `${verb === 'close' ? 'Close' : 'Cancel'} demand D${id}`, detail: verb === 'close' ? 'Closed demand is kept for history and no longer planned.' : 'Cancelled demand is kept for history and no longer planned.', confirmLabel: verb === 'close' ? 'Close' : 'Cancel demand' },
      reason => command(`/api/planning/demand/${id}/${verb}`, {}, reason));
    if (done) { toast(done.message); await reload(); }
  }

  function draftPo(p) {
    const activeSuppliers = (data.suppliers?.data ?? []).filter(supplier => supplier.status === 'Active');
    const approvedCodes = new Set((data.sources?.data ?? []).filter(source => source.partNumber === p.partNumber).map(source => source.supplierCode));
    const approvedFirst = activeSuppliers.filter(supplier => approvedCodes.has(supplier.code));
    const remaining = approvedCodes.size ? [] : activeSuppliers;
    const eligibleSuppliers = [...approvedFirst, ...remaining];
    const supplierField = eligibleSuppliers.length
      ? { name: 'supplier', label: 'Supplier', type: 'select', required: true, options: [
        ...approvedFirst.map(s => [s.code, `★ ${s.name} (${s.code}) — Approved source`]),
        ...remaining.map(s => [s.code, `${s.name} (${s.code})`]),
      ], value: approvedFirst[0]?.code ?? remaining[0]?.code, hint: approvedFirst.length ? `Approved sources for ${p.partNumber} are listed first.` : 'No approved source is recorded for this part.' }
      : { name: 'supplier', label: 'Supplier code or name', required: true, hint: approvedCodes.size
        ? `No active approved supplier is available for ${p.partNumber}. Check the supplier master before drafting.`
        : 'No active suppliers could be loaded. Enter a supplier code or name.' };
    return formModal({
      title: `Draft a purchase order for ${p.partNumber}`, detail: 'Created as a draft; a different person approves it before it counts as supply.', confirmLabel: 'Draft PO',
      fields: [
        supplierField,
        { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', value: p.quantity, required: true },
        { name: 'promisedDate', label: 'Promised date', type: 'date', value: p.dueDate, required: true },
        { name: 'unitPrice', label: 'Unit price (optional)', type: 'number', min: 0, step: '0.01' },
        { name: 'currency', label: 'Currency', type: 'select', value: 'USD', options: [['USD', 'USD'], ['EUR', 'EUR'], ['GBP', 'GBP'], ['CAD', 'CAD']] },
      ],
    }, (v, reason) => command('/api/procurement/purchase-orders', { supplier: v.supplier, lines: [{ partNumber: p.partNumber, quantity: v.quantity, promisedDate: v.promisedDate, unitPrice: v.unitPrice, currency: v.currency ?? 'USD' }] }, reason),
    async () => { location.hash = '#/procurement'; });
  }

  function createOrder(p) {
    return formModal({
      title: `Set up a production order for ${p.partNumber}`, detail: 'Created as Planned; a planner (not you) releases it.', confirmLabel: 'Create order',
      fields: [
        { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', value: p.quantity, required: true },
        { name: 'dueDate', label: 'Due', type: 'date', value: p.dueDate, required: true },
        { name: 'priority', label: 'Priority (1 = highest)', type: 'number', min: 1, value: 5 },
      ],
    }, (v, reason) => command('/api/orders/', { partNumber: p.partNumber, ...v }, reason),
    async r => { location.hash = `#/orders/${r.entityId}`; });
  }

  await load();
  render();
}

function policyText(i) {
  const multiple = i.orderMultiple > 0 ? `, multiples of ${qty(i.orderMultiple)}` : '';
  if (i.lotPolicy === 'FixedQuantity') return `Fixed lots of ${qty(i.orderQuantity)}${multiple}`;
  if (i.lotPolicy === 'MinimumQuantity') return `Minimum ${qty(i.orderQuantity)}${multiple}`;
  return `Lot-for-lot${multiple}`;
}

