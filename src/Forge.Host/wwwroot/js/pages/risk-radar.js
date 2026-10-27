import { tryGet } from '../api.js';
import { esc, $$, fmtDateTime, errorBlock } from '../ui.js';

// Production Risk Radar (owner request 2026-10-27). Source-backed signals banded by when they start to affect work:
// High = today up to 6 days out, Medium = 7 to 29 days, Low = 30 days or more. Nothing is scored or merged.

const VIEWS = [['current', 'Current (today)'], ['7d', 'Next 7 days'], ['30d', 'Next 30 days']];
const BAND = { High: { tone: 'bad', title: 'High' }, Medium: { tone: 'warn', title: 'Medium' }, Low: { tone: 'info', title: 'Low' } };
const CATEGORY = {
  ReadinessBlocker: 'Operation blocked', MaterialRisk: 'Material', QualityNcr: 'Open NCR', ProductionStoppage: 'Stoppage',
  MachineDowntime: 'Machine downtime', RootCauseAssessment: 'Root cause',
};
const label = category => CATEGORY[category] ?? category.replace(/Hold$/, ' hold');

/** Where a link points in Forge, or null when Forge has no page for it. */
export function linkHref(link) {
  if (link.targetType === 'Order') return `#/orders/${encodeURIComponent(link.targetId)}`;
  if (link.targetType === 'Operation') return `#/orders/${encodeURIComponent(link.targetId.split('/')[0])}`;
  if (link.targetType === 'Part') return `#/engineering/${encodeURIComponent(link.targetId)}`;
  return null;
}

function linkText(link) {
  if (link.targetType === 'Operation') { const [order, seq] = link.targetId.split('/'); return `Order #${order} · op ${seq}`; }
  if (link.targetType === 'Order') return `Order #${link.targetId}`;
  return `${link.targetType} ${link.targetId}`;
}

export function itemHtml(item) {
  const s = item.signal;
  const links = (s.links ?? []).filter(link => ['Order', 'Operation', 'Machine', 'Part', 'Supply'].includes(link.targetType));
  const seen = new Set();
  const chips = links.filter(link => { const key = linkText(link); if (seen.has(key)) return false; seen.add(key); return true; })
    .map(link => { const href = linkHref(link); return href ? `<a class="chip" href="${href}">${esc(linkText(link))}</a>` : `<span class="chip">${esc(linkText(link))}</span>`; }).join('');
  return `<article class="risk-card" data-band="${esc(item.band)}">
    <header><b>${esc(label(s.category))}</b><span class="pill ${BAND[item.band]?.tone ?? 'none'}">${item.daysOut === 0 ? 'Today' : `${item.daysOut} day${item.daysOut === 1 ? '' : 's'}`}</span></header>
    <div class="faint">${esc(s.state)} · ${esc(item.impactBasis)}</div>
    <div class="faint">Affects work ${esc(fmtDateTime(item.impactAt))}${s.causeState && s.causeState !== 'Unattributed' ? ` · cause ${esc(s.causeState.toLowerCase())}` : ''}</div>
    ${chips ? `<div class="risk-card-links">${chips}</div>` : ''}
  </article>`;
}

export function radarHtml(radar) {
  const sources = radar.evidence?.sources ?? [];
  const withheld = sources.filter(source => source.status !== 'Included');
  return `<div class="risk-bands">${radar.bands.map(band => {
    const items = radar.items.filter(item => item.band === band.band);
    return `<section class="risk-lane" data-band="${esc(band.band)}" aria-labelledby="risk-lane-${esc(band.band)}">
      <header class="risk-lane-head"><h2 id="risk-lane-${esc(band.band)}">${esc(BAND[band.band]?.title ?? band.band)}</h2><span class="pill ${BAND[band.band]?.tone ?? 'none'}">${band.count}</span></header>
      <div class="faint risk-rule">${esc(band.rule)}</div>
      <div class="risk-lane-cards">${items.length ? items.map(itemHtml).join('') : '<p class="faint">Nothing in this band for this view.</p>'}</div></section>`;
  }).join('')}</div>
  <div class="faint">Read ${esc(fmtDateTime(radar.evidence?.read?.readCompletedAt))} in one database snapshot · site day ${esc(radar.today)} (${esc(radar.timeZone)})${radar.outsideView ? ` · ${radar.outsideView} later signal(s) are outside this view` : ''}.</div>
  ${withheld.length ? `<div class="notice warn">Not shown because your role cannot read them: ${withheld.map(source => esc(source.code)).join(', ')}. An empty band does not mean those areas are clear.</div>` : ''}
  <details class="panel"><summary>How the radar works</summary>${(radar.evidence?.limitations ?? []).map(text => `<p class="faint">${esc(text)}</p>`).join('')}
    <p class="faint">Each signal is placed by when it starts to affect work: now for conditions in force (open NCRs, holds, stoppages, machines down, overdue supply), the recorded start of upcoming downtime, the approved schedule start of the affected operation, or the order's due date when the schedule does not place it. Signals on the same order or machine are shown separately; sharing an order does not mean they share a cause.</p></details>`;
}

export async function riskRadarPage(page, ctx) {
  if (ctx?.operationalVisualizationsEnabled === false) {
    page.innerHTML = '<div class="page-head"><div><h1>Risk Radar</h1></div></div><div class="notice">Operational visualizations are turned off on this server (Forge:Features:OperationalVisualizations).</div>';
    return;
  }
  let view = VIEWS.some(([key]) => key === ctx?.query?.get('view')) ? ctx.query.get('view') : 'current';
  const load = async () => {
    page.innerHTML = `<div class="page-head"><div><h1>Risk Radar</h1><div class="sub">What will affect production, banded by how soon: High today to 6 days, Medium 7 to 29 days, Low 30 days or later. Every card links to its source record.</div></div>
      <div class="spacer"></div><div class="seg" role="tablist" aria-label="Radar view">${VIEWS.map(([key, text]) => `<button type="button" role="tab" data-view="${key}" aria-selected="${key === view}" class="${key === view ? 'on' : ''}">${esc(text)}</button>`).join('')}</div>
      <button class="btn" data-refresh-radar type="button">Refresh</button></div><div data-radar class="muted">Reading the current evidence…</div>`;
    $$('[data-view]', page).forEach(button => button.addEventListener('click', () => { view = button.dataset.view; void load(); }));
    page.querySelector('[data-refresh-radar]')?.addEventListener('click', () => { void load(); });
    const result = await tryGet(`/api/planning/risk-radar?view=${encodeURIComponent(view)}`);
    const host = page.querySelector('[data-radar]');
    host.className = '';
    host.innerHTML = result.error ? errorBlock(result.error, 'the risk radar') : radarHtml(result.data);
  };
  await load();
}
