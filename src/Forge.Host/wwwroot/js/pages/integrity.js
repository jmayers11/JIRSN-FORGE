import { get } from '../api.js';
import { esc, toast } from '../ui.js';

const labels = {
  NegativeStock: 'Negative ledger balance',
  DuplicateAssetSerial: 'Duplicate asset serial',
  ConsumptionBeforeIssue: 'Consumption without a prior issue',
  ExpiredCalibrationInUse: 'Tool use after calibration due date',
};

export async function integrityPage(root) {
  let exceptions = [];
  try { exceptions = await get('/api/integrity/exceptions'); }
  catch (error) { toast(error.message, true); }
  root.innerHTML = `<section class="page-head"><div><h1>Data Integrity</h1><p class="muted">Evidence-based data or process exceptions. These findings describe records and do not imply intent or misconduct.</p></div><button class="btn" id="refresh-integrity">Refresh</button></section>
    <section class="section"><div class="section-head"><h2>Current findings</h2><span class="muted">${exceptions.length}</span></div><div class="stack">${exceptions.map(x => `<article class="card"><div class="card-head"><div><span class="badge">Data or process exception</span><h3>${esc(labels[x.kind] || x.summary)}</h3><p class="muted">${esc(x.source)} · observed ${esc(new Date(x.observedAt).toLocaleString())}</p></div></div><p>${esc(x.evidence)}</p></article>`).join('') || '<div class="empty">No integrity exceptions were found in the records you may read.</div>'}</div></section>`;
  root.querySelector('#refresh-integrity')?.addEventListener('click', () => integrityPage(root).catch(e => toast(e.message, true)));
}
