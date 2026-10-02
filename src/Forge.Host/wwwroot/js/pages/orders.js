import { tryGet, command } from '../api.js';
import { icon } from '../icons.js';
import { who } from '../people.js';
import { esc, $, $$, fmtNumber, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast } from '../ui.js';

const STATUS_TONE = { Planned: 'none', FirmPlanned: 'info', Released: 'ok', Held: 'warn', Cancelled: 'bad', Completed: 'ok' };
const DELIVERY_TONE = { OnTime: 'ok', AtRisk: 'warn', Late: 'bad', Unschedulable: 'bad' };
const can = (me, ...roles) => roles.some(r => me.roles.includes(r));

export async function ordersPage(page, ctx) {
  if (ctx.params[0]) return orderDetail(page, ctx, Number(ctx.params[0]));
  const part = ctx.query.get('part') ?? '';
  const [orders, schedule] = await Promise.all([tryGet('/api/orders/'), tryGet('/api/planning/schedules/approved')]);
  const delivery = new Map((schedule.data?.result?.orders ?? []).map(o => [o.orderNumber, o]));
  const canCreate = can(ctx.me, 'ManufacturingEngineer', 'Planner');
  const list = (orders.data ?? []).filter(o => !part || o.partNumber === part);

  page.innerHTML = `
    <div class="page-head">
      <div><h1>Production Orders</h1><div class="sub">${part ? `Part ${esc(part)} · <a href="#/orders">show all</a>` : 'Every order you are cleared to see. Program orders are hidden from people without that program.'}</div></div>
      <div class="spacer"></div>
      ${canCreate ? '<button class="btn primary" id="new-order">New order</button>' : ''}
    </div>
    <section class="panel">
      ${orders.error ? errorBlock(orders.error, 'production orders') : !list.length ? emptyBlock('No orders yet', 'Orders are created for released parts, then released by a different person.') : `
      <table class="table">
        <thead><tr><th>Order</th><th>Part</th><th>Qty</th><th>Due</th><th>Priority</th><th>Status</th><th>Delivery (approved schedule)</th><th></th></tr></thead>
        <tbody>${list.map(o => {
          const d = delivery.get(o.orderNumber);
          return `<tr class="click" data-order="${o.orderNumber}">
            <td>#${o.orderNumber}</td><td>${esc(o.partNumber)} <span class="faint">rev ${esc(o.revision)}</span></td>
            <td>${fmtNumber(o.quantity, 3)}</td><td>${esc(fmtDate(o.dueDate))}</td><td>${o.priority}</td>
            <td><span class="pill ${STATUS_TONE[o.status]}">${esc(o.status)}</span>${o.programId ? ' <span class="badge gray">Program</span>' : ''}</td>
            <td>${d ? `<span class="pill ${DELIVERY_TONE[d.status]}" title="${esc(d.explanation)}">${esc(d.status)}</span> <span class="muted">${d.projectedCompletion ? esc(fmtDateTime(d.projectedCompletion)) : ''}</span>` : '<span class="faint">Not in an approved schedule</span>'}</td>
            <td class="chev">${icon('chevron')}</td></tr>`;
        }).join('')}</tbody>
      </table>`}
    </section>`;
  $$('[data-order]', page).forEach(r => r.addEventListener('click', () => { location.hash = `#/orders/${r.dataset.order}`; }));
  $('#new-order', page)?.addEventListener('click', () => newOrder());
}

async function newOrder() {
  const items = await tryGet('/api/engineering/items');
  const makeParts = (items.data ?? []).filter(i => i.makeBuy === 'Make');
  const extra = `
    <div class="field"><label for="o-part">Part (a released Make part)</label>
      <select id="o-part" class="select">${makeParts.map(i => `<option value="${esc(i.partNumber)}">${esc(i.partNumber)} — ${esc(i.description)}</option>`).join('')}</select></div>
    <div class="grid-2" style="gap:10px">
      <div class="field"><label for="o-qty">Quantity</label><input id="o-qty" class="input" type="number" min="0" step="any" value="1"></div>
      <div class="field"><label for="o-pri">Priority (1 = highest)</label><input id="o-pri" class="input" type="number" min="1" max="9" value="5"></div>
    </div>
    <div class="field"><label for="o-due">Due date</label><input id="o-due" class="input" type="date"></div>`;
  const result = await confirmWithReason({ title: 'New production order', detail: makeParts.length ? 'The order is created as Planned. Someone else releases it.' : 'No Make parts exist yet.', confirmLabel: 'Create order', extraHtml: extra }, (reason, m) => {
    const body = {
      partNumber: $('#o-part', m).value, quantity: Number($('#o-qty', m).value), priority: Number($('#o-pri', m).value), dueDate: $('#o-due', m).value,
    };
    if (!body.dueDate) throw new Error('Pick a due date.');
    return command('/api/orders/', body, reason);
  });
  if (result) { toast(`Order #${result.entityId} created.`); location.hash = `#/orders/${result.entityId}`; }
}

async function orderDetail(page, ctx, number) {
  const [order, readiness, dropDead, material, issues] = await Promise.all([
    tryGet(`/api/orders/${number}`), tryGet(`/api/orders/${number}/readiness`), tryGet(`/api/orders/${number}/drop-dead-dates`),
    tryGet('/api/planning/material'), tryGet(`/api/inventory/transactions?order=${number}`),
  ]);
  if (order.error) { page.innerHTML = `<a href="#/orders">← Production Orders</a>${errorBlock(order.error, `order #${number}`)}`; return; }
  const o = order.data;
  const me = ctx.me;
  const actions = [];
  if (can(me, 'ManufacturingEngineer', 'Planner') && o.status === 'Planned') actions.push(['firm', 'Firm']);
  if (can(me, 'Planner') && ['Planned', 'FirmPlanned'].includes(o.status)) actions.push(['release', 'Release']);
  if (can(me, 'Planner', 'ManufacturingEngineer') && o.status === 'Released') actions.push(['hold', 'Hold']);
  if (can(me, 'Planner', 'ManufacturingEngineer') && o.status === 'Held') actions.push(['resume', 'Resume']);
  if (can(me, 'Planner', 'ManufacturingEngineer') && !['Cancelled', 'Completed'].includes(o.status)) actions.push(['cancel', 'Cancel order']);

  const r = readiness.data?.output;
  const dd = dropDead.data?.output;
  page.innerHTML = `
    <a href="#/orders">← Production Orders</a>
    <div class="page-head">
      <div><h1>Order #${o.orderNumber}</h1><div class="sub">${esc(o.partNumber)} rev ${esc(o.revision)} × ${fmtNumber(o.quantity, 3)} · due ${esc(fmtDate(o.dueDate))} · priority ${o.priority}</div></div>
      <div class="spacer"></div>
      <span class="pill ${STATUS_TONE[o.status]}" style="font-size:13px">${esc(o.status)}</span>
      <div class="head-controls">${actions.map(([k, label]) => `<button class="btn ${k === 'release' ? 'primary' : k === 'cancel' ? 'danger' : ''}" data-act="${k}">${esc(label)}</button>`).join('')}</div>
    </div>
    <div class="notice">Created by ${esc(who(o.createdBy))}${o.releasedBy ? ` · released by ${esc(who(o.releasedBy))} at ${esc(fmtDateTime(o.releasedAt))}` : ''}. The person who sets up an order cannot release it.</div>
    <section class="grid-2">
      <div class="panel"><header><h2>Readiness now</h2></header>
        ${readiness.error ? errorBlock(readiness.error, 'readiness') : `
        <p>${esc(r.summary)}</p>
        <table class="table"><thead><tr><th>Op</th><th>Description</th><th>Status</th><th>Best machine</th></tr></thead><tbody>
        ${r.operations.map(op => `<tr><td>${op.sequence}</td><td>${esc(op.description)}</td>
          <td>${op.executable ? '<span class="pill ok">Executable</span>' : `<span class="pill bad">${esc(op.primaryConstraint ?? 'Blocked')}</span>`}</td>
          <td>${esc(op.bestMachine ?? '—')}</td></tr>
          <tr><td></td><td colspan="3"><ul class="evidence">${op.dimensions.map(dm => `<li><span class="${dm.outcome === 'Pass' ? 'c-ok' : dm.outcome === 'Fail' ? 'c-bad' : 'c-warn'}">${esc(dm.outcome)}</span> <b>${esc(dm.name)}</b> — ${esc(dm.detail)}</li>`).join('')}
            ${op.notEvaluated.length ? `<li class="faint">Not evaluated yet: ${esc(op.notEvaluated.join(', '))}</li>` : ''}</ul></td></tr>`).join('')}
        </tbody></table>`}
      </div>
      <div class="panel"><header><h2>Drop-dead dates</h2></header>
        ${dropDead.error ? errorBlock(dropDead.error, 'drop-dead dates') : `
        <p class="${dd.alreadyMissed ? 'c-bad' : ''}">${esc(dd.summary)}</p>
        <dl class="kv"><dt>Need by</dt><dd>${esc(fmtDateTime(dd.needBy))}</dd><dt>Ship by</dt><dd>${esc(fmtDateTime(dd.shipBy))}</dd>
          <dt>Material required by</dt><dd>${esc(fmtDateTime(dd.materialRequiredBy))}</dd></dl>
        <table class="table"><thead><tr><th>Step</th><th>Latest start</th><th>Latest finish</th><th>Basis</th></tr></thead><tbody>
          ${[...dd.operations, ...dd.purchaseBy].map(s => `<tr><td>${esc(s.label)}</td><td>${esc(fmtDateTime(s.latestStart))}</td><td>${esc(fmtDateTime(s.latestFinish))}</td><td class="muted">${esc(s.basis)}</td></tr>`).join('')}
        </tbody></table>`}
      </div>
    </section>
    <section class="grid-2">
      <div class="panel"><header><h2>Operations</h2></header>
        ${o.operations?.length ? `<table class="table"><thead><tr><th>Op</th><th>Work center</th><th>Activity</th><th>Machines</th><th>Planned</th><th>Good</th><th>Scrap</th><th>Status</th></tr></thead><tbody>
        ${o.operations.map(op => `<tr><td>${op.sequence}</td><td>${esc(op.workCenterCode)}</td><td>${esc(op.activityCode)}</td><td>${esc(op.eligibleMachines.join(', ') || '—')}</td><td>${fmtNumber(op.plannedMinutes / 60)} h</td>
          <td>${fmtNumber(op.goodQuantity, 3)} / ${fmtNumber(o.quantity, 3)}</td><td>${op.scrapQuantity > 0 ? `<span class="c-warn">${fmtNumber(op.scrapQuantity, 3)}</span>` : '0'}</td>
          <td><span class="pill ${op.status === 'Complete' ? 'ok' : op.status === 'InProgress' ? 'info' : 'none'}">${esc(op.status)}</span>${op.completedAt ? `<br><span class="faint">${esc(fmtDateTime(op.completedAt))}</span>` : op.startedAt ? `<br><span class="faint">since ${esc(fmtDateTime(op.startedAt))}</span>` : ''}</td></tr>`).join('')}
        </tbody></table>` : emptyBlock('Not released yet', 'Operations and materials are copied from the routing and BOM when the order is released.')}
      </div>
      <div class="panel"><header><h2>Materials</h2></header>
        ${o.materials?.length ? `<table class="table"><thead><tr><th>Line</th><th>Component</th><th>Rev</th><th>Required</th></tr></thead><tbody>
        ${o.materials.map(m => `<tr><td>${m.lineNumber}</td><td>${esc(m.componentPartNumber)}</td><td>${esc(m.componentRevision ?? '—')}</td><td>${fmtNumber(m.requiredQuantity, 3)} ${esc(m.unitOfMeasure)}</td></tr>`).join('')}
        </tbody></table>${materialStatus(o, material, issues)}` : emptyBlock('No materials yet', 'Copied from the BOM at release.')}
      </div>
    </section>`;

  const floor = await tryGet(`/api/orders/${number}/shopfloor`);
  if (floor.data && (floor.data.labor.length || floor.data.reports.length || floor.data.training.length)) {
    const f = floor.data;
    const section = document.createElement('section');
    section.className = 'grid-2';
    section.innerHTML = `
      <div class="panel"><header><h2>Direct labor</h2></header>
        <table class="table"><thead><tr><th>Op</th><th>Person</th><th>Role</th><th>Machine</th><th>From</th><th>To</th></tr></thead><tbody>
        ${f.labor.map(l => `<tr><td>${l.sequence}</td><td>${esc(who('person:' + l.personId))}</td><td>${esc(l.role)}</td><td>${esc(l.machineCode ?? '—')}</td><td>${esc(fmtDateTime(l.startedAt))}</td><td>${l.endedAt ? esc(fmtDateTime(l.endedAt)) : '<span class="pill info">working</span>'}</td></tr>`).join('')}
        </tbody></table></div>
      <div class="panel"><header><h2>Reports and training</h2></header>
        <table class="table"><thead><tr><th>Op</th><th>When</th><th>By</th><th>Good</th><th>Scrap</th></tr></thead><tbody>
        ${f.reports.map(r => `<tr><td>${r.sequence}${r.completedOperation ? ' ✓' : ''}</td><td>${esc(fmtDateTime(r.reportedAt))}</td><td>${esc(who('person:' + r.personId))}</td><td>${fmtNumber(r.goodQuantity, 3)}</td><td>${r.scrapQuantity > 0 ? `${fmtNumber(r.scrapQuantity, 3)} <span class="muted">${esc(r.scrapReason)}</span>` : '0'}</td></tr>`).join('') || '<tr><td colspan="5" class="muted">No quantities reported yet.</td></tr>'}
        </tbody></table>
        ${f.training.map(t => `<div class="notice" style="margin-top:8px">Supervised training, op ${t.sequence}: ${esc(who('person:' + t.traineeId))} under ${esc(who('person:' + t.trainerId))} for ${esc(t.qualifications.join(', '))} — ${t.evaluation ? `evaluated <b>${esc(t.evaluation)}</b>${t.evaluationNotes ? `: ${esc(t.evaluationNotes)}` : ''}` : t.endedAt ? 'awaiting the trainer\'s evaluation' : 'in progress'}. No qualification is granted by a session.</div>`).join('')}
      </div>`;
    page.append(section);
  }

  $$('[data-act]', page).forEach(b => b.addEventListener('click', async () => {
    const act = b.dataset.act;
    const done = await confirmWithReason({ title: `${b.textContent} order #${number}`, detail: act === 'release' ? 'Release copies the routing and BOM in force now. You cannot release an order you created.' : '', confirmLabel: b.textContent },
      reason => command(`/api/orders/${number}/${act}`, {}, reason));
    if (done) { toast(`Order #${number}: ${b.textContent.toLowerCase()} recorded.`); await orderDetail(page, ctx, number); }
  }));
}


const MATERIAL_TONE = { Ready: 'ok', Expected: 'warn', Shortfall: 'bad' };

/** Allocation of stock and open supply to this order, and what has been issued to it. */
function materialStatus(o, material, issues) {
  if (o.status !== 'Released') return '<div class="faint" style="margin-top:6px">Material is allocated once the order is released.</div>';
  const m = (material.data?.output ?? []).find(x => x.orderNumber === o.orderNumber);
  const moves = issues.data ?? [];
  return `${m ? `<div style="margin-top:8px"><span class="pill ${MATERIAL_TONE[m.state]}">${esc(m.state === 'Expected' ? 'Waiting for receipts' : m.state === 'Shortfall' ? 'Short' : m.state)}</span> ${esc(m.summary)}</div>
    <ul class="evidence">${m.lines.map(l => `<li><b>${esc(l.partNumber)}</b>: needs ${fmtNumber(l.required, 3)}, issued ${fmtNumber(l.issued, 3)}${l.remaining > 0 ? ` — still to issue ${fmtNumber(l.remaining, 3)}: ${l.allocations.map(a => `${fmtNumber(a.quantity, 3)} from ${esc(a.description)}${a.dueDate ? ` (due ${esc(fmtDate(a.dueDate))})` : ''}`).join(', ') || 'nothing allocated'}${l.shortfall > 0 ? ` <span class="c-bad">short ${fmtNumber(l.shortfall, 3)}</span>` : ''}` : ''}</li>`).join('')}</ul>`
    : material.error ? errorBlock(material.error, 'material status') : ''}
    ${moves.length ? `<div class="faint" style="margin-top:6px">${moves.length} inventory movement(s) for this order — <a href="#/inventory?tab=ledger">ledger</a>.</div>` : ''}`;
}
