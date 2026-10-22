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
      const raw = el.value.trim();
      if (raw === '') out[f.name] = null;
      else {
        const option = $$('option', $(`#f-${f.name}-list`, m)).find(item => item.value === raw || item.getAttribute('data-key') === raw);
        if (!option && !f.allowCustom) throw new Error(`${f.label}: choose a listed value.`);
        out[f.name] = option ? option.getAttribute('data-key') : raw;
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

/** Parse RFC 4180 CSV and pasted tab-separated spreadsheet rows. */
export function parseCsvText(text) {
  const source = String(text ?? '').replace(/^\uFEFF/, '');
  const firstLine = source.split(/\r?\n/, 1)[0] ?? '';
  const delimiter = firstLine.includes('\t') && !firstLine.includes(',') ? '\t' : ',';
  const rows = [], row = [];
  let value = '', quoted = false;
  for (let i = 0; i < source.length; i++) {
    const char = source[i];
    if (quoted) {
      if (char === '"' && source[i + 1] === '"') { value += '"'; i++; }
      else if (char === '"') quoted = false;
      else value += char;
    } else if (char === '"' && value === '') quoted = true;
    else if (char === delimiter) { row.push(value); value = ''; }
    else if (char === '\n' || char === '\r') {
      if (char === '\r' && source[i + 1] === '\n') i++;
      row.push(value); value = '';
      if (row.some(cell => cell.trim() !== '')) rows.push(row.splice(0)); else row.length = 0;
    } else value += char;
  }
  row.push(value);
  if (row.some(cell => cell.trim() !== '')) rows.push(row);
  if (quoted) throw new Error('A quoted CSV field was not closed.');
  return rows;
}

function csvQuote(value) { return `"${String(value ?? '').replace(/"/g, '""')}"`; }
function downloadCsv(name, rows) {
  const content = `\uFEFF${rows.map(row => row.map(csvQuote).join(',')).join('\r\n')}`;
  const link = document.createElement('a');
  link.href = URL.createObjectURL(new Blob([content], { type: 'text/csv;charset=utf-8' }));
  link.download = name; document.body.append(link); link.click();
  setTimeout(() => { URL.revokeObjectURL(link.href); link.remove(); }, 1000);
}

function csvPreview(text, columns) {
  const records = parseCsvText(text);
  if (records.length < 2) throw new Error('Add a header row and at least one data row.');
  const headers = records[0].map(value => value.trim().toLowerCase());
  const indexes = new Map(headers.map((value, index) => [value, index]));
  const missing = columns.filter(column => !indexes.has(column.name.toLowerCase()));
  if (missing.length) throw new Error(`Missing column${missing.length > 1 ? 's' : ''}: ${missing.map(column => column.name).join(', ')}.`);
  return records.slice(1).map((record, index) => {
    const value = {}, problems = [];
    for (const column of columns) {
      const raw = (record[indexes.get(column.name.toLowerCase())] ?? '').trim();
      if (column.required && !raw) { problems.push(`${column.label ?? column.name} is required`); continue; }
      if (!raw) { value[column.name] = null; continue; }
      if (column.type === 'number') {
        const number = Number(raw);
        if (!Number.isFinite(number) || column.min != null && number < column.min || column.max != null && number > column.max)
          problems.push(`${column.label ?? column.name} must be a number${column.min != null ? ` ≥ ${column.min}` : ''}${column.max != null ? ` ≤ ${column.max}` : ''}`);
        else value[column.name] = number;
      } else if (column.type === 'boolean') {
        const normalized = raw.toLowerCase();
        if (['true', 'yes', '1'].includes(normalized)) value[column.name] = true;
        else if (['false', 'no', '0'].includes(normalized)) value[column.name] = false;
        else problems.push(`${column.label ?? column.name} must be true or false`);
      } else if (column.type === 'date') {
        const parsed = /^\d{4}-\d{2}-\d{2}$/.test(raw) ? new Date(`${raw}T00:00:00Z`) : null;
        if (!parsed || Number.isNaN(parsed.getTime()) || parsed.toISOString().slice(0, 10) !== raw) problems.push(`${column.label ?? column.name} must use a valid YYYY-MM-DD date`);
        else value[column.name] = raw;
      } else {
        const options = column.options?.map(option => String(Array.isArray(option) ? option[0] : option));
        if (options?.length && !options.includes(raw)) problems.push(`${column.label ?? column.name} must be one of ${options.join(', ')}`);
        else value[column.name] = raw;
      }
    }
    return { number: index + 1, value, error: problems.join('; '), status: problems.length ? 'Invalid' : 'Ready' };
  });
}

/** Preview, validate and sequentially submit a CSV import, retaining rejected-row details for review. */
export async function csvImportModal({ title, detail, columns, sampleRows = [], onRow }, onDone) {
  const headers = columns.map(column => column.name);
  const examples = sampleRows.map(row => Array.isArray(row) ? row : columns.map(column => row[column.name] ?? ''));
  let importSummary = null;
  const pending = confirmWithReason({
    title, detail, confirmLabel: 'Import valid rows', reasonRequired: true,
    extraHtml: `<div class="csv-import">
      <div class="toolbar"><button class="btn small" type="button" data-csv-template>Download CSV template</button><span class="muted">${columns.length} columns · ${columns.filter(column => column.required).length} required</span></div>
      <div class="field"><label>Choose a CSV file</label><input class="input" type="file" accept=".csv,text/csv" data-csv-file></div>
      <div class="field"><label for="csv-import-text">Paste CSV rows or tab-separated spreadsheet rows</label><textarea class="input mono" id="csv-import-text" rows="6" data-csv-text placeholder="${esc(headers.join(','))}"></textarea></div>
      <div class="muted small" data-csv-status>Paste or choose a file to preview.</div>
      <div class="table-wrap" data-csv-preview><div class="faint">No preview yet.</div></div>
    </div>`,
  }, async (reason, modal) => {
    const ok = modal.querySelector('[data-ok]');
    if (ok.getAttribute('data-keep-open-done') === 'true') return { message: 'CSV import completed.' };
    const text = modal.querySelector('[data-csv-text]').value;
    const rows = csvPreview(text, columns);
    const ready = rows.filter(row => row.status === 'Ready');
    if (!ready.length) throw new Error('There are no valid rows to import. Correct the preview errors and try again.');
    let succeeded = 0;
    const status = modal.querySelector('[data-csv-status]');
    const paint = current => {
      status.textContent = current;
      modal.querySelector('[data-csv-preview]').innerHTML = `<table class="table"><thead><tr><th>CSV row</th><th>Status</th><th>Details</th></tr></thead><tbody>${rows.map(row => `<tr><td>${row.number}</td><td>${esc(row.status)}</td><td>${esc(row.error || (row.status === 'Imported' ? 'Imported' : 'Ready'))}</td></tr>`).join('')}</tbody></table>`;
    };
    paint(`Importing 0 / ${ready.length}…`);
    for (const [index, row] of ready.entries()) {
      status.textContent = `Importing ${index + 1} / ${ready.length}…`;
      try { await onRow(row.value, reason); row.status = 'Imported'; succeeded++; }
      catch (error) { row.status = 'Rejected'; row.error = error?.message ?? String(error); }
      paint(`Importing ${index + 1} / ${ready.length}…`);
    }
    const rejected = rows.length - succeeded;
    const message = `Imported ${succeeded} of ${rows.length} row(s); ${rejected} row(s) need attention.`;
    status.textContent = message;
    paint(message);
    importSummary = { succeeded, rejected, total: rows.length };
    modal.setAttribute('data-csv-summary', JSON.stringify(importSummary));
    await onDone?.(importSummary);
    return { keepOpen: true, closeLabel: 'Done', message };
  });
  const modal = [...document.querySelectorAll('.modal')].at(-1);
  if (modal) {
    const text = modal.querySelector('[data-csv-text]');
    const preview = modal.querySelector('[data-csv-preview]');
    const status = modal.querySelector('[data-csv-status]');
    const paintPreview = () => {
      if (!text.value.trim()) { status.textContent = 'Paste or choose a file to preview.'; preview.innerHTML = '<div class="faint">No preview yet.</div>'; return; }
      try {
        const rows = csvPreview(text.value, columns);
        status.textContent = `${rows.filter(row => row.status === 'Ready').length} valid row(s), ${rows.filter(row => row.status === 'Invalid').length} row(s) with errors.`;
        preview.innerHTML = `<table class="table"><thead><tr><th>CSV row</th><th>Status</th><th>Details</th></tr></thead><tbody>${rows.map(row => `<tr><td>${row.number}</td><td>${esc(row.status)}</td><td>${esc(row.error || 'Ready')}</td></tr>`).join('')}</tbody></table>`;
      } catch (error) {
        status.textContent = 'The CSV needs attention.';
        preview.innerHTML = `<div class="notice bad">${esc(error.message)}</div>`;
      }
    };
    text.addEventListener('input', paintPreview);
    modal.querySelector('[data-csv-file]').addEventListener('change', async event => {
      const file = event.target.files?.[0];
      if (!file) return;
      try { text.value = await file.text(); paintPreview(); }
      catch (error) { status.textContent = `Could not read file: ${error.message}`; }
    });
    modal.querySelector('[data-csv-template]').addEventListener('click', () => {
      const name = `${title.toLowerCase().replace(/[^a-z0-9]+/g, '-')}-template.csv`;
      downloadCsv(name, [headers, ...examples]);
    });
  }
  if (!pending) return null;
  const summaryEl = modal?.getAttribute('data-csv-summary');
  const summary = importSummary ?? (summaryEl ? JSON.parse(summaryEl) : null);
  if (summary?.succeeded) toast(`Imported ${summary.succeeded} row(s).`);
  return summary;
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
