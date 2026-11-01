import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

const doc = (over = {}) => ({ documentId: 3, direction: 'Inbound', partnerCode: 'ACME', transactionSet: '850', interchangeControl: '000000101', status: 'NeedsReview',
  summary: 'Purchase order PO-77 from Acme needs review.', problems: ['Line 2: no unit price; Forge does not guess prices.'], createdAt: '2026-11-01T12:00:00Z', ...over });

test('the document log shows direction, document, status and problems, with review actions only where they apply', async t => {
  installBrowser(t);
  const { documentsHtml, statusPill } = await import('../../src/Forge.Host/wwwroot/js/pages/interchange.js');
  const html = documentsHtml([doc(), doc({ documentId: 4, direction: 'Outbound', transactionSet: '997', status: 'Sent', problems: [], summary: 'acknowledges' })], { canReview: true });
  assert.match(html, /Purchase order <span class="faint mono">850/);
  assert.match(html, /Functional acknowledgment/);
  assert.match(html, /pill bad">Needs review/);
  assert.match(html, /does not guess prices/);
  assert.match(html, /data-reprocess="3"/);
  assert.doesNotMatch(html, /data-reprocess="4"/);
  assert.doesNotMatch(documentsHtml([doc()]), /data-reprocess/);
  assert.match(documentsHtml([]), /No EDI documents yet/);
  assert.match(statusPill('Accepted'), /pill ok">Accepted/);
});

test('partners, keys and webhooks show what each may do; secrets are shown once', async t => {
  installBrowser(t);
  const { partnersHtml, keysHtml, webhooksHtml, secretHtml, outboundFor } = await import('../../src/Forge.Host/wwwroot/js/pages/interchange.js');
  const partners = partnersHtml([
    { code: 'ACME', name: 'Acme Corp', kind: 'Customer', partyCode: 'ACME', ediQualifier: 'ZZ', ediId: 'ACMEEDI', ourQualifier: 'ZZ', ourId: 'FORGE', outbound: ['810', '856'], active: true, test: true },
    { code: 'ERP', name: 'Accounting', kind: 'System', partyCode: null, outbound: [], active: false },
  ], { canManage: true });
  assert.match(partners, /ZZ\/ACMEEDI · ZZ\/FORGE/);
  assert.match(partners, /title="Invoice">810/);
  assert.match(partners, /API only/);
  assert.match(partners, /pill warn">Test/);
  assert.match(partners, /data-partner="ERP"/);
  const keys = keysHtml([{ keyId: 1, partnerCode: 'ACME', prefix: 'fk_ab12cd34', scopes: ['orders', 'edi'], issuedBy: 'person:a', issuedAt: '2026-11-01T12:00:00Z' },
    { keyId: 2, partnerCode: 'ACME', prefix: 'fk_00000000', scopes: ['edi'], issuedBy: 'person:a', issuedAt: '2026-10-01T12:00:00Z', revokedAt: '2026-10-02T12:00:00Z' }], { canManage: true });
  assert.match(keys, /fk_ab12cd34…/);
  assert.match(keys, /data-revoke="1"/);
  assert.doesNotMatch(keys, /data-revoke="2"/);
  const hooks = webhooksHtml([{ webhookId: 5, name: 'ERP', url: 'https://erp.example/hooks', events: ['invoice.changed'], partnerCode: null, active: true }],
    [{ deliveryId: 9, eventName: 'invoice.changed', status: 'Pending', attempts: 2, lastStatusCode: 503, lastError: 'HTTP 503', nextAttemptAt: '2026-11-01T12:30:00Z' }]);
  assert.match(hooks, /https:\/\/erp.example\/hooks/);
  assert.match(hooks, />All</);
  assert.match(hooks, /HTTP 503/);
  assert.match(webhooksHtml([], []), /No webhooks/);
  assert.match(secretHtml('API key', 'fk_x_y', 'Use it'), /does not show it again/);
  assert.deepEqual(outboundFor('Customer'), ['855', '856', '810']);
});
