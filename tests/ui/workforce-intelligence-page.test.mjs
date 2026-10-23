import assert from 'node:assert/strict';
import test from 'node:test';
import { appendWorkforceIntelligence } from '../../src/Forge.Host/wwwroot/js/pages/workforce-intelligence.js';
import { installBrowser, response, fire, flush } from './browser-harness.mjs';

test('Workforce Command Center uses recorded sources, exposes honest gaps, and keeps absence reasons private', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div');
  browser.document.body.append(page);
  const attendanceCalls = [];
  let receiptPreviewRequest;
  const worker = { personId: 'worker-1', displayName: 'Ari Operator', homeWorkCenterCode: 'WC-A', isActive: true, shiftCode: 'DAY', unavailability: null };
  globalThis.fetch = (path, options = {}) => {
    if (path === '/api/workforce/workers') return Promise.resolve(response([worker]));
    if (path === '/api/resources/') return Promise.resolve(response({
      shifts: [{ code: 'DAY', name: 'Day Shift' }, { code: 'NIGHT', name: 'Night Shift' }],
      workCenters: [{ code: 'WC-A', name: 'Winding' }],
      machines: [{ code: 'WD-01', workCenterCode: 'WC-A' }],
      machineActivities: [{ machineCode: 'WD-01', activityCode: 'WIND', operatorQualifications: ['WIND'] }],
      workCenterShifts: [{ workCenterCode: 'WC-A', shiftCode: 'DAY' }],
    }));
    if (path === '/api/competency/qualification-types') return Promise.resolve(response([{ code: 'WIND', name: 'Winding' }]));
    if (path.startsWith('/api/competency/people/qualifications?')) return Promise.resolve(response([
      { personId: 'worker-1', code: 'WIND', status: 'Qualified', expiresAt: '2026-10-25T12:00:00Z' },
    ]));
    if (path.startsWith('/api/workforce/unavailability?')) return Promise.resolve(response([
      { personId: 'worker-1', kind: 'ApprovedPto', startsAt: '2026-10-25T00:00:00Z', endsAt: '2026-10-26T00:00:00Z' },
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
  assert.match(page.innerHTML, /Current qualifications[\s\S]*?1/);
  assert.match(page.innerHTML, /expires in 2 days/);
  assert.match(page.innerHTML, /WO 77 · operation 10 is blocked by Workforce/);
  assert.match(page.innerHTML, /order due 2026-10-27/);
  assert.match(page.innerHTML, /Floor receiver: independently acknowledge the delivered quantity/);
  assert.match(page.innerHTML, /operation also has a Workforce blocker/);
  const receiptPreviewButton = page.querySelector('[data-wi-material-recovery="55"]');
  assert.ok(receiptPreviewButton);
  assert.match(page.innerHTML, /Attendance reliability: On-time arrival: 96.36%/);
  assert.match(page.innerHTML, /Eligible arrivals:[\s\S]*?55/);
  assert.match(page.innerHTML, /Excluded:[\s\S]*?5/);
  assert.match(page.innerHTML, /Calendar quarter to date/);
  assert.match(page.innerHTML, /existing workforce content/);
  assert.doesNotMatch(page.innerHTML, /ApprovedPto|protected leave|Medical/i);

  const shift = page.querySelector('[data-wi-filter="shift"]');
  shift.value = 'NIGHT';
  fire(shift, 'change');
  assert.equal(page.querySelector('[data-wi-filter="shift"]').value, 'NIGHT');

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
          ? ['Candidate presence is projected from the selected shift calendar; it is not proof of clock-in.', 'Future source-center production coverage is not evaluated.']
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
  assert.match(scheduledResult, /Future source-center production coverage is not forecasted/);
  assert.match(scheduledResult, /not proof of clock-in/);
});
