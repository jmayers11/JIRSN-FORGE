# JIRSN Forge — Sprint 8 Supervisor Kanban Employee Editor & V8 Polish Specification

## Executive Summary

In **V8 (`Forge.Host NEWV8`)**, JIRSN Forge introduced the **Supervisor Plant Overview (`#/supervisor`)**, the dual-mode **Workforce Kanban & Spatial Plant Floor Map (`#/workforce-kanban`)**, the 4-lane **Machine Readiness Kanban (`#/machine-kanban`)**, and the audited binary **Plant Visual Service (`PlantVisualService.cs` / `PlantVisualEndpoints.cs`)**, raising the overall platform score to **98/100 (A+)**.

However, when a Supervisor or Manager clicks an employee card on **`#/workforce-kanban`**, the expanded card (`cardHtml` in `wwwroot/js/pages/workforce-kanban.js:437–463`) currently only stages a hypothetical move or links to `Model absence scenario`. To update that employee's **training/qualifications**, **upload/replace their photo**, **change their official job title**, **confirm presence / record a callout**, or **apply a live assignment**, the supervisor has to leave the Kanban board, open `#/workforce`, scroll to the bottom of the page, and manually re-select the employee from the `#wf-person` dropdown.

This **Sprint 8 Specification** is 100% frontend-only (`wwwroot/js/pages/workforce-kanban.js`, `wwwroot/js/pages/workforce.js`, `wwwroot/js/pages/workforce-admin.js`, `wwwroot/js/pages/machine-kanban.js`, `wwwroot/js/executable.js`, and `wwwroot/css/forge.css`), requires **zero C# backend changes** (all endpoints already exist in `OperationsEndpoints.cs` and `PlantVisualEndpoints.cs`), and closes both the **Inline Employee Card Management** workflow and the **4 V8 UI/WCAG polish items** to bring V8 to a clean **100/100**.

| Task | Target File(s) (`src/Forge.Host/wwwroot/...`) | Capability / Fix Added | Existing Backend Endpoint(s) Used | Effort | Risk |
|---|---|---|---|---|---|
| **8.1** | `js/pages/workforce-kanban.js`, `css/forge.css` | **Inline Employee Card Editor & Training Drawer on `#/workforce-kanban`**: Clicking any employee card reveals permission-gated buttons to **Change Title**, **Upload/Remove Photo**, open an interactive **Training & Qualifications Drawer** (start training, certify, suspend/revoke, record document training), **Confirm Presence / Record Callout**, or **Apply Live Assignment** without leaving the Kanban board. | `POST /api/workforce/workers/{id}/job-title`, `POST/DELETE .../photo`, `GET/POST /api/competency/people/{id}/...`, `POST /api/engineering/document-training`, `POST .../presence`, `POST .../unavailability`, `POST .../assignment` | 35 min | Zero |
| **8.2** | `js/pages/workforce.js`, `js/pages/workforce-admin.js`, `js/executable.js` | **Deep-Link Auto-Selection (`#/workforce?personId=...`) & Searchable Document Training Picker**: Clicking **`Open full profile →`** on any employee card in `#/workforce-kanban` opens `#/workforce` with that employee automatically selected and scrolled into view in `Manage people`, and replaces the raw `personId` UUID text box in `appendDocumentTraining()` with a directory `<select>`. | `GET /api/kernel/directory`, `GET /api/competency/people/{id}`, `GET /api/workforce/moves`, `GET /api/workforce/transfers` | 15 min | Zero |
| **8.3** | `css/forge.css` | **Dark-Mode WCAG 2.1 AA Contrast Fix for V8 Status Text**: Replaces light-mode dark-ink hex colors on `.machine-status.bad/warn/ok` (2.06:1–2.42:1) and `.staff-presence-tag` (3.20:1) with high-contrast dark-surface tokens (**8.98:1 to 11.75:1**). | Pure CSS | 5 min | Zero |
| **8.4** | `js/pages/machine-kanban.js`, `js/pages/workforce-kanban.js` | **Machine Readiness KPI Strip & Lane Count Pills + Accordion State Preservation**: Renders the 4-card `.machine-board-kpis` strip and lane count pills on `#/machine-kanban`, and keeps `<details>` accordions open in `renderFloorMap()` when expanding an employee card inside them. | Pure JS DOM | 15 min | Zero |


## Detailed Implementation Specifications for Codex

### Task 8.1 — Inline Employee Management Bar & Training/Qualifications Drawer on `#/workforce-kanban` (`wwwroot/js/pages/workforce-kanban.js` & `wwwroot/css/forge.css`)

**Problem**: In `wwwroot/js/pages/workforce-kanban.js` (`cardHtml`, lines 437–463), clicking an employee expands `cardHtml(worker, lane)`, which only shows `.staff-card-proposal-controls` (`Proposed destination` + `Stage move` + `Model absence scenario`). Supervisors and managers cannot change the employee's title, upload/replace their photo, update their training/certifications, confirm on-site presence, record a callout, or commit the staged assignment directly from the card.

#### Step 8.1A — Update Imports in `wwwroot/js/pages/workforce-kanban.js` (`lines 1–4`)
Import `drawer` from `../ui.js` and `formModal` from `../forms.js`:

```javascript
import { command, fetchFile, readPost, removeVisualImage, tryGet, uploadVisualImage } from '../api.js';
import { esc, $, $$, errorBlock, emptyBlock, fmtDateTime, toast, confirmWithReason, drawer } from '../ui.js';
import { canAction, formModal } from '../forms.js';
import { stagePhoto } from '../icons.js';
```

#### Step 8.1B — Upgrade `cardHtml(worker, lane)` in `wwwroot/js/pages/workforce-kanban.js` (`lines 437–463`)
Inside `cardHtml(worker, lane)`, add a permission-aware **Employee Quick-Manage Bar** (`.staff-card-manage`) inside the `expanded` block right below `.staff-card-proposal-controls`:

```javascript
  function renderQuickManageBar(worker, move) {
    const canTitle = canAction(ctx.me, 'workforce.manage');
    const canPhoto = canAction(ctx.me, 'workforce.photos.manage');
    const canSupervise = canAction(ctx.me, 'workforce.supervise');
    const canTraining = ['competency.read', 'competency.manage', 'competency.certify'].some(p => canAction(ctx.me, p));
    const canApplyMove = canSupervise && move && !moveIncomplete(move);

    return `<div class="staff-card-manage" role="group" aria-label="Manage ${esc(worker.displayName)}">
      <div class="staff-card-manage-head">Manage employee</div>
      <div class="staff-card-manage-actions">
        ${canTraining ? `<button class="btn small" type="button" data-emp-training="${esc(worker.personId)}">Training &amp; quals</button>` : ''}
        ${canTitle ? `<button class="btn small" type="button" data-emp-title="${esc(worker.personId)}">Edit title</button>` : ''}
        ${canPhoto ? `<label class="btn small staff-photo-upload-btn">${worker.hasPhoto ? 'Replace photo' : 'Upload photo'}<input type="file" accept="image/png,image/jpeg,image/webp" data-emp-photo-upload="${esc(worker.personId)}" aria-label="Upload photo for ${esc(worker.displayName)}"></label>` : ''}
        ${canPhoto && worker.hasPhoto ? `<button class="btn small ghost" type="button" data-emp-photo-remove="${esc(worker.personId)}">Remove photo</button>` : ''}
        ${canSupervise && !confirmedOnSite(worker) ? `<button class="btn small" type="button" data-emp-presence="${esc(worker.personId)}">Confirm on site</button>` : ''}
        ${canSupervise ? `<button class="btn small" type="button" data-emp-unavailable="${esc(worker.personId)}">Record callout / PTO</button>` : ''}
        ${canApplyMove ? `<button class="btn small primary" type="button" data-emp-apply-move="${esc(worker.personId)}">Apply live assignment</button>` : ''}
        <a class="btn small ghost" href="#/workforce?personId=${encodeURIComponent(worker.personId)}">Open full profile →</a>
      </div>
    </div>`;
  }
```

And in `cardHtml(worker, lane)` (`line 461`), include `${renderQuickManageBar(worker, move)}` inside the `expanded` block:

```javascript
      ${expanded ? `<div class="staff-card-proposal-controls">${controls}<div class="staff-card-scenario-row"><a class="btn small" href="#/scenarios?kind=PersonUnavailable&amp;personId=${encodeURIComponent(worker.personId)}&amp;scenarioName=${encodeURIComponent(`Model absence · ${worker.displayName}`)}">Model absence scenario</a></div>${renderQuickManageBar(worker, move)}</div>` : ''}
```

#### Step 8.1C — Add Helper Functions for Title, Photo, Presence, Callout, Live Assignment & Training Drawer in `wwwroot/js/pages/workforce-kanban.js`
Add these functions inside `workforceKanbanPage(page, ctx)` (right before `wireCards()`):

```javascript
  function editWorkerTitle(worker) {
    formModal({
      title: `Set ${worker.displayName}'s official title`,
      detail: 'Displayed beneath their name across Workforce Kanban and the plant floor map. Leave blank to clear.',
      confirmLabel: 'Save title',
      reasonRequired: true,
      fields: [
        { name: 'jobTitle', label: 'Official title', value: worker.jobTitle ?? '', placeholder: 'Production Operator' },
      ],
    }, (v, reason) => command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/job-title`, { jobTitle: v.jobTitle || null }, reason), () => load());
  }

  function confirmWorkerPresence(worker) {
    formModal({
      title: `Record ${worker.displayName}'s presence`,
      detail: 'Only clocked-in or supervisor-confirmed presence counts as on site.',
      confirmLabel: 'Record presence',
      reasonRequired: true,
      fields: [
        { name: 'kind', label: 'Basis', type: 'select', value: 'SupervisorConfirmed', options: [['SupervisorConfirmed', 'Supervisor confirmed on site'], ['ClockedIn', 'Clocked in'], ['Projected', 'Projected (planning only)']] },
        { name: 'validUntil', label: 'Valid until (optional)', type: 'datetime-local' },
      ],
    }, (v, reason) => command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/presence`, {
      kind: v.kind,
      validUntil: v.validUntil ? new Date(v.validUntil).toISOString() : null,
    }, reason), () => load());
  }

  function recordWorkerUnavailable(worker) {
    const now = new Date();
    const end = new Date(now.getTime() + 8 * 3600 * 1000);
    const toLocalInput = d => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
    formModal({
      title: `Record ${worker.displayName} unavailable`,
      detail: 'Records a call-out, PTO, training window, or unavailability and updates the Kanban planning rail.',
      confirmLabel: 'Record unavailability',
      reasonRequired: true,
      fields: [
        { name: 'kind', label: 'Kind', type: 'select', value: 'CallOut', options: [['CallOut', 'Call-out'], ['Unavailable', 'Unavailable'], ['ApprovedPto', 'Approved time off'], ['Training', 'Training']] },
        { name: 'startsAt', label: 'From', type: 'datetime-local', value: toLocalInput(now), required: true },
        { name: 'endsAt', label: 'To', type: 'datetime-local', value: toLocalInput(end), required: true },
      ],
    }, (v, reason) => {
      const startsAt = v.startsAt ? new Date(v.startsAt).toISOString() : null;
      const endsAt = v.endsAt ? new Date(v.endsAt).toISOString() : null;
      if (!startsAt || !endsAt || endsAt <= startsAt) throw new Error('Enter a start and a later end.');
      return command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/unavailability`, { kind: v.kind, startsAt, endsAt }, reason);
    }, () => load());
  }

  async function applyLiveMove(worker) {
    const move = state.moves.get(worker.personId);
    if (!move || moveIncomplete(move)) return;
    if (move.unassign) {
      const ok = await confirmWithReason({
        title: `End assignment for ${worker.displayName}`,
        detail: `${worker.displayName} will be unassigned from ${worker.assignment?.workCenterCode ?? 'their current work'}.`,
        confirmLabel: 'End live assignment',
        reasonRequired: true,
      }, reason => command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/assignment/end`, {}, reason));
      if (ok) { state.moves.delete(worker.personId); await load(); }
      return;
    }
    const ok = await confirmWithReason({
      title: `Assign ${worker.displayName} to ${move.workCenterCode}`,
      detail: `Commits live assignment: ${[move.activityCode, move.machineCode, move.workCenterCode].filter(Boolean).join(' · ')}.`,
      confirmLabel: 'Apply live assignment',
      reasonRequired: true,
    }, reason => command(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/assignment`, {
      safetyAreaCode: move.safetyAreaCode || null,
      workCenterCode: move.workCenterCode || null,
      machineCode: move.machineCode || null,
      activityCode: move.activityCode,
      asTrainee: false,
    }, reason));
    if (ok) { state.moves.delete(worker.personId); await load(); }
  }

  async function openEmployeeTrainingDrawer(worker) {
    const canManageQual = canAction(ctx.me, 'competency.manage');
    const canCertify = canAction(ctx.me, 'competency.certify');
    const statusTone = { InTraining: 'info', Qualified: 'ok', Suspended: 'warn', Revoked: 'bad' };

    const [qualsRes, typesRes, docTrainRes] = await Promise.all([
      tryGet(`/api/competency/people/${encodeURIComponent(worker.personId)}`),
      tryGet('/api/competency/qualification-types'),
      tryGet(`/api/engineering/document-training?personId=${encodeURIComponent(worker.personId)}`),
    ]);
    const types = typesRes.data ?? [];
    const typeName = code => types.find(t => t.code === code)?.name ?? code;
    const typeOptions = () => types.map(t => [t.code, `${t.name} (${t.code})`]);
    const quals = qualsRes.data ?? [];
    const docRecords = (docTrainRes.data?.records ?? []).filter(r => !r.personId || r.personId === worker.personId);

    const box = drawer(`Training & qualifications · ${worker.displayName}`, `
      <div class="stack" id="emp-training-drawer">
        <div class="row">
          <div><b>${esc(worker.displayName)}</b> <span class="muted">· ${esc(worker.jobTitle || 'Official title not set')}</span><div class="faint">Home work center: ${esc(worker.homeWorkCenterCode ?? '—')} · Shift: ${esc(worker.shiftCode ?? '—')}</div></div>
          <div class="inline">
            ${canManageQual ? '<button class="btn small primary" type="button" id="drawer-start-training">+ Start training</button>' : ''}
            ${canCertify ? '<button class="btn small" type="button" id="drawer-certify">+ Certify</button>' : ''}
            ${canManageQual ? '<button class="btn small" type="button" id="drawer-doc-training">+ Document training</button>' : ''}
          </div>
        </div>
        <section class="panel">
          <header><h3>Qualifications (${quals.length})</h3></header>
          ${qualsRes.error ? errorBlock(qualsRes.error, 'qualifications') : !quals.length ? '<div class="muted">No qualifications recorded for this employee yet.</div>' : `
          <table class="table"><thead><tr><th>Qualification</th><th>Status</th><th>Certified</th><th>Expires</th><th></th></tr></thead><tbody>
            ${quals.map(q => `<tr>
              <td><b>${esc(typeName(q.code))}</b> <span class="mono faint">${esc(q.code)}</span></td>
              <td><span class="pill ${statusTone[q.status] ?? 'none'}">${esc(q.status)}</span></td>
              <td>${q.certifiedAt ? esc(fmtDateTime(q.certifiedAt)) : '—'}</td>
              <td>${q.expiresAt ? esc(fmtDateTime(q.expiresAt)) : '—'}</td>
              <td class="row-actions">
                ${canCertify && q.status === 'InTraining' ? `<button class="btn small" type="button" data-drawer-qual="certify" data-code="${esc(q.code)}">Certify</button>` : ''}
                ${canCertify && q.status === 'Qualified' ? `<button class="btn small ghost" type="button" data-drawer-qual="suspensions" data-code="${esc(q.code)}">Suspend</button>` : ''}
                ${canCertify && (q.status === 'Qualified' || q.status === 'Suspended') ? `<button class="btn small ghost" type="button" data-drawer-qual="revocations" data-code="${esc(q.code)}">Revoke</button>` : ''}
              </td>
            </tr>`).join('')}
          </tbody></table>`}
        </section>
        <section class="panel">
          <header><h3>Controlled document training (${docRecords.length})</h3></header>
          ${docTrainRes.error ? errorBlock(docTrainRes.error, 'document training') : !docRecords.length ? '<div class="muted">No controlled document training recorded for this employee.</div>' : `
          <table class="table"><thead><tr><th>Document</th><th>Revision</th><th>Trained on</th></tr></thead><tbody>
            ${docRecords.map(r => `<tr><td class="mono">${esc(r.documentNumber)}</td><td class="mono">${esc(r.revision)}</td><td>${esc(r.trainedOn ?? '—')}</td></tr>`).join('')}
          </tbody></table>`}
        </section>
      </div>
    `);

    const refreshDrawer = async () => {
      await load();
      await openEmployeeTrainingDrawer(state.workers.find(w => w.personId === worker.personId) ?? worker);
    };

    $('#drawer-start-training', box)?.addEventListener('click', () => formModal({
      title: `Start ${worker.displayName}'s training`,
      fields: [{ name: 'code', label: 'Qualification', type: 'select', required: true, blank: '— choose —', options: typeOptions() }],
      confirmLabel: 'Start training',
      reasonRequired: true,
    }, (v, reason) => command(`/api/competency/people/${encodeURIComponent(worker.personId)}/training`, { code: v.code }, reason), refreshDrawer));

    const openCertifyModal = code => formModal({
      title: `Certify ${worker.displayName}`,
      detail: 'Certification requires evidence. Prerequisites must already be held.',
      fields: [
        { name: 'code', label: 'Qualification', type: 'select', required: true, blank: '— choose —', value: code ?? '', options: typeOptions() },
        { name: 'evidence', label: 'Evidence', type: 'textarea', required: true },
      ],
      confirmLabel: 'Certify',
      reasonRequired: true,
    }, (v, reason) => command(`/api/competency/people/${encodeURIComponent(worker.personId)}/certifications`, { code: v.code, evidence: v.evidence }, reason), refreshDrawer);

    $('#drawer-certify', box)?.addEventListener('click', () => openCertifyModal(null));

    $('#drawer-doc-training', box)?.addEventListener('click', () => formModal({
      title: `Record document training for ${worker.displayName}`,
      confirmLabel: 'Record training',
      reasonRequired: true,
      fields: [
        { name: 'documentNumber', label: 'Document number', code: true, required: true, placeholder: 'WI-SLIT-01' },
        { name: 'revision', label: 'Revision', required: true, placeholder: 'A' },
        { name: 'trainedOn', label: 'Trained on', type: 'date' },
      ],
    }, (v, reason) => command('/api/engineering/document-training', {
      personId: worker.personId,
      documentNumber: v.documentNumber,
      revision: v.revision,
      trainedOn: v.trainedOn || null,
    }, reason), refreshDrawer));

    $$('[data-drawer-qual]', box).forEach(btn => btn.addEventListener('click', () => {
      const kind = btn.dataset.drawerQual;
      const code = btn.dataset.code;
      if (kind === 'certify') return openCertifyModal(code);
      const word = kind === 'suspensions' ? 'Suspend' : 'Revoke';
      formModal({
        title: `${word} ${typeName(code)} for ${worker.displayName}`,
        detail: kind === 'suspensions' ? 'They cannot perform work requiring this qualification until reinstated.' : 'Revocation is final; a new certification is needed.',
        fields: [],
        confirmLabel: word,
        reasonRequired: true,
      }, (_, reason) => command(`/api/competency/people/${encodeURIComponent(worker.personId)}/${kind}`, { code }, reason), refreshDrawer);
    }));
  }
```

#### Step 8.1D — Bind the Quick-Manage Controls in `wireCards()` (`wwwroot/js/pages/workforce-kanban.js`, line 637)
Inside `wireCards()`, add the event listeners for `[data-emp-training]`, `[data-emp-title]`, `[data-emp-photo-upload]`, `[data-emp-photo-remove]`, `[data-emp-presence]`, `[data-emp-unavailable]`, and `[data-emp-apply-move]`:

```javascript
    $$('[data-emp-training]', page).forEach(button => button.addEventListener('click', () => {
      const worker = state.workers.find(item => item.personId === button.dataset.empTraining);
      if (worker) void openEmployeeTrainingDrawer(worker);
    }));
    $$('[data-emp-title]', page).forEach(button => button.addEventListener('click', () => {
      const worker = state.workers.find(item => item.personId === button.dataset.empTitle);
      if (worker) editWorkerTitle(worker);
    }));
    $$('[data-emp-photo-upload]', page).forEach(input => input.addEventListener('change', async () => {
      const file = input.files?.[0];
      const worker = state.workers.find(item => item.personId === input.dataset.empPhotoUpload);
      if (!file || !worker) return;
      try {
        const result = await confirmWithReason({
          title: `Upload photo for ${worker.displayName}`,
          detail: 'The photo is visible to people who can read the workforce board. Use a clear, current headshot.',
          confirmLabel: 'Upload photo',
          reasonRequired: true,
        }, reason => uploadVisualImage(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/photo`, file, reason));
        if (result) { clearVisual(`worker:${worker.personId}`); await load(); }
      } catch (error) { toast(error.message || 'Photo upload failed.', true); }
      finally { input.value = ''; }
    }));
    $$('[data-emp-photo-remove]', page).forEach(button => button.addEventListener('click', async () => {
      const worker = state.workers.find(item => item.personId === button.dataset.empPhotoRemove);
      if (!worker) return;
      try {
        const result = await confirmWithReason({
          title: `Remove photo for ${worker.displayName}`,
          detail: 'The worker will show initials on the Kanban board and plant map.',
          confirmLabel: 'Remove photo',
          reasonRequired: true,
        }, reason => removeVisualImage(`/api/workforce/workers/${encodeURIComponent(worker.personId)}/photo`, reason));
        if (result) { clearVisual(`worker:${worker.personId}`); await load(); }
      } catch (error) { toast(error.message || 'Photo removal failed.', true); }
    }));
    $$('[data-emp-presence]', page).forEach(button => button.addEventListener('click', () => {
      const worker = state.workers.find(item => item.personId === button.dataset.empPresence);
      if (worker) confirmWorkerPresence(worker);
    }));
    $$('[data-emp-unavailable]', page).forEach(button => button.addEventListener('click', () => {
      const worker = state.workers.find(item => item.personId === button.dataset.empUnavailable);
      if (worker) recordWorkerUnavailable(worker);
    }));
    $$('[data-emp-apply-move]', page).forEach(button => button.addEventListener('click', () => {
      const worker = state.workers.find(item => item.personId === button.dataset.empApplyMove);
      if (worker) void applyLiveMove(worker);
    }));
```


### Task 8.2 — Deep-Link Auto-Selection (`#/workforce?personId=...`) & Directory Dropdown in Document Training (`wwwroot/js/pages/workforce.js`, `wwwroot/js/pages/workforce-admin.js`, `wwwroot/js/executable.js`)

**Problem**:
1. When clicking `Open full profile →` (`#/workforce?personId=...`) from an employee card on Workforce Kanban, `workforcePage` (`pages/workforce.js:35`) calls `await appendWorkforceAdmin(page, ctx.me)` without passing `ctx.query.get('personId')`, so the supervisor lands on `#/workforce` with nobody selected.
2. In `wwwroot/js/executable.js` (`appendDocumentTraining`, line 104), clicking `+ Training` still asks the supervisor to manually type a 36-character UUID into `{ name: 'personId', label: 'Person ID', required: true }`.

**Fix in `wwwroot/js/pages/workforce.js` (`line 35`)**:
Pass `ctx.query.get('personId')` to `appendWorkforceAdmin`:
```javascript
  await appendWorkforceAdmin(page, ctx.me, ctx.query.get('personId'));
```

**Fix in `wwwroot/js/pages/workforce-admin.js` (`line 51` & `line 299`)**:
Accept `initialPersonId = null` in `export async function appendWorkforceAdmin(page, me, initialPersonId = null)` and at the end of `appendWorkforceAdmin` (`line 299`), auto-select and scroll to the employee if `initialPersonId` is provided:
```javascript
  if (initialPersonId && (people.data ?? []).some(p => p.personId === initialPersonId)) {
    await select(initialPersonId);
    section.scrollIntoView({ behavior: 'smooth', block: 'start' });
  } else {
    render();
  }
  return section;
```

**Fix in `wwwroot/js/executable.js` (`appendDocumentTraining`, lines 92–108)**:
Fetch `tryGet('/api/kernel/directory')` alongside `/api/engineering/document-training` and populate a `<select>` of employees (falling back to text input only if directory is empty/restricted):
```javascript
  const load = async () => {
    const [{ data, error }, dirRes] = await Promise.all([
      tryGet('/api/engineering/document-training'),
      tryGet('/api/kernel/directory'),
    ]);
    const directory = dirRes.data ?? [];
    const personField = directory.length
      ? { name: 'personId', label: 'Employee', type: 'select', required: true, blank: '— choose employee —', options: directory.map(p => [p.personId, `${p.displayName} (${p.personId.slice(0, 8)})`]) }
      : { name: 'personId', label: 'Person ID', required: true };
```
Use `personField` inside `$$('[data-train-add]', section)` so supervisors pick an employee by name instead of typing a raw UUID.


### Task 8.3 — Dark-Mode WCAG 2.1 AA Contrast Fix for V8 Status & Presence Classes (`wwwroot/css/forge.css`)

**Problem**: In `wwwroot/css/forge.css`:
* Line 845: `.staff-presence-tag { color: #15803D; font-weight: 650; }` has **3.20:1–3.42:1** contrast against `--panel-2 (#1C2229)` and `--panel (#171C22)` (fails WCAG 2.1 AA 4.5:1).
* Lines 899–901: `.machine-status.bad { color: #991B1B; }`, `.machine-status.warn { color: #92400E; }`, and `.machine-status.ok { color: #166534; }` have **2.06:1 to 2.42:1** contrast against `--panel (#171C22)`.

**Fix in `wwwroot/css/forge.css` (`lines 845` and `899–901`, plus styles for `.staff-card-manage`)**:

```css
/* Fix dark-surface contrast on V8 presence and machine status labels (WCAG 2.1 AA >= 4.5:1) */
.staff-presence-tag { color: #4ADE80; font-weight: 650; }
.machine-status.bad { color: #FCA5A5; }
.machine-status.warn { color: #FCD34D; }
.machine-status.ok { color: #4ADE80; }

/* Employee Quick-Manage Bar inside expanded Workforce Kanban cards */
.staff-card-scenario-row { margin-top: 8px; }
.staff-card-manage {
  margin-top: 10px;
  padding-top: 8px;
  border-top: 1px dashed var(--line);
}
.staff-card-manage-head {
  font: 700 10px var(--font-display);
  text-transform: uppercase;
  letter-spacing: .06em;
  color: var(--muted);
  margin-bottom: 6px;
}
.staff-card-manage-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}
.staff-photo-upload-btn {
  position: relative;
  overflow: hidden;
  cursor: pointer;
}
.staff-photo-upload-btn input[type="file"] {
  position: absolute;
  inset: 0;
  opacity: 0;
  cursor: pointer;
}
```


### Task 8.4 — Machine Readiness KPI Strip, Lane Count Badges & Floor-Map Accordion Persistence (`wwwroot/js/pages/machine-kanban.js` & `wwwroot/js/pages/workforce-kanban.js`)

**Problem**:
1. In `wwwroot/js/pages/machine-kanban.js` (`render()`, line 198, and `renderLanes()`, line 230), neither the top KPI strip (`.machine-board-kpis`) nor the 4 lane headers display the count of machines in each signal state (`blocked`, `attention`, `ready`, `unknown`).
2. In `wwwroot/js/pages/workforce-kanban.js` (`renderFloorMap()`, lines 432–433), expanding a worker card inside `<details class="workforce-unassigned-zone">` or `<details class="workforce-offsite">` causes `render()` to close the `<details>` element.

**Fix Part A — Add KPI Summary Strip & Lane Count Pills in `wwwroot/js/pages/machine-kanban.js` (`lines 198–235`)**:
In `render()` (`machine-kanban.js:204`), render the 4-card `.machine-board-kpis` summary right above `Find or filter machines`:
```javascript
      <section class="kpis machine-board-kpis" aria-label="Machine readiness counts">
        ${LANE_ORDER.map(key => {
          const count = shown.filter(card => card.status === key).length;
          return `<div class="kpi ${key}"><div class="k">${esc(LANE[key].title)}</div><div class="v">${count}</div></div>`;
        }).join('')}
      </section>
```
And in `renderLanes(list)` (`machine-kanban.js:230`), include a count pill in each lane `<header>`:
```javascript
        <header><h2 id="machine-lane-${key}"><span class="machine-signal-dot ${key}" aria-hidden="true">${lane.icon}</span>${esc(lane.title)}</h2><span class="pill ${lane.tone}">${items.length}</span></header>
```

**Fix Part B — Preserve `<details>` Open State in `renderFloorMap()` (`wwwroot/js/pages/workforce-kanban.js:432–433`)**:
In `renderFloorMap()`, keep the `<details>` elements open whenever the currently expanded worker is inside them:
```javascript
    const unassignedOpen = presentUnassigned.some(w => w.personId === state.expandedPersonId) ? 'open' : '';
    const offsiteOpen = notPresent.some(w => w.personId === state.expandedPersonId) ? 'open' : '';
    return `<div class="workforce-floor-map">${siteCards || emptyBlock('No work centers in this area', 'Set up work centers under Plant Setup.')}
      ${presentUnassigned.length ? `<details class="workforce-unassigned-zone" ${unassignedOpen}><summary>On site with no assigned work center · ${presentUnassigned.length}</summary><div class="workforce-floor-people">${presentUnassigned.map(worker => workerChip(worker, null)).join('')}</div></details>` : ''}
      ${notPresent.length ? `<details class="workforce-offsite" ${offsiteOpen}><summary>Not currently confirmed on site · ${notPresent.length}</summary><div class="workforce-offsite-list">${notPresent.map(worker => `<div class="workforce-offsite-person">${cardHtml(worker, centerByCode(destination(worker)))}<span class="muted small">${esc(centerByCode(destination(worker))?.name ?? 'No work center recorded')}</span></div>`).join('')}</div></details>` : ''}
    </div>`;
```


## Verification Checklist for Codex

1. **Syntax & Test Suite Verification**:
   * Run `node --check` on `workforce-kanban.js`, `machine-kanban.js`, `workforce.js`, `workforce-admin.js`, and `executable.js`.
   * Run `node --test tests/ui/*.test.mjs` and confirm all UI tests pass.
2. **Functional Smoke Verification**:
   * Open `#/workforce-kanban` as a Supervisor or Manager and click any employee chip on the **4×4 Staffing Board**, the **Planning Rail**, or a **Plant Map pin**:
     * Verify the expanded card displays **Manage employee** actions (`Training & quals`, `Edit title`, `Upload photo` / `Remove photo`, `Confirm on site`, `Record callout / PTO`, `Apply live assignment`, and `Open full profile →`).
     * Click **`Training & quals`** and verify the slide-out drawer lists current qualifications and controlled document training, and lets you start training, certify, suspend/revoke, or record document training inline.
     * Click **`Edit title`** and **`Upload photo`** and verify the employee's title and circular avatar update immediately on the Kanban board and Plant Map.
     * Click **`Open full profile →`** and verify `#/workforce?personId=...` automatically selects that employee in `Manage people` and scrolls to their profile.
   * Open `#/machine-kanban` and verify the 4 KPI summary cards, lane count pills, and high-contrast status text (`#FCA5A5`, `#FCD34D`, `#4ADE80`) render cleanly.
