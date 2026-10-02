import { tryGet } from '../api.js';
import { icon } from '../icons.js';
import { esc, $$, fmtNumber, fmtDateTime, errorBlock, emptyBlock } from '../ui.js';
import { isDownAt } from '../model.js';

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

  page.innerHTML = `
    <div class="page-head"><div><h1>Work Centers</h1><div class="sub">${esc(r.sites.map(s => `${s.name} (${s.timeZone})`).join(' · ') || 'No site set up yet')}</div></div><div class="spacer"></div><a class="btn" href="#/plant-setup">Add or edit work centers</a></div>
    <section class="panel">
      ${!rows.length ? emptyBlock('No work centers yet', 'A manufacturing engineer sets up sites, safety areas, work centers and machines under Plant Setup.') : `
      <table class="table"><thead><tr><th>Work center</th><th>Safety area</th><th>Machines</th><th>Shifts</th><th>Now</th><th></th></tr></thead><tbody>
      ${rows.map(x => `<tr class="click" data-wc="${esc(x.wc.code)}" ${x.wc.code === selected ? 'style="outline:1px solid #F97316"' : ''}>
        <td><b>${esc(x.wc.name)}</b><br><span class="faint">${esc(x.wc.code)}</span></td>
        <td>${esc(x.area?.name ?? x.wc.safetyAreaCode)}</td>
        <td>${x.ms.map(m => esc(m.code)).join(', ') || '<span class="faint">none</span>'}</td>
        <td>${x.shifts.map(s => esc(shiftText(s))).join('<br>') || '<span class="c-warn">No shift — no working time</span>'}</td>
        <td>${x.down.length ? `<span class="pill bad">${x.down.length} down</span>` : '<span class="pill ok">All up</span>'}</td>
        <td class="chev">${icon('chevron')}</td></tr>`).join('')}
      </tbody></table>`}
    </section>
    ${selected ? detail(rows.find(x => x.wc.code === selected)) : ''}`;

  function detail(x) {
    if (!x) return emptyBlock('Unknown work center', selected);
    const capRows = cap.filter(c => c.workCenterCode === x.wc.code);
    return `
    <section class="panel"><header><h2>${esc(x.wc.name)} — executable capacity, next 7 days</h2></header>
      ${capacity.error ? errorBlock(capacity.error, 'capacity') : !capRows.length ? emptyBlock('No machine capacity', 'This work center has no machines with activities set up.') : `
      <table class="table"><thead><tr><th>Machine</th><th>Activity</th><th>Working hours</th><th>Executable hours</th><th>Lost to</th></tr></thead><tbody>
      ${capRows.map(c => {
        const lost = {};
        for (const i of c.intervals.filter(i => !i.executable)) lost[i.limiting] = (lost[i.limiting] ?? 0) + (new Date(i.end) - new Date(i.start)) / 36e5;
        return `<tr><td>${esc(c.machineCode)}</td><td>${esc(c.activityCode)}</td><td>${fmtNumber(c.workingHours)}</td>
          <td class="${Number(c.executableHours) < Number(c.workingHours) ? 'c-warn' : 'c-ok'}">${fmtNumber(c.executableHours)}</td>
          <td>${Object.entries(lost).map(([k, h]) => `${esc(k)} ${fmtNumber(h)} h`).join(', ') || '—'}</td></tr>`;
      }).join('')}
      </tbody></table>
      <div class="faint" style="margin-top:6px">${esc(capacity.data.output.confidence)}</div>`}
    </section>
    <section class="panel"><header><h2>Downtime</h2></header>
      ${(downtime.data ?? []).filter(w => x.ms.some(m => m.code === w.machineCode)).map(w => `<div>${esc(w.machineCode)} · <span class="pill ${w.kind === 'Planned' ? 'info' : 'bad'}">${esc(w.kind)}</span> ${esc(fmtDateTime(w.startsAt))} – ${w.endsAt ? esc(fmtDateTime(w.endsAt)) : 'open'} · ${esc(w.reason)}</div>`).join('') || '<span class="muted">None recorded in the last day or planned in the next two weeks.</span>'}
    </section>`;
  }

  $$('[data-wc]', page).forEach(row => row.addEventListener('click', () => { location.hash = `#/work-centers/${encodeURIComponent(row.dataset.wc)}`; }));
}
