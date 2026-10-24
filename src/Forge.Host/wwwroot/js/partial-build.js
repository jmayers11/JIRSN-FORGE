import { readPost, command } from './api.js';
import { esc, fmtNumber, fmtDateTime, errorBlock, drawer, toast } from './ui.js';
import { formModal, canAction } from './forms.js';

// Partial builds (Sprint 10 §17–19): how far an order can go with the material it can use now, the WIP that would wait in safety-approved
// storage, when the missing material is expected to be usable, and partial versus wait. The preview never reserves anything.

const qty = value => fmtNumber(Number(value ?? 0), 3);

export function partialBuildBody(values) {
  const n = key => values[key] === '' || values[key] == null ? null : Number(values[key]);
  return {
    receivingHours: n('receivingHours'), inspectionHours: n('inspectionHours'), putAwayHours: n('putAwayHours'),
    stagingHours: n('stagingHours') ?? 0, setupHours: n('setupHours') ?? 0, quantity: n('quantity'), partialAllowed: values.partialAllowed !== false,
  };
}

/** The preview as HTML: per-operation reach, the recommendation and its limits, storage, usable dates and blockers. */
export function partialBuildHtml(plan, me) {
  const ops = plan.operations.map(o => `<tr class="${o.sequence === plan.blockedSequence ? 'c-warn' : ''}"><td>${o.sequence}${o.sequence === plan.stopSequence ? ' <span class="pill info">stop</span>' : ''}${o.sequence === plan.blockedSequence ? ' <span class="pill warn">blocked</span>' : ''}</td>
    <td>${esc(o.description)}</td><td class="num">${qty(o.done)}</td><td class="num">${qty(o.reachable)}</td><td class="num">${qty(o.additional)}</td><td>${esc(o.limitedBy ?? '')}</td></tr>`).join('');
  const storage = plan.storage.map(s => `<li>${esc(s.status)}${s.rule.maxDwellHours ? ` <span class="faint">max dwell ${s.rule.maxDwellHours} h</span>` : ''}</li>`).join('');
  const dates = plan.materialDates.map(d => `<tr><td>${esc(d.partNumber)}</td><td>${d.supplierPromisedArrival ? esc(fmtDateTime(d.supplierPromisedArrival)) : '<span class="faint">none</span>'}</td>
    <td>${d.expectedUsableAt ? esc(fmtDateTime(d.expectedUsableAt)) : '<span class="faint">unknown</span>'}</td><td>${esc(d.arrivalConfidence)}<div class="faint">${esc(d.source)}</div></td></tr>`).join('');
  const reserve = plan.materials.map(m => `<li>${qty(m.quantity)} ${esc(m.partNumber)} <span class="faint">(consumed at op ${m.consumedAtSequence})</span></li>`).join('');
  const active = plan.activeBuild;
  return `<p>${esc(plan.comparison)}</p>
    ${plan.operations.length ? `<div class="table-wrap"><table class="table"><thead><tr><th>Op</th><th>Description</th><th class="num">Done</th><th class="num">Reachable now</th><th class="num">More now</th><th>Limited by</th></tr></thead><tbody>${ops}</tbody></table></div>` : ''}
    ${plan.quantity > 0 ? `<h3>Recommendation</h3><p><strong>${qty(plan.quantity)}</strong> through operation ${plan.stopSequence}${plan.storageQuantity > 0 ? `, ${qty(plan.storageQuantity)} waiting at ${esc(plan.storageLocation)}` : ''}${plan.workBroughtForwardMinutes > 0 ? ` · about ${fmtNumber(plan.workBroughtForwardMinutes, 0)} planned minutes brought forward` : ''}.</p>
      ${plan.feasibility?.limitingConstraints?.length ? `<p class="faint">Limited by: ${plan.feasibility.limitingConstraints.map(esc).join(', ')}</p>` : ''}
      ${reserve ? `<p>Approval reserves:</p><ul>${reserve}</ul>` : ''}` : ''}
    ${plan.storage.length ? `<h3>Approved WIP storage</h3><ul>${storage}</ul>` : ''}
    ${dates ? `<h3>Missing material</h3><div class="table-wrap"><table class="table"><thead><tr><th>Part</th><th>Promised</th><th>Expected usable</th><th>Basis</th></tr></thead><tbody>${dates}</tbody></table></div>
      <p>Expected restart: ${plan.expectedRestart ? esc(fmtDateTime(plan.expectedRestart)) : '<span class="faint">unknown</span>'}${plan.expectedDwellHours != null ? ` · WIP waits about ${fmtNumber(plan.expectedDwellHours, 1)} h` : ''}</p>` : ''}
    ${plan.blockers.length ? `<h3>Why it cannot be approved now</h3><ul>${plan.blockers.map(b => `<li class="c-bad">${esc(b)}</li>`).join('')}</ul>` : ''}
    ${plan.notes.length ? `<ul class="faint">${plan.notes.map(n => `<li>${esc(n)}</li>`).join('')}</ul>` : ''}
    ${active ? `<p><span class="pill info">Approved</span> Partial build ${active.buildId}: ${qty(active.quantity)} through op ${active.sequence}${active.storageLocation ? ` at ${esc(active.storageLocation)}` : ''}
      ${canAction(me, 'planning.schedule.approve') ? ` <button class="btn small" data-pb-close="complete|${active.buildId}">Complete</button> <button class="btn small danger" data-pb-close="cancel|${active.buildId}">Cancel</button>` : ''}</p>` : ''}
    <div class="actions"><button class="btn" data-pb-options>Lead times and options…</button>
      ${plan.canApprove && canAction(me, 'planning.schedule.approve') ? '<button class="btn primary" data-pb-approve>Approve partial build…</button>' : ''}</div>
    <p class="faint">Rule ${esc(plan.ruleVersion)} · as of ${esc(fmtDateTime(plan.asOf))}. Readiness is as of now; machine time is scheduled separately.</p>`;
}

/** Opens the partial-build preview for one released order. */
export async function openPartialBuild(orderNumber, me, after) {
  const d = drawer(`Order ${orderNumber} · partial build`, '<div class="muted">Loading…</div>');
  let options = {};
  const load = async () => {
    let plan;
    try { plan = await readPost(`/api/planning/orders/${orderNumber}/partial-build-preview`, options); } catch (error) { d.element.innerHTML = errorBlock(error, 'partial-build preview'); return; }
    d.element.innerHTML = partialBuildHtml(plan, me);
    bind();
  };
  const act = async promise => { try { if (await promise) { await load(); after?.(); } } catch (error) { toast(error.message, true); } };
  const bind = () => {
    d.element.querySelector('[data-pb-options]')?.addEventListener('click', async () => {
      const values = await formModal({
        title: 'Partial-build options', detail: 'Forge holds no receiving or inspection lead times, so give the ones that apply. They are kept with an approval.', confirmLabel: 'Recalculate',
        fields: [
          { name: 'receivingHours', label: 'Receiving (hours)', type: 'number', min: 0, value: options.receivingHours ?? '' },
          { name: 'inspectionHours', label: 'Inspection (hours)', type: 'number', min: 0, value: options.inspectionHours ?? '' },
          { name: 'putAwayHours', label: 'Put-away (hours)', type: 'number', min: 0, value: options.putAwayHours ?? '' },
          { name: 'stagingHours', label: 'Staging (hours)', type: 'number', min: 0, value: options.stagingHours ?? 0 },
          { name: 'setupHours', label: 'Restart setup (hours)', type: 'number', min: 0, value: options.setupHours ?? 0 },
          { name: 'quantity', label: 'Quantity (blank = recommended)', type: 'number', min: 0, value: options.quantity ?? '' },
          { name: 'partialAllowed', label: 'Policy', type: 'check', text: 'The customer and site allow a partial build', value: options.partialAllowed !== false },
        ],
      }, async v => { options = partialBuildBody(v); return true; });
      if (values) await load();
    });
    d.element.querySelector('[data-pb-approve]')?.addEventListener('click', () => act(formModal({
      title: 'Approve partial build', detail: 'Re-checks material and storage under lock and reserves both. Nothing is issued or moved.', fields: [], confirmLabel: 'Approve', reasonRequired: true,
    }, (_, reason) => command(`/api/planning/orders/${orderNumber}/partial-builds`, options, reason))));
    d.element.querySelectorAll('[data-pb-close]').forEach(b => b.addEventListener('click', () => {
      const [step, id] = b.dataset.pbClose.split('|');
      act(formModal({ title: `${step === 'complete' ? 'Complete' : 'Cancel'} partial build ${id}`, detail: 'Its material and storage reservations are released.', fields: [], confirmLabel: step === 'complete' ? 'Complete' : 'Cancel build', reasonRequired: true },
        (_, reason) => command(`/api/planning/partial-builds/${id}/${step}`, {}, reason)));
    }));
  };
  await load();
}
