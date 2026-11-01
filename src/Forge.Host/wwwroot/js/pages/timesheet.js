import { tryGet, command } from '../api.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast, tabifyPanels } from '../ui.js';
import { formModal, canAction, today } from '../forms.js';
import { who } from '../people.js';

// Timekeeping (owner decision 2026-10-31: DCAA-style timesheets). Every hour is recorded daily against an order operation or a charge
// code; the employee signs the week and a supervisor who is not the employee approves it. Corrections never overwrite: they supersede
// with a reason, and correcting a signed or approved week sends it back for signature and approval.

const KIND = { Contract: 'Contract (direct)', Indirect: 'Indirect', Overhead: 'Overhead', GeneralAndAdministrative: 'G&A', Leave: 'Leave / holiday', Unallowable: 'Unallowable' };
const STATUS = { Open: 'none', Signed: 'warn', Approved: 'ok', Returned: 'bad' };
const pill = s => `<span class="pill ${STATUS[s] ?? 'none'}">${esc(s)}</span>`;
const hours = n => `${Number(n).toLocaleString('en-US', { maximumFractionDigits: 2 })} h`;

export const days = weekStart => Array.from({ length: 7 }, (_, i) => {
  const d = new Date(`${weekStart}T12:00:00Z`); d.setUTCDate(d.getUTCDate() + i);
  return d.toISOString().slice(0, 10);
});
export const shift = (weekStart, weeks) => { const d = new Date(`${weekStart}T12:00:00Z`); d.setUTCDate(d.getUTCDate() + weeks * 7); return d.toISOString().slice(0, 10); };
export const charge = e => e.orderNumber ? `Order ${e.orderNumber} op ${e.sequence}` : e.chargeCode;

/** A week as a grid: one row per charge, one column per day, active entries only; corrections and withdrawals listed beneath. */
export function weekGridHtml(sheet, weekStart, { correctable = false } = {}) {
  const active = (sheet?.entries ?? []).filter(e => e.state === 'Active');
  const week = days(weekStart);
  const rows = [...new Set(active.map(charge))];
  const total = d => active.filter(e => e.workDate === d).reduce((n, e) => n + Number(e.hours), 0);
  const history = (sheet?.entries ?? []).filter(e => e.state !== 'Active' || e.supersedesEntryId);
  return `<table class="table timesheet"><thead><tr><th>Charge</th>${week.map(d => `<th class="num">${esc(fmtDate(d))}</th>`).join('')}<th class="num">Total</th></tr></thead><tbody>
    ${rows.length ? rows.map(r => `<tr><td>${esc(r)}</td>${week.map(d => {
      const cell = active.filter(e => charge(e) === r && e.workDate === d);
      return `<td class="num">${cell.map(e => `${hours(e.hours)}${e.lateReason ? ' <span class="pill warn" title="' + esc(e.lateReason) + '">late</span>' : ''}${correctable ? ` <button class="btn small" data-correct="${e.entryId}">Correct</button>` : ''}`).join('<br>')}</td>`;
    }).join('')}<td class="num"><b>${hours(active.filter(e => charge(e) === r).reduce((n, e) => n + Number(e.hours), 0))}</b></td></tr>`).join('')
      : `<tr><td colspan="9" class="faint">No time recorded this week.</td></tr>`}
    <tr><td><b>Day total</b></td>${week.map(d => `<td class="num"><b>${hours(total(d))}</b></td>`).join('')}<td class="num"><b>${hours(active.reduce((n, e) => n + Number(e.hours), 0))}</b></td></tr></tbody></table>
    ${history.length ? `<details><summary>Corrections (${history.length})</summary><ul>${history.map(e => `<li>${esc(fmtDate(e.workDate))} ${esc(charge(e))} ${hours(e.hours)} — ${esc(e.state)}${e.correctionReason ? `: ${esc(e.correctionReason)}` : ''} <span class="faint">${esc(who(e.recordedBy))} ${esc(fmtDateTime(e.recordedAt))}</span></li>`).join('')}</ul></details>` : ''}`;
}

/** Order hours on the timesheet against the employee's shop-floor labor, day by day. */
export function reconciliationHtml(lines) {
  if (!lines?.length) return '<div class="faint">No order time this week.</div>';
  return `<table class="table"><thead><tr><th>Day</th><th>Order</th><th class="num">Timesheet</th><th class="num">Shop floor</th><th class="num">Difference</th></tr></thead><tbody>
    ${lines.map(l => `<tr><td>${esc(fmtDate(l.workDate))}</td><td>${l.orderNumber} op ${l.sequence}</td><td class="num">${hours(l.timesheetHours)}</td><td class="num">${hours(l.shopFloorHours)}</td>
      <td class="num">${Math.abs(l.difference) >= 0.25 ? `<span class="pill warn">${hours(l.difference)}</span>` : hours(l.difference)}</td></tr>`).join('')}</tbody></table>`;
}

export async function timesheetPage(page, ctx) {
  const me = ctx?.me;
  const can = { record: canAction(me, 'timekeeping.record'), approve: canAction(me, 'timekeeping.approve'), read: canAction(me, 'timekeeping.read'), codes: canAction(me, 'timekeeping.codes.manage') };
  let week = null;
  let reviewWeek = null;
  const load = async () => {
    const [mine, codes, review] = await Promise.all([
      can.record ? tryGet(`/api/timekeeping/my${week ? `?week=${week}` : ''}`) : Promise.resolve({ data: null }),
      tryGet('/api/timekeeping/charge-codes'),
      can.approve || can.read ? tryGet(`/api/timekeeping/timesheets${reviewWeek ? `?week=${reviewWeek}` : ''}`) : Promise.resolve({ data: null })]);
    const codeList = codes.data ?? [];
    week = mine.data?.weekStart ?? week;
    reviewWeek = review.data?.weekStart ?? reviewWeek;
    const sheet = mine.data?.timesheet?.timesheet;
    const open = !sheet || sheet.status === 'Open' || sheet.status === 'Returned';
    page.innerHTML = `
      <div class="page-head"><div><h1>Timekeeping</h1><div class="sub">Record every hour each day against an order operation or a charge code, sign the week, and your supervisor approves it. Corrections keep the original and need a reason.</div></div></div>
      ${can.record ? `<section class="panel"><header><h2>My week</h2></header>
        ${mine.error ? errorBlock(mine.error, 'your timesheet') : `<div class="inline"><button class="btn small" data-week="-1">◀ Previous</button> <b>Week of ${esc(fmtDate(week))}</b> ${sheet ? pill(sheet.status) : pill('Open')} <button class="btn small" data-week="1">Next ▶</button>
          <span class="spacer"></span>${open ? '<button class="btn" data-add-time>+ Time</button>' : ''} ${sheet && open && sheet.entries.some(e => e.state === 'Active') ? '<button class="btn primary" data-sign>Sign week</button>' : ''}</div>
          ${sheet?.status === 'Returned' ? `<div class="notice bad">Returned by ${esc(who(sheet.returnedBy))}: ${esc(sheet.returnReason)}</div>` : ''}
          ${weekGridHtml(sheet, week, { correctable: true })}
          ${mine.data?.timesheet ? `<h3>Against shop-floor labor</h3>${reconciliationHtml(mine.data.timesheet.reconciliation)}` : ''}`}</section>` : ''}
      ${can.approve || can.read ? `<section class="panel"><header><h2>Approve timesheets</h2></header>
        ${review.error ? errorBlock(review.error, 'timesheets') : `<div class="inline"><button class="btn small" data-review-week="-1">◀ Previous</button> <b>Week of ${esc(fmtDate(reviewWeek))}</b> <button class="btn small" data-review-week="1">Next ▶</button></div>
          ${(review.data?.timesheets ?? []).length ? review.data.timesheets.map(v => `<article class="card"><header><b>${esc(v.person)}</b> ${pill(v.timesheet.status)} <span class="spacer"></span><b>${hours(v.hours)}</b>
            ${can.approve && v.timesheet.status === 'Signed' ? ` <button class="btn small primary" data-approve="${v.timesheet.timesheetId}">Approve</button>` : ''}
            ${can.approve && (v.timesheet.status === 'Signed' || v.timesheet.status === 'Approved') ? ` <button class="btn small" data-return="${v.timesheet.timesheetId}">Return</button>` : ''}</header>
            ${v.timesheet.signedBy ? `<div class="faint">Signed ${esc(fmtDateTime(v.timesheet.signedAt))}${v.timesheet.approvedBy && v.timesheet.status === 'Approved' ? ` · approved by ${esc(who(v.timesheet.approvedBy))} ${esc(fmtDateTime(v.timesheet.approvedAt))}` : ''}</div>` : ''}
            ${weekGridHtml(v.timesheet, reviewWeek)}${reconciliationHtml(v.reconciliation)}</article>`).join('') : emptyBlock('No timesheets this week', 'Employees\' weeks appear here once they record time.')}`}</section>` : ''}
      <section class="panel"><header><h2>Charge codes</h2></header>
        ${can.codes ? '<div class="actions"><button class="btn" data-add-code>+ Charge code</button></div>' : ''}
        ${codeList.length ? `<table class="table"><thead><tr><th>Code</th><th>Name</th><th>Kind</th><th>Contract</th><th>Status</th><th></th></tr></thead><tbody>
          ${codeList.map(c => `<tr><td class="mono">${esc(c.code)}</td><td>${esc(c.name)}</td><td>${esc(KIND[c.kind] ?? c.kind)}</td><td>${esc([c.contractNumber, c.clin && `CLIN ${c.clin}`].filter(Boolean).join(' '))}</td>
            <td>${c.active ? '<span class="pill ok">Active</span>' : '<span class="pill none">Inactive</span>'}</td><td>${can.codes ? `<button class="btn small" data-code-active="${esc(c.code)}" data-active="${c.active}">${c.active ? 'Deactivate' : 'Activate'}</button>` : ''}</td></tr>`).join('')}</tbody></table>`
          : emptyBlock('No charge codes yet', 'A cost accountant sets up indirect, overhead, G&A, leave, unallowable and contract codes. Direct production time is charged to the order itself.')}</section>`;
    tabifyPanels(page, [{ label: 'My week', match: ['my week'] }, { label: 'Approve timesheets', match: ['approve timesheets'] }, { label: 'Charge codes', match: ['charge codes'] }]);

    const activeCodes = codeList.filter(c => c.active).map(c => [c.code, `${c.code} — ${c.name}`]);
    const chargeFields = (e = {}) => [
      { name: 'orderNumber', label: 'Order (direct production work)', type: 'number', min: 1, value: e.orderNumber },
      { name: 'sequence', label: 'Operation', type: 'number', min: 1, value: e.sequence },
      { name: 'chargeCode', label: 'Or charge code', type: 'select', blank: '—', options: activeCodes, value: e.chargeCode ?? '' },
    ];
    for (const b of $$('[data-week]', page)) b.addEventListener('click', () => { week = shift(week, Number(b.dataset.week)); load(); });
    for (const b of $$('[data-review-week]', page)) b.addEventListener('click', () => { reviewWeek = shift(reviewWeek, Number(b.dataset.reviewWeek)); load(); });
    $('[data-add-time]', page)?.addEventListener('click', () => formModal({
      title: 'Record time', detail: 'Record time the day you work it. Charge it to an order and operation, or to a charge code.', confirmLabel: 'Record',
      fields: [{ name: 'workDate', label: 'Day worked', type: 'date', required: true, value: today() }, { name: 'hours', label: 'Hours (quarter hours)', type: 'number', min: 0.25, max: 24, step: 0.25, required: true },
        ...chargeFields(), { name: 'note', label: 'Note' }, { name: 'lateReason', label: 'If recorded after the next day: why' }],
    }, (v) => command('/api/timekeeping/entries', { ...v, chargeCode: v.chargeCode || null }), load));
    for (const b of $$('[data-correct]', page)) b.addEventListener('click', () => {
      const e = sheet.entries.find(x => x.entryId === b.dataset.correct);
      formModal({
        title: `Correct ${fmtDate(e.workDate)} — ${charge(e)}`, detail: 'The original stays on record. Zero hours withdraws it. Correcting a signed or approved week sends it back for signature.', confirmLabel: 'Record correction', reasonRequired: true,
        fields: [{ name: 'hours', label: 'Hours', type: 'number', min: 0, max: 24, step: 0.25, required: true, value: e.hours }, ...chargeFields(e), { name: 'note', label: 'Note', value: e.note }],
      }, (v, reason) => command(`/api/timekeeping/entries/${e.entryId}/correct`, { ...v, chargeCode: v.chargeCode || null }, reason), load);
    });
    $('[data-sign]', page)?.addEventListener('click', () => confirmWithReason({ title: `Sign the week of ${fmtDate(week)}`, detail: 'I certify that these hours are complete and correct and charged to the work I performed.', confirmLabel: 'Sign', reasonRequired: false },
      reason => command('/api/timekeeping/sign', { weekStart: week }, reason)).then(r => { if (r) { toast(r.message ?? 'Signed'); load(); } }));
    for (const b of $$('[data-approve]', page)) b.addEventListener('click', () => confirmWithReason({ title: 'Approve timesheet', detail: 'You cannot approve your own week.', confirmLabel: 'Approve', reasonRequired: false },
      reason => command(`/api/timekeeping/timesheets/${b.dataset.approve}/approve`, {}, reason)).then(r => { if (r) { toast(r.message ?? 'Approved'); load(); } }));
    for (const b of $$('[data-return]', page)) b.addEventListener('click', () => confirmWithReason({ title: 'Return timesheet', detail: 'The employee sees your reason, corrects and signs again.', confirmLabel: 'Return' },
      reason => command(`/api/timekeeping/timesheets/${b.dataset.return}/return`, {}, reason)).then(r => { if (r) { toast(r.message ?? 'Returned'); load(); } }));
    $('[data-add-code]', page)?.addEventListener('click', () => formModal({
      title: 'Add a charge code', confirmLabel: 'Add', reasonRequired: true,
      fields: [{ name: 'code', label: 'Code', code: true, required: true }, { name: 'name', label: 'Name', required: true }, { name: 'kind', label: 'Kind', type: 'select', options: Object.entries(KIND), value: 'Indirect' },
        { name: 'contractNumber', label: 'Contract (contract codes only)' }, { name: 'clin', label: 'CLIN (contract codes only)' }],
    }, (v, reason) => command('/api/timekeeping/charge-codes', v, reason), load));
    for (const b of $$('[data-code-active]', page)) b.addEventListener('click', () => confirmWithReason({ title: `${b.dataset.active === 'true' ? 'Deactivate' : 'Activate'} ${b.dataset.codeActive}`, detail: 'An inactive code takes no new time; recorded time stays.' },
      reason => command(`/api/timekeeping/charge-codes/${encodeURIComponent(b.dataset.codeActive)}/active`, { active: b.dataset.active !== 'true' }, reason)).then(r => { if (r) { toast(r.message ?? 'Done'); load(); } }));
  };
  await load();
}
