import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

const view = (over = {}) => ({ partNumber: 'SOLV-1', description: 'Cleaning solvent', waiting: 0, locations: [{ locationCode: 'FLAM-CAB', quantity: 4 }],
  chemical: { partNumber: 'SOLV-1', hazardous: true, signalWord: 'Danger', pictograms: ['GHS02', 'GHS07'], ppe: 'Nitrile gloves, goggles' },
  current: { sdsId: 3, productName: 'Kleen-All', manufacturer: 'Acme Chem', revisionDate: '2024-01-15', reviewDue: '2027-01-15' }, ...over });

test('the register shows hazards, the SDS in force with its review state, and flags hazardous materials without one', async t => {
  installBrowser(t);
  const { registerHtml, reviewState } = await import('../../src/Forge.Host/wwwroot/js/pages/chemicals.js');
  assert.equal(reviewState({ reviewDue: '2026-10-01' }, '2026-11-01'), 'overdue');
  assert.equal(reviewState({ reviewDue: '2026-11-20' }, '2026-11-01'), 'soon');
  assert.equal(reviewState({ reviewDue: '2027-11-20' }, '2026-11-01'), 'ok');
  const html = registerHtml([view(), view({ partNumber: 'ACID-2', current: null, waiting: 1 })], { today: '2026-11-01', canManage: true });
  assert.match(html, /pill bad">Danger/);
  assert.match(html, /title="Flammable">GHS02/);
  assert.match(html, /data-open="3"/);
  assert.match(html, /FLAM-CAB/);
  assert.match(html, /No approved SDS — cannot be bought or received/);
  assert.match(html, /1 waiting/);
  assert.match(html, /data-chemical="ACID-2"/);
  assert.match(registerHtml([view({ current: { ...view().current, reviewDue: '2026-10-01' } })], { today: '2026-11-01' }), /Review overdue/);
});

test('safety data sheets show status and the EHS actions only where they apply', async t => {
  installBrowser(t);
  const { sdsHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/chemicals.js');
  const html = sdsHtml([
    { sdsId: 4, partNumber: 'SOLV-1', productName: 'Kleen-All', manufacturer: 'Acme Chem', revisionDate: '2026-09-01', status: 'Submitted', submittedBy: 'person:b', submittedAt: '2026-11-01T12:00:00Z' },
    { sdsId: 3, partNumber: 'SOLV-1', productName: 'Kleen-All', manufacturer: 'Acme Chem', revisionDate: '2024-01-15', status: 'Approved', submittedBy: 'person:b', submittedAt: '2024-02-01T12:00:00Z', reviewDue: '2027-01-15' },
  ], { canManage: true });
  assert.match(html, /data-approve="4"/);
  assert.doesNotMatch(html, /data-approve="3"/);
  assert.match(html, /data-review="3"/);
  assert.match(html, /pill warn">Submitted/);
});

test('HazCom training shows the site requirement, who is current, and lets people be selected for training', async t => {
  installBrowser(t);
  const { trainingHtml, registerHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/chemicals.js');
  const view = {
    qualificationCode: 'HAZCOM', qualificationName: 'Hazard Communication (HazCom)', validityDays: 365,
    requirements: [{ requirementId: 'r1', workCenterCode: null, qualificationCode: 'HAZCOM', dueDays: 7 }],
    chemicalsAwaitingTrainingReview: ['ACID-2'],
    people: [
      { personId: 'p1', displayName: 'Ana', workCenterCode: 'SLIT-WC', status: 'Missing' },
      { personId: 'p2', displayName: 'Ben', workCenterCode: 'SLIT-WC', status: 'Expiring', expiresAt: '2026-11-20T00:00:00Z' },
      { personId: 'p3', displayName: 'Cy', workCenterCode: 'PAINT', status: 'Current', expiresAt: '2027-06-01T00:00:00Z' },
      { personId: 'p4', displayName: 'Di', workCenterCode: 'PAINT', status: 'Expired', openRequestId: 'q1', openRequestStatus: 'Requested', openRequestDue: '2026-11-08' },
    ],
  };
  const html = trainingHtml(view, { canRequest: true });
  assert.match(html, /every 365 days/);
  assert.match(html, /Every new employee within 7 days/);
  assert.match(html, /New hazards to review<\/span> ACID-2/);
  assert.match(html, /Not trained: 1/);
  assert.match(html, /data-train-person="p1"/);
  assert.match(html, /data-train-person="p2"/);
  assert.doesNotMatch(html, /data-train-person="p3"/); // current
  assert.doesNotMatch(html, /data-train-person="p4"/); // already requested
  assert.match(html, /Requested · due/);
  assert.match(html, /data-request-training/);
  assert.doesNotMatch(trainingHtml(view), /data-request-training|data-train-person/);
  assert.match(trainingHtml({ ...view, validityDays: null, requirements: [] }), /no expiry.*no onboarding requirement/s);
  const reg = registerHtml([{ partNumber: 'ACID-2', waiting: 0, locations: [], chemical: { partNumber: 'ACID-2', hazardous: true, signalWord: 'Danger', pictograms: [], trainingReviewDue: true }, current: null }], { today: '2026-11-01', canManage: true });
  assert.match(reg, /HazCom training review needed/);
  assert.match(reg, /data-training-review="ACID-2"/);
});
