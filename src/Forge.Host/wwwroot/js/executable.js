import { tryGet, command } from './api.js';
import { who } from './people.js';
import { esc, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock } from './ui.js';
import { formModal, can } from './forms.js';

// Executable capacity and presence (V3.9): every open operation answers "can this happen now, and why not?";
// production holds; each worker's presence level (a schedule is never presence); training on controlled documents.

const SCOPES = [['Order', 'Order'], ['Operation', 'Operation'], ['Part', 'Part'], ['WorkCenter', 'Work center'], ['Machine', 'Machine']];
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

/** Production holds: on an order page (that order's), or plant-wide. */
export async function appendHolds(container, me, order) {
  const section = document.createElement('section');
  section.className = 'panel';
  container.appendChild(section);
  const manage = can(me, 'ManufacturingEngineer', 'Supervisor', 'Planner', 'QualityEngineer', 'QualityManager');
  const load = async () => {
    const { data, error } = await tryGet('/api/holds?includeReleased=true');
    const list = (data ?? []).filter(h => !order || h.orderNumber === order.orderNumber || (h.scope === 'Part' && h.partNumber === order.partNumber)
      || (h.scope === 'WorkCenter' && order.operations.some(op => op.workCenterCode === h.workCenterCode))
      || (h.scope === 'Machine' && order.operations.some(op => op.eligibleMachines.includes(h.machineCode))));
    const target = h => ({ Order: `Order ${h.orderNumber}`, Operation: `Op ${h.orderNumber}/${h.sequence}`, Part: `Part ${h.partNumber}`, WorkCenter: `Work center ${h.workCenterCode}`, Machine: `Machine ${h.machineCode}` })[h.scope];
    section.innerHTML = `<header><h2>Holds</h2><span class="spacer"></span>${manage ? '<button class="btn small" data-hold-add>+ Hold</button>' : ''}</header>
      ${error ? errorBlock(error, 'holds') : !list.length ? emptyBlock('No holds', 'A hold stops work starting on its order, operation, part, work center or machine until a different person releases it.') : `
      <table class="table"><thead><tr><th>Hold</th><th>On</th><th>Why</th><th>Placed</th><th>Released</th><th></th></tr></thead><tbody>
      ${list.map(h => `<tr><td>${h.holdId}</td><td>${esc(target(h))}</td><td>${esc(h.reason)}</td><td>${esc(fmtDateTime(h.placedAt))}<div class="faint">${esc(who(h.placedBy))}</div></td>
        <td>${h.releasedAt ? `${esc(fmtDateTime(h.releasedAt))}<div class="faint">${esc(who(h.releasedBy))}: ${esc(h.releaseReason)}</div>` : '<span class="pill bad">Active</span>'}</td>
        <td>${manage && !h.releasedAt ? `<button class="btn small ghost" data-hold-release="${h.holdId}">Release</button>` : ''}</td></tr>`).join('')}
      </tbody></table>`}`;
    $$('[data-hold-add]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: 'Place a hold', detail: 'Work does not start where the hold applies until a different person releases it.', confirmLabel: 'Place hold', reasonRequired: true,
      fields: [
        { name: 'scope', label: 'Hold', type: 'select', options: order ? SCOPES.filter(([s]) => s === 'Order' || s === 'Operation') : SCOPES },
        { name: 'orderNumber', label: 'Order', type: 'number', value: order?.orderNumber ?? '' },
        { name: 'sequence', label: 'Operation (for an operation hold)', type: 'number' },
        ...(order ? [] : [{ name: 'partNumber', label: 'Part (for a part hold)', code: true }, { name: 'workCenterCode', label: 'Work center', code: true }, { name: 'machineCode', label: 'Machine', code: true }]),
      ],
    }, (v, reason) => command('/api/holds', {
      scope: v.scope, orderNumber: v.orderNumber ? Number(v.orderNumber) : null, sequence: v.sequence ? Number(v.sequence) : null,
      partNumber: v.partNumber || null, workCenterCode: v.workCenterCode || null, machineCode: v.machineCode || null,
    }, reason), load)));
    $$('[data-hold-release]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: `Release hold ${b.dataset.holdRelease}`, detail: 'The person who placed it cannot release it.', confirmLabel: 'Release', reasonRequired: true, fields: [],
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
