import { icon } from './icons.js';

/** Escapes text for HTML. Every value from the API goes through this. */
export function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

export const $ = (sel, root = document) => root.querySelector(sel);
export const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

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
  t.textContent = message;
  document.body.append(t);
  setTimeout(() => t.remove(), bad ? 7000 : 3500);
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
    m.setAttribute('role', 'dialog');
    m.innerHTML = `
      <h2>${esc(title)}</h2>
      ${detail ? `<div class="muted">${esc(detail)}</div>` : ''}
      ${extraHtml}
      <div class="field"><label for="reason">Reason (recorded in the audit trail)${reasonRequired ? '' : ' — optional'}</label>
        <textarea id="reason" class="input" rows="3"></textarea></div>
      <div class="notice bad hidden" data-error></div>
      <div class="actions"><button class="btn" data-cancel>Cancel</button><button class="btn primary" data-ok>${esc(confirmLabel)}</button></div>`;
    const close = value => { scrim.remove(); m.remove(); resolve(value); };
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
    m.querySelector('#reason').focus();
  });
}

/** Renders an evidence tree from an engine run. */
export function evidenceTree(node) {
  if (!node) return '';
  const cls = { Pass: 'c-ok', Fail: 'c-bad', Indeterminate: 'c-warn', Info: 'c-info' }[node.outcome] ?? '';
  const kids = (node.children ?? []).map(evidenceTree).join('');
  return `<li><span class="${cls}">${esc(node.outcome)}</span> <b>${esc(node.check)}</b> — ${esc(node.detail)}${kids ? `<ul class="evidence">${kids}</ul>` : ''}</li>`;
}
