import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

test('customer part numbers list their number against ours, and order changes can be closed once handled', async t => {
  installBrowser(t);
  const { customerPartsHtml, documentsHtml, SETS } = await import('../../src/Forge.Host/wwwroot/js/pages/interchange.js');
  const html = customerPartsHtml([{ customerCode: 'ACME', customerPart: 'ACME-ROLL', partNumber: 'ROLL-100', description: 'Their roll' }], { canEdit: true });
  assert.match(html, /ACME-ROLL<\/td><td class="mono">ROLL-100/);
  assert.match(html, /data-cpart-remove="ACME\|ACME-ROLL"/);
  assert.doesNotMatch(customerPartsHtml([{ customerCode: 'ACME', customerPart: 'X', partNumber: 'Y' }]), /data-cpart-remove/);
  assert.match(customerPartsHtml([]), /never guesses/);
  assert.equal(SETS[860], 'Order change');
  const docs = documentsHtml([{ documentId: 9, direction: 'Inbound', partnerCode: 'ACME', transactionSet: '860', interchangeControl: '000000202', status: 'NeedsReview', summary: 'Acme asks to change PO-X-1', problems: ['Line 1 (ACME-ROLL): increase quantity to 6 EA'], createdAt: '2026-11-01T12:00:00Z' }], { canReview: true });
  assert.match(docs, /Order change/);
  assert.match(docs, /data-close="9"/);
});
