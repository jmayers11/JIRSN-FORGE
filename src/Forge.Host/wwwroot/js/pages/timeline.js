import { tryGet } from '../api.js';
import { esc, $$, fmtDateTime, timeZone, errorBlock, emptyBlock } from '../ui.js';

const H = 36e5;
const TONE = { OnTime: '#22C55E', AtRisk: '#F97316', Late: '#EF4444', Unschedulable: '#EF4444' };

export async function timelinePage(page, ctx) {
  let span = Number(ctx.query.get('days') ?? 3);
  let which = 'approved';
  const now = Date.now();
  const iso = ms => new Date(ms).toISOString();
  const [approved, proposed, downtime, resources] = await Promise.all([
    tryGet('/api/planning/schedules/approved'), tryGet('/api/planning/schedules/proposed'),
    tryGet(`/api/maintenance/downtime?from=${encodeURIComponent(iso(now - 864e5))}&to=${encodeURIComponent(iso(now + 15 * 864e5))}`),
    tryGet('/api/resources/'),
  ]);

  function render() {
    const s = (which === 'approved' ? approved : proposed).data;
    const head = `
      <div class="page-head">
        <div><h1>Horizon</h1><div class="sub">The schedule over time: every operation on its machine, with downtime and the reason each one starts when it does.</div></div>
        <div class="spacer"></div>
        <div class="head-controls">
          <div class="seg">${['approved', 'proposed'].map(k => `<button data-which="${k}" class="${k === which ? 'on' : ''}">${k === 'approved' ? 'Approved' : 'Proposal'}</button>`).join('')}</div>
          <div class="seg">${[1, 3, 7, 14].map(d => `<button data-days="${d}" class="${d === span ? 'on' : ''}">${d === 1 ? '24h' : d === 7 ? '1w' : d === 14 ? '2w' : `${d}d`}</button>`).join('')}</div>
        </div>
      </div>`;
    if (!s) {
      const r = which === 'approved' ? approved : proposed;
      page.innerHTML = head + (r.error && r.error.status !== 404 ? errorBlock(r.error, 'the schedule') : emptyBlock(which === 'approved' ? 'No approved schedule' : 'No proposal', 'A planner proposes a schedule under Scheduling; another approves it.'));
      wire();
      return;
    }
    const ops = s.result.operations.filter(o => o.scheduled);
    const orderStatus = new Map(s.result.orders.map(o => [o.orderNumber, o.status]));
    const from = now - 2 * H, to = from + span * 24 * H;
    const rows = [...new Set(ops.map(o => o.machineCode ?? `${o.workCenterCode} (no machine)`))]
      .sort((a, b) => a.localeCompare(b));
    for (const m of resources.data?.machines ?? []) if (!rows.includes(m.code)) rows.push(m.code);
    if (!rows.length) { page.innerHTML = head + emptyBlock('Nothing scheduled', 'This schedule has no scheduled operations.'); wire(); return; }

    const W = 1100, L = 130, RH = 30, T = 28, Hh = T + rows.length * RH + 8;
    const x = ms => L + ((ms - from) / (to - from)) * (W - L - 8);
    const tickStep = span <= 1 ? 3 * H : span <= 3 ? 12 * H : 24 * H;
    const ticks = [];
    for (let tk = Math.ceil(from / tickStep) * tickStep; tk <= to; tk += tickStep) ticks.push(tk);
    const fmtTick = ms => new Date(ms).toLocaleString(undefined, tickStep < 24 * H ? { weekday: 'short', hour: 'numeric', timeZone: timeZone() } : { weekday: 'short', month: 'short', day: 'numeric', timeZone: timeZone() });
    const clip = (a, b) => [Math.max(a, from), Math.min(b, to)];

    const grid = ticks.map(tk => `<line x1="${x(tk)}" x2="${x(tk)}" y1="${T - 6}" y2="${Hh}" stroke="#222830"/><text x="${x(tk) + 3}" y="${T - 10}">${esc(fmtTick(tk))}</text>`).join('');
    const lanes = rows.map((r, i) => `<rect x="0" y="${T + i * RH}" width="${W}" height="${RH}" fill="${i % 2 ? '#161B21' : '#1A2027'}"/><text x="8" y="${T + i * RH + 19}">${esc(r)}</text>`).join('');
    const downs = (downtime.data ?? []).filter(w => rows.includes(w.machineCode)).map(w => {
      const [a, b] = clip(new Date(w.startsAt).getTime(), w.endsAt ? new Date(w.endsAt).getTime() : now);
      if (b <= a) return '';
      const i = rows.indexOf(w.machineCode);
      return `<rect x="${x(a)}" y="${T + i * RH + 2}" width="${x(b) - x(a)}" height="${RH - 4}" fill="url(#hatch)" stroke="#EF4444" stroke-width=".8"><title>${esc(w.machineCode)} ${esc(w.kind)} downtime: ${esc(w.reason)}</title></rect>`;
    }).join('');
    const bars = ops.flatMap(o => (o.segments ?? []).map(g => {
      const [a, b] = clip(new Date(g.start).getTime(), new Date(g.end).getTime());
      if (b <= a) return '';
      const i = rows.indexOf(o.machineCode ?? `${o.workCenterCode} (no machine)`);
      const tip = `Order #${o.orderNumber} op ${o.sequence} ${o.description}\n${fmtDateTime(g.start)} – ${fmtDateTime(g.end)}\nStarts because: ${o.bindingConstraint} — ${o.bindingDetail}\nCrew: ${(o.operators ?? []).join(', ') || '—'}`;
      const w = x(b) - x(a);
      return `<g><rect x="${x(a)}" y="${T + i * RH + 6}" width="${Math.max(2, w)}" height="${RH - 12}" rx="2" fill="${TONE[orderStatus.get(o.orderNumber)] ?? '#6B7280'}" fill-opacity=".85"><title>${esc(tip)}</title></rect>
        ${w > 74 ? `<text x="${x(a) + 4}" y="${T + i * RH + 19}" fill="#0F1419" style="fill:#0F1419;font-weight:600">#${o.orderNumber}/${o.sequence}</text>` : ''}</g>`;
    })).join('');
    const nowLine = `<line x1="${x(now)}" x2="${x(now)}" y1="${T - 6}" y2="${Hh}" stroke="#F97316" stroke-width="2"/><text x="${x(now) + 4}" y="${Hh - 4}" style="fill:#F97316">now</text>`;

    page.innerHTML = `${head}
      <section class="panel">
        <div class="legend" style="justify-content:flex-start"><span><i style="background:#22C55E"></i>On time</span><span><i style="background:#F97316"></i>At risk</span><span><i style="background:#EF4444"></i>Late</span><span><i style="background:repeating-linear-gradient(45deg,#EF4444 0 2px,transparent 2px 5px)"></i>Downtime</span>
          <span class="faint">Schedule #${s.versionId} · ${esc(s.status)} · hover a bar for why it starts then</span></div>
        <div class="gantt"><svg viewBox="0 0 ${W} ${Hh}" width="${W}" style="min-width:${W}px" role="img" aria-label="Schedule timeline">
          <defs><pattern id="hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="6" height="6" fill="rgba(239,68,68,.12)"/><line x1="0" y1="0" x2="0" y2="6" stroke="#EF4444" stroke-width="1.5"/></pattern></defs>
          ${lanes}${grid}${downs}${bars}${nowLine}</svg></div>
      </section>`;
    wire();
  }

  function wire() {
    $$('[data-days]', page).forEach(b => b.addEventListener('click', () => { span = Number(b.dataset.days); render(); }));
    $$('[data-which]', page).forEach(b => b.addEventListener('click', () => { which = b.dataset.which; render(); }));
  }

  if (!approved.data && proposed.data) which = 'proposed';
  render();
}
