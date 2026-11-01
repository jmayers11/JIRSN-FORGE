import { tryGet, command } from '../api.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock } from '../ui.js';
import { formModal, canAction, qty } from '../forms.js';
import { who } from '../people.js';

// Costing (Sprint 10 WW-S10-24, owner decision 2026-10-25): US GAAP practice, standard cost with full absorption (material, labor,
// overhead), kept by a Cost Accountant. Every change takes effect when recorded and never rewrites what was valued before.

const METHOD = { Standard: 'Standard cost', WeightedAverage: 'Weighted average', Fifo: 'FIFO' };

export const money = (amount, currency) => amount == null ? '—' : `${Number(amount).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 })} ${currency}`;

/** The cost reconciliation of a split NCR family, as a table that shows each portion and whether it adds up. */
export function reconciliationHtml(r) {
  const ok = (flag, yes, no) => flag == null ? '<span class="pill none">Not valued</span>' : flag ? `<span class="pill ok">${esc(yes)}</span>` : `<span class="pill bad">${esc(no)}</span>`;
  return `<div class="faint">${esc(r.partNumber)}${r.orderNumber ? ` · order #${r.orderNumber}` : ''} · ${esc(METHOD[r.method] ?? r.method)} · valued at ${esc(fmtDateTime(r.valuedAt))} · unit ${esc(money(r.unitStandardCost, r.currency))}</div>
    <table class="table"><thead><tr><th>NCR</th><th class="num">Qty</th><th>Disposition</th><th>Treatment</th><th class="num">Standard value</th></tr></thead><tbody>
    ${r.portions.map(p => `<tr><td>${p.ncrId}${p.parentNcrId ? ` <span class="faint">split from ${p.parentNcrId}</span>` : ''}</td><td class="num">${qty(p.quantity)}</td><td>${esc(p.disposition ?? p.status)}</td><td>${esc(p.treatment)}</td><td class="num">${esc(money(p.value, r.currency))}</td></tr>`).join('')}
    <tr><td><b>Original</b></td><td class="num"><b>${qty(r.originalQuantity)}</b></td><td colspan="2"></td><td class="num"><b>${esc(money(r.originalValue, r.currency))}</b></td></tr></tbody></table>
    <div>${ok(r.quantitiesReconcile, 'Quantities reconcile', 'Quantities do not reconcile')} ${ok(r.valuesReconcile, 'Values reconcile', 'Values do not reconcile')} · Scrap loss ${esc(money(r.scrapLoss, r.currency))} (${qty(r.scrapQuantityRecorded)} recorded scrapped)</div>
    ${r.rework.length ? `<h3 style="margin:8px 0 0">Work rework added</h3><table class="table"><thead><tr><th>Rework</th><th class="num">Qty</th><th class="num">Labor hours</th><th class="num">Labor + overhead</th><th class="num">Material</th></tr></thead><tbody>
      ${r.rework.map(w => `<tr><td>${w.reworkId}${w.notes.length ? `<div class="faint">${w.notes.map(esc).join(' ')}</div>` : ''}</td><td class="num">${qty(w.quantity)}</td><td class="num">${qty(w.laborHours)}</td><td class="num">${esc(money(w.laborAndOverhead, r.currency))}</td><td class="num">${esc(money(w.material, r.currency))}</td></tr>`).join('')}</tbody></table>` : ''}
    ${r.notes.map(n => `<div class="faint">${esc(n)}</div>`).join('')}`;
}

/** Actual job cost against standard (owner decisions 2026-10-31): material issued at standard, labor and overhead at the plant rates. */
export function jobCostHtml(j) {
  const m = v => money(v, j.currency);
  const variance = v => v == null ? '—' : `<span class="${v > 0 ? 'c-bad' : 'c-ok'}">${v > 0 ? '+' : ''}${esc(m(v))}</span>`;
  return `<div class="faint">Order #${j.orderNumber} · ${esc(j.partNumber)} · ${esc(j.status)} · ${qty(j.goodReceived)} good of ${qty(j.orderQuantity)}${j.scrapReported ? ` · ${qty(j.scrapReported)} scrapped` : ''} · standard ${esc(m(j.unitStandard))} each</div>
    <table class="table"><thead><tr><th>Element</th><th class="num">Actual</th><th class="num">Standard of output</th><th class="num">Variance</th></tr></thead><tbody>
    ${j.elements.map(e => `<tr><td>${esc(e.element)}</td><td class="num">${esc(m(e.actual))}</td><td class="num">${esc(m(e.standard))}</td><td class="num">${variance(e.variance)}</td></tr>`).join('')}
    <tr><td><b>Total</b></td><td class="num"><b>${esc(m(j.actualTotal))}</b></td><td class="num"><b>${esc(m(j.standardOfOutput))}</b></td><td class="num"><b>${j.variance != null ? variance(j.variance) : j.workInProcess != null ? `WIP ${esc(m(j.workInProcess))}` : '—'}</b></td></tr></tbody></table>
    <div>Hours: shop floor ${qty(j.shopFloorHours, 2)} · timesheets ${qty(j.timesheetHours, 2)} (${qty(j.timesheetHoursApproved, 2)} approved)</div>
    ${j.material.length ? `<details><summary>Material (${j.material.length})</summary><table class="table"><thead><tr><th>Part</th><th class="num">Issued</th><th class="num">Returned</th><th class="num">Credited</th><th class="num">Value</th></tr></thead><tbody>
      ${j.material.map(x => `<tr><td>${esc(x.partNumber)}</td><td class="num">${qty(x.issued)}</td><td class="num">${qty(x.returned)}</td><td class="num">${qty(x.credited)}</td><td class="num">${esc(m(x.value))}</td></tr>`).join('')}</tbody></table></details>` : ''}
    ${j.labor.length ? `<details><summary>Labor (${j.labor.length} operation(s))</summary><table class="table"><thead><tr><th>Op</th><th>Work center</th><th class="num">Operator h</th><th class="num">Trainer h</th><th class="num">Trainee h</th><th class="num">Labor</th><th class="num">Overhead</th></tr></thead><tbody>
      ${j.labor.map(x => `<tr><td>${x.sequence}</td><td>${esc(x.workCenterCode)}</td><td class="num">${qty(x.operatorHours, 2)}</td><td class="num">${qty(x.trainerHours, 2)}</td><td class="num">${qty(x.traineeHours, 2)}</td><td class="num">${esc(m(x.labor))}</td><td class="num">${esc(m(x.overhead))}</td></tr>`).join('')}</tbody></table></details>` : ''}
    ${j.notes.map(n => `<div class="faint">${esc(n)}</div>`).join('')}`;
}

export async function costingPage(page, ctx) {
  const me = ctx?.me;
  const manager = canAction(me, 'costing.manage');
  const load = async () => {
    const [policies, costs] = await Promise.all([tryGet('/api/costing/policies'), tryGet('/api/costing/standard-costs')]);
    if (policies.error) { page.innerHTML = errorBlock(policies.error, 'costing policy'); return; }
    const list = policies.data ?? [];
    const current = list.at(-1);
    const versions = costs.data ?? [];
    const latest = [...versions.reduce((byPart, c) => byPart.set(c.partNumber, c), new Map()).values()];
    page.innerHTML = `
      <div class="page-head"><div><h1>Costing</h1><div class="sub">US GAAP practice: standard cost with material, labor and overhead. Changes take effect when recorded and never rewrite earlier valuations.</div></div><div class="spacer"></div>
        ${manager ? '<button class="btn" data-set-policy>Change policy</button> <button class="btn primary" data-set-cost>Set standard cost</button>' : ''}</div>
      <section class="panel"><header><h2>Costing policy</h2></header>
        ${current ? `<p><b>${esc(METHOD[current.method] ?? current.method)}</b> in ${esc(current.currency)} · labor ${esc(money(current.laborRatePerHour, current.currency))} and overhead ${esc(money(current.overheadRatePerHour, current.currency))} per direct labor hour</p>` : '<p class="faint">No policy recorded.</p>'}
        <table class="table"><thead><tr><th>From</th><th>Method</th><th class="num">Labor / h</th><th class="num">Overhead / h</th><th>Set by</th><th>Reason</th></tr></thead><tbody>
        ${list.slice().reverse().map(p => `<tr><td class="nowrap">${esc(fmtDateTime(p.effectiveFrom))}</td><td>${esc(METHOD[p.method] ?? p.method)}</td><td class="num">${esc(money(p.laborRatePerHour, p.currency))}</td><td class="num">${esc(money(p.overheadRatePerHour, p.currency))}</td><td>${esc(who(p.setBy))}</td><td>${esc(p.reason)}</td></tr>`).join('')}</tbody></table></section>
      <section class="panel"><header><h2>Standard costs</h2></header>
        ${costs.error ? errorBlock(costs.error, 'standard costs') : latest.length ? `<table class="table"><thead><tr><th>Part</th><th class="num">Material</th><th class="num">Labor</th><th class="num">Overhead</th><th class="num">Unit cost</th><th>From</th><th>Reason</th></tr></thead><tbody>
          ${latest.map(c => `<tr><td>${esc(c.partNumber)}</td><td class="num">${esc(money(c.material, c.currency))}</td><td class="num">${esc(money(c.labor, c.currency))}</td><td class="num">${esc(money(c.overhead, c.currency))}</td><td class="num"><b>${esc(money(c.material + c.labor + c.overhead, c.currency))}</b></td><td class="nowrap">${esc(fmtDateTime(c.effectiveFrom))}</td><td>${esc(c.reason)}</td></tr>`).join('')}</tbody></table>` : emptyBlock('No standard costs yet', 'A Cost Accountant sets each part\'s material, labor and overhead per unit.')}</section>
      <section class="panel"><header><h2>Job cost</h2></header>
        <form id="job-cost" class="inline"><input class="input mono" id="job-cost-order" type="number" min="1" placeholder="Order number" style="width:140px"> <button class="btn">Show job cost</button></form>
        <div id="job-cost-result" class="faint">Actual material, labor and overhead of a production order against the standard of what it produced.</div></section>
      <section class="panel"><header><h2>NCR cost reconciliation</h2></header>
        <form id="ncr-cost" class="inline"><input class="input mono" id="ncr-cost-id" type="number" min="1" placeholder="NCR number" style="width:140px"> <button class="btn">Reconcile</button></form>
        <div id="ncr-cost-result" class="faint">Shows how a nonconformance's standard value splits across its dispositions, including NCRs split from it.</div></section>`;
    $('[data-set-policy]', page)?.addEventListener('click', () => formModal({
      title: 'Change the costing policy', detail: 'Applies from now on; earlier valuations stay as they were. Weighted average and FIFO need actual costs Forge does not record yet.', confirmLabel: 'Record policy', reasonRequired: true,
      fields: [
        { name: 'method', label: 'Method', type: 'select', options: Object.entries(METHOD), value: current?.method ?? 'Standard', required: true },
        { name: 'currency', label: 'Currency', value: current?.currency ?? 'USD', required: true },
        { name: 'laborRatePerHour', label: 'Labor rate per direct labor hour', type: 'number', min: 0, step: 'any', value: current?.laborRatePerHour },
        { name: 'overheadRatePerHour', label: 'Overhead rate per direct labor hour', type: 'number', min: 0, step: 'any', value: current?.overheadRatePerHour },
      ],
    }, (v, reason) => command('/api/costing/policies', v, reason), load));
    $('[data-set-cost]', page)?.addEventListener('click', () => formModal({
      title: 'Set a standard cost', detail: 'Unit cost split into material, labor and overhead. It applies from now on.', confirmLabel: 'Set cost', reasonRequired: true,
      fields: [
        { name: 'partNumber', label: 'Part', code: true, required: true },
        { name: 'material', label: 'Material per unit', type: 'number', min: 0, step: 'any', required: true },
        { name: 'labor', label: 'Labor per unit', type: 'number', min: 0, step: 'any', required: true },
        { name: 'overhead', label: 'Overhead per unit', type: 'number', min: 0, step: 'any', required: true },
      ],
    }, (v, reason) => command('/api/costing/standard-costs', { ...v, partNumber: String(v.partNumber).trim().toUpperCase() }, reason), load));
    $('#job-cost', page)?.addEventListener('submit', async event => {
      event.preventDefault();
      const order = Number($('#job-cost-order', page).value);
      const host = $('#job-cost-result', page);
      if (!order) return;
      const result = await tryGet(`/api/costing/orders/${order}/job-cost`);
      host.className = '';
      host.innerHTML = result.error ? errorBlock(result.error, 'job cost') : jobCostHtml(result.data);
    });
    $('#ncr-cost', page)?.addEventListener('submit', async event => {
      event.preventDefault();
      const id = Number($('#ncr-cost-id', page).value);
      const host = $('#ncr-cost-result', page);
      if (!id) return;
      const result = await tryGet(`/api/costing/ncrs/${id}/reconciliation`);
      host.className = '';
      host.innerHTML = result.error ? errorBlock(result.error, 'cost reconciliation') : reconciliationHtml(result.data);
    });
    $$('.table', page);
  };
  await load();
}
