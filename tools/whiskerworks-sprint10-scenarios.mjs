// Sprint 10 acceptance matrix from the approved workforce-intelligence brief.
// Each case carries the evidence map below; statuses are claims about named checks, re-verify them when those tests change.
const core = [
  ['PTO coverage gap', 'Workforce', 'Approved CNC setup PTO removes the next-shift specialist; the gap and safe alternatives remain explicit.'],
  ['Unexpected callout recovery', 'Workforce', 'A same-day proposal ranks only currently qualified alternatives and reports source-center impact.'],
  ['No-show versus callout', 'Attendance', 'A missed clock-in remains pending until the shift ends and independent review distinguishes it from a reported callout.'],
  ['Two-person crew constraint', 'Safety', 'Removing one member of a required two-person crew makes the operation non-executable.'],
  ['Expired qualification block', 'Competency', 'A qualification expired yesterday and cannot authorize dispatch.'],
  ['Machine-specific authorization', 'Competency', 'CNC-01 authorization does not authorize CNC-03.'],
  ['Pending practical authorization', 'Competency', 'J-STD-001 without a passed machine practical does not authorize independent work.'],
  ['IPC reassessment window', 'Competency', 'IPC-A-610 due for reassessment is visible under the configured policy.'],
  ['Quality trend confounders', 'Quality', 'Harness trend is shown with equipment/material context and does not assign operator fault.'],
  ['Inaccurate labor standard', 'Performance', 'An unsupported or inaccurate standard is marked for review, not used as a worker score.'],
  ['Downtime-context labor', 'Performance', 'Lower output during recorded machine downtime retains the downtime context and is not treated as operator fault.'],
  ['Advanced training prerequisites', 'Training', 'Prerequisites and future bottleneck benefit are shown before human nomination.'],
  ['Multi-person packaging-safe recovery', 'Workforce', 'CNC coverage is restored without leaving Packaging below its safety or capacity requirements.'],
  ['Rework qualification expansion', 'Work Orders', 'Added solder/test work adds required qualifications and labor hours while retaining the original disposition.'],
  ['Sixty-shift attendance denominator', 'Attendance', 'The selected 60-shift quarter shows 53/55 eligible arrivals, 4 PTO days, 1 protected leave day, and 5 excluded opportunities.'],
  ['Attendance correction review', 'Attendance', 'A no-show correction supersedes the original event and returns the change for independent review.'],
  ['Quarter rollover history', 'Attendance', 'The current period resets at quarter rollover while the prior quarter remains queryable.'],
  ['Effective-dated attendance policy', 'Attendance', 'A new policy affects its effective period only and does not rewrite historical results.'],
  ['Insufficient evidence nomination', 'Training', 'A worker below the sample threshold receives insufficient evidence and no ranking.'],
  ['Advisory advancement packet', 'Development', 'A promotion packet requires human review and never changes compensation automatically.'],
  ['Forge-only workflow', 'Architecture', 'Employee, attendance, and qualification workflows continue with Nexus disconnected.'],
  ['Personnel privacy and tenant scope', 'Security', 'Cross-tenant and role checks prevent unauthorized access to sensitive personnel records.'],
];

const material = [
  ['Suspect scrap report', 'Quality / Inventory', 'Eight reported defects enter suspect hold and notify Quality without posting final scrap.'],
  ['Split disposition reconciliation', 'Quality / Inventory', 'Five units scrap and three rework; stock, genealogy, cost, and order quantities reconcile.'],
  ['Linked replacement request', 'Inventory', 'Five replacement units link to one suspect report, deduplicate, and enter the stockroom queue.'],
  ['Partial issue and backorder', 'Inventory', 'A request for 20 with 12 available issues at most 12 and leaves 8 backordered.'],
  ['Concurrent last-stock requests', 'Inventory / Concurrency', 'Two requests for the final 10 connectors cannot reserve or issue more than 10 total.'],
  ['Non-BOM approval gate', 'Inventory / Engineering', 'A non-BOM request requires planner approval and does not silently amend the released order.'],
  ['Rework routing expansion', 'Quality / Engineering / Planning', 'Approved rework adds a connector, inspection step, labor standard, MRP demand, and training requirement.'],
  ['Due-date risk email', 'Notifications / Planning', 'A projected shortage emits one actionable, permission-checked planner email with a deep link.'],
  ['Overdue escalation de-duplication', 'Notifications', 'An unfilled urgent request escalates once at threshold and resolves when fulfilled.'],
  ['Email provider retry', 'Notifications / Resilience', 'Provider failure retries delivery without duplicating a request or inventory transaction.'],
  ['Held stock pick denial', 'Inventory / Quality', 'Quality-held stock cannot be picked even when its physical quantity is positive.'],
  ['Network retry idempotency', 'Inventory / Idempotency', 'Repeated submission of one operator request creates a single canonical request.'],
  ['Manufacturing replacement recovery', 'Planning / Workforce', 'A replacement order proposal checks qualified operators and source-center impact before human approval.'],
  ['Machine-fault variance context', 'Performance / Maintenance', 'A recorded machine fault remains a contextual variance reason and does not penalize an employee.'],
  ['Partial issue cancellation', 'Inventory / Audit', 'Cancellation after partial issue reconciles the remaining demand without deleting history.'],
  ['Cross-role and cross-site permissions', 'Security / Inventory', 'Operator, stockroom, planner, Quality, and supervisor permissions are enforced across site and tenant boundaries.'],
];

// Where each case is proven (2026-10-25). 'Covered' means named regressions or replay checks that passed assert the acceptance
// invariant (sometimes on the slitting fixture rather than the WhiskerWorks names); 'Partial' means part of the invariant is proven
// and the note names the gap; 'Open' means the behavior is not implemented. Test names are Class.Method; 'replay Sxx' is
// tools/replay-whiskerworks.mjs; 'ui:' is a tests/ui file and test title.
const I = 'Forge.Integration.Tests.', P = 'Forge.Planning.Tests.';
const MAT = I + 'MaterialRequestAcceptanceTests.';
const MAT_A = MAT + 'Suspect_scrap_replacement_retried_requests_and_additional_material_stay_controlled';
const MAT_B = MAT + 'Held_and_last_stock_is_never_double_reserved_and_cancelling_after_a_partial_issue_reopens_only_the_rest';
const CAUSE = I + 'CauseAdjustedPerformanceTests.Paid_time_downtime_and_first_pass_quality_are_attributed_from_verified_evidence';
const CALC = I + 'AttendanceReliabilityCalculatorTests.';
const READY = P + 'ReadinessTests.Machine_specific_practical_authorization_is_required_in_addition_to_the_qualification';
const TRAIN = I + 'TrainingRequestTests.New_hires_supervisors_expiring_qualifications_and_approved_packets_open_requests_that_trainers_schedule';
const RECOVERY = P + 'WorkforceRecoveryEngineTests.';
const REWORK = I + 'ReworkPlanTests.Approved_rework_adds_operations_inspection_labor_demand_and_training_without_changing_the_disposition';
const V319 = I + 'V319Tests.Notification_rules_deduplicate_resolve_respect_current_program_access_and_delivery_failure_is_separate';
export const sprint10Evidence = {
  'WW-S10-01': ['Covered', [I + 'Phase4Tests.Spec_81_approved_pto_surfaces_before_the_absence_with_alternatives', P + 'SchedulingTests.Spec_81_future_pto_interrupts_work_and_makes_the_order_late',
    'ui:workforce-intelligence.test.mjs upcoming approved time away warns when it removes the last scheduled qualified machine option'], 'Proven on the slitting fixture; the CNC setup roster is the same rule.'],
  'WW-S10-02': ['Covered', [RECOVERY + 'Ranking_returns_only_currently_qualified_workers_and_is_stable', RECOVERY + 'Projected_shift_preview_reports_open_source_work_impact_and_an_ordered_backfill_option', 'replay S02'], ''],
  'WW-S10-03': ['Covered', [CALC + 'Missing_punch_is_pending_review_and_never_inferred_as_a_no_show', CALC + 'Callouts_and_no_shows_are_separate_and_only_explicit_verified_no_shows_enter_the_denominator',
    I + 'DailyMissionAttendanceTests.Current_shift_mission_keeps_a_missing_clock_in_pending_and_aggregated'], ''],
  'WW-S10-04': ['Covered', ['replay S03', P + 'CompositionTests.One_person_with_every_qualification_never_counts_as_two'], ''],
  'WW-S10-05': ['Covered', [I + 'HistoricalExecutionGovernanceTests.An_authenticated_supervisor_cannot_override_their_expired_operator_qualification',
    P + 'SkillEvidenceTests.An_expired_qualification_or_missing_refresher_is_a_hard_stop_whatever_the_history', P + 'ShopFloorEngineTests.Expired_qualification_does_not_count', 'replay S05'], ''],
  'WW-S10-06': ['Covered', [READY, RECOVERY + 'Machine_authorization_is_a_hard_gate_for_recovery_candidates',
    'ui:workforce-intelligence.test.mjs machine-specific authorization is separate from qualification'], 'The readiness test asserts authorization on one machine does not authorize its sibling.'],
  'WW-S10-07': ['Covered', [READY, 'ui:workforce-intelligence.test.mjs machine-specific authorization is separate from qualification'], ''],
  'WW-S10-08': ['Covered', ['ui:workforce-intelligence.test.mjs qualification summary honors expiry windows', TRAIN, 'ui:performance-cards.test.mjs green, yellow and red follow the published thresholds'],
    'The configured window is the qualification validity/expiry and refresher policy; the expiry scan opens one reassessment request.'],
  'WW-S10-09': ['Partial', [CAUSE], 'Employee-attributable FPY stays N/A without a Quality-verified attribution, so no operator fault is assumed. There is no quality trend view that shows equipment and material confounders.'],
  'WW-S10-10': ['Covered', [I + 'LaborStandardReviewTests.A_standard_under_review_is_never_a_worker_score_until_an_engineer_confirms_it',
    I + 'ProductionLaborMetricsTests.Released_routing_setup_and_run_targets_compare_with_person_hours_without_claiming_unverified_efficiency', CAUSE],
    'A standard implausible against recorded labor is flagged; once a supervisor marks it for review it leaves efficiency until an engineer confirms it (counts again) or records it corrected (stays out).'],
  'WW-S10-11': ['Covered', [CAUSE, I + 'ProductionLaborMetricsTests.Linked_machine_stoppages_are_reported_as_context_without_becoming_labor_or_proven_causes',
    P + 'SkillEvidenceTests.Machine_downtime_is_excluded_from_the_run_rate_and_overlapping_labor_counts_once'], ''],
  'WW-S10-12': ['Partial', [TRAIN], 'A packet needs a rationale and expected plant benefit and is decided by a human; prerequisites are checked for trainers and authorizations, but are not listed on the packet before nomination.'],
  'WW-S10-13': ['Covered', [RECOVERY + 'Ranked_two_step_option_backfills_the_source_work_before_moving_the_operator',
    P + 'ReallocationTests.A_cascading_plan_rechecks_the_next_move_against_the_previous_move_and_does_not_change_source', P + 'CompositionTests.Every_applicable_rule_must_pass'], 'Proven on the slitting/paint fixture.'],
  'WW-S10-14': ['Covered', [REWORK, I + 'V35Tests.Rework_and_repair_take_output_back_without_rewriting_history_and_quantity_states_follow', 'ui:rework-plans.test.mjs the rework row shows what its plans add'],
    'An approved rework plan adds solder and retest operations with their own labor standard and qualifications; the NCR disposition, rework record and existing lines are unchanged.'],
  'WW-S10-15': ['Covered', [I + 'AttendanceSixtyShiftHistoryTests.Sixty_reviewed_shift_history_preserves_denominator_and_protected_exclusions', CALC + 'Quarter_excludes_verified_pto_and_protected_leave_and_explains_the_denominator'], ''],
  'WW-S10-16': ['Covered', [I + 'AttendanceSelfCorrectionTests.Employee_can_request_only_own_attendance_correction_and_reviewer_keeps_independent_decision', CALC + 'Approved_correction_supersedes_prior_event_without_deleting_it'], ''],
  'WW-S10-17': ['Covered', [I + 'AttendanceQuarterTests.The_summary_rolls_over_on_the_sites_quarter_start_and_keeps_the_previous_quarter_queryable',
    I + 'AttendanceQuarterTests.Quarters_follow_the_calendar_and_only_started_quarters_can_be_named', 'ui:attendance-period.test.mjs calendar quarters are asked of the server in plant time'],
    'The summary defaults to the current calendar quarter at the site and starts again at local midnight on the quarter\'s first day; the previous quarter and any earlier quarter by name stay queryable.'],
  'WW-S10-18': ['Covered', [CALC + 'Effective_policy_versions_apply_to_their_own_scheduled_dates', CALC + 'Overlapping_policy_versions_make_the_result_unavailable'], ''],
  'WW-S10-19': ['Covered', ['ui:performance-cards.test.mjs green, yellow and red follow the published thresholds and say why; thin or unverified evidence stays gray'], 'Forge never ranks employees; thin evidence is gray, not scored.'],
  'WW-S10-20': ['Covered', [TRAIN], 'The packet author cannot decide it; an approval only opens a training request. Forge holds no compensation records to change.'],
  'WW-S10-21': ['Covered', [I + 'AttendanceSourceImportTests.External_arrival_import_is_pending_deduplicated_private_and_requires_independent_review', TRAIN, I + 'AttendanceSixtyShiftHistoryTests.Sixty_reviewed_shift_history_preserves_denominator_and_protected_exclusions'],
    'Every workforce, attendance and competency regression runs with development sign-in and no Nexus connection; Nexus is only an optional identity provider and import source.'],
  'WW-S10-22': ['Covered', [CAUSE, I + 'AttendanceSelfCorrectionTests.Employee_can_request_only_own_attendance_correction_and_reviewer_keeps_independent_decision',
    'ui:workforce-intelligence-page.test.mjs keeps absence reasons private', I + 'DemoTenantTests.The_demo_clock_drives_expiry_and_a_reset_restores_the_baseline_and_archives_the_rest'],
    'Each plant (and the demo) is its own database, so tenant isolation is the database boundary; role checks are asserted per section.'],
  'WW-S10-23': ['Covered', [MAT_A], ''],
  'WW-S10-24': ['Partial', [I + 'V5RejectionSplitTests.Part_of_a_rejection_goes_back_and_the_rest_needs_its_own_decision_while_an_open_return_only_goes_down',
    'Forge.Simulation.Tests.SimulationScenarios.S10b_split_dispositions_account_for_every_rejected_unit', P + 'GenealogyGraphTests.Issued_equals_consumed_returned_scrap_loss_remnant_and_remaining_within_tolerance'],
    'Quantities and genealogy reconcile; cost reconciliation of a split disposition is not asserted.'],
  'WW-S10-25': ['Covered', [MAT_A], ''],
  'WW-S10-26': ['Covered', [MAT + 'A_short_request_issues_only_what_is_there_and_backorders_the_rest_until_stock_arrives', MAT_B],
    'Allocating a 20 request against 12 on the shelf allocates 12 and backorders 8 as a linked task: still requested demand, planners notified, allocated when stock arrives.'],
  'WW-S10-27': ['Covered', [MAT_B, I + 'InventoryScrapRequestTests.Concurrent_requests_never_claim_the_same_units', 'Forge.Simulation.Tests.SimulationScenarios.S16_concurrent_transactions_on_the_same_material_apply_exactly_once'], ''],
  'WW-S10-28': ['Covered', [MAT_A], 'The PostgreSQL test found additional-material requests could not be saved; fixed by inventory migration 0029.'],
  'WW-S10-29': ['Covered', [REWORK], 'Approval adds the connector (released demand MRP plans to buy), an inspection step, the labor standard and training requests for the people named; the proposer cannot approve.'],
  'WW-S10-30': ['Covered', [I + 'MaterialRiskNotificationTests.A_projected_shortage_notifies_each_permitted_planner_once_with_a_link_and_clears_when_covered', V319],
    'The hourly material risk check raises one planner notice per order at risk (shortfall, overdue or late supply), deep-linked to the order and limited to planners who may see it; email once an administrator enables it.'],
  'WW-S10-31': ['Covered', [I + 'NotificationEscalationTests.An_unfilled_request_escalates_once_at_the_threshold_and_resolves_when_fulfilled', V319],
    'Escalation is an email to the escalation role at the configured threshold; in-app alerts are not re-sent.'],
  'WW-S10-32': ['Covered', [V319, 'Forge.Simulation.Tests.SimulationScenarios.S15_lost_responses_and_concurrent_retries_apply_once'], ''],
  'WW-S10-33': ['Covered', [MAT_B, I + 'SelectionHoldGovernanceTests.Selection_excludes_direct_and_descendant_issue_holds_preserves_independent_material_and_restores_after_release'], ''],
  'WW-S10-34': ['Covered', [MAT_A, I + 'AttendanceReplayAuthorityTests.Shared_terminal_clock_in_retry_is_bound_to_current_badge_owner_without_a_second_attendance_or_secret_record'], ''],
  'WW-S10-35': ['Covered', [MAT_B, 'Forge.Integration.Tests.MaterialTaskDemandTests.Cancelled_tasks_release_their_unfilled_demand'], ''],
  'WW-S10-36': ['Covered', [CAUSE, P + 'WorkforcePerformanceCalculationsTests.Machine_downtime_is_limited_to_assignment_and_paid_shift_overlap_after_prior_classification',
    P + 'RecoveryOptionTests.Selected_maintenance_window_preserves_observed_failure_and_cannot_erase_source_downtime'], ''],
  'WW-S10-37': ['Partial', [P + 'RecoveryOptionTests.Additional_shift_needs_selected_workers_and_cannot_create_qualification',
    I + 'RecoveryGovernanceTests.Machine_failure_changes_capacity_goals_alerts_and_fact_evaluated_recovery_without_auto_commit'],
    'Recovery proposals check qualified people and never commit on their own; there is no replacement manufacturing order proposal type.'],
  'WW-S10-38': ['Covered', [MAT_A, I + 'SiteLimitTests.A_database_runs_one_site_until_multi_site_operation_is_turned_on',
    I + 'HorizonLensTests.All_seven_lenses_share_archived_scope_counts_and_keep_old_projection_and_commitment_history', I + 'V310Tests.Program_holds_are_hidden_and_cannot_be_released_without_program_access', CAUSE],
    'Operator, terminal, stockroom, planner and Quality steps and program scope are enforced; a database runs one site (owner decision) and facility-scoped permissions already bound readers to a site; tenancy is the database.'],
};

const STATUS_TEXT = { Covered: 'Covered by passing regression and replay checks', Partial: 'Partially covered; gap named in note', Open: 'Open: not implemented' };

export const sprint10ScenarioCatalog = [
  ...core.map(([title, domain, acceptance], index) => ({
    code: `WW-S10-${String(index + 1).padStart(2, '0')}`,
    phase: 'core', title, domain, acceptance,
  })),
  ...material.map(([title, domain, acceptance], index) => ({
    code: `WW-S10-${String(index + 23).padStart(2, '0')}`,
    phase: 'material-extension', title, domain, acceptance,
  })),
].map(scenario => {
  const [status, checks, note] = sprint10Evidence[scenario.code];
  return { ...scenario, executionStatus: STATUS_TEXT[status], evidence: { status, checks, note } };
});

export function validateSprint10ScenarioCatalog(catalog = sprint10ScenarioCatalog) {
  if (!Array.isArray(catalog) || catalog.length !== 38) throw new Error('Sprint 10 acceptance requires exactly 38 scenario definitions.');
  const expected = Array.from({ length: 38 }, (_, index) => `WW-S10-${String(index + 1).padStart(2, '0')}`);
  if (catalog.some((scenario, index) => scenario.code !== expected[index] || !scenario.title || !scenario.domain || !scenario.acceptance || !scenario.executionStatus))
    throw new Error('Sprint 10 scenarios must be complete, unique, and ordered by acceptance ID.');
  if (catalog.slice(0, 22).some(scenario => scenario.phase !== 'core') || catalog.slice(22).some(scenario => scenario.phase !== 'material-extension'))
    throw new Error('The Sprint 10 catalog must preserve its 22 core and 16 material-extension cases.');
  for (const scenario of catalog) {
    const evidence = scenario.evidence;
    if (!evidence || !STATUS_TEXT[evidence.status]) throw new Error(`${scenario.code} needs a Covered, Partial or Open evidence status.`);
    if (evidence.status !== 'Open' && evidence.checks.length === 0) throw new Error(`${scenario.code} claims coverage without naming a check.`);
    if (evidence.status !== 'Covered' && !evidence.note) throw new Error(`${scenario.code} must name its gap.`);
  }
  return true;
}
