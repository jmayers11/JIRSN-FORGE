import { session, loadConfig, get, tryGet, statePost, signedIn, signInDevelopment, signOut, whenSignedOut, beginOidcSignIn, completeOidcSignIn, onCommand } from './api.js';
import { icon, img } from './icons.js';
import { esc, $, $$, setTimeZone, closeOverlays, drawer, toast } from './ui.js';
import { startDemoClock } from './demo-clock.js';
import { horizonPage } from './pages/horizon.js';
import { ordersPage } from './pages/orders.js';
import { schedulingPage } from './pages/scheduling.js';
import { workCentersPage } from './pages/workcenters.js';
import { maintenancePage } from './pages/maintenance.js';
import { timelinePage } from './pages/timeline.js';
import { workforcePage } from './pages/workforce.js';
import { workforceKanbanPage } from './pages/workforce-kanban.js';
import { machineKanbanPage } from './pages/machine-kanban.js';
import { performancePage } from './pages/performance.js';
import { settingsPage } from './pages/settings.js';
import { shopFloorPage } from './pages/shopfloor.js';
import { plantSetupPage } from './pages/plantsetup.js';
import { inventoryPage, receivingPage } from './pages/inventory.js';
import { mrpPage } from './pages/mrp.js';
import { procurementPage } from './pages/procurement.js';
import { qualityPage } from './pages/quality.js';
import { qmsPage } from './pages/qms.js';
import { qmsPlanningPage } from './pages/qms-planning.js';
import { investigationsPage } from './pages/investigations.js';
import { acceptancePage } from './pages/acceptance.js';
import { safetyRulesPage } from './pages/safety-rules.js';
import { accessPage } from './pages/access.js';
import { qmsAuditPage } from './pages/qms-audit.js';
import { qmsReviewPage } from './pages/qms-review.js';
import { qmsMetricsPage } from './pages/qms-metrics.js';
import { commercialQualityPage } from './pages/commercial-quality.js';
import { shippingPage } from './pages/shipping.js';
import { programsPage } from './pages/programs.js';
import { projectsPage } from './pages/projects.js';
import { reportsPage } from './pages/reports.js';
import { engineeringPage } from './pages/engineering.js';
import { analystPage } from './pages/analyst.js';
import { shiftContinuityPage } from './pages/shift-continuity.js';
import { notificationsPage } from './pages/notifications.js';
import { workInboxPage } from './pages/work-inbox.js';
import { dailyMissionPage } from './pages/daily-mission.js';
import { integrityPage } from './pages/integrity.js';
import { scenariosPage } from './pages/scenarios.js';
import { costingPage } from './pages/costing.js';
import { salesPage } from './pages/sales.js';
import { timesheetPage } from './pages/timesheet.js';
import { spcPage } from './pages/spc.js';
import { supplierScorecardPage } from './pages/supplier-scorecard.js';
import { riskRadarPage } from './pages/risk-radar.js';
import { operationsVisualizationsPage } from './pages/operations-visualizations.js';
import { loadDirectory } from './people.js';
import { roleTitle } from './roles.js';
import { isKioskMode } from './shell-mode.mjs';
import { filterNavGroups } from './navigation.mjs';
import { canAction } from './ui-authority.mjs';

const NAV = [
  { route: 'daily-mission', label: 'Daily Mission', icon: 'dashboard', page: dailyMissionPage },
  { route: 'home', label: 'Home', icon: 'dashboard', page: horizonPage },
  { route: 'supervisor', label: 'Plant Overview', icon: 'dashboard', page: horizonPage },
  { route: 'workforce-kanban', label: 'Workforce Kanban', icon: 'workforce', page: workforceKanbanPage },
  { route: 'machine-kanban', label: 'Machine Readiness', icon: 'production', page: machineKanbanPage },
  { route: 'performance', label: 'Training & Performance', icon: 'training', page: performancePage },
  { route: 'horizon', label: 'Horizon', icon: 'horizon', page: timelinePage },
  { route: 'programs', label: 'Programs', icon: 'trend', page: programsPage },
  { route: 'projects', label: 'Projects', icon: 'calendar', page: projectsPage },
  { route: 'orders', label: 'Production Orders', icon: 'orders', page: ordersPage },
  { route: 'engineering', label: 'Engineering', icon: 'blade', page: engineeringPage },
  { route: 'scheduling', label: 'Scheduling', icon: 'planning', page: schedulingPage },
  { route: 'scenarios', label: 'Scenarios', icon: 'scenarios', page: scenariosPage },
  { route: 'risk-radar', label: 'Risk Radar', icon: 'alert', page: riskRadarPage },
  { route: 'operations-visualizations', label: 'Operations Visualizations', icon: 'reports', page: operationsVisualizationsPage },
  { route: 'material-plan', label: 'Material Plan', icon: 'material', page: mrpPage },
  { route: 'work-centers', label: 'Work Centers', icon: 'production', page: workCentersPage },
  { route: 'tools', label: 'Tools & Calibration', icon: 'calibration', href: '#/work-centers?focus=tools' },
  { route: 'shop-floor', label: 'Shop Floor', icon: 'operator', page: shopFloorPage },
  { route: 'plant-setup', label: 'Plant Setup', icon: 'factory', page: plantSetupPage },
  { route: 'workforce', label: 'Workforce Records', icon: 'workforce', page: workforcePage },
  { route: 'safety-rules', label: 'Safety Rules', icon: 'safety', page: safetyRulesPage },
  { route: 'shift-continuity', label: 'Shift Continuity', icon: 'handover', page: shiftContinuityPage },
  { route: 'notifications', label: 'Notifications', icon: 'bell', page: notificationsPage },
  { route: 'my-work', label: 'My Work', icon: 'inbox', page: workInboxPage },
  { route: 'integrity', label: 'Data Integrity', icon: 'integrity', page: integrityPage },
  { route: 'inventory', label: 'Inventory', icon: 'inventory', page: inventoryPage },
  { route: 'receiving', label: 'Receiving', icon: 'inventory', page: receivingPage },
  { route: 'procurement', label: 'Procurement', icon: 'purchasing', page: procurementPage },
  { route: 'supplier-scorecard', label: 'Supplier Scorecards', icon: 'purchasing', page: supplierScorecardPage },
  { route: 'quality', label: 'Quality', icon: 'quality', page: qualityPage },
  { route: 'spc', label: 'Statistical Process Control', icon: 'analytics', page: spcPage },
  { route: 'acceptance', label: 'Product Acceptance', icon: 'acceptance', page: acceptancePage },
  { route: 'investigations', label: 'Investigations', icon: 'trace', page: investigationsPage },
  { route: 'qms', label: 'Quality Management', icon: 'qms', page: qmsPage },
  { route: 'qms-planning', label: 'QMS Planning', icon: 'target', page: qmsPlanningPage },
  { route: 'qms-audit', label: 'Audits & CAPA', icon: 'audit', page: qmsAuditPage },
  { route: 'qms-review', label: 'QMS Reviews', icon: 'review', page: qmsReviewPage },
  { route: 'qms-metrics', label: 'Quality Metrics', icon: 'analytics', page: qmsMetricsPage },
  { route: 'commercial-quality', label: 'Customer & Provider Quality', icon: 'commercial', page: commercialQualityPage },
  { route: 'maintenance', label: 'Maintenance', icon: 'maintenance', page: maintenancePage },
  { route: 'shipping', label: 'Shipping', icon: 'truck', page: shippingPage },
  { route: 'sales', label: 'Sales & Invoicing', icon: 'commercial', page: salesPage },
  { route: 'timesheet', label: 'Timekeeping', icon: 'calendar', page: timesheetPage },
  { route: 'costing', label: 'Costing', icon: 'analytics', page: costingPage },
  { route: 'reports', label: 'Reports', icon: 'reports', page: reportsPage },
  { route: 'analyst', label: 'Forge Analyst', icon: 'knowledge', page: analystPage },
  { route: 'access', label: 'Access Control', icon: 'access', page: accessPage },
];
const NAV_GROUPS = [
  ['Daily', ['daily-mission']],
  ['Supervisor', ['supervisor', 'operations-visualizations', 'risk-radar', 'workforce-kanban', 'machine-kanban', 'performance']],
  ['Plan', ['home', 'horizon', 'programs', 'projects', 'engineering', 'scheduling', 'scenarios', 'material-plan']],
  ['Sell', ['sales']],
  ['Make', ['orders', 'work-centers', 'tools', 'shop-floor', 'plant-setup', 'safety-rules', 'shift-continuity', 'inventory', 'receiving', 'procurement', 'supplier-scorecard', 'maintenance', 'shipping']],
  ['Quality', ['quality', 'spc', 'acceptance', 'investigations', 'qms', 'qms-planning', 'qms-audit', 'qms-review', 'qms-metrics', 'commercial-quality']],
  ['Admin', ['my-work', 'timesheet', 'notifications', 'workforce', 'costing', 'integrity', 'reports', 'analyst', 'access']],
];
const SETTINGS = { route: 'settings', label: 'Settings', icon: 'settings', page: settingsPage };

const NAV_ACCESS = {
  supervisor: ['workforce.supervise', 'workforce.manage'], 'workforce-kanban': ['workforce.supervise', 'workforce.manage', 'resources.floor-map.manage'],
  'machine-kanban': ['planning.readiness.read', 'maintenance.read', 'resources.read'],
  performance: ['workforce.read'],
  costing: ['costing.read'],
  sales: ['sales.read'],
  spc: ['quality.read'], 'supplier-scorecard': ['procurement.read'],
  timesheet: ['timekeeping.record', 'timekeeping.approve', 'timekeeping.read', 'timekeeping.codes.manage'],
  'risk-radar': ['planning.readiness.read'],
  'operations-visualizations': ['execution.orders.read', 'planning.readiness.read', 'workforce.read', 'workforce.supervise', 'workforce.manage', 'resources.floor-map.manage', 'planning.schedule.read', 'inventory.read'],
  home: ['planning.readiness.read', 'planning.schedule.read', 'execution.orders.read', 'resources.read', 'quality.read', 'inventory.read'],
  horizon: ['planning.schedule.read', 'planning.readiness.read'], programs: ['programs.read', 'programs.manage'],
  projects: ['projects.read', 'projects.manage'], orders: ['execution.orders.read', 'execution.orders.manage'],
  engineering: ['engineering.read', 'engineering.manage'], scheduling: ['planning.schedule.read', 'planning.schedule.propose', 'planning.schedule.approve'],
  scenarios: ['planning.schedule.read', 'planning.readiness.read'], 'material-plan': ['planning.mrp.read', 'planning.demand.manage'],
  'work-centers': ['resources.read', 'planning.readiness.read', 'execution.shopfloor.read'], 'shop-floor': ['execution.shopfloor.read'],
  tools: ['resources.read', 'planning.readiness.read', 'execution.shopfloor.read'],
  'plant-setup': ['resources.manage', 'resources.read'], workforce: ['workforce.read', 'workforce.manage', 'workforce.attendance-correction.request'],
  receiving: ['inventory.read', 'inventory.transact'],
  'safety-rules': ['safety.rules.read', 'safety.rules.draft', 'safety.rules.approve'],
  'shift-continuity': ['execution.shopfloor.read', 'workforce.read'], notifications: ['notifications.read'],
  integrity: ['kernel.audit.read'], inventory: ['inventory.read', 'inventory.transact'], procurement: ['procurement.read', 'procurement.manage'],
  quality: ['quality.read', 'quality.inspect'], acceptance: ['quality.read', 'quality.inspect', 'quality.product.release'],
  investigations: ['quality.read', 'quality.plan', 'quality.disposition'], qms: ['quality.qms.read'],
  'qms-planning': ['quality.qms.read', 'quality.qms.manage'], 'qms-audit': ['quality.audit.conduct', 'quality.audit.manage', 'quality.qms.read'],
  'qms-review': ['quality.qms.read', 'quality.qms.review'], 'qms-metrics': ['quality.qms.read'],
  'commercial-quality': ['quality.read', 'quality.qms.read'], maintenance: ['maintenance.read', 'maintenance.work', 'maintenance.plan'],
  shipping: ['inventory.read', 'inventory.shipping.ship'], reports: ['planning.reports.save', 'planning.schedule.read', 'quality.read'],
  analyst: ['planning.analyst.use'], access: ['kernel.access.manage'],
};

export { roleTitle };

// Brand artwork is cut from the JIRSN Forge identity sheet (img/brand); replace those files to update it everywhere.
const brand = () => `<div class="brand"><img class="lockup" src="/img/brand/forge-logo-primary.png" alt="JIRSN Forge"></div>`;
const loginBrand = () => `<div class="login-brand-head"><img class="login-hawk-mark" src="/img/brand/forge-logo-icon.png" alt=""><span class="word"><small>JIRSN</small><b>FORGE</b></span></div>`;

// ---------------------------------------------------------------- sign-in

function renderLogin(message) {
  const mode = session.config?.authMode;
  const app = $('#app');
  let form;
  if (mode === 'Development') {
    form = `
      <form class="field" id="login-form" autocomplete="on" style="gap:12px">
        <label for="employee">Role or username</label>
        <input class="input" id="employee" name="username" placeholder="e.g. planner or kiosk" autocomplete="username" required>
        <button class="btn primary big" type="submit">Sign in to Forge</button>
      </form>
      <button class="btn ghost big login-kiosk-launch" id="quick-kiosk-launch" type="button">Launch Operator Kiosk →</button>
      <div class="notice warn">Development sign-in trusts the name entered and asks for no password. Badge numbers are scanned on the Shop Floor screen after signing in as <b>kiosk</b>. Use this sign-in only with fictional or sample records.</div>`;
  } else if (mode === 'Oidc' && session.config.authority && session.config.clientId) {
    form = `<button class="btn primary big" id="oidc">Sign in with your organization</button>
      <div class="notice">You will sign in with your company account, then return here.</div>`;
  } else {
    form = `<div class="notice bad">Sign-in is not configured on this server. Set Forge:Authentication:Authority and Forge:Authentication:ClientId, or run the development build.</div>`;
  }
  app.innerHTML = `
    <main class="login">
      <div class="login-split">
        <section class="login-showcase" aria-label="JIRSN manufacturing suite">
          <img class="login-hawk-feature" src="/img/brand/bg-hero-hawk.jpg" alt="The original JIRSN redtail hawk artwork">
          ${mode === 'Development' ? '<div class="login-demo-badge" role="note">FICTIONAL / SAMPLE DATA · DEVELOPMENT ENVIRONMENT</div>' : ''}
          <div class="login-showcase-copy"><span class="eyebrow">JIRSN MANUFACTURING SUITE</span><h1>Plan. Synchronize.<br>Produce. Deliver.</h1>
            <p>One connected workspace for manufacturing planning, execution, quality and traceability.</p></div>
          <div class="login-suite-pills" aria-label="JIRSN products"><span>Forge · MRP / MES / QMS</span><span>Nexus · Identity</span><span>Sentinel · Audit</span></div>
        </section>
        <section class="login-card" aria-labelledby="login-heading">
          ${loginBrand()}
          <h2 id="login-heading">Manufacturing operations sign-in</h2>
          ${message ? `<div class="notice bad" role="alert">${esc(message)}</div>` : ''}
          ${form}
          <div class="faint login-help">Need help? Ask your Forge security administrator.</div>
        </section>
      </div>
    </main>`;
  $('#login-form')?.addEventListener('submit', async e => {
    e.preventDefault();
    signInDevelopment($('#employee').value);
    await start();
  });
  $('#quick-kiosk-launch')?.addEventListener('click', async () => {
    signInDevelopment('kiosk');
    location.hash = '#/shop-floor';
    await start();
  });
  $('#oidc')?.addEventListener('click', () => beginOidcSignIn().catch(err => renderLogin(err.message)));
  $('#employee')?.focus();
}

// ---------------------------------------------------------------- shell

let lookups = null; // search index, loaded lazily
let supervisorKioskMode = null;
let showAllModules = false;
let accessibleWorkspaces = [];
onCommand(() => { lookups = null; $('#search-results')?.classList.add('hidden'); });

function renderShell() {
  const me = session.me;
  const initials = me.displayName.split(/\s+/).map(p => p[0]).join('').slice(0, 2).toUpperCase();
  const navItem = n => `<li><a href="${n.href ?? `#/${n.route}`}" data-route="${n.route}">${img(n.icon)}<span>${esc(n.label)}</span>${n.badge ?? ''}</a></li>`;
  const groupedNav = () => {
    const activeRoute = location.hash.replace(/^#\/?/, '').split(/[/?#]/)[0] || 'home';
    const groupsToShow = filterNavGroups(NAV_GROUPS, NAV_ACCESS, permission => canAction(me, permission), showAllModules);
    const byRoute = new Map(NAV.map(item => [item.route, item]));
    const assigned = new Set();
    const groups = groupsToShow.map(([label, routes]) => {
      const entries = routes.map(route => byRoute.get(route)).filter(Boolean);
      entries.forEach(item => assigned.add(item.route));
      return entries.length ? `<li class="nav-group"><details data-nav-group="${esc(label)}" ${routes.includes(activeRoute) ? 'open' : ''}><summary>${esc(label)}<span>${entries.length}</span></summary><ul class="nav-sub">${entries.map(navItem).join('')}</ul></details></li>` : '';
    }).join('');
    const remaining = NAV.filter(item => !assigned.has(item.route));
    return groups + (remaining.length ? `<li class="nav-group"><details><summary>More</summary><ul class="nav-sub">${remaining.map(navItem).join('')}</ul></details></li>` : '');
  };
  $('#app').innerHTML = `
    <div class="shell" id="shell">
      <nav class="sidebar" aria-label="Main">
        ${brand()}
        <ul class="nav" id="main-nav">${groupedNav()}</ul>
        <div class="nav-tools"><button class="btn small ghost" id="show-all-modules" type="button" aria-pressed="${showAllModules}">${showAllModules ? 'Show permitted modules' : 'Show all modules'}</button></div>
        <ul class="nav nav-bottom">${navItem(SETTINGS)}</ul>
      </nav>
      <div class="main">
        <div id="demo-clock" hidden></div>
        <header class="topbar">
          <button class="icon-btn menu-btn" id="menu" aria-label="Menu">${icon('menu')}</button>
          <div class="search">
            ${icon('search')}
            <input class="input" id="search" type="search" placeholder="Search orders, parts, employees, machines, or ask Forge…" autocomplete="off" aria-label="Search">
            <div class="search-results hidden" id="search-results"></div>
          </div>
          <label class="workspace-switch hidden" id="workspace-switch-wrap"><span class="sr-only">Switch workspace</span><select class="input" id="workspace-switcher" aria-label="Switch workspace"></select></label>
          <div class="top-icons">
            <button class="icon-btn" id="bell" aria-label="Open notifications">${icon('bell')}<span class="count hidden" id="bell-count"></span></button>
            <button class="icon-btn" id="help" aria-label="Help">${icon('help')}</button>
          </div>
          <div class="user-card" id="user-card" title="Settings">
            <div class="avatar">${esc(initials)}</div>
            <div class="who"><b>${esc(me.displayName)}</b><span>${esc(me.roles.length ? roleTitle(me.roles[0]) : 'No roles yet')}</span></div>
            ${icon('down')}
          </div>
        </header>
        <main class="content" id="page"></main>
      </div>
    </div>`;
  $('#menu').onclick = () => $('#shell').classList.toggle('nav-open');
  const allModules = $('#show-all-modules');
  allModules.onclick = () => {
    showAllModules = !showAllModules;
    allModules.setAttribute('aria-pressed', String(showAllModules));
    allModules.textContent = showAllModules ? 'Show permitted modules' : 'Show all modules';
    $('#main-nav').innerHTML = groupedNav();
  };
  $('#user-card').onclick = () => { location.hash = '#/settings'; };
  $('#workspace-switcher').onchange = event => { if (event.target.value) location.hash = `#/${event.target.value}`; };
  $('#bell').onclick = () => { location.hash = '#/notifications'; };
  $('#help').onclick = showHelp;
  wireSearch();
  refreshBell();
}

async function refreshBell() {
  const { data } = await tryGet('/api/notifications/inbox?unreadOnly=true');
  const n = data?.length ?? 0;
  const el = $('#bell-count');
  if (!el) return;
  el.textContent = n > 99 ? '99+' : String(n);
  el.classList.toggle('hidden', !n);
  $('#bell').title = data ? `${n} unread notification${n === 1 ? '' : 's'}` : 'Notifications are not visible with your roles';
}

function showHelp() {
  drawer('Help', `
    <p>Forge shows what its engines worked out from your records. Every figure links back to the records and rules behind it — open a work center, order or constraint to see the evidence.</p>
    <p><b>Nothing here is estimated.</b> Where Forge has no data yet the page says so instead of showing a number.</p>
    <p><b>Changes need a reason.</b> Releasing orders, approving schedules and resolving constraints ask for a reason that is kept in the audit trail, and several of them need a second person.</p>
    <p class="muted">Signed in as ${esc(session.me.displayName)} (${esc(session.me.roles.map(roleTitle).join(', ') || 'no roles')}).</p>`);
}

function wireSearch() {
  const input = $('#search'), box = $('#search-results');
  let timer;
  const close = () => box.classList.add('hidden');
  input.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(() => runSearch(input.value), 150); });
  input.addEventListener('focus', () => { if (input.value) runSearch(input.value); });
  input.addEventListener('keydown', e => { if (e.key === 'Escape') { close(); input.blur(); } });
  document.addEventListener('click', e => { if (!e.target.closest('.search')) close(); });
  box.addEventListener('click', e => { if (e.target.closest('a')) { close(); input.value = ''; } });
}

async function runSearch(text) {
  const box = $('#search-results');
  const q = text.trim().toLowerCase();
  if (!q) { box.classList.add('hidden'); return; }
  if (!lookups) {
    const [orders, items, resources, people] = await Promise.all([
      tryGet('/api/orders/'), tryGet('/api/engineering/items'), tryGet('/api/resources/'), tryGet('/api/kernel/people'),
    ]);
    lookups = { orders: orders.data ?? [], items: items.data ?? [], resources: resources.data ?? {}, people: people.data ?? [] };
  }
  const hit = (...fields) => fields.some(f => String(f ?? '').toLowerCase().includes(q));
  const rows = [
    ...lookups.orders.filter(o => hit(o.orderNumber, o.partNumber)).map(o => ['Order', `#${o.orderNumber} · ${o.partNumber} × ${o.quantity}`, `#/orders/${o.orderNumber}`]),
    ...lookups.items.filter(i => hit(i.partNumber, i.description)).map(i => ['Part', `${i.partNumber} — ${i.description}`, `#/orders?part=${encodeURIComponent(i.partNumber)}`]),
    ...(lookups.resources.machines ?? []).filter(m => hit(m.code, m.name)).map(m => ['Machine', `${m.code} — ${m.name}`, `#/work-centers/${encodeURIComponent(m.workCenterCode)}`]),
    ...(lookups.resources.workCenters ?? []).filter(w => hit(w.code, w.name)).map(w => ['Work center', `${w.code} — ${w.name}`, `#/work-centers/${encodeURIComponent(w.code)}`]),
    ...lookups.people.filter(p => hit(p.displayName, p.idpSubject)).map(p => ['Employee', p.displayName, `#/workforce`]),
  ].slice(0, 12);
  const ask = q.includes(' ') ? `<a href="#/analyst?q=${encodeURIComponent(text.trim())}"><span class="kind">Ask Forge</span><span>${esc(text.trim())}</span></a>` : '';
  box.innerHTML = rows.length
    ? rows.map(([k, label, href]) => `<a href="${esc(href)}"><span class="kind">${esc(k)}</span><span>${esc(label)}</span></a>`).join('') + ask
    : `<div class="empty" style="border:0">No orders, parts, machines or people match “${esc(text)}”.</div>${ask}`;
  box.classList.remove('hidden');
}

// ---------------------------------------------------------------- routing

let current = null;
let routingHash = null;
async function route() {
  if (!session.me) return;
  if (location.hash === routingHash) return;
  routingHash = location.hash;
  closeOverlays();
  $('#shell')?.classList.remove('nav-open');
  const [path, query] = location.hash.replace(/^#\/?/, '').split('?');
  const [name, ...rest] = path.split('/');
  const entry = [...NAV, SETTINGS].find(n => n.route === (name || 'home')) ?? NAV[0];
  const workspaceSelect = $('#workspace-switcher');
  if (workspaceSelect && accessibleWorkspaces.some(workspace => workspace.route === entry.route)) workspaceSelect.value = entry.route;
  const supervisorCanToggleKiosk = entry.route === 'shop-floor' && session.me.roles.includes('Supervisor');
  const setKioskMode = value => {
    if (!supervisorCanToggleKiosk) return;
    supervisorKioskMode = !!value;
    $('#shell')?.classList.toggle('kiosk-mode', isKioskMode(entry.route, session.me.roles, supervisorKioskMode));
  };
  $('#shell')?.classList.toggle('kiosk-mode', isKioskMode(entry.route, session.me.roles, supervisorKioskMode));
  const focus = new URLSearchParams(location.hash.split('?')[1] ?? '').get('focus');
  const activeRoute = entry.route === 'work-centers' && focus === 'tools' ? 'tools' : entry.route;
  $$('.nav a').forEach(a => a.classList.toggle('active', a.dataset.route === activeRoute));
  // Each visit renders into a fresh element, so a slow page that finishes after you moved on writes nowhere.
  const page = document.createElement('div');
  page.style.display = 'contents';
  $('#page').replaceChildren(page);
  current?.dispose?.();
  page.innerHTML = '<div class="muted">Loading…</div>';
  try {
    current = await entry.page(page, { params: rest.map(decodeURIComponent), query: new URLSearchParams(query ?? ''), route: entry.route, me: session.me, refreshBell,
      kioskMode: isKioskMode(entry.route, session.me.roles, supervisorKioskMode), setKioskMode,
      operationalVisualizationsEnabled: session.config?.operationalVisualizationsEnabled !== false }) ?? null;
  } catch (e) {
    page.innerHTML = `<div class="empty"><b>This page could not load</b>${esc(e.message)}</div>`;
  }
}

// ---------------------------------------------------------------- start

async function start() {
  if (!signedIn()) return renderLogin();
  try {
    const me = await get('/api/kernel/me');
    session.me = me;
  } catch (e) {
    const subject = session.subject;
    signOut();
    return renderLogin(e.status === 403
      ? (/^\d+$/.test(subject ?? '')
        ? `“${subject}” looks like a badge number. Badges are scanned on the Shop Floor screen: sign in as kiosk, open Shop Floor, then scan ${subject}.`
        : `${subject ? `“${subject}”` : 'This account'} is not registered in Forge. Ask a security administrator to register you.`)
      : e.message);
  }
  const [resources] = await Promise.all([tryGet('/api/resources/'), loadDirectory()]);
  setTimeZone(resources.data?.sites?.[0]?.timeZone);
  renderShell();
  await startDemoClock($('#demo-clock'));
  const [missionSession, workspacePreferences] = await Promise.all([
    statePost('/api/daily-mission/session').catch(() => null),
    get('/api/daily-mission/preferences').catch(() => null),
  ]);
  if (workspacePreferences?.options) {
    accessibleWorkspaces = workspacePreferences.options;
    const switcher = $('#workspace-switcher');
    if (switcher) {
      switcher.innerHTML = accessibleWorkspaces.map(workspace => `<option value="${esc(workspace.route)}">${esc(workspace.label)}</option>`).join('');
      $('#workspace-switch-wrap')?.classList.toggle('hidden', accessibleWorkspaces.length < 2);
    }
  }
  if (missionSession) session.me.dailyMissionSession = missionSession;
  if (missionSession?.briefing?.shouldShow) {
    location.hash = '#/daily-mission';
  } else if (workspacePreferences?.landingRoute && !location.hash.replace(/^#\/?/, '').includes('?')
      && location.hash.replace(/^#\/?/, '').split('/').filter(Boolean).length <= 1) {
    location.hash = `#/${workspacePreferences.landingRoute}`;
  }
  await route();
}

whenSignedOut(() => { renderLogin('Your sign-in has ended. Sign in again.'); });
window.addEventListener('hashchange', route);

(async () => {
  await loadConfig();
  try {
    if (session.config.authMode === 'Oidc') await completeOidcSignIn();
  } catch (e) {
    return renderLogin(e.message);
  }
  await start();
})().catch(e => toast(e.message, true));

export { start, renderLogin };
