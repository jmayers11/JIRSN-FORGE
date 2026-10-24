import { tryGet, command, fetchFile } from '../api.js';
import { icon } from '../icons.js';
import { who } from '../people.js';
import { canAction, formModal } from '../forms.js';
import { appendAttachments, appendApplicability, fileUrl } from '../attachments.js';
import { appendOrderGenealogy, appendOrderReconciliation } from '../genealogy.js';
import { appendOrderMaterialFlow } from '../materialflow.js';
import { appendOrderQuantityStates } from '../actuals.js';
import { appendHolds } from '../executable.js';
import { esc, $, $$, fmtNumber, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast, tabifyPanels, enhanceTables } from '../ui.js';

const STATUS_TONE = { Planned: 'none', FirmPlanned: 'info', Released: 'ok', Held: 'warn', Cancelled: 'bad', Completed: 'ok' };
const DELIVERY_TONE = { OnTime: 'ok', AtRisk: 'warn', Late: 'bad', Unschedulable: 'bad' };


export async function ordersPage(page, ctx) {
  if (ctx.params[0]) return orderDetail(page, ctx, Number(ctx.params[0]));
  const part = ctx.query.get('part') ?? '';
  const query = ctx.query.get('q') ?? '';
  const [orders, schedule] = await Promise.all([tryGet('/api/orders/'), tryGet('/api/planning/schedules/approved')]);
  const delivery = new Map((schedule.data?.result?.orders ?? []).map(o => [o.orderNumber, o]));
  const canCreate = canAction(ctx.me, 'execution.orders.manage');
  const list = (orders.data ?? []).filter(o => !part || o.partNumber === part);

  page.innerHTML = `
    <div class="page-head">
      <div><h1>Production Orders</h1><div class="sub">${part ? `Part ${esc(part)} · <a href="#/orders">show all</a>` : 'Every order you are cleared to see. Program orders are hidden from people without that program.'}</div></div>
      <div class="spacer"></div>
      ${canCreate ? '<button class="btn primary" id="new-order">New order</button>' : ''}
    </div>
    <section class="panel">
      ${orders.error ? errorBlock(orders.error, 'production orders') : !list.length ? emptyBlock('No orders yet', 'Orders are created for released parts, then released by a different person.') : `
      <div class="order-search">
        <label class="field" for="order-search-input"><span>Search orders</span><input class="input" id="order-search-input" data-order-search-input type="search" autocomplete="off" placeholder="Order number, part, revision, or status"></label>
        <span class="muted" data-order-search-count aria-live="polite"></span>
      </div>
      <table class="table">
        <thead><tr><th>Order</th><th>Part</th><th>Qty</th><th>Due</th><th>Priority</th><th>Status</th><th>Delivery (approved schedule)</th><th></th></tr></thead>
        <tbody>${list.map(o => {
          const d = delivery.get(o.orderNumber);
          const searchable = `#${o.orderNumber} ${o.partNumber} ${o.revision} ${o.quantity} ${o.dueDate} ${o.priority} ${o.status} ${d?.status ?? ''}`;
          return `<tr class="click" data-order="${o.orderNumber}" data-order-search-row data-order-search="${esc(searchable)}">
            <td>#${o.orderNumber}</td><td>${esc(o.partNumber)} <span class="faint">rev ${esc(o.revision)}</span></td>
            <td>${fmtNumber(o.quantity, 3)}</td><td>${esc(fmtDate(o.dueDate))}</td><td>${o.priority}</td>
            <td><span class="pill ${STATUS_TONE[o.status]}">${esc(o.status)}</span>${o.programId ? ' <span class="badge gray">Program</span>' : ''}</td>
            <td>${d ? `<span class="pill ${DELIVERY_TONE[d.status]}" title="${esc(d.explanation)}">${esc(d.status)}</span> <span class="muted">${d.projectedCompletion ? esc(fmtDateTime(d.projectedCompletion)) : ''}</span>` : '<span class="faint">Not in an approved schedule</span>'}</td>
            <td class="chev">${icon('chevron')}</td></tr>`;
        }).join('')}</tbody>
      </table><div class="empty" data-order-search-empty hidden>No orders match this search.</div>`}
    </section>`;
  bindOrderSearch(page, query);
  enhanceTables(page, { filter: false });
  $$('[data-order]', page).forEach(r => r.addEventListener('click', () => { location.hash = `#/orders/${r.dataset.order}`; }));
  $('#new-order', page)?.addEventListener('click', () => newOrder());
}

/** Bind the always-visible production-order search, including single-row and no-match results. */
export function bindOrderSearch(root, initialQuery = '') {
  const input = $('[data-order-search-input]', root);
  if (!input) return 0;
  const rows = $$('[data-order-search-row]', root);
  const count = $('[data-order-search-count]', root);
  const empty = $('[data-order-search-empty]', root);
  input.value = initialQuery;
  const update = () => {
    const query = input.value.trim().toLocaleLowerCase();
    let visible = 0;
    for (const row of rows) {
      const matches = !query || (row.dataset.orderSearch ?? row.textContent).toLocaleLowerCase().includes(query);
      row.hidden = !matches;
      if (matches) visible++;
    }
    if (count) count.textContent = query ? `${visible} of ${rows.length} orders` : `${rows.length} ${rows.length === 1 ? 'order' : 'orders'}`;
    if (empty) empty.hidden = visible > 0;
    return visible;
  };
  input.addEventListener('input', update);
  update();
  return rows.length;
}

/** Applies item-wide and operation-specific documentation to one released operation. */
export function instructionsForOperation(documents, sequence) {
  return (documents ?? []).filter(item => item.operationSequence == null || Number(item.operationSequence) === Number(sequence));
}

/** Prefer the order's released revision; use current applicability only before order release. */
export function instructionRevision(item) {
  return item?.atRelease ?? item?.applicable ?? item?.pinnedRevision ?? null;
}

/**
 * Applicability states in which the order's revision may be printed for the floor: it applies now, it is pinned, or
 * it was released with the order before a newer revision took effect (work in progress continues on it). A mismatch,
 * an obsolete or unreleased revision, a missing revision or one awaiting review must never reach the floor as a print.
 */
export const PRINTABLE_STATES = new Set(['Current', 'Pinned', 'LegitimatelySuperseded']);

export function isPrintableInstruction(item) {
  return PRINTABLE_STATES.has(item?.state);
}

function printableMime(fileName) {
  const ext = String(fileName ?? '').split('.').at(-1).toLowerCase();
  return ({ pdf: 'application/pdf', png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', webp: 'image/webp', gif: 'image/gif', txt: 'text/plain', csv: 'text/csv' })[ext] ?? null;
}

async function orderWorkInstructions(applicability, order, me) {
  const byOperation = new Map();
  if (!applicability?.documents?.length || !canAction(me, 'engineering.read')) return byOperation;
  const needs = new Map();
  for (const item of applicability.documents) {
    const revision = instructionRevision(item);
    if (!item.documentNumber || !revision) continue;
    needs.set(`${item.documentNumber}\u0000${revision}`, { item, revision });
  }
  const resolved = await Promise.all([...needs.values()].map(async ({ item, revision }) => {
    const result = await tryGet(`/api/engineering/documents/${encodeURIComponent(item.documentNumber)}`);
    const document = result.data?.document;
    if (result.error || !document || document.type !== 'WorkInstruction' || document.authority === 'Reference') return null;
    const blocked = !isPrintableInstruction(item);
    const file = document.revisions?.find(candidate => candidate.revision === revision && candidate.fileName);
    if (!file && !blocked) return null;
    return {
      documentNumber: item.documentNumber, title: item.title || document.title, revision,
      fileName: file?.fileName ?? null, operationSequence: item.operationSequence, state: item.state,
      explanation: item.explanation, blocked,
    };
  }));
  for (const instruction of resolved.filter(Boolean)) {
    const sequences = instruction.operationSequence == null ? (order.operations ?? []).map(op => op.sequence) : [instruction.operationSequence];
    for (const sequence of sequences) {
      if (!byOperation.has(sequence)) byOperation.set(sequence, []);
      byOperation.get(sequence).push(instruction);
    }
  }
  return byOperation;
}

async function printOperationPacket(order, operation, instructions) {
  const popup = window.open('', '_blank');
  if (!popup) { toast('Allow pop-ups to open the work-instruction print view.', true); return; }
  popup.document.write(`<!doctype html><html><head><meta charset="utf-8"><title>Order ${esc(order.orderNumber)} · Op ${operation.sequence} work instructions</title><style>
    body{font:15px Arial,sans-serif;color:#17202a;margin:24px}header{display:flex;align-items:center;gap:12px;border-bottom:1px solid #aaa;padding-bottom:12px}header button{padding:8px 14px}.instruction{break-after:page;page-break-after:always;padding:18px 0}.instruction:last-child{break-after:auto;page-break-after:auto}iframe{display:block;width:100%;height:9.3in;border:0}.notice{padding:10px;background:#f1f3f5;border:1px solid #aaa}.muted{color:#555;font-size:12px}@media print{.screen-only{display:none!important}body{margin:0}.instruction{padding:0}}
    </style></head><body><header><div><b>Order #${esc(order.orderNumber)} · Operation ${operation.sequence}</b><div>${esc(order.partNumber)} rev ${esc(order.revision)} · ${esc(operation.description)}</div></div><button class="screen-only" data-print type="button">Print this operation</button></header><p class="muted">Work-instruction revisions selected from the order's recorded documentation applicability.</p><main data-print-body><p>Loading work instructions…</p></main></body></html>`);
  popup.document.close();
  const records = await Promise.all(instructions.map(async instruction => {
    try {
      const { blob, name } = await fetchFile(fileUrl(instruction.documentNumber, instruction.revision), instruction.fileName);
      const mime = printableMime(name || instruction.fileName);
      const preview = mime ? URL.createObjectURL(new Blob([blob], { type: mime })) : null;
      const downloadUrl = preview ?? URL.createObjectURL(blob);
      return { instruction, name, preview, downloadUrl, error: null };
    } catch (error) { return { instruction, error }; }
  }));
  const body = popup.document.querySelector('[data-print-body]');
  if (!body) { popup.close(); toast('The print view could not be prepared.', true); return; }
  body.innerHTML = records.map(({ instruction, name, preview, downloadUrl, error }) => error
    ? `<section class="instruction"><h2>${esc(instruction.documentNumber)} rev ${esc(instruction.revision)}</h2><div class="notice">Could not load this file: ${esc(error.message)}</div></section>`
    : `<section class="instruction"><h2>${esc(instruction.documentNumber)} rev ${esc(instruction.revision)} — ${esc(instruction.title)}</h2><p>${esc(name || instruction.fileName)} · ${esc(instruction.state)}</p>${preview ? `<iframe title="${esc(instruction.title)}" src="${esc(preview)}"></iframe>` : `<div class="notice">This file type cannot be previewed in the browser. Open the downloaded file in its associated application to print it.</div>`}<p><a href="${esc(downloadUrl)}" download="${esc(name || instruction.fileName)}">Download this work instruction</a></p></section>`).join('');
  popup.document.querySelector('[data-print]')?.addEventListener('click', () => { popup.focus(); popup.print(); });
  popup.addEventListener?.('beforeunload', () => records.forEach(record => {
    if (record.preview) URL.revokeObjectURL(record.preview);
    if (record.downloadUrl && record.downloadUrl !== record.preview) URL.revokeObjectURL(record.downloadUrl);
  }));
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
  const [order, readiness, dropDead, material, issues, applicability] = await Promise.all([
    tryGet(`/api/orders/${number}`), tryGet(`/api/orders/${number}/readiness`), tryGet(`/api/orders/${number}/drop-dead-dates`),
    tryGet('/api/planning/material'), tryGet(`/api/inventory/transactions?order=${number}`), tryGet(`/api/planning/applicability?order=${number}`),
  ]);
  if (order.error) { page.innerHTML = `<a href="#/orders">← Production Orders</a>${errorBlock(order.error, `order #${number}`)}`; return; }
  const o = order.data;
  const me = ctx.me;
  const instructions = await orderWorkInstructions(applicability.data, o, me);
  const actions = [];
  if (canAction(me, 'execution.orders.manage') && o.status === 'Planned') actions.push(['firm', 'Firm']);
  if (canAction(me, 'execution.orders.release') && ['Planned', 'FirmPlanned'].includes(o.status)) actions.push(['release', 'Release']);
  if (canAction(me, 'execution.orders.manage') && o.status === 'Released') actions.push(['hold', 'Hold']);
  if (canAction(me, 'execution.orders.manage') && o.status === 'Held') actions.push(['resume', 'Resume']);
  if (canAction(me, 'execution.orders.manage') && !['Cancelled', 'Completed'].includes(o.status)) actions.push(['cancel', 'Cancel order']);

  const r = readiness.data?.output;
  const dd = dropDead.data?.output;
  page.innerHTML = `
    <a href="#/orders">← Production Orders</a>
    <div class="page-head object-summary">
      <div><h1>Order #${o.orderNumber}</h1><div class="sub">${esc(o.partNumber)} rev ${esc(o.revision)} × ${fmtNumber(o.quantity, 3)} · due ${esc(fmtDate(o.dueDate))} · priority ${o.priority}</div></div>
      <div class="spacer"></div>
      <span class="pill ${STATUS_TONE[o.status]}" style="font-size:13px">${esc(o.status)}</span>
      <div class="head-controls">${actions.map(([k, label]) => `<button class="btn ${k === 'release' ? 'primary' : k === 'cancel' ? 'danger' : ''}" data-act="${k}">${esc(label)}</button>`).join('')}
        ${canAction(me, 'execution.orders.manage') && !['Cancelled', 'Completed'].includes(o.status) ? '<button class="btn outline" id="change-priority">Change priority</button>' : ''}</div>
    </div>
    <section class="order-facts panel" aria-label="Work order specifics">
      <div><span>Part / revision</span><b>${esc(o.partNumber)} · ${esc(o.revision)}</b></div>
      <div><span>Order quantity</span><b>${fmtNumber(o.quantity, 3)}</b></div>
      <div><span>Due date</span><b>${esc(fmtDate(o.dueDate))}</b></div>
      <div><span>Priority</span><b>${o.priority} <small>(1 is highest)</small></b></div>
      <div><span>Order status</span><b>${esc(o.status)}${o.heldFrom ? ` · held from ${esc(o.heldFrom)}` : ''}</b></div>
      ${o.programId ? `<div><span>Program</span><b>${esc(o.programId)}</b></div>` : ''}
      ${o.wbsCode ? `<div><span>WBS</span><b>${esc(o.wbsCode)}</b></div>` : ''}
      ${o.contractId ? `<div><span>Contract / CLIN</span><b>${o.contractId} · ${esc(o.contractClin ?? '—')}</b></div>` : ''}
      <div><span>Created by</span><b>${esc(who(o.createdBy))}</b></div>
      <div><span>Released</span><b>${o.releasedBy ? `${esc(who(o.releasedBy))} · ${esc(fmtDateTime(o.releasedAt))}` : 'Not released'}</b></div>
    </section>
    <div class="notice">The person who sets up an order cannot release it.</div>
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
    <section class="panel"><header><h2>Operations</h2><span class="spacer"></span><span class="muted">Routing copied at release · work instructions use the order's recorded revision</span></header>
        ${o.operations?.length ? `<table class="table"><thead><tr><th>Op</th><th>Description</th><th>Work center</th><th>Activity</th><th>Machines</th><th>Set-up family</th><th>Planned</th><th>Good</th><th>Scrap</th><th>Status</th><th>Work instructions</th></tr></thead><tbody>
        ${o.operations.map(op => { const forOperation = instructionsForOperation(instructions.get(op.sequence) ?? [], op.sequence); const opInstructions = forOperation.filter(item => !item.blocked); const opBlocked = forOperation.filter(item => item.blocked); return `<tr><td>${op.sequence}</td><td>${esc(op.description)}</td><td>${esc(op.workCenterCode)}</td><td>${esc(op.activityCode)}</td><td>${esc(op.eligibleMachines.join(', ') || '—')}</td><td>${esc(op.setupFamily ?? '—')}</td><td>${fmtNumber(op.plannedMinutes / 60)} h</td>
          <td>${fmtNumber(op.goodQuantity, 3)} / ${fmtNumber(o.quantity, 3)}</td><td>${op.scrapQuantity > 0 ? `<span class="c-warn">${fmtNumber(op.scrapQuantity, 3)}</span>` : '0'}</td>
          <td><span class="pill ${op.status === 'Complete' ? 'ok' : op.status === 'InProgress' ? 'info' : 'none'}">${esc(op.status)}</span>${op.completedAt ? `<br><span class="faint">${esc(fmtDateTime(op.completedAt))}</span>` : op.startedAt ? `<br><span class="faint">since ${esc(fmtDateTime(op.startedAt))}</span>` : ''}</td>
          <td>${opInstructions.length ? `<button class="btn small" data-print-operation="${op.sequence}" title="Print ${opInstructions.length} applicable work instruction${opInstructions.length === 1 ? '' : 's'}">Print (${opInstructions.length})</button>` : opBlocked.length ? '' : '<span class="faint" title="No accessible printable Work Instruction file was found for this operation">No printable instruction</span>'}${opBlocked.map(item => `<div class="c-bad" data-instruction-blocked="${esc(item.documentNumber)}" title="${esc(item.explanation ?? '')}">${esc(item.documentNumber)} rev ${esc(item.revision)}: ${esc(item.state)}, not printable. See Documents.</div>`).join('')}</td></tr>`; }).join('')}
        </tbody></table>` : emptyBlock('Not released yet', 'Operations and materials are copied from the routing and BOM when the order is released.')}
    </section>
      <section class="panel"><header><h2>Bill of materials</h2><span class="spacer"></span><span class="muted">Component revisions and quantities copied at release</span></header>
        ${o.status === 'Released' && o.materials?.length && canAction(me, 'inventory.material-request') ? '<button class="btn small primary" id="stage-remaining-materials" style="margin-bottom:10px">Request remaining BOM materials</button>' : ''}
        ${o.materials?.length ? `<table class="table"><thead><tr><th>Line</th><th>Component</th><th>Rev</th><th>Required</th></tr></thead><tbody>
        ${o.materials.map(m => `<tr><td>${m.lineNumber}</td><td>${esc(m.componentPartNumber)}</td><td>${esc(m.componentRevision ?? '—')}</td><td>${fmtNumber(m.requiredQuantity, 3)} ${esc(m.unitOfMeasure)}</td></tr>`).join('')}
        </tbody></table>${materialStatus(o, material, issues)}` : emptyBlock('No materials yet', 'Copied from the BOM at release.')}
      </section>`;

  const quality = await tryGet(`/api/quality/orders/${number}`);
  if (quality.data && (quality.data.plans.length || quality.data.inspections.length || quality.data.quality.openNcrs.length)) {
    const q = quality.data;
    const passed = new Set(q.quality.passedSequences);
    const section = document.createElement('section');
    section.className = 'panel';
    section.innerHTML = `<header><h2>Quality</h2><span class="spacer"></span><a class="btn small" href="#/quality">Open Quality</a></header>
      ${q.quality.openNcrs.map(n => `<div class="notice bad">NCR ${n.ncrId} (${esc(n.defect)}) is ${n.status === 'AwaitingApproval' ? 'waiting for use-as-is approval' : 'not dispositioned'}: the order is held until it is decided.</div>`).join('')}
      <table class="table"><thead><tr><th>After op</th><th>Inspection</th><th>Status</th><th>Inspections</th></tr></thead><tbody>
      ${q.plans.map(p => {
        const done = q.inspections.filter(i => i.sequence === p.sequence);
        return `<tr><td>${p.sequence}</td><td>${esc(p.title)}</td><td>${passed.has(p.sequence) ? '<span class="pill ok">Passed</span>' : done.length ? '<span class="pill bad">Failed</span>' : '<span class="pill none">Not inspected</span>'}</td>
          <td>${done.map(i => `${esc(i.result)} ${esc(fmtDateTime(i.at))} by ${esc(who(i.inspector))}`).join('<br>') || '—'}</td></tr>`;
      }).join('')}</tbody></table>`;
    page.append(section);
  }

  const floor = await tryGet(`/api/orders/${number}/shopfloor`);
  if (floor.data && (floor.data.labor.length || floor.data.reports.length || floor.data.training.length || floor.data.laborMetrics?.length)) {
    const f = floor.data;
    const section = document.createElement('section');
    section.className = 'grid-2';
    section.innerHTML = `
      <div class="panel"><header><h2>Direct labor</h2></header>
        <table class="table"><thead><tr><th>Op</th><th>Person</th><th>Role</th><th>Phase</th><th>Machine</th><th>From</th><th>To</th></tr></thead><tbody>
        ${f.labor.map(l => `<tr><td>${l.sequence}</td><td>${esc(who('person:' + l.personId))}</td><td>${esc(l.role)}</td><td>${esc(l.phase ?? 'Run')}</td><td>${esc(l.machineCode ?? '—')}</td><td>${esc(fmtDateTime(l.startedAt))}</td><td>${l.endedAt ? esc(fmtDateTime(l.endedAt)) : '<span class="pill info">working</span>'}</td></tr>`).join('')}
        </tbody></table></div>
      <div class="panel"><header><h2>Reports and training</h2></header>
        <table class="table"><thead><tr><th>Op</th><th>When</th><th>By</th><th>Good</th><th>Scrap</th></tr></thead><tbody>
        ${f.reports.map(r => `<tr><td>${r.sequence}${r.completedOperation ? ' ✓' : ''}</td><td>${esc(fmtDateTime(r.reportedAt))}</td><td>${esc(who('person:' + r.personId))}</td><td>${fmtNumber(r.goodQuantity, 3)}</td><td>${r.scrapQuantity > 0 ? `${fmtNumber(r.scrapQuantity, 3)} <span class="muted">${esc(r.scrapReason)}</span>` : '0'}</td></tr>`).join('') || '<tr><td colspan="5" class="muted">No quantities reported yet.</td></tr>'}
        </tbody></table>
        ${f.training.map(t => `<div class="notice" style="margin-top:8px">Supervised training, op ${t.sequence}: ${esc(who('person:' + t.traineeId))} under ${esc(who('person:' + t.trainerId))} for ${esc(t.qualifications.join(', '))} — ${t.evaluation ? `evaluated <b>${esc(t.evaluation)}</b>${t.evaluationNotes ? `: ${esc(t.evaluationNotes)}` : ''}` : t.endedAt ? 'awaiting the trainer\'s evaluation' : 'in progress'}. No qualification is granted by a session.</div>`).join('')}
      </div>`;
    page.append(section);
    if (f.laborMetrics?.length) {
      const metrics = document.createElement('section');
      metrics.className = 'panel';
      metrics.innerHTML = `<header><h2>Operation target vs actual labor</h2><span class="spacer"></span><span class="muted">Released routing targets and recorded person-hours</span></header>
        <div class="table-wrap"><table class="table orders-labor-metrics"><thead><tr><th>Operation</th><th>Planned setup / run</th><th>Crew</th><th>Recorded operator</th><th>Actual phases · setup / run / inspect / rework</th><th>Training</th><th>Reported machine stops</th><th>Reported good / scrap</th><th>Earned standard / efficiency</th><th>Completed variance</th></tr></thead><tbody>
        ${f.laborMetrics.map(metric => `<tr><td>${metric.sequence} · ${esc(metric.workCenterCode)}</td><td>${metric.plannedSetupLaborHours == null || metric.plannedRunLaborHours == null ? 'N/A' : `${fmtNumber(metric.plannedSetupLaborHours)} / ${fmtNumber(metric.plannedRunLaborHours)} h`}<div class="faint">${metric.plannedTotalLaborHours == null ? 'Routing setup target unavailable' : `${fmtNumber(metric.plannedTotalLaborHours)} h total`}</div></td><td>${metric.crewSize}</td><td>${fmtNumber(metric.recordedOperatorHours)} h</td><td>${fmtNumber(metric.recordedSetupHours)} / ${fmtNumber(metric.recordedRunHours)} / ${fmtNumber(metric.recordedInspectionHours)} / ${fmtNumber(metric.recordedReworkHours)} h</td><td>${fmtNumber(metric.recordedTrainingHours)} h</td><td>${fmtNumber(metric.recordedStoppageMinutes)} min<div class="faint">${esc(metric.stoppageContext)}</div></td><td>${fmtNumber(metric.reportedGoodQuantity, 3)} / ${fmtNumber(metric.reportedScrapQuantity, 3)}</td><td><span class="pill none">N/A</span><div class="faint">${esc(metric.earnedStandardStatus)}</div></td><td>${metric.completedScheduleVarianceHours == null ? '<span class="pill none">N/A</span>' : `${fmtNumber(metric.completedScheduleVarianceHours)} h`}<div class="faint">${esc(metric.varianceContext)}</div></td></tr>`).join('')}
        </tbody></table></div>
        <p class="faint workforce-intelligence-note">Reported good quantity is not quality acceptance. Efficiency remains N/A until approved accepted-output evidence is linked to each operation. Phase hours come from the operator-selected labor phase; training remains separate. Linked stop categories and reason codes are contextual records, not confirmed causes or operator fault.</p>`;
      page.append(metrics);
    }
  }

  if (o.programId) await appendOrderCommitment(page, o, ctx);
  await appendOrderGenealogy(page, o.orderNumber);
  if (o.status !== 'Planned' && o.status !== 'FirmPlanned') await appendOrderQuantityStates(page, o, me);
  if (o.status !== 'Planned' && o.status !== 'FirmPlanned') await appendHolds(page, me, o);
  await appendOrderMaterialFlow(page, o.orderNumber, canAction(me, 'inventory.transact'));
  await appendOrderReconciliation(page, o.orderNumber, canAction(me, 'inventory.transact'));
  // After a documentation reissue the whole order view is rebuilt, so the print buttons use the reissued revisions.
  await appendApplicability(page, o.orderNumber, me, applicability, () => orderDetail(page, ctx, number));
  await appendAttachments(page, { type: 'ProductionOrder', id: o.orderNumber, me, title: 'Documentation',
    note: 'Documents this order must also follow, and files kept with it (customer PO, certificates, photos). Attaching never changes which revision of the item applies.' });

  tabifyPanels(page, [
    { label: 'Summary', match: ['readiness now', 'drop-dead dates', 'contract commitment'] },
    { label: 'Operations', match: ['operations'] },
    { label: 'Bill of materials', match: ['bill of materials'] },
    { label: 'Execution', match: ['direct labor', 'reports and training', 'target vs actual labor', 'quantity states'] },
    { label: 'Material', match: ['as built and genealogy', 'material to the machine', 'what became of the material'] },
    { label: 'Quality', match: ['quality', 'holds'] },
    { label: 'Documents', match: ['applicable documentation', 'documentation'] },
  ]);
  enhanceTables(page);

  $$('[data-print-operation]', page).forEach(button => button.addEventListener('click', () => {
    const operation = o.operations.find(item => String(item.sequence) === button.dataset.printOperation);
    if (operation) printOperationPacket(o, operation, instructionsForOperation(instructions.get(operation.sequence) ?? [], operation.sequence).filter(item => !item.blocked));
  }));

  $$('[data-act]', page).forEach(b => b.addEventListener('click', async () => {
    const act = b.dataset.act;
    const done = await confirmWithReason({ title: `${b.textContent} order #${number}`, detail: act === 'release' ? 'Release copies the routing and BOM in force now. You cannot release an order you created.' : '', confirmLabel: b.textContent },
      reason => command(`/api/orders/${number}/${act}`, {}, reason));
    if (done) { toast(`Order #${number}: ${b.textContent.toLowerCase()} recorded.`); await orderDetail(page, ctx, number); }
  }));
  $('#stage-remaining-materials', page)?.addEventListener('click', async () => {
    try {
      const response = await tryGet('/api/inventory/locations');
      if (response.error) throw new Error(response.error.message);
      const centers = new Set((o.operations ?? []).map(operation => operation.workCenterCode));
      const locations = (response.data ?? []).filter(location => location.active && location.inCompanyCustody
        && (location.kind === 'Staging' || location.kind === 'Production' || location.kind === 'MachineSide' && location.pointOfUse)
        && (!location.workCenterCode || centers.has(location.workCenterCode)));
      if (!locations.length) throw new Error('No active staging or point-of-use destination is configured at this order’s work centers.');
      const result = await formModal({
        title: `Stage remaining BOM demand — order #${o.orderNumber}`,
        detail: 'Forge compares the released BOM with recorded issues and existing material tasks, then creates tasks only for uncovered demand. Submitting tasks does not deduct stock; the stockroom allocates, picks, dispatches, and confirms receipt separately.',
        confirmLabel: 'Create staging tasks', reasonRequired: true,
        fields: [{ name: 'toLocation', label: 'Destination', type: 'select', required: true, value: locations[0].code,
          options: locations.map(location => [location.code, `${location.name} · ${location.code}`]) }],
      }, (values, reason) => command(`/api/inventory/orders/${o.orderNumber}/stage-shortages`, values, reason));
      if (result) { toast(result.message ?? 'Material tasks created.'); await orderDetail(page, ctx, number); }
    } catch (error) { toast(error.message, true); }
  });
  $('#change-priority', page)?.addEventListener('click', () => changePriority(number, o, page, ctx));
}

async function appendOrderCommitment(page, order, ctx) {
  const result = await tryGet(`/api/programs/${order.programId}/contracts`);
  const eligible = (result.data ?? []).filter(contract => contract.status === 'Active')
    .flatMap(contract => contract.clins.filter(line => line.partNumber === order.partNumber)
      .map(line => [JSON.stringify({ contractId: contract.contractId, clin: line.clin }), `${contract.number} · CLIN ${line.clin} · ${line.description}`]));
  const selected = order.contractId == null ? '' : JSON.stringify({ contractId: order.contractId, clin: order.contractClin });
  const current = (result.data ?? []).find(contract => contract.contractId === order.contractId);
  const allowed = canAction(ctx.me, 'execution.orders.manage', order) && !['Cancelled', 'Completed'].includes(order.status);
  const section = document.createElement('section');
  section.className = 'panel';
  section.innerHTML = `<header><h2>Contract commitment</h2><span class="spacer"></span>${allowed ? '<button class="btn small" data-commitment>Set commitment</button>' : ''}</header>
    <p>${order.contractId == null ? 'This order has no recorded contract commitment.' : `${esc(current?.number ?? `Contract ${order.contractId}`)} · CLIN ${esc(order.contractClin)}`}</p>
    <div class="faint">A production order belongs to a contract line only when an authorized person records that link. Changes keep their history so a past Horizon view uses the commitment recorded then.</div>`;
  page.append(section);
  $('[data-commitment]', section)?.addEventListener('click', () => formModal({
    title: `Set contract commitment for order #${order.orderNumber}`, detail: 'Select the existing contract line for this part and program. Clearing the current link keeps its history.', confirmLabel: 'Record commitment', reasonRequired: true,
    fields: result.error ? [
      { name: 'contractId', label: 'Existing contract ID (blank to clear)', type: 'number', min: 1 },
      { name: 'clin', label: 'Existing contract line' },
    ] : [{ name: 'commitment', label: 'Contract line', type: 'select', blank: 'No contract commitment', value: selected, options: eligible }],
  }, (values, reason) => {
    const commitment = result.error ? { contractId: values.contractId, clin: values.clin } : values.commitment ? JSON.parse(values.commitment) : { contractId: null, clin: null };
    return command(`/api/orders/${order.orderNumber}/contract-commitment`, commitment, reason);
  }, () => orderDetail(page, ctx, order.orderNumber)));
}

async function changePriority(number, order, page, ctx) {
  const scrim = document.createElement('div');
  scrim.className = 'scrim';
  const modal = document.createElement('div');
  modal.className = 'modal';
  modal.setAttribute('role', 'dialog');
  modal.innerHTML = `
    <h2>Change priority for order #${number}</h2>
    <div class="muted">Priority 1 is highest and 9 is lowest. Review which released orders move or finish later before committing.</div>
    <div class="field"><label for="priority-value">New priority</label><select id="priority-value" class="select">${Array.from({ length: 9 }, (_, i) => i + 1).map(n => `<option value="${n}" ${n === order.priority ? 'selected' : ''}>${n}${n === order.priority ? ' · current' : ''}</option>`).join('')}</select></div>
    <div data-impact class="panel" aria-live="polite"><span class="muted">Calculating impact…</span></div>
    <div class="field"><label for="priority-reason">Reason (recorded in the audit trail)</label><textarea id="priority-reason" class="input" rows="3"></textarea></div>
    <div class="notice bad hidden" data-error></div>
    <div class="actions"><button class="btn" data-cancel>Cancel</button><button class="btn primary" data-ok disabled>Commit priority</button></div>`;
  const close = () => { scrim.remove(); modal.remove(); };
  scrim.onclick = close;
  modal.querySelector('[data-cancel]').onclick = close;
  document.body.append(scrim, modal);

  const select = $('#priority-value', modal);
  select.focus();
  const impactBox = $('[data-impact]', modal);
  const errorBox = $('[data-error]', modal);
  const confirm = $('[data-ok]', modal);
  let preview = null;
  let generation = 0;
  const renderImpact = data => {
    const changes = data.orders ?? [];
    const target = changes.find(x => x.orderNumber === number);
    const delayed = changes.filter(x => x.orderNumber !== number && x.delayMinutes != null).sort((a, b) => b.delayMinutes - a.delayMinutes);
    impactBox.innerHTML = `
      <div><b>Order #${number}</b>: ${esc(target?.statusBefore ?? 'not scheduled')} → ${esc(target?.statusAfter ?? 'not scheduled')}
        ${target?.completionAfter ? `<div class="muted">Projected completion ${esc(fmtDateTime(target.completionAfter))}</div>` : ''}
        ${target?.explanationAfter ? `<div class="muted">${esc(target.explanationAfter)}</div>` : ''}</div>
      ${delayed.length ? `<div style="margin-top:10px"><b>Orders delayed by this change</b><ul class="evidence">${delayed.map(x => `<li>Order #${x.orderNumber} · ${fmtNumber(x.delayMinutes, 0)} min later${x.completionAfter ? ` · ${esc(fmtDateTime(x.completionAfter))}` : ''}</li>`).join('')}</ul></div>` : '<div class="muted" style="margin-top:8px">No released order is projected to finish later.</div>'}
      <div class="faint" style="margin-top:8px">Preview expires after five minutes. Any changed planning input requires a new review.</div>`;
  };
  const refresh = async () => {
    const sequence = ++generation;
    preview = null;
    confirm.disabled = true;
    impactBox.innerHTML = '<span class="muted">Recalculating schedule impact…</span>';
    try {
      const data = await command(`/api/orders/${number}/priority-preview`, { priority: Number(select.value) });
      if (sequence !== generation) return;
      preview = data;
      renderImpact(data);
      confirm.disabled = Number(select.value) === order.priority;
      errorBox.classList.add('hidden');
    } catch (error) {
      if (sequence !== generation) return;
      impactBox.innerHTML = `<span class="c-bad">${esc(error.message)}</span>`;
    }
  };
  select.addEventListener('change', refresh);
  confirm.onclick = async () => {
    const reason = $('#priority-reason', modal).value.trim();
    if (!reason) {
      errorBox.textContent = 'Give a reason; it is kept with the change.';
      errorBox.classList.remove('hidden');
      return;
    }
    if (!preview) return;
    confirm.disabled = true;
    try {
      const result = await command(`/api/orders/${number}/priority`, {
        priority: preview.proposedPriority,
        expectedPriority: preview.currentPriority,
        previewAsOf: preview.asOf,
        previewFingerprint: preview.fingerprint,
      }, reason);
      close();
      toast(result.message ?? `Order #${number} priority changed.`);
      await orderDetail(page, ctx, number);
    } catch (error) {
      confirm.disabled = false;
      errorBox.textContent = error.message;
      errorBox.classList.remove('hidden');
      if (error.problem?.code === 'stale_priority_preview') await refresh();
    }
  };
  await refresh();
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
