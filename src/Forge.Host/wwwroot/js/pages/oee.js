import { tryGet, command } from '../api.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, toast, tabifyPanels } from '../ui.js';
import { formModal, canAction, today, plusDays } from '../forms.js';
import { who, loadDirectory } from '../people.js';

// Machines and OEE (owner decisions 2026-11-01). OEE is worked out per machine per shift from stoppages, production reports and the
// routing standards. Downtime reaches Forge from the terminal, from a supervisor or maintenance entering what an operator told them,
// or from a machine connection (unexplained until someone gives it a reason). OEE describes machines, never the people running them.

const pct = v => v === null || v === undefined ? '—' : `${(Number(v) * 100).toLocaleString('en-US', { maximumFractionDigits: 1 })}%`;
const min = v => `${Number(v ?? 0).toLocaleString('en-US', { maximumFractionDigits: 1 })} min`;
const tone = v => v === null || v === undefined ? 'none' : v >= 0.85 ? 'ok' : v >= 0.6 ? 'warn' : 'bad';
const STATE = { Running: 'ok', Idle: 'warn', Stopped: 'bad', Alarm: 'bad', Offline: 'none' };
const SOURCE = { Kiosk: 'At the terminal', Recorded: 'Entered for the operator', Connector: 'Machine connection' };
export const CATEGORIES = ['Unplanned', 'Maintenance', 'Setup', 'Changeover', 'Waiting', 'Starved', 'Blocked', 'Planned'];

/** OEE and its three parts, worked out from summed minutes and quantities across shifts. */
export function rollUp(shifts) {
  const sum = k => shifts.reduce((n, s) => n + Number(s[k] ?? 0), 0);
  const planned = sum('scheduledMinutes') - sum('plannedStopMinutes');
  const run = sum('runMinutes');
  const total = sum('good') + sum('scrap');
  const availability = planned > 0 ? run / planned : null;
  const performance = run > 0 && total > 0 ? sum('idealMinutes') / run : null;
  const quality = total > 0 ? sum('good') / total : null;
  const oee = availability !== null && performance !== null && quality !== null ? availability * Math.min(performance, 1) * quality : null;
  return { availability, performance, quality, oee, downtime: sum('downtimeMinutes'), unexplained: sum('unexplainedMinutes') };
}

export function oeeHtml(report) {
  const shifts = report?.shifts ?? [];
  if (!shifts.length) return emptyBlock('No shifts to show', 'OEE needs machines whose work centers have shifts. Assign shifts under Plant Setup.');
  const byMachine = [...new Set(shifts.map(s => s.machineCode))].map(code => ({ code, name: shifts.find(s => s.machineCode === code).machineName, ...rollUp(shifts.filter(s => s.machineCode === code)) }));
  return `<div class="kpi-row">${byMachine.map(m => `<article class="card"><header><b>${esc(m.code)}</b> <span class="faint">${esc(m.name)}</span></header>
      <div class="big ${tone(m.oee)}">${pct(m.oee)}</div><div class="faint">A ${pct(m.availability)} · P ${pct(m.performance)} · Q ${pct(m.quality)}</div>
      ${m.unexplained > 0 ? `<div class="pill warn">${min(m.unexplained)} unexplained</div>` : ''}</article>`).join('')}</div>
    <table class="table"><thead><tr><th>Machine</th><th>Shift</th><th>Day</th><th class="num">Planned</th><th class="num">Downtime</th><th class="num">Run</th><th class="num">Good</th><th class="num">Scrap</th>
      <th class="num">Availability</th><th class="num">Performance</th><th class="num">Quality</th><th class="num">OEE</th><th>Top reason</th></tr></thead><tbody>
    ${shifts.map(s => `<tr><td class="mono">${esc(s.machineCode)}</td><td>${esc(s.shiftName)}${s.inProgress ? ' <span class="pill info">now</span>' : ''}</td><td>${esc(fmtDate(s.date))}</td>
      <td class="num">${min(s.scheduledMinutes - s.plannedStopMinutes)}</td><td class="num">${min(s.downtimeMinutes)}${s.unexplainedMinutes > 0 ? ` <span class="pill warn" title="Detected from the machine; needs a reason">${min(s.unexplainedMinutes)} ?</span>` : ''}</td>
      <td class="num">${min(s.runMinutes)}</td><td class="num">${s.good}</td><td class="num">${s.scrap}</td>
      <td class="num">${pct(s.availability)}</td><td class="num">${pct(s.performance)}</td><td class="num">${pct(s.quality)}</td><td class="num"><span class="pill ${tone(s.oee)}">${pct(s.oee)}</span></td>
      <td>${s.reasons?.length ? `${esc(s.reasons[0].reasonCode)} <span class="faint">${min(s.reasons[0].minutes)}</span>` : '—'}${(s.notes ?? []).map(n => `<div class="faint">${esc(n)}</div>`).join('')}</td></tr>`).join('')}</tbody></table>
    ${(report.notes ?? []).map(n => `<div class="faint">${esc(n)}</div>`).join('')}`;
}

export function downtimeHtml(views, { canRecord = false } = {}) {
  if (!views?.length) return emptyBlock('No stoppages', 'Stoppages reported at the terminal, entered for an operator, or detected from a machine appear here.');
  return `<table class="table"><thead><tr><th>Machine</th><th>Started</th><th class="num">Minutes</th><th>Category</th><th>Reason</th><th>How it was reported</th><th></th></tr></thead><tbody>
    ${views.map(v => { const s = v.stoppage; return `<tr><td class="mono">${esc(s.machineCode)}</td><td>${esc(fmtDateTime(s.startedAt))}${s.endedAt ? '' : ' <span class="pill bad">open</span>'}</td>
      <td class="num">${min(v.minutes)}</td><td>${esc(s.category)}</td>
      <td>${s.reasonCode === 'UNEXPLAINED' ? '<span class="pill warn">Needs a reason</span>' : `${esc(s.reasonCode)} <span class="faint">${esc(v.reasonDescription)}</span>`}${s.note ? `<div class="faint">${esc(s.note)}</div>` : ''}</td>
      <td>${esc(SOURCE[s.source] ?? s.source)}${s.reportedBy ? ` · reported by ${esc(who(s.reportedBy))}` : ''}${s.source === 'Recorded' ? ` · entered by ${esc(who(s.startedBy))}` : ''}</td>
      <td>${canRecord ? `<button class="btn small" data-reclassify="${s.stoppageId}">${s.reasonCode === 'UNEXPLAINED' ? 'Give reason' : 'Change reason'}</button>${s.endedAt ? '' : ` <button class="btn small" data-end="${s.stoppageId}">Running again</button>`}` : ''}</td></tr>`; }).join('')}</tbody></table>`;
}

export function connectionsHtml(machines, connections, status, connectors, { canManage = false } = {}) {
  if (!machines?.length) return emptyBlock('No machines', 'Add machines under Plant Setup first.');
  const byCode = Object.fromEntries((connections ?? []).map(c => [c.machineCode, c]));
  const states = Object.fromEntries((status ?? []).map(s => [s.machineCode, s]));
  const names = Object.fromEntries((connectors ?? []).map(c => [c.kind, c.name]));
  return `<table class="table"><thead><tr><th>Machine</th><th>Connection</th><th>State</th><th class="num">Count</th><th>Last heard</th><th></th></tr></thead><tbody>
    ${machines.map(m => { const c = byCode[m.code]; const s = states[m.code]; return `<tr><td class="mono">${esc(m.code)}</td>
      <td>${c ? `${esc(names[c.kind] ?? c.kind)}${c.active ? '' : ' <span class="pill none">inactive</span>'}` : '<span class="faint">Not connected (manual)</span>'}</td>
      <td>${s ? `<span class="pill ${STATE[s.state] ?? 'none'}">${esc(s.state)}</span>${s.alarm ? ` <span class="faint">${esc(s.alarm)}</span>` : ''}${s.lastError ? `<div class="faint">${esc(s.lastError)}</div>` : ''}` : '—'}</td>
      <td class="num">${s?.partCount ?? '—'}</td><td>${s?.lastSeen ? esc(fmtDateTime(s.lastSeen)) : '—'}</td>
      <td>${canManage ? `<button class="btn small" data-connect="${esc(m.code)}">${c ? 'Change' : 'Connect'}</button>` : ''}</td></tr>`; }).join('')}</tbody></table>`;
}

export async function oeePage(page, ctx) {
  const me = ctx?.me;
  const can = { record: canAction(me, 'maintenance.downtime.report'), manage: canAction(me, 'connectivity.manage'), connections: canAction(me, 'connectivity.read') };
  let range = { from: plusDays(-6), to: today() };
  const load = async () => {
    const [oee, stops, reasons, machines, conn, connectors] = await Promise.all([
      tryGet(`/api/oee?from=${range.from}&to=${range.to}`), tryGet('/api/execution/stoppages?days=2'), tryGet('/api/execution/reason-codes'),
      tryGet('/api/resources/'), can.connections ? tryGet('/api/connectivity/machines') : Promise.resolve({ data: null }), can.connections ? tryGet('/api/connectivity/connectors') : Promise.resolve({ data: [] })]);
    const downtimeReasons = (reasons.data ?? []).filter(r => r.kind === 'Downtime' && r.active && r.code !== 'UNEXPLAINED');
    const machineList = machines.data?.machines ?? [];
    const people = can.record ? [...(await loadDirectory()).entries()].sort((a, b) => a[1].localeCompare(b[1])) : [];
    page.innerHTML = `
      <div class="page-head"><div><h1>Machines &amp; OEE</h1><div class="sub">OEE per machine per shift, downtime and its reasons, and how each machine reports to Forge. OEE describes machines, not the people who run them.</div></div></div>
      <section class="panel"><header><h2>OEE by shift</h2><span class="spacer"></span>
        <label class="inline">From <input class="input" type="date" data-from value="${range.from}"></label> <label class="inline">to <input class="input" type="date" data-to value="${range.to}"></label></header>
        ${oee.error ? errorBlock(oee.error, 'OEE') : oeeHtml(oee.data)}</section>
      <section class="panel"><header><h2>Downtime</h2>${can.record ? '<span class="spacer"></span><button class="btn" data-record>+ Record downtime for an operator</button>' : ''}</header>
        ${stops.error ? errorBlock(stops.error, 'stoppages') : downtimeHtml(stops.data, { canRecord: can.record })}</section>
      ${can.connections ? `<section class="panel"><header><h2>Machine connections</h2>${can.manage ? '<span class="spacer"></span><button class="btn" data-poll>Read machines now</button>' : ''}</header>
        ${conn.error ? errorBlock(conn.error, 'machine connections') : connectionsHtml(machineList, conn.data?.connections, conn.data?.status, connectors.data, { canManage: can.manage })}</section>` : ''}`;
    tabifyPanels(page, [{ label: 'OEE by shift', match: ['oee by shift'] }, { label: 'Downtime', match: ['downtime'] }, { label: 'Machine connections', match: ['machine connections'] }]);

    for (const input of $$('[data-from], [data-to]', page)) input.addEventListener('change', () => { range = { from: $('[data-from]', page).value, to: $('[data-to]', page).value }; load(); });
    const reasonOptions = downtimeReasons.map(r => [r.code, `${r.code} — ${r.description} (${r.category})`]);
    const categoryOf = code => downtimeReasons.find(r => r.code === code)?.category;
    $('[data-record]', page)?.addEventListener('click', () => formModal({
      title: 'Record downtime an operator reported', detail: 'For a stop told to you by an operator. Forge keeps who reported it and that you entered it.', confirmLabel: 'Record', reasonRequired: false,
      fields: [{ name: 'machineCode', label: 'Machine', type: 'select', options: machineList.filter(m => m.active !== false).map(m => [m.code, `${m.code} — ${m.name}`]) },
        { name: 'reasonCode', label: 'Reason', type: 'select', options: reasonOptions },
        { name: 'reportedBy', label: 'Reported by', type: 'select', blank: '— (I saw it myself)', options: people },
        { name: 'startedAt', label: 'Stopped at (blank: now)', type: 'datetime-local' }, { name: 'note', label: 'What they said', type: 'textarea' }],
    }, v => command('/api/execution/stoppages', { machineCode: v.machineCode, category: categoryOf(v.reasonCode), reasonCode: v.reasonCode, note: v.note || null,
      startedAt: v.startedAt ? new Date(v.startedAt).toISOString() : null, reportedBy: v.reportedBy || null }), load));
    for (const b of $$('[data-reclassify]', page)) b.addEventListener('click', () => formModal({
      title: `Reason for stoppage ${b.dataset.reclassify}`, confirmLabel: 'Save', fields: [{ name: 'reasonCode', label: 'Reason', type: 'select', options: reasonOptions }, { name: 'note', label: 'Note', type: 'textarea' }],
    }, v => command(`/api/execution/stoppages/${b.dataset.reclassify}/reclassify`, { category: categoryOf(v.reasonCode), reasonCode: v.reasonCode, note: v.note || null }), load));
    for (const b of $$('[data-end]', page)) b.addEventListener('click', async () => { const r = await command(`/api/execution/stoppages/${b.dataset.end}/end`, {}); toast(r.message ?? 'Ended'); load(); });
    $('[data-poll]', page)?.addEventListener('click', async () => { const r = await command('/api/connectivity/poll', {}); toast(`${r.polled} machine(s) read.`); load(); });
    for (const b of $$('[data-connect]', page)) b.addEventListener('click', () => {
      const current = (conn.data?.connections ?? []).find(c => c.machineCode === b.dataset.connect) ?? { kind: 'Manual', settings: {}, pollSeconds: 10, stopAfterSeconds: 120, active: true };
      const kinds = (connectors.data ?? []);
      formModal({
        title: `How ${b.dataset.connect} reports to Forge`, detail: kinds.map(k => `${k.name}: ${k.description}${k.available ? '' : ' (not in this build yet)'}`).join(' · '), confirmLabel: 'Save', reasonRequired: true,
        fields: [{ name: 'kind', label: 'Connector', type: 'select', options: kinds.map(k => [k.kind, k.name]), value: current.kind },
          { name: 'settings', label: 'Settings (name=value per line; see the connector list)', type: 'textarea', value: Object.entries(current.settings ?? {}).map(([k, v]) => `${k}=${v}`).join('\n') },
          { name: 'pollSeconds', label: 'Read every (seconds)', type: 'number', min: 2, max: 3600, value: current.pollSeconds },
          { name: 'stopAfterSeconds', label: 'A stop counts as downtime after (seconds)', type: 'number', min: 0, max: 3600, value: current.stopAfterSeconds },
          { name: 'active', label: 'Active', type: 'check', text: 'Active', value: current.active }],
      }, (v, reason) => command(`/api/connectivity/machines/${encodeURIComponent(b.dataset.connect)}`, { kind: v.kind, pollSeconds: Number(v.pollSeconds), stopAfterSeconds: Number(v.stopAfterSeconds), active: !!v.active,
        settings: Object.fromEntries(String(v.settings ?? '').split('\n').map(l => l.trim()).filter(l => l.includes('=')).map(l => [l.slice(0, l.indexOf('=')).trim(), l.slice(l.indexOf('=') + 1).trim()])) }, reason), load);
    });
  };
  await load();
}
