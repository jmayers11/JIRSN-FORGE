import { tryGet, command } from './api.js';
import { who } from './people.js';
import { esc, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast } from './ui.js';
import { formModal, qty, can } from './forms.js';

// Aging, shelf life and selection (V3.6): age, expiry, open life, retest and storage are separate facts. A lot keeps
// the dates it was given; an extension (one person asks, another approves) and a retest are their own records.

export const SHELF_TONE = { NotTracked: 'none', Ok: 'ok', Expiring: 'warn', Expired: 'bad', OpenLifeExpired: 'bad', RetestOverdue: 'bad', RetestFailed: 'bad', Excursion: 'bad' };
export const SHELF_LABEL = { NotTracked: 'No shelf life', Ok: 'OK', Expiring: 'Expiring', Expired: 'Expired', OpenLifeExpired: 'Open life over', RetestOverdue: 'Retest overdue', RetestFailed: 'Failed retest', Excursion: 'Storage excursion' };
const pill = s => `<span class="pill ${SHELF_TONE[s.status] ?? 'none'}">${esc(SHELF_LABEL[s.status] ?? s.status)}</span>`;

/** Data for the inventory page's Shelf life tab. */
export async function loadShelfLife() {
  const [lots, pending, excursions] = await Promise.all([tryGet('/api/inventory/shelf-life'), tryGet('/api/inventory/extensions?pending=true'), tryGet('/api/inventory/excursions')]);
  return { lots, pending, excursions };
}

export function shelfLifeTab(shelf, me) {
  if (shelf.lots.error) return errorBlock(shelf.lots.error, 'shelf life');
  const approver = can(me, 'QualityManager');
  const pending = shelf.pending.data ?? [];
  const lots = shelf.lots.data ?? [];
  return `${can(me, 'MaterialHandler', 'InventoryController') ? '<div class="toolbar"><button class="btn small" data-excursion>Record a storage excursion</button></div>' : ''}
    ${pending.length ? `<h3 style="margin:0">Extensions waiting for a decision</h3><table class="table"><thead><tr><th>#</th><th>Lot</th><th>From</th><th>To</th><th>Evidence</th><th>Asked by</th><th></th></tr></thead><tbody>
      ${pending.map(e => `<tr><td>${e.extensionId}</td><td>${esc(e.partNumber)} ${esc(e.lotNumber)}</td><td>${esc(fmtDate(e.previousExpires))}</td><td><b>${esc(fmtDate(e.newExpires))}</b></td><td class="muted">${esc(e.evidence)}</td>
        <td>${esc(who(e.requestedBy))}</td><td class="nowrap">${approver && e.requestedBy !== `person:${me.personId}` ? `<button class="btn small primary" data-ext-approve="${e.extensionId}">Approve</button> <button class="btn small" data-ext-reject="${e.extensionId}">Reject</button>` : '<span class="faint">someone else decides</span>'}</td></tr>`).join('')}</tbody></table>` : ''}
    ${!lots.length ? emptyBlock('No lots with shelf life on hand', 'Quality sets a part\'s shelf life, retest and open life; lots then get their dates at receipt.') : `
    <table class="table"><thead><tr><th>Part</th><th>Lot</th><th class="num">On hand</th><th class="num">Age (d)</th><th>Expires</th><th>Retest due</th><th>Open life ends</th><th>Status</th></tr></thead><tbody>
    ${lots.map(v => { const s = v.shelfLife; return `<tr class="click" data-part="${esc(s.partNumber)}"><td><b>${esc(s.partNumber)}</b></td><td>${esc(s.lotNumber)}</td><td class="num">${qty(v.onHand)}</td><td class="num">${s.ageDays}</td>
      <td>${s.expires ? esc(fmtDate(s.expires)) : '—'}${s.originalExpires && s.originalExpires !== s.expires ? `<div class="faint">given ${esc(fmtDate(s.originalExpires))}</div>` : ''}</td>
      <td>${s.retestDue ? esc(fmtDate(s.retestDue)) : '—'}</td><td>${s.openExpiresAt ? esc(fmtDateTime(s.openExpiresAt)) : '—'}</td><td>${pill(s)}<div class="faint">${esc(s.explanation)}</div></td></tr>`; }).join('')}</tbody></table>`}
    ${(shelf.excursions.data ?? []).length ? `<h3 style="margin:8px 0 0">Storage excursions</h3>${shelf.excursions.data.slice(-10).reverse().map(x => `<div>${esc(x.locationCode)} · ${esc(x.condition)}${x.measuredC != null ? ` (${qty(x.measuredC)} °C)` : ''} · ${esc(fmtDateTime(x.startedAt))} – ${esc(fmtDateTime(x.endedAt))} <span class="faint">${esc(who(x.recordedBy))}${x.note ? ` · ${esc(x.note)}` : ''}</span></div>`).join('')}` : ''}
    <div class="faint" style="margin-top:6px">Expired, open-life-expired, retest-overdue or failed lots, and lots that sat through a storage excursion without a retest since, are never issued or shipped and do not count for planning.</div>`;
}

export function bindShelfLifeTab(page, locations, reload) {
  $$('[data-excursion]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: 'Record a storage excursion', detail: 'Recorded once it is over. Lots stored there whose storage limits it broke need a retest before use.', confirmLabel: 'Record', reasonRequired: true,
    fields: [
      { name: 'locationCode', label: 'Location', type: 'select', options: locations, required: true },
      { name: 'condition', label: 'What happened', required: true, placeholder: 'Freezer door left open' },
      { name: 'measuredC', label: 'Worst temperature measured (°C, optional)', type: 'number', step: 'any' },
      { name: 'startedAt', label: 'From', type: 'datetime-local', required: true },
      { name: 'endedAt', label: 'To', type: 'datetime-local', required: true },
      { name: 'note', label: 'Note (optional)' },
    ],
  }, (v, reason) => command('/api/inventory/excursions', { ...v, startedAt: new Date(v.startedAt).toISOString(), endedAt: new Date(v.endedAt).toISOString() }, reason), reload)));
  for (const [attr, verb] of [['extApprove', 'approve'], ['extReject', 'reject']]) {
    $$(`[data-${attr.replace(/[A-Z]/g, c => '-' + c.toLowerCase())}]`, page).forEach(b => b.addEventListener('click', async () => {
      const id = b.dataset[attr];
      const done = await confirmWithReason({ title: `${verb === 'approve' ? 'Approve' : 'Reject'} extension ${id}`, detail: 'The lot keeps its original date on record either way.', confirmLabel: verb === 'approve' ? 'Approve' : 'Reject' },
        reason => command(`/api/inventory/extensions/${id}/${verb}`, {}, reason));
      if (done) { toast(done.message); await reload(); }
    }));
  }
}

/** The shelf-life section of a part drawer: the rule, and each lot's dates with open, retest and extension actions. */
export async function appendPartShelfLife(container, part, item, me, refresh) {
  if (!item?.lotTracked && !item?.serialized) return;
  const [rule, lots] = await Promise.all([tryGet(`/api/inventory/shelf-life-rules/${encodeURIComponent(part)}`), tryGet(`/api/inventory/shelf-life?part=${encodeURIComponent(part)}`)]);
  const r = rule.data;
  const section = document.createElement('div');
  section.style.marginTop = '8px';
  const states = lots.data ?? [];
  section.innerHTML = `<div class="notice">Shelf life: ${r ? `${r.shelfLifeDays ? `${r.shelfLifeDays} days from ${r.basis === 'Manufacture' ? 'manufacture' : 'receipt'}` : 'no expiry'}${r.retestDays ? `, retest every ${r.retestDays} days` : ''}${r.openLifeHours ? `, ${r.openLifeHours} h once opened` : ''}${r.storageMinC != null || r.storageMaxC != null ? `, store ${r.storageMinC ?? '…'} to ${r.storageMaxC ?? '…'} °C` : ''}; ${r.selection === 'Fefo' ? 'first expired, first out' : 'first in, first out'} <span class="faint">· set by ${esc(who(r.setBy))}: ${esc(r.reason)}</span>` : 'not set — first in, first out'}
      ${can(me, 'QualityEngineer') ? ' <button class="btn small" data-shelf-rule>Change</button>' : ''}</div>
    ${states.length ? `<table class="table"><thead><tr><th>Lot</th><th class="num">Age (d)</th><th>Expires</th><th>Retest due</th><th>Opened</th><th>Status</th><th></th></tr></thead><tbody>
      ${states.map(v => { const s = v.shelfLife; return `<tr><td>${esc(s.lotNumber)}</td><td class="num">${s.ageDays}</td><td>${s.expires ? esc(fmtDate(s.expires)) : '—'}${s.originalExpires && s.originalExpires !== s.expires ? `<div class="faint">given ${esc(fmtDate(s.originalExpires))}</div>` : ''}</td>
        <td>${s.retestDue ? esc(fmtDate(s.retestDue)) : '—'}</td><td>${s.openedAt ? `${esc(fmtDateTime(s.openedAt))}<div class="faint">until ${esc(fmtDateTime(s.openExpiresAt))}</div>` : '—'}</td><td>${pill(s)}<div class="faint">${esc(s.explanation)}</div></td>
        <td class="nowrap">${r?.openLifeHours && !s.openedAt && can(me, 'MaterialHandler') ? `<button class="btn small ghost" data-open-lot="${esc(s.lotNumber)}">Open</button> ` : ''}${can(me, 'Inspector', 'QualityEngineer') ? `<button class="btn small ghost" data-retest="${esc(s.lotNumber)}">Retest</button> ` : ''}${s.expires && can(me, 'QualityEngineer') ? `<button class="btn small ghost" data-extend="${esc(s.lotNumber)}|${esc(s.expires)}">Extend</button>` : ''}</td></tr>`; }).join('')}</tbody></table>` : ''}`;
  container.appendChild(section);
  $$('[data-shelf-rule]', section).forEach(b => b.addEventListener('click', () => formModal({
    title: `Shelf life of ${part}`, detail: 'Applies to lots received from now on; lots already received keep their dates. Every setting is kept.', confirmLabel: 'Save', reasonRequired: true,
    fields: [
      { name: 'shelfLifeDays', label: 'Shelf life (days, empty for none)', type: 'number', min: 1, value: r?.shelfLifeDays },
      { name: 'basis', label: 'Counted from', type: 'select', value: r?.basis ?? 'Receipt', options: [['Receipt', 'Receipt'], ['Manufacture', 'Manufacture (certificate date)']] },
      { name: 'retestDays', label: 'Retest every (days, optional)', type: 'number', min: 1, value: r?.retestDays },
      { name: 'openLifeHours', label: 'Open-container life (hours, optional)', type: 'number', min: 1, value: r?.openLifeHours },
      { name: 'storageMinC', label: 'Store at least (°C, optional)', type: 'number', step: 'any', value: r?.storageMinC },
      { name: 'storageMaxC', label: 'Store at most (°C, optional)', type: 'number', step: 'any', value: r?.storageMaxC },
      { name: 'storageNote', label: 'Storage note (optional)', value: r?.storageNote },
      { name: 'selection', label: 'Use first', type: 'select', value: r?.selection ?? 'Fifo', options: [['Fifo', 'First in, first out'], ['Fefo', 'First expired, first out']] },
    ],
  }, (v, reason) => command(`/api/inventory/shelf-life-rules/${encodeURIComponent(part)}`, v, reason), refresh)));
  $$('[data-open-lot]', section).forEach(b => b.addEventListener('click', async () => {
    const done = await confirmWithReason({ title: `Open ${part} ${b.dataset.openLot}`, detail: `Its open life of ${r.openLifeHours} h starts now.`, confirmLabel: 'Open', reasonRequired: false },
      reason => command(`/api/inventory/lots/${encodeURIComponent(part)}/${encodeURIComponent(b.dataset.openLot)}/open`, {}, reason));
    if (done) { toast(done.message); await refresh(); }
  }));
  $$('[data-retest]', section).forEach(b => b.addEventListener('click', () => formModal({
    title: `Retest ${part} ${b.dataset.retest}`, detail: 'A pass sets the next retest (from the part\'s rule unless you give one) and clears a storage excursion before it. A failure stops the lot being used.', confirmLabel: 'Record',
    fields: [
      { name: 'passed', type: 'check', label: 'Result', text: 'Passed', value: true },
      { name: 'nextDue', label: 'Next retest (optional)', type: 'date' },
      { name: 'evidence', label: 'Evidence', type: 'textarea', required: true },
    ],
  }, (v, reason) => command(`/api/inventory/lots/${encodeURIComponent(part)}/${encodeURIComponent(b.dataset.retest)}/retests`, { ...v, nextDue: v.passed ? v.nextDue || null : null }, reason), refresh)));
  $$('[data-extend]', section).forEach(b => b.addEventListener('click', () => {
    const [lot, expires] = b.dataset.extend.split('|');
    formModal({
      title: `Extend ${part} ${lot}`, detail: `Expires ${fmtDate(expires)}. A different person with quality approval authority decides; the original date stays on record.`, confirmLabel: 'Request', reasonRequired: true,
      fields: [{ name: 'newExpires', label: 'New expiry', type: 'date', required: true }, { name: 'evidence', label: 'Evidence (test results, supplier letter)', type: 'textarea', required: true }],
    }, (v, reason) => command(`/api/inventory/lots/${encodeURIComponent(part)}/${encodeURIComponent(lot)}/extensions`, v, reason), refresh);
  }));
}
