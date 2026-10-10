import { tryGet } from './api.js';
import { who } from './people.js';
import { esc, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, drawer } from './ui.js';
import { traceLink } from './pages/reports.js';

// "What changed?" (V2.12): the recorded events for one asset, machine, project, document, change or part,
// newest first, each linked to the record it comes from. Nothing is inferred; a quiet period is shown as quiet.

// Any button with data-history-subject (and optionally data-history-kind) opens the drawer.
document.addEventListener('click', e => {
  const b = e.target.closest?.('[data-history-subject]');
  if (b) { e.preventDefault(); historyDrawer(b.dataset.historySubject, b.dataset.historyKind || null); }
});

/** A "What changed?" button for any page header. */
export const historyButton = (subject, kind) => `<button class="btn small ghost" data-history-subject="${esc(subject)}" data-history-kind="${esc(kind)}">What changed?</button>`;

const PERIODS = [[7, '7 days'], [30, '30 days'], [90, '90 days'], [365, '1 year'], [0, 'All']];

/** Opens "What changed?" for a subject in a drawer. kind narrows the lookup (asset, machine, project, document, change, item). */
export async function historyDrawer(subject, kind, days = 0) {
  const d = drawer(`What changed? · ${subject}`, '<p class="muted">Loading…</p>');
  async function load() {
    const q = new URLSearchParams();
    if (kind) q.set('kind', kind);
    if (days) q.set('days', String(days));
    const res = await tryGet(`/api/history/${encodeURIComponent(subject)}?${q}`);
    if (res.error) { d.element.innerHTML = errorBlock(res.error, 'this history'); return; }
    const h = res.data;
    d.element.innerHTML = `
      <p><b>${esc(h.title)}</b></p>
      <div class="seg" role="group" aria-label="Period" style="margin-bottom:10px">${PERIODS.map(([n, label]) => `<button data-days="${n}" class="${n === days ? 'on' : ''}">${esc(label)}</button>`).join('')}</div>
      ${h.events.length ? eventsTable(h.events) : emptyBlock('Nothing recorded', days ? `No recorded event in the last ${days} days.` : 'No event is recorded for this yet.')}
      <div class="faint" style="margin-top:8px">${h.notes.map(esc).join('<br>')}</div>`;
    $$('[data-days]', d.element).forEach(b => b.addEventListener('click', () => { days = Number(b.dataset.days); load(); }));
    $$('a[href^="#/"]', d.element).forEach(a => a.addEventListener('click', () => d.close()));
  }
  await load();
}

export function eventsTable(events) {
  return `<table class="table"><thead><tr><th>When</th><th>What</th><th>Detail</th><th>Who</th><th>Record</th></tr></thead><tbody>
    ${events.map(e => `<tr><td>${esc(fmtDateTime(e.at))}</td><td>${esc(e.kind)}</td><td>${esc(e.title)}</td><td>${esc(e.actor ? who(e.actor) : '—')}</td>
      <td>${traceLink(e.link)} <span class="faint">${esc(e.source)}</span></td></tr>`).join('')}
    </tbody></table>`;
}

/** Horizon's "Coming up" panel: future events as the records state them. */
export function futureEventsHtml(events, days) {
  if (!events.length) return emptyBlock('Nothing dated ahead', `No record states anything for the next ${days} days.`);
  return `<table class="table"><thead><tr><th>Date</th><th>What</th><th>Detail</th><th>Record</th></tr></thead><tbody>
    ${events.map(e => `<tr><td>${esc(fmtDate(e.at))}</td><td>${esc(e.kind)}</td><td>${esc(e.title)}</td><td>${traceLink(e.link)} <span class="faint">${esc(e.source)}</span></td></tr>`).join('')}
    </tbody></table>`;
}
