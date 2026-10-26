import { tryGet, command } from './api.js';
import { esc, $$, fmtDate, fmtDateTime, errorBlock } from './ui.js';
import { formModal, canAction, qty } from './forms.js';
import { who } from './people.js';

// Replacement orders (Sprint 10 WW-S10-35): a proposed order for scrapped or lost production, shown with the operator check made when it
// was proposed. A different planner approves (a planned order is created) or rejects it. Nobody is assigned by a proposal.

const where = c => `${esc(c.activityCode)} on ${esc(c.machineCode ?? c.workCenterCode)}`;

/** One routing operation's check: ready now, the qualified people who could be moved, and what each move leaves uncovered. */
export function operationCheckHtml(c, names = {}) {
  const state = c.readyNow ? '<span class="pill ok">Can run now</span>'
    : c.candidates.length ? `<span class="pill warn">${c.candidates.length} qualified option(s)</span>` : '<span class="pill bad">No qualified cover</span>';
  const people = c.candidates.map(p => `<li>${esc(names[p.personId] ?? p.personName)}${p.requiresSourceBackfill ? ' <span class="badge gray">needs backfill</span>' : ''}
      ${p.sourceImpacts.length ? `<div class="faint">Leaves: ${p.sourceImpacts.map(esc).join('; ')}</div>` : '<div class="faint">No source work stops.</div>'}
      ${p.evidence.filter(e => e.startsWith('Backfill:')).map(e => `<div class="faint">${esc(e)}</div>`).join('')}</li>`).join('');
  return `<tr><td>${c.sequence}</td><td>${esc(c.description)}<div class="faint">${where(c)}</div></td><td>${state}${!c.readyNow && c.currentConstraint ? `<div class="faint">${esc(c.currentConstraint)}</div>` : ''}</td>
    <td>${people ? `<ul class="plain">${people}</ul>` : '<span class="faint">—</span>'}</td></tr>`;
}

export const uncovered = p => p.checks.filter(c => !c.readyNow && !c.candidates.length);

export function proposalHtml(p, me) {
  const decide = p.status === 'Proposed' && canAction(me, 'execution.orders.release');
  const gaps = uncovered(p);
  return `<div class="card" data-replacement="${esc(p.proposalId)}">
    <div><b>${qty(p.quantity)} × ${esc(p.partNumber)}</b> <span class="faint">rev ${esc(p.revision)}</span> · due ${esc(fmtDate(p.dueDate))} · priority ${p.priority}
      ${p.ncrId ? ` · replaces scrap on NCR ${p.ncrId}` : p.replacesOrderNumber ? ` · replaces order #${p.replacesOrderNumber}` : ''}
      <span class="pill ${p.status === 'Approved' ? 'ok' : p.status === 'Rejected' ? 'bad' : 'info'}">${esc(p.status)}</span>
      ${p.orderNumber ? ` <a href="#/orders/${p.orderNumber}">order #${p.orderNumber}</a>` : ''}</div>
    <div class="faint">${esc(p.rationale)} — proposed by ${esc(who(p.proposedBy))}; operators checked ${esc(fmtDateTime(p.checkedAt))}</div>
    <table class="table"><thead><tr><th>Op</th><th>Operation</th><th>Now</th><th>Qualified people who could move</th></tr></thead><tbody>${p.checks.map(c => operationCheckHtml(c)).join('')}</tbody></table>
    ${gaps.length ? `<div class="pill bad">Nobody qualified for ${gaps.map(where).join(', ')}</div>` : ''}
    ${decide ? `<div><button class="btn small primary" data-replacement-decide="approve">Approve…</button> <button class="btn small" data-replacement-decide="reject">Reject…</button></div>` : ''}
  </div>`;
}

export async function appendReplacementOrders(page, me) {
  if (!canAction(me, 'execution.orders.read')) return;
  const host = document.createElement('section');
  host.className = 'panel';
  page.append(host);
  const load = async () => {
    const result = await tryGet('/api/planning/replacement-orders?open=true');
    const list = result.data ?? [];
    host.innerHTML = `<header><h2>Replacement orders to decide</h2><span class="spacer"></span>
        ${canAction(me, 'execution.orders.manage') ? '<button class="btn small" data-replacement-propose>Propose replacement…</button>' : ''}</header>
      ${result.error ? errorBlock(result.error, 'replacement orders') : list.length ? list.map(p => proposalHtml(p, me)).join('') : '<p class="faint">None waiting. Propose one for scrapped or lost production; Forge checks who can run each operation first.</p>'}`;
    host.querySelector('[data-replacement-propose]')?.addEventListener('click', () => formModal({
      title: 'Propose a replacement order', detail: 'Forge checks each operation of the released routing for qualified, authorized people and what moving them would leave unstaffed. A different planner decides it.',
      confirmLabel: 'Propose', reasonRequired: true,
      fields: [
        { name: 'partNumber', label: 'Part', code: true, required: true },
        { name: 'quantity', label: 'Quantity', type: 'number', min: 0, step: 'any', required: true },
        { name: 'dueDate', label: 'Due', type: 'date', required: true },
        { name: 'priority', label: 'Priority (1 highest)', type: 'number', min: 1, max: 9, value: 3, required: true },
        { name: 'ncrId', label: 'Scrapped NCR (optional)', type: 'number', min: 1 },
        { name: 'replacesOrderNumber', label: 'Order it replaces (optional)', type: 'number', min: 1 },
        { name: 'rationale', label: 'Why', type: 'textarea', required: true },
      ],
    }, (v, reason) => command('/api/planning/replacement-orders', {
      ...v, partNumber: String(v.partNumber).trim().toUpperCase(), ncrId: v.ncrId || null, replacesOrderNumber: v.replacesOrderNumber || null,
    }, reason), load));
    $$('[data-replacement-decide]', host).forEach(button => button.addEventListener('click', () => {
      const card = button.closest('[data-replacement]');
      const proposal = list.find(p => p.proposalId === card.dataset.replacement);
      const approve = button.dataset.replacementDecide === 'approve';
      const gaps = uncovered(proposal);
      formModal({
        title: approve ? 'Approve the replacement order' : 'Reject the replacement order',
        detail: approve ? 'Creates a planned order; it is released and staffed through the normal steps.' : 'No order is created.',
        confirmLabel: approve ? 'Approve' : 'Reject', reasonRequired: true,
        fields: [
          { name: 'note', label: 'Decision note', type: 'textarea', required: true },
          ...(approve && gaps.length ? [{ name: 'acceptUncoveredOperations', label: 'Uncovered work', type: 'check', text: `Approve although nobody qualified could cover ${gaps.map(g => `op ${g.sequence}`).join(', ')}` }] : []),
        ],
      }, (v, reason) => command(`/api/planning/replacement-orders/${proposal.proposalId}/decide`, { approve, note: v.note, acceptUncoveredOperations: !!v.acceptUncoveredOperations }, reason), load);
    }));
  };
  await load();
}
