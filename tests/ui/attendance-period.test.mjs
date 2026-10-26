import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

test('calendar quarters are asked of the server in plant time; other periods send their own dates', async t => {
  installBrowser(t);
  const { attendanceSummaryUrl } = await import('../../src/Forge.Host/wwwroot/js/pages/workforce-intelligence.js');
  const now = new Date('2026-10-25T16:00:00Z');
  assert.equal(attendanceSummaryUrl('quarter', now), '/api/workforce/attendance-intelligence?period=quarter');
  assert.equal(attendanceSummaryUrl('previous-quarter', now), '/api/workforce/attendance-intelligence?period=previous-quarter');
  const rolling = attendanceSummaryUrl('rolling90', now);
  assert.match(rolling, /from=2026-07-27T16%3A00%3A00\.000Z&to=2026-10-25T16%3A00%3A00\.000Z$/);
});
