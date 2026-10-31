import { icon } from '../icons.js';
import { esc } from '../ui.js';
import { canAction } from '../ui-authority.mjs';

const VIEWS = [
  { title: 'Manufacturing flow map', icon: 'factory', permission: 'execution.orders.read', gated: true,
    href: '#/orders', area: 'Production orders',
    description: 'Follow an order through its released operations, readiness evidence, recorded crew, and downstream blockers.',
    open: 'Browse orders', steps: 'Choose an order → Overview → flow map' },
  { title: 'Production risk radar', icon: 'alert', permission: 'planning.readiness.read', gated: true,
    href: '#/risk-radar', area: 'Supervisor',
    description: 'Review source-linked signals by when they may affect production, with current, 7-day, and 30-day views.',
    open: 'Open Risk Radar', steps: 'Current · next 7 days · next 30 days' },
  { title: 'Interactive plant floor', icon: 'workforce', permissions: ['workforce.supervise', 'workforce.manage', 'resources.floor-map.manage'],
    href: '#/workforce-kanban', area: 'Supervisor',
    description: 'See work centers, people, machine readiness, and staffing proposals on the 4×4 board or configured plant map.',
    open: 'Open plant floor', steps: '4×4 staffing · plant map' },
  { title: 'Work-order readiness', icon: 'check', permission: 'execution.orders.read',
    href: '#/orders', area: 'Production orders',
    description: 'Inspect the order’s readiness dimensions and evidence, then open the owning workflow for any blocker.',
    open: 'Browse orders', steps: 'Choose an order → Overview → readiness' },
  { title: 'Workforce coverage matrix', icon: 'people', permission: 'workforce.read', gated: true,
    href: '#/performance?tab=coverage', area: 'Training & Performance',
    description: 'Compare people with operation requirements, current qualifications, machine authorization, shift, and availability.',
    open: 'Open workforce intelligence', steps: 'Coverage tab → select a site and shift' },
  { title: 'Production recovery comparison', icon: 'horizon', permissions: ['planning.schedule.read', 'planning.readiness.read'], gated: true,
    href: '#/scenarios', area: 'Scenarios',
    description: 'Evaluate independent recovery options against the same scenario snapshot without changing the approved schedule.',
    open: 'Open Scenarios', steps: 'Run a scenario → select recovery options → compare' },
  { title: 'Inventory location map', icon: 'inventory', permission: 'inventory.read', gated: true,
    href: '#/inventory?tab=map', area: 'Inventory',
    description: 'Explore configured storage locations, visible stock and quality state, and approved work-in-process capacity.',
    open: 'Open location map', steps: 'Search by part, lot, location, or work order' },
];

function allowed(view, me) {
  const permissions = view.permissions ?? [view.permission];
  return permissions.some(permission => canAction(me, permission));
}

export function operationsVisualizationsPage(page, ctx) {
  const visualizationsEnabled = ctx?.operationalVisualizationsEnabled !== false;
  const views = VIEWS.filter(view => allowed(view, ctx?.me) && (visualizationsEnabled || !view.gated));
  const hiddenCount = VIEWS.filter(view => allowed(view, ctx?.me) && visualizationsEnabled === false && view.gated).length;
  page.innerHTML = `
    <div class="ops-viz-page">
      <header class="ops-viz-hero">
        <div class="ops-viz-mark" aria-hidden="true">${icon('factory')}</div>
        <div class="ops-viz-hero-copy"><p class="eyebrow">CONNECTED OPERATIONS</p><h1>Operations Visualizations</h1>
          <p>Open the right live Forge workspace for the question in front of you. Each view keeps its source records and authorization rules.</p></div>
        <span class="ops-viz-count">${views.length} ${views.length === 1 ? 'view' : 'views'} available</span>
      </header>
      ${hiddenCount ? '<div class="notice warn" role="status">Some operational visualizations are disabled by this server’s feature setting. Existing operational workflows remain available in their usual workspaces.</div>' : ''}
      ${views.length ? `<div class="ops-viz-grid" aria-label="Available operational visualizations">${views.map((view, index) => `
        <article class="ops-viz-card" data-visualization="${esc(view.title)}">
          <div class="ops-viz-card-top"><span class="ops-viz-icon" aria-hidden="true">${icon(view.icon)}</span><span class="ops-viz-area">${esc(view.area)}</span><span class="ops-viz-number">${String(index + 1).padStart(2, '0')}</span></div>
          <h2>${esc(view.title)}</h2><p>${esc(view.description)}</p>
          <div class="ops-viz-steps">${esc(view.steps)}</div>
          <a class="ops-viz-open" href="${view.href}">${esc(view.open)}<span aria-hidden="true">→</span></a>
        </article>`).join('')}</div>` : '<section class="panel">No operational visualization is available for this account. Ask a Forge administrator to review your role permissions.</section>'}
      ${visualizationsEnabled && canAction(ctx?.me, 'workforce.read') ? `<a class="ops-viz-related" href="#/daily-mission"><span><b>Related signals in Daily Mission</b><small>Open order-linked source items together while keeping each source status separate.</small></span><span aria-hidden="true">→</span></a>` : ''}
    </div>`;
}

