// Replays the WhiskerWorks demo on its own isolated database (Sprint 9/10). Run against a demo host started by
// run-whiskerworks-demo.cmd (Forge:Demo:Enabled, database forge_whiskerworks_demo, port 5090). It never touches the PLANT1 preview:
// every write goes to --base, and the run stops unless that host reports demo mode and a demo-only database.
//
//   node tools/replay-whiskerworks.mjs --base=http://localhost:5090 [--admin=dev-admin] [--out=.forge-logs/whiskerworks-replay.json]
//
// First run: seeds the four WhiskerWorks datasets through the audited APIs, designates WW-DEMO / WW-P01 and saves the baseline.
// Every run: resets to the baseline, then replays the clock-driven checks and records evidence with pass/fail per assertion.
import { spawnSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { scenarioQualificationExpiry } from './whiskerworks-plant.mjs';

const args = process.argv.slice(2);
const value = name => args.find(argument => argument.startsWith(`--${name}=`))?.slice(name.length + 3);
const base = value('base');
const admin = value('admin') ?? 'dev-admin';
const out = value('out') ?? '.forge-logs/whiskerworks-replay.json';
if (!base) throw new Error('Pass --base=http://localhost:<demo port>; there is no default target.');
const target = new URL(base);
if (!['localhost', '127.0.0.1', '::1'].includes(target.hostname)) throw new Error('Refusing a non-local replay target.');

const engineer = 'ww-demo-engineer';
const planner = 'ww-demo-planner';
const evidence = { startedAt: new Date().toISOString(), base, steps: [], assertions: [] };
const step = (name, detail = {}) => { evidence.steps.push({ name, at: new Date().toISOString(), ...detail }); console.log(`- ${name}`); };
const check = (name, passed, detail = {}) => { evidence.assertions.push({ name, passed: !!passed, ...detail }); console.log(`  ${passed ? 'PASS' : 'FAIL'} ${name}`); };

async function request(method, path, who, body) {
  const headers = { 'X-Forge-Dev-Subject': who, Accept: 'application/json' };
  if (body !== undefined) { headers['Content-Type'] = 'application/json'; headers['Idempotency-Key'] = `ww-replay-${randomUUID()}`; }
  const response = await fetch(`${base}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(30 * 60_000) });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path} as ${who} returned ${response.status}: ${text}`);
  return text ? JSON.parse(text) : null;
}
const get = (path, who = admin) => request('GET', path, who);
const post = (path, body, who = admin) => request('POST', path, who, body);

async function ready() {
  for (let attempt = 0; attempt < 120; attempt++) {
    try { const r = await fetch(`${base}/health/ready`); if (r.ok) return; } catch { /* starting */ }
    await new Promise(resolve => setTimeout(resolve, 2000));
  }
  throw new Error(`Forge is not ready at ${base}.`);
}

function seed(script, extra) {
  const run = spawnSync(process.execPath, [`tools/${script}`, '--apply', `--base=${base}`, ...extra], { stdio: 'inherit' });
  if (run.status !== 0) throw new Error(`${script} failed with exit code ${run.status}.`);
  step(`Applied ${script}`);
}

async function ensureReader() {
  const people = await get('/api/kernel/people');
  if (people.some(person => person.idpSubject === engineer)) return;
  const created = await post('/api/kernel/people', { idpSubject: engineer, displayName: 'WhiskerWorks Demo Engineer', reason: 'WhiskerWorks replay reader' });
  for (const role of ['ManufacturingEngineer', 'TrainingCoordinator', 'SafetyEngineer', 'QualityEngineer', 'MaintenancePlanner', 'Buyer', 'MaterialHandler', 'DocumentController'])
    await post(`/api/kernel/people/${created.entityId}/roles`, { role, reason: 'WhiskerWorks replay reader' });
}

async function cncMillReadiness() {
  const result = await get(`/api/readiness/machines/${encodeURIComponent(scenarioQualificationExpiry.machineCode)}?activity=CNC_MILL`, engineer);
  const output = result.output ?? result;
  const workforce = (output.dimensions ?? []).find(d => d.name === 'Workforce');
  return { outcome: workforce?.outcome ?? null, detail: workforce?.detail ?? null };
}

async function main() {
  await ready();
  let status = await get('/api/demo/status');
  if (!status.enabled) throw new Error('The target host is not in demo mode (Forge:Demo:Enabled); nothing was written.');
  step('Demo host reachable', { simulatedNow: status.simulatedNow, designated: status.designated });

  if (!status.designated) {
    const sites = (await get('/api/resources/')).sites ?? [];
    if (sites.some(site => site.code !== 'WW-P01')) throw new Error(`Refusing: the target database holds other sites (${sites.map(s => s.code).join(', ')}).`);
    await ensureReader();
    // The foundation refuses to run again once the commercial layer exists; the later seeds are safe to rerun.
    if ((await get('/api/engineering/items', engineer)).length === 0) seed('seed-whiskerworks.mjs', [`--subject=${admin}`, `--reader=${engineer}`]);
    else step('Foundation already seeded');
    seed('seed-whiskerworks-commercial.mjs', [`--subject=${admin}`, `--reader=${planner}`]);
    seed('seed-whiskerworks-sprint10-competency.mjs', [`--subject=${admin}`, `--reader=${engineer}`]);
    const policies = await get('/api/workforce/attendance-policies');
    if (policies.some(policy => policy.policyCode === 'WW-S10-ATTENDANCE')) step('Attendance history already seeded');
    else seed('seed-whiskerworks-sprint10-attendance.mjs', [`--admin=${admin}`, '--reader=ww-demo-security', '--importer=ww-demo-attendance-importer', '--reviewer=ww-demo-supervisor']);
    await post('/api/demo/designate', { demoCode: 'WW-DEMO', siteCode: 'WW-P01', reason: 'Isolated WhiskerWorks replay database' });
    step('Designated WW-DEMO / WW-P01');
    status = await get('/api/demo/status');
  }

  if (!status.baseline) {
    step('Saving baseline (whole-database copy)');
    await post('/api/demo/baseline', { reason: 'S01 clean WhiskerWorks baseline' }, planner);
    step('Saved baseline');
  } else {
    step('Resetting to saved baseline');
    await post('/api/demo/reset', { reason: 'Replay from the saved baseline' });
    step('Reset to saved baseline');
  }

  status = await get('/api/demo/status');
  const baselineOffset = status.offsetSeconds;
  const baselineKpis = await get('/api/demo/kpis', planner);
  evidence.baseline = { status, kpis: baselineKpis };
  const sites = (await get('/api/resources/')).sites ?? [];
  check('Only the WW-P01 site exists in the replay database', sites.length === 1 && sites[0].code === 'WW-P01', { sites: sites.map(s => s.code) });
  check('The S01 baseline has released work', baselineKpis.releasedOrders > 0, { releasedOrders: baselineKpis.releasedOrders });

  // S05: a CNC-02 mill qualification seeded to expire five days out stops counting once the simulated clock passes it.
  const before = await cncMillReadiness();
  const workers = await get('/api/workforce/workers', engineer);
  const ids = workers.map(w => w.personId);
  const records = ids.length ? await get(`/api/competency/people/qualifications?${ids.map(id => `personIds=${encodeURIComponent(id)}`).join('&')}`, engineer) : [];
  const expiring = records.filter(r => r.code === scenarioQualificationExpiry.qualificationCode && r.expiresAt
    && Date.parse(r.expiresAt) - Date.parse(status.simulatedNow) < 7 * 864e5 && Date.parse(r.expiresAt) > Date.parse(status.simulatedNow));
  check('S05 seeds a CNC mill qualification expiring within a week', expiring.length >= 1, { expiring: expiring.map(r => ({ personId: r.personId, expiresAt: r.expiresAt })) });
  const person = workers.find(w => w.personId === expiring[0]?.personId);
  await post('/api/demo/clock/advance', { minutes: 6 * 24 * 60, reason: 'S05: six days later' }, planner);
  const later = await get('/api/demo/status');
  const after = await cncMillReadiness();
  evidence.s05 = { person: person?.displayName ?? null, before, after, simulatedNow: later.simulatedNow };
  check('The simulated clock moved six days', later.offsetSeconds - baselineOffset === 6 * 24 * 3600, { offsetSeconds: later.offsetSeconds });
  check('After expiry the CNC-02 mill readiness no longer counts that operator',
    !!person && !(after.detail ?? '').includes(person.displayName), { detail: after.detail });

  evidence.comparison = await get('/api/demo/compare', planner);
  step('Compared with baseline');

  // A reset returns to the baseline exactly, clock included; the replaced state is archived, not deleted.
  const reset = await post('/api/demo/reset', { reason: 'Return to the clean baseline after the replay' });
  const restored = await get('/api/demo/status');
  const restoredKpis = await get('/api/demo/kpis', planner);
  check('Reset restores the baseline clock', restored.offsetSeconds === baselineOffset, { offsetSeconds: restored.offsetSeconds });
  check('Reset restores the baseline orders and stock', restoredKpis.releasedOrders === baselineKpis.releasedOrders
    && restoredKpis.heldOrders === baselineKpis.heldOrders && restoredKpis.stockHold === baselineKpis.stockHold, { restoredKpis });
  check('Reset archives the replaced database', /_archive_/.test(reset.message ?? ''), { message: reset.message });

  evidence.finishedAt = new Date().toISOString();
  evidence.passed = evidence.assertions.every(a => a.passed);
  writeFileSync(out, JSON.stringify(evidence, null, 2));
  console.log(`${evidence.assertions.filter(a => a.passed).length}/${evidence.assertions.length} assertions passed. Evidence: ${out}`);
  if (!evidence.passed) process.exitCode = 1;
}

main().catch(error => {
  evidence.error = error.cause ? `${error.message}: ${error.cause.code ?? ''} ${error.cause.message ?? error.cause}` : error.message;
  evidence.passed = false;
  try { writeFileSync(out, JSON.stringify(evidence, null, 2)); } catch { /* ignore */ }
  console.error(evidence.error);
  process.exitCode = 1;
});
