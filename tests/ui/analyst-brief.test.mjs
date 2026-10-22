import assert from 'node:assert/strict';
import test from 'node:test';
import { analystPage } from '../../src/Forge.Host/wwwroot/js/pages/analyst.js';
import { session } from '../../src/Forge.Host/wwwroot/js/api.js';
import { installBrowser, response, flush, fire } from './browser-harness.mjs';

test('Brief me reads and renders the authorized brief for the selected shift', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  const before = { subject: session.subject, token: session.token }; session.subject = 'analyst-brief-user'; session.token = null; t.after(() => Object.assign(session, before));
  const brief = {
    from: '2026-10-16T00:00:00Z', to: '2026-10-16T08:00:00Z', since: '2026-10-15T00:00:00Z', generatedAt: '2026-10-16T08:00:00Z',
    headlines: ['One order needs attention'],
    sections: [{ tone: 'warn', title: 'Delivery risk', note: 'Based on released-order evidence.', facts: [{ tone: 'warn', text: 'Order 42 is behind its approved schedule.' }] }],
    omitted: [],
  };
  globalThis.fetch = path => {
    calls.push(path);
    if (path === '/api/analyst/status') return Promise.resolve(response({ narration: false, suggestions: [] }));
    if (path === '/api/resources/') return Promise.resolve(response({ shifts: [{ code: 'DAY', name: 'Day shift' }] }));
    if (path === '/api/analyst/brief?shift=DAY') return Promise.resolve(response(brief));
    return Promise.resolve(response({ detail: 'Unexpected request.' }, 404));
  };

  const controller = await analystPage(page, { query: new URLSearchParams() }); t.after(controller.dispose);
  page.querySelector('#brief-shift').value = 'DAY';
  fire(page.querySelector('#brief'), 'click');
  await flush();

  assert.ok(calls.includes('/api/analyst/brief?shift=DAY'));
  assert.equal(calls.some(path => path === '/api/analyst/ask'), false);
  assert.match(page.innerHTML, /Operations brief/);
  assert.match(page.innerHTML, /One order needs attention/);
  assert.match(page.innerHTML, /Order 42 is behind its approved schedule/);
});

test('Clear questions removes the brief and prevents an in-flight answer from returning', async t => {
  const browser = installBrowser(t), calls = [], page = browser.document.createElement('div'); browser.document.body.append(page);
  const before = { subject: session.subject, token: session.token }; session.subject = 'analyst-clear-user'; session.token = null; t.after(() => Object.assign(session, before));
  let finishQuestion;
  globalThis.fetch = path => {
    calls.push(path);
    if (path === '/api/analyst/status') return Promise.resolve(response({ narration: false, suggestions: [] }));
    if (path === '/api/resources/') return Promise.resolve(response({ shifts: [] }));
    if (path === '/api/analyst/brief') return Promise.resolve(response({
      from: '2026-10-16T00:00:00Z', to: '2026-10-16T08:00:00Z', since: '2026-10-15T00:00:00Z', generatedAt: '2026-10-16T08:00:00Z',
      headlines: ['Brief that should be cleared'], sections: [], omitted: [],
    }));
    if (path === '/api/analyst/ask') return new Promise(resolve => { finishQuestion = () => resolve(response({})); });
    return Promise.resolve(response({ detail: 'Unexpected request.' }, 404));
  };

  const controller = await analystPage(page, { query: new URLSearchParams() }); t.after(controller.dispose);
  fire(page.querySelector('#brief'), 'click');
  await flush();
  assert.match(page.innerHTML, /Brief that should be cleared/);

  page.querySelector('#q').value = 'Question that should be cleared';
  const conversation = page.querySelector('#conversation');
  conversation.insertAdjacentHTML = (position, html) => { conversation._html = position === 'afterbegin' ? html + conversation._html : conversation._html + html; };
  fire(page.querySelector('#ask'), 'submit');
  await flush();
  assert.equal(calls.includes('/api/analyst/ask'), true);
  assert.match(conversation.innerHTML, /Question that should be cleared/);

  fire(page.querySelector('#clear-questions'), 'click');
  assert.equal(page.querySelector('#q').value, '');
  assert.doesNotMatch(page.innerHTML, /Brief that should be cleared|Question that should be cleared/);
  finishQuestion();
  await flush();
  assert.equal(page.querySelector('#conversation').innerHTML, '');
});
