import { command, get, tryGet, session as signIn } from './api.js';
import { esc, $, $$, toast } from './ui.js';
import { isServiceUnavailable } from './terminal-state.mjs';
import { mountIdentificationInput, describeIdentification } from './identification-input.mjs';

const API = '/api/identification';
const MODES = ['Manual', 'KeyboardWedge', 'Camera', 'ConfiguredAdapter'];
const words = value => String(value).replace(/([a-z])([A-Z])/g, '$1 $2');
const key = value => encodeURIComponent(value);

export function operationIdentificationCode(config, orderNumber, sequence) {
  const positive = value => /^[1-9][0-9]*$/.test(String(value)) && (typeof value !== 'number' || Number.isSafeInteger(value));
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(config?.namespace ?? '') || !positive(orderNumber) || !positive(sequence)
    || BigInt(orderNumber) > 9223372036854775807n || BigInt(sequence) > 2147483647n) throw new Error('This operation does not have a configured canonical identifier.');
  return `forge:v1:${config.namespace}:operation:${orderNumber}:${sequence}`;
}

// The server owns stages and accepted provenance. This controller supplies explicit immutable
// request headers only when the caller captures a current snapshot for one business command.
export async function mountScanSession(element, { operationCode, operatorPersonId, machineCode = null, badge = null,
  isCurrent = () => true, canMutate = () => true, onUnavailable = () => {}, onChange = () => {}, drivers = {} }) {
  const auth = { subject: signIn.subject, token: signIn.token };
  let disposed = false, generation = 0, view = null, busy = false, preview = null, previewCode = null, widget = null, expiry = null, cameraStream = null, captureTimer = null, unsubscribe = null;
  let config = null, scanners = null, requirements = null, captureEvidence = null, verified = false, captureEpoch = 0;
  const current = token => !disposed && element.isConnected && isCurrent() && auth.subject === signIn.subject && auth.token === signIn.token && token === generation;
  const notify = () => onChange(view);
  const clearCapture = () => { captureEpoch++; clearTimeout(captureTimer); captureTimer = null; cameraStream?.getTracks().forEach(track => track.stop()); cameraStream = null; unsubscribe?.(); unsubscribe = null; };
  const clearPreview = () => { preview = null; previewCode = null; };
  const active = () => verified && current(generation) && view?.state === 'Active' && Date.parse(view.expiresAt) > Date.now();
  const scheduleExpiry = () => { clearTimeout(expiry); if (view?.state === 'Active') expiry = setTimeout(() => { if (current(generation)) { view = { ...view, state: 'Expired' }; clearCapture(); clearPreview(); render(); notify(); } }, Math.max(0, Date.parse(view.expiresAt) - Date.now())); };
  const accept = result => {
    if (result.operatorPersonId !== operatorPersonId || result.operationCode !== operationCode || (result.machineCode ?? null) !== machineCode) throw new Error('The server session does not belong to this operator, operation and selected machine.');
    view = result; verified = true; clearPreview(); scheduleExpiry(); render(); notify();
  };

  async function refresh() {
    if (!view || busy || !current(generation)) return;
    busy = true;
    const token = ++generation; verified = false; clearCapture(); widget?.dispose(); widget = null;
    try { const result = await get(`${API}/scan-sessions/${key(view.id)}`); if (current(token)) accept(result); }
    catch (error) { if (current(token)) { view = null; clearPreview(); render(error.message); notify(); if (isServiceUnavailable(error)) onUnavailable(error); } }
    finally { busy = false; }
  }

  async function invalidate(reason = 'Operator cleared the staged work context') {
    const old = view; generation++; verified = false; clearTimeout(expiry); clearCapture(); widget?.dispose(); widget = null; view = null; clearPreview(); notify();
    if (!disposed && element.isConnected) render();
    if (!old || auth.subject !== signIn.subject || auth.token !== signIn.token) return;
    try { await command(`${API}/scan-sessions/${key(old.id)}/invalidate`, { expectedVersion: old.version }, reason); }
    catch { /* Local context is cleared regardless; server expiry/current-context checks remain authoritative. No retry queue. */ }
  }

  async function start(reason) {
    if (!current(generation) || !canMutate() || busy) return;
    if (!config) { toast('Identification must be configured before a server-owned staging session can begin.', true); return; }
    if (!reason?.trim()) { toast('Give a reason for beginning this staged work context.', true); return; }
    if (!requirements?.adopted) { toast('No effective work-staging policy has been confirmed for this operation. Existing optional work is allowed only when the requirements summary explicitly reports no historically effective adoption.', true); return; }
    busy = true; const token = ++generation;
    try {
      const previous = await get(`${API}/scan-sessions/current`);
      if (!current(token)) return;
      if (!Object.hasOwn(previous, 'id') || (previous.id !== null && typeof previous.id !== 'string')) throw new Error('Forge did not confirm this authenticated channel’s current staging pointer.');
      const result = await command(`${API}/scan-sessions`, { operationCode, machineCode, badge, previousSessionId: previous.id }, reason.trim());
      if (!current(token)) return;
      const next = await get(`${API}/scan-sessions/${key(result.entityId)}`);
      if (current(token)) accept(next);
    } catch (error) {
      if (!current(token)) return;
      view = null; clearPreview(); render(error.message); notify();
      if (isServiceUnavailable(error) && error.problem?.code !== 'qms_policy_not_adopted' && error.problem?.code !== 'scan_session_not_configured') onUnavailable(error);
    } finally { busy = false; }
  }

  async function stageReference(reason) {
    if (!active() || !canMutate() || busy) return;
    if (!preview || !previewCode) { toast('Resolve, review and select the reference before recording it.', true); return; }
    if (!reason?.trim()) { toast('Give a reason for recording this reference.', true); return; }
    const mode = $('[data-stage-mode]', element).value, adapterId = $('[data-stage-adapter]', element).value || null, symbology = $('[data-stage-symbology]', element).value || null;
    if (!MODES.includes(mode)) { toast('Choose the actual declared capture method.', true); return; }
    if (mode === 'Manual' && view.manualAllowed === false) { toast('This adopted operation rule does not allow manual selection.', true); return; }
    if (mode === 'ConfiguredAdapter' && (!(scanners?.adapters ?? []).some(adapter => adapter.enabled && adapter.id === adapterId && drivers[adapter.id]) || captureEvidence?.mode !== mode || captureEvidence?.adapterId !== adapterId || captureEvidence?.code !== previewCode)) { toast('Capture this code through the configured adapter before declaring adapter input.', true); return; }
    if (mode === 'Camera' && (captureEvidence?.mode !== mode || captureEvidence?.code !== previewCode)) { toast('Capture the code with the camera before declaring camera input.', true); return; }
    const body = { expectedVersion: view.version, code: previewCode, inputMode: mode, adapterId: mode === 'ConfiguredAdapter' ? adapterId : null, symbology };
    busy = true; verified = false; const token = ++generation, id = view.id;
    clearPreview(); widget?.dispose(); widget = null;
    try {
      await command(`${API}/scan-sessions/${key(id)}/stages`, body, reason.trim());
      if (!current(token)) return;
      const next = await get(`${API}/scan-sessions/${key(id)}`);
      if (current(token)) accept(next);
    } catch (error) {
      if (!current(token)) return;
      render(error.message);
      if (isServiceUnavailable(error)) { view = null; clearPreview(); render(error.message); notify(); onUnavailable(error); }
    } finally { busy = false; }
  }

  function capture(code, mode, symbology = null, adapterId = null) {
    if (!active() || !canMutate() || typeof code !== 'string' || code.length > config.maxCodeLength) return;
    const input = $('[data-stage-identification]', element)?.querySelector('input');
    if (!input) return;
    widget?.stage.clear(); input.value = code;
    captureEvidence = { code, mode, adapterId };
    $('[data-stage-mode]', element).value = mode;
    if (symbology) $('[data-stage-symbology]', element).value = symbology;
  }

  function clearCapturedReference() {
    captureEvidence = null; widget?.stage.clear(); clearPreview();
    const input = $('[data-stage-identification]', element)?.querySelector('input');
    if (input) input.value = '';
  }

  async function beginCamera() {
    if (!active() || !canMutate()) return;
    if (!globalThis.navigator?.mediaDevices?.getUserMedia || typeof globalThis.BarcodeDetector !== 'function') { toast('Camera decoding is unavailable in this browser. Use an allowed input method.', true); return; }
    clearCapture(); clearCapturedReference(); const token = generation, captureToken = captureEpoch; let ownedStream = null;
    const captureCurrent = () => current(token) && captureToken === captureEpoch;
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false });
      ownedStream = stream;
      if (!captureCurrent()) { stream.getTracks().forEach(track => track.stop()); return; }
      cameraStream = stream; const video = $('[data-stage-camera]', element); video.srcObject = stream; await video.play();
      const supported = await BarcodeDetector.getSupportedFormats(), formats = ['code_128', 'qr_code', 'data_matrix'].filter(format => supported.includes(format));
      if (!formats.length) throw new Error('No supported camera barcode format is available.');
      const detector = new BarcodeDetector({ formats });
      const read = async () => {
        if (!captureCurrent() || !cameraStream) return;
        const results = await detector.detect(video);
        if (!captureCurrent() || !cameraStream) return;
        if (results.length) { capture(results[0].rawValue, 'Camera', { code_128: 'Code128', qr_code: 'QR', data_matrix: 'DataMatrix' }[results[0].format] ?? null); return; }
        captureTimer = setTimeout(() => { void read().catch(error => { if (captureCurrent()) { clearCapture(); toast(error.message, true); } }); }, 150);
      };
      await read();
    } catch (error) { if (captureCurrent()) { clearCapture(); toast(error.message, true); } else ownedStream?.getTracks().forEach(track => track.stop()); }
  }

  function beginAdapter() {
    if (!active() || !canMutate()) return;
    const id = $('[data-stage-adapter]', element).value, metadata = (scanners?.adapters ?? []).find(adapter => adapter.id === id && adapter.enabled), driver = drivers[id];
    if (!metadata || typeof driver?.subscribe !== 'function') { toast('The selected adapter driver is unavailable. No hardware capture is inferred.', true); return; }
    clearCapture(); clearCapturedReference(); const token = generation, captureToken = captureEpoch;
    unsubscribe = driver.subscribe(event => { if (current(token) && captureToken === captureEpoch) capture(event.code, 'ConfiguredAdapter', event.symbology, id); });
  }

  function render(error = '') {
    if (!current(generation)) return;
    clearCapture(); widget?.dispose(); widget = null;
    const sessionActive = active();
    element.innerHTML = `<header><h2>Governed work staging</h2></header><div class="faint">Stages belong to the current verified operator and operation. A declared capture method or device is not authentication or physical capture proof.</div>
      ${error ? `<div class="notice bad" role="alert">${esc(error)}</div>` : ''}${requirements?.adopted === false ? '<div class="notice">The current operation summary reports no historically effective adoption. Optional legacy work remains available; staging does not invent a policy.</div>' : ''}
      ${view ? `<div class="notice">${esc(view.state)} · session version ${view.version}${view.machineCode ? ` · selected machine ${esc(view.machineCode)}` : ''} · expires ${esc(view.expiresAt)} · policy ${esc(view.policy.code)} revision ${view.policy.revision}</div>${!verified ? '<div class="notice warn">Refresh this recorded session before further use. Its current server version has not been confirmed.</div>' : ''}<div class="toolbar"><button class="btn small" data-session-refresh>Refresh staging</button><button class="btn small" data-session-clear>Clear staged session</button></div>
      <h3>Required references</h3><ul>${(view.requiredKinds ?? []).map(kind => `<li>${esc(words(kind))}: ${(view.selections ?? []).some(selection => selection.kind.toLowerCase() === kind.toLowerCase()) ? 'recorded' : 'still required'}</li>`).join('')}</ul>
      <h3>Recorded stages</h3>${(view.selections ?? []).length ? `<ul>${view.selections.map(selection => `<li>${esc(words(selection.kind))} · ${esc(selection.subjectCode)} · declared ${esc(words(selection.inputMode))}${selection.adapterId ? ` · adapter ${esc(selection.adapterId)}` : ''}</li>`).join('')}</ul>` : '<div class="faint">No references recorded.</div>'}
      <h3>Accepted authoritative commands</h3>${(view.accepted ?? []).length ? `<ul>${view.accepted.map((entry, index) => `<li>${esc(entry.commandType)} · ${esc(entry.entityType)} ${esc(entry.entityId)} · audit ${esc(entry.auditId)} · correlation ${esc(entry.correlationId)} · ${esc(entry.recordedAt)} <button class="btn small" data-acceptance-lookup="${index}" data-acceptance-id="${esc(entry.acceptanceId)}">Read accepted record</button><div data-acceptance-result="${index}" aria-live="polite"></div></li>`).join('')}</ul>` : '<div class="faint">No production or movement command has been accepted through this session.</div>'}` : `<div class="field"><label for="session-start-reason">Why this work is being staged</label><input class="input" id="session-start-reason" data-session-reason autocomplete="off" value="Stage operation references at point of use"></div><div class="actions" aria-label="Suggested staging reasons"><button class="btn small" type="button" data-reason-preset="session-start-reason" data-reason="Stage operation references at point of use">Use standard reason</button><button class="btn small" type="button" data-reason-preset="session-start-reason" data-reason="Verify references before starting work">Verify before work</button></div><button class="btn" data-session-start>Begin server-owned staging</button>`}
      ${sessionActive ? `<div data-stage-identification></div><div data-stage-preview role="status" aria-live="polite"></div>
      <div class="field"><label for="session-input-mode">Declared capture method</label><select class="select" id="session-input-mode" data-stage-mode><option value="">— choose —</option>${MODES.map(mode => `<option value="${mode}">${esc(words(mode))}</option>`).join('')}</select></div>
      <div class="field"><label for="session-adapter">Configured adapter</label><select class="select" id="session-adapter" data-stage-adapter><option value="">— none —</option>${(scanners?.adapters ?? []).filter(adapter => adapter.enabled).map(adapter => `<option value="${esc(adapter.id)}">${esc(adapter.displayName)} — ${esc(adapter.availability)}</option>`).join('')}</select></div>
      <div class="field"><label for="session-symbology">Reported symbology (optional)</label><select class="select" id="session-symbology" data-stage-symbology><option value="">— unknown —</option>${['Code128', 'QR', 'DataMatrix', 'GS1DataMatrix'].map(value => `<option>${value}</option>`).join('')}</select></div>
      <div class="toolbar"><button class="btn small" data-stage-use-camera>Enable camera capture</button><button class="btn small" data-stage-use-adapter>Enable configured adapter</button><button class="btn small" data-stage-stop-capture>Stop capture</button></div><video data-stage-camera muted playsinline style="max-width:100%"></video>
      <div class="field"><label for="stage-reason">Reason for recording this reference</label><input class="input" id="stage-reason" data-stage-reason autocomplete="off" value="Verified reference at work center"></div><div class="actions" aria-label="Suggested reference reasons"><button class="btn small" type="button" data-reason-preset="stage-reason" data-reason="Verified reference at work center">Use standard reason</button><button class="btn small" type="button" data-reason-preset="stage-reason" data-reason="Confirmed reference matches this operation">Matches operation</button></div><button class="btn" data-session-stage>Record reviewed reference</button>` : ''}`;
    $$('[data-reason-preset]', element).forEach(button => button.addEventListener('click', () => {
      const target = $('#' + button.dataset.reasonPreset, element);
      if (!target) return;
      target.value = button.dataset.reason; target.focus();
    }));
    $('[data-session-start]', element)?.addEventListener('click', () => start($('[data-session-reason]', element).value));
    $('[data-session-refresh]', element)?.addEventListener('click', refresh);
    $('[data-session-clear]', element)?.addEventListener('click', () => { void invalidate(); });
    $$('[data-acceptance-lookup]', element).forEach(button => button.addEventListener('click', async () => {
      const token = generation, id = button.dataset.acceptanceId, target = $(`[data-acceptance-result="${button.dataset.acceptanceLookup}"]`, element);
      if (!id || !target || button.disabled) return;
      button.disabled = true; target.innerHTML = '<span class="muted">Reading authorized acceptance evidence…</span>';
      const result = await tryGet(`${API}/scan-acceptances/${key(id)}`);
      if (!current(token) || !target.isConnected) return;
      button.disabled = false;
      if (result.error) { target.innerHTML = `<div class="notice warn">${esc(result.error.message)}</div>`; return; }
      const accepted = result.data.entry;
      target.innerHTML = `<div class="notice">Accepted record · session ${esc(result.data.sessionId)} · operator ${esc(accepted.operatorPersonId)} · ${esc(accepted.actor)} · ${esc(accepted.recordedAt)}</div>
        <div class="muted">Event IDs: ${esc((accepted.eventIds ?? []).join(', ') || 'none returned')} · policy fingerprint ${esc(accepted.policyFingerprint)}</div>
        ${(accepted.selections ?? []).length ? `<ul>${accepted.selections.map(selection => `<li>${esc(words(selection.kind))}: ${esc(selection.subjectCode)} · source fingerprint ${esc(selection.sourceFingerprint)}</li>`).join('')}</ul>` : '<div class="faint">No staged references were recorded.</div>'}`;
    }));
    if (sessionActive) {
      const mount = $('[data-stage-identification]', element);
      widget = mountIdentificationInput(mount, { id: 'session-reference-code', label: 'Scan or type the next reference', kinds: config.supportedKinds, loadConfig: () => Promise.resolve(config), resolve: code => command(`${API}/resolve`, { code }), isCurrent: () => active() && canMutate(), onUnavailable,
        onClear: () => { clearPreview(); $('[data-stage-preview]', element).textContent = ''; },
        onSelect: (subject, detectedMode) => {
          preview = subject; previewCode = mount.querySelector('input').value; $('[data-stage-preview]', element).textContent = describeIdentification(subject);
          const mode = $('[data-stage-mode]', element);
          if (!captureEvidence && mode.value === '') mode.value = detectedMode === 'KeyboardWedge' ? 'KeyboardWedge' : (view?.manualAllowed === false ? 'KeyboardWedge' : 'Manual');
        },
      });
      mount.querySelector('input').addEventListener('input', () => { captureEvidence = null; });
      $('[data-session-stage]', element).addEventListener('click', () => stageReference($('[data-stage-reason]', element).value));
      $('[data-stage-use-camera]', element).addEventListener('click', beginCamera); $('[data-stage-use-adapter]', element).addEventListener('click', beginAdapter); $('[data-stage-stop-capture]', element).addEventListener('click', clearCapture);
    }
  }

  const dispose = () => { if (disposed) return; void invalidate('Terminal work context ended'); disposed = true; generation++; clearTimeout(expiry); clearCapture(); widget?.dispose(); widget = null; observer?.disconnect(); };
  const observer = typeof MutationObserver === 'function' ? new MutationObserver(() => { if (!element.isConnected || !isCurrent() || auth.subject !== signIn.subject || auth.token !== signIn.token) dispose(); }) : null;
  observer?.observe(element.ownerDocument.body, { childList: true, subtree: true });
  const [metadata, scannerConfig, summary] = await Promise.all([tryGet(`${API}/config`), tryGet(`${API}/scanners/config`), tryGet(`${API}/scan-sessions/requirements?operationCode=${key(operationCode)}${machineCode ? `&machineCode=${key(machineCode)}` : ''}`)]);
  if (current(generation)) {
    requirements = summary.data ?? null;
    if (metadata.error) render(metadata.error.message); else { config = metadata.data; scanners = scannerConfig.data; render(summary.error?.message ?? scannerConfig.error?.message ?? ''); }
  }
  return { get snapshot() { return !busy && active() ? Object.freeze({ id: view.id, version: view.version }) : null; }, get view() { return active() ? view : null; }, get requirements() { return requirements; }, start, refresh, invalidate, dispose };
}
