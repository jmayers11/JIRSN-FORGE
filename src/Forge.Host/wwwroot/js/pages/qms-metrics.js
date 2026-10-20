import { tryGet, session } from '../api.js';
import { esc } from '../ui.js';

const ROOT = '/api/quality/qms/planning/metrics/dashboard';

function localInputValue(date) {
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
}

function valueFor(item) {
  if (!item.calculated) return 'Human assessment required';
  if (item.status !== 'Complete' || item.measurement?.availability !== 'Complete' || item.measurement.actual == null) return 'Not available';
  return `${item.measurement.actual} ${item.unit}`;
}

function basisFor(item) {
  if (!item.calculated) return 'This KPI is an attributed human assessment. Forge does not calculate or infer its value.';
  return item.measurement?.basis ?? item.status;
}

export function metricDashboardRows(snapshot) {
  const items = snapshot?.items ?? [];
  if (!items.length) return '<tr><td colspan="8" class="muted">No effective metric definitions are available to you for this period.</td></tr>';
  const objectivesFor = item => {
    if (!item.objectives?.length) return '<span class="muted">No linked effective objective</span>';
    return item.objectives.map(objective => `<div><strong>${esc(objective.title)}</strong><div class="muted">${esc(objective.comparison)} ${esc(objective.target)}${objective.upperTarget == null ? '' : `–${esc(objective.upperTarget)}`} ${esc(item.unit)} ± ${esc(objective.tolerance)} · ${esc(objective.status)}</div><div class="muted">${esc(objective.code)} revision ${esc(objective.revision)}</div></div>`).join('<hr>');
  };
  return items.map(item => `<tr>
    <td><strong>${esc(item.name)}</strong><div class="muted">${esc(item.code)} · revision ${esc(item.revision)}</div></td>
    <td>${item.calculated ? 'Recorded calculation' : 'Human assessment'}</td>
    <td>${esc(valueFor(item))}</td>
    <td>${esc(item.status)}</td>
    <td>${esc(item.numeratorDefinition)}<br><span class="muted">Denominator: ${esc(item.denominatorDefinition)}</span></td>
    <td>${esc(item.cohortBasis)}<br><span class="muted">${esc(item.measurement?.from ?? '')} – ${esc(item.measurement?.to ?? '')}</span></td>
    <td>${objectivesFor(item)}</td>
    <td>${esc(basisFor(item))}</td>
  </tr>`).join('');
}

export async function qmsMetricsPage(page) {
  const auth = { subject: session.subject, token: session.token };
  const now = new Date();
  let disposed = false, generation = 0, fromValue = localInputValue(new Date(now.getTime() - 30 * 24 * 60 * 60 * 1000)), toValue = localInputValue(now), data = null, error = null, loading = false;
  const live = token => !disposed && page.isConnected && generation === token && session.subject === auth.subject && session.token === auth.token;

  function render() {
    page.innerHTML = `<section class="page-head"><div><p class="eyebrow">V4.6 · Quality</p><h1>Quality metrics</h1><p class="muted">Effective, authorized KPI definitions only. Values use their exact declared source cohort and half-open period. Human assessments stay separate from calculated values.</p></div></section>
      <section class="panel"><form data-metric-period class="form-grid">
        <label class="field"><span>From, inclusive</span><input class="input" type="datetime-local" data-from required value="${esc(fromValue)}"></label>
        <label class="field"><span>To, exclusive</span><input class="input" type="datetime-local" data-to required value="${esc(toValue)}"></label>
        <div class="actions"><button class="btn primary" type="submit" ${loading ? 'disabled' : ''}>${loading ? 'Measuring…' : 'Load metrics'}</button></div>
      </form><p class="muted">Enter times in your browser’s local timezone; Forge sends the corresponding UTC instants.</p>${error ? `<div class="notice bad" role="alert">${esc(error)}</div>` : ''}</section>
      ${data ? `<section class="panel"><div class="panel-head"><div><h2>Authorized KPI results</h2><p class="muted">Captured ${esc(data.capturedAt)}. Each calculated value is withheld unless its full declared cohort is available.</p></div></div>
        <p class="muted">${data.definitionsComplete ? 'All authorized metric definitions are included.' : 'More authorized metric definitions are available on the next page.'} ${data.calculatedMetricsComplete ? 'All calculated metrics are complete for this period.' : 'Some calculated metrics are partial, unavailable, or outside their approved period.'}</p>
        <div class="table-wrap"><table class="table"><thead><tr><th>Metric</th><th>Method</th><th>Value</th><th>Status</th><th>Numerator / denominator</th><th>Declared scope / period</th><th>Linked objective</th><th>Evidence basis</th></tr></thead><tbody>${metricDashboardRows(data)}</tbody></table></div>
        ${data.nextCursor ? '<div class="actions"><button class="btn" data-next>Next metrics</button></div>' : ''}</section>` : ''}`;
    const form = page.querySelector('[data-metric-period]');
    form?.addEventListener('submit', event => {
      event.preventDefault(); fromValue = form.querySelector('[data-from]').value; toValue = form.querySelector('[data-to]').value;
      load(null);
    });
    page.querySelector('[data-next]')?.addEventListener('click', () => load(data?.nextCursor ?? null));
  }

  async function load(after) {
    const from = new Date(fromValue), to = new Date(toValue);
    if (!fromValue || !toValue || !Number.isFinite(from.getTime()) || !Number.isFinite(to.getTime()) || to <= from) {
      error = 'Choose a valid period with To later than From.'; data = null; render(); return;
    }
    const token = ++generation; loading = true; error = null; render();
    const query = new URLSearchParams({ from: from.toISOString(), to: to.toISOString(), take: '100' });
    if (after) query.set('after', after);
    const result = await tryGet(`${ROOT}?${query}`);
    if (!live(token)) return;
    loading = false;
    if (result.error) { error = result.error; data = null; }
    else { data = result.data; error = null; }
    render();
  }

  render();
  load(null);
  return { dispose() { disposed = true; generation++; page.innerHTML = ''; } };
}

export { valueFor as metricValueFor };
