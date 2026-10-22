import assert from 'node:assert/strict';
import test from 'node:test';
import { enhanceTable, tableCsv } from '../../src/Forge.Host/wwwroot/js/ui.js';
import { csvImportModal, parseCsvText } from '../../src/Forge.Host/wwwroot/js/forms.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

test('CSV parser handles RFC quoting, embedded lines, BOM and pasted TSV', () => {
  assert.deepEqual(parseCsvText('\uFEFFpart,description\r\nP-1,"A, part"\r\nP-2,"First line\nsecond line"\r\nP-3,"A ""quoted"" value"'), [
    ['part', 'description'], ['P-1', 'A, part'], ['P-2', 'First line\nsecond line'], ['P-3', 'A "quoted" value'],
  ]);
  assert.deepEqual(parseCsvText('partNumber\tdescription\r\nP-1\tWidget'), [['partNumber', 'description'], ['P-1', 'Widget']]);
  assert.throws(() => parseCsvText('part,description\nP-1,"unfinished'), /not closed/);
});

test('CSV import previews validation and continues after rejected rows', async t => {
  const browser = installBrowser(t);
  const imports = [];
  const completionSummaries = [];
  const flow = csvImportModal({
    title: 'Import test rows', detail: 'CSV acceptance test',
    columns: [
      { name: 'code', label: 'Code', required: true },
      { name: 'quantity', label: 'Quantity', type: 'number', required: true, min: 1 },
    ],
    sampleRows: [{ code: 'EXAMPLE', quantity: 3 }],
    onRow: async row => {
      imports.push(row);
      if (row.code === 'REJECT') throw new Error('duplicate code');
    },
  }, summary => completionSummaries.push(summary));

  const modal = browser.document.querySelector('.modal');
  assert.ok(modal);
  assert.ok(modal.querySelector('[data-csv-template]'));
  assert.ok(modal.querySelector('[data-csv-file]'));
  const text = modal.querySelector('[data-csv-text]');
  text.value = 'code,quantity\nGOOD,2\n,4\nREJECT,3\nLAST,5';
  fire(text, 'input');
  assert.match(modal.querySelector('[data-csv-status]').textContent, /3 valid row\(s\), 1 row\(s\) with errors/);
  modal.querySelector('#reason').value = 'Spreadsheet onboarding';
  await modal.querySelector('[data-ok]').onclick();
  assert.deepEqual(imports, [{ code: 'GOOD', quantity: 2 }, { code: 'REJECT', quantity: 3 }, { code: 'LAST', quantity: 5 }]);
  assert.equal(modal.querySelector('[data-ok]').textContent, 'Done');
  assert.match(modal.querySelector('[data-csv-status]').textContent, /Imported 2 of 4 row\(s\); 2 row\(s\) need attention/);
  assert.match(modal.querySelector('[data-csv-preview]').textContent, /duplicate code/);
  assert.equal(modal.getAttribute('data-csv-summary'), '{"succeeded":2,"rejected":2,"total":4}');
  assert.deepEqual(completionSummaries, [{ succeeded: 2, rejected: 2, total: 4 }]);
  await modal.querySelector('[data-ok]').onclick();
  await flow;
  assert.equal(browser.document.querySelector('.modal'), null);

  const { document } = browser;
  const container = document.createElement('section');
  const table = document.createElement('table');
  const head = document.createElement('thead'), headRow = document.createElement('tr');
  const partHeader = document.createElement('th'), descriptionHeader = document.createElement('th'), actionHeader = document.createElement('th');
  partHeader.textContent = 'Part'; descriptionHeader.textContent = 'Description'; actionHeader.textContent = 'Actions';
  headRow.append(partHeader, descriptionHeader, actionHeader); head.append(headRow);
  const body = document.createElement('tbody'), row = document.createElement('tr');
  const part = document.createElement('td'), description = document.createElement('td'), action = document.createElement('td');
  part.textContent = 'P-1'; description.textContent = 'Widget, "blue"';
  const edit = document.createElement('button'), remove = document.createElement('button'); edit.textContent = 'Edit'; remove.textContent = 'Remove';
  action.textContent = ' '; action.append(edit, remove);
  row.append(part, description, action); body.append(row); table.append(head, body); container.append(table); document.body.append(container);

  assert.equal(tableCsv(table), '"Part","Description"\r\n"P-1","Widget, ""blue"""');
  assert.equal(enhanceTable(table), true);
  assert.ok(container.querySelector('[data-table-export]'));
  assert.equal(table.querySelector('th').tabIndex, -1, 'a one-row table does not gain misleading sort controls');

  const oldSession = { subject: session.subject, token: session.token };
  const oldFetch = globalThis.fetch;
  session.subject = 'sprint7-item-create-test'; session.token = null;
  globalThis.fetch = async () => response([]);
  t.after(() => { Object.assign(session, oldSession); globalThis.fetch = oldFetch; });
  const page = document.createElement('main'); document.body.append(page);
  const { engineeringPage } = await import('../../src/Forge.Host/wwwroot/js/pages/engineering.js');
  await engineeringPage(page, { params: [], query: new URLSearchParams(), me: { personId: 'engineer', permissions: ['engineering.manage', 'engineering.read'] } });
  fire(page.querySelector('#new-item'), 'click');
  const itemModal = document.querySelector('.modal');
  const makeBuy = itemModal.querySelector('#f-makeBuy'), itemClass = itemModal.querySelector('#f-class');
  assert.equal(itemClass.value, 'Make');
  makeBuy.value = 'Buy'; fire(makeBuy, 'change');
  assert.equal(itemClass.value, 'Unclassified', 'buy items default honestly to unclassified');
  assert.equal(itemClass.querySelectorAll('option').some(option => option.value === 'Make'), false, 'the form prevents incompatible make/buy classes');
  itemModal.querySelector('[data-cancel]').onclick();

  const now = Date.now();
  const operation = { orderNumber: 42, sequence: 10, description: 'Cut stock', machineCode: 'SLITTER-1', workCenterCode: 'SLITTING', scheduled: true,
    segments: [{ start: new Date(now - 30 * 60_000).toISOString(), end: new Date(now + 30 * 60_000).toISOString(), kind: 'Run' }],
    bindingConstraint: 'Material availability', bindingDetail: 'Material available at this start time.', setupMinutes: 12, setupFamily: 'COIL', operators: [] };
  const proposed = { versionId: 8, status: 'Proposed', result: { operations: [operation], orders: [{ orderNumber: 42, status: 'AtRisk' }] } };
  globalThis.fetch = async path => {
    const url = String(path);
    if (url.endsWith('/api/planning/schedules/approved')) return response(null);
    if (url.endsWith('/api/planning/schedules/proposed')) return response(proposed);
    if (url.startsWith('/api/maintenance/downtime')) return response([]);
    if (url === '/api/resources/') return response({ machines: [{ code: 'SLITTER-1' }], workCenters: [{ code: 'SLITTING' }], activityTypes: [] });
    if (url === '/api/horizon/events?days=90' || url === '/api/planning/constraints') return response([]);
    if (url === '/api/orders/42') return response({ orderNumber: 42, partNumber: 'COIL-AL', priority: 5, operations: [operation] });
    if (url === '/api/planning/scenarios' || url === '/api/engineering/items') return response([]);
    if (url === '/api/kernel/directory') return response([{ personId: 'person-42', displayName: 'Casey Operator' }]);
    return response([]);
  };
  const { timelinePage } = await import('../../src/Forge.Host/wwwroot/js/pages/timeline.js');
  await timelinePage(page, { query: new URLSearchParams('source=proposed'), me: { personId: 'planner', permissions: ['orders.priority'] } });
  const bar = page.querySelector('[data-gantt-order="42"]');
  assert.ok(bar, 'scheduled operation is exposed as a keyboard-operable Gantt bar');
  fire(bar, 'click'); await flush(); await flush();
  const inspector = document.querySelector('.drawer');
  assert.ok(inspector);
  assert.match(inspector.textContent, /Order & operation summary/);
  assert.match(inspector.textContent, /Material availability/);
  assert.ok(inspector.querySelector('#dispatch-priority'));
  assert.equal(inspector.querySelector('a[href="#/scenarios?order=42&op=10&wc=SLITTING"]')?.textContent, 'Run What-If Recovery Trial →');

  const { scenariosPage } = await import('../../src/Forge.Host/wwwroot/js/pages/scenarios.js');
  await scenariosPage(page, { query: new URLSearchParams('order=42&op=10&wc=SLITTING') });
  assert.equal(page.querySelector('#scenario-form input[name="orderNumber"]').value, '42');
  assert.equal(page.querySelector('#scenario-form input[name="sequence"]').value, '10');
  assert.equal(page.querySelector('#scenario-form select[name="workCenterCode"]').value, 'SLITTING');
  assert.equal(page.querySelector('#scenario-form select[name="kind"]').value, 'OperationMachine');

  globalThis.fetch = async path => {
    const url = String(path);
    if (url === '/api/reports/catalog' || url === '/api/reports/definitions' || url === '/api/programs/' || url === '/api/resources/') return response([]);
    if (url === '/api/inventory/transactions?take=500') return response([{ transactionId: 99, at: new Date(now).toISOString(), kind: 'Shipment', partNumber: 'COIL-AL', lotNumber: 'LOT-9', quantity: 3, shipmentId: 17, projectCode: 'GRANT-1', actor: 'system:shipping', reason: 'Customer delivery' }]);
    if (url === '/api/procurement/purchase-orders') return response([]);
    if (url === '/api/inventory/shipments?includeClosed=true') return response([{ shipmentId: 17, reference: 'SO-88' }]);
    return response([]);
  };
  const { reportsPage } = await import('../../src/Forge.Host/wwwroot/js/pages/reports.js');
  await reportsPage(page, { query: new URLSearchParams('report=accounting-bridge'), me: { personId: 'auditor', permissions: [] } });
  assert.match(page.innerHTML, /Accounting, Receiving & Grant Reconciliation Feed/);
  assert.match(page.innerHTML, /Shipment #17 · SO-88/);
  assert.match(page.innerHTML, /GRANT-1/);
  assert.ok(page.querySelector('#bridge-csv'));
  assert.ok(page.querySelector('#bridge-json'));
});
