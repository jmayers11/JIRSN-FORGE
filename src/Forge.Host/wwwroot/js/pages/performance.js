// Training & Performance (Supervisor menu). Every employee is listed alphabetically on the right; choosing one fills the centre with
// their review, training, attendance, labor and development. With nobody chosen, the centre is the plant view: the Command Center
// plus the training queue technical trainers work from and the new-employee training checklist.
// Figures are evidence for a review conversation; Forge makes no ranking, pay, promotion or disciplinary decision from them.
import { tryGet, command } from '../api.js';
import { esc, fmtNumber, fmtDate, fmtDateTime, errorBlock, toast } from '../ui.js';
import { appendWorkforceIntelligence } from './workforce-intelligence.js';
import { openCausePerformance } from '../cause-performance.js';
import { gradeCard, reviewPeriods, printDocument, tile, evidenceHtml, contributionsHtml, GRADE_LABEL, GRADE_ORDER, PILL, SHORT } from '../performance-cards.js';

const DAY = 86_400_000;
const SOURCE_LABEL = { Onboarding: 'New employee', Expiry: 'Expiring', Supervisor: 'Requested', Development: 'Development' };
const attr = value => esc(value).replaceAll('"', '&quot;');
const initials = name => String(name ?? '?').split(/\s+/).filter(Boolean).slice(0, 2).map(part => part[0]).join('').toUpperCase() || '?';
const localInput = date => {
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
};
const today = () => new Date().toISOString().slice(0, 10);

export async function performancePage(page, ctx) {
  const me = ctx.me ?? {};
  const can = new Set(me.permissions ?? []);
  const canRead = can.has('workforce.training-requests.read');
  const canRequest = can.has('workforce.training-requests.request');
  const canManage = can.has('workforce.training-requests.manage');
  const canOnboard = can.has('workforce.onboarding.manage');
  const periods = reviewPeriods(new Date());
  const state = {
    periodKey: periods[0].key, cards: new Map(), cardsError: null, loadingCards: false,
    workers: [], requests: [], requirements: [], types: [], centers: [], nominations: [],
    selected: ctx.query?.get?.('personId') ?? null, personTab: 'review', search: '', filter: 'all',
    highlight: ctx.query?.get?.('training') ?? null,
  };
  const period = () => periods.find(item => item.key === state.periodKey) ?? periods[0];
  const cardData = () => state.cards.get(state.periodKey) ?? null;
  const cardOf = personId => cardData()?.cards?.find(card => card.personId === personId) ?? null;
  const gradingOf = card => card ? gradeCard(card, cardData()?.expiringWithinDays ?? 30) : null;
  const nameOf = personId => state.workers.find(worker => worker.personId === personId)?.displayName ?? 'Employee';

  page.innerHTML = `<div class="page-head"><div><h1>Training &amp; Performance</h1><div class="sub">Choose an employee on the right for their review, training and attendance. Reviews are for people to discuss; Forge makes no personnel decision.</div></div>
      <div class="spacer"></div><div class="head-controls tp-head-controls">
        <label class="field"><span>Review period</span><select class="input" data-tp-period>${periods.map(item => `<option value="${attr(item.key)}" ${item.key === state.periodKey ? 'selected' : ''}>${esc(item.label)}</option>`).join('')}</select></label>
        <button class="btn" type="button" data-tp-print="all">Print full summary</button></div></div>
    <div class="tp-layout"><section class="tp-center"><div data-tp-employee hidden></div><div data-tp-plant></div></section><aside class="tp-roster panel" data-tp-roster aria-label="Employees"></aside></div>`;
  const center = { employee: page.querySelector('[data-tp-employee]'), plant: page.querySelector('[data-tp-plant]') };
  const roster = page.querySelector('[data-tp-roster]');

  // ------------------------------------------------------------------ data
  const loadTraining = async () => {
    if (!canRead) return;
    const [requests, requirements] = await Promise.all([tryGet('/api/workforce/training-requests'), tryGet('/api/workforce/onboarding-requirements')]);
    state.requests = Array.isArray(requests.data) ? requests.data : [];
    state.requestsError = requests.error ?? null;
    state.requirements = Array.isArray(requirements.data) ? requirements.data : [];
  };
  const loadCards = async () => {
    if (state.cards.has(state.periodKey)) return;
    const selected = period();
    state.loadingCards = true;
    const result = await tryGet(`/api/workforce/performance-cards?${new URLSearchParams({ from: selected.from.toISOString(), to: selected.to.toISOString() })}`);
    state.loadingCards = false;
    if (result.error) { state.cardsError = result.error; return; }
    state.cardsError = null;
    state.cards.set(selected.key, result.data && !Array.isArray(result.data) ? result.data : { cards: [] });
  };
  const [workers, types, resources, nominations] = await Promise.all([
    tryGet('/api/workforce/workers'), tryGet('/api/competency/qualification-types'), tryGet('/api/resources/'),
    can.has('workforce.development.read') ? tryGet('/api/workforce/development-nominations') : Promise.resolve({ data: [] }),
    loadTraining(),
  ]);
  state.workers = (Array.isArray(workers.data) ? workers.data : []).slice().sort((a, b) => a.displayName.localeCompare(b.displayName));
  state.workersError = workers.error ?? null;
  state.types = Array.isArray(types.data) ? types.data : [];
  state.centers = resources.data?.workCenters ?? [];
  state.nominations = Array.isArray(nominations.data) ? nominations.data : [];

  // ------------------------------------------------------------------ shared pieces
  const openRequests = personId => state.requests.filter(r => r.personId === personId && (r.status === 'Requested' || r.status === 'Scheduled'));
  const overdue = r => r.dueOn && r.status === 'Requested' && r.dueOn < today();
  const requestHtml = (r, { showPerson = true } = {}) => {
    const trainer = r.trainerPersonId ? nameOf(r.trainerPersonId) : null;
    const when = r.scheduledStart ? `${fmtDateTime(r.scheduledStart)} – ${fmtDateTime(r.scheduledEnd)}${trainer ? ` · trainer ${esc(trainer)}` : ''}${r.location ? ` · ${esc(r.location)}` : ''}` : '';
    const open = r.status === 'Requested' || r.status === 'Scheduled';
    const start = new Date(Date.now() + DAY); start.setHours(8, 0, 0, 0);
    const trainers = [`<option value="">Me</option>`, ...state.workers.filter(w => w.personId !== r.personId && w.isActive).map(w => `<option value="${attr(w.personId)}" ${w.personId === r.trainerPersonId ? 'selected' : ''}>${esc(w.displayName)}</option>`)].join('');
    return `<article class="tp-request ${esc(r.status.toLowerCase())}${overdue(r) ? ' overdue' : ''}${state.highlight === r.requestId ? ' highlight' : ''}" data-tp-request="${attr(r.requestId)}">
      <div class="tp-request-head">${showPerson ? `<button class="linkish" type="button" data-tp-person="${attr(r.personId)}"><b>${esc(nameOf(r.personId))}</b></button> · ` : ''}<b>${esc(r.title)}</b>
        <span class="pill none">${esc(SOURCE_LABEL[r.source] ?? r.source)}</span><span class="pill ${r.status === 'Completed' ? 'ok' : r.status === 'Cancelled' ? 'none' : r.status === 'Scheduled' ? 'info' : overdue(r) ? 'bad' : 'warn'}">${esc(r.status)}${overdue(r) ? ' · overdue' : ''}</span></div>
      <div class="faint">${r.dueOn ? `Due ${esc(fmtDate(r.dueOn))} · ` : ''}requested ${esc(fmtDate(r.requestedAt))}${when ? ` · ${when}` : ''}</div>
      <div class="faint">${esc(r.reason)}${r.outcomeNote ? ` · Outcome: ${esc(r.outcomeNote)}` : ''}</div>
      ${canManage && open ? `<div class="tp-request-actions">
        <details${r.status === 'Requested' && state.highlight === r.requestId ? ' open' : ''}><summary>${r.status === 'Scheduled' ? 'Reschedule' : 'Schedule'}</summary><form class="tp-inline-form" data-tp-schedule="${attr(r.requestId)}">
          <label>Starts<input class="input" type="datetime-local" name="start" required value="${localInput(r.scheduledStart ? new Date(r.scheduledStart) : start)}"></label>
          <label>Hours<input class="input" type="number" name="hours" min="0.25" max="72" step="0.25" required value="${r.scheduledStart ? fmtNumber((new Date(r.scheduledEnd) - new Date(r.scheduledStart)) / 36e5, 2) : 2}"></label>
          <label>Trainer<select class="input" name="trainer">${trainers}</select></label>
          <label>Location<input class="input" name="location" maxlength="120" value="${attr(r.location ?? '')}" placeholder="Training room, machine…"></label>
          <button class="btn small primary" type="submit">${r.status === 'Scheduled' ? 'Reschedule' : 'Schedule'}</button></form></details>
        ${r.status === 'Scheduled' ? `<form class="tp-inline-form" data-tp-complete="${attr(r.requestId)}"><input class="input" name="note" required maxlength="800" placeholder="What was delivered"><button class="btn small" type="submit">Mark delivered</button></form>` : ''}
        <details><summary>Cancel</summary><form class="tp-inline-form" data-tp-cancel="${attr(r.requestId)}"><input class="input" name="note" required maxlength="800" placeholder="Why it is no longer needed"><button class="btn small" type="submit">Cancel request</button></form></details>
      </div>` : ''}
    </article>`;
  };
  const avatar = (name, grade, big = false) => `<span class="tp-avatar ${grade ?? 'na'}${big ? ' big' : ''}" aria-hidden="true">${esc(initials(name))}</span>`;

  // ------------------------------------------------------------------ roster (right)
  const renderRoster = () => {
    const rows = state.workers.filter(worker => {
      if (state.search && !worker.displayName.toLowerCase().includes(state.search.toLowerCase())) return false;
      const grading = gradingOf(cardOf(worker.personId));
      if (state.filter === 'concern' || state.filter === 'watch') return grading?.overall === state.filter;
      if (state.filter === 'training') return openRequests(worker.personId).length > 0;
      if (state.filter === 'onboarding') return openRequests(worker.personId).some(r => r.source === 'Onboarding');
      return true;
    });
    let letter = '';
    const list = rows.map(worker => {
      const card = cardOf(worker.personId);
      const grading = gradingOf(card);
      const first = worker.displayName[0]?.toUpperCase() ?? '#';
      const heading = first !== letter ? `<h4 class="tp-letter">${esc(letter = first)}</h4>` : '';
      const open = openRequests(worker.personId).length;
      const dots = grading ? `<span class="tp-dots" aria-label="${attr(grading.metrics.map(m => `${m.label} ${m.display}`).join(', '))}">${grading.metrics.map(m => `<span class="perf-dot ${m.grade}" title="${attr(`${SHORT[m.key]}: ${m.display}`)}"></span>`).join('')}</span>` : '';
      return `${heading}<button class="tp-person${state.selected === worker.personId ? ' on' : ''}${worker.isActive ? '' : ' inactive'}" type="button" data-tp-person="${attr(worker.personId)}" aria-pressed="${state.selected === worker.personId}">
        ${avatar(worker.displayName, grading?.overall)}<span class="tp-person-text"><b>${esc(worker.displayName)}</b><small>${esc([worker.jobTitle, worker.homeWorkCenterCode, worker.shiftCode].filter(Boolean).join(' · '))}</small></span>
        <span class="tp-person-side">${dots}${open ? `<span class="badge orange" title="Open training requests">${open}</span>` : ''}</span></button>`;
    }).join('');
    const counts = { concern: 0, watch: 0 };
    for (const worker of state.workers) { const g = gradingOf(cardOf(worker.personId))?.overall; if (g in counts) counts[g]++; }
    const chip = (key, label, count) => `<button class="tp-filter${state.filter === key ? ' on' : ''}" type="button" data-tp-filter="${key}">${esc(label)}${count == null ? '' : ` <b>${count}</b>`}</button>`;
    roster.innerHTML = `<header class="tp-roster-head"><h2>Employees <span class="badge gray">${state.workers.length}</span></h2>
        <input class="input" type="search" data-tp-search placeholder="Find an employee" value="${attr(state.search)}" aria-label="Find an employee">
        <div class="tp-filters">${chip('all', 'All')}${chip('concern', 'Needs review', counts.concern)}${chip('watch', 'Watch', counts.watch)}${canRead ? chip('training', 'Training open', state.workers.filter(w => openRequests(w.personId).length).length) + chip('onboarding', 'New hires', state.workers.filter(w => openRequests(w.personId).some(r => r.source === 'Onboarding')).length) : ''}</div>
        ${state.loadingCards ? '<p class="faint">Gathering review figures…</p>' : state.cardsError ? `<p class="faint">Review figures unavailable: ${esc(state.cardsError.message ?? state.cardsError)}</p>` : ''}</header>
      <div class="tp-roster-list">${state.workersError ? errorBlock(state.workersError, 'employees') : list || '<p class="faint">No employees match.</p>'}</div>`;
  };

  // ------------------------------------------------------------------ employee (centre)
  const checklistHtml = (worker, card) => {
    const applicable = state.requirements.filter(r => !r.workCenterCode || r.workCenterCode === worker.homeWorkCenterCode);
    if (!applicable.length) return `<p class="faint">No new-employee training is defined${canOnboard ? ' yet. Add requirements under New employee training.' : '.'}</p>`;
    const quals = card?.competency?.qualifications ?? null;
    const mine = state.requests.filter(r => r.personId === worker.personId);
    const rows = applicable.map(requirement => {
      const held = quals?.some(q => q.code === requirement.qualificationCode && q.status === 'Qualified');
      const request = mine.filter(r => r.qualificationCode === requirement.qualificationCode).sort((a, b) => (b.requestedAt ?? '').localeCompare(a.requestedAt ?? ''))[0];
      const status = held ? ['ok', 'Certified'] : request?.status === 'Scheduled' ? ['info', `Scheduled ${fmtDateTime(request.scheduledStart)}`]
        : request?.status === 'Requested' ? [overdue(request) ? 'bad' : 'warn', `Needs scheduling${request.dueOn ? ` · due ${fmtDate(request.dueOn)}` : ''}`]
          : request?.status === 'Completed' ? ['warn', 'Delivered · awaiting certification'] : ['none', 'Not started'];
      return { requirement, held, status };
    });
    const done = rows.filter(row => row.held).length;
    const notStarted = rows.some(row => row.status[1] === 'Not started');
    return `<div class="tp-progress" role="progressbar" aria-valuemin="0" aria-valuemax="${rows.length}" aria-valuenow="${done}"><span style="width:${Math.round(done / rows.length * 100)}%"></span></div>
      <p class="faint">${done} of ${rows.length} certified${quals ? '' : ' (qualification records are not visible to this account)'}.</p>
      <ul class="tp-checklist">${rows.map(({ requirement, status }) => `<li><span class="pill ${status[0]}">${esc(status[1])}</span> <b>${esc(state.types.find(t => t.code === requirement.qualificationCode)?.name ?? requirement.qualificationCode)}</b> <span class="faint">${esc(requirement.qualificationCode)} · ${requirement.workCenterCode ? esc(requirement.workCenterCode) : 'every new employee'} · within ${requirement.dueDays} days</span></li>`).join('')}</ul>
      ${canRequest && notStarted ? `<button class="btn small" type="button" data-tp-onboard="${attr(worker.personId)}">Start new-employee training</button>` : ''}`;
  };
  const requestFormHtml = worker => canRequest ? `<form class="tp-inline-form tp-request-form" data-tp-request-form="${attr(worker.personId)}">
      <label>Qualification<select class="input" name="code"><option value="">Other (describe)</option>${state.types.map(t => `<option value="${attr(t.code)}">${esc(t.name)} (${esc(t.code)})</option>`).join('')}</select></label>
      <label>Or describe<input class="input" name="title" maxlength="200" placeholder="e.g. Forklift refresher"></label>
      <label>Needed by<input class="input" type="date" name="due" min="${today()}"></label>
      <label class="grow">Why<input class="input" name="reason" required maxlength="800" placeholder="Cross-training for night coverage"></label>
      <button class="btn small primary" type="submit">Request training</button></form>` : '';
  const qualificationTable = card => {
    const q = card?.competency;
    if (!q) return '<p class="faint">Qualification records are not visible to this account.</p>';
    return q.qualifications?.length ? `<div class="table-wrap"><table class="table"><thead><tr><th>Qualification</th><th>Status</th><th>Certified</th><th>Expires</th></tr></thead><tbody>${q.qualifications.map(row => `<tr><td>${esc(row.name)}<div class="faint">${esc(row.code)}</div></td><td><span class="pill ${row.status === 'Qualified' ? 'ok' : row.status === 'Expired' ? 'bad' : row.status === 'InTraining' ? 'warn' : 'none'}">${esc(row.status === 'InTraining' ? 'In training' : row.status)}</span></td><td>${esc(fmtDate(row.certifiedAt))}</td><td>${row.expiresAt ? esc(fmtDate(row.expiresAt)) : 'No expiry'}</td></tr>`).join('')}</tbody></table></div>`
      : '<p class="faint">No qualifications recorded.</p>';
  };
  const metricBlock = (grading, key) => {
    const metric = grading?.metrics.find(m => m.key === key);
    return metric ? `<div class="tp-metric">${tile(metric)}${metric.context ? `<p class="faint">${esc(metric.context)}</p>` : ''}</div>` : '';
  };
  const renderEmployee = () => {
    const worker = state.workers.find(item => item.personId === state.selected);
    if (!worker) { center.employee.hidden = true; center.plant.hidden = false; return; }
    center.employee.hidden = false; center.plant.hidden = true;
    const card = cardOf(worker.personId);
    const grading = gradingOf(card);
    const mine = state.requests.filter(r => r.personId === worker.personId);
    const open = mine.filter(r => r.status === 'Requested' || r.status === 'Scheduled');
    const closed = mine.filter(r => !(r.status === 'Requested' || r.status === 'Scheduled'));
    const packets = state.nominations.filter(n => n.personId === worker.personId);
    const tabs = [
      ['review', 'Review', null], ['training', 'Training', open.length || null], ['attendance', 'Attendance', null],
      ['labor', 'Labor & quality', null], ['development', 'Development', packets.length || null],
    ];
    const panels = {
      review: !card ? `<p class="faint">${state.loadingCards ? 'Gathering review figures…' : 'No review figures for this period.'}</p>`
        : `<div class="perf-tiles tp-tiles">${grading.metrics.map(tile).join('')}</div>
           <div class="perf-contributions"><h3>Documented contributions</h3>${contributionsHtml(card)}</div>
           <details class="perf-evidence" open><summary>Why these colors · evidence and context</summary>${evidenceHtml(card, grading)}</details>`,
      training: `${canRead ? `<section><h3>New employee training</h3>${checklistHtml(worker, card)}</section>
           <section><h3>Open training requests <span class="badge ${open.length ? 'orange' : 'gray'}">${open.length}</span></h3>${open.map(r => requestHtml(r, { showPerson: false })).join('') || '<p class="faint">Nothing waiting.</p>'}${requestFormHtml(worker)}</section>` : '<p class="faint">Training requests are not visible to this account.</p>'}
           <section><h3>Qualifications</h3>${qualificationTable(card)}</section>
           ${closed.length ? `<details><summary>Training history (${closed.length})</summary>${closed.map(r => requestHtml(r, { showPerson: false })).join('')}</details>` : ''}`,
      attendance: card?.attendance ? `${metricBlock(grading, 'attendance')}
           <div class="tp-stats">${[['Scheduled', card.attendance.scheduled], ['Eligible arrivals', card.attendance.eligible], ['On time', card.attendance.onTime], ['Verified late', card.attendance.verifiedLate], ['Call-outs', card.attendance.verifiedCallOuts], ['No-shows', card.attendance.verifiedNoShows], ['Excluded (approved leave)', card.attendance.excluded], ['Pending review', card.attendance.pendingReview]].map(([label, value]) => `<div><b>${fmtNumber(value, 0)}</b><span>${esc(label)}</span></div>`).join('')}</div>
           <p class="faint">${esc(card.attendance.explanation ?? '')} Only verified events count; a missing punch never becomes a no-show.</p>`
        : `<p class="faint">${card ? 'Attendance rates are visible to supervisors and Human Resources.' : 'No attendance figures for this period.'}</p>`,
      labor: `${metricBlock(grading, 'efficiency')}${metricBlock(grading, 'firstPass')}${metricBlock(grading, 'coverage')}
           ${card ? `<p class="faint">${fmtNumber(card.paidHours)} paid hours across ${fmtNumber(card.shiftsRecorded, 0)} shift(s); ${fmtNumber(card.unaccountedHours)} h not yet accounted for by labor, verified downtime or approved coded time.</p>` : ''}
           <div class="toolbar"><button class="btn small" type="button" data-tp-cause="${attr(worker.personId)}">Open cause-adjusted time and quality</button><a class="btn small" href="#/workforce?personId=${encodeURIComponent(worker.personId)}">Workforce profile and operation history</a></div>`,
      development: packets.length ? packets.map(n => `<article class="tp-request"><div class="tp-request-head"><b>${esc(n.pathway)}</b><span class="pill none">${esc(String(n.kind).replaceAll(/([a-z])([A-Z])/g, '$1 $2'))}</span><span class="pill info">${esc(String(n.status).replaceAll(/([a-z])([A-Z])/g, '$1 $2'))}</span></div><div class="faint">Updated ${esc(fmtDate(n.updatedAt))}</div><p>${esc(n.rationale)}</p></article>`).join('')
        : `<p class="faint">${can.has('workforce.development.read') ? 'No development packets for this employee.' : 'Development packets are not visible to this account.'}</p>`,
    };
    const active = panels[state.personTab] ? state.personTab : 'review';
    center.employee.innerHTML = `<section class="panel tp-employee">
      <header class="tp-emp-head"><button class="btn small" type="button" data-tp-back>← Plant overview</button>${avatar(worker.displayName, grading?.overall, true)}
        <div class="tp-emp-name"><h2>${esc(worker.displayName)}</h2><div class="faint">${esc([worker.jobTitle, worker.department, worker.homeWorkCenterCode, worker.shiftCode].filter(Boolean).join(' · '))}</div></div>
        <span class="spacer"></span>${grading ? `<span class="pill ${PILL[grading.overall]}">${esc(GRADE_LABEL[grading.overall])}</span>` : ''}
        ${card ? `<button class="btn small" type="button" data-tp-print="${attr(worker.personId)}">Print review</button>` : ''}</header>
      <p class="tp-emp-why ${grading?.overall ?? 'na'}">${esc(period().kind)} review · ${esc(period().label)}${grading ? ` — ${esc(grading.summary)}` : ''}</p>
      <div class="workspace-tabs"><div class="workspace-tablist" role="tablist" aria-label="Employee sections">${tabs.map(([key, label, badge]) => `<button class="workspace-tab" type="button" role="tab" data-tp-tab="${key}" aria-selected="${key === active}" tabindex="${key === active ? 0 : -1}">${esc(label)}${badge ? ` <span class="badge ${key === 'training' ? 'orange' : 'gray'}">${badge}</span>` : ''}</button>`).join('')}</div>
        ${tabs.map(([key]) => `<div class="workspace-panel tp-panel" role="tabpanel" data-tp-panel="${key}" ${key === active ? '' : 'hidden'}>${panels[key]}</div>`).join('')}</div>
    </section>`;
  };

  // ------------------------------------------------------------------ plant tabs (training queue, new employee training)
  const queueHtml = () => {
    if (!canRead) return '<p class="faint">Training requests are visible to supervisors, trainers, training coordinators and Human Resources.</p>';
    const recent = Date.now() - 30 * DAY;
    const columns = [
      ['Needs scheduling', state.requests.filter(r => r.status === 'Requested').sort((a, b) => (a.dueOn ?? '9999').localeCompare(b.dueOn ?? '9999'))],
      ['Scheduled', state.requests.filter(r => r.status === 'Scheduled').sort((a, b) => (a.scheduledStart ?? '').localeCompare(b.scheduledStart ?? ''))],
      ['Delivered in the last 30 days', state.requests.filter(r => r.status === 'Completed' && new Date(r.updatedAt).getTime() >= recent).reverse()],
    ];
    return `<div class="tp-queue-head"><p class="faint">New hires, expiring qualifications, supervisors and approved development packets open requests here. Technical trainers are notified, schedule the training and mark it delivered; certification is recorded separately in Competency.</p>
        ${canRequest ? '<button class="btn small" type="button" data-tp-scan>Check expiring qualifications</button>' : ''}</div>
      ${state.requestsError ? errorBlock(state.requestsError, 'training requests') : ''}
      <div class="tp-queue">${columns.map(([label, items]) => `<section class="tp-queue-col"><h3>${esc(label)} <span class="badge gray">${items.length}</span></h3>${items.map(r => requestHtml(r)).join('') || '<p class="faint">None.</p>'}</section>`).join('')}</div>`;
  };
  const onboardingHtml = () => {
    if (!canRead) return '<p class="faint">New-employee training is visible to supervisors, trainers, training coordinators and Human Resources.</p>';
    const groups = [[null, 'Every new employee'], ...[...new Set(state.requirements.map(r => r.workCenterCode).filter(Boolean))].sort().map(code => [code, `New employees in ${state.centers.find(c => c.code === code)?.name ?? code}`])];
    const newHires = state.workers.filter(w => openRequests(w.personId).some(r => r.source === 'Onboarding'));
    return `<p class="faint">When a worker is registered, Forge opens a training request for each item below (plant-wide plus their home work center) and notifies the technical trainers. Use “Start new-employee training” on an employee to do the same for someone already on the roster.</p>
      ${groups.map(([code, label]) => {
        const items = state.requirements.filter(r => (r.workCenterCode ?? null) === code);
        return `<section><h3>${esc(label)}</h3>${items.length ? `<ul class="tp-checklist">${items.map(r => `<li><b>${esc(state.types.find(t => t.code === r.qualificationCode)?.name ?? r.qualificationCode)}</b> <span class="faint">${esc(r.qualificationCode)} · within ${r.dueDays} days${r.note ? ` · ${esc(r.note)}` : ''}</span>${canOnboard ? ` <button class="btn small" type="button" data-tp-retire="${attr(r.requirementId)}">Retire</button>` : ''}</li>`).join('')}</ul>` : '<p class="faint">Nothing required yet.</p>'}</section>`;
      }).join('')}
      ${canOnboard ? `<form class="tp-inline-form" data-tp-requirement-form><label>Applies to<select class="input" name="center"><option value="">Every new employee</option>${state.centers.map(c => `<option value="${attr(c.code)}">${esc(c.name)} (${esc(c.code)})</option>`).join('')}</select></label>
        <label>Qualification<select class="input" name="code" required>${state.types.map(t => `<option value="${attr(t.code)}">${esc(t.name)} (${esc(t.code)})</option>`).join('')}</select></label>
        <label>Within days<input class="input" type="number" name="days" min="1" max="365" value="30" required></label>
        <label class="grow">Note<input class="input" name="note" maxlength="200" placeholder="Before working alone"></label>
        <label class="grow">Audit reason<input class="input" name="reason" required maxlength="240" value="New employee training requirement"></label>
        <button class="btn small primary" type="submit">Add requirement</button></form>` : ''}
      <section><h3>New hires in training <span class="badge gray">${newHires.length}</span></h3>${newHires.length ? `<ul class="tp-checklist">${newHires.map(w => { const items = openRequests(w.personId).filter(r => r.source === 'Onboarding'); return `<li><button class="linkish" type="button" data-tp-person="${attr(w.personId)}"><b>${esc(w.displayName)}</b></button> <span class="faint">${items.length} open · ${items.filter(r => r.status === 'Scheduled').length} scheduled${items.some(overdue) ? ' · overdue' : ''}</span></li>`; }).join('')}</ul>` : '<p class="faint">No new hires with open training.</p>'}</section>`;
  };
  const extraTabs = [
    { key: 'queue', label: 'Training queue', badge: state.requests.filter(r => r.status === 'Requested').length || null, mount: host => { host.innerHTML = queueHtml(); } },
    { key: 'onboarding', label: 'New employee training', badge: null, mount: host => { host.innerHTML = onboardingHtml(); } },
  ];
  const plant = await appendWorkforceIntelligence(center.plant, me, null, { employeesTab: false, extraTabs, tab: state.highlight ? 'queue' : null });

  const refreshAll = () => { renderRoster(); renderEmployee(); plant?.refreshExtra?.(); };
  const select = personId => {
    state.selected = personId;
    try { history.replaceState(null, '', personId ? `#/performance?personId=${encodeURIComponent(personId)}` : '#/performance'); } catch { /* not in a browser */ }
    renderRoster(); renderEmployee();
    page.querySelector('.tp-center')?.scrollIntoView?.({ block: 'start', behavior: 'smooth' });
  };
  const run = async (action, success) => {
    try {
      const result = await action();
      toast(result?.message ?? success);
      await loadTraining();
      refreshAll();
    } catch (error) {
      toast(error?.message ?? 'That change could not be made.', true);
    }
  };

  // ------------------------------------------------------------------ events
  page.addEventListener('click', async event => {
    const person = event.target.closest?.('[data-tp-person]');
    if (person) { select(person.dataset.tpPerson); return; }
    if (event.target.closest?.('[data-tp-back]')) { select(null); return; }
    const filter = event.target.closest?.('[data-tp-filter]');
    if (filter) { state.filter = filter.dataset.tpFilter; renderRoster(); return; }
    const tab = event.target.closest?.('[data-tp-tab]');
    if (tab) { state.personTab = tab.dataset.tpTab; renderEmployee(); return; }
    const print = event.target.closest?.('[data-tp-print]');
    if (print) {
      const data = cardData();
      if (!data) { toast('Review figures are still loading.', true); return; }
      const view = typeof window !== 'undefined' && typeof window.open === 'function' ? window.open('', '_blank') : null;
      if (!view) { toast('The browser blocked the print window. Allow pop-ups for Forge and try again.', true); return; }
      view.document.open(); view.document.write(printDocument(data, period(), print.dataset.tpPrint === 'all' ? null : print.dataset.tpPrint)); view.document.close();
      view.focus(); setTimeout(() => view.print(), 250);
      return;
    }
    const cause = event.target.closest?.('[data-tp-cause]');
    if (cause) { await openCausePerformance(cause.dataset.tpCause, nameOf(cause.dataset.tpCause), me); return; }
    const onboard = event.target.closest?.('[data-tp-onboard]');
    if (onboard) { await run(() => command(`/api/workforce/workers/${encodeURIComponent(onboard.dataset.tpOnboard)}/onboarding`, {}, 'Start new-employee training'), 'New-employee training requested.'); return; }
    if (event.target.closest?.('[data-tp-scan]')) { await run(() => command('/api/workforce/training-requests/expiry-scan', {}, 'Check for qualifications expiring within 30 days'), 'Checked expiring qualifications.'); return; }
    const retire = event.target.closest?.('[data-tp-retire]');
    if (retire) await run(() => command(`/api/workforce/onboarding-requirements/${encodeURIComponent(retire.dataset.tpRetire)}/retire`, {}, 'No longer required for new employees'), 'Requirement retired.');
  });
  page.addEventListener('input', event => {
    if (!event.target?.matches?.('[data-tp-search]')) return;
    state.search = event.target.value;
    renderRoster();
    const search = roster.querySelector('[data-tp-search]');
    search?.focus?.();
    try { search?.setSelectionRange?.(state.search.length, state.search.length); } catch { /* not a text input */ }
  });
  page.addEventListener('change', async event => {
    if (!event.target?.matches?.('[data-tp-period]')) return;
    state.periodKey = event.target.value;
    renderRoster(); renderEmployee();
    await loadCards();
    renderRoster(); renderEmployee();
  });
  page.addEventListener('submit', async event => {
    const form = event.target.closest?.('form');
    if (!form || !(form.matches('[data-tp-schedule]') || form.matches('[data-tp-complete]') || form.matches('[data-tp-cancel]') || form.matches('[data-tp-request-form]') || form.matches('[data-tp-requirement-form]'))) return;
    event.preventDefault();
    const value = name => form.querySelector(`[name="${name}"]`)?.value?.trim() ?? '';
    if (form.matches('[data-tp-schedule]')) {
      const start = new Date(value('start'));
      const hours = Number(value('hours'));
      if (!Number.isFinite(start.getTime()) || !(hours > 0)) { toast('Choose a start time and a length.', true); return; }
      await run(() => command(`/api/workforce/training-requests/${encodeURIComponent(form.dataset.tpSchedule)}/schedule`, {
        trainerPersonId: value('trainer') || null, startsAt: start.toISOString(), endsAt: new Date(start.getTime() + hours * 36e5).toISOString(), location: value('location') || null,
      }, 'Training scheduled'), 'Training scheduled.');
    } else if (form.matches('[data-tp-complete]')) {
      await run(() => command(`/api/workforce/training-requests/${encodeURIComponent(form.dataset.tpComplete)}/complete`, { note: value('note') }), 'Training marked delivered.');
    } else if (form.matches('[data-tp-cancel]')) {
      await run(() => command(`/api/workforce/training-requests/${encodeURIComponent(form.dataset.tpCancel)}/cancel`, { note: value('note') }), 'Training request cancelled.');
    } else if (form.matches('[data-tp-request-form]')) {
      await run(() => command('/api/workforce/training-requests', {
        personId: form.dataset.tpRequestForm, qualificationCode: value('code') || null, title: value('title') || null, dueOn: value('due') || null,
      }, value('reason')), 'Training requested; technical trainers were notified.');
    } else {
      await run(() => command('/api/workforce/onboarding-requirements', {
        workCenterCode: value('center') || null, qualificationCode: value('code'), dueDays: Number(value('days')), note: value('note') || null,
      }, value('reason')), 'Requirement added.');
    }
  });

  renderRoster();
  renderEmployee();
  await loadCards();
  renderRoster();
  renderEmployee();
  if (state.highlight) plant?.selectTab?.('queue');
}
