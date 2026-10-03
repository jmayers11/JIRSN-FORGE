import { tryGet, command } from '../api.js';
import { who } from '../people.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock } from '../ui.js';
import { formModal, can } from '../forms.js';

// Engineering (V2.1): what each item is. The class decides how it is bought, documented, inspected and
// substituted; nothing is guessed — bought items start Unclassified and are worked off from this list.
// COTS items are bought as manufacturer parts, proposed by one person and approved by another.

export const CLASS_LABEL = {
  Unclassified: 'Unclassified', Make: 'Make', BuildToPrint: 'Build to print', Cots: 'COTS', RawMaterial: 'Raw material',
  StandardHardware: 'Standard hardware', Consumable: 'Consumable', Mro: 'MRO / spare', Tooling: 'Tooling',
  Service: 'Service / special process', OtherControlled: 'Other controlled',
};
const ALWAYS_BOUGHT = ['BuildToPrint', 'Cots', 'RawMaterial', 'StandardHardware', 'Consumable', 'Service'];
const LIFECYCLE_LABEL = { Active: 'Active', NotRecommended: 'Not recommended for new designs', LastTimeBuy: 'Last-time buy', EndOfLife: 'End of life', Unknown: 'Unknown' };
const LIFECYCLE_TONE = { Active: 'ok', NotRecommended: 'warn', LastTimeBuy: 'warn', EndOfLife: 'bad', Unknown: 'none' };
const STATUS_TONE = { Approved: 'ok', Proposed: 'info', Withdrawn: 'none' };
const MPN_CLASSES = ['Cots', 'RawMaterial', 'StandardHardware', 'Consumable', 'Mro', 'Tooling', 'OtherControlled'];

const classesFor = makeBuy => Object.entries(CLASS_LABEL)
  .filter(([k]) => k !== 'Unclassified' && (k === 'Make' ? makeBuy === 'Make' : makeBuy === 'Buy' || !ALWAYS_BOUGHT.includes(k)));

export async function engineeringPage(page, ctx) {
  const part = ctx.params[0];
  return part ? itemDetail(page, ctx, decodeURIComponent(part)) : itemList(page, ctx);
}

async function itemList(page, ctx) {
  const filter = ctx.query.get('class') ?? '';
  const [items, mpns] = await Promise.all([tryGet('/api/engineering/items'), tryGet('/api/engineering/manufacturer-parts')]);
  if (items.error) { page.innerHTML = errorBlock(items.error, 'items'); return; }
  const all = items.data;
  const counts = Object.fromEntries(Object.keys(CLASS_LABEL).map(k => [k, all.filter(i => i.class === k).length]));
  const approved = new Map();
  for (const m of mpns.data ?? []) if (m.status === 'Approved') approved.set(m.partNumber, (approved.get(m.partNumber) ?? 0) + 1);
  const list = filter ? all.filter(i => i.class === filter) : all;
  const cotsWithout = all.filter(i => i.class === 'Cots' && !approved.get(i.partNumber));
  page.innerHTML = `
    <div class="page-head"><div><h1>Engineering</h1><div class="sub">Items and what they are. The class decides how an item is bought, documented, inspected and substituted.</div></div></div>
    ${counts.Unclassified ? `<div class="notice warn">${counts.Unclassified} item(s) are unclassified. Forge does not guess: classify them so purchasing, documentation and substitution rules apply. <a href="#/engineering?class=Unclassified">Show them</a></div>` : ''}
    ${cotsWithout.length ? `<div class="notice bad">${cotsWithout.length} COTS item(s) have no approved manufacturer part, so they cannot be bought: ${cotsWithout.map(i => `<a href="#/engineering/${encodeURIComponent(i.partNumber)}">${esc(i.partNumber)}</a>`).join(', ')}.</div>` : ''}
    <section class="panel">
      <header><h2>Items</h2><span class="spacer"></span>
        <select class="select" id="class-filter"><option value="">All classes (${all.length})</option>${Object.entries(CLASS_LABEL).filter(([k]) => counts[k]).map(([k, l]) => `<option value="${k}" ${filter === k ? 'selected' : ''}>${esc(l)} (${counts[k]})</option>`).join('')}</select></header>
      ${!list.length ? emptyBlock('No items', 'Items are created by manufacturing engineering.') : `
      <div class="table-wrap"><table class="table"><thead><tr><th>Part</th><th>Description</th><th>Make / buy</th><th>Class</th><th class="num">Approved MPNs</th></tr></thead><tbody>
        ${list.map(i => `<tr class="click" data-part="${esc(i.partNumber)}"><td><b>${esc(i.partNumber)}</b></td><td>${esc(i.description)}</td><td>${esc(i.makeBuy)}</td>
          <td><span class="pill ${i.class === 'Unclassified' ? 'warn' : 'none'}">${esc(CLASS_LABEL[i.class] ?? i.class)}</span></td>
          <td class="num">${MPN_CLASSES.includes(i.class) ? (approved.get(i.partNumber) ?? 0) : '<span class="faint">—</span>'}</td></tr>`).join('')}
      </tbody></table></div>`}
    </section>`;
  $('#class-filter', page).onchange = e => { location.hash = e.target.value ? `#/engineering?class=${e.target.value}` : '#/engineering'; };
  $$('[data-part]', page).forEach(r => r.addEventListener('click', () => { location.hash = `#/engineering/${encodeURIComponent(r.dataset.part)}`; }));
}

async function itemDetail(page, ctx, partNumber) {
  const me = ctx.me;
  const engineer = can(me, 'ManufacturingEngineer');
  const proposer = can(me, 'ManufacturingEngineer', 'Buyer', 'MaintenancePlanner');
  const approver = can(me, 'EngineeringApprover');
  const [items, mpns] = await Promise.all([tryGet('/api/engineering/items'), tryGet(`/api/engineering/manufacturer-parts?part=${encodeURIComponent(partNumber)}`)]);
  if (items.error) { page.innerHTML = errorBlock(items.error, 'items'); return; }
  const item = items.data.find(i => i.partNumber === partNumber);
  if (!item) { page.innerHTML = emptyBlock('Item not found', `There is no part ${partNumber}.`); return; }
  const list = mpns.data ?? [];
  const mine = m => m.proposedBy === `person:${me.personId}`;
  const takesMpns = MPN_CLASSES.includes(item.class);
  const reload = () => itemDetail(page, ctx, partNumber);

  page.innerHTML = `
    <div class="page-head"><div><a href="#/engineering" class="faint">← Items</a><h1>${esc(item.partNumber)}</h1><div class="sub">${esc(item.description)} · ${esc(item.makeBuy)} · ${esc(item.unitOfMeasure)} · lead time ${item.leadTimeDays} days</div></div>
      <div class="spacer"></div>${engineer ? '<button class="btn" id="classify">Classify</button>' : ''}</div>
    <section class="panel"><header><h2>Class</h2></header>
      <p><span class="pill ${item.class === 'Unclassified' ? 'warn' : 'info'}">${esc(CLASS_LABEL[item.class] ?? item.class)}</span>
      ${item.class === 'Unclassified' ? ' Not classified yet. Forge applies no class-based rule to it and does not guess one.' : ''}
      ${item.class === 'Cots' ? ' Commercial off-the-shelf: the manufacturer owns the design and its revisions. It is bought only as an approved manufacturer part.' : ''}
      ${item.class === 'BuildToPrint' ? ' Bought, made by a supplier to our drawing.' : ''}</p>
    </section>
    <section class="panel"><header><h2>Manufacturer parts</h2><span class="spacer"></span>
      ${takesMpns && proposer ? '<button class="btn small primary" id="propose">Propose a manufacturer part</button>' : ''}</header>
      ${!takesMpns ? `<div class="muted">${item.class === 'Unclassified' ? 'Classify the item first.' : `${esc(CLASS_LABEL[item.class])} items are defined by our drawing or specification, not a manufacturer part number.`}</div>`
        : !list.length ? emptyBlock('None recorded', item.class === 'Cots' ? 'A COTS item cannot be bought until a manufacturer part is approved.' : 'Optional for this class.')
        : `<div class="table-wrap"><table class="table"><thead><tr><th>Manufacturer</th><th>MPN</th><th>Mfr rev</th><th>Lifecycle</th><th>Status</th><th>Proposed</th><th>Approved</th><th></th></tr></thead><tbody>
          ${list.map((m, i) => `<tr class="${m.status === 'Withdrawn' ? 'retired' : ''}"><td>${esc(m.manufacturer)}</td><td><b>${esc(m.mpn)}</b>${m.datasheet ? `<div class="faint">${esc(m.datasheet)}</div>` : ''}</td><td>${esc(m.manufacturerRevision ?? '—')}</td>
            <td><span class="pill ${LIFECYCLE_TONE[m.lifecycle]}">${esc(LIFECYCLE_LABEL[m.lifecycle])}</span></td>
            <td><span class="pill ${STATUS_TONE[m.status]}">${esc(m.status)}</span></td>
            <td>${esc(who(m.proposedBy))}<div class="faint">${esc(fmtDateTime(m.proposedAt))}</div></td>
            <td>${m.approvedBy ? `${esc(who(m.approvedBy))}<div class="faint">${esc(fmtDateTime(m.approvedAt))}</div>` : '—'}</td>
            <td class="nowrap">
              ${m.status === 'Proposed' && approver && !mine(m) ? `<button class="btn small primary" data-approve="${i}">Approve</button>` : ''}
              ${m.status === 'Proposed' && approver && mine(m) ? '<span class="faint">You proposed it — someone else approves</span>' : ''}
              ${m.status !== 'Withdrawn' && proposer ? `<button class="btn small" data-lifecycle="${i}">Lifecycle</button>` : ''}
              ${m.status !== 'Withdrawn' && approver ? `<button class="btn small ghost" data-withdraw="${i}">Withdraw</button>` : ''}</td></tr>`).join('')}
        </tbody></table></div>`}
    </section>`;

  $('#classify', page)?.addEventListener('click', () => formModal({
    title: `Classify ${item.partNumber}`, detail: 'The class decides purchasing, documentation, inspection and substitution rules for this item.', reasonRequired: true, confirmLabel: 'Classify',
    fields: [{ name: 'class', label: 'Class', type: 'select', value: item.class === 'Unclassified' ? '' : item.class, blank: 'Choose…', options: classesFor(item.makeBuy), required: true }],
  }, (v, reason) => command(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/classification`, v, reason), reload));
  $('#propose', page)?.addEventListener('click', () => formModal({
    title: `Propose a manufacturer part for ${item.partNumber}`, detail: 'Recorded as proposed. A different person with COTS authority approves it before it can be bought.', confirmLabel: 'Propose',
    fields: [
      { name: 'manufacturer', label: 'Manufacturer', required: true, placeholder: 'SKF' },
      { name: 'mpn', label: 'Manufacturer part number', required: true, placeholder: '6205-2RSH' },
      { name: 'manufacturerRevision', label: 'Manufacturer revision (if any)' },
      { name: 'lifecycle', label: 'Lifecycle, as the manufacturer states it', type: 'select', value: 'Unknown', options: Object.entries(LIFECYCLE_LABEL) },
      { name: 'datasheet', label: 'Datasheet reference', placeholder: 'Document number or link' },
      { name: 'note', label: 'Note', type: 'textarea' },
    ],
  }, (v, reason) => command(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/manufacturer-parts`, v, reason), reload));
  const pick = i => list[Number(i)];
  const act = async (verb, m, title, detail, extra = {}, reasonRequired = true) => {
    const done = await formModal({ title, detail, reasonRequired, confirmLabel: title.split(' ')[0], fields: [] },
      (_, reason) => command(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/manufacturer-parts/${verb}`, { manufacturer: m.manufacturer, mpn: m.mpn, ...extra }, reason));
    if (done) await reload();
  };
  $$('[data-approve]', page).forEach(b => b.addEventListener('click', () => {
    const m = pick(b.dataset.approve);
    act('approve', m, `Approve ${m.manufacturer} ${m.mpn}`, `${item.partNumber} may then be bought as this part.`, {}, false);
  }));
  $$('[data-withdraw]', page).forEach(b => b.addEventListener('click', () => {
    const m = pick(b.dataset.withdraw);
    act('withdraw', m, `Withdraw ${m.manufacturer} ${m.mpn}`, 'It stops being an approved way to buy this item. Its history stays.');
  }));
  $$('[data-lifecycle]', page).forEach(b => b.addEventListener('click', () => {
    const m = pick(b.dataset.lifecycle);
    formModal({ title: `Lifecycle of ${m.manufacturer} ${m.mpn}`, detail: 'Record what the manufacturer now states, with where you saw it in the reason.', reasonRequired: true, confirmLabel: 'Record',
      fields: [{ name: 'lifecycle', label: 'Lifecycle', type: 'select', value: m.lifecycle, options: Object.entries(LIFECYCLE_LABEL) }] },
    (v, reason) => command(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/manufacturer-parts/lifecycle`, { manufacturer: m.manufacturer, mpn: m.mpn, lifecycle: v.lifecycle }, reason), reload);
  }));
}
