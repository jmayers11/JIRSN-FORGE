// Cross-training plan for the WhiskerWorks demo (WW-P01): puts a few workers on Swing and Weekend shifts into training for the
// qualifications their shift has no qualified holder of, so the Training & Performance Command Center shows real training work.
// In Training grants nothing; certification stays an independent Competency step. Idempotent: existing records are left alone.
//
//   node tools/seed-whiskerworks-training.mjs --base=http://localhost:5080 [--admin=dev-admin] [--save-baseline]
//
// It also adds ww-demo-manager (Supervisor + Human Resources): the one demo sign-in that sees every figure on the Command Center's
// employee review cards, attendance rates included (those are Human Resources only).
// With --save-baseline the demo baseline is saved again afterwards, so "Reset demo" returns to the plan as well.
import { randomUUID } from 'node:crypto';

const args = process.argv.slice(2);
const value = name => args.find(argument => argument.startsWith(`--${name}=`))?.slice(name.length + 3);
const base = value('base');
if (!base) throw new Error('Pass --base=http://localhost:<port>.');
if (!['localhost', '127.0.0.1', '::1'].includes(new URL(base).hostname)) throw new Error('Refusing a non-local target.');

const coordinator = 'ww-demo-engineer';
const admin = value('admin') ?? 'dev-admin';
const manager = { subject: 'ww-demo-manager', displayName: 'WhiskerWorks Demo Plant Manager', roles: ['Supervisor', 'HumanResources'] };
// Which qualification each uncovered shift needs, and how many people to start on it. Work centers are where the trainees come from.
const crossTraining = [
  { qualification: 'QUAL-ASM-TORQUE', shift: 'SHIFT-SWG', from: ['WC-ASM-HVY', 'WC-PKG', 'WC-KIT'], count: 2 },
  { qualification: 'QUAL-ASM-HVY', shift: 'SHIFT-SWG', from: ['WC-ASM-STD', 'WC-ASM-HVY'], count: 1 },
  { qualification: 'QUAL-CNC-MILL', shift: 'SHIFT-WKD', from: ['WC-CNC-LATHE', 'WC-FAB', 'WC-MNT'], count: 1 },
  { qualification: 'QUAL-CNC-LATHE', shift: 'SHIFT-WKD', from: ['WC-CNC-MILL', 'WC-FAB', 'WC-MNT'], count: 1 },
];

async function request(method, path, who, body) {
  const headers = { 'X-Forge-Dev-Subject': who, Accept: 'application/json' };
  if (body !== undefined) { headers['Content-Type'] = 'application/json'; headers['Idempotency-Key'] = `ww-training-${randomUUID()}`; }
  const response = await fetch(`${base}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(10 * 60_000) });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path} as ${who} returned ${response.status}: ${text}`);
  return text ? JSON.parse(text) : null;
}

const rank = (plan, worker) => { const index = plan.from.indexOf(worker.homeWorkCenterCode); return index < 0 ? plan.from.length : index; };

async function ensureManager() {
  const people = await request('GET', '/api/kernel/people', admin);
  let person = people.find(item => item.idpSubject === manager.subject);
  const reason = 'WhiskerWorks demo plant manager for the employee review cards';
  if (!person) {
    const created = await request('POST', '/api/kernel/people', admin, { idpSubject: manager.subject, displayName: manager.displayName, reason });
    person = { personId: created.entityId, roles: [] };
  }
  const id = person.personId ?? person.id;
  const missing = manager.roles.filter(role => !(person.roles ?? []).includes(role));
  for (const role of missing) await request('POST', `/api/kernel/people/${id}/roles`, admin, { role, reason });
  return missing.length ? `${manager.subject}: added ${missing.join(', ')}` : null;
}

async function main() {
  const sites = (await request('GET', '/api/resources/', coordinator)).sites ?? [];
  if (sites.length !== 1 || sites[0].code !== 'WW-P01') throw new Error('Expected the isolated WW-P01 demo plant only; nothing was written.');
  const managerChange = await ensureManager();
  if (managerChange) console.log(managerChange);
  const workers = await request('GET', '/api/workforce/workers', coordinator);
  const query = workers.map(worker => `personIds=${encodeURIComponent(worker.personId)}`).join('&');
  const records = await request('GET', `/api/competency/people/qualifications?${query}`, coordinator);
  const has = (personId, code) => records.some(record => record.personId === personId && record.code === code);
  const started = [];
  for (const plan of crossTraining) {
    const holders = workers.filter(worker => worker.shiftCode === plan.shift && has(worker.personId, plan.qualification));
    const already = workers.filter(worker => worker.shiftCode === plan.shift
      && records.some(record => record.personId === worker.personId && record.code === plan.qualification && record.status === 'InTraining'));
    const needed = Math.max(0, plan.count - already.length);
    const candidates = workers
      .filter(worker => worker.shiftCode === plan.shift && !has(worker.personId, plan.qualification))
      // Nearest skills first (the listed work centers), then anyone else on that shift.
      .sort((a, b) => rank(plan, a) - rank(plan, b) || a.displayName.localeCompare(b.displayName))
      .slice(0, needed);
    for (const worker of candidates) {
      await request('POST', `/api/competency/people/${worker.personId}/training`, coordinator, {
        code: plan.qualification, reason: `Cross-training: ${plan.shift} has ${holders.length} qualified ${plan.qualification} holder(s)`,
      });
      started.push(`${worker.displayName} (${worker.homeWorkCenterCode}) → ${plan.qualification} for ${plan.shift}`);
    }
  }

  console.log(started.length ? `Started cross-training:\n- ${started.join('\n- ')}` : 'Cross-training plan already in place; nothing changed.');
  if (args.includes('--save-baseline')) {
    const result = await request('POST', '/api/demo/baseline', 'ww-demo-planner', { reason: 'S01 baseline with the cross-training plan' });
    console.log(result.message);
  }
}

main().catch(error => { console.error(`WhiskerWorks training plan stopped: ${error.message}`); process.exitCode = 1; });
