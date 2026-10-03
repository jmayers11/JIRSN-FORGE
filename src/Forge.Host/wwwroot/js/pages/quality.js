import { tryGet, command } from '../api.js';
import { who } from '../people.js';
import { attachmentsDrawer } from '../attachments.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, confirmWithReason, toast } from '../ui.js';
import { formModal, qty, can, plusDays, today } from '../forms.js';

// Quality (spec §45-46): inspection points after routing operations, inspections by someone who did
// not do the work, with a calibrated gauge for anything measured. A failure raises an NCR that holds
// the order; use-as-is needs a second person, and so does verifying a corrective action.

const NCR_TONE = { Open: 'bad', AwaitingApproval: 'warn', Dispositioned: 'info', Closed: 'ok' };
const NCR_LABEL = { Open: 'Open', AwaitingApproval: 'Awaiting approval', Dispositioned: 'Dispositioned', Closed: 'Closed' };
const DISPOSITION = [['Rework', 'Rework'], ['Scrap', 'Scrap'], ['ReturnToSupplier', 'Return to supplier'], ['UseAsIs', 'Use as is (needs approval)']];
const CAPA_TONE = { Open: 'warn', Implemented: 'info', Verified: 'ok' };

const limits = c => c.kind === 'Attribute' ? 'Accept / reject'
  : [c.lower != null ? `≥ ${qty(c.lower, 6)}` : null, c.upper != null ? `≤ ${qty(c.upper, 6)}` : null].filter(Boolean).join(' and ') + (c.unit ? ` ${c.unit}` : '');

export async function qualityPage(page, ctx) {
  const me = ctx.me;
  const inspector = can(me, 'Inspector', 'QualityEngineer');
  const engineer = can(me, 'QualityEngineer');
  const manager = can(me, 'QualityManager');
  let tab = ctx.query.get('tab') ?? 'due';
  let showClosed = false;
  let data = {};

  async function load() {
    const [due, ncrs, capas, plans, gauges, inspections, items] = await Promise.all([
      tryGet('/api/quality/due'), tryGet(`/api/quality/ncrs?includeClosed=${showClosed}`), tryGet(`/api/quality/capas?includeVerified=${showClosed}`),
      tryGet('/api/quality/plans'), tryGet('/api/quality/gauges'), tryGet('/api/quality/inspections?take=100'), tryGet('/api/engineering/items'),
    ]);
    data = { due, ncrs, capas, plans, gauges, inspections, items };
  }
  const reload = async () => { await load(); render(); };
  const mine = actor => actor === `person:${me.personId}`;
  const calibrated = () => (data.gauges.data ?? []).filter(g => g.active && g.calibrationDue >= today());

  function render() {
    const ncrs = data.ncrs.data ?? [];
    const open = ncrs.filter(n => n.status === 'Open' || n.status === 'AwaitingApproval').length;
    const due = data.due.data?.length ?? 0;
    const overdueGauges = (data.gauges.data ?? []).filter(g => g.active && g.calibrationDue < today());
    const tabs = [['due', `Inspections due${due ? ` (${due})` : ''}`], ['ncrs', `Nonconformances${open ? ` (${open})` : ''}`], ['capas', 'Corrective actions'], ['history', 'Inspection history'], ['plans', 'Inspection plans'], ['gauges', 'Gauges']];
    page.innerHTML = `
      <div class="page-head">
        <div><h1>Quality</h1><div class="sub">Inspections, nonconformances and corrective actions. An undecided NCR or a missed inspection holds the order; finished goods enter stock and ship only after they pass.</div></div>
        <div class="spacer"></div>
        <label class="opt"><input type="checkbox" id="closed" ${showClosed ? 'checked' : ''}> Show closed</label>
        ${inspector ? '<button class="btn" id="raise">Raise NCR</button>' : ''}
      </div>
      ${overdueGauges.length ? `<div class="notice warn">${overdueGauges.length} gauge(s) are past their calibration date and cannot be used for inspection: ${overdueGauges.map(g => esc(g.code)).join(', ')}.</div>` : ''}
      <section class="panel">
        <header><div class="seg">${tabs.map(([k, l]) => `<button data-tab="${k}" class="${tab === k ? 'on' : ''}">${esc(l)}</button>`).join('')}</div></header>
        ${{ due: dueTab, ncrs: ncrTab, capas: capaTab, history: historyTab, plans: plansTab, gauges: gaugesTab }[tab]?.() ?? dueTab()}
      </section>`;

    $$('[data-tab]', page).forEach(b => b.addEventListener('click', () => { tab = b.dataset.tab; render(); }));
    $('#closed', page).onchange = async e => { showClosed = e.target.checked; await reload(); };
    $('#raise', page)?.addEventListener('click', () => raiseNcr());
    $$('[data-inspect]', page).forEach(b => b.addEventListener('click', () => inspect(...b.dataset.inspect.split('/').map(Number))));
    $$('[data-dispose]', page).forEach(b => b.addEventListener('click', () => dispose(Number(b.dataset.dispose))));
    $$('[data-docs-ncr]', page).forEach(b => b.addEventListener('click', () => attachmentsDrawer(`NCR ${b.dataset.docsNcr}`, { type: 'Ncr', id: b.dataset.docsNcr, me, note: 'Photos, measurements, supplier responses and evidence for this nonconformance.' })));
    $$('[data-approve]', page).forEach(b => b.addEventListener('click', () => decide(Number(b.dataset.approve), true)));
    $$('[data-reject]', page).forEach(b => b.addEventListener('click', () => decide(Number(b.dataset.reject), false)));
    $$('[data-close]', page).forEach(b => b.addEventListener('click', () => closeNcr(Number(b.dataset.close))));
    $$('[data-capa-for]', page).forEach(b => b.addEventListener('click', () => openCapa(Number(b.dataset.capaFor) || null)));
    $('#new-capa', page)?.addEventListener('click', () => openCapa(null));
    $$('[data-implement]', page).forEach(b => b.addEventListener('click', () => implement(Number(b.dataset.implement))));
    $$('[data-verify]', page).forEach(b => b.addEventListener('click', () => verify(Number(b.dataset.verify))));
    $('#new-plan', page)?.addEventListener('click', () => editPlan(null));
    $$('[data-plan]', page).forEach(b => b.addEventListener('click', () => editPlan((data.plans.data ?? []).find(p => `${p.partNumber}/${p.sequence}` === b.dataset.plan))));
    $$('[data-remove-plan]', page).forEach(b => b.addEventListener('click', () => removePlan(...b.dataset.removePlan.split('/'))));
    $('#new-gauge', page)?.addEventListener('click', newGauge);
    $$('[data-calibrate]', page).forEach(b => b.addEventListener('click', () => calibrate(b.dataset.calibrate)));
  }

  // ------------------------------------------------------------ tabs

  function dueTab() {
    if (data.due.error) return errorBlock(data.due.error, 'inspections due');
    const rows = data.due.data;
    if (!rows.length) return emptyBlock('Nothing waiting for inspection', 'An order appears here once work has started on an operation that has an inspection point.');
    return `<table class="table"><thead><tr><th>Order</th><th>Part</th><th>Operation</th><th>Inspection</th><th>Checks</th><th></th></tr></thead><tbody>
      ${rows.map(d => `<tr><td><a href="#/orders/${d.orderNumber}">#${d.orderNumber}</a></td><td><b>${esc(d.partNumber)}</b> × ${qty(d.orderQuantity)}</td>
        <td>${d.sequence} ${esc(d.operation)} <span class="faint">${esc(d.operationStatus)}</span></td>
        <td>${esc(d.plan.title)}${d.lastFailed ? ' <span class="pill bad">Last inspection failed</span>' : ''}</td>
        <td class="faint">${d.plan.characteristics.map(c => esc(c.name)).join(', ')}</td>
        <td>${inspector ? `<button class="btn primary small" data-inspect="${d.orderNumber}/${d.sequence}">Inspect</button>` : ''}</td></tr>`).join('')}
      </tbody></table>`;
  }

  function ncrTab() {
    if (data.ncrs.error) return errorBlock(data.ncrs.error, 'nonconformances');
    const rows = data.ncrs.data;
    if (!rows.length) return emptyBlock('No nonconformances', 'A failed inspection raises one automatically; anyone who inspects can raise one by hand.');
    return rows.map(n => `<div class="card-row ncr">
      <div class="grow">
        <div><b>NCR ${n.ncrId}</b> · ${esc(n.partNumber)} × ${qty(n.quantity)}${n.orderNumber ? ` · <a href="#/orders/${n.orderNumber}">order #${n.orderNumber}</a>${n.sequence ? ` op ${n.sequence}` : ''}` : ''}${n.lotNumber ? ` · lot ${esc(n.lotNumber)}` : ''}
          <span class="pill ${NCR_TONE[n.status]}">${esc(NCR_LABEL[n.status])}</span>${n.disposition ? ` <span class="pill none">${esc(DISPOSITION.find(d => d[0] === n.disposition)?.[1].replace(' (needs approval)', '') ?? n.disposition)}</span>` : ''}</div>
        <div>${esc(n.defect)}: ${esc(n.description)}</div>
        <div class="faint">Raised by ${esc(who(n.raisedBy))} ${esc(fmtDateTime(n.raisedAt))}${n.dispositionedBy ? ` · dispositioned by ${esc(who(n.dispositionedBy))}: ${esc(n.dispositionNote ?? '')}` : ''}${n.approvedBy ? ` · approved by ${esc(who(n.approvedBy))}` : ''}${n.closedBy ? ` · closed by ${esc(who(n.closedBy))} ${esc(fmtDateTime(n.closedAt))}: ${esc(n.closureNote ?? '')}` : ''}</div>
      </div>
      <div class="nowrap">
        ${n.status === 'Open' && engineer ? `<button class="btn primary small" data-dispose="${n.ncrId}">Disposition</button>` : ''}
        ${n.status === 'AwaitingApproval' && manager && !mine(n.dispositionedBy) ? `<button class="btn primary small" data-approve="${n.ncrId}">Approve use as is</button> <button class="btn small" data-reject="${n.ncrId}">Reject</button>` : ''}
        ${n.status === 'AwaitingApproval' && mine(n.dispositionedBy) ? '<span class="faint">You proposed it — someone else approves</span>' : ''}
        ${n.status === 'Dispositioned' && engineer ? `<button class="btn small" data-close="${n.ncrId}">Close</button> <button class="btn small ghost" data-capa-for="${n.ncrId}">Corrective action</button>` : ''}
        <button class="btn small ghost" data-docs-ncr="${n.ncrId}">Documents</button>
      </div></div>`).join('');
  }

  function capaTab() {
    if (data.capas.error) return errorBlock(data.capas.error, 'corrective actions');
    const rows = data.capas.data;
    const add = engineer ? '<div style="margin-bottom:8px"><button class="btn small" id="new-capa">New corrective action</button></div>' : '';
    if (!rows.length) return add + emptyBlock('No corrective actions', 'Open one from an NCR to fix the cause, not just the part.');
    return add + rows.map(c => {
      const late = c.status === 'Open' && c.dueDate < today();
      return `<div class="card-row">
        <div class="grow"><div><b>CA ${c.capaId}</b> · ${esc(c.title)} <span class="pill ${CAPA_TONE[c.status]}">${esc(c.status)}</span>${c.ncrId ? ` <span class="faint">from NCR ${c.ncrId}</span>` : ''}</div>
          <div><span class="faint">Root cause:</span> ${esc(c.rootCause)} · <span class="faint">Action:</span> ${esc(c.action)}</div>
          <div class="faint ${late ? 'c-bad' : ''}">Due ${esc(fmtDate(c.dueDate))}${late ? ' · overdue' : ''} · opened by ${esc(who(c.openedBy))}${c.implementedBy ? ` · implemented by ${esc(who(c.implementedBy))}: ${esc(c.implementationNote ?? '')}` : ''}${c.verifiedBy ? ` · verified by ${esc(who(c.verifiedBy))} ${esc(fmtDateTime(c.verifiedAt))}` : ''}</div></div>
        <div class="nowrap">
          ${c.status === 'Open' && engineer ? `<button class="btn small" data-implement="${c.capaId}">Mark implemented</button>` : ''}
          ${c.status === 'Implemented' && manager && !mine(c.implementedBy) ? `<button class="btn primary small" data-verify="${c.capaId}">Verify effective</button>` : ''}
          ${c.status === 'Implemented' && mine(c.implementedBy) ? '<span class="faint">You implemented it — someone else verifies</span>' : ''}
        </div></div>`;
    }).join('');
  }

  function historyTab() {
    if (data.inspections.error) return errorBlock(data.inspections.error, 'inspections');
    const rows = data.inspections.data;
    if (!rows.length) return emptyBlock('No inspections recorded yet');
    return `<table class="table"><thead><tr><th>When</th><th>Order</th><th>Op</th><th>Result</th><th class="num">Qty</th><th>Findings</th><th>Gauge</th><th>Inspector</th></tr></thead><tbody>
      ${rows.map(i => `<tr><td class="nowrap">${esc(fmtDateTime(i.at))}</td><td><a href="#/orders/${i.orderNumber}">#${i.orderNumber}</a> ${esc(i.partNumber)}</td><td>${i.sequence}</td>
        <td><span class="pill ${i.result === 'Pass' ? 'ok' : 'bad'}">${esc(i.result)}</span>${i.ncrId ? ` NCR ${i.ncrId}` : ''}</td><td class="num">${qty(i.quantityInspected)}</td>
        <td>${i.results.map(r => `<span class="${r.pass ? '' : 'c-bad'}">${esc(r.name)} ${r.kind === 'Variable' ? qty(r.measured, 6) : r.accepted ? '✓' : '✗'}</span>`).join(' · ')}${i.notes ? `<div class="faint">${esc(i.notes)}</div>` : ''}</td>
        <td>${esc(i.gaugeCode ?? '—')}</td><td>${esc(who(i.inspector))}</td></tr>`).join('')}
      </tbody></table>`;
  }

  function plansTab() {
    if (data.plans.error) return errorBlock(data.plans.error, 'inspection plans');
    const rows = data.plans.data;
    const add = engineer ? '<div style="margin-bottom:8px"><button class="btn small" id="new-plan">New inspection point</button></div>' : '';
    if (!rows.length) return add + emptyBlock('No inspection points', 'Add one after a routing operation: what to check, and the limits.');
    return add + `<table class="table"><thead><tr><th>Part</th><th>After op</th><th>Inspection</th><th>Characteristics</th><th></th></tr></thead><tbody>
      ${rows.map(p => `<tr><td><b>${esc(p.partNumber)}</b></td><td>${p.sequence}</td><td>${esc(p.title)}</td>
        <td>${p.characteristics.map(c => `${esc(c.name)} <span class="faint">${esc(limits(c))}</span>`).join('<br>')}</td>
        <td class="nowrap">${engineer ? `<button class="btn small" data-plan="${esc(p.partNumber)}/${p.sequence}">Edit</button> <button class="btn small ghost" data-remove-plan="${esc(p.partNumber)}/${p.sequence}">Remove</button>` : ''}</td></tr>`).join('')}
      </tbody></table>`;
  }

  function gaugesTab() {
    if (data.gauges.error) return errorBlock(data.gauges.error, 'gauges');
    const rows = data.gauges.data;
    const add = engineer ? '<div style="margin-bottom:8px"><button class="btn small" id="new-gauge">New gauge</button></div>' : '';
    if (!rows.length) return add + emptyBlock('No gauges', 'Measured characteristics need a calibrated gauge.');
    return add + `<table class="table"><thead><tr><th>Gauge</th><th>Description</th><th>Calibration due</th><th>Certificate</th><th></th></tr></thead><tbody>
      ${rows.map(g => {
        const out = g.calibrationDue < today();
        return `<tr><td><b>${esc(g.code)}</b></td><td>${esc(g.description)}</td><td class="${out ? 'c-bad' : ''}">${esc(fmtDate(g.calibrationDue))}${out ? ' · out of calibration' : ''}</td>
          <td>${esc(g.certificate ?? '—')}</td><td>${engineer ? `<button class="btn small" data-calibrate="${esc(g.code)}">Record calibration</button>` : ''}</td></tr>`;
      }).join('')}
      </tbody></table>`;
  }

  // ------------------------------------------------------------ actions

  function inspect(orderNumber, sequence) {
    const due = (data.due.data ?? []).find(d => d.orderNumber === orderNumber && d.sequence === sequence);
    const plan = due.plan;
    const measured = plan.characteristics.some(c => c.kind === 'Variable');
    const gauges = calibrated().map(g => [g.code, `${g.code} — ${g.description} (due ${fmtDate(g.calibrationDue)})`]);
    const fields = [
      { name: 'quantity', label: 'Quantity inspected', type: 'number', min: 0, step: 'any', value: 1, required: true },
      ...(measured ? [{ name: 'gaugeCode', label: 'Gauge used', type: 'select', options: gauges, required: true, hint: gauges.length ? 'Only gauges in calibration are listed.' : 'No gauge is in calibration — record a calibration first.' }] : []),
      ...plan.characteristics.map(c => c.kind === 'Variable'
        ? { name: `c${c.number}`, label: `${c.name} (${limits(c)})`, type: 'number', step: 'any', required: true }
        : { name: `c${c.number}`, label: c.name, type: 'select', options: [['true', 'Accept'], ['false', 'Reject']], required: true }),
      { name: 'rejectedQuantity', label: 'Quantity rejected (if it fails)', type: 'number', min: 0, step: 'any' },
      { name: 'notes', label: 'Notes', type: 'textarea' },
    ];
    return formModal({ title: `Inspect order #${orderNumber} after operation ${sequence}`, detail: `${plan.title}. You must not have worked this operation. A failure raises an NCR and holds the order.`, confirmLabel: 'Record inspection', fields },
      (v, reason) => command('/api/quality/inspections', {
        orderNumber, sequence, quantity: v.quantity, gaugeCode: v.gaugeCode ?? null, notes: v.notes, rejectedQuantity: v.rejectedQuantity,
        findings: plan.characteristics.map(c => c.kind === 'Variable' ? { number: c.number, measured: v[`c${c.number}`] } : { number: c.number, accepted: v[`c${c.number}`] === 'true' }),
      }, reason), reload);
  }

  function raiseNcr() {
    const parts = (data.items.data ?? []).map(i => [i.partNumber, `${i.partNumber} — ${i.description}`]);
    return formModal({ title: 'Raise a nonconformance', detail: 'If it is against an order, the order is held until the NCR is dispositioned.', confirmLabel: 'Raise NCR',
      fields: [
        { name: 'partNumber', label: 'Part', type: 'select', options: parts, required: true },
        { name: 'quantity', label: 'Quantity affected', type: 'number', min: 0, step: 'any', required: true },
        { name: 'orderNumber', label: 'Order number (optional)', type: 'number' },
        { name: 'sequence', label: 'Operation (optional)', type: 'number' },
        { name: 'lotNumber', label: 'Lot (optional)' },
        { name: 'defect', label: 'Defect', required: true, placeholder: 'e.g. Edge burr' },
        { name: 'description', label: 'What was found', type: 'textarea', required: true },
      ] }, (v, reason) => command('/api/quality/ncrs', v, reason), reload);
  }

  function dispose(id) {
    return formModal({ title: `Disposition NCR ${id}`, detail: 'Use as is accepts nonconforming material and needs a quality manager other than you to approve it.', confirmLabel: 'Record disposition', reasonRequired: true,
      fields: [
        { name: 'disposition', label: 'Disposition', type: 'select', options: DISPOSITION, required: true },
        { name: 'note', label: 'Disposition instructions', type: 'textarea', required: true },
      ] }, (v, reason) => command(`/api/quality/ncrs/${id}/disposition`, v, reason), reload);
  }

  async function decide(id, approve) {
    const done = await confirmWithReason({ title: `${approve ? 'Approve' : 'Reject'} use as is — NCR ${id}`, detail: approve ? 'The material is accepted as it is and the order may continue.' : 'The NCR goes back to Open to be dispositioned again.', confirmLabel: approve ? 'Approve' : 'Reject' },
      reason => command(`/api/quality/ncrs/${id}/${approve ? 'approve' : 'reject'}`, {}, reason));
    if (done) { toast(done.message); await reload(); }
  }

  function closeNcr(id) {
    return formModal({ title: `Close NCR ${id}`, detail: 'Close it once the disposition has been carried out.', confirmLabel: 'Close NCR', reasonRequired: true,
      fields: [{ name: 'note', label: 'What was done', type: 'textarea', required: true }] },
    (v, reason) => command(`/api/quality/ncrs/${id}/close`, v, reason), reload);
  }

  function openCapa(ncrId) {
    return formModal({ title: ncrId ? `Corrective action for NCR ${ncrId}` : 'New corrective action', confirmLabel: 'Open corrective action',
      fields: [
        { name: 'title', label: 'Title', required: true },
        { name: 'rootCause', label: 'Root cause', type: 'textarea', required: true },
        { name: 'action', label: 'Action to prevent it happening again', type: 'textarea', required: true },
        { name: 'dueDate', label: 'Due', type: 'date', value: plusDays(30), required: true },
      ] }, (v, reason) => command('/api/quality/capas', { ...v, ncrId }, reason), reload);
  }

  function implement(id) {
    return formModal({ title: `Corrective action ${id} implemented`, detail: 'A quality manager other than you then verifies it worked.', confirmLabel: 'Mark implemented',
      fields: [{ name: 'note', label: 'What was changed', type: 'textarea', required: true }] },
    (v, reason) => command(`/api/quality/capas/${id}/implement`, v, reason), reload);
  }

  async function verify(id) {
    const done = await confirmWithReason({ title: `Verify corrective action ${id}`, detail: 'Confirm the action was effective: the problem has not come back.', confirmLabel: 'Verify' },
      reason => command(`/api/quality/capas/${id}/verify`, {}, reason));
    if (done) { toast(done.message); await reload(); }
  }

  function editPlan(plan) {
    const parts = (data.items.data ?? []).filter(i => i.makeBuy === 'Make').map(i => [i.partNumber, `${i.partNumber} — ${i.description}`]);
    const rows = [1, 2, 3, 4, 5];
    const fields = [
      { name: 'partNumber', label: 'Part', type: plan ? 'text' : 'select', options: parts, value: plan?.partNumber, readonly: !!plan, required: true },
      { name: 'sequence', label: 'After operation', type: 'number', value: plan?.sequence, readonly: !!plan, required: true, hint: 'The operation number in the released routing.' },
      { name: 'title', label: 'Inspection title', value: plan?.title, required: true },
      ...rows.flatMap(n => {
        const c = plan?.characteristics[n - 1];
        return [
          { name: `name${n}`, label: `Check ${n}${n > 1 ? ' (optional)' : ''}`, value: c?.name, required: n === 1 },
          { name: `kind${n}`, label: `Check ${n} kind`, type: 'select', value: c?.kind ?? 'Attribute', options: [['Attribute', 'Accept / reject'], ['Variable', 'Measured']] },
          { name: `lower${n}`, label: `Check ${n} lower limit`, type: 'number', step: 'any', value: c?.lower },
          { name: `upper${n}`, label: `Check ${n} upper limit`, type: 'number', step: 'any', value: c?.upper },
          { name: `unit${n}`, label: `Check ${n} unit`, value: c?.unit },
        ];
      }),
    ];
    return formModal({ title: plan ? `Inspection after ${plan.partNumber} op ${plan.sequence}` : 'New inspection point', detail: 'Measured checks need at least one limit. Past inspections keep the limits they were made against.', confirmLabel: 'Save inspection point', fields },
      (v, reason) => command('/api/quality/plans', {
        partNumber: v.partNumber, sequence: v.sequence, title: v.title,
        characteristics: rows.filter(n => v[`name${n}`]).map(n => ({ name: v[`name${n}`], kind: v[`kind${n}`], lower: v[`lower${n}`], upper: v[`upper${n}`], unit: v[`unit${n}`] })),
      }, reason), reload);
  }

  async function removePlan(part, sequence) {
    const done = await confirmWithReason({ title: `Remove the inspection after ${part} op ${sequence}`, detail: 'Orders no longer wait for it. Inspections already recorded are kept.', confirmLabel: 'Remove' },
      reason => command(`/api/quality/plans/${encodeURIComponent(part)}/${sequence}/remove`, {}, reason));
    if (done) { toast(done.message); await reload(); }
  }

  function newGauge() {
    return formModal({ title: 'New gauge', confirmLabel: 'Add gauge',
      fields: [
        { name: 'code', label: 'Gauge ID', code: true, required: true },
        { name: 'description', label: 'Description', required: true },
        { name: 'calibrationDue', label: 'Calibration due', type: 'date', value: plusDays(365), required: true },
        { name: 'certificate', label: 'Certificate (optional)' },
      ] }, (v, reason) => command('/api/quality/gauges', v, reason), reload);
  }

  function calibrate(code) {
    return formModal({ title: `Calibration — ${code}`, confirmLabel: 'Record calibration',
      fields: [
        { name: 'calibratedOn', label: 'Calibrated on', type: 'date', value: today(), required: true },
        { name: 'dueDate', label: 'Next due', type: 'date', value: plusDays(365), required: true },
        { name: 'certificate', label: 'Certificate (optional)' },
      ] }, (v, reason) => command(`/api/quality/gauges/${encodeURIComponent(code)}/calibrations`, v, reason), reload);
  }

  await load();
  render();
}
