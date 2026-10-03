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
    public void V2_questions_map_to_their_reports(string question, string report)
    {
        var parsed = Parse(question);
        Assert.Equal(AnalystIntent.Report, parsed.Intent);
        Assert.Equal(report, parsed.Report!.ReportId);
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
}
