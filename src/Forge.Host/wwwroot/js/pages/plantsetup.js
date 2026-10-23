import { tryGet, command } from '../api.js';
import { img, PICTURES, pictureUrl, stagePhoto } from '../icons.js';
import { canAction } from '../forms.js';
import { esc, $, $$, toast, emptyBlock, errorBlock, confirmWithReason } from '../ui.js';

// Plant setup: build the plant the way it really is — sites, safety areas, work centers, machines and the
// kinds of work done there — for any industry. Codes are permanent business keys; names, pictures and
// placement can change. Nothing is deleted: retired items stay in history and can be reactivated.

const DAY = ['', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const PATH = { Site: 'sites', SafetyArea: 'safety-areas', WorkCenter: 'work-centers', Machine: 'machines', ActivityType: 'activity-types' };
const KIND = { Site: 'site', SafetyArea: 'safety area', WorkCenter: 'work center', Machine: 'machine', ActivityType: 'activity type' };
const enc = encodeURIComponent;

export async function plantSetupPage(page, ctx) {
  const canManage = canAction(ctx.me, 'resources.manage');
  let showRetired = false;
  let r, quals;
  let clockTimer = null;
  let disposed = false;

  async function load() {
    const [res, q] = await Promise.all([tryGet('/api/resources/?includeRetired=true'), tryGet('/api/competency/qualification-types')]);
    if (res.error) { page.innerHTML = `<div class="page-head"><h1>Plant Setup</h1></div>${errorBlock(res.error, 'plant setup')}`; return false; }
    r = res.data;
    quals = q.data ?? [];
    return true;
  }

  async function refresh() { if (await load()) render(); }

  const visible = list => list.filter(x => showRetired || x.active !== false);
  const shiftText = s => `${s.name} · ${s.start.slice(0, 5)} for ${+(s.durationMinutes / 60).toFixed(2)} h · ${s.days.map(d => DAY[d]).join(' ')}`;
  const siteClockText = (timeZone, now = new Date()) => new Intl.DateTimeFormat(undefined, {
    weekday: 'short', month: 'short', day: 'numeric', year: 'numeric',
    hour: 'numeric', minute: '2-digit', second: '2-digit', timeZone, timeZoneName: 'short',
  }).format(now);
  function refreshSiteClocks() {
    if (disposed) return;
    for (const site of r?.sites ?? []) {
      const clock = Array.from(page.querySelectorAll('[data-site-clock]')).find(node => node.dataset.siteClock === site.code);
      if (clock) clock.textContent = siteClockText(site.timeZone);
    }
    if (clockTimer !== null) clearTimeout(clockTimer);
    clockTimer = setTimeout(refreshSiteClocks, 1000);
  }
  const retiredPill = x => x.active === false ? ' <span class="pill none">Retired</span>' : '';
  const btn = (act, label, data = {}, cls = '') => canManage
    ? `<button class="btn small ${cls}" data-act="${act}" ${Object.entries(data).map(([k, v]) => `data-${k}="${esc(v)}"`).join(' ')}>${esc(label)}</button>` : '';
  const retireBtn = (kind, x) => x.active === false
    ? btn('reactivate', 'Reactivate', { kind, code: x.code })
    : btn('retire', 'Retire', { kind, code: x.code }, 'ghost');

  function render() {
    const sites = r.sites;
    page.innerHTML = `
      <div class="page-head">
        <div><h1>Plant Setup</h1><div class="sub">Your plant as it really is: sites, safety areas, work centers, machines and the kinds of work done at each. Slitting, bonding, winding, machining — name them the way your floor does.</div></div>
        <div class="spacer"></div>
        <label class="opt"><input type="checkbox" id="retired" ${showRetired ? 'checked' : ''}> Show retired</label>
        ${btn('add-site', '+ Site', {}, 'primary')}
      </div>
      ${canManage ? '' : '<div class="notice">Only a manufacturing engineer can change plant setup. You can look around.</div>'}
      <div class="notice">Codes (like <span class="mono">BOND-WC</span>) are permanent and appear on orders and routings; names, pictures and placement can be edited. Nothing is deleted — retire what you no longer use and it stays in history. Forge refuses to retire or move anything an open order, a routing or a person still depends on, and tells you what.</div>
      <div class="plant-grid">
        <div class="plant-tree">
          ${!sites.length ? emptyBlock('No site yet', canManage ? 'Start with + Site, then add safety areas, work centers and machines.' : 'A manufacturing engineer has not set up a site yet.') : sites.map(site).join('')}
        </div>
        <div class="plant-side">
          <section class="panel"><header><h2>Kinds of work</h2><span class="spacer"></span>${btn('add-activity', '+ Activity type')}</header>
            <div class="muted" style="margin-bottom:8px">What people do: run a laminator, wind rolls, stage material. Machines and work centers say which of these they support.</div>
            ${visible(r.activityTypes).map(a => `<div class="plant-row${a.active === false ? ' retired' : ''}">
              <div><b>${esc(a.name)}</b>${retiredPill(a)}<div class="faint mono">${esc(a.code)}</div>${a.description ? `<div class="faint">${esc(a.description)}</div>` : ''}</div>
              <div class="row-actions">${btn('edit-activity', 'Edit', { code: a.code })}${retireBtn('ActivityType', a)}</div></div>`).join('') || '<span class="muted">None yet.</span>'}
          </section>
          <section class="panel"><header><h2>Shifts</h2></header>
            ${sites.map(s => {
              const closures = (r.closures ?? []).filter(c => c.siteCode === s.code).sort((a, b) => a.date.localeCompare(b.date));
              return `<div class="plant-sub"><b>${esc(s.name)}</b> ${btn('add-shift', '+ Shift', { site: s.code })} ${btn('add-closure', '+ Closure', { site: s.code }, 'ghost')}</div>
                ${r.shifts.filter(x => x.siteCode === s.code).map(x => `<div class="plant-row"><div><b>${esc(x.code)}</b><div class="faint">${esc(shiftText(x))}</div></div></div>`).join('') || '<div class="muted">No shifts yet: work centers without a shift have no working time.</div>'}
                <div class="faint" style="margin:8px 0 4px">Site closures</div>
                ${closures.length ? closures.map(c => `<div class="plant-row"><div><b>${esc(c.date)}</b>${c.note ? `<div class="faint">${esc(c.note)}</div>` : '<div class="faint">No note recorded</div>'}</div><span class="pill warn">Closed</span></div>`).join('') : '<div class="muted">No closures recorded.</div>'}`;
            }).join('') || '<span class="muted">Add a site first.</span>'}
          </section>
        </div>
      </div>`;
    $('#retired', page).onchange = e => { showRetired = e.target.checked; render(); };
    refreshSiteClocks();
  }

  function site(s) {
    const areas = visible(r.safetyAreas.filter(a => a.siteCode === s.code));
    return `<section class="panel plant-site">
      <header><div><h2>${esc(s.name)} <span class="faint mono">${esc(s.code)}</span></h2><div class="faint">${esc(s.timeZone)}</div></div><span class="spacer"></span>
        ${btn('edit-site', 'Edit', { code: s.code })}${btn('add-area', '+ Safety area', { site: s.code })}</header>
      <div class="notice info" style="margin:8px 0 12px"><b>Site date and time</b><div data-site-clock="${esc(s.code)}" role="timer" aria-live="off">${esc(siteClockText(s.timeZone))}</div><div class="faint">Displayed in ${esc(s.timeZone)}. Forge uses this site zone for date rules.</div></div>
      ${areas.map(area).join('') || `<div class="muted">No safety areas yet. A safety area is a part of the floor where staffing and lone-work rules apply — a hall, a line, a room.</div>`}
    </section>`;
  }

  function area(a) {
    const wcs = visible(r.workCenters.filter(w => w.safetyAreaCode === a.code));
    return `<div class="plant-area${a.active === false ? ' retired' : ''}">
      <div class="plant-area-head">${img('alert')}<div><b>${esc(a.name)}</b>${retiredPill(a)} <span class="faint mono">${esc(a.code)}</span><div class="faint">Safety area</div></div><span class="spacer"></span>
        ${btn('edit-area', 'Edit', { code: a.code })}${retireBtn('SafetyArea', a)}${a.active !== false ? btn('add-wc', '+ Work center', { area: a.code }) : ''}</div>
      ${wcs.map(workCenter).join('') || '<div class="muted" style="padding:6px 0 4px 34px">No work centers in this area.</div>'}
    </div>`;
  }

  function workCenter(w) {
    const machines = visible(r.machines.filter(m => m.workCenterCode === w.code));
    const shifts = r.workCenterShifts.filter(x => x.workCenterCode === w.code).map(x => r.shifts.find(s => s.code === x.shiftCode)).filter(Boolean);
    const acts = r.workCenterActivities.filter(x => x.workCenterCode === w.code);
    const actName = code => r.activityTypes.find(a => a.code === code)?.name ?? code;
    const live = w.active !== false;
    const chip = (label, act, data, title = '') => `<span class="chip" title="${esc(title)}">${esc(label)}${live && canManage ? `<button class="chip-x" data-act="${act}" ${Object.entries(data).map(([k, v]) => `data-${k}="${esc(v)}"`).join(' ')} aria-label="Remove">×</button>` : ''}</span>`;
    return `<div class="wc-card${live ? '' : ' retired'}">
      <img class="wc-photo" src="${esc(stagePhoto(w.code, w.name, w.picture))}" alt="">
      <div class="wc-body">
        <div class="wc-head"><div><b>${esc(w.name)}</b>${retiredPill(w)} <span class="faint mono">${esc(w.code)}</span></div><span class="spacer"></span>
          ${btn('edit-wc', 'Edit', { code: w.code })}${retireBtn('WorkCenter', w)}</div>
        <div class="wc-line"><span class="faint">Shifts</span> ${shifts.map(s => chip(s.name, 'remove-wcshift', { wc: w.code, shift: s.code }, shiftText(s))).join('') || '<span class="c-warn">None — no working time</span>'} ${live ? btn('add-wcshift', '+', { wc: w.code }, 'tiny') : ''}</div>
        <div class="wc-line"><span class="faint">Work without a machine</span> ${acts.map(a => chip(actName(a.activityCode), 'remove-wcact', { wc: w.code, activity: a.activityCode })).join('') || '<span class="faint">none</span>'} ${live ? btn('add-wcact', '+', { wc: w.code }, 'tiny') : ''}</div>
        <div class="wc-machines">
          ${machines.map(m => machine(m, live)).join('') || '<div class="faint">No machines. Work here is assigned to the work center itself.</div>'}
          ${live ? btn('add-machine', '+ Machine', { wc: w.code }) : ''}
        </div>
      </div>
    </div>`;
  }

  function machine(m, wcLive) {
    const profile = r.machineActivities.filter(x => x.machineCode === m.code);
    const actName = code => r.activityTypes.find(a => a.code === code)?.name ?? code;
    const live = m.active !== false && wcLive;
    return `<div class="plant-row machine${m.active === false ? ' retired' : ''}">
      <div>${img('production', 'sm')} <b>${esc(m.name)}</b>${retiredPill(m)} <span class="faint mono">${esc(m.code)}</span>
        <div class="machine-acts">${profile.map(p => `<span class="chip" title="Operator needs one of: ${esc(p.operatorQualifications.join(', '))}${p.requiresMachineAuthorization ? '; plus a current practical authorization for this exact machine and activity' : ''}">${esc(actName(p.activityCode))} · ${esc(p.operatorQualifications.join(' / '))}${p.requiresMachineAuthorization ? ' · machine authorization' : ''}${live && canManage ? `<button class="chip-x" data-act="edit-mact" data-machine="${esc(m.code)}" data-activity="${esc(p.activityCode)}" aria-label="Change">✎</button><button class="chip-x" data-act="remove-mact" data-machine="${esc(m.code)}" data-activity="${esc(p.activityCode)}" aria-label="Remove">×</button>` : ''}</span>`).join('') || '<span class="c-warn">No work set up — it cannot be scheduled</span>'}
        ${live ? btn('add-mact', '+ Work it does', { machine: m.code }, 'tiny') : ''}</div>
      </div>
      <div class="row-actions">${btn('edit-machine', 'Edit', { code: m.code })}${m.active === false && !wcLive ? '' : retireBtn('Machine', m)}</div>
    </div>`;
  }

  // ---------------------------------------------------------------- forms

  const opt = (value, label, selected) => `<option value="${esc(value)}" ${value === selected ? 'selected' : ''}>${esc(label)}</option>`;
  const field = (f) => {
    const id = `f-${f.name}`;
    const hint = f.hint ? `<div class="faint" style="font-size:12px">${esc(f.hint)}</div>` : '';
    let input;
    switch (f.type) {
      case 'select':
        input = `<select class="select" id="${id}">${f.options.map(([v, l]) => opt(v, l, f.value)).join('')}</select>`; break;
      case 'textarea':
        input = `<textarea class="input" id="${id}" rows="2">${esc(f.value ?? '')}</textarea>`; break;
      case 'checks':
        input = `<div class="checks" id="${id}">${f.options.map(([v, l]) => `<label class="opt"><input type="checkbox" value="${esc(v)}" ${(f.value ?? []).includes(v) ? 'checked' : ''}> ${esc(l)}</label>`).join('') || '<span class="muted">None defined yet.</span>'}</div>`; break;
      case 'days':
        input = `<div class="checks" id="${id}">${[1, 2, 3, 4, 5, 6, 7].map(d => `<label class="opt"><input type="checkbox" value="${d}" ${(f.value ?? []).includes(d) ? 'checked' : ''}> ${DAY[d]}</label>`).join('')}</div>`; break;
      case 'picture':
        input = `<div class="pictures" id="${id}">
          <label class="pic"><input type="radio" name="${id}" value="" ${!f.value ? 'checked' : ''}><span class="auto">Auto<br><small>from the name</small></span></label>
          ${PICTURES.map(([v, l]) => `<label class="pic" title="${esc(l)}"><input type="radio" name="${id}" value="${v}" ${f.value === v ? 'checked' : ''}><img src="${esc(pictureUrl(v))}" alt=""><small>${esc(l)}</small></label>`).join('')}</div>`; break;
      default:
        input = `<input class="input${f.code ? ' mono' : ''}" id="${id}" type="${f.type ?? 'text'}" value="${esc(f.value ?? '')}" ${f.readonly ? 'readonly' : ''} ${f.placeholder ? `placeholder="${esc(f.placeholder)}"` : ''} ${f.step ? `step="${f.step}"` : ''}>`;
    }
    return `<div class="field"><label for="${id}">${esc(f.label)}</label>${input}${hint}</div>`;
  };

  function readForm(m, fields) {
    const out = {};
    for (const f of fields) {
      const el = $(`#f-${f.name}`, m);
      if (f.type === 'checks' || f.type === 'days') out[f.name] = $$('input:checked', el).map(i => (f.type === 'days' ? Number(i.value) : i.value));
      else if (f.type === 'picture') out[f.name] = $(`input[name="f-${f.name}"]:checked`, m)?.value || null;
      else out[f.name] = el.value.trim();
      if (f.code) out[f.name] = out[f.name].toUpperCase();
      if (f.required && (out[f.name] === '' || (Array.isArray(out[f.name]) && !out[f.name].length))) throw new Error(`${f.label} is required.`);
    }
    return out;
  }

  /** Opens a form; `send(values, reason)` runs the command. Edits need a reason, creates do not. */
  async function formModal({ title, detail, fields, confirmLabel = 'Save', reasonRequired = true }, send) {
    const result = await confirmWithReason(
      { title, detail, confirmLabel, reasonRequired, extraHtml: `<div class="form-grid">${fields.map(field).join('')}</div>` },
      (reason, m) => send(readForm(m, fields), reason));
    if (result) { toast(result.message ?? 'Saved.'); await refresh(); }
  }

  const codeField = (label, placeholder) => ({ name: 'code', label: `${label} code (permanent)`, code: true, required: true, placeholder, hint: 'Letters, digits, - or _. It cannot be changed later.' });
  const areaOptions = siteCode => r.safetyAreas.filter(a => a.active !== false && (!siteCode || a.siteCode === siteCode)).map(a => [a.code, `${a.name} (${a.code})`]);
  const wcOptions = () => r.workCenters.filter(w => w.active !== false).map(w => [w.code, `${w.name} (${w.code})`]);
  const activityOptions = () => r.activityTypes.filter(a => a.active !== false).map(a => [a.code, `${a.name} (${a.code})`]);
  const qualOptions = () => quals.map(q => [q.code, `${q.name} (${q.code})`]);
  const siteOf = wcCode => r.safetyAreas.find(a => a.code === r.workCenters.find(w => w.code === wcCode)?.safetyAreaCode)?.siteCode;

  const actions = {
    'add-site': () => formModal({ title: 'Add a site', confirmLabel: 'Add site', reasonRequired: false, fields: [
      codeField('Site', 'WEST'), { name: 'name', label: 'Name', required: true, placeholder: 'West plant' },
      { name: 'timeZone', label: 'Time zone (IANA)', required: true, placeholder: 'America/Chicago', hint: 'Choose the plant’s zone explicitly; for example America/New_York or America/Chicago. This is never taken from your device.' }] },
    (v, reason) => command('/api/resources/sites', { code: v.code, name: v.name, timeZone: v.timeZone }, reason)),

    'edit-site': d => { const s = r.sites.find(x => x.code === d.code); return formModal({ title: `Edit site ${s.code}`, detail: 'Date rules and the site clock use this IANA time zone. The change is audited.', reasonRequired: true, fields: [
      { name: 'name', label: 'Name', value: s.name, required: true }, { name: 'timeZone', label: 'Time zone (IANA)', value: s.timeZone, required: true }] },
    (v, reason) => command(`/api/resources/sites/${enc(s.code)}/edit`, v, reason)); },

    'add-area': d => formModal({ title: 'Add a safety area', detail: 'A part of the floor where staffing and lone-work rules apply.', confirmLabel: 'Add area', reasonRequired: false, fields: [
      codeField('Safety area', 'BONDING'), { name: 'name', label: 'Name', required: true, placeholder: 'Bonding hall' }] },
    (v, reason) => command('/api/resources/safety-areas', { ...v, parent: d.site }, reason)),

    'edit-area': d => { const a = r.safetyAreas.find(x => x.code === d.code); return formModal({ title: `Edit safety area ${a.code}`, fields: [{ name: 'name', label: 'Name', value: a.name, required: true }] },
      (v, reason) => command(`/api/resources/safety-areas/${enc(a.code)}/edit`, v, reason)); },

    'add-wc': d => formModal({ title: 'Add a work center', detail: 'A place where one kind of work is done: a bonding line, a bank of winders, a kitting bench.', confirmLabel: 'Add work center', reasonRequired: false, fields: [
      codeField('Work center', 'BOND-WC'), { name: 'name', label: 'Name', required: true, placeholder: 'Bonding' },
      { name: 'picture', label: 'Picture', type: 'picture' }] },
    (v, reason) => command('/api/resources/work-centers', { code: v.code, name: v.name, parent: d.area, picture: v.picture }, reason)),

    'edit-wc': d => { const w = r.workCenters.find(x => x.code === d.code); return formModal({ title: `Edit work center ${w.code}`, detail: 'Moving it to another safety area is refused while orders, routings or people still depend on it.', fields: [
      { name: 'name', label: 'Name', value: w.name, required: true },
      { name: 'parent', label: 'Safety area', type: 'select', value: w.safetyAreaCode, options: areaOptions(siteOf(w.code)) },
      { name: 'picture', label: 'Picture', type: 'picture', value: w.picture }] },
    (v, reason) => command(`/api/resources/work-centers/${enc(w.code)}/edit`, v, reason)); },

    'add-machine': d => formModal({ title: 'Add a machine', confirmLabel: 'Add machine', reasonRequired: false, fields: [
      codeField('Machine', 'LAM-1'), { name: 'name', label: 'Name', required: true, placeholder: 'Laminator 1' }] },
    (v, reason) => command('/api/resources/machines', { ...v, parent: d.wc }, reason)),

    'edit-machine': d => { const m = r.machines.find(x => x.code === d.code); return formModal({ title: `Edit machine ${m.code}`, detail: 'Moving it to another work center is refused while orders, routings or people still depend on it.', fields: [
      { name: 'name', label: 'Name', value: m.name, required: true },
      { name: 'parent', label: 'Work center', type: 'select', value: m.workCenterCode, options: wcOptions() }] },
    (v, reason) => command(`/api/resources/machines/${enc(m.code)}/edit`, v, reason)); },

    'add-mact': d => machineActivity(d.machine),
    'edit-mact': d => machineActivity(d.machine, d.activity),

    'remove-mact': d => removal(`Stop ${d.activity} on ${d.machine}`, `/api/resources/machines/${enc(d.machine)}/activities/${enc(d.activity)}/remove`),
    'add-wcact': d => formModal({ title: `Work done at ${d.wc} without a machine`, detail: 'For hand work such as staging, kitting or cleaning.', confirmLabel: 'Add', reasonRequired: false, fields: [
      { name: 'activityCode', label: 'Kind of work', type: 'select', options: activityOptions() }] },
    (v, reason) => command(`/api/resources/work-centers/${enc(d.wc)}/activities`, v, reason)),
    'remove-wcact': d => removal(`Stop ${d.activity} at ${d.wc}`, `/api/resources/work-centers/${enc(d.wc)}/activities/${enc(d.activity)}/remove`),

    'add-wcshift': d => {
      const options = r.shifts.filter(s => s.siteCode === siteOf(d.wc)).map(s => [s.code, shiftText(s)]);
      if (!options.length) { toast('This site has no shifts yet. Add one under Shifts first.', true); return; }
      return formModal({ title: `Shift for ${d.wc}`, confirmLabel: 'Assign', reasonRequired: false, fields: [{ name: 'shiftCode', label: 'Shift', type: 'select', options }] },
        (v, reason) => command(`/api/resources/work-centers/${enc(d.wc)}/shifts`, v, reason));
    },
    'remove-wcshift': d => removal(`Take ${d.wc} off shift ${d.shift}`, `/api/resources/work-centers/${enc(d.wc)}/shifts/${enc(d.shift)}/remove`),

    'add-activity': () => formModal({ title: 'Add a kind of work', confirmLabel: 'Add', reasonRequired: false, fields: [
      codeField('Activity', 'RUN_LAMINATOR'), { name: 'name', label: 'Name', required: true, placeholder: 'Run laminator' },
      { name: 'description', label: 'Description', type: 'textarea' }] },
    (v, reason) => command('/api/resources/activity-types', v, reason)),

    'edit-activity': d => { const a = r.activityTypes.find(x => x.code === d.code); return formModal({ title: `Edit ${a.code}`, fields: [
      { name: 'name', label: 'Name', value: a.name, required: true }, { name: 'description', label: 'Description', type: 'textarea', value: a.description }] },
    (v, reason) => command(`/api/resources/activity-types/${enc(a.code)}/edit`, v, reason)); },

    'add-shift': d => formModal({ title: `Add a shift at ${d.site}`, confirmLabel: 'Add shift', reasonRequired: false, fields: [
      codeField('Shift', 'DAYS'), { name: 'name', label: 'Name', required: true, placeholder: 'Day shift' },
      { name: 'start', label: 'Starts (local time)', type: 'time', value: '06:00', required: true },
      { name: 'hours', label: 'Length (hours)', type: 'number', value: '8', step: '0.25', required: true },
      { name: 'days', label: 'Days', type: 'days', value: [1, 2, 3, 4, 5], required: true }] },
    (v, reason) => command('/api/resources/shifts', { code: v.code, siteCode: d.site, name: v.name, start: `${v.start}:00`.slice(0, 8), durationMinutes: Math.round(Number(v.hours) * 60), days: v.days }, reason)),

    'add-closure': d => formModal({ title: `Close ${d.site} for a day`, detail: 'A plant closure (holiday or shutdown) removes that day from capacity for every work center at the site.', confirmLabel: 'Add closure', reasonRequired: true, fields: [
      { name: 'date', label: 'Date', type: 'date', required: true }, { name: 'note', label: 'Why (for example, the holiday name)' }] },
    (v, reason) => command(`/api/resources/sites/${enc(d.site)}/closures`, { date: v.date, note: v.note }, reason)),

    retire: d => confirmWithReason({ title: `Retire ${KIND[d.kind]} ${d.code}`, detail: 'It stays in history but nothing new can be planned, assigned or run there. Forge refuses if anything still depends on it.', confirmLabel: 'Retire' },
      reason => command(`/api/resources/${PATH[d.kind]}/${enc(d.code)}/retire`, {}, reason)).then(done),
    reactivate: d => confirmWithReason({ title: `Reactivate ${KIND[d.kind]} ${d.code}`, confirmLabel: 'Reactivate' },
      reason => command(`/api/resources/${PATH[d.kind]}/${enc(d.code)}/reactivate`, {}, reason)).then(done),
  };

  async function done(result) { if (result) { toast(result.message ?? 'Saved.'); await refresh(); } }

  function removal(title, url) {
    return confirmWithReason({ title, detail: 'Refused while an open order, a routing or a work assignment still uses it.', confirmLabel: 'Remove' },
      reason => command(url, {}, reason)).then(done);
  }

  function machineActivity(machineCode, activityCode) {
    const current = activityCode ? r.machineActivities.find(x => x.machineCode === machineCode && x.activityCode === activityCode) : null;
    return formModal({
      title: activityCode ? `${activityCode} on ${machineCode}` : `Work ${machineCode} does`,
      detail: 'An operator needs any one of the ticked qualifications to run it. For specialized work, require a separately assessed authorization for this exact machine and activity.',
      confirmLabel: 'Save', reasonRequired: false,
      fields: [
        activityCode ? { name: 'activityCode', label: 'Kind of work', value: activityCode, readonly: true } : { name: 'activityCode', label: 'Kind of work', type: 'select', options: activityOptions() },
        { name: 'operatorQualifications', label: 'Operator qualifications', type: 'checks', options: qualOptions(), value: current?.operatorQualifications ?? [], required: true },
        { name: 'requiresMachineAuthorization', label: 'Require machine-specific authorization', type: 'check', text: 'Independent practical assessment and approval must be current for this exact machine and activity', value: current?.requiresMachineAuthorization ?? false },
      ],
    }, (v, reason) => command(`/api/resources/machines/${enc(machineCode)}/activities`, v, reason));
  }

  page.addEventListener('click', e => {
    const b = e.target.closest('[data-act]');
    if (!b || !page.contains(b)) return;
    e.preventDefault();
    actions[b.dataset.act]?.({ ...b.dataset });
  });

  if (await load()) render();
  return { dispose() { disposed = true; if (clockTimer !== null) clearTimeout(clockTimer); } };
}
