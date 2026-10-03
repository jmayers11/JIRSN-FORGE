import { tryGet, command, upload, download } from '../api.js';
import { who } from '../people.js';
import { esc, $, $$, fmtDate, fmtDateTime, errorBlock, emptyBlock, toast } from '../ui.js';
import { formModal, can, today } from '../forms.js';
import { appendAttachments, TYPE_LABEL, MARKING_LABEL, markings, markingPills, fileSize, fileUrl } from '../attachments.js';

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
  const [first, second] = ctx.params;
  if (first === 'documents') return second ? documentDetail(page, ctx, decodeURIComponent(second)) : documentList(page, ctx);
  return first ? itemDetail(page, ctx, decodeURIComponent(first)) : itemList(page, ctx);
}

const tabs = active => `<div class="seg" style="margin-bottom:12px">${[['items', 'Items', '#/engineering'], ['documents', 'Documents', '#/engineering/documents']]
  .map(([k, l, href]) => `<button class="${active === k ? 'on' : ''}" data-nav="${href}">${l}</button>`).join('')}</div>`;
const wireTabs = page => $$('[data-nav]', page).forEach(b => b.addEventListener('click', () => { location.hash = b.dataset.nav; }));

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
const MANAGERS = ['ManufacturingEngineer', 'QualityEngineer', 'DocumentController'];
const APPROVERS = ['EngineeringApprover', 'DocumentController'];
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
      <div class="spacer"></div>${can(ctx.me, ...MANAGERS) ? '<button class="btn primary" id="new-doc">New document</button>' : ''}</div>
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
  const manager = can(me, ...MANAGERS);
  const approver = can(me, ...APPROVERS);
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
    if (r.status === 'Draft' && d.authority === 'External' && approver && !mine(r)) out.push(['release', 'Record as applicable']);
    if (r.status === 'InReview' && approver && !mine(r)) out.push(['approve', 'Approve']);
    if (r.status === 'InReview' && approver) out.push(['return', 'Return']);
    if (r.status === 'Approved' && approver && !mine(r)) out.push(['release', 'Release']);
    if (!['Superseded', 'Obsolete'].includes(r.status) && approver) out.push(['obsolete', 'Obsolete']);
    return out;
  };

  page.innerHTML = `
    <div class="page-head"><div><a href="#/engineering/documents" class="faint">← Documents</a><h1>${esc(d.number)} ${markingPills(d.markings)}</h1>
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
