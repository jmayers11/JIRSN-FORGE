import assert from 'node:assert/strict';
import test from 'node:test';
import { appendOwnAttendanceCorrections, appendWorkforceIntelligence } from '../../src/Forge.Host/wwwroot/js/pages/workforce-intelligence.js';
import { workforcePage } from '../../src/Forge.Host/wwwroot/js/pages/workforce.js';
import { installBrowser, response, fire, flush } from './browser-harness.mjs';

test('Workforce Command Center uses recorded sources, exposes honest gaps, and keeps absence reasons private', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div');
  browser.document.body.append(page);
  const attendanceCalls = [];
  let receiptPreviewRequest;
  const worker = { personId: 'worker-1', displayName: 'Ari Operator', homeWorkCenterCode: 'WC-A', isActive: true, shiftCode: 'DAY', jobTitle: 'Machine Operator', department: 'Production', unavailability: null };
  globalThis.fetch = (path, options = {}) => {
    if (path === '/api/workforce/workers') return Promise.resolve(response([worker]));
    if (path === '/api/resources/') return Promise.resolve(response({
      sites: [{ code: 'SITE-A', name: 'Plant A' }], safetyAreas: [{ code: 'AREA-A', siteCode: 'SITE-A', name: 'Winding Area' }],
      shifts: [{ code: 'DAY', siteCode: 'SITE-A', name: 'Day Shift' }, { code: 'NIGHT', siteCode: 'SITE-A', name: 'Night Shift' }],
      workCenters: [{ code: 'WC-A', name: 'Winding', safetyAreaCode: 'AREA-A' }],
      machines: [{ code: 'WD-01', workCenterCode: 'WC-A', name: 'Winder 1' }],
      machineActivities: [{ machineCode: 'WD-01', activityCode: 'WIND', operatorQualifications: ['WIND'] }],
      workCenterShifts: [{ workCenterCode: 'WC-A', shiftCode: 'DAY' }],
    }));
    if (path === '/api/competency/qualification-types') return Promise.resolve(response([{ code: 'WIND', name: 'Winding' }]));
    if (path === '/api/planning/schedules/approved') return Promise.resolve(response({ result: { operations: [] } }));
    if (path.startsWith('/api/competency/people/qualifications?')) return Promise.resolve(response([
      { personId: 'worker-1', code: 'WIND', status: 'Qualified', expiresAt: '2026-10-25T23:59:00Z' },
    ]));
    if (path.startsWith('/api/workforce/unavailability?')) return Promise.resolve(response([
      { personId: 'worker-1', kind: 'ApprovedPto', startsAt: '2026-10-25T00:00:00Z', endsAt: '2026-10-26T00:00:00Z' },
    ]));
    if (path.startsWith('/api/workforce/attendance-opportunities?')) return Promise.resolve(response([
      { opportunityId: 'shift-w1', personId: 'worker-1', shiftCode: 'DAY', scheduledStart: '2026-10-25T10:00:00Z', scheduledEnd: '2026-10-25T18:00:00Z', state: 'Scheduled' },
    ]));
    if (path === '/api/readiness/operations') return Promise.resolve(response([
      { orderNumber: 77, sequence: 10, dueDate: '2026-10-27', workCenter: 'WC-A', executable: false, primaryConstraint: 'Workforce', why: 'No qualified operator.' },
    ]));
    if (path === '/api/inventory/tasks?open=true') return Promise.resolve(response([
      { taskId: 55, orderNumber: 77, operationSequence: 10, partNumber: 'COMP-7', quantity: 4, toLocation: 'WC-A-STAGE', status: 'InTransit', kind: 'Stage' },
    ]));
    if (path === '/api/readiness/material-receipt-impact-preview') {
      receiptPreviewRequest = JSON.parse(options.body);
      return Promise.resolve(response({ taskId: 55, orderNumber: 77, baselineMaterialState: 'Shortfall', proposedMaterialState: 'Ready',
        baselineMaterialSummary: 'Short COMP-7', proposedMaterialSummary: 'Ready', assumption: 'Read-only hypothetical receipt.',
        targetOrder: { orderNumber: 77, dueDate: '2026-10-27', beforeStatus: 'Unschedulable', proposedStatus: 'OnTime', beforeCompletion: null,
          proposedCompletion: '2026-10-26T18:00:00Z', completionShiftHours: null }, otherOrderImpacts: [
          { orderNumber: 78, dueDate: '2026-10-28', beforeStatus: 'OnTime', proposedStatus: 'AtRisk', beforeCompletion: '2026-10-28T12:00:00Z', proposedCompletion: '2026-10-28T14:00:00Z', completionShiftHours: 2 },
        ] }));
    }
    if (path.startsWith('/api/shopfloor/activity?')) return Promise.resolve(response({ labor: [], reports: [], attendance: [] }));
    if (path.startsWith('/api/workforce/attendance-intelligence?')) {
      attendanceCalls.push(path);
      return Promise.resolve(response({ onTimeArrivalPercent: 96.36, eligibleArrivalOpportunities: 55, scheduledOpportunities: 60,
        onTimeArrivals: 53, verifiedLateArrivals: 2, verifiedCallOuts: 1, verifiedNoShows: 0, excludedOpportunities: 5,
        pendingReviewOpportunities: 0, missingPolicyOpportunities: 0, explanation: '53 of 55 eligible arrivals; 5 excluded.' }));
    }
    return Promise.resolve(response([]));
  };

  page.innerHTML = '<p>existing workforce content</p>';
  await appendWorkforceIntelligence(page, { permissions: ['inventory.read', 'planning.schedule.read'] });
  assert.match(page.innerHTML, /Training &amp; Performance Command Center/);
  assert.match(page.innerHTML, /Upcoming training windows · next 14 days/);
  assert.match(page.innerHTML, /No scheduled learner-and-qualification gap pairs/);
  assert.match(page.innerHTML, /Current qualifications[\s\S]*?1/);
  assert.match(page.innerHTML, /expires in 2 days/);
  assert.match(page.innerHTML, /WO 77 · operation 10 is blocked by Workforce/);
  assert.match(page.innerHTML, /WD-01 · WIND has no qualified coverage/);
  assert.match(page.innerHTML, /private absence details are hidden/);
  assert.match(page.innerHTML, /order due 2026-10-27/);
  assert.match(page.innerHTML, /Floor receiver: independently acknowledge the delivered quantity/);
  assert.match(page.innerHTML, /operation also has a Workforce blocker/);
  const receiptPreviewButton = page.querySelector('[data-wi-material-recovery="55"]');
  assert.ok(receiptPreviewButton);
  assert.match(page.innerHTML, /Attendance reliability: On-time arrival: 96.36%/);
  assert.match(page.innerHTML, /Eligible arrivals:[\s\S]*?55/);
  assert.match(page.innerHTML, /Excluded:[\s\S]*?5/);
  assert.match(page.innerHTML, /Calendar quarter to date/);
  assert.match(page.innerHTML, /data-wi-filter="site"/);
  assert.match(page.innerHTML, /data-wi-filter="machine"/);
  assert.match(page.innerHTML, /data-wi-filter="department"/);
  assert.match(page.innerHTML, /data-wi-filter="area"/);
  assert.match(page.innerHTML, /data-wi-filter="role"/);
  assert.match(page.innerHTML, /data-wi-filter="qualification"/);
  assert.match(page.innerHTML, /Departments are maintained on employee profiles/);
  assert.match(page.innerHTML, /existing workforce content/);
  assert.doesNotMatch(page.innerHTML, /ApprovedPto|protected leave|Medical/i);

  const shift = page.querySelector('[data-wi-filter="shift"]');
  shift.value = 'NIGHT';
  fire(shift, 'change');
  assert.equal(page.querySelector('[data-wi-filter="shift"]').value, 'NIGHT');

  page.querySelector('[data-wi-filter="shift"]').value = '';
  fire(page.querySelector('[data-wi-filter="shift"]'), 'change');
  const role = page.querySelector('[data-wi-filter="role"]');
  role.value = 'Machine Operator'; fire(role, 'change');
  assert.equal(page.querySelector('[data-wi-filter="role"]').value, 'Machine Operator');
  assert.match(page.innerHTML, /Ari Operator/);

  const period = page.querySelector('[data-wi-filter="period"]');
  period.value = 'year'; fire(period, 'change'); await flush(); await flush();
  assert.equal(page.querySelector('[data-wi-filter="period"]').value, 'year');
  assert.equal(attendanceCalls.length, 2, 'initial quarter and selected year are computed by the canonical attendance service');
  const yearRange = new URL(attendanceCalls[1], 'http://forge.local');
  assert.ok(new Date(yearRange.searchParams.get('to')).getTime() - new Date(yearRange.searchParams.get('from')).getTime() > 250 * 86_400_000);
  const currentReceiptPreviewButton = page.querySelector('[data-wi-material-recovery="55"]');
  fire(currentReceiptPreviewButton, 'click'); await flush(); await flush();
  assert.deepEqual(receiptPreviewRequest, { taskId: 55 });
  const receiptImpact = page.querySelector('[data-wi-material-recovery-result="55"]').innerHTML;
  assert.match(receiptImpact, /WO 77/);
  assert.match(receiptImpact, /Unschedulable → OnTime/);
  assert.match(receiptImpact, /Other visible orders affected/);
  assert.match(receiptImpact, /WO 78/);
  assert.match(receiptImpact, /Read-only hypothetical receipt/);
});

test('supervisors can request a read-only ranked recovery preview for a machine capability gap', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div');
  browser.document.body.append(page);
  let recoveryRequest;
  globalThis.fetch = (path, options = {}) => {
    if (path === '/api/workforce/workers') return Promise.resolve(response([
      { personId: 'worker-1', displayName: 'Ari Operator', homeWorkCenterCode: 'WC-A', isActive: true, shiftCode: 'DAY', unavailability: null },
    ]));
    if (path === '/api/resources/') return Promise.resolve(response({
      shifts: [{ code: 'DAY', name: 'Day Shift' }],
      workCenters: [{ code: 'WC-A', name: 'Winding' }],
      machines: [{ code: 'WD-01', workCenterCode: 'WC-A', safetyAreaCode: 'AREA-A' }],
      machineActivities: [{ machineCode: 'WD-01', activityCode: 'WIND', operatorQualifications: ['WIND'], requiresMachineAuthorization: true }],
      workCenterShifts: [{ workCenterCode: 'WC-A', shiftCode: 'DAY' }],
    }));
    if (path === '/api/competency/qualification-types') return Promise.resolve(response([{ code: 'WIND', name: 'Winding' }]));
    if (path.startsWith('/api/competency/people/qualifications?')) return Promise.resolve(response([]));
    if (path.startsWith('/api/competency/machine-authorizations?')) return Promise.resolve(response([]));
    if (path === '/api/readiness/workforce-recovery-preview') {
      recoveryRequest = JSON.parse(options.body);
      const projectedShift = Boolean(recoveryRequest.shiftDate);
      return Promise.resolve(response({ output: {
        evaluatedCandidates: 8,
        projectedShift,
        currentConstraint: 'Workforce',
        candidates: [{ rank: 1, personId: 'worker-2', personName: 'Morgan Trainer', requiresSourceBackfill: true,
          moves: [
            { personId: 'worker-3', personName: 'Taylor', assignment: { machineCode: 'WD-02', workCenterCode: 'WC-B', safetyAreaCode: 'AREA-B', activityCode: 'WIND' } },
            { personId: 'worker-2', personName: 'Morgan Trainer', assignment: { machineCode: 'WD-01', workCenterCode: 'WC-A', safetyAreaCode: 'AREA-A', activityCode: 'WIND' } },
          ],
          sourceImpacts: [{ detail: 'WC-B would be unstaffed unless Taylor is moved first.' }],
          evidence: ['Taylor covers the source before Morgan moves.'],
        }],
        limitations: projectedShift
          ? ['Candidate presence is projected from the selected shift calendar; it is not proof of clock-in.', 'Future source dispatch and workload are not forecasted.']
          : ['Material and due-date effects are not evaluated.'],
      } }));
    }
    return Promise.resolve(response([]));
  };

  await appendWorkforceIntelligence(page, { permissions: ['planning.readiness.read', 'workforce.supervise'] });
  const button = page.querySelector('[data-wi-recovery]');
  assert.ok(button);
  assert.equal(button.dataset.safetyArea, 'AREA-A');
  fire(button, 'click'); await flush(); await flush();

  assert.deepEqual(recoveryRequest, { safetyAreaCode: 'AREA-A', workCenterCode: 'WC-A', machineCode: 'WD-01', activityCode: 'WIND' });
  const result = page.querySelector('[data-wi-recovery-result]').innerHTML;
  assert.match(result, /#1 · Morgan Trainer/);
  assert.match(result, /Two-step plan/);
  assert.match(result, /WC-B would be unstaffed unless Taylor is moved first/);
  assert.match(result, /Material and due-date effects are not evaluated/);
  assert.equal(button.disabled, false);

  const scheduledDate = page.querySelector('[data-wi-recovery-date]');
  scheduledDate.value = '2026-10-27';
  const scheduledShift = page.querySelector('[data-wi-recovery-shift]');
  scheduledShift.value = 'DAY';
  fire(page.querySelector('[data-projected="true"]'), 'click'); await flush(); await flush();
  assert.deepEqual(recoveryRequest, {
    safetyAreaCode: 'AREA-A', workCenterCode: 'WC-A', machineCode: 'WD-01', activityCode: 'WIND',
    shiftDate: '2026-10-27', shiftCode: 'DAY',
  });
  const scheduledResult = page.querySelector('[data-wi-recovery-result]').innerHTML;
  assert.match(scheduledResult, /Scheduled candidate/);
  assert.match(scheduledResult, /Currently open source-work impact: WC-B would be unstaffed unless Taylor is moved first/);
  assert.match(scheduledResult, /Future source dispatch and workload remain unknown/);
  assert.match(scheduledResult, /not proof of clock-in/);
});

test('employee profile links personal labor intervals to shared operation targets and downtime context', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div');
  browser.document.body.append(page);
  const now = Date.now();
  const worker = { personId: 'worker-profile', displayName: 'Ari Operator', homeWorkCenterCode: 'WC-A', isActive: true, shiftCode: 'DAY' };
  const startedAt = new Date(now - 2 * 60 * 60 * 1000).toISOString();
  const endedAt = new Date(now - 60 * 60 * 1000).toISOString();
  globalThis.fetch = path => {
    if (path === '/api/workforce/workers') return Promise.resolve(response([worker]));
    if (path === '/api/resources/') return Promise.resolve(response({ shifts: [], workCenters: [], machines: [], machineActivities: [], workCenterShifts: [] }));
    if (path.startsWith('/api/shopfloor/activity?')) return Promise.resolve(response({ labor: [
      { personId: worker.personId, orderNumber: 77, sequence: 10, role: 'Operator', phase: 'Setup', workCenterCode: 'WC-A', machineCode: 'M-1', startedAt, endedAt },
    ], reports: [], attendance: [] }));
    if (path === '/api/orders/77/shopfloor') return Promise.resolve(response({
      labor: [], reports: [], training: [],
      laborMetrics: [{ sequence: 10, workCenterCode: 'WC-A', plannedTotalLaborHours: 4.5, recordedOperatorHours: 4,
        crewSize: 2, reportedGoodQuantity: 6, reportedScrapQuantity: 1, recordedStoppageMinutes: 30,
        stoppageContext: 'Equipment · spindle vibration (30 min). Reported categories are context, not confirmed root causes.',
        earnedStandardStatus: 'N/A: accepted output has not been verified.' }],
    }));
    return Promise.resolve(response([]));
  };

  await appendWorkforceIntelligence(page, { permissions: ['execution.shopfloor.read', 'execution.orders.read'] }, worker.personId);
  assert.match(page.innerHTML, /Ari Operator · operation history/);
  assert.match(page.innerHTML, /WO 77 · op 10/);
  assert.match(page.innerHTML, /Setup 1 h/);
  assert.match(page.innerHTML, /4.5 h target/);
  assert.match(page.innerHTML, /4 h crew actual · crew 2/);
  assert.match(page.innerHTML, /6 good \/ 1 scrap/);
  assert.match(page.innerHTML, /spindle vibration/);
  assert.match(page.innerHTML, /not an individual score/);
  assert.match(page.innerHTML, /not confirmed root causes/);
});

test('employee operation history respects separate shop-floor and order read permissions', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div');
  browser.document.body.append(page);
  let orderDetailCalls = 0;
  const worker = { personId: 'worker-profile', displayName: 'Ari Operator', homeWorkCenterCode: 'WC-A', isActive: true };
  globalThis.fetch = path => {
    if (path === '/api/workforce/workers') return Promise.resolve(response([worker]));
    if (path === '/api/resources/') return Promise.resolve(response({ shifts: [], workCenters: [], machines: [], machineActivities: [], workCenterShifts: [] }));
    if (path.startsWith('/api/shopfloor/activity?')) return Promise.resolve(response({ labor: [
      { personId: worker.personId, orderNumber: 77, sequence: 10, role: 'Operator', phase: 'Run', workCenterCode: 'WC-A', startedAt: new Date().toISOString() },
    ], reports: [], attendance: [] }));
    if (path === '/api/orders/77/shopfloor') orderDetailCalls++;
    return Promise.resolve(response([]));
  };

  await appendWorkforceIntelligence(page, { permissions: ['execution.shopfloor.read'] }, worker.personId);
  assert.match(page.innerHTML, /Shop-floor and work-order read permissions are required/);
  assert.equal(orderDetailCalls, 0);
});

test('employee attendance view is self-scoped and correction requests remain pending for independent review', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div');
  browser.document.body.append(page);
  const eventId = 'arrival-event-1', correctionId = 'arrival-correction-1';
  const original = { eventId, opportunityId: 'shift-1', kind: 'Arrival', observedAt: '2026-10-22T12:12:00Z', reviewState: 'Verified', source: 'Hris', recordedAt: '2026-10-22T12:13:00Z', supersedesEventId: null };
  let eventReads = 0, correctionRequest;
  globalThis.fetch = async (path, options = {}) => {
    if (path.startsWith('/api/workforce/my-attendance-events?')) {
      eventReads++;
      return response(eventReads === 1 ? [original] : [
        { ...original, reviewState: 'Superseded' },
        { ...original, eventId: correctionId, observedAt: '2026-10-22T12:02:00Z', reviewState: 'PendingReview', source: 'Manual', recordedAt: new Date().toISOString(), supersedesEventId: eventId },
      ]);
    }
    if (path === `/api/workforce/my-attendance-events/${eventId}/correction`) {
      correctionRequest = JSON.parse(options.body);
      return response({ succeeded: true, entityId: correctionId, message: 'Correction request submitted for independent review.' });
    }
    return response([]);
  };

  page.innerHTML = '<p>employee profile</p>';
  await appendOwnAttendanceCorrections(page, { permissions: ['workforce.attendance-correction.request'] });
  assert.match(page.innerHTML, /My attendance records/);
  assert.match(page.innerHTML, /arrival-event-1/);
  assert.match(page.innerHTML, /Request correction/);
  assert.doesNotMatch(page.innerHTML, /other employee|sourceRecordId|reasonCode/);

  const form = page.querySelector('[data-own-attendance-correction="arrival-event-1"]');
  form.querySelector('[name="kind"]').value = 'Arrival';
  form.querySelector('[name="observedAt"]').value = '2026-10-22T08:02';
  form.querySelector('[name="reason"]').value = 'Time clock shows the employee arrived at 8:02';
  fire(form, 'submit', { bubbles: true });
  await flush(); await flush();

  assert.deepEqual(correctionRequest, {
    kind: 'Arrival', observedAt: new Date('2026-10-22T08:02').toISOString(), reasonCode: 'employee-correction-request',
    reason: 'Time clock shows the employee arrived at 8:02',
  });
  assert.equal(eventReads, 2);
  assert.match(page.textContent, /awaiting independent review/);
  assert.equal(page.querySelector('[data-own-attendance-correction="arrival-correction-1"]'), null);
});

test('employee-only Workforce route avoids loading the manager command center', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div');
  browser.document.body.append(page);
  const calls = [];
  globalThis.fetch = async path => {
    calls.push(path);
    if (path.startsWith('/api/workforce/my-attendance-events?')) return response([]);
    return response([]);
  };

  await workforcePage(page, { me: { permissions: ['workforce.attendance-correction.request'] }, query: { get: () => null } });
  assert.match(page.innerHTML, /My Workforce/);
  assert.match(page.innerHTML, /My attendance records/);
  assert.equal(calls.length, 1);
  assert.match(calls[0], /^\/api\/workforce\/my-attendance-events\?/);
});
