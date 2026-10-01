import { tryGet, signOut, session } from '../api.js';
import { esc, $, errorBlock } from '../ui.js';
import { roleTitle, renderLogin } from '../app.js';

export async function settingsPage(page, ctx) {
  const me = ctx.me;
  const canAudit = me.roles.includes('SecurityAdministrator') || me.roles.includes('Auditor');
  page.innerHTML = `
    <div class="page-head"><div><h1>Settings</h1><div class="sub">Your Forge identity. Roles and program access are granted by a security administrator, never by yourself.</div></div></div>
    <section class="grid-2">
      <div class="panel"><header><h2>You</h2></header>
        <dl class="kv">
          <dt>Name</dt><dd>${esc(me.displayName)}</dd>
          <dt>Employee ID</dt><dd class="mono">${esc(session.subject ?? '(company sign-in)')}</dd>
          <dt>Person ID</dt><dd class="mono">${esc(me.personId)}</dd>
          <dt>Roles</dt><dd>${esc(me.roles.map(roleTitle).join(', ') || 'None yet')}</dd>
          <dt>Programs</dt><dd>${me.programs.length ? me.programs.map(p => `<span class="mono">${esc(p)}</span>`).join('<br>') : 'None'}</dd>
          <dt>Data markings</dt><dd>${esc(me.markings)}</dd>
        </dl>
        <div style="margin-top:12px"><button class="btn" id="signout">Sign out</button></div>
      </div>
      <div class="panel"><header><h2>Audit trail</h2></header><div id="audit">${canAudit ? 'Checking…' : '<span class="muted">Auditors and security administrators can check the audit chain here.</span>'}</div></div>
    </section>`;
  $('#signout', page).onclick = () => { signOut(); location.hash = ''; renderLogin(); };
  if (canAudit) {
    const v = await tryGet('/api/kernel/audit/verify');
    $('#audit', page).innerHTML = v.error ? errorBlock(v.error, 'the audit chain')
      : v.data.intact ? `<span class="pill ok">Intact</span> ${esc(v.data.entriesChecked)} entries checked; every entry is hash-chained to the one before it.`
      : `<span class="pill bad">Broken</span> The chain breaks at entry ${esc(v.data.firstBrokenSequence)}. Report this to your security administrator.`;
  }
}
