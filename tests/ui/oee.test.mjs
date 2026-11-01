import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

const shift = (over = {}) => ({ machineCode: 'SLITTER-A', machineName: 'Slitter A', shiftName: 'Day', date: '2026-11-02', inProgress: false, scheduledMinutes: 480, plannedStopMinutes: 30,
  downtimeMinutes: 45, unexplainedMinutes: 0, runMinutes: 405, good: 190, scrap: 10, idealMinutes: 360, availability: 0.9, performance: 0.8889, quality: 0.95, oee: 0.76,
  reasons: [{ category: 'Maintenance', reasonCode: 'JAM', minutes: 30 }], notes: [], ...over });

test('OEE rolls minutes and quantities up per machine and shows each shift with its top reason', async t => {
  installBrowser(t);
  const { rollUp, oeeHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/oee.js');
  const r = rollUp([shift(), shift({ scheduledMinutes: 480, plannedStopMinutes: 0, runMinutes: 480, downtimeMinutes: 0, good: 100, scrap: 0, idealMinutes: 240 })]);
  assert.equal(Math.round(r.availability * 1000), Math.round(885 / 930 * 1000));
  assert.equal(Math.round(r.quality * 1000), Math.round(290 / 300 * 1000));
  assert.equal(r.downtime, 45);
  const html = oeeHtml({ shifts: [shift({ unexplainedMinutes: 12, inProgress: true, notes: ['12 min of stops detected from the machine still need a reason.'] })], notes: ['OEE describes machines.'] });
  assert.match(html, /SLITTER-A/);
  assert.match(html, /JAM/);
  assert.match(html, /pill info">now/);
  assert.match(html, /12 min \?/);
  assert.match(html, /76%/);
  assert.match(html, /OEE describes machines/);
  assert.match(oeeHtml({ shifts: [] }), /No shifts to show/);
});

test('downtime shows how each stop was reported and asks for a reason on detected ones', async t => {
  installBrowser(t);
  const { downtimeHtml, connectionsHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/oee.js');
  const html = downtimeHtml([
    { stoppage: { stoppageId: 4, machineCode: 'SLITTER-A', startedAt: '2026-11-02T10:00:00Z', endedAt: null, category: 'Unplanned', reasonCode: 'UNEXPLAINED', source: 'Connector', note: 'Detected' }, reasonDescription: 'Not yet explained', minutes: 12 },
    { stoppage: { stoppageId: 3, machineCode: 'SLITTER-A', startedAt: '2026-11-02T08:00:00Z', endedAt: '2026-11-02T08:30:00Z', category: 'Maintenance', reasonCode: 'JAM', source: 'Recorded', reportedBy: 'person:p1', startedBy: 'person:s1' }, reasonDescription: 'Material jam', minutes: 30 },
  ], { canRecord: true });
  assert.match(html, /Needs a reason/);
  assert.match(html, /Give reason/);
  assert.match(html, /data-end="4"/);
  assert.doesNotMatch(html, /data-end="3"/);
  assert.match(html, /Entered for the operator · reported by/);
  assert.match(html, /Machine connection/);
  const conn = connectionsHtml([{ code: 'SLITTER-A' }, { code: 'PRESS-1' }], [{ machineCode: 'SLITTER-A', kind: 'Simulator', active: true }],
    [{ machineCode: 'SLITTER-A', state: 'Running', partCount: 42, lastSeen: '2026-11-02T10:00:00Z' }], [{ kind: 'Simulator', name: 'Simulator' }], { canManage: true });
  assert.match(conn, /pill ok">Running/);
  assert.match(conn, /Not connected \(manual\)/);
  assert.match(conn, /data-connect="PRESS-1">Connect/);
});
