import { tryGet, command, session } from '../api.js';
import { esc, $, $$, errorBlock, emptyBlock } from '../ui.js';
import { formModal, canAction } from '../forms.js';
import { ROLE_NAMES, roleTitle } from '../roles.js';

// Access control: people, their roles and program access, role policy (permissions and scopes), separation of
// duties and approval thresholds. Every change is a command with a reason. Forge refuses self-grants while the
// separation policy is on, and access management itself cannot be removed through role policy.

const API = '/api/kernel';
const MARKINGS = [['ControlledUnclassified', 'Controlled unclassified'], ['ExportControlled', 'Export controlled'], ['Proprietary', 'Proprietary']];
const SCOPES = [['program', 'Program'], ['facility', 'Facility'], ['work_center', 'Work center'], ['contract', 'Contract'], ['customer', 'Customer'], ['shift', 'Shift']];
export const SEPARATION_POLICIES = [
  ['access.self_role_change', 'A person cannot assign or revoke their own roles.'],
  ['access.self_program_change', 'A person cannot grant or revoke their own program access.'],
];

/** Permission codes, one per line or comma separated, lower-cased and de-duplicated. */
export function permissionList(text) {
  const list = [...new Set(String(text ?? '').split(/[\s,]+/).map(p => p.trim().toLowerCase()).filter(Boolean))];
  if (!list.length) throw new Error('List at least one permission.');
  const bad = list.find(p => !/^[a-z][a-z0-9_.]*$/.test(p));
  if (bad) throw new Error(`"${bad}" is not a permission code.`);
  return list;
}

/** A blank amount removes the threshold; an amount needs a three-letter currency. */
export function thresholdBody(v) {
  if (v.maximumAmount == null) return { maximumAmount: null, currency: null };
  if (!(v.maximumAmount >= 0)) throw new Error('The maximum amount cannot be negative.');
  const currency = String(v.currency ?? '').toUpperCase();
  if (!/^[A-Z]{3}$/.test(currency)) throw new Error('Use a three-letter currency code such as USD.');
  return { maximumAmount: v.maximumAmount, currency };
}

export function peopleRows(people, programs, can, local = false) {
  const programName = id => programs.find(p => p.programId === id)?.name ?? `program ${String(id).slice(0, 8)}`;
  return people.map(p => `<tr>
    <td><b>${esc(p.displayName)}</b><div class="faint mono">${esc(p.idpSubject)}</div></td>
    <td>${p.roles.map(r => `<span class="chip">${esc(roleTitle(r))}${can ? `<button class="chip-x" aria-label="Revoke ${esc(roleTitle(r))} from ${esc(p.displayName)}" data-revoke-role="${esc(r)}" data-person="${p.personId}">×</button>` : ''}</span>`).join(' ') || '<span class="muted">None</span>'}</td>
    <td>${p.programs.map(id => `<span class="chip">${esc(programName(id))}${can ? `<button class="chip-x" aria-label="Revoke ${esc(programName(id))} from ${esc(p.displayName)}" data-revoke-program="${id}" data-person="${p.personId}">×</button>` : ''}</span>`).join(' ') || '<span class="muted">None</span>'}</td>
    <td>${p.markings && p.markings !== 'None' ? esc(p.markings) : '—'}</td>
    <td>${p.isActive ? '<span class="pill ok">Active</span>' : '<span class="pill none">Inactive</span>'}</td>
    <td class="row-actions">${can ? `<button class="btn small" data-grant-role="${p.personId}">Grant role</button><button class="btn small ghost" data-grant-program="${p.personId}">Grant program</button>${local ? `<button class="btn small ghost" data-set-password="${p.personId}">Set password</button>` : ''}` : ''}</td></tr>`).join('');
}

export async function accessPage(page, ctx) {
  const can = canAction(ctx.me, 'kernel.access.manage');
  let people = { data: [] }, policy = { data: null }, programs = [], selectedPolicyRole = '', disposed = false;

  async function load() {
    const [p, prog, accessPolicy] = await Promise.all([tryGet(`${API}/people`), tryGet('/api/programs/'), can ? tryGet(`${API}/access-policy`) : Promise.resolve({ data: null })]);
    people = p; policy = accessPolicy; programs = (prog.data ?? []).map(x => ({ programId: x.programId, name: x.name ?? x.code ?? x.programId }));
  }
  const reload = async () => { await load(); if (!disposed) render(); };
  const person = id => (people.data ?? []).find(p => p.personId === id);
  const policyRoles = () => Array.isArray(policy.data?.roles) ? policy.data.roles : [];
  const roles = () => [...new Set([...ROLE_NAMES, ...policyRoles().map(r => r.role), ...(people.data ?? []).flatMap(p => p.roles)])].sort((a, b) => roleTitle(a).localeCompare(roleTitle(b)));

  function render() {
    const knownPolicyRoles = policyRoles();
    if (!knownPolicyRoles.some(r => r.role === selectedPolicyRole)) selectedPolicyRole = knownPolicyRoles[0]?.role ?? '';
    const selectedRole = knownPolicyRoles.find(r => r.role === selectedPolicyRole);
    const selectedPermissions = selectedRole?.permissions ?? [];
    page.innerHTML = `
      <div class="page-head"><div><h1>Access control</h1><div class="sub">Who can do what. Roles carry permissions; scopes narrow a permission to a program, facility, work center, contract, customer or shift.</div></div>
        <div class="spacer"></div>${can ? '<button class="btn" id="register">Register person</button>' : ''}</div>
      ${can ? '' : '<div class="notice">You can see access but not change it. Changes need access management authority.</div>'}
      <section class="panel"><header><h2>People</h2></header>
        ${people.error ? errorBlock(people.error, 'people') : !(people.data ?? []).length ? emptyBlock('Nobody registered', 'Register a person with their identity-provider subject.') : `
        <div class="table-wrap"><table class="table"><thead><tr><th>Person</th><th>Roles</th><th>Programs</th><th>Markings</th><th>Status</th><th></th></tr></thead>
          <tbody>${peopleRows(people.data, programs, can, session.config?.authMode === 'Local')}</tbody></table></div>`}
      </section>
      ${can ? `<section class="panel"><header><h2>Role policy</h2></header>
        <div class="muted">Roles and their permissions are part of the audited configuration. Removing access management from every role is refused.</div>
        ${policy.error ? errorBlock(policy.error, 'access policy') : `<div class="field"><label for="policy-role">Current role</label><select class="input" id="policy-role">${knownPolicyRoles.map(r => `<option value="${esc(r.role)}" ${r.role === selectedPolicyRole ? 'selected' : ''}>${esc(r.displayName)} (${esc(r.role)})</option>`).join('')}</select></div>
          ${!selectedRole ? emptyBlock('No active roles', 'No active role policy is available.') : selectedPermissions.length ? `<div class="table-wrap"><table class="table"><thead><tr><th>Permission</th><th>Scope</th></tr></thead><tbody>${selectedPermissions.map(p => `<tr><td class="mono">${esc(p.permission)}</td><td>${(p.scopes ?? []).length ? p.scopes.map(s => `<span class="chip">${esc(s.scopeType)}: ${esc(s.scopeId)}</span>`).join(' ') : '<span class="muted">All resources</span>'}</td></tr>`).join('')}</tbody></table></div>` : '<div class="muted">This role has no permission grants.</div>'}`}
        <div class="actions"><button class="btn" id="create-role">Create role</button><button class="btn" id="add-permission">Add permission to role</button><button class="btn ghost" id="remove-permission">Remove permission from role</button>
          <button class="btn" id="add-scope">Scope a role permission</button><button class="btn ghost" id="remove-scope">Remove a scope</button></div></section>
      <section class="panel"><header><h2>Separation of duties</h2></header>
        ${policy.error ? errorBlock(policy.error, 'separation policy') : `<table class="table"><tbody>${SEPARATION_POLICIES.map(([id, text]) => { const current = policy.data?.separationPolicies?.find(item => item.policy === id); return `<tr><td class="mono">${esc(id)}</td><td>${esc(text)}</td>
          <td>${current ? `<span class="pill ${current.enabled ? 'ok' : 'none'}">${current.enabled ? 'Enabled' : 'Disabled'}</span>` : '<span class="muted">State unavailable</span>'}</td>
          <td class="row-actions">${current ? `<button class="btn small ${current.enabled ? 'ghost' : ''}" data-policy="${esc(id)}" data-enabled="${!current.enabled}">Turn ${current.enabled ? 'off' : 'on'}</button>` : ''}</td></tr>`; }).join('')}</tbody></table>`}</section>
      <section class="panel"><header><h2>Approval thresholds</h2></header>
        <div class="muted">Caps the amount a permission can approve, in one currency. Leave the amount blank to remove a cap.</div>
        ${policy.error ? errorBlock(policy.error, 'approval thresholds') : (policy.data?.approvalThresholds ?? []).length ? `<div class="table-wrap"><table class="table"><thead><tr><th>Permission</th><th>Limit</th></tr></thead><tbody>${policy.data.approvalThresholds.map(t => `<tr><td class="mono">${esc(t.permission)}</td><td>${esc(t.maximumAmount)} ${esc(t.currency)}</td></tr>`).join('')}</tbody></table></div>` : '<div class="muted">No approval thresholds are currently configured.</div>'}
        <div class="actions"><button class="btn" id="threshold">Set approval threshold</button></div></section>` : ''}`;
    $('#register', page)?.addEventListener('click', register);
    $$('[data-grant-role]', page).forEach(b => b.addEventListener('click', () => grantRole(person(b.dataset.grantRole))));
    $$('[data-grant-program]', page).forEach(b => b.addEventListener('click', () => grantProgram(person(b.dataset.grantProgram))));
    $$('[data-revoke-role]', page).forEach(b => b.addEventListener('click', () => revoke(person(b.dataset.person), 'roles', b.dataset.revokeRole, roleTitle(b.dataset.revokeRole))));
    $$('[data-revoke-program]', page).forEach(b => b.addEventListener('click', () => revoke(person(b.dataset.person), 'programs', b.dataset.revokeProgram, programs.find(p => p.programId === b.dataset.revokeProgram)?.name ?? 'program')));
    $$('[data-set-password]', page).forEach(b => b.addEventListener('click', () => setPassword(person(b.dataset.setPassword))));
    $('#create-role', page)?.addEventListener('click', createRole);
    $('#add-permission', page)?.addEventListener('click', () => rolePermission(false));
    $('#remove-permission', page)?.addEventListener('click', () => rolePermission(true));
    $('#add-scope', page)?.addEventListener('click', () => roleScope(false));
    $('#remove-scope', page)?.addEventListener('click', () => roleScope(true));
    $$('[data-policy]', page).forEach(b => b.addEventListener('click', () => separation(b.dataset.policy, b.dataset.enabled === 'true')));
    $('#threshold', page)?.addEventListener('click', threshold);
    $('#policy-role', page)?.addEventListener('change', e => { selectedPolicyRole = e.target.value; render(); });
  }

  const roleField = { name: 'role', label: 'Role', required: true, list: null, hint: 'A catalog role, or a role you created.' };

  function register() {
    formModal({ title: 'Register person', detail: 'The subject is the person\'s identity-provider user ID or employee ID. Roles are granted separately.',
      fields: [{ name: 'idpSubject', label: 'Identity-provider subject', required: true }, { name: 'displayName', label: 'Display name', required: true },
        ...MARKINGS.map(([value, text]) => ({ name: `m_${value}`, label: 'Handling', type: 'check', text: `May see ${text.toLowerCase()} data` }))],
      confirmLabel: 'Register', reasonRequired: true },
    (v, reason) => command(`${API}/people`, { idpSubject: v.idpSubject, displayName: v.displayName, markings: MARKINGS.filter(([value]) => v[`m_${value}`]).map(([value]) => value) }, reason), reload);
  }

  // Forge accounts: a temporary password the person replaces at their next sign-in. Their username is their subject.
  function setPassword(p) {
    formModal({ title: `Set a temporary password for ${p.displayName}`, detail: `They sign in as ${p.idpSubject} with this password and must choose their own straight away. At least 10 characters.`,
      fields: [{ name: 'password', label: 'Temporary password', type: 'password', required: true }], confirmLabel: 'Set password', reasonRequired: true },
    async (v, reason) => {
      const r = await fetch(`${API}/people/${p.personId}/password`, { method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json', Authorization: `Bearer ${session.token}` },
        body: JSON.stringify({ password: v.password, reason }) });
      const body = await r.json().catch(() => ({}));
      if (!r.ok) throw new Error(body.detail ?? `Password not set (${r.status}).`);
      return body;
    }, reload);
  }

  function grantRole(p) {
    formModal({ title: `Grant a role to ${p.displayName}`, detail: 'An expiry makes the grant temporary.',
      fields: [{ ...roleField, list: roles() }, { name: 'expiresAt', label: 'Expires (optional)', type: 'datetime-local' }], confirmLabel: 'Grant', reasonRequired: true },
    (v, reason) => command(`${API}/people/${p.personId}/roles`, { role: v.role, expiresAt: v.expiresAt ? new Date(v.expiresAt).toISOString() : null }, reason), reload);
  }

  function grantProgram(p) {
    const options = programs.filter(x => !p.programs.includes(x.programId)).map(x => [x.programId, x.name]);
    formModal({ title: `Grant program access to ${p.displayName}`, detail: options.length ? '' : 'They already have every program you can see.',
      fields: [{ name: 'programId', label: 'Program', type: 'select', required: true, blank: '— choose —', options }], confirmLabel: 'Grant', reasonRequired: true },
    (v, reason) => command(`${API}/people/${p.personId}/programs`, { programId: v.programId }, reason), reload);
  }

  function revoke(p, kind, id, name) {
    formModal({ title: `Revoke ${name} from ${p.displayName}`, detail: 'The grant ends now; its history stays in the audit trail.', fields: [], confirmLabel: 'Revoke', reasonRequired: true },
      (_, reason) => command(`${API}/people/${p.personId}/${kind}/${encodeURIComponent(id)}/revoke`, {}, reason), reload);
  }

  function createRole() {
    formModal({ title: 'Create role', fields: [{ name: 'role', label: 'Role code', required: true, placeholder: 'e.g. NightLead' }, { name: 'displayName', label: 'Display name', required: true },
      { name: 'permissions', label: 'Permissions', type: 'textarea', required: true, hint: 'Permission codes, one per line or comma separated (for example quality.read).' }], confirmLabel: 'Create', reasonRequired: true },
    (v, reason) => command(`${API}/roles`, { role: v.role, displayName: v.displayName, permissions: permissionList(v.permissions) }, reason), reload);
  }

  function rolePermission(remove) {
    formModal({ title: remove ? 'Remove permission from role' : 'Add permission to role', fields: [{ ...roleField, list: roles() }, { name: 'permission', label: 'Permission', required: true }],
      confirmLabel: remove ? 'Remove' : 'Add', reasonRequired: true },
    (v, reason) => { const [permission] = permissionList(v.permission); return remove
      ? command(`${API}/roles/${encodeURIComponent(v.role)}/permissions/${encodeURIComponent(permission)}/revoke`, {}, reason)
      : command(`${API}/roles/${encodeURIComponent(v.role)}/permissions`, { permission }, reason); }, reload);
  }

  function roleScope(remove) {
    formModal({ title: remove ? 'Remove a scope' : 'Scope a role permission', detail: remove ? '' : 'The role then holds the permission only for the named program, facility, work center, contract, customer or shift.',
      fields: [{ ...roleField, list: roles() }, { name: 'permission', label: 'Permission', required: true }, { name: 'scopeType', label: 'Scope', type: 'select', options: SCOPES, required: true }, { name: 'scopeId', label: 'Scope ID or code', required: true }],
      confirmLabel: remove ? 'Remove scope' : 'Add scope', reasonRequired: true },
    (v, reason) => command(`${API}/roles/${encodeURIComponent(v.role)}/permission-scopes${remove ? '/revoke' : ''}`, { permission: permissionList(v.permission)[0], scopeType: v.scopeType, scopeId: v.scopeId }, reason), reload);
  }

  function separation(policy, enabled) {
    formModal({ title: `${enabled ? 'Turn on' : 'Turn off'} ${policy}`, detail: enabled ? '' : 'Turning separation off lets a person change their own access. Forge still refuses self role changes that would protect administrative access.',
      fields: [], confirmLabel: enabled ? 'Turn on' : 'Turn off', reasonRequired: true },
    (_, reason) => command(`${API}/separation-policies/${encodeURIComponent(policy)}`, { enabled }, reason), reload);
  }

  function threshold() {
    formModal({ title: 'Set approval threshold', fields: [{ name: 'permission', label: 'Permission', required: true, placeholder: 'e.g. purchasing.approve' },
      { name: 'maximumAmount', label: 'Maximum amount (blank removes the cap)', type: 'number', min: 0, step: 'any' }, { name: 'currency', label: 'Currency', placeholder: 'USD' }], confirmLabel: 'Save', reasonRequired: true },
    (v, reason) => command(`${API}/approval-thresholds/${encodeURIComponent(permissionList(v.permission)[0])}`, thresholdBody(v), reason), reload);
  }

  await load();
  if (!disposed) render();
  return { dispose() { disposed = true; } };
}
