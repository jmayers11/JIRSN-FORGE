import { command, get } from '../api.js';
import { $, esc, fmtDateTime, toast, confirmWithReason } from '../ui.js';

const KINDS = ['MachineUnavailable', 'PersonUnavailable', 'PriorityChange', 'OperationMachine', 'Overtime', 'SupplierDelay', 'AddOperator', 'AddTrainer'];

export async function scenariosPage(root) {
  let scenarios = [];
  let result = null;
  let loading = false;
  const read = async () => { scenarios = await get('/api/planning/scenarios'); };
  const field = (name, label, type = 'text', required = false, placeholder = '') => `<label class="field"><span>${label}</span><input class="input" name="${name}" type="${type}" ${required ? 'required' : ''} ${placeholder ? `placeholder="${placeholder}"` : ''}></label>`;
  const render = () => {
    root.innerHTML = `<div class="page-head"><div><h1>Scenarios and recovery</h1><p class="muted">Save private what-if cases, compare them against today’s authorized material, capacity and schedule inputs, and review recovery actions. Running a scenario does not change the approved schedule or source records.</p></div></div>
      <section class="panel"><header><h2>New scenario</h2></header><form id="scenario-form" class="form-grid">
        ${field('name', 'Private scenario name', 'text', true, 'e.g. Slitter outage next shift')}
        <label class="field"><span>Change to test</span><select class="input" name="kind">${KINDS.map(k => `<option>${k}</option>`).join('')}</select></label>
        ${field('machineCode', 'Machine code')}${field('personId', 'Person ID')}${field('orderNumber', 'Released order number', 'number')}${field('sequence', 'Operation sequence', 'number')}${field('priority', 'Proposed priority (1 highest)', 'number')}
        ${field('workCenterCode', 'Work center code')}${field('activityCode', 'Activity code')}${field('startsAt', 'Starts at', 'datetime-local')}${field('durationHours', 'Hours (outage, overtime)', 'number')}${field('delayDays', 'Supplier delay days', 'number')}
        ${field('reason', 'Why save this scenario', 'text', true, 'Required for the audit record')}
        <div class="field"><span>&nbsp;</span><button class="btn primary" type="submit">Save private scenario</button></div>
      </form><p class="muted small">The selected change uses only its relevant fields. Person and order lookups are checked again using your current access when you run the scenario.</p></section>
      <section class="panel"><header><h2>Your private scenarios</h2><span class="spacer"></span><button class="btn" id="refresh-scenarios">Refresh</button></header>
        ${scenarios.length ? `<div class="stack">${scenarios.map(s => `<article class="card"><div class="card-head"><div><h3>${esc(s.name)}</h3><p class="muted">${esc(s.change.kind)} · created ${esc(fmtDateTime(s.createdAt))}</p></div><span class="spacer"></span><button class="btn primary" data-run="${s.scenarioId}" ${loading ? 'disabled' : ''}>Run preview</button><button class="btn danger" data-delete="${s.scenarioId}">Delete</button></div></article>`).join('')}</div>` : '<div class="empty">No private scenarios saved yet.</div>'}</section>
      ${result ? renderResult(result) : ''}`;

    $('#refresh-scenarios', root)?.addEventListener('click', () => scenariosPage(root).catch(e => toast(e.message, true)));
    $('#scenario-form', root)?.addEventListener('submit', saveScenario);
    $$('[data-run]', root).forEach(button => button.addEventListener('click', () => runScenario(button.dataset.run)));
    $$('[data-delete]', root).forEach(button => button.addEventListener('click', () => deleteScenario(button.dataset.delete)));
  };
  const renderResult = r => `<section class="panel"><header><h2>Scenario preview</h2><span class="spacer"></span><span class="muted">As of ${esc(fmtDateTime(r.asOf))}</span></header>
    <p><b>${esc(r.scenarioDescription)}</b></p><p class="muted">${esc(r.evidenceNote)}</p>
    <h3>Schedule impact</h3>${r.orders.length ? `<div class="table-wrap"><table class="table"><thead><tr><th>Order</th><th>Part</th><th>Change</th><th>Baseline</th><th>Scenario</th><th>Hours</th><th>Evidence</th></tr></thead><tbody>${r.orders.map(o => `<tr><td><a href="#/orders/${o.orderNumber}">#${o.orderNumber}</a></td><td>${esc(o.partNumber)}</td><td>${esc(o.change)}</td><td>${esc(fmtDateTime(o.baselineCompletion))}<br>${esc(o.baselineStatus)}</td><td>${esc(fmtDateTime(o.proposedCompletion))}<br>${esc(o.proposedStatus)}</td><td>${o.shiftHours == null ? '—' : esc(String(o.shiftHours))}</td><td>${o.evidence.map(esc).join('<br>')}</td></tr>`).join('')}</tbody></table></div>` : '<div class="empty">No released order projection changed in this comparison.</div>'}
    <h3>Skills MRP</h3>${r.skillsCapacity.length ? `<div class="table-wrap"><table class="table"><thead><tr><th>Day</th><th>Qualification</th><th>Baseline hours</th><th>Scenario hours</th></tr></thead><tbody>${r.skillsCapacity.map(x => `<tr><td>${esc(x.day)}</td><td>${esc(x.qualification)}</td><td>${esc(String(x.baselineHours))}</td><td>${esc(String(x.proposedHours))}</td></tr>`).join('')}</tbody></table></div>` : '<p class="muted">No qualification-hour difference was projected.</p>'}
    <h3>Recovery options (${r.recoveryOptions.length})</h3><p class="muted">Each option states its current feasibility and consequences. “Open workflow” takes you to the owning domain, which applies its own permissions and command checks.</p>
    <div class="stack">${r.recoveryOptions.map(o => `<article class="card"><div class="card-head"><h4>${esc(o.title)}</h4><span class="pill ${o.feasible ? 'ok' : 'warn'}">${o.feasible ? 'Feasible to review' : 'Not currently feasible'}</span><span class="spacer"></span>${o.feasible && o.applyPath ? `<a class="btn" href="${esc(o.applyPath)}">Open workflow</a>` : ''}</div><p>${esc(o.feasibility)}</p><p class="muted small">Required permission: <code>${esc(o.requiredPermission)}</code></p><ul>${o.consequences.map(c => `<li>${esc(c)}</li>`).join('')}</ul></article>`).join('')}</div></section>`;

  async function saveScenario(event) {
    event.preventDefault();
    const f = new FormData(event.currentTarget);
    const kind = String(f.get('kind'));
    const change = { kind };
    for (const name of ['machineCode', 'personId', 'orderNumber', 'sequence', 'priority', 'workCenterCode', 'activityCode', 'startsAt', 'durationHours', 'delayDays']) {
      const value = String(f.get(name) ?? '').trim();
      if (!value) continue;
      if (name === 'personId') change.personId = value;
      else if (name === 'orderNumber' || name === 'sequence' || name === 'priority' || name === 'durationHours' || name === 'delayDays') change[name] = Number(value);
      else if (name === 'startsAt') change.startsAt = new Date(value).toISOString();
      else change[name] = value;
    }
    try {
      const saved = await command('/api/planning/scenarios', { name: f.get('name'), change }, f.get('reason'));
      toast('Private scenario saved.');
      await read(); result = null; render();
      if (saved.entityId) await runScenario(saved.entityId);
    } catch (error) { toast(error.message, true); }
  }

  async function runScenario(id) {
    loading = true; render();
    try { result = await command(`/api/planning/scenarios/${id}/run`, {}); }
    catch (error) { toast(error.message, true); }
    finally { loading = false; render(); }
  }

  async function deleteScenario(id) {
    try {
      const done = await confirmWithReason({ title: 'Delete private scenario', detail: 'Only your saved planning scenario is removed. No production record is changed.', confirmLabel: 'Delete' },
        reason => command(`/api/planning/scenarios/${id}/delete`, {}, reason));
      if (done) { toast('Scenario deleted.'); result = null; await read(); render(); }
    } catch (error) { toast(error.message, true); }
  }

  try { await read(); render(); }
  catch (error) { root.innerHTML = `<section class="panel"><h1>Scenarios and recovery</h1><p class="notice bad">${esc(error.message)}</p></section>`; }
}
