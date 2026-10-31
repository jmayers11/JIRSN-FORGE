import { get, command, tryGet } from '../api.js';
import { esc, toast, errorBlock, confirmWithReason, tabifyPanels } from '../ui.js';
import { canAction, fieldHtml, readFields } from '../forms.js';

const links = { NCR: '#/quality', Schedule: '#/scheduling', PurchaseOrder: '#/procurement', Project: '#/projects', ProjectChange: '#/projects', Outage: '#/maintenance', Inspection: '#/quality', WorkOrder: '#/maintenance', RFQ: '#/procurement', ProjectReview: '#/projects', MaterialRequest: '#/inventory' };

export async function workInboxPage(root, { me }) {
  const header = `<section class="page-head"><div><h1>My Work Inbox</h1><p class="muted">Approvals you may decide and work assigned directly to you.</p></div><button class="btn" id="refresh-work">Refresh</button></section>`;
  const canAssign = canAction(me, 'work.assignments.manage');
  let response, directory = [];
  try { response = await get('/api/work-inbox'); }
  catch (error) {
    root.innerHTML = header + errorBlock(error, 'the work inbox');
    root.querySelector('#refresh-work')?.addEventListener('click', () => workInboxPage(root, { me }));
    return;
  }
  const approvals = response.approvals ?? [];
  const assignments = response.assignments ?? [];
  if (canAssign) {
    const result = await tryGet('/api/kernel/directory');
    if (Array.isArray(result.data)) directory = result.data;
  }
  const assignFields = [
    { name: 'workType', label: 'Work type', type: 'select', options: ['NCR', 'Inspection', 'WorkOrder', 'RFQ', 'ProjectReview', 'MaterialRequest'].map(value => [value, value]) },
    { name: 'workId', label: 'Record ID', required: true, maxLength: 100 },
    { name: 'title', label: 'Title', required: true, maxLength: 240 },
    { name: 'assigneePersonId', label: 'Assignee', type: 'lookup', required: true, options: directory.map(person => [person.personId, `${person.displayName} · ${person.personId}`]), hint: 'Choose a person from the current Forge directory.' },
    { name: 'deepLink', label: 'Open page', type: 'select', options: ['#/quality', '#/procurement', '#/maintenance', '#/projects', '#/inventory', '#/engineering', '#/shopfloor'].map(value => [value, value]) },
    { name: 'reason', label: 'Reason', required: true, maxLength: 500 },
  ];
  const nextState = { Assigned: 'Acknowledged', Acknowledged: 'InProgress', InProgress: 'Completed' };
  root.innerHTML = `${header}
    <section class="section"><div class="section-head"><h2>Waiting for my decision</h2><span class="muted">${approvals.length}</span></div><div class="stack">${approvals.map(a => `<article class="card"><div class="card-head"><div><span class="badge">${esc(a.workType)}</span><h3>${esc(a.title)}</h3><p class="muted">Requested by ${esc(a.requester)} · ${esc(a.permission)}</p></div><time>${esc(new Date(a.createdAt).toLocaleString())}</time></div><a class="btn primary" href="${esc(a.deepLink)}">Review in Forge</a></article>`).join('') || '<div class="empty">No approvals are currently available for your roles and access.</div>'}</div></section>
    <section class="section"><div class="section-head"><h2>Assigned to me</h2><span class="muted">${assignments.length}</span></div><div class="stack">${assignments.map(a => `<article class="card"><div class="card-head"><div><span class="badge">${esc(a.workType)} · ${esc(a.status)}</span><h3>${esc(a.title)}</h3><p class="muted">Assigned ${esc(new Date(a.assignedAt).toLocaleString())} · ${esc(a.reason)}</p></div></div><div class="actions"><a class="btn" href="${esc(a.deepLink)}">Open work</a>${nextState[a.status] ? `<button class="btn primary" data-next="${esc(a.assignmentId)}" data-status="${nextState[a.status]}">Mark ${esc(nextState[a.status])}</button>` : '<span class="muted">Assignment complete</span>'}</div></article>`).join('') || '<div class="empty">Nothing has been assigned to you.</div>'}</div></section>
    ${canAssign ? `<section class="section"><div class="section-head"><h2>Assign work</h2><span class="muted">Assignments are tracked separately from source record decisions.</span></div>${directory.length ? `<form id="assign-work-form" class="card form-grid">${assignFields.map(fieldHtml).join('')}<div class="actions"><button class="btn primary" type="submit">Assign</button></div></form>` : '<div class="notice warn">The people directory is unavailable or empty. Refresh it before assigning work.</div>'}</section>` : ''}`;
  // Each queue in its own tab (UI density audit 2026-10-27).
  tabifyPanels(root, [
    { label: `Waiting for my decision (${approvals.length})`, match: ['waiting for my decision'] },
    { label: `Assigned to me (${assignments.length})`, match: ['assigned to me'] },
    { label: 'Assign work', match: ['assign work'] },
  ]);
  root.querySelector('#refresh-work')?.addEventListener('click', () => workInboxPage(root, { me }).catch(e => toast(e.message, true)));
  root.querySelectorAll('[data-next]').forEach(button => button.addEventListener('click', async () => {
    await confirmWithReason({
      title: `Mark assignment ${button.dataset.status.toLowerCase()}`,
      detail: 'Record why this work assignment is moving to its next status.',
      confirmLabel: `Mark ${button.dataset.status}`,
    }, async reason => {
      const result = await command(`/api/work-inbox/assignments/${button.dataset.next}/status`, { status: button.dataset.status }, reason);
      await workInboxPage(root, { me });
      return result;
    });
  }));
  root.querySelector('#assign-work-form')?.addEventListener('submit', async event => {
    event.preventDefault();
    try { const values = readFields(event.currentTarget, assignFields); await command('/api/work-inbox/assignments', values, values.reason); toast('Work assigned.'); await workInboxPage(root, { me }); }
    catch (e) { toast(e.message, true); }
  });
}
