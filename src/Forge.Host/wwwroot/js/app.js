import { session, loadConfig, get, tryGet, signedIn, signInDevelopment, signOut, whenSignedOut, beginOidcSignIn, completeOidcSignIn } from './api.js';
import { icon, img } from './icons.js';
import { esc, $, $$, setTimeZone, closeOverlays, drawer, toast } from './ui.js';
import { horizonPage } from './pages/horizon.js';
import { ordersPage } from './pages/orders.js';
import { schedulingPage } from './pages/scheduling.js';
import { workCentersPage } from './pages/workcenters.js';
import { maintenancePage } from './pages/maintenance.js';
import { timelinePage } from './pages/timeline.js';
import { workforcePage } from './pages/workforce.js';
import { settingsPage } from './pages/settings.js';
import { shopFloorPage } from './pages/shopfloor.js';
import { plantSetupPage } from './pages/plantsetup.js';
import { inventoryPage } from './pages/inventory.js';
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
import { integrityPage } from './pages/integrity.js';
import { scenariosPage } from './pages/scenarios.js';
import { loadDirectory } from './people.js';
import { roleTitle } from './roles.js';

const NAV = [
  { route: 'home', label: 'Home', icon: 'dashboard', page: horizonPage },
  { route: 'horizon', label: 'Horizon', icon: 'horizon', page: timelinePage },
  { route: 'programs', label: 'Programs', icon: 'trend', page: programsPage },
  { route: 'projects', label: 'Projects', icon: 'calendar', page: projectsPage },
  { route: 'orders', label: 'Production Orders', icon: 'orders', page: ordersPage },
  { route: 'engineering', label: 'Engineering', icon: 'blade', page: engineeringPage },
  { route: 'scheduling', label: 'Scheduling', icon: 'planning', page: schedulingPage },
  { route: 'scenarios', label: 'Scenarios', icon: 'scenarios', page: scenariosPage },
  { route: 'material-plan', label: 'Material Plan', icon: 'material', page: mrpPage },
  { route: 'work-centers', label: 'Work Centers', icon: 'production', page: workCentersPage },
  { route: 'shop-floor', label: 'Shop Floor', icon: 'operator', page: shopFloorPage },
  { route: 'plant-setup', label: 'Plant Setup', icon: 'factory', page: plantSetupPage },
  { route: 'workforce', label: 'Workforce', icon: 'workforce', page: workforcePage },
  { route: 'safety-rules', label: 'Safety Rules', icon: 'safety', page: safetyRulesPage },
  { route: 'shift-continuity', label: 'Shift Continuity', icon: 'handover', page: shiftContinuityPage },
  { route: 'notifications', label: 'Notifications', icon: 'bell', page: notificationsPage },
  { route: 'my-work', label: 'My Work', icon: 'inbox', page: workInboxPage },
  { route: 'integrity', label: 'Data Integrity', icon: 'integrity', page: integrityPage },
  { route: 'inventory', label: 'Inventory', icon: 'inventory', page: inventoryPage },
  { route: 'procurement', label: 'Procurement', icon: 'purchasing', page: procurementPage },
  { route: 'quality', label: 'Quality', icon: 'quality', page: qualityPage },
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
  { route: 'reports', label: 'Reports', icon: 'reports', page: reportsPage },
  { route: 'analyst', label: 'Forge Analyst', icon: 'knowledge', page: analystPage },
  { route: 'access', label: 'Access Control', icon: 'access', page: accessPage },
];
const NAV_GROUPS = [
  ['Plan', ['home', 'horizon', 'programs', 'projects', 'engineering', 'scheduling', 'scenarios', 'material-plan']],
  ['Make', ['orders', 'work-centers', 'shop-floor', 'plant-setup', 'workforce', 'safety-rules', 'shift-continuity', 'inventory', 'procurement', 'maintenance', 'shipping']],
  ['Quality', ['quality', 'acceptance', 'investigations', 'qms', 'qms-planning', 'qms-audit', 'qms-review', 'qms-metrics', 'commercial-quality']],
  ['Admin', ['my-work', 'notifications', 'integrity', 'reports', 'analyst', 'access']],
];
const SETTINGS = { route: 'settings', label: 'Settings', icon: 'settings', page: settingsPage };

export { roleTitle };

// Brand artwork is cut from the JIRSN Forge identity sheet (img/brand); replace those files to update it everywhere.
const brand = () => `<div class="brand"><img class="lockup" src="/img/brand/forge-logo-primary.png" alt="JIRSN Forge"></div>`;
const loginBrand = () => `<div class="brand"><img class="login-lockup" src="/img/brand/login-lockup.jpg" alt="JIRSN Forge"></div>`;

// ---------------------------------------------------------------- sign-in

function renderLogin(message) {
  const mode = session.config?.authMode;
  const app = $('#app');
  let form;
  if (mode === 'Development') {
    form = `
      <form class="field" id="login-form" autocomplete="on" style="gap:12px">
        <input class="input" id="employee" name="username" placeholder="Sign-in name (e.g. planner or kiosk)" autocomplete="username" required>
        <button class="btn primary big" type="submit">Sign In</button>
      </form>
      <div class="notice warn">Development sign-in: Forge trusts the sign-in name typed here and asks for no password.
        Badge numbers are not sign-ins: sign in as <b>kiosk</b>, open Shop Floor, and scan the badge there.
        It works only on a development build. In production, people sign in through your company identity provider.</div>`;
  } else if (mode === 'Oidc' && session.config.authority && session.config.clientId) {
    form = `<button class="btn primary big" id="oidc">Sign In</button>
      <div class="notice">You will sign in with your company account, then return here.</div>`;
  } else {
    form = `<div class="notice bad">Sign-in is not configured on this server. Set Forge:Authentication:Authority and Forge:Authentication:ClientId, or run the development build.</div>`;
  }
  app.innerHTML = `
    <div class="login">
      <img class="login-hero" src="/img/brand/hero-banner.jpg" alt="JIRSN Forge — Plan. Synchronize. Produce. Deliver.">
      <div class="login-card">
        ${loginBrand()}
        ${message ? `<div class="notice bad">${esc(message)}</div>` : ''}
        ${form}
        <div class="faint" style="text-align:center;font-size:12px">Need help? Ask your Forge security administrator.</div>
      </div>
    </div>`;
  $('#login-form')?.addEventListener('submit', async e => {
    e.preventDefault();
    signInDevelopment($('#employee').value);
    await start();
  });
  $('#oidc')?.addEventListener('click', () => beginOidcSignIn().catch(err => renderLogin(err.message)));
  $('#employee')?.focus();
}

// ---------------------------------------------------------------- shell

let lookups = null; // search index, loaded lazily

function renderShell() {
  const me = session.me;
  const initials = me.displayName.split(/\s+/).map(p => p[0]).join('').slice(0, 2).toUpperCase();
  const navItem = n => `<li><a href="#/${n.route}" data-route="${n.route}">${img(n.icon)}<span>${esc(n.label)}</span>${n.badge ?? ''}</a></li>`;
  const groupedNav = (() => {
    const byRoute = new Map(NAV.map(item => [item.route, item]));
    const assigned = new Set();
    const groups = NAV_GROUPS.map(([label, routes]) => {
      const entries = routes.map(route => byRoute.get(route)).filter(Boolean);
      entries.forEach(item => assigned.add(item.route));
      return entries.length ? `<li class="nav-section">${esc(label)}</li>${entries.map(navItem).join('')}` : '';
    }).join('');
    const remaining = NAV.filter(item => !assigned.has(item.route));
    return groups + (remaining.length ? `<li class="nav-section">More</li>${remaining.map(navItem).join('')}` : '');
  })();
  $('#app').innerHTML = `
    <div class="shell" id="shell">
      <nav class="sidebar" aria-label="Main">
        ${brand()}
        <ul class="nav">${groupedNav}</ul>
        <ul class="nav nav-bottom">${navItem(SETTINGS)}</ul>
      </nav>
      <div class="main">
        <header class="topbar">
          <button class="icon-btn menu-btn" id="menu" aria-label="Menu">${icon('menu')}</button>
          <div class="search">
            ${icon('search')}
            <input class="input" id="search" type="search" placeholder="Search orders, parts, employees, machines, or ask Forge…" autocomplete="off" aria-label="Search">
            <div class="search-results hidden" id="search-results"></div>
          </div>
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
  $('#user-card').onclick = () => { location.hash = '#/settings'; };
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
async function route() {
  if (!session.me) return;
  closeOverlays();
  $('#shell')?.classList.remove('nav-open');
  const [path, query] = location.hash.replace(/^#\/?/, '').split('?');
  const [name, ...rest] = path.split('/');
  const entry = [...NAV, SETTINGS].find(n => n.route === (name || 'home')) ?? NAV[0];
  $$('.nav a').forEach(a => a.classList.toggle('active', a.dataset.route === entry.route));
  // Each visit renders into a fresh element, so a slow page that finishes after you moved on writes nowhere.
  const page = document.createElement('div');
  page.style.display = 'contents';
  $('#page').replaceChildren(page);
  current?.dispose?.();
  page.innerHTML = '<div class="muted">Loading…</div>';
  try {
    current = await entry.page(page, { params: rest.map(decodeURIComponent), query: new URLSearchParams(query ?? ''), me: session.me, refreshBell }) ?? null;
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
