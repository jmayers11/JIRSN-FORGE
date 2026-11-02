import { tryGet, command } from '../api.js';
import { esc, $$, fmtDateTime, errorBlock, confirmWithReason, toast } from '../ui.js';
import { canAction } from '../forms.js';
import { who } from '../people.js';

// Owner decision 2026-11-01: a company turns off the parts of Forge it does not need. Everyone sees what is on; security administrators switch.

export function featuresHtml(features, { canManage = false } = {}) {
  return `<table class="table"><thead><tr><th>Feature</th><th>What it does</th><th>State</th><th></th></tr></thead><tbody>
    ${(features ?? []).map(f => `<tr><td><b>${esc(f.name)}</b></td><td class="muted">${esc(f.description)}</td>
      <td><span class="pill ${f.enabled ? 'ok' : 'none'}">${f.enabled ? 'On' : 'Off'}</span>${f.updatedAt ? `<div class="faint">${esc(who(f.updatedBy))} ${esc(fmtDateTime(f.updatedAt))}</div>` : ''}</td>
      <td>${canManage ? `<button class="btn small" data-feature="${esc(f.code)}" data-enable="${f.enabled ? 'false' : 'true'}">${f.enabled ? 'Turn off' : 'Turn on'}</button>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

/** Fills a Settings panel with the plant's optional features. */
export async function featuresPanel(section, me) {
  if (!section) return;
  const canManage = canAction(me, 'site.features.manage');
  const load = async () => {
    const features = await tryGet('/api/site/features');
    section.innerHTML = `<header><h2>Features</h2></header><p class="muted">Parts of Forge this plant can turn off when it does not use them. Turning one off keeps everything recorded; turning it on again carries on.</p>
      ${features.error ? errorBlock(features.error, 'features') : featuresHtml(features.data, { canManage })}`;
    for (const b of $$('[data-feature]', section)) b.addEventListener('click', () => confirmWithReason({
      title: `${b.dataset.enable === 'true' ? 'Turn on' : 'Turn off'} ${b.closest('tr').querySelector('b').textContent}`, confirmLabel: b.dataset.enable === 'true' ? 'Turn on' : 'Turn off',
    }, reason => command(`/api/site/features/${encodeURIComponent(b.dataset.feature)}`, { enabled: b.dataset.enable === 'true' }, reason)).then(r => { if (r) { toast(r.message ?? 'Saved'); load(); } }));
  };
  await load();
}

/** Whether a feature is on (true when unknown). */
export async function featureOn(code) {
  const features = await tryGet('/api/site/features');
  return (features.data ?? []).find(f => f.code === code)?.enabled ?? true;
}
