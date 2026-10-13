using Forge.Planning.Analyst;

namespace Forge.Planning.Tests;

/// <summary>Spec §58–60, §66: Forge's own language parser, deterministic and explicit about what it understood.</summary>
public class AnalystParserTests
{
    private static readonly Guid Sarah = Guid.Parse("018f0000-0000-7000-8000-000000000001");
    private static readonly Guid Falcon = Guid.Parse("018f0000-0000-7000-8000-0000000000f1");

    private static readonly AnalystVocabulary Vocabulary = new(
        [new("SLITTER-A", "Slitter A", "SLIT-WC"), new("SLITTER-B", "Slitter B", "SLIT-WC"), new("LASER-1", "Laser cutter", "FAB-WC")],
        [new("SLIT-WC", "Slitting"), new("PAINT-WC", "Paint"), new("FAB-WC", "Fabrication")],
        [new("DAY", "Day shift", new TimeOnly(6, 0), 480), new("SWING", "Swing shift", new TimeOnly(14, 0), 480), new("NIGHT", "Night shift", new TimeOnly(22, 0), 480)],
        ["ROLL-100", "PANEL-500"],
        [new(Falcon, "FALCON", "Program Falcon")],
        [new(Sarah, "Sarah Lopez")],
        new HashSet<long> { 10001, 48172 });

    // Wednesday 2026-09-30, 15:30 at the site; the site is UTC-4.
    private static readonly AnalystClock Clock = new(
        new DateTimeOffset(2026, 9, 30, 19, 30, 0, TimeSpan.Zero),
        new DateOnly(2026, 9, 30),
        new TimeOnly(15, 30),
        (d, t) => new DateTimeOffset(d.Year, d.Month, d.Day, t.Hour, t.Minute, 0, TimeSpan.FromHours(-4)).ToUniversalTime());

    private static Interpretation Parse(string q) => QueryParser.Parse(q, Vocabulary, Clock);

    [Fact]
    public void Why_is_an_order_late_names_the_order()
    {
        var i = Parse("Why is Order 48172 late?");
        Assert.Equal(AnalystIntent.OrderStatus, i.Intent);
        Assert.Equal(48172, i.OrderNumber);
        Assert.Equal(AnalystConfidence.High, i.Confidence);
        Assert.Contains("order 48172", i.Understood, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bare_number_is_an_order_only_when_such_an_order_exists()
    {
        Assert.Equal(10001, Parse("when will 10001 finish").OrderNumber);
        Assert.Null(Parse("what is late in the next 9999 days").OrderNumber);
    }

    [Theory]
    [InlineData("What is late?", AnalystIntent.LateOrders)]
    [InlineData("Which orders are at risk", AnalystIntent.LateOrders)]
    [InlineData("What machines are unavailable?", AnalystIntent.UnavailableMachines)]
    [InlineData("What is preventing SLITTER-A from running?", AnalystIntent.MachineBlockers)]
    [InlineData("Why can't Slitter B run", AnalystIntent.MachineBlockers)]
    [InlineData("Who is qualified to work Slitting tonight?", AnalystIntent.QualifiedPeople)]
    [InlineData("What changed since yesterday?", AnalystIntent.WhatChanged)]
    [InlineData("Which orders are affected by Sarah's PTO?", AnalystIntent.PersonImpact)]
    [InlineData("What happens if SLITTER-A goes down tomorrow?", AnalystIntent.Simulate)]
    [InlineData("Show orders affected by Paint capacity", AnalystIntent.AffectedOrders)]
    [InlineData("Which certifications expire in the next 90 days?", AnalystIntent.Report)]
    [InlineData("Compare first and second shift output", AnalystIntent.Report)]
    [InlineData("Brief me - second shift", AnalystIntent.Brief)]
    [InlineData("What is the capital of France?", AnalystIntent.Unsupported)]
    public void Spec_58_questions_map_to_intents(string question, AnalystIntent intent) =>
        Assert.Equal(intent, Parse(question).Intent);

    [Theory]
    [InlineData("Show the document register", "document-register")]
    [InlineData("Which drawings need review?", "document-register")]
    [InlineData("show unclassified items", "item-classification")]
    [InlineData("Show open engineering changes", "engineering-changes")]
    [InlineData("Show open change actions", "change-actions")]
    [InlineData("Show proposed alternates", "alternates")]
    [InlineData("Which parts are obsolete?", "obsolescence")]
    [InlineData("Show restricted assets", "assets")]
    [InlineData("Which work orders are blocked?", "maintenance-readiness")]
    [InlineData("Show maintenance readiness", "maintenance-readiness")]
    [InlineData("Show proposed outages", "outages")]
    [InlineData("Show facility project lines", "project-material")]
    [InlineData("Show project material", "project-material")]
    [InlineData("Show the as-built record", "as-built")]
    [InlineData("What is coming up in the next 30 days?", "dated-events")]
    [InlineData("Where is lot L-104?", "stock-positions")]
    [InlineData("What material is at outside processors?", "stock-positions")]
    [InlineData("Show stock at the point of use", "stock-positions")]
    [InlineData("What was roll R-100 split into?", "lot-lineage")]
    [InlineData("Show lot splits and merges", "lot-lineage")]
    [InlineData("What is the lineage of lot CU-1001-A?", "lot-lineage")]
    [InlineData("Which machines were starved this week?", "stoppages")]
    [InlineData("Show changeovers against expected", "stoppages")]
    [InlineData("Which stoppages have no root cause?", "stoppages")]
    [InlineData("How much of order 1042 is in rework?", "quantity-states")]
    [InlineData("Show order quantity states", "quantity-states")]
    [InlineData("Which lots are expired?", "shelf-life")]
    [InlineData("Which goals are at risk?", "goal-attainment")]
    [InlineData("Which goals are behind plan?", "goal-attainment")]
    [InlineData("Show goal attainment", "goal-attainment")]
    [InlineData("Which tools are out for service?", "tool-status")]
    [InlineData("Which gauges are due for calibration?", "tool-status")]
    [InlineData("Which blades are out for sharpening?", "tool-status")]
    [InlineData("Was CAL-1 calibrated when it was used?", "tool-use-history")]
    [InlineData("What happened last shift?", "production-output")]
    [InlineData("What became of the material issued to order 10001?", "material-reconciliation")]
    [InlineData("Which operations can run now?", "executable-operations")]
    [InlineData("Which operations are on hold?", "executable-operations")]
    [InlineData("Which operations are blocked by tooling?", "executable-operations")]
    [InlineData("Which receipts are waiting for inspection?", "receiving-inspection")]
    [InlineData("Which FAIs are pending determination?", "receiving-inspection")]
    [InlineData("What is on hold?", "holds")]
    [InlineData("Which quality holds are open?", "holds")]
    [InlineData("Who is present?", "presence")]
    [InlineData("Who is scheduled but not clocked in?", "presence")]
    [InlineData("What needs a retest?", "shelf-life")]
    [InlineData("Show shelf life and aging", "shelf-life")]
    [InlineData("Which lots are overdue for retest?", "shelf-life")]
    [InlineData("Trace roll R-100", "genealogy-trace")]
    [InlineData("What became of lot L-7?", "genealogy-trace")]
    [InlineData("Where did roll CU-1001-A come from?", "genealogy-trace")]
    [InlineData("Show unexplained material", "material-reconciliation")]
    [InlineData("Show material reconciliation", "material-reconciliation")]
    [InlineData("Show remnants", "material-reconciliation")]
    [InlineData("Show material flow", "material-flow")]
    [InlineData("Show what is in the warehouse", "material-flow")]
    [InlineData("What is staged for slitting?", "material-flow")]
    [InlineData("Which cycle counts are due?", "counts-due")]
    [InlineData("Which training acknowledgments are open?", "change-actions")]
    [InlineData("Show orders with drawing mismatches", "documentation-status")]
    public void V2_questions_map_to_their_reports(string question, string report)
    {
        var parsed = Parse(question);
        Assert.Equal(AnalystIntent.Report, parsed.Intent);
        Assert.Equal(report, parsed.Report!.ReportId);
    }

    [Fact]
    public void Historical_tool_question_filters_to_the_named_tool_and_as_of_use()
    {
        var vocabulary = Vocabulary with { Tools = ["CAL-1"] };
        var parsed = QueryParser.Parse("Was CAL-1 calibrated when it was used?", vocabulary, Clock);

        Assert.Equal(AnalystIntent.Report, parsed.Intent);
        Assert.Equal("tool-use-history", parsed.Report!.ReportId);
        Assert.Equal("CAL-1", parsed.Report.Parameters.Search);
    }

    [Fact]
    public void Last_shift_resolves_to_the_latest_completed_configured_shift()
    {
        var parsed = Parse("What happened last shift?");

        Assert.Equal("production-output", parsed.Report!.ReportId);
        Assert.Equal("DAY", parsed.Report.Parameters.Shift);
        Assert.Equal(new DateOnly(2026, 9, 30), parsed.Report.Parameters.From);
        Assert.Equal(new DateOnly(2026, 9, 30), parsed.Report.Parameters.To);
    }

    [Fact]
    public void Recovery_options_question_uses_private_scenario_intent()
    {
        var parsed = Parse("What are my recovery options?");

        Assert.Equal(AnalystIntent.RecoveryOptions, parsed.Intent);
        Assert.False(parsed.NeedsConfirmation);
    }

    [Fact]
    public void Machines_and_work_centers_are_recognized_by_code_name_and_word()
    {
        var blocked = Parse("What is preventing Slitter A from running?");
        Assert.Equal("SLITTER-A", blocked.Machine);
        Assert.Null(blocked.WorkCenter);

        var people = Parse("Who is qualified to work Slitting tonight?");
        Assert.Equal("SLIT-WC", people.WorkCenter);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero), people.At);

        var paint = Parse("Show orders affected by Paint capacity");
        Assert.Equal("PAINT-WC", paint.WorkCenter);
        Assert.Equal("Capacity", paint.Category);
    }

    [Fact]
    public void An_ambiguous_machine_is_asked_about_not_guessed()
    {
        var i = Parse("What is preventing machine 1 from running?");
        Assert.Equal("LASER-1", i.Machine);

        var vocabulary = Vocabulary with { Machines = [new("SLITTER-A", "Slitter A", "SLIT-WC"), new("PRESS-A", "Press A", "FAB-WC")] };
        var ambiguous = QueryParser.Parse("What is preventing machine A from running?", vocabulary, Clock);
        Assert.Null(ambiguous.Machine);
        Assert.True(ambiguous.NeedsConfirmation);
        Assert.Contains(ambiguous.Ambiguities, a => a.Contains("SLITTER-A or PRESS-A", StringComparison.Ordinal));
    }

    [Fact]
    public void People_are_only_recognized_when_the_reader_may_see_them()
    {
        Assert.Equal(Sarah, Parse("Which orders are affected by Sarah's PTO?").PersonId);
        var hidden = QueryParser.Parse("Which orders are affected by Sarah's PTO?", Vocabulary with { People = [] }, Clock);
        Assert.Null(hidden.PersonId);
    }

    [Fact]
    public void Simulation_reads_the_machine_time_and_duration()
    {
        var i = Parse("What happens if SLITTER-A goes down tomorrow for 8 hours?");
        Assert.Equal("SLITTER-A", i.Machine);
        Assert.Equal(8m, i.Hours);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), i.At);
        Assert.Contains("Scenario: SLITTER-A unavailable for 8 h", i.Understood, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("downtime last week", "2026-09-21", "2026-09-27")]
    [InlineData("downtime this month", "2026-09-01", "2026-09-30")]
    [InlineData("downtime last month", "2026-08-01", "2026-08-31")]
    [InlineData("downtime in the last 14 days", "2026-09-17", "2026-09-30")]
    [InlineData("downtime yesterday", "2026-09-29", "2026-09-29")]
    [InlineData("downtime September 21-27", "2026-09-21", "2026-09-27")]
    [InlineData("downtime from 2026-09-01 to 2026-09-10", "2026-09-01", "2026-09-10")]
    [InlineData("certifications expiring in the next 90 days", "2026-09-30", "2026-12-28")]
    public void Relative_and_absolute_periods(string text, string from, string to)
    {
        var (period, _) = QueryParser.ParsePeriod(" " + QueryParser.Normalize(text) + " ", Clock);
        Assert.NotNull(period);
        Assert.Equal(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), period.From);
        Assert.Equal(DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture), period.To);
    }

    [Fact]
    public void Since_a_time_of_day_is_today_when_it_has_passed()
    {
        var (_, at) = QueryParser.ParsePeriod(" what changed since 6am ", Clock);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero), at);
        var (_, evening) = QueryParser.ParsePeriod(" what changed since 8 pm ", Clock);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero), evening);
    }

    [Fact]
    public void Spec_66_report_requests_become_report_definitions_for_confirmation()
    {
        var i = Parse("Create an Excel report showing certifications expiring in the next 90 days grouped by work center");
        Assert.Equal(AnalystIntent.Report, i.Intent);
        Assert.True(i.NeedsConfirmation);
        var r = i.Report!;
        Assert.Equal("qualifications-expiring", r.ReportId);
        Assert.Equal("xlsx", r.Output);
        Assert.Equal("workCenter", r.Parameters.GroupBy);
        Assert.Equal(new DateOnly(2026, 12, 28), r.Parameters.To);
        Assert.Contains("Excel", i.Understood, StringComparison.Ordinal);

        var downtime = Parse("Export downtime for Slitter B last week as CSV by machine");
        Assert.Equal("downtime", downtime.Report!.ReportId);
        Assert.Equal("SLITTER-B", downtime.Report.Parameters.Machine);
        Assert.Equal("machine", downtime.Report.Parameters.GroupBy);
        Assert.Equal("csv", downtime.Report.Output);
        Assert.Equal(new DateOnly(2026, 9, 21), downtime.Report.Parameters.From);
    }

    [Fact]
    public void Compare_shifts_groups_output_by_shift_and_says_what_was_clamped()
    {
        var i = Parse("Compare first and second shift output for the last 12 months");
        var r = i.Report!;
        Assert.Equal("production-output", r.ReportId);
        Assert.Equal("shift", r.Parameters.GroupBy);
        Assert.Null(r.Parameters.Shift);
        Assert.Contains(r.Notes, n => n.Contains("at most", StringComparison.Ordinal));
    }

    [Fact]
    public void A_filter_the_report_lacks_is_reported_not_silently_dropped()
    {
        var i = Parse("Show nonconformances for SLITTER-A this month");
        Assert.Equal("ncr-summary", i.Report!.ReportId);
        Assert.Contains(i.Report.Notes, n => n.Contains("no machine filter", StringComparison.Ordinal));
    }

    [Fact]
    public void Shifts_by_ordinal_or_name()
    {
        Assert.Equal("SWING", Parse("brief me second shift").ShiftCode);
        Assert.Equal("NIGHT", Parse("Brief me — Night shift").ShiftCode);
        var none = QueryParser.Parse("brief me fourth shift", Vocabulary, Clock);
        Assert.Null(none.ShiftCode);
    }

    [Fact]
    public void Program_codes_are_recognized_only_when_visible()
    {
        Assert.Equal(Falcon, Parse("what is late on FALCON").ProgramId);
        Assert.Null(QueryParser.Parse("what is late on FALCON", Vocabulary with { Programs = [] }, Clock).ProgramId);
    }

    [Fact]
    public void General_knowledge_is_declined()
    {
        var i = Parse("Write me a poem about the ocean");
        Assert.Equal(AnalystIntent.Unsupported, i.Intent);
        Assert.Equal(AnalystConfidence.Low, i.Confidence);
    }

    [Fact]
    public void What_changed_on_a_code_names_the_subject()
    {
        var i = Parse("What changed on SL-02?");
        Assert.Equal(AnalystIntent.WhatChanged, i.Intent);
        Assert.Equal("SL-02", i.Subject);
        Assert.Null(i.SubjectKind);
        Assert.StartsWith("What changed on SL-02", i.Understood, StringComparison.Ordinal);

        var project = Parse("What changed on project prj-7 this month?");
        Assert.Equal(("PRJ-7", "project"), (project.Subject, project.SubjectKind));
        Assert.NotNull(project.Period);

        Assert.Equal(("ECO-12", "change"), (Parse("what has changed with ECO ECO-12").Subject, Parse("what has changed with ECO ECO-12").SubjectKind));
    }

    [Fact]
    public void What_changed_falls_back_to_a_recognized_machine_and_never_takes_times_for_codes()
    {
        Assert.Equal(("SLITTER-A", "machine"), (Parse("what changed on slitter a").Subject, Parse("what changed on slitter a").SubjectKind));
        Assert.Null(Parse("What changed since yesterday?").Subject);
        Assert.Null(Parse("what changed since 6am").Subject);
        Assert.Null(Parse("what changed for 3 days").Subject);
    }

    [Fact]
    public void Stock_position_questions_search_for_the_lot_or_the_custody_asked_about()
    {
        Assert.Equal("L-104", Parse("Where is lot L-104?").Report!.Parameters.Search);
        Assert.Equal("Outside processor", Parse("What material is at outside processors?").Report!.Parameters.Search);
        Assert.Equal("In transit", Parse("Show material in transit").Report!.Parameters.Search);
        Assert.Null(Parse("Show stock at the point of use").Report!.Parameters.Search);
    }

    [Fact]
    public void Lineage_questions_search_for_the_instance_asked_about()
    {
        Assert.Equal("R-100", Parse("What was roll R-100 split into?").Report!.Parameters.Search);
        Assert.Equal("CU-1001-A", Parse("What is the lineage of lot CU-1001-A?").Report!.Parameters.Search);
        Assert.Null(Parse("Show lot splits and merges").Report!.Parameters.Search);
    }

    [Fact]
    public void Trace_questions_name_the_instance_to_trace_from()
    {
        Assert.Equal("R-100", Parse("Trace roll R-100").Report!.Parameters.Search);
        Assert.Equal("CU-1001-A", Parse("Where did roll CU-1001-A come from?").Report!.Parameters.Search);
    }

    [Fact]
    public void Stoppage_and_quantity_questions_carry_the_category_or_order_asked_about()
    {
        Assert.Equal("Starved", Parse("Which machines were starved this week?").Report!.Parameters.Status);
        Assert.Equal("Changeover", Parse("Show changeovers against expected").Report!.Parameters.Status);
        Assert.Null(Parse("Which stoppages have no root cause?").Report!.Parameters.Status);
        Assert.Equal("1042", Parse("How much of order 1042 is in rework?").Report!.Parameters.Search);
    }

    [Fact]
    public void Material_reconciliation_questions_scope_an_exact_order_and_actual_remnants()
    {
        var material = Parse("What became of the material issued to order 1042?").Report!;
        Assert.Equal("material-reconciliation", material.ReportId);
        Assert.Equal("order:1042", material.Parameters.Search);
        Assert.Equal("Remnants", Parse("Show remnants").Report!.Parameters.Status);
        Assert.Equal(("order:1042", "Remnants"),
            (Parse("Show remnants from order 1042").Report!.Parameters.Search, Parse("Show remnants from order 1042").Report!.Parameters.Status));
        Assert.Null(Parse("Show material reconciliation").Report!.Parameters.Search);
    }

    [Fact]
    public void Shelf_life_questions_carry_the_status_asked_about()
    {
        Assert.Equal("Expired", Parse("Which lots are expired?").Report!.Parameters.Status);
        Assert.Equal("Expiring", Parse("Which lots are expiring soon?").Report!.Parameters.Status);
        Assert.Equal("RetestOverdue", Parse("Which lots are overdue for retest?").Report!.Parameters.Status);
        Assert.Null(Parse("Show shelf life and aging").Report!.Parameters.Status);
        Assert.Equal("qualifications-expiring", Parse("Which certifications expire in the next 90 days?").Report!.ReportId);
    }

    [Fact]
    public void Goal_questions_keep_behind_plan_apart_from_at_risk()
    {
        Assert.Equal("AtRisk", Parse("Which goals are at risk?").Report!.Parameters.Status);
        Assert.Equal("BehindPlan", Parse("Which goals are behind plan?").Report!.Parameters.Status);
        Assert.Equal("Late", Parse("Which goals are late?").Report!.Parameters.Status);
        Assert.Null(Parse("Show goal attainment").Report!.Parameters.Status);
        Assert.Equal("schedule-attainment", Parse("Show schedule attainment").Report!.ReportId);
    }

    [Fact]
    public void Tool_questions_ask_for_tools_out_for_service()
    {
        Assert.Equal("OutForService", Parse("Which tools are out for service?").Report!.Parameters.Status);
        Assert.Null(Parse("Which gauges are due for calibration?").Report!.Parameters.Status);
        Assert.Equal("qualifications-expiring", Parse("Which certifications expire in the next 90 days?").Report!.ReportId);
    }

    [Fact]
    public void Executable_and_presence_questions_ask_for_the_constraint_or_level()
    {
        Assert.Equal("Holds", Parse("Which operations are on hold?").Report!.Parameters.Status);
        Assert.Equal("Tooling", Parse("Which operations are blocked by tooling?").Report!.Parameters.Status);
        Assert.Equal("Executable", Parse("Which operations can run now?").Report!.Parameters.Status);
        Assert.Equal("Training", Parse("Which operations are waiting for training?").Report!.Parameters.Status);
        Assert.Equal("Scheduled", Parse("Who is scheduled but not clocked in?").Report!.Parameters.Status);
        Assert.Null(Parse("Who is present?").Report!.Parameters.Status);
        Assert.Equal("stock-positions", Parse("Show stock at the point of use").Report!.ReportId);
        Assert.Equal("Quality", Parse("Which quality holds are open?").Report!.Parameters.Status);
        Assert.Equal("Procurement", Parse("Show procurement holds").Report!.Parameters.Status);
        Assert.Equal("PendingDetermination", Parse("Which FAIs are pending determination?").Report!.Parameters.Status);
    }
}
