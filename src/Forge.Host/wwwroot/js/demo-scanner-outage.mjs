// Browser-session-only fault rehearsal for the fictional WhiskerWorks scenario.
// No printer, scanner, API or production command is called by this module.
export const DEMO_SCANNER_OUTAGE_KEY = 'forge.whiskerworks.scanner-outage.v1';
const SCENARIO = 'WW-S12-SCAN-OUTAGE';
const MAX_TEXT = 240;

function cleanText(value, label) {
  const result = String(value ?? '').trim();
  if (result.length > MAX_TEXT) throw new Error(`${label} must be ${MAX_TEXT} characters or fewer.`);
  return result;
}

function save(storage, state) {
  storage.setItem(DEMO_SCANNER_OUTAGE_KEY, JSON.stringify(state));
  return state;
}

export function readDemoScannerOutage(storage = globalThis.sessionStorage) {
  try {
    const state = JSON.parse(storage?.getItem(DEMO_SCANNER_OUTAGE_KEY) ?? 'null');
    if (state?.version !== 1 || state?.scenarioCode !== SCENARIO || state?.siteCode !== 'WW-P01'
      || !['armed', 'failed'].includes(state.scanner?.status)
      || !['armed', 'pending-reconciliation', 'reconciled'].includes(state.label?.status)
      || !validDraft(state.scanner?.draft, ['reference', 'operation'])
      || !validDraft(state.label?.draft, ['partNumber', 'quantity', 'labelText'])
      || !Number.isInteger(state.scanner?.attempts) || !Number.isInteger(state.label?.attempts)) return null;
    return state;
  } catch { return null; }
}

function validDraft(draft, fields) {
  return !!draft && typeof draft === 'object' && fields.every(field => typeof draft[field] === 'string' && draft[field].length <= MAX_TEXT);
}

export function armDemoScannerOutage(storage = globalThis.sessionStorage) {
  if (!storage) throw new Error('Browser session storage is unavailable for the demo rehearsal.');
  const existing = readDemoScannerOutage(storage);
  if (existing) return existing;
  return save(storage, {
    version: 1, scenarioCode: SCENARIO, siteCode: 'WW-P01', armedAt: new Date().toISOString(),
    scanner: { status: 'armed', attempts: 0, draft: { reference: '', operation: '' } },
    label: { status: 'armed', attempts: 0, draft: { partNumber: '', quantity: '', labelText: '' } },
  });
}

export function saveDemoOutageDraft(storage = globalThis.sessionStorage, area, draft) {
  const state = readDemoScannerOutage(storage);
  if (!state || !['scanner', 'label'].includes(area)) throw new Error('The WhiskerWorks outage rehearsal is not armed.');
  const target = state[area];
  if (target.status !== 'armed') return state;
  const fields = area === 'scanner' ? ['reference', 'operation'] : ['partNumber', 'quantity', 'labelText'];
  target.draft = Object.fromEntries(fields.map(field => [field, cleanText(draft?.[field], field)]));
  return save(storage, state);
}

export function simulateDemoOutage(storage = globalThis.sessionStorage, area) {
  const state = readDemoScannerOutage(storage);
  if (!state || !['scanner', 'label'].includes(area)) throw new Error('The WhiskerWorks outage rehearsal is not armed.');
  const target = state[area];
  if (target.status !== 'armed' || target.attempts !== 0) throw new Error('This demo outage attempt has already been recorded.');
  if (area === 'scanner') {
    if (!target.draft.reference || !target.draft.operation) throw new Error('Enter a reference and operation before simulating barcode staging.');
    target.status = 'failed';
    target.responseStatus = 503;
    target.failedAt = new Date().toISOString();
  } else {
    if (!target.draft.partNumber || !target.draft.quantity || !target.draft.labelText) throw new Error('Enter the label details before simulating the printer timeout.');
    target.status = 'pending-reconciliation';
    target.responseStatus = 503;
    target.attemptedAt = new Date().toISOString();
  }
  target.attempts = 1;
  return save(storage, state);
}

export function reconcileDemoLabel(storage = globalThis.sessionStorage, { outcome, signer, reason } = {}) {
  const state = readDemoScannerOutage(storage);
  if (!state || state.label.status !== 'pending-reconciliation') throw new Error('No demo label is waiting for reconciliation.');
  if (!['printed', 'not-printed'].includes(outcome)) throw new Error('Choose whether the label was physically observed.');
  const signedBy = cleanText(signer, 'Signer');
  const note = cleanText(reason, 'Observation note');
  if (!signedBy || !note) throw new Error('A supervisor identity and observation note are required.');
  state.label.status = 'reconciled';
  state.label.observedOutcome = outcome;
  state.label.observedBy = signedBy;
  state.label.observationNote = note;
  state.label.observedAt = new Date().toISOString();
  return save(storage, state);
}

export function clearDemoScannerOutage(storage = globalThis.sessionStorage) {
  const state = readDemoScannerOutage(storage);
  if (!state || state.scanner.status !== 'failed' || state.label.status !== 'reconciled') {
    throw new Error('Complete both the scanner failure and label reconciliation before closing the rehearsal.');
  }
  storage.removeItem(DEMO_SCANNER_OUTAGE_KEY);
}
