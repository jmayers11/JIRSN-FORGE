#!/usr/bin/env node
// Appends fictional historical attendance through the audited schedule/import/review APIs.
// It never fabricates a clock-in, exposes protected leave detail, or resets data.
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';
import { site, workers } from './whiskerworks-plant.mjs';

const args = process.argv.slice(2);
const value = name => args.find(argument => argument.startsWith(`--${name}=`))?.slice(name.length + 3);
const apply = args.includes('--apply');
const baseArg = value('base');
const admin = value('admin');
const reader = value('reader');
const importer = value('importer');
const reviewer = value('reviewer');
const base = baseArg?.replace(/\/+$/, '');
const reason = 'Seed fictional WhiskerWorks Sprint 10 attendance history';

export function buildSprint10AttendanceFact(worker, index, scheduledStart, scheduledEnd, focusSubject) {
  if (worker.subject === focusSubject && index < 60) {
    if (index < 4) return { kind: 'ApprovedPto', observedAt: null };
    if (index === 4) return { kind: 'ProtectedLeave', observedAt: null };
    return { kind: 'Arrival', observedAt: scheduledStart + (index < 58 ? 2 : 12) * 60_000 };
  }
  if (index % 41 === 40) return { kind: 'ApprovedPto', observedAt: null };
  if (index % 89 === 88) return { kind: 'ProtectedLeave', observedAt: null };
  if (index % 113 === 112) return { kind: 'CallOut', observedAt: scheduledStart - 30 * 60_000 };
  if (index % 197 === 196 && scheduledEnd < Date.now()) return { kind: 'NoShow', observedAt: scheduledEnd + 60_000 };
  return { kind: 'Arrival', observedAt: scheduledStart + (index % 13 === 12 ? 12 : index % 6) * 60_000 };
}

function localDayStartUtc(day, zone) {
  const local = new Date(day);
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(local);
  const read = kind => Number(parts.find(part => part.type === kind)?.value);
  const utcMidnight = Date.UTC(read('year'), read('month') - 1, read('day'));
  const offsetText = new Intl.DateTimeFormat('en-US', { timeZone: zone, timeZoneName: 'longOffset' }).formatToParts(local).find(part => part.type === 'timeZoneName')?.value ?? 'GMT+00:00';
  const match = /GMT([+-])(\d{2}):(\d{2})/.exec(offsetText);
  if (!match) throw new Error(`Could not resolve the site offset for ${zone}.`);
  const offsetMinutes = (match[1] === '-' ? -1 : 1) * (Number(match[2]) * 60 + Number(match[3]));
  return utcMidnight - offsetMinutes * 60_000;
}

async function request(method, path, who, body = undefined) {
  const headers = { 'X-Forge-Dev-Subject': who, Accept: 'application/json' };
  if (body !== undefined) {
    body = { reason, ...body };
    headers['Content-Type'] = 'application/json';
    const hash = createHash('sha256').update(`${method}\n${path}\n${JSON.stringify(body)}`).digest('hex').slice(0, 32);
    headers['Idempotency-Key'] = `ww-s10-attendance-v1-${hash}`;
  }
  const response = await fetch(`${base}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path} as ${who} returned ${response.status}: ${text}`);
  return text ? JSON.parse(text) : null;
}
const get = (path, who) => request('GET', path, who);
const post = (path, body, who) => request('POST', path, who, body);
const hasPermission = (profile, permission) => (profile.permissions ?? []).includes(permission);

async function main() {
  if (!apply) {
    console.log('WhiskerWorks Sprint 10 attendance history plan (dry run; no network or database calls)');
    console.log(`Target site: ${site.code}; workers: ${workers.length}; historical window: 84 site-calendar days.`);
    console.log('One Day Shift worker receives 60 reviewed opportunities with 4 PTO, 1 protected-leave exclusion, 53 on-time arrivals and 2 late arrivals.');
    console.log('To apply, pass --apply --base=http://localhost:<port> --admin=<policy-admin> --reader=<attendance-reader> --importer=<AttendanceImporter> --reviewer=<independent-supervisor>.');
    console.log('All event facts are fictional, source-keyed, audited and independently reviewed; no real clock or customer database is changed.');
    return;
  }
  if (!baseArg || !admin || !reader || !importer || !reviewer) throw new Error('Applying requires explicit --base, --admin, --reader, --importer and --reviewer arguments.');
  if (importer === reviewer) throw new Error('The importer and reviewer must be distinct identities.');
  const targetUrl = new URL(base);
  if (!['localhost', '127.0.0.1', '::1'].includes(targetUrl.hostname) && !args.includes('--allow-remote-demo')) {
    throw new Error('Refusing a non-local seed target. Pass --allow-remote-demo only for a deliberately isolated demo host.');
  }
  await get('/health/ready', reader).catch(error => { throw new Error(`Forge is not ready at ${base}: ${error.message}`); });
  const [adminProfile, readerProfile, importerProfile, reviewerProfile, resources, roster, policies] = await Promise.all([
    get('/api/kernel/me', admin), get('/api/kernel/me', reader), get('/api/kernel/me', importer), get('/api/kernel/me', reviewer),
    get('/api/resources/?includeRetired=true', reader), get('/api/workforce/workers', reader), get('/api/workforce/attendance-policies', admin),
  ]);
  for (const [profile, permission, name] of [
    [adminProfile, 'workforce.attendance-policy.manage', 'admin'],
    [readerProfile, 'workforce.attendance-schedule.manage', 'reader'],
    [readerProfile, 'workforce.attendance-intelligence.read', 'reader'],
    [importerProfile, 'workforce.attendance.import', 'importer'],
    [reviewerProfile, 'workforce.attendance-classification.review', 'reviewer'],
  ]) if (!hasPermission(profile, permission)) throw new Error(`${name} identity is missing ${permission}.`);
  if ((resources.sites ?? []).length !== 1 || resources.sites[0].code !== site.code) throw new Error(`Expected an isolated ${site.code} demo plant only.`);
  if (roster.length !== workers.length) throw new Error(`Expected the seeded ${workers.length}-worker roster; found ${roster.length}.`);
  const personByName = new Map(roster.map(worker => [worker.displayName, worker]));
  if (workers.some(worker => !personByName.has(worker.displayName))) throw new Error('The seeded roster differs from the WhiskerWorks manifest.');
  const workerByPersonId = new Map(workers.map(worker => [personByName.get(worker.displayName).personId, worker]));
  const focusWorker = workers.find(worker => worker.shiftCode === 'SHIFT-DAY');
  if (!focusWorker) throw new Error('The manifest has no Day Shift worker for the 60-shift attendance fixture.');

  const now = new Date();
  const throughMs = localDayStartUtc(now, site.timeZone);
  const fromMs = localDayStartUtc(new Date(throughMs - 84 * 86_400_000), site.timeZone);
  const from = new Date(fromMs).toISOString();
  const through = new Date(throughMs).toISOString();
  const startPolicy = policies.find(policy => policy.policyCode === 'WW-S10-ATTENDANCE');
  if (!startPolicy) {
    await post('/api/workforce/attendance-policies', {
      policyCode: 'WW-S10-ATTENDANCE', revision: 1, effectiveFrom: from, effectiveThrough: null,
      graceMinutes: 5, includeCallOutsInArrivalDenominator: false,
    }, admin);
  } else if (startPolicy.revision !== 1 || startPolicy.graceMinutes !== 5 || startPolicy.includeCallOutsInArrivalDenominator !== false
      || Date.parse(startPolicy.effectiveFrom) > fromMs) {
    throw new Error('Existing WW-S10-ATTENDANCE policy differs from this fixture. Review it; no policy was overwritten.');
  }
  await post('/api/workforce/attendance-schedule/generate', { from, through }, reader);
  const query = `from=${encodeURIComponent(from)}&to=${encodeURIComponent(through)}`;
  const opportunities = await get(`/api/workforce/attendance-opportunities?${query}`, reader);
  const focusOpportunities = opportunities.filter(opportunity => workerByPersonId.get(opportunity.personId)?.subject === focusWorker.subject);
  if (focusOpportunities.length !== 60) throw new Error(`Expected 60 Day Shift opportunities for the focus employee; found ${focusOpportunities.length}. No attendance facts were imported.`);
  const events = await get(`/api/workforce/attendance-intelligence/events?${query}`, reviewer);
  const eventsByOpportunity = new Map();
  for (const event of events) {
    if (!eventsByOpportunity.has(event.opportunityId)) eventsByOpportunity.set(event.opportunityId, []);
    eventsByOpportunity.get(event.opportunityId).push(event);
  }
  const opportunityIndex = new Map();
  const counted = new Map();
  for (const opportunity of opportunities.sort((left, right) => Date.parse(left.scheduledStart) - Date.parse(right.scheduledStart)
      || left.personId.localeCompare(right.personId))) {
    const worker = workerByPersonId.get(opportunity.personId);
    if (!worker) throw new Error('An attendance opportunity references a worker outside the seeded roster.');
    const index = opportunityIndex.get(worker.subject) ?? 0;
    opportunityIndex.set(worker.subject, index + 1);
    const prior = eventsByOpportunity.get(opportunity.opportunityId) ?? [];
    if (prior.length) {
      if (prior.some(event => event.source !== 'Hris')) continue;
      const superseded = new Set(prior.map(event => event.supersedesEventId).filter(Boolean));
      const current = prior.filter(event => !superseded.has(event.eventId)).sort((left, right) => Date.parse(left.recordedAt) - Date.parse(right.recordedAt)).at(-1);
      if (current?.reviewState === 'Verified') continue;
      if (current?.reviewState !== 'PendingReview' || !current.eventId) throw new Error(`Existing HRIS fact for ${worker.displayName} needs review before reseeding.`);
      await post(`/api/workforce/attendance-intelligence/events/${current.eventId}/review`, {
        decision: 'Verified', reasonCode: 'WW_DEMO_REVIEW', reason: 'Independent review of fictional attendance source evidence',
      }, reviewer);
      counted.set('reviewed', (counted.get('reviewed') ?? 0) + 1);
      continue;
    }
    const scheduledStart = Date.parse(opportunity.scheduledStart);
    const scheduledEnd = Date.parse(opportunity.scheduledEnd);
    const fact = buildSprint10AttendanceFact(worker, index, scheduledStart, scheduledEnd, focusWorker.subject);
    const sourceRecordId = `WW-S10:${worker.code}:${scheduledStart}`;
    const imported = await post('/api/workforce/attendance-intelligence/imports', {
      opportunityId: opportunity.opportunityId, kind: fact.kind, observedAt: fact.observedAt === null ? null : new Date(fact.observedAt).toISOString(),
      source: 'Hris', sourceRecordId,
    }, importer);
    const eventId = imported?.entityId;
    if (!eventId) throw new Error(`Attendance import for ${worker.displayName} did not return an event identifier.`);
    await post(`/api/workforce/attendance-intelligence/events/${eventId}/review`, {
      decision: 'Verified', reasonCode: 'WW_DEMO_REVIEW', reason: 'Independent review of fictional attendance source evidence',
    }, reviewer);
    counted.set(fact.kind, (counted.get(fact.kind) ?? 0) + 1);
  }
  const finalEvents = await get(`/api/workforce/attendance-intelligence/events?${query}`, reviewer);
  const focusIds = new Set(focusOpportunities.map(opportunity => opportunity.opportunityId));
  const focusEventRows = finalEvents.filter(event => focusIds.has(event.opportunityId));
  const supersededFocusIds = new Set(focusEventRows.map(event => event.supersedesEventId).filter(Boolean));
  const focusCurrent = focusEventRows.filter(event => !supersededFocusIds.has(event.eventId));
  const opportunityById = new Map(focusOpportunities.map(opportunity => [opportunity.opportunityId, opportunity]));
  const focusArrivals = focusCurrent.filter(event => event.kind === 'Arrival');
  const onTime = focusArrivals.filter(event => Date.parse(event.observedAt)
    <= Date.parse(opportunityById.get(event.opportunityId).scheduledStart) + 5 * 60_000).length;
  if (focusCurrent.length !== 60 || focusCurrent.some(event => event.reviewState !== 'Verified')
      || onTime !== 53 || focusArrivals.length !== 55
      || focusCurrent.filter(event => event.kind === 'ApprovedPto').length !== 4
      || focusCurrent.filter(event => event.kind === 'ProtectedLeave').length !== 1) {
    throw new Error(`The 60-shift focus fixture did not reconcile to 53/55 arrivals, 4 PTO and 1 protected-leave exclusion (${focusCurrent.length} verified events, ${onTime} on time).`);
  }
  console.log(`Seeded ${opportunities.length} scheduled attendance opportunities for ${workers.length} fictional workers at ${site.code}.`);
  console.log(`The focus employee has ${focusOpportunities.length} reviewed shifts (53 on-time arrivals, 2 late arrivals, 4 PTO, 1 protected leave).`);
  console.log(`New reviewed events: ${[...counted.entries()].map(([kind, count]) => `${kind}=${count}`).join(', ') || 'none'}; source records are retained for retry protection (${finalEvents.length} event history rows).`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch(error => { console.error(`WhiskerWorks Sprint 10 attendance seed stopped: ${error.message}`); process.exitCode = 1; });
}
