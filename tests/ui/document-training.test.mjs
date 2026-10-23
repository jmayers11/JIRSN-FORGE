import assert from 'node:assert/strict';
import test from 'node:test';
import { appendDocumentTraining } from '../../src/Forge.Host/wwwroot/js/executable.js';
import { installBrowser, response, fire } from './browser-harness.mjs';

const employee = { personId: '11111111-1111-4111-8111-111111111111', displayName: 'Alex Operator' };
const manager = { personId: '22222222-2222-4222-8222-222222222222', permissions: ['competency.manage'] };

test('document training uses an employee directory selector when available', async t => {
  const browser = installBrowser(t), section = browser.document.createElement('section'); browser.document.body.append(section);
  globalThis.fetch = path => path === '/api/kernel/directory'
    ? Promise.resolve(response([employee]))
    : Promise.resolve(response({ policy: { policy: 'Warn', documentTypes: ['WI'] }, records: [] }));

  await appendDocumentTraining(section, manager);
  fire(section.querySelector('[data-train-add]'), 'click');
  const modal = browser.document.querySelector('.modal');
  assert.equal(modal.querySelector('#f-personId').tagName, 'select');
  assert.match(modal.querySelector('#f-personId').textContent, /Alex Operator/);
  assert.doesNotMatch(modal.textContent, /Person ID/);
});

test('document training retains the manual ID fallback when directory access is unavailable', async t => {
  const browser = installBrowser(t), section = browser.document.createElement('section'); browser.document.body.append(section);
  globalThis.fetch = path => path === '/api/kernel/directory'
    ? Promise.resolve(response({ detail: 'Forbidden' }, 403))
    : Promise.resolve(response({ policy: { policy: 'Warn', documentTypes: ['WI'] }, records: [] }));

  await appendDocumentTraining(section, manager);
  fire(section.querySelector('[data-train-add]'), 'click');
  const modal = browser.document.querySelector('.modal');
  assert.equal(modal.querySelector('#f-personId').tagName, 'input');
  assert.match(modal.textContent, /Person ID/);
});
