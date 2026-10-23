import assert from 'node:assert/strict';
import test from 'node:test';
import { deriveMachineCards, machineKanbanPage } from '../../src/Forge.Host/wwwroot/js/pages/machine-kanban.js';
import { installBrowser, response, fire } from './browser-harness.mjs';

const now = Date.parse('2026-10-22T15:00:00Z');
const permissions = ['resources.read', 'planning.readiness.read', 'planning.schedule.read', 'maintenance.read', 'workforce.read', 'competency.read'];

test('machine signal precedence preserves every blocker and keeps missing readiness gray', () => {
  const cards = deriveMachineCards({
    now,
    permissions,
    machines: [
      { code: 'M-RED', name: 'Slitter', workCenterCode: 'WC1' },
      { code: 'M-PM', name: 'Saw', workCenterCode: 'WC1' },
      { code: 'M-READY', name: 'Press', workCenterCode: 'WC2' },
      { code: 'M-UNKNOWN', name: 'Idle machine', workCenterCode: 'WC2' },
    ],
    workCenters: [{ code: 'WC1', name: 'Cutting' }, { code: 'WC2', name: 'Forming' }],
    operations: [
      { orderNumber: 101, sequence: 10, machine: 'M-RED', executable: false, primaryConstraint: 'Safety', why: 'Two qualified workers are required.', dimensions: [{ name: 'Safety', outcome: 'Fail', detail: 'Only one qualified worker is assigned.' }] },
      { orderNumber: 102, sequence: 10, machine: 'M-READY', executable: true, priority: 1, dueDate: '2026-10-24', description: 'Form housing', partNumber: 'CASE-1', dimensions: [{ name: 'Safety', outcome: 'Pass', detail: 'Required crew is present.' }] },
    ],
    activeWork: [{ output: { work: { machineCode: 'M-RED', activityCode: 'SLIT' }, executable: false, dimensions: [{ name: 'Safety', outcome: 'Fail', detail: 'Only one qualified worker is assigned.' }] } }],
    downtime: [{ machineCode: 'M-RED', kind: 'Unplanned', startsAt: '2026-10-22T14:00:00Z', endsAt: null, reason: 'Drive fault' }],
    assets: [{ machineCode: 'M-RED', assetCode: 'ASSET-M-RED', status: 'Restricted' }],
    maintenanceOrders: [{ machineCode: 'M-PM', workOrderId: 88, title: 'Preventive inspection', kind: 'Preventive', status: 'Open', dueDate: '2026-10-20', dueByRunHours: false }],
  });

  assert.deepEqual(cards.map(card => [card.machine.code, card.status]), [
    ['M-RED', 'blocked'], ['M-PM', 'attention'], ['M-READY', 'ready'], ['M-UNKNOWN', 'unknown'],
  ]);
  const red = cards[0];
  assert.ok(red.blockers.some(item => item.source === 'Recorded downtime' && item.text.includes('Drive fault')));
  assert.ok(red.blockers.some(item => item.source === 'Production asset'));
  assert.ok(red.blockers.some(item => item.source === 'Assigned-work readiness' && item.text.includes('Only one qualified worker')));
  assert.equal(cards[2].summary, 'Eligible open operation passes readiness');
});

test('machine board reads Forge sources, exposes evidence and filters by signal', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('main'); browser.document.body.append(page);
  const calls = [];
  const machine = { code: 'M-READY', name: 'Hydraulic Press', workCenterCode: 'PRESS' };
  globalThis.fetch = path => {
    calls.push(path);
    if (path === '/api/resources/') return Promise.resolve(response({ machines: [machine], workCenters: [{ code: 'PRESS', name: 'Press Shop' }] }));
    if (path === '/api/readiness/operations') return Promise.resolve(response([{ orderNumber: 501, sequence: 10, machine: 'M-READY', workCenter: 'PRESS', executable: true, priority: 1, dueDate: '2026-10-25', description: 'Press bracket', partNumber: 'BRKT-1', dimensions: [{ name: 'Safety', outcome: 'Pass', detail: 'Qualified operator present.' }, { name: 'Equipment', outcome: 'Pass', detail: 'No recorded downtime.' }] }]));
    if (path === '/api/readiness/active-work') return Promise.resolve(response([]));
    if (path.startsWith('/api/maintenance/downtime?')) return Promise.resolve(response([]));
    if (path === '/api/maintenance/assets') return Promise.resolve(response([{ machineCode: 'M-READY', assetCode: 'PRESS-ASSET', status: 'Active' }]));
    if (path === '/api/maintenance/work-orders?includeClosed=false') return Promise.resolve(response([]));
    if (path === '/api/planning/schedules/approved') return Promise.resolve(response({ versionId: 9, result: { operations: [] } }));
    if (path === '/api/workforce/workers') return Promise.resolve(response([]));
    return Promise.resolve(response({ detail: 'Unexpected request.' }, 404));
  };

  await machineKanbanPage(page, { me: { personId: 'supervisor', permissions } });
  assert.ok(calls.includes('/api/readiness/operations'));
  assert.ok(calls.some(path => path.startsWith('/api/maintenance/downtime?from=')));
  assert.match(page.innerHTML, /Machine Readiness/);
  assert.match(page.innerHTML, /Hydraulic Press/);
  const cardStart = page.innerHTML.indexOf('<details class="machine-kanban-card ready"');
  const cardEnd = page.innerHTML.indexOf('</details>', cardStart);
  const cardHtml = page.innerHTML.slice(cardStart, cardEnd);
  const summary = cardHtml.slice(0, cardHtml.indexOf('</summary>'));
  assert.match(summary, /Hydraulic Press/);
  assert.match(summary, /Press Shop \(PRESS\)/);
  assert.doesNotMatch(summary, /PRESS-ASSET|Eligible open operation|Order #|Readiness/);
  assert.match(cardHtml, /Eligible open operation passes readiness/);
  assert.match(cardHtml, /No recorded downtime/);
  assert.match(cardHtml, /#\/orders\/501/);
  assert.doesNotMatch(cardHtml.match(/^<details[^>]*>/)?.[0] ?? '', /\bopen\b/);
  assert.match(page.innerHTML, /<details class="panel machine-board-legend"><summary>About readiness signals/);
  assert.match(page.innerHTML, /<section class="kpis machine-board-kpis" aria-label="Machine readiness counts"/);
  assert.match(page.innerHTML, /class="kpi ready"><div class="k">Ready<\/div><div class="v">1<\/div>/);
  assert.match(page.innerHTML, /<header><h2 id="machine-lane-ready"[^]*?<span class="pill ok">1<\/span><\/header>/);

  const signal = page.querySelector('#machine-signal');
  signal.value = 'unknown';
  fire(signal, 'change');
  assert.match(page.innerHTML, /No machines in this signal group/);
  assert.doesNotMatch(page.innerHTML, /Hydraulic Press/);
  assert.match(page.innerHTML, /class="kpi ready"><div class="k">Ready<\/div><div class="v">0<\/div>/, 'KPI counts follow active filters');
});

test('missing readiness data cannot produce a Ready machine card', async t => {
  installBrowser(t);
  const card = deriveMachineCards({
    now, permissions,
    machines: [{ code: 'M-1', name: 'Cell 1', workCenterCode: 'WC1' }],
    workCenters: [{ code: 'WC1', name: 'Cell' }],
    errors: { operations: new Error('Unavailable') },
  })[0];
  assert.equal(card.status, 'unknown');
  assert.equal(card.summary, 'Readiness data unavailable');
});

test('machine readiness is permission-gated', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('main'); browser.document.body.append(page);
  await machineKanbanPage(page, { me: { personId: 'operator', permissions: ['workforce.read'] } });
  assert.match(page.innerHTML, /Plant access required/);
});
