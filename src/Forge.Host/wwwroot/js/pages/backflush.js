import { tryGet, command } from '../api.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock, tabifyPanels } from '../ui.js';
import { formModal, canAction } from '../forms.js';
import { who } from '../people.js';
import { featureOn } from './features.js';

// Automatic material deduction (owner decisions 2026-11-01): parts set to backflush are deducted when the operation that uses them reports
// production. A shortage never stops production; it is listed here for inventory to issue by hand or correct, then resolve.

export function settingsHtml(settings, { canManage = false } = {}) {
  if (!settings?.length) return emptyBlock('No parts backflush yet', 'Choose the parts that are deducted automatically, and where from.');
  return `<table class="table"><thead><tr><th>Part</th><th>Deducted from</th><th>State</th><th>Changed</th><th></th></tr></thead><tbody>
    ${settings.map(s => `<tr><td class="mono">${esc(s.partNumber)}</td><td class="mono">${esc(s.locationCode)}</td><td><span class="pill ${s.enabled ? 'ok' : 'none'}">${s.enabled ? 'Backflush' : 'Issued by hand'}</span></td>
      <td>${esc(who(s.updatedBy))} ${esc(fmtDateTime(s.updatedAt))}</td>
      <td>${canManage ? `<button class="btn small" data-set="${esc(s.partNumber)}" data-location="${esc(s.locationCode)}" data-enabled="${s.enabled}">Change</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

export function exceptionsHtml(list, { canResolve = false } = {}) {
  if (!list?.length) return emptyBlock('No backflush shortages', 'Everything reported was deducted.');
  return `<table class="table"><thead><tr><th>Order</th><th>Part</th><th>Due</th><th>Deducted</th><th>From</th><th>Problem</th><th>Raised</th><th></th></tr></thead><tbody>
    ${list.map(e => `<tr><td class="mono">${e.orderNumber} / ${e.sequence}</td><td class="mono">${esc(e.partNumber)}</td><td>${e.quantity}</td><td>${e.deducted}</td><td class="mono">${esc(e.locationCode)}</td>
      <td class="muted">${esc(e.problem)}</td><td>${esc(fmtDateTime(e.raisedAt))}</td>
      <td>${e.status === 'Open' ? (canResolve ? `<button class="btn small primary" data-resolve="${e.exceptionId}">Resolve</button>` : '<span class="pill warn">Open</span>') : `<span class="pill ok">Resolved</span><div class="faint">${esc(e.resolutionNote ?? '')}</div>`}</td></tr>`).join('')}</tbody></table>`;
}

export async function backflushPage(page, ctx) {
  const me = ctx?.me;
  const can = { manage: canAction(me, 'inventory.manage'), resolve: canAction(me, 'inventory.transact') };
  const load = async () => {
    if (!(await featureOn('backflush'))) {
      page.innerHTML = `<div class="page-head"><div><h1>Backflush</h1></div></div>${emptyBlock('Turned off', 'Automatic material deduction is turned off for this plant (Settings → Features).')}`;
      return;
    }
    const [settings, exceptions] = await Promise.all([tryGet('/api/inventory/backflush/settings'), tryGet('/api/inventory/backflush/exceptions?open=false')]);
    page.innerHTML = `
      <div class="page-head"><div><h1>Backflush</h1><div class="sub">Parts deducted automatically when the operation that uses them reports good and scrap quantity. Production is never stopped for a shortage.</div></div>
        <div class="spacer"></div>${can.manage ? '<button class="btn primary" data-set="">+ Part</button>' : ''}</div>
      <section class="panel"><header><h2>Shortages</h2></header>${exceptions.error ? errorBlock(exceptions.error, 'backflush shortages') : exceptionsHtml(exceptions.data, { canResolve: can.resolve })}</section>
      <section class="panel"><header><h2>Parts that backflush</h2></header>${settings.error ? errorBlock(settings.error, 'backflush settings') : settingsHtml(settings.data, { canManage: can.manage })}</section>`;
    tabifyPanels(page, [{ label: 'Shortages', match: ['shortages'] }, { label: 'Parts that backflush', match: ['parts that backflush'] }]);
    for (const b of $$('[data-set]', page)) b.addEventListener('click', () => formModal({
      title: b.dataset.set ? `Backflush ${b.dataset.set}` : 'Backflush a part', confirmLabel: 'Save', reasonRequired: true,
      fields: [{ name: 'partNumber', label: 'Part', code: true, required: true, value: b.dataset.set, readonly: !!b.dataset.set },
        { name: 'locationCode', label: 'Deduct from location', code: true, required: true, value: b.dataset.location ?? '' },
        { name: 'enabled', label: 'Backflush', type: 'check', text: 'Deduct automatically when production is reported', value: b.dataset.enabled !== 'false' }],
    }, (v, reason) => command(`/api/inventory/backflush/settings/${encodeURIComponent(v.partNumber)}`, { enabled: !!v.enabled, locationCode: v.locationCode }, reason), load));
    for (const b of $$('[data-resolve]', page)) b.addEventListener('click', () => formModal({
      title: 'Resolve backflush shortage', detail: 'Issue the rest by hand (Inventory → Issue) or correct the count first, then say what was done.', confirmLabel: 'Resolve',
      fields: [{ name: 'note', label: 'What was done', type: 'textarea', required: true }],
    }, v => command(`/api/inventory/backflush/exceptions/${b.dataset.resolve}/resolve`, { note: v.note }), load));
  };
  await load();
}
