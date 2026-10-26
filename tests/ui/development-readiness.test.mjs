import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

test('the nomination form shows held and missing prerequisites and the qualification bottleneck before nominating', async t => {
  installBrowser(t);
  const { readinessHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/workforce-intelligence.js');
  const html = readinessHtml({ qualificationName: 'Slitting lead', alreadyHeld: false, prerequisitesMet: false,
    prerequisites: [{ code: 'SLIT-II', name: 'Slitting Level II', held: true }, { code: 'FIRST-AID', name: 'First aid', held: false, group: null }],
    benefit: 'No active worker holds Slitting lead. Prerequisites are missing; they come first.' });
  assert.match(html, /prerequisites missing/);
  assert.match(html, /✓ Slitting Level II/);
  assert.match(html, /✗ First aid/);
  assert.match(html, /Prerequisites are missing/);
  assert.match(readinessHtml({ qualificationName: 'X', alreadyHeld: false, prerequisitesMet: true, prerequisites: [], benefit: 'b' }), /No prerequisites are configured/);
});
