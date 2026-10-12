import { tryGet, session } from '../api.js';
import { esc, $, $$, fmtDateTime, errorBlock, toast } from '../ui.js';
import { traceLink, cellHtml, authHeaders } from './reports.js';

// Forge Analyst (spec §58–62, §66–67). Questions are parsed by Forge itself (no AI needed) and answered
// by Forge's services with your own access. What Forge understood is always shown. Report requests are
// confirmed before export. An AI summary appears only if an administrator enabled a provider, and it is
// labeled and shown next to the facts it restates.

const TONE_DOT = { ok: 'ok', bad: 'bad', warn: 'warn', info: 'info' };
const CONFIDENCE = { High: ['ok', 'Understood'], Medium: ['warn', 'Partly understood'], Low: ['bad', 'Not sure'] };
const history = []; // this visit's questions and answers, newest last

async function post(path, body) {
  const r = await fetch(path, { method: 'POST', headers: { ...authHeaders(), 'Content-Type': 'application/json', Accept: 'application/json' }, body: JSON.stringify(body) });
  const text = await r.text();
  const data = text ? JSON.parse(text) : null;
  if (!r.ok) throw Object.assign(new Error(data?.detail ?? data?.title ?? `The server answered ${r.status}.`), { status: r.status });
  return data;
}

function reportQuery(report) {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(report.parameters ?? {})) if (v !== null && v !== undefined && v !== '') q.set(k, v);
  return q;
}

async function download(report, format) {
  const res = await fetch(`/api/reports/${encodeURIComponent(report.reportId)}/export?format=${format}&${reportQuery(report)}`, { headers: authHeaders() });
  if (!res.ok) { toast(`Export failed (${res.status}).`, true); return; }
  const blob = await res.blob();
  const name = /filename="?([^";]+)"?/.exec(res.headers.get('Content-Disposition') ?? '')?.[1] ?? `${report.reportId}.${format}`;
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob); a.download = name; document.body.append(a); a.click();
  setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
}

function factsHtml(facts) {
  if (!facts?.length) return '';
  return `<ul class="analyst-facts">${facts.map(f => `<li class="${TONE_DOT[f.tone] ?? ''}">${esc(f.text)}${f.trace ? ` ${traceLink(f.trace)}` : ''}</li>`).join('')}</ul>`;
}

function tableHtml(columns, rows) {
  if (!columns?.length) return '';
  if (!rows?.length) return '<div class="muted small">No rows.</div>';
  const num = c => c.kind === 'Number' || c.kind === 'Hours';
  return `<div class="table-wrap"><table class="table report-table"><thead><tr>${columns.map(c => `<th class="${num(c) ? 'num' : ''}">${esc(c.title)}</th>`).join('')}<th>Source</th></tr></thead>
    <tbody>${rows.map(r => `<tr>${columns.map((c, i) => `<td class="${num(c) ? 'num' : ''}">${cellHtml(c, r.cells[i])}</td>`).join('')}<td class="trace">${r.trace.map(traceLink).join(' ')}</td></tr>`).join('')}</tbody></table></div>`;
}

function briefHtml(b) {
  return `<div class="brief">
    <div class="report-meta">${esc(fmtDateTime(b.from))} – ${esc(fmtDateTime(b.to))} · changes since ${esc(fmtDateTime(b.since))} · generated ${esc(fmtDateTime(b.generatedAt))}</div>
    ${b.headlines.length ? `<ul class="report-summary">${b.headlines.map(h => `<li><b>${esc(h)}</b></li>`).join('')}</ul>` : ''}
    ${b.sections.map(s => `<div class="brief-section ${s.tone}"><h3>${esc(s.title)}</h3>${s.note ? `<div class="muted small">${esc(s.note)}</div>` : ''}${factsHtml(s.facts)}</div>`).join('')}
    ${b.omitted.length ? `<div class="muted small">Not included (no access): ${esc(b.omitted.join('; '))}.</div>` : ''}
  </div>`;
}

function answerHtml(entry, index) {
  if (entry.error) return `<div class="analyst-turn"><div class="analyst-q">${esc(entry.question)}</div>${errorBlock(entry.error, 'the answer')}</div>`;
  const a = entry.answer;
  const i = a.interpretation;
  const [tone, label] = CONFIDENCE[i.confidence] ?? ['none', i.confidence];
  const report = i.report;
  const exportLabel = { xlsx: 'Generate Excel', csv: 'Generate CSV', pdf: 'Open to print / PDF' }[report?.output];
  return `<div class="analyst-turn">
    <div class="analyst-q">${esc(entry.question)}</div>
    <div class="analyst-a">
      <div class="analyst-understood"><span class="pill ${tone}">${esc(label)}</span> <span class="muted">I understood:</span> ${esc(i.understood)}</div>
      ${i.ambiguities.length ? `<div class="notice warn">${i.ambiguities.map(esc).join('<br>')}</div>` : ''}
      ${a.notAuthorized ? `<div class="notice bad"><b>${esc(a.headline)}</b></div>` : `<div class="analyst-headline">${esc(a.headline)}</div>`}
      ${a.narrative ? `<div class="analyst-narrative"><div class="muted small">AI summary (${esc(a.narrativeProvider)}) — it restates the facts below, which are authoritative.</div>${esc(a.narrative)}</div>` : ''}
      ${report ? `<div class="analyst-report">
          <div><b>Report request</b> — ${esc(report.title)}${report.notes.length ? `<div class="muted small">${report.notes.map(esc).join(' ')}</div>` : ''}</div>
          <div class="toolbar">
            ${exportLabel && report.output !== 'pdf' ? `<button class="btn primary small" data-export="${index}" data-format="${report.output}">${exportLabel}</button>` : ''}
            <a class="btn small ${report.output === 'pdf' ? 'primary' : ''}" href="#/reports?report=${encodeURIComponent(report.reportId)}&${reportQuery(report)}">${report.output === 'pdf' ? exportLabel : 'Open in Reports'}</a>
            ${report.output !== 'xlsx' ? `<button class="btn small" data-export="${index}" data-format="xlsx">Excel</button>` : ''}
            ${report.output !== 'csv' ? `<button class="btn small" data-export="${index}" data-format="csv">CSV</button>` : ''}
          </div></div>` : ''}
      ${factsHtml(a.facts)}
      ${a.brief ? briefHtml(a.brief) : ''}
      ${tableHtml(a.columns, a.rows)}
      ${a.notes.length ? `<div class="muted small">${a.notes.map(esc).join('<br>')}</div>` : ''}
      ${a.sources.length ? `<div class="muted small">Sources: ${esc(a.sources.join(', '))}.</div>` : ''}
    </div>
  </div>`;
}

export async function analystPage(page, ctx) {
  const [status, resources] = await Promise.all([tryGet('/api/analyst/status'), tryGet('/api/resources/')]);
  if (status.error) {
    page.innerHTML = `<div class="page-head"><div><h1>Forge Analyst</h1></div></div>${errorBlock(status.error, 'Forge Analyst')}`;
    return;
  }

  const s = status.data;
  const shifts = resources.data?.shifts ?? [];
  const initialQuestion = (ctx.query.get('q') ?? '').trim().slice(0, 500);
  let narrate = false;
  let initialAsked = false;

  function render() {
    page.innerHTML = `
      <div class="page-head"><div><h1>Forge Analyst</h1><div class="sub">Ask about your operation in plain language. Forge works out what you mean, shows what it understood, and answers from its own records with your access. ${s.narration ? '' : 'No AI provider is configured: every answer is computed by Forge.'}</div></div></div>
      <section class="panel analyst-ask">
        <form id="ask" class="analyst-form">
          <input class="input" id="q" value="${esc(initialQuestion)}" placeholder="e.g. Why is order 10001 late?" autocomplete="off" maxlength="500">
          <button class="btn primary" type="submit">Ask</button>
        </form>
        <div class="analyst-tools">
          <div class="chips">${s.suggestions.map(q => `<button class="chip" data-q="${esc(q)}">${esc(q)}</button>`).join('')}</div>
          <div class="toolbar">
            <select class="input" id="brief-shift" style="max-width:220px"><option value="">Current shift</option>${shifts.map(x => `<option value="${esc(x.code)}">${esc(x.name)}</option>`).join('')}</select>
            <button class="btn" id="brief">Brief me</button>
            ${s.narration ? `<label class="opt"><input type="checkbox" id="narrate" ${narrate ? 'checked' : ''}> Add an AI summary (${esc(s.provider)})</label>` : ''}
          </div>
        </div>
      </section>
      <div id="conversation">${history.map((h, n) => answerHtml(h, n)).reverse().join('')}</div>`;

    $('#ask', page).addEventListener('submit', e => { e.preventDefault(); ask($('#q', page).value); });
    $$('[data-q]', page).forEach(b => b.addEventListener('click', () => ask(b.dataset.q)));
    $('#brief', page).addEventListener('click', () => brief($('#brief-shift', page).value));
    $('#narrate', page)?.addEventListener('change', e => { narrate = e.target.checked; });
    $$('[data-export]', page).forEach(b => b.addEventListener('click', () => download(history[Number(b.dataset.export)].answer.interpretation.report, b.dataset.format)));
    $('#q', page).focus();
  }

  async function ask(question) {
    question = (question ?? '').trim();
    if (!question) return;
    const pending = { question, answer: null };
    page.querySelector('#conversation').insertAdjacentHTML('afterbegin', `<div class="analyst-turn"><div class="analyst-q">${esc(question)}</div><div class="muted">Working it out…</div></div>`);
    try {
      pending.answer = await post('/api/analyst/ask', { question, narrate });
    } catch (error) {
      pending.error = error;
    }
    history.push(pending);
    render();
  }

  async function brief(shift) {
    const name = shifts.find(x => x.code === shift)?.name;
    await ask(name ? `Brief me — ${name}` : 'Brief me');
  }

  render();
  if (initialQuestion && !initialAsked) {
    initialAsked = true;
    void ask(initialQuestion);
  }
  return { dispose() {} };
}
