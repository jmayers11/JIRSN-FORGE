import { tryGet, command } from './api.js';
import { esc, $$, fmtDate, fmtDateTime, confirmWithReason, toast } from './ui.js';
import { formModal, canAction } from './forms.js';
import { who } from './people.js';

// Released-order amendments (owner decisions 2026-10-31): a Manufacturing Engineer proposes, a Planner who did not propose it approves,
// and approval applies the change. Started operations and issued material are never rewritten.

const STATUS = { Proposed: 'warn', Approved: 'ok', Rejected: 'bad', Withdrawn: 'none' };

/** What an amendment changes, in plain words. */
export function amendmentSummary(c) {
  const out = [];
  if (c.newQuantity != null) out.push(`quantity → ${c.newQuantity}`);
  if (c.newDueDate) out.push(`due → ${fmtDate(c.newDueDate)}`);
  if (c.newPriority != null) out.push(`priority → ${c.newPriority}`);
  for (const s of c.removeOperations ?? []) out.push(`remove op ${s}`);
  for (const o of c.addOperations ?? []) out.push(`add op ${o.sequence} ${o.workCenterCode} ${o.description}`);
  for (const l of c.removeMaterialLines ?? []) out.push(`remove material line ${l}`);
  for (const m of c.addMaterials ?? []) out.push(`add ${m.componentPartNumber} × ${m.requiredQuantity}`);
  for (const m of c.changeMaterials ?? []) out.push(`material line ${m.lineNumber} → ${m.requiredQuantity}`);
  return out.join('; ') || 'no change';
}

export function amendmentsHtml(list, can, me) {
  return `<header><h2>Amendments</h2><span class="spacer"></span>${can.propose ? '<button class="btn small primary" data-propose-amendment>Propose amendment</button>' : ''}</header>
    ${list.length ? `<table class="table"><thead><tr><th>#</th><th>Status</th><th>Change</th><th>Why</th><th>Proposed</th><th>Decided</th><th></th></tr></thead><tbody>
    ${list.map(a => `<tr><td>${a.amendmentId}</td><td><span class="pill ${STATUS[a.status] ?? 'none'}">${esc(a.status)}</span></td><td>${esc(amendmentSummary(a.changes))}</td><td>${esc(a.rationale)}</td>
      <td>${esc(who(a.proposedBy))}<div class="faint">${esc(fmtDateTime(a.proposedAt))}</div></td>
      <td>${a.decidedBy ? `${esc(who(a.decidedBy))}<div class="faint">${esc(fmtDateTime(a.decidedAt))}${a.decisionNote ? ` · ${esc(a.decisionNote)}` : ''}</div>` : '—'}</td>
      <td class="nowrap">${a.status === 'Proposed' && can.approve && a.proposedBy !== me ? `<button class="btn small primary" data-amendment-approve="${a.amendmentId}">Approve</button> <button class="btn small" data-amendment-reject="${a.amendmentId}">Reject</button>` : ''}
        ${a.status === 'Proposed' && a.proposedBy === me ? `<button class="btn small" data-amendment-withdraw="${a.amendmentId}">Withdraw</button>` : ''}</td></tr>`).join('')}</tbody></table>`
      : '<p class="faint">No amendments. Operations not yet started, material not yet issued, the quantity (not below what was produced), due date and priority can change through an approved amendment.</p>'}`;
}

const numbers = text => String(text ?? '').split(/[\s,]+/).filter(Boolean).map(Number).filter(n => Number.isInteger(n) && n > 0);

export async function appendOrderAmendments(page, order, me, refresh) {
  if (order.status !== 'Released' && order.status !== 'Held') return;
  const can = { propose: canAction(me, 'execution.orders.amend.propose'), approve: canAction(me, 'execution.orders.amend.approve') };
  const list = await tryGet(`/api/orders/${order.orderNumber}/amendments`);
  if (list.error && !can.propose) return;
  const actor = me?.personId ? `person:${me.personId}` : null;
  const section = document.createElement('section');
  section.className = 'panel';
  section.innerHTML = amendmentsHtml(list.data ?? [], can, actor);
  page.append(section);
  section.querySelector('[data-propose-amendment]')?.addEventListener('click', () => formModal({
    title: `Propose an amendment to order #${order.orderNumber}`,
    detail: 'A planner who did not propose it approves it, and approval applies it. To reorder, remove a pending operation and add it at a new sequence.',
    confirmLabel: 'Propose',
    fields: [
      { name: 'newQuantity', label: `New quantity (now ${order.quantity})`, type: 'number', min: 0, step: 'any' },
      { name: 'newDueDate', label: `New due date (now ${fmtDate(order.dueDate)})`, type: 'date' },
      { name: 'newPriority', label: `New priority (now ${order.priority})`, type: 'number', min: 1, max: 9 },
      { name: 'removeOperations', label: 'Remove pending operations (sequences, comma-separated)' },
      { name: 'addOperations', label: 'Add operations', type: 'repeater', rowLabel: 'operation', columns: [
        { name: 'sequence', label: 'Seq', type: 'number', min: 1, required: true }, { name: 'description', label: 'Description', required: true },
        { name: 'workCenterCode', label: 'Work center', code: true, required: true }, { name: 'activityCode', label: 'Activity', code: true, required: true },
        { name: 'setupMinutes', label: 'Set-up min', type: 'number', min: 0, step: 'any', required: true }, { name: 'runMinutesPerUnit', label: 'Run min/unit', type: 'number', min: 0, step: 'any', required: true },
        { name: 'crewSize', label: 'Crew', type: 'number', min: 1, required: true }] },
      { name: 'removeMaterialLines', label: 'Remove material lines (line numbers, comma-separated)' },
      { name: 'addMaterials', label: 'Add materials', type: 'repeater', rowLabel: 'material', columns: [
        { name: 'componentPartNumber', label: 'Part', code: true, required: true }, { name: 'requiredQuantity', label: 'Qty', type: 'number', min: 0, step: 'any', required: true },
        { name: 'consumedAtSequence', label: 'At op', type: 'number', min: 1 }] },
      { name: 'changeMaterials', label: 'Change material quantities', type: 'repeater', rowLabel: 'line', columns: [
        { name: 'lineNumber', label: 'Line', type: 'number', min: 1, required: true }, { name: 'requiredQuantity', label: 'New qty', type: 'number', min: 0, step: 'any', required: true }] },
      { name: 'rationale', label: 'Why the order must change', type: 'textarea', required: true },
    ],
  }, v => command(`/api/orders/${order.orderNumber}/amendments`, {
    rationale: v.rationale,
    changes: {
      newQuantity: v.newQuantity ?? null, newDueDate: v.newDueDate || null, newPriority: v.newPriority ?? null,
      removeOperations: numbers(v.removeOperations), removeMaterialLines: numbers(v.removeMaterialLines),
      addOperations: (v.addOperations ?? []).map(o => ({ ...o, workCenterCode: String(o.workCenterCode).toUpperCase(), activityCode: String(o.activityCode).toUpperCase(), eligibleMachines: [], requiredQualifications: [] })),
      addMaterials: (v.addMaterials ?? []).map(m => ({ ...m, componentPartNumber: String(m.componentPartNumber).toUpperCase() })),
      changeMaterials: v.changeMaterials ?? [],
    },
  }), refresh));
  const decide = (id, approve) => confirmWithReason({ title: `${approve ? 'Approve' : 'Reject'} amendment ${id}`, detail: approve ? 'Approval applies the change to the order now.' : 'The order stays as it is.', confirmLabel: approve ? 'Approve and apply' : 'Reject', reasonRequired: !approve },
    reason => command(`/api/orders/amendments/${id}/decide`, { approve }, reason)).then(r => { if (r) { toast(r.message ?? 'Done'); refresh(); } });
  for (const b of $$('[data-amendment-approve]', section)) b.addEventListener('click', () => decide(b.dataset.amendmentApprove, true));
  for (const b of $$('[data-amendment-reject]', section)) b.addEventListener('click', () => decide(b.dataset.amendmentReject, false));
  for (const b of $$('[data-amendment-withdraw]', section)) b.addEventListener('click', () => confirmWithReason({ title: `Withdraw amendment ${b.dataset.amendmentWithdraw}`, confirmLabel: 'Withdraw' },
    reason => command(`/api/orders/amendments/${b.dataset.amendmentWithdraw}/withdraw`, {}, reason)).then(r => { if (r) { toast(r.message ?? 'Withdrawn'); refresh(); } }));
}
