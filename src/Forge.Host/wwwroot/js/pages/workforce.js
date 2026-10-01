import { tryGet } from '../api.js';
import { esc, fmtNumber, errorBlock, emptyBlock } from '../ui.js';
import { roleTitle } from '../app.js';

export async function workforcePage(page) {
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
    <div class="page-head"><div><h1>Workforce</h1><div class="sub">Qualified labor supply from shift calendars, certifications and recorded absences.</div></div></div>
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
}
