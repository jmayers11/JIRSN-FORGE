import assert from 'node:assert/strict';
import test from 'node:test';
import { workInboxPage } from '../../src/Forge.Host/wwwroot/js/pages/work-inbox.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

test('assignment status changes record a reason in an accessible Forge dialog', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'), calls = [];
  browser.document.body.append(page);
  const previous = { subject: session.subject, token: session.token }; session.subject = 'work-inbox-user'; session.token = null;
  t.after(() => Object.assign(session, previous));
  globalThis.fetch = (path, options = {}) => {
    calls.push({ path, options, body: options.body ? JSON.parse(options.body) : null });
    if (path === '/api/work-inbox') return Promise.resolve(response({ approvals: [], assignments: [{ assignmentId: 'assignment-7', workType: 'Inspection', title: 'Inspect lot L-17', status: 'Assigned', assignedAt: '2026-10-18T13:00:00Z', reason: 'Verify first article', deepLink: '#/quality' }] }));
    return Promise.resolve(response({ message: 'Assignment updated.' }));
  };

  await workInboxPage(page, { me: { personId: 'operator-7', permissions: [] } });
  fire(page.querySelector('[data-next="assignment-7"]'), 'click');
  const modal = browser.document.querySelector('.modal');
  assert.equal(modal.getAttribute('role'), 'dialog'); assert.equal(modal.getAttribute('aria-modal'), 'true');
  modal.querySelector('#reason').value = 'The inspection work has started.';
  fire(modal.querySelector('[data-ok]'), 'click'); await flush(); await flush();
  const update = calls.find(call => call.path === '/api/work-inbox/assignments/assignment-7/status');
  assert.deepEqual(update.body, { status: 'Acknowledged', reason: 'The inspection work has started.' });
  assert.equal(update.options.method, 'POST'); assert.ok(update.options.headers['Idempotency-Key']);
  assert.equal(modal.isConnected, false); assert.equal(browser.document.listeners.get('keydown').size, 0);
});

test('work assignment selects a directory person by display label but submits only the person ID', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'), calls = [];
  browser.document.body.append(page);
  const personId = 'a7a35f21-3b44-42fd-935d-1c1461669520';
  const previous = { subject: session.subject, token: session.token }; session.subject = 'work-inbox-assignment-user'; session.token = null;
  t.after(() => Object.assign(session, previous));
  globalThis.fetch = (path, options = {}) => {
    calls.push({ path, options, body: options.body ? JSON.parse(options.body) : null });
    if (path === '/api/kernel/directory') return Promise.resolve(response([{ personId, displayName: 'Morgan Chen' }]));
    if (path === '/api/work-inbox') return Promise.resolve(response({ approvals: [], assignments: [] }));
    return Promise.resolve(response({ message: 'Assigned.' }));
  };
  await workInboxPage(page, { me: { personId: 'manager-1', permissions: ['work.assignments.manage'] } });
  const form = page.querySelector('#assign-work-form');
  assert.equal(form.querySelector('[name="assigneePersonId"]'), null);
  assert.equal(form.querySelector('#f-assigneePersonId-list option').value, 'Morgan Chen · ' + personId);
  form.querySelector('#f-workType').value = 'Inspection'; form.querySelector('#f-workId').value = 'INSP-9';
  form.querySelector('#f-title').value = 'Verify first article'; form.querySelector('#f-assigneePersonId').value = 'Morgan Chen · ' + personId;
  form.querySelector('#f-deepLink').value = '#/quality'; form.querySelector('#f-reason').value = 'Inspection must be completed before release.';
  fire(form, 'submit'); await flush(); await flush();
  const assigned = calls.find(call => call.path === '/api/work-inbox/assignments' && call.body);
  assert.equal(assigned.body.assigneePersonId, personId); assert.equal(assigned.body.title, 'Verify first article');
  assert.equal(assigned.body.reason, 'Inspection must be completed before release.');
});
