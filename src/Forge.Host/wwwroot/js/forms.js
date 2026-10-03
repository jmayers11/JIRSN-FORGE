import { esc, $, $$, toast, confirmWithReason } from './ui.js';

// Small form builder shared by the materials pages: a field list in, values out, sent with a reason.

const DAY = ['', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

function opt(value, label, selected) {
  return `<option value="${esc(value)}" ${String(value) === String(selected ?? '') ? 'selected' : ''}>${esc(label)}</option>`;
}

export function fieldHtml(f) {
  const id = `f-${f.name}`;
  const hint = f.hint ? `<div class="faint" style="font-size:12px">${esc(f.hint)}</div>` : '';
  let input;
  switch (f.type) {
    case 'select':
      input = `<select class="select" id="${id}">${f.blank ? opt('', f.blank, f.value) : ''}${f.options.map(([v, l]) => opt(v, l, f.value)).join('')}</select>`; break;
    case 'textarea':
      input = `<textarea class="input" id="${id}" rows="2">${esc(f.value ?? '')}</textarea>`; break;
    case 'check':
      input = `<label class="opt"><input type="checkbox" id="${id}" ${f.value ? 'checked' : ''}> ${esc(f.text ?? '')}</label>`; break;
    case 'days':
      input = `<div class="checks" id="${id}">${[1, 2, 3, 4, 5, 6, 7].map(d => `<label class="opt"><input type="checkbox" value="${d}" ${(f.value ?? []).includes(d) ? 'checked' : ''}> ${DAY[d]}</label>`).join('')}</div>`; break;
    case 'file':
      input = `<input class="input" id="${id}" type="file">`; break;
    case 'html':
      return f.html;
    default:
      input = `<input class="input${f.code ? ' mono' : ''}" id="${id}" type="${f.type ?? 'text'}" value="${esc(f.value ?? '')}" ${f.readonly ? 'readonly' : ''} ${f.placeholder ? `placeholder="${esc(f.placeholder)}"` : ''} ${f.step ? `step="${f.step}"` : ''} ${f.min != null ? `min="${f.min}"` : ''} ${f.list ? `list="${id}-list"` : ''}>${f.list ? `<datalist id="${id}-list">${f.list.map(v => `<option value="${esc(v)}">`).join('')}</datalist>` : ''}`;
  }
  return `<div class="field"><label for="${id}">${esc(f.label)}</label>${input}${hint}</div>`;
}

export function readFields(m, fields) {
  const out = {};
  for (const f of fields) {
    if (f.type === 'html') continue;
    const el = $(`#f-${f.name}`, m);
    if (f.type === 'days') out[f.name] = $$('input:checked', el).map(i => Number(i.value));
    else if (f.type === 'check') out[f.name] = el.checked;
    else if (f.type === 'file') out[f.name] = el.files?.[0] ?? null;
    else if (f.type === 'number') out[f.name] = el.value === '' ? null : Number(el.value);
    else out[f.name] = el.value.trim();
    if (f.code && typeof out[f.name] === 'string') out[f.name] = out[f.name].toUpperCase();
    if (f.type !== 'number' && f.type !== 'check' && f.type !== 'file' && out[f.name] === '') out[f.name] = null;
    if (f.required && (out[f.name] == null || (Array.isArray(out[f.name]) && !out[f.name].length))) throw new Error(`${f.label} is required.`);
  }
  return out;
}

/**
 * Opens a form in the reason dialog. `send(values, reason)` runs the command; its message is shown
 * and `after` runs on success. Returns the command result or null.
 */
export async function formModal({ title, detail, fields, confirmLabel = 'Save', reasonRequired = false }, send, after) {
  const result = await confirmWithReason(
    { title, detail, confirmLabel, reasonRequired, extraHtml: `<div class="form-grid">${fields.map(fieldHtml).join('')}</div>` },
    (reason, m) => send(readFields(m, fields), reason));
  if (result) {
    toast(result.message ?? 'Saved.');
    await after?.(result);
  }
  return result;
}

/** Numbers as people read them: 1,000,090 and 26, not 1000090.000000. */
export function qty(n, digits = 3) {
  if (n == null || Number.isNaN(Number(n))) return '—';
  return Number(n).toLocaleString(undefined, { maximumFractionDigits: digits });
}

export const can = (me, ...roles) => roles.some(r => me.roles.includes(r));

export const today = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

export const plusDays = n => {
  const d = new Date(Date.now() + n * 864e5);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};
