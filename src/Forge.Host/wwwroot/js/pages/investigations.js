import { tryGet, command } from '../api.js';
import { who, loadDirectory } from '../people.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock } from '../ui.js';
import { formModal, qty, canAction } from '../forms.js';

// Investigations (V4.2): measurement findings against a measuring resource, and trace investigations that
// capture an immutable snapshot of every recorded path from a supplier lot or an approved finding. People
// decide the impact on each candidate; Forge records the decision, its basis and any hold it is linked to.

const API = '/api/quality/investigations';
const FINDING_TONE = { Draft: 'info', Submitted: 'warn', Approved: 'ok', Withdrawn: 'none' };
const DECISIONS = [['PotentiallyAffected', 'Potentially affected'], ['NotAffected', 'Not affected'], ['Undetermined', 'Undetermined']];
const label = value => String(value ?? '').replace(/([a-z])([A-Z])/g, '$1 $2');
const pill = (text, tone = 'none') => `<span class="pill ${tone}">${esc(label(text))}</span>`;

/** A datetime-local value (browser time) as an exact UTC instant; blank stays unknown. */
export function instant(value, required = false) {
  if (!value) { if (required) throw new Error('Record an exact date and time.'); return null; }
  const at = new Date(value);
  if (!Number.isFinite(at.getTime())) throw new Error('Enter a valid date and time.');
  return at.toISOString();
}

const local = iso => {
  if (!iso) return '';
  const d = new Date(iso), p = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
};

export function findingFields(content = {}, tools = []) {
  const range = content.affectedRange ?? {};
  return [
    { name: 'toolCode', label: 'Measuring resource (tool or gauge code)', value: content.toolCode, code: true, required: true, list: tools },
    { name: 'calibrationEventId', label: 'Calibration event number (optional)', type: 'number', min: 1, value: content.calibrationEventId, hint: 'The calibration that found the condition, when there is one.' },
    { name: 'discoveredAt', label: 'Discovered at', type: 'datetime-local', value: local(content.discoveredAt), required: true },
    { name: 'asFoundObservation', label: 'As-found observation', type: 'textarea', value: content.asFoundObservation, required: true },
    { name: 'suspectedFrom', label: 'Suspected from (blank: unknown)', type: 'datetime-local', value: local(content.suspectedFrom) },
    { name: 'suspectedTo', label: 'Suspected to (blank: unknown)', type: 'datetime-local', value: local(content.suspectedTo) },
    { name: 'intervalBasis', label: 'Basis for the suspected interval', type: 'textarea', value: content.intervalBasis, required: true },
    { name: 'characteristicName', label: 'Affected characteristic (optional)', value: range.characteristicName },
    { name: 'rangeFrom', label: 'Affected range from (optional)', type: 'number', step: 'any', value: range.from },
    { name: 'rangeTo', label: 'Affected range to (optional)', type: 'number', step: 'any', value: range.to },
    { name: 'unit', label: 'Range unit', value: range.unit },
    { name: 'rangeBasis', label: 'Basis for the affected range', type: 'textarea', value: range.basis, hint: 'Required when any range detail is given.' },
    { name: 'evidence', label: 'Evidence', type: 'textarea', value: content.evidence, required: true },
  ];
}

/** Form values to MeasurementFindingContent. Unknown bounds stay null; a range is sent only when declared. */
export function findingContent(v) {
  const declared = v.characteristicName || v.rangeFrom != null || v.rangeTo != null || v.unit || v.rangeBasis;
  if (declared && !v.rangeBasis) throw new Error('Give the basis for the affected range.');
  return {
    toolCode: v.toolCode, calibrationEventId: v.calibrationEventId ?? null,
    discoveredAt: instant(v.discoveredAt, true), asFoundObservation: v.asFoundObservation,
    affectedRange: declared ? { characteristicName: v.characteristicName, unit: v.unit, from: v.rangeFrom, to: v.rangeTo, basis: v.rangeBasis } : null,
    suspectedFrom: instant(v.suspectedFrom), suspectedTo: instant(v.suspectedTo),
    intervalBasis: v.intervalBasis, evidence: v.evidence,
  };
}

/** Form values to a trace root: either an exact supplier lot or an approved finding, never both. */
export function traceRoot(v) {
  if (v.kind === 'MeasurementFinding') {
    if (!v.findingId) throw new Error('Choose the approved measurement finding.');
    return { kind: 'MeasurementFinding', partNumber: null, lotNumber: null, findingId: Number(v.findingId) };
  }
  if (!v.partNumber || !v.lotNumber) throw new Error('Identify the exact supplier part and lot.');
  return { kind: 'SupplierLot', partNumber: v.partNumber, lotNumber: v.lotNumber, findingId: null };
}

export const rootText = root => root.kind === 'SupplierLot' ? `Supplier lot ${root.partNumber} ${root.lotNumber}` : `Measurement finding ${root.findingId}`;

/** What the snapshot does and does not know. Completeness is never implied. */
export function completenessNotes(c) {
  if (!c) return '';
  const facts = [
    [c.recordedTraversalComplete, 'Every recorded path was followed', 'Recorded paths were not all followed'],
    [c.intervalKnown, 'The suspect interval is known', 'The suspect interval is not fully known'],
    [c.physicalCoverageKnown, 'Physical coverage is known', 'Physical coverage beyond the records is unknown'],
    [!c.restrictedSources, 'No sources were withheld from you', 'Some sources are restricted for you'],
    [!c.concurrentSnapshotUncertain, 'The snapshot is consistent', 'Records changed while the snapshot was taken'],
  ];
  return `<ul class="evidence">${facts.map(([ok, yes, no]) => `<li class="${ok ? 'c-ok' : ''}">${esc(ok ? yes : no)}</li>`).join('')}
    ${(c.gaps ?? []).map(g => `<li><b>${esc(g.code)}</b> ${esc(g.description)}</li>`).join('')}</ul>`;
}

const candidateWhere = c => [c.partNumber && `${c.partNumber}${c.revision ? ` rev ${c.revision}` : ''}`, c.lotNumber && `lot ${c.lotNumber}`,
  c.orderNumber && `order ${c.orderNumber}${c.sequence ? `/${c.sequence}` : ''}`, c.shipmentId && `shipment ${c.shipmentId}`,
  c.customer && `for ${c.customer}`, c.locationCode && `at ${c.locationCode}`].filter(Boolean).join(' · ');

export function candidateRows(view, can = {}) {
  const candidates = view?.snapshot?.capture?.candidates ?? [];
  if (!candidates.length) return `<tr><td colspan="5" class="muted">The snapshot found no candidates.</td></tr>`;
  const open = view.header.state === 'Open' && view.snapshot.number === view.header.latestSnapshot;
  return candidates.map(c => {
    const assessments = (view.assessments ?? []).filter(a => a.candidateKey === c.key);
    const live = assessments.find(a => a.state !== 'Withdrawn');
    const links = (view.holdLinks ?? []).filter(l => l.candidateKey === c.key);
    return `<tr>
      <td><b>${esc(label(c.category))}</b><div class="faint">${esc(c.recordType)} ${esc(c.recordId)}</div></td>
      <td>${esc(candidateWhere(c)) || '—'}${c.quantity != null ? `<div class="faint">${qty(c.quantity)} ${esc(c.unit ?? '')}${c.status ? ` · ${esc(c.status)}` : ''}</div>` : ''}
        <div class="faint">${(c.paths ?? []).map(p => esc(`${p.relationship}: ${p.basis}`)).join('<br>')}</div></td>
      <td>${assessments.map(a => `<div>${pill(a.decision, a.decision === 'PotentiallyAffected' ? 'bad' : a.decision === 'NotAffected' ? 'ok' : 'warn')} ${pill(a.state)}
          <div class="faint">${esc(a.basis)} · planned: ${esc(a.plannedDisposition)} · ${esc(who(a.proposedBy))}${a.approvedBy ? `, approved by ${esc(who(a.approvedBy))}` : ''}</div>
          ${open && a.state === 'Proposed' ? `${can.approve ? `<button class="btn small" data-assess-approve="${a.assessmentId}" data-version="${a.version}">Approve</button>` : ''}${can.disposition ? `<button class="btn small ghost" data-assess-withdraw="${a.assessmentId}" data-version="${a.version}">Withdraw</button>` : ''}` : ''}</div>`).join('') || '<span class="muted">Not assessed</span>'}</td>
      <td>${links.map(l => `<div>Hold ${esc(l.holdId)} <span class="faint">${esc(l.basis)}</span></div>`).join('') || '—'}</td>
      <td class="row-actions">${open && can.disposition && !live ? `<button class="btn small" data-propose="${esc(c.key)}">Assess</button>` : ''}
        ${open && can.disposition && live?.state === 'Approved' && (c.holdTargets ?? []).length ? `<button class="btn small" data-link="${live.assessmentId}" data-version="${live.version}" data-key="${esc(c.key)}">Link hold</button>` : ''}</td>
    </tr>`;
  }).join('');
}

export async function investigationsPage(page, ctx) {
  const me = ctx.me;
  const can = { plan: canAction(me, 'quality.plan'), disposition: canAction(me, 'quality.disposition'), approve: canAction(me, 'quality.approve') };
  let tab = ctx.query.get('tab') ?? 'trace';
  let selected = ctx.query.get('id') ? Number(ctx.query.get('id')) : null, snapshot = null;
  let data = {}, view = null, disposed = false;

  async function load() {
    const [cases, findings, tools] = await Promise.all([tryGet(`${API}/trace`), tryGet(`${API}/measurement-findings`), tryGet('/api/resources/tools')]);
    data = { cases, findings, tools: (tools.data ?? []).map(t => t.code ?? t.tool?.code).filter(Boolean) };
    view = selected ? await tryGet(`${API}/trace/${selected}${snapshot ? `?snapshot=${snapshot}` : ''}`) : null;
    await loadDirectory();
  }
  const reload = async () => { await load(); if (!disposed) render(); };

  function render() {
    const tabs = [['trace', 'Trace investigations'], ['findings', 'Measurement findings']];
    page.innerHTML = `
      <div class="page-head">
        <div><h1>Investigations</h1><div class="sub">Trace a suspect supplier lot or measuring resource through every recorded use. Each snapshot is kept; people decide the impact on each candidate.</div></div>
        <div class="spacer"></div>
        ${can.disposition ? '<button class="btn" id="open-trace">Open trace investigation</button>' : ''}
        ${can.plan ? '<button class="btn" id="new-finding">Record measurement finding</button>' : ''}
      </div>
      <section class="panel">
        <header><div class="seg">${tabs.map(([k, l]) => `<button data-tab="${k}" class="${tab === k ? 'on' : ''}">${esc(l)}</button>`).join('')}</div></header>
        ${tab === 'findings' ? findingsTab() : traceTab()}
      </section>
      ${tab === 'trace' && selected ? detail() : ''}`;
    $$('[data-tab]', page).forEach(b => b.addEventListener('click', () => { tab = b.dataset.tab; render(); }));
    $('#open-trace', page)?.addEventListener('click', () => openTrace());
    $('#new-finding', page)?.addEventListener('click', () => editFinding(null));
    $$('[data-case]', page).forEach(b => b.addEventListener('click', async () => { selected = Number(b.dataset.case); snapshot = null; await reload(); }));
    $$('[data-finding-edit]', page).forEach(b => b.addEventListener('click', () => editFinding(find(b.dataset.findingEdit))));
    $$('[data-finding-act]', page).forEach(b => b.addEventListener('click', () => transitionFinding(find(b.dataset.findingId), b.dataset.findingAct)));
    $$('[data-trace-finding]', page).forEach(b => b.addEventListener('click', () => openTrace({ kind: 'MeasurementFinding', findingId: b.dataset.traceFinding })));
    $('#snapshot', page)?.addEventListener('change', async e => { snapshot = Number(e.target.value) || null; await reload(); });
    $('#refresh', page)?.addEventListener('click', refresh);
    $('#close-case', page)?.addEventListener('click', closeCase);
    $$('[data-propose]', page).forEach(b => b.addEventListener('click', () => propose(b.dataset.propose)));
    $$('[data-assess-approve]', page).forEach(b => b.addEventListener('click', () => assessmentAction(b.dataset.assessApprove, b.dataset.version, 'approve')));
    $$('[data-assess-withdraw]', page).forEach(b => b.addEventListener('click', () => assessmentAction(b.dataset.assessWithdraw, b.dataset.version, 'withdraw')));
    $$('[data-link]', page).forEach(b => b.addEventListener('click', () => linkHold(Number(b.dataset.link), Number(b.dataset.version), b.dataset.key)));
  }

  const find = id => (data.findings.data ?? []).find(f => String(f.findingId) === String(id));

  function traceTab() {
    if (data.cases.error) return errorBlock(data.cases.error, 'trace investigations');
    const cases = data.cases.data ?? [];
    if (!cases.length) return emptyBlock('No trace investigations', 'Open one from a suspect supplier lot or an approved measurement finding.');
    return `<div class="table-wrap"><table class="table"><thead><tr><th>#</th><th>Title</th><th>Started from</th><th>State</th><th>Snapshots</th><th>Owner</th><th>Opened</th><th></th></tr></thead><tbody>
      ${cases.map(c => `<tr class="${c.investigationId === selected ? 'on' : ''}"><td>${esc(c.investigationId)}</td><td><b>${esc(c.title)}</b></td><td>${esc(rootText(c.root))}</td>
        <td>${pill(c.state, c.state === 'Open' ? 'warn' : 'ok')}</td><td class="num">${esc(c.latestSnapshot)}</td><td>${esc(who(`person:${c.ownerPersonId}`))}</td>
        <td>${esc(fmtDateTime(c.createdAt))}</td><td><button class="btn small" data-case="${c.investigationId}">View</button></td></tr>`).join('')}
      </tbody></table></div>`;
  }

  function detail() {
    if (!view) return '';
    if (view.error) return `<section class="panel">${errorBlock(view.error, `investigation ${selected}`)}</section>`;
    const v = view.data, h = v.header, s = v.snapshot, open = h.state === 'Open';
    return `<section class="panel" aria-label="Investigation ${esc(h.investigationId)}">
      <header><h2>${esc(h.title)}</h2><div class="spacer"></div>
        <label class="opt">Snapshot <select class="select" id="snapshot">${Array.from({ length: h.latestSnapshot }, (_, i) => i + 1).reverse().map(n => `<option value="${n}" ${n === s.number ? 'selected' : ''}>${n}${n === h.latestSnapshot ? ' (latest)' : ''}</option>`).join('')}</select></label>
        ${open && can.disposition ? '<button class="btn small" id="refresh">Take new snapshot</button><button class="btn small" id="close-case">Close</button>' : ''}</header>
      <dl class="kv"><dt>Started from</dt><dd>${esc(rootText(h.root))}</dd><dt>Trigger evidence</dt><dd>${v.narrativeRedacted ? '<span class="muted">Withheld: some sources are not available to you.</span>' : esc(h.triggerEvidence)}</dd>
        <dt>Snapshot</dt><dd>${esc(s.number)} by ${esc(who(s.recordedBy))}, ${esc(fmtDateTime(s.recordedAt))}; data to ${esc(fmtDateTime(s.capture.dataCutoff))}</dd></dl>
      <h3>What this snapshot knows</h3>${completenessNotes(s.capture.completeness)}
      <h3>Candidates</h3>
      <div class="table-wrap"><table class="table"><thead><tr><th>Candidate</th><th>Where and how it was reached</th><th>Impact decision</th><th>Holds</th><th></th></tr></thead>
        <tbody>${candidateRows(v, can)}</tbody></table></div>
      ${s.number !== h.latestSnapshot ? '<div class="notice">This is an earlier snapshot. Assess and link holds on the latest one.</div>' : ''}
    </section>`;
  }

  function findingsTab() {
    if (data.findings.error) return errorBlock(data.findings.error, 'measurement findings');
    const findings = data.findings.data ?? [];
    if (!findings.length) return emptyBlock('No measurement findings', 'Record one when a gauge or tool is found out of tolerance or damaged.');
    const action = (f, act, text, ok) => ok ? `<button class="btn small ghost" data-finding-act="${act}" data-finding-id="${f.findingId}">${text}</button>` : '';
    return `<div class="table-wrap"><table class="table"><thead><tr><th>#</th><th>Measuring resource</th><th>As found</th><th>Suspected interval</th><th>State</th><th>Recorded by</th><th></th></tr></thead><tbody>
      ${findings.map(f => { const c = f.content; return `<tr><td>${esc(f.findingId)}</td><td><b class="mono">${esc(c.toolCode)}</b>${c.calibrationEventId ? `<div class="faint">calibration ${esc(c.calibrationEventId)}</div>` : ''}</td>
        <td>${esc(c.asFoundObservation)}<div class="faint">discovered ${esc(fmtDateTime(c.discoveredAt))}</div></td>
        <td>${c.suspectedFrom ? esc(fmtDateTime(c.suspectedFrom)) : 'unknown'} → ${c.suspectedTo ? esc(fmtDateTime(c.suspectedTo)) : 'unknown'}<div class="faint">${esc(c.intervalBasis)}</div></td>
        <td>${pill(f.state, FINDING_TONE[f.state])}</td><td>${esc(who(f.createdBy))}${f.approvedBy ? `<div class="faint">approved by ${esc(who(f.approvedBy))}</div>` : ''}</td>
        <td class="row-actions">${f.state === 'Draft' && can.plan ? `<button class="btn small" data-finding-edit="${f.findingId}">Edit</button>` : ''}
          ${action(f, 'submit', 'Submit', f.state === 'Draft' && can.plan)}${action(f, 'approve', 'Approve', f.state === 'Submitted' && can.approve)}
          ${action(f, 'withdraw', 'Withdraw', (f.state === 'Draft' || f.state === 'Submitted') && can.disposition)}
          ${f.state === 'Approved' && can.disposition ? `<button class="btn small" data-trace-finding="${f.findingId}">Trace uses</button>` : ''}</td></tr>`; }).join('')}
      </tbody></table></div>`;
  }

  // ------------------------------------------------------------ actions

  function editFinding(finding) {
    const fields = findingFields(finding?.content ?? {}, data.tools);
    formModal({
      title: finding ? `Edit measurement finding ${finding.findingId}` : 'Record measurement finding',
      detail: 'State what was found and when. Leave a suspected bound blank when it is genuinely unknown; Forge will not guess it.',
      fields, confirmLabel: finding ? 'Save draft' : 'Record', reasonRequired: true,
    }, (v, reason) => finding
      ? command(`${API}/measurement-findings/${finding.findingId}/edit`, { expectedVersion: finding.version, content: findingContent(v) }, reason)
      : command(`${API}/measurement-findings`, { content: findingContent(v) }, reason), async () => { tab = 'findings'; await reload(); });
  }

  function transitionFinding(finding, act) {
    const words = { submit: ['Submit for approval', 'An independent quality approver reviews it next.'], approve: ['Approve finding', 'An approved finding can start a trace investigation.'], withdraw: ['Withdraw finding', 'The finding and its history are kept.'] }[act];
    formModal({ title: `${words[0]} ${finding.findingId}`, detail: words[1], fields: [], confirmLabel: words[0], reasonRequired: true },
      (_, reason) => command(`${API}/measurement-findings/${finding.findingId}/${act}`, { expectedVersion: finding.version }, reason), reload);
  }

  async function openTrace(preset = {}) {
    const names = await loadDirectory();
    const approved = (data.findings.data ?? []).filter(f => f.state === 'Approved');
    formModal({
      title: 'Open trace investigation',
      detail: 'Forge captures every recorded path from this starting point into a kept snapshot. Labels and guesses are not sources.',
      fields: [
        { name: 'kind', label: 'Start from', type: 'select', value: preset.kind ?? 'SupplierLot', options: [['SupplierLot', 'A supplier lot'], ['MeasurementFinding', 'An approved measurement finding']] },
        { name: 'partNumber', label: 'Supplier part (for a lot)', code: true },
        { name: 'lotNumber', label: 'Lot number (for a lot)' },
        { name: 'findingId', label: 'Measurement finding (for a finding)', type: 'select', value: preset.findingId ?? '', blank: '— choose —', options: approved.map(f => [f.findingId, `${f.findingId} · ${f.content.toolCode}`]) },
        { name: 'title', label: 'Title', required: true },
        { name: 'owner', label: 'Owner', type: 'select', value: me.personId, required: true, options: [...names].map(([id, name]) => [id, name]) },
        { name: 'triggerEvidence', label: 'What triggered it (evidence)', type: 'textarea', required: true },
      ],
      confirmLabel: 'Open and capture', reasonRequired: true,
    }, (v, reason) => command(`${API}/trace`, { root: traceRoot(v), title: v.title, ownerPersonId: v.owner, triggerEvidence: v.triggerEvidence }, reason),
    async result => { tab = 'trace'; selected = Number(result.entityId) || selected; snapshot = null; await reload(); });
  }

  function refresh() {
    const h = view.data.header;
    formModal({ title: 'Take new snapshot', detail: 'Captures current records again. Earlier snapshots and their decisions are kept.', fields: [], confirmLabel: 'Capture', reasonRequired: true },
      (_, reason) => command(`${API}/trace/${h.investigationId}/refresh`, { expectedVersion: h.version }, reason), async () => { snapshot = null; await reload(); });
  }

  function closeCase() {
    const h = view.data.header, gaps = (view.data.snapshot.capture.completeness.gaps ?? []).length;
    formModal({
      title: `Close investigation ${h.investigationId}`, detail: gaps ? `The latest snapshot records ${gaps} gap(s). Acknowledge them to close.` : 'Record why the investigation is complete.',
      fields: [{ name: 'acknowledgedGaps', label: 'Gaps', type: 'check', text: 'I acknowledge the recorded gaps and unknowns' }, { name: 'basis', label: 'Basis for closing', type: 'textarea', required: true }],
      confirmLabel: 'Close investigation', reasonRequired: true,
    }, (v, reason) => command(`${API}/trace/${h.investigationId}/close`, { expectedVersion: h.version, acknowledgedGaps: v.acknowledgedGaps, basis: v.basis }, reason), reload);
  }

  function propose(key) {
    const h = view.data.header, s = view.data.snapshot;
    formModal({
      title: 'Assess candidate', detail: 'Your decision is proposed; an independent quality approver approves it.',
      fields: [
        { name: 'decision', label: 'Impact decision', type: 'select', options: DECISIONS, required: true },
        { name: 'basis', label: 'Basis', type: 'textarea', required: true },
        { name: 'evidence', label: 'Evidence', type: 'textarea', required: true },
        { name: 'plannedDisposition', label: 'Planned disposition', type: 'textarea', required: true },
      ], confirmLabel: 'Propose', reasonRequired: true,
    }, (v, reason) => command(`${API}/trace/${h.investigationId}/assessments`, { expectedVersion: h.version, snapshotNumber: s.number, candidateKey: key, ...v }, reason), reload);
  }

  function assessmentAction(id, version, act) {
    formModal({ title: `${act === 'approve' ? 'Approve' : 'Withdraw'} assessment ${id}`, fields: [], confirmLabel: act === 'approve' ? 'Approve' : 'Withdraw', reasonRequired: true },
      (_, reason) => command(`${API}/assessments/${id}/${act}`, { expectedVersion: Number(version) }, reason), reload);
  }

  async function linkHold(assessmentId, assessmentVersion, key) {
    const h = view.data.header;
    const candidate = view.data.snapshot.capture.candidates.find(c => c.key === key);
    const { data: holds, error } = await tryGet('/api/holds');
    const matches = (holds ?? []).filter(x => x.active !== false && (candidate.holdTargets ?? []).some(t => t.type === x.targetType && String(t.id) === String(x.targetId)));
    formModal({
      title: 'Link a hold to this candidate', detail: error ? 'Holds could not be loaded; you can still enter the hold number.' : matches.length ? 'Choose an active hold on this candidate.' : 'No active hold matches this candidate yet. Place one on Shop Floor or Production Orders first.',
      fields: [
        { name: 'holdId', label: 'Hold', type: 'select', required: true, blank: '— choose —', options: matches.map(x => [x.holdId, `${x.holdId} · ${label(x.kind)} on ${x.target ?? x.targetId}`]) },
        { name: 'basis', label: 'Basis', type: 'textarea', required: true },
      ], confirmLabel: 'Link hold', reasonRequired: true,
    }, (v, reason) => {
      const hold = matches.find(x => String(x.holdId) === String(v.holdId));
      return command(`${API}/trace/${h.investigationId}/hold-links`, { expectedVersion: h.version, assessmentId, expectedAssessmentVersion: assessmentVersion, holdId: Number(v.holdId), expectedHoldVersion: hold?.recordVersion ?? 1, basis: v.basis }, reason);
    }, reload);
  }

  await load();
  if (!disposed) render();
  return { dispose() { disposed = true; } };
}
