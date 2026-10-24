import assert from 'node:assert/strict';
import test from 'node:test';
import { buildWorkforceIntelligence, filterWorkforceIntelligenceInputs, renderEvidenceReference } from '../../src/Forge.Host/wwwroot/js/workforce-intelligence.mjs';

const now = new Date('2026-10-23T12:00:00Z');
const workers = [
  { personId: 'w-1', displayName: 'Ari Operator', homeWorkCenterCode: 'WC-A', isActive: true, shiftCode: 'DAY', unavailability: null },
  { personId: 'w-2', displayName: 'Bo Operator', homeWorkCenterCode: 'WC-B', isActive: true, shiftCode: 'DAY', unavailability: null },
];
const qualifications = [
  { personId: 'w-1', code: 'WIND', status: 'Qualified', expiresAt: '2026-11-12T12:00:00Z' },
  { personId: 'w-2', code: 'SLIT', status: 'Qualified', expiresAt: '2026-10-22T12:00:00Z' },
  { personId: 'w-2', code: 'CNC', status: 'InTraining', expiresAt: null },
];
const resources = {
  shifts: [{ code: 'DAY', name: 'Day Shift' }, { code: 'NIGHT', name: 'Night Shift' }],
  workCenters: [{ code: 'WC-A', name: 'Winding' }, { code: 'WC-B', name: 'Slitting' }],
  machines: [{ code: 'WD-01', workCenterCode: 'WC-A' }, { code: 'SL-01', workCenterCode: 'WC-B' }],
  machineActivities: [
    { machineCode: 'WD-01', activityCode: 'WIND', operatorQualifications: ['WIND', 'WIND-II'] },
    { machineCode: 'SL-01', activityCode: 'SLIT', operatorQualifications: ['SLIT'] },
  ],
  workCenterShifts: [{ workCenterCode: 'WC-A', shiftCode: 'DAY' }, { workCenterCode: 'WC-B', shiftCode: 'DAY' }],
};

test('qualification summary honors expiry windows and never counts in-training as current authorization', () => {
  const summary = buildWorkforceIntelligence({ now, workers, qualifications, qualificationTypes: [{ code: 'WIND' }], resources, expiryDays: 30 });
  assert.equal(summary.qualificationCounts.current, 1);
  assert.equal(summary.qualificationCounts.expired, 1);
  assert.equal(summary.qualificationCounts.inTraining, 1);
  assert.equal(summary.expiring.length, 1);
  assert.equal(summary.expiring[0].workerName, 'Ari Operator');
  assert.equal(summary.capabilityGaps.length, 1);
  assert.equal(summary.capabilityGaps[0].machineCode, 'SL-01');
});

test('coverage is limited to selected configured shift and work center and supports alternative qualifications', () => {
  const qualificationsWithAlternate = [...qualifications, { personId: 'w-1', code: 'WIND-II', status: 'Qualified', expiresAt: null }];
  const all = buildWorkforceIntelligence({ now, workers, qualifications: qualificationsWithAlternate, resources });
  assert.equal(all.coverage.find(row => row.machineCode === 'WD-01').qualifiedWorkerCount, 1);
  const night = buildWorkforceIntelligence({ now, workers, qualifications: qualificationsWithAlternate, resources, shiftCode: 'NIGHT' });
  assert.equal(night.coverage, null);
  const slitting = buildWorkforceIntelligence({ now, workers, qualifications: qualificationsWithAlternate, resources, workCenterCode: 'WC-B' });
  assert.equal(slitting.capabilityGaps.length, 1);
});

test('machine-specific authorization is separate from qualification and gates coverage and practical-assessment recommendations', () => {
  const gatedResources = { ...resources, machineActivities: resources.machineActivities.map(profile => ({ ...profile, requiresMachineAuthorization: true })) };
  const qualificationsWithAlternate = [...qualifications, { personId: 'w-1', code: 'WIND-II', status: 'Qualified', expiresAt: null }];
  const withoutAuthorization = buildWorkforceIntelligence({ now, workers, qualifications: qualificationsWithAlternate, resources: gatedResources, machineAuthorizations: [] });
  assert.equal(withoutAuthorization.coverage.find(row => row.machineCode === 'WD-01').qualifiedWorkerCount, 0);
  assert.ok(withoutAuthorization.trainingRecommendations.some(row => row.personId === 'w-1' && row.machineCode === 'WD-01'));
  const withAuthorization = buildWorkforceIntelligence({ now, workers, qualifications: qualificationsWithAlternate, resources: gatedResources, machineAuthorizations: [
    { personId: 'w-1', machineCode: 'WD-01', activityCode: 'WIND', qualificationCode: 'WIND-II', status: 'Authorized', independentOperationAllowed: true, expiresAt: null },
  ] });
  assert.equal(withAuthorization.coverage.find(row => row.machineCode === 'WD-01').qualifiedWorkerCount, 1);
  assert.equal(withAuthorization.capabilityGaps.some(row => row.machineCode === 'WD-01'), false);
  const unknown = buildWorkforceIntelligence({ now, workers, qualifications: qualificationsWithAlternate, resources: gatedResources });
  assert.equal(unknown.coverage, null, 'authorization data unavailable is not reported as zero or as a passing count');
});

test('evidence links accept only recognized Forge source-reference formats', () => {
  assert.match(renderEvidenceReference('OrderOperation/1042/10'), /href="#\/orders\?search=1042"/);
  assert.match(renderEvidenceReference('Ncr/NCR-24'), /href="#\/quality"/);
  assert.match(renderEvidenceReference('MaterialTask/44'), /href="#\/inventory\?tab=tasks"/);
  assert.match(renderEvidenceReference('SuspectScrapReport/9'), /href="#\/quality\?tab=suspect"/);
  assert.doesNotMatch(renderEvidenceReference('javascript:alert(1)'), /<a /);
  assert.match(renderEvidenceReference('<script>'), /&lt;script&gt;/);
});

test('command-center filters scope workers, qualifications, machine coverage, and review evidence by recorded dimensions', () => {
  const filtered = filterWorkforceIntelligenceInputs({
    workers: [
      { personId: 'a', displayName: 'Ari', homeWorkCenterCode: 'WC-A', isActive: true, shiftCode: 'DAY', jobTitle: 'Operator', department: 'Production' },
      { personId: 'b', displayName: 'Bo', homeWorkCenterCode: 'WC-B', isActive: true, shiftCode: 'DAY', jobTitle: 'Inspector', department: 'Quality' },
    ],
    qualifications: [
      { personId: 'a', code: 'WIND', status: 'Qualified' },
      { personId: 'a', code: 'FORK', status: 'InTraining' },
      { personId: 'b', code: 'WIND', status: 'Qualified' },
    ],
    machineAuthorizations: [
      { personId: 'a', machineCode: 'WD-01' }, { personId: 'b', machineCode: 'SL-01' },
    ],
    qualificationTypes: [{ code: 'WIND' }, { code: 'FORK' }],
    resources: {
      sites: [{ code: 'SITE-A', name: 'Plant A' }, { code: 'SITE-B', name: 'Plant B' }],
      safetyAreas: [{ code: 'AREA-A', siteCode: 'SITE-A' }, { code: 'AREA-B', siteCode: 'SITE-B' }],
      workCenters: [{ code: 'WC-A', safetyAreaCode: 'AREA-A' }, { code: 'WC-B', safetyAreaCode: 'AREA-B' }],
      machines: [{ code: 'WD-01', workCenterCode: 'WC-A' }, { code: 'SL-01', workCenterCode: 'WC-B' }],
      machineActivities: [{ machineCode: 'WD-01', operatorQualifications: ['WIND'] }, { machineCode: 'SL-01', operatorQualifications: ['SLIT'] }],
      shifts: [{ code: 'DAY' }, { code: 'NIGHT' }],
      workCenterShifts: [{ workCenterCode: 'WC-A', shiftCode: 'DAY' }, { workCenterCode: 'WC-B', shiftCode: 'DAY' }],
    },
    attendanceEvents: [
      { eventId: 'pending-a', personId: 'a', reviewState: 'PendingReview' },
      { eventId: 'verified-a', personId: 'a', reviewState: 'Verified' },
      { eventId: 'pending-b', personId: 'b', reviewState: 'PendingReview' },
    ],
    attendanceOpportunities: [{ personId: 'a' }, { personId: 'b' }],
    readiness: [{ workCenter: 'WC-A', machineCode: 'WD-01' }, { workCenter: 'WC-B', machineCode: 'SL-01' }],
  }, {
    siteCode: 'SITE-A', areaCode: 'AREA-A', shiftCode: 'DAY', workCenterCode: '', machineCode: 'WD-01',
    roleTitle: 'Operator', department: 'Production', qualificationCode: 'WIND', workerStatus: 'active', reviewState: 'PendingReview',
  });

  assert.deepEqual(filtered.workers.map(row => row.personId), ['a']);
  assert.deepEqual(filtered.qualifications.map(row => row.code), ['WIND', 'FORK'], 'all evidence remains available to the authorization calculation');
  assert.deepEqual(filtered.qualificationRecords.map(row => row.code), ['WIND'], 'the selected training type scopes the evidence matrix');
  assert.deepEqual(filtered.machineAuthorizations.map(row => row.machineCode), ['WD-01']);
  assert.deepEqual(filtered.resources.machineActivities.map(row => row.machineCode), ['WD-01']);
  assert.deepEqual(filtered.attendanceEvents.map(row => row.eventId), ['pending-a']);
  assert.equal(filtered.attendanceOpportunities.length, 1);
  assert.equal(filtered.readiness.length, 1);
  const summary = buildWorkforceIntelligence({ ...filtered, now });
  assert.equal(summary.capabilityGaps.length, 0, 'selecting a training type does not discard other qualifications from machine authorization checks');
  assert.equal(summary.qualificationCounts.current, 1);

  const unavailable = filterWorkforceIntelligenceInputs({ workers: undefined, qualifications: undefined, resources: undefined });
  assert.equal(unavailable.workers, undefined);
  assert.equal(unavailable.resources, undefined);
  assert.equal(buildWorkforceIntelligence({ ...unavailable, now }).coverage, null, 'unreadable sources stay unknown through the filter layer');
});

test('upcoming training forecast pairs scheduled learners with matching trainers and shows qualified coverage separately', () => {
  const start = new Date(now.getTime() + 2 * 86_400_000);
  const end = new Date(start.getTime() + 8 * 60 * 60_000);
  const forecastInput = {
    now,
    workers: [
      { personId: 'learner', displayName: 'Taylor Learner', homeWorkCenterCode: 'WC-S', isActive: true, shiftCode: 'DAY' },
      { personId: 'trainer', displayName: 'Morgan Trainer', homeWorkCenterCode: 'WC-S', isActive: true, shiftCode: 'DAY' },
    ],
    qualifications: [
      { personId: 'learner', code: 'SLIT', status: 'InTraining' },
      { personId: 'trainer', code: 'SLIT', status: 'Qualified', expiresAt: null },
      { personId: 'trainer', code: 'SLIT-TRAINER', status: 'Qualified', expiresAt: null },
    ],
    qualificationTypes: [
      { code: 'SLIT', kind: 'Machine', prerequisites: [] },
      { code: 'SLIT-TRAINER', kind: 'Trainer', prerequisites: ['SLIT'] },
    ],
    machineAuthorizations: [],
    attendanceOpportunities: [
      { opportunityId: 'learner-shift', personId: 'learner', shiftCode: 'DAY', scheduledStart: start, scheduledEnd: end, state: 'Scheduled' },
      { opportunityId: 'trainer-shift', personId: 'trainer', shiftCode: 'DAY', scheduledStart: start, scheduledEnd: end, state: 'Scheduled' },
    ],
    unavailability: [],
    resources: {
      sites: [{ code: 'SITE', name: 'Plant' }], safetyAreas: [{ code: 'AREA', siteCode: 'SITE' }],
      workCenters: [{ code: 'WC-S', safetyAreaCode: 'AREA' }], machines: [{ code: 'SL-01', workCenterCode: 'WC-S' }],
      machineActivities: [{ machineCode: 'SL-01', activityCode: 'SLIT', operatorQualifications: ['SLIT'] }],
      shifts: [{ code: 'DAY', siteCode: 'SITE', name: 'Day' }], workCenterShifts: [{ workCenterCode: 'WC-S', shiftCode: 'DAY' }],
    },
    approvedSchedule: { operations: [] },
  };
  const summary = buildWorkforceIntelligence(forecastInput);
  assert.equal(summary.trainingForecast.length, 1);
  assert.equal(summary.trainingForecast[0].traineeCount, 1);
  assert.equal(summary.trainingForecast[0].trainerCount, 1);
  assert.equal(summary.trainingForecast[0].qualifiedCount, 1, 'the trainer also remains visible in independent qualified coverage');
  assert.equal(summary.trainingForecast[0].largestSharedFreeMinutes, 480);
  assert.equal(summary.trainingForecast[0].canReviewWindow, true);
  assert.match(summary.trainingForecast[0].detail, /Review course duration, production demand, crew coverage/);

  const withoutTrainer = buildWorkforceIntelligence({ ...forecastInput, qualifications: forecastInput.qualifications.filter(row => row.personId !== 'trainer' || row.code !== 'SLIT-TRAINER') });
  assert.equal(withoutTrainer.trainingForecast[0].trainerCount, 0);
  assert.match(withoutTrainer.trainingForecast[0].detail, /no qualified trainer is scheduled/);

  const unknown = buildWorkforceIntelligence({ ...forecastInput, attendanceOpportunities: undefined });
  assert.equal(unknown.trainingForecast, null);
  assert.equal(buildWorkforceIntelligence({ ...forecastInput, approvedSchedule: undefined }).trainingForecast, null);
});

test('training-window forecast respects approved production assignments and machine load', () => {
  const start = new Date(now.getTime() + 2 * 86_400_000);
  const end = new Date(start.getTime() + 8 * 60 * 60_000);
  const forecastInput = {
    now,
    workers: [
      { personId: 'learner', displayName: 'Taylor Learner', homeWorkCenterCode: 'WC-S', isActive: true, shiftCode: 'DAY' },
      { personId: 'trainer', displayName: 'Morgan Trainer', homeWorkCenterCode: 'WC-S', isActive: true, shiftCode: 'DAY' },
    ],
    qualifications: [
      { personId: 'learner', code: 'SLIT', status: 'InTraining' },
      { personId: 'trainer', code: 'SLIT', status: 'Qualified', expiresAt: null },
      { personId: 'trainer', code: 'SLIT-TRAINER', status: 'Qualified', expiresAt: null },
    ],
    qualificationTypes: [
      { code: 'SLIT', kind: 'Machine', prerequisites: [] },
      { code: 'SLIT-TRAINER', kind: 'Trainer', prerequisites: ['SLIT'] },
    ],
    machineAuthorizations: [],
    attendanceOpportunities: [
      { opportunityId: 'learner-shift', personId: 'learner', shiftCode: 'DAY', scheduledStart: start, scheduledEnd: end, state: 'Scheduled' },
      { opportunityId: 'trainer-shift', personId: 'trainer', shiftCode: 'DAY', scheduledStart: start, scheduledEnd: end, state: 'Scheduled' },
    ],
    unavailability: [],
    resources: {
      workCenters: [{ code: 'WC-S' }], machines: [{ code: 'SL-01', workCenterCode: 'WC-S' }],
      machineActivities: [{ machineCode: 'SL-01', activityCode: 'SLIT', operatorQualifications: ['SLIT'] }],
      workCenterShifts: [{ workCenterCode: 'WC-S', shiftCode: 'DAY' }],
    },
    approvedSchedule: { operations: [{ scheduled: true, workCenterCode: 'WC-S', machineCode: 'SL-01',
      segments: [{ start: new Date(start.getTime() + 60 * 60_000), end: new Date(start.getTime() + 7 * 60 * 60_000), operators: ['trainer'] }] }] },
  };
  const forecast = buildWorkforceIntelligence(forecastInput).trainingForecast[0];
  assert.equal(forecast.plannedOperationCount, 1);
  assert.equal(forecast.plannedMachineOperationCount, 1);
  assert.equal(forecast.largestSharedFreeMinutes, 60, 'the six-hour production segment leaves a one-hour shared block at either end');
  assert.equal(forecast.availableFrom, start.toISOString(), 'equal-sized windows prefer the earlier shift period');
  assert.match(forecast.detail, /clear of the pair's assigned work and this machine's scheduled work/);

  const fullyBooked = buildWorkforceIntelligence({ ...forecastInput, approvedSchedule: { operations: [{ scheduled: true,
    workCenterCode: 'WC-S', machineCode: 'SL-01', segments: [{ start, end, operators: ['trainer'] }] }] } }).trainingForecast[0];
  assert.equal(fullyBooked.canReviewWindow, false);
  assert.equal(fullyBooked.largestSharedFreeMinutes, 0);
  assert.match(fullyBooked.detail, /leaves no shared window clear/);
});

test('material and suspect-scrap disruptions become source-linked workforce alerts without implying disposition', () => {
  const summary = buildWorkforceIntelligence({
    now, materialTasks: [
      { taskId: 44, orderNumber: 1042, operationSequence: 10, partNumber: 'COMP-1', quantity: 8, receivedQuantity: 5, rejectedQuantity: 1, toLocation: 'WC-A-STAGE', status: 'InTransit' },
      { taskId: 45, orderNumber: 1042, operationSequence: 20, partNumber: 'COMP-2', quantity: 4, toLocation: 'WC-A-STAGE', status: 'ApprovalNeeded', isNonBom: true },
    ],
    suspectReports: [{ reportId: 9, kind: 'Scrap', orderNumber: 1042, sequence: 10, partNumber: 'COMP-1', reasonCode: 'DIM', ncrId: null }],
  });
  assert.equal(summary.materialDisruptions.length, 3);
  assert.equal(summary.materialDisruptions[0].severity, 'urgent');
  assert.match(summary.materialDisruptions[0].title, /rejected quantity awaiting resolution/);
  assert.match(summary.materialDisruptions[0].detail, /5 accepted · 1 quarantined/);
  assert.ok(summary.materialDisruptions[0].evidenceReferences.includes('MaterialTask/44'));
  assert.ok(summary.materialDisruptions.some(row => row.evidenceReferences.includes('SuspectScrapReport/9')));
  assert.match(summary.materialDisruptions.find(row => row.title.includes('Suspect report')).detail, /reported evidence, not an approved disposition/);
});

test('material recovery priorities use linked readiness and order due dates without proposing unsafe worker moves', () => {
  const summary = buildWorkforceIntelligence({
    now,
    readiness: [
      { orderNumber: 200, sequence: 20, dueDate: '2026-11-03', workCenter: 'WC-A', executable: false, primaryConstraint: 'Material', why: 'Point-of-use material is short.' },
      { orderNumber: 201, sequence: 10, dueDate: '2026-10-27', workCenter: 'WC-B', executable: false, primaryConstraint: 'Workforce', why: 'No qualified operator.' },
    ],
    materialTasks: [
      { taskId: 1, orderNumber: 200, operationSequence: 20, partNumber: 'PART-A', quantity: 6, toLocation: 'WC-A-STAGE', status: 'Requested' },
      { taskId: 2, orderNumber: 201, operationSequence: 10, partNumber: 'PART-B', quantity: 3, toLocation: 'WC-B-STAGE', status: 'InTransit' },
      { taskId: 3, orderNumber: 202, operationSequence: 10, partNumber: 'PART-C', quantity: 2, toLocation: 'WC-A-STAGE', status: 'InTransit', rejectedQuantity: 1 },
    ],
  });

  assert.deepEqual(summary.materialDisruptions.map(row => row.orderNumber), [202, 201, 200]);
  assert.match(summary.materialDisruptions[0].detail, /Inventory Controller: review the rejected quantity/);
  assert.match(summary.materialDisruptions[1].detail, /order due 2026-10-27/);
  assert.match(summary.materialDisruptions[2].detail, /currently blocked by material readiness/);
  assert.match(summary.materialDisruptions[2].detail, /does not authorize a worker move/);
  const centerA = buildWorkforceIntelligence({ now, workCenterCode: 'WC-A', readiness: [
    { orderNumber: 200, sequence: 20, dueDate: '2026-11-03', workCenter: 'WC-A', executable: false, primaryConstraint: 'Material' },
    { orderNumber: 201, sequence: 10, dueDate: '2026-10-27', workCenter: 'WC-B', executable: false, primaryConstraint: 'Workforce' },
  ], materialTasks: [
    { taskId: 1, orderNumber: 200, operationSequence: 20, partNumber: 'PART-A', quantity: 6, toLocation: 'WC-A-STAGE', status: 'Requested' },
    { taskId: 2, orderNumber: 201, operationSequence: 10, partNumber: 'PART-B', quantity: 3, toLocation: 'WC-B-STAGE', status: 'InTransit' },
  ] });
  assert.deepEqual(centerA.materialDisruptions.map(row => row.orderNumber), [200]);
});

test('unreadable capability sources remain unknown rather than showing zero qualified workers', () => {
  const summary = buildWorkforceIntelligence({ now, workers, resources });
  assert.equal(summary.coverage, null);
  assert.equal(summary.capabilityGaps.length, 0);
});

test('readiness issues retain only program-visible workforce and safety blockers', () => {
  const summary = buildWorkforceIntelligence({
    now, workers, qualifications, resources,
    readiness: [
      { orderNumber: 101, sequence: 10, workCenter: 'WC-A', executable: false, primaryConstraint: 'Workforce', why: 'No qualified operator.' },
      { orderNumber: 102, sequence: 20, workCenter: 'WC-A', executable: false, primaryConstraint: 'Material', why: 'Short.' },
      { orderNumber: 103, sequence: 30, workCenter: 'WC-B', executable: false, primaryConstraint: 'Safety', why: 'Two-person rule is not met.' },
    ],
  });
  assert.deepEqual(summary.blockers.map(row => row.orderNumber), [101, 103]);
});

test('operation labor stays contextual and does not invent standards, efficiency, or attendance scores', () => {
  const summary = buildWorkforceIntelligence({
    now,
    activity: {
      labor: [
        { orderNumber: 1001, sequence: 10, personId: 'w-1', workCenterCode: 'WC-A', role: 'Operator', startedAt: '2026-10-23T10:00:00Z', endedAt: null },
        { orderNumber: 1001, sequence: 10, personId: 'w-2', workCenterCode: 'WC-A', role: 'Trainee', startedAt: '2026-10-23T11:00:00Z', endedAt: '2026-10-23T12:00:00Z' },
      ],
      reports: [{ orderNumber: 1001, sequence: 10, goodQuantity: 48, scrapQuantity: 2 }],
    },
    unavailability: [{ personId: 'w-1', kind: 'ApprovedPto', startsAt: '2026-10-24T00:00:00Z', endsAt: '2026-10-25T00:00:00Z' }],
  });
  assert.equal(summary.labor[0].operatorHours, 2);
  assert.equal(summary.labor[0].trainingHours, 1);
  assert.equal(summary.labor[0].goodQuantity, 48);
  assert.equal(summary.labor[0].scrapQuantity, 2);
  assert.equal(summary.labor[0].targetHours, null);
  assert.equal(summary.labor[0].efficiencyPercent, null);
  assert.equal(summary.attendance.onTimePercent, null);
  assert.equal(summary.upcomingAbsence.length, 1);
  assert.equal('reason' in summary.upcomingAbsence[0], false);
});

test('attendance summary keeps denominators, exclusions, and pending-review counts available to the UI', () => {
  const summary = buildWorkforceIntelligence({ now, attendanceSummary: {
    onTimeArrivalPercent: 96.36, eligibleArrivalOpportunities: 55, scheduledOpportunities: 60,
    onTimeArrivals: 53, verifiedLateArrivals: 2, verifiedCallOuts: 1, verifiedNoShows: 0,
    excludedOpportunities: 5, pendingReviewOpportunities: 0, missingPolicyOpportunities: 0,
    explanation: '53 of 55 eligible arrivals; 5 excluded.',
  } });
  assert.equal(summary.attendance.eligibleOpportunities, 55);
  assert.equal(summary.attendance.scheduledOpportunities, 60);
  assert.equal(summary.attendance.excludedOpportunities, 5);
  assert.equal(summary.attendance.verifiedLateArrivals, 2);
});

test('upcoming approved time away warns when it removes the last scheduled qualified machine option without exposing leave details', () => {
  const scheduledStart = new Date(now.getTime() + 2 * 86_400_000);
  const scheduledEnd = new Date(scheduledStart.getTime() + 8 * 60 * 60_000);
  const summary = buildWorkforceIntelligence({
    now,
    workers,
    qualifications,
    resources,
    attendanceOpportunities: [
      { opportunityId: 'op-w1', personId: 'w-1', shiftCode: 'DAY', scheduledStart, scheduledEnd, state: 'Scheduled' },
      { opportunityId: 'op-w2', personId: 'w-2', shiftCode: 'DAY', scheduledStart, scheduledEnd, state: 'Scheduled' },
    ],
    unavailability: [{ personId: 'w-1', kind: 'ApprovedPto', startsAt: scheduledStart, endsAt: scheduledEnd, reason: 'private leave detail' }],
  });
  assert.equal(summary.attendanceRisks.length, 1);
  assert.match(summary.attendanceRisks[0].title, /WD-01 · WIND has no qualified coverage/);
  assert.match(summary.attendanceRisks[0].detail, /private absence details are hidden/);
  assert.doesNotMatch(summary.attendanceRisks[0].detail, /ApprovedPto|private leave detail|w-1/);
  assert.ok(summary.attendanceRisks[0].evidenceReferences.includes('AttendanceOpportunity/op-w1'));

  const withQualifiedBackfill = buildWorkforceIntelligence({
    now,
    workers: [...workers, { personId: 'w-3', displayName: 'Cy Qualified', homeWorkCenterCode: 'WC-A', isActive: true, shiftCode: 'DAY' }],
    qualifications: [...qualifications, { personId: 'w-3', code: 'WIND-II', status: 'Qualified', expiresAt: null }],
    resources,
    attendanceOpportunities: [
      { opportunityId: 'op-w1', personId: 'w-1', shiftCode: 'DAY', scheduledStart, scheduledEnd, state: 'Scheduled' },
      { opportunityId: 'op-w2', personId: 'w-2', shiftCode: 'DAY', scheduledStart, scheduledEnd, state: 'Scheduled' },
      { opportunityId: 'op-w3', personId: 'w-3', shiftCode: 'DAY', scheduledStart, scheduledEnd, state: 'Scheduled' },
    ],
    unavailability: [{ personId: 'w-1', kind: 'ApprovedPto', startsAt: scheduledStart, endsAt: scheduledEnd }],
  });
  assert.equal(withQualifiedBackfill.attendanceRisks.length, 0, 'a qualified, authorized scheduled alternative prevents the coverage alert');
});

test('reported call-outs and verified no-shows create source-linked coverage alerts without reading absence reasons', () => {
  const scheduledStart = new Date(now.getTime() - 60 * 60_000);
  const scheduledEnd = new Date(now.getTime() + 7 * 60 * 60_000);
  const summary = buildWorkforceIntelligence({
    now, workers, qualifications, resources,
    attendanceOpportunities: [
      { opportunityId: 'op-w1', personId: 'w-1', shiftCode: 'DAY', scheduledStart, scheduledEnd, state: 'Scheduled' },
      { opportunityId: 'op-w2', personId: 'w-2', shiftCode: 'DAY', scheduledStart, scheduledEnd, state: 'Scheduled' },
    ],
    attendanceEvents: [{ eventId: 'callout-event', opportunityId: 'op-w1', personId: 'w-1', kind: 'CallOut', reviewState: 'PendingReview', reasonCode: 'DO_NOT_EXPOSE' }],
  });
  const callout = summary.attendanceRisks.find(row => row.evidenceReferences.includes('AttendanceEvent/callout-event'));
  assert.ok(callout);
  assert.equal(callout.severity, 'attention');
  assert.match(callout.title, /WD-01 · WIND has no qualified coverage/);
  assert.match(callout.detail, /awaiting independent review; missing punches alone are never treated as no-shows/);
  assert.doesNotMatch(JSON.stringify(callout), /DO_NOT_EXPOSE/);
});
