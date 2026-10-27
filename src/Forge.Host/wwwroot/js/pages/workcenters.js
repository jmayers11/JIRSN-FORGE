import { tryGet } from '../api.js';
import { icon } from '../icons.js';
import { esc, $, $$, fmtNumber, fmtDateTime, errorBlock, emptyBlock, wireWorkspaceTabs } from '../ui.js';
import { isDownAt } from '../model.js';
import { appendStoppages, appendReasonCodes } from '../actuals.js';
import { appendTools, appendToolRequirements } from '../tools.js';
import { appendOperations, appendPresence, appendHolds } from '../executable.js';

const DAY = ['', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

export async function workCentersPage(page, ctx) {
  const now = Date.now();
  const iso = ms => new Date(ms).toISOString();
  const [resources, capacity, downtime] = await Promise.all([
    tryGet('/api/resources/'),
    tryGet(`/api/planning/capacity?from=${encodeURIComponent(iso(now))}&to=${encodeURIComponent(iso(now + 7 * 864e5))}`),
    tryGet(`/api/maintenance/downtime?from=${encodeURIComponent(iso(now - 864e5))}&to=${encodeURIComponent(iso(now + 14 * 864e5))}`),
  ]);
  if (resources.error) { page.innerHTML = `<div class="page-head"><h1>Work Centers</h1></div>${errorBlock(resources.error, 'work centers')}`; return; }
  const r = resources.data;
  const selected = ctx.params[0] ?? null;
  const shiftsOf = wc => r.workCenterShifts.filter(s => s.workCenterCode === wc).map(s => r.shifts.find(x => x.code === s.shiftCode)).filter(Boolean);
  const shiftText = s => `${s.name}: ${s.start.slice(0, 5)} for ${fmtNumber(s.durationMinutes / 60)} h, ${s.days.map(d => DAY[d]).join(' ')}`;
  const cap = capacity.data?.output?.machines ?? [];

  const rows = r.workCenters.map(wc => {
    const ms = r.machines.filter(m => m.workCenterCode === wc.code);
    const area = r.safetyAreas.find(a => a.code === wc.safetyAreaCode);
    const down = ms.filter(m => (downtime.data ?? []).some(w => w.machineCode === m.code && isDownAt(w, now)));
    return { wc, ms, area, down, shifts: shiftsOf(wc.code) };
  });

  const tab = (id, label, selectedTab) => `<button class="workspace-tab" id="wc-tab-${id}" type="button" role="tab" data-workspace-tab="${id}" aria-controls="wc-panel-${id}" aria-selected="${selectedTab}" ${selectedTab ? '' : 'tabindex="-1"'}>${label}</button>`;
  const panel = (id, body = '', shown = false) => `<div id="wc-panel-${id}" data-workspace-panel="${id}" role="tabpanel" aria-labelledby="wc-tab-${id}" ${shown ? '' : 'hidden'}>${body}</div>`;
  const current = selected ? rows.find(x => x.wc.code === selected) : null;
  const picker = rows.length ? `<label class="wc-picker">Work center <select class="select" data-wc-picker><option value="">All work centers</option>${rows.map(x => `<option value="${esc(x.wc.code)}" ${x.wc.code === selected ? 'selected' : ''}>${esc(x.wc.name)} (${esc(x.wc.code)})</option>`).join('')}</select></label>` : '';

  // One screen at a time: tabs instead of every section stacked on one long page (owner request 2026-10-27).
  page.innerHTML = `
    <div class="page-head"><div><h1>${current ? esc(current.wc.name) : 'Work Centers'}</h1><div class="sub">${current
      ? `${esc(current.wc.code)} · ${esc(current.area?.name ?? current.wc.safetyAreaCode)} · ${current.ms.length} machine${current.ms.length === 1 ? '' : 's'} · ${current.shifts.length} shift${current.shifts.length === 1 ? '' : 's'} · ${current.down.length ? `<span class="pill bad">${current.down.length} down</span>` : '<span class="pill ok">All up</span>'}`
      : esc(r.sites.map(s => `${s.name} (${s.timeZone})`).join(' · ') || 'No site set up yet')}</div></div><div class="spacer"></div>${picker}<a class="btn" href="#/plant-setup">Add or edit work centers</a></div>
    ${selected && !current ? emptyBlock('Unknown work center', selected) : ''}
    <section class="workspace-tabs">
    <div class="workspace-tablist" role="tablist" aria-label="Work center views">
      ${current ? tab('overview', 'Capacity &amp; downtime', true) : tab('list', `All work centers (${rows.length})`, true)}
      ${tab('readiness', 'Readiness &amp; presence', false)}
      ${tab('support', 'Stoppages', false)}
      ${tab('tools', 'Tools', false)}
      ${tab('holds', 'Holds &amp; reason codes', false)}
    </div>
    <div class="workspace-panels">
      ${current ? panel('overview', detail(current), true) : panel('list', listHtml(), true)}
      ${panel('readiness')}${panel('support')}${panel('tools')}${panel('holds')}
    </div></section>`;

  // A compact card grid: one card per work center, shifts as short chips with the full hours on hover (owner feedback 2026-10-27:
  // the table with every shift on its own line made a long scroll).
  function listHtml() {
    if (!rows.length) return `<section class="panel">${emptyBlock('No work centers yet', 'A manufacturing engineer sets up sites, safety areas, work centers and machines under Plant Setup.')}</section>`;
    return `<div class="wc-card-grid">${rows.map(x => `<a class="wc-card" href="#/work-centers/${encodeURIComponent(x.wc.code)}" data-wc="${esc(x.wc.code)}">
      <div class="wc-card-head"><div><b>${esc(x.wc.name)}</b><span class="faint">${esc(x.wc.code)}</span></div>${x.down.length ? `<span class="pill bad">${x.down.length} down</span>` : '<span class="pill ok">All up</span>'}</div>
      <div class="wc-card-area">${esc(x.area?.name ?? x.wc.safetyAreaCode)}</div>
      <div class="wc-card-chips" title="Machines">${x.ms.map(m => `<span class="chip${x.down.includes(m) ? ' bad' : ''}">${esc(m.code)}</span>`).join('') || '<span class="faint">No machines</span>'}</div>
      <div class="wc-card-chips" title="Shifts">${x.shifts.map(sh => `<span class="chip info" title="${esc(shiftText(sh))}">${esc(sh.name.split(/[\s/]/)[0])}</span>`).join('') || '<span class="c-warn">No shift — no working time</span>'}</div>
    </a>`).join('')}</div>`;
  }

  function detail(x) {
    const capRows = cap.filter(c => c.workCenterCode === x.wc.code);
    return `
    <section class="panel"><header><h2>Executable capacity, next 7 days</h2></header>
      ${capacity.error ? errorBlock(capacity.error, 'capacity') : !capRows.length ? emptyBlock('No machine capacity', 'This work center has no machines with activities set up.') : `
      <table class="table"><thead><tr><th>Machine</th><th>Activity</th><th>Working hours</th><th>Executable hours</th><th>Lost to</th><th>Readiness now</th></tr></thead><tbody>
      ${capRows.map((c, index) => {
        const lost = {};
        for (const i of c.intervals.filter(i => !i.executable)) lost[i.limiting] = (lost[i.limiting] ?? 0) + (new Date(i.end) - new Date(i.start)) / 36e5;
        return `<tr><td>${esc(c.machineCode)}</td><td>${esc(c.activityCode)}</td><td>${fmtNumber(c.workingHours)}</td>
          <td class="${Number(c.executableHours) < Number(c.workingHours) ? 'c-warn' : 'c-ok'}">${fmtNumber(c.executableHours)}</td>
          <td>${Object.entries(lost).map(([k, h]) => `${esc(k)} ${fmtNumber(h)} h`).join(', ') || '—'}</td>
          <td><button class="btn small" data-readiness-check="${index}" data-machine-code="${esc(c.machineCode)}" data-activity-code="${esc(c.activityCode)}">Evaluate now</button><div data-readiness-result="${index}" class="small"></div></td></tr>`;
      }).join('')}
      </tbody></table>
      <div class="faint" style="margin-top:6px">${esc(capacity.data.output.confidence)}</div>`}
    </section>
    <section class="panel"><header><h2>Shifts</h2></header>${x.shifts.map(sh => `<div>${esc(shiftText(sh))}</div>`).join('') || '<span class="c-warn">No shift — no working time</span>'}</section>
    <section class="panel"><header><h2>Downtime</h2></header>
      ${(downtime.data ?? []).filter(w => x.ms.some(m => m.code === w.machineCode)).map(w => `<div>${esc(w.machineCode)} · <span class="pill ${w.kind === 'Planned' ? 'info' : 'bad'}">${esc(w.kind)}</span> ${esc(fmtDateTime(w.startsAt))} – ${w.endsAt ? esc(fmtDateTime(w.endsAt)) : 'open'} · ${esc(w.reason)}</div>`).join('') || '<span class="muted">None recorded in the last day or planned in the next two weeks.</span>'}
    </section>`;
  }

  $$('[data-wc]', page).forEach(row => row.addEventListener('click', () => { location.hash = `#/work-centers/${encodeURIComponent(row.dataset.wc)}`; }));
  $$('[data-readiness-check]', page).forEach(button => button.addEventListener('click', async () => {
    const machine = button.dataset.machineCode, activity = button.dataset.activityCode;
    if (!machine || !activity || button.disabled) return;
    button.disabled = true;
    const result = await tryGet(`/api/readiness/machines/${encodeURIComponent(machine)}?activity=${encodeURIComponent(activity)}`);
    const target = $(`[data-readiness-result="${button.dataset.readinessCheck}"]`, page);
    if (target) {
      if (result.error) target.innerHTML = errorBlock(result.error, 'machine readiness');
      else {
        const output = result.data.output;
        target.innerHTML = `<div class="${output.executable ? 'c-ok' : 'c-warn'}">${output.executable ? 'Executable on evaluated dimensions' : `Blocked: ${esc(output.primaryConstraint ?? 'review required')}`}</div>
          <details><summary>Readiness evidence</summary><ul>${output.dimensions.map(d => `<li><b>${esc(d.name)} · ${esc(d.outcome)}</b>: ${esc(d.detail)}</li>`).join('')}${output.notEvaluated.length ? `<li>Not evaluated: ${esc(output.notEvaluated.join(', '))}</li>` : ''}</ul></details>`;
      }
    }
    button.disabled = false;
  }));

  // V3.5: stoppages on the selected work center's machines (or all machines), and the plant's reason codes; V3.9 readiness,
  // presence and holds. Each goes into its own tab.
  $('[data-wc-picker]', page)?.addEventListener('change', event => {
    location.hash = event.target.value ? `#/work-centers/${encodeURIComponent(event.target.value)}` : '#/work-centers';
  });
  const into = id => $(`[data-workspace-panel="${id}"]`, page);
  const machines = current ? current.ms : r.machines;
  await appendStoppages(into('support'), machines, ctx.me);
  await appendTools(into('tools'), current ? current.ms : r.machines, ctx.me);
  await appendToolRequirements(into('tools'), ctx.me);
  await appendOperations(into('readiness'), current?.wc.code);
  await appendPresence(into('readiness'), current?.wc.code);
  await appendHolds(into('holds'), ctx.me);
  await appendReasonCodes(into('holds'), ctx.me);
  wireWorkspaceTabs(page);
  const focus = ctx.query?.get('focus') ?? ctx.query?.get('tab');
  const focused = focus ? $(`[data-workspace-tab="${focus}"]`, page) : null;
  if (focused && typeof focused.click === 'function') focused.click();
  else if (focused) focused.dispatchEvent({ type: 'click', bubbles: true });
}
