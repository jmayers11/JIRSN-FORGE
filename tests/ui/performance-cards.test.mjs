import assert from 'node:assert/strict';
import test from 'node:test';
import { gradeCard, boardHtml, printDocument, reviewPeriods, mountPerformanceCards, THRESHOLDS } from '../../src/Forge.Host/wwwroot/js/performance-cards.js';
import { appendWorkforceIntelligence } from '../../src/Forge.Host/wwwroot/js/pages/workforce-intelligence.js';
import { installBrowser, response, fire, flush } from './browser-harness.mjs';

const later = days => new Date(Date.now() + days * 86_400_000).toISOString();
const strong = {
  personId: 'p-1', displayName: 'Ari Operator', jobTitle: 'Machine Operator', department: 'Production', homeWorkCenterCode: 'WC-SEW', shiftCode: 'SHIFT-DAY', isActive: true,
  efficiency: { controllablePercent: 112, unadjustedPercent: 104, directLaborHours: 120, eligibleLaborHours: 110, verifiedExternalDelayHours: 10, operationsInScope: 14, operationsOutsideScope: 2, status: 'OK' },
  firstPass: { officialProcessPercent: 97.5, employeeAttributablePercent: 98.7, inspectedQuantity: 400, coveragePercent: 92, minimumCoveragePercent: 80, externallyCausedQuantity: 4, unassignedQuantity: 0, status: 'OK' },
  attendance: { onTimePercent: 100, scheduled: 60, eligible: 58, onTime: 58, verifiedLate: 0, verifiedCallOuts: 0, verifiedNoShows: 0, excluded: 2, pendingReview: 0, missingPolicy: 0, explanation: '' },
  coverage: { goalSuccessPercent: null, averageAttainmentPercent: null, completed: 0, goalEligible: 0, goalSuccesses: 0, verifiedHours: 0, distinctOperations: 0, excluded: 0, status: '' },
  competency: { current: 4, expiringSoon: 0, expired: 0, inTraining: 0, machineAuthorizationsValid: 4, machineAuthorizationsInvalid: 0, coursesCompletedInPeriod: 1, practicalsPassedInPeriod: 1, practicalsFailedInPeriod: 0, peopleTrainedInPeriod: 2,
    qualifications: [{ code: 'QUAL-SEW', name: 'Sewing', status: 'Qualified', certifiedAt: '2026-01-05T00:00:00Z', expiresAt: null }], machineAuthorizations: [], coursesInPeriod: [] },
  paidHours: 480, unaccountedHours: 2, shiftsRecorded: 60, contributions: ['Trained 2 co-worker(s) as the recorded trainer.'],
};

test('green, yellow and red follow the published thresholds and say why; thin or unverified evidence stays gray', () => {
  const good = gradeCard(strong);
  assert.equal(good.overall, 'good');
  assert.deepEqual(good.metrics.map(m => [m.key, m.grade]), [['efficiency', 'good'], ['firstPass', 'good'], ['attendance', 'good'], ['coverage', 'na'], ['qualifications', 'good']]);
  assert.equal(good.metrics[0].display, '112%');
  assert.match(good.metrics[3].reason, /No cross-functional coverage in this period \(not a shortfall\)/);

  const mixed = gradeCard({ ...strong,
    efficiency: { ...strong.efficiency, controllablePercent: 90 },
    attendance: { ...strong.attendance, onTimePercent: 85, pendingReview: 3 },
    competency: { ...strong.competency, expired: 1, qualifications: [{ code: 'QUAL-FORK', name: 'Forklift', status: 'Expired', expiresAt: '2026-01-01T00:00:00Z' }] } });
  assert.equal(mixed.overall, 'concern');
  const byKey = Object.fromEntries(mixed.metrics.map(m => [m.key, m]));
  assert.equal(byKey.efficiency.grade, 'watch');
  assert.match(byKey.efficiency.reason, new RegExp(`Between ${THRESHOLDS.efficiency.watch}% and the ${THRESHOLDS.efficiency.good}% target`));
  assert.equal(byKey.attendance.grade, 'concern');
  assert.match(byKey.attendance.reason, /3 records still awaiting review are not counted/);
  assert.equal(byKey.qualifications.grade, 'concern');
  assert.match(byKey.qualifications.reason, /QUAL-FORK/);
  assert.match(mixed.summary, /Labor efficiency:/);

  const soon = gradeCard({ ...strong, competency: { ...strong.competency, expiringSoon: 1, qualifications: [{ code: 'QUAL-SEW', name: 'Sewing', status: 'Qualified', expiresAt: later(10) }] } });
  assert.equal(soon.metrics[4].grade, 'watch');
  assert.match(soon.metrics[4].reason, /expire within 30 days \(QUAL-SEW\)/);

  // A low figure on too little evidence, or an unverified attribution, is never red.
  const thin = gradeCard({ ...strong, efficiency: { ...strong.efficiency, controllablePercent: 40, eligibleLaborHours: 3 },
    firstPass: { ...strong.firstPass, employeeAttributablePercent: null, status: 'N/A: attribution coverage 20% is below 80%.' },
    attendance: null, coverage: null, competency: null });
  assert.equal(thin.overall, 'na');
  assert.ok(thin.metrics.every(m => m.grade === 'na'));
  assert.match(thin.metrics[0].reason, /at least 8 h are needed/);
  assert.match(thin.metrics[1].reason, /below 80%/);
  assert.match(thin.metrics[2].reason, /Human Resources only/);
});

test('the board groups employees by status, filters to the visible roster and prints a review summary', () => {
  const periods = reviewPeriods(new Date(2026, 9, 24));
  assert.deepEqual(periods.slice(0, 2).map(p => p.label), ['Q4 2026 (to date)', 'Q3 2026']);
  assert.ok(periods.some(p => p.label === '2026 year to date' && p.kind === 'Annual'));
  assert.ok(periods.some(p => p.label === '2025 full year'));
  const weak = { ...strong, personId: 'p-2', displayName: 'Bo Trainee', efficiency: { ...strong.efficiency, controllablePercent: 70 } };
  const data = { from: '2026-07-01T00:00:00Z', to: '2026-10-01T00:00:00Z', asOf: '2026-10-24T00:00:00Z', expiringWithinDays: 30, cards: [strong, weak], restrictions: ['Attendance is hidden.'], basis: ['Basis text.'] };
  const html = boardHtml(data, { periodKey: '2026-Q3', groupBy: 'status' }, periods);
  assert.match(html, /1 needs review/);
  assert.match(html, /1 on track/);
  assert.ok(html.indexOf('Bo Trainee') < html.indexOf('Ari Operator'), 'Needs review comes first');
  assert.match(html, /Quarterly performance summary · Q3 2026/);
  assert.match(html, /Documented contributions/);
  assert.match(html, /Attendance is hidden\./);
  assert.match(html, /green ≥ 95% · yellow ≥ 85%/);
  const onlyAri = boardHtml(data, { periodKey: '2026-Q3', groupBy: 'shift', visibleIds: new Set(['p-1']) }, periods);
  assert.doesNotMatch(onlyAri, /Bo Trainee/);
  assert.match(onlyAri, /SHIFT-DAY/);

  const printed = printDocument(data, periods.find(p => p.key === '2026-Q3'));
  assert.match(printed, /<title>Quarterly performance summary · Q3 2026<\/title>/);
  assert.match(printed, /<h2>Summary<\/h2>/);
  assert.match(printed, /class="g-concern">70%/);
  assert.match(printed, /makes no ranking, pay, promotion or disciplinary decision/);
  const single = printDocument(data, periods.find(p => p.key === '2026'), 'p-1');
  assert.match(single, /Annual performance summary/);
  assert.doesNotMatch(single, /Bo Trainee/);
  assert.match(single, /Reviewer/);
});

test('cards load per review period on demand and the print button opens a printable summary', async t => {
  const browser = installBrowser(t);
  const root = browser.document.createElement('div');
  browser.document.body.append(root);
  const calls = [];
  const get = async path => { calls.push(path); return { data: { from: 'x', to: 'y', cards: [strong], restrictions: [], basis: [], expiringWithinDays: 30 } }; };
  let written = '';
  globalThis.window = { open: () => ({ document: { open() {}, write(html) { written += html; }, close() {} }, focus() {}, print() {} }) };
  t.after(() => { delete globalThis.window; });
  const cards = mountPerformanceCards(root, { get, now: new Date(2026, 9, 24) });
  await flush();
  assert.equal(calls.length, 1);
  assert.match(calls[0], /^\/api\/workforce\/performance-cards\?from=2026-10-0/);
  assert.match(root.querySelector('[data-perf-board]').innerHTML, /Ari Operator/);
  const period = root.querySelector('[data-perf-control="period"]');
  period.value = '2026';
  fire(period, 'change');
  await flush();
  assert.equal(calls.length, 2);
  assert.match(calls[1], /from=2026-01-0/);
  period.value = '2026-Q4';
  fire(period, 'change');
  await flush();
  assert.equal(calls.length, 2, 'a period already read is reused');
  fire(root.querySelector('[data-perf-print="all"]'), 'click');
  assert.match(written, /Quarterly performance summary · Q4 2026 \(to date\)/);
  assert.equal(cards.periods[0].key, '2026-Q4');
});

test('the Command Center is tabbed and gathers employee cards only when the Employees tab opens', async t => {
  const browser = installBrowser(t);
  const page = browser.document.createElement('div');
  browser.document.body.append(page);
  const paths = [];
  globalThis.fetch = path => {
    paths.push(path);
    if (path === '/api/workforce/workers') return Promise.resolve(response([{ personId: 'p-1', displayName: 'Ari Operator', homeWorkCenterCode: 'WC-SEW', isActive: true, shiftCode: 'SHIFT-DAY' }]));
    if (path.startsWith('/api/workforce/performance-cards?')) return Promise.resolve(response({ from: 'x', to: 'y', cards: [strong], restrictions: [], basis: [], expiringWithinDays: 30 }));
    if (path === '/api/resources/') return Promise.resolve(response({ sites: [], shifts: [], workCenters: [], machines: [] }));
    return Promise.resolve(response([]));
  };
  await appendWorkforceIntelligence(page, { permissions: ['workforce.read'] });
  const tabs = page.querySelectorAll('[data-wi-tab]').map(button => button.dataset.wiTab);
  assert.deepEqual(tabs, ['overview', 'employees', 'training', 'coverage', 'attendance', 'labor']);
  assert.equal(page.querySelector('[data-wi-panel="overview"]').hidden, false);
  assert.equal(page.querySelector('[data-wi-panel="attendance"]').hidden, true);
  assert.ok(!paths.some(path => path.startsWith('/api/workforce/performance-cards')));
  fire(page.querySelector('[data-wi-tab="employees"]'), 'click');
  await flush();
  assert.equal(page.querySelector('[data-wi-panel="employees"]').hidden, false);
  assert.equal(page.querySelector('[data-wi-panel="overview"]').hidden, true);
  assert.equal(page.querySelector('[data-wi-tab="employees"]').getAttribute('aria-selected'), 'true');
  assert.equal(paths.filter(path => path.startsWith('/api/workforce/performance-cards')).length, 1);
  assert.match(page.querySelector('[data-perf-board]').innerHTML, /Ari Operator/);
});

test('each employee is a collapsed row of colored figures that opens into the full card; expand and collapse all', async t => {
  const browser = installBrowser(t);
  const root = browser.document.createElement('div');
  browser.document.body.append(root);
  const weak = { ...strong, personId: 'p-2', displayName: 'Bo Trainee', efficiency: { ...strong.efficiency, controllablePercent: 70 } };
  mountPerformanceCards(root, { get: async () => ({ data: { cards: [strong, weak], restrictions: [], basis: [], expiringWithinDays: 30 } }), now: new Date(2026, 9, 24) });
  await flush();
  const board = root.querySelector('[data-perf-board]');
  assert.match(board.innerHTML, /<details class="perf-card concern" data-perf-card="p-2" >/);
  assert.match(board.innerHTML, /perf-chip concern[^>]*><span class="perf-dot concern" aria-hidden="true"><\/span>Eff <b>70%<\/b>/);
  assert.match(board.innerHTML, /perf-chip good[^>]*>.*?On-time <b>100%<\/b>/s);
  fire(root.querySelector('[data-perf-expand="open"]'), 'click');
  assert.ok(root.querySelectorAll('details.perf-card').every(card => card.open));
  // Opened cards stay open when the board is regrouped.
  const group = root.querySelector('[data-perf-control="group"]');
  group.value = 'shift';
  fire(group, 'change');
  assert.match(root.querySelector('[data-perf-board]').innerHTML, /data-perf-card="p-1" open>/);
  fire(root.querySelector('[data-perf-expand="close"]'), 'click');
  fire(group, 'change');
  assert.doesNotMatch(root.querySelector('[data-perf-board]').innerHTML, /data-perf-card="p-1" open>/);
});

test('Training & Performance has its own page with the Command Center under the page heading', async t => {
  const { performancePage } = await import('../../src/Forge.Host/wwwroot/js/pages/performance.js');
  const browser = installBrowser(t);
  const page = browser.document.createElement('div');
  browser.document.body.append(page);
  globalThis.fetch = path => Promise.resolve(response(path === '/api/resources/' ? { sites: [], shifts: [], workCenters: [], machines: [] } : []));
  await performancePage(page, { me: { permissions: ['workforce.read'] }, query: new URLSearchParams('') });
  assert.ok(page.innerHTML.indexOf('<h1>Training &amp; Performance</h1>') < page.innerHTML.indexOf('Training &amp; Performance Command Center'));
  assert.equal(page.querySelector('[data-wi-panel="overview"]').hidden, false);
});
