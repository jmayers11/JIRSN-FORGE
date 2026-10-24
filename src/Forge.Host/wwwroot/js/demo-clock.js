import { get, command } from './api.js';
import { esc, fmtDateTime, fmtNumber, toast, confirmWithReason } from './ui.js';

// Sprint 9 simulated demo clock. Only a host in demo mode with a designated demo database reports an offset; then every page
// computes "now" the way the server does, and a banner says the clock is simulated so it is never mistaken for real time.

const RealDate = globalThis.Date;
let offsetMs = 0;

/** Shifts the browser's notion of now by the server's demo offset (0 restores real time). */
export function applyDemoOffset(seconds) {
  offsetMs = Math.max(0, Number(seconds) || 0) * 1000;
  if (offsetMs === 0) { globalThis.Date = RealDate; return; }
  class SimulatedDate extends RealDate {
    constructor(...args) { if (args.length === 0) super(RealDate.now() + offsetMs); else super(...args); }
    static now() { return RealDate.now() + offsetMs; }
  }
  globalThis.Date = SimulatedDate;
}

export function demoOffsetMs() { return offsetMs; }

export function demoBannerHtml(status) {
  if (!status?.designated) return '';
  return `<div class="demo-clock-banner" role="status" data-demo-clock><strong>${esc(status.label)}</strong> · ${esc(status.demoCode)} / ${esc(status.siteCode)} · simulated ${esc(fmtDateTime(status.simulatedNow))}
    <span class="faint">(real ${esc(fmtDateTime(status.realNow))}, +${fmtNumber(status.offsetSeconds / 3600, 2)} h)</span></div>`;
}

/** Reads the server's demo status once after sign-in, applies the offset and shows the banner above the page. */
export async function startDemoClock(host) {
  let status = null;
  try { status = await get('/api/demo/status'); } catch { status = null; }
  applyDemoOffset(status?.designated ? status.offsetSeconds : 0);
  if (host) {
    host.innerHTML = demoBannerHtml(status);
    host.hidden = !status?.designated;
  }
  return status;
}

export const ADVANCES = [[15, '+15 min'], [60, '+1 h'], [240, '+4 h'], [480, '+1 shift']];

/** The server-side controls of the Demo Scenario Control Center: clock, saved baseline, comparison and reset. */
export function demoControlHtml(status, me, comparison = null) {
  if (!status?.enabled) return '';
  const perms = me?.permissions ?? [];
  const control = perms.includes('demo.control');
  const reset = perms.includes('demo.reset');
  if (!status.designated) {
    return `<section class="panel" data-demo-control><header><h2>Demo tenant</h2></header>
      <p class="muted">This server runs in demo mode, but this database is not designated a demo tenant. Designation is refused while it holds any other site (such as PLANT1).</p>
      ${reset ? '<button class="btn" data-demo-designate>Designate WW-DEMO / WW-P01…</button>' : ''}</section>`;
  }
  const rows = comparison?.rows?.map(r => `<tr><td>${esc(r.metric)}</td><td class="num">${r.baseline ?? '—'}</td><td class="num">${r.current ?? '—'}</td>
    <td class="num ${r.delta ? 'c-warn' : ''}">${r.delta == null ? '—' : (r.delta > 0 ? '+' : '') + fmtNumber(r.delta, 3)}</td></tr>`).join('') ?? '';
  return `<section class="panel" data-demo-control><header><h2>Demo clock and baseline</h2><span class="spacer"></span><span class="pill warn">SIMULATED DEMO CLOCK</span></header>
    <p>Simulated time ${esc(fmtDateTime(status.simulatedNow))} (+${fmtNumber(status.offsetSeconds / 3600, 2)} h). The clock only moves forward; a reset returns it to the baseline's time.</p>
    ${control ? `<div class="actions">${ADVANCES.map(([minutes, label]) => `<button class="btn" data-demo-advance="${minutes}">${label}</button>`).join('')}
      <button class="btn" data-demo-baseline>Save baseline…</button>${status.baseline ? '<button class="btn" data-demo-compare>Compare baseline</button>' : ''}
      ${reset && status.baseline ? '<button class="btn danger" data-demo-reset>Reset demo…</button>' : ''}</div>` : '<p class="faint">You can see the simulated clock but not change it.</p>'}
    <p class="faint">${status.baseline ? `Baseline ${status.baseline.baselineId}: ${esc(status.baseline.reason)} · saved ${esc(fmtDateTime(status.baseline.capturedAt))} at simulated ${esc(fmtDateTime(status.baseline.simulatedAt))}.` : 'No baseline saved yet.'}
      A reset swaps in a fresh copy of the baseline database; the replaced state is kept as an archive database, never deleted.</p>
    ${rows ? `<div class="table-wrap"><table class="table"><thead><tr><th>Metric</th><th class="num">Baseline</th><th class="num">Now</th><th class="num">Change</th></tr></thead><tbody>${rows}</tbody></table></div><p class="faint">${esc(comparison.note)}</p>` : ''}
  </section>`;
}

/** Renders the control panel into host and wires its buttons; after a change the page reloads so every view uses the new time and data. */
export async function appendDemoControl(host, me, reload = () => location.reload()) {
  let status;
  try { status = await get('/api/demo/status'); } catch { return; }
  let comparison = null;
  const draw = () => { host.innerHTML = demoControlHtml(status, me, comparison); bind(); };
  const run = async (title, detail, path, body, label) => {
    const done = await confirmWithReason({ title, detail, confirmLabel: label, reasonRequired: true }, reason => command(path, body, reason));
    if (done) { toast(done.message ?? 'Done.'); reload(); }
  };
  const bind = () => {
    host.querySelectorAll('[data-demo-advance]').forEach(b => b.addEventListener('click', () => run(`Advance the simulated clock ${b.textContent}`,
      'Time-driven states (expiries, overdue calibration, due dates) move with it.', '/api/demo/clock/advance', { minutes: Number(b.dataset.demoAdvance) }, 'Advance')));
    host.querySelector('[data-demo-baseline]')?.addEventListener('click', () => run('Save the demo baseline',
      'Copies the whole demo database. Connections pause for a few seconds.', '/api/demo/baseline', {}, 'Save baseline'));
    host.querySelector('[data-demo-reset]')?.addEventListener('click', () => run('Reset the demo to its baseline',
      'Everyone is disconnected briefly. The current state is archived as a separate database; nothing is deleted.', '/api/demo/reset', {}, 'Reset demo'));
    host.querySelector('[data-demo-designate]')?.addEventListener('click', () => run('Designate this database as the WW-DEMO tenant',
      'Refused if it holds any site other than WW-P01.', '/api/demo/designate', { demoCode: 'WW-DEMO', siteCode: 'WW-P01' }, 'Designate'));
    host.querySelector('[data-demo-compare]')?.addEventListener('click', async () => {
      try { comparison = await get('/api/demo/compare'); draw(); } catch (error) { toast(error.message, true); }
    });
  };
  draw();
}
