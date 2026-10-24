import assert from 'node:assert/strict';
import test from 'node:test';
import { candidateCapacityHtml, capacityByPerson } from '../../src/Forge.Host/wwwroot/js/skill-capacity.js';
import { coverageHtml } from '../../src/Forge.Host/wwwroot/js/cause-performance.js';

// Sprint 10 §21.5 and §22: forecasts show their evidence and stay planning estimates; coverage goals are judged per assignment.

const forecast = { isEligible: true, eligibilityStatus: 'Eligible', productiveMinutes: 420, conservativeGoodUnits: 68, expectedGoodUnits: 75, optimisticGoodUnits: 82, daysSinceLastVerifiedProductiveExecution: 124, confidence: 'Medium', estimateBasis: '6 verified runs of CNC_RUN on CNC-02.' };
const capacity = { rank: 1, personId: 'p1', explanation: 'Dana: qualified for CNC-02; last verified operation 124 days ago.', atTarget: { forecast, lastOperatedAt: '2026-06-22T12:00:00Z', expectedSetupMinutes: 35, requiredRefresher: null, notes: ['35 minutes of reacclimation are added to setup after 124 days away.'] }, atOrigin: { forecast: { expectedGoodUnits: 20 } } };

test('a candidate shows qualified, last operated, setup, range, confidence, refresher and origin impact', () => {
  const html = candidateCapacityHtml(capacity);
  for (const label of ['Qualified', 'Last operated', 'Expected setup', 'Expected good units', 'Confidence', 'Required refresher', 'Impact on original work']) assert.match(html, new RegExp(label));
  assert.match(html, /68–82 good units \(expected 75\)/);
  assert.match(html, /124 days ago/);
  assert.match(html, /About 20 units lost unless backfilled/);
  assert.match(html, /reacclimation/);
  assert.equal(capacityByPerson({ candidates: [capacity] }).get('p1'), capacity);
});

test('an ineligible candidate gets no unit range, only the reason', () => {
  const html = candidateCapacityHtml({ ...capacity, atTarget: { ...capacity.atTarget, requiredRefresher: 'Required: a passed practical assessment.', forecast: { ...forecast, isEligible: false, eligibilityStatus: 'A required refresher or supervised reacclimation is incomplete.', expectedGoodUnits: null } } });
  assert.match(html, /No — A required refresher/);
  assert.doesNotMatch(html, /68–82/);
});

const assignment = (id, state, extra = {}) => ({ assignmentId: id, state, machineCode: 'CNC-02', activityCode: 'CNC_RUN', destinationWorkCenter: 'CNC-WC', plannedStart: '2026-10-20T12:00:00Z', comparable: true, approvedBy: 'person:s1', forecastExpected: 72, ...extra });
const report = {
  basis: 'Basis.',
  metrics: { coverageGoalSuccessRatePercent: 87.5, goalSuccessCount: 7, goalEligibleAssignmentCount: 8, averageCoverageGoalAttainmentPercent: 99.6, medianCoverageGoalAttainmentPercent: 104.2, completedAssignmentCount: 8, verifiedCoverageHours: 52, distinctQualifiedOperationsCovered: 3, meanAbsoluteForecastErrorGoodUnits: 4, forecastComparisonCount: 8, excludedGoalAssignmentCount: 1, goalExclusions: [{ assignmentId: 'x', reason: 'Cancelled assignment.' }], status: 'Coverage goal results use accepted good output.' },
  assignments: [
    { assignment: assignment('a1', 'Completed'), goals: [{ version: 1, kind: 'Original', goodUnits: 70, afterStart: false }, { version: 2, kind: 'Revision', goodUnits: 65, afterStart: true }], actualGoodUnits: 76, outputAccepted: true, contextFlags: ['The goal was revised after work began; the original and every revision are shown.'] },
    { assignment: assignment('a2', 'Planned'), goals: [{ version: 1, kind: 'Original', goodUnits: 70, afterStart: false }], actualGoodUnits: null, outputAccepted: true, contextFlags: [] },
  ],
};

test('coverage shows success and attainment with their samples, goal history and exclusions', () => {
  const html = coverageHtml(report, { personId: 's2', permissions: ['workforce.supervise'] });
  assert.match(html, /87\.5%/);
  assert.match(html, /7 of 8 eligible assignments/);
  assert.match(html, /v1 Original: 70 → v2 Revision: 65 \(after start\)/);
  assert.match(html, /1 excluded: Cancelled assignment\./);
  assert.match(html, /not a measure of the person/);
  assert.match(html, /data-cov-review="a1"/);
  assert.match(html, /data-cov-step="start\|a2"/);
  assert.doesNotMatch(coverageHtml(report, { personId: 's1', permissions: ['workforce.supervise'] }), /data-cov-review/);
  assert.doesNotMatch(coverageHtml(report, { personId: 'e1', permissions: [] }), /data-cov-/);
  assert.match(coverageHtml({ ...report, assignments: [], metrics: { ...report.metrics, coverageGoalSuccessRatePercent: null } }, { permissions: [] }), /N\/A, not 0%/);
});
