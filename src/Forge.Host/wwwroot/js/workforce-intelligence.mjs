const DAY_MS = 24 * 60 * 60 * 1000;

const isoTime = value => {
  if (value == null || value === '') return null;
  const parsed = value instanceof Date ? value : new Date(value);
  return Number.isFinite(parsed.getTime()) ? parsed.getTime() : null;
};

const workerCenter = worker => worker.assignment?.workCenterCode || worker.homeWorkCenterCode;

/** Apply only source-backed roster and operations filters; unavailable dimensions stay unfiltered. */
export function filterWorkforceIntelligenceInputs(input = {}, filters = {}) {
  const resources = input.resources ?? {};
  const centers = resources.workCenters ?? [];
  const areas = resources.safetyAreas ?? [];
  const areaByCode = new Map(areas.map(area => [area.code, area]));
  const centerByCode = new Map(centers.map(center => [center.code, center]));
  const belongsToSite = center => !filters.siteCode || areaByCode.get(center.safetyAreaCode)?.siteCode === filters.siteCode;
  const belongsToArea = center => !filters.areaCode || center.safetyAreaCode === filters.areaCode;
  const machineByCode = new Map((resources.machines ?? []).map(machine => [machine.code, machine]));
  const selectedMachineCenter = filters.machineCode ? machineByCode.get(filters.machineCode)?.workCenterCode : null;
  const allowedCenters = new Set(centers.filter(center => belongsToSite(center) && belongsToArea(center)
    && (!filters.workCenterCode || center.code === filters.workCenterCode)
    && (!selectedMachineCenter || center.code === selectedMachineCenter)).map(center => center.code));
  const inScopedCenter = centerCode => centerCode
    ? allowedCenters.has(centerCode)
    : !filters.siteCode && !filters.areaCode && !filters.workCenterCode;
  const inMachineScope = (machineCode, workCenterCode) => !filters.machineCode
    || machineCode === filters.machineCode || (!machineCode && selectedMachineCenter && workCenterCode === selectedMachineCenter);
  const allowedMachines = new Set((resources.machines ?? []).filter(machine => allowedCenters.has(machine.workCenterCode)
    && (!filters.machineCode || machine.code === filters.machineCode)).map(machine => machine.code));
  const sourceWorkers = input.workers;
  const sourceQualifications = input.qualifications;
  const roleMatches = worker => !filters.roleTitle || (worker.jobTitle ?? '').trim() === filters.roleTitle;
  const workerMatches = worker => {
    const center = workerCenter(worker);
    const area = centerByCode.get(center)?.safetyAreaCode;
    return (!filters.shiftCode || worker.shiftCode === filters.shiftCode)
      && (!filters.workCenterCode || center === filters.workCenterCode)
      && (!selectedMachineCenter || selectedMachineCenter === center)
      && (!filters.siteCode || allowedCenters.has(center))
      && (!filters.areaCode || area === filters.areaCode)
      && (!filters.department || (filters.department === '__unassigned' ? !worker.department : worker.department === filters.department))
      && roleMatches(worker)
      && (filters.workerStatus === 'all' || !filters.workerStatus || (filters.workerStatus === 'active' ? worker.isActive : !worker.isActive));
  };
  const workers = Array.isArray(sourceWorkers) ? sourceWorkers.filter(workerMatches) : sourceWorkers;
  const personIds = new Set((workers ?? []).map(worker => worker.personId));
  const qualifications = Array.isArray(sourceQualifications) ? sourceQualifications.filter(row => personIds.has(row.personId)) : sourceQualifications;
  const qualificationRecords = Array.isArray(qualifications) ? qualifications.filter(row => (!filters.qualificationCode || row.code === filters.qualificationCode)
    && (!filters.qualificationStatus || filters.qualificationStatus === 'all' || row.status === filters.qualificationStatus)) : qualifications;
  const scopedResources = {
    ...resources,
    sites: (resources.sites ?? []).filter(site => !filters.siteCode || site.code === filters.siteCode),
    safetyAreas: areas.filter(area => (!filters.siteCode || area.siteCode === filters.siteCode) && (!filters.areaCode || area.code === filters.areaCode)),
    workCenters: centers.filter(center => allowedCenters.has(center.code)),
    machines: (resources.machines ?? []).filter(machine => allowedMachines.has(machine.code)),
    machineActivities: (resources.machineActivities ?? []).filter(profile => allowedMachines.has(profile.machineCode)),
    workCenterShifts: (resources.workCenterShifts ?? []).filter(link => allowedCenters.has(link.workCenterCode)
      && (!filters.shiftCode || link.shiftCode === filters.shiftCode)),
    shifts: (resources.shifts ?? []).filter(shift => (!filters.siteCode || shift.siteCode === filters.siteCode)
      && (!filters.shiftCode || shift.code === filters.shiftCode)),
  };
  const approvedSchedule = input.approvedSchedule && Array.isArray(input.approvedSchedule.operations)
    ? { ...input.approvedSchedule, operations: input.approvedSchedule.operations.filter(operation => {
      if (!filters.siteCode && !filters.areaCode) return true;
      const center = centerByCode.get(operation.workCenterCode);
      const areaCode = center?.safetyAreaCode;
      const siteCode = operation.siteCode ?? areaByCode.get(areaCode)?.siteCode;
      return (!filters.siteCode || siteCode === filters.siteCode) && (!filters.areaCode || areaCode === filters.areaCode);
    }) }
    : input.approvedSchedule;
  return {
    ...input,
    qualificationCode: filters.qualificationCode ?? input.qualificationCode ?? null,
    workers,
    qualifications,
    qualificationRecords,
    machineAuthorizations: Array.isArray(input.machineAuthorizations) ? input.machineAuthorizations.filter(row => personIds.has(row.personId)
      && (!filters.machineCode || row.machineCode === filters.machineCode)) : input.machineAuthorizations,
    qualificationTypes: input.qualificationTypes,
    approvedSchedule,
    resources: input.resources ? scopedResources : input.resources,
    unavailability: Array.isArray(input.unavailability) ? input.unavailability.filter(row => personIds.has(row.personId)) : input.unavailability,
    attendanceOpportunities: Array.isArray(input.attendanceOpportunities) ? input.attendanceOpportunities.filter(row => personIds.has(row.personId)) : input.attendanceOpportunities,
    attendanceEvents: Array.isArray(input.attendanceEvents) ? input.attendanceEvents.filter(row => personIds.has(row.personId)
      && (!filters.reviewState || filters.reviewState === 'all' || row.reviewState === filters.reviewState)) : input.attendanceEvents,
    readiness: Array.isArray(input.readiness) ? input.readiness.filter(row => inScopedCenter(row.workCenterCode ?? row.workCenter)
      && (!filters.machineCode || row.machineCode === filters.machineCode)) : input.readiness,
    activity: input.activity ? {
      ...input.activity,
      labor: (input.activity.labor ?? []).filter(row => personIds.has(row.personId)
        && inScopedCenter(row.workCenterCode)
        && inMachineScope(row.machineCode, row.workCenterCode)),
      reports: (input.activity.reports ?? []).filter(row => inScopedCenter(row.workCenterCode)
        && inMachineScope(row.machineCode, row.workCenterCode)),
    } : input.activity,
    materialTasks: Array.isArray(input.materialTasks) ? input.materialTasks.filter(row => {
      const operation = (input.readiness ?? []).find(item => item.orderNumber === row.orderNumber
        && (row.operationSequence == null || item.sequence === row.operationSequence));
      const center = row.workCenterCode ?? operation?.workCenter;
      return inScopedCenter(center) && inMachineScope(row.machineCode, center);
    }) : input.materialTasks,
    suspectReports: Array.isArray(input.suspectReports) ? input.suspectReports.filter(row => inScopedCenter(row.workCenterCode)
      && inMachineScope(row.machineCode, row.workCenterCode)) : input.suspectReports,
  };
}

function currentQualification(record, now) {
  if (record.status !== 'Qualified') return false;
  if (record.expiresAt == null || record.expiresAt === '') return true;
  const expiry = isoTime(record.expiresAt);
  return expiry !== null && expiry > now;
}

function qualifiedForRequirement(worker, qualificationRows, authorizationRows, profile, shiftCode, at) {
  if (!worker.isActive) return false;
  if (worker.shiftCode !== shiftCode) return false;
  const codes = qualificationRows
    .filter(row => row.personId === worker.personId && currentQualification(row, at))
    .map(row => row.code);
  const qualifiedCodes = (profile.operatorQualifications ?? []).filter(code => codes.includes(code));
  if (!qualifiedCodes.length) return false;
  if (!profile.requiresMachineAuthorization) return true;
  return (authorizationRows ?? []).some(row => row.personId === worker.personId && row.machineCode === profile.machineCode
    && row.activityCode === profile.activityCode && row.status === 'Authorized'
    && row.independentOperationAllowed === true
    && (row.expiresAt == null || row.expiresAt === '' || (isoTime(row.expiresAt) !== null && isoTime(row.expiresAt) > at))
    && (row.courseExpiresAt == null || row.courseExpiresAt === '' || (isoTime(row.courseExpiresAt) !== null && isoTime(row.courseExpiresAt) > at))
    && qualifiedCodes.includes(row.qualificationCode));
}

function availableForRequirement(worker, qualificationRows, authorizationRows, profile, shiftCode, now) {
  return !worker.unavailability && qualifiedForRequirement(worker, qualificationRows, authorizationRows, profile, shiftCode, now);
}

function attendanceDisruptionRisks(input, now) {
  const workers = input.workers ?? [];
  const qualifications = input.qualifications ?? [];
  const authorizationRows = input.machineAuthorizations ?? [];
  const opportunities = input.attendanceOpportunities ?? [];
  const unavailability = input.unavailability ?? [];
  const events = input.attendanceEvents ?? [];
  const resources = input.resources;
  if (!resources || !Array.isArray(input.workers) || !Array.isArray(input.qualifications) || !Array.isArray(input.attendanceOpportunities)) return [];
  const workerById = new Map(workers.map(worker => [worker.personId, worker]));
  const machineByCode = new Map((resources.machines ?? []).map(machine => [machine.code, machine]));
  const absentEventByOpportunity = new Map();
  for (const event of events) {
    const isCallOut = event.kind === 'CallOut' && ['PendingReview', 'Verified'].includes(event.reviewState);
    const isVerifiedNoShow = event.kind === 'NoShow' && event.reviewState === 'Verified';
    if (isCallOut || isVerifiedNoShow) absentEventByOpportunity.set(event.opportunityId, event);
  }
  const overlapping = (personId, start, end) => unavailability.some(row => row.personId === personId
    && (isoTime(row.startsAt) ?? Infinity) < end && (isoTime(row.endsAt) ?? -Infinity) > start);
  const emitted = new Set();
  const coverageOpportunityIds = new Set();
  const risks = [];
  for (const opportunity of opportunities) {
    if (opportunity.state === 'Cancelled') continue;
    const start = isoTime(opportunity.scheduledStart), end = isoTime(opportunity.scheduledEnd);
    if (start === null || end === null || end <= now - DAY_MS || start > now + 30 * DAY_MS) continue;
    const worker = workerById.get(opportunity.personId);
    if (!worker) continue;
    if (input.shiftCode && opportunity.shiftCode !== input.shiftCode) continue;
    if (input.workCenterCode && workerCenter(worker) !== input.workCenterCode) continue;
    const absenceEvent = absentEventByOpportunity.get(opportunity.opportunityId);
    if (!absenceEvent && !overlapping(worker.personId, start, end)) continue;
    const center = workerCenter(worker);
    for (const profile of resources.machineActivities ?? []) {
      const machine = machineByCode.get(profile.machineCode);
      if (!machine || machine.workCenterCode !== center) continue;
      const shiftConfigured = (resources.workCenterShifts ?? []).some(link => link.workCenterCode === center && link.shiftCode === opportunity.shiftCode);
      if (!shiftConfigured || !qualifiedForRequirement(worker, qualifications, authorizationRows, profile, opportunity.shiftCode, start)) continue;
      const scheduled = opportunities.filter(item => item.shiftCode === opportunity.shiftCode
        && (isoTime(item.scheduledStart) ?? Infinity) < end && (isoTime(item.scheduledEnd) ?? -Infinity) > start);
      const qualified = scheduled.map(item => ({ item, person: workerById.get(item.personId) }))
        .filter(candidate => candidate.person && qualifiedForRequirement(candidate.person, qualifications, authorizationRows, profile, opportunity.shiftCode, start));
      const remaining = qualified.filter(candidate => !overlapping(candidate.person.personId, start, end)
        && !absentEventByOpportunity.has(candidate.item.opportunityId));
      if (remaining.length) continue;
      const key = `${opportunity.opportunityId}/${profile.machineCode}/${profile.activityCode}`;
      if (emitted.has(key)) continue;
      emitted.add(key);
      coverageOpportunityIds.add(opportunity.opportunityId);
      const date = new Date(start).toLocaleDateString(undefined, { month: 'short', day: 'numeric', timeZone: 'UTC' });
      const sourceStatus = absenceEvent?.reviewState === 'PendingReview' ? 'A reported call-out is awaiting independent review; missing punches alone are never treated as no-shows.'
        : absenceEvent?.kind === 'NoShow' ? 'A verified no-show affects this scheduled shift.'
          : absenceEvent ? 'A verified call-out affects this scheduled shift.' : 'A scheduled absence affects this shift.';
      risks.push({ severity: absenceEvent?.reviewState === 'PendingReview' ? 'attention' : 'urgent',
        title: `${machine.code} · ${profile.activityCode} has no qualified coverage on ${date}`,
        detail: `${center} · ${opportunity.shiftCode}. ${sourceStatus} No qualified, authorized worker remains scheduled for this activity; private absence details are hidden. Review a safe coverage plan.`,
        evidenceReferences: [`MachineActivity/${machine.code}/${profile.activityCode}`, `AttendanceOpportunity/${opportunity.opportunityId}`,
          ...(absenceEvent ? [`AttendanceEvent/${absenceEvent.eventId}`] : [])] });
    }
  }
  for (const event of events) {
    const isCallOut = event.kind === 'CallOut' && ['PendingReview', 'Verified'].includes(event.reviewState);
    const isVerifiedNoShow = event.kind === 'NoShow' && event.reviewState === 'Verified';
    if (!isCallOut && !isVerifiedNoShow) continue;
    if (coverageOpportunityIds.has(event.opportunityId)) continue;
    const opportunity = opportunities.find(item => item.opportunityId === event.opportunityId);
    if (!opportunity) continue;
    const worker = workerById.get(opportunity.personId);
    if (!worker || (input.shiftCode && opportunity.shiftCode !== input.shiftCode)
        || (input.workCenterCode && workerCenter(worker) !== input.workCenterCode)) continue;
    risks.push({ severity: event.reviewState === 'PendingReview' ? 'attention' : 'urgent',
      title: `${event.kind === 'CallOut' ? 'Call-out' : 'No-show'} · ${worker?.displayName ?? 'Employee'} · ${opportunity.shiftCode}`,
      detail: `${new Date(opportunity.scheduledStart).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })}. ${event.reviewState === 'PendingReview' ? 'This reported call-out is awaiting independent review; missing punches alone are never treated as no-shows.' : 'Verified attendance event. Review current shift coverage and use an authorized recovery plan.'} Private absence details are hidden.`,
      evidenceReferences: [`AttendanceEvent/${event.eventId}`, `AttendanceOpportunity/${opportunity.opportunityId}`] });
  }
  return risks;
}

function capabilityCoverage({ workers, qualifications, machineAuthorizations, resources, shiftCode, workCenterCode, now }) {
  const machines = resources.machines ?? [];
  const profiles = (resources.machineActivities ?? []).filter(profile => profile.operatorQualifications?.length);
  if (!profiles.length) return null;
  const workCenterShifts = resources.workCenterShifts ?? [];
  const shifts = resources.shifts ?? [];
  const machineByCode = new Map(machines.map(machine => [machine.code, machine]));
  const selected = [];

  for (const profile of profiles) {
    const machine = machineByCode.get(profile.machineCode);
    if (!machine) continue;
    if (workCenterCode && machine.workCenterCode !== workCenterCode) continue;
    const shiftCodes = workCenterShifts
      .filter(link => link.workCenterCode === machine.workCenterCode)
      .map(link => link.shiftCode);
    const codes = shiftCode ? shiftCodes.filter(code => code === shiftCode) : shiftCodes;
    for (const code of new Set(codes)) {
      const shift = shifts.find(item => item.code === code);
      const qualifiedWorkers = workers.filter(worker =>
        workerCenter(worker) === machine.workCenterCode
        && availableForRequirement(worker, qualifications, machineAuthorizations, profile, code, now));
      selected.push({
        machineCode: machine.code,
        workCenterCode: machine.workCenterCode,
        activityCode: profile.activityCode,
        requiresMachineAuthorization: profile.requiresMachineAuthorization === true,
        requiredQualifications: [...profile.operatorQualifications],
        shiftCode: code,
        shiftName: shift?.name ?? code,
        qualifiedWorkerCount: qualifiedWorkers.length,
        qualifiedWorkerNames: qualifiedWorkers.map(worker => worker.displayName).sort(),
      });
    }
  }

  if (!selected.length) return null;
  return selected.sort((a, b) => a.qualifiedWorkerCount - b.qualifiedWorkerCount
    || a.workCenterCode.localeCompare(b.workCenterCode)
    || a.machineCode.localeCompare(b.machineCode)
    || a.shiftCode.localeCompare(b.shiftCode));
}

function operationLabor(activity, now) {
  const groups = new Map();
  for (const entry of activity?.labor ?? []) {
    const key = `${entry.orderNumber}/${entry.sequence}`;
    const row = groups.get(key) ?? {
      orderNumber: entry.orderNumber, sequence: entry.sequence,
      workCenterCode: entry.workCenterCode, operatorHours: 0, trainingHours: 0,
      people: new Set(),
    };
    const started = isoTime(entry.startedAt);
    const ended = isoTime(entry.endedAt) ?? now;
    if (started !== null && ended >= started) {
      const hours = (ended - started) / 3_600_000;
      if (entry.role === 'Operator') row.operatorHours += hours;
      else row.trainingHours += hours;
    }
    row.people.add(entry.personId);
    groups.set(key, row);
  }

  const reports = new Map();
  for (const report of activity?.reports ?? []) {
    const key = `${report.orderNumber}/${report.sequence}`;
    const totals = reports.get(key) ?? { goodQuantity: 0, scrapQuantity: 0 };
    totals.goodQuantity += Number(report.goodQuantity) || 0;
    totals.scrapQuantity += Number(report.scrapQuantity) || 0;
    reports.set(key, totals);
  }

  return [...groups.entries()].map(([key, row]) => ({
    key,
    orderNumber: row.orderNumber,
    sequence: row.sequence,
    workCenterCode: row.workCenterCode,
    operatorHours: Math.round(row.operatorHours * 100) / 100,
    trainingHours: Math.round(row.trainingHours * 100) / 100,
    peopleCount: row.people.size,
    ...(reports.get(key) ?? { goodQuantity: 0, scrapQuantity: 0 }),
    targetHours: null,
    efficiencyPercent: null,
    targetStatus: 'Insufficient evidence: no approved operation labor standard is available.',
  })).sort((a, b) => b.operatorHours + b.trainingHours - (a.operatorHours + a.trainingHours));
}

function evidenceBackedTrainingRecommendations(gaps, workers, qualifications, now) {
  const recommendations = [];
  for (const gap of gaps) {
    for (const worker of workers) {
      if (!worker.isActive || workerCenter(worker) !== gap.workCenterCode || worker.shiftCode !== gap.shiftCode || worker.unavailability) continue;
      for (const code of gap.requiredQualifications) {
        const inTraining = qualifications.some(row => row.personId === worker.personId && row.code === code && row.status === 'InTraining');
        const qualified = qualifications.some(row => row.personId === worker.personId && row.code === code && currentQualification(row, now));
        if (!inTraining && !(gap.requiresMachineAuthorization && qualified)) continue;
        recommendations.push({ personId: worker.personId, workerName: worker.displayName, qualificationCode: code,
          machineCode: gap.machineCode, activityCode: gap.activityCode, workCenterCode: gap.workCenterCode, shiftCode: gap.shiftCode,
          evidenceReferences: [`PersonQualification/${worker.personId}/${code}`, `MachineActivity/${gap.machineCode}/${gap.activityCode}`],
          rationale: inTraining
            ? `Current coverage gap: ${gap.machineCode} · ${gap.activityCode}. ${worker.displayName} has an In Training record for ${code}; verify prerequisites, trainer capacity and assessment evidence before scheduling a practical assessment.`
            : `Current coverage gap: ${gap.machineCode} · ${gap.activityCode}. ${worker.displayName} holds ${code}, but no current authorization is recorded for this exact machine and activity; review course, practical assessment, and approval evidence.`,
          expectedPlantBenefit: `May add a future qualified option for ${gap.workCenterCode} / ${gap.shiftName}. Recommendation does not authorize independent work.` });
      }
    }
  }
  return recommendations;
}

function upcomingTrainingWindows(input, now) {
  const resources = input.resources;
  if (!resources || !Array.isArray(input.workers) || !Array.isArray(input.qualifications)
      || !Array.isArray(input.qualificationTypes) || !Array.isArray(input.attendanceOpportunities)
      || !Array.isArray(input.unavailability) || !Array.isArray(input.approvedSchedule?.operations)) return null;
  if ((resources.machineActivities ?? []).some(profile => profile.requiresMachineAuthorization === true)
      && !Array.isArray(input.machineAuthorizations)) return null;
  const until = now + 14 * DAY_MS;
  const workers = input.workers;
  const qualifications = input.qualifications;
  const authorizations = input.machineAuthorizations ?? [];
  const unavailable = input.unavailability;
  const currentAt = (personId, code, at) => qualifications.some(row => row.personId === personId && row.code === code
    && row.status === 'Qualified' && (row.expiresAt == null || row.expiresAt === '' || (isoTime(row.expiresAt) !== null && isoTime(row.expiresAt) > at)));
  const unavailableAt = (personId, start, end) => unavailable.some(row => row.personId === personId
    && (isoTime(row.startsAt) ?? Infinity) < end && (isoTime(row.endsAt) ?? -Infinity) > start);
  const trainerTypesFor = code => input.qualificationTypes.filter(type => type.kind === 'Trainer'
    && ((type.prerequisites ?? []).includes(code)
      || (type.prerequisiteAlternatives ?? []).some(group => (group.qualificationCodes ?? []).includes(code))));
  const relevantOpportunities = input.attendanceOpportunities.filter(row => row.state === 'Scheduled'
    && isoTime(row.scheduledStart) !== null && isoTime(row.scheduledStart) >= now && isoTime(row.scheduledStart) < until);
  const groups = new Map();
  for (const opportunity of relevantOpportunities) {
    // Keep the date supplied by the site calendar; converting to UTC can move an overnight
    // or west-of-UTC shift into the neighboring production day.
    const rawStart = String(opportunity.scheduledStart);
    const date = /^\d{4}-\d{2}-\d{2}/.test(rawStart) ? rawStart.slice(0, 10) : new Date(isoTime(opportunity.scheduledStart)).toISOString().slice(0, 10);
    const key = `${opportunity.shiftCode}/${date}`;
    if (!groups.has(key)) groups.set(key, { date, shiftCode: opportunity.shiftCode, opportunities: [] });
    const group = groups.get(key);
    group.opportunities.push(opportunity);
  }
  const workerById = new Map(workers.map(worker => [worker.personId, worker]));
  const machines = new Map((resources.machines ?? []).map(machine => [machine.code, machine]));
  const links = resources.workCenterShifts ?? [];
  const profiles = (resources.machineActivities ?? []).filter(profile => profile.operatorQualifications?.length);
  const scheduleOperations = input.approvedSchedule.operations.filter(operation => operation.scheduled === true);
  const segmentOverlaps = (segment, start, end) => {
    const segmentStart = isoTime(segment.start), segmentEnd = isoTime(segment.end);
    return segmentStart !== null && segmentEnd !== null && segmentStart < end && segmentEnd > start;
  };
  const workerBusyIntervals = new Map();
  for (const operation of scheduleOperations) {
    for (const segment of operation.segments ?? []) {
      const start = isoTime(segment.start), end = isoTime(segment.end);
      if (start === null || end === null || end <= start) continue;
      for (const personId of segment.operators ?? []) {
        const intervals = workerBusyIntervals.get(personId) ?? [];
        intervals.push({ start, end });
        workerBusyIntervals.set(personId, intervals);
      }
    }
  }
  const largestSharedFreeBlock = (learnerOpportunity, trainerOpportunity, learnerId, trainerId, machineCode) => {
    const start = Math.max(isoTime(learnerOpportunity.scheduledStart) ?? Infinity, isoTime(trainerOpportunity.scheduledStart) ?? Infinity);
    const end = Math.min(isoTime(learnerOpportunity.scheduledEnd) ?? -Infinity, isoTime(trainerOpportunity.scheduledEnd) ?? -Infinity);
    if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) return null;
    const machineBusy = scheduleOperations.filter(operation => operation.machineCode === machineCode)
      .flatMap(operation => operation.segments ?? [])
      .map(segment => ({ start: isoTime(segment.start), end: isoTime(segment.end) }))
      .filter(interval => interval.start !== null && interval.end !== null && interval.start < end && interval.end > start);
    const busy = [...(workerBusyIntervals.get(learnerId) ?? []), ...(workerBusyIntervals.get(trainerId) ?? []), ...machineBusy]
      .filter(row => row.start < end && row.end > start)
      .map(row => ({ start: Math.max(start, row.start), end: Math.min(end, row.end) }))
      .sort((a, b) => a.start - b.start || a.end - b.end);
    let cursor = start, best = null;
    for (const interval of busy) {
      if (interval.start > cursor && (!best || interval.start - cursor > best.end - best.start)) best = { start: cursor, end: interval.start };
      cursor = Math.max(cursor, interval.end);
    }
    if (cursor < end && (!best || end - cursor > best.end - best.start)) best = { start: cursor, end };
    return best;
  };
  const rows = [];
  for (const profile of profiles) {
    const machine = machines.get(profile.machineCode);
    if (!machine) continue;
    const shiftCodes = links.filter(link => link.workCenterCode === machine.workCenterCode).map(link => link.shiftCode);
    for (const window of groups.values()) {
      if (!shiftCodes.includes(window.shiftCode)) continue;
      const scheduledWorkers = [...new Map(window.opportunities.map(opportunity => [opportunity.personId, { worker: workerById.get(opportunity.personId), opportunity }])).values()]
        .filter(({ worker, opportunity }) => worker?.isActive && worker.shiftCode === window.shiftCode
          && workerCenter(worker) === machine.workCenterCode
          && isoTime(opportunity.scheduledEnd) > isoTime(opportunity.scheduledStart)
          && !unavailableAt(worker.personId, isoTime(opportunity.scheduledStart), isoTime(opportunity.scheduledEnd)));
      if (!scheduledWorkers.length) continue;
      for (const code of profile.operatorQualifications) {
        if (input.qualificationCode && input.qualificationCode !== code) continue;
        const trainees = scheduledWorkers.filter(({ worker, opportunity }) => qualifications.some(row => row.personId === worker.personId && row.code === code && row.status === 'InTraining')
          && !currentAt(worker.personId, code, isoTime(opportunity.scheduledStart)));
        if (!trainees.length) continue;
        const trainerTypes = trainerTypesFor(code);
        const trainers = scheduledWorkers.filter(({ worker, opportunity }) => currentAt(worker.personId, code, isoTime(opportunity.scheduledStart))
          && trainerTypes.some(type => currentAt(worker.personId, type.code, isoTime(opportunity.scheduledStart)))
          && (!profile.requiresMachineAuthorization || authorizations.some(row => row.personId === worker.personId
            && row.machineCode === profile.machineCode && row.activityCode === profile.activityCode
            && row.qualificationCode === code && row.status === 'Authorized' && row.independentOperationAllowed === true
            && (row.expiresAt == null || isoTime(row.expiresAt) > isoTime(opportunity.scheduledStart))
            && (row.courseExpiresAt == null || isoTime(row.courseExpiresAt) > isoTime(opportunity.scheduledStart)))));
        const qualifiedCount = scheduledWorkers.filter(({ worker, opportunity }) => qualifiedForRequirement(worker, qualifications, authorizations, profile,
          window.shiftCode, isoTime(opportunity.scheduledStart))).length;
        let bestPair = null;
        for (const learner of trainees) for (const trainer of trainers) {
          if (learner.worker.personId === trainer.worker.personId) continue;
          const available = largestSharedFreeBlock(learner.opportunity, trainer.opportunity, learner.worker.personId, trainer.worker.personId, profile.machineCode);
          if (available && (!bestPair || available.end - available.start > bestPair.available.end - bestPair.available.start
              || (available.end - available.start === bestPair.available.end - bestPair.available.start && available.start < bestPair.available.start))) {
            bestPair = { learner, trainer, available };
          }
        }
        const referenceOpportunity = trainees[0].opportunity;
        const shiftStart = isoTime(referenceOpportunity.scheduledStart), shiftEnd = isoTime(referenceOpportunity.scheduledEnd);
        const plannedCenterOperations = scheduleOperations.filter(operation => operation.workCenterCode === machine.workCenterCode
          && (operation.segments ?? []).some(segment => segmentOverlaps(segment, shiftStart, shiftEnd)));
        const plannedMachineOperations = scheduleOperations.filter(operation => operation.machineCode === profile.machineCode
          && (operation.segments ?? []).some(segment => segmentOverlaps(segment, shiftStart, shiftEnd)));
        const availableMinutes = bestPair ? Math.floor((bestPair.available.end - bestPair.available.start) / 60_000) : 0;
        const hasReviewableWindow = availableMinutes > 0;
        rows.push({ date: window.date, shiftCode: window.shiftCode, machineCode: machine.code, activityCode: profile.activityCode,
          workCenterCode: machine.workCenterCode, qualificationCode: code, traineeCount: trainees.length, trainerCount: trainers.length, qualifiedCount,
          trainerCapacity: trainers.length > 0, plannedOperationCount: plannedCenterOperations.length,
          plannedMachineOperationCount: plannedMachineOperations.length, largestSharedFreeMinutes: availableMinutes,
          availableFrom: bestPair ? new Date(bestPair.available.start).toISOString() : null,
          availableThrough: bestPair ? new Date(bestPair.available.end).toISOString() : null,
          canReviewWindow: hasReviewableWindow,
          detail: !trainers.length
            ? `${trainees.length} scheduled learner(s) in training; no qualified trainer is scheduled for this shift. Do not book a training session from this forecast.`
            : !hasReviewableWindow
              ? `${trainees.length} scheduled learner(s) and ${trainers.length} qualified trainer(s); ${qualifiedCount} worker(s) still meet the independent qualification gate. The approved schedule leaves no shared window clear of assigned work and ${plannedMachineOperations.length} planned operation(s) on this machine during the shift.`
              : `${trainees.length} scheduled learner(s) and ${trainers.length} qualified trainer(s); ${qualifiedCount} worker(s) still meet the independent qualification gate. Largest shared block clear of the pair's assigned work and this machine's scheduled work: ${availableMinutes} minutes (${plannedCenterOperations.length} operation(s) planned in the work center). Review course duration, production demand, crew coverage, and learner interest before booking.` });
      }
    }
  }
  return rows.sort((a, b) => a.date.localeCompare(b.date) || b.largestSharedFreeMinutes - a.largestSharedFreeMinutes || a.trainerCount - b.trainerCount
    || a.workCenterCode.localeCompare(b.workCenterCode) || a.machineCode.localeCompare(b.machineCode) || a.qualificationCode.localeCompare(b.qualificationCode));
}

/** Build a transparent, source-backed summary. Missing source records stay unknown, never zero. */
export function buildWorkforceIntelligence(input = {}) {
  const now = isoTime(input.now ?? new Date()) ?? Date.now();
  const expiryDays = [30, 60, 90].includes(Number(input.expiryDays)) ? Number(input.expiryDays) : 60;
  const workers = input.workers ?? [];
  const qualifications = input.qualifications ?? [];
  const types = input.qualificationTypes ?? [];
  const workerById = new Map(workers.map(worker => [worker.personId, worker]));
  const uniq = new Set();
  const uniqueQualifications = qualifications.filter(row => {
    const key = `${row.personId}/${row.code}/${row.status}/${row.expiresAt ?? ''}`;
    if (uniq.has(key)) return false;
    uniq.add(key);
    return true;
  });
  const reviewQualifications = input.qualificationCode
    ? uniqueQualifications.filter(row => row.code === input.qualificationCode)
    : uniqueQualifications;
  const qualified = reviewQualifications.filter(row => row.status === 'Qualified');
  const current = qualified.filter(row => currentQualification(row, now));
  const expired = qualified.filter(row => {
    const expiry = isoTime(row.expiresAt);
    return expiry !== null && expiry <= now;
  });
  const expiring = qualified.filter(row => {
    const expiry = isoTime(row.expiresAt);
    return expiry !== null && expiry > now && expiry <= now + expiryDays * DAY_MS;
  }).map(row => ({
    ...row,
    workerName: workerById.get(row.personId)?.displayName ?? 'Worker record unavailable',
    daysRemaining: Math.max(0, Math.ceil((isoTime(row.expiresAt) - now) / DAY_MS)),
  })).sort((a, b) => a.daysRemaining - b.daysRemaining || a.workerName.localeCompare(b.workerName));

  const hasAuthorizationGates = (input.resources?.machineActivities ?? []).some(profile => profile.requiresMachineAuthorization);
  const coverage = input.resources && input.workers && input.qualifications && (!hasAuthorizationGates || Array.isArray(input.machineAuthorizations))
    ? capabilityCoverage({
      workers, qualifications: uniqueQualifications, machineAuthorizations: input.machineAuthorizations, resources: input.resources,
      shiftCode: input.shiftCode || null, workCenterCode: input.workCenterCode || null, now,
    })
    : null;
  const capabilityGaps = coverage?.filter(row => row.qualifiedWorkerCount === 0) ?? [];
  const trainingRecommendations = evidenceBackedTrainingRecommendations(capabilityGaps, workers, uniqueQualifications, now);
  const blockers = (input.readiness ?? [])
    .filter(row => row.executable === false)
    .filter(row => ['Workforce', 'Safety'].includes(row.primaryConstraint))
    .filter(row => !input.workCenterCode || row.workCenter === input.workCenterCode)
    .map(row => ({
      orderNumber: row.orderNumber,
      sequence: row.sequence,
      workCenter: row.workCenter,
      evidenceReferences: [`OrderOperation/${row.orderNumber}/${row.sequence}`],
      primaryConstraint: row.primaryConstraint ?? 'Readiness',
      why: row.why,
    }))
    .sort((a, b) => a.orderNumber - b.orderNumber || a.sequence - b.sequence);
  const upcomingAbsence = (input.unavailability ?? []).filter(row => {
    const end = isoTime(row.endsAt);
    return end !== null && end > now;
  });
  const attendanceRisks = attendanceDisruptionRisks(input, now);
  const trainingForecast = upcomingTrainingWindows(input, now);
  const operationByOrder = new Map();
  for (const operation of input.readiness ?? []) {
    const operations = operationByOrder.get(operation.orderNumber) ?? [];
    operations.push(operation);
    operationByOrder.set(operation.orderNumber, operations);
  }
  for (const operations of operationByOrder.values()) operations.sort((a, b) => a.sequence - b.sequence);
  const materialRecoveryAction = task => {
    if (task.rejectedQuantity > 0 || ['DeliveryRejected', 'Rejected'].includes(task.status))
      return 'Inventory Controller: review the rejected quantity and route it through the audited correction or replacement workflow.';
    if (task.status === 'ApprovalNeeded') return 'Planner / Inventory: review the request and record the required approval.';
    if (task.status === 'Requested') return 'Stockroom: allocate released stock, then pick and scan the material.';
    if (task.status === 'Allocated') return 'Stockroom: pick and scan the allocated lot and bin.';
    if (task.status === 'Picked') return 'Stockroom deliverer: scan the destination and dispatch the custody handoff.';
    if (task.status === 'InTransit') return 'Floor receiver: independently acknowledge the delivered quantity; resolve any discrepancy through Inventory.';
    return 'Review the canonical Inventory task for its next authorized action.';
  };
  const materialDisruptions = (input.materialTasks ?? [])
    .filter(task => task.orderNumber != null && ['ApprovalNeeded', 'Requested', 'Allocated', 'Picked', 'InTransit', 'DeliveryRejected', 'Rejected'].includes(task.status))
    .map(task => {
      const operations = operationByOrder.get(task.orderNumber) ?? [];
      const operation = task.operationSequence == null ? operations[0] : operations.find(row => row.sequence === task.operationSequence);
      const workCenter = task.workCenterCode ?? operation?.workCenter;
      const dueDate = operation?.dueDate ?? null;
      const materialBlocked = operation?.executable === false && ['Material', 'PointOfUse'].includes(operation.primaryConstraint);
      const otherBlocker = operation?.executable === false && !materialBlocked ? operation.primaryConstraint : null;
      const statusPriority = task.rejectedQuantity > 0 || ['DeliveryRejected', 'Rejected'].includes(task.status) ? 0
        : task.status === 'InTransit' ? 1 : task.status === 'ApprovalNeeded' ? 2
          : task.status === 'Requested' ? 3 : task.status === 'Allocated' ? 4 : 5;
      return {
        severity: statusPriority <= 1 || materialBlocked ? 'urgent' : 'attention',
        recoveryPriority: statusPriority,
        taskId: task.taskId,
        dueDate,
        canPreviewReceiptImpact: task.status === 'InTransit' && task.kind === 'Stage' && task.isNonBom !== true,
        title: `WO ${task.orderNumber} · ${task.partNumber} material ${task.status === 'ApprovalNeeded' ? 'needs approval' : task.status === 'InTransit' ? task.rejectedQuantity > 0 ? 'has rejected quantity awaiting resolution' : 'awaits receipt' : ['DeliveryRejected', 'Rejected'].includes(task.status) ? 'was rejected' : 'awaits stockroom'}`,
        detail: `${task.quantity} requested for ${task.toLocation}${task.receivedQuantity ? ` · ${task.receivedQuantity} accepted` : ''}${task.rejectedQuantity ? ` · ${task.rejectedQuantity} quarantined` : ''}${task.operationSequence == null ? '' : ` · operation ${task.operationSequence}`}${dueDate ? ` · order due ${dueDate}` : ''}${materialBlocked ? ' · linked operation is currently blocked by material readiness' : otherBlocker ? ` · operation also has a ${otherBlocker} blocker` : ''}. Next: ${materialRecoveryAction(task)} This is a material workflow recommendation; it does not authorize a worker move or bypass any gate.`,
        orderNumber: task.orderNumber,
        sequence: task.operationSequence,
        workCenter,
        evidenceReferences: [`MaterialTask/${task.taskId}`, ...(task.operationSequence == null ? [] : [`OrderOperation/${task.orderNumber}/${task.operationSequence}`])],
      };
    })
    .filter(task => !input.workCenterCode || !task.workCenter || task.workCenter === input.workCenterCode)
    .sort((a, b) => a.recoveryPriority - b.recoveryPriority || (a.dueDate ?? '9999-12-31').localeCompare(b.dueDate ?? '9999-12-31') || a.orderNumber - b.orderNumber);
  const suspectReports = (input.suspectReports ?? [])
    .filter(report => !report.ncrId)
    .filter(report => !input.workCenterCode || !report.workCenterCode || report.workCenterCode === input.workCenterCode)
    .map(report => ({
      severity: 'attention',
      title: `Suspect report ${report.reportId} needs Quality review`,
      detail: `WO ${report.orderNumber} · operation ${report.sequence} · ${report.partNumber} · ${report.kind} / ${report.reasonCode}. This is reported evidence, not an approved disposition.`,
      orderNumber: report.orderNumber,
      sequence: report.sequence,
      evidenceReferences: [`SuspectScrapReport/${report.reportId}`, `OrderOperation/${report.orderNumber}/${report.sequence}`],
    }));
  const attendanceSummary = input.attendanceSummary;
  const attendance = attendanceSummary?.onTimeArrivalPercent != null
    ? {
      status: `On-time arrival: ${attendanceSummary.onTimeArrivalPercent}%`,
      detail: attendanceSummary.explanation,
      onTimePercent: attendanceSummary.onTimeArrivalPercent,
      eligibleOpportunities: attendanceSummary.eligibleArrivalOpportunities,
      scheduledOpportunities: attendanceSummary.scheduledOpportunities,
      onTimeArrivals: attendanceSummary.onTimeArrivals,
      verifiedLateArrivals: attendanceSummary.verifiedLateArrivals,
      verifiedCallOuts: attendanceSummary.verifiedCallOuts,
      verifiedNoShows: attendanceSummary.verifiedNoShows,
      excludedOpportunities: attendanceSummary.excludedOpportunities,
      pendingReviewOpportunities: attendanceSummary.pendingReviewOpportunities,
      missingPolicyOpportunities: attendanceSummary.missingPolicyOpportunities,
    }
    : {
      status: attendanceSummary ? 'Insufficient evidence' : 'Unavailable to this account',
      detail: attendanceSummary?.explanation ?? 'A versioned attendance schedule and policy are required. Missing punches are never counted as no-shows.',
      onTimePercent: null,
      eligibleOpportunities: null,
      scheduledOpportunities: attendanceSummary?.scheduledOpportunities ?? null,
      onTimeArrivals: attendanceSummary?.onTimeArrivals ?? null,
      verifiedLateArrivals: attendanceSummary?.verifiedLateArrivals ?? null,
      verifiedCallOuts: attendanceSummary?.verifiedCallOuts ?? null,
      verifiedNoShows: attendanceSummary?.verifiedNoShows ?? null,
      excludedOpportunities: attendanceSummary?.excludedOpportunities ?? null,
      pendingReviewOpportunities: attendanceSummary?.pendingReviewOpportunities ?? null,
      missingPolicyOpportunities: attendanceSummary?.missingPolicyOpportunities ?? null,
    };
  attendance.period = attendanceSummary?.periodLabel ?? null;

  return {
    expiryDays,
    qualificationCounts: {
      current: current.length,
      expiring: expiring.length,
      expired: expired.length,
      inTraining: reviewQualifications.filter(row => row.status === 'InTraining').length,
      types: types.length,
    },
    expiring,
    coverage,
    capabilityGaps,
    trainingRecommendations,
    trainingForecast,
    blockers,
    attendanceRisks,
    materialDisruptions: [...materialDisruptions, ...suspectReports].sort((a, b) => (a.severity === 'urgent' ? 0 : 1) - (b.severity === 'urgent' ? 0 : 1)
      || (a.recoveryPriority ?? 99) - (b.recoveryPriority ?? 99) || (a.dueDate ?? '9999-12-31').localeCompare(b.dueDate ?? '9999-12-31') || a.title.localeCompare(b.title)),
    upcomingAbsence,
    labor: operationLabor(input.activity, now),
    attendance,
    sources: {
      workers: Array.isArray(input.workers),
      qualifications: Array.isArray(input.qualifications),
      resources: Boolean(input.resources),
      unavailability: Array.isArray(input.unavailability),
      readiness: Array.isArray(input.readiness),
      activity: Boolean(input.activity),
      machineAuthorizations: Array.isArray(input.machineAuthorizations),
      trainingForecast: trainingForecast !== null,
      materialTasks: Array.isArray(input.materialTasks),
      suspectReports: Array.isArray(input.suspectReports),
    },
  };
}

/** Return a link only for recognized Forge-owned source references; unknown refs remain inert text. */
export function renderEvidenceReference(reference) {
  const text = String(reference ?? '').trim();
  const safeText = text.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');
  const person = /^PersonQualification\/([0-9a-f-]{36})\/([A-Z0-9._-]{1,48})$/i.exec(text);
  if (person) return `<a href="#/workforce?personId=${encodeURIComponent(person[1])}">${safeText}</a>`;
  const machine = /^MachineActivity\/([A-Z0-9._-]{1,48})\/([A-Z0-9._-]{1,48})$/i.exec(text);
  if (machine) return `<a href="#/plantsetup">${safeText}</a>`;
  const orderOperation = /^OrderOperation\/([A-Z0-9._-]{1,48})\/(\d{1,6})$/i.exec(text);
  if (orderOperation) return `<a href="#/orders?search=${encodeURIComponent(orderOperation[1])}">${safeText}</a>`;
  const ncr = /^(?:Ncr|NCR)\/([A-Z0-9._-]{1,48})$/.exec(text);
  if (ncr) return `<a href="#/quality">${safeText}</a>`;
  const materialTask = /^MaterialTask\/(\d{1,18})$/.exec(text);
  if (materialTask) return `<a href="#/inventory?tab=tasks">${safeText}</a>`;
  const suspectReport = /^SuspectScrapReport\/(\d{1,18})$/.exec(text);
  if (suspectReport) return `<a href="#/quality?tab=suspect">${safeText}</a>`;
  if (/^Attendance(?:Event|Opportunity)\/[0-9a-f-]{36}$/i.test(text)) return `<a href="#/workforce">${safeText}</a>`;
  return `<code>${safeText}</code>`;
}
