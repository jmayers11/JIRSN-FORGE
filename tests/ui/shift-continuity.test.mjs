import assert from 'node:assert/strict';
import test from 'node:test';
import { shiftContinuityPage } from '../../src/Forge.Host/wwwroot/js/pages/shift-continuity.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, fire, flush } from './browser-harness.mjs';

const resources = { workCenters: [{ code: 'CUT', name: 'Cutting', safetyAreaCode: 'AREA' }], shifts: [{ code: 'DAY', name: 'Day', start: '07:00', durationMinutes: 480 }], workCenterShifts: [{ workCenterCode: 'CUT', shiftCode: 'DAY' }], sites: [], safetyAreas: [] };
const handoff = { handoffId: 42, shiftCode: 'DAY', workCenterCode: 'CUT', shiftFrom: '2026-10-18T07:00:00Z', shiftUntil: '2026-10-18T15:00:00Z', reviewedAt: '2026-10-18T15:10:00Z', createdBy: 'person:outgoing', context: 'Tool change completed', snapshot: { asOf: '2026-10-18T15:00:00Z', production: { reportCount: 0, goodQuantity: 0, scrapQuantity: 0, openOperations: 0 }, unknowns: [] }, facts: [], checklist: [] };
const detail = { handoff, acknowledgments: [{ personId: 'incoming-id', acknowledgedAt: '2026-10-18T15:15:00Z' }], };

async function harness(t, { directory = [{ personId: 'incoming-id', displayName: 'Incoming Operator' }], denyDirectory = false } = {}) {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const previous = { subject: session.subject, token: session.token }; session.subject = 'shift-continuity-test'; session.token = null; t.after(() => Object.assign(session, previous));
  const calls = [];
  globalThis.fetch = (path, options = {}) => {
    const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body });
    if (path === '/api/resources/?includeRetired=false') return Promise.resolve(response(resources));
    if (path === '/api/kernel/directory') return Promise.resolve(denyDirectory ? response({ detail: 'Directory unavailable.' }, 503) : response(directory));
    if (path.includes('/handoffs?')) return Promise.resolve(response([handoff]));
    if (path === '/api/execution/shifts/closeouts?workCenter=CUT&limit=30' || path === '/api/execution/shifts/checklist?workCenter=CUT') return Promise.resolve(response([]));
    if (path === '/api/execution/shifts/handoffs/42') return Promise.resolve(response(detail));
    if (path === '/api/execution/shifts/handoffs/42/actions') return Promise.resolve(response([]));
    return Promise.resolve(response({ message: 'Recorded.' }));
  };
  const controller = await shiftContinuityPage(page, { me: { personId: 'supervisor-id', permissions: ['workforce.supervise', 'execution.shopfloor.record'] } });
  t.after(controller.dispose);
  return { ...browser, page, calls, open: async () => { fire(page.querySelector('[data-open="42"]'), 'click'); await flush(); } };
}

test('shift actions use directory assignees and acknowledgments show human names', async t => {
  const h = await harness(t); await h.open();
  const assignee = h.page.querySelector('[name="assignee"]');
  assert.ok(assignee, JSON.stringify({ html: h.page.innerHTML, calls: h.calls.map(call => call.path) }));
  assert.equal(assignee.tagName, 'select'); assert.match(assignee.textContent, /Incoming Operator/); assert.match(assignee.textContent, /incoming-id/);
  assert.match(h.page.querySelector('#handoff-detail').textContent, /Incoming Operator/);
  assert.doesNotMatch(h.page.querySelector('#handoff-detail').textContent, /No acknowledgment recorded/);
});

test('shift action assignment falls back to person ID text input when directory is unavailable', async t => {
  const h = await harness(t, { denyDirectory: true }); await h.open();
  assert.equal(h.page.querySelector('[name="assignee"]').tagName, 'input');
  assert.equal(h.page.querySelector('[name="assignee"]').getAttribute('placeholder'), 'Assignee person ID');
});
