import assert from 'node:assert/strict';
import test from 'node:test';
import { workforceKanbanPage } from '../../src/Forge.Host/wwwroot/js/pages/workforce-kanban.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

const personId = '1f9a60c2-0ff8-4276-8125-f17efb69570a';

test('supervisor Kanban stages an accessible move and runs only the sequential read-only preview', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div');
  browser.document.body.append(page);
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null;
    calls.push({ path, options, body });
    if (path === '/api/resources/') return Promise.resolve(response({
      safetyAreas: [{ code: 'CUT', name: 'Cutting' }, { code: 'ASSY', name: 'Assembly' }],
      workCenters: [{ code: 'WC1', name: 'Slitting', safetyAreaCode: 'CUT' }, { code: 'WC2', name: 'Assembly', safetyAreaCode: 'ASSY' }],
      machines: [], workCenterActivities: [{ workCenterCode: 'WC1', activityCode: 'STAGE' }, { workCenterCode: 'WC2', activityCode: 'ASSEMBLE' }],
      machineActivities: [], shifts: [{ code: 'DAY', name: 'Day' }],
    }));
    if (path === '/api/workforce/workers') return Promise.resolve(response([{
      personId, displayName: 'Operator One', homeWorkCenterCode: 'WC1', isActive: true,
      presenceKind: 'ClockedIn', presentUntil: '2026-10-22T20:00:00Z', unavailability: null,
      assignment: { safetyAreaCode: 'CUT', workCenterCode: 'WC1', machineCode: null, activityCode: 'STAGE', asTrainee: false }, shiftCode: 'DAY',
    }]));
    if (path === '/api/readiness/operations') return Promise.resolve(response([]));
    if (path === '/api/planning/schedules/approved') return Promise.resolve(response({ versionId: 12, result: { operations: [], orders: [] } }));
    if (path.startsWith('/api/maintenance/downtime?')) return Promise.resolve(response([]));
    if (path.startsWith('/api/competency/people/qualifications?')) return Promise.resolve(response([{ personId, code: 'ASSEMBLY-I', status: 'Qualified', expiresAt: null }]));
    if (path === '/api/readiness/reallocation-plan-preview') return Promise.resolve(response({
      inputFingerprint: 'snapshot-1', evidence: { outcome: 'Pass' }, output: {
        asOf: '2026-10-22T15:00:00Z', steps: [{ number: 1, preview: {
          personName: 'Operator One', current: { activityCode: 'STAGE', workCenterCode: 'WC1' },
          proposed: { activityCode: 'ASSEMBLE', workCenterCode: 'WC2' }, impacts: [], after: [], options: [],
        } }], finalActiveWork: [],
      },
    }));
    return Promise.resolve(response({}));
  };

  await workforceKanbanPage(page, { me: { personId: 'supervisor', permissions: ['workforce.supervise'] } });
  assert.match(page.innerHTML, /Current-state board/);
  assert.match(page.innerHTML, /Operator One/);
  assert.match(page.innerHTML, /ASSEMBLY-I/);
  assert.match(page.innerHTML, /Model absence \(scenario only\)/);
  assert.match(page.innerHTML, /Plant Overview/);

  const destination = page.querySelector(`[data-destination="${personId}"]`);
  destination.value = 'WC2';
  fire(page.querySelector(`[data-stage-worker="${personId}"]`), 'click');
  assert.match(page.innerHTML, /Proposed activity/);
  assert.match(page.innerHTML, /Scenario draft · 1 proposed move/);

  fire(page.querySelector('#analyze-draft'), 'click');
  await flush();
  const preview = calls.find(call => call.path === '/api/readiness/reallocation-plan-preview');
  assert.ok(preview);
  assert.equal(preview.options.method, 'POST');
  assert.deepEqual(preview.body.moves, [{
    personId, safetyAreaCode: 'ASSY', workCenterCode: 'WC2', machineCode: null,
    activityCode: 'ASSEMBLE', asTrainee: false, unassign: false,
  }]);
  assert.match(page.innerHTML, /Scenario safety preview/);
  assert.match(page.innerHTML, /Read-only/);
  assert.equal(calls.some(call => call.path.endsWith('/assignment') || call.path.includes('/attendance') || call.path.includes('/unavailability')), false);
});

test('Kanban is gated to supervisors and workforce managers', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  await workforceKanbanPage(page, { me: { personId: 'viewer', permissions: ['workforce.read'] } });
  assert.match(page.innerHTML, /Supervisor access required/);
});
