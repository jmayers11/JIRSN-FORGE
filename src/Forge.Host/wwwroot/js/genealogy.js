import { tryGet } from './api.js';
import { who } from './people.js';
import { esc, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, drawer } from './ui.js';
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
