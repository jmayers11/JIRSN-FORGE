import assert from 'node:assert/strict';
import test from 'node:test';
import { plantSetupPage } from '../../src/Forge.Host/wwwroot/js/pages/plantsetup.js';
import { installBrowser, response, fire } from './browser-harness.mjs';

test('plant setup displays the already-returned site closure dates and safely escapes their notes', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const resources = {
    sites: [{ code: 'PLANT1', name: 'Main plant', timeZone: 'America/New_York' }], safetyAreas: [], workCenters: [], machines: [], activityTypes: [],
    machineActivities: [], shifts: [{ code: 'DAY', siteCode: 'PLANT1', name: 'Day shift', start: '08:00:00', durationMinutes: 480, days: [1, 2, 3, 4, 5] }],
    workCenterShifts: [], closures: [{ siteCode: 'PLANT1', date: '2026-12-25', note: '<holiday>' }], workCenterActivities: [],
  };
  globalThis.fetch = path => Promise.resolve(response(String(path).includes('/api/resources/?includeRetired=true') ? resources : []));
  await plantSetupPage(page, { me: { personId: '1', permissions: ['resources.read'] } });
  assert.match(page.innerHTML, /Site closures/);
  assert.match(page.innerHTML, /2026-12-25/);
  assert.match(page.innerHTML, /&lt;holiday&gt;/);
  assert.doesNotMatch(page.innerHTML, /<holiday>/);
});

test('plant setup shows a live site-local clock and requires an explicit zone for new sites', async t => {
  const browser = installBrowser(t), page = browser.document.createElement('div'); browser.document.body.append(page);
  const resources = {
    sites: [{ code: 'PLANT1', name: 'Main plant', timeZone: 'America/Los_Angeles' }], safetyAreas: [], workCenters: [], machines: [], activityTypes: [],
    machineActivities: [], shifts: [], workCenterShifts: [], closures: [], workCenterActivities: [],
  };
  globalThis.fetch = path => Promise.resolve(response(String(path).includes('/api/resources/?includeRetired=true') ? resources : []));
  const controller = await plantSetupPage(page, { me: { personId: '1', permissions: ['resources.read', 'resources.manage'] } });
  t.after(() => controller?.dispose?.());
  assert.match(page.innerHTML, /Site date and time/);
  assert.match(page.innerHTML, /America\/Los_Angeles/);
  assert.match(page.innerHTML, /data-site-clock="PLANT1"/);
  fire(page.querySelector('[data-act="add-site"]'), 'click');
  assert.equal(browser.document.querySelector('#f-timeZone').value, '');
  assert.equal(browser.document.querySelector('#f-timeZone').getAttribute('placeholder'), 'America/Chicago');
  controller.dispose();
});
