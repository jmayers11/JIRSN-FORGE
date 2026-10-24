#!/usr/bin/env node
// Adds audited course, practical-assessment, and machine-authorization history
// to an already seeded WhiskerWorks demo plant. This has no reset/delete path.
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';
import { demoPrincipals, machineActivities, site, workers } from './whiskerworks-plant.mjs';

const args = process.argv.slice(2);
const value = name => args.find(argument => argument.startsWith(`--${name}=`))?.slice(name.length + 3);
const apply = args.includes('--apply');
const baseArg = value('base');
const subject = value('subject');
const reader = value('reader');
const base = baseArg?.replace(/\/+$/, '');
const reason = 'Record fictional WhiskerWorks Sprint 10 competency evidence';

export function buildSprint10CompetencyPlan(roster = workers, profiles = machineActivities) {
  const profileByKey = new Map(profiles.map(profile => [`${profile.machineCode}:${profile.activityCode}`, profile]));
  const plan = [];
  for (const worker of roster) {
    if (!worker.machineCode) continue;
    const profile = profileByKey.get(`${worker.machineCode}:${worker.activityCode}`);
    if (!profile) throw new Error(`Missing machine activity profile for ${worker.machineCode}:${worker.activityCode}.`);
    if (profile.operatorQualifications.length === 0) continue;
    const qualificationCode = profile.operatorQualifications.find(code => worker.qualifications.includes(code));
    if (!qualificationCode) throw new Error(`No manifest qualification covers ${worker.subject} at ${worker.machineCode}:${worker.activityCode}.`);
    plan.push({
      workerSubject: worker.subject,
      workerName: worker.displayName,
      machineCode: worker.machineCode,
      activityCode: worker.activityCode,
      qualificationCode,
    });
  }
  return plan;
}

const plan = buildSprint10CompetencyPlan();
const uniqueCourses = new Set(plan.map(item => `${item.workerSubject}:${item.qualificationCode}`));

async function request(method, path, who = reader, body = undefined) {
  const headers = { 'X-Forge-Dev-Subject': who, Accept: 'application/json' };
  if (body !== undefined) {
    body = { reason, ...body };
    headers['Content-Type'] = 'application/json';
    const hash = createHash('sha256').update(`${method}\n${path}\n${JSON.stringify(body)}`).digest('hex').slice(0, 32);
    headers['Idempotency-Key'] = `ww-s10-competency-v1-${hash}`;
  }
  const response = await fetch(`${base}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path} as ${who} returned ${response.status}: ${text}`);
  return text ? JSON.parse(text) : null;
}
const get = (path, who = reader) => request('GET', path, who);
const post = (path, body, who = subject) => request('POST', path, who, body);
const commandId = result => result?.entityId ?? result?.id ?? null;
const isCurrent = record => !record.expiresAt || Date.parse(record.expiresAt) > Date.now();

async function main() {
  if (!apply) {
    console.log('WhiskerWorks Sprint 10 competency history plan (dry run; no network or database calls)');
    console.log(`Target site: ${site.code}`);
    console.log(`Machine assignments: ${plan.length}; distinct course completions: ${uniqueCourses.size}; practical assessments: ${plan.length}; machine authorizations: ${plan.length}.`);
    console.log('To apply, first seed the WhiskerWorks foundation, then pass --apply --base=http://localhost:<port> --subject=<authorized-admin> --reader=<competency-reader>.');
    console.log('The apply path only appends missing evidence. It refuses a mismatched, failed, suspended, or revoked record and has no reset/delete operation.');
    return;
  }
  if (!baseArg || !subject || !reader) throw new Error('Applying requires explicit --base, --subject and --reader arguments.');
  const targetUrl = new URL(base);
  if (!['localhost', '127.0.0.1', '::1'].includes(targetUrl.hostname) && !args.includes('--allow-remote-demo')) {
    throw new Error('Refusing a non-local seed target. Pass --allow-remote-demo only for a deliberately isolated demo host.');
  }
  await get('/health/ready').catch(error => { throw new Error(`Forge is not ready at ${base}: ${error.message}`); });
  const profile = await get('/api/kernel/me', reader);
  const permissions = new Set(profile.permissions ?? []);
  for (const permission of ['competency.read', 'resources.read', 'workforce.read']) {
    if (!permissions.has(permission)) throw new Error(`Reader ${reader} is missing ${permission}.`);
  }

  const [resources, people, seededWorkers, qualificationTypes] = await Promise.all([
    get('/api/resources/?includeRetired=true'), get('/api/kernel/people', subject), get('/api/workforce/workers', reader),
    get('/api/competency/qualification-types', reader),
  ]);
  if ((resources.sites ?? []).length !== 1 || resources.sites[0].code !== site.code) {
    throw new Error(`Expected the isolated ${site.code} demo site only. No writes were made.`);
  }
  const resourceProfiles = new Map((resources.machineActivities ?? []).map(item => [`${item.machineCode}:${item.activityCode}`, item]));
  for (const item of plan) {
    const actual = resourceProfiles.get(`${item.machineCode}:${item.activityCode}`);
    if (!actual || !actual.operatorQualifications?.includes(item.qualificationCode)) {
      throw new Error(`Demo resource profile ${item.machineCode}:${item.activityCode} does not match the training plan. No writes were made.`);
    }
  }
  const peopleBySubject = new Map(people.map(person => [person.idpSubject, person]));
  const workersByName = new Map(seededWorkers.map(worker => [worker.displayName, worker]));
  if (seededWorkers.length !== workers.length || workers.some(worker => !peopleBySubject.has(worker.subject) || !workersByName.has(worker.displayName))) {
    throw new Error(`Expected the complete ${workers.length}-worker WhiskerWorks foundation. No writes were made.`);
  }
  const qualificationsByPerson = new Map();
  const personIds = [...workers.map(worker => workersByName.get(worker.displayName).personId), peopleBySubject.get(demoPrincipals[1].subject)?.personId].filter(Boolean);
  const personQuery = new URLSearchParams();
  for (const id of personIds) personQuery.append('personIds', id);
  const [qualificationRecords, courseRecords, assessmentRecords, authorizations] = await Promise.all([
    get(`/api/competency/people/qualifications?${personQuery}`),
    get(`/api/competency/training-course-completions?${personQuery}`),
    get(`/api/competency/practical-assessments?${personQuery}`),
    get(`/api/competency/machine-authorizations?${personQuery}`),
  ]);
  for (const record of qualificationRecords) {
    const key = record.personId;
    if (!qualificationsByPerson.has(key)) qualificationsByPerson.set(key, new Map());
    qualificationsByPerson.get(key).set(record.code, record);
  }
  const trainerWorker = workers.find(worker => worker.qualifications.includes('QUAL-TRAINER-CNC'));
  const trainerPerson = peopleBySubject.get(trainerWorker?.subject);
  const certifier = peopleBySubject.get(demoPrincipals[1].subject);
  if (!trainerWorker || !trainerPerson || !certifier) throw new Error('A distinct seeded trainer and demo certifier are required.');

  const trainerRole = (trainerPerson.roles ?? []).includes('Certifier');
  if (!trainerRole) await post(`/api/kernel/people/${trainerPerson.personId}/roles`, { role: 'Certifier' }, subject);

  const typesByCode = new Map(qualificationTypes.map(item => [item.code, item]));
  const certifierQualifications = qualificationsByPerson.get(certifier.personId) ?? new Map();
  const prerequisites = [];
  const collectPrerequisites = code => {
    const type = typesByCode.get(code);
    if (!type) throw new Error(`Unknown qualification type ${code}.`);
    for (const prerequisite of type.prerequisites ?? []) collectPrerequisites(prerequisite);
    if (!prerequisites.includes(code)) prerequisites.push(code);
  };
  collectPrerequisites('QUAL-TRAINER-CNC');
  for (const code of prerequisites) {
    const held = certifierQualifications.get(code);
    if (held?.status === 'Qualified' && isCurrent(held)) continue;
    if (held && held.status !== 'InTraining') throw new Error(`The demo certifier has ${held.status} ${code}; review it before changing competency history.`);
    if (!held) await post(`/api/competency/people/${certifier.personId}/training`, { code }, 'ww-demo-engineer');
    await post(`/api/competency/people/${certifier.personId}/certifications`, {
      code, evidence: 'Fictional WhiskerWorks trainer qualification; demo evidence only.',
    }, trainerWorker.subject);
    certifierQualifications.set(code, { personId: certifier.personId, code, status: 'Qualified', expiresAt: null });
  }

  const currentCourse = (personId, code) => courseRecords.find(item => item.personId === personId && item.courseCode === code && isCurrent(item));
  const findAssessment = (item, personId) => assessmentRecords.find(record => record.personId === personId
    && record.qualificationCode === item.qualificationCode && record.machineCode === item.machineCode
    && record.activityCode === item.activityCode);
  let coursesAdded = 0, assessmentsAdded = 0, authorizationsAdded = 0;
  const assessmentIdByPlan = new Map();
  for (const item of plan) {
    const personId = workersByName.get(item.workerName).personId;
    const qualification = qualificationsByPerson.get(personId)?.get(item.qualificationCode);
    if (qualification?.status !== 'Qualified' || !isCurrent(qualification)) {
      throw new Error(`${item.workerName} lacks a current ${item.qualificationCode}; no machine evidence was written for this plan item.`);
    }
    let course = currentCourse(personId, item.qualificationCode);
    if (!course) {
      const created = await post(`/api/competency/people/${personId}/training-course-completions`, {
        courseCode: item.qualificationCode, courseVersion: 'WW-S10-1', expiresAt: null,
        evidence: 'Fictional WhiskerWorks course completion; not a real-world credential.',
      }, certifier.idpSubject);
      course = { personId, courseCode: item.qualificationCode, completionId: commandId(created), expiresAt: null };
      courseRecords.push(course);
      coursesAdded++;
    }
    let assessment = findAssessment(item, personId);
    if (assessment && !assessment.passed) throw new Error(`${item.workerName} has a failed practical on ${item.machineCode}; review the result rather than replacing it.`);
    if (!assessment) {
      const created = await post(`/api/competency/people/${personId}/practical-assessments`, {
        qualificationCode: item.qualificationCode, machineCode: item.machineCode, activityCode: item.activityCode,
        passed: true, assessmentVersion: 'WW-S10-1', evidence: 'Fictional WhiskerWorks supervised practical; demo evidence only.',
      }, certifier.idpSubject);
      assessment = { personId, qualificationCode: item.qualificationCode, machineCode: item.machineCode, activityCode: item.activityCode, passed: true, assessmentId: commandId(created) };
      assessmentRecords.push(assessment);
      assessmentsAdded++;
    }
    if (!assessment.assessmentId) throw new Error(`Missing passing practical assessment identifier for ${item.workerName} on ${item.machineCode}.`);
    assessmentIdByPlan.set(`${personId}:${item.machineCode}:${item.activityCode}:${item.qualificationCode}`, assessment.assessmentId);
  }

  for (const item of plan) {
    const personId = workersByName.get(item.workerName).personId;
    const existing = authorizations.find(record => record.personId === personId && record.machineCode === item.machineCode
      && record.activityCode === item.activityCode && record.qualificationCode === item.qualificationCode);
    if (existing) {
      if (existing.status !== 'Authorized' || !existing.independentOperationAllowed || !isCurrent(existing)) {
        throw new Error(`${item.workerName} has a non-current or restricted authorization for ${item.machineCode}; review it instead of overwriting it.`);
      }
      continue;
    }
    const assessmentId = assessmentIdByPlan.get(`${personId}:${item.machineCode}:${item.activityCode}:${item.qualificationCode}`);
    await post(`/api/competency/people/${personId}/machine-authorizations`, {
      machineCode: item.machineCode, activityCode: item.activityCode, qualificationCode: item.qualificationCode,
      assessmentId, expiresAt: null, evidence: 'Fictional WhiskerWorks authorization; demo evidence only.',
      restrictions: null, independentOperationAllowed: true,
    }, certifier.idpSubject);
    authorizationsAdded++;
  }

  console.log(`Recorded WhiskerWorks competency evidence for ${plan.length} machine assignments at ${site.code}.`);
  console.log(`Added ${coursesAdded} course completion(s), ${assessmentsAdded} practical assessment(s), and ${authorizationsAdded} machine authorization(s); existing valid evidence was retained.`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch(error => { console.error(`WhiskerWorks Sprint 10 competency seed stopped: ${error.message}`); process.exitCode = 1; });
}
