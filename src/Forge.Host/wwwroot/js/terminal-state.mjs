// Shop-floor actions must stop when the API cannot confirm their outcome. No offline queue exists.
export function isServiceUnavailable(error) {
  return !Number.isInteger(error?.status) || error.status >= 500;
}

export function terminalStateAfterFailure(error, state) {
  if (!isServiceUnavailable(error)) return { ...state, locked: false };
  return { ...state, badge: null, person: null, locked: true };
}

// A request belongs to the badge session that started it. A lock, new scan or page disposal
// invalidates every earlier request, even if it completes successfully afterwards.
export function createTerminalSession() {
  let revision = 0;
  let badge = null;
  let person = null;
  let pending = null;
  let disposed = false;
  const capture = () => Object.freeze({ revision, badge });
  const isCurrent = token => !disposed && token?.revision === revision;
  const clear = () => { revision++; badge = null; person = null; pending = null; };
  return {
    get badge() { return badge; },
    get person() { return person; },
    get busy() { return pending !== null; },
    get disposed() { return disposed; },
    capture,
    isCurrent,
    clear,
    beginIdentification(value) {
      clear();
      return Object.freeze({ revision, badge: value });
    },
    acceptIdentification(token, result) {
      if (!isCurrent(token)) return false;
      badge = token.badge;
      person = result;
      return true;
    },
    beginAction(token = capture()) {
      if (!isCurrent(token) || !token.badge || !person || pending) return null;
      pending = Object.freeze({ ...token });
      return pending;
    },
    endAction(token) { if (pending === token) pending = null; },
    dispose() { clear(); disposed = true; },
  };
}
