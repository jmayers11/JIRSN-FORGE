import { tryGet, command } from '../api.js';
import { esc, $, $$, fmtDateTime, fmtDate, errorBlock, emptyBlock, confirmWithReason, toast } from '../ui.js';
import { categoryInfo } from '../model.js';
import { who } from '../people.js';

const DELIVERY_TONE = { OnTime: 'ok', AtRisk: 'warn', Late: 'bad', Unschedulable: 'bad' };
const CONSTRAINT_TONE = { Open: 'bad', Investigating: 'warn', Resolved: 'info', Verified: 'ok' };
const can = (me, ...roles) => roles.some(r => me.roles.includes(r));

export async function schedulingPage(page, ctx) {
  let showClosed = ctx.query.get('closed') === '1';
  let which = 'proposed';

  async function render() {
    const [approved, proposed, constraints] = await Promise.all([
      tryGet('/api/planning/schedules/approved'), tryGet('/api/planning/schedules/proposed'),
      tryGet(`/api/planning/constraints${showClosed ? '?includeClosed=true' : ''}`),
    ]);
    const me = ctx.me;
    const version = (which === 'approved' ? approved : proposed).data;
    const prop = proposed.data;
    const canApprove = can(me, 'Planner') && prop?.status === 'Proposed';
    const selfProposed = prop && prop.proposedBy === `person:${me.personId}`;

    const card = (title, r) => `
      <div class="panel"><header><h2>${title}</h2></header>
        ${r.error?.status === 404 || (!r.data && !r.error) ? emptyBlock('None', title === 'Approved schedule' ? 'Nothing approved yet. A second planner approves a proposal.' : 'No proposal yet.')
          : r.error ? errorBlock(r.error, title.toLowerCase())
          : `<dl class="kv">
              <dt>Version</dt><dd>#${r.data.versionId} · ${esc(r.data.status)}</dd>
              <dt>Horizon</dt><dd>${esc(fmtDateTime(r.data.from))} – ${esc(fmtDateTime(r.data.to))}</dd>
              <dt>Summary</dt><dd>${esc(r.data.summary)}</dd>
              <dt>Proposed</dt><dd>${esc(who(r.data.proposedBy))}, ${esc(fmtDateTime(r.data.proposedAt))}</dd>
              ${r.data.approvedBy ? `<dt>Approved</dt><dd>${esc(who(r.data.approvedBy))}, ${esc(fmtDateTime(r.data.approvedAt))}</dd>` : ''}
              <dt>Engine</dt><dd class="mono">${esc(r.data.engineVersion)} · in ${esc(String(r.data.inputFingerprint).slice(0, 12))} · out ${esc(String(r.data.outputFingerprint).slice(0, 12))}</dd>
            </dl>`}
      </div>`;

    const orders = version?.result?.orders ?? [];
    page.innerHTML = `
      <div class="page-head">
        <div><h1>Scheduling</h1><div class="sub">Finite-capacity schedules: proposed by one planner, approved by another. Every start time records what fixed it.</div></div>
        <div class="spacer"></div>
        <div class="head-controls">
          ${can(me, 'Planner') ? '<button class="btn" id="propose">Propose schedule</button>' : ''}
          ${canApprove ? `<button class="btn primary" id="approve" ${selfProposed ? 'title="You proposed this one; another planner approves it."' : ''}>Approve #${prop.versionId}</button>` : ''}
        </div>
      </div>
      <section class="grid-2">${card('Latest proposal', proposed)}${card('Approved schedule', approved)}</section>
      <section class="panel">
        <header><h2>Order delivery</h2><span class="spacer"></span>
          <div class="seg"><button data-which="proposed" class="${which === 'proposed' ? 'on' : ''}">Proposal</button><button data-which="approved" class="${which === 'approved' ? 'on' : ''}">Approved</button></div></header>
        ${!orders.length ? emptyBlock('No orders in this schedule', 'Released orders are scheduled when a planner proposes a schedule.') : `
        <table class="table"><thead><tr><th>Order</th><th>Part</th><th>Due</th><th>Projected completion</th><th>Delivery</th><th>Why</th></tr></thead><tbody>
        ${orders.map(o => `<tr><td><a href="#/orders/${o.orderNumber}">#${o.orderNumber}</a></td><td>${esc(o.partNumber)}</td><td>${esc(fmtDate(o.dueDate))}</td>
          <td>${esc(fmtDateTime(o.projectedCompletion))}</td><td><span class="pill ${DELIVERY_TONE[o.status]}">${esc(o.status)}</span></td><td class="muted">${esc(o.explanation)}</td></tr>`).join('')}
        </tbody></table>`}
      </section>
      <section class="panel">
        <header><h2>Constraints</h2><span class="spacer"></span>
          <label class="muted" style="font-size:12.5px"><input type="checkbox" id="closed" ${showClosed ? 'checked' : ''}> Include resolved and verified</label></header>
        ${constraints.error ? errorBlock(constraints.error, 'constraints') : !constraints.data.length ? emptyBlock('No constraints', 'The latest schedule run found nothing limiting the work.') : `
        <table class="table"><thead><tr><th>Constraint</th><th>Status</th><th>Window</th><th>Orders</th><th>Evidence and alternatives</th><th></th></tr></thead><tbody>
        ${constraints.data.map(c => `<tr>
          <td><b>${esc(categoryInfo(c.category).label)}</b><br><span class="muted">${esc(c.subject)}</span></td>
          <td><span class="pill ${CONSTRAINT_TONE[c.status]}">${esc(c.status)}</span>${c.owner ? `<br><span class="faint">${esc(who(c.owner))}</span>` : ''}${c.reopenedCount ? `<br><span class="c-warn">reopened ×${c.reopenedCount}</span>` : ''}</td>
          <td class="nowrap">${esc(fmtDateTime(c.windowFrom))}<br><span class="faint">to ${esc(fmtDateTime(c.windowUntil))}</span></td>
          <td>${(c.affectedOrders ?? []).map(n => `<a href="#/orders/${n}">#${n}</a>`).join(' ') || '—'}</td>
          <td>${esc(c.evidence)}${(c.alternatives ?? []).length ? `<br><span class="c-info">Could help: ${esc(c.alternatives.join('; '))}</span>` : ''}${c.resolution ? `<br><span class="c-ok">Resolution: ${esc(c.resolution)} (${esc(who(c.resolvedBy))})${c.verifiedBy ? ` · verified by ${esc(who(c.verifiedBy))}` : ''}</span>` : ''}</td>
          <td class="nowrap">${constraintActions(c, me)}</td></tr>`).join('')}
        </tbody></table>`}
      </section>`;

    $$('[data-which]', page).forEach(b => b.addEventListener('click', () => { which = b.dataset.which; render(); }));
    $('#closed', page)?.addEventListener('change', e => { showClosed = e.target.checked; render(); });
    $('#propose', page)?.addEventListener('click', async () => {
      const done = await confirmWithReason({
        title: 'Propose a schedule', detail: 'Schedules every released order on executable capacity and records the constraints found.', confirmLabel: 'Propose', reasonRequired: false,
        extraHtml: '<div class="field"><label for="days">Horizon (days)</label><input id="days" class="input" type="number" min="1" max="62" value="14"></div>',
      }, (reason, m) => command('/api/planning/schedules', { horizonDays: Number($('#days', m).value) }, reason));
      if (done) { toast(`Schedule #${done.entityId} proposed.`); which = 'proposed'; ctx.refreshBell(); render(); }
    });
    $('#approve', page)?.addEventListener('click', async () => {
      const done = await confirmWithReason({ title: `Approve schedule #${prop.versionId}`, detail: `Proposed by ${who(prop.proposedBy)}. The proposer cannot approve their own schedule.`, confirmLabel: 'Approve' },
        reason => command(`/api/planning/schedules/${prop.versionId}/approve`, {}, reason));
      if (done) { toast(`Schedule #${prop.versionId} approved.`); which = 'approved'; render(); }
    });
    $$('[data-c]', page).forEach(b => b.addEventListener('click', async () => {
      const [id, act] = b.dataset.c.split(':');
      const resolve = act === 'resolve';
      const done = await confirmWithReason({
        title: `${b.textContent} constraint`, confirmLabel: b.textContent, reasonRequired: act !== 'investigate',
        detail: act === 'verify' ? 'Confirms the resolution. The person who resolved it cannot verify it.' : act === 'investigate' ? 'You become the owner.' : '',
        extraHtml: resolve ? '<div class="field"><label for="res">What was done</label><input id="res" class="input"></div>' : '',
      }, (reason, m) => command(`/api/planning/constraints/${id}/${act}`, resolve ? { resolution: $('#res', m).value } : {}, reason));
      if (done) { toast('Recorded.'); ctx.refreshBell(); render(); }
    }));
  }

  await render();
}

function constraintActions(c, me) {
  if (!can(me, 'Planner', 'Supervisor')) return '';
  const b = (act, label) => `<button class="btn" data-c="${c.constraintId}:${act}">${label}</button>`;
  if (c.status === 'Open') return b('investigate', 'Investigate') + ' ' + b('resolve', 'Resolve');
  if (c.status === 'Investigating') return b('resolve', 'Resolve');
  if (c.status === 'Resolved') return b('verify', 'Verify');
  return '';
}
