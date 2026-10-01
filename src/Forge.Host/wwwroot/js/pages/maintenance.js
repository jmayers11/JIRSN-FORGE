import { tryGet, command } from '../api.js';
import { esc, $, $$, fmtNumber, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast } from '../ui.js';
import { isDownAt } from '../model.js';

const can = (me, ...roles) => roles.some(r => me.roles.includes(r));

export async function maintenancePage(page, ctx) {
  async function render() {
    const now = Date.now();
    const iso = ms => new Date(ms).toISOString();
    const [resources, downtime] = await Promise.all([
      tryGet('/api/resources/'),
      tryGet(`/api/maintenance/downtime?from=${encodeURIComponent(iso(now - 30 * 864e5))}&to=${encodeURIComponent(iso(now + 60 * 864e5))}`),
    ]);
    const machines = resources.data?.machines ?? [];
    const windows = downtime.data ?? [];
    const canReport = can(ctx.me, 'MaintenanceTechnician', 'Supervisor');
    const canPlan = can(ctx.me, 'MaintenanceTechnician');
    page.innerHTML = `
      <div class="page-head"><div><h1>Maintenance</h1><div class="sub">Machine status and downtime. Downtime removes executable capacity and moves scheduled work.</div></div></div>
      <section class="panel"><header><h2>Machines now</h2></header>
        ${resources.error ? errorBlock(resources.error, 'machines') : !machines.length ? emptyBlock('No machines yet', 'Machines are set up under work centers.') : `
        <table class="table"><thead><tr><th>Machine</th><th>Work center</th><th>Status</th><th></th></tr></thead><tbody>
        ${machines.map(m => {
          const down = windows.find(w => w.machineCode === m.code && isDownAt(w, now));
          return `<tr><td><b>${esc(m.code)}</b> <span class="muted">${esc(m.name)}</span></td><td>${esc(m.workCenterCode)}</td>
            <td>${down ? `<span class="pill bad">${down.kind === 'Planned' ? 'Planned maintenance' : 'Down'}</span> <span class="muted">since ${esc(fmtDateTime(down.startsAt))} · ${esc(down.reason)}</span>` : '<span class="pill ok">Up</span>'}</td>
            <td class="nowrap">${down && down.kind === 'Unplanned' && canReport ? `<button class="btn" data-restore="${esc(m.code)}">Restore</button>` : ''}
              ${!down && canReport ? `<button class="btn" data-down="${esc(m.code)}">Report down</button>` : ''}
              ${canPlan ? `<button class="btn" data-plan="${esc(m.code)}">Plan maintenance</button>` : ''}</td></tr>`;
        }).join('')}</tbody></table>`}
      </section>
      <section class="panel"><header><h2>Downtime log</h2><span class="spacer"></span><span class="muted">last 30 days and next 60</span></header>
        ${downtime.error ? errorBlock(downtime.error, 'downtime') : !windows.length ? emptyBlock('No downtime recorded', 'Nothing in this period.') : `
        <table class="table"><thead><tr><th>Machine</th><th>Kind</th><th>From</th><th>Until</th><th>Hours</th><th>Reason</th></tr></thead><tbody>
        ${[...windows].sort((a, b) => new Date(b.startsAt) - new Date(a.startsAt)).map(w => `<tr><td>${esc(w.machineCode)}</td>
          <td><span class="pill ${w.kind === 'Planned' ? 'info' : 'bad'}">${esc(w.kind)}</span></td><td>${esc(fmtDateTime(w.startsAt))}</td>
          <td>${w.endsAt ? esc(fmtDateTime(w.endsAt)) : '<span class="c-bad">open</span>'}</td>
          <td>${w.endsAt ? fmtNumber((new Date(w.endsAt) - new Date(w.startsAt)) / 36e5) : `${fmtNumber((now - new Date(w.startsAt)) / 36e5)} so far`}</td>
          <td>${esc(w.reason)}</td></tr>`).join('')}</tbody></table>`}
      </section>`;

    $$('[data-down]', page).forEach(b => b.addEventListener('click', async () => {
      const m = b.dataset.down;
      const done = await confirmWithReason({ title: `Report ${m} down`, confirmLabel: 'Report down', reasonRequired: false,
        extraHtml: '<div class="field"><label for="what">What is wrong</label><input id="what" class="input"></div>' },
        (reason, el) => { const d = $('#what', el).value.trim(); if (!d) throw new Error('Describe the fault.'); return command(`/api/maintenance/machines/${encodeURIComponent(m)}/down`, { description: d }, reason); });
      if (done) { toast(`${m} reported down.`); render(); }
    }));
    $$('[data-restore]', page).forEach(b => b.addEventListener('click', async () => {
      const m = b.dataset.restore;
      const done = await confirmWithReason({ title: `Restore ${m}`, confirmLabel: 'Restore' }, reason => command(`/api/maintenance/machines/${encodeURIComponent(m)}/restore`, {}, reason));
      if (done) { toast(`${m} restored.`); render(); }
    }));
    $$('[data-plan]', page).forEach(b => b.addEventListener('click', async () => {
      const m = b.dataset.plan;
      const done = await confirmWithReason({ title: `Plan maintenance on ${m}`, confirmLabel: 'Plan', reasonRequired: false,
        extraHtml: `<div class="field"><label for="s">Starts</label><input id="s" class="input" type="datetime-local"></div>
          <div class="field"><label for="e">Ends</label><input id="e" class="input" type="datetime-local"></div>
          <div class="field"><label for="what">Work to be done</label><input id="what" class="input"></div>` },
        (reason, el) => {
          const s = $('#s', el).value, e = $('#e', el).value, d = $('#what', el).value.trim();
          if (!s || !e || !d) throw new Error('Give a start, an end and the work to be done.');
          return command(`/api/maintenance/machines/${encodeURIComponent(m)}/planned`, { startsAt: new Date(s).toISOString(), endsAt: new Date(e).toISOString(), description: d }, reason);
        });
      if (done) { toast(`Maintenance planned on ${m}.`); render(); }
    }));
  }
  await render();
}
