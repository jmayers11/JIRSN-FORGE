import { esc } from './ui.js';
import { isServiceUnavailable } from './terminal-state.mjs';

// A resolved code is a transient preview. Selection and the consequential command are
// separate actions; every asynchronous result belongs to the context that requested it.
export function createIdentificationStage({ loadConfig, resolve, kinds, isCurrent = () => true,
  onChange = () => {}, onSelect = () => {}, onClear = () => {}, onUnavailable = () => {} }) {
  let generation = 0, disposed = false, resolved = null, selected = null;
  let config = null, busy = false, message = '';
  const current = token => !disposed && isCurrent() && token === generation;
  const notify = () => onChange({ resolved, selected, config, busy, message });
  const clear = () => {
    generation++; resolved = null; selected = null; busy = false; message = '';
    onClear(); notify();
  };
  return {
    get resolved() { return current(generation) ? resolved : null; },
    get selected() { return current(generation) ? selected : null; },
    clear,
    async resolve(code) {
      if (disposed || !isCurrent()) return null;
      clear();
      const token = generation;
      busy = true; message = 'Resolving…'; notify();
      try {
        const metadata = await loadConfig();
        if (!current(token)) return null;
        if (!metadata?.namespace || !Array.isArray(metadata.supportedKinds)
          || !Number.isInteger(metadata.maxCodeLength) || metadata.maxCodeLength < 1)
          throw Object.assign(new Error('Identification configuration is unavailable.'), { status: 503 });
        config = Object.freeze({ ...metadata });
        notify();
        const result = await resolve(code);
        if (!current(token)) return null;
        if (result?.namespace !== config.namespace || !metadata.supportedKinds.includes(result?.kind)
          || !kinds.includes(result?.kind)) throw Object.assign(new Error('This code identifies a different kind of subject.'), { status: 422 });
        resolved = Object.freeze({ ...result });
        busy = false; message = describeIdentification(resolved); notify();
        return resolved;
      } catch (error) {
        if (!current(token)) return null;
        busy = false; resolved = null; selected = null; message = error.message;
        onClear(); notify();
        if (isServiceUnavailable(error) && error.problem?.code !== 'identification_not_configured') onUnavailable(error);
        return null;
      }
    },
    select() {
      if (!current(generation) || busy || !resolved) return null;
      try {
        onSelect(resolved);
        selected = resolved;
        message = `${describeIdentification(resolved)} Selected; confirm the action separately.`;
        notify(); return selected;
      } catch (error) {
        selected = null; message = error.message; notify(); return null;
      }
    },
    dispose() { clear(); disposed = true; },
  };
}

export function describeIdentification(subject) {
  if (subject.kind === 'material') return `Material ${subject.partNumber}, lot ${subject.lotNumber}${subject.identityKind ? ` (${subject.identityKind})` : ''}. Recorded identity; stock eligibility is checked when you confirm.`;
  if (subject.kind === 'location') return `Location ${subject.locationCode} (${subject.status}).`;
  return `Order #${subject.orderNumber}${subject.kind === 'operation' ? `, operation ${subject.sequence}` : ''}: ${subject.partNumber}${subject.revision ? ` revision ${subject.revision}` : ''} (${subject.status}).`;
}

// Keeps manual/keyboard-wedge entry independent of badge identification and business commands.
// Mount callers own disposal and context checks (page, dialog and badge session).
export function mountIdentificationInput(element, { id, label, ...options }) {
  element.innerHTML = `<div class="field"><label for="${esc(id)}">${esc(label)}</label>
    <input id="${esc(id)}" class="input mono" type="text" autocomplete="off" spellcheck="false" placeholder="Scan or type a Forge code" aria-describedby="${esc(id)}-status">
    <div class="actions"><button class="btn" type="button" data-identify-resolve>Resolve code</button><button class="btn" type="button" data-identify-use disabled>Use this subject</button></div>
    <div class="faint" id="${esc(id)}-status" data-identify-status role="status" aria-live="polite" aria-atomic="true">Optional. Resolve and review the subject before selecting it.</div></div>`;
  const input = element.querySelector('input'), resolveButton = element.querySelector('[data-identify-resolve]');
  const useButton = element.querySelector('[data-identify-use]'), status = element.querySelector('[data-identify-status]');
  let keystrokeTimes = [], detectedMode = 'Manual', lastInputLength = 0;
  const stage = createIdentificationStage({ ...options, isCurrent: () => element.isConnected && (options.isCurrent?.() ?? true),
    onSelect: resolved => options.onSelect?.(resolved, detectedMode),
    onChange: state => {
      resolveButton.disabled = state.busy;
      useButton.disabled = !state.resolved || state.busy || !!state.selected;
      status.textContent = state.message || 'Optional. Resolve and review the subject before selecting it.';
      if (state.config) input.setAttribute('maxlength', state.config.maxCodeLength);
      options.onChange?.(state);
    } });
  const resolveCode = () => { if (input.value) void stage.resolve(input.value); };
  Object.defineProperty(stage, 'lastInputMode', { get: () => detectedMode });
  const onKey = event => {
    if (event.key === 'Enter') { event.preventDefault(); resolveCode(); return; }
    if (event.key.length !== 1) return;
    if (input.value.length === 0 || input.value.length < lastInputLength) { keystrokeTimes = []; detectedMode = 'Manual'; }
    const now = Date.now();
    keystrokeTimes.push(now);
    if (keystrokeTimes.length > 32) keystrokeTimes.shift();
    if (keystrokeTimes.length >= 4) {
      const span = keystrokeTimes.at(-1) - keystrokeTimes[0];
      detectedMode = span / (keystrokeTimes.length - 1) <= 25 ? 'KeyboardWedge' : 'Manual';
    }
  };
  const onInput = () => {
    if (!input.value || input.value.length < lastInputLength) { keystrokeTimes = []; detectedMode = 'Manual'; }
    lastInputLength = input.value.length;
    stage.clear();
  };
  const onUse = () => stage.select();
  input.addEventListener('keydown', onKey);
  input.addEventListener('input', onInput);
  resolveButton.addEventListener('click', resolveCode);
  useButton.addEventListener('click', onUse);
  return { stage, get detectedMode() { return detectedMode; }, dispose: () => {
    stage.dispose(); input.value = '';
    input.removeEventListener('keydown', onKey); input.removeEventListener('input', onInput);
    resolveButton.removeEventListener('click', resolveCode); useButton.removeEventListener('click', onUse);
  } };
}

export function materialPickSelection(subject, task) {
  if (subject?.kind !== 'material' || subject.partNumber !== task.partNumber
    || subject.lotNumber !== task.lotNumber) throw new Error('The material must match this task’s part and lot.');
  return Object.freeze({ partNumber: subject.partNumber, lotNumber: subject.lotNumber });
}

export function workQueueSelection(subject, queue) {
  const choices = queue.filter(work => work.orderNumber === subject.orderNumber
    && (subject.kind === 'order' || work.operation.sequence === subject.sequence));
  if (!['order', 'operation'].includes(subject.kind) || !choices.length)
    throw new Error('That work is not in the current operator’s available queue.');
  return choices;
}
