import { esc, $, $$, toast, confirmWithReason } from './ui.js';
export { canAction } from './ui-authority.mjs';

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
    case 'repeater':
      input = repeaterHtml(f, id); break;
    case 'select':
      input = `<select class="select" id="${id}">${f.blank ? opt('', f.blank, f.value) : ''}${f.options.map(([v, l]) => opt(v, l, f.value)).join('')}</select>`; break;
    case 'lookup': {
      const options = (f.options ?? []).map(([value, label]) => ({ value: String(value), label: String(label) }));
      const selected = options.find(option => option.value === String(f.value ?? ''));
      input = `<input class="input" id="${id}" list="${id}-list" autocomplete="off" value="${esc(selected?.label ?? '')}" ${f.placeholder ? `placeholder="${esc(f.placeholder)}"` : ''}><datalist id="${id}-list">${options.map(option => `<option value="${esc(option.label)}" data-key="${esc(option.value)}"></option>`).join('')}</datalist>`;
      break;
    }
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
      input = `<input class="input${f.code ? ' mono' : ''}" id="${id}" type="${f.type ?? 'text'}" value="${esc(f.value ?? '')}" ${f.readonly ? 'readonly' : ''} ${f.placeholder ? `placeholder="${esc(f.placeholder)}"` : ''} ${f.step ? `step="${f.step}"` : ''} ${f.min != null ? `min="${f.min}"` : ''} ${f.max != null ? `max="${f.max}"` : ''} ${f.maxLength ? `maxlength="${Number(f.maxLength)}"` : ''} ${f.list ? `list="${id}-list"` : ''}>${f.list ? `<datalist id="${id}-list">${f.list.map(v => `<option value="${esc(v)}">`).join('')}</datalist>` : ''}`;
  }
  const label = f.type === 'repeater' ? `<span class="field-label" id="${id}-label">${esc(f.label)}</span>` : `<label for="${id}">${esc(f.label)}</label>`;
  return `<div class="field">${label}${input}${hint}</div>`;
}

function repeaterControl(column, value, ariaLabel) {
  const id = `data-repeater-column="${esc(column.name)}"`;
  const required = column.required ? ' required' : '';
  if (column.type === 'select') {
    return `<select class="select" ${id} aria-label="${esc(ariaLabel)}"${required}>${column.blank ? opt('', column.blank, value) : ''}${(column.options ?? []).map(([item, label]) => opt(item, label, value)).join('')}</select>`;
  }
  if (column.type === 'check') return `<input type="checkbox" ${id} aria-label="${esc(ariaLabel)}" ${value ? 'checked' : ''}>`;
  if (column.type === 'textarea') return `<textarea class="input" rows="2" ${id} aria-label="${esc(ariaLabel)}"${required}>${esc(value ?? '')}</textarea>`;
  const type = ['number', 'datetime-local', 'date'].includes(column.type) ? column.type : 'text';
  return `<input class="input${column.code ? ' mono' : ''}" type="${type}" ${id} aria-label="${esc(ariaLabel)}" value="${esc(value ?? '')}"${column.min != null ? ` min="${esc(column.min)}"` : ''}${column.max != null ? ` max="${esc(column.max)}"` : ''}${column.step ? ` step="${esc(column.step)}"` : ''}${column.readonly ? ' readonly' : ''}${required}>`;
}

function repeaterRowHtml(field, rowIndex, values = {}) {
  const id = `f-${field.name}`;
  const cells = field.columns.map(column => `<td>${repeaterControl(column, values[column.name], `${field.label}, row ${rowIndex + 1}, ${column.label}`)}</td>`).join('');
  return `<tr data-repeater-row><td class="repeater-row-number">${rowIndex + 1}</td>${cells}<td><button class="btn small" type="button" data-repeater-remove aria-label="Remove ${esc(field.label)} row ${rowIndex + 1}">Remove</button></td></tr>`;
}

function repeaterHtml(field, id) {
  const minRows = Math.max(0, Number(field.minRows ?? 0));
  const initial = Array.isArray(field.value) ? field.value : [];
  const rows = initial.length >= minRows
    ? initial
    : [...initial, ...Array.from({ length: minRows - initial.length }, () => ({}))];
  const headings = field.columns.map(column => `<th scope="col">${esc(column.label)}</th>`).join('');
  return `<div class="repeater" id="${id}" role="group" aria-labelledby="${id}-label" data-repeater="${esc(field.name)}" data-min-rows="${minRows}">
    <div class="table-wrap"><table class="table"><thead><tr><th scope="col">#</th>${headings}<th scope="col"><span class="sr-only">Actions</span></th></tr></thead><tbody>${rows.map((row, index) => repeaterRowHtml(field, index, row)).join('')}</tbody></table></div>
    <button class="btn small" type="button" data-repeater-add>Add ${esc(field.rowLabel ?? 'row')}</button>
  </div>`;
}

/** Binds add/remove controls for repeaters rendered by fieldHtml(). Safe to call more than once. */
export function bindRepeaters(root) {
  $$('[data-repeater]', root).forEach(element => {
    if (element.getAttribute('data-bound') === 'true') return;
    element.setAttribute('data-bound', 'true');
    const fieldName = element.dataset.repeater;
    element.addEventListener('click', event => {
      const button = event.target.closest?.('[data-repeater-add], [data-repeater-remove]');
      if (!button || !element.contains(button)) return;
      const tableBody = $('tbody', element), minRows = Number(element.dataset.minRows ?? 0);
      if (button.getAttribute('data-repeater-add') !== null) {
        const field = element.repeaterField;
        if (!field) return;
        const template = element.ownerDocument.createElement('tbody');
        template.innerHTML = repeaterRowHtml(field, tableBody.children.length);
        if (template.children[0]) tableBody.append(template.children[0]);
      } else {
        if (tableBody.children.length <= minRows) return;
        button.closest('[data-repeater-row]')?.remove();
        $$('[data-repeater-row]', tableBody).forEach((row, index) => {
          const number = $('.repeater-row-number', row), remove = $('[data-repeater-remove]', row);
          if (number) number.textContent = String(index + 1);
          if (remove) remove.setAttribute('aria-label', `Remove ${element.repeaterField?.label ?? fieldName} row ${index + 1}`);
        });
      }
    });
  });
}

export function readFields(m, fields) {
  const out = {};
  for (const f of fields) {
    if (f.type === 'html') continue;
    const el = $(`#f-${f.name}`, m);
    if (f.type === 'repeater') {
      const rows = $$('[data-repeater-row]', el).map((row, index) => {
        const value = {};
        for (const column of f.columns) {
          const control = $(`[data-repeater-column="${column.name}"]`, row);
          let item = column.type === 'check' ? control.checked : column.type === 'number' ? control.value === '' ? null : Number(control.value) : control.value.trim();
          if (typeof item === 'string' && item === '') item = null;
          if (column.type === 'number' && item != null && column.min != null && item < Number(column.min)) throw new Error(`${f.label}, row ${index + 1}: ${column.label} must be at least ${column.min}.`);
          if (column.type === 'number' && item != null && column.max != null && item > Number(column.max)) throw new Error(`${f.label}, row ${index + 1}: ${column.label} must be at most ${column.max}.`);
          value[column.name] = item;
        }
        const populated = Object.values(value).some(item => item !== null && item !== false);
        if (!populated) return null;
        for (const column of f.columns) {
          if (column.required && value[column.name] == null) throw new Error(`${f.label}, row ${index + 1}: ${column.label} is required.`);
        }
        return value;
      }).filter(Boolean);
      const minimum = Number(f.minRows ?? (f.required ? 1 : 0));
      if (rows.length < minimum) throw new Error(`${f.label} needs at least ${minimum} ${minimum === 1 ? (f.rowLabel ?? 'entry') : (f.pluralRowLabel ?? `${f.rowLabel ?? 'entry'}s`)}.`);
      out[f.name] = f.serialize ? f.serialize(rows) : rows;
    }
    else if (f.type === 'lookup') {
      if (el.value.trim() === '') out[f.name] = null;
      else {
        const option = $$('option', $(`#f-${f.name}-list`, m)).find(item => item.value === el.value.trim());
        if (!option) throw new Error(`${f.label}: choose a listed value.`);
        out[f.name] = option.getAttribute('data-key');
      }
    }
    else if (f.type === 'days') out[f.name] = $$('input:checked', el).map(i => Number(i.value));
    else if (f.type === 'check') out[f.name] = el.checked;
    else if (f.type === 'file') out[f.name] = el.files?.[0] ?? null;
    else if (f.type === 'number') out[f.name] = el.value === '' ? null : Number(el.value);
    else out[f.name] = el.value.trim();
    if (f.code && typeof out[f.name] === 'string') out[f.name] = out[f.name].toUpperCase();
    if (f.type === 'number' && out[f.name] != null && f.min != null && out[f.name] < Number(f.min)) throw new Error(`${f.label} must be at least ${f.min}.`);
    if (f.type === 'number' && out[f.name] != null && f.max != null && out[f.name] > Number(f.max)) throw new Error(`${f.label} must be at most ${f.max}.`);
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
  const pending = confirmWithReason(
    { title, detail, confirmLabel, reasonRequired, extraHtml: `<div class="form-grid">${fields.map(fieldHtml).join('')}</div>` },
    (reason, m) => send(readFields(m, fields), reason));
  const modal = [...document.querySelectorAll('.modal')].at(-1);
  if (modal) {
    fields.forEach(field => {
      const element = $(`#f-${field.name}`, modal);
      if (field.type === 'repeater' && element) element.repeaterField = field;
      if (field.onChange && element) element.addEventListener('change', () => field.onChange(element, modal));
    });
    bindRepeaters(modal);
  }
  const result = await pending;
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

export const today = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

export const plusDays = n => {
  const d = new Date(Date.now() + n * 864e5);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};
