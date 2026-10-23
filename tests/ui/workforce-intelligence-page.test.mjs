import assert from 'node:assert/strict';
import test from 'node:test';
import { appendWorkforceIntelligence } from '../../src/Forge.Host/wwwroot/js/pages/workforce-intelligence.js';
import { installBrowser, response, fire, flush } from './browser-harness.mjs';

test('Workforce Command Center uses recorded sources, exposes honest gaps, and keeps absence reasons private', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div');
  browser.document.body.append(page);
  const attendanceCalls = [];
  const worker = { personId: 'worker-1', displayName: 'Ari Operator', homeWorkCenterCode: 'WC-A', isActive: true, shiftCode: 'DAY', unavailability: null };
  globalThis.fetch = path => {
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
      { orderNumber: 77, sequence: 10, workCenter: 'WC-A', executable: false, primaryConstraint: 'Workforce', why: 'No qualified operator.' },
    ]));
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
  await appendWorkforceIntelligence(page);
  assert.match(page.innerHTML, /Training &amp; Performance Command Center/);
  assert.match(page.innerHTML, /Current qualifications[\s\S]*?1/);
  assert.match(page.innerHTML, /expires in 2 days/);
  assert.match(page.innerHTML, /WO 77 · operation 10 is blocked by Workforce/);
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
});
