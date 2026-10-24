const SISAL_PARENT = 'LOT-SISAL-M01';
const SISAL_CHILDREN = [
  ['LOT-SISAL-M01-A', 100], ['LOT-SISAL-M01-B', 100], ['LOT-SISAL-M01-C', 45],
];

/** Select the same fictional shift roster on retries, independent of API response ordering. */
export function selectDayShiftScenarioRoster(workers) {
  const scheduled = (workers ?? []).filter(worker => worker.isActive && worker.shiftCode === 'SHIFT-DAY')
    .sort(compareWorker);
  if (scheduled.length < 5) throw new Error('The WW-P01 Day Shift needs at least five active scheduled workers. Seed the fictional workforce first.');
  return { callouts: scheduled.slice(0, 3), missing: scheduled.slice(3, 5) };
}

/** Select one current ASM-03 team member only when the seeded two-person rule can actually be exercised. */
export function selectAsm03Callout(workers, qualifications, now = Date.now()) {
  const qualifiedIds = new Set((qualifications ?? []).filter(record => record.code === 'QUAL-ASM-HVY'
    && record.status === 'Qualified' && (!record.expiresAt || Date.parse(record.expiresAt) > now))
    .map(record => record.personId));
  const crew = (workers ?? []).filter(worker => worker.isActive && worker.shiftCode === 'SHIFT-DAY' && worker.assignment?.machineCode === 'ASM-03'
    && worker.presentUntil && Date.parse(worker.presentUntil) > now && qualifiedIds.has(worker.personId))
    .sort(compareWorker);
  if (crew.length !== 2) throw new Error(`The two-person safety scenario requires exactly two currently present, independently qualified workers assigned to ASM-03; Forge reports ${crew.length}. No callout was recorded.`);
  return { worker: crew[0], crew };
}

export function selectOpenSensorPromiseLine(purchaseOrders) {
  const candidates = (purchaseOrders ?? []).flatMap(order => (order.lines ?? [])
    .filter(line => order.status === 'Approved' && line.status === 'Open' && Number(line.open) > 0
      && line.partNumber === 'WW-SENS-PROX-05')
    .map(line => ({ order, line })))
    .sort((a, b) => String(a.line.promisedDate).localeCompare(String(b.line.promisedDate))
      || Number(a.order.poNumber) - Number(b.order.poNumber) || Number(a.line.lineNumber) - Number(b.line.lineNumber));
  if (!candidates.length) throw new Error('No visible approved, open purchase line for WW-SENS-PROX-05 has remaining quantity.');
  return candidates[0];
}

/** Select a current CNC-02 mill qualification whose recorded expiry is within the S05 five-day window. */
export function selectCnc02ExpiringQualification(workers, qualifications, observedAt = Date.now(), windowDays = 5) {
  if (!Number.isFinite(observedAt) || !Number.isSafeInteger(windowDays) || windowDays <= 0)
    throw new Error('A valid observation time and positive whole-day window are required.');
  const workerById = new Map((workers ?? []).filter(worker => worker.isActive && worker.assignment?.machineCode === 'CNC-02')
    .map(worker => [String(worker.personId), worker]));
  const candidates = (qualifications ?? []).filter(record => record.code === 'QUAL-CNC-MILL' && record.status === 'Qualified'
    && workerById.has(String(record.personId)) && Number.isFinite(Date.parse(record.expiresAt))
    && Date.parse(record.expiresAt) > observedAt && Date.parse(record.expiresAt) <= observedAt + windowDays * 86400000)
    .sort((a, b) => Date.parse(a.expiresAt) - Date.parse(b.expiresAt) || String(a.personId).localeCompare(String(b.personId)));
  if (!candidates.length) throw new Error('No current CNC-02 mill operator has a recorded qualification expiry within five days. No PM window was proposed.');
  const qualification = candidates[0];
  return {
    personId: qualification.personId,
    code: qualification.code,
    expiresAt: qualification.expiresAt,
    observedAt: new Date(observedAt).toISOString(),
    displayName: workerById.get(String(qualification.personId)).displayName,
  };
}

export function delayedPromiseDate(promisedDate, delayDays = 4) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(String(promisedDate)) || !Number.isSafeInteger(delayDays) || delayDays <= 0)
    throw new Error('A valid purchase-line date and positive whole-day delay are required.');
  const [year, month, day] = promisedDate.split('-').map(Number);
  const date = new Date(Date.UTC(year, month - 1, day));
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day)
    throw new Error('Purchase line has an invalid promised date.');
  date.setUTCDate(date.getUTCDate() + delayDays);
  return date.toISOString().slice(0, 10);
}

/** Verify the recorded CNC-02 maintenance proposal and any independent decision from source records. */
export function cnc02MaintenanceWindowEvidence({ outages = [], target = {} } = {}) {
  const outage = outages.find(row => Number(row.outageId) === Number(target.outageId));
  if (!outage) return {
    window: { outcome: 'Fail', detail: `Outage ${target.outageId ?? 'from the scenario record'} is not present in the authorized maintenance read.` },
    approval: { outcome: 'Fail', detail: 'No source outage exists to verify the independent approval workflow.' },
  };

  const exactTarget = outage.machineCode === 'CNC-02'
    && Number(outage.workOrderId) === Number(target.workOrderId)
    && Date.parse(outage.startsAt) === Date.parse(target.startsAt)
    && Date.parse(outage.endsAt) === Date.parse(target.endsAt);
  const oneHour = Date.parse(outage.endsAt) - Date.parse(outage.startsAt) === 60 * 60 * 1000;
  const requestAt = Date.parse(target.requestedAt);
  const startAt = Date.parse(outage.startsAt);
  const exactOffset = Number.isFinite(requestAt) && Number.isFinite(startAt) && startAt - requestAt === 4 * 60 * 60 * 1000;
  const windowOutcome = !exactTarget || !oneHour ? 'Fail' : Number.isFinite(requestAt) ? (exactOffset ? 'Pass' : 'Fail') : 'Indeterminate';
  const windowDetail = !exactTarget
    ? `Outage ${outage.outageId} does not match the recorded CNC-02 work order and proposed interval.`
    : !oneHour ? `Outage ${outage.outageId} is not exactly one hour long.`
      : !Number.isFinite(requestAt) ? `Outage ${outage.outageId} matches the saved interval, but the original request time was not retained; the four-hour offset cannot be verified.`
        : `Outage ${outage.outageId} matches CNC-02 work order ${outage.workOrderId}; it starts four hours after request and lasts one hour.`;

  const proposedBy = String(outage.proposedBy ?? '');
  const decidedBy = String(outage.decidedBy ?? '');
  let approvalOutcome = 'Fail';
  let approvalDetail = `Outage status ${outage.status ?? 'unknown'} is not a valid pending or independently approved state.`;
  if (exactTarget && outage.status === 'Proposed' && !decidedBy && outage.downtimeId == null) {
    approvalOutcome = 'Pass';
    approvalDetail = `Outage ${outage.outageId} remains proposed with no downtime posted; a separate authorized person must review and decide it.`;
  } else if (exactTarget && outage.status === 'Approved' && proposedBy && decidedBy && proposedBy.toLowerCase() !== decidedBy.toLowerCase() && outage.downtimeId != null) {
    approvalOutcome = 'Pass';
    approvalDetail = `Outage ${outage.outageId} was approved by ${decidedBy}, independently of proposer ${proposedBy}, and has downtime record ${outage.downtimeId}.`;
  } else if (outage.status === 'Approved') {
    approvalDetail = `Outage ${outage.outageId} does not prove an independent approved decision and corresponding downtime record.`;
  }
  return {
    outage,
    window: { outcome: windowOutcome, detail: windowDetail },
    approval: { outcome: approvalOutcome, detail: approvalDetail },
  };
}

/** Evaluate only the healthy-baseline claims supported by captured Forge source reads. */
export function healthyBaselineAssertions(snapshot) {
  const { sources = {}, details = {} } = snapshot ?? {};
  const result = (check, requiredSources, evaluate) => {
    const missing = requiredSources.filter(source => sources[source] !== 'available');
    if (missing.length) return { check, outcome: 'Indeterminate', detail: `Required source(s) unavailable: ${missing.join(', ')}.` };
    return { check, ...evaluate() };
  };
  const machineSignals = details.machineSignals ?? [];
  const activeMachines = (details.machineAssets ?? []).filter(machine => machine.active);
  const signalByMachine = new Map(machineSignals.map(machine => [machine.machineCode, machine]));
  const notReady = activeMachines.filter(machine => machine.assetStatus !== 'Active' || signalByMachine.get(machine.machineCode)?.status !== 'ready');
  const checks = [result('All 24 active machines commissioned and ready', ['resources', 'assets', 'operations', 'activeWork', 'downtime', 'maintenance'], () => ({
    outcome: activeMachines.length === 24 && notReady.length === 0 ? 'Pass' : 'Fail',
    detail: `${activeMachines.length} active machines; ${notReady.length} are not commissioned Active and Ready${notReady.length ? ` (${notReady.map(machine => machine.machineCode).join(', ')})` : ''}.`,
  }))];

  checks.push(result('Preventive maintenance current on all active machines', ['resources', 'maintenance'], () => {
    const maintenanceByMachine = new Map((details.machineMaintenance ?? []).map(item => [item.machineCode, item]));
    const missingEvidence = activeMachines.filter(machine => !maintenanceByMachine.has(machine.machineCode));
    const overdue = activeMachines.flatMap(machine => (maintenanceByMachine.get(machine.machineCode)?.overdueWorkOrders ?? [])
      .map(order => `${machine.machineCode} WO ${order.workOrderId}`));
    const complete = activeMachines.length === 24 && missingEvidence.length === 0;
    return { outcome: complete && overdue.length === 0 ? 'Pass' : 'Fail',
      detail: `${activeMachines.length} active machines; ${missingEvidence.length} missing maintenance evidence; ${overdue.length} open PM/inspection/calibration items are due${overdue.length ? ` (${overdue.join(', ')})` : ''}.` };
  }));

  checks.push(result('Issued WIP material reconciles exactly', ['orders', 'wipReconciliation'], () => {
    const evidence = details.wipReconciliation ?? {};
    const rows = evidence.rows ?? [];
    if (!evidence.orderCount || !rows.length) return { outcome: 'Indeterminate',
      detail: `${evidence.orderCount ?? 0} released open orders checked; no issued material reconciliation rows were returned.` };
    const unbalanced = rows.filter(row => row.status !== 'Balanced');
    return { outcome: unbalanced.length === 0 ? 'Pass' : 'Fail',
      detail: `${evidence.orderCount} released open orders; ${rows.length} issued part/lot balances; ${unbalanced.length} are not exactly Balanced${unbalanced.length ? ` (${unbalanced.map(row => `${row.orderNumber}/${row.partNumber}/${row.lotNumber ?? 'unlotted'}: ${row.status}, unexplained ${row.unexplained}`).join('; ')})` : ''}.` };
  }));

  checks.push(result('Zero blocking Safety or Quality exceptions', ['operations', 'holds'], () => {
    const blockedDimensions = (details.operationBlocks ?? []).filter(block => ['Safety', 'Quality'].some(name => String(block.dimension).toLowerCase().includes(name.toLowerCase())));
    const activeHolds = (details.activeHolds ?? []).filter(hold => ['Safety', 'Quality'].includes(hold.kind));
    const count = blockedDimensions.length + activeHolds.length;
    return { outcome: count === 0 ? 'Pass' : 'Fail', detail: `${blockedDimensions.length} blocking Safety/Quality readiness dimensions and ${activeHolds.length} active Safety/Quality holds.` };
  }));

  checks.push(result('All released open order operations executable', ['orders', 'operations'], () => {
    const orders = details.releasedOrderOperations ?? [];
    const pending = orders.flatMap(order => order.operations);
    const notExecutable = pending.filter(operation => operation.executable !== true);
    return { outcome: orders.length > 0 && pending.length > 0 && notExecutable.length === 0 ? 'Pass' : 'Fail',
      detail: `${orders.length} released orders; ${pending.length} open operations; ${notExecutable.length} not proven executable.` };
  }));

  checks.push(result('Full active Day Shift roster present', ['workers', 'unavailability'], () => {
    const roster = (details.workers ?? []).filter(worker => worker.isActive && worker.shiftCode === 'SHIFT-DAY');
    const capturedAt = Date.parse(snapshot.capturedAt);
    const absent = roster.filter(worker => !['ClockedIn', 'SupervisorConfirmed'].includes(worker.presenceKind)
      || worker.presentUntil && Date.parse(worker.presentUntil) <= capturedAt
      || (details.unavailability ?? []).some(window => window.personId === worker.personId
        && Date.parse(window.startsAt) <= capturedAt && Date.parse(window.endsAt) > capturedAt));
    return { outcome: roster.length > 0 && absent.length === 0 ? 'Pass' : 'Fail',
      detail: `${roster.length - absent.length}/${roster.length} active Day Shift workers have current confirmed presence${absent.length ? `; absent: ${absent.map(worker => worker.personId).join(', ')}` : ''}.` };
  }));

  return checks;
}

function compareWorker(left, right) {
  const leftName = String(left.displayName ?? '').toLowerCase();
  const rightName = String(right.displayName ?? '').toLowerCase();
  if (leftName !== rightName) return leftName < rightName ? -1 : 1;
  const leftId = String(left.personId ?? '');
  const rightId = String(right.personId ?? '');
  return leftId === rightId ? 0 : leftId < rightId ? -1 : 1;
}

/** Return only transitions supported by the inventory ledger; reject ambiguous partial balances. */
export function sisalSplitSteps(parentReleased, childReleased) {
  if (!Number.isFinite(parentReleased) || !Number.isFinite(childReleased) || parentReleased < 0 || childReleased < 0)
    throw new Error('Sisal lot quantities must be finite, non-negative values.');
  if (parentReleased === 250 && childReleased === 0) return ['split', 'scrap'];
  if (parentReleased === 5 && childReleased === 245) return ['scrap'];
  if (parentReleased === 0 && childReleased === 245) return [];
  throw new Error(`The sisal lot does not match a safe replay state (parent ${parentReleased} m, child total ${childReleased} m). No split or scrap was recorded.`);
}

/** Reconcile stock positions with immutable split/scrap ledger entries. */
export function sisalQuantityEvidence({ positions = [], transactions = [] }) {
  const parentReleased = positions.filter(row => row.lotNumber === SISAL_PARENT && row.qualityStatus === 'Released')
    .reduce((sum, row) => sum + Number(row.quantity), 0);
  const children = SISAL_CHILDREN.map(([lotNumber, expectedQuantity]) => ({
    lotNumber,
    expectedQuantity,
    quantity: positions.filter(row => row.lotNumber === lotNumber && row.qualityStatus === 'Released')
      .reduce((sum, row) => sum + Number(row.quantity), 0),
    rawQuantity: positions.filter(row => row.lotNumber === lotNumber && row.qualityStatus === 'Released' && row.locationCode === 'WW-RAW')
      .reduce((sum, row) => sum + Number(row.quantity), 0),
  }));
  const splitQuantity = transactions.filter(row => row.kind === 'Split' && SISAL_CHILDREN.some(([lot]) => lot === row.lotNumber) && row.toLocation === 'WW-RAW')
    .reduce((sum, row) => sum + Number(row.quantity), 0);
  const scrappedQuantity = transactions.filter(row => row.kind === 'Scrap' && row.lotNumber === SISAL_PARENT && row.fromLocation === 'WW-RAW')
    .reduce((sum, row) => sum + Number(row.quantity), 0);
  const childQuantity = children.reduce((sum, row) => sum + row.quantity, 0);
  return {
    pass: children.every(row => row.quantity === row.expectedQuantity && row.rawQuantity === row.expectedQuantity)
      && childQuantity === 245 && splitQuantity === 245 && scrappedQuantity === 5 && parentReleased === 0,
    children, childQuantity, splitQuantity, scrappedQuantity, parentReleased,
  };
}
