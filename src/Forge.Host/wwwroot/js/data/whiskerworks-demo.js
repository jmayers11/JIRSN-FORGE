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
