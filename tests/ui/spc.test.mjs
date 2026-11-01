import assert from 'node:assert/strict';
import test from 'node:test';
import { installBrowser } from './browser-harness.mjs';

const chart = (over = {}) => ({
  key: { partNumber: 'SHAFT-1', sequence: 20, number: 1 }, name: 'Diameter', unit: 'mm', nominal: 10, lowerSpec: 9.9, upperSpec: 10.1, critical: false,
  points: [10.0, 10.01, 9.99, 10.0, 10.08].map((value, i) => ({ inspectionId: i + 1, orderNumber: 10002, at: `2026-11-0${i + 1}T12:00:00Z`, value })),
  mean: 10.016, averageMovingRange: 0.03, sigma: 0.0266, upperControlLimit: 10.0958, lowerControlLimit: 9.9362, movingRangeUpperLimit: 0.098, cp: 1.253, cpk: 1.053,
  requiredCpk: 1.33, capable: false, violations: [{ pointIndex: 4, inspectionId: 5, rule: 'BeyondThreeSigma', description: 'One point beyond 3 sigma.' }],
  notes: ['Cpk 1.05 is below the required 1.33.'], ...over });

test('an individuals chart draws limits, the drawing tolerance and marks rule breaks', async t => {
  installBrowser(t);
  const { iChartSvg, chartDetailHtml, capabilityPill } = await import('../../src/Forge.Host/wwwroot/js/pages/spc.js');
  const svg = iChartSvg(chart());
  for (const label of ['UCL', 'CL', 'LCL', 'USL', 'LSL']) assert.match(svg, new RegExp(`>${label}<`));
  assert.equal((svg.match(/<circle /g) ?? []).length, 5);
  assert.match(svg, /r="4.5"[^>]*data-point="4"/);
  assert.match(iChartSvg(chart({ points: [] })), /No measurements/);
  const detail = chartDetailHtml(chart());
  assert.match(detail, /Cpk 1.05 &lt; 1.33/);
  assert.match(detail, /Beyond 3σ/);
  assert.match(detail, /below the required 1.33/);
  assert.match(capabilityPill(null), /Too few points/);
  assert.match(capabilityPill(true, 1.71, 1.67), /pill ok">Cpk 1.71 ≥ 1.67/);
});

test('capability lists open signals and critical characteristics, and signals can be acknowledged', async t => {
  installBrowser(t);
  const { capabilityHtml, signalsHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/spc.js');
  const html = capabilityHtml([{ key: { partNumber: 'SHAFT-1', sequence: 20, number: 1 }, name: 'Diameter', unit: 'mm', critical: true, points: 25, cpk: 1.5, requiredCpk: 1.67, capable: false, violations: 2, openSignals: 1, last: '2026-11-01T12:00:00Z' }], { canManage: true });
  assert.match(html, /pill warn">Critical/);
  assert.match(html, /pill bad">1</);
  assert.match(html, /data-chart="SHAFT-1\|20\|1"/);
  assert.match(html, /Not critical/);
  assert.match(capabilityHtml([]), /No measurements yet/);
  const signals = [
    { signalId: 7, key: { partNumber: 'SHAFT-1', sequence: 20, number: 1 }, characteristicName: 'Diameter', inspectionId: 5, orderNumber: 10002, rule: 'EightOnOneSide', description: '', value: 10.02, detectedAt: '2026-11-01T12:00:00Z', status: 'Open' },
    { signalId: 6, key: { partNumber: 'SHAFT-1', sequence: 20, number: 1 }, characteristicName: 'Diameter', inspectionId: 4, orderNumber: 10002, rule: 'BeyondThreeSigma', description: '', value: 10.2, detectedAt: '2026-10-30T12:00:00Z', status: 'Acknowledged', acknowledgedBy: 'person:qe', note: 'Insert changed' },
  ];
  const s = signalsHtml(signals, { canAcknowledge: true });
  assert.match(s, /data-ack="7"/);
  assert.doesNotMatch(s, /data-ack="6"/);
  assert.match(s, /8 on one side/);
  assert.match(s, /Insert changed/);
  assert.match(signalsHtml([]), /No open signals/);
});

test('supplier scorecards show raw measures with a grade and say what counts as on time', async t => {
  installBrowser(t);
  const { scorecardHtml, onTimeRule, gradePill, supplierDetailHtml } = await import('../../src/Forge.Host/wwwroot/js/pages/supplier-scorecard.js');
  assert.equal(onTimeRule({ lateToleranceDays: 0, earlyLimitDays: null }), 'On time: received on or before the promised date.');
  assert.equal(onTimeRule({ lateToleranceDays: 2, earlyLimitDays: 5 }), 'On time: received up to 2 day(s) after the promised date, and no more than 5 day(s) early.');
  assert.match(gradePill(null), /No receipts/);
  const supplier = { supplierCode: 'ACME', supplierName: 'Acme Metals', receipts: 4, onTimeReceipts: 3, onTimePercent: 75, averageDaysLate: 3, lateReceipts: 1, quantityReceived: 400, quantityRejected: 2,
    nonconformances: 1, qualityPercent: 99.5, ppm: 5000, pastDueLines: 1, grade: 'C',
    late: [{ poNumber: 5001, lineNumber: 1, partNumber: 'BAR-1', quantity: 100, received: '2026-10-20', promised: '2026-10-17', daysLate: 3, onTime: false }],
    rejections: [{ ncrId: 9, partNumber: 'BAR-1', lotNumber: 'L-1', quantity: 2, defect: 'Cracked', raisedAt: '2026-10-21T10:00:00Z' }] };
  const html = scorecardHtml({ suppliers: [supplier] });
  assert.match(html, /Acme Metals/);
  assert.match(html, /pill bad">C/);
  assert.match(html, /75% <span class="faint">\(3\/4\)/);
  assert.match(html, /5,000/);
  assert.match(html, /pill warn">1</);
  assert.match(html, /data-detail="ACME"/);
  assert.match(scorecardHtml({ suppliers: [] }), /No supplier activity/);
  const detail = supplierDetailHtml(supplier);
  assert.match(detail, /3 late/);
  assert.match(detail, /Cracked/);
});
