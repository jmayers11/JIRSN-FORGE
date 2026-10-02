import { esc } from '../ui.js';

// Areas in the plan that have no data in Forge yet. They say so instead of showing sample numbers.
const PLANNED = {
  quality: { title: 'Quality', phase: 'Phase 7 — Programs, quality, maintenance and shipping', items: ['Inspection plans and results', 'Nonconformances and holds', 'Quality as a readiness dimension'] },
  shipping: { title: 'Shipping', phase: 'Phase 7 — Programs, quality, maintenance and shipping', items: ['Ship dates against customer need dates', 'Late shipments by cause', 'Shipping work center flow'] },
  reports: { title: 'Reports', phase: 'Phase 8 — Insight and Horizon history', items: ['Plan versus actual output', 'Production attainment', 'Constraint history and time-to-resolve'] },
  analyst: { title: 'Forge Analyst', phase: 'Phase 9 — Forge Analyst (optional AI)', items: ['Plain-language questions over Forge data', 'Answers that cite the records and engine runs they came from', 'Never changes data on its own'] },
};

export function plannedPage(key) {
  return async page => {
    const p = PLANNED[key];
    page.innerHTML = `
      <div class="planned panel">
        <h1>${esc(p.title)}</h1>
        <div class="muted">Not built yet: arrives in ${esc(p.phase)}.</div>
        <p>Forge has no ${esc(p.title.toLowerCase())} data to show, so this page shows none rather than sample figures. When this phase lands it will cover:</p>
        <ul>${p.items.map(i => `<li>${esc(i)}</li>`).join('')}</ul>
      </div>`;
  };
}
