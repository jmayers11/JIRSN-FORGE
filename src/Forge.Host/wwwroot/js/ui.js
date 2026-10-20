import { icon } from './icons.js';

/** Escapes text for HTML. Every value from the API goes through this. */
export function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

export const $ = (sel, root = document) => root.querySelector(sel);
export const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

const enhancedTables = new WeakSet();

/** Add a compact search field and sortable column headings to a data table. */
export function enhanceTable(table, { filter = true } = {}) {
  if (!table || enhancedTables.has(table)) return false;
  const body = table.querySelector('tbody');
  const head = table.querySelector('thead');
  const headers = head ? [...head.querySelectorAll('th')] : [];
  if (!body || headers.length < 2 || body.querySelectorAll('tr').length < 2) return false;
  enhancedTables.add(table);

  let filterInput = null;
  if (filter) {
    const tools = document.createElement('div');
    tools.className = 'table-tools';
    tools.innerHTML = '<label class="field"><span>Filter rows</span><input class="input" type="search" data-table-filter autocomplete="off" placeholder="Search this table"></label><span class="muted">Select a column heading to sort</span>';
    table.parentElement?.insertBefore(tools, table);
    filterInput = tools.querySelector('[data-table-filter]');
  }
  const rows = () => [...body.querySelectorAll('tr')];
  filterInput?.addEventListener('input', () => {
    const query = filterInput.value.trim().toLocaleLowerCase();
    for (const row of rows()) row.hidden = !!query && !row.textContent.toLocaleLowerCase().includes(query);
  });

  headers.forEach((header, column) => {
    header.tabIndex = 0;
    header.title = 'Sort by ' + header.textContent.trim();
    header.setAttribute('aria-sort', 'none');
    const sort = () => {
      const direction = header.getAttribute('aria-sort') === 'ascending' ? -1 : 1;
      headers.forEach(item => item.setAttribute('aria-sort', 'none'));
      header.setAttribute('aria-sort', direction === 1 ? 'ascending' : 'descending');
      const indexed = rows().map((row, index) => ({ row, index, value: row.children[column]?.dataset.sortValue ?? row.children[column]?.textContent.trim() ?? '' }));
      const values = indexed.map(item => item.value);
      const numeric = values.length > 0 && values.every(value => /^#?\s*-?[\d,]+(?:\.\d+)?\s*$/.test(value));
      const isoDates = values.length > 0 && values.every(value => /^\d{4}-\d{2}-\d{2}(?:[T ]|$)/.test(value));
      const dateColumn = /date|when|time|received|expires|promised|due/i.test(header.textContent);
      const dates = dateColumn && values.length > 0 && values.every(value => ['—', '-', ''].includes(value) || Number.isFinite(new Date(value).getTime()));
      indexed.sort((a, b) => {
        let compared;
        if (numeric) compared = Number(a.value.replace(/[#,\s]/g, '')) - Number(b.value.replace(/[#,\s]/g, ''));
        else if (isoDates || dates) {
          const left = ['—', '-', ''].includes(a.value) ? Number.POSITIVE_INFINITY : new Date(a.value).getTime();
          const right = ['—', '-', ''].includes(b.value) ? Number.POSITIVE_INFINITY : new Date(b.value).getTime();
          compared = left - right;
        }
        else compared = a.value.localeCompare(b.value, undefined, { numeric: true, sensitivity: 'base' });
        return (compared || a.index - b.index) * direction;
      });
      body.append(...indexed.map(item => item.row));
    };
    header.addEventListener('click', sort);
    header.addEventListener('keydown', event => {
      if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); sort(); }
    });
  });
  return true;
}

export function enhanceTables(root = document, options) {
  return $$('.table', root).reduce((count, table) => count + Number(enhanceTable(table, options)), 0);
}

export function fmtNumber(n, digits = 1) {
  if (n == null || Number.isNaN(n)) return '—';
  return Number(n).toLocaleString(undefined, { maximumFractionDigits: digits, minimumFractionDigits: 0 });
}

let zone;
export function setTimeZone(tz) { zone = tz; }
export function timeZone() { return zone; }

export function fmtTime(iso, opts = {}) {
  if (!iso) return '—';
  return new Date(iso).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit', timeZone: zone, ...opts });
}
export function fmtDateTime(iso) {
  if (!iso) return '—';
  return new Date(iso).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', timeZone: zone });
}
export function fmtDate(isoOrDateOnly) {
  if (!isoOrDateOnly) return '—';
  const d = /^\d{4}-\d{2}-\d{2}$/.test(isoOrDateOnly) ? new Date(`${isoOrDateOnly}T12:00:00Z`) : new Date(isoOrDateOnly);
  return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric', timeZone: /^\d{4}-\d{2}-\d{2}$/.test(isoOrDateOnly) ? 'UTC' : zone });
}
export function relative(iso, now = Date.now()) {
  const ms = new Date(iso).getTime() - now;
  const abs = Math.abs(ms), h = abs / 36e5;
  const text = h < 1 ? `${Math.max(1, Math.round(abs / 6e4))} min` : h < 48 ? `${fmtNumber(h, h < 10 ? 1 : 0)} h` : `${Math.round(h / 24)} days`;
  return ms >= 0 ? `in ${text}` : `${text} ago`;
}

/** A panel that failed to load says why, instead of showing zeros. */
export function errorBlock(error, what) {
  if (error?.status === 403) {
    const reasons = (error.reasons ?? []).map(r => `<li>${esc(r)}</li>`).join('');
    return `<div class="empty"><b>No access to ${esc(what)}</b>Your Forge roles do not include this. ${reasons ? `<ul class="evidence">${reasons}</ul>` : ''}</div>`;
  }
  return `<div class="empty"><b>Could not load ${esc(what)}</b>${esc(error?.message ?? error)}</div>`;
}

export function emptyBlock(title, detail) {
  return `<div class="empty"><b>${esc(title)}</b>${esc(detail)}</div>`;
}

export function toast(message, bad = false) {
  const t = document.createElement('div');
  t.className = `toast${bad ? ' bad' : ''}`;
  t.setAttribute('role', bad ? 'alert' : 'status');
  t.setAttribute('aria-live', bad ? 'assertive' : 'polite');
  t.setAttribute('aria-atomic', 'true');
  t.textContent = message;
  document.body.append(t);
  setTimeout(() => t.remove(), bad ? 7000 : 3500);
}

/** Keeps a modal's keyboard focus inside it, then restores focus when its owner closes it. */
export function manageDialogFocus(element, onClose, opener = document.activeElement) {
  const doc = element.ownerDocument ?? document;
  const heading = element.querySelector('h2');
  element.setAttribute('role', 'dialog');
  element.setAttribute('aria-modal', 'true');
  element.tabIndex = -1;
  if (heading) {
    heading.id ||= `forge-dialog-${Math.random().toString(36).slice(2)}`;
    element.setAttribute('aria-labelledby', heading.id);
  }
  const focusable = () => [...element.querySelectorAll('button,input,select,textarea,a[href],[tabindex]')]
    .filter(el => !el.matches(':disabled') && el.tabIndex >= 0
      && !el.closest('[hidden],[aria-hidden="true"],.hidden') && el.getClientRects().length);
  const focusFirst = () => (focusable()[0] ?? element).focus();
  let closed = false;
  const release = (restore = true) => {
    if (closed) return;
    closed = true;
    doc.removeEventListener('keydown', onKey);
    doc.removeEventListener('focusin', onFocus);
    if (restore && opener?.isConnected && !opener.matches(':disabled')) opener.focus();
  };
  const onKey = e => {
    if (!element.isConnected) { release(false); return; }
    if (e.key === 'Escape') { e.preventDefault(); onClose(); return; }
    if (e.key !== 'Tab') return;
    const controls = focusable();
    if (!controls.length) { e.preventDefault(); element.focus(); return; }
    const first = controls[0], last = controls.at(-1), active = doc.activeElement;
    if (!element.contains(active) || active === element) {
      e.preventDefault(); (e.shiftKey ? last : first).focus();
    } else if (e.shiftKey && active === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && active === last) { e.preventDefault(); first.focus(); }
  };
  const onFocus = e => {
    if (!element.isConnected) { release(false); return; }
    if (!element.contains(e.target)) focusFirst();
  };
  doc.addEventListener('keydown', onKey);
  doc.addEventListener('focusin', onFocus);
  focusFirst();
  return release;
}

/** A persistent region: changing its text can announce an operator switch or terminal lock. */
export function liveRegion(label) {
  const region = document.createElement('div');
  region.setAttribute('role', 'status');
  region.setAttribute('aria-live', 'polite');
  region.setAttribute('aria-atomic', 'true');
  region.setAttribute('aria-label', label);
  Object.assign(region.style, { position: 'absolute', width: '1px', height: '1px', overflow: 'hidden', clipPath: 'inset(50%)' });
  document.body.append(region);
  return region;
}

export function drawer(title, bodyHtml) {
  closeOverlays();
  const scrim = document.createElement('div');
  scrim.className = 'scrim';
  const d = document.createElement('aside');
  d.className = 'drawer';
  d.innerHTML = `<header><h2>${esc(title)}</h2><button class="icon-btn" data-close aria-label="Close">${icon('close')}</button></header><div class="body">${bodyHtml}</div>`;
  const close = () => { scrim.remove(); d.remove(); document.removeEventListener('keydown', onKey); };
  const onKey = e => { if (e.key === 'Escape') close(); };
  scrim.onclick = close;
  d.querySelector('[data-close]').onclick = close;
  document.addEventListener('keydown', onKey);
  document.body.append(scrim, d);
  return { element: d.querySelector('.body'), close };
}

let workspaceTabId = 0;

/** Move a page's direct content sections into accessible horizontal tabs by heading text. */
export function tabifyPanels(root, definitions) {
  const groups = definitions.map(definition => ({ ...definition, nodes: [] }));
  const summaryNodes = [];
  for (const child of [...root.children]) {
    const isSummary = child.matches?.('.page-head, .object-summary, .notice') || child.tagName === 'A';
    if (isSummary) { summaryNodes.push(child); continue; }
    const heading = child.querySelector?.('h2')?.textContent?.toLowerCase() ?? '';
    if (!heading) continue;
    const group = groups.find(item => item.match.some(term => heading.includes(term.toLowerCase()))) ?? groups[0];
    group.nodes.push(child);
  }
  const visibleGroups = groups.filter(group => group.nodes.length);
  if (visibleGroups.length < 2) return null;
  const id = `forge-workspace-${++workspaceTabId}`;
  const shell = document.createElement('section'); shell.className = 'workspace-tabs';
  const list = document.createElement('div'); list.className = 'workspace-tablist'; list.setAttribute('role', 'tablist');
  const panels = document.createElement('div'); panels.className = 'workspace-panels';
  for (const [index, group] of visibleGroups.entries()) {
    const key = `${id}-${index}`;
    const button = document.createElement('button'); button.className = 'workspace-tab'; button.type = 'button';
    button.setAttribute('role', 'tab'); button.setAttribute('data-workspace-tab', key); button.setAttribute('aria-selected', String(index === 0));
    button.setAttribute('aria-controls', `${key}-panel`); button.tabIndex = index === 0 ? 0 : -1; button.textContent = group.label;
    list.append(button);
    const panel = document.createElement('div'); panel.className = 'workspace-panel'; panel.setAttribute('role', 'tabpanel');
    panel.setAttribute('id', `${key}-panel`); panel.setAttribute('data-workspace-panel', key); panel.setAttribute('aria-labelledby', key); panel.tabIndex = 0;
    panel.hidden = index !== 0;
    for (const node of group.nodes) panel.append(node);
    panels.append(panel);
  }
  shell.append(list, panels);
  root.append(shell);
  wireWorkspaceTabs(shell);
  return shell;
}

/** Bind tab controls already paired by data-workspace-tab and data-workspace-panel. */
export function wireWorkspaceTabs(root) {
  const buttons = $$('[data-workspace-tab]', root);
  const panels = $$('[data-workspace-panel]', root);
  if (!buttons.length || !panels.length) return;
  const activate = button => {
    for (const candidate of buttons) {
      const active = candidate === button;
      candidate.setAttribute('aria-selected', String(active)); candidate.tabIndex = active ? 0 : -1;
    }
    for (const panel of panels) panel.hidden = panel.dataset.workspacePanel !== button.dataset.workspaceTab;
  };
  for (const button of buttons) {
    button.addEventListener('click', () => activate(button));
    button.addEventListener('keydown', event => {
      const index = buttons.indexOf(button);
      const next = event.key === 'ArrowRight' ? (index + 1) % buttons.length : event.key === 'ArrowLeft' ? (index - 1 + buttons.length) % buttons.length : -1;
      if (next >= 0) { event.preventDefault(); buttons[next].focus(); activate(buttons[next]); }
    });
  }
}

export function closeOverlays() {
  $$('.scrim, .drawer, .modal').forEach(e => e.remove());
}

/**
 * Asks for the reason that goes into the audit trail, then runs the action.
 * Returns the action's result, or null if cancelled. Errors are shown, not thrown.
 */
export function confirmWithReason({ title, detail, confirmLabel = 'Confirm', reasonRequired = true, extraHtml = '' }, action) {
  return new Promise(resolve => {
    const scrim = document.createElement('div');
    scrim.className = 'scrim';
    const m = document.createElement('div');
    m.className = 'modal';
    m.innerHTML = `
      <h2>${esc(title)}</h2>
      ${detail ? `<div class="muted">${esc(detail)}</div>` : ''}
      ${extraHtml}
      <div class="field"><label for="reason">Reason (recorded in the audit trail)${reasonRequired ? '' : ' — optional'}</label>
        <textarea id="reason" class="input" rows="3"></textarea></div>
      <div class="notice bad hidden" data-error></div>
      <div class="actions"><button class="btn" data-cancel>Cancel</button><button class="btn primary" data-ok>${esc(confirmLabel)}</button></div>`;
    let closed = false, releaseFocus = () => {};
    const close = value => {
      if (closed) return;
      closed = true;
      scrim.remove(); m.remove(); releaseFocus(); resolve(value);
    };
    scrim.onclick = () => close(null);
    m.querySelector('[data-cancel]').onclick = () => close(null);
    m.querySelector('[data-ok]').onclick = async () => {
      const reason = m.querySelector('#reason').value.trim();
      const err = m.querySelector('[data-error]');
      if (reasonRequired && !reason) { err.textContent = 'Give a reason; it is kept with the change.'; err.classList.remove('hidden'); return; }
      const ok = m.querySelector('[data-ok]');
      ok.disabled = true;
      try {
        close(await action(reason || undefined, m));
      } catch (e) {
        ok.disabled = false;
        const reasons = (e.reasons ?? []).map(r => `• ${r}`).join('\n');
        err.textContent = `${e.message}${reasons ? `\n${reasons}` : ''}`;
        err.style.whiteSpace = 'pre-line';
        err.classList.remove('hidden');
      }
    };
    document.body.append(scrim, m);
    releaseFocus = manageDialogFocus(m, () => close(null));
  });
}

/** Renders an evidence tree from an engine run. */
export function evidenceTree(node) {
  if (!node) return '';
  const cls = { Pass: 'c-ok', Fail: 'c-bad', Indeterminate: 'c-warn', Info: 'c-info' }[node.outcome] ?? '';
  const kids = (node.children ?? []).map(evidenceTree).join('');
  return `<li><span class="${cls}">${esc(node.outcome)}</span> <b>${esc(node.check)}</b> — ${esc(node.detail)}${kids ? `<ul class="evidence">${kids}</ul>` : ''}</li>`;
}
