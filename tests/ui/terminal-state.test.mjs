import assert from 'node:assert/strict';
import test from 'node:test';
import { isServiceUnavailable, terminalStateAfterFailure, createTerminalSession } from '../../src/Forge.Host/wwwroot/js/terminal-state.mjs';

test('locks the shared terminal when fetch fails or Forge returns a server error', () => {
  assert.equal(isServiceUnavailable(new TypeError('Failed to fetch')), true);
  assert.equal(isServiceUnavailable({ status: 503 }), true);
  assert.equal(isServiceUnavailable({ status: 500 }), true);
});

test('keeps ordinary authorization and validation responses actionable', () => {
  assert.equal(isServiceUnavailable({ status: 401 }), false);
  assert.equal(isServiceUnavailable({ status: 403 }), false);
  assert.equal(isServiceUnavailable({ status: 422 }), false);
});

test('an uncertain server outcome clears the selected operator and locks the terminal', () => {
  const state = { badge: 'B-17', person: { displayName: 'Operator' }, locked: false };
  assert.deepEqual(terminalStateAfterFailure(new TypeError('Failed to fetch'), state), { badge: null, person: null, locked: true });
  assert.deepEqual(terminalStateAfterFailure({ status: 503 }, state), { badge: null, person: null, locked: true });
});

test('a validation response keeps the operator so they can correct the input', () => {
  const state = { badge: 'B-17', person: { displayName: 'Operator' }, locked: false };
  assert.deepEqual(terminalStateAfterFailure({ status: 422 }, state), state);
});

test('a lock invalidates both a pending scan and a pending action refresh', () => {
  const terminal = createTerminalSession();
  const scan = terminal.beginIdentification('B-17');
  terminal.clear();
  assert.equal(terminal.acceptIdentification(scan, { displayName: 'Expired operator' }), false);
  const next = terminal.beginIdentification('B-18');
  terminal.acceptIdentification(next, { displayName: 'Current operator' });
  const action = terminal.beginAction();
  terminal.clear();
  assert.equal(terminal.isCurrent(action), false);
  assert.equal(terminal.acceptIdentification(action, { displayName: 'Expired refresh' }), false);
  assert.equal(terminal.badge, null);
  assert.equal(terminal.person, null);
});

test('the most recent scan wins even when identify responses arrive out of order', () => {
  const terminal = createTerminalSession();
  const old = terminal.beginIdentification('B-17');
  const current = terminal.beginIdentification('B-18');
  assert.equal(terminal.acceptIdentification(current, { displayName: 'Current operator' }), true);
  assert.equal(terminal.acceptIdentification(old, { displayName: 'Previous operator' }), false);
  assert.equal(terminal.badge, 'B-18');
  assert.equal(terminal.person.displayName, 'Current operator');
});

test('an action owns an immutable badge snapshot and cannot overlap another action', () => {
  const terminal = createTerminalSession();
  terminal.acceptIdentification(terminal.beginIdentification('B-17'), { displayName: 'Operator' });
  const action = terminal.beginAction();
  assert.equal(action.badge, 'B-17');
  assert.equal(Object.isFrozen(action), true);
  assert.equal(terminal.busy, true);
  assert.equal(terminal.beginAction(), null);
  terminal.endAction(action);
  assert.equal(terminal.busy, false);
  assert.ok(terminal.beginAction());
});

test('finishing an earlier action cannot release the next operator\'s pending action', () => {
  const terminal = createTerminalSession();
  terminal.acceptIdentification(terminal.beginIdentification('B-17'), { displayName: 'First operator' });
  const first = terminal.beginAction();
  terminal.acceptIdentification(terminal.beginIdentification('B-18'), { displayName: 'Second operator' });
  const second = terminal.beginAction();
  terminal.endAction(first);
  assert.equal(terminal.busy, true);
  assert.equal(terminal.beginAction(), null);
  terminal.endAction(second);
  assert.equal(terminal.busy, false);
});

test('disposal clears the operator and permanently rejects requests', () => {
  const terminal = createTerminalSession();
  terminal.acceptIdentification(terminal.beginIdentification('B-17'), { displayName: 'Operator' });
  const before = terminal.capture();
  terminal.dispose();
  assert.equal(terminal.isCurrent(before), false);
  assert.equal(terminal.badge, null);
  assert.equal(terminal.person, null);
  assert.equal(terminal.beginAction(), null);
  assert.equal(terminal.acceptIdentification(terminal.beginIdentification('B-18'), {}), false);
});
