import { tryGet } from '../api.js';
import { esc, fmtNumber, errorBlock, emptyBlock, tabifyPanels } from '../ui.js';
import { roleTitle } from '../roles.js';
import { appendPresence, appendDocumentTraining } from '../executable.js';
import { appendWorkforceAdmin } from './workforce-admin.js';
import { appendOwnAttendanceCorrections } from './workforce-intelligence.js';

export async function workforcePage(page, ctx) {
  const permissions = new Set(ctx.me?.permissions ?? []);
  if (permissions.has('workforce.attendance-correction.request')
      && !permissions.has('workforce.read') && !permissions.has('workforce.manage')) {
    page.innerHTML = '<div class="page-head"><div><h1>My Workforce</h1><div class="sub">Review your attendance records and request a correction when needed.</div></div></div>';
    await appendOwnAttendanceCorrections(page, ctx.me);
    return;
  }
  const now = Date.now();
  const iso = ms => new Date(ms).toISOString();
  const [people, capacity] = await Promise.all([
    tryGet('/api/kernel/people'),
    tryGet(`/api/planning/capacity?from=${encodeURIComponent(iso(now))}&to=${encodeURIComponent(iso(now + 7 * 864e5))}`),
  ]);
  const labor = capacity.data?.output?.qualifiedLabor ?? [];
  const quals = [...new Set(labor.map(l => l.qualification))].sort();
  const days = [...new Set(labor.map(l => l.day))].sort();
  const cell = (d, q) => labor.find(l => l.day === d && l.qualification === q);

  page.innerHTML = `
    <div class="page-head"><div><h1>Workforce Records</h1><div class="sub">People, worker records, badges, presence and controlled-document training. Reviews, training requests and the Command Center are under <a href="#/performance">Training &amp; Performance</a>.</div></div></div>
    <section class="panel"><header><h2>Qualified labor hours, next 7 days</h2></header>
      ${capacity.error ? errorBlock(capacity.error, 'qualified labor') : !labor.length ? emptyBlock('No qualified labor yet', 'Hours appear once workers have shifts and current certifications.') : `
      <div style="overflow-x:auto"><table class="table"><thead><tr><th>Qualification</th>${days.map(d => `<th>${esc(new Date(`${d}T12:00:00Z`).toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric', timeZone: 'UTC' }))}</th>`).join('')}</tr></thead><tbody>
      ${quals.map(q => `<tr><td><b>${esc(q)}</b></td>${days.map(d => { const c = cell(d, q); return `<td title="${esc(c?.people.join(', ') ?? '')}">${c ? `${fmtNumber(c.hours)} h <span class="faint">(${c.people.length})</span>` : '<span class="faint">—</span>'}</td>`; }).join('')}</tr>`).join('')}
      </tbody></table></div>
      <div class="faint" style="margin-top:6px">${esc(capacity.data.output.confidence)} Hover a cell for the people.</div>`}
    </section>
    <section class="panel"><header><h2>People</h2></header>
      ${people.error ? errorBlock(people.error, 'the people list') : !people.data.length ? emptyBlock('Nobody registered', 'A security administrator registers people.') : `
      <table class="table"><thead><tr><th>Name</th><th>Employee ID</th><th>Roles</th><th>Status</th></tr></thead><tbody>
      ${people.data.map(p => `<tr><td>${esc(p.displayName)}</td><td class="mono">${esc(p.idpSubject)}</td><td>${esc(p.roles.map(roleTitle).join(', ') || '—')}</td><td>${p.isActive ? '<span class="pill ok">Active</span>' : '<span class="pill none">Inactive</span>'}</td></tr>`).join('')}
      </tbody></table>`}
    </section>`;
  await appendOwnAttendanceCorrections(page, ctx.me);
  // V3.9: presence levels, and training on controlled documents.
  await appendPresence(page);
  await appendDocumentTraining(page, ctx.me);
  // Worker, presence, badge and qualification commands.
  await appendWorkforceAdmin(page, ctx.me, ctx.query.get('personId'));
  // One section at a time, like the work-order workspace. Worker records come first, so a profile link (?personId=) lands on them.
  tabifyPanels(page, [
    { label: 'Worker records', match: ['manage people'] },
    { label: 'People & roles', match: ['people'] },
    { label: 'Presence', match: ['presence now'] },
    { label: 'Labor supply', match: ['qualified labor hours'] },
    { label: 'Document training', match: ['training on controlled documents'] },
    { label: 'My attendance', match: ['my attendance records'] },
  ]);
}
