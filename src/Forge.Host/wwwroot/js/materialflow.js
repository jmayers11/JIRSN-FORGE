import { tryGet, command } from './api.js';
import { esc, $$ } from './ui.js';
import { formModal, qty } from './forms.js';

// Material flow (V3.4): each component of an order through required, requested, allocated, picked, delivered,
// issued and consumed. "In the warehouse" is a material-flow constraint, kept apart from a shortage.

const STATE = { Issued: ['ok', 'Issued'], AtPointOfUse: ['ok', 'At the machine'], InWarehouse: ['warn', 'In the warehouse'], Shortage: ['bad', 'Short'] };

/** The material flow panel on an order page; material handlers can request staging from it. */
export async function appendOrderMaterialFlow(page, orderNumber, canRequest) {
  const [res, locations] = await Promise.all([tryGet(`/api/inventory/orders/${orderNumber}/material-flow`), tryGet('/api/inventory/locations')]);
  if (res.error || !res.data.lines.length) return;
  const section = document.createElement('section');
  section.className = 'panel';
  const render = f => {
    section.innerHTML = `<header><h2>Material to the machine</h2><span class="spacer"></span><span class="muted">each state on its own; each can be partial</span></header>
      <table class="table"><thead><tr><th>Part</th><th>Work center</th><th class="num">Required</th><th class="num">Requested</th><th class="num">Allocated</th><th class="num">Picked</th><th class="num">Delivered</th><th class="num">Issued</th><th class="num">Consumed</th><th>State</th><th></th></tr></thead><tbody>
      ${f.lines.map((l, i) => `<tr><td class="mono">${esc(l.partNumber)}</td><td>${esc(l.workCenterCode ?? '—')}</td><td class="num">${qty(l.required)}</td><td class="num">${qty(l.requested)}</td><td class="num">${qty(l.allocated)}</td>
        <td class="num">${qty(l.picked)}</td><td class="num">${qty(l.delivered)}</td><td class="num">${qty(l.issued)}</td><td class="num">${qty(l.consumed)}</td>
        <td><span class="pill ${STATE[l.state]?.[0] ?? 'none'}">${esc(STATE[l.state]?.[1] ?? l.state)}</span><div class="faint">${esc(l.explanation)}</div></td>
        <td>${canRequest && l.state === 'InWarehouse' && f.status === 'Released' ? `<button class="btn small" data-flow-request="${i}">Request staging</button>` : ''}</td></tr>`).join('')}</tbody></table>
      ${f.tasks.length ? `<div class="faint" style="margin-top:6px">Tasks: ${f.tasks.map(t => `#${t.taskId} ${esc(t.partNumber)} ${qty(t.quantity)} ${esc(t.status.toLowerCase())}`).join(' · ')}</div>` : ''}`;
    $$('[data-flow-request]', section).forEach(b => b.addEventListener('click', () => {
      const l = f.lines[Number(b.dataset.flowRequest)];
      const targets = (locations.data ?? []).filter(x => x.active && x.workCenterCode === l.workCenterCode && (x.pointOfUse || x.kind === 'Staging')).map(x => [x.code, `${x.code} — ${x.name}`]);
      const sources = (locations.data ?? []).filter(x => x.active && x.inCompanyCustody && !x.pointOfUse).map(x => [x.code, `${x.code} — ${x.name}`]);
      formModal({
        title: `Stage ${l.partNumber} for order ${orderNumber}`, detail: `${qty(Math.max(0, l.required - Math.max(l.issued, l.requested)))} is not yet requested or issued.`, confirmLabel: 'Request',
        fields: [
          { name: 'toLocation', label: `To (${l.workCenterCode} staging or point of use)`, type: 'select', options: targets, required: true },
          { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, value: Math.max(0, l.required - Math.max(l.issued, l.requested)), required: true },
          { name: 'fromLocation', label: 'From (optional: allocates now)', type: 'select', options: [['', '— allocate later —'], ...sources] },
          { name: 'lotNumber', label: 'Lot (lot-tracked parts)' },
        ],
      }, (v, reason) => command('/api/inventory/tasks', { kind: 'Stage', orderNumber, partNumber: l.partNumber, quantity: Number(v.quantity), toLocation: v.toLocation, fromLocation: v.fromLocation || null, lotNumber: v.lotNumber || null }, reason),
      async () => { const again = await tryGet(`/api/inventory/orders/${orderNumber}/material-flow`); if (!again.error) render(again.data); });
    }));
  };
  render(res.data);
  page.append(section);
}
