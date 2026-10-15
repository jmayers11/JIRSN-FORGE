import { tryGet, command } from '../api.js';
import { who } from '../people.js';
import { esc, $, $$, fmtDateTime, errorBlock, emptyBlock } from '../ui.js';
import { formModal, canAction } from '../forms.js';

// Safety rules: minimum physical staffing, lone-work prohibition and role slots for a safety area, work center or
// machine. A safety engineer drafts; an independent safety approver publishes. Readiness and the shop floor apply
// only the published, effective version.

const API = '/api/safety-rules';
const STATUS_TONE = { Draft: 'info', Published: 'ok', Retired: 'none' };

/** "slot name | count | qualification codes (comma) | trainee may fill (yes/no)", one slot per line. */
export function parseSlots(text) {
  return String(text ?? '').split(/\r?\n/).map(l => l.trim()).filter(Boolean).map((line, i) => {
    const [name, count, satisfiedBy, trainee] = line.split('|').map(p => p.trim());
    const n = Number(count);
    const codes = String(satisfiedBy ?? '').split(/[\s,]+/).map(c => c.toUpperCase()).filter(Boolean);
    if (!name || !Number.isSafeInteger(n) || n < 1 || !codes.length) throw new Error(`Slot line ${i + 1}: use "name | count | qualification codes | trainee yes/no".`);
    return { name, count: n, satisfiedBy: codes, traineeMayFill: /^(y|yes|true)$/i.test(trainee ?? '') };
  });
}
export const formatSlots = slots => (slots ?? []).map(s => `${s.name} | ${s.count} | ${s.satisfiedBy.join(', ')} | ${s.traineeMayFill ? 'yes' : 'no'}`).join('\n');

export function ruleBody(v) {
  const minimum = Number(v.minimumPhysicalPeople);
  if (!Number.isSafeInteger(minimum) || minimum < 0) throw new Error('Minimum physical people is a whole number.');
  return { code: v.code, title: v.title, scopeKind: v.scopeKind, scopeCode: v.scopeCode, activityCodes: String(v.activityCodes ?? '').split(/[\s,]+/).map(c => c.toUpperCase()).filter(Boolean),
    minimumPhysicalPeople: minimum, prohibitLoneWork: !!v.prohibitLoneWork, slots: parseSlots(v.slots) };
}

/** Each rule code's versions, newest first, with the actions that version allows. */
export function ruleActions(rule, versions, can) {
  const actions = [];
  if (rule.status === 'Draft') { if (can.approve) actions.push('publish'); if (can.draft) actions.push('discard'); }
  if (rule.status === 'Published' && can.approve) actions.push('retire');
  if (rule.status !== 'Draft' && can.draft && rule.version === Math.max(...versions.map(r => r.version)) && !versions.some(r => r.status === 'Draft')) actions.push('revise');
  return actions;
}

export async function safetyRulesPage(page, ctx) {
  const can = { draft: canAction(ctx.me, 'safety.rules.draft'), approve: canAction(ctx.me, 'safety.rules.approve') };
  let rules = { data: [] }, disposed = false;
  const load = async () => { rules = await tryGet(`${API}/`); };
  const reload = async () => { await load(); if (!disposed) render(); };
  const versionsOf = code => (rules.data ?? []).filter(r => r.code === code);

  function render() {
    const list = [...(rules.data ?? [])].sort((a, b) => a.code.localeCompare(b.code) || b.version - a.version);
    page.innerHTML = `
      <div class="page-head"><div><h1>Safety rules</h1><div class="sub">Minimum people physically present, lone-work limits and required role slots. Work cannot start where a published rule is not met.</div></div>
        <div class="spacer"></div>${can.draft ? '<button class="btn" id="new-rule">Draft a rule</button>' : ''}</div>
      <section class="panel">${rules.error ? errorBlock(rules.error, 'safety rules') : !list.length ? emptyBlock('No safety rules', 'Draft one for a safety area, work center or machine.') : `
        <div class="table-wrap"><table class="table"><thead><tr><th>Rule</th><th>Applies to</th><th>Staffing</th><th>Slots</th><th>Status</th><th>Authored / approved</th><th></th></tr></thead><tbody>
        ${list.map(r => { const acts = ruleActions(r, versionsOf(r.code), can); return `<tr><td><b class="mono">${esc(r.code)}</b> v${esc(r.version)}<div class="faint">${esc(r.title)}</div></td>
          <td>${esc(r.scopeKind.replace(/([a-z])([A-Z])/g, '$1 $2'))} ${esc(r.scopeCode)}${r.activityCodes?.length ? `<div class="faint">activities ${esc(r.activityCodes.join(', '))}</div>` : '<div class="faint">all activities</div>'}</td>
          <td>${esc(r.minimumPhysicalPeople)} present${r.prohibitLoneWork ? '<div class="faint">no lone work</div>' : ''}</td>
          <td>${(r.slots ?? []).map(s => `<div>${esc(s.count)} × ${esc(s.name)} <span class="faint">(${esc(s.satisfiedBy.join(', '))}${s.traineeMayFill ? ', trainee may fill' : ''})</span></div>`).join('') || '—'}</td>
          <td><span class="pill ${STATUS_TONE[r.status] ?? 'none'}">${esc(r.status)}</span>${r.effectiveFrom ? `<div class="faint">${esc(fmtDateTime(r.effectiveFrom))}${r.effectiveTo ? ` – ${esc(fmtDateTime(r.effectiveTo))}` : ''}</div>` : ''}</td>
          <td>${esc(who(r.authoredBy))}${r.approvedBy ? `<div class="faint">${esc(who(r.approvedBy))}</div>` : ''}</td>
          <td class="row-actions">${acts.map(a => `<button class="btn small ${a === 'publish' || a === 'revise' ? '' : 'ghost'}" data-rule="${esc(r.code)}" data-rule-version="${r.version}" data-rule-act="${a}">${{ publish: 'Publish', discard: 'Discard draft', retire: 'Retire', revise: 'Draft new version' }[a]}</button>`).join('')}</td></tr>`; }).join('')}
        </tbody></table></div>`}</section>`;
    $('#new-rule', page)?.addEventListener('click', () => draft(null));
    $$('[data-rule-act]', page).forEach(b => b.addEventListener('click', () => act(versionsOf(b.dataset.rule).find(r => String(r.version) === b.dataset.ruleVersion), b.dataset.ruleAct)));
  }

  function draft(rule) {
    formModal({ title: rule ? `New version of ${rule.code}` : 'Draft a safety rule', detail: 'A draft has no effect until an independent safety approver publishes it.',
      fields: [
        { name: 'code', label: 'Rule code', code: true, required: true, value: rule?.code, readonly: !!rule },
        { name: 'title', label: 'Title', required: true, value: rule?.title },
        { name: 'scopeKind', label: 'Applies to', type: 'select', value: rule?.scopeKind ?? 'WorkCenter', options: [['SafetyArea', 'Safety area'], ['WorkCenter', 'Work center'], ['Machine', 'Machine']] },
        { name: 'scopeCode', label: 'Area, work center or machine code', code: true, required: true, value: rule?.scopeCode },
        { name: 'activityCodes', label: 'Activities (blank: all)', value: rule?.activityCodes?.join(', '), hint: 'Activity codes, comma separated.' },
        { name: 'minimumPhysicalPeople', label: 'Minimum people physically present', type: 'number', min: 0, required: true, value: rule?.minimumPhysicalPeople ?? 1 },
        { name: 'prohibitLoneWork', label: 'Lone work', type: 'check', text: 'Prohibit lone work', value: rule?.prohibitLoneWork },
        { name: 'slots', label: 'Role slots (optional)', type: 'textarea', value: formatSlots(rule?.slots), hint: 'One per line: name | count | qualification codes | trainee may fill (yes/no).' },
      ], confirmLabel: 'Save draft', reasonRequired: true },
    (v, reason) => command(`${API}/drafts`, ruleBody(v), reason), reload);
  }

  function act(rule, action) {
    if (action === 'revise') return draft(rule);
    const words = { publish: ['Publish', 'Takes effect now and replaces the published version.'], discard: ['Discard draft', 'The draft is removed; published versions are unchanged.'], retire: ['Retire', 'The rule stops applying. Its history is kept.'] }[action];
    formModal({ title: `${words[0]} ${rule.code} v${rule.version}`, detail: words[1], fields: [], confirmLabel: words[0], reasonRequired: true },
      (_, reason) => command(action === 'publish' ? `${API}/${encodeURIComponent(rule.code)}/versions/${rule.version}/publish` : action === 'discard' ? `${API}/${encodeURIComponent(rule.code)}/draft/discard` : `${API}/${encodeURIComponent(rule.code)}/retire`, {}, reason), reload);
  }

  await load();
  if (!disposed) render();
  return { dispose() { disposed = true; } };
}
