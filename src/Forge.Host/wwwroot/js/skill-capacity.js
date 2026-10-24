import { esc, fmtNumber, fmtDate } from './ui.js';

// Skill recency and proficiency (Sprint 10 §21.5): what a recovery candidate could produce at the gap and what their own work loses.
// A planning estimate with its evidence; never a grade of the person.

const range = f => f.expectedGoodUnits == null
  ? 'No capacity estimate'
  : `${fmtNumber(f.conservativeGoodUnits, 0)}–${fmtNumber(f.optimisticGoodUnits, 0)} good units (expected ${fmtNumber(f.expectedGoodUnits, 0)})`;

/** One candidate's capacity facts as a compact block: qualified, last operated, setup, range, confidence, refresher, origin impact. */
export function candidateCapacityHtml(capacity) {
  if (!capacity) return '';
  const t = capacity.atTarget;
  const f = t.forecast;
  const origin = capacity.atOrigin?.forecast;
  const facts = [
    ['Qualified', f.isEligible ? 'Yes' : `No — ${f.eligibilityStatus}`],
    ['Last operated', t.lastOperatedAt ? `${fmtDate(t.lastOperatedAt)} (${f.daysSinceLastVerifiedProductiveExecution} days ago)` : 'No verified operation on record'],
    ['Expected setup', t.expectedSetupMinutes == null ? '—' : `${fmtNumber(t.expectedSetupMinutes, 0)} min`],
    ['Expected good units', f.isEligible ? range(f) : '—'],
    ['Confidence', f.confidence ?? '—'],
    ['Required refresher', t.requiredRefresher ?? 'None'],
    ['Impact on original work', origin?.expectedGoodUnits == null ? 'Not estimated' : `About ${fmtNumber(origin.expectedGoodUnits, 0)} units lost unless backfilled`],
  ];
  return `<div class="skill-capacity">
    <dl class="compact">${facts.map(([k, v]) => `<dt>${esc(k)}</dt><dd>${esc(v)}</dd>`).join('')}</dl>
    <div class="faint">${esc(capacity.explanation)}</div>
    <div class="faint">${esc(f.estimateBasis)} ${(t.notes ?? []).map(esc).join(' ')}</div>
  </div>`;
}

/** The capacity entries by person, for joining to the recovery candidates. */
export const capacityByPerson = result => new Map((result?.candidates ?? []).map(c => [c.personId, c]));
