import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

const order = (over = {}) => ({
  orderNumber: 8001, customerCode: 'ACME', customerName: 'Acme Corp', customerPo: 'PO-77', quoteNumber: 7001, shipTo: null, currency: 'USD', status: 'Approved',
  createdBy: 'person:rep', createdAt: '2026-10-31T12:00:00Z', approvedBy: 'person:mgr', approvedAt: '2026-10-31T13:00:00Z', total: 153, invoiced: 0,
  lines: [{ lineNumber: 1, partNumber: 'ROLL-100', quantity: 6, unitOfMeasure: 'EA', unitPrice: 25.5, amount: 153, dueDate: '2026-11-08', status: 'Open', shipped: 4, invoiced: 0, remaining: 2 }],
  uninvoicedShipments: [{ shipmentId: 12, orderLine: 1, partNumber: 'ROLL-100', quantity: 4, shippedAt: '2026-10-31T15:00:00Z', carrier: 'UPS', tracking: '1Z' }],
  ...over,
});

test('a sales order card shows shipped and invoiced quantities and only the actions the reader holds', async t => {
  installBrowser(t);
  const { salesOrderCard, money, taxText } = await import('../../src/Forge.Host/wwwroot/js/pages/sales.js');
  assert.equal(money(108.38, 'USD'), '108.38 USD');
  assert.equal(taxText({ taxExemptCertificate: 'RESALE-77', taxRatePercent: 0 }), 'Tax exempt (RESALE-77)');
  assert.equal(taxText({ taxExemptCertificate: null, taxRatePercent: 6.25 }), '6.25 % sales tax');

  const billing = salesOrderCard(order(), { bill: true });
  assert.match(billing, /SO 8001/);
  assert.match(billing, /PO PO-77/);
  assert.match(billing, /from quote 7001/);
  assert.match(billing, /data-order-invoice="8001"[^>]*>Invoice 1 shipment\(s\)/);
  assert.doesNotMatch(billing, /data-order-approve/);
  assert.doesNotMatch(billing, /data-order-cancel/);
  assert.match(billing, /customer-order demand/);

  const draft = salesOrderCard(order({ status: 'Draft', uninvoicedShipments: [] }), { manage: true, approve: true, bill: true });
  assert.match(draft, /data-order-approve="8001"/);
  assert.match(draft, /data-order-line="8001"/);
  assert.doesNotMatch(draft, /data-order-invoice/);
  assert.doesNotMatch(draft, /data-order-close/);
});

test('the accounting export summary says whether each journal balances and what was left out', async t => {
  installBrowser(t);
  const { exportSummaryHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/sales.js');
  const html = exportSummaryHtml({
    from: '2026-10-01', to: '2026-10-31', invoices: 2, voidedInPeriod: 1, invoicedSubtotal: 204, invoicedTax: 12.76, invoicedTotal: 216.76,
    salesJournalBalances: true, inventoryIncluded: true, inventoryJournalBalances: true,
    inventoryKinds: [{ kind: 'Shipment', movements: 1, valued: 1, value: 60, treatment: 'Inventory against CostOfGoodsSold.' }],
    partsWithoutStandardCost: ['COIL-AL'], accounts: { AccountsReceivable: '1200' }, notes: ['Forge keeps no general ledger.'],
  });
  assert.match(html, /<b>2<\/b> invoice\(s\)/);
  assert.match(html, /1 void\(s\) reversed/);
  assert.match(html, /Sales journal <span class="pill ok">balances/);
  assert.match(html, /Inventory against CostOfGoodsSold/);
  assert.match(html, /No standard cost for COIL-AL/);
  assert.match(html, /AccountsReceivable 1200/);
  assert.match(exportSummaryHtml({ from: '2026-10-01', to: '2026-10-31', invoices: 0, voidedInPeriod: 0, invoicedSubtotal: 0, invoicedTax: 0, invoicedTotal: 0,
    salesJournalBalances: true, inventoryIncluded: false, inventoryJournalBalances: null, inventoryKinds: [], partsWithoutStandardCost: [], accounts: {}, notes: [] }), /Not included/);
});
