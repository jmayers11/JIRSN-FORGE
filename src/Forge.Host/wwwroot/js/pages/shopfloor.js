import { command, get, tryGet } from '../api.js';
import { img } from '../icons.js';
import { esc, $, $$, fmtNumber, fmtDate, fmtDateTime, relative, toast, emptyBlock, manageDialogFocus, liveRegion, drawer } from '../ui.js';
import { who } from '../people.js';
import { isServiceUnavailable, createTerminalSession } from '../terminal-state.mjs';
import { mountIdentificationInput, workQueueSelection } from '../identification-input.mjs';
import { mountScanSession, operationIdentificationCode } from '../scan-session.mjs';
import { formModal, canAction } from '../forms.js';
import { attachmentsDrawer } from '../attachments.js';
import { appendOrderReconciliation } from '../genealogy.js';

// The shop-floor terminal. A badge scanner types the badge and presses Enter (keyboard wedge); any
// reader that does that works. The badge is held in memory only for this interaction and is
// forgotten after the configured inactivity timeout, or when the next person scans.

const SCRAP_REASONS = ['Edge wave', 'Surface defect', 'Out of tolerance', 'Setup', 'Material defect', 'Damaged in handling'];

export async function shopFloorPage(page, ctx) {
  // V3.5: the plant's scrap reason codes come first; the fixed words remain for plants without codes.
  const scrapCodes = ((await tryGet('/api/execution/reason-codes')).data ?? []).filter(r => r.kind === 'Scrap' && r.active);
  const terminal = createTerminalSession();
  const idleTimeoutSeconds = ctx.me.shopFloorIdleTimeoutSeconds;
  const idleMs = (Number.isInteger(idleTimeoutSeconds) && idleTimeoutSeconds >= 1 && idleTimeoutSeconds <= 3600 ? idleTimeoutSeconds : 60) * 1000;
  let idle = null;
  let idleCountdown = null;
  let idleSecondsRemaining = idleMs / 1000;
  let passOnLoadedFor = null;
  const dialogs = new Set();
  let workIdentification = null;
  let scanWork = null, scanContext = 0;

  function clearScanWork() {
    scanContext++;
    scanWork?.controller?.dispose(); scanWork?.element.remove(); scanWork = null;
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

  function lockTerminal() {
    clearTimeout(idle);
    clearTimeout(idleCountdown);
    idleCountdown = null;
    terminal.clear();
    clearScanWork();
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
      closeDialogs();
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
    workIdentification?.dispose(); workIdentification = null;
    operatorStatus.textContent = terminal.person ? `Current operator: ${terminal.person.displayName}.` : 'Terminal locked. Scan your badge.';
    if (!terminal.person) {
      page.innerHTML = `
        <div class="page-head"><div><h1>Shop Floor</h1><div class="sub">Scan your badge to clock in, start work, report quantities or supervise training.</div></div></div>
        <section class="panel kiosk-start">${img('operator', 'kiosk-ico')}${scanBox('Scan your badge')}
          <div class="faint">A badge says who you are; what you do next is chosen here. Nothing is recorded until you press a button.</div></section>`;
      wireScan();
      $('#badge', page)?.focus();
      return;
    }

    const p = terminal.person;
    const session = terminal.capture();
    const labor = p.labor;
    const clocked = p.clockedInSince;
    const myOp = labor ? p.queue.find(q => q.orderNumber === labor.orderNumber && q.operation.sequence === labor.sequence) : null;
    const awaiting = (p.training ?? []).filter(t => t.trainerId === p.personId && !t.evaluation);
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
      ${!p.isWorker ? emptyBlock('Not a registered worker', 'This badge belongs to someone who is not set up as a worker.') : ''}
      ${labor ? `
      <section class="panel">
        <header><h2>Now working: order #${labor.orderNumber} op ${labor.sequence}</h2><span class="spacer"></span>
          <span class="pill ${labor.role === 'Operator' ? 'info' : 'warn'}">${esc(labor.role)}</span></header>
        <div class="muted" style="margin-bottom:10px">${esc(myOp ? `${myOp.partNumber} · ${myOp.operation.description}` : '')} ${labor.machineCode ? `· ${esc(labor.machineCode)}` : ''} · started ${esc(fmtDateTime(labor.startedAt))}
          ${myOp ? ` · ${fmtNumber(myOp.operation.goodQuantity, 3)} of ${fmtNumber(myOp.orderQuantity, 3)} good so far` : ''}</div>
        ${labor.role === 'Trainer' ? '<div class="notice">You are supervising. The trainee reports quantities; stopping ends the session for both of you.</div>' : `
        <form class="report-form" id="report">
          <div class="field"><label for="good">Good</label><input id="good" class="input big-num" type="number" inputmode="none" min="0" step="any" value="0">${numpad('good', true)}</div>
          <div class="field"><label for="scrap">Scrap</label><input id="scrap" class="input big-num" type="number" inputmode="none" min="0" step="any" value="0">${numpad('scrap', true)}</div>
          <div class="field"><label for="reason">Scrap reason</label>
            <select id="reason" class="select"><option value="">—</option>${scrapCodes.map(r => `<option value="code:${esc(r.code)}">${esc(r.code)} — ${esc(r.description)}</option>`).join('')}${scrapCodes.length ? '' : SCRAP_REASONS.map(r => `<option>${esc(r)}</option>`).join('')}</select></div>
          <label class="check"><input id="complete" type="checkbox"> Operation complete</label>
          <button class="btn primary big" type="submit">Report</button>
        </form>`}
        <div class="shopfloor-actions" style="margin-top:12px"><button class="btn" data-stage-current-work>Stage current operation references</button>
          ${canAction(ctx.me, 'engineering.read') ? '<button class="btn" id="work-instructions" type="button">Work instructions &amp; drawings</button>' : '<button class="btn" type="button" disabled title="Engineering document access is required">Work instructions unavailable</button>'}
          ${canAction(ctx.me, 'inventory.read') ? '<button class="btn" id="material-reconciliation" type="button">Material &amp; remnants</button>' : '<button class="btn" type="button" disabled title="Inventory read access is required">Material view unavailable</button>'}
          ${canAction(ctx.me, 'quality.inspect') ? '<button class="btn danger" id="flag-ncr" type="button">Flag defect / NCR</button>' : ''}
          ${canAction(ctx.me, 'holds.place') ? '<button class="btn danger" id="quality-hold" type="button">Place quality hold</button>' : ''}
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
        ${p.queue.length ? `<table class="table"><thead><tr><th>Order</th><th>Operation</th><th>Machine</th><th>Progress</th><th>Scheduled</th><th></th></tr></thead><tbody>
        ${p.queue.map((q, i) => `<tr>
          <td><b>#${q.orderNumber}</b><br><span class="muted">${esc(q.partNumber)} · due ${esc(fmtDate(q.dueDate))}</span></td>
          <td>${q.operation.sequence} ${esc(q.operation.description)}<br><span class="muted">${esc(q.operation.workCenterCode)}</span></td>
          <td>${q.operation.eligibleMachines.length > 1
            ? `<select class="select" data-machine="${i}" aria-label="Machine for order ${q.orderNumber} operation ${q.operation.sequence}">${q.operation.eligibleMachines.map(m => `<option ${m === q.scheduledMachine ? 'selected' : ''}>${esc(m)}</option>`).join('')}</select>`
            : esc(q.operation.eligibleMachines[0] ?? '—')}</td>
          <td>${fmtNumber(q.operation.goodQuantity, 3)} / ${fmtNumber(q.orderQuantity, 3)}</td>
          <td>${q.scheduledStart ? esc(fmtDateTime(q.scheduledStart)) : '<span class="faint">—</span>'}${q.scheduledForYou ? '<br><span class="pill info">Scheduled for you</span>' : ''}</td>
          <td>${q.hold ? `<span class="pill bad">On hold</span><div class="faint" style="max-width:260px">${esc(q.hold)}</div>` : `<button class="btn" data-stage-work="${i}">Stage references</button> <button class="btn primary" data-start="${i}" ${clocked ? '' : 'disabled title="Clock in first"'}>Start</button>`}</td></tr>`).join('')}
        </tbody></table>` : emptyBlock('Nothing to start', 'No released work at your work center, your assignment or in the schedule for you.')}
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
      start(q, sel ? sel.value : q.operation.eligibleMachines[0] ?? null, null, session);
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
          ${q.hold ? `<span class="pill bad">On hold</span><div class="faint">${esc(q.hold)}</div>` : `<button class="btn" data-identified-stage="${i}">Stage selected operation</button> <button class="btn primary" data-identified-start="${i}" ${clocked ? '' : 'disabled title="Clock in first"'}>Start selected work</button>`}</div>`).join('')}`;
        $$('[data-identified-stage]', selected).forEach(button => button.addEventListener('click', () => { if (!terminal.isCurrent(session) || terminal.busy) return; const index = Number(button.dataset.identifiedStage), q = choices[index]; stageWork(q, session, $(`#identified-machine-${index}`, selected)?.value ?? q.operation.eligibleMachines[0] ?? null); }));
        choices.forEach((q, index) => $(`#identified-machine-${index}`, selected)?.addEventListener('change', event => { if (scanWork?.work.orderNumber === q.orderNumber && scanWork.work.operation.sequence === q.operation.sequence && scanWork.machineCode !== event.target.value) clearScanWork(); }));
        $$('[data-identified-start]', selected).forEach(button => button.addEventListener('click', () => {
          if (!terminal.isCurrent(session) || terminal.busy || !workIdentification?.stage.selected) return;
          const index = Number(button.dataset.identifiedStart), q = choices[index];
          const machine = $(`#identified-machine-${index}`, selected)?.value ?? q.operation.eligibleMachines[0] ?? null;
          workIdentification.stage.clear();
          start(q, machine, null, session);
        }));
      },
    });
    if (clocked) loadPassOn(session, p, myOp?.operation.workCenterCode ?? p.homeWorkCenter);
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

  async function start(q, machine, trainerBadge, session) {
    const r = await act('/api/shopfloor/start', { badge: session.badge, orderNumber: q.orderNumber, sequence: q.operation.sequence, machineCode: machine, trainerBadge: trainerBadge ?? null }, null, true, session);
    if (!terminal.isCurrent(session)) return;
    if (!(r instanceof Error)) return;
    if (isServiceUnavailable(r)) return;
    const code = r.problem?.code;
    if (code === 'trainer_required') trainerDialog(q, machine, r.message, session);
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

  function trainerDialog(q, machine, message, session) {
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
      await start(q, machine, trainer, session);
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
    terminal.dispose();
    clearScanWork();
    workIdentification?.dispose(); workIdentification = null;
    closeDialogs();
    operatorStatus.remove();
    page.removeEventListener('click', onActivity);
    page.removeEventListener('keydown', onActivity);
    page.removeEventListener('input', onActivity);
  } };
}

