using Forge.Planning.Reporting;

namespace Forge.Planning.Analyst;

/// <summary>What a question asks for. Spec §59 intents, mapped to what Forge can answer from its records.</summary>
public enum AnalystIntent
{
    /// <summary>Not a question Forge can answer from its records.</summary>
    Unsupported,

    /// <summary>"Why is order 48172 late?", "When will order 48172 finish?"</summary>
    OrderStatus,

    /// <summary>"What is late?", "Which orders are at risk?"</summary>
    LateOrders,

    /// <summary>"What machines are unavailable?"</summary>
    UnavailableMachines,

    /// <summary>"What is preventing SLITTER-A from running?"</summary>
    MachineBlockers,

    /// <summary>"Who is qualified to work Slitting tonight?"</summary>
    QualifiedPeople,

    /// <summary>"What changed since yesterday?"</summary>
    WhatChanged,

    /// <summary>"Show orders affected by Paint capacity", "Which orders are affected by SLITTER-B?"</summary>
    AffectedOrders,

    /// <summary>"Which orders are affected by Sarah's absence?" — a scenario with the person available.</summary>
    PersonImpact,

    /// <summary>"What happens if SLITTER-A goes down tomorrow?" — a scenario, never saved.</summary>
    Simulate,

    /// <summary>A standard report, from "show", "list", "compare", "generate", "export" requests.</summary>
    Report,

    /// <summary>"Brief me", "Brief me — second shift".</summary>
    Brief,

    /// <summary>"What documentation applies to order 48172?" (V2.3).</summary>
    OrderDocumentation,
}

public enum AnalystConfidence
{
    High,
    Medium,
    Low,
}

/// <summary>A date range in site time, both ends inclusive, with the words it came from.</summary>
public sealed record AnalystPeriod(DateOnly From, DateOnly To, string Label);

/// <summary>A report the question maps to, shown for confirmation before it runs (spec §60).</summary>
public sealed record AnalystReportRequest(string ReportId, string Title, ReportParameters Parameters, string Output, IReadOnlyList<string> Notes);

/// <summary>
/// What Forge understood. Shown to the person with every answer, so a wrong reading is visible rather
/// than silently guessed. Entities are business codes (order numbers, machine and work-center codes).
/// </summary>
public sealed record Interpretation(
    string Question,
    AnalystIntent Intent,
    string Understood,
    AnalystConfidence Confidence,
    long? OrderNumber,
    string? Machine,
    string? WorkCenter,
    string? PartNumber,
    Guid? ProgramId,
    Guid? PersonId,
    string? Category,
    string? ShiftCode,
    AnalystPeriod? Period,
    DateTimeOffset? At,
    decimal? Hours,
    AnalystReportRequest? Report,
    IReadOnlyList<string> Ambiguities)
{
    public bool NeedsConfirmation => Intent == AnalystIntent.Report || Ambiguities.Count > 0;
}

/// <summary>One finding in an answer, with the record it comes from.</summary>
public sealed record AnalystFact(string Text, string? Trace = null, string? Tone = null);

/// <summary>
/// An answer: a headline, facts each traceable to a record, an optional table, and what Forge could
/// not say. Every statement is computed by Forge's services; nothing here is generated text.
/// </summary>
public sealed record AnalystAnswer(
    Interpretation Interpretation,
    string Headline,
    IReadOnlyList<AnalystFact> Facts,
    IReadOnlyList<ReportColumn>? Columns,
    IReadOnlyList<ReportRow>? Rows,
    IReadOnlyList<string> Notes,
    bool NotAuthorized,
    IReadOnlyList<string> Sources,
    string? Narrative = null,
    string? NarrativeProvider = null,
    OperationalBrief? Brief = null);

public sealed record BriefSection(string Title, string Tone, IReadOnlyList<AnalystFact> Facts, string? Note = null);

/// <summary>An operational brief (spec §67): every line derived from Forge records the reader may see.</summary>
public sealed record OperationalBrief(
    string Title,
    string? ShiftCode,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset Since,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<string> Headlines,
    IReadOnlyList<BriefSection> Sections,
    IReadOnlyList<string> Omitted);

/// <summary>
/// Optional generative AI (spec §61). It receives only an answer Forge already computed and filtered for
/// the reader, and may only restate it in prose. Forge works fully with no provider configured.
/// </summary>
public interface IAnalystNarrator
{
    /// <summary>"none" when generative AI is disabled.</summary>
    string Name { get; }

    bool Enabled { get; }

    Task<string?> NarrateAsync(string question, string factsJson, CancellationToken ct);
}

/// <summary>The default: generative AI disabled.</summary>
public sealed class DisabledNarrator : IAnalystNarrator
{
    public string Name => "none";

    public bool Enabled => false;

    public Task<string?> NarrateAsync(string question, string factsJson, CancellationToken ct) => Task.FromResult<string?>(null);
}
