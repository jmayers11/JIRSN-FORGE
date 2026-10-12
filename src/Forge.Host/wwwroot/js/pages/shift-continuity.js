import { tryGet, command } from '../api.js';
import { esc, $, toast, errorBlock, fmtDateTime } from '../ui.js';
import { canAction } from '../forms.js';

const enc = encodeURIComponent;
const localDate = date => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
const localInput = date => `${localDate(date)}T${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`;

function zonedIso(value, zone) {
  const [d, t] = value.split('T');
  const [year, month, day] = d.split('-').map(Number);
  const [hour, minute] = t.split(':').map(Number);
  const desired = Date.UTC(year, month - 1, day, hour, minute);
  let instant = desired;
  const format = new Intl.DateTimeFormat('en-CA', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
  for (let i = 0; i < 3; i++) {
    const p = Object.fromEntries(format.formatToParts(new Date(instant)).map(x => [x.type, x.value]));
    const actual = Date.UTC(+p.year, +p.month - 1, +p.day, +p.hour, +p.minute);
    instant += desired - actual;
  }
  return new Date(instant).toISOString();
}

export async function shiftContinuityPage(page, ctx) {
  const supervisor = canAction(ctx.me, 'workforce.supervise');
  const canManageChecklist = canAction(ctx.me, 'resources.manage');
  const canRecord = canAction(ctx.me, 'execution.shopfloor.record');
  const resourcesResult = await tryGet('/api/resources/?includeRetired=false');
  if (resourcesResult.error) { page.innerHTML = `<div class="page-head"><h1>Shift continuity</h1></div>${errorBlock(resourcesResult.error, 'shift resources')}`; return; }
  const resources = resourcesResult.data;
  const centers = resources.workCenters ?? [];
  const shifts = resources.shifts ?? [];
  const sites = resources.sites ?? [];
  let selected = centers[0]?.code ?? '';
  let selectedShift = '';
  let handoffs = [];
  let closeouts = [];
  let checklist = [];
  let details = null;
  let actions = [];
  const startDate = new Date(); startDate.setDate(startDate.getDate() - 1); startDate.setHours(0, 0, 0, 0);
  const endDate = new Date(startDate); endDate.setDate(endDate.getDate() + 1);

  async function load() {
    const [h, c, cl] = await Promise.all([
      tryGet(`/api/execution/shifts/handoffs?workCenter=${enc(selected)}&limit=30`),
      tryGet(`/api/execution/shifts/closeouts?workCenter=${enc(selected)}&limit=30`),
      tryGet(`/api/execution/shifts/checklist?workCenter=${enc(selected)}`),
    ]);
    handoffs = h.data ?? []; closeouts = c.data ?? []; checklist = cl.data ?? [];
  }

  const centerSelect = () => `<select id="shift-wc" class="select">${centers.map(w => `<option value="${esc(w.code)}" ${w.code === selected ? 'selected' : ''}>${esc(w.name)} · ${esc(w.code)}</option>`).join('')}</select>`;
  const shiftOptions = () => {
    const assigned = new Set((resources.workCenterShifts ?? []).filter(x => x.workCenterCode === selected).map(x => x.shiftCode));
    return shifts.filter(s => assigned.has(s.code)).map(s => `<option value="${esc(s.code)}" ${s.code === selectedShift ? 'selected' : ''}>${esc(s.name)} · ${esc(s.code)} · ${esc(s.start.slice(0, 5))}</option>`).join('');
  };
  const checklistForm = () => checklist.length ? checklist.filter(x => x.active !== false).map(i => `
    <div class="row" style="align-items:end;gap:10px"><div style="flex:1"><b>${esc(i.description)}</b>${i.required ? ' <span class="pill warn">Required</span>' : ''}</div>
      <select class="select" data-check="${esc(i.code)}"><option>Open</option><option>Complete</option><option>NotApplicable</option></select>
      <input class="input" data-note="${esc(i.code)}" placeholder="Note for open or N/A"></div>`).join('') : '<div class="muted">No configured checklist items for this work center.</div>';

  async function render() {
    await load();
    const siteCode = resources.workCenters?.find(w => w.code === selected)?.safetyAreaCode;
    const area = resources.safetyAreas?.find(a => a.code === siteCode);
    const site = sites.find(s => s.code === area?.siteCode);
    const zone = site?.timeZone ?? 'America/New_York';
    const shiftWindow = shifts.find(s => s.code === selectedShift);
    page.innerHTML = `
      <div class="page-head"><div><h1>Shift continuity</h1><div class="sub">Pass-on snapshots come from current production, material, downtime and maintenance records. Acknowledgment records receipt; assigned actions remain open until completed.</div></div></div>
      <section class="panel"><header><h2>Work center</h2></header><div class="toolbar">${centerSelect()}<select id="shift-code" class="select"><option value="">Choose assigned shift</option>${shiftOptions()}</select><span class="muted">${esc(zone)}</span></div></section>
      ${canRecord ? `<section class="panel"><header><h2>Capture shift pass-on</h2><span class="spacer"></span><span class="muted">${shiftWindow ? `${esc(shiftWindow.name)} · configured ${shiftWindow.durationMinutes} minutes` : 'Select an assigned shift'}</span></header>
        <div class="toolbar"><label class="field">Shift starts<input id="shift-from" type="datetime-local" class="input" value="${localInput(startDate)}"></label><label class="field">Shift ends<input id="shift-until" type="datetime-local" class="input" value="${localInput(endDate)}"></label></div>
        <div class="field"><label for="shift-context">Outgoing shift context</label><textarea id="shift-context" class="input" rows="3" maxlength="2000" placeholder="What should the next shift know? This note is recorded in the audit trail."></textarea></div>
        <h3>Work-center checklist</h3><div class="stack">${checklistForm()}</div><div class="actions"><button id="capture-handoff" class="btn primary" ${selectedShift ? '' : 'disabled'}>Capture handoff</button></div></section>` : ''}
      ${canManageChecklist && selected ? `<section class="panel"><header><h2>Configure checklist</h2><span class="spacer"></span><span class="muted">A change applies to new handoffs; recorded checklist versions stay with their shift.</span></header>
        <form id="checklist-config" class="toolbar"><input class="input" name="code" maxlength="40" placeholder="Item code" required><input class="input" name="description" maxlength="240" placeholder="Checklist item" required><label><input name="required" type="checkbox"> Required</label><button class="btn" type="submit">Save item</button></form></section>` : ''}
      <section class="panel"><header><h2>Recent handoffs</h2></header>${handoffs.length ? `<div class="stack">${handoffs.map(h => `<article class="card"><div class="row"><b>${esc(h.shiftCode)} · ${esc(h.workCenterCode)}</b><span class="spacer"></span><span class="pill ${h.reviewedAt ? 'ok' : 'warn'}">${h.reviewedAt ? 'Reviewed' : 'Awaiting review'}</span><button class="btn small" data-open="${h.handoffId}">Open</button></div><div class="muted">${esc(fmtDateTime(h.shiftFrom))} – ${esc(fmtDateTime(h.shiftUntil))} · by ${esc(h.createdBy)}</div><div>${esc(h.context ?? '')}</div></article>`).join('')}</div>` : '<div class="muted">No handoffs recorded for this work center.</div>'}</section>
      <section class="panel"><header><h2>Shift closeout</h2><span class="spacer"></span>${supervisor ? '<button id="close-shift" class="btn primary" ' + (selectedShift ? '' : 'disabled') + '>Capture closeout for selected window</button>' : ''}</header>
        <div class="muted">Closeout records production reports, current WIP, order-linked material movements, downtime, maintenance and any source sections unavailable to your access. It never rewrites those records.</div>
        ${closeouts.length ? `<div class="stack">${closeouts.map(c => `<article class="card"><b>${esc(c.shiftCode)} · ${esc(c.workCenterCode)}</b><div class="muted">${esc(fmtDateTime(c.shiftFrom))} – ${esc(fmtDateTime(c.shiftUntil))} · closed ${esc(fmtDateTime(c.closedAt))}</div><div>${c.facts.production.reportCount} reports · ${c.facts.production.goodQuantity} good · ${c.facts.production.scrapQuantity} scrap · ${c.facts.production.openOperations} open operations</div><details><summary>Operational facts and unresolved exceptions (${c.facts.facts.filter(f => f.kind === 'Reconciliation').length})</summary><ul class="evidence">${c.facts.facts.map(f => `<li><b>${esc(f.kind)}</b> · ${esc(f.summary)} <span class="muted">${esc(f.status)}</span></li>`).join('') || '<li class="muted">No visible source facts.</li>'}</ul></details>${c.facts.unknowns?.length ? `<div class="notice warn">${c.facts.unknowns.map(esc).join('<br>')}</div>` : ''}</article>`).join('')}</div>` : '<div class="muted">No closeouts recorded for this work center.</div>'}</section>
      <div id="handoff-detail"></div>`;

    $('#shift-wc', page)?.addEventListener('change', async e => { selected = e.target.value; selectedShift = ''; await render(); });
    $('#shift-code', page)?.addEventListener('change', e => { selectedShift = e.target.value; const btn = $('#capture-handoff', page); if (btn) btn.disabled = !selectedShift; const close = $('#close-shift', page); if (close) close.disabled = !selectedShift; });
    $('#checklist-config', page)?.addEventListener('submit', async e => {
      e.preventDefault(); const f = new FormData(e.currentTarget);
      try { await command('/api/execution/shifts/checklist', { workCenterCode: selected, code: f.get('code'), description: f.get('description'), required: f.has('required'), sortOrder: checklist.length, active: true }, 'Configure work-center shift checklist'); toast('Checklist item saved.'); await render(); }
      catch (error) { toast(error.message); }
    });
    $('#capture-handoff', page)?.addEventListener('click', async () => {
      const fromValue = $('#shift-from', page).value, untilValue = $('#shift-until', page).value;
      const context = $('#shift-context', page).value.trim();
      if (!selectedShift || !context) { toast('Choose a shift and enter handoff context.'); return; }
      const entries = checklist.filter(x => x.active !== false).map(i => ({ code: i.code, state: $(`[data-check="${CSS.escape(i.code)}"]`, page).value, note: $(`[data-note="${CSS.escape(i.code)}"]`, page).value.trim() || null }));
      try { const result = await command('/api/execution/shifts/handoffs', { workCenterCode: selected, shiftCode: selectedShift, shiftFrom: zonedIso(fromValue, zone), shiftUntil: zonedIso(untilValue, zone), context, checklist: entries }, context); toast(result.message); await render(); }
      catch (error) { toast(error.message); }
    });
    $('#close-shift', page)?.addEventListener('click', async () => {
      try { const result = await command('/api/execution/shifts/closeouts', { workCenterCode: selected, shiftCode: selectedShift, shiftFrom: zonedIso($('#shift-from', page).value, zone), shiftUntil: zonedIso($('#shift-until', page).value, zone), note: $('#shift-context', page).value.trim() || null }, 'Review shift production, material, WIP, downtime, tooling and open issues'); toast(result.message); await render(); }
      catch (error) { toast(error.message); }
    });
    page.querySelectorAll('[data-open]').forEach(button => button.addEventListener('click', () => openHandoff(Number(button.dataset.open), zone)));
  }

  async function openHandoff(id) {
    try {
      const [result, actionList] = await Promise.all([tryGet(`/api/execution/shifts/handoffs/${id}`), tryGet(`/api/execution/shifts/handoffs/${id}/actions`)]);
      if (result.error) throw result.error;
      details = result.data; actions = actionList.data ?? [];
      const h = details.handoff;
      const box = $('#handoff-detail', page);
      box.innerHTML = `<section class="panel"><header><h2>Handoff #${h.handoffId}</h2><span class="spacer"></span>
        ${supervisor && !h.reviewedAt ? '<button class="btn" data-review>Review</button>' : ''}
        ${canRecord && h.reviewedAt ? '<button class="btn" data-ack>Acknowledge receipt</button>' : ''}</header>
        <p>${esc(h.context ?? '')}</p><h3>Operational snapshot at ${esc(fmtDateTime(h.snapshot.asOf))}</h3>
        <div>${h.snapshot.production.reportCount} reports · ${h.snapshot.production.goodQuantity} good · ${h.snapshot.production.scrapQuantity} scrap · ${h.snapshot.production.openOperations} open operations</div>
        ${h.snapshot.unknowns?.length ? `<div class="notice warn">${h.snapshot.unknowns.map(esc).join('<br>')}</div>` : ''}
        <ul class="evidence">${h.facts.map(f => `<li><b>${esc(f.kind)}</b> · ${esc(f.summary)} <span class="muted">${esc(f.status)}</span></li>`).join('') || '<li class="muted">No visible source facts for this work center and shift.</li>'}</ul>
        <h3>Checklist</h3><ul class="evidence">${h.checklist.map(x => `<li>${esc(x.description)} — <b>${esc(x.state)}</b>${x.note ? ` · ${esc(x.note)}` : ''}</li>`).join('') || '<li class="muted">No checklist configured.</li>'}</ul>
        <h3>Actions</h3>${actions.map(a => {
          const next = ({ Assigned: 'Acknowledged', Acknowledged: 'InProgress', InProgress: 'Completed' })[a.status];
          const allowed = next && ((a.assignee === ctx.me.personId && canRecord) || supervisor);
          return `<div class="row"><span>${esc(a.description)}${a.sourceKind ? `<div class="muted">Linked ${esc(a.sourceKind)} ${esc(a.sourceReference)}</div>` : ''}</span><span class="spacer"></span><span class="pill ${a.status === 'Completed' ? 'ok' : 'warn'}">${esc(a.status)}</span>${allowed ? `<button class="btn small" data-action-next="${a.actionId}" data-status="${esc(a.status)}" data-next="${esc(next)}">Mark ${esc(next)}</button>` : ''}</div>`;
        }).join('') || '<div class="muted">No assigned actions.</div>'}
        ${supervisor ? `<form data-action-form class="toolbar"><input name="description" class="input" placeholder="Action to follow up" required><input name="sourceKind" class="input" placeholder="Source: Order, Machine, WorkOrder or Tool"><input name="sourceReference" class="input" placeholder="Source record ID or code"><input name="assignee" class="input" placeholder="Assignee person ID" required><button class="btn" type="submit">Assign action</button></form>` : ''}
        <h3>Incoming shift acknowledgments</h3><div>${details.acknowledgments.map(a => `<span class="pill ok">${esc(a.personId)} · ${esc(fmtDateTime(a.acknowledgedAt))}</span>`).join(' ') || '<span class="muted">No acknowledgment recorded.</span>'}</div></section>`;
      box.querySelector('[data-review]')?.addEventListener('click', async () => { try { await command(`/api/execution/shifts/handoffs/${id}/review`, {}, 'Reviewed shift handoff'); toast('Handoff reviewed.'); await render(); await openHandoff(id); } catch (e) { toast(e.message); } });
      box.querySelector('[data-ack]')?.addEventListener('click', async () => { try { await command(`/api/execution/shifts/handoffs/${id}/acknowledge`, { note: 'Received by incoming shift' }, 'Incoming shift received handoff'); toast('Receipt acknowledged. Actions remain independently tracked.'); await render(); await openHandoff(id); } catch (e) { toast(e.message); } });
      box.querySelector('[data-action-form]')?.addEventListener('submit', async e => { e.preventDefault(); const f = new FormData(e.currentTarget); try { await command(`/api/execution/shifts/handoffs/${id}/actions`, { description: f.get('description'), sourceKind: f.get('sourceKind') || null, sourceReference: f.get('sourceReference') || null, assignee: f.get('assignee') }, 'Assign shift follow-up'); toast('Action assigned.'); await openHandoff(id); } catch (error) { toast(error.message); } });
      box.querySelectorAll('[data-action-next]').forEach(button => button.addEventListener('click', async () => {
        const actionId = Number(button.dataset.actionNext);
        try { await command(`/api/execution/shifts/actions/${actionId}/status`, { expected: button.dataset.status, status: button.dataset.next }, `Shift action ${button.dataset.next}`); toast(`Action marked ${button.dataset.next}.`); await openHandoff(id); }
        catch (error) { toast(error.message); }
      }));
    } catch (error) { toast(error.message); }
  }

  await render();
  return { dispose() {} };
}

