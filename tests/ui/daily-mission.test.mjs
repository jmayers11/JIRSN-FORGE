import assert from 'node:assert/strict';
import test from 'node:test';
import { dailyMissionPage } from '../../src/Forge.Host/wwwroot/js/pages/daily-mission.js';
import { installBrowser, response } from './browser-harness.mjs';

test('Daily Mission groups authorized source links and keeps missing coverage visible', async t => {
  const browser = installBrowser(t), calls = [];
  const data = {
    generatedAt: new Date().toISOString(),
    actor: { personId: 'worker-1', displayName: 'Avery Manager', roles: ['Supervisor'] },
    operationalContext: { siteCode: 'WW', siteName: 'WhiskerWorks', timeZone: 'America/New_York', shiftCode: 'SECOND', shiftName: 'Second Shift', operationalDate: '2026-10-25', shiftStartsAt: '2026-10-25T14:00:00Z', shiftEndsAt: '2026-10-25T22:00:00Z', relation: 'Current', operationalDayId: 'WW:SECOND:2026-10-25' },
    sources: [
      { code: 'personal-work', status: 'Available', asOf: '2026-10-25T12:00:00Z', scope: 'Current identity' },
      { code: 'quality-and-mrb', status: 'PendingIntegration', asOf: null, scope: 'Not yet included', note: 'No all-clear conclusion is available.' },
    ],
    items: [
      { id: 'assignment:00000000-0000-4000-8000-000000000042', group: 'Assigned Work', title: 'Review order 42', reason: 'Source workflow remains authoritative.', sourceType: 'ProductionOrder', sourceId: '42', deepLink: '#/orders/42', sourceLifecycle: 'WorkAssignment', sourceStatus: 'Assigned', sourceTimestamp: '2026-10-25T11:00:00Z', deadline: null },
      { id: 'ready-operation:43:10', group: 'Ready to Execute', title: 'Eligible operation · Order 43 · Winding', reason: 'Readiness passed; review approved schedule and dispatch before starting.', sourceType: 'ProductionOperationReadiness', sourceId: '43:10', deepLink: '#/orders/43', sourceLifecycle: 'CurrentOrderReadinessProjection', sourceStatus: 'ReadinessPassed', sourceTimestamp: '2026-10-25T11:00:00Z', deadline: '2026-10-25' },
    ],
    isPartial: true,
    warnings: ['Quality and MRB coverage is still pending.'],
  };
  let preferences = { landingRoute: 'workforce-kanban', hawkAccents: 'subtle', programScope: 'all', programOptions: [
    { route: 'all', label: 'All authorized programs' }, { route: 'program:00000000-0000-4000-8000-000000000001', label: 'WW · WhiskerWorks' },
  ], options: [
    { route: 'daily-mission', label: 'Daily Mission' }, { route: 'workforce-kanban', label: 'Workforce Kanban' },
  ] };
  globalThis.fetch = (path, request = {}) => {
    calls.push({ path, method: request.method ?? 'GET', body: request.body ? JSON.parse(request.body) : null });
    if (path.startsWith('/api/work-inbox/assignments/')) data.items[0].sourceStatus = JSON.parse(request.body).status;
    if (path === '/api/daily-mission/session') return Promise.resolve(response({ mission: data, briefing: { shouldShow: true, viewedAt: '2026-10-25T12:00:00Z', dismissedAt: null } }));
    if (path === '/api/daily-mission/preferences' && request.method === 'PUT') {
      preferences = { ...preferences, ...JSON.parse(request.body) };
      return Promise.resolve(response(preferences));
    }
    if (path === '/api/daily-mission/preferences') return Promise.resolve(response(preferences));
    return Promise.resolve(response({ viewedAt: '2026-10-25T12:00:00Z', dismissedAt: '2026-10-25T12:01:00Z' }));
  };
  const root = browser.document.createElement('main'); browser.document.body.append(root);

  await dailyMissionPage(root, { me: { displayName: 'Avery Manager', roles: ['Supervisor'] } });

  assert.ok(calls.some(call => call.path === '/api/daily-mission/session' && call.method === 'POST'));
  assert.match(root.innerHTML, /WhiskerWorks · Second Shift/);
  assert.match(root.innerHTML, /10:00:00 AM/);
  assert.match(root.innerHTML, /Review order 42/);
  assert.match(root.innerHTML, /Ready to Execute/);
  assert.match(root.innerHTML, /approved schedule and dispatch/);
  assert.match(root.innerHTML, /href="#\/orders\/42"/);
  assert.match(root.innerHTML, /PARTIAL MISSION/);
  assert.match(root.innerHTML, /Updates assignment tracking only/);
  assert.match(root.innerHTML, /quality and mrb/);
  assert.match(root.innerHTML, /No all-clear conclusion is available/);
  assert.match(root.innerHTML, /FIRST BRIEFING THIS OPERATIONAL DAY/);
  assert.match(root.innerHTML, /Dismiss for Now/);
  assert.match(root.innerHTML, /Classic Workspace|Workforce Kanban/);

  const dismiss = root.querySelector('[data-dismiss-briefing]');
  await [...dismiss.listeners.get('click')][0]({ currentTarget: dismiss, target: dismiss });
  assert.ok(calls.some(call => call.path === '/api/daily-mission/briefing/dismiss' && call.method === 'POST'));
  assert.equal(root.querySelector('[data-first-briefing]').hidden, true);
  const reopen = root.querySelector('[data-reopen-briefing]');
  await [...reopen.listeners.get('click')][0]({ currentTarget: reopen, target: reopen });
  assert.equal(root.querySelector('[data-first-briefing]').hidden, false);
  assert.equal(reopen.getAttribute('aria-expanded'), 'true');

  const save = root.querySelector('[data-save-preferences]');
  root.querySelector('[data-program-scope]').value = 'program:00000000-0000-4000-8000-000000000001';
  await [...save.listeners.get('click')][0]({ currentTarget: save, target: save });
  const preferenceWrite = calls.find(call => call.path === '/api/daily-mission/preferences' && call.method === 'PUT');
  assert.equal(preferenceWrite.body.landingRoute, 'workforce-kanban');
  assert.equal(preferenceWrite.body.hawkAccents, 'subtle');
  assert.equal(preferenceWrite.body.programScope, 'program:00000000-0000-4000-8000-000000000001');

  const advance = root.querySelector('[data-assignment-id]');
  assert.equal(advance.dataset.assignmentStatus, 'Acknowledged');
  const transition = [...advance.listeners.get('click')][0]({ currentTarget: advance, target: advance });
  const modal = browser.document.querySelector('.modal');
  modal.querySelector('#reason').value = 'I reviewed the linked order and accepted the assignment.';
  const confirm = modal.querySelector('[data-ok]');
  await confirm.onclick({ currentTarget: confirm, target: confirm });
  await transition;
  const statusWrite = calls.find(call => call.path === '/api/work-inbox/assignments/00000000-0000-4000-8000-000000000042/status');
  assert.equal(statusWrite.body.status, 'Acknowledged');
  assert.equal(statusWrite.body.reason, 'I reviewed the linked order and accepted the assignment.');
  assert.match(root.innerHTML, /Acknowledged/);
});

test('a changed role or program scope gets a nonintrusive notice instead of a repeated first-login briefing', async t => {
  const browser = installBrowser(t);
  const data = {
    generatedAt: '2026-10-25T12:00:00Z', actor: { displayName: 'Avery Manager', roles: ['Supervisor'] },
    operationalContext: { siteName: 'WhiskerWorks', shiftName: 'Second Shift', timeZone: 'America/New_York', operationalDate: '2026-10-25', relation: 'Current', shiftStartsAt: '2026-10-25T14:00:00Z', shiftEndsAt: '2026-10-25T22:00:00Z' },
    sources: [], items: [], isPartial: false, warnings: [],
  };
  globalThis.fetch = path => Promise.resolve(response(path === '/api/daily-mission/session'
    ? { mission: data, briefing: { shouldShow: false, scopeChanged: true, viewedAt: '2026-10-25T10:00:00Z', dismissedAt: null } }
    : { landingRoute: 'daily-mission', hawkAccents: 'off', options: [{ route: 'daily-mission', label: 'Daily Mission' }] }));
  const root = browser.document.createElement('main'); browser.document.body.append(root);
  await dailyMissionPage(root, { me: { displayName: 'Avery Manager', roles: ['Supervisor'] } });
  assert.match(root.innerHTML, /mission now reflects your current authorized scope/);
  assert.doesNotMatch(root.innerHTML, /FIRST BRIEFING THIS OPERATIONAL DAY/);
  assert.equal(root.querySelector('[data-first-briefing]').hidden, true);
});

test('a stale mission stays visible but disables assignment progression until refresh', async t => {
  const browser = installBrowser(t);
  const data = {
    generatedAt: '2020-01-01T00:00:00Z', actor: { displayName: 'Avery Manager', roles: ['Supervisor'] },
    operationalContext: null, sources: [], warnings: [], isPartial: false,
    items: [{ id: 'assignment:00000000-0000-4000-8000-000000000043', group: 'Assigned Work', title: 'Review order 43', reason: 'Source workflow remains authoritative.', sourceType: 'ProductionOrder', sourceId: '43', deepLink: '#/orders/43', sourceLifecycle: 'WorkAssignment', sourceStatus: 'Assigned', sourceTimestamp: '2020-01-01T00:00:00Z', deadline: null }],
  };
  globalThis.fetch = path => Promise.resolve(response(path === '/api/daily-mission/session'
    ? { mission: data, briefing: { shouldShow: false, viewedAt: '2020-01-01T00:00:00Z', dismissedAt: null } }
    : { landingRoute: 'daily-mission', hawkAccents: 'off', options: [{ route: 'daily-mission', label: 'Daily Mission' }] }));
  const root = browser.document.createElement('main'); browser.document.body.append(root);
  await dailyMissionPage(root, { me: { displayName: 'Avery Manager', roles: ['Supervisor'] } });
  assert.match(root.innerHTML, /older than five minutes/);
  assert.equal(root.querySelector('[data-assignment-id]').disabled, true);
});

test('supervisors can request a fresh, read-only safe coverage preview for a workforce blocker', async t => {
  const browser = installBrowser(t), calls = [];
  const data = {
    generatedAt: new Date().toISOString(), actor: { displayName: 'Avery Manager', roles: ['Supervisor'] },
    operationalContext: { siteName: 'WhiskerWorks', shiftName: 'Second Shift', timeZone: 'America/New_York', operationalDate: '2026-10-25', relation: 'Current', shiftStartsAt: '2026-10-25T14:00:00Z', shiftEndsAt: '2026-10-25T22:00:00Z' },
    sources: [], warnings: [], isPartial: true,
    items: [{ id: 'workforce-readiness:42:20', group: 'Requires Attention', title: 'Staffing readiness · Order 42 · Slitting', reason: 'Order operation 20 is workforce blocked.', sourceType: 'ProductionOperationReadiness', sourceId: '42:20', deepLink: '#/orders/42', sourceLifecycle: 'WorkforceOrderReadinessProjection', sourceStatus: 'WorkforceBlocked', sourceTimestamp: new Date().toISOString(), deadline: '2026-10-26' }],
  };
  const preferences = { landingRoute: 'daily-mission', hawkAccents: 'subtle', programScope: 'all', programOptions: [{ route: 'all', label: 'All authorized programs' }], options: [{ route: 'daily-mission', label: 'Daily Mission' }] };
  globalThis.fetch = (path, request = {}) => {
    calls.push({ path, method: request.method ?? 'GET' });
    if (path === '/api/daily-mission/session') return Promise.resolve(response({ mission: data, briefing: { shouldShow: false } }));
    if (path === '/api/daily-mission/preferences') return Promise.resolve(response(preferences));
    if (path === '/api/daily-mission/recovery/42/20') return Promise.resolve(response({
      asOf: '2026-10-25T18:00:00Z', scheduledMinutes: 240, ruleVersion: 'skill-capacity-heuristic-v1',
      recovery: { asOf: '2026-10-25T18:00:00Z', output: { evaluatedCandidates: 4, limitations: ['No assignment is written.'], candidates: [{ rank: 1, personName: 'Casey Safe', requiresSourceBackfill: false, evidence: ['Casey has current required qualifications and clears all safety checks.'] }] } },
      candidates: [{ rank: 1, atTarget: { lastOperatedAt: '2026-10-20T18:00:00Z', forecast: { expectedGoodUnits: 20, conservativeGoodUnits: 16, optimisticGoodUnits: 24, confidence: 'High' } } }],
    }));
    return Promise.resolve(response({}));
  };
  const root = browser.document.createElement('main'); browser.document.body.append(root);

  await dailyMissionPage(root, { me: { displayName: 'Avery Manager', roles: ['Supervisor'], permissions: ['workforce.supervise'] } });
  const button = root.querySelector('[data-recovery-order]');
  assert.ok(button);
  assert.equal(button.dataset.recoveryOrder, '42');
  assert.equal(button.dataset.recoverySequence, '20');
  await [...button.listeners.get('click')][0]({ currentTarget: button, target: button });

  assert.ok(calls.some(call => call.path === '/api/daily-mission/recovery/42/20'));
  const panel = root.querySelector('[data-recovery-panel="42:20"]');
  assert.equal(panel.hidden, false);
  assert.match(panel.innerHTML, /Read-only coverage preview/);
  assert.match(panel.innerHTML, /Casey Safe/);
  assert.match(panel.innerHTML, /20 good units/);
  assert.match(panel.innerHTML, /No staffing change was made/);
});
