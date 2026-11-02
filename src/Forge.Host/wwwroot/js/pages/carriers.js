import { tryGet, command, download } from '../api.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast, tabifyPanels } from '../ui.js';
import { formModal, canAction } from '../forms.js';
import { featureOn } from './features.js';

// Shipping carriers (owner decisions 2026-11-01): UPS, FedEx, USPS, ShipEngine and EasyPost connections for rates, labels and tracking,
// and freight or any carrier by hand with a printed bill of lading. A shipper still releases the shipment on the Shipping page.

const STATUS = { Labeled: 'warn', Manual: 'none', InTransit: 'warn', Delivered: 'ok', Exception: 'bad', Voided: 'none' };
const KINDS = [['Ups', 'UPS'], ['FedEx', 'FedEx'], ['Usps', 'USPS'], ['ShipEngine', 'ShipEngine'], ['EasyPost', 'EasyPost'], ['Manual', 'Freight / other (by hand)']];
const SECRETS = { Ups: ['clientId', 'clientSecret'], FedEx: ['clientId', 'clientSecret'], Usps: ['clientId', 'clientSecret'], ShipEngine: ['apiKey'], EasyPost: ['apiKey'], Manual: [] };
const money = (v, c) => v == null ? '' : `${Number(v).toFixed(2)} ${esc(c ?? '')}`;

export function accountsHtml(accounts, { canManage = false } = {}) {
  if (!accounts?.length) return emptyBlock('No carrier accounts', 'Add a UPS, FedEx, USPS, ShipEngine or EasyPost account, or a freight carrier entered by hand.');
  return `<table class="table"><thead><tr><th>Account</th><th>Carrier</th><th>Number</th><th>Ships from</th><th>State</th><th></th></tr></thead><tbody>
    ${accounts.map(a => `<tr><td><b>${esc(a.name)}</b> <span class="mono faint">${esc(a.code)}</span></td><td>${esc(KINDS.find(k => k[0] === a.kind)?.[1] ?? a.kind)}${a.sandbox ? ' <span class="pill warn">Sandbox</span>' : ''}</td>
      <td class="mono">${esc(a.accountNumber ?? '—')}</td><td>${esc([a.settings?.fromCity, a.settings?.fromState, a.settings?.fromPostal].filter(Boolean).join(', ') || '—')}</td>
      <td><span class="pill ${a.active ? 'ok' : 'none'}">${a.active ? 'Active' : 'Not in use'}</span>${a.kind !== 'Manual' ? (a.hasCredentials ? ' <span class="faint">credentials stored</span>' : ' <span class="pill bad">No credentials</span>') : ''}</td>
      <td>${canManage ? `<button class="btn small" data-account="${esc(a.code)}">Edit</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

export function shipmentsHtml(rows, { canShip = false } = {}) {
  if (!rows?.length) return emptyBlock('No shipments', 'Shipments are created on the Shipping page.');
  return `<table class="table"><thead><tr><th>Shipment</th><th>Customer</th><th>Status</th><th>Carrier</th><th>Tracking</th><th>Cost</th><th></th></tr></thead><tbody>
    ${rows.map(({ shipment: s, carrier: c }) => `<tr><td class="mono">${s.shipmentId}</td><td>${esc(s.customer)}${s.shipTo ? `<div class="faint">${esc(s.shipTo)}</div>` : ''}</td><td>${esc(s.status)}</td>
      <td>${c ? `${esc(c.carrier)}${c.service ? `<div class="faint">${esc(c.service)}</div>` : ''}` : '—'}</td>
      <td>${c ? `${esc(c.tracking ?? c.proNumber ?? '—')} <span class="pill ${STATUS[c.status] ?? 'none'}">${esc(c.status)}</span>${c.trackingSummary ? `<div class="faint">${esc(c.trackingSummary)}${c.lastTrackedAt ? ` · ${esc(fmtDateTime(c.lastTrackedAt))}` : ''}</div>` : ''}` : '—'}</td>
      <td>${c ? money(c.cost, c.currency) : ''}</td>
      <td>${canShip && ['Open', 'Packed'].includes(s.status) && !(c && c.status === 'Labeled') ? `<button class="btn small primary" data-rate="${s.shipmentId}">Rates &amp; label</button> <button class="btn small" data-manual="${s.shipmentId}">Freight / by hand</button>` : ''}
        ${c?.hasLabel ? ` <button class="btn small" data-label="${s.shipmentId}">Label</button>` : ''} <button class="btn small ghost" data-bol="${s.shipmentId}">Bill of lading</button>
        ${canShip && c?.status === 'Labeled' && s.status !== 'Shipped' ? ` <button class="btn small ghost" data-void="${s.shipmentId}">Void label</button>` : ''}
        ${canShip && c?.accountCode && ['Labeled', 'InTransit', 'Exception'].includes(c.status) ? ` <button class="btn small ghost" data-track="${s.shipmentId}">Track</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

const addressFields = (a = {}) => [
  { name: 'name', label: 'Ship to (name)', required: true, value: a.name }, { name: 'company', label: 'Company', value: a.company }, { name: 'street1', label: 'Street', required: true, value: a.street1 },
  { name: 'street2', label: 'Street 2', value: a.street2 }, { name: 'city', label: 'City', required: true, value: a.city }, { name: 'state', label: 'State', required: true, value: a.state },
  { name: 'postalCode', label: 'Postal code', required: true, value: a.postalCode }, { name: 'country', label: 'Country', required: true, value: a.country ?? 'US' }, { name: 'phone', label: 'Phone', value: a.phone },
  { name: 'packages', label: 'Packages: weight lb[@LxWxH in], comma separated (e.g. 12@10x8x6, 30)', required: true },
];
const address = v => ({ name: v.name, company: v.company || null, street1: v.street1, street2: v.street2 || null, city: v.city, state: v.state, postalCode: v.postalCode, country: v.country, phone: v.phone || null });
export const parsePackages = text => String(text ?? '').split(',').map(s => s.trim()).filter(Boolean).map(s => {
  const [w, dims] = s.split('@');
  const [l, wd, h] = (dims ?? '').toLowerCase().split('x').map(Number);
  return { weightLb: Number(w), lengthIn: dims ? l : null, widthIn: dims ? wd : null, heightIn: dims ? h : null };
});

export async function carriersPage(page, ctx) {
  const me = ctx?.me;
  const can = { manage: canAction(me, 'inventory.carriers.manage'), ship: canAction(me, 'inventory.shipping.ship') };
  const load = async () => {
    if (!(await featureOn('shipping-carriers'))) {
      page.innerHTML = `<div class="page-head"><div><h1>Carriers &amp; Labels</h1></div></div>${emptyBlock('Turned off', 'Shipping carriers are turned off for this plant (Settings → Features).')}`;
      return;
    }
    const [accounts, shipments] = await Promise.all([tryGet('/api/carriers/accounts'), tryGet('/api/carriers/shipments')]);
    page.innerHTML = `
      <div class="page-head"><div><h1>Carriers &amp; Labels</h1><div class="sub">Rates, labels and tracking from your carrier accounts; bills of lading and tracking numbers by hand for freight. Release the shipment on the Shipping page.</div></div>
        <div class="spacer"></div>${can.manage ? '<button class="btn" data-account="">+ Carrier account</button>' : ''}</div>
      <section class="panel"><header><h2>Shipments</h2></header>${shipments.error ? errorBlock(shipments.error, 'shipments') : shipmentsHtml(shipments.data, { canShip: can.ship })}</section>
      <section class="panel"><header><h2>Carrier accounts</h2></header>${accounts.error ? errorBlock(accounts.error, 'carrier accounts') : accountsHtml(accounts.data, { canManage: can.manage })}</section>`;
    tabifyPanels(page, [{ label: 'Shipments', match: ['shipments'] }, { label: 'Carrier accounts', match: ['carrier accounts'] }]);
    const list = accounts.data ?? [];
    for (const b of $$('[data-account]', page)) b.addEventListener('click', () => {
      const a = list.find(x => x.code === b.dataset.account) ?? { kind: 'Ups', sandbox: true, active: true, settings: {} };
      const s = a.settings ?? {};
      formModal({
        title: a.code ? `Carrier account ${a.code}` : 'Add a carrier account', confirmLabel: 'Save', reasonRequired: true,
        detail: 'Credentials are encrypted and never shown again; leave them empty to keep what is stored. UPS, FedEx and USPS use a client id and secret; ShipEngine and EasyPost an API key.',
        fields: [{ name: 'code', label: 'Code', code: true, required: true, value: a.code, readonly: !!a.code }, { name: 'name', label: 'Name', required: true, value: a.name },
          { name: 'kind', label: 'Carrier', type: 'select', options: KINDS, value: a.kind }, { name: 'accountNumber', label: 'Account / shipper number', value: a.accountNumber },
          { name: 'sandbox', label: 'Sandbox', type: 'check', text: 'Use the carrier\'s test environment', value: a.sandbox }, { name: 'active', label: 'Active', type: 'check', text: 'In use', value: a.active },
          { name: 'clientId', label: 'Client id' }, { name: 'clientSecret', label: 'Client secret' }, { name: 'apiKey', label: 'API key' },
          { name: 'fromName', label: 'Ship from: name', value: s.fromName }, { name: 'fromCompany', label: 'Ship from: company', value: s.fromCompany },
          { name: 'fromStreet1', label: 'Ship from: street', value: s.fromStreet1 }, { name: 'fromCity', label: 'Ship from: city', value: s.fromCity }, { name: 'fromState', label: 'Ship from: state', value: s.fromState },
          { name: 'fromPostal', label: 'Ship from: postal code', value: s.fromPostal }, { name: 'fromCountry', label: 'Ship from: country', value: s.fromCountry ?? 'US' }, { name: 'fromPhone', label: 'Ship from: phone', value: s.fromPhone },
          { name: 'carrierIds', label: 'ShipEngine carrier ids (comma separated)', value: s.carrierIds }, { name: 'crid', label: 'USPS CRID', value: s.crid }, { name: 'mid', label: 'USPS MID', value: s.mid }],
      }, (v, reason) => {
        const settings = Object.fromEntries(['fromName', 'fromCompany', 'fromStreet1', 'fromCity', 'fromState', 'fromPostal', 'fromCountry', 'fromPhone', 'carrierIds', 'crid', 'mid'].map(k => [k, v[k] ?? '']).filter(([, x]) => x));
        const credentials = Object.fromEntries((SECRETS[v.kind] ?? []).map(k => [k, v[k] ?? '']).filter(([, x]) => x));
        return command('/api/carriers/accounts', { code: v.code, kind: v.kind, name: v.name, accountNumber: v.accountNumber || null, sandbox: !!v.sandbox, active: !!v.active, settings,
          credentials: Object.keys(credentials).length ? credentials : null }, reason);
      }, load);
    });
    for (const b of $$('[data-rate]', page)) b.addEventListener('click', () => {
      let body = null;
      formModal({ title: `Rates for shipment ${b.dataset.rate}`, confirmLabel: 'Get rates', fields: addressFields() }, v => {
        body = { shipTo: address(v), packages: parsePackages(v.packages) };
        return command(`/api/carriers/shipments/${b.dataset.rate}/rates`, body);
      }, quote => {
        const rates = quote?.rates ?? [];
        if (!rates.length) { toast((quote?.problems ?? []).join('; ') || 'No rates returned', true); return; }
        formModal({
          title: 'Choose a rate and buy the label', confirmLabel: 'Buy label',
          detail: (quote.problems ?? []).length ? `Some carriers did not answer: ${quote.problems.join('; ')}` : 'The label is charged to the carrier account.',
          fields: [{ name: 'rate', label: 'Rate', type: 'select', required: true, options: rates.map((r, i) => [String(i), `${r.carrier} ${r.serviceName} — ${Number(r.amount).toFixed(2)} ${r.currency}${r.transitDays ? ` (${r.transitDays} days)` : ''}`]) }],
        }, choice => command(`/api/carriers/shipments/${b.dataset.rate}/label`, { rate: rates[Number(choice.rate)], ...body }), load);
      });
    });
    for (const b of $$('[data-manual]', page)) b.addEventListener('click', () => formModal({
      title: `Freight or carrier by hand for shipment ${b.dataset.manual}`, confirmLabel: 'Save',
      fields: [{ name: 'carrier', label: 'Carrier', required: true }, { name: 'proNumber', label: 'PRO number' }, { name: 'tracking', label: 'Tracking number' }, { name: 'bolNumber', label: 'BOL number (empty: BOL-shipment)' },
        ...addressFields().map(f => ({ ...f, required: false }))],
    }, v => command(`/api/carriers/shipments/${b.dataset.manual}/manual`, { carrier: v.carrier, proNumber: v.proNumber || null, tracking: v.tracking || null, bolNumber: v.bolNumber || null,
      shipTo: v.street1 ? address(v) : null, packages: v.packages ? parsePackages(v.packages) : null }), load));
    for (const b of $$('[data-label]', page)) b.addEventListener('click', () => download(`/api/carriers/shipments/${b.dataset.label}/label`, `label-${b.dataset.label}`).catch(e => toast(e.message, true)));
    for (const b of $$('[data-bol]', page)) b.addEventListener('click', () => download(`/api/carriers/shipments/${b.dataset.bol}/bol`, `bol-${b.dataset.bol}.html`).catch(e => toast(e.message, true)));
    for (const b of $$('[data-void]', page)) b.addEventListener('click', () => confirmWithReason({ title: `Void the label for shipment ${b.dataset.void}`, detail: 'Void it with the carrier too.', confirmLabel: 'Void' },
      reason => command(`/api/carriers/shipments/${b.dataset.void}/void`, {}, reason)).then(r => { if (r) { toast(r.message ?? 'Voided'); load(); } }));
    for (const b of $$('[data-track]', page)) b.addEventListener('click', () => command(`/api/carriers/shipments/${b.dataset.track}/track`, {}, 'Check tracking').then(r => { toast(r?.message ?? 'Checked'); load(); }).catch(e => toast(e.message, true)));
  };
  await load();
}
