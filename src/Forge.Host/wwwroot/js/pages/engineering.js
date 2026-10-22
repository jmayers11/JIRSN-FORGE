import { tryGet, command, upload, download } from '../api.js';
import { who } from '../people.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, toast, drawer, confirmWithReason } from '../ui.js';
import { formModal, canAction, today, csvImportModal } from '../forms.js';
import { historyButton } from '../history.js';
import { appendAttachments, TYPE_LABEL, MARKING_LABEL, markings, markingPills, fileSize, fileUrl } from '../attachments.js';

// Engineering (V2.1): what each item is. The class decides how it is bought, documented, inspected and
// substituted; nothing is guessed — bought items start Unclassified and are worked off from this list.
// COTS items are bought as manufacturer parts, proposed by one person and approved by another.

export const CLASS_LABEL = {
  Unclassified: 'Unclassified', Make: 'Make', BuildToPrint: 'Build to print', Cots: 'COTS', RawMaterial: 'Raw material',
  StandardHardware: 'Standard hardware', Consumable: 'Consumable', Mro: 'MRO / spare', Tooling: 'Tooling',
  Service: 'Service / special process', OtherControlled: 'Other controlled', Packaging: 'Packaging',
};
const ALWAYS_BOUGHT = ['BuildToPrint', 'Cots', 'RawMaterial', 'StandardHardware', 'Consumable', 'Service'];
const LIFECYCLE_LABEL = { Active: 'Active', NotRecommended: 'Not recommended for new designs', LastTimeBuy: 'Last-time buy', EndOfLife: 'End of life', Unknown: 'Unknown' };
const LIFECYCLE_TONE = { Active: 'ok', NotRecommended: 'warn', LastTimeBuy: 'warn', EndOfLife: 'bad', Unknown: 'none' };
const STATUS_TONE = { Approved: 'ok', Proposed: 'info', Withdrawn: 'none' };
const MPN_CLASSES = ['Cots', 'RawMaterial', 'StandardHardware', 'Consumable', 'Mro', 'Tooling', 'OtherControlled'];

const classesFor = makeBuy => Object.entries(CLASS_LABEL)
  .filter(([k]) => k !== 'Unclassified' && (k === 'Make' ? makeBuy === 'Make' : makeBuy === 'Buy' || !ALWAYS_BOUGHT.includes(k)));
const creationClassesFor = makeBuy => Object.entries(CLASS_LABEL)
  .filter(([k]) => k === 'Unclassified' || (k === 'Make' ? makeBuy === 'Make' : makeBuy === 'Buy' || !ALWAYS_BOUGHT.includes(k)));

export function routingOperationBody(v) {
  const sequence = Number(v.sequence), setupMinutes = Number(v.setupMinutes), runMinutesPerUnit = Number(v.runMinutesPerUnit), crewSize = Number(v.crewSize);
  if (!Number.isInteger(sequence) || sequence < 1 || sequence > 9999) throw new Error('Sequence must be from 1 to 9999.');
  if (!Number.isFinite(setupMinutes) || setupMinutes < 0 || !Number.isFinite(runMinutesPerUnit) || runMinutesPerUnit < 0) throw new Error('Set-up and run minutes must be zero or greater.');
  if (!Number.isInteger(crewSize) || crewSize < 1 || crewSize > 20) throw new Error('Crew size must be from 1 to 20.');
  const setupFamily = v.setupFamily?.trim().toUpperCase() || null;
  const familySetupMinutes = v.familySetupMinutes == null || v.familySetupMinutes === '' ? null : Number(v.familySetupMinutes);
  if ((setupFamily == null) !== (familySetupMinutes == null)) throw new Error('Enter both a set-up family and its same-family set-up minutes.');
  if (familySetupMinutes != null && (!Number.isFinite(familySetupMinutes) || familySetupMinutes < 0 || familySetupMinutes > setupMinutes)) {
    throw new Error('Same-family set-up minutes must be between zero and the full set-up time.');
  }
  const codes = value => [...new Set(String(value ?? '').split(/[\n,]/).map(code => code.trim().toUpperCase()).filter(Boolean))];
  return {
    sequence, description: v.description?.trim(), workCenterCode: v.workCenterCode, activityCode: v.activityCode,
    eligibleMachines: codes(v.eligibleMachines), requiredQualifications: codes(v.requiredQualifications),
    setupMinutes, runMinutesPerUnit, crewSize, setupFamily, familySetupMinutes,
  };
}

export async function engineeringPage(page, ctx) {
  const [first, second] = ctx.params;
  if (first === 'documents') return second ? documentDetail(page, ctx, decodeURIComponent(second)) : documentList(page, ctx);
  if (first === 'changes') return second ? changeDetail(page, ctx, decodeURIComponent(second)) : changeList(page, ctx);
  if (first === 'structure') return parentStructurePage(page, ctx, second ? decodeURIComponent(second) : null);
  if (first === 'actions') return actionList(page, ctx);
  if (first === 'alternates') return alternateList(page, ctx);
  if (first === 'obsolescence') return obsolescenceList(page, ctx);
  return first ? itemDetail(page, ctx, decodeURIComponent(first)) : itemList(page, ctx);
}

const tabs = active => `<div class="seg" style="margin-bottom:12px">${[['items', 'Items', '#/engineering'], ['structure', 'Parent Parts', '#/engineering/structure'], ['documents', 'Documents', '#/engineering/documents'], ['changes', 'Changes', '#/engineering/changes'], ['actions', 'Change actions', '#/engineering/actions'], ['alternates', 'Alternates', '#/engineering/alternates'], ['obsolescence', 'Obsolescence', '#/engineering/obsolescence']]
  .map(([k, l, href]) => `<button class="${active === k ? 'on' : ''}" data-nav="${href}">${l}</button>`).join('')}</div>`;
const wireTabs = page => $$('[data-nav]', page).forEach(b => b.addEventListener('click', () => { location.hash = b.dataset.nav; }));

async function itemList(page, ctx) {
  const engineer = canAction(ctx.me, 'engineering.manage');
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
    <div class="page-head"><div><h1>Engineering</h1><div class="sub">Items and what they are. The class decides how an item is bought, documented, inspected and substituted.</div></div><span class="spacer"></span>${engineer ? '<button class="btn" id="import-items-csv">Import items CSV</button><button class="btn primary" id="new-item">+ New item</button>' : ''}</div>
    ${tabs('items')}
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
  wireTabs(page);
  $('#new-item', page)?.addEventListener('click', () => {
    let partNumber = '';
    formModal({ title: 'Create engineering item', detail: 'Defines a new part master record. Create and release a draft revision before scheduling production.', confirmLabel: 'Create item', reasonRequired: true,
      fields: [
        { name: 'partNumber', label: 'Part number', code: true, required: true, placeholder: 'ASSY-200' },
        { name: 'description', label: 'Description', required: true },
        { name: 'unitOfMeasure', label: 'Unit of measure', type: 'select', value: 'EA', required: true, options: [['EA', 'EA — Each'], ['KG', 'KG — Kilogram'], ['M', 'M — Meter'], ['L', 'L — Liter'], ['SET', 'SET — Kit / Set']] },
        { name: 'makeBuy', label: 'Make or buy', type: 'select', value: 'Make', options: [['Make', 'Make (manufactured in-house)'], ['Buy', 'Buy (purchased from supplier)']], onChange: (element, modal) => {
          const classSelect = $('#f-class', modal), current = classSelect.value;
          const classes = creationClassesFor(element.value);
          classSelect.innerHTML = classes.map(([value, label]) => `<option value="${esc(value)}" ${value === current ? 'selected' : ''}>${esc(label)}</option>`).join('');
          if (!classes.some(([value]) => value === current)) classSelect.value = element.value === 'Make' ? 'Make' : 'Unclassified';
        } },
        { name: 'class', label: 'Engineering class', type: 'select', value: 'Make', options: creationClassesFor('Make') },
        { name: 'leadTimeDays', label: 'Planning lead time (days)', type: 'number', value: 5, min: 0, max: 365, required: true },
        { name: 'lotTracked', label: 'Track inventory by lot number', type: 'check', text: 'Track inventory by lot number', value: true },
        { name: 'serialized', label: 'Track individual serial numbers', type: 'check', text: 'Track individual serial numbers', value: false },
      ] }, (v, reason) => { partNumber = v.partNumber; return command('/api/engineering/items', { ...v, leadTimeDays: Number(v.leadTimeDays) }, reason); },
    () => { location.hash = `#/engineering/${encodeURIComponent(partNumber)}`; });
  });
  $('#import-items-csv', page)?.addEventListener('click', () => csvImportModal({
    title: 'Import engineering items', detail: 'Each row creates one item master. Existing part numbers are reported as rejected rows.',
    columns: [
      { name: 'partNumber', label: 'Part number', required: true }, { name: 'description', label: 'Description', required: true },
      { name: 'unitOfMeasure', label: 'Unit of measure', required: true }, { name: 'makeBuy', label: 'Make or buy', required: true, options: ['Make', 'Buy'] },
      { name: 'class', label: 'Engineering class', required: true, options: Object.keys(CLASS_LABEL) },
      { name: 'leadTimeDays', label: 'Lead time days', type: 'number', required: true, min: 0, max: 365 },
      { name: 'lotTracked', label: 'Lot tracked', type: 'boolean', required: true }, { name: 'serialized', label: 'Serialized', type: 'boolean', required: true },
    ], sampleRows: [{ partNumber: 'ASSY-200', description: 'Manifold assembly', unitOfMeasure: 'EA', makeBuy: 'Make', class: 'Make', leadTimeDays: 5, lotTracked: true, serialized: false }],
    onRow: (row, reason) => command('/api/engineering/items', row, reason),
  }, () => itemList(page, ctx)));
  $('#class-filter', page).onchange = e => { location.hash = e.target.value ? `#/engineering?class=${e.target.value}` : '#/engineering'; };
  $$('[data-part]', page).forEach(r => r.addEventListener('click', () => { location.hash = `#/engineering/${encodeURIComponent(r.dataset.part)}`; }));
}

async function itemDetail(page, ctx, partNumber) {
  const me = ctx.me;
  const engineer = canAction(me, 'engineering.manage');
  const proposer = canAction(me, 'engineering.cots.propose');
  const approver = canAction(me, 'engineering.cots.manage');
  const [items, mpns] = await Promise.all([tryGet('/api/engineering/items'), tryGet(`/api/engineering/manufacturer-parts?part=${encodeURIComponent(partNumber)}`)]);
  if (items.error) { page.innerHTML = errorBlock(items.error, 'items'); return; }
  const item = items.data.find(i => i.partNumber === partNumber);
  if (!item) { page.innerHTML = emptyBlock('Item not found', `There is no part ${partNumber}.`); return; }
  const list = mpns.data ?? [];
  const mine = m => m.proposedBy === `person:${me.personId}`;
  const takesMpns = MPN_CLASSES.includes(item.class);
  const reload = () => itemDetail(page, ctx, partNumber);

  page.innerHTML = `
    <div class="page-head"><div><a href="#/engineering" class="faint">← Items</a><h1>${esc(item.partNumber)} ${historyButton(item.partNumber, 'item')}</h1><div class="sub">${esc(item.description)} · ${esc(item.makeBuy)} · ${esc(item.unitOfMeasure)} · lead time ${item.leadTimeDays} days</div></div>
      <div class="spacer"></div>${engineer ? `<a class="btn primary" href="#/engineering/structure/${encodeURIComponent(item.partNumber)}">Parent part workspace</a><button class="btn" id="classify">Classify</button>` : ''}</div>
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

  appendRequirements(page, ctx, item);
  await appendRoutingOperations(page, ctx, item);
  appendAlternates(page, ctx, item, items.data);
  appendAttachments(page, { type: 'Item', id: item.partNumber, me, title: 'Documents', note: 'Drawings, specifications and datasheets for this item. Released revisions apply; reference files never do.' });

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

// ── Documents (V2.2) ──────────────────────────────────────────────────────────────────────────────
// Internal documents are drafted, reviewed, approved by someone other than the author and released.
// External documents (customer, standards body, manufacturer) are recorded as applicable. Reference
// documents are uncontrolled attachments and never production authority.

const AUTHORITY_LABEL = { Internal: 'Internal', External: 'External', Reference: 'Reference' };
const REV_TONE = { Draft: 'none', InReview: 'info', Approved: 'info', Released: 'ok', Applicable: 'ok', Superseded: 'warn', Obsolete: 'bad', Uncontrolled: 'none' };
const REV_LABEL = { InReview: 'In review' };
const TARGET_HREF = {
  ProductionOrder: id => `#/orders/${id}`, PurchaseOrder: () => '#/procurement', Supplier: () => '#/procurement?tab=suppliers', Item: id => `#/engineering/${encodeURIComponent(id)}`,
  ItemRevision: id => `#/engineering/${encodeURIComponent(id.split(':')[0])}`, WorkOrder: () => '#/maintenance', Ncr: () => '#/quality', Program: () => '#/programs', WorkCenter: () => '#/workcenters',
};

const current = d => d.revisions.find(r => r.status === 'Released' || r.status === 'Applicable')
  ?? (d.authority === 'Reference' ? [...d.revisions].reverse().find(r => r.status !== 'Obsolete') : null);
const open = d => d.revisions.find(r => ['Draft', 'InReview', 'Approved'].includes(r.status));
const revPill = r => `<span class="pill ${REV_TONE[r.status] ?? 'none'}">${esc(REV_LABEL[r.status] ?? r.status)}</span>`;

async function documentList(page, ctx) {
  const filter = ctx.query.get('authority') ?? '';
  const res = await tryGet('/api/engineering/documents');
  if (res.error) { page.innerHTML = errorBlock(res.error, 'documents'); return; }
  const all = res.data;
  const list = filter ? all.filter(d => d.authority === filter) : all;
  const day = today();
  const overdue = all.filter(d => current(d)?.reviewBy && current(d).reviewBy < day && d.authority !== 'Reference');
  const waiting = all.filter(d => open(d)?.status === 'InReview' || open(d)?.status === 'Approved');
  page.innerHTML = `
    <div class="page-head"><div><h1>Engineering</h1><div class="sub">Controlled documents: drawings, specifications, procedures and work instructions, with who wrote, approved and released each revision.</div></div>
      <div class="spacer"></div>${canAction(ctx.me, 'engineering.documents.manage') ? '<button class="btn primary" id="new-doc">New document</button>' : ''}</div>
    ${tabs('documents')}
    ${waiting.length ? `<div class="notice">${waiting.length} revision(s) waiting for approval or release: ${waiting.map(d => `<a href="#/engineering/documents/${encodeURIComponent(d.number)}">${esc(d.number)}</a>`).join(', ')}.</div>` : ''}
    ${overdue.length ? `<div class="notice warn">${overdue.length} document(s) are past their review date: ${overdue.map(d => `<a href="#/engineering/documents/${encodeURIComponent(d.number)}">${esc(d.number)}</a>`).join(', ')}.</div>` : ''}
    <section class="panel"><header><h2>Documents</h2><span class="spacer"></span>
      <select class="select" id="authority-filter"><option value="">All (${all.length})</option>${Object.keys(AUTHORITY_LABEL).map(k => `<option value="${k}" ${filter === k ? 'selected' : ''}>${k} (${all.filter(d => d.authority === k).length})</option>`).join('')}</select></header>
      ${!list.length ? emptyBlock('No documents', 'Documents you may see appear here. Program documents need program access; marked documents need the marking.') : `
      <div class="table-wrap"><table class="table"><thead><tr><th>Document</th><th>Title</th><th>Kind</th><th>Authority</th><th>In force</th><th>Open revision</th></tr></thead><tbody>
      ${list.map(d => {
        const c = current(d); const o = open(d);
        return `<tr class="click" data-doc="${esc(d.number)}"><td><b>${esc(d.number)}</b> ${markingPills(d.markings)}</td><td>${esc(d.title)}</td><td>${esc(TYPE_LABEL[d.type] ?? d.type)}</td>
          <td>${d.authority === 'External' ? `External <span class="faint">(${esc(d.externalAuthority)})</span>` : d.authority === 'Reference' ? '<span class="faint">Reference</span>' : 'Internal'}</td>
          <td>${c ? `${esc(c.revision)} ${revPill(c)}` : '<span class="pill warn">None</span>'}</td><td>${o ? `${esc(o.revision)} ${revPill(o)}` : '<span class="faint">—</span>'}</td></tr>`;
      }).join('')}
      </tbody></table></div>`}
    </section>`;
  wireTabs(page);
  appendEnforcement(page, ctx);
  $('#authority-filter', page).onchange = e => { location.hash = e.target.value ? `#/engineering/documents?authority=${e.target.value}` : '#/engineering/documents'; };
  $$('[data-doc]', page).forEach(r => r.addEventListener('click', () => { location.hash = `#/engineering/documents/${encodeURIComponent(r.dataset.doc)}`; }));
  $('#new-doc', page)?.addEventListener('click', async () => {
    const programs = (await tryGet('/api/programs/')).data ?? [];
    formModal({
      title: 'New document', detail: 'Internal: we control it. External: a customer, standards body or manufacturer controls it; Forge records which revision applies. Reference: kept for information, never production authority.', confirmLabel: 'Create',
      fields: [
        { name: 'number', label: 'Document number', required: true, code: true, placeholder: 'DWG-100' },
        { name: 'title', label: 'Title', required: true },
        { name: 'type', label: 'Kind', type: 'select', value: 'Drawing', options: Object.entries(TYPE_LABEL) },
        { name: 'authority', label: 'Authority', type: 'select', value: 'Internal', options: Object.entries(AUTHORITY_LABEL) },
        { name: 'externalAuthority', label: 'Controlled by (external only)', placeholder: 'ASTM International, the customer…' },
        { name: 'externalNumber', label: 'Their number (external only)' },
        { name: 'programId', label: 'Program (optional)', type: 'select', blank: 'Not program-specific', options: programs.map(p => [p.programId, `${p.code} — ${p.name}`]), hint: 'A program document is visible only to that program\'s team.' },
        { name: 'markings', label: 'Data marking', type: 'select', value: 'None', options: Object.entries(MARKING_LABEL), hint: 'You must hold the marking yourself.' },
      ],
    }, (v, reason) => command('/api/engineering/documents', v, reason), r => { location.hash = `#/engineering/documents/${encodeURIComponent(r.entityId)}`; });
  });
}

async function documentDetail(page, ctx, number) {
  const me = ctx.me;
  const res = await tryGet(`/api/engineering/documents/${encodeURIComponent(number)}`);
  if (res.error) { page.innerHTML = `<a href="#/engineering/documents" class="faint">← Documents</a>${errorBlock(res.error, `document ${number}`)}`; return; }
  const { document: d, links } = res.data;
  const manager = canAction(me, 'engineering.documents.manage');
  const approver = canAction(me, 'engineering.documents.approve');
  const releaser = canAction(me, 'engineering.documents.release');
  const mine = r => r.author === `person:${me.personId}`;
  const reload = () => documentDetail(page, ctx, number);
  const base = `/api/engineering/documents/${encodeURIComponent(d.number)}`;
  const c = current(d);
  const o = open(d);
  const canAdd = manager && (d.authority === 'Reference' || !o);

  const actions = r => {
    const out = [];
    if (r.status === 'Draft' && manager) out.push(['edit', 'Edit']);
    if (r.status === 'Draft' && d.authority === 'Internal' && manager) out.push(['submit', 'Submit for review']);
    if (r.status === 'Draft' && d.authority === 'External' && releaser && !mine(r)) out.push(['release', 'Record as applicable']);
    if (r.status === 'InReview' && approver && !mine(r)) out.push(['approve', 'Approve']);
    if (r.status === 'InReview' && approver) out.push(['return', 'Return']);
    if (r.status === 'Approved' && releaser && !mine(r)) out.push(['release', 'Release']);
    if (!['Superseded', 'Obsolete'].includes(r.status) && releaser) out.push(['obsolete', 'Obsolete']);
    return out;
  };

  page.innerHTML = `
    <div class="page-head"><div><a href="#/engineering/documents" class="faint">← Documents</a><h1>${esc(d.number)} ${markingPills(d.markings)} ${historyButton(d.number, 'document')}</h1>
      <div class="sub">${esc(d.title)} · ${esc(TYPE_LABEL[d.type] ?? d.type)} · ${d.authority === 'External' ? `External, controlled by ${esc(d.externalAuthority)}${d.externalNumber ? ` (${esc(d.externalNumber)})` : ''}` : esc(d.authority)}${d.programId ? ' · program document' : ''}</div></div>
      <div class="spacer"></div>
      ${manager ? '<button class="btn" id="change-doc">Change</button>' : ''}${canAdd ? '<button class="btn primary" id="add-rev">Add a revision</button>' : ''}</div>
    ${d.authority === 'Reference' ? '<div class="notice">Reference document: kept for information. It is never production authority and never satisfies a documentation requirement.</div>' : ''}
    ${d.authority !== 'Reference' && !c ? '<div class="notice warn">No revision is in force yet.</div>' : ''}
    <section class="panel"><header><h2>Revisions</h2></header>
      ${!d.revisions.length ? emptyBlock('No revisions', 'Add the first revision with its file.') : `
      <div class="table-wrap"><table class="table"><thead><tr><th>Rev</th><th>Status</th><th>File</th><th>Written</th><th>Approved</th><th>${d.authority === 'External' ? 'Recorded' : 'Released'}</th><th>Effective</th><th>Review by</th><th></th></tr></thead><tbody>
      ${[...d.revisions].reverse().map(r => `<tr class="${['Superseded', 'Obsolete'].includes(r.status) ? 'retired' : ''}">
        <td><b>${esc(r.revision)}</b>${r.changeNote ? `<div class="faint">${esc(r.changeNote)}</div>` : ''}</td><td>${revPill(r)}</td>
        <td>${r.fileName ? `<button class="btn small ghost" data-file="${esc(r.revision)}|${esc(r.fileName)}">${esc(r.fileName)}</button><div class="faint">${esc(fileSize(r.fileSize))}</div>` : '<span class="faint">No file yet</span>'}</td>
        <td>${esc(who(r.author))}<div class="faint">${esc(fmtDateTime(r.authoredAt))}</div></td>
        <td>${r.approvedBy ? `${esc(who(r.approvedBy))}<div class="faint">${esc(fmtDateTime(r.approvedAt))}</div>` : '—'}</td>
        <td>${r.releasedBy ? `${esc(who(r.releasedBy))}<div class="faint">${esc(fmtDateTime(r.releasedAt))}</div>` : '—'}</td>
        <td>${r.effectiveFrom ? `${esc(fmtDate(r.effectiveFrom))}${r.effectiveTo ? ` – ${esc(fmtDate(r.effectiveTo))}` : ''}` : '—'}</td>
        <td>${r.reviewBy ? `<span class="${r.reviewBy < today() && !['Superseded', 'Obsolete'].includes(r.status) ? 'c-warn' : ''}">${esc(fmtDate(r.reviewBy))}</span>` : '—'}</td>
        <td class="nowrap">${actions(r).map(([k, l]) => `<button class="btn small ${['approve', 'release'].includes(k) ? 'primary' : k === 'obsolete' ? 'ghost' : ''}" data-act="${k}" data-rev="${esc(r.revision)}">${esc(l)}</button>`).join('')}
          ${mine(r) && approver && ['InReview', 'Approved'].includes(r.status) ? '<div class="faint">You wrote it — someone else approves and releases</div>' : ''}</td></tr>`).join('')}
      </tbody></table></div>`}
    </section>
    <section class="panel"><header><h2>Where it is attached</h2></header>
      ${!links.length ? emptyBlock('Not attached', 'Attach it from an order, purchase order, work order, NCR, supplier or item.') : `
      <div class="table-wrap"><table class="table"><thead><tr><th>Record</th><th>Use</th><th>Revision</th><th>Attached</th><th>Detached</th></tr></thead><tbody>
      ${links.map(l => `<tr class="${l.detachedAt ? 'retired' : ''}"><td><a href="${(TARGET_HREF[l.targetType] ?? (() => '#/'))(l.targetId)}">${esc(l.targetLabel)}</a></td>
        <td>${esc(l.purpose)}</td><td>${esc(l.revision ?? 'In force')}</td><td>${esc(who(l.linkedBy))} <span class="faint">${esc(fmtDateTime(l.linkedAt))}</span></td>
        <td>${l.detachedAt ? `${esc(who(l.detachedBy))} <span class="faint">${esc(fmtDateTime(l.detachedAt))}: ${esc(l.detachReason)}</span>` : '—'}</td></tr>`).join('')}
      </tbody></table></div>`}
    </section>`;

  appendRules(page, ctx, d);
  $$('[data-file]', page).forEach(b => b.addEventListener('click', () => {
    const [rev, name] = b.dataset.file.split('|');
    download(fileUrl(d.number, rev), name).catch(e => toast(e.message, true));
  }));
  $('#change-doc', page)?.addEventListener('click', () => formModal({
    title: `Change ${d.number}`, detail: 'Markings decide who may see the document and its files. You must hold any marking you set.', reasonRequired: true, confirmLabel: 'Save',
    fields: [
      { name: 'title', label: 'Title', value: d.title, required: true },
      { name: 'markings', label: 'Data marking', type: 'select', value: markings(d.markings)[0] ?? 'None', options: Object.entries(MARKING_LABEL) },
    ],
  }, (v, reason) => command(base, v, reason), reload));
  $('#add-rev', page)?.addEventListener('click', () => formModal({
    title: `Add a revision to ${d.number}`,
    detail: d.authority === 'Internal' ? 'Starts as a draft. Submit it for review; a different person approves and releases it.'
      : d.authority === 'External' ? 'Starts as a draft. A different person with release authority records it as the applicable revision.'
        : 'Kept as reference, uncontrolled.',
    confirmLabel: 'Add',
    fields: [
      { name: 'revision', label: 'Revision', required: true, code: true, placeholder: d.authority === 'External' ? 'e.g. 2021' : 'e.g. A' },
      { name: 'file', label: 'File (at most 50 MB)', type: 'file', required: d.authority === 'Reference' },
      { name: 'changeNote', label: 'What changed', type: 'textarea' },
      { name: 'reviewBy', label: 'Review by (optional)', type: 'date' },
    ],
  }, async (v, reason) => {
    const sha = v.file ? await upload(v.file) : null;
    return command(`${base}/revisions`, { revision: v.revision, fileSha256: sha, fileName: v.file?.name ?? null, changeNote: v.changeNote, reviewBy: v.reviewBy }, reason);
  }, reload));

  $$('[data-act]', page).forEach(b => b.addEventListener('click', () => {
    const r = d.revisions.find(x => x.revision === b.dataset.rev);
    const url = `${base}/revisions/${encodeURIComponent(r.revision)}`;
    const act = b.dataset.act;
    if (act === 'edit') {
      formModal({ title: `Edit draft ${d.number} ${r.revision}`, detail: 'Choose a new file to replace the current one, or leave it empty to keep it.', confirmLabel: 'Save',
        fields: [
          { name: 'file', label: `File${r.fileName ? ` (now ${r.fileName})` : ''}`, type: 'file' },
          { name: 'changeNote', label: 'What changed', type: 'textarea', value: r.changeNote ?? '' },
          { name: 'reviewBy', label: 'Review by', type: 'date', value: r.reviewBy ?? '' },
        ] },
      async (v, reason) => {
        const sha = v.file ? await upload(v.file) : r.fileSha256;
        return command(url, { fileSha256: sha, fileName: v.file?.name ?? r.fileName, changeNote: v.changeNote, reviewBy: v.reviewBy }, reason);
      }, reload);
      return;
    }

    if (act === 'release') {
      formModal({ title: d.authority === 'External' ? `Record ${d.number} ${r.revision} as applicable` : `Release ${d.number} ${r.revision}`,
        detail: c ? `Revision ${c.revision} is superseded from the effective date.` : 'It becomes the revision in force from the effective date.', reasonRequired: true, confirmLabel: d.authority === 'External' ? 'Record' : 'Release',
        fields: [{ name: 'effectiveFrom', label: 'Effective from', type: 'date', value: today() }] },
      (v, reason) => command(`${url}/release`, v, reason), reload);
      return;
    }

    const text = {
      submit: ['Submit for review', 'Someone other than you approves it.', false],
      approve: ['Approve', 'Then someone with release authority releases it.', false],
      return: ['Return to the author', 'It goes back to draft. Say what needs to change.', true],
      obsolete: ['Make obsolete', 'It no longer applies anywhere. This cannot be undone.', true],
    }[act];
    formModal({ title: `${text[0]}: ${d.number} ${r.revision}`, detail: text[1], reasonRequired: text[2], confirmLabel: text[0], fields: [] },
      (_, reason) => command(`${url}/${act}`, {}, reason), reload);
  }));
}

// ── What each item revision is built to (V2.3) ───────────────────────────────────────────────────
async function appendRoutingOperations(page, ctx, item) {
  const section = document.createElement('section');
  section.className = 'panel';
  section.setAttribute('aria-label', 'Routing operations');
  page.append(section);
  const manage = canAction(ctx.me, 'engineering.manage');
  const part = encodeURIComponent(item.partNumber);
  const [revisionResult, resourcesResult, qualificationsResult] = await Promise.all([
    tryGet(`/api/engineering/items/${part}/revisions`),
    manage ? tryGet('/api/resources/') : Promise.resolve({ data: null }),
    manage ? tryGet('/api/competency/qualification-types') : Promise.resolve({ data: [] }),
  ]);
  if (revisionResult.error) { section.innerHTML = `<header><h2>Routing operations</h2></header>${errorBlock(revisionResult.error, 'item revisions')}`; return; }
  let revisions = revisionResult.data ?? [];
  let selected = revisions.find(revision => revision.status === 'Draft')?.revision ?? revisions.at(-1)?.revision ?? '';
  let renderSequence = 0;
  const resources = resourcesResult.data;
  const workCenters = (resources?.workCenters ?? []).filter(record => record.active !== false);
  const activities = (resources?.activityTypes ?? []).filter(record => record.active !== false);
  const qualifications = qualificationsResult.data ?? [];
  const revisionUrl = revision => `/api/engineering/items/${part}/revisions/${encodeURIComponent(revision)}`;

  async function reload(prefer = selected) {
    const result = await tryGet(`/api/engineering/items/${part}/revisions`);
    if (result.error) { section.innerHTML = `<header><h2>Routing operations</h2></header>${errorBlock(result.error, 'item revisions')}`; return; }
    revisions = result.data ?? [];
    selected = revisions.some(revision => revision.revision === prefer) ? prefer
      : revisions.find(revision => revision.status === 'Draft')?.revision ?? revisions.at(-1)?.revision ?? '';
    await render();
  }

  async function render() {
    const sequence = ++renderSequence;
    const revision = revisions.find(candidate => candidate.revision === selected) ?? null;
    const detailResult = revision ? await tryGet(revisionUrl(selected)) : null;
    if (sequence !== renderSequence) return;
    const operations = detailResult?.data?.routing ?? [];
    const isDraft = revision?.status === 'Draft';
    section.innerHTML = `
      <header><div><h2>Routing operations</h2><div class="faint">Set-up time is counted once per order. A matching set-up family can reduce it on the same machine.</div></div><span class="spacer"></span>
        ${revisions.length ? `<label for="routing-revision">Revision</label><select class="select" id="routing-revision">${revisions.map(candidate => `<option value="${esc(candidate.revision)}" ${candidate.revision === selected ? 'selected' : ''}>${esc(candidate.revision)} · ${esc(candidate.status)}</option>`).join('')}</select>` : ''}
        ${manage && !revisions.some(candidate => candidate.status === 'Draft') ? '<button class="btn small" id="routing-new-revision">New draft revision</button>' : ''}
        ${manage && isDraft ? '<button class="btn small primary" id="routing-add-operation">Add operation</button>' : ''}</header>
      ${!revisions.length ? emptyBlock(item.makeBuy === 'Make' ? 'No routing revision' : 'No item revisions', item.makeBuy === 'Make' ? 'Create a draft revision, then add its routing operations before a separate release.' : 'A routing is added to a draft item revision.') : detailResult?.error ? errorBlock(detailResult.error, 'routing') : operations.length ? `<div class="table-wrap"><table class="table"><thead><tr><th>Step</th><th>Operation</th><th>Work center / activity</th><th>Machines / qualifications</th><th>Set-up</th><th></th></tr></thead><tbody>
        ${operations.map(operation => `<tr><td>${operation.sequence}</td><td>${esc(operation.description)}</td><td>${esc(operation.workCenterCode)} · ${esc(operation.activityCode)}</td><td>${esc(operation.eligibleMachines?.join(', ') || 'Any allowed machine')}<div class="faint">${esc(operation.requiredQualifications?.join(', ') || 'No additional qualifications')}</div></td>
          <td>${Number(operation.setupMinutes).toLocaleString()} min${operation.setupFamily ? `<div class="faint">${esc(operation.setupFamily)} · ${Number(operation.familySetupMinutes).toLocaleString()} min same family</div>` : ''}<div class="faint">Run ${Number(operation.runMinutesPerUnit).toLocaleString()} min/unit · crew ${operation.crewSize}</div></td>
          <td>${manage && isDraft ? `<button class="btn small ghost" data-routing-remove="${operation.sequence}">Remove</button>` : ''}</td></tr>`).join('')}
        </tbody></table></div>` : `<div class="muted">No routing operations in revision ${esc(selected)}.${item.makeBuy === 'Make' ? ' A made item needs at least one operation before release.' : ''}</div>`}
      ${revision && !isDraft ? '<div class="faint" style="margin-top:8px">Released routing definitions are preserved. Create a new draft revision to change them.</div>' : ''}
      ${manage && !resourcesResult.data && resourcesResult.error ? errorBlock(resourcesResult.error, 'plant resources') : ''}
    `;
    $('#routing-revision', section)?.addEventListener('change', event => { selected = event.target.value; render(); });
    $('#routing-new-revision', section)?.addEventListener('click', () => {
      let newRevision = '';
      formModal({ title: `New revision of ${item.partNumber}`, detail: 'Routing operations are added to a draft revision. Release remains a separate controlled action.', confirmLabel: 'Create revision', reasonRequired: true,
        fields: [{ name: 'revision', label: 'Revision code', code: true, required: true, placeholder: 'A, B, 1, 2…' }] },
      (values, reason) => { newRevision = values.revision; return command(`/api/engineering/items/${part}/revisions`, values, reason); },
      () => reload(newRevision));
    });
    $('#routing-add-operation', section)?.addEventListener('click', () => {
      if (!workCenters.length || !activities.length) { toast('Set up at least one active work center and activity before adding a routing operation.', true); return; }
      const nextSequence = Math.min(9999, Math.max(0, ...operations.map(operation => operation.sequence)) + 10);
      formModal({ title: `Add routing operation to ${item.partNumber} rev ${selected}`, detail: 'Eligible machines and extra qualifications are optional. Set-up family and same-family minutes must be entered together; same-family time cannot exceed full set-up.', confirmLabel: 'Add operation', reasonRequired: true,
        fields: [
          { name: 'sequence', label: 'Operation sequence', type: 'number', value: nextSequence, min: 1, max: 9999, required: true },
          { name: 'description', label: 'Operation', required: true, placeholder: 'Cut, wind, inspect…' },
          { name: 'workCenterCode', label: 'Work center', type: 'select', required: true, options: workCenters.map(center => [center.code, `${center.name} (${center.code})`]) },
          { name: 'activityCode', label: 'Activity', type: 'select', required: true, options: activities.map(activity => [activity.code, `${activity.name} (${activity.code})`]) },
          { name: 'eligibleMachines', label: 'Eligible machines (optional)', type: 'textarea', hint: 'Enter machine codes separated by commas or new lines.' },
          { name: 'requiredQualifications', label: 'Additional qualifications (optional)', type: 'textarea', hint: `Known qualifications: ${qualifications.map(qualification => qualification.code).join(', ') || 'none listed'}.` },
          { name: 'setupMinutes', label: 'Full set-up minutes', type: 'number', value: 0, min: 0, step: '0.01', required: true },
          { name: 'runMinutesPerUnit', label: 'Run minutes per unit', type: 'number', value: 0, min: 0, step: '0.01', required: true },
          { name: 'crewSize', label: 'Crew size', type: 'number', value: 1, min: 1, max: 20, required: true },
          { name: 'setupFamily', label: 'Set-up family (optional)', code: true, placeholder: 'FIXTURE-42' },
          { name: 'familySetupMinutes', label: 'Same-family set-up minutes', type: 'number', min: 0, step: '0.01', hint: 'Required with a set-up family; must be no greater than full set-up.' },
        ] },
      (values, reason) => command(`${revisionUrl(selected)}/routing`, routingOperationBody(values), reason), () => reload());
    });
    $$('[data-routing-remove]', section).forEach(button => button.addEventListener('click', async () => {
      const operationSequence = Number(button.dataset.routingRemove);
      const result = await confirmWithReason({ title: `Remove operation ${operationSequence}`, detail: 'The operation is removed from this draft revision. Released revisions remain unchanged.', confirmLabel: 'Remove', reasonRequired: true },
        reason => command(`${revisionUrl(selected)}/routing/${operationSequence}/remove`, {}, reason));
      if (result) await reload();
    }));
  }

  await render();
}

async function parentStructurePage(page, ctx, selectedPart) {
  const { data: items, error: itemsError } = await tryGet('/api/engineering/items');
  if (itemsError) { page.innerHTML = errorBlock(itemsError, 'items'); return; }
  const allItems = items ?? [];
  const manage = canAction(ctx.me, 'engineering.manage');
  const itemByPart = new Map(allItems.map(item => [item.partNumber, item]));

  if (!selectedPart) {
    const candidates = allItems.slice().sort((a, b) => a.partNumber.localeCompare(b.partNumber));
    page.innerHTML = `
      <div class="page-head"><div><h1>Parent part workspace</h1><div class="sub">Choose a part to review its engineering revision, bill of materials and routing operations together.</div></div></div>
      ${tabs('structure')}
      <section class="panel"><header><div><h2>Parent parts</h2><div class="faint">Search by part number or description. The engineering revision shown in the workspace is separate from MRP’s low-level planning code.</div></div><span class="spacer"></span><span class="pill none">${candidates.length} parts</span></header>
        <div class="field" style="max-width:460px"><label for="parent-search">Search parts</label><input class="input" id="parent-search" type="search" placeholder="Part number or description"></div>
        ${candidates.length ? `<div class="table-wrap"><table class="table"><thead><tr><th>Part</th><th>Description</th><th>Make / buy</th><th>Unit</th><th></th></tr></thead><tbody id="parent-parts-rows"></tbody></table></div>` : emptyBlock('No parts yet', 'Create an Engineering item before defining its structure.')}
      </section>`;
    wireTabs(page);
    const search = $('#parent-search', page);
    const rows = $('#parent-parts-rows', page);
    const renderRows = () => {
      if (!rows) return;
      const needle = (search?.value ?? '').trim().toLowerCase();
      const filtered = candidates.filter(item => `${item.partNumber} ${item.description}`.toLowerCase().includes(needle));
      rows.innerHTML = filtered.length ? filtered.map(item => `<tr><td><b>${esc(item.partNumber)}</b></td><td>${esc(item.description)}</td><td>${esc(item.makeBuy)}</td><td>${esc(item.unitOfMeasure)}</td><td><a class="btn small" href="#/engineering/structure/${encodeURIComponent(item.partNumber)}">Open</a></td></tr>`).join('')
        : `<tr><td colspan="5">${emptyBlock('No matches', 'Try a different part number or description.')}</td></tr>`;
    };
    search?.addEventListener('input', renderRows);
    renderRows();
    return;
  }

  const item = itemByPart.get(selectedPart);
  if (!item) {
    page.innerHTML = `${tabs('structure')}<section class="panel">${emptyBlock('Part not found', `There is no part ${selectedPart}.`)}</section>`;
    wireTabs(page);
    return;
  }

  const encodedPart = encodeURIComponent(item.partNumber);
  const [revisionResult, resourcesResult, qualificationsResult] = await Promise.all([
    tryGet(`/api/engineering/items/${encodedPart}/revisions`),
    manage ? tryGet('/api/resources/') : Promise.resolve({ data: null }),
    manage ? tryGet('/api/competency/qualification-types') : Promise.resolve({ data: [] }),
  ]);
  if (revisionResult.error) { page.innerHTML = `${tabs('structure')}<section class="panel">${errorBlock(revisionResult.error, 'item revisions')}</section>`; wireTabs(page); return; }
  const revisions = revisionResult.data ?? [];
  const revisionFromQuery = ctx.query.get('revision');
  let selectedRevision = revisions.some(revision => revision.revision === revisionFromQuery) ? revisionFromQuery
    : revisions.find(revision => revision.status === 'Draft')?.revision ?? revisions.at(-1)?.revision ?? '';
  let activeTab = ['operations', 'tree'].includes(ctx.query.get('tab')) ? ctx.query.get('tab') : 'bom';
  const resources = resourcesResult.data;
  const workCenters = resources?.workCenters ?? [];
  const activities = resources?.activityTypes ?? [];
  const qualifications = qualificationsResult.data ?? [];
  const revisionUrl = revision => `/api/engineering/items/${encodedPart}/revisions/${encodeURIComponent(revision)}`;
  let nextLine = 10;
  let nextSequence = 10;
  let renderSequence = 0;

  async function reload(preferRevision = selectedRevision) {
    const result = await tryGet(`/api/engineering/items/${encodedPart}/revisions`);
    if (result.error) { page.innerHTML = `${tabs('structure')}<section class="panel">${errorBlock(result.error, 'item revisions')}</section>`; wireTabs(page); return; }
    const updatedRevisions = result.data ?? [];
    revisions.splice(0, revisions.length, ...updatedRevisions);
    selectedRevision = revisions.some(revision => revision.revision === preferRevision) ? preferRevision
      : revisions.find(revision => revision.status === 'Draft')?.revision ?? revisions.at(-1)?.revision ?? '';
    await render();
  }

  function statusLabel(revision) {
    if (!revision) return 'No revision';
    if (revision.status === 'Draft') return 'Draft';
    const from = revision.effectiveFrom ? fmtDate(revision.effectiveFrom) : 'date not set';
    const to = revision.effectiveTo ? ` · ended ${fmtDate(revision.effectiveTo)}` : '';
    return `Released · effective ${from}${to}`;
  }

  async function render() {
    const sequence = ++renderSequence;
    const revision = revisions.find(candidate => candidate.revision === selectedRevision) ?? null;
    const detailResult = revision ? await tryGet(revisionUrl(selectedRevision)) : null;
    if (sequence !== renderSequence) return;
    const detail = detailResult?.data;
    const isDraft = revision?.status === 'Draft';
    const editable = manage && isDraft;
    const bom = detail?.bom ?? [];
    const operations = detail?.routing ?? [];
    const treeHtml = activeTab === 'tree' ? await renderIndentedTree(bom) : '';
    if (sequence !== renderSequence) return;
    const revisionOptions = revisions.map(candidate => `<option value="${esc(candidate.revision)}" ${candidate.revision === selectedRevision ? 'selected' : ''}>Rev ${esc(candidate.revision)} · ${esc(candidate.status)}</option>`).join('');
    nextLine = Math.min(9999, Math.max(0, ...bom.map(line => line.lineNumber)) + 10);
    nextSequence = Math.min(9999, Math.max(0, ...operations.map(operation => operation.sequence)) + 10);
    page.innerHTML = `
      <div class="page-head"><div><a href="#/engineering/structure" class="faint">← Parent parts</a><h1>${esc(item.partNumber)} <span class="faint">·</span> ${esc(item.description)}</h1><div class="sub">${esc(item.makeBuy)} · ${esc(item.unitOfMeasure)} · Engineering structure and routing</div></div>
        <div class="spacer"></div><a class="btn" href="#/engineering/${encodedPart}">Item details</a></div>
      ${tabs('structure')}
      <section class="panel structure-summary" aria-label="Parent part revision">
        <header><div><h2>Parent part definition</h2><div class="faint">Changes apply to draft revisions only. Released revisions remain frozen, and released orders keep their captured definition.</div></div><span class="spacer"></span>
          ${revisions.length ? `<label for="parent-revision">Engineering revision</label><select class="select" id="parent-revision">${revisionOptions}</select>` : '<span class="pill warn">No engineering revision</span>'}
          ${manage && !revisions.some(candidate => candidate.status === 'Draft') ? '<button class="btn small primary" id="parent-new-revision">New draft revision</button>' : ''}
          ${manage && !revisions.length ? '<button class="btn small primary" id="parent-first-revision">Create first revision</button>' : ''}
          ${manage && isDraft ? '<button class="btn small primary" id="parent-release-revision">Release revision</button>' : ''}
          ${manage && revision?.status === 'Released' ? '<button class="btn small danger" id="parent-withdraw-revision">Withdraw revision</button>' : ''}
        </header>
        ${revision ? `<div class="structure-revision-facts"><div><span>Part</span><b>${esc(item.partNumber)}</b></div><div><span>Engineering revision</span><b>${esc(revision.revision)}</b></div><div><span>Status</span><b><span class="pill ${isDraft ? 'warn' : 'ok'}">${esc(statusLabel(revision))}</span></b></div><div><span>Definition</span><b>${bom.length} BOM lines · ${operations.length} operations</b></div></div>` : `<div class="notice">Create the first draft revision to define this part’s bill of materials and operations.</div>`}
      </section>
      ${revision ? `<section class="panel" aria-label="Parent part structure">
        <header><div class="seg" role="tablist" aria-label="Engineering definition">${[['bom', 'Bill of materials'], ['operations', 'Operations'], ['tree', 'Indented multi-level tree']].map(([key, label]) => `<button role="tab" aria-selected="${activeTab === key}" data-structure-tab="${key}" class="${activeTab === key ? 'on' : ''}">${label}</button>`).join('')}</div><span class="spacer"></span>
          ${editable && activeTab === 'bom' ? '<button class="btn small" id="parent-import-bom">Import BOM CSV</button><button class="btn small primary" id="parent-add-component">Add component</button>' : ''}
          ${editable && activeTab === 'operations' ? '<button class="btn small primary" id="parent-add-operation">Add operation</button>' : ''}
        </header>
        ${detailResult?.error ? errorBlock(detailResult.error, 'revision definition') : activeTab === 'bom' ? renderBomTable(bom, allItems, editable) : activeTab === 'tree' ? treeHtml : renderOperationsTable(operations, editable)}
        ${!isDraft ? '<div class="faint" style="margin-top:10px">This released definition is read-only. Create a new draft revision to make changes.</div>' : ''}
      </section>` : ''}
      ${manage && !resourcesResult.data && resourcesResult.error ? `<section class="panel">${errorBlock(resourcesResult.error, 'plant resources')}</section>` : ''}`;
    wireTabs(page);
    $('#parent-revision', page)?.addEventListener('change', event => { selectedRevision = event.target.value; render(); });
    $$('[data-structure-tab]', page).forEach(button => button.addEventListener('click', () => { activeTab = button.dataset.structureTab; render(); }));
    $('#parent-new-revision', page)?.addEventListener('click', () => createRevision());
    $('#parent-first-revision', page)?.addEventListener('click', () => createRevision());
    $('#parent-release-revision', page)?.addEventListener('click', () => formModal({ title: `Release ${item.partNumber} revision ${selectedRevision}`, detail: 'Released engineering definitions are frozen and copied to new production orders. Another authorized engineer must approve your release.', confirmLabel: 'Release revision', reasonRequired: true,
      fields: [{ name: 'effectiveFrom', label: 'Effective from (optional)', type: 'date', value: today() }] },
    (values, reason) => command(`${revisionUrl(selectedRevision)}/release`, { effectiveFrom: values.effectiveFrom ? new Date(`${values.effectiveFrom}T00:00:00`).toISOString() : null }, reason), () => reload(selectedRevision)));
    $('#parent-withdraw-revision', page)?.addEventListener('click', async () => {
      const done = await confirmWithReason({ title: `Withdraw ${item.partNumber} revision ${selectedRevision}`, detail: 'The revision remains in history but is no longer effective for new work.', confirmLabel: 'Withdraw revision', reasonRequired: true },
        reason => command(`${revisionUrl(selectedRevision)}/withdraw`, {}, reason));
      if (done) { toast(done.message); await reload(selectedRevision); }
    });
    $('#parent-add-component', page)?.addEventListener('click', () => editBomLine(null));
    $('#parent-import-bom', page)?.addEventListener('click', () => csvImportModal({
      title: `Import BOM for ${item.partNumber} rev ${selectedRevision}`, detail: 'Rows are added sequentially to this draft revision. Keep line numbers unique; rejected rows stay visible in the preview.',
      columns: [
        { name: 'lineNumber', label: 'Line number', type: 'number', required: true, min: 1, max: 9999 },
        { name: 'componentPartNumber', label: 'Component part', required: true },
        { name: 'quantityPer', label: 'Quantity per', type: 'number', required: true, min: 0.000001 },
        { name: 'scrapPercent', label: 'Scrap percent', type: 'number', required: true, min: 0, max: 99.99 },
      ], sampleRows: [{ lineNumber: 10, componentPartNumber: allItems.find(candidate => candidate.partNumber !== item.partNumber)?.partNumber ?? 'MAT-100', quantityPer: 1, scrapPercent: 0 }],
      onRow: (row, reason) => command(`${revisionUrl(selectedRevision)}/bom`, row, reason),
    }, async () => render()));
    $$('[data-bom-edit]', page).forEach(button => button.addEventListener('click', () => editBomLine(bom.find(line => line.lineNumber === Number(button.dataset.bomEdit)))));
    $$('[data-bom-remove]', page).forEach(button => button.addEventListener('click', () => removeBomLine(Number(button.dataset.bomRemove))));
    $('#parent-add-operation', page)?.addEventListener('click', () => editOperation(null));
    $$('[data-operation-edit]', page).forEach(button => button.addEventListener('click', () => editOperation(operations.find(operation => operation.sequence === Number(button.dataset.operationEdit)))));
    $$('[data-operation-remove]', page).forEach(button => button.addEventListener('click', () => removeOperation(Number(button.dataset.operationRemove))));
  }

  async function renderIndentedTree(rootLines) {
    const revisionCache = new Map();
    const lookup = itemByPart;
    const revisionDetail = async part => {
      if (!revisionCache.has(part)) revisionCache.set(part, (async () => {
        const revisionsResult = await tryGet(`/api/engineering/items/${encodeURIComponent(part)}/revisions`);
        if (revisionsResult.error) return null;
        const childRevisions = revisionsResult.data ?? [];
        const childRevision = childRevisions.find(candidate => candidate.status === 'Released') ?? childRevisions.find(candidate => candidate.status === 'Draft');
        if (!childRevision) return null;
        const detail = await tryGet(`/api/engineering/items/${encodeURIComponent(part)}/revisions/${encodeURIComponent(childRevision.revision)}`);
        return detail.error ? null : detail.data;
      })());
      return revisionCache.get(part);
    };
    const lines = [];
    async function expand(parentPart, children, parentQuantity, depth, path) {
      for (const [index, line] of children.entries()) {
        const child = lookup.get(line.componentPartNumber);
        const qty = parentQuantity * Number(line.quantityPer ?? 0);
        const connector = index === children.length - 1 ? '└─' : '├─';
        const prefix = depth === 1 ? connector : `${'│  '.repeat(depth - 1)}${connector}`;
        const repeated = path.has(line.componentPartNumber);
        const capped = depth >= 6;
        let descendants = [];
        if (!repeated && !capped && child) descendants = (await revisionDetail(child.partNumber))?.bom ?? [];
        let critical = Number(child?.leadTimeDays ?? 0);
        if (!repeated && descendants.length) {
          const childrenPaths = await Promise.all(descendants.map(async nested => {
            if (path.has(nested.componentPartNumber) || nested.componentPartNumber === child.partNumber) return 0;
            const next = lookup.get(nested.componentPartNumber);
            if (!next || depth + 1 >= 6) return Number(next?.leadTimeDays ?? 0);
            const nestedBom = (await revisionDetail(next.partNumber))?.bom ?? [];
            return Number(next.leadTimeDays ?? 0) + await descendantsCritical(nestedBom, next.partNumber, new Set([...path, child.partNumber, next.partNumber]), depth + 2);
          }));
          critical += Math.max(0, ...childrenPaths);
        }
        const note = repeated ? 'Cycle guarded' : capped ? 'Expansion limit reached' : !child ? 'Item details unavailable' : '';
        lines.push(`<tr><td class="mono">L${depth}</td><td><span class="bom-tree-connector">${esc(prefix)}</span> <a href="#/engineering/${encodeURIComponent(line.componentPartNumber)}"><b>${esc(line.componentPartNumber)}</b></a>${note ? `<div class="faint">${esc(note)}</div>` : ''}</td><td>${esc(child?.description ?? 'Part description unavailable')}</td><td>${child ? `<span class="pill ${child.makeBuy === 'Make' ? 'info' : 'none'}">${esc(child.makeBuy)}</span>` : '—'}</td><td class="num">${qty.toLocaleString(undefined, { maximumFractionDigits: 6 })} ${esc(child?.unitOfMeasure ?? '')}</td><td class="num">${child?.leadTimeDays ?? '—'} days</td><td class="num"><b>${critical} days</b></td></tr>`);
        if (descendants.length && !repeated && !capped && child) await expand(child.partNumber, descendants, qty, depth + 1, new Set([...path, line.componentPartNumber]));
      }
    }
    async function descendantsCritical(children, parent, path, depth) {
      if (depth > 6 || !children.length) return 0;
      let longest = 0;
      for (const line of children) {
        if (path.has(line.componentPartNumber)) continue;
        const child = lookup.get(line.componentPartNumber);
        if (!child) continue;
        const nested = depth >= 6 ? [] : (await revisionDetail(child.partNumber))?.bom ?? [];
        longest = Math.max(longest, Number(child.leadTimeDays ?? 0) + await descendantsCritical(nested, child.partNumber, new Set([...path, line.componentPartNumber]), depth + 1));
      }
      return longest;
    }
    const rootCritical = Number(item.leadTimeDays ?? 0) + await descendantsCritical(rootLines, item.partNumber, new Set([item.partNumber]), 1);
    await expand(item.partNumber, rootLines, 1, 1, new Set([item.partNumber]));
    if (!lines.length) return emptyBlock('No components in this revision', 'Add BOM lines to show the multi-level engineering structure.');
    return `<div class="notice info">Critical-path lead time for ${esc(item.partNumber)}: <b>${rootCritical} days</b>. Each subassembly expands through its latest released revision, or its draft when none is released; recursion stops at six levels and detects cycles.</div><div class="table-wrap"><table class="table"><thead><tr><th>Level</th><th>Component</th><th>Description</th><th>Make / buy</th><th class="num">Extended qty / parent</th><th class="num">Item lead time</th><th class="num">Critical path</th></tr></thead><tbody>${lines.join('')}</tbody></table></div>`;
  }

  function renderBomTable(lines, itemsList, editable) {
    if (!lines.length) return emptyBlock('No components in this revision', editable ? 'Add the material and parts this parent consumes.' : 'No BOM lines were released for this revision.');
    const lookup = new Map(itemsList.map(candidate => [candidate.partNumber, candidate]));
    return `<div class="table-wrap"><table class="table"><thead><tr><th>Line</th><th>Component part</th><th>Description</th><th class="num">Qty per</th><th class="num">Scrap</th><th>Alternate policy</th>${editable ? '<th></th>' : ''}</tr></thead><tbody>
      ${lines.map(line => `<tr><td>${line.lineNumber}</td><td><a href="#/engineering/${encodeURIComponent(line.componentPartNumber)}"><b>${esc(line.componentPartNumber)}</b></a></td><td>${esc(lookup.get(line.componentPartNumber)?.description ?? 'Part description unavailable')}</td><td class="num">${Number(line.quantityPer).toLocaleString(undefined, { maximumFractionDigits: 6 })} ${esc(lookup.get(line.componentPartNumber)?.unitOfMeasure ?? '')}</td><td class="num">${Number(line.scrapPercent).toLocaleString(undefined, { maximumFractionDigits: 2 })}%</td><td>${esc(POLICY_LABEL[line.policy] ?? line.policy ?? 'Suggest')}</td>${editable ? `<td class="nowrap"><button class="btn small" data-bom-edit="${line.lineNumber}">Edit</button> <button class="btn small ghost" data-bom-remove="${line.lineNumber}">Remove</button></td>` : ''}</tr>`).join('')}
      </tbody></table></div>`;
  }

  function renderOperationsTable(operations, editable) {
    if (!operations.length) return emptyBlock('No operations in this revision', editable ? 'Add the manufacturing steps used to build this part.' : 'No routing operations were released for this revision.');
    return `<div class="table-wrap"><table class="table"><thead><tr><th>Step</th><th>Operation</th><th>Work center / activity</th><th>Machines / qualifications</th><th>Set-up and run</th>${editable ? '<th></th>' : ''}</tr></thead><tbody>
      ${operations.map(operation => `<tr><td>${operation.sequence}</td><td><b>${esc(operation.description)}</b></td><td>${esc(operation.workCenterCode)} · ${esc(operation.activityCode)}</td><td>${esc(operation.eligibleMachines?.join(', ') || 'Any allowed machine')}<div class="faint">${esc(operation.requiredQualifications?.join(', ') || 'No additional qualifications')}</div></td><td>${Number(operation.setupMinutes).toLocaleString()} min set-up${operation.setupFamily ? `<div class="faint">${esc(operation.setupFamily)} · ${Number(operation.familySetupMinutes).toLocaleString()} min same family</div>` : ''}<div class="faint">Run ${Number(operation.runMinutesPerUnit).toLocaleString()} min/unit · crew ${operation.crewSize}</div></td>${editable ? `<td class="nowrap"><button class="btn small" data-operation-edit="${operation.sequence}">Edit</button> <button class="btn small ghost" data-operation-remove="${operation.sequence}">Remove</button></td>` : ''}</tr>`).join('')}
      </tbody></table></div>`;
  }

  function createRevision() {
    let newRevision = '';
    formModal({ title: `New revision of ${item.partNumber}`, detail: 'Create a draft to add, remove or update components and routing. A separate authorized person releases it.', reasonRequired: true, confirmLabel: 'Create revision',
      fields: [{ name: 'revision', label: 'Revision code', code: true, required: true, placeholder: 'A, B, 1, 2…' }] },
    (values, reason) => { newRevision = values.revision; return command(`/api/engineering/items/${encodedPart}/revisions`, values, reason); },
    () => reload(newRevision));
  }

  function editBomLine(line) {
    const fields = line ? [
      { name: 'lineNumber', label: 'Line number', type: 'number', value: line.lineNumber, min: 1, max: 9999, readonly: true, required: true },
      { name: 'componentPartNumber', label: 'Component part', type: 'select', value: line.componentPartNumber, options: allItems.filter(candidate => candidate.partNumber !== item.partNumber).map(candidate => [candidate.partNumber, `${candidate.partNumber} — ${candidate.description}`]), required: true },
      { name: 'quantityPer', label: 'Quantity per parent', type: 'number', value: line.quantityPer, min: 0.000001, step: 'any', required: true },
      { name: 'scrapPercent', label: 'Scrap percent', type: 'number', value: line.scrapPercent, min: 0, max: 99.99, step: '0.01', required: true },
    ] : [
      { name: 'lineNumber', label: 'Line number', type: 'number', value: nextLine, min: 1, max: 9999, required: true },
      { name: 'componentPartNumber', label: 'Component part', type: 'select', options: allItems.filter(candidate => candidate.partNumber !== item.partNumber).map(candidate => [candidate.partNumber, `${candidate.partNumber} — ${candidate.description}`]), required: true },
      { name: 'quantityPer', label: 'Quantity per parent', type: 'number', value: 1, min: 0.000001, step: 'any', required: true },
      { name: 'scrapPercent', label: 'Scrap percent', type: 'number', value: 0, min: 0, max: 99.99, step: '0.01', required: true },
    ];
    formModal({ title: line ? `Update BOM line ${line.lineNumber}` : `Add component to ${item.partNumber}`, detail: line ? 'Update the component or usage in this draft revision. The change is recorded in the audit trail.' : 'The BOM line is part of this draft revision and will be copied to production orders at release.', reasonRequired: true, confirmLabel: line ? 'Update line' : 'Add component', fields },
      (values, reason) => line
        ? command(`${revisionUrl(selectedRevision)}/bom/${line.lineNumber}/update`, { componentPartNumber: values.componentPartNumber, quantityPer: values.quantityPer, scrapPercent: values.scrapPercent }, reason)
        : command(`${revisionUrl(selectedRevision)}/bom`, values, reason),
      () => render());
  }

  async function removeBomLine(lineNumber) {
    const done = await confirmWithReason({ title: `Remove BOM line ${lineNumber}`, detail: 'The component is removed from this draft revision. Released revisions remain unchanged.', confirmLabel: 'Remove line', reasonRequired: true },
      reason => command(`${revisionUrl(selectedRevision)}/bom/${lineNumber}/remove`, {}, reason));
    if (done) await render();
  }

  function operationFields(operation) {
    const centerOptions = workCenters.filter(center => center.active !== false).map(center => [center.code, `${center.name} (${center.code})`]);
    const activityOptions = activities.filter(activity => activity.active !== false).map(activity => [activity.code, `${activity.name} (${activity.code})`]);
    const withCurrent = (options, code) => code && !options.some(([value]) => value === code) ? [...options, [code, `${code} (current)`]] : options;
    return [
      { name: 'sequence', label: 'Operation sequence', type: 'number', value: operation?.sequence ?? nextSequence, min: 1, max: 9999, readonly: Boolean(operation), required: true },
      { name: 'description', label: 'Operation', value: operation?.description ?? '', required: true, placeholder: 'Cut, wind, inspect…' },
      { name: 'workCenterCode', label: 'Work center', type: 'select', value: operation?.workCenterCode, options: withCurrent(centerOptions, operation?.workCenterCode), required: true },
      { name: 'activityCode', label: 'Activity', type: 'select', value: operation?.activityCode, options: withCurrent(activityOptions, operation?.activityCode), required: true },
      { name: 'eligibleMachines', label: 'Eligible machines (optional)', type: 'textarea', value: operation?.eligibleMachines?.join(', ') ?? '', hint: 'Enter machine codes separated by commas or new lines.' },
      { name: 'requiredQualifications', label: 'Additional qualifications (optional)', type: 'textarea', value: operation?.requiredQualifications?.join(', ') ?? '', hint: `Known qualifications: ${qualifications.map(qualification => qualification.code).join(', ') || 'none listed'}.` },
      { name: 'setupMinutes', label: 'Full set-up minutes', type: 'number', value: operation?.setupMinutes ?? 0, min: 0, step: '0.01', required: true },
      { name: 'runMinutesPerUnit', label: 'Run minutes per unit', type: 'number', value: operation?.runMinutesPerUnit ?? 0, min: 0, step: '0.01', required: true },
      { name: 'crewSize', label: 'Crew size', type: 'number', value: operation?.crewSize ?? 1, min: 1, max: 20, required: true },
      { name: 'setupFamily', label: 'Set-up family (optional)', code: true, value: operation?.setupFamily ?? '', placeholder: 'FIXTURE-42' },
      { name: 'familySetupMinutes', label: 'Same-family set-up minutes', type: 'number', value: operation?.familySetupMinutes ?? '', min: 0, step: '0.01', hint: 'Required with a set-up family; must be no greater than full set-up.' },
    ];
  }

  function editOperation(operation) {
    if (!operation && (!workCenters.some(center => center.active !== false) || !activities.some(activity => activity.active !== false))) {
      toast('Set up at least one active work center and activity before adding a routing operation.', true);
      return;
    }
    formModal({ title: operation ? `Update operation ${operation.sequence}` : `Add operation to ${item.partNumber}`, detail: 'Eligible machines and extra qualifications are optional. Set-up family and same-family minutes must be entered together.', reasonRequired: true, confirmLabel: operation ? 'Update operation' : 'Add operation', fields: operationFields(operation) },
      (values, reason) => command(operation ? `${revisionUrl(selectedRevision)}/routing/${operation.sequence}/update` : `${revisionUrl(selectedRevision)}/routing`, routingOperationBody(values), reason),
      () => render());
  }

  async function removeOperation(sequence) {
    const done = await confirmWithReason({ title: `Remove operation ${sequence}`, detail: 'The operation is removed from this draft revision. Released revisions remain unchanged.', confirmLabel: 'Remove operation', reasonRequired: true },
      reason => command(`${revisionUrl(selectedRevision)}/routing/${sequence}/remove`, {}, reason));
    if (done) await render();
  }

  await render();
}

async function appendRequirements(page, ctx, item) {
  const section = document.createElement('section');
  section.className = 'panel';
  page.append(section);
  const revs = await tryGet(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/revisions`);
  if (revs.error) { section.innerHTML = `<header><h2>Built to</h2></header>${errorBlock(revs.error, 'revisions')}`; return; }
  const list = revs.data;
  if (!list.length) { section.innerHTML = `<header><h2>Built to</h2></header>${emptyBlock('No revisions', 'Create a revision to list the documents it is built to.')}`; return; }
  const now = new Date();
  const inForce = list.find(r => r.status === 'Released' && new Date(r.effectiveFrom) <= now && (!r.effectiveTo || new Date(r.effectiveTo) > now));
  let selected = inForce?.revision ?? list[list.length - 1].revision;
  const manager = canAction(ctx.me, 'engineering.documents.manage');

  async function render() {
    const rev = list.find(r => r.revision === selected);
    const [reqs, detail] = await Promise.all([
      tryGet(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/revisions/${encodeURIComponent(selected)}/documents?history=true`),
      tryGet(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/revisions/${encodeURIComponent(selected)}`),
    ]);
    const rows = reqs.data ?? [];
    section.innerHTML = `
      <header><h2>Built to</h2><span class="spacer"></span>
        <select class="select" id="req-rev">${list.map(r => `<option value="${esc(r.revision)}" ${r.revision === selected ? 'selected' : ''}>Rev ${esc(r.revision)} (${esc(r.status)}${r === inForce ? ', in force' : ''})</option>`).join('')}</select>
        ${manager ? '<button class="btn small primary" id="add-req">Add a document</button>' : ''}</header>
      <div class="muted" style="margin-bottom:8px">The documents rev ${esc(selected)} is built to. With no revision named, the revision that applies is used (date effectivity, then applicability rules).${rev?.status === 'Released' ? ' This revision is released: changes need a reason.' : ''}</div>
      ${reqs.error ? errorBlock(reqs.error, 'requirements') : !rows.length ? emptyBlock('No documents listed', 'Add the drawings, specifications and work instructions this revision is built to.') : `
      <div class="table-wrap"><table class="table"><thead><tr><th>Document</th><th>Revision</th><th>Scope</th><th>Added</th><th>Removed</th><th></th></tr></thead><tbody>
      ${rows.map(q => `<tr class="${q.removedAt ? 'retired' : ''}"><td><a href="#/engineering/documents/${encodeURIComponent(q.documentNumber)}"><b>${esc(q.documentNumber)}</b></a>${q.note ? `<div class="faint">${esc(q.note)}</div>` : ''}</td>
        <td>${esc(q.documentRevision ?? 'The revision that applies')}</td><td>${q.operationSequence ? `Operation ${q.operationSequence}` : 'Whole item'}</td>
        <td>${esc(who(q.addedBy))} <span class="faint">${esc(fmtDateTime(q.addedAt))}</span></td>
        <td>${q.removedAt ? `${esc(who(q.removedBy))} <span class="faint">${esc(fmtDateTime(q.removedAt))}: ${esc(q.removedReason)}</span>` : '—'}</td>
        <td>${!q.removedAt && manager ? `<button class="btn small ghost" data-remove-req="${q.requirementId}">Remove</button>` : ''}</td></tr>`).join('')}
      </tbody></table></div>`}`;
    $('#req-rev', section).onchange = e => { selected = e.target.value; render(); };
    $$('[data-remove-req]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: 'Remove document requirement', detail: 'The revision is no longer built to this document. The requirement stays in its history.', reasonRequired: true, confirmLabel: 'Remove', fields: [],
    }, (_, reason) => command(`/api/engineering/document-requirements/${b.dataset.removeReq}/remove`, {}, reason), render)));
    $('#add-req', section)?.addEventListener('click', async () => {
      const docs = (await tryGet('/api/engineering/documents')).data ?? [];
      const controlled = docs.filter(d => d.authority !== 'Reference');
      if (!controlled.length) { toast('Create the document first in Engineering → Documents.', true); return; }
      const ops = (detail.data?.routing ?? []).map(o => [o.sequence, `Op ${o.sequence} — ${o.description}`]);
      formModal({
        title: `Add a document to ${item.partNumber} rev ${selected}`,
        detail: 'Name a revision only when this item revision needs exactly that one; otherwise the revision that applies is used.',
        reasonRequired: rev?.status === 'Released', confirmLabel: 'Add',
        fields: [
          { name: 'documentNumber', label: 'Document', type: 'select', blank: 'Choose…', required: true, options: controlled.map(d => [d.number, `${d.number} — ${d.title}`]) },
          { name: 'documentRevision', label: 'Document revision (optional)', code: true },
          { name: 'operationSequence', label: 'Scope', type: 'select', blank: 'Whole item', options: ops },
          { name: 'note', label: 'Note', type: 'textarea' },
        ],
      }, (v, reason) => command(`/api/engineering/items/${encodeURIComponent(item.partNumber)}/revisions/${encodeURIComponent(selected)}/documents`,
        { ...v, operationSequence: v.operationSequence ? Number(v.operationSequence) : null }, reason), render);
    });
  }

  await render();
}

// ── Applicability rules and enforcement (V2.3) ───────────────────────────────────────────────────
const CONDITION_LABEL = { Program: 'Program', Customer: 'Customer', DueDate: 'Due date range', Order: 'One order (deviation)' };

async function appendRules(page, ctx, d) {
  const section = document.createElement('section');
  section.className = 'panel';
  page.append(section);
  const approver = canAction(ctx.me, 'engineering.documents.release');
  let history = false;

  async function render() {
    const res = await tryGet(`/api/engineering/documents/${encodeURIComponent(d.number)}/rules${history ? '?history=true' : ''}`);
    const rules = res.data ?? [];
    section.innerHTML = `
      <header><h2>Applicability rules</h2><span class="spacer"></span>
        <label class="opt faint"><input type="checkbox" data-history ${history ? 'checked' : ''}> Show withdrawn</label>
        ${approver && d.authority !== 'Reference' ? '<button class="btn small primary" id="add-rule">Add a rule</button>' : ''}</header>
      <div class="muted" style="margin-bottom:8px">Without a rule, the revision in force on the date applies. A rule sets a revision for some orders; a deviation for one order wins. Rules that disagree are never guessed: the order shows "requires review".</div>
      ${res.error ? errorBlock(res.error, 'rules') : !rules.length ? emptyBlock('No rules', 'Date effectivity decides which revision applies.') : `
      <div class="table-wrap"><table class="table"><thead><tr><th>Rule</th><th>Revision</th><th>When</th><th>Added</th><th>Withdrawn</th><th></th></tr></thead><tbody>
      ${rules.map(r => `<tr class="${r.withdrawnAt ? 'retired' : ''}"><td>${r.ruleId}</td><td><b>${esc(r.revision)}</b></td>
        <td>${esc(CONDITION_LABEL[r.condition])}: ${esc(r.condition === 'Customer' ? r.customer : r.condition === 'DueDate' ? `${r.dueFrom ? fmtDate(r.dueFrom) : '…'} – ${r.dueTo ? fmtDate(r.dueTo) : '…'}` : r.condition === 'Order' ? `order ${r.orderNumber} under ${r.reference}` : 'program')}${r.note ? `<div class="faint">${esc(r.note)}</div>` : ''}</td>
        <td>${esc(who(r.createdBy))} <span class="faint">${esc(fmtDateTime(r.createdAt))}</span></td>
        <td>${r.withdrawnAt ? `${esc(who(r.withdrawnBy))} <span class="faint">${esc(fmtDateTime(r.withdrawnAt))}: ${esc(r.withdrawnReason)}</span>` : '—'}</td>
        <td>${!r.withdrawnAt && approver ? `<button class="btn small ghost" data-withdraw-rule="${r.ruleId}">Withdraw</button>` : ''}</td></tr>`).join('')}
      </tbody></table></div>`}`;
    $('[data-history]', section).onchange = e => { history = e.target.checked; render(); };
    $$('[data-withdraw-rule]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: `Withdraw rule ${b.dataset.withdrawRule}`, detail: 'Orders it selected go back to date effectivity (or another rule). It stays in the history.', reasonRequired: true, confirmLabel: 'Withdraw', fields: [],
    }, (_, reason) => command(`/api/engineering/applicability-rules/${b.dataset.withdrawRule}/withdraw`, {}, reason), render)));
    $('#add-rule', section)?.addEventListener('click', async () => {
      const programs = (await tryGet('/api/programs/')).data ?? [];
      const revisions = d.revisions.filter(r => ['Released', 'Applicable', 'Superseded'].includes(r.status)).map(r => [r.revision, `Rev ${r.revision} (${r.status})`]);
      if (!revisions.length) { toast('Release a revision first; a rule only selects a released revision.', true); return; }
      formModal({
        title: `Add an applicability rule to ${d.number}`, detail: 'Fill in only the field for the condition you choose.', reasonRequired: true, confirmLabel: 'Add rule',
        fields: [
          { name: 'revision', label: 'Revision that applies', type: 'select', options: revisions, required: true },
          { name: 'condition', label: 'When', type: 'select', value: 'Program', options: Object.entries(CONDITION_LABEL) },
          { name: 'programId', label: 'Program', type: 'select', blank: '—', options: programs.map(p => [p.programId, `${p.code} — ${p.name}`]) },
          { name: 'customer', label: 'Customer' },
          { name: 'dueFrom', label: 'Due from', type: 'date' },
          { name: 'dueTo', label: 'Due to', type: 'date' },
          { name: 'orderNumber', label: 'Order', type: 'number' },
          { name: 'reference', label: 'Deviation or concession number (order rules)' },
          { name: 'note', label: 'Note', type: 'textarea' },
        ],
      }, (v, reason) => command(`/api/engineering/documents/${encodeURIComponent(d.number)}/rules`, v, reason), render);
    });
  }

  await render();
}

async function appendEnforcement(page, ctx) {
  const section = document.createElement('section');
  section.className = 'panel';
  page.append(section);
  const approver = canAction(ctx.me, 'engineering.documents.release');
  const render = async () => {
    const res = await tryGet('/api/engineering/documentation-policy');
    const enforced = new Map((res.data ?? []).map(p => [p.itemClass, p]));
    section.innerHTML = `
      <header><h2>Documentation in readiness</h2></header>
      <div class="muted" style="margin-bottom:8px">Per item class: when enforced, a mismatched, missing or unreleased document stops the operation, and rules that disagree hold it for review. When not enforced, documentation is shown on orders and listed as not evaluated — never as passed.</div>
      ${res.error ? errorBlock(res.error, 'policy') : `
      <div class="table-wrap"><table class="table"><thead><tr><th>Item class</th><th>Documentation</th><th>Changed</th><th></th></tr></thead><tbody>
      ${Object.entries(CLASS_LABEL).map(([k, l]) => {
        const p = enforced.get(k);
        return `<tr><td>${esc(l)}</td><td>${p?.enforced ? '<span class="pill info">Blocks readiness</span>' : '<span class="pill none">Not enforced</span>'}</td>
          <td>${p ? `${esc(who(p.changedBy))} <span class="faint">${esc(fmtDateTime(p.changedAt))}</span>` : '—'}</td>
          <td>${approver ? `<button class="btn small" data-enforce="${k}" data-on="${p?.enforced ? '0' : '1'}">${p?.enforced ? 'Stop enforcing' : 'Enforce'}</button>` : ''}</td></tr>`;
      }).join('')}
      </tbody></table></div>`}`;
    $$('[data-enforce]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: `${b.dataset.on === '1' ? 'Enforce' : 'Stop enforcing'} documentation for ${CLASS_LABEL[b.dataset.enforce]} items`,
      detail: b.dataset.on === '1' ? 'Operations of these items will stop when a document does not apply or match.' : 'Documentation will be shown but will not block these items.',
      reasonRequired: true, confirmLabel: 'Save', fields: [],
    }, (_, reason) => command(`/api/engineering/documentation-policy/${b.dataset.enforce}`, { enforced: b.dataset.on === '1' }, reason), render)));
  };
  await render();
}

// ── Engineering changes (V2.4) ───────────────────────────────────────────────────────────────────
// A change lists the revisions it moves; reviews required by policy are decided by people other than
// the originator; the change board approves; release (by someone else again) puts every revision in force.

const CHANGE_TONE = { Draft: 'none', InReview: 'info', Approved: 'info', Released: 'ok', Rejected: 'bad', Cancelled: 'none' };
const CHANGE_LABEL = { InReview: 'In review' };
const DECISION_LABEL = { Approve: 'Approved', ApproveWithComments: 'Approved with comments', Reject: 'Rejected' };
const IMPACT_LABEL = {
  FutureOrder: 'Future orders', ReleasedNotStarted: 'Released, not started', WorkInProgress: 'Work in progress', Completed: 'Completed (unchanged)',
  OpenPurchase: 'Open purchase lines', Stock: 'Stock', WhereUsed: 'Used in', InspectionPlan: 'Inspection plans', Document: 'Documents built to',
};
const changePill = (c, incomplete) => `<span class="pill ${CHANGE_TONE[c.status] ?? 'none'}">${esc(CHANGE_LABEL[c.status] ?? c.status)}</span>${incomplete ? ' <span class="pill warn" title="Propagation has steps not done yet">propagation incomplete</span>' : ''}`;
const changeFields = (c = {}) => [
  { name: 'title', label: 'Title', value: c.title, required: true },
  { name: 'class', label: 'Class', type: 'select', value: c.class ?? 'Minor', options: [['Major', 'Major'], ['Minor', 'Minor'], ['Administrative', 'Administrative']] },
  { name: 'type', label: 'Type', type: 'select', value: c.type ?? 'Design', options: [['Design', 'Design'], ['Documentation', 'Documentation'], ['Process', 'Process'], ['Supplier', 'Supplier'], ['Correction', 'Correction']] },
  { name: 'priority', label: 'Priority', type: 'select', value: c.priority ?? 'Routine', options: [['Routine', 'Routine'], ['Urgent', 'Urgent'], ['Emergency', 'Emergency']] },
  { name: 'changeReason', label: 'Why the change is needed', type: 'textarea', value: c.reason, required: true },
  { name: 'description', label: 'Description', type: 'textarea', value: c.description },
  { name: 'customer', label: 'Customer (optional)', value: c.customer },
  { name: 'requestedEffective', label: 'Requested effective date (optional)', type: 'date', value: c.requestedEffective ?? '' },
];

async function changeList(page, ctx) {
  const filter = ctx.query.get('status') ?? 'open';
  const [res, pending] = await Promise.all([tryGet('/api/engineering/changes'), tryGet('/api/engineering/change-propagations')]);
  if (res.error) { page.innerHTML = errorBlock(res.error, 'engineering changes'); return; }
  const all = res.data;
  const incomplete = new Set((pending.data ?? []).map(p => p.changeNumber));
  const isOpen = c => ['Draft', 'InReview', 'Approved'].includes(c.status);
  const list = filter === 'open' ? all.filter(isOpen) : filter === 'all' ? all : all.filter(c => c.status === filter);
  page.innerHTML = `
    <div class="page-head"><div><h1>Engineering</h1><div class="sub">Engineering changes: what changes, who reviewed it, who approved and released it, and what it touches.</div></div>
      <div class="spacer"></div>${canAction(ctx.me, 'engineering.changes.manage') ? '<button class="btn primary" id="new-change">New change</button>' : ''}</div>
    ${tabs('changes')}
    <section class="panel"><header><h2>Changes</h2><span class="spacer"></span>
      <select class="select" id="change-filter">${[['open', `Open (${all.filter(isOpen).length})`], ['all', `All (${all.length})`], ...['Released', 'Rejected', 'Cancelled'].map(s => [s, `${s} (${all.filter(c => c.status === s).length})`])]
        .map(([v, l]) => `<option value="${v}" ${filter === v ? 'selected' : ''}>${esc(l)}</option>`).join('')}</select></header>
      ${!list.length ? emptyBlock('No changes', 'Engineering changes are drafted by manufacturing engineering or document control.') : `
      <div class="table-wrap"><table class="table"><thead><tr><th>Change</th><th>Title</th><th>Class</th><th>Status</th><th>Changes</th><th>Reviews</th><th>Originator</th></tr></thead><tbody>
      ${list.map(c => {
        const current = c.reviews.filter(r => r.round === c.reviewRound);
        return `<tr class="click" data-change="${esc(c.number)}"><td><b>${esc(c.number)}</b>${c.priority !== 'Routine' ? ` <span class="pill warn">${esc(c.priority)}</span>` : ''}</td><td>${esc(c.title)}</td><td>${esc(c.class)}</td><td>${changePill(c, incomplete.has(c.number))}</td>
          <td>${c.lines.filter(l => !l.removedAt).map(l => `${esc(l.documentNumber ?? l.partNumber)} ${esc(l.fromRevision ?? 'new')}→${esc(l.toRevision)}`).join('<br>') || '<span class="faint">—</span>'}</td>
          <td>${current.length ? `${current.filter(r => r.decision).length}/${current.length}` : '<span class="faint">—</span>'}</td><td>${esc(who(c.originator))}</td></tr>`;
      }).join('')}
      </tbody></table></div>`}
    </section>`;
  wireTabs(page);
  appendPolicies(page, ctx);
  $('#change-filter', page).onchange = e => { location.hash = `#/engineering/changes?status=${e.target.value}`; };
  $$('[data-change]', page).forEach(r => r.addEventListener('click', () => { location.hash = `#/engineering/changes/${encodeURIComponent(r.dataset.change)}`; }));
  $('#new-change', page)?.addEventListener('click', () => formModal({
    title: 'New engineering change', detail: 'Starts as a draft. List the document and item revisions it moves, then submit it for the reviews its policy requires.', confirmLabel: 'Create',
    fields: changeFields(),
  }, (v, reason) => command('/api/engineering/changes', v, reason), r => { location.hash = `#/engineering/changes/${encodeURIComponent(r.entityId)}`; }));
}

async function appendPolicies(page, ctx) {
  const section = document.createElement('section');
  section.className = 'panel';
  page.append(section);
  const board = canAction(ctx.me, 'engineering.changes.approve');
  const render = async () => {
    const res = await tryGet('/api/engineering/review-policies');
    const list = res.data ?? [];
    section.innerHTML = `
      <header><h2>Review policies</h2><span class="spacer"></span>${board ? '<button class="btn small" id="add-policy">Add a policy</button>' : ''}</header>
      <div class="muted" style="margin-bottom:8px">A change needs the reviews of every policy that matches it. With none, a technical review is required — never fewer than one review besides the originator. The change board always approves, and the releaser differs from the approver unless a policy combines them.</div>
      ${res.error ? errorBlock(res.error, 'policies') : !list.length ? emptyBlock('No policies', 'Every change gets a technical review.') : `
      <div class="table-wrap"><table class="table"><thead><tr><th>Policy</th><th>Applies to</th><th>Requires</th><th>Approver may release</th><th></th></tr></thead><tbody>
      ${list.map(p => `<tr><td>${p.policyId} <b>${esc(p.name)}</b></td>
        <td>${esc([p.changeClass && `${p.changeClass} changes`, p.changeType && `${p.changeType} changes`, p.itemClass && `${CLASS_LABEL[p.itemClass] ?? p.itemClass} items`, p.customer && `customer ${p.customer}`, p.programId && 'one program'].filter(Boolean).join(', ') || 'Every change')}</td>
        <td>${esc(p.requiredReviews.join(', '))}</td><td>${p.combineApprovalRelease ? 'Yes' : 'No'}</td>
        <td>${board ? `<button class="btn small ghost" data-withdraw-policy="${p.policyId}">Withdraw</button>` : ''}</td></tr>`).join('')}
      </tbody></table></div>`}`;
    $$('[data-withdraw-policy]', section).forEach(b => b.addEventListener('click', () => formModal({
      title: `Withdraw review policy ${b.dataset.withdrawPolicy}`, detail: 'Changes submitted from now on no longer need its reviews.', reasonRequired: true, confirmLabel: 'Withdraw', fields: [],
    }, (_, reason) => command(`/api/engineering/review-policies/${b.dataset.withdrawPolicy}/withdraw`, {}, reason), render)));
    $('#add-policy', section)?.addEventListener('click', () => formModal({
      title: 'Add a review policy', detail: 'Leave a criterion empty to match every change for it.', reasonRequired: true, confirmLabel: 'Add',
      fields: [
        { name: 'name', label: 'Name', required: true },
        { name: 'changeClass', label: 'Change class', type: 'select', blank: 'Any', options: [['Major', 'Major'], ['Minor', 'Minor'], ['Administrative', 'Administrative']] },
        { name: 'changeType', label: 'Change type', type: 'select', blank: 'Any', options: [['Design', 'Design'], ['Documentation', 'Documentation'], ['Process', 'Process'], ['Supplier', 'Supplier'], ['Correction', 'Correction']] },
        { name: 'itemClass', label: 'Item class affected', type: 'select', blank: 'Any', options: Object.entries(CLASS_LABEL) },
        { name: 'customer', label: 'Customer' },
        ...Object.keys(REVIEW_LABEL).map(k => ({ name: `r_${k}`, type: 'check', label: `${REVIEW_LABEL[k]} review`, text: 'Required' })),
        { name: 'combineApprovalRelease', type: 'check', label: 'Approval and release', text: 'The approver may also release' },
      ],
    }, (v, reason) => command('/api/engineering/review-policies', {
      name: v.name, changeClass: v.changeClass, changeType: v.changeType, itemClass: v.itemClass, customer: v.customer,
      requiredReviews: Object.keys(REVIEW_LABEL).filter(k => v[`r_${k}`]), combineApprovalRelease: v.combineApprovalRelease,
    }, reason), render));
  };
  await render();
}

const REVIEW_LABEL = { Technical: 'Technical', Quality: 'Quality', Manufacturing: 'Manufacturing', Procurement: 'Procurement', Program: 'Program', Maintenance: 'Maintenance' };

async function changeDetail(page, ctx, number) {
  const me = ctx.me;
  const res = await tryGet(`/api/engineering/changes/${encodeURIComponent(number)}`);
  if (res.error) { page.innerHTML = `<a href="#/engineering/changes" class="faint">← Changes</a>${errorBlock(res.error, `change ${number}`)}`; return; }
  const { change: c, required, reviewers } = res.data;
  const mine = c.originator === `person:${me.personId}`;
  const manager = canAction(me, 'engineering.changes.manage', { programId: c.programId });
  const base = `/api/engineering/changes/${encodeURIComponent(c.number)}`;
  const reload = () => changeDetail(page, ctx, number);
  const lines = c.lines.filter(l => !l.removedAt);
  const current = c.reviews.filter(r => r.round === c.reviewRound);
  const history = c.reviews.filter(r => r.round !== c.reviewRound);
  const canReview = kind => canAction(me, 'engineering.changes.review', { programId: c.programId }) && (reviewers[kind] ?? []).some(r => me.roles.includes(r));

  const actions = [];
  if (c.status === 'Draft' && manager) actions.push(['edit', 'Edit', ''], ['submit', 'Submit for review', 'primary']);
  if (c.status === 'InReview' && canAction(me, 'engineering.changes.approve', { programId: c.programId }) && !mine && current.every(r => r.decision)) actions.push(['approve', 'Approve (change board)', 'primary']);
  if (c.status === 'Approved' && canAction(me, 'engineering.release', { programId: c.programId }) && !mine && (required.combineApprovalRelease || c.approvedBy !== `person:${me.personId}`)) actions.push(['release', 'Release', 'primary']);
  if (['InReview', 'Approved'].includes(c.status) && canAction(me, 'engineering.changes.approve', { programId: c.programId })) actions.push(['reject', 'Reject', 'danger']);
  if (['Draft', 'InReview', 'Approved'].includes(c.status) && manager) actions.push(['cancel', 'Cancel', 'ghost']);

  page.innerHTML = `
    <div class="page-head"><div><a href="#/engineering/changes" class="faint">← Changes</a><h1>${esc(c.number)} ${changePill(c)} ${historyButton(c.number, 'change')}</h1>
      <div class="sub">${esc(c.title)} · ${esc(c.class)} ${esc(c.type)} change · ${esc(c.priority)} · originated by ${esc(who(c.originator))} ${esc(fmtDateTime(c.createdAt))}</div></div>
      <div class="spacer"></div><div class="head-controls">${actions.map(([k, l, tone]) => `<button class="btn ${tone}" data-act="${k}">${esc(l)}</button>`).join('')}</div></div>
    <section class="panel"><header><h2>Why</h2></header><p>${esc(c.reason)}</p>${c.description ? `<p class="muted">${esc(c.description)}</p>` : ''}
      <dl class="kv"><dt>Requested effective</dt><dd>${c.requestedEffective ? esc(fmtDate(c.requestedEffective)) : '—'}</dd>
        <dt>Approved</dt><dd>${c.approvedBy ? `${esc(who(c.approvedBy))} ${esc(fmtDateTime(c.approvedAt))}` : '—'}</dd>
        <dt>Released</dt><dd>${c.releasedBy ? `${esc(who(c.releasedBy))} ${esc(fmtDateTime(c.releasedAt))}, effective ${esc(fmtDate(c.effectiveFrom))}` : '—'}</dd>
        ${c.closedReason ? `<dt>${esc(c.status)}</dt><dd>${esc(who(c.closedBy))}: ${esc(c.closedReason)}</dd>` : ''}</dl>
      ${mine && ['InReview', 'Approved'].includes(c.status) ? '<div class="faint">You originated this change: others review, approve and release it.</div>' : ''}</section>
    <section class="panel"><header><h2>What it changes</h2><span class="spacer"></span>${c.status === 'Draft' && manager ? '<button class="btn small primary" id="add-line">Add a revision</button>' : ''}</header>
      ${!c.lines.length ? emptyBlock('Nothing listed yet', 'Add the draft document or item revisions this change releases.') : `
      <div class="table-wrap"><table class="table"><thead><tr><th>#</th><th>Kind</th><th>Subject</th><th>From</th><th>To</th><th>Added</th><th></th></tr></thead><tbody>
      ${c.lines.map(l => `<tr class="${l.removedAt ? 'retired' : ''}"><td>${l.lineNumber}</td><td>${l.kind === 'DocumentRevision' ? 'Document' : 'Item'}</td>
        <td>${l.documentNumber ? `<a href="#/engineering/documents/${encodeURIComponent(l.documentNumber)}"><b>${esc(l.documentNumber)}</b></a>` : `<a href="#/engineering/${encodeURIComponent(l.partNumber)}"><b>${esc(l.partNumber)}</b></a>`}${l.note ? `<div class="faint">${esc(l.note)}</div>` : ''}</td>
        <td>${esc(l.fromRevision ?? '—')}</td><td><b>${esc(l.toRevision)}</b></td><td>${esc(who(l.addedBy))} <span class="faint">${esc(fmtDateTime(l.addedAt))}</span></td>
        <td>${!l.removedAt && c.status === 'Draft' && manager ? `<button class="btn small ghost" data-remove-line="${l.lineNumber}">Remove</button>` : l.removedAt ? '<span class="faint">removed</span>' : ''}</td></tr>`).join('')}
      </tbody></table></div>`}
    </section>
    <section class="panel"><header><h2>Reviews</h2></header>
      <div class="muted" style="margin-bottom:8px">${c.status === 'Draft' ? `Submitting would require: ${esc(required.kinds.join(', '))} (${esc(required.basis.join('; '))}).` : `Round ${c.reviewRound}. ${esc(required.basis.join('; '))}.`}</div>
      ${!current.length ? emptyBlock('Not submitted', 'Reviews are created when the change is submitted.') : `
      <div class="table-wrap"><table class="table"><thead><tr><th>Review</th><th>Decision</th><th>By</th><th>Notes</th><th></th></tr></thead><tbody>
      ${current.map(r => `<tr><td>${esc(REVIEW_LABEL[r.kind] ?? r.kind)}</td><td>${r.decision ? `<span class="pill ${r.decision === 'Reject' ? 'bad' : 'ok'}">${esc(DECISION_LABEL[r.decision])}</span>` : '<span class="pill none">Open</span>'}</td>
        <td>${r.reviewer ? `${esc(who(r.reviewer))} <span class="faint">${esc(fmtDateTime(r.decidedAt))}</span>` : `<span class="faint">${esc((reviewers[r.kind] ?? []).join(' or '))}</span>`}</td><td>${esc(r.notes ?? '')}</td>
        <td>${!r.decision && c.status === 'InReview' && !mine && canReview(r.kind) ? `<button class="btn small primary" data-review="${r.kind}">Review</button>` : ''}</td></tr>`).join('')}
      </tbody></table></div>`}
      ${history.length ? `<details><summary class="faint">Earlier rounds (${history.length})</summary><ul>${history.map(r => `<li>Round ${r.round} ${esc(r.kind)}: ${r.decision ? `${esc(DECISION_LABEL[r.decision])} by ${esc(who(r.reviewer))}${r.notes ? ` — ${esc(r.notes)}` : ''}` : 'not decided'}</li>`).join('')}</ul></details>` : ''}
    </section>`;

  if (c.status === 'Released') appendPropagation(page, ctx, c, reload);
  const impact = document.createElement('section');
  impact.className = 'panel';
  page.append(impact);
  tryGet(`${base}/impact`).then(({ data, error }) => {
    if (error) { impact.innerHTML = `<header><h2>Impact</h2></header>${errorBlock(error, 'impact')}`; return; }
    const groups = Object.keys(IMPACT_LABEL).map(k => [k, data.records.filter(r => r.category === k)]).filter(([, rs]) => rs.length);
    impact.innerHTML = `<header><h2>Impact${c.status === 'Released' ? ' (now)' : ' before release'}</h2></header>
      <p>${esc(data.summary)}</p>
      <div class="muted" style="margin-bottom:8px">Parts reached: ${data.parts.map(p => `<b>${esc(p.partNumber)}</b> (${esc(p.reason)})`).join('; ') || '—'}.</div>
      ${!groups.length ? emptyBlock('Nothing found', 'No orders, purchases, stock, assemblies, plans or documents for these parts.') : groups.map(([k, rs]) => `
        <h3 style="margin:12px 0 4px">${esc(IMPACT_LABEL[k])} (${rs.length})</h3>
        <table class="table"><tbody>${rs.map(r => `<tr><td style="width:22%"><b>${esc(r.reference)}</b></td><td style="width:12%">${esc(r.partNumber)}</td><td class="muted">${esc(r.detail)}</td></tr>`).join('')}</tbody></table>`).join('')}`;
  });

  $('#add-line', page)?.addEventListener('click', async () => {
    const docs = ((await tryGet('/api/engineering/documents')).data ?? []).filter(d => d.authority !== 'Reference' && d.revisions.some(r => ['Draft', 'InReview', 'Approved'].includes(r.status)));
    const items = (await tryGet('/api/engineering/items')).data ?? [];
    formModal({
      title: `Add a revision to ${c.number}`, detail: 'The revision must already exist as a draft (create a document revision in Documents, an item revision in engineering setup). This change will approve and release it.', confirmLabel: 'Add',
      fields: [
        { name: 'kind', label: 'Kind', type: 'select', value: 'DocumentRevision', options: [['DocumentRevision', 'Document revision'], ['ItemRevision', 'Item revision']] },
        { name: 'document', label: 'Document (for a document revision)', type: 'select', blank: '—', options: docs.map(d => [d.number, `${d.number} — draft ${d.revisions.filter(r => ['Draft', 'InReview', 'Approved'].includes(r.status)).map(r => r.revision).join(', ')}`]) },
        { name: 'part', label: 'Part (for an item revision)', type: 'select', blank: '—', options: items.map(i => [i.partNumber, `${i.partNumber} — ${i.description}`]) },
        { name: 'toRevision', label: 'To revision', required: true, code: true },
        { name: 'note', label: 'Note', type: 'textarea' },
      ],
    }, (v, reason) => command(`${base}/lines`, { kind: v.kind, subject: v.kind === 'DocumentRevision' ? v.document : v.part, toRevision: v.toRevision, note: v.note }, reason), reload);
  });
  $$('[data-remove-line]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: `Remove line ${b.dataset.removeLine}`, detail: 'The line stays in the change history.', reasonRequired: true, confirmLabel: 'Remove', fields: [],
  }, (_, reason) => command(`${base}/lines/${b.dataset.removeLine}/remove`, {}, reason), reload)));
  $$('[data-review]', page).forEach(b => b.addEventListener('click', () => formModal({
    title: `${REVIEW_LABEL[b.dataset.review]} review of ${c.number}`, detail: 'A rejection returns the change to its originator as a draft; say what must change.', confirmLabel: 'Record review',
    fields: [
      { name: 'decision', label: 'Decision', type: 'select', value: 'Approve', options: Object.entries(DECISION_LABEL) },
      { name: 'notes', label: 'Notes (required to reject)', type: 'textarea' },
    ],
  }, (v, reason) => command(`${base}/reviews`, { kind: b.dataset.review, decision: v.decision, notes: v.notes }, reason), reload)));
  $$('[data-act]', page).forEach(b => b.addEventListener('click', () => {
    const act = b.dataset.act;
    if (act === 'edit') {
      formModal({ title: `Edit ${c.number}`, confirmLabel: 'Save', fields: changeFields(c) }, (v, reason) => command(base, v, reason), reload);
      return;
    }

    if (act === 'release') {
      formModal({ title: `Release ${c.number}`, detail: `Releases ${lines.map(l => `${l.documentNumber ?? l.partNumber} rev ${l.toRevision}`).join(', ')} together and supersedes the revisions in force from the effective date.`,
        reasonRequired: true, confirmLabel: 'Release', fields: [{ name: 'effectiveFrom', label: 'Effective from', type: 'date', value: c.requestedEffective ?? today() }] },
      (v, reason) => command(`${base}/release`, v, reason), reload);
      return;
    }

    const text = {
      submit: ['Submit for review', `Required reviews: ${required.kinds.join(', ')}.`, false],
      approve: ['Approve', 'Change board approval. Someone other than you then releases it (unless a policy combines them).', false],
      reject: ['Reject', 'The change is closed; its draft revisions stay drafts.', true],
      cancel: ['Cancel', 'The change is closed; its draft revisions stay drafts.', true],
    }[act];
    formModal({ title: `${text[0]}: ${c.number}`, detail: text[1], reasonRequired: text[2], confirmLabel: text[0], fields: [] }, (_, reason) => command(`${base}/${act}`, {}, reason), reload);
  }));
}

// V2.5: propagation of a released change, its verification record, and the actions people decide.
const STEP_LABEL = {
  MoveOrder: 'Orders moved to the new revision', ReissueDocumentation: 'Order documentation reissued', WipDisposition: 'Work in progress → disposition',
  PurchaseReview: 'Open purchase lines → buyer review', TrainingAcknowledgment: 'Training acknowledgments', CompletedUnchanged: 'Completed orders left unchanged',
};
const STEP_TONE = { Done: 'ok', Pending: 'info', Waiting: 'warn', Failed: 'bad' };
const PROPAGATION_TONE = { Complete: 'ok', Running: 'info', Incomplete: 'warn' };
const ACTION_LABEL = { WipDisposition: 'WIP disposition', PurchaseReview: 'Purchase review', TrainingAcknowledgment: 'Training acknowledgment' };
const ACTION_PERMISSIONS = { WipDisposition: 'quality.disposition', PurchaseReview: 'procurement.manage', TrainingAcknowledgment: 'competency.manage' };
const RESOLUTIONS = {
  WipDisposition: [['CompleteAtOldRevision', 'Complete at the old revision (deviation)'], ['ReworkToNewRevision', 'Rework to the new revision'], ['Scrap', 'Scrap'], ['ReplanAtNewRevision', 'Cancel and re-plan at the new revision']],
  PurchaseReview: [['NoImpact', 'No impact on what the supplier makes'], ['SupplierNotified', 'Supplier sent the new revision'], ['LineAmended', 'Line amended or replaced'], ['LineCancelled', 'Line cancelled']],
  TrainingAcknowledgment: [['Acknowledged', 'Read and understood']],
};
const RESOLUTION_LABEL = Object.fromEntries(Object.values(RESOLUTIONS).flat());
const actionTarget = a => a.kind === 'WipDisposition' ? `<a href="#/orders/${encodeURIComponent(a.target)}">Order ${esc(a.target)}</a>`
  : a.kind === 'PurchaseReview' ? `<a href="#/procurement">PO ${esc(a.target.replace('/', ' line '))}</a>`
  : `${esc(who('person:' + a.personId))} <span class="faint">${esc(a.documentNumber)} rev ${esc(a.revision)}</span>`;

function actionRows(list, me, reload, showChange) {
  return `<div class="table-wrap"><table class="table"><thead><tr>${showChange ? '<th>Change</th>' : ''}<th>Action</th><th>For</th><th>What</th><th>Status</th><th></th></tr></thead><tbody>
    ${list.map(a => `<tr>${showChange ? `<td><a href="#/engineering/changes/${encodeURIComponent(a.changeNumber)}"><b>${esc(a.changeNumber)}</b></a></td>` : ''}<td>${esc(ACTION_LABEL[a.kind])}</td><td>${actionTarget(a)}</td>
      <td class="muted">${esc(a.detail)}</td>
      <td>${a.status === 'Open' ? '<span class="pill warn">Open</span>' : `<span class="pill ok">${esc(RESOLUTION_LABEL[a.resolution] ?? a.resolution)}</span><div class="faint">${esc(who(a.resolvedBy))} ${esc(fmtDateTime(a.resolvedAt))}${a.notes ? ` — ${esc(a.notes)}` : ''}</div>`}</td>
      <td>${a.status === 'Open' && canAction(me, ACTION_PERMISSIONS[a.kind], { programId: a.programId }) ? `<button class="btn small primary" data-resolve="${a.actionId}">Resolve</button>` : ''}</td></tr>`).join('')}
    </tbody></table></div>`;
}

function wireResolve(root, list, reload) {
  $$('[data-resolve]', root).forEach(b => b.addEventListener('click', () => {
    const a = list.find(x => String(x.actionId) === b.dataset.resolve);
    formModal({
      title: `${ACTION_LABEL[a.kind]}: ${a.target}`, detail: a.detail, confirmLabel: 'Record',
      fields: [
        { name: 'resolution', label: 'Decision', type: 'select', value: RESOLUTIONS[a.kind][0][0], options: RESOLUTIONS[a.kind] },
        { name: 'notes', label: a.kind === 'WipDisposition' ? 'Basis (deviation, concession or rework instruction) — required' : a.kind === 'PurchaseReview' ? 'Reference (required unless no impact)' : 'Notes', type: 'textarea' },
      ],
    }, (v, reason) => command(`/api/engineering/change-actions/${a.actionId}/resolve`, v, reason), reload);
  }));
}

async function appendPropagation(page, ctx, c, reload) {
  const section = document.createElement('section');
  section.className = 'panel';
  page.append(section);
  const { data, error } = await tryGet(`/api/engineering/changes/${encodeURIComponent(c.number)}/propagation`);
  if (error) { section.innerHTML = `<header><h2>Propagation</h2></header>${errorBlock(error, 'propagation')}`; return; }
  const { propagation: p, verification: v, actions } = data;
  if (!p) { section.innerHTML = `<header><h2>Propagation</h2></header>${emptyBlock('Not recorded', 'Changes released before V2.5 have no propagation record.')}`; return; }
  section.innerHTML = `
    <header><h2>Propagation</h2> <span class="pill ${PROPAGATION_TONE[p.status] ?? 'none'}">${esc(p.status)}</span><span class="spacer"></span>
      ${p.status !== 'Complete' && canAction(ctx.me, 'engineering.release') ? '<button class="btn small" id="retry-propagation">Run again</button>' : ''}</header>
    <p>${esc(v.summary)}</p>
    <div class="faint" style="margin-bottom:8px">Runs as Forge propagation under the release's correlation id; ${p.runs} run(s), last ${esc(fmtDateTime(p.lastRunAt))}.</div>
    <h3 style="margin:8px 0 4px">Verification</h3>
    <table class="table"><thead><tr><th>Step</th><th>Expected</th><th>Done</th><th>Waiting</th><th>Failed</th></tr></thead><tbody>
      ${v.lines.map(l => `<tr><td>${esc(STEP_LABEL[l.kind] ?? l.kind)}</td><td>${l.expected}</td><td>${l.done}</td><td>${l.waiting || ''}</td><td>${l.failed ? `<span class="pill bad">${l.failed}</span>` : ''}</td></tr>`).join('') || '<tr><td colspan="5" class="faint">Nothing reached.</td></tr>'}
    </tbody></table>
    <details style="margin-top:8px"><summary class="faint">Steps (${p.steps.length})</summary>
      <table class="table"><tbody>${p.steps.map(s => `<tr><td style="width:12%"><span class="pill ${STEP_TONE[s.status] ?? 'none'}">${esc(s.status)}</span></td><td class="muted">${esc(s.detail)}${s.error ? `<div style="color:var(--bad)">${esc(s.error)}</div>` : ''}</td></tr>`).join('')}</tbody></table></details>
    <h3 style="margin:12px 0 4px">Actions (${actions.length}, ${v.actionsOpen} open)</h3>
    ${actions.length ? actionRows(actions, ctx.me, reload, false) : '<div class="faint">None raised.</div>'}`;
  wireResolve(section, actions, reload);
  $('#retry-propagation', section)?.addEventListener('click', async () => {
    try {
      await command(`/api/engineering/changes/${encodeURIComponent(c.number)}/propagation/retry`, {}, null);
      toast('Propagation ran again.');
      reload();
    } catch (e) { toast(e.message ?? 'Could not run propagation', true); }
  });
}

async function actionList(page, ctx) {
  const filter = ctx.query.get('kind') ?? '';
  const showAll = ctx.query.get('all') === '1';
  const res = await tryGet(`/api/engineering/change-actions?open=${showAll ? 'false' : 'true'}${filter ? `&kind=${filter}` : ''}`);
  const reload = () => actionList(page, ctx);
  page.innerHTML = `
    <div class="page-head"><div><h1>Engineering</h1><div class="sub">What released changes ask people to decide: dispositions for work in progress, buyer reviews of open purchase lines, training acknowledgments.</div></div></div>
    ${tabs('actions')}
    <section class="panel"><header><h2>${showAll ? 'All change actions' : 'Open change actions'}</h2><span class="spacer"></span>
      <select class="select" id="action-kind"><option value="">Every kind</option>${Object.entries(ACTION_LABEL).map(([k, l]) => `<option value="${k}" ${filter === k ? 'selected' : ''}>${esc(l)}</option>`).join('')}</select>
      <button class="btn small ghost" id="action-all">${showAll ? 'Open only' : 'Include resolved'}</button></header>
      ${res.error ? errorBlock(res.error, 'change actions') : !res.data.length ? emptyBlock('Nothing to decide', 'Actions appear when a released change reaches work in progress, open purchase lines or people trained on a changed instruction.') : actionRows(res.data, ctx.me, reload, true)}
    </section>`;
  wireTabs(page);
  if (!res.error) wireResolve(page, res.data, reload);
  const go = (kind, all) => { location.hash = `#/engineering/actions?${new URLSearchParams({ ...(kind ? { kind } : {}), ...(all ? { all: '1' } : {}) })}`; };
  $('#action-kind', page).onchange = e => go(e.target.value, showAll);
  $('#action-all', page).onclick = () => go(filter, !showAll);
}

// ── Alternates (V2.6) ─────────────────────────────────────────────────────────────────────────────
// A relationship is proposed by one person and approved by another with authority; proposed ones are
// never usable and are always listed apart. Where-used works both ways.
const ALT_KIND_LABEL = {
  ApprovedAlternate: 'Approved alternate', Equivalent: 'Equivalent', SupersededBy: 'Superseded by', Interchangeable: 'Interchangeable', Conditional: 'Conditional',
  Temporary: 'Temporary', MaintenanceOnly: 'Maintenance only', SupplierSpecific: 'Supplier-specific', ProgramSpecific: 'Program-specific', EngineeringApprovalRequired: 'Engineering approval required',
};
const ALT_TONE = { Proposed: 'warn', Approved: 'ok', Rejected: 'bad', Expired: 'none' };
const POLICY_LABEL = { Never: 'Never', Suggest: 'Suggest (default)', Automatic: 'Automatic for approved equivalents' };
const altApprover = (me, a) => canAction(me, a.kind === 'MaintenanceOnly' ? 'maintenance.alternates.approve' : 'engineering.alternates.approve', { programId: a.programId });

function alternateRows(list, me, showParts) {
  return `<div class="table-wrap"><table class="table"><thead><tr><th>#</th>${showParts ? '<th>Primary</th>' : ''}<th>Alternate</th><th>Kind</th><th>Per 1</th><th>Where</th><th>Status</th><th>Proposed / decided</th><th></th></tr></thead><tbody>
    ${list.map(a => {
      const mine = a.proposedBy === `person:${me.personId}`;
      const decide = altApprover(me, a) && !mine;
      return `<tr class="${['Rejected', 'Expired'].includes(a.status) ? 'retired' : ''}"><td>${a.alternateId}</td>
        ${showParts ? `<td><a href="#/engineering/${encodeURIComponent(a.primaryPart)}"><b>${esc(a.primaryPart)}</b></a></td>` : ''}
        <td><a href="#/engineering/${encodeURIComponent(a.alternatePart)}"><b>${esc(a.alternatePart)}</b></a></td><td>${esc(ALT_KIND_LABEL[a.kind] ?? a.kind)}</td><td>${esc(String(a.factor))}</td>
        <td class="muted">${esc(a.conditions)}${a.qualityNotes ? `<div class="faint">Quality: ${esc(a.qualityNotes)}</div>` : ''}${a.evidence ? `<div class="faint">Evidence: ${esc(a.evidence)}</div>` : ''}</td>
        <td><span class="pill ${ALT_TONE[a.status] ?? 'none'}">${esc(a.status)}</span></td>
        <td>${esc(who(a.proposedBy))}<div class="faint">${esc(fmtDateTime(a.proposedAt))}</div>${a.decidedBy ? `<div>${esc(who(a.decidedBy))} <span class="faint">${esc(fmtDateTime(a.decidedAt))}${a.decisionReason ? ` — ${esc(a.decisionReason)}` : ''}</span></div>` : ''}${a.expiredReason ? `<div class="faint">Expired: ${esc(a.expiredReason)}</div>` : ''}</td>
        <td class="nowrap">${a.status === 'Proposed' && decide ? `<button class="btn small primary" data-alt-approve="${a.alternateId}">Approve</button> <button class="btn small ghost" data-alt-reject="${a.alternateId}">Reject</button>` : ''}
          ${a.status === 'Proposed' && mine ? '<span class="faint">You proposed it — someone else decides</span>' : ''}
          ${['Proposed', 'Approved'].includes(a.status) && altApprover(me, a) ? ` <button class="btn small ghost" data-alt-expire="${a.alternateId}">Expire</button>` : ''}</td></tr>`;
    }).join('')}
    </tbody></table></div>`;
}

function wireAlternateActions(root, reload) {
  const go = (id, verb, title, detail, reasonRequired) => formModal({ title, detail, reasonRequired, confirmLabel: title.split(' ')[0], fields: [] },
    (_, reason) => command(`/api/engineering/alternates/${id}/${verb}`, {}, reason), reload);
  $$('[data-alt-approve]', root).forEach(b => b.addEventListener('click', () => go(b.dataset.altApprove, 'approve', `Approve alternate ${b.dataset.altApprove}`, 'It becomes usable wherever its conditions hold: MRP (on lines that allow it), issue to orders and purchasing.', false)));
  $$('[data-alt-reject]', root).forEach(b => b.addEventListener('click', () => go(b.dataset.altReject, 'reject', `Reject alternate ${b.dataset.altReject}`, 'It is never usable. The record stays.', true)));
  $$('[data-alt-expire]', root).forEach(b => b.addEventListener('click', () => go(b.dataset.altExpire, 'expire', `Expire alternate ${b.dataset.altExpire}`, 'It stops being usable from now on. What was already issued keeps its record.', true)));
}

function proposeAlternate(primary, items, reload) {
  return formModal({
    title: primary ? `Propose an alternate for ${primary}` : 'Propose an alternate', detail: 'Recorded as proposed: never usable by MRP, issue or purchasing until someone else with authority approves it.', confirmLabel: 'Propose',
    fields: [
      ...(primary ? [] : [{ name: 'primaryPart', label: 'Primary part (the one needed)', type: 'select', options: items.map(i => [i.partNumber, `${i.partNumber} — ${i.description}`]), required: true }]),
      { name: 'alternatePart', label: 'Alternate part', type: 'select', options: items.filter(i => i.partNumber !== primary).map(i => [i.partNumber, `${i.partNumber} — ${i.description}`]), required: true },
      { name: 'kind', label: 'Kind', type: 'select', value: 'Equivalent', options: Object.entries(ALT_KIND_LABEL) },
      { name: 'factor', label: 'Alternate units per 1 primary unit', type: 'number', value: 1, min: 0, step: 'any' },
      { name: 'parentPart', label: 'Only in this assembly (optional)', type: 'select', blank: '— any —', options: items.filter(i => i.makeBuy === 'Make').map(i => [i.partNumber, i.partNumber]) },
      { name: 'customer', label: 'Only for customer (optional)' },
      { name: 'machineCode', label: 'Only on machine (optional)', code: true },
      { name: 'assetCode', label: 'Only on asset (optional)', code: true },
      { name: 'supplierCode', label: 'Only from supplier (optional)', code: true },
      { name: 'validFrom', label: 'Valid from (optional)', type: 'date' },
      { name: 'validTo', label: 'Valid to (required for temporary)', type: 'date' },
      { name: 'qualityNotes', label: 'Quality notes', type: 'textarea' },
      { name: 'evidence', label: 'Evidence (test report, datasheet, deviation)', type: 'textarea' },
    ],
  }, (v, reason) => command('/api/engineering/alternates', { ...v, primaryPart: primary ?? v.primaryPart }, reason), reload);
}

async function appendAlternates(page, ctx, item, items) {
  const section = document.createElement('section');
  section.className = 'panel';
  page.append(section);
  const reload = () => itemDetail(page, ctx, item.partNumber);
  const { data, error } = await tryGet(`/api/engineering/alternates/where-used/${encodeURIComponent(item.partNumber)}`);
  if (error) { section.innerHTML = `<header><h2>Where used and alternates</h2></header>${errorBlock(error, 'where-used')}`; return; }
  const me = ctx.me;
  const approved = data.alternates.filter(a => a.status !== 'Proposed');
  const proposed = data.alternates.filter(a => a.status === 'Proposed');
  section.innerHTML = `
    <header><h2>Where used and alternates</h2><span class="spacer"></span>${canAction(me, 'engineering.alternates.propose') ? '<button class="btn small primary" id="propose-alt">Propose an alternate</button>' : ''}</header>
    <h3 style="margin:4px 0 6px">Used in (${data.usedIn.length})</h3>
    ${!data.usedIn.length ? '<div class="muted">No released bill of materials uses it.</div>' : `<table class="table"><thead><tr><th>Assembly</th><th>Rev</th><th>Line</th><th>Qty per</th><th>Alternates on this line</th><th></th></tr></thead><tbody>
      ${data.usedIn.map(w => `<tr class="${w.inForce ? '' : 'retired'}"><td><a href="#/engineering/${encodeURIComponent(w.parentPart)}"><b>${esc(w.parentPart)}</b></a></td><td>${esc(w.revision)}${w.inForce ? '' : ' <span class="faint">not in force</span>'}</td><td>${w.lineNumber}</td><td>${esc(String(w.quantityPer))}</td>
        <td>${esc(POLICY_LABEL[w.policy] ?? w.policy)}</td><td>${canAction(me, 'engineering.alternates.approve') ? `<button class="btn small ghost" data-policy="${esc(w.parentPart)}|${esc(w.revision)}|${w.lineNumber}|${w.policy}">Policy</button>` : ''}</td></tr>`).join('')}</tbody></table>`}
    <h3 style="margin:14px 0 6px">Alternates for ${esc(item.partNumber)} (${approved.length})</h3>
    ${approved.length ? alternateRows(approved, me, false) : '<div class="muted">None approved.</div>'}
    ${proposed.length ? `<div class="notice warn" style="margin-top:10px"><b>Proposed — not usable until approved (${proposed.length})</b>${alternateRows(proposed, me, false)}</div>` : ''}
    <h3 style="margin:14px 0 6px">${esc(item.partNumber)} is an alternate for (${data.alternateFor.length})</h3>
    ${data.alternateFor.length ? alternateRows(data.alternateFor, me, true) : '<div class="muted">Nothing.</div>'}`;
  wireAlternateActions(section, reload);
  $('#propose-alt', section)?.addEventListener('click', () => proposeAlternate(item.partNumber, items, reload));
  $$('[data-policy]', section).forEach(b => b.addEventListener('click', () => {
    const [parent, rev, line, policy] = b.dataset.policy.split('|');
    formModal({ title: `Alternates on ${parent} rev ${rev} line ${line}`, detail: 'How MRP may use approved alternates for this line. Automatic substitutes approved equivalents up to the shortage and records why; suggest only lists them.', reasonRequired: true, confirmLabel: 'Set',
      fields: [{ name: 'policy', label: 'Policy', type: 'select', value: policy, options: Object.entries(POLICY_LABEL) }] },
    (v, reason) => command(`/api/engineering/items/${encodeURIComponent(parent)}/revisions/${encodeURIComponent(rev)}/bom/${line}/policy`, v, reason), reload);
  }));
}

async function alternateList(page, ctx) {
  const [res, items] = await Promise.all([tryGet('/api/engineering/alternates'), tryGet('/api/engineering/items')]);
  const reload = () => alternateList(page, ctx);
  const me = ctx.me;
  const all = res.data ?? [];
  const proposed = all.filter(a => a.status === 'Proposed');
  const rest = all.filter(a => a.status !== 'Proposed');
  page.innerHTML = `
    <div class="page-head"><div><h1>Engineering</h1><div class="sub">Alternate parts: who proposed them, who approved them, and where they apply. Proposed alternates are never used anywhere.</div></div>
      <div class="spacer"></div>${canAction(me, 'engineering.alternates.propose') ? '<button class="btn primary" id="propose-alt">Propose an alternate</button>' : ''}</div>
    ${tabs('alternates')}
    ${res.error ? errorBlock(res.error, 'alternates') : `
    <section class="panel" style="border-left:3px solid var(--warn)"><header><h2>Proposed — requires approval (${proposed.length})</h2></header>
      ${proposed.length ? alternateRows(proposed, me, true) : '<div class="muted">Nothing waiting.</div>'}</section>
    <section class="panel"><header><h2>Decided (${rest.length})</h2></header>
      ${rest.length ? alternateRows(rest, me, true) : emptyBlock('No alternates', 'Propose one from an item or here.')}</section>`}`;
  wireTabs(page);
  wireAlternateActions(page, reload);
  $('#propose-alt', page)?.addEventListener('click', () => proposeAlternate(null, items.data ?? [], reload));
}

// ------------------------------------------------------------ obsolescence (V2.8)

const OBS_LABEL = { NotRecommended: 'Not recommended', LastTimeBuy: 'Last-time buy', EndOfLife: 'End of life', Obsolete: 'Obsolete' };
const OBS_TONE = { NotRecommended: 'warn', LastTimeBuy: 'warn', EndOfLife: 'bad', Obsolete: 'bad' };
const REPL_TONE = { None: 'bad', Proposed: 'info', Approved: 'ok', Rejected: 'bad' };

async function obsolescenceList(page, ctx) {
  const me = ctx.me;
  const recorder = canAction(me, 'engineering.obsolescence.record');
  const approver = canAction(me, 'engineering.alternates.approve');
  const showAll = ctx.query.get('all') === '1';
  const [records, items] = await Promise.all([tryGet(`/api/engineering/obsolescence${showAll ? '?all=true' : ''}`), tryGet('/api/engineering/items')]);
  if (records.error) { page.innerHTML = errorBlock(records.error, 'obsolescence records'); return; }
  const rows = records.data;
  const flagged = rows.filter(r => r.replacement !== 'Approved');
  page.innerHTML = `
    <div class="page-head"><div><h1>Engineering</h1><div class="sub">Obsolescence: what is known about an item's end of life, from what evidence, and what it touches. Forge never invents a replacement.</div></div>
      <div class="spacer"></div>${recorder ? '<button class="btn primary" id="record">Record obsolescence</button>' : ''}</div>
    ${tabs('obsolescence')}
    ${flagged.length && !showAll ? `<div class="notice bad">${flagged.length} item(s) have no approved replacement and stay flagged.</div>` : ''}
    <section class="panel"><header><h2>${showAll ? 'All records' : 'Current record per item'}</h2><span class="spacer"></span><label class="opt"><input type="checkbox" id="all" ${showAll ? 'checked' : ''}> Show superseded records</label></header>
      ${!rows.length ? emptyBlock('No obsolescence recorded', 'Record a manufacturer notice, last-time buy or end of support with its evidence.') : `
      <div class="table-wrap"><table class="table"><thead><tr><th>Record</th><th>Part</th><th>Status</th><th>Last-time buy</th><th>End of life</th><th>End of support</th><th>Replacement</th><th></th></tr></thead><tbody>
      ${rows.map(r => `<tr><td>${r.recordId}</td><td><b>${esc(r.partNumber)}</b></td><td><span class="pill ${OBS_TONE[r.status]}">${esc(OBS_LABEL[r.status])}</span></td>
        <td>${r.lastTimeBuy ? esc(fmtDate(r.lastTimeBuy)) : '—'}</td><td>${r.endOfLife ? esc(fmtDate(r.endOfLife)) : '—'}</td><td>${r.endOfSupport ? esc(fmtDate(r.endOfSupport)) : '—'}</td>
        <td><span class="pill ${REPL_TONE[r.replacement]}">${r.replacement === 'None' ? 'None recorded' : `${esc(r.replacementPart)} · ${esc(r.replacement)}`}</span></td>
        <td class="nowrap"><button class="btn small" data-impact="${r.recordId}">Impact</button>
          ${recorder && ['None', 'Rejected'].includes(r.replacement) ? `<button class="btn small ghost" data-propose="${r.recordId}">Propose replacement</button>` : ''}
          ${approver && r.replacement === 'Proposed' && r.replacementProposedBy !== `person:${me.personId}` ? `<button class="btn small" data-approve="${r.recordId}">Approve</button> <button class="btn small ghost" data-reject="${r.recordId}">Reject</button>` : ''}</td></tr>`).join('')}
      </tbody></table></div>`}
    </section>`;
  wireTabs(page);
  const reload = () => obsolescenceList(page, ctx);
  const partOptions = (items.data ?? []).map(i => [i.partNumber, `${i.partNumber} — ${i.description}`]);
  $('#all', page).onchange = e => { location.hash = e.target.checked ? '#/engineering/obsolescence?all=1' : '#/engineering/obsolescence'; };
  $('#record', page)?.addEventListener('click', () => formModal({ title: 'Record obsolescence', detail: 'A newer record for the same item supersedes the older one; nothing is edited. Name a replacement only if one is known — someone else approves it.', confirmLabel: 'Record', fields: [
    { name: 'partNumber', label: 'Item', type: 'select', options: partOptions, required: true },
    { name: 'status', label: 'Status', type: 'select', options: Object.entries(OBS_LABEL), required: true },
    { name: 'lastTimeBuy', label: 'Last-time buy', type: 'date' }, { name: 'endOfLife', label: 'End of life', type: 'date' }, { name: 'endOfSupport', label: 'End of support', type: 'date' },
    { name: 'evidence', label: 'Evidence (notice, distributor statement…)', type: 'textarea', required: true },
    { name: 'replacementPart', label: 'Replacement (only if known)', type: 'select', blank: '— none —', options: partOptions },
  ] }, (v, reason) => command('/api/engineering/obsolescence', v, reason), reload));
  $$('[data-propose]', page).forEach(b => b.addEventListener('click', () => formModal({ title: `Propose a replacement for record ${b.dataset.propose}`, detail: 'A different person with engineering authority approves it. Alternates, BOMs and assets still change only through their own records.', confirmLabel: 'Propose',
    fields: [{ name: 'replacementPart', label: 'Replacement', type: 'select', options: partOptions, required: true }] },
  (v, reason) => command(`/api/engineering/obsolescence/${b.dataset.propose}/replacement`, v, reason), reload)));
  for (const [attr, verb, label] of [['approve', 'approve', 'Approve replacement'], ['reject', 'reject', 'Reject replacement']]) {
    $$(`[data-${attr}]`, page).forEach(b => b.addEventListener('click', async () => {
      const done = await confirmWithReason({ title: `${label} (record ${b.dataset[attr]})`, confirmLabel: label }, reason => command(`/api/engineering/obsolescence/${b.dataset[attr]}/replacement/${verb}`, {}, reason));
      if (done) { toast(done.message); await reload(); }
    }));
  }
  $$('[data-impact]', page).forEach(b => b.addEventListener('click', () => obsolescenceImpact(Number(b.dataset.impact))));
}

export async function obsolescenceImpact(recordId) {
  const r = await tryGet(`/api/engineering/obsolescence/${recordId}/impact`);
  if (r.error) { toast(r.error.message ?? 'Could not load the impact.', true); return; }
  const i = r.data;
  drawer(`Obsolescence ${recordId}: ${i.partNumber}`, `
    <div class="notice ${i.actionRequired ? 'bad' : ''}">${esc(i.headline)}</div>
    <dl class="kv"><dt>Installed</dt><dd>${i.installedQuantity}</dd><dt>Assets</dt><dd>${esc(i.assets.join(', ') || '—')}</dd><dt>Work centers</dt><dd>${esc(i.workCenters.join(', ') || '—')}</dd>
      <dt>Spares available</dt><dd>${i.sparesAvailable}${i.sparesHeld ? ` (+${i.sparesHeld} held)` : ''}</dd><dt>On order</dt><dd>${i.onOrder}</dd>
      <dt>Approved alternates</dt><dd>${esc(i.approvedAlternates.join('; ') || 'none')}</dd><dt>Replacement</dt><dd>${esc(i.replacement)}</dd></dl>
    <h3>What is recorded</h3><ul>${i.findings.map(f => `<li>${esc(f)}</li>`).join('')}</ul>
    <p class="faint">No score and no suggested replacement: whether to buy spares, approve an alternate or open a capital project is for people to decide.</p>`);
}
