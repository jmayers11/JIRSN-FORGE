import assert from 'node:assert/strict';
import test from 'node:test';
import { cnc02MaintenanceWindowEvidence, delayedPromiseDate, healthyBaselineAssertions, selectAsm03Callout, selectCnc02ExpiringQualification, selectDayShiftScenarioRoster, selectOpenSensorPromiseLine, sisalQuantityEvidence, sisalSplitSteps } from '../../src/Forge.Host/wwwroot/js/data/whiskerworks-demo.js';

test('start-shift roster selection is stable and separates three callouts from two expected absences', () => {
  const workers = [
    { personId: 'd', displayName: 'Delta', isActive: true, shiftCode: 'SHIFT-DAY' },
    { personId: 'b', displayName: 'Bravo', isActive: true, shiftCode: 'SHIFT-DAY' },
    { personId: 'inactive', isActive: false, shiftCode: 'SHIFT-DAY' },
    { personId: 'a', displayName: 'Alpha', isActive: true, shiftCode: 'SHIFT-DAY' },
    { personId: 'c', displayName: 'Charlie', isActive: true, shiftCode: 'SHIFT-DAY' },
    { personId: 'e', displayName: 'Echo', isActive: true, shiftCode: 'SHIFT-DAY' },
    { personId: 'swing', isActive: true, shiftCode: 'SHIFT-SWG' },
  ];
  const first = selectDayShiftScenarioRoster(workers);
  const second = selectDayShiftScenarioRoster([...workers].reverse());
  assert.deepEqual(first, second);
  assert.deepEqual(first.callouts.map(worker => worker.personId), ['a', 'b', 'c']);
  assert.deepEqual(first.missing.map(worker => worker.personId), ['d', 'e']);
  assert.throws(() => selectDayShiftScenarioRoster(workers.slice(0, 3)), /at least five active scheduled workers/);
});

test('ASM-03 scenario only selects a present, qualified crew member when exactly two are covering the machine', () => {
  const now = Date.parse('2026-10-23T13:00:00Z');
  const workers = [
    { personId: 'heavy-b', displayName: 'Jordan Ellis', isActive: true, shiftCode: 'SHIFT-DAY', assignment: { machineCode: 'ASM-03' }, presentUntil: '2026-10-23T14:00:00Z' },
    { personId: 'heavy-a', displayName: 'Avery Martin', isActive: true, shiftCode: 'SHIFT-DAY', assignment: { machineCode: 'ASM-03' }, presentUntil: '2026-10-23T14:00:00Z' },
    { personId: 'not-assigned', isActive: true, shiftCode: 'SHIFT-DAY', assignment: { machineCode: 'ASM-01' }, presentUntil: '2026-10-23T14:00:00Z' },
  ];
  const qualifications = ['heavy-a', 'heavy-b', 'not-assigned'].map(personId => ({ personId, code: 'QUAL-ASM-HVY', status: 'Qualified' }));
  assert.deepEqual(selectAsm03Callout(workers, qualifications, now), { worker: workers[1], crew: [workers[1], workers[0]] });
  assert.throws(() => selectAsm03Callout(workers, qualifications.slice(0, 1), now), /exactly two/);
  assert.throws(() => selectAsm03Callout(workers, [...qualifications.filter(row => row.personId !== 'heavy-b'), { personId: 'heavy-b', code: 'QUAL-ASM-HVY', status: 'Expired' }], now), /exactly two/);
});

test('sensor shortage selects only an approved open purchase line with remaining quantity, deterministically', () => {
  const purchaseOrders = [
    { poNumber: 30, status: 'Approved', lines: [{ lineNumber: 1, partNumber: 'WW-SENS-PROX-05', status: 'Open', open: 4, promisedDate: '2026-11-30' }] },
    { poNumber: 20, status: 'Approved', lines: [{ lineNumber: 2, partNumber: 'WW-SENS-PROX-05', status: 'Open', open: 8, promisedDate: '2026-11-20' }] },
    { poNumber: 10, status: 'Draft', lines: [{ lineNumber: 1, partNumber: 'WW-SENS-PROX-05', status: 'Open', open: 12, promisedDate: '2026-11-10' }] },
    { poNumber: 5, status: 'Approved', lines: [{ lineNumber: 1, partNumber: 'WW-SENS-PROX-05', status: 'Open', open: 0, promisedDate: '2026-11-01' }] },
  ];
  assert.deepEqual(selectOpenSensorPromiseLine(purchaseOrders), { order: purchaseOrders[1], line: purchaseOrders[1].lines[0] });
  assert.throws(() => selectOpenSensorPromiseLine(purchaseOrders.slice(2)), /No visible approved, open purchase line/);
});

test('sensor shortage delay advances exactly four calendar days and rejects invalid dates', () => {
  assert.equal(delayedPromiseDate('2026-10-30'), '2026-11-03');
  assert.equal(delayedPromiseDate('2028-02-27'), '2028-03-02');
  assert.throws(() => delayedPromiseDate('2026-02-30'), /invalid promised date/);
  assert.throws(() => delayedPromiseDate('2026-10-30', 0), /positive whole-day delay/);
});

test('S05 selects only a current five-day CNC-02 mill qualification from an active assigned operator', () => {
  const now = Date.parse('2026-10-23T13:00:00Z');
  const workers = [
    { personId: 'target', displayName: 'CNC Operator', isActive: true, assignment: { machineCode: 'CNC-02' } },
    { personId: 'other-machine', displayName: 'Other Machine', isActive: true, assignment: { machineCode: 'CNC-01' } },
    { personId: 'inactive', displayName: 'Inactive Operator', isActive: false, assignment: { machineCode: 'CNC-02' } },
  ];
  const qualifications = [
    { personId: 'target', code: 'QUAL-CNC-MILL', status: 'Qualified', expiresAt: '2026-10-28T13:00:00Z' },
    { personId: 'other-machine', code: 'QUAL-CNC-MILL', status: 'Qualified', expiresAt: '2026-10-24T13:00:00Z' },
    { personId: 'inactive', code: 'QUAL-CNC-MILL', status: 'Qualified', expiresAt: '2026-10-24T13:00:00Z' },
  ];
  assert.equal(selectCnc02ExpiringQualification(workers, qualifications, now).personId, 'target');
  assert.throws(() => selectCnc02ExpiringQualification(workers, qualifications.map(row => ({ ...row, expiresAt: '2026-10-29T13:00:00Z' })), now), /within five days/);
});

test('CNC-02 maintenance evidence verifies the exact one-hour four-hour-ahead window and pending independent decision', () => {
  const requestedAt = '2026-10-23T13:00:00.000Z';
  const target = { outageId: 42, workOrderId: 87, requestedAt, startsAt: '2026-10-23T17:00:00.000Z', endsAt: '2026-10-23T18:00:00.000Z' };
  const outage = { ...target, machineCode: 'CNC-02', status: 'Proposed', proposedBy: 'planner-1', decidedBy: null, downtimeId: null };
  const evidence = cnc02MaintenanceWindowEvidence({ outages: [outage], target });
  assert.equal(evidence.window.outcome, 'Pass');
  assert.equal(evidence.approval.outcome, 'Pass');
  assert.match(evidence.approval.detail, /separate authorized person/);
  assert.equal(cnc02MaintenanceWindowEvidence({ outages: [{ ...outage, workOrderId: 88 }], target }).window.outcome, 'Fail');
  assert.equal(cnc02MaintenanceWindowEvidence({ outages: [{ ...outage, startsAt: '2026-10-23T16:00:00.000Z' }], target }).window.outcome, 'Fail');
});

test('CNC-02 maintenance evidence accepts only an independent approval and marks legacy request time unknown', () => {
  const outage = { outageId: 42, workOrderId: 87, machineCode: 'CNC-02', status: 'Approved',
    startsAt: '2026-10-23T17:00:00.000Z', endsAt: '2026-10-23T18:00:00.000Z', proposedBy: 'planner-1', decidedBy: 'manager-2', downtimeId: 9 };
  const target = { ...outage, requestedAt: null };
  const evidence = cnc02MaintenanceWindowEvidence({ outages: [outage], target });
  assert.equal(evidence.window.outcome, 'Indeterminate');
  assert.equal(evidence.approval.outcome, 'Pass');
  assert.equal(cnc02MaintenanceWindowEvidence({ outages: [{ ...outage, decidedBy: 'planner-1' }], target }).approval.outcome, 'Fail');
  assert.equal(cnc02MaintenanceWindowEvidence({ outages: [{ ...outage, status: 'Withdrawn' }], target }).approval.outcome, 'Fail');
});

test('S01 healthy-baseline evidence passes only when commissioned machines, readiness, holds, orders and shift presence agree', () => {
  const sources = Object.fromEntries(['resources', 'assets', 'operations', 'activeWork', 'downtime', 'maintenance', 'holds', 'orders', 'workers', 'unavailability', 'wipReconciliation']
    .map(source => [source, 'available']));
  const details = {
    machineAssets: Array.from({ length: 24 }, (_, index) => ({ machineCode: `M-${index}`, active: true, assetStatus: 'Active' })),
    machineSignals: Array.from({ length: 24 }, (_, index) => ({ machineCode: `M-${index}`, status: 'ready' })),
    machineMaintenance: Array.from({ length: 24 }, (_, index) => ({ machineCode: `M-${index}`, overdueWorkOrders: [] })),
    operationBlocks: [], activeHolds: [],
    releasedOrderOperations: [{ orderNumber: 100, operations: [{ sequence: 10, executable: true }] }],
    wipReconciliation: { orderCount: 1, rows: [{ orderNumber: 100, partNumber: 'WW-PLATE-01', lotNumber: 'LOT-01', status: 'Balanced', unexplained: 0 }] },
    workers: [{ personId: 'day-1', isActive: true, shiftCode: 'SHIFT-DAY', presenceKind: 'SupervisorConfirmed' }],
    unavailability: [],
  };
  const snapshot = { sources, details, capturedAt: '2026-10-26T12:00:00.000Z' };
  assert.deepEqual(healthyBaselineAssertions(snapshot).map(assertion => assertion.outcome), ['Pass', 'Pass', 'Pass', 'Pass', 'Pass', 'Pass']);
  assert.equal(healthyBaselineAssertions({ ...snapshot, details: { ...details, wipReconciliation: { orderCount: 1, rows: [] } } })[2].outcome, 'Indeterminate');
  details.machineMaintenance[4].overdueWorkOrders.push({ workOrderId: 105, kind: 'Preventive' });
  assert.equal(healthyBaselineAssertions(snapshot)[1].outcome, 'Fail');
  details.machineMaintenance[4].overdueWorkOrders = [];
  details.wipReconciliation.rows[0].status = 'Unexplained';
  assert.equal(healthyBaselineAssertions(snapshot)[2].outcome, 'Fail');
  details.wipReconciliation.rows[0].status = 'Balanced';
  details.machineSignals[0].status = 'unknown';
  details.activeHolds.push({ kind: 'Quality' });
  details.releasedOrderOperations[0].operations[0].executable = false;
  details.unavailability.push({ personId: 'day-1', startsAt: '2026-10-26T11:00:00.000Z', endsAt: '2026-10-26T13:00:00.000Z' });
  assert.deepEqual(healthyBaselineAssertions(snapshot).map(assertion => assertion.outcome), ['Fail', 'Pass', 'Pass', 'Fail', 'Fail', 'Fail']);
  sources.assets = 'unavailable under current permissions or source state';
  assert.equal(healthyBaselineAssertions(snapshot)[0].outcome, 'Indeterminate');
  sources.maintenance = 'unavailable under current permissions or source state';
  assert.equal(healthyBaselineAssertions(snapshot)[1].outcome, 'Indeterminate');
  sources.wipReconciliation = 'unavailable under current permissions or source state';
  assert.equal(healthyBaselineAssertions(snapshot)[2].outcome, 'Indeterminate');
});

test('sisal replay accepts only known pre-split, interrupted, and completed source states', () => {
  assert.deepEqual(sisalSplitSteps(250, 0), ['split', 'scrap']);
  assert.deepEqual(sisalSplitSteps(5, 245), ['scrap']);
  assert.deepEqual(sisalSplitSteps(0, 245), []);
  assert.throws(() => sisalSplitSteps(200, 50), /safe replay state/);
});

test('sisal evidence requires exact child quantities, 245 m split, 5 m scrap, and zero released parent balance', () => {
  const positions = [
    { lotNumber: 'LOT-SISAL-M01-A', locationCode: 'WW-RAW', qualityStatus: 'Released', quantity: 100 },
    { lotNumber: 'LOT-SISAL-M01-B', locationCode: 'WW-RAW', qualityStatus: 'Released', quantity: 100 },
    { lotNumber: 'LOT-SISAL-M01-C', locationCode: 'WW-RAW', qualityStatus: 'Released', quantity: 45 },
  ];
  const transactions = [
    ...positions.map(position => ({ kind: 'Split', lotNumber: position.lotNumber, toLocation: 'WW-RAW', quantity: position.quantity })),
    { kind: 'Scrap', lotNumber: 'LOT-SISAL-M01', fromLocation: 'WW-RAW', quantity: 5 },
  ];
  assert.equal(sisalQuantityEvidence({ positions, transactions }).pass, true);
  assert.equal(sisalQuantityEvidence({ positions: positions.map((row, index) => index === 2 ? { ...row, quantity: 44 } : row), transactions }).pass, false);
  assert.equal(sisalQuantityEvidence({ positions: positions.map((row, index) => index === 0 ? { ...row, locationCode: 'WW-SHIP' } : row), transactions }).pass, false);
  assert.equal(sisalQuantityEvidence({ positions, transactions: transactions.slice(0, -1) }).pass, false);
});
