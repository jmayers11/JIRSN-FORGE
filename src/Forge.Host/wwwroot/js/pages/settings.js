import { tryGet, signOut, session, command } from '../api.js';
import { esc, $, $$, errorBlock, emptyBlock, confirmWithReason, toast, fmtDateTime } from '../ui.js';
import { formModal, canAction } from '../forms.js';
import { roleTitle, renderLogin } from '../app.js';

export async function settingsPage(page, ctx) {
  const me = ctx.me;
  const canAudit = canAction(me, 'kernel.audit.read');
  const admin = canAction(me, 'kernel.access.manage');
  // Program codes: the directory for administrators, otherwise the programs the reader can open.
  const [directory, visible] = await Promise.all([admin ? tryGet('/api/programs/directory') : Promise.resolve({ data: [] }), tryGet('/api/programs/')]);
  const names = new Map([...(directory.data ?? []).map(p => [p.programId, `${p.code} — ${p.name}`]), ...(visible.data ?? []).map(h => [h.program.programId, `${h.program.code} — ${h.program.name}`])]);
  page.innerHTML = `
    <div class="page-head"><div><h1>Settings</h1><div class="sub">Your Forge identity. Roles and program access are granted by a security administrator, never by yourself.</div></div></div>
    <section class="grid-2">
      <div class="panel"><header><h2>You</h2></header>
        <dl class="kv">
          <dt>Name</dt><dd>${esc(me.displayName)}</dd>
          <dt>Employee ID</dt><dd class="mono">${esc(session.subject ?? '(company sign-in)')}</dd>
          <dt>Person ID</dt><dd class="mono">${esc(me.personId)}</dd>
          <dt>Roles</dt><dd>${esc(me.roles.map(roleTitle).join(', ') || 'None yet')}</dd>
          <dt>Programs</dt><dd>${me.programs.length ? me.programs.map(p => names.has(p) ? esc(names.get(p)) : `<span class="mono">${esc(p)}</span>`).join('<br>') : 'None'}</dd>
          <dt>Data markings</dt><dd>${esc(me.markings)}</dd>
        </dl>
        <div style="margin-top:12px"><button class="btn" id="signout">Sign out</button></div>
      </div>
      <div class="panel"><header><h2>Audit trail</h2></header><div id="audit">${canAudit ? 'Checking…' : '<span class="muted">Auditors and security administrators can check the audit chain here.</span>'}</div></div>
    </section>
    ${admin ? '<section class="panel" id="access"><header><h2>Program access</h2><span class="spacer"></span><button class="btn small primary" id="grant">Grant access</button></header><div id="access-body" class="muted">Loading…</div></section>' : ''}
    <section class="panel" id="delegations"><header><h2>Temporary delegation</h2><span class="spacer"></span><button class="btn small primary" id="propose-delegation">Propose delegation</button></header><div id="delegation-body" class="muted">Loading…</div></section>`;
  $('#signout', page).onclick = () => { signOut(); location.hash = ''; renderLogin(); };
  if (admin) await programAccess(page, me, directory, names);
  await temporaryDelegations(page, me, admin);
  if (canAudit) {
    const v = await tryGet('/api/kernel/audit/verify');
    $('#audit', page).innerHTML = v.error ? errorBlock(v.error, 'the audit chain')
      : v.data.intact ? `<span class="pill ok">Intact</span> ${esc(v.data.entriesChecked)} entries checked; every entry is hash-chained to the one before it.`
      : `<span class="pill bad">Broken</span> The chain breaks at entry ${esc(v.data.firstBrokenSequence)}. Report this to your security administrator.`;
  }
}

async function temporaryDelegations(page, me, admin) {
  const [records, directory, lendable] = await Promise.all([tryGet('/api/kernel/delegations'), tryGet('/api/kernel/directory'), tryGet('/api/kernel/delegations/lendable')]);
  const body = $('#delegation-body', page);
  if (records.error || directory.error || lendable.error) {
    body.innerHTML = errorBlock(records.error ?? directory.error ?? lendable.error, 'temporary delegation');
    return;
  }
  const people = new Map(directory.data.map(person => [person.personId, person.displayName]));
  body.classList.remove('muted');
  body.innerHTML = `<p class="muted">Lend a subset of your own authority for a defined interval. An independent security administrator approves it. Base roles and program access stay separate.</p>
    ${records.data.length ? `<table class="table"><thead><tr><th>People</th><th>Actions and scope</th><th>Interval</th><th>State</th><th>Reason</th><th></th></tr></thead><tbody>${records.data.map(record => `<tr>
      <td>${esc(people.get(record.grantor) ?? record.grantor)} → ${esc(people.get(record.recipient) ?? record.recipient)}</td>
      <td>${record.permissions.map(permission => esc(permission)).join('<br>')}<div class="faint">${record.scopes.length ? record.scopes.map(scope => `${esc(scope.scopeType)}: ${esc(scope.scopeId)}`).join(' · ') : 'Global'}</div></td>
      <td>${esc(fmtDateTime(record.startsAt))}<br>to ${esc(fmtDateTime(record.expiresAt))}</td><td>${esc(record.state)}${record.approver ? `<div class="faint">Approved by ${esc(people.get(record.approver) ?? record.approver)}</div>` : ''}</td>
      <td>${esc(record.reason)}</td><td>${admin && record.state === 'Pending approval' && record.grantor !== me.personId && record.recipient !== me.personId ? `<button class="btn small primary" data-approve-delegation="${record.delegationId}">Approve</button>` : ''}
      ${admin && record.state !== 'Revoked' ? `<button class="btn small danger" data-revoke-delegation="${record.delegationId}">Revoke</button>` : ''}</td></tr>`).join('')}</tbody></table>` : emptyBlock('No delegations', 'Proposed, scheduled, active, expired and revoked delegations appear here.')}`;
  const propose = $('#propose-delegation', page);
  propose.disabled = !lendable.data.permissions.length;
  propose.onclick = () => formModal({
    title: 'Propose temporary delegation', detail: 'The recipient receives only the selected actions and scope after independent approval. Borrowed authority cannot be delegated onward.', confirmLabel: 'Propose', reasonRequired: true,
    fields: [
      { name: 'recipient', label: 'Recipient', type: 'select', required: true, options: directory.data.filter(person => person.personId !== me.personId).map(person => [person.personId, person.displayName]) },
      { name: 'permission', label: 'Business action', type: 'select', required: true, options: lendable.data.permissions.map(permission => [permission, permission]) },
      { name: 'additional', label: 'Additional business actions (comma separated, optional)', list: lendable.data.permissions },
      { name: 'scopeType', label: 'Scope', type: 'select', options: [['', 'Global'], ['program', 'Program'], ['facility', 'Facility'], ['work_center', 'Work center'], ['contract', 'Contract'], ['customer', 'Customer'], ['shift', 'Shift']] },
      { name: 'scopeId', label: 'Scope identifier', hint: 'Use the existing program ID or facility, work-center, contract, customer or shift code. Scoped source authority must cover this scope.' },
      { name: 'startsAt', label: 'Starts at', type: 'datetime-local', required: true },
      { name: 'expiresAt', label: 'Expires at', type: 'datetime-local', required: true },
    ],
  }, (values, reason) => command('/api/kernel/delegations', {
    recipient: values.recipient,
    permissions: [...new Set([values.permission, ...String(values.additional ?? '').split(',').map(value => value.trim()).filter(Boolean)])],
    scopes: values.scopeType ? [{ scopeType: values.scopeType, scopeId: values.scopeId }] : [],
    startsAt: new Date(values.startsAt).toISOString(), expiresAt: new Date(values.expiresAt).toISOString(),
  }, reason), () => temporaryDelegations(page, me, admin));
  for (const action of ['approve', 'revoke']) {
    $$(`[data-${action}-delegation]`, body).forEach(button => button.addEventListener('click', async () => {
      const id = button.getAttribute(`data-${action}-delegation`);
      const result = await confirmWithReason({ title: action === 'approve' ? 'Approve temporary delegation' : 'Revoke temporary delegation', confirmLabel: action === 'approve' ? 'Approve' : 'Revoke', reasonRequired: true },
        reason => command(`/api/kernel/delegations/${id}/${action}`, {}, reason));
      if (result) { toast(result.message); await temporaryDelegations(page, me, admin); }
    }));
  }
}

// Who can see which program. Access is granted by a security administrator, never to themselves.
async function programAccess(page, me, directory, names) {
  const people = await tryGet('/api/kernel/people');
  const section = $('#access', page);
  const body = $('#access-body', section);
  body.classList.remove('muted');
  if (directory.error || people.error) { body.innerHTML = errorBlock(directory.error ?? people.error, 'program access'); return; }
  const programs = directory.data;
  const rows = people.data.filter(p => p.programs.length);
  body.innerHTML = !programs.length ? emptyBlock('No programs yet', 'A program manager creates programs; grant access here afterwards.')
    : `<table class="table"><thead><tr><th>Person</th><th>Programs</th></tr></thead><tbody>
      ${rows.map(p => `<tr><td>${esc(p.displayName)} <span class="faint">${esc(p.idpSubject)}</span></td><td>${p.programs.map(id => `<span class="pill none">${esc(names.get(id) ?? id)} <a href="#" data-revoke="${p.personId}/${id}" title="Revoke">×</a></span>`).join(' ')}</td></tr>`).join('') || '<tr><td colspan="2" class="muted">Nobody has program access yet.</td></tr>'}
      </tbody></table>`;
  $('#grant', page).onclick = () => formModal({
    title: 'Grant program access', detail: 'The person sees that program\'s orders, demand, contracts and audit records. You cannot grant yourself.', confirmLabel: 'Grant', reasonRequired: true,
    fields: [
      { name: 'personId', label: 'Person', type: 'select', required: true, options: people.data.filter(p => p.personId !== me.personId && p.isActive).map(p => [p.personId, `${p.displayName} (${p.idpSubject})`]) },
      { name: 'programId', label: 'Program', type: 'select', required: true, options: programs.map(p => [p.programId, `${p.code} — ${p.name}`]) },
    ],
  }, (v, reason) => command(`/api/kernel/people/${v.personId}/programs`, { programId: v.programId }, reason), () => programAccess(page, me, directory, names));
  $$('[data-revoke]', section).forEach(a => a.addEventListener('click', async e => {
    e.preventDefault();
    const [person, program] = a.dataset.revoke.split('/');
    const done = await confirmWithReason({ title: `Revoke ${names.get(program) ?? program}`, confirmLabel: 'Revoke' }, reason => command(`/api/kernel/people/${person}/programs/${program}/revoke`, {}, reason));
    if (done) { toast(done.message); await programAccess(page, me, directory, names); }
  }));
}
