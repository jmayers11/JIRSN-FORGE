import assert from 'node:assert/strict';
import test from 'node:test';
import { scenariosPage } from '../../src/Forge.Host/wwwroot/js/pages/scenarios.js';
import { installBrowser, response, fire, flush } from './browser-harness.mjs';

const scenarioIds = [
  '00000000-0000-4000-8000-000000000001',
  '00000000-0000-4000-8000-000000000002',
  '00000000-0000-4000-8000-000000000003',
];

test('recovery trials accumulate as independent alternatives and compare on one snapshot', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div');
  browser.document.body.append(page);
  const scenarios = [{ scenarioId: scenarioIds[0], name: 'Slitter outage next shift', createdAt: '2026-10-27T12:00:00Z', change: {
    kind: 'MachineUnavailable', machineCode: 'SL-01', orderNumber: 101, sequence: 10,
    startsAt: '2026-10-28T08:00:00Z', durationHours: 8,
  } }];
  const savedPayloads = [];
  const runIds = [];
  const optionsFor = selections => [
    { id: 'alternate-machine', title: 'Alternate Machine', feasible: selections.some(item => item.optionId === 'alternate-machine'),
      status: selections.some(item => item.optionId === 'alternate-machine') ? 'Feasible' : 'RequiresInputs',
      feasibility: 'Independently evaluated against the full visible queue.', requiredPermission: 'planning.schedule.propose',
      consequences: [], requiredInputs: [], constraints: [], evidence: [], machineAlternatives: [], affectedOrders: [101],
      projections: selections.some(item => item.optionId === 'alternate-machine') ? [{ orderNumber: 101, partNumber: 'PART-A',
        baselineCompletion: '2026-10-30T12:00:00Z', proposedCompletion: '2026-10-29T10:00:00Z', shiftHours: -26,
        baselineStatus: 'Late', proposedStatus: 'OnTime', evidence: [] }] : [] },
    { id: 'resequence', title: 'Resequencing', feasible: selections.some(item => item.optionId === 'resequence'),
      status: selections.some(item => item.optionId === 'resequence') ? 'Feasible' : 'RequiresInputs',
      feasibility: 'Independent resequence trial.', requiredPermission: 'planning.schedule.propose', consequences: [],
      requiredInputs: [], constraints: [], evidence: [], machineAlternatives: [], affectedOrders: [101],
      projections: selections.some(item => item.optionId === 'resequence') ? [{ orderNumber: 101, partNumber: 'PART-A',
        baselineCompletion: '2026-10-30T12:00:00Z', proposedCompletion: '2026-10-30T08:00:00Z', shiftHours: -4,
        baselineStatus: 'Late', proposedStatus: 'Late', evidence: [] }] : [],
      plantImpact: selections.some(item => item.optionId === 'resequence') ? { currency: 'USD', baseline: { unitsCompleted: 30, operationsCompleted: 4, laborHours: 40, setupMinutes: 30, ordersInProcess: 1, wipUnits: 5, ordersNotOnTime: 1, outputValue: 600, laborCost: 3000 }, proposed: { unitsCompleted: 36, operationsCompleted: 4, laborHours: 44, setupMinutes: 30, ordersInProcess: 1, wipUnits: 5, ordersNotOnTime: 0, outputValue: 720, laborCost: 3300 },
        operationsChanged: 1, operations: [{ orderNumber: 101, sequence: 10, baselineMachine: 'SL-01', proposedMachine: 'SL-02', baselineEnd: '2026-10-31T12:00:00Z', proposedEnd: '2026-10-30T08:00:00Z', baselineWithinHorizon: false, proposedWithinHorizon: true, units: 6, baselineLaborHours: 0, baselineSetupMinutes: 0, proposedLaborHours: 4, proposedSetupMinutes: 30 }], commitments: [], notes: [] } : null },
    { id: 'additional-shift', title: 'Additional Shift', feasible: false, status: 'RequiresInputs',
      feasibility: 'The source shift and crew are not selected.', requiredPermission: 'planning.schedule.propose', consequences: [],
      requiredInputs: ['Select a shift and qualified crew.'], constraints: [], evidence: [], machineAlternatives: [], affectedOrders: [101],
      // The API can carry the disruption scenario's projection while this option still needs inputs.
      projections: [{ orderNumber: 101, partNumber: 'PART-A', baselineCompletion: '2026-10-30T12:00:00Z',
        proposedCompletion: '2040-10-30T12:00:00Z', shiftHours: 80000, baselineStatus: 'Late', proposedStatus: 'Late', evidence: [] }] },
  ];
  const previewFor = scenario => ({ scenarioId: scenario.scenarioId, asOf: '2026-10-27T12:30:00Z', horizonDays: 14,
    scenarioDescription: 'SL-01 unavailable for eight hours.', evidenceNote: 'Read-only scenario comparison.',
    orders: [{ orderNumber: 101, partNumber: 'PART-A', change: 'Changed', baselineCompletion: '2026-10-30T12:00:00Z',
      proposedCompletion: '2026-10-31T12:00:00Z', shiftHours: 24, baselineStatus: 'Late', proposedStatus: 'Late', evidence: [] }],
    skillsCapacity: [], recoveryOptions: optionsFor(scenario.change.recoverySelections ?? []),
    plantImpact: { currency: 'USD', baseline: { unitsCompleted: 40, operationsCompleted: 4, laborHours: 48, setupMinutes: 30, ordersInProcess: 1, wipUnits: 5, ordersNotOnTime: 0, outputValue: 800, laborCost: 3600 }, proposed: { unitsCompleted: 30, operationsCompleted: 4, laborHours: 40, setupMinutes: 30, ordersInProcess: 1, wipUnits: 5, ordersNotOnTime: 1, outputValue: 600, laborCost: 3000 }, operationsChanged: 0, operations: [], commitments: [], notes: [] } });

  globalThis.fetch = (path, request = {}) => {
    const method = request.method ?? 'GET';
    if (path === '/api/planning/scenarios' && method === 'GET') return Promise.resolve(response(scenarios));
    if (path === '/api/planning/scenarios' && method === 'POST') {
      const payload = JSON.parse(request.body); savedPayloads.push(payload);
      const scenarioId = scenarioIds[savedPayloads.length];
      scenarios.push({ scenarioId, name: payload.name, change: payload.change, createdAt: '2026-10-27T12:30:00Z' });
      return Promise.resolve(response({ entityId: scenarioId, message: 'Private comparison saved.' }));
    }
    const run = /^\/api\/planning\/scenarios\/([^/]+)\/run$/.exec(path);
    if (run && method === 'POST') { runIds.push(run[1]); return Promise.resolve(response(previewFor(scenarios.find(item => item.scenarioId === run[1])))); }
    if (path === '/api/kernel/directory' || path === '/api/engineering/items' || path === '/api/workforce/workers') return Promise.resolve(response([]));
    if (path === '/api/resources/') return Promise.resolve(response({ machines: [
      { code: 'SL-01', name: 'Slitter 1' }, { code: 'SL-02', name: 'Slitter 2' },
    ], workCenters: [], activityTypes: [] }));
    if (path === '/api/kernel/me') return Promise.resolve(response({ personId: 'planner', permissions: ['planning.analyst.use'] }));
    return Promise.resolve(response([]));
  };

  await scenariosPage(page, { query: new URLSearchParams() });
  fire(page.querySelector(`[data-run="${scenarioIds[0]}"]`), 'click'); await flush(); await flush();
  assert.match(page.innerHTML, /Recovery options/);
  assert.doesNotMatch(page.innerHTML, /Recovery comparison ·/);

  const choose = async (optionId, fields) => {
    const button = page.querySelector(`[data-evaluate-option="${optionId}"]`);
    assert.ok(button, `expected ${optionId} to be selectable`);
    fire(button, 'click'); await flush();
    const modal = browser.document.querySelector('.modal');
    assert.ok(modal);
    for (const [name, value] of Object.entries({ name: 'Slitter outage recovery comparison', ...fields, reason: 'Compare independent recovery options.' })) {
      const input = modal.querySelector(name === 'reason' ? '#reason' : `#f-${name}`);
      assert.ok(input, `missing form field ${name}`);
      input.value = value;
    }
    fire(modal.querySelector('[data-ok]'), 'click');
    await flush(); await flush(); await flush();
  };

  await choose('alternate-machine', { orderNumber: '101', sequence: '10', machineCode: 'SL-02' });
  assert.equal(savedPayloads.length, 1);
  assert.deepEqual(savedPayloads[0].change.recoverySelections.map(item => item.optionId), ['alternate-machine']);
  assert.match(page.innerHTML, /Recovery comparison · 1 independent trial/);
  assert.match(page.innerHTML, /Current baseline/);
  assert.match(page.innerHTML, /Disruption scenario/);
  assert.match(page.innerHTML, /Alternate Machine · Feasible/);

  await choose('resequence', { orderNumber: '101', priority: '1' });
  assert.equal(savedPayloads.length, 2);
  assert.deepEqual(savedPayloads[1].change.recoverySelections.map(item => item.optionId), ['alternate-machine', 'resequence']);
  assert.match(page.innerHTML, /Recovery comparison · 2 independent trials/);
  assert.match(page.innerHTML, /Alternate Machine · Feasible/);
  assert.match(page.innerHTML, /Resequencing · Feasible/);
  assert.match(page.innerHTML, /same “Slitter outage recovery comparison” source snapshot as of/);
  assert.match(page.innerHTML, /choices are not combined into one plan and nothing is committed/);
  // Whole-plant figures for the baseline, the disruption and each feasible option, side by side.
  const plant = page.querySelector('.scenario-recovery-comparison table.plant-impact');
  assert.ok(plant);
  assert.match(plant.textContent, /Units finished in horizon/);
  assert.match(plant.textContent, /Standard value of finished units/);
  assert.match(plant.textContent, /720 USD/);
  assert.match(page.innerHTML, /1 operation moved/);
  assert.match(page.innerHTML, /enters horizon/);
  assert.match(page.innerHTML, /WO 101/);

  scenarios[2].change.recoverySelections.push({ optionId: 'additional-shift', orderNumber: 101 });
  fire(page.querySelector('#refresh-scenarios'), 'click'); await flush(); await flush(); await flush();
  fire(page.querySelector(`[data-run="${scenarioIds[2]}"]`), 'click'); await flush(); await flush();
  assert.equal(runIds.at(-1), scenarioIds[2]);
  const comparison = page.querySelector('.scenario-recovery-comparison');
  assert.match(comparison.textContent, /No feasible recovery projection \(RequiresInputs\)/);
  assert.doesNotMatch(comparison.textContent, /2040/);

  const disabledPage = browser.document.createElement('div'); browser.document.body.append(disabledPage);
  await scenariosPage(disabledPage, { query: new URLSearchParams(), operationalVisualizationsEnabled: false });
  fire(disabledPage.querySelector(`[data-run="${scenarioIds[2]}"]`), 'click'); await flush(); await flush();
  assert.doesNotMatch(disabledPage.innerHTML, /class="scenario-recovery-comparison"/);
  assert.match(disabledPage.innerHTML, /Evaluate a specific selection in a new private trial/);
});
