// Canonical fictional WhiskerWorks scenario catalog shared by Forge's demo UI and seed tooling.
export const scenarioCatalog = [
  ['WW-S01-BASELINE', 'Healthy Baseline', 'All commissioned machines, PM current, full shift and balanced WIP.'],
  ['WW-S02-ABSENCES', 'Shift Start Absences', 'Three confirmed callouts and two projected, unconfirmed absences.'],
  ['WW-S03-TWO-PERSON', 'Two-Person Safety Block', 'Remove one qualified operator from the ASM-03 two-person team.'],
  ['WW-S04-CNC-DOWN', 'Unplanned CNC Breakdown', 'Record an unplanned CNC-01 spindle coolant failure.'],
  ['WW-S05-INSP-SOON', 'Upcoming Inspection and PM', 'Set CNC-02 PM due in four hours and one qualification due in five days.'],
  ['WW-S06-MRB-HOLD', 'Motor Lot Quality Hold', 'Hold lot LOT-MOT-24V-881 and expose pegged orders.'],
  ['WW-S07-SHORTAGE', 'Bearing and Sensor Shortage', 'Delay the WW-SENS-PROX-05 supply by four days.'],
  ['WW-S08-LOT-SPLIT', 'Master Spool Lot Split and Hold', 'Split 250 m sisal parent into three child lots and reconcile five metres of scrap.'],
  ['WW-S09-ECN-REV', 'Engineering Revision Release', 'Release Rev B and WI-ELE-610 Rev B with training applicability.'],
  ['WW-S10-RUSH-ORDER', 'Rush VIP Customer Order', 'Insert a priority-one order for fifty WW-CAT-2000 units.'],
  ['WW-S11-CAL-EXPIRED', 'Out-of-Calibration Gauge and PM', 'Expire torque and optical gauges to verify execution interlocks.'],
  ['WW-S12-SCAN-OUTAGE', 'Scanner and Label Printer Outage', 'Exercise the kiosk fail-closed and pending reconciliation path.'],
  ['WW-S13-IPC-MISMATCH', 'IPC and Machine Qualification Blocks', 'Run the twelve negative competency and trainer coverage cases.'],
  ['WW-S14-RECOVERY-BOARD', 'Recovery Action Board', 'Create owned, dated actions with blocked dependencies.'],
  ['WW-S15-HANDOFF', 'Shift Handoff Continuity', 'Review and acknowledge open downtime and unconfirmed absences across shifts.'],
  ['WW-S16-CASCADE', 'Multi-Event Cascade', 'Combine ASM-03 absence, CNC-01 breakdown, and the held motor lot.'],
].map(([code, title, event]) => ({ code, title, event }));
