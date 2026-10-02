import { tryGet, signOut, session, command } from '../api.js';
import { esc, $, $$, errorBlock, emptyBlock, confirmWithReason, toast } from '../ui.js';
import { formModal } from '../forms.js';
import { roleTitle, renderLogin } from '../app.js';

export async function settingsPage(page, ctx) {
  const me = ctx.me;
  const canAudit = me.roles.includes('SecurityAdministrator') || me.roles.includes('Auditor');
  const admin = me.roles.includes('SecurityAdministrator');
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
    ${admin ? '<section class="panel" id="access"><header><h2>Program access</h2><span class="spacer"></span><button class="btn small primary" id="grant">Grant access</button></header><div id="access-body" class="muted">Loading…</div></section>' : ''}`;
  $('#signout', page).onclick = () => { signOut(); location.hash = ''; renderLogin(); };
  if (admin) await programAccess(page, me, directory, names);
  if (canAudit) {
    const v = await tryGet('/api/kernel/audit/verify');
    $('#audit', page).innerHTML = v.error ? errorBlock(v.error, 'the audit chain')
      : v.data.intact ? `<span class="pill ok">Intact</span> ${esc(v.data.entriesChecked)} entries checked; every entry is hash-chained to the one before it.`
      : `<span class="pill bad">Broken</span> The chain breaks at entry ${esc(v.data.firstBrokenSequence)}. Report this to your security administrator.`;
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
