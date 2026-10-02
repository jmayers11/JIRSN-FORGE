// Factory Horizon arithmetic. Pure functions over API responses, so every figure on the page can be
// traced to the records it came from. Nothing here invents a number: a metric without a source
// is returned as `null` with the reason it is not available.

const H = 36e5;

export const WINDOWS = {
  '-24h': { label: '-24h', describe: 'the last 24 hours', from: now => now - 24 * H, to: now => now },
  now: { label: 'Now', describe: 'the next 8 hours', from: now => now, to: now => now + 8 * H },
  '+24h': { label: '+24h', describe: 'the next 24 hours', from: now => now, to: now => now + 24 * H },
  '+3d': { label: '+3d', describe: 'the next 3 days', from: now => now, to: now => now + 72 * H },
  '+1w': { label: '+1w', describe: 'the next 7 days', from: now => now, to: now => now + 168 * H },
};

export const CATEGORY = {
  Maintenance: { label: 'Machine Down', tone: 'bad', rank: 1 },
  SafetyStaffing: { label: 'Safety Staffing', tone: 'bad', rank: 2 },
  QualifiedLabor: { label: 'Qualified Labor', tone: 'warn', rank: 3 },
  LaborShortage: { label: 'Qualified People Booked', tone: 'warn', rank: 4 },
  MachineCapacity: { label: 'Machine Fully Booked', tone: 'warn', rank: 5 },
  RequirementProfile: { label: 'Machine Requirement Missing', tone: 'bad', rank: 2 },
  Horizon: { label: 'Not Enough Capacity', tone: 'warn', rank: 6 },
  Calendar: { label: 'No Working Time', tone: 'warn', rank: 7 },
};
export const categoryInfo = c => CATEGORY[c] ?? { label: c, tone: 'warn', rank: 9 };

// Readiness dimension names -> constraint categories.
const DIMENSION = { Equipment: 'Maintenance', Safety: 'SafetyStaffing', Workforce: 'QualifiedLabor', RequirementProfile: 'RequirementProfile' };

const t = iso => (iso ? new Date(iso).getTime() : null);
const overlapMs = (a0, a1, b0, b1) => Math.max(0, Math.min(a1, b1) - Math.max(a0, b0));

/** Hours of a downtime window inside [from, to]. An open outage counts only up to now: Forge does not guess when it ends. */
export function downtimeHours(w, from, to, now) {
  const start = t(w.startsAt);
  const end = w.endsAt ? t(w.endsAt) : now;
  return overlapMs(start, end, from, to) / H;
}

export function isDownAt(w, at) {
  return t(w.startsAt) <= at && (w.endsAt == null || t(w.endsAt) > at);
}

/** Flow order from routings: each work center's average position in the scheduled routings. */
export function flowOrder(workCenters, operations) {
  const positions = new Map();
  const byOrder = new Map();
  for (const op of operations) {
    if (!byOrder.has(op.orderNumber)) byOrder.set(op.orderNumber, []);
    byOrder.get(op.orderNumber).push(op);
  }
  for (const ops of byOrder.values()) {
    ops.sort((a, b) => a.sequence - b.sequence);
    const centers = [...new Set(ops.map(o => o.workCenterCode))];
    centers.forEach((wc, i) => {
      const p = positions.get(wc) ?? [];
      p.push(centers.length === 1 ? 0.5 : i / (centers.length - 1));
      positions.set(wc, p);
    });
  }
  const avg = wc => { const p = positions.get(wc.code); return p ? p.reduce((a, b) => a + b, 0) / p.length : null; };
  return [...workCenters].sort((a, b) => {
    const pa = avg(a), pb = avg(b);
    if (pa != null && pb != null && pa !== pb) return pa - pb;
    if (pa != null && pb == null) return -1;
    if (pa == null && pb != null) return 1;
    return a.code.localeCompare(b.code);
  });
}

/**
 * Builds the Factory Horizon view.
 * @param d { resources, schedule, constraints, downtime, capacity, orders, activeWork, now, windowKey, safetyArea }
 */
export function buildHorizon(d) {
  const now = d.now;
  const win = WINDOWS[d.windowKey] ?? WINDOWS.now;
  const from = win.from(now), to = win.to(now);
  const res = d.resources ?? {};
  let workCenters = res.workCenters ?? [];
  if (d.safetyArea) workCenters = workCenters.filter(w => w.safetyAreaCode === d.safetyArea);
  const machines = res.machines ?? [];
  const machineWc = new Map(machines.map(m => [m.code, m.workCenterCode]));
  const result = d.schedule?.result ?? null;
  const ops = result?.operations ?? [];
  const schedOrders = result?.orders ?? [];
  const orderStatus = new Map(schedOrders.map(o => [o.orderNumber, o.status]));
  const constraints = d.constraints ?? [];
  const downtime = d.downtime ?? [];
  const capacityMachines = d.capacity?.output?.machines ?? null;
  const opWc = new Map(ops.map(o => [`${o.orderNumber}/${o.sequence}`, o.workCenterCode]));

  const constraintWcs = c => {
    const set = new Set();
    if (workCenters.some(w => w.code === c.subject) || (res.workCenters ?? []).some(w => w.code === c.subject)) set.add(c.subject);
    if (machineWc.has(c.subject)) set.add(machineWc.get(c.subject));
    for (const key of c.affectedOperations ?? []) if (opWc.has(key)) set.add(opWc.get(key));
    return set;
  };
  const inWindow = c => t(c.windowFrom) <= to && t(c.windowUntil) >= from;
  const liveConstraints = constraints.filter(c => (c.status === 'Open' || c.status === 'Investigating') && inWindow(c));

  const centers = flowOrder(workCenters, ops).map(wc => {
    const ms = machines.filter(m => m.workCenterCode === wc.code);
    const downNow = ms.filter(m => downtime.some(w => w.machineCode === m.code && isDownAt(w, now)));
    const downHours = downtime.filter(w => ms.some(m => m.code === w.machineCode)).reduce((s, w) => s + downtimeHours(w, from, to, now), 0);

    let workingHours = null, executableHours = null;
    if (capacityMachines) {
      const per = new Map();
      for (const c of capacityMachines.filter(c => c.workCenterCode === wc.code)) {
        const p = per.get(c.machineCode) ?? { w: 0, e: 0 };
        p.w = Math.max(p.w, Number(c.workingHours)); p.e = Math.max(p.e, Number(c.executableHours));
        per.set(c.machineCode, p);
      }
      workingHours = [...per.values()].reduce((s, p) => s + p.w, 0);
      executableHours = [...per.values()].reduce((s, p) => s + p.e, 0);
    }

    const wcOps = ops.filter(o => o.workCenterCode === wc.code);
    const bookedHours = wcOps.reduce((s, o) => s + (o.segments ?? []).reduce((x, g) => x + overlapMs(t(g.start), t(g.end), from, to), 0), 0) / H;
    const load = workingHours ? bookedHours / workingHours : null;

    const wcConstraints = liveConstraints.filter(c => constraintWcs(c).has(wc.code))
      .sort((a, b) => categoryInfo(a.category).rank - categoryInfo(b.category).rank);
    const readiness = (d.activeWork ?? []).filter(r => {
      const w = r.output?.work ?? {};
      return w.workCenterCode === wc.code || (w.machineCode && machineWc.get(w.machineCode) === wc.code);
    });
    const blocked = readiness.filter(r => !r.output.executable);

    const orderNumbers = [...new Set(wcOps.map(o => o.orderNumber))];
    const late = orderNumbers.filter(n => ['Late', 'Unschedulable'].includes(orderStatus.get(n)));
    const atRisk = orderNumbers.filter(n => orderStatus.get(n) === 'AtRisk');

    let primary = null;
    if (downNow.length) primary = { category: 'Maintenance', detail: `${downNow.map(m => m.code).join(', ')} down now` };
    else if (blocked.length) {
      const dim = blocked[0].output.primaryConstraint;
      primary = { category: DIMENSION[dim] ?? dim, detail: `${blocked[0].output.work.activityCode}: ${dim}` };
    } else if (wcConstraints.length) primary = { category: wcConstraints[0].category, detail: wcConstraints[0].finding };

    let status;
    if (ms.length && downNow.length === ms.length) status = 'stopped';
    else if (primary) status = 'constrained';
    else if (bookedHours > 0 || readiness.length) status = 'ready';
    else status = 'idle';

    let state;
    if (status === 'stopped') state = { text: 'Stopped — all machines down', tone: 'bad' };
    else if (primary) state = { text: categoryInfo(primary.category).label, tone: categoryInfo(primary.category).tone === 'bad' ? 'bad' : 'warn' };
    else if (late.length) {
      const stuck = late.filter(n => orderStatus.get(n) === 'Unschedulable').length;
      state = stuck === late.length
        ? { text: `${stuck} Order${stuck > 1 ? 's' : ''} Unschedulable`, tone: 'bad' }
        : { text: `${late.length} Order${late.length > 1 ? 's' : ''} Late`, tone: 'bad' };
    }
    else if (atRisk.length) state = { text: `${atRisk.length} Order${atRisk.length > 1 ? 's' : ''} At Risk`, tone: 'warn' };
    else if (status === 'ready') state = { text: 'On Track', tone: 'ok' };
    else state = { text: 'No scheduled work', tone: 'none' };

    return {
      code: wc.code, name: wc.name, picture: wc.picture, safetyAreaCode: wc.safetyAreaCode, machines: ms, downNow, downHours,
      workingHours, executableHours, bookedHours, load, constraints: wcConstraints, readiness, blocked,
      orders: orderNumbers, late, atRisk, primary, status, state, operations: wcOps,
    };
  });

  // KPIs over the whole view.
  const visibleWcs = new Set(centers.map(c => c.code));
  const viewOps = ops.filter(o => visibleWcs.has(o.workCenterCode));
  const viewOrders = new Set(viewOps.map(o => o.orderNumber));
  const troubled = schedOrders.filter(o => viewOrders.has(o.orderNumber) && o.status !== 'OnTime');
  const interruptionHours = kinds => viewOps.reduce((s, o) => s + (o.interruptions ?? [])
    .filter(i => kinds.includes(i.limiting))
    .reduce((x, i) => x + overlapMs(t(i.from), t(i.until), from, to), 0), 0) / H;

  // Looking back: downtime inside the window. Looking ahead: machines down now (hours so far) plus planned downtime in the window.
  function downtimeKpi() {
    const wcMachines = new Set(centers.flatMap(c => c.machines.map(m => m.code)));
    const mine = downtime.filter(w => wcMachines.has(w.machineCode));
    if (to <= now) return { value: mine.reduce((s, w) => s + downtimeHours(w, from, to, now), 0), why: `Recorded, ${win.describe}` };
    const open = mine.filter(w => isDownAt(w, now));
    const soFar = open.reduce((s, w) => s + (now - t(w.startsAt)) / H, 0);
    const ahead = mine.filter(w => !isDownAt(w, now) && w.endsAt).reduce((s, w) => s + overlapMs(t(w.startsAt), t(w.endsAt), from, to) / H, 0);
    return { value: soFar + ahead, why: `${open.length} down now (${fmt(soFar)} h so far) + ${fmt(ahead)} h planned, ${win.describe}` };
  }
  const fmt = n => (Math.round(n * 10) / 10).toString();

  const kpis = {
    attainment: attainmentKpi(d.attainment),
    ordersAtRisk: result
      ? { value: troubled.length, why: `${troubled.filter(o => o.status === 'Late').length} late, ${troubled.filter(o => o.status === 'Unschedulable').length} unschedulable, of ${viewOrders.size} scheduled` }
      : { value: null, why: 'No schedule has been proposed yet.' },
    downtime: downtimeKpi(),
    laborConstraint: result ? { value: interruptionHours(['Workforce', 'OperatorsBusy']), why: 'Scheduled work paused for qualified people' } : { value: null, why: 'No schedule yet.' },
    materialConstraint: { value: null, why: 'Material is not evaluated until Materials/MRP (Phase 6).' },
    safetyConstraint: result ? { value: interruptionHours(['Safety']), why: 'Scheduled work paused for safety staffing' } : { value: null, why: 'No schedule yet.' },
  };

  // Top issues: machines down now, then open constraints in the window, most serious first.
  const issues = [];
  for (const c of centers) for (const m of c.downNow) {
    const w = downtime.find(x => x.machineCode === m.code && isDownAt(x, now));
    issues.push({ tone: 'bad', icon: 'bang', title: `${m.name || m.code} is down`, detail: `${w.kind === 'Planned' ? 'Planned maintenance' : 'Since'} ${w.startsAt}`, at: w.startsAt, reason: w.reason, href: '#/maintenance', kind: 'down' });
  }
  for (const c of [...liveConstraints].sort((a, b) => categoryInfo(a.category).rank - categoryInfo(b.category).rank || t(a.windowFrom) - t(b.windowFrom))) {
    if (c.category === 'Maintenance' && issues.some(i => i.kind === 'down' && i.title.startsWith(c.subject))) continue;
    issues.push({ tone: categoryInfo(c.category).tone, icon: categoryInfo(c.category).tone === 'bad' ? 'bang' : 'alert', title: `${categoryInfo(c.category).label} — ${c.subject}`, at: c.windowFrom, affects: (c.affectedOrders ?? []).length, constraint: c, href: '#/scheduling', kind: 'constraint' });
  }

  return { from, to, window: win, centers, kpis, issues, scheduleUsed: d.schedule ?? null, hasCapacity: !!capacityMachines };
}

function attainmentKpi(result) {
  if (!result) return { value: null, why: 'Schedule attainment is not visible with your roles.' };
  if (result.error) return { value: null, why: result.error.message };
  const a = result.data.output;
  if (a.percent == null) return { value: null, why: a.explanation };
  return { value: Number(a.percent), why: `Last 24 h: ${a.completedOperations} of ${a.plannedOperations} operations due were completed (schedule #${a.scheduleVersionId})` };
}

/** Planned completions per local day (quantity), from the schedule's projected completions. */
export function plannedOutput(schedule, orders, days, timeZone) {
  const qty = new Map((orders ?? []).map(o => [o.orderNumber, Number(o.quantity)]));
  const key = date => new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(date);
  const totals = new Map(days.map(dd => [dd, 0]));
  for (const o of schedule?.result?.orders ?? []) {
    if (!o.projectedCompletion) continue;
    const k = key(new Date(o.projectedCompletion));
    if (totals.has(k)) totals.set(k, totals.get(k) + (qty.get(o.orderNumber) ?? 0));
  }
  return days.map(dd => ({ day: dd, plan: totals.get(dd) }));
}

/** Units finished per local day: good quantity reported on each order's final operation. */
export function actualOutput(reports, days, timeZone) {
  const key = date => new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(date);
  const totals = new Map(days.map(dd => [dd, 0]));
  for (const r of reports ?? []) {
    if (!r.finalOperation) continue;
    const k = key(new Date(r.reportedAt));
    if (totals.has(k)) totals.set(k, totals.get(k) + Number(r.goodQuantity));
  }
  return days.map(dd => totals.get(dd));
}

export function localDays(startMs, count, timeZone) {
  const fmt = new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' });
  const out = [];
  for (let i = 0; i < count; i++) out.push(fmt.format(new Date(startMs + i * 24 * H)));
  return [...new Set(out)];
}

/** Scheduled operations a person is crewed on, from the schedule's segments. */
export function myAssignments(schedule, personId, from, to) {
  const ops = schedule?.result?.operations ?? [];
  return ops.filter(o => (o.segments ?? []).some(s => (s.operators ?? []).includes(personId) && t(s.end) > from && t(s.start) < to))
    .sort((a, b) => t(a.start) - t(b.start));
}
