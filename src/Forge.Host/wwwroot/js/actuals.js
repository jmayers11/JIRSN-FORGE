import { tryGet, command } from './api.js';
import { who } from './people.js';
import { esc, $$, fmtDateTime, errorBlock, confirmWithReason, toast } from './ui.js';
import { formModal, qty, can } from './forms.js';

// Production actuals (V3.5): stoppages with one category and one reason code each, setups and changeovers against
// the minutes the routing expected, root causes proven only by a second person; an order's quantity through its
// states; rework and repair that take good output back without rewriting what was reported.

const CATEGORIES = ['Planned', 'Unplanned', 'Setup', 'Changeover', 'Maintenance', 'Waiting', 'Starved', 'Blocked'];
const CATEGORY_TONE = { Planned: 'info', Unplanned: 'bad', Setup: 'info', Changeover: 'info', Maintenance: 'warn', Waiting: 'warn', Starved: 'warn', Blocked: 'warn' };
const reporter = me => can(me, 'Supervisor', 'MaintenanceTechnician', 'MaintenancePlanner');
const confirmer = me => can(me, 'ManufacturingEngineer', 'QualityEngineer', 'MaintenancePlanner');
const mine = (me, actor) => actor === `person:${me.personId}`;

function rootCauseText(v) {
  const proven = v.rootCauses.find(r => r.confirmedAt);
  if (proven) return `<span class="pill ok">Proven</span> ${esc(proven.cause)} <span class="faint">· confirmed by ${esc(who(proven.confirmedBy))}</span>`;
  const last = v.rootCauses[v.rootCauses.length - 1];
  return last ? `<span class="pill warn">Proposed</span> ${esc(last.cause)} <span class="faint">· ${esc(last.evidence)}</span>` : '<span class="faint">No root cause yet — the reason code is what was reported</span>';
}

/** Stoppages of a work center's machines, last 7 days and open ones, with stop, end and root-cause actions. */
export async function appendStoppages(container, machines, me) {
  const section = document.createElement('section');
  section.className = 'panel';
  container.appendChild(section);
  const load = async () => {
    const [stoppages, reasons] = await Promise.all([tryGet('/api/execution/stoppages?days=7'), tryGet('/api/execution/reason-codes')]);
    const codes = machines.map(m => m.code);
    const list = (stoppages.data ?? []).filter(v => codes.includes(v.stoppage.machineCode)).reverse();
    const downtime = (reasons.data ?? []).filter(r => r.kind === 'Downtime' && r.active);
    section.innerHTML = `<header><h2>Stoppages</h2><span class="spacer"></span>${reporter(me) && machines.length ? '<button class="btn small" data-stop>Machine stopped</button>' : ''}</header>
      ${stoppages.error ? errorBlock(stoppages.error, 'stoppages') : !list.length ? '<div class="muted">No stoppages reported on these machines in the last 7 days.</div>' : `
      <table class="table"><thead><tr><th>Machine</th><th>Category</th><th>Reason</th><th>From</th><th class="num">Minutes</th><th class="num">Expected</th><th>Root cause</th><th></th></tr></thead><tbody>
      ${list.map(v => { const s = v.stoppage; return `<tr><td class="mono">${esc(s.machineCode)}${s.orderNumber ? `<br><a class="faint" href="#/orders/${s.orderNumber}">${s.orderNumber}/${s.sequence}</a>` : ''}</td>
        <td><span class="pill ${CATEGORY_TONE[s.category] ?? 'none'}">${esc(s.category)}</span></td><td>${esc(s.reasonCode)} <span class="faint">${esc(v.reasonDescription)}</span>${s.note ? `<div class="faint">${esc(s.note)}</div>` : ''}</td>
        <td class="nowrap">${esc(fmtDateTime(s.startedAt))}<br><span class="faint">${s.endedAt ? `to ${esc(fmtDateTime(s.endedAt))}` : 'still stopped'} · ${esc(who(s.startedBy))}</span></td>
        <td class="num">${qty(v.minutes, 1)}</td><td class="num ${v.variance > 0 ? 'c-warn' : ''}">${s.expectedMinutes != null ? `${qty(s.expectedMinutes, 1)}${v.variance != null ? `<br><span class="faint">${v.variance > 0 ? '+' : ''}${qty(v.variance, 1)}</span>` : ''}` : '—'}</td>
        <td>${rootCauseText(v)}</td>
        <td class="nowrap">${!s.endedAt && reporter(me) ? `<button class="btn small primary" data-end="${s.stoppageId}">Running again</button> ` : ''}${!v.rootCauses.some(r => r.confirmedAt) && reporter(me) ? `<button class="btn small ghost" data-cause="${s.stoppageId}">Propose cause</button> ` : ''}${v.rootCauses.filter(r => !r.confirmedAt && confirmer(me) && !mine(me, r.proposedBy)).map(r => `<button class="btn small" data-confirm="${r.rootCauseId}">Confirm cause</button>`).join('')}</td></tr>`; }).join('')}
      </tbody></table>`}
      <div class="faint" style="margin-top:6px">A reason code is what was reported. A root cause is proposed with its evidence and proven only when a different person confirms it.</div>`;
    $$('[data-stop]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: 'Machine stopped', detail: 'One category and one reason code. Setups and changeovers name the order operation they prepare; the routing\'s setup minutes become the expected time.', confirmLabel: 'Record',
      fields: [
        { name: 'machineCode', label: 'Machine', type: 'select', options: machines.map(m => [m.code, `${m.code} — ${m.name}`]), required: true },
        { name: 'category', label: 'Category', type: 'select', options: CATEGORIES.map(c => [c, c]), required: true },
        { name: 'reasonCode', label: 'Reason code', type: 'select', options: downtime.map(r => [r.code, `${r.code} — ${r.description} (${r.category})`]), required: true },
        { name: 'orderNumber', label: 'Order (setups and changeovers)', type: 'number' },
        { name: 'sequence', label: 'Operation', type: 'number' },
        { name: 'note', label: 'Note (optional)' },
      ],
    }, (v, reason) => command('/api/execution/stoppages', v, reason), load)));
    $$('[data-end]', section).forEach(b => b.addEventListener('click', async () => {
      const done = await confirmWithReason({ title: `Stoppage ${b.dataset.end} — running again`, detail: 'Ends the stoppage now.', confirmLabel: 'Running again', reasonRequired: false },
        reason => command(`/api/execution/stoppages/${b.dataset.end}/end`, {}, reason));
      if (done) { toast(done.message); await load(); }
    }));
    $$('[data-cause]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: `Propose a root cause for stoppage ${b.dataset.cause}`, detail: 'It stays a proposal until someone else confirms it from the evidence.', confirmLabel: 'Propose',
      fields: [{ name: 'cause', label: 'Root cause', required: true }, { name: 'evidence', label: 'Evidence', type: 'textarea', required: true }],
    }, (v, reason) => command(`/api/execution/stoppages/${b.dataset.cause}/root-causes`, v, reason), load)));
    $$('[data-confirm]', section).forEach(b => b.addEventListener('click', async () => {
      const done = await confirmWithReason({ title: `Confirm root cause ${b.dataset.confirm}`, detail: 'You confirm the evidence proves it. You cannot confirm a cause you proposed.', confirmLabel: 'Confirm' },
        reason => command(`/api/execution/root-causes/${b.dataset.confirm}/confirm`, {}, reason));
      if (done) { toast(done.message); await load(); }
    }));
  };
  await load();
}

/** The plant's reason codes; manufacturing engineers define, change and retire them. */
export async function appendReasonCodes(container, me) {
  const manage = can(me, 'ManufacturingEngineer');
  const section = document.createElement('section');
  section.className = 'panel';
  container.appendChild(section);
  const load = async () => {
    const res = await tryGet('/api/execution/reason-codes');
    section.innerHTML = `<header><h2>Reason codes</h2><span class="spacer"></span>${manage ? '<button class="btn small" data-add-reason>+ Reason code</button>' : ''}</header>
      ${res.error ? errorBlock(res.error, 'reason codes') : !res.data.length ? `<div class="muted">No reason codes yet.${manage ? ' Add the plant\'s own words for downtime, scrap and rework.' : ' A manufacturing engineer adds them.'}</div>` : `
      <table class="table"><thead><tr><th>Code</th><th>Kind</th><th>Category</th><th>Description</th><th></th></tr></thead><tbody>
      ${res.data.map(r => `<tr class="${r.active ? '' : 'retired'}"><td class="mono">${esc(r.code)}</td><td>${esc(r.kind)}</td><td>${esc(r.category ?? '—')}</td><td>${esc(r.description)}</td>
        <td>${!r.active ? '<span class="pill none">Retired</span>' : manage ? `<button class="btn small ghost" data-retire-reason="${esc(r.code)}">Retire</button>` : ''}</td></tr>`).join('')}</tbody></table>`}`;
    $$('[data-add-reason]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: 'Add a reason code', detail: 'Downtime reasons belong to one stoppage category.', confirmLabel: 'Add',
      fields: [
        { name: 'code', label: 'Code (permanent)', code: true, required: true, placeholder: 'NO-COIL' },
        { name: 'kind', label: 'Kind', type: 'select', options: [['Downtime', 'Downtime'], ['Scrap', 'Scrap'], ['Rework', 'Rework']] },
        { name: 'category', label: 'Stoppage category (downtime only)', type: 'select', options: [['', '—'], ...CATEGORIES.map(c => [c, c])] },
        { name: 'description', label: 'Description', required: true },
      ],
    }, (v, reason) => command('/api/execution/reason-codes', v, reason), load)));
    $$('[data-retire-reason]', section).forEach(b => b.addEventListener('click', async () => {
      const r = res.data.find(x => x.code === b.dataset.retireReason);
      const done = await confirmWithReason({ title: `Retire ${r.code}`, detail: 'It can no longer be chosen; what was recorded with it keeps it.', confirmLabel: 'Retire' },
        reason => command(`/api/execution/reason-codes/${encodeURIComponent(r.code)}`, { description: r.description, active: false }, reason));
      if (done) { toast(done.message); await load(); }
    }));
  };
  await load();
}

/** An order's quantity states and its rework and repair, on the order page. */
export async function appendOrderQuantityStates(page, order, me) {
  const section = document.createElement('section');
  section.className = 'panel';
  page.appendChild(section);
  const supervisor = can(me, 'Supervisor');
  const load = async () => {
    const [states, rework, reasons] = await Promise.all([tryGet(`/api/orders/${order.orderNumber}/quantity-states`), tryGet(`/api/quality/rework?order=${order.orderNumber}`), tryGet('/api/execution/reason-codes')]);
    if (states.error) { section.innerHTML = `<header><h2>Quantity states</h2></header>${errorBlock(states.error, 'quantity states')}`; return; }
    const q = states.data;
    const row = s => `<tr><td>${esc(s.state)}</td><td class="num"><b>${qty(s.quantity)}</b></td><td class="faint">${esc(s.explanation)}</td></tr>`;
    const list = rework.data ?? [];
    section.innerHTML = `<header><h2>Quantity states</h2><span class="spacer"></span>${supervisor && order.status !== 'Cancelled' ? '<button class="btn small" data-rework>Take back for rework or repair</button>' : ''}</header>
      <div class="grid-2" style="display:grid;grid-template-columns:1fr 1fr;gap:12px">
        <div><h3 style="margin:0">So far</h3><table class="table"><tbody>${q.states.filter(s => s.kind === 'SoFar').map(row).join('')}</tbody></table></div>
        <div><h3 style="margin:0">Now</h3><table class="table"><tbody>${q.states.filter(s => s.kind === 'Now').map(row).join('')}</tbody></table></div>
      </div>
      ${q.notes.map(n => `<div class="faint">${esc(n)}</div>`).join('')}
      ${list.length ? `<h3 style="margin:8px 0 0">Rework and repair</h3><table class="table"><thead><tr><th>#</th><th>Kind</th><th>Op</th><th class="num">Qty</th><th>Why</th><th>Opened</th><th>Outcome</th><th></th></tr></thead><tbody>
        ${list.map(r => `<tr><td>${r.reworkId}</td><td><span class="pill ${r.kind === 'Repair' ? 'warn' : 'info'}">${esc(r.kind)}</span></td><td>${r.sequence}</td><td class="num">${qty(r.quantity)}</td>
          <td>${r.ncrId ? `NCR ${r.ncrId}` : esc(r.reasonCode ?? '')}<div class="faint">${esc(r.instructions)}</div></td><td class="nowrap">${esc(fmtDateTime(r.openedAt))}<br><span class="faint">${esc(who(r.openedBy))}</span></td>
          <td>${r.closedAt ? `${qty(r.goodQuantity)} good · ${qty(r.scrapQuantity)} scrap<div class="faint">${esc(r.closeNote ?? '')}</div>` : '<span class="pill warn">Open</span>'}</td>
          <td>${!r.closedAt && supervisor ? `<button class="btn small" data-close-rework="${r.reworkId}">Close</button>` : ''}</td></tr>`).join('')}</tbody></table>` : ''}
      <div class="faint" style="margin-top:6px">Production reports are never changed: rework and repair take good output back, and closing says what came out good and what was scrapped.</div>`;
    $$('[data-rework]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: `Rework or repair on order ${order.orderNumber}`, detail: 'Rework brings it back to the drawing. Repair follows an approved repair disposition on an NCR and leaves it usable, not to the drawing.', confirmLabel: 'Take back', reasonRequired: true,
      fields: [
        { name: 'kind', label: 'Kind', type: 'select', options: [['Rework', 'Rework'], ['Repair', 'Repair']] },
        { name: 'sequence', label: 'Operation whose good output goes back', type: 'select', options: order.operations.map(o => [o.sequence, `${o.sequence} — ${o.description} (${qty(o.goodQuantity)} good)`]), required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, required: true },
        { name: 'ncrId', label: 'NCR (required for repair)', type: 'number' },
        { name: 'reasonCode', label: 'Rework reason code (without an NCR)', type: 'select', options: [['', '—'], ...(reasons.data ?? []).filter(r => r.kind === 'Rework' && r.active).map(r => [r.code, `${r.code} — ${r.description}`])] },
        { name: 'instructions', label: 'Instructions', type: 'textarea', required: true },
      ],
    }, (v, reason) => command('/api/quality/rework', { ...v, orderNumber: order.orderNumber, sequence: Number(v.sequence) }, reason), load)));
    $$('[data-close-rework]', section).forEach(b => b.addEventListener('click', () => {
      const r = list.find(x => x.reworkId === Number(b.dataset.closeRework));
      formModal({
        title: `Close ${r.kind.toLowerCase()} ${r.reworkId}`, detail: `${qty(r.quantity)} went back; good and scrap add up to that.`, confirmLabel: 'Close',
        fields: [
          { name: 'goodQuantity', label: 'Good', type: 'number', step: 'any', min: 0, value: r.quantity, required: true },
          { name: 'scrapQuantity', label: 'Scrapped', type: 'number', step: 'any', min: 0, value: 0, required: true },
          { name: 'note', label: 'Note', required: true },
        ],
      }, (v, reason) => command(`/api/quality/rework/${r.reworkId}/close`, v, reason), load);
    }));
  };
  await load();
}
