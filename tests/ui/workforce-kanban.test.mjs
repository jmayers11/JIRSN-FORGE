import assert from 'node:assert/strict';
import test from 'node:test';
import { workforceKanbanPage } from '../../src/Forge.Host/wwwroot/js/pages/workforce-kanban.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

const personId = '1f9a60c2-0ff8-4276-8125-f17efb69570a';

test('supervisor Kanban stays sparse and shows section safety impacts only after a read-only proposal analysis', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div');
  browser.document.body.append(page);
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null;
    calls.push({ path, options, body });
    if (path === '/api/resources/') return Promise.resolve(response({
      sites: [{ code: 'PLANT', name: 'Main Plant' }],
      safetyAreas: [{ code: 'CUT', siteCode: 'PLANT', name: 'Cutting' }, { code: 'ASSY', siteCode: 'PLANT', name: 'Assembly' }],
      workCenters: [{ code: 'WC1', name: 'Slitting', safetyAreaCode: 'CUT', picture: 'slitting' }, { code: 'WC2', name: 'Assembly', safetyAreaCode: 'ASSY', picture: 'assembly' }],
      machines: [], workCenterActivities: [{ workCenterCode: 'WC1', activityCode: 'STAGE' }, { workCenterCode: 'WC2', activityCode: 'ASSEMBLE' }],
      machineActivities: [], shifts: [{ code: 'DAY', name: 'Day' }],
    }));
    if (path === '/api/workforce/workers') return Promise.resolve(response([{
      personId, displayName: 'Operator One', homeWorkCenterCode: 'WC1', isActive: true,
      presenceKind: 'ClockedIn', presentUntil: '2030-10-22T20:00:00Z', unavailability: null,
      assignment: { safetyAreaCode: 'CUT', workCenterCode: 'WC1', machineCode: null, activityCode: 'STAGE', asTrainee: false }, shiftCode: 'DAY', jobTitle: 'Production Operator',
    }]));
    if (path === '/api/planning/schedules/approved') return Promise.resolve(response({ versionId: 12, result: { operations: [], orders: [] } }));
    if (path === '/api/readiness/reallocation-plan-preview') return Promise.resolve(response({
      inputFingerprint: 'snapshot-1', evidence: { outcome: 'Fail' }, output: {
        asOf: '2026-10-22T15:00:00Z', steps: [{ number: 1, preview: {
          personName: 'Operator One', current: { activityCode: 'STAGE', workCenterCode: 'WC1' },
          proposed: { activityCode: 'ASSEMBLE', workCenterCode: 'WC2' }, impacts: [
            { kind: 'WouldStop', detail: 'Cutting would lose its qualified operator.', work: { workCenterCode: 'WC1', safetyAreaCode: 'CUT' } },
            { kind: 'NewWork', detail: 'Assembly can start the proposed work.', work: { workCenterCode: 'WC2', safetyAreaCode: 'ASSY' } },
          ], after: [], options: [],
        } }], finalActiveWork: [],
      },
    }));
    return Promise.resolve(response({}));
  };

  await workforceKanbanPage(page, { me: { personId: 'supervisor', permissions: ['workforce.supervise'] } });
  assert.match(page.innerHTML, /Plant floor/);
  assert.match(page.innerHTML, /workforce-kanban-lanes/);
  assert.match(page.innerHTML, /4 × 4 staffing/);
  assert.match(page.innerHTML, /workforce-kanban-lane/);
  assert.match(page.innerHTML, /data-board-view="board"/);
  assert.match(page.innerHTML, /\/img\/stages\/slitting.jpg/);
  assert.match(page.innerHTML, /Operator One/);
  assert.match(page.innerHTML, /staff-job-title">Production Operator/);
  assert.match(page.innerHTML, /staff-presence-tag[^>]*>On site/);
  assert.doesNotMatch(page.innerHTML, /data-layout-slot/);
  assert.doesNotMatch(page.innerHTML, /class="workforce-lanes/);
  assert.doesNotMatch(page.innerHTML, /ASSEMBLY-I|ClockedIn|Current qualified records|lane-metrics/);
  assert.match(page.innerHTML, /Plant Overview/);
  assert.match(page.innerHTML, /<details class="panel workforce-schedule-context"><summary>/);
  assert.doesNotMatch(page.innerHTML, /data-destination=/);
  assert.equal(calls.some(call => call.path === '/api/readiness/operations' || call.path.startsWith('/api/maintenance/downtime?') || call.path.startsWith('/api/competency/')), false);

  const search = page.querySelector('#kanban-search');
  search.value = 'not a match'; fire(search, 'input');
  assert.equal(page.querySelector('#kanban-search'), search, 'typing keeps the search control mounted');
  assert.equal(page.querySelector('[data-worker-card]'), null, 'the unmatched person is filtered from the live board');
  search.value = ''; fire(search, 'input');
  assert.ok(page.querySelector('[data-worker-card]'), 'clearing the query restores the employee card');

  fire(page.querySelector(`[data-toggle-person="${personId}"]`), 'click');
  assert.match(page.innerHTML, /Model absence scenario/);
  assert.doesNotMatch(page.innerHTML, /data-emp-presence="1f9a60c2-0ff8-4276-8125-f17efb69570a"/, 'confirmed workers do not get a redundant presence action');
  assert.match(page.innerHTML, /data-emp-unavailable="1f9a60c2-0ff8-4276-8125-f17efb69570a"/);
  assert.match(page.innerHTML, /href="#\/workforce\?personId=1f9a60c2-0ff8-4276-8125-f17efb69570a"/);
  const destination = page.querySelector(`[data-destination="${personId}"]`);
  destination.value = 'WC2';
  fire(page.querySelector(`[data-stage-worker="${personId}"]`), 'click');
  assert.match(page.innerHTML, /Proposed activity/);
  assert.match(page.innerHTML, /Proposed moves · 1/);
  assert.ok(page.querySelector(`[data-emp-apply-move="${personId}"]`), 'a complete, unblocked proposal can be applied from the employee card');
  assert.doesNotMatch(page.innerHTML, /Scenario safety preview|WouldStop/);

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
  assert.match(page.innerHTML, /Cutting/);
  assert.match(page.innerHTML, /Assembly/);
  assert.match(page.innerHTML, /Cutting would lose its qualified operator/);
  assert.equal(calls.some(call => call.options.method === 'POST' && (call.path.endsWith('/assignment') || call.path.includes('/attendance') || call.path.includes('/unavailability'))), false);
});

test('Kanban is gated to supervisors and workforce managers', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  await workforceKanbanPage(page, { me: { personId: 'viewer', permissions: ['workforce.read'] } });
  assert.match(page.innerHTML, /Supervisor access required/);
});

test('expanded employee card exposes permission-gated management actions and the training drawer', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  const worker = { personId, displayName: 'Operator One', homeWorkCenterCode: 'WC1', isActive: true, hasPhoto: true, presenceKind: null, presentUntil: null, unavailability: null, assignment: null, shiftCode: 'DAY', jobTitle: 'Production Operator' };
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null;
    calls.push({ path, options, body });
    if (path === '/api/resources/') return Promise.resolve(response({ sites: [{ code: 'PLANT', name: 'Main Plant' }], safetyAreas: [{ code: 'CUT', siteCode: 'PLANT', name: 'Cutting' }], workCenters: [{ code: 'WC1', name: 'Slitting', safetyAreaCode: 'CUT' }], machines: [], workCenterActivities: [{ workCenterCode: 'WC1', activityCode: 'SLIT' }], machineActivities: [], shifts: [] }));
    if (path === '/api/workforce/workers') return Promise.resolve(response([worker]));
    if (path === '/api/planning/schedules/approved') return Promise.resolve(response({ versionId: null, result: { operations: [], orders: [] } }));
    if (path === '/api/competency/qualification-types') return Promise.resolve(response([{ code: 'SLIT-I', name: 'Slitting operator', kind: 'Machine', prerequisites: [] }]));
    if (path === `/api/competency/people/${personId}`) return Promise.resolve(response([{ personId, code: 'SLIT-I', status: 'InTraining', certifiedAt: null, expiresAt: null }]));
    if (path === `/api/engineering/document-training?personId=${personId}`) return Promise.resolve(response({ policy: { policy: 'Warn', documentTypes: ['WI'] }, records: [{ personId, documentNumber: 'WI-SLIT-01', revision: 'A', trainedOn: '2026-10-01' }] }));
    return Promise.resolve(response({ succeeded: true, message: 'Saved.' }));
  };

  await workforceKanbanPage(page, { me: { personId: 'manager', permissions: ['workforce.supervise', 'workforce.manage', 'workforce.photos.manage', 'competency.read', 'competency.manage', 'competency.certify'] } });
  fire(page.querySelector(`[data-toggle-person="${personId}"]`), 'click');
  assert.ok(page.querySelector(`[data-emp-training="${personId}"]`));
  assert.ok(page.querySelector(`[data-emp-title="${personId}"]`));
  assert.ok(page.querySelector(`[data-emp-department="${personId}"]`));
  assert.ok(page.querySelector(`[data-emp-photo-upload="${personId}"]`));
  assert.ok(page.querySelector(`[data-emp-photo-remove="${personId}"]`));
  assert.ok(page.querySelector(`[data-emp-presence="${personId}"]`));

  fire(page.querySelector(`[data-emp-training="${personId}"]`), 'click');
  await flush(); await flush();
  const drawerBody = browser.document.querySelector('#emp-training-drawer');
  assert.ok(drawerBody);
  assert.match(drawerBody.textContent, /Slitting operator/);
  assert.match(drawerBody.textContent, /WI-SLIT-01/);
  assert.ok(drawerBody.querySelector('[data-drawer-qual="certify"]'));

  fire(page.querySelector(`[data-emp-title="${personId}"]`), 'click');
  let modal = browser.document.querySelector('.modal');
  assert.equal(modal.querySelector('#f-jobTitle').getAttribute('maxlength'), '100');
  modal.querySelector('#f-jobTitle').value = 'Lead Slitting Operator';
  modal.querySelector('#reason').value = 'Title reviewed by supervisor';
  fire(modal.querySelector('[data-ok]'), 'click');
  await flush(); await flush();
  assert.deepEqual(calls.find(call => call.path.endsWith('/job-title'))?.body, { jobTitle: 'Lead Slitting Operator', reason: 'Title reviewed by supervisor' });

  fire(page.querySelector(`[data-emp-department="${personId}"]`), 'click');
  modal = browser.document.querySelector('.modal');
  assert.equal(modal.querySelector('#f-department').getAttribute('maxlength'), '100');
  modal.querySelector('#f-department').value = 'Production';
  modal.querySelector('#reason').value = 'Department confirmed by supervisor';
  fire(modal.querySelector('[data-ok]'), 'click');
  await flush(); await flush();
  assert.deepEqual(calls.find(call => call.path.endsWith('/department'))?.body, { department: 'Production', reason: 'Department confirmed by supervisor' });

  fire(page.querySelector(`[data-emp-presence="${personId}"]`), 'click');
  modal = browser.document.querySelector('.modal');
  assert.doesNotMatch(modal.innerHTML, /Projected/);
});

test('shift changer sits in the page header and filters the visible workforce', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const workers = [
    { personId: 'day-operator', displayName: 'Day Operator', homeWorkCenterCode: 'WC1', isActive: true, presenceKind: 'ClockedIn', assignment: { safetyAreaCode: 'CUT', workCenterCode: 'WC1' }, shiftCode: 'DAY', jobTitle: 'Operator' },
    { personId: 'night-operator', displayName: 'Night Operator', homeWorkCenterCode: 'WC1', isActive: true, presenceKind: 'ClockedIn', assignment: { safetyAreaCode: 'CUT', workCenterCode: 'WC1' }, shiftCode: 'NIGHT', jobTitle: 'Operator' },
  ];
  globalThis.fetch = path => {
    if (path === '/api/resources/') return Promise.resolve(response({
      sites: [{ code: 'PLANT', name: 'Main Plant' }],
      safetyAreas: [{ code: 'CUT', siteCode: 'PLANT', name: 'Cutting' }],
      workCenters: [{ code: 'WC1', name: 'Slitting', safetyAreaCode: 'CUT' }],
      machines: [], workCenterActivities: [], machineActivities: [],
      shifts: [{ code: 'DAY', name: 'Day Shift' }, { code: 'NIGHT', name: 'Night Shift' }],
    }));
    if (path === '/api/workforce/workers') return Promise.resolve(response(workers));
    if (path === '/api/planning/schedules/approved') return Promise.resolve(response({ versionId: null, result: { operations: [], orders: [] } }));
    return Promise.resolve(response({}));
  };

  await workforceKanbanPage(page, { me: { personId: 'supervisor', permissions: ['workforce.supervise'] } });
  const shift = page.querySelector('#kanban-shift');
  assert.ok(shift);
  assert.ok(page.innerHTML.indexOf('workforce-kanban-head-controls') < page.innerHTML.indexOf('workforce-kanban-toolbar'));
  assert.match(page.innerHTML, /Day Operator/);
  assert.match(page.innerHTML, /Night Operator/);

  shift.value = 'NIGHT'; fire(shift, 'change');
  assert.equal(page.querySelector('#kanban-shift'), shift, 'the header changer stays mounted while the board refreshes');
  assert.ok(page.querySelector('[data-worker-card="night-operator"]'));
  assert.equal(page.querySelector('[data-worker-card="day-operator"]'), null, 'workers assigned to other shifts are hidden');
});

test('workforce planning rail supports assigning unplaced staff and shows PTO and callouts', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  const now = Date.now();
  const workers = [
    { personId: 'available-person', displayName: 'Alex Available', homeWorkCenterCode: 'WC1', isActive: true, presenceKind: 'SupervisorConfirmed', unavailability: null, assignment: null, shiftCode: 'DAY', jobTitle: 'Production Operator' },
    { personId: 'pto-person', displayName: 'Pat PTO', homeWorkCenterCode: 'WC1', isActive: true, presenceKind: null, unavailability: null, assignment: null, shiftCode: 'DAY', jobTitle: 'Inspector' },
    { personId: 'callout-person', displayName: 'Casey Callout', homeWorkCenterCode: 'WC1', isActive: true, presenceKind: null, unavailability: 'CallOut', assignment: null, shiftCode: 'DAY', jobTitle: 'Technician' },
  ];
  globalThis.fetch = (path, options = {}) => {
    calls.push({ path, options });
    if (path === '/api/resources/') return Promise.resolve(response({
      sites: [{ code: 'PLANT', name: 'Main Plant' }], safetyAreas: [{ code: 'CUT', siteCode: 'PLANT', name: 'Cutting' }],
      workCenters: [{ code: 'WC1', name: 'Slitting', safetyAreaCode: 'CUT' }], machines: [],
      workCenterActivities: [{ workCenterCode: 'WC1', activityCode: 'SLIT' }], machineActivities: [], shifts: [{ code: 'DAY', name: 'Day Shift' }],
    }));
    if (path === '/api/workforce/workers') return Promise.resolve(response(workers));
    if (path.startsWith('/api/workforce/unavailability?')) return Promise.resolve(response([
      { personId: 'pto-person', kind: 'ApprovedPto', startsAt: new Date(now + 2 * 86400000).toISOString(), endsAt: new Date(now + 4 * 86400000).toISOString() },
      { personId: 'callout-person', kind: 'CallOut', startsAt: new Date(now - 60000).toISOString(), endsAt: new Date(now + 3600000).toISOString() },
    ]));
    if (path === '/api/planning/schedules/approved') return Promise.resolve(response({ versionId: null, result: { operations: [], orders: [] } }));
    return Promise.resolve(response({}));
  };

  await workforceKanbanPage(page, { me: { personId: 'supervisor', permissions: ['workforce.supervise'] } });
  assert.match(page.innerHTML, /workforce-planning-rail/);
  assert.match(page.innerHTML, /Available to assign/);
  assert.match(page.innerHTML, /Pre-shift roster/);
  assert.match(page.innerHTML, /1 active · not clocked in/);
  assert.match(page.innerHTML, /Callouts & upcoming time away/);
  assert.match(page.innerHTML, /PTO \/ vacation/);
  assert.match(page.innerHTML, /Callout/);
  assert.match(page.innerHTML, /Current/);
  assert.ok(page.querySelector('[data-worker-card="available-person"]'), 'available staff can be dragged from the right rail');
  assert.ok(page.querySelector('[data-worker-card="pto-person"]'), 'active staff who have not clocked in can be planned');
  assert.ok(page.querySelector('[data-drop-lane="WC1"]'), 'work centers accept a dropped staff card');
  assert.ok(calls.some(call => call.path.startsWith('/api/workforce/unavailability?') && call.path.includes('from=') && call.path.includes('to=')));

  fire(page.querySelector('[data-toggle-person="pto-person"]'), 'click');
  page.querySelector('[data-destination="pto-person"]').value = 'WC1';
  fire(page.querySelector('[data-stage-worker="pto-person"]'), 'click');
  assert.match(page.innerHTML, /Proposed moves · 1/);
  assert.match(page.innerHTML, /Planned · not clocked in/);
  assert.match(page.innerHTML, /1 planned, not clocked in/);
  assert.ok(page.querySelector('[data-drop-lane="WC1"] [data-worker-card="pto-person"]'), 'a staged pre-shift worker appears in the planned work-center slot');
  assert.equal(calls.some(call => call.options.method === 'POST'), false, 'assignment remains a staged proposal until analyzed');

  const mapButton = page.querySelector('[data-board-view="map"]');
  assert.equal(mapButton.dataset.boardView, 'map');
  fire(mapButton, 'click');
  assert.equal(page.querySelector('.workforce-board-header h2').textContent, 'Plant map');
  const offsite = page.querySelector('[data-floor-details="offsite"]');
  assert.ok(offsite, 'plant map shows the off-site accordion');
  assert.ok(offsite.getAttribute('open') !== null, 'the expanded employee keeps the off-site accordion open during the view change');
});

test('specialized placement allows a trainee only with an authorized trainer already on site at that work center', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  const trainerId = 'trainer-person', traineeId = 'trainee-person';
  const workers = [
    { personId: trainerId, displayName: 'Morgan Trainer', homeWorkCenterCode: 'WC1', isActive: true, presenceKind: 'ClockedIn', unavailability: null, assignment: { safetyAreaCode: 'CUT', workCenterCode: 'WC1', machineCode: 'SLITTER-A', activityCode: 'RUN_SLITTER' }, shiftCode: 'DAY', jobTitle: 'Slitting Trainer' },
    { personId: traineeId, displayName: 'Taylor Trainee', homeWorkCenterCode: 'WC1', isActive: true, presenceKind: null, unavailability: null, assignment: null, shiftCode: 'DAY', jobTitle: 'Operator' },
  ];
  globalThis.fetch = (path, options = {}) => {
    calls.push({ path, options, body: options.body ? JSON.parse(options.body) : null });
    if (path === '/api/resources/') return Promise.resolve(response({
      sites: [{ code: 'PLANT', name: 'Main Plant' }], safetyAreas: [{ code: 'CUT', siteCode: 'PLANT', name: 'Cutting' }],
      workCenters: [{ code: 'WC1', name: 'Slitting', safetyAreaCode: 'CUT' }],
      machines: [{ code: 'SLITTER-A', name: 'Slitter A', workCenterCode: 'WC1' }], workCenterActivities: [],
      machineActivities: [{ machineCode: 'SLITTER-A', activityCode: 'RUN_SLITTER', operatorQualifications: ['SLIT-II'] }],
      shifts: [{ code: 'DAY', name: 'Day Shift' }],
    }));
    if (path === '/api/workforce/workers') return Promise.resolve(response(workers));
    if (path === '/api/competency/qualification-types') return Promise.resolve(response([
      { code: 'SLIT-II', name: 'Slitting operator', kind: 'Machine', prerequisites: [] },
      { code: 'SLIT-II-TRAINER', name: 'Slitting trainer', kind: 'Trainer', prerequisites: ['SLIT-II'] },
    ]));
    if (path.startsWith('/api/competency/people/qualifications?')) return Promise.resolve(response([
      { personId: trainerId, code: 'SLIT-II', status: 'Qualified', expiresAt: null },
      { personId: trainerId, code: 'SLIT-II-TRAINER', status: 'Qualified', expiresAt: null },
      { personId: traineeId, code: 'SLIT-II', status: 'InTraining', expiresAt: null },
    ]));
    if (path === '/api/planning/schedules/approved') return Promise.resolve(response({ versionId: null, result: { operations: [], orders: [] } }));
    if (path === '/api/readiness/reallocation-plan-preview') return Promise.resolve(response({ output: { asOf: new Date().toISOString(), steps: [] } }));
    return Promise.resolve(response({}));
  };

  await workforceKanbanPage(page, { me: { personId: 'supervisor', permissions: ['workforce.supervise', 'competency.read'] } });
  fire(page.querySelector(`[data-toggle-person="${traineeId}"]`), 'click');
  page.querySelector(`[data-destination="${traineeId}"]`).value = 'WC1';
  fire(page.querySelector(`[data-stage-worker="${traineeId}"]`), 'click');
  assert.match(page.innerHTML, /Morgan Trainer is a qualified trainer already on site at Slitting/);
  assert.match(page.innerHTML, /Keep the trainer there while this trainee works/);
  const machine = page.querySelector(`[data-draft-machine="${traineeId}"]`);
  assert.ok(machine, 'the specialized activity requires an explicit machine selection');
  machine.value = 'SLITTER-A'; fire(machine, 'change');
  assert.equal(page.querySelector('#analyze-draft').disabled, false, 'supervised move can proceed to scenario review with current trainer coverage');
  fire(page.querySelector('#analyze-draft'), 'click');
  await flush();
  assert.ok(calls.some(call => call.path === '/api/readiness/reallocation-plan-preview'));
  fire(page.querySelector(`[data-emp-apply-move="${traineeId}"]`), 'click');
  const modal = browser.document.querySelector('.modal');
  assert.ok(modal, 'live assignment requires a reason-confirmed action');
  modal.querySelector('#reason').value = 'Trainer remains assigned to Slitting';
  fire(modal.querySelector('[data-ok]'), 'click');
  await flush(); await flush();
  const liveAssignment = calls.find(call => call.path === `/api/workforce/workers/${traineeId}/assignment`);
  assert.equal(liveAssignment?.body?.asTrainee, true, 'supervised training remains explicitly marked as trainee in the live assignment');
  assert.equal(liveAssignment?.body?.machineCode, 'SLITTER-A');
});

test('a currently qualified worker can be planned into specialized work without a trainer', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const workerId = 'qualified-worker';
  globalThis.fetch = path => {
    if (path === '/api/resources/') return Promise.resolve(response({
      sites: [{ code: 'PLANT', name: 'Main Plant' }], safetyAreas: [{ code: 'CUT', siteCode: 'PLANT', name: 'Cutting' }],
      workCenters: [{ code: 'WC1', name: 'Slitting', safetyAreaCode: 'CUT' }],
      machines: [{ code: 'SLITTER-A', name: 'Slitter A', workCenterCode: 'WC1' }], workCenterActivities: [],
      machineActivities: [{ machineCode: 'SLITTER-A', activityCode: 'SLIT', operatorQualifications: ['SLIT-II'] }], shifts: [],
    }));
    if (path === '/api/workforce/workers') return Promise.resolve(response([
      { personId: workerId, displayName: 'Sam Qualified', homeWorkCenterCode: 'WC1', isActive: true, presenceKind: null, unavailability: null, assignment: null },
    ]));
    if (path === '/api/competency/qualification-types') return Promise.resolve(response([
      { code: 'SLIT-II', name: 'Slitting operator', kind: 'Machine', prerequisites: [] },
    ]));
    if (path.startsWith('/api/competency/people/qualifications?')) return Promise.resolve(response([
      { personId: workerId, code: 'SLIT-II', status: 'Qualified', expiresAt: null },
    ]));
    if (path === '/api/planning/schedules/approved') return Promise.resolve(response({ versionId: null, result: { operations: [], orders: [] } }));
    return Promise.resolve(response({}));
  };

  await workforceKanbanPage(page, { me: { personId: 'supervisor', permissions: ['workforce.supervise', 'competency.read'] } });
  fire(page.querySelector(`[data-toggle-person="${workerId}"]`), 'click');
  page.querySelector(`[data-destination="${workerId}"]`).value = 'WC1';
  fire(page.querySelector(`[data-stage-worker="${workerId}"]`), 'click');
  const machine = page.querySelector(`[data-draft-machine="${workerId}"]`);
  machine.value = 'SLITTER-A'; fire(machine, 'change');
  assert.doesNotMatch(page.innerHTML, /Specialized placement blocked|authorized trainer must/);
  assert.equal(page.querySelector('#analyze-draft').disabled, false);
});

test('specialized placement is blocked until an authorized trainer is clocked in at the destination', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const traineeId = 'trainee-person';
  globalThis.fetch = path => {
    if (path === '/api/resources/') return Promise.resolve(response({
      sites: [{ code: 'PLANT', name: 'Main Plant' }], safetyAreas: [{ code: 'CUT', siteCode: 'PLANT', name: 'Cutting' }],
      workCenters: [{ code: 'WC1', name: 'Winding', safetyAreaCode: 'CUT' }],
      machines: [{ code: 'WINDER-A', name: 'Winder A', workCenterCode: 'WC1' }], workCenterActivities: [{ workCenterCode: 'WC1', activityCode: 'WIND' }],
      machineActivities: [{ machineCode: 'WINDER-A', activityCode: 'WIND', operatorQualifications: ['WIND-I'] }], shifts: [],
    }));
    if (path === '/api/workforce/workers') return Promise.resolve(response([
      { personId: traineeId, displayName: 'Taylor Trainee', homeWorkCenterCode: 'WC1', isActive: true, presenceKind: null, unavailability: null, assignment: null },
    ]));
    if (path === '/api/competency/qualification-types') return Promise.resolve(response([
      { code: 'WIND-I', name: 'Winding operator', kind: 'Machine', prerequisites: [] },
      { code: 'WIND-I-TRAINER', name: 'Winding trainer', kind: 'Trainer', prerequisites: ['WIND-I'] },
    ]));
    if (path.startsWith('/api/competency/people/qualifications?')) return Promise.resolve(response([
      { personId: traineeId, code: 'WIND-I', status: 'InTraining', expiresAt: null },
    ]));
    if (path === '/api/planning/schedules/approved') return Promise.resolve(response({ versionId: null, result: { operations: [], orders: [] } }));
    return Promise.resolve(response({}));
  };

  await workforceKanbanPage(page, { me: { personId: 'supervisor', permissions: ['workforce.supervise', 'competency.read'] } });
  fire(page.querySelector(`[data-toggle-person="${traineeId}"]`), 'click');
  page.querySelector(`[data-destination="${traineeId}"]`).value = 'WC1';
  fire(page.querySelector(`[data-stage-worker="${traineeId}"]`), 'click');
  assert.equal(page.querySelector(`[data-emp-apply-move="${traineeId}"]`), null, 'a blocked specialized assignment cannot be applied directly');
  assert.match(page.innerHTML, /A currently qualified trainer must already be clocked in and assigned to Winding/);
  assert.equal(page.querySelector('#analyze-draft').disabled, true, 'the blocked specialized move cannot be analyzed');
});

test('specialized placement fails closed when the planner cannot read qualification coverage', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const traineeId = 'trainee-person';
  globalThis.fetch = path => {
    if (path === '/api/resources/') return Promise.resolve(response({
      sites: [{ code: 'PLANT', name: 'Main Plant' }], safetyAreas: [{ code: 'CUT', siteCode: 'PLANT', name: 'Cutting' }],
      workCenters: [{ code: 'WC1', name: 'Slitting', safetyAreaCode: 'CUT' }],
      machines: [{ code: 'SLITTER-A', name: 'Slitter A', workCenterCode: 'WC1' }], workCenterActivities: [],
      machineActivities: [{ machineCode: 'SLITTER-A', activityCode: 'SLIT', operatorQualifications: ['SLIT-II'] }], shifts: [],
    }));
    if (path === '/api/workforce/workers') return Promise.resolve(response([
      { personId: traineeId, displayName: 'Taylor Trainee', homeWorkCenterCode: 'WC1', isActive: true, presenceKind: null, unavailability: null, assignment: null },
    ]));
    if (path === '/api/planning/schedules/approved') return Promise.resolve(response({ versionId: null, result: { operations: [], orders: [] } }));
    return Promise.resolve(response({}));
  };

  await workforceKanbanPage(page, { me: { personId: 'manager', permissions: ['workforce.supervise'] } });
  fire(page.querySelector(`[data-toggle-person="${traineeId}"]`), 'click');
  page.querySelector(`[data-destination="${traineeId}"]`).value = 'WC1';
  fire(page.querySelector(`[data-stage-worker="${traineeId}"]`), 'click');
  assert.match(page.innerHTML, /Qualification records are unavailable/);
  assert.equal(page.querySelector('#analyze-draft').disabled, true);
});

test('Facilities Manager can arrange image-map work centers and save with an audit reason', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null;
    calls.push({ path, options, body });
    if (path === '/api/resources/') return Promise.resolve(response({
      sites: [{ code: 'PLANT', name: 'Main Plant' }],
      safetyAreas: [{ code: 'CUT', siteCode: 'PLANT', name: 'Cutting' }],
      workCenters: [
        { code: 'WC1', name: 'Slitting', safetyAreaCode: 'CUT', picture: 'slitting', active: true },
        { code: 'WC2', name: 'Winding', safetyAreaCode: 'CUT', picture: 'slitting', active: true },
      ], machines: [], workCenterActivities: [], machineActivities: [], shifts: [],
    }));
    if (path === '/api/workforce/workers') return Promise.resolve(response([]));
    if (path === '/api/planning/schedules/approved') return Promise.resolve(response({ versionId: null, result: { operations: [], orders: [] } }));
    if (path === '/api/resources/work-centers/map-layout') return Promise.resolve(response({ succeeded: true, message: 'Map positions saved.' }));
    return Promise.resolve(response({}));
  };

  await workforceKanbanPage(page, { me: { personId: 'facilities-manager', permissions: ['workforce.supervise', 'workforce.read', 'resources.floor-map.manage'] } });
  fire(page.querySelector('[data-board-view="map"]'), 'click');
  assert.ok(page.querySelector('.plant-map-background'));
  assert.ok(page.querySelector('[data-map-pin="WC1"]'));
  assert.ok(page.querySelector('#edit-floor-layout'));
  fire(page.querySelector('#edit-floor-layout'), 'click');
  fire(page.querySelector('[data-map-step="WC1"][data-dx="1"]'), 'click');
  fire(page.querySelector('[data-map-step="WC1"][data-dx="1"]'), 'click');
  assert.ok(page.querySelector('#save-floor-layout'));
  fire(page.querySelector('#save-floor-layout'), 'click');
  const modal = browser.document.querySelector('.modal');
  modal.querySelector('#reason').value = 'Match the physical slitting hall';
  fire(modal.querySelector('[data-ok]'), 'click');
  await flush();

  const saved = calls.find(call => call.path === '/api/resources/work-centers/map-layout');
  assert.ok(saved);
  assert.equal(saved.options.method, 'POST');
  assert.equal(saved.body.reason, 'Match the physical slitting hall');
  assert.deepEqual(saved.body.placements.sort((a, b) => a.code.localeCompare(b.code)), [
    { code: 'WC1', mapX: 420, mapY: 230 }, { code: 'WC2', mapX: 700, mapY: 180 },
  ]);
  assert.ok(page.querySelector('#edit-floor-layout'), 'the map returns to read-only mode after saving');
});

test('plant map uses the clean reference plan and shows machine signals derived from Forge readiness records', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const machine = { code: 'CNC-01', name: 'Three-axis mill', workCenterCode: 'WC-CNC-MILL' };
  const secondMachine = { code: 'CNC-02', name: 'Production mill', workCenterCode: 'WC-CNC-MILL' };
  const worker = { personId: 'qualified-miller', displayName: 'Sam Miller', jobTitle: 'CNC Operator', isActive: true, presenceKind: 'ClockedIn', unavailability: null, assignment: null, shiftCode: 'DAY' };
  globalThis.fetch = path => {
    if (path === '/api/resources/') return Promise.resolve(response({
      sites: [{ code: 'WW-P01', name: 'WhiskerWorks — Purrcision Plant 01' }],
      safetyAreas: [{ code: 'CNC', siteCode: 'WW-P01', name: 'CNC Machining' }],
      workCenters: [{ code: 'WC-CNC-MILL', name: 'CNC Machining · Mills', safetyAreaCode: 'CNC' }],
      machines: [machine, secondMachine], workCenterActivities: [], machineActivities: [
        { machineCode: 'CNC-01', activityCode: 'MILL', operatorQualifications: ['QUAL-CNC-MILL', 'QUAL-CNC-SETUP'] },
        { machineCode: 'CNC-02', activityCode: 'MILL', operatorQualifications: ['QUAL-CNC-MILL'] },
      ], shifts: [],
    }));
    if (path === '/api/workforce/workers') return Promise.resolve(response([worker]));
    if (path === '/api/competency/qualification-types') return Promise.resolve(response([{ code: 'QUAL-CNC-MILL', name: 'CNC mill operator', kind: 'Machine', prerequisites: [] }]));
    if (path.startsWith('/api/competency/people/qualifications?')) return Promise.resolve(response([{ personId: worker.personId, code: 'QUAL-CNC-MILL', status: 'Qualified', expiresAt: null }]));
    if (path === '/api/planning/schedules/approved') return Promise.resolve(response({ versionId: null, result: { operations: [], orders: [] } }));
    if (path === '/api/readiness/operations') return Promise.resolve(response([{ machine: 'CNC-01', orderNumber: 771, sequence: 10, priority: 1, dueDate: '2030-10-24', partNumber: 'WW-CAT-2000', executable: false, why: 'Material shortfall: one bracket missing', dimensions: [
      { name: 'Material availability', outcome: 'Fail', detail: 'Short one bracket for this operation.' },
      { name: 'Safety staffing', outcome: 'Pass', detail: 'SAFE-CNC-01 operator requirement is met.' },
      { name: 'Quality inspection', outcome: 'Pass', detail: 'No inspection hold applies.' },
    ] }]));
    if (path === '/api/readiness/active-work' || path === '/api/maintenance/assets' || path === '/api/maintenance/work-orders?includeClosed=false') return Promise.resolve(response([]));
    if (path.startsWith('/api/maintenance/downtime?')) return Promise.resolve(response([{ machineCode: 'CNC-01', kind: 'Unplanned', startsAt: new Date(Date.now() - 60_000).toISOString(), endsAt: null, reason: 'Coolant pressure fault' }]));
    return Promise.resolve(response([]));
  };
  await workforceKanbanPage(page, { me: { personId: 'manager', permissions: ['workforce.supervise', 'resources.floor-map.manage', 'resources.read', 'planning.readiness.read', 'maintenance.read', 'competency.read'] } });
  fire(page.querySelector('[data-board-view="map"]'), 'click');
  assert.match(page.querySelector('.plant-map-background').getAttribute('src'), /sample-plant-floor\.svg$/);
  assert.match(page.querySelector('[data-map-canvas]').getAttribute('style'), /100%/);
  fire(page.querySelector('[data-map-zoom="in"]'), 'click');
  assert.match(page.querySelector('[data-map-canvas]').getAttribute('style'), /125%/, 'map zoom enlarges the scrollable canvas');
  fire(page.querySelector('[data-map-zoom="fit"]'), 'click');
  assert.match(page.querySelector('[data-map-canvas]').getAttribute('style'), /100%/, 'Fit plant returns the whole site to view');
  fire(page.querySelector('[data-map-zoom="in"]'), 'click');
  fire(page.querySelector('[data-map-layer="workers"]'), 'click');
  assert.equal(page.querySelector('[data-map-layer="workers"]').getAttribute('aria-pressed'), 'false');
  assert.equal(page.querySelector('.plant-map-pin-avatars'), null, 'the worker layer can be hidden independently');
  fire(page.querySelector('[data-map-layer="workers"]'), 'click');
  fire(page.querySelector('[data-map-toggle="WC-CNC-MILL"]'), 'click');
  assert.ok(page.querySelector('.plant-map-pin-status .machine-signal-dot.blocked'));
  assert.match(page.querySelector('[data-map-machine="CNC-01"]').textContent, /Reported down/);
  fire(page.querySelector('[data-map-layer="readiness"]'), 'click');
  assert.equal(page.querySelector('.plant-map-pin-status'), null, 'readiness signals can be hidden independently');
  fire(page.querySelector('[data-map-layer="readiness"]'), 'click');
  fire(page.querySelector('[data-map-layer="machines"]'), 'click');
  assert.equal(page.querySelector('[data-map-machine="CNC-01"]'), null, 'machine details can be hidden independently');
  fire(page.querySelector('[data-map-layer="machines"]'), 'click');
  fire(page.querySelector('[data-map-layer="orders"]'), 'click');
  assert.match(page.querySelector('[data-map-layer-panel="orders"]').textContent, /Order #771\/Op 10/);
  fire(page.querySelector('[data-map-layer="materials"]'), 'click');
  assert.match(page.querySelector('[data-map-layer-panel="materials"]').textContent, /Short one bracket/);
  fire(page.querySelector('[data-map-layer="quality"]'), 'click');
  assert.match(page.querySelector('[data-map-layer-panel="quality"]').textContent, /Pass · Quality inspection/);
  fire(page.querySelector('[data-map-layer="safety"]'), 'click');
  assert.match(page.querySelector('[data-map-layer-panel="safety"]').textContent, /Pass · Safety staffing/);
  fire(page.querySelector('[data-map-layer="maintenance"]'), 'click');
  assert.match(page.querySelector('[data-map-layer-panel="maintenance"]').textContent, /Coolant pressure fault/);
  fire(page.querySelector('[data-map-layer="bottlenecks"]'), 'click');
  assert.match(page.querySelector('[data-map-layer-panel="bottlenecks"]').textContent, /blocked|Reported down/);
  fire(page.querySelector('[data-map-machine="CNC-01"]'), 'click');
  assert.match(browser.document.querySelector('.drawer').textContent, /Machine readiness · CNC-01/);
  assert.match(browser.document.querySelector('.drawer').textContent, /Coolant pressure fault/);
  const stageButton = browser.document.querySelector('[data-machine-stage-person="qualified-miller"]');
  assert.ok(stageButton, 'machine drawer offers a stage action only for an on-site worker with current required qualifications');
  fire(stageButton, 'click');
  const coverageQuery = page.querySelector('#map-coverage-query');
  coverageQuery.value = 'CNC-02';
  fire(page.querySelector('[data-map-coverage-search]'), 'submit');
  assert.match(page.querySelector('.plant-map-coverage-results').textContent, /CNC-02 · Production mill/);
  assert.match(page.querySelector('.plant-map-coverage-results').textContent, /1 present · qualified/);
  fire(page.querySelector('.plant-map-coverage-result'), 'click');
  assert.match(browser.document.querySelector('.drawer').textContent, /Machine readiness · CNC-02/, 'coverage results open the same machine evidence drawer');
  fire(page.querySelector('[data-map-coverage-clear]'), 'click');
  assert.equal(page.querySelector('.plant-map-coverage-results'), null, 'clear removes stale search results');
  fire(page.querySelector('[data-map-zoom="reset-view"]'), 'click');
  assert.equal(page.querySelector('[data-map-layer="materials"]').getAttribute('aria-pressed'), 'false', 'Reset view restores the concise default layer set');
  assert.equal(page.querySelector('.plant-map-layer-panel'), null, 'Reset view closes expanded map details');
  fire(page.querySelector('[data-board-view="board"]'), 'click');
  fire(page.querySelector('[data-toggle-person="qualified-miller"]'), 'click');
  assert.equal(page.querySelector('[data-draft-activity="qualified-miller"]').value, 'MILL');
  assert.equal(page.querySelector('[data-draft-machine="qualified-miller"]').value, 'CNC-01');
  assert.equal(page.querySelector('#analyze-draft').disabled, false, 'staging the qualified operator leaves the proposal ready for scenario analysis');
});
