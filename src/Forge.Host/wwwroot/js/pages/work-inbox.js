import { get, command } from '../api.js';
import { esc, toast, errorBlock } from '../ui.js';
import { canAction } from '../forms.js';

const links = { NCR: '#/quality', Schedule: '#/scheduling', PurchaseOrder: '#/procurement', Project: '#/projects', ProjectChange: '#/projects', Outage: '#/maintenance', Inspection: '#/quality', WorkOrder: '#/maintenance', RFQ: '#/procurement', ProjectReview: '#/projects', MaterialRequest: '#/inventory' };

export async function workInboxPage(root, { me }) {
  const header = `<section class="page-head"><div><h1>My Work Inbox</h1><p class="muted">Approvals you may decide and work assigned directly to you.</p></div><button class="btn" id="refresh-work">Refresh</button></section>`;
  let response;
  try { response = await get('/api/work-inbox'); }
  catch (error) {
    root.innerHTML = header + errorBlock(error, 'the work inbox');
    root.querySelector('#refresh-work')?.addEventListener('click', () => workInboxPage(root, { me }));
    return;
  }
  const approvals = response.approvals ?? [];
  const assignments = response.assignments ?? [];
  const canAssign = canAction(me, 'work.assignments.manage');
  const nextState = { Assigned: 'Acknowledged', Acknowledged: 'InProgress', InProgress: 'Completed' };
  root.innerHTML = `${header}
    <section class="section"><div class="section-head"><h2>Waiting for my decision</h2><span class="muted">${approvals.length}</span></div><div class="stack">${approvals.map(a => `<article class="card"><div class="card-head"><div><span class="badge">${esc(a.workType)}</span><h3>${esc(a.title)}</h3><p class="muted">Requested by ${esc(a.requester)} · ${esc(a.permission)}</p></div><time>${esc(new Date(a.createdAt).toLocaleString())}</time></div><a class="btn primary" href="${esc(a.deepLink)}">Review in Forge</a></article>`).join('') || '<div class="empty">No approvals are currently available for your roles and access.</div>'}</div></section>
    <section class="section"><div class="section-head"><h2>Assigned to me</h2><span class="muted">${assignments.length}</span></div><div class="stack">${assignments.map(a => `<article class="card"><div class="card-head"><div><span class="badge">${esc(a.workType)} · ${esc(a.status)}</span><h3>${esc(a.title)}</h3><p class="muted">Assigned ${esc(new Date(a.assignedAt).toLocaleString())} · ${esc(a.reason)}</p></div></div><div class="actions"><a class="btn" href="${esc(a.deepLink)}">Open work</a>${nextState[a.status] ? `<button class="btn primary" data-next="${esc(a.assignmentId)}" data-status="${nextState[a.status]}">Mark ${esc(nextState[a.status])}</button>` : '<span class="muted">Assignment complete</span>'}</div></article>`).join('') || '<div class="empty">Nothing has been assigned to you.</div>'}</div></section>
    ${canAssign ? `<section class="section"><div class="section-head"><h2>Assign work</h2><span class="muted">Assignments are tracked separately from source record decisions.</span></div><form id="assign-work-form" class="card form-grid"><label class="field"><span>Work type</span><select class="input" name="workType"><option>NCR</option><option>Inspection</option><option>WorkOrder</option><option>RFQ</option><option>ProjectReview</option><option>MaterialRequest</option></select></label><label class="field"><span>Record ID</span><input class="input" name="workId" required maxlength="100"></label><label class="field"><span>Title</span><input class="input" name="title" required maxlength="240"></label><label class="field"><span>Forge person ID</span><input class="input" name="assigneePersonId" required type="text" placeholder="UUID"></label><label class="field"><span>Open page</span><select class="input" name="deepLink"><option>#/quality</option><option>#/procurement</option><option>#/maintenance</option><option>#/projects</option><option>#/inventory</option><option>#/engineering</option><option>#/shopfloor</option></select></label><label class="field"><span>Reason</span><input class="input" name="reason" required maxlength="500"></label><div class="actions"><button class="btn primary" type="submit">Assign</button></div></form></section>` : ''}`;
  root.querySelector('#refresh-work')?.addEventListener('click', () => workInboxPage(root, { me }).catch(e => toast(e.message, true)));
  root.querySelectorAll('[data-next]').forEach(button => button.addEventListener('click', async () => {
    const reason = prompt(`Reason for marking ${button.dataset.status}:`);
    if (!reason?.trim()) return;
    try { await command(`/api/work-inbox/assignments/${button.dataset.next}/status`, { status: button.dataset.status }, reason); await workInboxPage(root, { me }); }
    catch (e) { toast(e.message, true); }
  }));
  root.querySelector('#assign-work-form')?.addEventListener('submit', async event => {
    event.preventDefault(); const values = Object.fromEntries(new FormData(event.currentTarget));
    try { await command('/api/work-inbox/assignments', values, values.reason); toast('Work assigned.'); await workInboxPage(root, { me }); }
    catch (e) { toast(e.message, true); }
  });
}
