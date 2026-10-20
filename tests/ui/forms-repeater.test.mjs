import assert from 'node:assert/strict';
import test from 'node:test';
import { fieldHtml, formModal, readFields } from '../../src/Forge.Host/wwwroot/js/forms.js';
import { installBrowser, flush, fire } from './browser-harness.mjs';

test('repeater rows add, remove and serialize into the existing pipe-delimited contract', async t => {
  const browser = installBrowser(t), field = {
    name: 'slots', label: 'Role slots', type: 'repeater', required: true, minRows: 1, rowLabel: 'slot', pluralRowLabel: 'slots',
    columns: [
      { name: 'name', label: 'Slot name', required: true },
      { name: 'count', label: 'People', type: 'number', min: 1, required: true },
      { name: 'qualificationCodes', label: 'Qualifications', required: true },
      { name: 'traineeMayFill', label: 'Trainee may fill', type: 'select', required: true, options: [['no', 'No'], ['yes', 'Yes']] },
    ],
    serialize: rows => rows.map(row => `${row.name} | ${row.count} | ${row.qualificationCodes} | ${row.traineeMayFill}`).join('\n'),
  };
  let sent;
  const pending = formModal({ title: 'Safety rule', fields: [field] }, async values => { sent = values; return { message: 'Saved.' }; });
  const modal = browser.document.querySelector('.modal'), repeater = modal.querySelector('#f-slots');
  assert.equal(repeater.getAttribute('role'), 'group'); assert.equal(repeater.getAttribute('aria-labelledby'), 'f-slots-label');
  assert.equal(repeater.querySelectorAll('[data-repeater-row]').length, 1, 'the required first row is ready without an extra click');
  const first = repeater.querySelector('[data-repeater-row]');
  first.querySelector('[data-repeater-column="name"]').value = 'Slitter operator';
  first.querySelector('[data-repeater-column="count"]').value = '2';
  first.querySelector('[data-repeater-column="qualificationCodes"]').value = 'SLITTER, LOTO';
  first.querySelector('[data-repeater-column="traineeMayFill"]').value = 'yes';
  fire(repeater.querySelector('[data-repeater-add]'), 'click');
  assert.equal(repeater.querySelectorAll('[data-repeater-row]').length, 2);
  fire(repeater.querySelectorAll('[data-repeater-remove]')[1], 'click');
  assert.equal(repeater.querySelectorAll('[data-repeater-row]').length, 1);
  modal.querySelector('#reason').value = 'Staffing plan approved for the slitter cell.';
  fire(modal.querySelector('[data-ok]'), 'click'); await flush(); await pending;
  assert.deepEqual(sent, { slots: 'Slitter operator | 2 | SLITTER, LOTO | yes' });
  assert.equal(modal.isConnected, false);
});

test('required repeater columns fail inline while preserving entered rows', async t => {
  const browser = installBrowser(t), field = { name: 'items', label: 'Items', type: 'repeater', minRows: 1, value: [], columns: [{ name: 'code', label: 'Code', required: true }] };
  const pending = formModal({ title: 'Items', fields: [field] }, async () => ({ message: 'Saved.' }));
  const modal = browser.document.querySelector('.modal'), repeater = modal.querySelector('#f-items');
  assert.equal(repeater.querySelectorAll('[data-repeater-row]').length, 1);
  fire(modal.querySelector('[data-ok]'), 'click'); await flush();
  assert.equal(modal.isConnected, true); assert.equal(repeater.querySelectorAll('[data-repeater-row]').length, 1);
  assert.match(modal.querySelector('[data-error]').textContent, /at least 1 entry/);
  fire(modal.querySelector('[data-cancel]'), 'click'); assert.equal(await pending, null);
});

test('formModal binds repeaters in the newest modal when an older modal remains open', async t => {
  const browser = installBrowser(t), stale = browser.document.createElement('div'); stale.className = 'modal'; browser.document.body.append(stale);
  const field = { name: 'items', label: 'Items', type: 'repeater', minRows: 0, columns: [{ name: 'code', label: 'Code' }] };
  const pending = formModal({ title: 'Items', fields: [field] }, async () => ({ message: 'Saved.' }));
  const modals = browser.document.querySelectorAll('.modal'), active = modals.at(-1), repeater = active.querySelector('#f-items');
  assert.equal(modals.length, 2);
  fire(repeater.querySelector('[data-repeater-add]'), 'click');
  assert.equal(repeater.querySelectorAll('[data-repeater-row]').length, 1);
  fire(active.querySelector('[data-cancel]'), 'click'); assert.equal(await pending, null);
  assert.equal(stale.isConnected, true);
});

test('searchable lookups display human labels and submit only their selected entity keys', t => {
  const browser = installBrowser(t), modal = browser.document.createElement('div');
  const field = { name: 'personId', label: 'Responsible person', type: 'lookup', value: 'person-2', required: true,
    options: [['person-1', 'Maria Chen · EMP-104'], ['person-2', 'Maria Chen · EMP-105']] };
  modal.innerHTML = fieldHtml(field); browser.document.body.append(modal);
  assert.equal(modal.querySelector('#f-personId').value, 'Maria Chen · EMP-105');
  assert.deepEqual(readFields(modal, [field]), { personId: 'person-2' });
  modal.querySelector('#f-personId').value = 'Maria Chen · EMP-104';
  assert.deepEqual(readFields(modal, [field]), { personId: 'person-1' });
  modal.querySelector('#f-personId').value = 'someone@example.invalid';
  assert.throws(() => readFields(modal, [field]), /choose a listed value/);
});
