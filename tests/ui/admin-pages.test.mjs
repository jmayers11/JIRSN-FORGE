import assert from 'node:assert/strict';
import test from 'node:test';
import { accessPage, permissionList, thresholdBody, peopleRows } from '../../src/Forge.Host/wwwroot/js/pages/access.js';
import { safetyRulesPage, parseSlots, formatSlots, ruleBody, ruleActions } from '../../src/Forge.Host/wwwroot/js/pages/safety-rules.js';
import { appendWorkforceAdmin, personActions, unavailabilityBody, assignmentBody } from '../../src/Forge.Host/wwwroot/js/pages/workforce-admin.js';
import { ROLE_NAMES, roleTitle } from '../../src/Forge.Host/wwwroot/js/roles.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

const ana = { personId: '11111111-1111-4111-8111-111111111111', idpSubject: 'E100', displayName: 'Ana Admin', isActive: true, markings: 'None', roles: ['SecurityAdministrator'], programs: [] };
const pat = { personId: '22222222-2222-4222-8222-222222222222', idpSubject: 'E200', displayName: 'Pat Planner', isActive: true, markings: 'Proprietary', roles: ['Planner'], programs: ['33333333-3333-4333-8333-333333333333'] };

function wire(t, route) {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const previous = { subject: session.subject, token: session.token }; session.subject = 'admin-test'; session.token = null; t.after(() => Object.assign(session, previous));
  const calls = [];
  globalThis.fetch = (path, options = {}) => { const body = options.body ? JSON.parse(options.body) : null; calls.push({ path, body }); return Promise.resolve(route(path, body) ?? response(body ? { succeeded: true, message: 'Done.' } : [])); };
  const submit = async values => {
    await flush(); const modal = browser.document.querySelector('.modal');
    for (const [name, value] of Object.entries(values)) { const el = modal.querySelector(`#f-${name}`); if (el.type === 'checkbox') el.checked = value; else el.value = value; }
    modal.querySelector('#reason').value = 'Requested by plant manager'; fire(modal.querySelector('[data-ok]'), 'click'); await flush(); await flush();
  };
  return { ...browser, page, calls, submit };
}

test('role catalog covers server roles added for audits', () => {
  for (const role of ['AuditPlanner', 'InternalAuditor', 'AuditApprover', 'SecurityAdministrator']) assert.ok(ROLE_NAMES.includes(role));
  assert.equal(roleTitle('InternalAuditor'), 'Internal Auditor'); assert.equal(roleTitle('CustomRole'), 'CustomRole');
});

test('permission lists and thresholds are validated before they are sent', () => {
  assert.deepEqual(permissionList('quality.read, Quality.Read\nworkforce.badges.manage'), ['quality.read', 'workforce.badges.manage']);
  assert.throws(() => permissionList(''), /at least one/); assert.throws(() => permissionList('quality read!'), /not a permission/);
  assert.deepEqual(thresholdBody({ maximumAmount: null, currency: 'usd' }), { maximumAmount: null, currency: null });
  assert.deepEqual(thresholdBody({ maximumAmount: 5000, currency: 'usd' }), { maximumAmount: 5000, currency: 'USD' });
  assert.throws(() => thresholdBody({ maximumAmount: 10, currency: 'US' }), /three-letter/);
});

test('people rows show revocable roles and programs only to access managers', () => {
  const programs = [{ programId: pat.programs[0], name: 'Falcon' }];
  assert.match(peopleRows([pat], programs, true), /data-revoke-role="Planner"/); assert.match(peopleRows([pat], programs, true), /Falcon/);
  assert.doesNotMatch(peopleRows([pat], programs, false), /data-revoke|data-grant/);
});

test('access page grants a role with an optional expiry and revokes a program', async t => {
  const { page, calls, submit } = wire(t, path => path.endsWith('/kernel/people') ? response([ana, pat]) : path.endsWith('/api/programs/') ? response([{ programId: pat.programs[0], name: 'Falcon' }]) : null);
  const controller = await accessPage(page, { me: { personId: ana.personId, permissions: ['kernel.access.manage'] } }); t.after(controller.dispose);
  fire(page.querySelector(`[data-grant-role="${pat.personId}"]`), 'click');
  await submit({ role: 'Inspector', expiresAt: '' });
  assert.deepEqual(calls.find(c => c.body && c.path.endsWith(`/people/${pat.personId}/roles`)).body, { role: 'Inspector', expiresAt: null, reason: 'Requested by plant manager' });
  fire(page.querySelector(`[data-revoke-program="${pat.programs[0]}"]`), 'click');
  await submit({});
  assert.ok(calls.some(c => c.body && c.path.endsWith(`/people/${pat.personId}/programs/${pat.programs[0]}/revoke`)));
});

test('access page is read-only without access management', async t => {
  const { page } = wire(t, path => path.endsWith('/kernel/people') ? response([pat]) : null);
  const controller = await accessPage(page, { me: { personId: pat.personId, permissions: ['kernel.access.read'] } }); t.after(controller.dispose);
  assert.match(page.innerHTML, /but not change it/); assert.doesNotMatch(page.innerHTML, /data-grant|id="register"|Role policy/);
});

test('safety rule slots round-trip and the next lifecycle action is offered', () => {
  const slots = parseSlots('Crane operator | 1 | CRANE, RIGGING | no\nSpotter | 2 | SPOT | yes');
  assert.deepEqual(slots[1], { name: 'Spotter', count: 2, satisfiedBy: ['SPOT'], traineeMayFill: true });
  assert.deepEqual(parseSlots(formatSlots(slots)), slots);
  assert.throws(() => parseSlots('Spotter | two | SPOT'), /Slot line 1/);
  assert.deepEqual(ruleBody({ code: 'CRANE', title: 'Crane lifts', scopeKind: 'Machine', scopeCode: 'CR-1', activityCodes: 'lift, rig', minimumPhysicalPeople: 2, prohibitLoneWork: true, slots: '' }).activityCodes, ['LIFT', 'RIG']);
  const published = { code: 'CRANE', version: 1, status: 'Published' }, draft = { code: 'CRANE', version: 2, status: 'Draft' };
  assert.deepEqual(ruleActions(draft, [published, draft], { draft: true, approve: true }), ['publish', 'discard']);
  assert.deepEqual(ruleActions(published, [published, draft], { draft: true, approve: true }), ['retire']);
  assert.deepEqual(ruleActions(published, [published], { draft: true, approve: false }), ['revise']);
});

test('safety rules page publishes a draft version', async t => {
  const rule = { code: 'CRANE', version: 2, title: 'Crane lifts', scopeKind: 'Machine', scopeCode: 'CR-1', activityCodes: [], minimumPhysicalPeople: 2, prohibitLoneWork: true, slots: [], status: 'Draft', authoredBy: 'person:x', approvedBy: null };
  const { page, calls, submit } = wire(t, path => path.endsWith('/api/safety-rules/') ? response([rule]) : null);
  const controller = await safetyRulesPage(page, { me: { personId: 'y', permissions: ['safety.rules.approve', 'safety.rules.read'] } }); t.after(controller.dispose);
  assert.doesNotMatch(page.innerHTML, /data-rule-act="discard"/);
  fire(page.querySelector('[data-rule-act="publish"]'), 'click'); await submit({});
  assert.ok(calls.some(c => c.body && c.path.endsWith('/api/safety-rules/CRANE/versions/2/publish')));
});

test('workforce actions follow permissions and validate windows and assignments', () => {
  assert.deepEqual(personActions({ permissions: ['workforce.supervise'] }).map(([a]) => a), ['assign', 'end-assignment', 'presence', 'departure', 'unavailable']);
  assert.deepEqual(personActions({ permissions: ['competency.certify', 'workforce.badges.manage'] }).map(([a]) => a), ['badge', 'revoke-badge', 'certify']);
  assert.throws(() => unavailabilityBody({ kind: 'CallOut', startsAt: '2026-10-15T10:00', endsAt: '2026-10-15T09:00' }), /later end/);
  assert.throws(() => assignmentBody({ activityCode: 'WELD' }), /safety area, work center or machine/);
  assert.equal(assignmentBody({ workCenterCode: 'WELD-1', activityCode: 'WELD', asTrainee: true }).asTrainee, true);
});

test('managing a person certifies a qualification with evidence and suspends a held one', async t => {
  const { page, calls, submit } = wire(t, path => path.endsWith('/kernel/directory') ? response([pat])
    : path.endsWith('/qualification-types') ? response([{ code: 'WELD', name: 'Welding', kind: 'Process', validityDays: 365, prerequisites: [] }])
    : path.endsWith(`/competency/people/${pat.personId}`) ? response([{ personId: pat.personId, code: 'WELD', status: 'Qualified', certifiedAt: '2026-01-01T00:00:00Z', expiresAt: null }])
    : path.endsWith('/api/resources/') ? response({ workCenters: [{ code: 'WELD-1' }] }) : null);
  const section = await appendWorkforceAdmin(page, { personId: ana.personId, permissions: ['competency.certify', 'workforce.supervise'] });
  const select = section.querySelector('#wf-person'); select.value = pat.personId; fire(select, 'change'); await flush(); await flush();
  assert.match(section.innerHTML, /Welding/); assert.match(section.innerHTML, /data-qual="suspensions"/);
  fire(section.querySelector('[data-wf="certify"]'), 'click'); await submit({ code: 'WELD', evidence: 'Weld coupon 44 passed' });
  assert.deepEqual(calls.find(c => c.body && c.path.endsWith('/certifications')).body, { code: 'WELD', evidence: 'Weld coupon 44 passed', reason: 'Requested by plant manager' });
  fire(section.querySelector('[data-qual="suspensions"]'), 'click'); await submit({});
  assert.deepEqual(calls.find(c => c.body && c.path.endsWith('/suspensions')).body, { code: 'WELD', reason: 'Requested by plant manager' });
});
