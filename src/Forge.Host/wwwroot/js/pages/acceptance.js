import { tryGet, command } from '../api.js';
import { who } from '../people.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast } from '../ui.js';
import { formModal, qty, canAction } from '../forms.js';

// Product acceptance (V4.2): approved acceptance criteria per part revision, an acceptance inspection of an exact
// physical subject (part, lot, location) against the effective criteria, and an exact-quantity product release
// decision that Inventory consumes as the product moves on. Nothing is released by default.

const API = '/api/quality/acceptance';
const STATE_TONE = { Draft: 'info', Submitted: 'warn', Approved: 'warn', Effective: 'ok', Superseded: 'none', Withdrawn: 'none' };
const label = value => String(value ?? '').replace(/([a-z])([A-Z])/g, '$1 $2');
const pill = (text, tone = 'none') => `<span class="pill ${tone}">${esc(label(text))}</span>`;
const num = value => value === '' || value == null ? null : Number(value);

/** "number | name | Variable|Attribute | nominal | lower | upper | unit", one characteristic per line. */
export function parseCharacteristics(text) {
  const lines = String(text ?? '').split(/\r?\n/).map(l => l.trim()).filter(Boolean);
  if (!lines.length) throw new Error('Define at least one characteristic.');
  return lines.map((line, i) => {
    const [number, name, kind, nominal, lower, upper, unit] = line.split('|').map(p => p.trim());
    const k = /^a/i.test(kind ?? '') ? 'Attribute' : /^v/i.test(kind ?? '') ? 'Variable' : null;
    if (!Number.isSafeInteger(Number(number)) || Number(number) <= 0 || !name || !k) throw new Error(`Line ${i + 1}: use "number | name | Variable or Attribute | nominal | lower | upper | unit".`);
    return k === 'Attribute' ? { number: Number(number), name, kind: k, nominal: null, lower: null, upper: null, unit: null }
      : { number: Number(number), name, kind: k, nominal: num(nominal), lower: num(lower), upper: num(upper), unit: unit || null };
  });
}

export const formatCharacteristics = list => (list ?? []).map(c => [c.number, c.name, c.kind, c.nominal ?? '', c.lower ?? '', c.upper ?? '', c.unit ?? ''].join(' | ')).join('\n');

/** "DOC-NUMBER REVISION", one controlled document revision per line. */
export function parseDocuments(text) {
  const lines = String(text ?? '').split(/\r?\n/).map(l => l.trim()).filter(Boolean);
  if (!lines.length) throw new Error('Name at least one controlled document revision.');
  return lines.map((line, i) => {
    const [documentNumber, revision] = line.split(/\s+/);
    if (!documentNumber || !revision) throw new Error(`Document line ${i + 1}: use "DOCUMENT-NUMBER REVISION".`);
    return { documentNumber: documentNumber.toUpperCase(), revision: revision.toUpperCase() };
  });
}

export function criteriaContent(v) {
  return {
    partNumber: v.partNumber, revision: v.revision, operationSequence: v.operationSequence ?? null, title: v.title,
    coverage: { kind: v.coverageKind, minimumSampleQuantity: v.minimumSample, maximumRepresentedQuantity: v.maximumRepresented, method: v.method, humanApprovalBasis: v.approvalBasis },
    characteristics: parseCharacteristics(v.characteristics), documents: parseDocuments(v.documents),
  };
}

function criteriaFields(revision = null, fixed = false) {
  const c = revision?.content ?? {}, cov = c.coverage ?? {};
  return [
    ...(revision && fixed ? [] : [
      { name: 'code', label: 'Criteria code', code: true, required: true, value: revision?.code },
      { name: 'facilityCode', label: 'Facility', code: true, required: true, value: revision?.facilityCode },
      { name: 'markings', label: 'Handling markings', type: 'select', value: revision?.markings ?? 'None', options: [['None', 'None'], ['ControlledUnclassified', 'Controlled unclassified'], ['ExportControlled', 'Export controlled'], ['Proprietary', 'Proprietary']] },
    ]),
    { name: 'partNumber', label: 'Part', code: true, required: true, value: c.partNumber },
    { name: 'revision', label: 'Part revision', code: true, required: true, value: c.revision },
    { name: 'operationSequence', label: 'Operation (blank: final acceptance)', type: 'number', min: 1, value: c.operationSequence },
    { name: 'title', label: 'Title', required: true, value: c.title },
    { name: 'coverageKind', label: 'Coverage', type: 'select', value: cov.kind ?? 'FullQuantity', options: [['FullQuantity', 'Every unit'], ['ApprovedSampling', 'Approved sampling plan']] },
    { name: 'minimumSample', label: 'Minimum sample', type: 'number', min: 1, required: true, value: cov.minimumSampleQuantity },
    { name: 'maximumRepresented', label: 'Largest quantity one inspection may represent', type: 'number', min: 1, required: true, value: cov.maximumRepresentedQuantity },
    { name: 'method', label: 'Sampling / inspection method', type: 'textarea', required: true, value: cov.method },
    { name: 'approvalBasis', label: 'Basis for this coverage', type: 'textarea', required: true, value: cov.humanApprovalBasis },
    { name: 'characteristics', label: 'Characteristics', type: 'repeater', required: true, minRows: 1, rowLabel: 'characteristic', pluralRowLabel: 'characteristics', value: c.characteristics ?? [],
      columns: [
        { name: 'number', label: 'No.', type: 'number', min: 1, required: true },
        { name: 'name', label: 'Characteristic', required: true },
        { name: 'kind', label: 'Type', type: 'select', required: true, options: [['Variable', 'Variable'], ['Attribute', 'Attribute']] },
        { name: 'nominal', label: 'Nominal', type: 'number', step: 'any' },
        { name: 'lower', label: 'Lower limit', type: 'number', step: 'any' },
        { name: 'upper', label: 'Upper limit', type: 'number', step: 'any' },
        { name: 'unit', label: 'Unit' },
      ],
      serialize: rows => rows.map(c => [c.number, c.name, c.kind, c.nominal ?? '', c.lower ?? '', c.upper ?? '', c.unit ?? ''].join(' | ')).join('\n'),
      hint: 'Use one row per characteristic. Attribute checks do not use numeric limits.' },
    { name: 'documents', label: 'Controlled documents', type: 'repeater', required: true, minRows: 1, rowLabel: 'document', pluralRowLabel: 'documents', value: c.documents ?? [],
      columns: [{ name: 'documentNumber', label: 'Document number', required: true }, { name: 'revision', label: 'Revision', required: true }],
      serialize: rows => rows.map(d => `${d.documentNumber} ${d.revision}`).join('\n'), hint: 'Add the exact controlled revision used by this criteria.' },
  ];
}

/** Findings for every characteristic of every sampled unit, read from the results sheet inputs. */
export function readFindings(root, characteristics, sample) {
  const findings = [];
  for (const c of characteristics) for (let s = 1; s <= sample; s++) {
    const cell = name => root.querySelector(`[data-cell="${c.number}-${s}-${name}"]`)?.value?.trim() ?? '';
    if (c.kind === 'Variable') {
      const measured = cell('measured'), gauge = cell('gauge');
      if (measured === '' || !gauge) throw new Error(`Record the measurement and gauge for ${c.name}, unit ${s}.`);
      findings.push({ characteristicNumber: c.number, sampleNumber: s, measured: Number(measured), accepted: null, gaugeCode: gauge.toUpperCase() });
    } else {
      const accepted = cell('accepted');
      if (!accepted) throw new Error(`Record accept or reject for ${c.name}, unit ${s}.`);
      findings.push({ characteristicNumber: c.number, sampleNumber: s, measured: null, accepted: accepted === 'true', gaugeCode: null });
    }
  }
  return findings;
}

export function resultsSheet(characteristics, sample, gauges = []) {
  const rows = [];
  for (const c of characteristics) for (let s = 1; s <= sample; s++) rows.push(`<tr><td>${s === 1 ? `<b>${esc(c.number)}. ${esc(c.name)}</b><div class="faint">${c.kind === 'Variable' ? `${c.lower ?? '…'} to ${c.upper ?? '…'} ${esc(c.unit ?? '')}` : 'Accept / reject'}</div>` : ''}</td><td class="num">${s}</td>
    <td>${c.kind === 'Variable' ? `<input class="input" type="number" step="any" aria-label="${esc(c.name)} unit ${s} measured" data-cell="${c.number}-${s}-measured">` : `<select class="select" aria-label="${esc(c.name)} unit ${s} result" data-cell="${c.number}-${s}-accepted"><option value="">—</option><option value="true">Accept</option><option value="false">Reject</option></select>`}</td>
    <td>${c.kind === 'Variable' ? `<input class="input mono" aria-label="${esc(c.name)} unit ${s} gauge" data-cell="${c.number}-${s}-gauge" list="acc-gauges">` : ''}</td></tr>`);
  return `<datalist id="acc-gauges">${gauges.map(g => `<option value="${esc(g)}">`).join('')}</datalist>
    <div class="table-wrap"><table class="table"><thead><tr><th>Characteristic</th><th>Unit</th><th>Result</th><th>Gauge</th></tr></thead><tbody>${rows.join('')}</tbody></table></div>`;
}

const subjectText = s => { const key = s?.key ?? s; return key ? `${key.partNumber} lot ${key.lotNumber} at ${key.locationCode}${key.returnCohortId ? ` (return cohort ${key.returnCohortId})` : ''}` : '—'; };

export async function acceptancePage(page, ctx) {
  const me = ctx.me;
  const can = { read: canAction(me, 'quality.read'), plan: canAction(me, 'quality.plan'), approve: canAction(me, 'quality.approve'), inspect: canAction(me, 'quality.inspect'), release: canAction(me, 'quality.product.release'), consume: canAction(me, 'inventory.transact') };
  let tab = ctx.query.get('tab') ?? 'criteria';
  let criteria = { data: [] }, inspections = { data: [] }, releases = { data: [] }, gauges = [], sheet = null, found = null, disposed = false;

  async function load() {
    const [c, tools, i, r] = await Promise.all([
      tryGet(`${API}/criteria`), tryGet('/api/resources/tools'),
      can.read ? tryGet(`${API}/inspections?take=30`) : Promise.resolve({ data: [] }),
      can.read ? tryGet(`${API}/product-releases?take=30`) : Promise.resolve({ data: [] }),
    ]);
    criteria = c; inspections = i; releases = r; gauges = (tools.data ?? []).map(t => t.tool?.code ?? t.code).filter(Boolean);
  }
  const reload = async () => { await load(); if (!disposed) render(); };
  const effective = () => (criteria.data ?? []).filter(r => r.state === 'Effective');

  function render() {
    const tabs = [['criteria', 'Acceptance criteria'], ['inspect', 'Inspect'], ['release', 'Release'], ['lookup', 'Look up'], ...(can.read ? [['history', 'History']] : [])];
    page.innerHTML = `
      <div class="page-head"><div><h1>Product acceptance</h1><div class="sub">Inspect an exact part, lot and location against its effective criteria, then release an exact quantity. Withdrawal and consumption keep the decision's history.</div></div>
        <div class="spacer"></div>${can.plan ? '<button class="btn" id="new-criteria">New acceptance criteria</button>' : ''}</div>
      <section class="panel"><header><div class="seg">${tabs.map(([k, l]) => `<button data-tab="${k}" class="${tab === k ? 'on' : ''}">${esc(l)}</button>`).join('')}</div></header>
        ${{ criteria: criteriaTab, inspect: inspectTab, release: releaseTab, lookup: lookupTab, history: historyTab }[tab]?.() ?? criteriaTab()}</section>`;
    $$('[data-tab]', page).forEach(b => b.addEventListener('click', async () => { tab = b.dataset.tab; if (tab === 'history') await load(); render(); }));
    $('#new-criteria', page)?.addEventListener('click', () => defineCriteria(null));
    $$('[data-crit-edit]', page).forEach(b => b.addEventListener('click', () => editCriteria(rev(b.dataset.critEdit))));
    $$('[data-crit-next]', page).forEach(b => b.addEventListener('click', () => defineCriteria(rev(b.dataset.critNext))));
    $$('[data-crit-act]', page).forEach(b => b.addEventListener('click', () => transition(rev(b.dataset.crit), b.dataset.critAct)));
    $('#build-sheet', page)?.addEventListener('click', buildSheet);
    $('#record-inspection', page)?.addEventListener('click', recordInspection);
    $('#release-form', page)?.addEventListener('submit', e => { e.preventDefault(); release(); });
    $('#lookup-form', page)?.addEventListener('submit', e => { e.preventDefault(); lookup($('#lookup-kind', page).value, $('#lookup-id', page).value); });
    $('#withdraw', page)?.addEventListener('click', withdraw);
    $('#consume', page)?.addEventListener('click', consume);
    $('#release-this', page)?.addEventListener('click', () => { tab = 'release'; render(); });
    $('#inspect-cohort', page)?.addEventListener('click', () => {
      const d = found?.data;
      if (!d) return;
      const subject = d.subject?.key ?? d.subject ?? {};
      const cohortId = d.cohortId ?? subject.returnCohortId ?? found.id;
      tab = 'inspect'; render();
      $('#i-part', page).value = subject.partNumber ?? '';
      $('#i-lot', page).value = subject.lotNumber ?? '';
      $('#i-loc', page).value = subject.locationCode ?? '';
      $('#i-cohort', page).value = cohortId ?? '';
      $('#i-qty', page).value = d.quantity ?? '';
    });
    $$('[data-inspection-id]', page).forEach(b => b.addEventListener('click', () => lookup('inspection', b.dataset.inspectionId)));
    $$('[data-release-id]', page).forEach(b => b.addEventListener('click', () => lookup('release', b.dataset.releaseId)));
  }

  const rev = key => (criteria.data ?? []).find(r => `${r.code}/${r.revision}` === key);

  function criteriaTab() {
    if (criteria.error) return errorBlock(criteria.error, 'acceptance criteria');
    const list = criteria.data ?? [];
    if (!list.length) return emptyBlock('No acceptance criteria', 'Define criteria for a part revision; they take effect after independent approval.');
    const act = (r, action, text, ok) => ok ? `<button class="btn small ghost" data-crit="${esc(r.code)}/${r.revision}" data-crit-act="${action}">${text}</button>` : '';
    const latest = new Map();
    list.forEach(r => latest.set(r.code, Math.max(latest.get(r.code) ?? 0, r.revision)));
    return `<div class="table-wrap"><table class="table"><thead><tr><th>Criteria</th><th>Part</th><th>Coverage</th><th>Characteristics</th><th>State</th><th>By</th><th></th></tr></thead><tbody>
      ${list.map(r => { const c = r.content, key = `${esc(r.code)}/${r.revision}`; return `<tr><td><b class="mono">${esc(r.code)}</b> rev ${esc(r.revision)}<div class="faint">${esc(c.title)} · ${esc(r.facilityCode)}</div></td>
        <td>${esc(c.partNumber)} rev ${esc(c.revision)}${c.operationSequence ? ` · op ${esc(c.operationSequence)}` : ' · final'}</td>
        <td>${c.coverage.kind === 'FullQuantity' ? 'Every unit' : `Sample ≥ ${qty(c.coverage.minimumSampleQuantity)}`}<div class="faint">up to ${qty(c.coverage.maximumRepresentedQuantity)} per inspection</div></td>
        <td>${(c.characteristics ?? []).map(x => esc(x.name)).join(', ')}</td><td>${pill(r.state, STATE_TONE[r.state])}${r.effectiveFrom ? `<div class="faint">from ${esc(fmtDateTime(r.effectiveFrom))}</div>` : ''}</td>
        <td>${esc(who(r.createdBy))}${r.approvedBy ? `<div class="faint">approved by ${esc(who(r.approvedBy))}</div>` : ''}</td>
        <td class="row-actions">${r.state === 'Draft' && can.plan ? `<button class="btn small" data-crit-edit="${key}">Edit</button>` : ''}
          ${act(r, 'submit', 'Submit', r.state === 'Draft' && can.plan)}${act(r, 'approve', 'Approve', r.state === 'Submitted' && can.approve)}
          ${act(r, 'effective', 'Make effective', r.state === 'Approved' && can.approve)}${act(r, 'withdraw', 'Withdraw', ['Draft', 'Submitted', 'Approved'].includes(r.state) && can.approve)}
          ${(r.state === 'Effective' || r.state === 'Withdrawn') && r.revision === latest.get(r.code) && can.plan ? `<button class="btn small ghost" data-crit-next="${key}">New revision</button>` : ''}</td></tr>`; }).join('')}
      </tbody></table></div>`;
  }

  function historyTab() {
    const inspectionList = inspections.data ?? [], releaseList = releases.data ?? [];
    return `<div class="notice">Only records you can read under their current source, marking and adopted-policy controls are listed. Select a record to review its full history.</div>
      <section class="panel"><header><h2>Recent inspections</h2></header>
        ${inspections.error ? errorBlock(inspections.error, 'acceptance inspections') : !inspectionList.length ? emptyBlock('No readable inspections', 'Inspections appear here after they are recorded.') : `<div class="table-wrap"><table class="table"><thead><tr><th>Inspection</th><th>Subject</th><th>Result</th><th>Quantity</th><th>Inspector</th><th>Recorded</th><th></th></tr></thead><tbody>
          ${inspectionList.map(i => `<tr><td class="mono">${esc(i.inspectionId)}</td><td>${esc(subjectText(i.subject))}</td><td>${pill(i.passed ? 'Passed' : 'Failed', i.passed ? 'ok' : 'bad')}</td><td class="num">${qty(i.quantity)}</td><td>${esc(who(i.inspector))}</td><td>${esc(fmtDateTime(i.at))}</td><td><button class="btn small ghost" data-inspection-id="${esc(i.inspectionId)}">Open</button></td></tr>`).join('')}</tbody></table></div>`}
      </section>
      <section class="panel"><header><h2>Recent product releases</h2></header>
        ${releases.error ? errorBlock(releases.error, 'product releases') : !releaseList.length ? emptyBlock('No readable releases', 'Independent product release decisions appear here after approval.') : `<div class="table-wrap"><table class="table"><thead><tr><th>Decision</th><th>Subject</th><th>Quantity</th><th>Approved by</th><th>Approved</th><th></th></tr></thead><tbody>
          ${releaseList.map(d => `<tr><td class="mono">${esc(d.decisionId)}</td><td>${esc(subjectText(d.subject))}</td><td class="num">${qty(d.quantity)}</td><td>${esc(who(d.approvedBy))}</td><td>${esc(fmtDateTime(d.approvedAt))}</td><td><button class="btn small ghost" data-release-id="${esc(d.decisionId)}">Open</button></td></tr>`).join('')}</tbody></table></div>`}
      </section>`;
  }

  function inspectTab() {
    if (!can.inspect) return emptyBlock('No inspection authority', 'Recording an acceptance inspection needs quality inspection authority.');
    const options = effective().map(r => `<option value="${esc(r.code)}/${r.revision}">${esc(r.code)} rev ${r.revision} · ${esc(r.content.partNumber)} rev ${esc(r.content.revision)}</option>`).join('');
    return `<div class="form-grid">
      <div class="field"><label for="i-part">Part</label><input class="input mono" id="i-part"></div>
      <div class="field"><label for="i-lot">Lot or serial</label><input class="input mono" id="i-lot"></div>
      <div class="field"><label for="i-loc">Location</label><input class="input mono" id="i-loc"></div>
      <div class="field"><label for="i-cohort">Return cohort (returned product only)</label><input class="input" type="number" min="1" id="i-cohort"></div>
      <div class="field"><label for="i-criteria">Effective criteria</label><select class="select" id="i-criteria"><option value="">— choose —</option>${options}</select></div>
      <div class="field"><label for="i-qty">Quantity represented</label><input class="input" type="number" min="0" step="any" id="i-qty"></div>
      <div class="field"><label for="i-sample">Units inspected</label><input class="input" type="number" min="1" step="1" id="i-sample"></div>
    </div><div class="actions"><button class="btn" id="build-sheet">Prepare results sheet</button></div>
    ${sheet ? `<h3>Results: ${esc(sheet.criteria.code)} rev ${esc(sheet.criteria.revision)}, ${esc(sheet.sample)} unit(s)</h3>${resultsSheet(sheet.criteria.content.characteristics, sheet.sample, gauges)}
      <div class="form-grid"><div class="field"><label for="i-evidence">Evidence</label><textarea class="input" rows="2" id="i-evidence"></textarea></div>
        <div class="field"><label for="i-quarantine">Quarantine location if it fails (optional)</label><input class="input mono" id="i-quarantine"></div></div>
      <div class="actions"><button class="btn primary" id="record-inspection">Record inspection</button></div>` : ''}
    ${!effective().length ? '<div class="notice">No criteria are effective yet. An inspection needs effective criteria for the exact part revision.</div>' : ''}`;
  }

  function buildSheet() {
    const chosen = rev($('#i-criteria', page).value);
    const subject = { partNumber: $('#i-part', page).value.trim().toUpperCase(), lotNumber: $('#i-lot', page).value.trim().toUpperCase(), locationCode: $('#i-loc', page).value.trim().toUpperCase() };
    const cohort = Number($('#i-cohort', page).value) || null, quantity = Number($('#i-qty', page).value), sample = Number($('#i-sample', page).value);
    if (!subject.partNumber || !subject.lotNumber || !subject.locationCode) return toast('Identify the exact part, lot and location.', true);
    if (!chosen) return toast('Choose the effective criteria.', true);
    if (!(quantity > 0) || !Number.isSafeInteger(sample) || sample < 1 || sample > quantity) return toast('Enter the quantity and a whole number of inspected units no larger than it.', true);
    if (cohort) subject.returnCohortId = cohort;
    sheet = { subject, criteria: chosen, quantity, sample };
    const keep = { part: subject.partNumber, lot: subject.lotNumber, loc: subject.locationCode, cohort: cohort ?? '', criteria: $('#i-criteria', page).value, qty: quantity, sample };
    render();
    for (const [id, value] of Object.entries(keep)) { const el = $(`#i-${id}`, page); if (el) el.value = value; }
  }

  async function recordInspection() {
    let body;
    try {
      body = { subject: sheet.subject, criteriaCode: sheet.criteria.code, criteriaRevision: sheet.criteria.revision, expectedCriteriaVersion: sheet.criteria.version,
        quantity: sheet.quantity, sampleQuantity: sheet.sample, findings: readFindings(page, sheet.criteria.content.characteristics, sheet.sample),
        evidence: $('#i-evidence', page).value.trim(), quarantineLocation: $('#i-quarantine', page).value.trim().toUpperCase() || null };
      if (!body.evidence) throw new Error('Record the inspection evidence.');
    } catch (e) { toast(e.message, true); return; }
    const result = await confirmWithReason({ title: 'Record acceptance inspection', detail: `${subjectText(sheet.subject)}: ${qty(sheet.quantity)} represented by ${sheet.sample} inspected.`, confirmLabel: 'Record' },
      reason => command(`${API}/inspections`, body, reason));
    if (!result) return;
    toast(result.message ?? 'Inspection recorded.');
    sheet = null; tab = 'lookup'; await lookup('inspection', result.entityId);
  }

  function releaseTab() {
    if (!can.release) return emptyBlock('No release authority', 'Product release needs the quality product-release permission.');
    const s = found?.kind === 'inspection' ? found.data.subject.key : null;
    return `<form id="release-form"><div class="form-grid">
      <div class="field"><label for="r-part">Part</label><input class="input mono" id="r-part" value="${esc(s?.partNumber ?? '')}"></div>
      <div class="field"><label for="r-lot">Lot or serial</label><input class="input mono" id="r-lot" value="${esc(s?.lotNumber ?? '')}"></div>
      <div class="field"><label for="r-loc">Location</label><input class="input mono" id="r-loc" value="${esc(s?.locationCode ?? '')}"></div>
      <div class="field"><label for="r-cohort">Return cohort (returned product only)</label><input class="input" type="number" min="1" id="r-cohort" value="${esc(s?.returnCohortId ?? '')}"></div>
      <div class="field"><label for="r-qty">Quantity to release</label><input class="input" type="number" min="0" step="any" id="r-qty" value="${esc(found?.kind === 'inspection' ? found.data.quantity : '')}"></div>
      <div class="field"><label for="r-inspections">Passed inspection numbers</label><input class="input" id="r-inspections" placeholder="e.g. 41, 42" value="${esc(found?.kind === 'inspection' ? found.data.inspectionId : '')}"></div>
      <div class="field"><label for="r-version">Inspection record version</label><input class="input" type="number" min="1" id="r-version" value="${esc(found?.kind === 'inspection' ? found.data.version : 1)}"></div>
      <div class="field"><label for="r-basis">Basis for release</label><textarea class="input" rows="2" id="r-basis"></textarea></div>
    </div><div class="actions"><button class="btn primary" type="submit">Release exact quantity</button></div></form>`;
  }

  async function release() {
    const ids = $('#r-inspections', page).value.split(/[\s,]+/).filter(Boolean).map(Number);
    const subject = { partNumber: $('#r-part', page).value.trim().toUpperCase(), lotNumber: $('#r-lot', page).value.trim().toUpperCase(), locationCode: $('#r-loc', page).value.trim().toUpperCase() };
    const cohort = Number($('#r-cohort', page).value) || null; if (cohort) subject.returnCohortId = cohort;
    const body = { subject, quantity: Number($('#r-qty', page).value), inspectionIds: ids, expectedInspectionVersion: Number($('#r-version', page).value) || 1, basis: $('#r-basis', page).value.trim() };
    if (!subject.partNumber || !subject.lotNumber || !subject.locationCode || !(body.quantity > 0) || !ids.length || ids.some(id => !Number.isSafeInteger(id) || id <= 0) || !body.basis)
      return toast('Give the exact subject, a quantity, the passed inspection numbers and the basis.', true);
    const result = await confirmWithReason({ title: 'Release product', detail: `${qty(body.quantity)} of ${subjectText(subject)}, backed by inspection ${ids.join(', ')}.`, confirmLabel: 'Release' },
      reason => command(`${API}/product-releases`, body, reason));
    if (!result) return;
    toast(result.message ?? 'Released.'); tab = 'lookup'; await lookup('release', result.entityId);
  }

  function lookupTab() {
    return `<form id="lookup-form" class="toolbar"><select class="select" id="lookup-kind" aria-label="Record type">
        <option value="inspection" ${found?.kind === 'inspection' ? 'selected' : ''}>Acceptance inspection</option><option value="release" ${found?.kind === 'release' ? 'selected' : ''}>Product release</option><option value="cohort" ${found?.kind === 'cohort' ? 'selected' : ''}>Return cohort</option></select>
      <input class="input" id="lookup-id" type="number" min="1" aria-label="Number" value="${esc(found?.id ?? '')}"><button class="btn" type="submit">Look up</button></form>
      ${found ? found.error ? errorBlock(found.error, `${found.kind} ${found.id}`) : foundHtml() : '<div class="muted">Enter the number of an inspection, release or return cohort.</div>'}`;
  }

  function foundHtml() {
    const d = found.data;
    if (found.kind === 'inspection') return `<dl class="kv"><dt>Inspection</dt><dd>${esc(d.inspectionId)} ${pill(d.passed ? 'Passed' : 'Failed', d.passed ? 'ok' : 'bad')}</dd>
      <dt>Subject</dt><dd>${esc(subjectText(d.subject.key))} · rev ${esc(d.subject.revision)}</dd><dt>Criteria</dt><dd>${esc(d.criteriaCode)} rev ${esc(d.criteriaRevision)}</dd>
      <dt>Quantity</dt><dd>${qty(d.quantity)} represented by ${qty(d.sampleQuantity)} inspected</dd><dt>Inspector</dt><dd>${esc(who(d.inspector))}, ${esc(fmtDateTime(d.at))}</dd>
      ${d.ncrId ? `<dt>NCR</dt><dd>${esc(d.ncrId)}</dd>` : ''}<dt>Evidence</dt><dd>${esc(d.evidence)}</dd></dl>
      <div class="table-wrap"><table class="table"><thead><tr><th>Characteristic</th><th>Unit</th><th>Result</th><th>Gauge</th><th></th></tr></thead><tbody>
      ${(d.results ?? []).map(r => `<tr><td>${esc(r.criterion.name)}</td><td class="num">${esc(r.sampleNumber)}</td><td>${r.measured != null ? `${qty(r.measured, 6)} ${esc(r.criterion.unit ?? '')}` : r.accepted ? 'Accept' : 'Reject'}</td><td>${esc(r.gaugeCode ?? '')}</td><td>${pill(r.passed ? 'Pass' : 'Fail', r.passed ? 'ok' : 'bad')}</td></tr>`).join('')}</tbody></table></div>
      ${d.passed && can.release ? '<div class="actions"><button class="btn primary" id="release-this">Release from this inspection</button></div>' : ''}`;
    if (found.kind === 'release') return `<dl class="kv"><dt>Release decision</dt><dd>${esc(d.decisionId)} (version ${esc(d.version)})</dd><dt>Subject</dt><dd>${esc(subjectText(d.subject))} · rev ${esc(d.revision)}</dd>
      <dt>Quantity</dt><dd>${qty(d.quantity)}</dd><dt>Inspections</dt><dd>${(d.inspectionIds ?? []).map(esc).join(', ')}</dd><dt>Approved</dt><dd>${esc(who(d.approvedBy))}, ${esc(fmtDateTime(d.approvedAt))}</dd><dt>Basis</dt><dd>${esc(d.basis)}</dd></dl>
      <div class="actions">${can.consume ? '<button class="btn" id="consume">Consume quantity</button>' : ''}${can.release ? '<button class="btn" id="withdraw">Withdraw release</button>' : ''}</div>`;
    const subject = d.subject?.key ?? d.subject ?? {};
    const cohortId = d.cohortId ?? subject.returnCohortId ?? found.id;
    return `<dl class="kv"><dt>Return cohort</dt><dd>#${esc(cohortId)} (version ${esc(d.version ?? 1)})</dd>
      <dt>Customer return (RMA)</dt><dd>${d.rmaId != null ? `<a href="#/receiving">RMA #${esc(d.rmaId)}</a>` : '—'}</dd>
      <dt>Subject</dt><dd>${esc(subjectText(subject))} · rev ${esc(d.revision ?? d.subject?.revision ?? '—')}</dd>
      <dt>Prepared quantity</dt><dd>${qty(d.quantity)}</dd>
      ${d.preparedBy || d.preparedAt ? `<dt>Prepared</dt><dd>${esc(who(d.preparedBy ?? ''))}${d.preparedAt ? `, ${esc(fmtDateTime(d.preparedAt))}` : ''}</dd>` : ''}
      ${d.consumedQuantity != null ? `<dt>Consumed / restocked</dt><dd>${qty(d.consumedQuantity)}</dd>` : ''}
    </dl>${can.inspect ? '<div class="actions"><button class="btn primary" id="inspect-cohort" type="button">Inspect this return cohort</button></div>' : ''}`;
  }

  async function lookup(kind, id) {
    const n = Number(id);
    if (!Number.isSafeInteger(n) || n <= 0) { toast('Enter a record number.', true); return; }
    const path = { inspection: `${API}/inspections/${n}`, release: `${API}/product-releases/${n}`, cohort: `${API}/return-cohorts/${n}` }[kind];
    const res = await tryGet(path);
    found = { kind, id: n, ...res };
    if (!disposed) render();
  }

  function withdraw() {
    const d = found.data;
    formModal({ title: `Withdraw release ${d.decisionId}`, detail: 'The released quantity is no longer available to move on. The decision and its history are kept.', fields: [{ name: 'basis', label: 'Basis', type: 'textarea', required: true }], confirmLabel: 'Withdraw', reasonRequired: true },
      (v, reason) => command(`${API}/product-releases/${d.decisionId}/withdraw`, { expectedVersion: d.version, basis: v.basis }, reason), () => lookup('release', d.decisionId));
  }

  function consume() {
    const d = found.data;
    formModal({ title: `Consume from release ${d.decisionId}`, detail: 'Records released quantity being used or shipped. It cannot exceed what remains released.', fields: [{ name: 'quantity', label: 'Quantity', type: 'number', step: 'any', min: 0, required: true }], confirmLabel: 'Consume', reasonRequired: true },
      (v, reason) => command(`/api/inventory/product-releases/${d.decisionId}/consume`, { expectedVersion: d.version, quantity: v.quantity }, reason), () => lookup('release', d.decisionId));
  }

  function defineCriteria(previous) {
    const fields = criteriaFields(previous, !!previous);
    formModal({ title: previous ? `New revision of ${previous.code}` : 'New acceptance criteria', detail: 'A draft until submitted, independently approved and made effective. Scope (facility, program, markings) cannot change between revisions.', fields, confirmLabel: 'Save draft', reasonRequired: true },
      (v, reason) => command(`${API}/criteria`, { code: previous?.code ?? v.code, facilityCode: previous?.facilityCode ?? v.facilityCode, programId: previous?.programId ?? null, markings: previous?.markings ?? v.markings,
        content: criteriaContent(v), expectedVersion: previous ? previous.version : null }, reason), reload);
  }

  function editCriteria(r) {
    formModal({ title: `Edit ${r.code} rev ${r.revision}`, fields: criteriaFields(r, true), confirmLabel: 'Save draft', reasonRequired: true },
      (v, reason) => command(`${API}/criteria/${encodeURIComponent(r.code)}/revisions/${r.revision}/edit`, { expectedVersion: r.version, content: criteriaContent(v) }, reason), reload);
  }

  function transition(r, action) {
    const words = { submit: 'Submit for approval', approve: 'Approve', effective: 'Make effective', withdraw: 'Withdraw' }[action];
    formModal({ title: `${words}: ${r.code} rev ${r.revision}`, detail: action === 'approve' ? 'Someone who did not write or edit these criteria approves them.' : '',
      fields: [{ name: 'note', label: action === 'effective' ? 'Applicability basis' : 'Basis', type: 'textarea', required: true }], confirmLabel: words, reasonRequired: true },
    (v, reason) => command(`${API}/criteria/${encodeURIComponent(r.code)}/revisions/${r.revision}/${action}`, { expectedVersion: r.version, note: v.note }, reason), reload);
  }

  await load();
  if (ctx.query.get('inspection')) await lookup('inspection', ctx.query.get('inspection'));
  if (!disposed) render();
  return { dispose() { disposed = true; } };
}
