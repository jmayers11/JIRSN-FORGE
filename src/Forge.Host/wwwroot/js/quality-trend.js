import { tryGet } from './api.js';
import { esc, $, fmtDate, errorBlock } from './ui.js';
import { qty } from './forms.js';

// Quality trend (Sprint 10 WW-S10-09): a part's yield and nonconformance by week beside the machines that ran it, their recorded
// stoppages and the material that went in. There is no person column by design; operator fault is never assigned here.

const machineText = m => `${esc(m.machineCode)}${m.stoppages ? ` <span class="pill warn">${m.stoppages} stoppage(s), ${qty(m.stoppageMinutes, 1)} min${m.stoppageCategories.length ? ` · ${m.stoppageCategories.map(esc).join(', ')}` : ''}</span>` : ''}${m.maintenanceWindows ? ` <span class="pill info">${m.maintenanceWindows} maintenance window(s)</span>` : ''}`;
const materialText = m => `${esc(m.componentPart)} ${m.lotNumber ? `lot ${esc(m.lotNumber)}` : '<span class="faint">(lot not tracked)</span>'}${m.supplierCode ? ` · ${esc(m.supplierCode)}` : ''}${m.ordersWithNonconformance ? ` <span class="pill warn">${m.ordersWithNonconformance}/${m.orders} orders with nonconformance</span>` : ''}`;

export function trendHtml(t) {
  const weeks = t.weeks.filter(w => w.good || w.scrap || w.inspected || w.nonconformingQuantity || w.machines.length);
  return `<div class="notice info">${esc(t.attribution)}</div>
    <div class="faint">${esc(t.partNumber)} · ${esc(fmtDate(t.from))} to ${esc(fmtDate(t.to))}</div>
    ${t.context.map(c => `<div>• ${esc(c)}</div>`).join('')}
    ${weeks.length ? `<table class="table"><thead><tr><th>Week of</th><th class="num">Good</th><th class="num">Scrap</th><th class="num">Yield</th><th class="num">Inspected</th><th class="num">Failed</th><th class="num">NCR qty</th><th>Equipment</th><th>Material</th></tr></thead><tbody>
      ${weeks.map(w => `<tr><td class="nowrap">${esc(fmtDate(w.weekStart))}</td><td class="num">${qty(w.good)}</td><td class="num">${qty(w.scrap)}${w.scrapReasons.length ? `<div class="faint">${w.scrapReasons.map(esc).join(', ')}</div>` : ''}</td>
        <td class="num">${w.yieldPercent == null ? '—' : `${qty(w.yieldPercent, 1)}%`}</td><td class="num">${qty(w.inspected)}</td><td class="num">${w.failedInspections}</td><td class="num">${qty(w.nonconformingQuantity)}</td>
        <td>${w.machines.map(machineText).join('<br>') || '<span class="faint">—</span>'}${w.gauges.length ? `<div class="faint">Gauges ${w.gauges.map(esc).join(', ')}</div>` : ''}</td>
        <td>${w.materials.map(materialText).join('<br>') || '<span class="faint">—</span>'}</td></tr>`).join('')}</tbody></table>` : '<p class="faint">Nothing was produced, inspected or reported on this part in the period.</p>'}
    ${t.limitations.map(l => `<div class="faint">${esc(l)}</div>`).join('')}`;
}

export const trendTabHtml = () => `<form id="quality-trend" class="inline"><input class="input mono" id="quality-trend-part" placeholder="Part number" style="width:180px" required>
  <input class="input" id="quality-trend-from" type="date" title="From"> <input class="input" id="quality-trend-to" type="date" title="To"> <button class="btn">Show trend</button></form>
  <div id="quality-trend-result" class="faint">Yield and nonconformance by week, with the machines, stoppages and material that coincided. No operator is named or scored.</div>`;

export function bindTrendTab(page) {
  $('#quality-trend', page)?.addEventListener('submit', async event => {
    event.preventDefault();
    const part = $('#quality-trend-part', page).value.trim().toUpperCase();
    if (!part) return;
    const params = new URLSearchParams({ part });
    for (const key of ['from', 'to']) { const value = $(`#quality-trend-${key}`, page).value; if (value) params.set(key, value); }
    const host = $('#quality-trend-result', page);
    const result = await tryGet(`/api/quality/trends?${params}`);
    host.className = '';
    host.innerHTML = result.error ? errorBlock(result.error, 'quality trend') : trendHtml(result.data);
  });
}
