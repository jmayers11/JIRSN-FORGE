import { get, command } from '../api.js';
import { esc, toast, tabifyPanels } from '../ui.js';
import { canAction } from '../forms.js';

const field = (label, name, value = '', type = 'text', extra = '') => `<label class="field"><span>${esc(label)}</span><input class="input" name="${esc(name)}" type="${type}" value="${esc(value)}" ${extra}></label>`;

export async function notificationsPage(root, { me, refreshBell }) {
  const canManage = canAction(me, 'notifications.rules.manage');
  const [inboxResult, rulesResult, eventResult] = await Promise.all([
    get('/api/notifications/inbox').catch(() => ({ data: [] })),
    canManage ? get('/api/notifications/rules').catch(() => ({ data: [] })) : Promise.resolve({ data: [] }),
    canManage ? get('/api/notifications/event-types').catch(() => ({ data: [] })) : Promise.resolve({ data: [] }),
  ]);
  const alerts = inboxResult.data ?? [];
  const rules = rulesResult.data ?? [];
  const events = eventResult.data ?? [];
  const renderAlert = a => {
    const needsAck = a.resolutionMessage ? !a.resolutionAcknowledgedAt : !a.acknowledgedAt;
    const acknowledgedAt = a.resolutionMessage ? a.resolutionAcknowledgedAt : a.acknowledgedAt;
    return `<article class="card"><div class="card-head"><div><span class="badge">${esc(a.severity)}</span> <span class="muted">${esc(a.confidence)} confidence · ${esc(a.state)}</span><h3>${esc(a.title)}</h3></div><time>${esc(new Date(a.openedAt).toLocaleString())}</time></div><p>${esc(a.message)}</p>${a.resolutionMessage ? `<p class="notice">${esc(a.resolutionMessage)}</p>` : ''}<div class="actions"><a class="btn" href="${esc(a.deepLink)}">Open source</a>${needsAck && canAction(me, 'notifications.acknowledge', { programId: a.programId }) ? `<button class="btn primary" data-ack="${esc(a.alertId)}">${a.resolutionMessage ? 'Acknowledge resolution' : 'Acknowledge'}</button>` : `<span class="muted">${acknowledgedAt ? `Acknowledged ${esc(new Date(acknowledgedAt).toLocaleString())}` : 'Awaiting acknowledgment'}</span>`}</div></article>`;
  };
  const renderRule = r => `<article class="card"><div class="card-head"><div><h3>${esc(r.name)} ${r.active ? '<span class="badge">Active</span>' : '<span class="badge">Paused</span>'}</h3><p class="muted">${esc(r.triggerEventType)}${r.clearEventType ? ` → clears on ${esc(r.clearEventType)}` : ''} · ${esc(r.severity)} · ${esc(r.confidence)} confidence</p><p>${esc(r.recipientRoles.join(', '))} · ${esc(r.channels.join(', '))}</p></div><button class="btn" data-toggle-rule="${esc(r.ruleId)}">${r.active ? 'Pause' : 'Activate'}</button></div></article>`;
  root.innerHTML = `<section class="page-head"><div><h1>Notifications</h1><p class="muted">Role and scope aware alerts from Forge records. Opening a link applies normal authorization again.</p></div><button class="btn" id="refresh-alerts">Refresh</button></section>
    <section class="section"><div class="section-head"><h2>My alerts</h2><span class="muted">${alerts.length} visible</span></div><div class="stack">${alerts.length ? alerts.map(renderAlert).join('') : '<div class="empty">No alerts are currently available to your active roles and program access.</div>'}</div></section>
    ${canManage ? `<section class="section"><div class="section-head"><h2>Notification rules</h2><span class="muted">Rule changes are audited. Email requires a configured provider.</span></div><form id="notification-rule-form" class="card form-grid">
      ${field('Rule name', 'name', '', 'text', 'required maxlength="120"')}
      <label class="field"><span>Trigger event</span><select class="input" name="triggerEventType" required>${events.map(e => `<option>${esc(e)}</option>`).join('')}</select></label>
      <label class="field"><span>Clear event</span><select class="input" name="clearEventType"><option value="">No automatic resolution event</option>${events.map(e => `<option>${esc(e)}</option>`).join('')}</select></label>
      <label class="field"><span>Scope</span><select class="input" name="scopeType"><option>Global</option><option>Program</option><option>WorkCenter</option><option>Entity</option></select></label>
      ${field('Scope ID (UUID for program; code for work center; entity ID for entity)', 'scopeId')}
      ${field('Condition payload field', 'conditionField')}${field('Equals (case-insensitive)', 'conditionEquals')}
      <label class="field"><span>Severity</span><select class="input" name="severity"><option>Information</option><option>Watch</option><option>ActionRequired</option><option>Critical</option></select></label>
      <label class="field"><span>Confidence</span><select class="input" name="confidence"><option>Confirmed</option><option>High</option><option>Medium</option><option>Low</option><option>Unknown</option></select></label>
      ${field('Recipient roles (comma separated)', 'recipientRoles', 'Supervisor', 'text', 'required')}${field('Escalation roles (comma separated)', 'escalationRoles')}
      <label class="field"><span>Channels</span><span><label><input type="checkbox" name="channelInApp" checked> In-app</label> <label><input type="checkbox" name="channelEmail"> Email</label></span></label>
      ${field('Alert template', 'template', '{eventType} on {entityType} {entityId}', 'text', 'required maxlength="500"')}${field('Resolution template', 'resolutionTemplate', 'Condition cleared by {eventType}.', 'text', 'required maxlength="500"')}
      ${field('Deep link (local route only)', 'deepLinkTemplate', '#/scheduling', 'text', 'required maxlength="300"')}
      ${field('Delay (seconds)', 'delaySeconds', '0', 'number', 'min="0" max="259200"')}${field('Reminder (seconds)', 'reminderSeconds', '', 'number', 'min="60" max="2592000"')}${field('Escalation (seconds)', 'escalationSeconds', '', 'number', 'min="60" max="2592000"')}
      ${field('Quiet-hours start', 'quietStart', '', 'time')}${field('Quiet-hours end', 'quietEnd', '', 'time')}${field('Time zone', 'timeZone', Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC')}
      ${field('Deduplication window (seconds)', 'dedupSeconds', '86400', 'number', 'min="0" max="2592000"')}${field('Audit reason', 'reason', '', 'text', 'required maxlength="500"')}
      <div class="actions"><button class="btn primary" type="submit">Create rule</button></div>
    </form><div class="stack">${rules.map(renderRule).join('') || '<div class="empty">No notification rules configured.</div>'}</div></section>` : ''}`;

  // Alerts and rule management in separate tabs (UI density audit 2026-10-27).
  tabifyPanels(root, [
    { label: `My alerts (${alerts.length})`, match: ['my alerts'] },
    { label: 'Notification rules', match: ['notification rules'] },
  ]);
  root.querySelector('#refresh-alerts')?.addEventListener('click', () => notificationsPage(root, { me, refreshBell }).catch(e => toast(e.message, true)));
  root.querySelectorAll('[data-ack]').forEach(button => button.addEventListener('click', async () => {
    try { await command(`/api/notifications/alerts/${button.dataset.ack}/acknowledge`, {}, 'Acknowledged after review'); await refreshBell(); await notificationsPage(root, { me, refreshBell }); }
    catch (e) { toast(e.message, true); }
  }));
  root.querySelector('#notification-rule-form')?.addEventListener('submit', async e => {
    e.preventDefault(); const f = new FormData(e.currentTarget); const channels = [];
    if (f.has('channelInApp')) channels.push('InApp'); if (f.has('channelEmail')) channels.push('Email');
    const scopeType = f.get('scopeType');
    const draft = { name: f.get('name'), active: true, triggerEventType: f.get('triggerEventType'), clearEventType: f.get('clearEventType') || null, scopeType, scopeId: scopeType === 'Global' ? null : f.get('scopeId'), conditionField: f.get('conditionField') || null, conditionEquals: f.get('conditionEquals') || null, severity: f.get('severity'), confidence: f.get('confidence'), recipientRoles: String(f.get('recipientRoles')).split(',').map(x => x.trim()).filter(Boolean), escalationRoles: String(f.get('escalationRoles')).split(',').map(x => x.trim()).filter(Boolean), channels, template: f.get('template'), resolutionTemplate: f.get('resolutionTemplate'), deepLinkTemplate: f.get('deepLinkTemplate'), delaySeconds: Number(f.get('delaySeconds') || 0), reminderSeconds: f.get('reminderSeconds') ? Number(f.get('reminderSeconds')) : null, escalationSeconds: f.get('escalationSeconds') ? Number(f.get('escalationSeconds')) : null, quietStart: f.get('quietStart') || null, quietEnd: f.get('quietEnd') || null, timeZone: f.get('timeZone') || 'UTC', dedupSeconds: Number(f.get('dedupSeconds') || 0) };
    try { await command('/api/notifications/rules', draft, f.get('reason')); toast('Notification rule saved.'); await notificationsPage(root, { me, refreshBell }); }
    catch (err) { toast(err.message, true); }
  });
  root.querySelectorAll('[data-toggle-rule]').forEach(button => button.addEventListener('click', async () => {
    const r = rules.find(x => x.ruleId === button.dataset.toggleRule); if (!r) return;
    try { await command('/api/notifications/rules', { ...r, active: !r.active }, `Set rule ${r.active ? 'inactive' : 'active'}`); await notificationsPage(root, { me, refreshBell }); }
    catch (e) { toast(e.message, true); }
  }));
  await refreshBell();
}
