import { command, fetchProtectedImage, get, tryGet } from '../api.js';
import { img } from '../icons.js';
import { esc, $, $$, fmtNumber, fmtDate, fmtDateTime, relative, toast, emptyBlock, manageDialogFocus, liveRegion, drawer } from '../ui.js';
import { who } from '../people.js';
import { isServiceUnavailable, createTerminalSession } from '../terminal-state.mjs';
import { mountIdentificationInput, workQueueSelection } from '../identification-input.mjs';
import { mountScanSession, operationIdentificationCode } from '../scan-session.mjs';
import { formModal, canAction } from '../forms.js';
import { attachmentsDrawer } from '../attachments.js';
import { appendOrderReconciliation } from '../genealogy.js';
import { clearDemoScannerOutage, readDemoScannerOutage, reconcileDemoLabel, saveDemoOutageDraft, simulateDemoOutage } from '../demo-scanner-outage.mjs';

// The shop-floor terminal. A badge scanner types the badge and presses Enter (keyboard wedge); any
// reader that does that works. The badge is held in memory only for this interaction and is
// forgotten after the configured inactivity timeout, or when the next person scans.

const SCRAP_REASONS = ['Edge wave', 'Surface defect', 'Out of tolerance', 'Setup', 'Material defect', 'Damaged in handling'];

export async function shopFloorPage(page, ctx) {
  // V3.5: the plant's scrap reason codes come first; the fixed words remain for plants without codes.
  const scrapCodes = ((await tryGet('/api/execution/reason-codes')).data ?? []).filter(r => r.kind === 'Scrap' && r.active);
  const terminal = createTerminalSession();
  let demoOutage = readDemoScannerOutage();
  const idleTimeoutSeconds = ctx.me.shopFloorIdleTimeoutSeconds;
  const idleMs = (Number.isInteger(idleTimeoutSeconds) && idleTimeoutSeconds >= 1 && idleTimeoutSeconds <= 3600 ? idleTimeoutSeconds : 60) * 1000;
  let idle = null;
  let idleCountdown = null;
  let idleSecondsRemaining = idleMs / 1000;
  let passOnLoadedFor = null;
  let activeQtyField = 'good';
  let kioskClockTimer = null;
  const dialogs = new Set();
  let workIdentification = null;
  let scanWork = null, scanContext = 0;
  let operatorPhotoUrl = null, operatorPhotoPersonId = null, photoCheckedPersonId = null, photoRequest = 0;

  function clearOperatorPhoto() {
    photoRequest++;
    if (operatorPhotoUrl) URL.revokeObjectURL(operatorPhotoUrl);
    operatorPhotoUrl = null; operatorPhotoPersonId = null; photoCheckedPersonId = null;
  }

  async function loadOperatorPhoto(person, session) {
    if (!person?.isWorker || photoCheckedPersonId === person.personId) return;
    photoCheckedPersonId = person.personId;
    const request = ++photoRequest;
    try {
      const blob = await fetchProtectedImage('/api/shopfloor/worker-photo', { badge: session.badge });
      if (!terminal.isCurrent(session) || request !== photoRequest || terminal.person?.personId !== person.personId || page.isConnected === false) return;
      operatorPhotoUrl = URL.createObjectURL(blob);
      operatorPhotoPersonId = person.personId;
      const avatar = $('.kiosk-operator-avatar', page);
      if (avatar) {
        const image = document.createElement('img');
        image.className = 'kiosk-operator-avatar kiosk-operator-photo';
        image.setAttribute('alt', `${person.displayName} employee photo`);
        image.src = operatorPhotoUrl;
        avatar.parentElement?.insertBefore(image, avatar);
        avatar.remove();
      }
    } catch { /* The initials avatar remains when a photo is absent or unavailable. */ }
  }

  function clearScanWork({ invalidateServerSession = true } = {}) {
    scanContext++;
    scanWork?.controller?.dispose({ invalidateServerSession }); scanWork?.element.remove(); scanWork = null;
  }

  function notifyInput(input) {
    const EventType = input.ownerDocument.defaultView?.Event;
    if (EventType) input.dispatchEvent(new EventType('input', { bubbles: true }));
    else input.dispatchEvent({ type: 'input', bubbles: true, target: input });
  }

  async function stageWork(work, session, machineCode = null) {
    if (!terminal.isCurrent(session) || terminal.busy) return;
    clearScanWork();
    const context = scanContext, element = document.createElement('section'); element.className = 'panel';
    $('[data-scan-session-host]', page)?.append(element);
    element.innerHTML = '<div class="muted">Reading staging configuration…</div>';
    const pending = { element, work, session, machineCode, controller: null }; scanWork = pending;
    try {
      const config = await get('/api/identification/config');
      if (!terminal.isCurrent(session) || context !== scanContext) return;
      const controller = await mountScanSession(element, { operationCode: operationIdentificationCode(config, work.orderNumber, work.operation.sequence), operatorPersonId: terminal.person.personId, machineCode, badge: session.badge, isCurrent: () => terminal.isCurrent(session) && context === scanContext, canMutate: () => !terminal.busy, onUnavailable: lockForOutage, drivers: ctx.scannerDrivers ?? {} });
      if (!terminal.isCurrent(session) || context !== scanContext) controller.dispose(); else pending.controller = controller;
    } catch (error) { if (terminal.isCurrent(session) && context === scanContext) { element.innerHTML = `<div class="notice bad" role="alert">${esc(error.message)}</div>`; } }
  }

  const canUse = ctx.me.roles.some(r => ['ShopFloorTerminal', 'Supervisor'].includes(r));
  if (!canUse) {
    page.innerHTML = `
      <div class="page-head"><div><h1>Shop Floor</h1><div class="sub">Badge terminal for clocking in, starting work, reporting quantities and supervised training.</div></div></div>
      ${emptyBlock('This screen is for shop-floor terminals', 'Sign in as a terminal account (role ShopFloorTerminal) or a supervisor to use it.')}`;
    return;
  }

  const operatorStatus = liveRegion('Current shop-floor operator');
  const onActivity = () => { if (terminal.person) resetIdle(); };
  page.addEventListener('click', onActivity);
  page.addEventListener('keydown', onActivity);
  page.addEventListener('input', onActivity);

  function closeDialogs() { for (const close of [...dialogs]) close(); }

  function lockTerminal({ preserveScanSession = false } = {}) {
    clearTimeout(idle);
    clearTimeout(idleCountdown);
    clearInterval(kioskClockTimer);
    kioskClockTimer = null;
    idleCountdown = null;
    terminal.clear();
    clearOperatorPhoto();
    clearScanWork({ invalidateServerSession: !preserveScanSession });
    passOnLoadedFor = null;
    closeDialogs();
    render();
  }

  function resetIdle() {
    if (terminal.disposed) return;
    clearTimeout(idle);
    clearTimeout(idleCountdown);
    idleSecondsRemaining = idleMs / 1000;
    idle = setTimeout(lockTerminal, idleMs);
    updateIdleCountdown();
  }

  function updateIdleCountdown() {
    const indicator = $('#idle-indicator', page);
    if (indicator && terminal.person) {
      const seconds = Math.max(0, Math.ceil(idleSecondsRemaining));
      const value = $('#idle-countdown', indicator);
      if (value) value.textContent = `${seconds}s`;
      if (seconds <= 15) indicator.classList.add('warn'); else indicator.classList.remove('warn');
      indicator.setAttribute('aria-label', `Session locks in ${seconds} seconds`);
    }
    if (terminal.person && idleSecondsRemaining > 0) {
      idleCountdown = setTimeout(() => {
        idleSecondsRemaining = Math.max(0, idleSecondsRemaining - 1);
        updateIdleCountdown();
      }, 1000);
    }
  }

  function numpad(inputId, decimal = false) {
    const keys = ['1','2','3','4','5','6','7','8','9',...(decimal ? ['.'] : []),'0','⌫','Clear'];
    return `<div class="touch-numpad" role="group" aria-label="${decimal ? 'Numeric keypad' : 'PIN keypad'}" data-numpad="${inputId}">${keys.map(key => {
      const value = key === '⌫' ? 'backspace' : key === 'Clear' ? 'clear' : key;
      const label = key === '⌫' ? 'Delete last digit' : key === 'Clear' ? 'Clear value' : key === '.' ? 'Decimal point' : `Enter ${key}`;
      return `<button class="btn" type="button" data-numpad-key="${value}" aria-label="${label}">${esc(key)}</button>`;
    }).join('')}</div>`;
  }

  function sharedQtyKeypad(remainingQty) {
    const rem = Math.max(0, Number(remainingQty ?? 0));
    const keys = ['7','8','9','4','5','6','1','2','3','0','.','Clear'];
    return `<div class="kiosk-keypad-panel" role="group" aria-label="Touch quantity entry keypad">
      <div class="kiosk-keypad-target-bar"><span class="muted small">Keypad target</span>
        <button class="btn small primary" type="button" data-focus-qty="good" aria-pressed="true">Good quantity</button>
        <button class="btn small ghost" type="button" data-focus-qty="scrap" aria-pressed="false">Scrap quantity</button>
      </div>
      <div class="kiosk-quick-chips" role="group" aria-label="Quick quantity increments">
        <button class="btn small" type="button" data-qty-add="1">+1</button><button class="btn small" type="button" data-qty-add="5">+5</button>
        <button class="btn small" type="button" data-qty-add="10">+10</button><button class="btn small" type="button" data-qty-add="25">+25</button>
        <button class="btn small" type="button" data-qty-fill="${rem}" ${rem > 0 ? '' : 'disabled'}>Fill remaining (${fmtNumber(rem, 3)})</button>
      </div>
      <div class="touch-numpad kiosk-shared-keypad" data-numpad="good" aria-label="Quantity keypad">${keys.map(key => {
        const value = key === 'Clear' ? 'clear' : key;
        return `<button class="btn" type="button" data-numpad-target="good" data-numpad-key="${value}" aria-label="${key === 'Clear' ? 'Clear value' : key === '.' ? 'Decimal point' : `Enter ${key}`}">${key}</button>`;
      }).join('')}</div>
    </div>`;
  }

  function setActiveQtyField(field) {
    if (!['good', 'scrap'].includes(field)) return;
    activeQtyField = field;
    const keypad = $('.kiosk-shared-keypad', page);
    if (keypad) keypad.setAttribute('data-numpad', field);
    $$('[data-numpad-target]', page).forEach(button => button.setAttribute('data-numpad-target', field));
    $$('[data-focus-qty]', page).forEach(button => {
      const active = button.dataset.focusQty === field;
      button.classList.remove(active ? 'ghost' : 'primary');
      button.classList.add(active ? 'primary' : 'ghost');
      button.setAttribute('aria-pressed', String(active));
    });
  }

  function wireSharedQtyControls() {
    $$('[data-focus-qty]', page).forEach(button => button.addEventListener('click', () => {
      setActiveQtyField(button.dataset.focusQty);
      $(`#${activeQtyField}`, page)?.focus();
    }));
    $$('[data-qty-add]', page).forEach(button => button.addEventListener('click', () => {
      const input = $(`#${activeQtyField}`, page);
      if (!input || input.disabled) return;
      input.value = String(Number(input.value || 0) + Number(button.dataset.qtyAdd));
      notifyInput(input);
    }));
    $('[data-qty-fill]', page)?.addEventListener('click', () => {
      const input = $(`#${activeQtyField}`, page);
      if (!input || activeQtyField !== 'good' || input.disabled) return;
      input.value = $('[data-qty-fill]', page).dataset.qtyFill;
      notifyInput(input);
    });
    ['good', 'scrap'].forEach(field => $(`#${field}`, page)?.addEventListener('focus', () => setActiveQtyField(field)));
    $$('[data-scrap-tile]', page).forEach(button => button.addEventListener('click', () => {
      const select = $('#reason', page);
      if (!select) return;
      select.value = button.dataset.scrapTile;
      $$('[data-scrap-tile]', page).forEach(tile => tile.setAttribute('aria-pressed', String(tile === button)));
    }));
  }

  function wireNumpads(root) {
    root.addEventListener('click', event => {
      const button = event.target.closest?.('[data-numpad-key]');
      if (!button || !root.contains(button)) return;
      const group = button.closest('[data-numpad]');
      const input = group ? $(`#${group.dataset.numpad}`, root) : null;
      if (!input || input.disabled || input.readOnly) return;
      const key = button.dataset.numpadKey;
      if (key === 'clear') input.value = '';
      else if (key === 'backspace') input.value = input.value.slice(0, -1);
      else if (key === '.') {
        if (input.value.includes('.')) return;
        input.value = `${input.value || '0'}.`;
      } else input.value = input.value === '0' ? key : `${input.value}${key}`;
      const EventType = input.ownerDocument.defaultView?.Event;
      if (EventType) input.dispatchEvent(new EventType('input', { bubbles: true }));
      else input.dispatchEvent({ type: 'input', bubbles: true, target: input });
    });
  }

  function lockForOutage(error) {
    if (!isServiceUnavailable(error)) return false;
    if (terminal.disposed) return true;
    lockTerminal();
    toast('Forge could not confirm that action. The terminal is locked; ask a supervisor to check the record before retrying.', true);
    return true;
  }

  function canReconcileDemoOutage() {
    return (ctx.me.permissions ?? []).includes('workforce.supervise') && !!ctx.me.personId;
  }

  function renderDemoOutagePanel() {
    if (!demoOutage) return '';
    const scanner = demoOutage.scanner;
    const label = demoOutage.label;
    const canReconcile = canReconcileDemoOutage();
    return `<section class="panel kiosk-demo-outage" aria-label="WhiskerWorks scanner outage rehearsal">
      <header><div><h2>WW-P01 scanner / label outage rehearsal</h2><p class="muted small">FICTIONAL / SAMPLE DATA · browser session only</p></div><span class="spacer"></span><span class="pill warn">No live device or Forge command</span></header>
      <p class="notice warn">This rehearsal stores its typed demo entries in this browser tab only. Simulated 503 actions make no source-changing Forge request and contact no scanner or printer; ordinary read-only kiosk data may still load. A terminal lock here is a local UI state.</p>
      <div class="grid two">
        <section class="card"><h3>Barcode staging</h3>
          ${scanner.status === 'armed' ? `<label class="field"><span>Scanned reference</span><input class="input" data-s12-scanner-reference maxlength="240" value="${esc(scanner.draft.reference)}"></label><label class="field"><span>Operation / work context</span><input class="input" data-s12-scanner-operation maxlength="240" value="${esc(scanner.draft.operation)}"></label><button class="btn danger" data-s12-fail-scanner type="button">Simulate barcode staging 503</button>` : `<div class="notice bad" role="status">Simulated HTTP 503 · terminal fail-closed</div><dl><dt>Reference preserved</dt><dd>${esc(scanner.draft.reference)}</dd><dt>Operation preserved</dt><dd>${esc(scanner.draft.operation)}</dd><dt>Attempts</dt><dd>${scanner.attempts}</dd></dl>`}
        </section>
        <section class="card"><h3>Label print reconciliation</h3>
          ${label.status === 'armed' ? `<label class="field"><span>Part number</span><input class="input" data-s12-label-part maxlength="240" value="${esc(label.draft.partNumber)}"></label><label class="field"><span>Quantity</span><input class="input" data-s12-label-quantity maxlength="240" value="${esc(label.draft.quantity)}"></label><label class="field"><span>Label contents</span><textarea class="input" data-s12-label-text maxlength="240">${esc(label.draft.labelText)}</textarea></label><button class="btn danger" data-s12-fail-label type="button">Simulate label printer timeout</button>`
            : label.status === 'pending-reconciliation' ? `<div class="notice warn" role="status">PendingReconciliation · printer outcome is unknown; inspect the physical output before signing.</div><dl><dt>Part</dt><dd>${esc(label.draft.partNumber)}</dd><dt>Quantity</dt><dd>${esc(label.draft.quantity)}</dd><dt>Label contents</dt><dd>${esc(label.draft.labelText)}</dd><dt>Print attempts</dt><dd>${label.attempts}</dd></dl>${canReconcile ? `<label class="field"><span>Observed physical outcome</span><select class="select" data-s12-label-outcome><option value="">Choose after inspection</option><option value="printed">Label observed printed</option><option value="not-printed">No label observed</option></select></label><label class="field"><span>Observation note</span><input class="input" data-s12-label-reason maxlength="240" placeholder="What was physically checked?"></label><button class="btn primary" data-s12-reconcile-label type="button">Sign observation and reconcile</button>` : '<p class="notice">A supervisor with workforce supervision access must sign the physical observation.</p>'}`
              : `<div class="notice ok" role="status">Reconciled · ${label.observedOutcome === 'printed' ? 'label observed printed' : 'no label observed'} by ${esc(label.observedBy)} at ${esc(fmtDateTime(label.observedAt))}</div><p>${esc(label.observationNote)}</p>`}
        </section>
      </div>
      ${scanner.status === 'failed' && label.status === 'reconciled' && canReconcile ? '<button class="btn" data-s12-close type="button">Close completed local rehearsal</button>' : ''}
    </section>`;
  }

  function wireDemoOutagePanel() {
    if (!demoOutage) return;
    const draft = area => area === 'scanner'
      ? { reference: $('[data-s12-scanner-reference]', page)?.value, operation: $('[data-s12-scanner-operation]', page)?.value }
      : { partNumber: $('[data-s12-label-part]', page)?.value, quantity: $('[data-s12-label-quantity]', page)?.value, labelText: $('[data-s12-label-text]', page)?.value };
    const persist = area => {
      try { demoOutage = saveDemoOutageDraft(globalThis.sessionStorage, area, draft(area)); }
      catch (error) { toast(error.message, true); }
    };
    ['[data-s12-scanner-reference]', '[data-s12-scanner-operation]'].forEach(selector => $(selector, page)?.addEventListener('input', () => persist('scanner')));
    ['[data-s12-label-part]', '[data-s12-label-quantity]', '[data-s12-label-text]'].forEach(selector => $(selector, page)?.addEventListener('input', () => persist('label')));
    $('[data-s12-fail-scanner]', page)?.addEventListener('click', () => {
      try { demoOutage = saveDemoOutageDraft(globalThis.sessionStorage, 'scanner', draft('scanner')); demoOutage = simulateDemoOutage(globalThis.sessionStorage, 'scanner'); lockTerminal({ preserveScanSession: true }); }
      catch (error) { toast(error.message, true); }
    });
    $('[data-s12-fail-label]', page)?.addEventListener('click', () => {
      try { demoOutage = saveDemoOutageDraft(globalThis.sessionStorage, 'label', draft('label')); demoOutage = simulateDemoOutage(globalThis.sessionStorage, 'label'); lockTerminal({ preserveScanSession: true }); }
      catch (error) { toast(error.message, true); }
    });
    $('[data-s12-reconcile-label]', page)?.addEventListener('click', () => {
      try {
        demoOutage = reconcileDemoLabel(globalThis.sessionStorage, { outcome: $('[data-s12-label-outcome]', page)?.value,
          signer: ctx.me.personId, reason: $('[data-s12-label-reason]', page)?.value });
        toast('Demo label observation recorded in this browser session.'); render();
      } catch (error) { toast(error.message, true); }
    });
    $('[data-s12-close]', page)?.addEventListener('click', () => {
      try { clearDemoScannerOutage(globalThis.sessionStorage); demoOutage = null; render(); }
      catch (error) { toast(error.message, true); }
    });
  }

  function scanBox(label, id = 'badge') {
    return `<form class="scan" data-scan="${id}">
      <label for="${id}">${esc(label)}</label>
      <input id="${id}" class="input scan-input" type="password" autocomplete="off" inputmode="none" placeholder="Scan badge" aria-label="${esc(label)}">
    </form>`;
  }

  async function identify(value, existingSession = null) {
    const token = existingSession ?? terminal.beginIdentification(value);
    if (!terminal.isCurrent(token)) return;
    if (!existingSession) {
      clearOperatorPhoto();
      closeDialogs();
      activeQtyField = 'good';
      resetIdle();
      render();
    }
    try {
      const person = await command('/api/shopfloor/identify', { badge: token.badge });
      if (!terminal.acceptIdentification(token, person)) return;
      resetIdle();
    } catch (e) {
      if (lockForOutage(e)) return;
      if (!terminal.isCurrent(token)) return;
      terminal.clear();
      clearOperatorPhoto();
      toast(e.message, true);
    }
    render();
  }

  async function act(path, body, ok, quiet = false, session = terminal.capture()) {
    const machineCode = (path === '/api/shopfloor/report' ? terminal.person?.labor?.machineCode : body.machineCode) ?? null;
    if (scanWork && ['/api/shopfloor/start', '/api/shopfloor/report'].includes(path) && (scanWork.machineCode !== machineCode || scanWork.work.orderNumber !== body.orderNumber || scanWork.work.operation.sequence !== body.sequence)) clearScanWork();
    const staged = scanWork && terminal.isCurrent(scanWork.session) && scanWork.work.orderNumber === body.orderNumber && scanWork.work.operation.sequence === body.sequence
      && ['/api/shopfloor/start', '/api/shopfloor/report'].includes(path) ? scanWork.controller?.snapshot : null;
    const token = terminal.beginAction(session);
    if (!token) return null;
    try {
      if (['/api/shopfloor/start', '/api/shopfloor/report'].includes(path)) {
        const requirements = await get(`/api/identification/scan-sessions/requirements?orderNumber=${encodeURIComponent(body.orderNumber)}&sequence=${encodeURIComponent(body.sequence)}${machineCode ? `&machineCode=${encodeURIComponent(machineCode)}` : ''}`);
        if (!terminal.isCurrent(token)) return null;
        if (requirements?.adopted !== false && requirements?.adopted !== true) throw Object.assign(new Error('Forge did not confirm the applicable work-staging requirements. Refresh the current work before continuing.'), { status: 409 });
        if (requirements.required && (!staged || !(requirements.requiredKinds ?? []).every(kind => (scanWork?.controller?.view?.selections ?? []).some(selection => selection.kind.toLowerCase() === kind.toLowerCase())))) throw Object.assign(new Error('The current adopted policy requires a server-owned scan session. Stage this operation’s required references before continuing.'), { status: 422 });
      }
      const r = await command(path, body, undefined, staged ? { scanSession: staged } : undefined);
      if (!terminal.isCurrent(token)) return null;
      if (staged && scanWork?.controller) await scanWork.controller.refresh();
      if (!terminal.isCurrent(token)) return null;
      if (path === '/api/shopfloor/stop' || path === '/api/shopfloor/clock-out' || (path === '/api/shopfloor/report' && body.complete)) clearScanWork();
      toast(r?.message ?? r?.clockedOut?.message ?? ok ?? 'Done.');
      await identify(token.badge, token);
      return r;
    } catch (e) {
      if (lockForOutage(e)) return e;
      if (!terminal.isCurrent(token)) return null;
      if (!quiet) toast(e.message, true);
      return e;
    } finally {
      terminal.endAction(token);
    }
  }

  function render() {
    if (terminal.disposed) return;
    clearInterval(kioskClockTimer);
    kioskClockTimer = null;
    workIdentification?.dispose(); workIdentification = null;
    operatorStatus.textContent = terminal.person ? `Current operator: ${terminal.person.displayName}.` : 'Terminal locked. Scan your badge.';
    if (!terminal.person) {
      page.innerHTML = `<section class="panel kiosk-attract-hud">
        <div class="kiosk-attract-top"><div><span class="eyebrow">SHOP-FLOOR EXECUTION TERMINAL</span><h1>Scan operator badge to begin</h1>
          <p class="muted">Scan your physical badge with the station wedge scanner, or type the badge and press Enter.</p></div>
          <div class="kiosk-station-status"><time id="kiosk-site-clock" aria-label="Site time">${esc(fmtDateTime(new Date()))}</time>
            <button class="kiosk-focus-pill is-armed" id="badge-focus-status" type="button" role="status" aria-live="polite">● Scanner armed — waiting for badge</button></div>
        </div>
        <div class="kiosk-badge-entry">${img('operator', 'kiosk-ico')}${scanBox('Employee badge barcode', 'badge')}</div>
        <div class="kiosk-safety-reminder"><span class="pill info">Safety &amp; IPC gate active</span>
          <span class="muted">Machine readiness, qualifications, safety staffing and controlled-work requirements are checked by Forge before work starts.</span></div>
      </section>${renderDemoOutagePanel()}`;
      wireScan();
      wireDemoOutagePanel();
      const badgeInput = $('#badge', page);
      const focusStatus = $('#badge-focus-status', page);
      const updateScannerStatus = () => {
        if (!focusStatus) return;
        const armed = document.activeElement === badgeInput;
        focusStatus.className = `kiosk-focus-pill ${armed ? 'is-armed' : 'is-blurred'}`;
        focusStatus.textContent = armed ? '● Scanner armed — waiting for badge' : '▲ Tap here to arm barcode scanner';
      };
      badgeInput?.addEventListener('focus', updateScannerStatus);
      badgeInput?.addEventListener('blur', updateScannerStatus);
      focusStatus?.addEventListener('click', () => badgeInput?.focus());
      badgeInput?.focus();
      updateScannerStatus();
      kioskClockTimer = setInterval(() => {
        const clock = $('#kiosk-site-clock', page);
        if (clock) clock.textContent = fmtDateTime(new Date());
      }, 15000);
      return;
    }

    const p = terminal.person;
    const session = terminal.capture();
    const labor = p.labor;
    const clocked = p.clockedInSince;
    const myOp = labor ? p.queue.find(q => q.orderNumber === labor.orderNumber && q.operation.sequence === labor.sequence) : null;
    const targetQty = Number(myOp?.orderQuantity ?? 0);
    const goodSoFar = Number(myOp?.operation.goodQuantity ?? 0);
    const scrapSoFar = Number(myOp?.operation.scrapQuantity ?? 0);
    const remainingQty = Math.max(0, targetQty - goodSoFar);
    const goodPct = targetQty > 0 ? Math.min(100, goodSoFar / targetQty * 100) : 0;
    const scrapPct = targetQty > 0 ? Math.min(Math.max(0, 100 - goodPct), scrapSoFar / targetQty * 100) : 0;
    const awaiting = (p.training ?? []).filter(t => t.trainerId === p.personId && !t.evaluation);
    const operatorInitials = p.displayName.split(/\s+/).map(part => part[0]).join('').slice(0, 2).toUpperCase();
    const operatorAvatar = operatorPhotoUrl && operatorPhotoPersonId === p.personId
      ? `<img class="kiosk-operator-avatar kiosk-operator-photo" src="${esc(operatorPhotoUrl)}" alt="${esc(p.displayName)} employee photo">`
      : `<div class="kiosk-operator-avatar" aria-hidden="true">${esc(operatorInitials)}</div>`;
    if (scanWork && (!terminal.isCurrent(scanWork.session) || (labor && (scanWork.work.orderNumber !== labor.orderNumber || scanWork.work.operation.sequence !== labor.sequence || scanWork.machineCode !== (labor.machineCode ?? null))))) clearScanWork();
    page.innerHTML = `
      <div class="page-head">
        <div><h1>${esc(p.displayName)}</h1>
          <div class="sub">${clocked ? `<span class="pill ok">Clocked in ${esc(relative(clocked))}</span>` : '<span class="pill none">Not clocked in</span>'}
            ${p.homeWorkCenter ? ` · Home: ${esc(p.homeWorkCenter)}` : ''}${p.hasPin ? '' : ' · <span class="c-warn">No PIN set</span>'}</div></div>
        <div class="spacer"></div>
        <div id="idle-indicator" class="pill info" role="timer" aria-live="off"><span>Locks in</span> <b id="idle-countdown">${idleSecondsRemaining}s</b> <button class="btn small" id="stay-signed-in" type="button">Stay signed in</button></div>
        <div class="head-controls">
          ${ctx.me.roles.includes('Supervisor') ? `<button class="btn" id="toggle-kiosk" type="button" aria-pressed="${ctx.kioskMode ? 'true' : 'false'}">${ctx.kioskMode ? 'Exit kiosk view' : 'Kiosk view'}</button>` : ''}
          ${clocked ? '<button class="btn" id="clock-out">Clock out</button>' : '<button class="btn primary big" id="clock-in">Clock in</button>'}
          <button class="btn" id="pin">${p.hasPin ? 'Change PIN' : 'Set PIN'}</button>
          <button class="btn outline" id="done">Switch user</button>
          <button class="btn danger" id="lock-terminal">Lock terminal</button>
        </div>
      </div>
      <div id="pass-on-banner"></div>
      ${renderDemoOutagePanel()}
      ${p.isWorker && !labor ? `<section class="panel kiosk-identified-operator" aria-label="Identified operator"><div class="kiosk-operator-strip">${operatorAvatar}<div><b>${esc(p.displayName)}</b><small>${p.homeWorkCenter ? `Home work center · ${esc(p.homeWorkCenter)}` : 'Worker identity confirmed'}</small></div><span class="spacer"></span><span class="pill info">Safety and qualification checks at start</span></div><p class="muted small">Forge checks current qualifications, machine readiness and required safety staffing before an operation starts.</p></section>` : ''}
      ${!p.isWorker ? emptyBlock('Not a registered worker', 'This badge belongs to someone who is not set up as a worker.') : ''}
      ${labor ? `
      <section class="panel kiosk-active-work">
        <header><div><span class="eyebrow">ACTIVE PRODUCTION</span><h2>Order #${labor.orderNumber} · operation ${labor.sequence}</h2></div><span class="spacer"></span>
          <span class="pill ${labor.role === 'Operator' ? 'info' : 'warn'}">${esc(labor.role)}</span><span class="pill">${esc(labor.phase ?? 'Run')}</span></header>
        <div class="kiosk-operator-strip">${operatorAvatar}<div><b>${esc(p.displayName)}</b><small>${esc(myOp ? `${myOp.partNumber} · ${myOp.operation.description}` : 'Work details unavailable')}${labor.machineCode ? ` · ${esc(labor.machineCode)}` : ''} · started ${esc(fmtDateTime(labor.startedAt))}</small></div></div>
        ${myOp ? `<div class="kiosk-progress-strip" role="region" aria-label="Order completion progress">
          <div class="kiosk-progress-bar" aria-hidden="true"><span class="kiosk-progress-good" style="width:${goodPct}%"></span><span class="kiosk-progress-scrap" style="width:${scrapPct}%"></span></div>
          <div class="kiosk-metrics-row"><div class="kiosk-metric"><span class="k">Target</span><b class="v">${fmtNumber(targetQty, 3)}</b></div><div class="kiosk-metric ok"><span class="k">Good so far</span><b class="v">${fmtNumber(goodSoFar, 3)}</b></div><div class="kiosk-metric ${scrapSoFar > 0 ? 'warn' : ''}"><span class="k">Scrap so far</span><b class="v">${fmtNumber(scrapSoFar, 3)}</b></div><div class="kiosk-metric"><span class="k">Remaining</span><b class="v">${fmtNumber(remainingQty, 3)}</b></div></div>
        </div>` : ''}
        ${labor.role === 'Trainer' ? '<div class="notice">You are supervising. The trainee reports quantities; stopping ends the session for both of you.</div>' : `
        <form class="report-form" id="report">
          <div class="kiosk-quantity-fields"><label class="field" for="good"><span>Good quantity</span><input id="good" class="input big-num" type="number" inputmode="none" min="0" step="any" value="0"></label>
            <label class="field" for="scrap"><span>Scrap quantity</span><input id="scrap" class="input big-num" type="number" inputmode="none" min="0" step="any" value="0"></label>
            <label class="check"><input id="complete" type="checkbox"> Operation complete</label><button class="btn primary big" type="submit">Report quantities</button></div>
          <div class="field kiosk-scrap-reasons"><label for="reason">Scrap reason</label><div class="kiosk-scrap-tiles" role="group" aria-label="Scrap reason quick choices">
            ${(scrapCodes.length ? scrapCodes.map(reason => ({ value: `code:${reason.code}`, label: `${reason.code} · ${reason.description}` })) : SCRAP_REASONS.map(reason => ({ value: reason, label: reason }))).map(reason => `<button class="btn small" type="button" data-scrap-tile="${esc(reason.value)}" aria-pressed="false">${esc(reason.label)}</button>`).join('')}
          </div><select id="reason" class="select" aria-label="Scrap reason detail"><option value="">Choose a reason</option>${scrapCodes.map(reason => `<option value="code:${esc(reason.code)}">${esc(reason.code)} — ${esc(reason.description)}</option>`).join('')}${scrapCodes.length ? '' : SCRAP_REASONS.map(reason => `<option>${esc(reason)}</option>`).join('')}</select></div>
          ${sharedQtyKeypad(remainingQty)}
        </form>`}
        <div class="shopfloor-actions" style="margin-top:12px"><button class="btn" data-stage-current-work>Stage current operation references</button>
          ${canAction(ctx.me, 'inventory.material-request') ? '<button class="btn primary" id="request-parts" type="button">Request parts</button><button class="btn" id="material-request-status" type="button">View request status</button>' : ''}
          ${canAction(ctx.me, 'inventory.material-request') ? '<button class="btn" id="request-additional-parts" type="button">Request additional part</button>' : ''}
          ${canAction(ctx.me, 'quality.suspect.report') ? '<button class="btn danger" id="report-suspect-scrap" type="button">Report scrap</button><button class="btn" id="report-quality-issue" type="button">Report quality issue</button>' : ''}
          ${canAction(ctx.me, 'engineering.read') ? '<button class="btn" id="work-instructions" type="button">Work instructions &amp; drawings</button>' : '<button class="btn" type="button" disabled title="Engineering document access is required">Work instructions unavailable</button>'}
          ${canAction(ctx.me, 'inventory.read') ? '<button class="btn" id="material-reconciliation" type="button">Material &amp; remnants</button>' : '<button class="btn" type="button" disabled title="Inventory read access is required">Material view unavailable</button>'}
          ${canAction(ctx.me, 'quality.inspect') ? '<button class="btn danger" id="flag-ncr" type="button">Flag defect / NCR</button>' : ''}
          ${canAction(ctx.me, 'holds.place') ? '<button class="btn danger" id="quality-hold" type="button">Place quality hold</button>' : ''}
          ${labor.machineCode && canAction(ctx.me, 'maintenance.downtime.report') ? `<button class="btn danger big" id="kiosk-machine-down" type="button">Report machine down · ${esc(labor.machineCode)}</button><button class="btn big" id="kiosk-downtime" type="button">Machine stopped — why? · ${esc(labor.machineCode)}</button><button class="btn" id="kiosk-downtime-end" type="button">Machine running again</button>` : ''}
          <button class="btn danger" id="stop">Stop work</button></div>
      </section>` : ''}
      ${awaiting.length ? `
      <section class="panel"><header><h2>Training to evaluate</h2></header>
        <table class="table"><tbody>${awaiting.map(t => `<tr><td>${esc(who('person:' + t.traineeId))}</td><td>#${t.orderNumber}/${t.sequence} · ${esc(t.qualifications.join(', '))}</td>
          <td>${esc(fmtDateTime(t.startedAt))}${t.endedAt ? '' : ' · <span class="c-warn">in progress</span>'}</td>
          <td><button class="btn" data-evaluate="${t.sessionId}">Evaluate &amp; sign</button></td></tr>`).join('')}</tbody></table>
      </section>` : ''}
      <div data-scan-session-host></div>
      ${p.isWorker && !labor ? `
      <section class="panel"><header><h2>Work you can start</h2><span class="spacer"></span><span class="muted">your work center, assignment and schedule</span></header>
        <div data-work-identification></div><div data-identified-work></div>
        ${p.queue.length ? `<div class="kiosk-dispatch-grid" aria-label="Available jobs">${p.queue.map((q, i) => `<article class="kiosk-dispatch-card">
          <div class="kiosk-dispatch-head"><div><span class="eyebrow">${q.scheduledForYou ? 'SCHEDULED FOR YOU' : 'AVAILABLE WORK'}</span><h3>Order #${q.orderNumber}</h3></div>${q.scheduledForYou ? '<span class="pill info">Scheduled for you</span>' : ''}</div>
          <p class="muted"><b>${esc(q.partNumber)}</b> · due ${esc(fmtDate(q.dueDate))} · ${fmtNumber(q.operation.goodQuantity, 3)} / ${fmtNumber(q.orderQuantity, 3)} good</p>
          <div class="kiosk-dispatch-operation"><b>Op ${q.operation.sequence} · ${esc(q.operation.description)}</b><span>${esc(q.operation.workCenterCode)}</span></div>
          <div class="kiosk-dispatch-machine">${q.operation.eligibleMachines.length > 1
            ? `<label class="field"><span>Choose a machine</span><select class="select big" data-machine="${i}" aria-label="Machine for order ${q.orderNumber} operation ${q.operation.sequence}">${q.operation.eligibleMachines.map(machine => `<option ${machine === q.scheduledMachine ? 'selected' : ''}>${esc(machine)}</option>`).join('')}</select></label>`
            : `<span class="pill info">${esc(q.operation.eligibleMachines[0] ?? 'Machine selected by the operation')}</span>`}
            <label class="field"><span>Labor phase</span><select class="select" data-phase="${i}" aria-label="Labor phase for order ${q.orderNumber} operation ${q.operation.sequence}"><option value="Setup">Setup</option><option value="Run" selected>Run</option><option value="Inspection">Inspection</option><option value="Rework">Rework</option></select></label>
            ${q.scheduledStart ? `<span class="muted">Scheduled ${esc(fmtDateTime(q.scheduledStart))}</span>` : ''}</div>
          ${q.hold ? `<div class="notice bad"><b>Cannot start · order hold</b><div>${esc(q.hold)}</div></div>` : `<div class="kiosk-dispatch-actions"><button class="btn" data-stage-work="${i}" type="button">Stage references</button><button class="btn primary big" data-start="${i}" type="button" ${clocked ? '' : 'disabled title="Clock in first"'}>Start operation</button></div>${!clocked ? '<div class="muted small">Clock in before starting this operation.</div>' : ''}`}
        </article>`).join('')}</div>` : emptyBlock('Nothing to start', 'No released work at your work center, your assignment or in the schedule for you.')}
      </section>` : ''}`;

    $('#clock-in', page)?.addEventListener('click', () => act('/api/shopfloor/clock-in', { badge: session.badge }, null, false, session));
    $('#clock-out', page)?.addEventListener('click', async () => {
      const r = await act('/api/shopfloor/clock-out', { badge: session.badge }, 'Clocked out.', false, session);
      if (r !== null && !(r instanceof Error) && terminal.isCurrent(session)) lockTerminal();
    });
    $('#done', page).addEventListener('click', lockTerminal);
    $('#lock-terminal', page).addEventListener('click', lockTerminal);
    $('#stay-signed-in', page)?.addEventListener('click', resetIdle);
    const kioskToggle = $('#toggle-kiosk', page);
    kioskToggle?.addEventListener('click', () => {
      const enabled = !ctx.kioskMode;
      ctx.setKioskMode?.(enabled);
      ctx.kioskMode = enabled;
      kioskToggle.textContent = enabled ? 'Exit kiosk view' : 'Kiosk view';
      kioskToggle.setAttribute('aria-pressed', String(enabled));
    });
    $('#stop', page)?.addEventListener('click', () => act('/api/shopfloor/stop', { badge: session.badge }, null, false, session));
    $('#pin', page).addEventListener('click', () => pinDialog(session));
    $('#report', page)?.addEventListener('submit', async e => {
      e.preventDefault();
      const good = Number($('#good', page).value || 0), scrap = Number($('#scrap', page).value || 0);
      await act('/api/shopfloor/report', {
        badge: session.badge, orderNumber: labor.orderNumber, sequence: labor.sequence, good, scrap,
        scrapReason: $('#reason', page).value.startsWith('code:') ? null : $('#reason', page).value || null,
        scrapReasonCode: $('#reason', page).value.startsWith('code:') ? $('#reason', page).value.slice(5) : null, complete: $('#complete', page).checked,
      }, null, false, session);
    });
    wireNumpads(page);
    wireSharedQtyControls();
    $('#reason', page)?.addEventListener('change', event => $$('[data-scrap-tile]', page).forEach(tile => tile.setAttribute('aria-pressed', String(tile.dataset.scrapTile === event.target.value))));
    $('#kiosk-machine-down', page)?.addEventListener('click', () => formModal({
      title: `Report ${labor.machineCode} down`,
      detail: 'This records an unplanned outage in Forge. Readiness will show the machine blocked until an authorized maintenance process restores it.',
      confirmLabel: 'Report machine down', reasonRequired: true,
      fields: [{ name: 'description', label: 'What happened?', type: 'textarea', required: true, placeholder: 'Describe the observed fault or stop condition' }],
    }, (values, reason) => command(`/api/maintenance/machines/${encodeURIComponent(labor.machineCode)}/down`, { description: values.description }, reason), () => identify(session.badge, session)));
    // Owner decisions 2026-11-01: the operator says why the machine stopped (explaining a stop the machine reported, if it did) and can ask maintenance to come.
    $('#kiosk-downtime', page)?.addEventListener('click', async () => {
      const codes = ((await tryGet('/api/execution/reason-codes')).data ?? []).filter(r => r.kind === 'Downtime' && r.active && r.code !== 'UNEXPLAINED');
      await formModal({
        title: `Why did ${labor.machineCode} stop?`, detail: 'Pick the reason. If the machine already reported the stop, this explains it.', confirmLabel: 'Report downtime', reasonRequired: false,
        fields: [{ name: 'reasonCode', label: 'Reason', type: 'select', options: codes.map(r => [r.code, `${r.description} (${r.category})`]) },
          { name: 'note', label: 'What happened', type: 'textarea' }, { name: 'requestMaintenance', label: 'Maintenance', type: 'check', text: 'Ask maintenance to come' }],
      }, v => command('/api/shopfloor/downtime', { badge: session.badge, machineCode: labor.machineCode, category: codes.find(r => r.code === v.reasonCode)?.category, reasonCode: v.reasonCode,
        note: v.note || null, requestMaintenance: !!v.requestMaintenance }), () => identify(session.badge, session));
    });
    $('#kiosk-downtime-end', page)?.addEventListener('click', async () => {
      try { const r = await command('/api/shopfloor/downtime/end', { badge: session.badge, machineCode: labor.machineCode }); toast(r.message ?? 'Running again'); }
      catch (error) { toast(error.message, true); }
    });
    $('#work-instructions', page)?.addEventListener('click', () => attachmentsDrawer(`Work instructions — order #${labor.orderNumber}`, {
      type: 'ProductionOrder', id: labor.orderNumber, me: ctx.me,
      note: 'Current controlled instructions and reference documents attached to this production order.',
    }));
    $('#material-reconciliation', page)?.addEventListener('click', async () => {
      const { element } = drawer(`Material & remnants — order #${labor.orderNumber}`, '<div class="muted">Loading material history…</div>');
      element.innerHTML = '';
      await appendOrderReconciliation(element, labor.orderNumber, canAction(ctx.me, 'inventory.transact'));
      if (!element.children.length) element.innerHTML = emptyBlock('No material reconciliation yet', 'No material has been issued to this order, or the current record is not visible to your roles.');
    });
    $('#request-parts', page)?.addEventListener('click', async () => {
      try {
        const [orderResponse, locationResponse] = await Promise.all([
          get(`/api/orders/${labor.orderNumber}/shopfloor`),
          get(`/api/shopfloor/material-request-locations/${labor.orderNumber}/${labor.sequence}`),
        ]);
        if (!terminal.isCurrent(session)) return;
        const parts = [...new Map((orderResponse.materials ?? []).map(line => [line.componentPartNumber, line])).values()];
        if (!parts.length) { toast('This released order has no BOM components available to request.', true); return; }
        const locations = locationResponse ?? [];
        if (!locations.length) { toast('No active staging or point-of-use location is configured at this operation’s work center.', true); return; }
        const fields = [
          { name: 'partNumber', label: 'BOM component', type: 'select', required: true, value: parts[0].componentPartNumber,
            options: parts.map(line => [line.componentPartNumber, `${line.componentPartNumber}${line.componentRevision ? ` · rev ${line.componentRevision}` : ''} · ${fmtNumber(line.requiredQuantity, 3)} ${line.unitOfMeasure}`]) },
          { name: 'quantity', label: `Quantity needed (${parts[0].unitOfMeasure})`, type: 'number', min: 0.000001, step: 'any', required: true, value: 1 },
          { name: 'toLocation', label: 'Destination', type: 'select', required: true, value: locations[0].code,
            options: locations.map(location => [location.code, `${location.name} · ${location.code}`]) },
        ];
        fields[0].onChange = (select, modal) => {
          const selected = parts.find(line => line.componentPartNumber === select.value);
          const label = modal.querySelector('label[for="f-quantity"]');
          if (selected && label) label.textContent = `Quantity needed (${selected.unitOfMeasure})`;
        };
        const idempotencyKey = crypto.randomUUID();
        await formModal({ title: `Request parts — order #${labor.orderNumber}, operation ${labor.sequence}`,
          detail: `The request is linked to ${myOp?.operation?.workCenterCode ?? 'this operation'} and a BOM component. Stock is not deducted when a request is submitted. Give a reason for this operator-initiated request.`,
          fields, confirmLabel: 'Submit part request', reasonRequired: true },
        (values, reason) => command('/api/shopfloor/material-request', {
          badge: session.badge, orderNumber: labor.orderNumber, sequence: labor.sequence,
          partNumber: values.partNumber, quantity: values.quantity, toLocation: values.toLocation,
        }, reason, { idempotencyKey }),
        () => identify(session.badge, session));
      } catch (error) { if (terminal.isCurrent(session) && !lockForOutage(error)) toast(error.message, true); }
    });
    $('#request-additional-parts', page)?.addEventListener('click', async () => {
      try {
        const response = await get(`/api/shopfloor/material-request-locations/${labor.orderNumber}/${labor.sequence}`);
        if (!terminal.isCurrent(session)) return;
        if (!response.length) { toast('No active staging or point-of-use location is configured at this operation’s work center.', true); return; }
        const fields = [
          { name: 'partNumber', label: 'Part number', required: true, code: true, maxLength: 80, placeholder: 'Scan or enter the part code' },
          { name: 'quantity', label: 'Quantity', type: 'number', min: 0.000001, step: 'any', required: true, value: 1 },
          { name: 'toLocation', label: 'Destination', type: 'select', required: true, value: response[0].code,
            options: response.map(location => [location.code, `${location.name} · ${location.code}`]) },
        ];
        await formModal({ title: `Request additional part — order #${labor.orderNumber}, operation ${labor.sequence}`,
          detail: 'The stockroom does not reserve material at request time. A part outside the released BOM, or beyond its remaining released quantity, stays in Approval Needed until a different authorized reviewer approves it.',
          fields, confirmLabel: 'Submit for review', reasonRequired: true },
        (values, reason) => command('/api/shopfloor/material-request', {
          badge: session.badge, orderNumber: labor.orderNumber, sequence: labor.sequence,
          partNumber: values.partNumber, quantity: values.quantity, toLocation: values.toLocation, allowNonBomMaterial: true,
        }, reason, { idempotencyKey: crypto.randomUUID() }),
        () => identify(session.badge, session));
      } catch (error) { if (terminal.isCurrent(session) && !lockForOutage(error)) toast(error.message, true); }
    });
    $('#material-request-status', page)?.addEventListener('click', async () => {
      try {
        const requests = await get(`/api/shopfloor/material-requests/${labor.orderNumber}`);
        if (!terminal.isCurrent(session)) return;
        const rows = (requests ?? []).filter(request => request.operationSequence == null || request.operationSequence === labor.sequence);
        const body = rows.length ? `<div class="table-wrap"><table class="table"><thead><tr><th>Request</th><th>Part</th><th>Quantity</th><th>Picked</th><th>Status</th><th>Destination</th><th>Source</th></tr></thead><tbody>${rows.map(request => `<tr><td>#${request.taskId}</td><td>${esc(request.partNumber)}${request.isNonBom ? '<div class="faint">Additional material</div>' : ''}</td><td>${fmtNumber(request.quantity, 3)}</td><td>${fmtNumber(request.pickedQuantity, 3)}</td><td><span class="pill ${request.status === 'Delivered' ? 'ok' : request.status === 'Cancelled' || request.status === 'Rejected' ? 'none' : 'warn'}">${request.status === 'ApprovalNeeded' ? 'Approval needed' : esc(request.status)}</span>${request.reviewNote ? `<div class="faint">${esc(request.reviewNote)}</div>` : ''}</td><td>${esc(request.toLocation)}</td><td>${request.sourceSuspectReportId ? `Suspect report #${request.sourceSuspectReportId}` : 'Operation request'}</td></tr>`).join('')}</tbody></table></div>`
          : emptyBlock('No material requests for this operation', 'Operator requests and work-order staging tasks will appear here.');
        drawer(`Material requests — order #${labor.orderNumber}, operation ${labor.sequence}`, body);
      } catch (error) { if (terminal.isCurrent(session) && !lockForOutage(error)) toast(error.message, true); }
    });
    const reportSuspect = kind => async () => {
      try {
      const order = await get(`/api/orders/${labor.orderNumber}/shopfloor`);
      if (!terminal.isCurrent(session)) return;
      const outputPart = order.partNumber ?? myOp?.partNumber ?? '';
      const partLines = [{ partNumber: outputPart, revision: order.revision, unitOfMeasure: 'order units' }, ...(order.materials ?? [])];
      const parts = [...new Map(partLines.filter(line => line.partNumber || line.componentPartNumber)
        .map(line => { const code = line.partNumber ?? line.componentPartNumber; return [code, { code, revision: line.revision ?? line.componentRevision }]; })).values()];
      const reasonChoices = scrapCodes.map(reason => [`${reason.code}`, `${reason.code} · ${reason.description}`]);
      const fields = [
        { name: 'partNumber', label: 'Affected part', type: 'select', required: true, value: outputPart,
          options: parts.map(part => [part.code, `${part.code}${part.revision ? ` · rev ${part.revision}` : ''}`]) },
        { name: 'quantity', label: 'Suspect quantity', type: 'number', min: 0.000001, step: 'any', required: true, value: 1 },
        { name: 'lotNumber', label: 'Lot / batch (if known)', maxLength: 80 },
        { name: 'serialNumber', label: 'Serial number (if applicable)', maxLength: 100 },
        ...(reasonChoices.length ? [{ name: 'reasonCode', label: 'Reason code', type: 'select', required: true, options: reasonChoices }] : [{ name: 'reasonCode', label: 'Reason code', required: true, maxLength: 80, placeholder: 'e.g. DIMENSIONAL' }]),
        { name: 'description', label: 'What was observed', type: 'textarea', required: true },
      ];
      const idempotencyKey = crypto.randomUUID();
      await formModal({ title: `${kind === 'Scrap' ? 'Report suspect scrap' : 'Report quality issue'} — order #${labor.orderNumber}, op ${labor.sequence}`,
        detail: 'This records a suspected issue for Quality review. It does not approve scrap, change reported production quantities, quarantine stock, or post an inventory transaction.',
        fields, confirmLabel: kind === 'Scrap' ? 'Report suspect scrap' : 'Report quality issue', reasonRequired: true },
      (values, reason) => command('/api/shopfloor/suspect-scrap', {
        badge: session.badge, kind, orderNumber: labor.orderNumber, sequence: labor.sequence, partNumber: values.partNumber,
        quantity: values.quantity, reasonCode: values.reasonCode, description: values.description,
        lotNumber: values.lotNumber, serialNumber: values.serialNumber,
      }, reason, { idempotencyKey }),
      () => identify(session.badge, session));
      } catch (error) { if (terminal.isCurrent(session) && !lockForOutage(error)) toast(error.message, true); }
    };
    $('#report-suspect-scrap', page)?.addEventListener('click', reportSuspect('Scrap'));
    $('#report-quality-issue', page)?.addEventListener('click', reportSuspect('QualityIssue'));
    $('#flag-ncr', page)?.addEventListener('click', () => formModal({
      title: `Flag a defect — order #${labor.orderNumber}, op ${labor.sequence}`,
      detail: 'Record the affected quantity and what was found. An NCR may place the order on hold until quality disposition.',
      confirmLabel: 'Raise NCR', reasonRequired: true,
      fields: [
        { name: 'partNumber', label: 'Part', value: myOp?.partNumber ?? '', readonly: true, required: true },
        { name: 'quantity', label: 'Quantity affected', type: 'number', value: 1, min: 0, step: 'any', required: true },
        { name: 'orderNumber', label: 'Order number', type: 'number', value: labor.orderNumber, readonly: true },
        { name: 'sequence', label: 'Operation', type: 'number', value: labor.sequence, readonly: true },
        { name: 'defect', label: 'Defect', required: true, placeholder: 'What is wrong?' },
        { name: 'description', label: 'What was found', type: 'textarea', required: true },
      ],
    }, (values, reason) => command('/api/quality/ncrs', values, reason), () => identify(session.badge, session)));
    $('#quality-hold', page)?.addEventListener('click', () => formModal({
      title: `Place a quality hold — order #${labor.orderNumber}, op ${labor.sequence}`,
      detail: 'The hold blocks downstream work according to plant policy. Quality disposition is required to release it.',
      confirmLabel: 'Place quality hold', reasonRequired: true,
      fields: [
        { name: 'evidence', label: 'Observed concern', type: 'textarea', required: true },
        { name: 'requiredResolution', label: 'What must happen before release', type: 'textarea', placeholder: 'Quality reviews and dispositions the affected work' },
      ],
    }, (values, reason) => command('/api/holds/', {
      kind: 'Quality', targetType: 'Operation', targetId: `${labor.orderNumber}/${labor.sequence}`,
      targetPart: null, source: 'ShopFloor', evidence: values.evidence, affectedRecords: [],
      requiredResolution: values.requiredResolution,
    }, reason), () => identify(session.badge, session)));
    $$('[data-start]', page).forEach(b => b.addEventListener('click', () => {
      const q = p.queue[Number(b.dataset.start)];
      const sel = $(`[data-machine="${b.dataset.start}"]`, page);
      start(q, sel ? sel.value : q.operation.eligibleMachines[0] ?? null, $(`[data-phase="${b.dataset.start}"]`, page)?.value ?? 'Run', null, session);
    }));
    $$('[data-evaluate]', page).forEach(b => b.addEventListener('click', () => evaluate(Number(b.dataset.evaluate), session)));
    if (scanWork && terminal.isCurrent(scanWork.session)) $('[data-scan-session-host]', page)?.append(scanWork.element);
    $$('[data-stage-work]', page).forEach(button => button.addEventListener('click', () => { const q = p.queue[Number(button.dataset.stageWork)]; stageWork(q, session, $(`[data-machine="${button.dataset.stageWork}"]`, page)?.value ?? q.operation.eligibleMachines[0] ?? null); }));
    $('[data-stage-current-work]', page)?.addEventListener('click', () => stageWork(myOp ?? { orderNumber: labor.orderNumber, operation: { sequence: labor.sequence } }, session, labor.machineCode ?? null));
    $$('[data-machine]', page).forEach(select => select.addEventListener('change', () => { const q = p.queue[Number(select.dataset.machine)]; if (scanWork?.work.orderNumber === q.orderNumber && scanWork.work.operation.sequence === q.operation.sequence && scanWork.machineCode !== select.value) clearScanWork(); }));
    const input = $('[data-work-identification]', page), selected = $('[data-identified-work]', page);
    if (input) workIdentification = mountIdentificationInput(input, {
      id: 'work-identification', label: 'Order or operation code (optional scan)', kinds: ['order', 'operation'],
      loadConfig: () => get('/api/identification/config'), resolve: code => command('/api/identification/resolve', { code }),
      isCurrent: () => terminal.isCurrent(session) && !terminal.busy,
      onUnavailable: lockForOutage,
      onClear: () => { selected.innerHTML = ''; },
      onSelect: subject => {
        const choices = workQueueSelection(subject, p.queue);
        selected.innerHTML = `<div class="notice">Selected work. Choose the operation and press Start.</div>${choices.map((q, i) => `
          <div class="field"><b>Order #${q.orderNumber}, operation ${q.operation.sequence}</b> — ${esc(q.operation.description)}
          ${q.operation.eligibleMachines.length > 1 ? `<label for="identified-machine-${i}">Machine</label><select class="select" id="identified-machine-${i}">${q.operation.eligibleMachines.map(m => `<option ${m === q.scheduledMachine ? 'selected' : ''}>${esc(m)}</option>`).join('')}</select>` : ` · ${esc(q.operation.eligibleMachines[0] ?? '—')}`}
          <label for="identified-phase-${i}">Labor phase</label><select class="select" id="identified-phase-${i}"><option value="Setup">Setup</option><option value="Run" selected>Run</option><option value="Inspection">Inspection</option><option value="Rework">Rework</option></select>
          ${q.hold ? `<span class="pill bad">On hold</span><div class="faint">${esc(q.hold)}</div>` : `<button class="btn" data-identified-stage="${i}">Stage selected operation</button> <button class="btn primary" data-identified-start="${i}" ${clocked ? '' : 'disabled title="Clock in first"'}>Start selected work</button>`}</div>`).join('')}`;
        $$('[data-identified-stage]', selected).forEach(button => button.addEventListener('click', () => { if (!terminal.isCurrent(session) || terminal.busy) return; const index = Number(button.dataset.identifiedStage), q = choices[index]; stageWork(q, session, $(`#identified-machine-${index}`, selected)?.value ?? q.operation.eligibleMachines[0] ?? null); }));
        choices.forEach((q, index) => $(`#identified-machine-${index}`, selected)?.addEventListener('change', event => { if (scanWork?.work.orderNumber === q.orderNumber && scanWork.work.operation.sequence === q.operation.sequence && scanWork.machineCode !== event.target.value) clearScanWork(); }));
        $$('[data-identified-start]', selected).forEach(button => button.addEventListener('click', () => {
          if (!terminal.isCurrent(session) || terminal.busy || !workIdentification?.stage.selected) return;
          const index = Number(button.dataset.identifiedStart), q = choices[index];
          const machine = $(`#identified-machine-${index}`, selected)?.value ?? q.operation.eligibleMachines[0] ?? null;
          const phase = $(`#identified-phase-${index}`, selected)?.value ?? 'Run';
          workIdentification.stage.clear();
          start(q, machine, phase, null, session);
        }));
      },
    });
    loadOperatorPhoto(p, session);
    if (clocked) loadPassOn(session, p, myOp?.operation.workCenterCode ?? p.homeWorkCenter);
    wireDemoOutagePanel();
  }

  async function loadPassOn(session, person, workCenterCode) {
    const host = $('#pass-on-banner', page);
    if (!host || !workCenterCode || !canAction(ctx.me, 'execution.shopfloor.read')) return;
    const key = `${person.personId}:${person.clockedInSince}:${workCenterCode}`;
    if (passOnLoadedFor === key) return;
    passOnLoadedFor = key;
    const result = await tryGet(`/api/execution/shifts/handoffs?workCenter=${encodeURIComponent(workCenterCode)}&limit=20`);
    if (!terminal.isCurrent(session) || !host.isConnected) return;
    if (result.error) { passOnLoadedFor = null; return; }
    const clockedAt = Date.parse(person.clockedInSince);
    const handoff = (result.data ?? []).filter(item => item.reviewedAt && Date.parse(item.shiftUntil) <= clockedAt
      && clockedAt - Date.parse(item.shiftUntil) <= 24 * 60 * 60 * 1000)
      .sort((a, b) => Date.parse(b.shiftUntil) - Date.parse(a.shiftUntil))[0];
    if (!handoff) return;
    const detail = await tryGet(`/api/execution/shifts/handoffs/${encodeURIComponent(handoff.handoffId)}`);
    if (!terminal.isCurrent(session) || !host.isConnected) return;
    if (detail.error) { passOnLoadedFor = null; return; }
    if ((detail.data.acknowledgments ?? []).some(item => item.personId === person.personId)) return;
    host.innerHTML = `<section class="notice info pass-on-banner" role="status"><div><b>Shift pass-on · ${esc(handoff.workCenterCode)} · ${esc(handoff.shiftCode)}</b>
      <span class="muted">${esc(fmtDateTime(handoff.shiftUntil))}</span><div>${esc(handoff.context ?? 'A reviewed handoff is available.')}</div></div>
      <div class="inline">${canAction(ctx.me, 'execution.shopfloor.record') ? `<button class="btn primary" type="button" data-ack-passon="${esc(handoff.handoffId)}">Acknowledge pass-on</button>` : ''}<a class="btn" href="#/shift-continuity">Open shift continuity</a></div></section>`;
    const button = $('[data-ack-passon]', host);
    button?.addEventListener('click', async () => {
      const token = terminal.beginAction(session);
      if (!token || button.disabled) return;
      button.disabled = true;
      try {
        await command(`/api/execution/shifts/handoffs/${encodeURIComponent(handoff.handoffId)}/acknowledge`, { note: 'Received by incoming shift' }, 'Incoming operator acknowledged shift pass-on');
        if (!terminal.isCurrent(token) || !host.isConnected) return;
        host.innerHTML = '';
        toast('Shift pass-on acknowledged.');
      } catch (error) {
        if (lockForOutage(error)) return;
        if (!terminal.isCurrent(token) || !host.isConnected) return;
        button.disabled = false;
        toast(error.message, true);
      } finally { terminal.endAction(token); }
    });
  }

  async function start(q, machine, phase, trainerBadge, session) {
    const r = await act('/api/shopfloor/start', { badge: session.badge, orderNumber: q.orderNumber, sequence: q.operation.sequence, machineCode: machine, phase, trainerBadge: trainerBadge ?? null }, null, true, session);
    if (!terminal.isCurrent(session)) return;
    if (!(r instanceof Error)) return;
    if (isServiceUnavailable(r)) return;
    const code = r.problem?.code;
    if (code === 'trainer_required') trainerDialog(q, machine, phase, r.message, session);
    else toast(r.message, true);
  }

  function modal(html) {
    closeDialogs();
    const scrim = document.createElement('div'); scrim.className = 'scrim';
    scrim.setAttribute('aria-hidden', 'true');
    const m = document.createElement('div'); m.className = 'modal'; m.innerHTML = html;
    let releaseFocus = () => {};
    let closed = false;
    const close = () => {
      if (closed) return;
      closed = true;
      scrim.remove(); m.remove(); dialogs.delete(close); releaseFocus();
    };
    dialogs.add(close);
    scrim.onclick = close;
    document.body.append(scrim, m);
    releaseFocus = manageDialogFocus(m, close);
    m.addEventListener('click', onActivity);
    m.addEventListener('keydown', onActivity);
    m.addEventListener('input', onActivity);
    $('[data-cancel]', m)?.addEventListener('click', close);
    return { m, close };
  }

  function trainerDialog(q, machine, phase, message, session) {
    if (!terminal.isCurrent(session)) return;
    const { m, close } = modal(`
      <h2>Training supervision required</h2>
      <div class="notice warn" role="alert">${esc(message)}</div>
      ${scanBox('Trainer: scan your badge', 'trainer')}
      <div class="faint">The trainer's qualifications, trainer certification and presence are checked. Supervised work does not grant the qualification.</div>
      <div class="actions"><button class="btn" data-cancel>Cancel</button></div>`);
    const input = $('#trainer', m);
    input.focus();
    $('form', m).addEventListener('submit', async e => {
      e.preventDefault();
      const trainer = input.value.trim();
      input.value = '';
      if (!trainer) return;
      close();
      await start(q, machine, phase, trainer, session);
    });
  }

  function pinDialog(session) {
    if (!terminal.isCurrent(session)) return;
    const { m, close } = modal(`
      <h2>${terminal.person.hasPin ? 'Change your PIN' : 'Set your PIN'}</h2>
      <div class="muted">Your PIN signs consequential actions, such as a trainer's evaluation. 4 to 8 digits.</div>
      ${terminal.person.hasPin ? `<div class="field"><label for="cur">Current PIN</label><input id="cur" class="input" type="password" inputmode="none" autocomplete="off">${numpad('cur')}</div>` : ''}
      <div class="field"><label for="new">New PIN</label><input id="new" class="input" type="password" inputmode="none" autocomplete="off">${numpad('new')}</div>
      <div class="field"><label for="new2">Repeat new PIN</label><input id="new2" class="input" type="password" inputmode="none" autocomplete="off">${numpad('new2')}</div>
      <div class="notice bad hidden" data-error role="alert" aria-live="assertive"></div>
      <div class="actions"><button class="btn" data-cancel>Cancel</button><button class="btn primary" data-ok>Save PIN</button></div>`);
    wireNumpads(m);
    $('[data-ok]', m).addEventListener('click', async () => {
      const err = $('[data-error]', m);
      const nw = $('#new', m).value, nw2 = $('#new2', m).value;
      if (nw !== nw2) { err.textContent = 'The two PINs differ.'; err.classList.remove('hidden'); return; }
      const token = terminal.beginAction(session);
      if (!token) return;
      const ok = $('[data-ok]', m);
      ok.disabled = true;
      try {
        await command('/api/shopfloor/pin', { badge: token.badge, currentPin: $('#cur', m)?.value || null, newPin: nw });
        if (!terminal.isCurrent(token)) return;
        close(); toast('PIN saved.'); await identify(token.badge, token);
      } catch (e) {
        if (lockForOutage(e)) return;
        if (!terminal.isCurrent(token) || !m.isConnected) return;
        err.textContent = e.message; err.classList.remove('hidden');
      } finally {
        terminal.endAction(token);
        if (m.isConnected) ok.disabled = false;
      }
    });
  }

  function evaluate(sessionId, session) {
    if (!terminal.isCurrent(session)) return;
    const { m, close } = modal(`
      <h2>Evaluate supervised training</h2>
      <div class="muted">Your evaluation is signed with your badge and PIN. It is not certification: a certifier records the qualification separately.</div>
      <div class="field"><label for="ev">Evaluation</label><select id="ev" class="select">
        <option value="ReadyForAssessment">Ready for practical assessment</option><option value="NeedsMoreTraining">Needs more training</option></select></div>
      <div class="field"><label for="notes">Notes</label><textarea id="notes" class="input" rows="3"></textarea></div>
      <div class="field"><label for="epin">Your PIN</label><input id="epin" class="input" type="password" inputmode="none" autocomplete="off">${numpad('epin')}</div>
      <div class="notice bad hidden" data-error role="alert" aria-live="assertive"></div>
      <div class="actions"><button class="btn" data-cancel>Cancel</button><button class="btn primary" data-ok>Sign evaluation</button></div>`);
    wireNumpads(m);
    $('[data-ok]', m).addEventListener('click', async () => {
      const token = terminal.beginAction(session);
      if (!token) return;
      const ok = $('[data-ok]', m);
      ok.disabled = true;
      try {
        const r = await command(`/api/shopfloor/training/${sessionId}/evaluate`, { trainerBadge: token.badge, pin: $('#epin', m).value, evaluation: $('#ev', m).value, notes: $('#notes', m).value || null });
        if (!terminal.isCurrent(token)) return;
        close(); toast(r.message); await identify(token.badge, token);
      } catch (e) {
        if (lockForOutage(e)) return;
        if (!terminal.isCurrent(token) || !m.isConnected) return;
        const err = $('[data-error]', m); err.textContent = e.message; err.classList.remove('hidden');
      } finally {
        terminal.endAction(token);
        if (m.isConnected) ok.disabled = false;
      }
    });
  }

  function wireScan() {
    $$('form[data-scan="badge"]', page).forEach(f => f.addEventListener('submit', e => {
      e.preventDefault();
      const input = $('input', f);
      const value = input.value.trim();
      input.value = '';
      if (value) identify(value);
    }));
  }

  render();
  return { dispose: () => {
    clearTimeout(idle);
    clearTimeout(idleCountdown);
    clearInterval(kioskClockTimer);
    terminal.dispose();
    clearOperatorPhoto();
    clearScanWork();
    workIdentification?.dispose(); workIdentification = null;
    closeDialogs();
    operatorStatus.remove();
    page.removeEventListener('click', onActivity);
    page.removeEventListener('keydown', onActivity);
    page.removeEventListener('input', onActivity);
  } };
}

