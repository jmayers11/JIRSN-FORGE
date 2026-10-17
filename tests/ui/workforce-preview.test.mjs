import assert from 'node:assert/strict';
import test from 'node:test';
import { appendWorkforceAdmin } from '../../src/Forge.Host/wwwroot/js/pages/workforce-admin.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

const personId = '1f9a60c2-0ff8-4276-8125-f17efb69570a';

test('workforce assignment impact preview is read-only and does not submit an assignment', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  const before = { subject: session.subject, token: session.token }; session.subject = 'readiness-preview-user'; session.token = null; t.after(() => Object.assign(session, before));
  globalThis.fetch = (path, options = {}) => {
    calls.push({ path, options, body: options.body ? JSON.parse(options.body) : null });
    if (path === '/api/kernel/directory') return Promise.resolve(response([{ personId, displayName: 'Operator One' }]));
    if (path === '/api/competency/qualification-types') return Promise.resolve(response([]));
    if (path === '/api/resources/') return Promise.resolve(response({ safetyAreas: [{ code: 'CUT' }], workCenters: [{ code: 'WC1' }], machines: [{ code: 'M1' }], activityTypes: [{ code: 'SLIT' }] }));
    if (path === `/api/competency/people/${personId}`) return Promise.resolve(response([]));
    if (path === `/api/readiness/reallocation-preview/${personId}`) return Promise.resolve(response({
      engineName: 'reallocation-impact', engineVersion: '1', output: { personId, personName: 'Operator One', impacts: [{ kind: 'WouldBeUnstaffed', work: { workCenterCode: 'WC2', activityCode: 'WIND' }, constraint: 'Staffing', detail: 'The active work would have nobody assigned.' }], options: [{ description: 'Review staffing manually.', consequences: ['No automatic reassignment occurs.'] }] }, evidence: {},
    }));
    return Promise.resolve(response({ message: 'Recorded.' }));
  };

  await appendWorkforceAdmin(page, { personId: 'supervisor', permissions: ['workforce.supervise', 'readiness.read'] });
  page.querySelector('#wf-person').value = personId;
  fire(page.querySelector('#wf-person'), 'change');
  await flush();
  page.querySelector('[data-preview-field="workCenterCode"]').value = 'WC1';
  page.querySelector('[data-preview-field="activityCode"]').value = 'SLIT';
  fire(page.querySelector('[data-reallocation-preview]'), 'click');
  await flush();

  const preview = calls.find(call => call.path === `/api/readiness/reallocation-preview/${personId}`);
  assert.ok(preview);
  assert.equal(preview.options.method, 'POST');
  assert.equal(preview.options.headers['Idempotency-Key'], undefined);
  assert.deepEqual(preview.body, { safetyAreaCode: '', workCenterCode: 'WC1', machineCode: '', activityCode: 'SLIT', asTrainee: false });
  const section = page.querySelector('[aria-label="Manage people"]');
  assert.match(section.innerHTML, /Preview only: no assignment was changed/);
  assert.match(section.innerHTML, /would have nobody assigned/);
  assert.equal(calls.some(call => call.path === `/api/workforce/workers/${personId}/assignment`), false);
});
