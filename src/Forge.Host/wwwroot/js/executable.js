import { tryGet, command } from './api.js';
import { who } from './people.js';
import { esc, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock } from './ui.js';
import { formModal, can } from './forms.js';

// Executable capacity and presence (V3.9): every open operation answers "can this happen now, and why not?";
// production holds; each worker's presence level (a schedule is never presence); training on controlled documents.

const KINDS = ['Quality', 'Material', 'Production', 'Engineering', 'Customer', 'Shipping', 'Procurement', 'Configuration', 'Safety', 'Administrative'];
const TARGETS = [['Order', 'Order'], ['Operation', 'Operation'], ['Part', 'Part'], ['Lot', 'Lot'], ['WorkCenter', 'Work center'], ['Machine', 'Machine'], ['Supplier', 'Supplier'], ['PurchaseOrder', 'Purchase order'], ['Shipment', 'Shipment'], ['Program', 'Program']];
const LEVEL = {
  ActiveOnOperation: ['ok', 'On an operation'], AssignedToWorkCenter: ['ok', 'Assigned'], ClockedIn: ['info', 'Clocked in'],
  Scheduled: ['warn', 'Scheduled only'], NotPresent: ['none', 'Not present'],
};

/** "Can it run now?" for the open operations of one work center (or all). */
export async function appendOperations(container, workCenter) {
  const section = document.createElement('section');
  section.className = 'panel';
  container.appendChild(section);
  const { data, error } = await tryGet(`/api/readiness/operations${workCenter ? `?workCenter=${encodeURIComponent(workCenter)}` : ''}`);
  const list = data ?? [];
  const ready = list.filter(o => o.executable).length;
  section.innerHTML = `<header><h2>Can it run now?</h2><span class="spacer"></span>${list.length ? `<span class="faint">${ready} of ${list.length} open operation(s) can run</span>` : ''}</header>
    ${error ? errorBlock(error, 'operation readiness') : !list.length ? emptyBlock('No open operations', 'Released orders with operations to do appear here.') : `
    <table class="table"><thead><tr><th>Order / op</th><th>Work center</th><th>Machine</th><th>Due</th><th>Now</th><th>Why</th></tr></thead><tbody>
    ${list.map(o => `<tr><td><a href="#/orders/${o.orderNumber}">${o.orderNumber}</a> / ${o.sequence} <span class="faint">${esc(o.description)}</span></td><td>${esc(o.workCenter)}</td><td>${esc(o.machine ?? '—')}</td>
      <td>${esc(fmtDate(o.dueDate))}</td><td>${o.executable ? '<span class="pill ok">Can run</span>' : `<span class="pill bad">${esc(o.primaryConstraint)}</span>`}</td><td>${esc(o.why)}</td></tr>`).join('')}
    </tbody></table>`}`;
}

/** The hold register (V3.10): every kind of hold, on an order page (that order's) or plant-wide. */
export async function appendHolds(container, me, order) {
  const section = document.createElement('section');
  section.className = 'panel';
  container.appendChild(section);
  const place = can(me, 'ManufacturingEngineer', 'Supervisor', 'Planner', 'QualityEngineer', 'QualityManager', 'Inspector', 'InventoryController', 'Buyer', 'PurchasingManager',
    'Shipper', 'SafetyEngineer', 'SafetyApprover', 'ProgramManager', 'ChangeBoard', 'EngineeringApprover', 'SecurityAdministrator');
  const load = async () => {
    const { data, error } = await tryGet('/api/holds?includeReleased=true');
    const ops = order ? order.operations : [];
    const list = (data ?? []).filter(h => !order
      || (h.targetType === 'Order' && h.target === `order ${order.orderNumber}`) || h.target.startsWith(`order ${order.orderNumber} `)
      || (h.targetType === 'Operation' && h.target.startsWith(`operation ${order.orderNumber}/`))
      || (h.targetType === 'Part' && h.target.startsWith(`part ${order.partNumber}`))
      || (h.targetType === 'WorkCenter' && ops.some(op => h.target === `work center ${op.workCenterCode}`))
      || (h.targetType === 'Machine' && ops.some(op => op.eligibleMachines.some(m => h.target === `machine ${m}`))));
    section.innerHTML = `<header><h2>Holds</h2><span class="spacer"></span>${place ? '<button class="btn small" data-hold-add>+ Hold</button>' : ''}</header>
      ${error ? errorBlock(error, 'holds') : !list.length ? emptyBlock('No holds', 'A hold of any kind stops the activities its kind blocks until someone with that kind\'s release authority releases it.') : `
      <table class="table"><thead><tr><th>Hold</th><th>Kind</th><th>On</th><th>Why</th><th>Stops</th><th>To release</th><th>Status</th><th></th></tr></thead><tbody>
      ${list.map(h => `<tr><td>${esc(h.reference)}<div class="faint">${esc(h.source)}</div></td><td>${esc(h.kind)}</td><td>${esc(h.target)}</td>
        <td>${esc(h.reason)}${h.evidence ? `<div class="faint">${esc(h.evidence)}</div>` : ''}</td><td>${esc(h.blocks.join(', '))}</td>
        <td>${esc(h.requiredResolution)}<div class="faint">${esc(h.releasedThrough)} · ${esc(h.releaseAuthority)}</div></td>
        <td>${h.active ? '<span class="pill bad">Active</span>' : `<span class="pill ok">Released</span><div class="faint">${esc(fmtDateTime(h.releasedAt))} ${esc(who(h.releasedBy))}: ${esc(h.releaseReason)}</div>`}</td>
        <td>${h.active && h.holdId ? `<button class="btn small ghost" data-hold-release="${h.holdId}">Release</button>` : ''}</td></tr>`).join('')}
      </tbody></table>`}`;
    $$('[data-hold-add]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: 'Place a hold', detail: 'It stops what its kind blocks. Releasing needs the kind\'s release authority.', confirmLabel: 'Place hold', reasonRequired: true,
      fields: [
        { name: 'kind', label: 'Kind', type: 'select', options: KINDS.map(k => [k, k]) },
        { name: 'targetType', label: 'On', type: 'select', options: TARGETS },
        { name: 'targetId', label: 'Which (order, ORDER/OP, part, lot, code or PO number)', required: true, value: order ? String(order.orderNumber) : '' },
        { name: 'targetPart', label: 'Part (for a lot)', code: true },
        { name: 'source', label: 'Source (optional)', placeholder: 'NCR 9001, supplier notice…' },
        { name: 'evidence', label: 'Evidence (optional)' },
        { name: 'requiredResolution', label: 'To release (optional)' },
      ],
    }, (v, reason) => command('/api/holds', { ...v, targetPart: v.targetPart || null, source: v.source || null, evidence: v.evidence || null, requiredResolution: v.requiredResolution || null }, reason), load)));
    $$('[data-hold-release]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: `Release hold ${b.dataset.holdRelease}`, detail: 'You need the release authority of its kind.', confirmLabel: 'Release', reasonRequired: true, fields: [],
    }, (v, reason) => command(`/api/holds/${b.dataset.holdRelease}/release`, {}, reason), load)));
  };
  await load();
}

/** Each worker's presence level now. */
export async function appendPresence(container, workCenter) {
  const section = document.createElement('section');
  section.className = 'panel';
  container.appendChild(section);
  const { data, error } = await tryGet(`/api/readiness/presence${workCenter ? `?workCenter=${encodeURIComponent(workCenter)}` : ''}`);
  const list = data ?? [];
  const present = list.filter(p => p.countsAsPresent).length;
  section.innerHTML = `<header><h2>Presence now</h2><span class="spacer"></span>${list.length ? `<span class="faint">${present} present · ${list.filter(p => p.level === 'Scheduled').length} scheduled but not clocked in</span>` : ''}</header>
    ${error ? errorBlock(error, 'presence') : !list.length ? emptyBlock('No workers', 'Workers appear once registered with a home work center.') : `
    <table class="table"><thead><tr><th>Person</th><th>Presence</th><th>Evidence</th><th>Where</th><th>Detail</th></tr></thead><tbody>
    ${list.map(p => { const [cls, label] = LEVEL[p.level] ?? ['none', p.level]; return `<tr><td>${esc(p.name)}<div class="faint">${esc(p.homeWorkCenter ?? '')}</div></td><td><span class="pill ${cls}">${esc(label)}</span></td>
      <td>${esc(p.source === 'SupervisorConfirmed' ? 'Supervisor confirmed' : p.source === 'ClockedIn' ? 'Clocked in' : '—')}</td><td>${esc([p.workCenter, p.machine].filter(Boolean).join(' · ') || '—')}</td><td>${esc(p.detail)}</td></tr>`; }).join('')}
    </tbody></table>
    <div class="faint" style="margin-top:6px">Only clocked-in or supervisor-confirmed people count as present. A shift schedule is never presence.</div>`}`;
}

/** Training on controlled documents, and the plant's policy for it. */
export async function appendDocumentTraining(container, me) {
  const section = document.createElement('section');
  section.className = 'panel';
  container.appendChild(section);
  const manage = can(me, 'TrainingCoordinator');
  const load = async () => {
    const { data, error } = await tryGet('/api/engineering/document-training');
    const records = data?.records ?? [];
    section.innerHTML = `<header><h2>Training on controlled documents</h2><span class="spacer"></span>${manage ? '<button class="btn small ghost" data-train-policy>Policy</button> <button class="btn small" data-train-add>+ Training</button>' : ''}</header>
      ${error ? errorBlock(error, 'document training') : `<div class="notice">Policy: <b>${esc(data.policy.policy)}</b> for ${esc(data.policy.documentTypes.join(', '))}. ${data.policy.policy === 'Enforce' ? 'An operation is not executable until enough trained operators are present.' : data.policy.policy === 'Warn' ? 'Untrained operators are named; work is not stopped.' : 'Training is not checked.'}</div>
      ${!records.length ? emptyBlock('No training recorded', 'Record who is trained on which revision; a new revision needs new training.') : `
      <table class="table"><thead><tr><th>Person</th><th>Document</th><th>Revision</th><th>Trained</th><th>Recorded by</th></tr></thead><tbody>
      ${records.slice().reverse().map(t => `<tr><td>${esc(who('person:' + t.personId))}</td><td>${esc(t.documentNumber)}</td><td>${esc(t.revision)}</td><td>${esc(fmtDate(t.trainedOn))}</td><td>${esc(who(t.recordedBy))}</td></tr>`).join('')}
      </tbody></table>`}`}`;
    $$('[data-train-add]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: 'Record training on a document', confirmLabel: 'Record',
      fields: [{ name: 'personId', label: 'Person ID', required: true }, { name: 'documentNumber', label: 'Document', code: true, required: true }, { name: 'revision', label: 'Revision', required: true }, { name: 'trainedOn', label: 'Trained on', type: 'date' }],
    }, (v, reason) => command('/api/engineering/document-training', { ...v, trainedOn: v.trainedOn || null }, reason), load)));
    $$('[data-train-policy]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: 'Training policy', detail: 'Off: not checked. Warn: untrained operators are named. Enforce: an operation is not executable until enough trained operators are present.', confirmLabel: 'Save', reasonRequired: true,
      fields: [{ name: 'policy', label: 'Policy', type: 'select', options: [['Off', 'Off'], ['Warn', 'Warn'], ['Enforce', 'Enforce']] }],
    }, (v, reason) => command('/api/engineering/document-training/policy', { policy: v.policy }, reason), load)));
  };
  await load();
}
