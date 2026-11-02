import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

test('features show their state and only administrators get the switch', async t => {
  installBrowser(t);
  const { featuresHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/features.js');
  const features = [{ code: 'backflush', name: 'Backflush', description: 'Deducts', enabled: true }, { code: 'receivables', name: 'Credit', description: 'Holds', enabled: false, updatedBy: 'person:x', updatedAt: '2026-11-01T12:00:00Z' }];
  const html = featuresHtml(features, { canManage: true });
  assert.match(html, /data-feature="backflush" data-enable="false">Turn off/);
  assert.match(html, /data-feature="receivables" data-enable="true">Turn on/);
  assert.doesNotMatch(featuresHtml(features), /data-feature/);
});

test('receivables show aging, credit holds and what waits for a release', async t => {
  installBrowser(t);
  const { customersHtml, waitingHtml, invoicesHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/receivables.js');
  const customer = { customerCode: 'ACME', customerName: 'Acme Corp', currency: 'USD', openInvoices: 40, onAccount: 0, openOrders: 125, creditLimit: 300, onHold: true,
    reasons: ['an invoice is 15 days past due (the plant holds after 10)'], aging: { current: 0, days1To30: 40, days31To60: 0, days61To90: 0, over90: 0 } };
  const html = customersHtml([customer], { canManage: true });
  assert.match(html, /Credit hold/);
  assert.match(html, /15 days past due/);
  assert.match(html, /data-limit="ACME"/);
  assert.match(waitingHtml([{ customerCode: 'ACME', customerName: 'Acme Corp', kind: 'Shipment', number: 7003, reasons: ['on hold'] }], { canRelease: true }), /data-release="ACME" data-kind="Shipment" data-number="7003"/);
  assert.doesNotMatch(waitingHtml([{ customerCode: 'ACME', customerName: 'Acme', kind: 'SalesOrder', number: 5, value: 75, reasons: [] }]), /data-release/);
  assert.match(invoicesHtml([{ invoiceNumber: 9, customerCode: 'ACME', invoiceDate: '2026-09-01', dueDate: '2026-10-01', currency: 'USD', total: 100, paid: 60, balance: 40, daysPastDue: 45 }]), /pill bad">45 days past due/);
});

test('supplier invoices offer decisions only to a purchasing manager who did not enter them', async t => {
  installBrowser(t);
  const { supplierInvoicesHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/payables.js');
  const invoice = { invoiceId: 3, supplierName: 'Alcoa', supplierInvoiceNumber: 'A-101', poNumber: 1001, invoiceDate: '2026-11-01', dueDate: '2026-12-01', currency: 'USD', total: 30, freight: 0, tax: 0,
    variance: 30, matchSummary: 'line 2: billed 10 but only 0 received', status: 'Exception', enteredBy: 'person:ap' };
  assert.match(supplierInvoicesHtml([invoice], { canDecide: true, me: 'person:pat' }), /data-decide="3" data-approve="true"/);
  assert.doesNotMatch(supplierInvoicesHtml([invoice], { canDecide: true, me: 'person:ap' }), /data-decide/);
  assert.match(supplierInvoicesHtml([{ ...invoice, status: 'Approved' }], { canEnter: true }), /data-paid="3"/);
});

test('backflush lists shortages to resolve and the parts that deduct automatically', async t => {
  installBrowser(t);
  const { exceptionsHtml, settingsHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/backflush.js');
  assert.match(exceptionsHtml([{ exceptionId: 4, orderNumber: 12, sequence: 10, partNumber: 'COIL-AL', quantity: 10.4, deducted: 0, locationCode: 'LINE-1', problem: 'no stock', status: 'Open', raisedAt: '2026-11-01T12:00:00Z' }], { canResolve: true }), /data-resolve="4"/);
  assert.match(settingsHtml([{ partNumber: 'COIL-AL', enabled: true, locationCode: 'STORES', updatedBy: 'person:s', updatedAt: '2026-11-01T12:00:00Z' }], { canManage: true }), /Backflush<\/span>/);
});

test('carriers show labels, tracking and the actions a shipper may take', async t => {
  installBrowser(t);
  const { shipmentsHtml, accountsHtml, parsePackages } = await import('../../src/Forge.Host/wwwroot/js/pages/carriers.js');
  assert.deepEqual(parsePackages('12@10x8x6, 30'), [{ weightLb: 12, lengthIn: 10, widthIn: 8, heightIn: 6 }, { weightLb: 30, lengthIn: null, widthIn: null, heightIn: null }]);
  const packed = { shipment: { shipmentId: 7001, customer: 'Acme', status: 'Packed' }, carrier: null };
  assert.match(shipmentsHtml([packed], { canShip: true }), /data-rate="7001"/);
  const labeled = { shipment: { shipmentId: 7002, customer: 'Acme', status: 'Shipped' }, carrier: { carrier: 'Ups', service: 'UPS Ground', tracking: '1Z', status: 'Exception', trackingSummary: 'Address unknown', hasLabel: true, accountCode: 'UPS', cost: 12.34, currency: 'USD' } };
  const html = shipmentsHtml([labeled], { canShip: true });
  assert.match(html, /pill bad">Exception/);
  assert.match(html, /data-label="7002"/);
  assert.match(html, /data-track="7002"/);
  assert.doesNotMatch(html, /data-rate="7002"/);
  assert.match(accountsHtml([{ code: 'UPS', name: 'UPS daily', kind: 'Ups', sandbox: true, active: true, hasCredentials: false, settings: {} }]), /No credentials/);
});
