import assert from 'node:assert/strict';
import test from 'node:test';
import { buildWorkforceIntelligence, renderEvidenceReference } from '../../src/Forge.Host/wwwroot/js/workforce-intelligence.mjs';

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
