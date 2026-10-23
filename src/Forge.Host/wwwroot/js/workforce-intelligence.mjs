const DAY_MS = 24 * 60 * 60 * 1000;

const isoTime = value => {
  if (value == null || value === '') return null;
  const parsed = value instanceof Date ? value : new Date(value);
  return Number.isFinite(parsed.getTime()) ? parsed.getTime() : null;
};

const workerCenter = worker => worker.assignment?.workCenterCode || worker.homeWorkCenterCode;

function currentQualification(record, now) {
  if (record.status !== 'Qualified') return false;
  if (record.expiresAt == null || record.expiresAt === '') return true;
  const expiry = isoTime(record.expiresAt);
  return expiry !== null && expiry > now;
}

function availableForRequirement(worker, qualificationRows, authorizationRows, profile, shiftCode, now) {
  if (!worker.isActive || worker.unavailability) return false;
  if (worker.shiftCode !== shiftCode) return false;
  const codes = qualificationRows
    .filter(row => row.personId === worker.personId && currentQualification(row, now))
    .map(row => row.code);
  const qualifiedCodes = (profile.operatorQualifications ?? []).filter(code => codes.includes(code));
  if (!qualifiedCodes.length) return false;
  if (!profile.requiresMachineAuthorization) return true;
  return (authorizationRows ?? []).some(row => row.personId === worker.personId && row.machineCode === profile.machineCode
    && row.activityCode === profile.activityCode && row.status === 'Authorized'
    && row.independentOperationAllowed === true
    && (row.expiresAt == null || row.expiresAt === '' || (isoTime(row.expiresAt) !== null && isoTime(row.expiresAt) > now))
    && (row.courseExpiresAt == null || row.courseExpiresAt === '' || (isoTime(row.courseExpiresAt) !== null && isoTime(row.courseExpiresAt) > now))
    && qualifiedCodes.includes(row.qualificationCode));
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
  const qualified = uniqueQualifications.filter(row => row.status === 'Qualified');
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
  const materialDisruptions = (input.materialTasks ?? [])
    .filter(task => task.orderNumber != null && ['ApprovalNeeded', 'Requested', 'Allocated', 'Picked', 'InTransit'].includes(task.status))
    .filter(task => !input.workCenterCode || !task.workCenterCode || task.workCenterCode === input.workCenterCode)
    .map(task => ({
      severity: task.status === 'InTransit' ? 'urgent' : 'attention',
      title: `WO ${task.orderNumber} · ${task.partNumber} material ${task.status === 'ApprovalNeeded' ? 'needs approval' : task.status === 'InTransit' ? task.rejectedQuantity > 0 ? 'has rejected quantity awaiting resolution' : 'awaits receipt' : 'awaits stockroom'}`,
      detail: `${task.quantity} requested for ${task.toLocation}${task.receivedQuantity ? ` · ${task.receivedQuantity} accepted` : ''}${task.rejectedQuantity ? ` · ${task.rejectedQuantity} quarantined` : ''}${task.operationSequence == null ? '' : ` · operation ${task.operationSequence}`}. No staffing or safety gate is bypassed.`,
      orderNumber: task.orderNumber,
      sequence: task.operationSequence,
      evidenceReferences: [`MaterialTask/${task.taskId}`, ...(task.operationSequence == null ? [] : [`OrderOperation/${task.orderNumber}/${task.operationSequence}`])],
    }));
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

  return {
    expiryDays,
    qualificationCounts: {
      current: current.length,
      expiring: expiring.length,
      expired: expired.length,
      inTraining: uniqueQualifications.filter(row => row.status === 'InTraining').length,
      types: types.length,
    },
    expiring,
    coverage,
    capabilityGaps,
    trainingRecommendations,
    blockers,
    materialDisruptions: [...materialDisruptions, ...suspectReports].sort((a, b) => (a.severity === 'urgent' ? 0 : 1) - (b.severity === 'urgent' ? 0 : 1) || a.title.localeCompare(b.title)),
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
  return `<code>${safeText}</code>`;
}
