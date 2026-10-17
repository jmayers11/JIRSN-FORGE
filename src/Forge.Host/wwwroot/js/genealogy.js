import { tryGet, command } from './api.js';
import { who } from './people.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, drawer, confirmWithReason, toast } from './ui.js';
import { qty } from './forms.js';

// Genealogy (V2.11): read from the as-built record and the other records as they were written — component
// revision, lot or serial, approved alternate and who approved it, supplier and purchase order, operators
// with the qualifications recorded when they started, inspections, NCRs, documents at release, shipments.

const RESULT_TONE = { Passed: 'ok', Failed: 'bad' };

export function componentsTable(components) {
  if (!components.length) return emptyBlock('No as-built record', 'Components issued to the order appear here, as issued.');
  return `<table class="table"><thead><tr><th>Component</th><th>Rev</th><th>Lot / serial</th><th class="num">Qty</th><th>Alternate</th><th>Supplier / PO</th><th>Made on</th></tr></thead><tbody>
    ${components.map(c => `<tr><td class="mono">${esc(c.partNumber)}</td><td>${esc(c.revision ?? '—')}</td>
      <td>${c.lotNumber ? `<a href="#" data-serial="${esc(c.lotNumber)}" data-part="${esc(c.partNumber)}">${esc(c.lotNumber)}</a>${c.serial ? ' <span class="faint">serial</span>' : ''}` : '<span class="faint">not tracked</span>'}</td>
      <td class="num">${qty(c.quantity)}</td>
      <td>${c.inPlaceOf ? `in place of <span class="mono">${esc(c.inPlaceOf)}</span> (${qty(c.primaryQuantity)}) — ${esc(c.alternateKind ?? '')} ${c.alternateId}, approved by ${esc(who(c.alternateApprovedBy))} ${esc(fmtDate(c.alternateApprovedAt))}<div class="faint">${esc(c.alternateConditions ?? '')}</div>` : '—'}</td>
      <td>${c.supplier ? `${esc(c.supplier)}${c.poNumber ? ` · PO ${c.poNumber}/${c.poLine}` : ''}${c.receivedAt ? ` <span class="faint">received ${esc(fmtDate(c.receivedAt))}</span>` : ''}` : '<span class="faint">—</span>'}</td>
      <td>${c.madeOnOrder ? `<a href="#/orders/${c.madeOnOrder}">#${c.madeOnOrder}</a>` : ''}</td></tr>`).join('')}
    </tbody></table>`;
}

export function orderGenealogyHtml(g, depth = 0) {
  return `
    <div class="faint">Order <a href="#/orders/${g.orderNumber}">#${g.orderNumber}</a> · ${esc(g.partNumber)} rev ${esc(g.revision)} · ${qty(g.quantity)} · ${esc(g.status)}${g.releasedAt ? ` · released ${esc(fmtDateTime(g.releasedAt))} by ${esc(who(g.releasedBy))}` : ''}</div>
    ${g.produced.length ? `<p>Produced: ${g.produced.map(s => `<a href="#" data-serial="${esc(s)}" data-part="${esc(g.partNumber)}">${esc(s)}</a>`).join(', ')}</p>` : ''}
    <h3>As built</h3>${componentsTable(g.components)}
    <h3>Operators</h3>${!g.labor.length ? '<p class="muted">No labor recorded.</p>' : `<table class="table"><thead><tr><th>Op</th><th>Person</th><th>Role</th><th>Machine</th><th>Started</th><th>Qualifications held</th></tr></thead><tbody>
      ${g.labor.map(l => `<tr><td>${l.sequence}</td><td>${esc(who(l.person))}</td><td>${esc(l.role)}</td><td>${esc(l.machine ?? '—')}</td><td>${esc(fmtDateTime(l.startedAt))}</td><td>${l.qualifications ? esc(l.qualifications.join(', ') || 'none required') : '<span class="faint">not recorded</span>'}</td></tr>`).join('')}</tbody></table>`}
    <h3>Inspections and NCRs</h3>${!g.inspections.length && !g.ncrs.length ? '<p class="muted">None recorded.</p>' : `<ul>
      ${g.inspections.map(i => `<li>Op ${i.sequence}: <span class="pill ${RESULT_TONE[i.result] ?? 'none'}">${esc(i.result)}</span> ${qty(i.quantityInspected)} by ${esc(who(i.inspector))} ${esc(fmtDateTime(i.at))}${i.gaugeCode ? ` with ${esc(i.gaugeCode)}` : ''}</li>`).join('')}
      ${g.ncrs.map(n => `<li>NCR ${n.ncrId}: ${esc(n.defect)} — ${esc(n.status)}${n.disposition ? ` (${esc(n.disposition)})` : ''}</li>`).join('')}</ul>`}
    <h3>Documents in force at release</h3>${!g.documents.length ? '<p class="muted">None.</p>' : `<ul>${g.documents.map(x => `<li><span class="mono">${esc(x.documentNumber)}</span> ${esc(x.title)} — rev ${esc(x.atRelease ?? x.applicable ?? 'none')} <span class="faint">${esc(x.explanation)}</span></li>`).join('')}</ul>`}
    ${g.notes.length ? `<div class="faint">${g.notes.map(esc).join('<br>')}</div>` : ''}
    ${depth < 3 && g.children.length ? `<h3>Made in-house</h3>${g.children.map(c => `<div class="panel" style="margin:6px 0">${orderGenealogyHtml(c, depth + 1)}</div>`).join('')}` : ''}`;
}

/** Opens the genealogy of a serial (or lot) in a drawer; serial links inside open their own. */
export async function serialDrawer(serial, part) {
  const res = await tryGet(`/api/execution/serials/${encodeURIComponent(serial)}/genealogy${part ? `?part=${encodeURIComponent(part)}` : ''}`);
  const d = drawer(`Genealogy · ${part ? part + ' ' : ''}${serial}`, '');
  if (res.error) { d.element.innerHTML = errorBlock(res.error, 'this serial'); return; }
  const g = res.data;
  d.element.innerHTML = `
    <p><b>${esc(g.summary)}</b></p>
    ${g.received.length ? `<h3>Received</h3><ul>${g.received.map(r => `<li>${esc(r.supplier ?? 'No supplier')}${r.poNumber ? ` · PO ${r.poNumber}/${r.poLine}` : ''} · rev ${esc(r.revision ?? 'not recorded')} · ${esc(fmtDate(r.receivedAt))}</li>`).join('')}</ul>` : ''}
    ${g.shipments.length ? `<h3>Shipped</h3><ul>${g.shipments.map(s => `<li>Shipment ${s.shipmentId} to ${esc(s.customer)}${s.shipTo ? `, ${esc(s.shipTo)}` : ''} · ${esc(fmtDateTime(s.shippedAt))}${s.tracking ? ` · ${esc(s.carrier ?? '')} ${esc(s.tracking)}` : ''}</li>`).join('')}</ul>` : ''}
    ${g.usedIn.length ? `<h3>Issued to</h3><ul>${g.usedIn.map(u => `<li>${esc(u.kind)} ${qty(u.quantity)} on order <a href="#/orders/${u.orderNumber}">#${u.orderNumber}</a> ${esc(fmtDateTime(u.recordedAt))}</li>`).join('')}</ul>` : ''}
    ${g.order ? orderGenealogyHtml(g.order) : ''}`;
  bindSerialLinks(d.element);
}

export function bindSerialLinks(root) {
  $$('[data-serial]', root).forEach(a => a.addEventListener('click', ev => { ev.preventDefault(); serialDrawer(a.dataset.serial, a.dataset.part); }));
}

/** The genealogy panel on an order page. */
export async function appendOrderGenealogy(page, orderNumber) {
  const res = await tryGet(`/api/orders/${orderNumber}/genealogy`);
  if (res.error || (!res.data.components.length && !res.data.produced.length)) return;
  const section = document.createElement('section');
  section.className = 'panel';
  section.innerHTML = `<header><h2>As built and genealogy</h2><span class="spacer"></span><span class="muted">as recorded at issue, never rewritten</span></header>${orderGenealogyHtml(res.data)}`;
  page.append(section);
  bindSerialLinks(section);
}

// ---------------------------------------------------------------- V3.3: the genealogy graph and material accounting

const RELATION_TEXT = {
  Received: 'received', Split: 'split', Merged: 'merged', Replaced: 'replaced by', Issued: 'issued', Returned: 'returned', Consumed: 'consumed', Scrapped: 'scrapped',
  Lost: 'lost', Remnant: 'remnant cut', Produced: 'produced', Byproduct: 'byproduct', Shipped: 'shipped', Installed: 'installed',
};

/** Opens a trace drawer: where something came from and what it became, each link as recorded. Moves are not genealogy. */
export async function traceDrawer({ part, lot, order, shipment }) {
  const q = new URLSearchParams({ direction: 'Both' });
  if (part) q.set('part', part);
  if (lot) q.set('lot', lot);
  if (order) q.set('order', order);
  if (shipment) q.set('shipment', shipment);
  const d = drawer(`Trace · ${order ? `order ${order}` : shipment ? `shipment ${shipment}` : `${part ? part + ' ' : ''}${lot}`}`, '<div class="muted">Loading…</div>');
  const res = await tryGet(`/api/genealogy/trace?${q}`);
  if (res.error) { d.element.innerHTML = errorBlock(res.error, 'the trace'); return; }
  const t = res.data;
  const byId = new Map(t.nodes.map(n => [n.id, n]));
  const label = id => {
    const n = byId.get(id);
    if (!n) return esc(id);
    if (n.kind === 'Instance') return `<a href="#" data-trace-part="${esc(n.partNumber)}" data-trace-lot="${esc(n.lotNumber)}">${esc(n.label)}</a>`;
    if (n.kind === 'Order') return `<a href="#/orders/${n.orderNumber}">${esc(n.label)}</a>`;
    return esc(n.label);
  };
  const tree = (id, forward, depth, seen) => {
    const links = t.edges.filter(e => (forward ? e.from : e.to) === id);
    if (!links.length || depth > 12) return '';
    return `<ul class="trace">${links.map(e => {
      const other = forward ? e.to : e.from;
      const again = seen.has(other);
      const next = new Set(seen).add(other);
      return `<li><span class="pill none">${esc(RELATION_TEXT[e.relation] ?? e.relation)}</span> ${qty(e.quantity)} · ${label(other)} <span class="faint">${esc(fmtDateTime(e.at))}${e.note ? ` · ${esc(e.note)}` : ''}</span>${again ? ' <span class="faint">(shown above)</span>' : tree(other, forward, depth + 1, next)}</li>`;
    }).join('')}</ul>`;
  };
  const start = t.start.id;
  const back = tree(start, false, 0, new Set([start]));
  const forward = tree(start, true, 0, new Set([start]));
  d.element.innerHTML = `
    <p><b>${esc(t.start.label)}</b></p>
    <h3>Came from</h3>${back || '<p class="muted">Nothing recorded before it.</p>'}
    <h3>Became</h3>${forward || '<p class="muted">Nothing recorded after it yet.</p>'}
    ${t.notes.length ? `<div class="faint">${t.notes.map(esc).join('<br>')}</div>` : ''}
    <div class="faint">Each link is read from a record written when it happened: receipts, splits and merges, issues and returns, transformation records, production receipts, shipments and installs. Moving stock is never a link.</div>`;
  $$('[data-trace-lot]', d.element).forEach(a => a.addEventListener('click', ev => { ev.preventDefault(); traceDrawer({ part: a.dataset.tracePart, lot: a.dataset.traceLot }); }));
}

const STATUS_PILL = { Balanced: 'ok', WithinTolerance: 'ok', Unexplained: 'bad', NotRecorded: 'none' };
const STATUS_TEXT = { Balanced: 'Balanced', WithinTolerance: 'Within tolerance', Unexplained: 'Unexplained', NotRecorded: 'Not recorded' };

/**
 * V5-S1: the issued lots a byproduct came from. Each value is the quantity of that issued lot attributed to the
 * byproduct; blank means none. Only lot-tracked or serialized rows (with a lot number) can be parents.
 */
export function byproductParents(issued, values) {
  const parents = [];
  issued.forEach((r, i) => {
    const raw = values[i];
    if (raw == null || raw === '') return;
    const quantity = Number(raw);
    if (!r.lotNumber) throw new Error(`${r.partNumber} has no lot, so it cannot be named as a byproduct source.`);
    if (!(quantity > 0)) throw new Error(`Enter a positive quantity of ${r.partNumber} lot ${r.lotNumber} for the byproduct, or leave it blank.`);
    parents.push({ partNumber: r.partNumber, lotNumber: r.lotNumber, quantity });
  });
  return parents;
}

/** The material accounting panel on an order page (V3.3): issued = consumed + returned + scrap + loss + remnant + remaining. */
export async function appendOrderReconciliation(page, orderNumber, canRecord) {
  const res = await tryGet(`/api/inventory/orders/${orderNumber}/reconciliation`);
  if (res.error || !res.data.rows.length) return;
  const section = document.createElement('section');
  section.className = 'panel';
  const render = v => {
    section.innerHTML = `<header><h2>What became of the material</h2><span class="spacer"></span>${canRecord ? '<button class="btn small" data-record>Record</button>' : ''}</header>
      <table class="table"><thead><tr><th>Part</th><th>Lot / serial</th><th class="num">Issued</th><th class="num">Returned</th><th class="num">Consumed</th><th class="num">Scrap</th><th class="num">Loss</th><th class="num">Remnant</th><th class="num">Remaining</th><th class="num">Unexplained</th><th></th></tr></thead><tbody>
      ${v.rows.map(r => `<tr><td class="mono">${esc(r.partNumber)}</td><td>${r.lotNumber ? `<a href="#" data-trace-part="${esc(r.partNumber)}" data-trace-lot="${esc(r.lotNumber)}">${esc(r.lotNumber)}</a>` : '—'}</td>
        <td class="num">${qty(r.issued)}</td><td class="num">${qty(r.returned)}</td><td class="num">${qty(r.consumed)}</td><td class="num">${qty(r.scrap)}</td><td class="num">${qty(r.loss)}</td><td class="num">${qty(r.remnant)}</td><td class="num">${qty(r.remaining)}</td>
        <td class="num ${r.status === 'Unexplained' ? 'c-bad' : ''}"><b>${qty(r.unexplained)}</b>${r.tolerancePercent ? ` <span class="faint">±${qty(r.tolerancePercent)}%</span>` : ''}</td>
        <td><span class="pill ${STATUS_PILL[r.status]}">${esc(STATUS_TEXT[r.status] ?? r.status)}</span></td></tr>`).join('')}</tbody></table>
      <div class="faint" style="margin-top:6px">Issued = consumed + returned + scrap + loss + remnant + remaining, within the part's yield tolerance. Unexplained material stays flagged until a later record explains it; nothing is scrapped to make it add up. ${v.transformations.length} record(s), never changed.</div>`;
    $$('[data-trace-lot]', section).forEach(a => a.addEventListener('click', ev => { ev.preventDefault(); traceDrawer({ part: a.dataset.tracePart, lot: a.dataset.traceLot }); }));
    $('[data-record]', section)?.addEventListener('click', () => recordTransformation(v));
  };
  const recordTransformation = async v => {
    const issued = v.rows.filter(r => r.issued - r.returned > 0);
    const input = (i, role) => `<input class="input num" type="number" step="any" min="0" data-row="${i}" data-role="${role}" style="width:90px">`;
    const extraHtml = `
      <table class="table"><thead><tr><th>Issued</th><th>Consumed</th><th>Scrap</th><th>Loss</th><th>Still at the work center</th><th>Byproduct came from</th></tr></thead><tbody>
      ${issued.map((r, i) => `<tr><td class="mono">${esc(r.partNumber)} ${esc(r.lotNumber ?? '')} <span class="faint">${qty(r.unexplained)} open</span></td><td>${input(i, 'Consumed')}</td><td>${input(i, 'Scrap')}</td><td>${input(i, 'Loss')}</td><td>${input(i, 'Remaining')}</td>
        <td>${r.lotNumber ? `<input class="input num" type="number" step="any" min="0" data-parent="${i}" aria-label="Quantity of ${esc(r.partNumber)} lot ${esc(r.lotNumber)} the byproduct came from" style="width:90px">` : '<span class="faint">no lot</span>'}</td></tr>`).join('')}
      </tbody></table>
      <div class="form-grid">
        <label>Remnant: cut from lot <input class="input mono" data-x="remnantSource" placeholder="${esc(issued.find(r => r.lotNumber)?.lotNumber ?? '')}"></label>
        <label>New remnant number <input class="input mono" data-x="remnantLot"></label>
        <label>Remnant quantity <input class="input" type="number" step="any" min="0" data-x="remnantQty"></label>
        <label>Remnant into location <input class="input mono" data-x="remnantLocation"></label>
        <label>Byproduct part <input class="input mono" data-x="byPart"></label>
        <label>Byproduct number <input class="input mono" data-x="byLot"></label>
        <label>Byproduct quantity <input class="input" type="number" step="any" min="0" data-x="byQty"></label>
        <label>Byproduct into location <input class="input mono" data-x="byLocation"></label>
      </div>
      <div class="faint">A lot-tracked or serialized byproduct names the issued lot or lots it came from in the "Byproduct came from" column. Across byproducts, a lot's amounts cannot exceed what was recorded as consumed from it.</div>`;
    const done = await confirmWithReason({ title: `What became of the material on order ${orderNumber}`, detail: 'Leave a box empty for nothing. Scrap or loss needs a reason.', confirmLabel: 'Record', reasonRequired: false, extraHtml },
      (reason, m) => {
        const entries = [];
        $$('[data-row]', m).forEach(el => {
          if (el.value === '') return;
          const r = issued[Number(el.dataset.row)];
          entries.push({ role: el.dataset.role, partNumber: r.partNumber, lotNumber: r.lotNumber, quantity: Number(el.value) });
        });
        const x = k => $(`[data-x="${k}"]`, m).value.trim();
        if (x('remnantQty')) {
          const source = issued.find(r => r.lotNumber === x('remnantSource').toUpperCase()) ?? issued.find(r => r.lotNumber);
          entries.push({ role: 'Remnant', partNumber: source?.partNumber ?? '', lotNumber: x('remnantLot'), quantity: Number(x('remnantQty')), sourceLot: x('remnantSource') || source?.lotNumber, locationCode: x('remnantLocation') });
        }
        if (x('byQty')) {
          const values = issued.map((_, i) => $(`[data-parent="${i}"]`, m)?.value.trim() ?? '');
          const parents = byproductParents(issued, values);
          entries.push({ role: 'Byproduct', partNumber: x('byPart'), lotNumber: x('byLot') || null, quantity: Number(x('byQty')), locationCode: x('byLocation'), ...(parents.length ? { parents } : {}) });
        }
        return command('/api/inventory/transformations', { orderNumber, entries }, reason);
      });
    if (done) {
      toast(done.message);
      const again = await tryGet(`/api/inventory/orders/${orderNumber}/reconciliation`);
      if (!again.error) render(again.data);
    }
  };
  render(res.data);
  page.append(section);
}
