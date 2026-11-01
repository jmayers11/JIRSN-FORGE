import { tryGet, command } from '../api.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast, tabifyPanels, drawer } from '../ui.js';
import { formModal, canAction } from '../forms.js';
import { who } from '../people.js';

// API and EDI (owner decisions 2026-11-01): ANSI X12 with customers and suppliers through an inbox/outbox folder, partner API keys and
// signed webhooks for other systems. An order that arrives is a draft sales order a Sales Manager approves; what Forge cannot map waits
// here for a person to fix and reprocess, or reject.

export const SETS = { 850: 'Purchase order', 855: 'Order acknowledgment', 856: 'Ship notice', 810: 'Invoice', 997: 'Functional acknowledgment' };
const TONE = { Received: 'none', Processed: 'ok', NeedsReview: 'bad', Rejected: 'none', Ready: 'warn', Sent: 'ok', Accepted: 'ok', Refused: 'bad' };
const LABEL = { NeedsReview: 'Needs review' };
export const statusPill = s => `<span class="pill ${TONE[s] ?? 'none'}">${esc(LABEL[s] ?? s)}</span>`;
const setName = t => `${esc(SETS[t] ?? `Set ${t}`)} <span class="faint mono">${esc(t)}</span>`;
const OUTBOUND = { Customer: ['855', '856', '810'], Supplier: ['850'], System: [] };

/** The document log: newest first, what each is about, and actions for documents that need review. */
export function documentsHtml(docs, { canReview = false } = {}) {
  if (!docs?.length) return emptyBlock('No EDI documents yet', 'Files placed in the inbox, uploads and partner API submissions appear here, with everything Forge sends.');
  return `<table class="table"><thead><tr><th>#</th><th>When</th><th>Direction</th><th>Partner</th><th>Document</th><th>Control</th><th>Status</th><th>About</th><th></th></tr></thead><tbody>
    ${docs.map(d => `<tr><td>${d.documentId}</td><td>${esc(fmtDateTime(d.createdAt))}</td><td>${d.direction === 'Inbound' ? '⬇ In' : '⬆ Out'}</td><td class="mono">${esc(d.partnerCode ?? '—')}</td>
      <td>${setName(d.transactionSet)}</td><td class="mono">${esc(d.interchangeControl)}</td><td>${statusPill(d.status)}</td>
      <td>${esc(d.summary ?? '')}${(d.problems ?? []).length ? `<ul class="problems">${d.problems.map(p => `<li>${esc(p)}</li>`).join('')}</ul>` : ''}</td>
      <td><button class="btn small" data-doc="${d.documentId}">View</button>${canReview && d.status === 'NeedsReview' ? ` <button class="btn small primary" data-reprocess="${d.documentId}">Reprocess</button> <button class="btn small" data-reject="${d.documentId}">Reject</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

export function partnersHtml(partners, { canManage = false } = {}) {
  if (!partners?.length) return emptyBlock('No trading partners', 'Add a customer or supplier you exchange EDI with, or another system that uses the API.');
  return `<table class="table"><thead><tr><th>Code</th><th>Name</th><th>Kind</th><th>Trades as</th><th>EDI ids (theirs / ours)</th><th>Forge sends</th><th>Status</th><th></th></tr></thead><tbody>
    ${partners.map(p => `<tr><td class="mono">${esc(p.code)}</td><td>${esc(p.name)}</td><td>${esc(p.kind)}</td><td class="mono">${esc(p.partyCode ?? '—')}</td>
      <td class="mono">${p.ediId ? `${esc(p.ediQualifier)}/${esc(p.ediId)} · ${esc(p.ourQualifier)}/${esc(p.ourId)}` : '<span class="faint">API only</span>'}</td>
      <td>${(p.outbound ?? []).map(s => `<span class="pill none" title="${esc(SETS[s] ?? s)}">${esc(s)}</span>`).join(' ') || '—'}</td>
      <td>${p.active ? '<span class="pill ok">Active</span>' : '<span class="pill none">Inactive</span>'}${p.test ? ' <span class="pill warn">Test</span>' : ''}</td>
      <td>${canManage ? `<button class="btn small" data-partner="${esc(p.code)}">Edit</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

export function keysHtml(keys, { canManage = false } = {}) {
  if (!keys?.length) return emptyBlock('No API keys', 'Issue a key to a partner or system; it is shown once and Forge keeps only its hash.');
  return `<table class="table"><thead><tr><th>Key</th><th>Partner</th><th>Scopes</th><th>Issued</th><th>Status</th><th></th></tr></thead><tbody>
    ${keys.map(k => `<tr><td class="mono">${esc(k.prefix)}…</td><td class="mono">${esc(k.partnerCode)}</td><td>${(k.scopes ?? []).map(esc).join(', ')}</td>
      <td>${esc(who(k.issuedBy))} ${esc(fmtDateTime(k.issuedAt))}</td>
      <td>${k.revokedAt ? `<span class="pill none">Revoked</span> <span class="faint">${esc(fmtDateTime(k.revokedAt))}</span>` : '<span class="pill ok">Active</span>'}</td>
      <td>${canManage && !k.revokedAt ? `<button class="btn small" data-revoke="${k.keyId}">Revoke</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

export function webhooksHtml(hooks, deliveries, { canManage = false } = {}) {
  const list = hooks?.length ? `<table class="table"><thead><tr><th>Name</th><th>Address</th><th>Events</th><th>Partner</th><th>Status</th><th></th></tr></thead><tbody>
    ${hooks.map(h => `<tr><td>${esc(h.name)}</td><td class="mono">${esc(h.url)}</td><td>${(h.events ?? []).map(esc).join(', ')}</td><td class="mono">${esc(h.partnerCode ?? 'All')}</td>
      <td>${h.active ? '<span class="pill ok">Active</span>' : '<span class="pill none">Inactive</span>'}</td>
      <td>${canManage ? `<button class="btn small" data-hook="${h.webhookId}">Edit</button>` : ''}</td></tr>`).join('')}</tbody></table>`
    : emptyBlock('No webhooks', 'A webhook sends signed notices of orders, shipments, invoices, purchase orders and EDI documents to another system.');
  const recent = deliveries?.length ? `<h3>Recent deliveries</h3><table class="table"><thead><tr><th>#</th><th>Event</th><th>Status</th><th class="num">Attempts</th><th>Last answer</th><th>Next try</th></tr></thead><tbody>
    ${deliveries.map(d => `<tr><td>${d.deliveryId}</td><td>${esc(d.eventName)}</td><td><span class="pill ${d.status === 'Delivered' ? 'ok' : d.status === 'Failed' ? 'bad' : 'warn'}">${esc(d.status)}</span></td>
      <td class="num">${d.attempts}</td><td>${d.lastStatusCode ? `HTTP ${d.lastStatusCode}` : ''} ${esc(d.lastError ?? '')}</td><td>${d.status === 'Pending' ? esc(fmtDateTime(d.nextAttemptAt)) : ''}</td></tr>`).join('')}</tbody></table>` : '';
  return list + recent;
}

/** Shown once: the new key or signing secret, with how to use it. */
export function secretHtml(label, value, usage) {
  return `<p>Copy this ${esc(label)} now. Forge does not show it again.</p><pre class="mono secret">${esc(value)}</pre><p class="faint">${esc(usage)}</p>`;
}

export async function interchangePage(page, ctx) {
  const me = ctx?.me;
  const can = { manage: canAction(me, 'interchange.manage'), review: canAction(me, 'interchange.review'), upload: canAction(me, 'interchange.receive') };
  let filter = '';
  const load = async () => {
    const [docs, partners, keys, hooks, deliveries] = await Promise.all([
      tryGet(`/api/interchange/documents${filter ? `?status=${filter}` : ''}`), tryGet('/api/interchange/partners'), tryGet('/api/interchange/keys'),
      tryGet('/api/interchange/webhooks'), tryGet('/api/interchange/webhooks/deliveries')]);
    const partnerList = partners.data ?? [];
    const events = hooks.data?.events ?? [];
    page.innerHTML = `
      <div class="page-head"><div><h1>Integrations</h1><div class="sub">EDI (ANSI X12) with customers and suppliers, API keys and signed webhooks for other systems. Orders that arrive become draft sales orders for approval; nothing Forge cannot map is guessed.</div></div></div>
      <section class="panel"><header><h2>Documents</h2><span class="spacer"></span>
        <select class="select" data-filter><option value="">All</option>${['NeedsReview', 'Received', 'Processed', 'Rejected', 'Ready', 'Sent', 'Accepted', 'Refused'].map(s => `<option value="${s}" ${filter === s ? 'selected' : ''}>${esc(LABEL[s] ?? s)}</option>`).join('')}</select>
        ${can.upload ? ' <button class="btn" data-upload>Upload X12 file</button>' : ''}${can.manage ? ' <button class="btn" data-run>Exchange folder now</button>' : ''}</header>
        ${docs.error ? errorBlock(docs.error, 'EDI documents') : documentsHtml(docs.data, { canReview: can.review })}</section>
      <section class="panel"><header><h2>Partners</h2>${can.manage ? '<span class="spacer"></span><button class="btn" data-partner="">+ Partner</button>' : ''}</header>
        ${partners.error ? errorBlock(partners.error, 'partners') : partnersHtml(partnerList, { canManage: can.manage })}</section>
      <section class="panel"><header><h2>API keys</h2>${can.manage ? '<span class="spacer"></span><button class="btn" data-key>+ API key</button>' : ''}</header>
        ${keys.error ? errorBlock(keys.error, 'API keys') : keysHtml(keys.data, { canManage: can.manage })}</section>
      <section class="panel"><header><h2>Webhooks</h2>${can.manage ? '<span class="spacer"></span><button class="btn" data-hook="">+ Webhook</button>' : ''}</header>
        ${hooks.error ? errorBlock(hooks.error, 'webhooks') : webhooksHtml(hooks.data?.webhooks, deliveries.data, { canManage: can.manage })}</section>`;
    tabifyPanels(page, [{ label: 'Documents', match: ['documents'] }, { label: 'Partners', match: ['partners'] }, { label: 'API keys', match: ['api keys'] }, { label: 'Webhooks', match: ['webhooks'] }]);

    $('[data-filter]', page)?.addEventListener('change', e => { filter = e.target.value; load(); });
    for (const b of $$('[data-doc]', page)) b.addEventListener('click', async () => {
      const r = await tryGet(`/api/interchange/documents/${b.dataset.doc}`);
      const d = r.data;
      drawer(`Document ${b.dataset.doc}`, r.error ? errorBlock(r.error, 'the document') : `<p>${setName(d.transactionSet)} · ${statusPill(d.status)} · ${esc(d.partnerCode ?? '')}</p>
        <p>${esc(d.summary ?? '')}</p>${d.businessRef ? `<p class="faint">About ${esc(d.businessRef)}</p>` : ''}<pre class="mono x12">${esc(d.content)}</pre>`);
    });
    for (const b of $$('[data-reprocess]', page)) b.addEventListener('click', () => confirmWithReason({ title: `Reprocess document ${b.dataset.reprocess}`, detail: 'Fix what it was missing first (the customer, the part, the partner). Forge then reads it again.', confirmLabel: 'Reprocess', reasonRequired: false },
      reason => command(`/api/interchange/documents/${b.dataset.reprocess}/reprocess`, {}, reason)).then(r => { if (r) { toast(r.message ?? 'Done'); load(); } }));
    for (const b of $$('[data-reject]', page)) b.addEventListener('click', () => confirmWithReason({ title: `Reject document ${b.dataset.reject}`, detail: 'Nothing is made from it. Tell the partner why.', confirmLabel: 'Reject' },
      reason => command(`/api/interchange/documents/${b.dataset.reject}/reject`, {}, reason)).then(r => { if (r) { toast(r.message ?? 'Rejected'); load(); } }));
    $('[data-upload]', page)?.addEventListener('click', () => formModal({
      title: 'Upload an X12 file', detail: 'For files a partner sent another way. The sender is read from the ISA segment.', confirmLabel: 'Receive',
      fields: [{ name: 'fileName', label: 'File name' }, { name: 'content', label: 'X12 content', type: 'textarea', required: true }],
    }, v => command('/api/interchange/documents/upload', v), load));
    $('[data-run]', page)?.addEventListener('click', async () => {
      const r = await command('/api/interchange/folder/run', {});
      toast(r.enabled ? `Folder: ${r.received} received, ${r.failed} refused, ${r.sent} sent.` : 'No EDI folder is configured (Forge:Interchange:Folder).');
      load();
    });
    for (const b of $$('[data-partner]', page)) b.addEventListener('click', () => {
      const p = partnerList.find(x => x.code === b.dataset.partner) ?? { kind: 'Customer', active: true, outbound: [] };
      formModal({
        title: p.code ? `Partner ${p.code}` : 'Add a trading partner', detail: 'EDI ids are optional: a partner without them uses the API only.', confirmLabel: 'Save', reasonRequired: true,
        fields: [{ name: 'code', label: 'Code', code: true, required: true, value: p.code, readonly: !!p.code }, { name: 'name', label: 'Name', required: true, value: p.name },
          { name: 'kind', label: 'Kind', type: 'select', options: [['Customer', 'Customer'], ['Supplier', 'Supplier'], ['System', 'Other system']], value: p.kind },
          { name: 'partyCode', label: 'Customer or supplier code', code: true, value: p.partyCode },
          { name: 'ediQualifier', label: 'Their ISA qualifier (e.g. ZZ, 01)', value: p.ediQualifier }, { name: 'ediId', label: 'Their ISA id', code: true, value: p.ediId },
          { name: 'ourQualifier', label: 'Our ISA qualifier', value: p.ourQualifier }, { name: 'ourId', label: 'Our ISA id (as they know us)', code: true, value: p.ourId },
          { name: 'outbound', label: 'Documents Forge sends (comma separated: 855, 856, 810 to customers; 850 to suppliers)', value: (p.outbound ?? []).join(', ') },
          { name: 'active', label: 'Active', type: 'check', text: 'Active', value: p.active }, { name: 'test', label: 'Test', type: 'check', text: 'Mark files as test (ISA15 T)', value: p.test }],
      }, (v, reason) => command('/api/interchange/partners', { ...v, partyCode: v.partyCode || null, ediQualifier: v.ediQualifier || null, ediId: v.ediId || null,
        ourQualifier: v.ourQualifier || null, ourId: v.ourId || null, outbound: String(v.outbound ?? '').split(',').map(s => s.trim()).filter(Boolean) }, reason), load);
    });
    $('[data-key]', page)?.addEventListener('click', () => formModal({
      title: 'Issue an API key', detail: 'Scopes: edi (submit X12), orders (customers: submit and read their orders), purchase-orders (suppliers: read their POs).', confirmLabel: 'Issue', reasonRequired: true,
      fields: [{ name: 'partnerCode', label: 'Partner', type: 'select', options: partnerList.filter(p => p.active).map(p => [p.code, `${p.code} — ${p.name}`]) },
        { name: 'scopes', label: 'Scopes (comma separated)', value: 'orders' }],
    }, (v, reason) => command('/api/interchange/keys', { partnerCode: v.partnerCode, scopes: String(v.scopes ?? '').split(',').map(s => s.trim()).filter(Boolean) }, reason),
      async r => { if (r?.key) drawer('New API key', secretHtml('API key', r.key, 'Send it in the X-Forge-Api-Key header, with an Idempotency-Key on every POST, to /api/partner/v1/.')); await load(); }));
    for (const b of $$('[data-revoke]', page)) b.addEventListener('click', () => confirmWithReason({ title: 'Revoke API key', detail: 'Requests with it stop working at once.', confirmLabel: 'Revoke' },
      reason => command(`/api/interchange/keys/${b.dataset.revoke}/revoke`, {}, reason)).then(r => { if (r) { toast(r.message ?? 'Revoked'); load(); } }));
    for (const b of $$('[data-hook]', page)) b.addEventListener('click', () => {
      const h = (hooks.data?.webhooks ?? []).find(x => String(x.webhookId) === b.dataset.hook) ?? { active: true, events: [] };
      formModal({
        title: h.webhookId ? `Webhook ${h.name}` : 'Add a webhook', detail: `Events: ${events.join(', ')}. Requests are signed: X-Forge-Signature = sha256 HMAC of "{X-Forge-Timestamp}.{body}".`, confirmLabel: 'Save', reasonRequired: true,
        fields: [{ name: 'name', label: 'Name', required: true, value: h.name }, { name: 'url', label: 'https:// address', required: true, value: h.url },
          { name: 'events', label: 'Events (comma separated)', value: (h.events ?? []).join(', ') },
          { name: 'partnerCode', label: 'Only events about this partner (blank: all)', type: 'select', blank: 'All', options: partnerList.filter(p => p.kind !== 'System').map(p => [p.code, p.name]), value: h.partnerCode ?? '' },
          { name: 'active', label: 'Active', type: 'check', text: 'Active', value: h.active },
          ...(h.webhookId ? [{ name: 'rotateSecret', label: 'Secret', type: 'check', text: 'Issue a new signing secret', value: false }] : [])],
      }, (v, reason) => command(h.webhookId ? `/api/interchange/webhooks/${h.webhookId}` : '/api/interchange/webhooks',
        { ...v, partnerCode: v.partnerCode || null, rotateSecret: !!v.rotateSecret, events: String(v.events ?? '').split(',').map(s => s.trim()).filter(Boolean) }, reason),
      async r => { if (r?.secret) drawer('Webhook signing secret', secretHtml('signing secret', r.secret, 'Check X-Forge-Signature on every request and refuse ones older than five minutes (X-Forge-Timestamp).')); await load(); });
    });
  };
  await load();
}

export const outboundFor = kind => OUTBOUND[kind] ?? [];
