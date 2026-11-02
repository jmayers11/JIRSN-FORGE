import { tryGet, command, upload, download } from '../api.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast, tabifyPanels } from '../ui.js';
import { formModal, canAction } from '../forms.js';
import { who } from '../people.js';

// Hazard communication (owner decisions 2026-11-01, OSHA 29 CFR 1910.1200). Every signed-in employee can read the chemical register and
// open approved safety data sheets; the EHS Coordinator keeps the register and approves SDS; a hazardous material without an approved
// SDS cannot be bought or received; the binder download is the backup for when systems are down.

export const PICTOGRAMS = { GHS01: 'Explosive', GHS02: 'Flammable', GHS03: 'Oxidizer', GHS04: 'Gas under pressure', GHS05: 'Corrosive', GHS06: 'Toxic', GHS07: 'Irritant / harmful', GHS08: 'Health hazard', GHS09: 'Environment' };
const SIGNAL = { Danger: 'bad', Warning: 'warn', None: 'none' };
const STATUS = { Submitted: 'warn', Approved: 'ok', Rejected: 'bad', Superseded: 'none' };

/** Whether an approved SDS's review is overdue or due within 30 days of today (YYYY-MM-DD strings). */
export function reviewState(sds, today) {
  if (!sds?.reviewDue) return null;
  if (sds.reviewDue < today) return 'overdue';
  const soon = new Date(`${today}T12:00:00Z`); soon.setUTCDate(soon.getUTCDate() + 30);
  return sds.reviewDue <= soon.toISOString().slice(0, 10) ? 'soon' : 'ok';
}

export function registerHtml(views, { today, canManage = false } = {}) {
  if (!views?.length) return emptyBlock('No chemicals in the register', 'The EHS Coordinator adds materials and marks the hazardous ones; each hazardous material needs an approved safety data sheet.');
  return `<table class="table"><thead><tr><th>Part</th><th>Product</th><th>Hazard</th><th>Safety data sheet</th><th>Kept at</th><th></th></tr></thead><tbody>
    ${views.map(v => { const c = v.chemical; const s = v.current; const review = reviewState(s, today); return `<tr>
      <td class="mono">${esc(v.partNumber)}</td><td>${esc(s?.productName ?? v.description ?? '')}${s ? `<div class="faint">${esc(s.manufacturer)}</div>` : ''}</td>
      <td>${c.hazardous ? `<span class="pill ${SIGNAL[c.signalWord] ?? 'none'}">${esc(c.signalWord === 'None' ? 'Hazardous' : c.signalWord)}</span> ${(c.pictograms ?? []).map(p => `<span class="pill none" title="${esc(PICTOGRAMS[p] ?? p)}">${esc(p)}</span>`).join(' ')}${c.ppe ? `<div class="faint">PPE: ${esc(c.ppe)}</div>` : ''}` : '<span class="faint">Not hazardous</span>'}</td>
      <td>${s ? `<button class="btn small" data-open="${s.sdsId}">Open SDS</button> <span class="faint">revised ${esc(fmtDate(s.revisionDate))}</span>${review === 'overdue' ? ' <span class="pill bad">Review overdue</span>' : review === 'soon' ? ' <span class="pill warn">Review due soon</span>' : ''}`
        : c.hazardous ? '<span class="pill bad">No approved SDS — cannot be bought or received</span>' : '—'}${v.waiting ? ` <span class="pill warn">${v.waiting} waiting</span>` : ''}</td>
      <td>${(v.locations ?? []).map(l => esc(l.locationCode)).join(', ') || '—'}</td>
      <td>${c.trainingReviewDue ? '<span class="pill warn">HazCom training review needed</span> ' : ''}${canManage && c.trainingReviewDue ? `<button class="btn small primary" data-training-review="${esc(v.partNumber)}">Review training</button> ` : ''}${canManage ? `<button class="btn small" data-chemical="${esc(v.partNumber)}">Edit</button>` : ''}</td></tr>`; }).join('')}</tbody></table>`;
}

const TRAINING = { Current: 'ok', Expiring: 'warn', Expired: 'bad', Missing: 'bad', InTraining: 'warn', Suspended: 'bad' };
const TRAINING_LABEL = { Current: 'Current', Expiring: 'Expiring within 30 days', Expired: 'Expired', Missing: 'Not trained', InTraining: 'In training', Suspended: 'Suspended' };

/** HazCom training for every active employee: the site requirement, who is current, and who needs training (with a checkbox to request it). */
export function trainingHtml(view, { canRequest = false } = {}) {
  if (!view) return emptyBlock('HazCom training unavailable', 'The HazCom qualification could not be read.');
  const people = view.people ?? [];
  const counts = people.reduce((m, p) => ({ ...m, [p.status]: (m[p.status] ?? 0) + 1 }), {});
  const req = (view.requirements ?? []).map(r => `${r.workCenterCode ? esc(r.workCenterCode) : 'Every new employee'} within ${r.dueDays} day${r.dueDays === 1 ? '' : 's'}`).join('; ');
  const needs = p => p.status !== 'Current' && p.status !== 'InTraining' && !p.openRequestId;
  return `<p class="muted">${esc(view.qualificationName ?? view.qualificationCode)} (${esc(view.qualificationCode)}) is a site-wide qualification.
      Renewal: ${view.validityDays ? `every ${view.validityDays} days` : 'no expiry'}. New employees: ${req || 'no onboarding requirement (the plant retired it)'}.
      Plants change these under Workforce (Change renewal period) and Training &amp; Performance (new-employee training).</p>
    ${(view.chemicalsAwaitingTrainingReview ?? []).length ? `<p><span class="pill warn">New hazards to review</span> ${view.chemicalsAwaitingTrainingReview.map(esc).join(', ')}</p>` : ''}
    <div class="toolbar">${Object.keys(TRAINING).map(s => counts[s] ? `<span class="pill ${TRAINING[s]}">${esc(TRAINING_LABEL[s])}: ${counts[s]}</span>` : '').join(' ')}
      ${canRequest && people.some(needs) ? '<span class="spacer"></span><button class="btn primary" data-request-training>Request HazCom training for selected</button>' : ''}</div>
    ${!people.length ? emptyBlock('No active employees', 'Register workers under Workforce.') : `<table class="table"><thead><tr>${canRequest ? '<th></th>' : ''}<th>Employee</th><th>Work center</th><th>HazCom</th><th>Expires</th><th>Training request</th></tr></thead><tbody>
    ${people.map(p => `<tr>${canRequest ? `<td>${needs(p) ? `<input type="checkbox" data-train-person="${esc(p.personId)}" checked aria-label="Request for ${esc(p.displayName)}">` : ''}</td>` : ''}
      <td>${esc(p.displayName)}${p.department ? `<div class="faint">${esc(p.department)}</div>` : ''}</td><td class="mono">${esc(p.workCenterCode)}</td>
      <td><span class="pill ${TRAINING[p.status] ?? 'none'}">${esc(TRAINING_LABEL[p.status] ?? p.status)}</span></td><td>${p.expiresAt ? esc(fmtDate(p.expiresAt)) : '—'}</td>
      <td>${p.openRequestId ? `${esc(p.openRequestStatus)}${p.openRequestDue ? ` · due ${esc(fmtDate(p.openRequestDue))}` : ''}` : '—'}</td></tr>`).join('')}</tbody></table>`}`;
}

export function sdsHtml(list, { canManage = false } = {}) {
  if (!list?.length) return emptyBlock('No safety data sheets', 'Upload the supplier\'s SDS; the EHS Coordinator approves it.');
  return `<table class="table"><thead><tr><th>Part</th><th>Product</th><th>Revised</th><th>Status</th><th>Submitted</th><th>Review due</th><th></th></tr></thead><tbody>
    ${list.map(s => `<tr><td class="mono">${esc(s.partNumber)}</td><td>${esc(s.productName)} <span class="faint">${esc(s.manufacturer)}</span></td><td>${esc(fmtDate(s.revisionDate))}</td>
      <td><span class="pill ${STATUS[s.status] ?? 'none'}">${esc(s.status)}</span>${s.decisionNote ? `<div class="faint">${esc(s.decisionNote)}</div>` : ''}</td>
      <td>${esc(who(s.submittedBy))} ${esc(fmtDateTime(s.submittedAt))}</td><td>${s.reviewDue ? esc(fmtDate(s.reviewDue)) : '—'}</td>
      <td><button class="btn small" data-open="${s.sdsId}">Open</button>${canManage && s.status === 'Submitted' ? ` <button class="btn small primary" data-approve="${s.sdsId}">Approve</button> <button class="btn small" data-reject="${s.sdsId}">Reject</button>` : ''}${canManage && s.status === 'Approved' ? ` <button class="btn small" data-review="${s.sdsId}">Confirm current</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

export async function chemicalsPage(page, ctx) {
  const me = ctx?.me;
  const can = { manage: canAction(me, 'hazcom.manage'), submit: canAction(me, 'hazcom.submit'), training: canAction(me, 'competency.read'), request: canAction(me, 'workforce.training-requests.request') };
  const today = new Date().toISOString().slice(0, 10);
  const load = async () => {
    const [register, sds, training] = await Promise.all([tryGet('/api/hazcom/chemicals'), tryGet('/api/hazcom/sds'), can.training ? tryGet('/api/hazcom/training') : Promise.resolve(null)]);
    const views = register.data ?? [];
    const locations = [...new Set(views.flatMap(v => (v.locations ?? []).map(l => l.locationCode)))].sort();
    page.innerHTML = `
      <div class="page-head"><div><h1>Chemicals &amp; SDS</h1><div class="sub">The chemical register and safety data sheets. Anyone can open an approved SDS here or at the kiosk; a hazardous material without one cannot be bought or received.</div></div></div>
      <section class="panel"><header><h2>Chemical register</h2><span class="spacer"></span>
        <select class="select" data-binder-location><option value="">All locations</option>${locations.map(l => `<option>${esc(l)}</option>`).join('')}</select> <button class="btn" data-binder>Download SDS binder</button>
        ${can.manage ? ' <button class="btn" data-chemical="">+ Chemical</button>' : ''}</header>
        ${register.error ? errorBlock(register.error, 'the chemical register') : registerHtml(views, { today, canManage: can.manage })}</section>
      <section class="panel"><header><h2>Safety data sheets</h2>${can.submit ? '<span class="spacer"></span><button class="btn" data-submit>Upload SDS</button>' : ''}</header>
        ${sds.error ? errorBlock(sds.error, 'safety data sheets') : sdsHtml(sds.data, { canManage: can.manage })}</section>
      ${training ? `<section class="panel"><header><h2>HazCom training</h2></header>${training.error ? errorBlock(training.error, 'HazCom training') : trainingHtml(training.data, { canRequest: can.request })}</section>` : ''}`;
    tabifyPanels(page, [{ label: 'Chemical register', match: ['chemical register'] }, { label: 'Safety data sheets', match: ['safety data sheets'] },
      ...(training ? [{ label: 'HazCom training', match: ['hazcom training'] }] : [])]);

    for (const b of $$('[data-open]', page)) b.addEventListener('click', () => download(`/api/hazcom/sds/${b.dataset.open}/file`, `SDS-${b.dataset.open}.pdf`).catch(e => toast(e.message, true)));
    $('[data-binder]', page)?.addEventListener('click', () => {
      const location = $('[data-binder-location]', page).value;
      download(`/api/hazcom/binder.zip${location ? `?location=${encodeURIComponent(location)}` : ''}`, 'sds-binder.zip').catch(e => toast(e.message, true));
    });
    for (const b of $$('[data-chemical]', page)) b.addEventListener('click', () => {
      const c = views.find(v => v.partNumber === b.dataset.chemical)?.chemical ?? { hazardous: true, signalWord: 'Warning', pictograms: [] };
      formModal({
        title: c.partNumber ? `Chemical ${c.partNumber}` : 'Add a material to the register', detail: `Pictograms: ${Object.entries(PICTOGRAMS).map(([k, v]) => `${k} ${v}`).join(', ')}.`, confirmLabel: 'Save', reasonRequired: true,
        fields: [{ name: 'partNumber', label: 'Part', code: true, required: true, value: c.partNumber, readonly: !!c.partNumber },
          { name: 'hazardous', label: 'Hazardous', type: 'check', text: 'Hazardous chemical (needs an approved SDS)', value: c.hazardous },
          { name: 'signalWord', label: 'Signal word', type: 'select', options: [['None', 'None'], ['Warning', 'Warning'], ['Danger', 'Danger']], value: c.signalWord },
          { name: 'pictograms', label: 'Pictograms (comma separated, e.g. GHS02, GHS07)', value: (c.pictograms ?? []).join(', ') },
          { name: 'hazardStatements', label: 'Hazard statements', type: 'textarea', value: c.hazardStatements }, { name: 'ppe', label: 'Required PPE', value: c.ppe },
          { name: 'storage', label: 'Storage', value: c.storage }],
      }, (v, reason) => command(`/api/hazcom/chemicals/${encodeURIComponent(v.partNumber)}`, { hazardous: !!v.hazardous, signalWord: v.signalWord,
        pictograms: String(v.pictograms ?? '').split(',').map(s => s.trim()).filter(Boolean), hazardStatements: v.hazardStatements || null, ppe: v.ppe || null, storage: v.storage || null }, reason), load);
    });
    $('[data-submit]', page)?.addEventListener('click', () => formModal({
      title: 'Upload a safety data sheet', detail: 'The supplier\'s SDS (PDF). The EHS Coordinator approves it before the material can be bought or received.', confirmLabel: 'Upload',
      fields: [{ name: 'partNumber', label: 'Part', code: true, required: true }, { name: 'manufacturer', label: 'Manufacturer', required: true }, { name: 'productName', label: 'Product name', required: true },
        { name: 'revisionDate', label: 'SDS revision date', type: 'date', required: true }, { name: 'file', label: 'SDS file', type: 'file' }],
    }, async v => {
      const file = v.file instanceof File ? v.file : v.file?.[0];
      const sha = await upload(file);
      return command('/api/hazcom/sds', { partNumber: v.partNumber, manufacturer: v.manufacturer, productName: v.productName, revisionDate: v.revisionDate, fileSha256: sha, fileName: file?.name ?? null });
    }, load));
    for (const b of $$('[data-training-review]', page)) b.addEventListener('click', () => formModal({
      title: `HazCom training for ${b.dataset.trainingReview}`, detail: 'OSHA requires training when a new chemical hazard is introduced. Decide whether site HazCom training already covers this hazard class.', confirmLabel: 'Record decision',
      fields: [{ name: 'refresher', label: 'Decision', type: 'select', required: true, options: [['covered', 'Covered by site HazCom training'], ['refresher', 'Refresher training needed']], value: 'covered' },
        { name: 'note', label: 'Why', type: 'textarea', required: true }],
    }, v => command(`/api/hazcom/chemicals/${encodeURIComponent(b.dataset.trainingReview)}/training-review`, { refresherNeeded: v.refresher === 'refresher', note: v.note }), load));
    $('[data-request-training]', page)?.addEventListener('click', () => {
      const personIds = $$('[data-train-person]', page).filter(c => c.checked).map(c => c.dataset.trainPerson);
      if (!personIds.length) { toast('Choose at least one employee.', true); return; }
      formModal({ title: `Request HazCom training for ${personIds.length} ${personIds.length === 1 ? 'person' : 'people'}`, detail: 'Each becomes a training request for the technical trainers to schedule. Delivering training certifies nobody; a trainer certifies.',
        confirmLabel: 'Request', reasonRequired: true, fields: [{ name: 'dueOn', label: 'Due by', type: 'date' }] },
      (v, reason) => command('/api/hazcom/training/requests', { personIds, dueOn: v.dueOn || null }, reason), load);
    });
    for (const b of $$('[data-approve]', page)) b.addEventListener('click', () => confirmWithReason({ title: `Approve SDS ${b.dataset.approve}`, detail: 'It becomes the SDS in force; an older approved one is superseded.', confirmLabel: 'Approve', reasonRequired: false },
      reason => command(`/api/hazcom/sds/${b.dataset.approve}/decide`, { approve: true }, reason)).then(r => { if (r) { toast(r.message ?? 'Approved'); load(); } }));
    for (const b of $$('[data-reject]', page)) b.addEventListener('click', () => confirmWithReason({ title: `Reject SDS ${b.dataset.reject}`, detail: 'Say why (wrong product, incomplete, not the latest revision …).', confirmLabel: 'Reject' },
      reason => command(`/api/hazcom/sds/${b.dataset.reject}/decide`, { approve: false, note: reason }, reason)).then(r => { if (r) { toast(r.message ?? 'Rejected'); load(); } }));
    for (const b of $$('[data-review]', page)) b.addEventListener('click', () => formModal({
      title: 'Confirm this SDS is still current', confirmLabel: 'Confirm', fields: [{ name: 'note', label: 'How you checked', type: 'textarea', required: true }],
    }, v => command(`/api/hazcom/sds/${b.dataset.review}/review`, { note: v.note }), load));
  };
  await load();
}
