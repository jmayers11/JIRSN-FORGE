import { tryGet, command, readPost } from './api.js';
import { esc, $$, fmtDateTime, errorBlock, emptyBlock, toast, confirmWithReason } from './ui.js';
import { formModal, qty, canAction } from './forms.js';
import { who } from './people.js';

// Planner-initiated inventory scrap (Sprint 10 §20). Asking, approving, Quality disposition and execution are separate steps,
// each by someone allowed to take it; a request never decides itself, and posted scrap is corrected only by a linked reversal.

export const SCRAP_REASONS = [
  ['Damaged', 'Damaged'], ['Obsolete', 'Obsolete'], ['Expired', 'Expired'], ['Contaminated', 'Contaminated'],
  ['QualityReject', 'Quality rejection'], ['Other', 'Other (describe in notes)'],
];

const STATE = {
  PendingApproval: ['warn', 'Waiting for Inventory approval'],
  PendingDisposition: ['warn', 'Waiting for Quality disposition'],
  Approved: ['info', 'Approved — not yet executed'],
  Rejected: ['none', 'Rejected'],
  Cancelled: ['none', 'Withdrawn'],
  Executed: ['ok', 'Executed'],
};

export function scrapStateLabel(state) {
  const [tone, label] = STATE[state] ?? ['none', state];
  return { tone, label };
}

const mineOf = (request, me) => request.requestedBy === `person:${me?.personId}`;

/**
 * The next steps this person may take on a request. The server enforces every rule again; this only hides buttons that would be refused.
 * @returns {string[]} any of approve, reject, dispose, cancel, execute, reverse
 */
export function scrapActions(request, me, all = []) {
  const actions = [];
  const mine = mineOf(request, me);
  if (request.state === 'PendingApproval' && !mine && canAction(me, 'inventory.approve')) actions.push('approve', 'reject');
  if (request.state === 'PendingDisposition' && !mine && canAction(me, 'quality.disposition')) actions.push('dispose');
  if ((request.state === 'PendingApproval' || request.state === 'PendingDisposition') && mine && canAction(me, 'inventory.scrap.request')) actions.push('cancel');
  if (request.state === 'Approved' && canAction(me, 'inventory.transact')) actions.push('execute');
  const corrected = all.some(r => r.correctsRequestId === request.requestId && r.state !== 'Rejected' && r.state !== 'Cancelled');
  if (request.kind === 'Scrap' && request.state === 'Executed' && !corrected && canAction(me, 'inventory.scrap.request')) actions.push('reverse');
  return actions;
}

/** The request body from the scrap form; throws a message a person can act on. */
export function scrapRequestBody(values) {
  const quantity = Number(values.quantity);
  if (!values.partNumber) throw new Error('Choose the part.');
  if (!values.locationCode) throw new Error('Choose the location.');
  if (!Number.isFinite(quantity) || quantity <= 0) throw new Error('Enter a quantity above zero.');
  if (values.reasonCode === 'Other' && !values.notes) throw new Error('Describe why the material is scrapped when the reason is Other.');
  const id = v => v == null || v === '' ? null : Number(v);
  return {
    partNumber: values.partNumber, locationCode: values.locationCode, lotNumber: values.lotNumber || null, status: values.status || 'Released', quantity,
    reasonCode: values.reasonCode, condition: values.condition || null, notes: values.notes || null,
    linkedOrderNumber: id(values.linkedOrderNumber), linkedPoNumber: id(values.linkedPoNumber), linkedWorkOrderId: id(values.linkedWorkOrderId),
    linkedNcr: values.linkedNcr || null, linkedSupplier: values.linkedSupplier || null,
  };
}

/** Plain-language summary of a scrap preview, shown before anything is submitted. */
export function scrapPreviewHtml(preview, quantity) {
  const e = preview.eligibility;
  const over = quantity > e.eligibleIfAllocationsReleased;
  const needsRelease = !over && quantity > e.eligible;
  const rows = [
    ['On hand', qty(e.onHand)], ['Allocated to material tasks', qty(e.allocatedToTasks)], ['Picked for material tasks', qty(e.pickedForTasks)],
    ['In other scrap requests', qty(e.pendingScrap)], ['Packed in containers', qty(e.packed)], ['Can be scrapped now', `<b>${qty(e.eligible)}</b>`],
  ];
  const value = preview.estimatedValue != null ? `${qty(preview.estimatedValue, 2)} ${esc(preview.currency ?? '')}` : '—';
  const orders = preview.planning?.affectedOrders ?? [];
  return `<div class="notice ${over ? 'bad' : needsRelease ? 'warn' : 'info'}">
      ${over ? `Only ${qty(e.eligibleIfAllocationsReleased)} can be scrapped from this stock; the request would be refused and nothing would change.`
        : needsRelease ? `${qty(quantity - e.eligible)} of this is allocated to material tasks ${e.allocatedTaskIds.map(t => `#${t}`).join(', ')}. Execution will need the allocations released; those tasks go back to Requested.`
        : 'This quantity is free to scrap.'}
    </div>
    <table class="table compact"><tbody>${rows.map(([k, v]) => `<tr><th>${esc(k)}</th><td class="num">${v}</td></tr>`).join('')}
      <tr><th>Estimated value</th><td class="num">${value}</td></tr></tbody></table>
    <div class="faint">${esc(preview.valueBasis ?? '')}</div>
    ${preview.planningRestricted ? '<div class="faint">You cannot read the schedule, so order impact is not shown.</div>'
      : preview.planning && !preview.planning.nettable ? '<div class="faint">This stock does not count for planning (not released, or not in a nettable location), so no order changes.</div>'
      : orders.length ? `<h3>Orders that would change</h3><table class="table compact"><thead><tr><th>Order</th><th>Material</th><th>Schedule</th><th class="num">Completion shift (h)</th></tr></thead><tbody>
          ${orders.map(o => `<tr><td><a href="#/orders/${o.orderNumber}">#${o.orderNumber}</a> ${esc(o.partNumber)}</td><td>${esc(o.beforeMaterialState)} → <b>${esc(o.proposedMaterialState)}</b></td>
            <td>${esc(o.beforeScheduleStatus)} → ${esc(o.proposedScheduleStatus)}</td><td class="num">${o.completionShiftHours == null ? '—' : qty(o.completionShiftHours, 2)}</td></tr>`).join('')}</tbody></table>`
      : '<div class="faint">No visible released order would change material state or projected completion.</div>'}`;
}

export async function loadScrap() {
  const [requests, policies] = await Promise.all([tryGet('/api/inventory/scrap-requests'), tryGet('/api/inventory/scrap-policies')]);
  return { requests, policies };
}

export function scrapTab(scrap, me) {
  const r = scrap?.requests;
  if (!r) return emptyBlock('Loading', '');
  if (r.error) return errorBlock(r.error, 'scrap requests');
  const canRequest = canAction(me, 'inventory.scrap.request');
  const all = r.data ?? [];
  const open = all.filter(x => ['PendingApproval', 'PendingDisposition', 'Approved'].includes(x.state));
  const closed = all.filter(x => !open.includes(x)).slice(0, 100);
  const row = x => {
    const { tone, label } = scrapStateLabel(x.state);
    const actions = scrapActions(x, me, all);
    const button = (a, text, primary = false) => `<button class="btn small${primary ? ' primary' : ''}" data-scrap-${a}="${x.requestId}">${text}</button>`;
    return `<tr><td>${x.requestId}${x.kind === 'Reversal' ? `<div class="faint">corrects #${x.correctsRequestId}</div>` : ''}</td>
      <td>${esc(x.partNumber)}${x.lotNumber ? ` <span class="faint">lot ${esc(x.lotNumber)}</span>` : ''}<div class="faint">${esc(x.locationCode)} · ${esc(x.stockStatus)}</div></td>
      <td class="num">${qty(x.quantity)}</td>
      <td>${esc(SCRAP_REASONS.find(([k]) => k === x.reasonCode)?.[1] ?? x.reasonCode)}${x.condition ? `<div class="faint">${esc(x.condition)}</div>` : ''}${x.notes ? `<div class="muted">${esc(x.notes)}</div>` : ''}
        ${x.linkedOrderNumber ? `<div class="faint">Order <a href="#/orders/${x.linkedOrderNumber}">#${x.linkedOrderNumber}</a></div>` : ''}${x.linkedNcr ? `<div class="faint">NCR ${esc(x.linkedNcr)}</div>` : ''}</td>
      <td><span class="pill ${tone}">${esc(label)}</span>${x.requiresQuality ? ' <span class="pill warn">Quality</span>' : ''}${x.holdTransactionId ? '<div class="faint">On hold while Quality decides</div>' : ''}</td>
      <td>${esc(who(x.requestedBy))}<div class="faint">${esc(fmtDateTime(x.requestedAt))}</div></td>
      <td>${x.decidedBy ? `${esc(x.policyId ? `Site rule ${x.policyId}` : who(x.decidedBy))}<div class="faint">${esc(fmtDateTime(x.decidedAt))}</div>` : '—'}
        ${x.executedBy ? `<div>Executed by ${esc(who(x.executedBy))}</div><div class="faint">transaction ${x.transactionId}</div>` : ''}</td>
      <td class="nowrap">${[
        actions.includes('approve') ? button('approve', 'Approve', true) : '', actions.includes('reject') ? button('reject', 'Reject') : '',
        actions.includes('dispose') ? button('dispose', 'Disposition', true) : '', actions.includes('execute') ? button('execute', 'Execute', true) : '',
        actions.includes('cancel') ? button('cancel', 'Withdraw') : '', actions.includes('reverse') ? button('reverse', 'Correct…') : '',
        mineOf(x, me) && (x.state === 'PendingApproval' || x.state === 'PendingDisposition') ? '<span class="faint">yours — someone else decides</span>' : '',
      ].filter(Boolean).join(' ')}</td></tr>`;
  };
  const table = rows => `<table class="table"><thead><tr><th>#</th><th>Stock</th><th class="num">Qty</th><th>Why</th><th>State</th><th>Asked by</th><th>Decided / executed</th><th></th></tr></thead>
    <tbody>${rows.map(row).join('')}</tbody></table>`;
  const policies = scrap.policies?.data ?? [];
  return `<div class="row" style="justify-content:space-between;align-items:center;margin-bottom:8px">
      <div class="muted">Report material that should be scrapped. Quality-held or quality-related material waits for a Quality disposition; everything else needs an independent approval, then a separate execution.</div>
      ${canRequest ? '<button class="btn primary" data-scrap-new>Scrap inventory…</button>' : ''}
    </div>
    <h3>Open requests</h3>
    ${open.length ? table(open) : emptyBlock('No open scrap requests', 'Requests waiting for approval, Quality disposition or execution appear here.')}
    ${closed.length ? `<details><summary>History (${closed.length})</summary>${table(closed)}</details>` : ''}
    <h3>Direct-scrap site rules</h3>
    ${policies.length ? `<table class="table compact"><thead><tr><th>#</th><th>Role</th><th>Reason</th><th class="num">Max qty</th><th class="num">Max value</th><th>State</th><th></th></tr></thead><tbody>
      ${policies.map(p => `<tr><td>${p.policyId}</td><td>${esc(p.roleName)}</td><td>${esc(p.reasonCode ?? 'Any non-quality reason')}</td><td class="num">${p.maxQuantity == null ? '—' : qty(p.maxQuantity)}</td>
        <td class="num">${p.maxValue == null ? '—' : qty(p.maxValue, 2)}</td><td>${esc(p.state)}</td>
        <td>${p.state === 'Proposed' && canAction(me, 'inventory.approve') && p.proposedBy !== `person:${me?.personId}` ? `<button class="btn small primary" data-policy-approve="${p.policyId}">Approve</button> <button class="btn small" data-policy-reject="${p.policyId}">Reject</button>` : ''}
          ${p.state === 'Active' && canAction(me, 'inventory.manage') ? `<button class="btn small" data-policy-retire="${p.policyId}">Retire</button>` : ''}</td></tr>`).join('')}</tbody></table>`
      : '<div class="faint">No site rules. Without one, every scrap needs an independent approval.</div>'}
    ${canAction(me, 'inventory.manage') ? '<button class="btn small" data-policy-new>Propose a site rule…</button>' : ''}`;
}

/**
 * @param {{ parts: Array, locations: Array, orders: Array, reload: Function, me: object, preset?: object }} options
 */
export async function openScrapRequest({ parts, locations, orders, reload, me, preset = {} }) {
  const fields = [
    { name: 'partNumber', label: 'Part', type: 'lookup', options: parts, value: preset.partNumber, required: true },
    { name: 'locationCode', label: 'Location', type: 'select', options: locations, value: preset.locationCode, required: true },
    { name: 'lotNumber', label: 'Lot or serial (tracked parts only)', value: preset.lotNumber ?? '', code: true },
    { name: 'status', label: 'Current quality status', type: 'select', value: preset.status ?? 'Released', options: [['Released', 'Released'], ['Inspection', 'Inspection'], ['Hold', 'Hold']] },
    { name: 'quantity', label: 'Quantity to scrap', type: 'number', min: 0, value: preset.quantity ?? '', required: true },
    { name: 'reasonCode', label: 'Reason', type: 'select', options: SCRAP_REASONS, value: 'Damaged' },
    { name: 'condition', label: 'Condition (optional)', placeholder: 'e.g. bent flange, water damage' },
    { name: 'notes', label: 'Notes (required for Other)', type: 'textarea' },
    { name: 'linkedOrderNumber', label: 'Related order (optional)', type: 'select', blank: 'None', options: orders },
    { name: 'linkedPoNumber', label: 'Related purchase order (optional)', type: 'number' },
    { name: 'linkedNcr', label: 'Related NCR (optional; makes it a Quality decision)', code: true },
    { name: 'linkedSupplier', label: 'Related supplier (optional)', code: true },
  ];
  return formModal({ title: 'Scrap inventory', detail: 'Step 1 of 2: describe the stock. You will see what can be scrapped, its estimated value and the orders it would affect before anything is submitted.', fields, confirmLabel: 'Preview' },
    async (values, reason) => {
      const body = scrapRequestBody(values);
      const preview = await readPost('/api/inventory/scrap-preview', { partNumber: body.partNumber, locationCode: body.locationCode, lotNumber: body.lotNumber, status: body.status, quantity: body.quantity });
      const direct = canAction(me, 'inventory.transact') && body.status === 'Released' && body.reasonCode !== 'QualityReject' && !body.linkedNcr;
      const extraHtml = `${scrapPreviewHtml(preview, body.quantity)}${direct ? '<label class="opt" style="margin-top:8px"><input type="checkbox" id="scrap-direct"> Scrap now under a site rule (refused if no active rule covers my role, reason, quantity and value)</label>' : ''}`;
      return confirmWithReason({ title: 'Scrap inventory — confirm', detail: 'Step 2 of 2. Nothing has changed yet.', confirmLabel: 'Submit request', extraHtml },
        (finalReason, modal) => command('/api/inventory/scrap-requests', { ...body, executeDirectly: !!modal.querySelector('#scrap-direct')?.checked }, finalReason || reason));
    }, reload);
}

export function bindScrapTab(page, { parts, locations, orders, reload, me, scrap }) {
  // formModal shows its own message; confirmWithReason does not, so only those results are toasted here.
  const run = async (promise, done) => { try { const result = await promise; if (result) { if (done) toast(result.message ?? done); await reload(); } } catch (error) { toast(error.message, true); } };
  $$('[data-scrap-new]', page).forEach(b => b.addEventListener('click', () => openScrapRequest({ parts, locations, orders, reload, me })));
  $$('[data-scrap-approve]', page).forEach(b => b.addEventListener('click', () => run(confirmWithReason({ title: `Approve scrap request ${b.dataset.scrapApprove}`, detail: 'Approval does not change stock. Execution is a separate, authorized step.', confirmLabel: 'Approve' },
    reason => command(`/api/inventory/scrap-requests/${b.dataset.scrapApprove}/approve`, {}, reason)), 'Approved.')));
  $$('[data-scrap-reject]', page).forEach(b => b.addEventListener('click', () => run(confirmWithReason({ title: `Reject scrap request ${b.dataset.scrapReject}`, confirmLabel: 'Reject' },
    reason => command(`/api/inventory/scrap-requests/${b.dataset.scrapReject}/reject`, {}, reason)), 'Rejected.')));
  $$('[data-scrap-cancel]', page).forEach(b => b.addEventListener('click', () => run(confirmWithReason({ title: `Withdraw scrap request ${b.dataset.scrapCancel}`, confirmLabel: 'Withdraw' },
    reason => command(`/api/inventory/scrap-requests/${b.dataset.scrapCancel}/cancel`, {}, reason)), 'Withdrawn.')));
  $$('[data-scrap-dispose]', page).forEach(b => b.addEventListener('click', () => run(formModal({
    title: `Quality disposition of scrap request ${b.dataset.scrapDispose}`, detail: 'Scrap it, or keep it: keeping rejects the request and releases the hold it placed.',
    fields: [{ name: 'decision', label: 'Disposition', type: 'select', options: [['scrap', 'Scrap'], ['keep', 'Do not scrap']], value: 'scrap' }, { name: 'note', label: 'Disposition note', type: 'textarea' }],
    confirmLabel: 'Record disposition', reasonRequired: true,
  }, (values, reason) => command(`/api/inventory/scrap-requests/${b.dataset.scrapDispose}/disposition`, { scrap: values.decision === 'scrap', note: values.note }, reason)), null)));
  $$('[data-scrap-execute]', page).forEach(b => b.addEventListener('click', () => {
    const request = (scrap?.requests?.data ?? []).find(x => String(x.requestId) === b.dataset.scrapExecute);
    run(formModal({
      title: `Execute ${request?.kind === 'Reversal' ? 'correction' : 'scrap'} ${b.dataset.scrapExecute}`,
      detail: request?.kind === 'Reversal' ? 'The quantity comes back on hold; Quality releases it as usual.' : 'This posts the scrap to the inventory ledger. It cannot be edited afterwards; mistakes are corrected by a linked reversal.',
      fields: request?.kind === 'Reversal' ? [] : [{ name: 'releaseAllocations', label: 'Material tasks', type: 'check', text: 'Release material-task allocations if they hold part of this stock' }],
      confirmLabel: 'Execute', reasonRequired: true,
    }, (values, reason) => command(`/api/inventory/scrap-requests/${b.dataset.scrapExecute}/execute`, { releaseAllocations: !!values.releaseAllocations }, reason)), null);
  }));
  $$('[data-scrap-reverse]', page).forEach(b => b.addEventListener('click', () => {
    const request = (scrap?.requests?.data ?? []).find(x => String(x.requestId) === b.dataset.scrapReverse);
    run(formModal({
      title: `Correct scrap request ${b.dataset.scrapReverse}`, detail: 'A linked compensating entry returns the quantity on hold. A different person approves it, and it is executed separately.',
      fields: [{ name: 'quantity', label: 'Quantity to return', type: 'number', min: 0, value: request?.quantity ?? '', required: true }],
      confirmLabel: 'Request correction', reasonRequired: true,
    }, (values, reason) => command(`/api/inventory/scrap-requests/${b.dataset.scrapReverse}/reversal`, { quantity: values.quantity }, reason)), null);
  }));
  $$('[data-policy-new]', page).forEach(b => b.addEventListener('click', () => run(formModal({
    title: 'Propose a direct-scrap site rule', detail: 'Members of the role may then scrap low-risk material without a separate approval. A different person must approve the rule. Quality-related scrap is never direct.',
    fields: [
      { name: 'roleName', label: 'Role', placeholder: 'e.g. InventoryController', required: true },
      { name: 'reasonCode', label: 'Reason', type: 'select', blank: 'Any non-quality reason', options: SCRAP_REASONS.filter(([k]) => k !== 'QualityReject') },
      { name: 'maxQuantity', label: 'Largest quantity per request', type: 'number', min: 0 },
      { name: 'maxValue', label: 'Largest estimated value per request', type: 'number', min: 0, hint: 'From the last purchase price; parts without a price never qualify when this is set.' },
    ],
    confirmLabel: 'Propose', reasonRequired: true,
  }, (values, reason) => command('/api/inventory/scrap-policies', { roleName: values.roleName, reasonCode: values.reasonCode || null, maxQuantity: values.maxQuantity, maxValue: values.maxValue }, reason)), null)));
  for (const [attr, approve] of [['policyApprove', true], ['policyReject', false]]) {
    $$(`[data-${attr === 'policyApprove' ? 'policy-approve' : 'policy-reject'}]`, page).forEach(b => b.addEventListener('click', () => run(confirmWithReason({
      title: `${approve ? 'Approve' : 'Reject'} site rule ${b.dataset[attr]}`, confirmLabel: approve ? 'Approve' : 'Reject',
    }, reason => command(`/api/inventory/scrap-policies/${b.dataset[attr]}/decide`, { approve }, reason)), 'Decided.')));
  }
  $$('[data-policy-retire]', page).forEach(b => b.addEventListener('click', () => run(confirmWithReason({ title: `Retire site rule ${b.dataset.policyRetire}`, confirmLabel: 'Retire' },
    reason => command(`/api/inventory/scrap-policies/${b.dataset.policyRetire}/retire`, {}, reason)), 'Retired.')));
}
