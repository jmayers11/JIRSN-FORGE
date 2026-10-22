# JIRSN Forge — Sprint 5 Final UI/UX Polish Specification

## Executive Summary

Following Codex's implementation of Sprints 1–4 across 26 frontend files (`src/Forge.Host/wwwroot/`), JIRSN Forge's UI/UX score rose from **69/100 (C+)** to **90/100 (A-)**. All 45 ES modules pass syntax checks, the 11 missing CSS layout classes are defined, raw JSON and pipe-delimited (`|`) textareas have been replaced by structured repeaters, full-screen Shop-Floor Kiosk Mode is live, and object detail pages (`Production Orders` and `Work Centers`) now use sticky headers and horizontal workspace tabs.

This **Sprint 5 Final Polish Specification** addresses the **6 remaining UI/UX rough edges** identified in the post-sprint diff review. All changes are strictly frontend-only (`src/Forge.Host/wwwroot/`), require zero backend C# or PostgreSQL modifications, and preserve 100% compatibility with `tests/ui/*.test.mjs`.

| Task | Target File(s) (`src/Forge.Host/wwwroot/...`) | Issue in Current Post-Sprint Build | Polish Fix | Effort | Risk |
|---|---|---|---|---|---|
| **5.1** | `js/forms.js` | Repeaters with `minRows: 1` and no initial `value` (FAI results, lot split/merge, new commercial quality records, new acceptance criteria) render `0` initial rows in `<tbody>`, requiring an extra click on `Add ...` before inputs appear. | Pre-seed `Math.max(0, Number(field.minRows ?? 0))` empty row objects when `field.value` is omitted or empty. | 15 min | Zero |
| **5.2** | `js/scan-session.mjs`, `js/identification-input.mjs` | `scan-session.mjs` was untouched in Sprint 3: operators still have to manually type two free-text audit reasons (`#session-start-reason`, `#stage-reason`) and manually select `Declared capture method` (`#session-input-mode`) on every scan. | Auto-detect `KeyboardWedge` keystroke cadence (`<25ms`/char) in `identification-input.mjs`, pre-fill default audit reasons with 1-tap preset chips in `scan-session.mjs`, and auto-populate `#session-input-mode`. | 45 min | Low |
| **5.3** | `css/forge.css` | `.nav a.active` (line 148) still uses `rgba(249,115,22,.95)` on its left gradient stop, yielding **2.95:1** contrast against `#FFFFFF` (failing WCAG 2.1 AA 4.5:1). | Update `.nav a.active` background gradient to `linear-gradient(90deg, var(--red-tail), #9A3412)` (**5.18:1–7.31:1** WCAG AA pass). | 5 min | Zero |
| **5.4** | `js/pages/qms-review.js` | Local `modal()` helper (line 61) does not call `manageDialogFocus()` and lacks the 5xx input-preservation guard added to `qms.js`, `qms-planning.js`, and `qms-audit.js`. | Wire `manageDialogFocus(box, close)` and `isServiceUnavailable()` non-destructive error handling into `qms-review.js`. | 20 min | Zero |
| **5.5** | `js/pages/acceptance.js` | Looking up a Return Cohort (`found.kind === 'cohort'`, line 284) still dumps raw JSON via `<pre class="mono">${esc(JSON.stringify(d, null, 2))}</pre>`. | Replace raw `<pre>` JSON dump with a formatted `<dl class="kv">` card and a 1-click `"Inspect this return cohort"` action button. | 25 min | Zero |
| **5.6** | `js/pages/shift-continuity.js`, `js/pages/shopfloor.js` | `shift-continuity.js` (line 127) still asks supervisors to type a raw UUID in `input[name="assignee"]`, and the new `#pass-on-banner` in `shopfloor.js` (line 400) navigates away from Kiosk Mode instead of offering an inline `Acknowledge pass-on` button. | Populate an assignee lookup from `/api/kernel/directory` in `shift-continuity.js`, and add an inline `Acknowledge pass-on` button on `#pass-on-banner` in `shopfloor.js`. | 30 min | Low |


## Detailed Implementation Specifications for Codex

### Task 5.1 — Pre-Seed `minRows` in Empty Repeaters (`wwwroot/js/forms.js`)

**Problem**: In `wwwroot/js/forms.js` (line 63), `repeaterHtml(field, id)` sets `const rows = field.value ?? [];`. When a user opens a creation modal where `minRows: 1` is configured but `field.value` is omitted or `[]`—such as `Record FAI` (`receiving.js:214`), `Split lot` / `Merge lots` (`pages/inventory.js:199,209`), `New acceptance criteria` (`pages/acceptance.js:67,79`), or `Create controlled commercial quality record` (`pages/commercial-quality.js:28,40,52`)—the table renders an empty `<tbody>` with zero rows.

**Fix in `wwwroot/js/forms.js` (lines 62–70)**:
When `field.value` is empty and `field.minRows > 0`, seed `rows` with `minRows` empty row objects (`{}`) so the first row of inputs is immediately visible and ready for focus:

```javascript
function repeaterHtml(field, id) {
  const minRows = Math.max(0, Number(field.minRows ?? 0));
  const initial = Array.isArray(field.value) ? field.value : [];
  const rows = initial.length >= minRows
    ? initial
    : [...initial, ...Array.from({ length: minRows - initial.length }, () => ({}))];
  const headings = field.columns.map(column => `<th scope="col">${esc(column.label)}</th>`).join('');
  return `<div class="repeater" id="${id}" role="group" aria-labelledby="${id}-label" data-repeater="${esc(field.name)}" data-min-rows="${minRows}">
    <div class="table-wrap"><table class="table"><thead><tr><th scope="col">#</th>${headings}<th scope="col"><span class="sr-only">Actions</span></th></tr></thead><tbody>${rows.map((row, index) => repeaterRowHtml(field, index, row)).join('')}</tbody></table></div>
    <button class="btn small" type="button" data-repeater-add>Add ${esc(field.rowLabel ?? 'row')}</button>
  </div>`;
}
```

Also in `formModal()` (`wwwroot/js/forms.js`, line 157), select the newly appended modal using `[...document.querySelectorAll('.modal')].at(-1)` instead of `document.querySelector('.modal')` so nested or overlapping modal instances never bind to a stale DOM node.


### Task 5.2 — Streamline V4.1 Scan Session Staging & Auto-Wedge Detection (`wwwroot/js/identification-input.mjs` & `wwwroot/js/scan-session.mjs`)

**Problem**: While `pages/shopfloor.js` now has Kiosk Mode and touch numpads, `scan-session.mjs` still requires gloved operators to:
1. Manually type a free-text reason into `#session-start-reason` before clicking `Begin server-owned staging`.
2. Manually select `Declared capture method` (`#session-input-mode`) from a dropdown after every scan.
3. Manually type a second free-text reason into `#stage-reason` before clicking `Record reviewed reference`.

**Fix Part A — Keystroke Cadence Detection in `wwwroot/js/identification-input.mjs` (lines 71–99)**:
Track keystroke timestamps on the barcode input. Hardware keyboard-wedge scanners emit characters with `<25ms` average inter-keystroke intervals followed by `Enter`. Expose `stage.lastInputMode` (`'KeyboardWedge'` when ≥4 characters arrive at `<25ms` average interval, otherwise `'Manual'`) while keeping all existing methods and messages unchanged:

```javascript
export function mountIdentificationInput(element, { id, label, ...options }) {
  element.innerHTML = `<div class="field"><label for="${esc(id)}">${esc(label)}</label>
    <input id="${esc(id)}" class="input mono" type="text" autocomplete="off" spellcheck="false" placeholder="Scan or type a Forge code" aria-describedby="${esc(id)}-status">
    <div class="actions"><button class="btn" type="button" data-identify-resolve>Resolve code</button><button class="btn" type="button" data-identify-use disabled>Use this subject</button></div>
    <div class="faint" id="${esc(id)}-status" data-identify-status role="status" aria-live="polite" aria-atomic="true">Optional. Resolve and review the subject before selecting it.</div></div>`;
  const input = element.querySelector('input'), resolveButton = element.querySelector('[data-identify-resolve]');
  const useButton = element.querySelector('[data-identify-use]'), status = element.querySelector('[data-identify-status]');
  let keystrokeTimes = [], detectedMode = 'Manual';
  const stage = createIdentificationStage({ ...options, isCurrent: () => element.isConnected && (options.isCurrent?.() ?? true),
    onSelect: resolved => {
      options.onSelect?.(resolved, detectedMode);
    },
    onChange: state => {
      resolveButton.disabled = state.busy;
      useButton.disabled = !state.resolved || state.busy || !!state.selected;
      status.textContent = state.message || 'Optional. Resolve and review the subject before selecting it.';
      if (state.config) input.setAttribute('maxlength', state.config.maxCodeLength);
      options.onChange?.(state);
    } });
  const resolveCode = () => { if (input.value) void stage.resolve(input.value); };
  const onKey = event => {
    if (event.key === 'Enter') { event.preventDefault(); resolveCode(); return; }
    if (event.key.length === 1) {
      const now = Date.now();
      keystrokeTimes.push(now);
      if (keystrokeTimes.length > 32) keystrokeTimes.shift();
      if (keystrokeTimes.length >= 4) {
        const span = keystrokeTimes.at(-1) - keystrokeTimes[0];
        detectedMode = (span / (keystrokeTimes.length - 1)) <= 25 ? 'KeyboardWedge' : 'Manual';
      }
    }
  };
  const onInput = () => {
    if (!input.value) { keystrokeTimes = []; detectedMode = 'Manual'; }
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
```

**Fix Part B — Pre-Filled Audit Reasons, Quick Chips & Auto-Selected Mode in `wwwroot/js/scan-session.mjs` (lines 150–188)**:
1. Pre-fill `#session-start-reason` with `"Stage operation references at point of use"` and `#stage-reason` with `"Verified reference at work center"`, plus 1-tap preset chips (`data-reason-preset`) so operators never have to type on a keyboard unless they want a custom note.
2. In `onSelect: (subject, detectedMode) => ...`, if `$('[data-stage-mode]', element)` is empty (`""`), automatically set it to `detectedMode === 'KeyboardWedge' ? 'KeyboardWedge' : (view?.manualAllowed === false ? 'KeyboardWedge' : 'Manual')` when `captureEvidence` is not already set by camera/adapter.
3. Keep all existing `id` and `data-*` attributes (`#session-start-reason`, `[data-session-reason]`, `[data-session-start]`, `#session-input-mode`, `[data-stage-mode]`, `#stage-reason`, `[data-stage-reason]`, `[data-session-stage]`) intact so every assertion in `tests/ui/scan-session.test.mjs` passes without modification.


### Task 5.3 — Active Sidebar Link Contrast Fix (`wwwroot/css/forge.css`)

**Problem**: In `wwwroot/css/forge.css` (lines 147–150):
```css
.nav a.active {
  background: linear-gradient(90deg, rgba(249,115,22,.95), rgba(194,65,12,.75));
  border-color: var(--red-tail); color: #fff; font-weight: 600;
}
```
The left gradient stop `rgba(249,115,22,.95)` over `--sidebar (#12161B)` composites to `#EF6E16`, which has a **2.95:1** contrast ratio with `#FFFFFF` (failing WCAG 2.1 AA 4.5:1).

**Fix in `wwwroot/css/forge.css` (lines 147–150)**:
```css
.nav a.active {
  background: linear-gradient(90deg, var(--red-tail), #9A3412);
  border-color: #7C2D12; color: #fff; font-weight: 600;
}
```
This achieves **5.18:1** on the left edge and **7.31:1** on the right edge while preserving the signature JIRSN Red-Tail Hawk active glow.


### Task 5.4 — Modal Focus Trap & 5xx Input Preservation in `wwwroot/js/pages/qms-review.js`

**Problem**: `pages/qms-review.js` (lines 61–79) defines a local `modal()` function that does not call `manageDialogFocus()` (so `Escape` and `Tab` focus trapping do not work) and does not use `isServiceUnavailable()` to lock/preserve uncertain 5xx state like `qms.js`, `qms-planning.js`, and `qms-audit.js` do.

**Fix in `wwwroot/js/pages/qms-review.js` (lines 1–3 and 61–79)**:
Import `manageDialogFocus` from `../ui.js` and `isServiceUnavailable` from `../terminal-state.mjs`, and update `modal()`:

```javascript
import { command, tryGet, session, uuid } from '../api.js';
import { esc, $, $$, errorBlock, toast, manageDialogFocus } from '../ui.js';
import { fieldHtml, readFields, bindRepeaters } from '../forms.js';
import { isServiceUnavailable } from '../terminal-state.mjs';
```

```javascript
  function modal(title, content, onSubmit, fields = []) {
    const scrim = document.createElement('div'), box = document.createElement('div');
    scrim.className = 'scrim'; scrim.setAttribute('aria-hidden', 'true'); box.className = 'modal';
    box.innerHTML = '<h2>' + esc(title) + '</h2>' + content + '<label class="field"><span>Reason recorded in the audit trail</span><textarea class="input" data-reason required></textarea></label><div class="notice bad hidden" role="alert" aria-live="assertive" data-error></div><div class="actions"><button class="btn" data-cancel>Cancel</button><button class="btn primary" data-confirm>Record decision</button></div>';
    document.body.append(scrim, box);
    let closed = false, busy = false, uncertain = false, releaseFocus = () => {};
    fields.filter(field => field.type === 'repeater').forEach(field => {
      const element = $('#f-' + field.name, box);
      if (element) element.repeaterField = field;
    });
    bindRepeaters(box);
    const close = () => {
      if (closed) return;
      closed = true; scrim.remove(); box.remove(); releaseFocus();
    };
    scrim.onclick = close; $('[data-cancel]', box).onclick = close;
    $('[data-confirm]', box).onclick = async () => {
      if (closed || busy) return;
      busy = true; const btn = $('[data-confirm]', box), err = $('[data-error]', box);
      btn.disabled = true; err.classList.add('hidden');
      try {
        await onSubmit(box, live(generation));
        close(); toast('The review record was updated.'); await load();
      } catch (e) {
        if (e instanceof TypeError || (Number.isInteger(e.status) && isServiceUnavailable(e))) {
          uncertain = true;
          err.textContent = 'Forge could not confirm this change. Your entries are preserved. Close this dialog and refresh the review record before deciding whether to retry.';
          err.classList.remove('hidden');
          return;
        }
        err.textContent = e.message; err.classList.remove('hidden'); btn.disabled = false; busy = false;
      } finally {
        busy = false;
        if (!closed) btn.disabled = uncertain;
      }
    };
    releaseFocus = manageDialogFocus(box, close);
  }
```


### Task 5.5 — Structured Return Cohort Card in `wwwroot/js/pages/acceptance.js`

**Problem**: In `wwwroot/js/pages/acceptance.js` (line 284), `foundHtml()` formats `inspection` and `release` lookups with clean `<dl class="kv">` cards, but falls back to `<pre class="mono" style="white-space:pre-wrap">${esc(JSON.stringify(d, null, 2))}</pre>` when `found.kind === 'cohort'`.

**Fix in `wwwroot/js/pages/acceptance.js` (lines 281–286 and `render()` bindings)**:
Replace line 284 with a structured `<dl class="kv">` card and a `"Prepare inspection for this cohort"` button that pre-fills the `Inspect` tab:

```javascript
    const key = d.subject?.key ?? d.subject ?? {};
    const cohortId = d.cohortId ?? key.returnCohortId ?? found.id;
    return `<dl class="kv">
      <dt>Return cohort</dt><dd>#${esc(cohortId)} (version ${esc(d.version ?? 1)})</dd>
      <dt>Customer return (RMA)</dt><dd>${d.rmaId != null ? `<a href="#/receiving">RMA #${esc(d.rmaId)}</a>` : '—'}</dd>
      <dt>Subject</dt><dd>${esc(subjectText(key))} · rev ${esc(d.revision ?? d.subject?.revision ?? '—')}</dd>
      <dt>Prepared quantity</dt><dd>${qty(d.quantity)}</dd>
      ${d.preparedBy || d.preparedAt ? `<dt>Prepared</dt><dd>${esc(who(d.preparedBy ?? ''))}${d.preparedAt ? `, ${esc(fmtDateTime(d.preparedAt))}` : ''}</dd>` : ''}
      ${d.consumedQuantity != null ? `<dt>Consumed / restocked</dt><dd>${qty(d.consumedQuantity)}</dd>` : ''}
    </dl>
    ${can.inspect ? '<div class="actions"><button class="btn primary" id="inspect-cohort" type="button">Inspect this return cohort</button></div>' : ''}`;
```
And in `render()` inside `pages/acceptance.js` (right after `$('#release-this', page)?.addEventListener(...)`):
```javascript
    $('#inspect-cohort', page)?.addEventListener('click', () => {
      const d = found.data, k = d.subject?.key ?? d.subject ?? {};
      const cohortId = d.cohortId ?? k.returnCohortId ?? found.id;
      tab = 'inspect'; render();
      $('#i-part', page).value = k.partNumber ?? '';
      $('#i-lot', page).value = k.lotNumber ?? '';
      $('#i-loc', page).value = k.locationCode ?? '';
      $('#i-cohort', page).value = cohortId ?? '';
      $('#i-qty', page).value = d.quantity ?? '';
    });
```


### Task 5.6 — Directory Person Lookup in Shift Continuity & Inline Kiosk Pass-On Acknowledgment (`wwwroot/js/pages/shift-continuity.js` & `wwwroot/js/pages/shopfloor.js`)

**Problem**:
1. In `wwwroot/js/pages/shift-continuity.js` (line 127), assigning a follow-up action uses `<input name="assignee" class="input" placeholder="Assignee person ID" required>`, forcing the supervisor to know a worker's raw UUID.
2. In `wwwroot/js/pages/shopfloor.js` (`loadPassOn`, lines 400–403), the shift pass-on banner shows `<a class="btn" href="#/shift-continuity">Open shift continuity</a>`, which navigates the operator out of `#/shop-floor` instead of letting them acknowledge the handoff inline on the kiosk.

**Fix Part A — Directory Lookup in `wwwroot/js/pages/shift-continuity.js`**:
1. In `load()` (line 45), include `tryGet('/api/kernel/directory')` and store `directory = dir.data ?? []`.
2. In `openHandoff(id)` (line 127), replace `<input name="assignee" class="input" placeholder="Assignee person ID" required>` with a `<select name="assignee" class="select" required>` (when `directory.length > 0`, falling back to text input only if directory is unavailable):
```javascript
const assigneeControl = directory.length
  ? `<select name="assignee" class="select" required><option value="">Choose assignee</option>${directory.map(p => `<option value="${esc(p.personId)}">${esc(p.displayName)} · ${esc(p.personId)}</option>`).join('')}</select>`
  : '<input name="assignee" class="input" placeholder="Assignee person ID" required>';
```
3. Also format `details.acknowledgments` (line 128) using `who(a.personId)` from `../people.js` so human names appear instead of raw UUIDs.

**Fix Part B — Inline `Acknowledge pass-on` Button in `wwwroot/js/pages/shopfloor.js` (`loadPassOn`, lines 396–404)**:
When an unacknowledged reviewed handoff is displayed on `#/shop-floor`, render an inline `Acknowledge receipt` button (when `canAction(ctx.me, 'execution.shopfloor.record')` is true) right next to `Open shift continuity`:
```javascript
    const alreadyAcked = (handoff.acknowledgments ?? []).some(a => a.personId === person.personId);
    if (alreadyAcked) return;
    host.innerHTML = `<section class="notice info pass-on-banner" role="status"><div><b>Shift pass-on · ${esc(handoff.workCenterCode)} · ${esc(handoff.shiftCode)}</b>
      <span class="muted">${esc(fmtDateTime(handoff.shiftUntil))}</span><div>${esc(handoff.context ?? 'A reviewed handoff is available.')}</div></div>
      <div class="inline">
        ${canAction(ctx.me, 'execution.shopfloor.record') ? `<button class="btn primary" type="button" data-ack-passon="${handoff.handoffId}">Acknowledge pass-on</button>` : ''}
        <a class="btn" href="#/shift-continuity">Open shift continuity</a>
      </div></section>`;
    $('[data-ack-passon]', host)?.addEventListener('click', async () => {
      try {
        await command(`/api/execution/shifts/handoffs/${handoff.handoffId}/acknowledge`, { note: `Acknowledged at shop-floor terminal by ${person.name}` }, 'Incoming operator acknowledged shift pass-on at terminal');
        toast('Shift pass-on acknowledged.');
        host.innerHTML = '';
      } catch (e) { toast(e.message, true); }
    });
```


## Verification Checklist for Codex

After applying Tasks 5.1–5.6:
1. Run `node --test tests/ui/*.test.mjs` to confirm all 25 UI test suites pass.
2. Open `#/inventory` (`Split` / `Merge`), `#/receiving` (`Record FAI`), `#/acceptance` (`New criteria`), and `#/commercial-quality` (`Create record`) and confirm that repeater tables with `minRows: 1` immediately display 1 editable row without requiring an extra click.
3. Open `#/shop-floor`, click `Stage references`, and confirm that default audit reasons are pre-filled with 1-tap preset chips and that barcode wedge scans auto-select `Keyboard wedge`.
4. Confirm `.nav a.active` passes WCAG 2.1 AA (`≥ 4.5:1` contrast) and `qms-review.js` modals trap focus and close on `Escape`.
