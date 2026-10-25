// Training & Performance (Supervisor menu): the Command Center on its own page — overview, employee review cards, training,
// coverage, attendance, labor evidence and development packets — without the rest of the Workforce administration page.
import { appendWorkforceIntelligence } from './workforce-intelligence.js';

export async function performancePage(page, ctx) {
  page.innerHTML = '<div class="page-head"><div><h1>Training &amp; Performance</h1><div class="sub">Employee review cards, qualifications, coverage and attendance from recorded evidence. Reviews are for people to discuss; Forge makes no personnel decision.</div></div></div>';
  await appendWorkforceIntelligence(page, ctx.me, ctx.query?.get?.('personId') ?? null, { after: true });
}
