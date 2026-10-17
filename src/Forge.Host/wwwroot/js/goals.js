import { tryGet, command } from './api.js';
import { who } from './people.js';
import { esc, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast } from './ui.js';
import { formModal, qty, canAction } from './forms.js';

// Goals (V3.7): production and delivery goals broken down, required-by dates cascading from the goal above,
// attainment against target, and a reforecast of the remaining work. Behind plan is not at risk.

const TONE = { Complete: 'ok', OnTrack: 'ok', BehindPlan: 'info', NoProjection: 'none', AtRisk: 'warn', Late: 'bad' };
const LABEL = { Complete: 'Complete', OnTrack: 'On track', BehindPlan: 'Behind plan, recoverable', NoProjection: 'No projection', AtRisk: 'At risk', Late: 'Late' };

/** The Goals panel on the Scheduling page. */
export async function appendGoals(page, me) {
  const section = document.createElement('section');
  section.className = 'panel';
  page.appendChild(section);
  const manage = canAction(me, 'planning.goals.manage');
  const load = async () => {
    const [res, orders, demand, goalRisk] = await Promise.all([tryGet('/api/planning/goals'), tryGet('/api/orders/'), tryGet('/api/planning/demand'), tryGet('/api/planning/goal-risk')]);
    const goals = res.data ?? [];
    const byId = new Map(goals.map(v => [v.goal.goalId, v]));
    const depth = v => { let d = 0, p = v.goal.parentGoalId; while (p && byId.has(p) && d < 8) { d++; p = byId.get(p).goal.parentGoalId; } return d; };
    const ordered = [];
    const walk = parent => goals.filter(v => (v.goal.parentGoalId ?? null) === parent || (parent === null && v.goal.parentGoalId && !byId.has(v.goal.parentGoalId)))
      .forEach(v => { if (!ordered.includes(v)) { ordered.push(v); walk(v.goal.goalId); } });
    walk(null);
    section.innerHTML = `<header><h2>Goals</h2><span class="spacer"></span>${manage ? '<button class="btn small" data-goal-add>+ Goal</button> <button class="btn small" data-reforecast>Reforecast</button>' : ''}</header>
      ${res.error ? errorBlock(res.error, 'goals') : !goals.length ? emptyBlock('No goals yet', 'Planners and program managers set production and delivery goals and break them down; the dates below follow the dates above.') : `
      <table class="table"><thead><tr><th>Goal</th><th>Required by</th><th>Reforecast</th><th class="num">Done / target</th><th class="num">Remaining min</th><th>State</th><th></th></tr></thead><tbody>
      ${ordered.map(v => { const g = v.goal; const last = v.history[0]; return `<tr>
        <td style="padding-left:${8 + depth(v) * 18}px">${depth(v) ? '└ ' : ''}<b>${esc(g.title)}</b> <span class="faint">#${g.goalId} · ${esc(g.kind)}${g.orderNumber ? ` · <a href="#/orders/${g.orderNumber}">order ${g.orderNumber}</a>` : ''}${g.demandId ? ` · demand ${g.demandId}` : ''}</span></td>
        <td class="nowrap">${esc(fmtDate(g.requiredBy))}</td>
        <td class="nowrap">${g.projected ? esc(fmtDateTime(g.projected)) : '—'}${last ? `<div class="faint">last reforecast ${esc(fmtDateTime(last.at))}: ${esc(LABEL[last.state] ?? last.state)}</div>` : ''}</td>
        <td class="num">${qty(g.done)} / ${qty(g.target)}<div class="faint">${qty(g.attainment * 100, 1)}%</div></td><td class="num">${qty(g.remainingMinutes, 0)}</td>
        <td><span class="pill ${TONE[g.state] ?? 'none'}">${esc(LABEL[g.state] ?? g.state)}</span>${g.behindPlan && g.state !== 'BehindPlan' ? ' <span class="pill info">behind plan</span>' : ''}<div class="faint">${esc(g.evidence.join(' '))}</div></td>
        <td class="nowrap">${manage ? `<button class="btn small ghost" data-goal-sub="${g.goalId}">+ Below</button> <button class="btn small ghost" data-goal-change="${g.goalId}">Change</button> <button class="btn small ghost" data-goal-close="${g.goalId}">Close</button>` : ''}</td></tr>`; }).join('')}
      </tbody></table>`}
      <h3 style="margin-top:22px">Released-order goal risk</h3>
      ${goalRisk.error ? errorBlock(goalRisk.error, 'released-order goal risk') : !goalRisk.data?.length ? emptyBlock('No released orders', 'Goal risk is assessed for released orders against the approved schedule and due date.') : `
      <table class="table"><thead><tr><th>Order</th><th>Part</th><th>Due</th><th>Projected completion</th><th>State</th><th>Behind plan</th><th>Evidence</th></tr></thead><tbody>
      ${goalRisk.data.map(line => `<tr><td><a href="#/orders/${line.orderNumber}">#${line.orderNumber}</a></td><td>${esc(line.partNumber)}</td><td>${esc(fmtDate(line.dueDate))}</td>
        <td>${line.projectedCompletion ? esc(fmtDateTime(line.projectedCompletion)) : 'No projection'}</td><td><span class="pill ${line.state === 'Late' || line.state === 'Unschedulable' ? 'bad' : line.atRisk ? 'warn' : 'ok'}">${esc(line.state)}</span></td>
        <td>${line.behindPlan ? 'Yes' : 'No'}</td><td class="muted">${esc((line.evidence ?? []).join(' '))}</td></tr>`).join('')}
      </tbody></table>`}
      <div class="faint" style="margin-top:6px">Behind plan compares done work with the approved schedule. At risk and late compare a fresh finite-capacity reforecast of the remaining work with the goal's own required-by date.</div>`;
    const orderOptions = (orders.data ?? []).filter(o => o.status !== 'Cancelled').map(o => [o.orderNumber, `#${o.orderNumber} — ${o.partNumber} × ${qty(o.quantity)} (${o.status})`]);
    const demandOptions = (demand.data ?? []).map(d => [d.demandId, `${d.demandId} — ${d.partNumber} × ${qty(d.quantity)} by ${d.needDate}${d.customer ? ` (${d.customer})` : ''}`]);
    const add = parent => formModal({
      title: parent ? `Goal below #${parent.goal.goalId}` : 'Set a goal', detail: 'A production goal follows a production order; a delivery goal follows a demand line; a goal with neither rolls up the goals below it.', confirmLabel: 'Set',
      fields: [
        { name: 'kind', label: 'Kind', type: 'select', value: parent?.goal.kind ?? 'Delivery', options: [['Delivery', 'Delivery (get it to the customer)'], ['Production', 'Production (make it)']] },
        { name: 'title', label: 'Title', required: true },
        { name: 'orderNumber', label: 'Follows order (production)', type: 'select', options: [['', '—'], ...orderOptions] },
        { name: 'demandId', label: 'Follows demand (delivery)', type: 'select', options: [['', '—'], ...demandOptions] },
        parent ? { name: 'leadDays', label: `Days before #${parent.goal.goalId}'s date (or give a date)`, type: 'number', min: 0, value: 0 } : { name: 'leadDays', type: 'html', html: '' },
        { name: 'requiredBy', label: parent ? 'Or its own required-by date' : 'Required by', type: 'date', required: !parent },
      ],
    }, (v, reason) => command('/api/planning/goals', {
      kind: v.kind, title: v.title, parentGoalId: parent?.goal.goalId ?? null, orderNumber: v.orderNumber ? Number(v.orderNumber) : null, demandId: v.demandId ? Number(v.demandId) : null,
      requiredBy: v.requiredBy || null, leadDays: v.requiredBy || !parent ? null : Number(v.leadDays ?? 0),
    }, reason), load);
    $$('[data-goal-add]', section).forEach(b => b.addEventListener('click', () => add(null)));
    $$('[data-goal-sub]', section).forEach(b => b.addEventListener('click', () => add(byId.get(Number(b.dataset.goalSub)))));
    $$('[data-goal-change]', section).forEach(b => b.addEventListener('click', () => {
      const g = byId.get(Number(b.dataset.goalChange)).goal;
      const own = g.parentGoalId == null || g.history?.length;
      formModal({
        title: `Change goal #${g.goalId}`, detail: 'Goals below that follow this one by a number of days move with it.', confirmLabel: 'Save', reasonRequired: true,
        fields: [
          { name: 'title', label: 'Title', value: g.title, required: true },
          { name: 'requiredBy', label: 'Required by (its own date)', type: 'date', value: g.requiredBy },
          ...(g.parentGoalId ? [{ name: 'leadDays', label: 'Or days before the goal above (leave the date empty)', type: 'number', min: 0 }] : []),
        ],
      }, (v, reason) => command(`/api/planning/goals/${g.goalId}`, { title: v.title, requiredBy: v.requiredBy || null, leadDays: v.requiredBy ? null : v.leadDays ?? (own ? null : 0) }, reason), load);
    }));
    $$('[data-goal-close]', section).forEach(b => b.addEventListener('click', async () => {
      const done = await confirmWithReason({ title: `Close goal #${b.dataset.goalClose}`, detail: 'Its forecasts stay on record. Close the goals below it first.', confirmLabel: 'Close' },
        reason => command(`/api/planning/goals/${b.dataset.goalClose}/close`, {}, reason));
      if (done) { toast(done.message); await load(); }
    }));
    $$('[data-reforecast]', section).forEach(b => b.addEventListener('click', async () => {
      const done = await confirmWithReason({ title: 'Reforecast goals', detail: 'Schedules the remaining work from now at finite capacity and records what it projects for every open goal.', confirmLabel: 'Reforecast', reasonRequired: false },
        reason => command('/api/planning/goals/reforecast', {}, reason));
      if (done) { toast(done.message); await load(); }
    }));
  };
  await load();
}
