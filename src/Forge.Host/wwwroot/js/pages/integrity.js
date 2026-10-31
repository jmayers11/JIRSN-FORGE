import { get } from '../api.js';
import { esc, toast } from '../ui.js';

const labels = {
  NegativeStock: 'Negative ledger balance',
  DuplicateAssetSerial: 'Duplicate asset serial',
  SerialInMultipleLocations: 'Material serial at multiple locations',
  ConsumptionBeforeIssue: 'Consumption without a prior issue',
  ExpiredCalibrationInUse: 'Tool use after calibration due date',
};

/** Findings grouped by kind: the kinds with their counts beside the selected kind's findings (UI density audit 2026-10-27). */
export async function integrityPage(root, ctx = {}) {
  let exceptions = [];
  try { exceptions = await get('/api/integrity/exceptions'); }
  catch (error) { toast(error.message, true); }
  const kinds = [...new Set(exceptions.map(x => x.kind))];
  let selected = kinds.includes(ctx?.query?.get?.('kind')) ? ctx.query.get('kind') : kinds[0] ?? null;
  const render = () => {
    const shown = exceptions.filter(x => x.kind === selected);
    root.innerHTML = `<section class="page-head"><div><h1>Data Integrity</h1><p class="muted">Evidence-based data or process exceptions. These findings describe records and do not imply intent or misconduct.</p></div><button class="btn" id="refresh-integrity">Refresh</button></section>
    ${exceptions.length ? `<div class="list-detail"><div class="list-detail-list"><section class="panel"><header><h2>Current findings</h2><span class="spacer"></span><span class="muted">${exceptions.length}</span></header>
      <div class="stack">${kinds.map(kind => `<button type="button" class="btn ghost integrity-kind${kind === selected ? ' on' : ''}" data-kind="${esc(kind)}" ${kind === selected ? 'aria-current="true"' : ''}><span>${esc(labels[kind] || kind)}</span><span class="badge">${exceptions.filter(x => x.kind === kind).length}</span></button>`).join('')}</div></section></div>
      <div class="list-detail-detail"><section class="panel"><header><h2>${esc(labels[selected] || selected)}</h2><span class="spacer"></span><span class="muted">${shown.length}</span></header>
      <div class="stack">${shown.map(x => `<article class="card"><div class="card-head"><div><span class="badge">Data or process exception</span><h3>${esc(x.summary || labels[x.kind] || x.kind)}</h3><p class="muted">${esc(x.source)} · observed ${esc(new Date(x.observedAt).toLocaleString())}</p></div></div><p>${esc(x.evidence)}</p></article>`).join('')}</div></section></div></div>`
      : '<section class="section"><div class="section-head"><h2>Current findings</h2><span class="muted">0</span></div><div class="empty">No integrity exceptions were found in the records you may read.</div></section>'}`;
    root.querySelector('#refresh-integrity')?.addEventListener('click', () => integrityPage(root, ctx).catch(e => toast(e.message, true)));
    root.querySelectorAll('[data-kind]').forEach(button => button.addEventListener('click', () => { selected = button.dataset.kind; render(); }));
  };
  render();
}
