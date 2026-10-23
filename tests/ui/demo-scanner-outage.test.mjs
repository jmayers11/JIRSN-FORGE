import assert from 'node:assert/strict';
import test from 'node:test';
import {
  armDemoScannerOutage, clearDemoScannerOutage, readDemoScannerOutage,
  reconcileDemoLabel, saveDemoOutageDraft, simulateDemoOutage,
} from '../../src/Forge.Host/wwwroot/js/demo-scanner-outage.mjs';

function memoryStorage() {
  const entries = new Map();
  return {
    getItem: key => entries.get(key) ?? null,
    setItem: (key, value) => entries.set(key, String(value)),
    removeItem: key => entries.delete(key),
  };
}

test('S12 scanner 503 preserves typed context and refuses duplicate attempts', () => {
  const storage = memoryStorage();
  armDemoScannerOutage(storage);
  saveDemoOutageDraft(storage, 'scanner', { reference: 'WW-REF-92', operation: 'Order 750 / op 20' });
  const failed = simulateDemoOutage(storage, 'scanner');
  assert.equal(failed.scanner.status, 'failed');
  assert.equal(failed.scanner.responseStatus, 503);
  assert.equal(failed.scanner.attempts, 1);
  assert.deepEqual(failed.scanner.draft, { reference: 'WW-REF-92', operation: 'Order 750 / op 20' });
  assert.throws(() => simulateDemoOutage(storage, 'scanner'), /already been recorded/);
});

test('S12 print timeout stays pending until a supervisor records an observed outcome', () => {
  const storage = memoryStorage();
  armDemoScannerOutage(storage);
  saveDemoOutageDraft(storage, 'scanner', { reference: 'WW-REF-92', operation: 'Order 750 / op 20' });
  simulateDemoOutage(storage, 'scanner');
  saveDemoOutageDraft(storage, 'label', { partNumber: 'WW-PCB-CTRL-200', quantity: '4', labelText: 'Order 750 / Rev B' });
  const pending = simulateDemoOutage(storage, 'label');
  assert.equal(pending.label.status, 'pending-reconciliation');
  assert.equal(pending.label.attempts, 1);
  assert.throws(() => reconcileDemoLabel(storage, { outcome: 'printed', signer: '', reason: 'Checked' }), /identity and observation note/);
  const signed = reconcileDemoLabel(storage, { outcome: 'not-printed', signer: 'person-supervisor', reason: 'Checked the printer tray and take-up bin' });
  assert.equal(signed.label.status, 'reconciled');
  assert.equal(signed.label.observedOutcome, 'not-printed');
  assert.throws(() => simulateDemoOutage(storage, 'label'), /already been recorded/);
  clearDemoScannerOutage(storage);
  assert.equal(readDemoScannerOutage(storage), null);
});
