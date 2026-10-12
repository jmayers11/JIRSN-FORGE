using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Execution.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Engines;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Kernel.Serialization;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Planning.Reporting;
using Forge.Programs.Contracts;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;
using NodaTime;

namespace Forge.Planning.Analyst;

/// <summary>
/// Forge Analyst (spec §58–62, §66): answers manufacturing questions by parsing them deterministically
/// and calling Forge's own services as the person asking. Each answer needs the same read permission
/// the screens need, and program-segregated records stay hidden; an answer the person may not see is
/// "not authorized", never a partial leak. Calculations come from the engines and the reporting
/// engine. A generative AI provider, when configured, may only restate a finished answer in prose.
/// </summary>
public sealed class AnalystService(
    IKernelUnitOfWorkFactory unitOfWork,
    IForgeAuthorizer authorizer,
    TimeProvider time,
    ReportService reports,
    HorizonHistoryService history,
    ReadinessService readiness,
    DocumentationService documentation,
    BriefService briefs,
    IAnalystNarrator narrator,
    HistoryService entities,
    PlanningScenarioService scenarios)
{
    public const int MaxRows = 200;

    public static readonly IReadOnlyList<string> Suggestions =
    [
        "What is late?",
        "What machines are unavailable?",
        "What is preventing SLITTER-A from running?",
        "Who is qualified to work Slitting tonight?",
        "What changed since yesterday?",
        "Which certifications expire in the next 90 days?",
        "What happens if SLITTER-A goes down tomorrow for 8 hours?",
        "Show orders affected by labor constraints",
        "Create an Excel report of downtime last week by machine",
        "Compare first and second shift output this week",
        "Where is lot L-104 and who holds it?",
        "What became of the material issued to order 10001?",
        "Was gauge G-1 calibrated when it was used?",
        "What happened last shift?",
        "What are my recovery options?",
        "Brief me",
    ];

    public bool NarrationAvailable => narrator.Enabled;

    public string NarratorName => narrator.Name;

    /// <summary>What Forge understood, without answering. The person can check it before anything runs.</summary>
    public async Task<Interpretation> InterpretAsync(ForgePrincipal principal, string question, CancellationToken ct)
    {
        Demand(principal, Permissions.AnalystUse);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var (vocabulary, clock, _) = await ContextAsync(uow, principal, ct);
        return QueryParser.Parse(question, vocabulary, clock);
    }

    public async Task<AnalystAnswer> AskAsync(ForgePrincipal principal, string question, bool narrate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal, Permissions.AnalystUse);
        if (string.IsNullOrWhiteSpace(question) || question.Length > 500)
        {
            throw new CommandRejectedException("invalid_question", "Ask a question of up to 500 characters.");
        }

        Interpretation interpretation;
        AnalystVocabulary vocabulary;
        AnalystClock clock;
        DateTimeZone zone;
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
        {
            (vocabulary, clock, zone) = await ContextAsync(uow, principal, ct);
            interpretation = QueryParser.Parse(question, vocabulary, clock);
        }

        AnalystAnswer answer;
        try
        {
            answer = await AnswerAsync(principal, interpretation, vocabulary, clock, zone, ct);
        }
        catch (ForgeAuthorizationException)
        {
            answer = NotAuthorized(interpretation);
        }
        catch (CommandRejectedException rejected)
        {
            answer = new AnalystAnswer(interpretation, rejected.Message, [], null, null, [], false, []);
        }

        if (narrate && narrator.Enabled && !answer.NotAuthorized && interpretation.Intent != AnalystIntent.Unsupported)
        {
            var facts = CanonicalJson.Serialize(new
            {
                answer.Headline,
                Facts = answer.Facts.Select(f => f.Text),
                answer.Notes,
                Columns = answer.Columns?.Select(c => c.Title),
                Rows = answer.Rows?.Take(25).Select(r => r.Cells),
                Brief = answer.Brief?.Sections.Select(s => new { s.Title, Facts = s.Facts.Select(f => f.Text) }),
            });
            var narrative = await narrator.NarrateAsync(question, facts, ct);
            if (!string.IsNullOrWhiteSpace(narrative))
            {
                answer = answer with { Narrative = narrative.Trim(), NarrativeProvider = narrator.Name };
            }
        }

        return answer;
    }

    private async Task<AnalystAnswer> AnswerAsync(ForgePrincipal principal, Interpretation i, AnalystVocabulary vocabulary, AnalystClock clock, DateTimeZone zone, CancellationToken ct)
    {
        if (i.Intent != AnalystIntent.Unsupported && i.Intent != AnalystIntent.Report && NeedsEntity(i))
        {
            return new AnalystAnswer(i, "I need a little more to answer that.", [], null, null, i.Ambiguities, false, []);
        }

        return i.Intent switch
        {
            AnalystIntent.OrderStatus => await OrderAsync(principal, i, zone, ct),
            AnalystIntent.LateOrders => await LateAsync(principal, i, zone, ct),
            AnalystIntent.UnavailableMachines => await MachinesDownAsync(principal, i, clock, zone, ct),
            AnalystIntent.MachineBlockers => await BlockersAsync(principal, i, ct),
            AnalystIntent.QualifiedPeople => await QualifiedAsync(principal, i, clock, zone, ct),
            AnalystIntent.WhatChanged => await ChangedAsync(principal, i, clock, zone, ct),
            AnalystIntent.AffectedOrders => await AffectedAsync(principal, i, vocabulary, zone, ct),
            AnalystIntent.PersonImpact or AnalystIntent.Simulate => await ScenarioAsync(principal, i, vocabulary, clock, zone, ct),
            AnalystIntent.RecoveryOptions => await RecoveryOptionsAsync(principal, i, ct),
            AnalystIntent.Report => await ReportAsync(principal, i, ct),
            AnalystIntent.Brief => await BriefAsync(principal, i, ct),
            AnalystIntent.OrderDocumentation => await DocumentationAsync(principal, i, ct),
            _ => new AnalystAnswer(
                i,
                "I can answer questions about your Forge manufacturing operation.",
                Suggestions.Select(s => new AnalystFact("Try: " + s)).ToList(),
                null, null, [], false, []),
        };
    }

    private static bool NeedsEntity(Interpretation i) => i.Intent switch
    {
        AnalystIntent.OrderStatus or AnalystIntent.OrderDocumentation => i.OrderNumber is null,
        AnalystIntent.MachineBlockers => i.Machine is null,
        AnalystIntent.Simulate => i.Machine is null && i.PersonId is null,
        AnalystIntent.PersonImpact => i.PersonId is null,
        AnalystIntent.QualifiedPeople => i.Machine is null && i.WorkCenter is null,
        AnalystIntent.AffectedOrders => i.Machine is null && i.WorkCenter is null && i.Category is null && i.PersonId is null,
        _ => false,
    };

    // ------------------------------------------------------------------ orders

    private async Task<AnalystAnswer> OrderAsync(ForgePrincipal principal, Interpretation i, DateTimeZone zone, CancellationToken ct)
    {
        Demand(principal, Permissions.OrdersRead);
        var number = i.OrderNumber!.Value;
        var facts = new List<AnalystFact>();
        var sources = new List<string> { "Production order " + Id(number) };
        OrderInfo order;
        ScheduleVersion? approved = null;
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
        {
            order = await uow.Store<IExecutionLookup>().FindOrderAsync(number, ct) is { } o && Visible(principal, o.ProgramId)
                ? o
                : throw new CommandRejectedException("order_not_found", $"Order {number} does not exist.");
            if (Permitted(principal, Permissions.ScheduleRead))
            {
                approved = await uow.Store<IPlanningStore>().LatestAsync(ScheduleStatus.Approved, true, ct);
            }
        }

        facts.Add(new($"Order {number}: {Fmt(order.Quantity)} × {order.PartNumber} rev {order.Revision}, due {order.DueDate:yyyy-MM-dd}, priority {order.Priority}, {order.Status}.", "order:" + Id(number)));
        var done = order.Operations.Count(op => op.Status == OperationStatus.Complete);
        if (order.Operations.Count > 0)
        {
            facts.Add(new($"{done} of {order.Operations.Count} operations complete" + (order.Operations.FirstOrDefault(op => op.Status != OperationStatus.Complete) is { } next ? $"; next is op {next.Sequence} {next.Description} at {next.WorkCenterCode} ({next.Status})." : ".")));
        }

        string headline;
        var projected = approved?.Result?.Orders.FirstOrDefault(o => o.OrderNumber == number);
        if (projected is not null)
        {
            sources.Add($"Approved schedule {Id(approved!.VersionId)}");
            var status = Status(projected.Status);
            headline = projected.Status == DeliveryStatus.OnTime
                ? $"Order {number} is on schedule: projected to finish {Local(projected.ProjectedCompletion, zone)}, due {order.DueDate:yyyy-MM-dd}."
                : $"Order {number} is {status}" + (projected.ProjectedCompletion is { } pc ? $": projected to finish {Local(pc, zone)}, due {order.DueDate:yyyy-MM-dd}." : ".");
            facts.Add(new(projected.Explanation, "schedule:" + Id(approved.VersionId), projected.Status == DeliveryStatus.OnTime ? "ok" : "bad"));
            foreach (var op in approved.Result!.Operations.Where(o => o.OrderNumber == number).OrderBy(o => o.Sequence))
            {
                var text = op.Scheduled
                    ? $"Op {op.Sequence} {op.Description}: {Local(op.Start, zone)} – {Local(op.End, zone)} on {op.MachineCode ?? op.WorkCenterCode}; start fixed by {Binding(op)}."
                    : $"Op {op.Sequence} {op.Description}: not scheduled — {Binding(op)}.";
                facts.Add(new(text, "schedule:" + Id(approved.VersionId), op.Scheduled ? null : "bad"));
                foreach (var stop in op.Interruptions)
                {
                    facts.Add(new($"   Interrupted {Local(stop.From, zone)} – {Local(stop.Until, zone)}: {stop.Limiting} — {stop.Detail}", "schedule:" + Id(approved.VersionId), "warn"));
                }
            }
        }
        else
        {
            headline = order.Status switch
            {
                OrderStatus.Completed => $"Order {number} is complete.",
                OrderStatus.Cancelled => $"Order {number} was cancelled.",
                OrderStatus.Released => approved is null && !Permitted(principal, Permissions.ScheduleRead)
                    ? $"Order {number} is released; you do not have access to the schedule's projection."
                    : $"Order {number} is released but not in the approved schedule; propose and approve a new schedule.",
                OrderStatus.Held => $"Order {number} is on hold.",
                _ => $"Order {number} is {order.Status} and not yet released, so it is not scheduled.",
            };
        }

        // Readiness now: what would stop its next operation.
        if (order.Status is OrderStatus.Released or OrderStatus.Held && Permitted(principal, Permissions.ReadinessRead))
        {
            var run = await readiness.EvaluateOrderAsync(principal, number, ct);
            sources.Add("Order readiness");
            foreach (var op in run.Output.Operations.Where(o => !o.Executable).Take(3))
            {
                var failing = op.Dimensions.Where(d => d.Outcome != EvidenceOutcome.Pass).Select(d => $"{d.Name}: {d.Detail}");
                facts.Add(new($"Right now op {op.Sequence} cannot run — " + string.Join("; ", failing), "order:" + Id(number), "warn"));
            }
        }

        return new AnalystAnswer(i, headline, facts, null, null, [], false, sources);
    }

    private async Task<AnalystAnswer> DocumentationAsync(ForgePrincipal principal, Interpretation i, CancellationToken ct)
    {
        var number = i.OrderNumber!.Value;
        var docs = await documentation.OrderAsync(principal, number, ct);
        var columns = Columns(("document", "Document", ReportColumnKind.Text), ("source", "Required by", ReportColumnKind.Text), ("atRelease", "Released with", ReportColumnKind.Text), ("applies", "Applies now", ReportColumnKind.Text), ("state", "State", ReportColumnKind.Text), ("why", "Why", ReportColumnKind.Text));
        var rows = docs.Documents.Select(d => new ReportRow(
            [d.DocumentNumber, d.Source, d.AtRelease, d.Applicable, DocumentationService.Describe(d.State), d.Explanation],
            d.DocumentNumber == DocumentationService.Restricted ? ["order:" + Id(number)] : ["order:" + Id(number), "document:" + d.DocumentNumber])).ToList();
        var notes = new List<string>();
        if (!docs.Enforced)
        {
            notes.Add($"Documentation is not enforced for {docs.ItemClass} items, so it does not block readiness; it is shown here for information.");
        }

        return new AnalystAnswer(i, $"Order {number} ({docs.PartNumber} rev {docs.Revision}): {docs.Summary}", [], columns, rows, notes, false, ["Applicability", "Controlled documents"]);
    }

    private async Task<AnalystAnswer> LateAsync(ForgePrincipal principal, Interpretation i, DateTimeZone zone, CancellationToken ct)
    {
        Demand(principal, Permissions.ScheduleRead);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var approved = await uow.Store<IPlanningStore>().LatestAsync(ScheduleStatus.Approved, true, ct);
        if (approved?.Result is null)
        {
            return new AnalystAnswer(i, "No schedule has been approved yet, so nothing is projected late.", [], null, null, ["Propose and approve a schedule on the Scheduling page."], false, []);
        }

        var columns = Columns(("order", "Order", ReportColumnKind.Text), ("part", "Part", ReportColumnKind.Text), ("due", "Due", ReportColumnKind.Date), ("projected", "Projected finish", ReportColumnKind.DateTime), ("status", "Status", ReportColumnKind.Text), ("why", "Why", ReportColumnKind.Text));
        var rows = approved.Result.Orders
            .Where(o => o.Status != DeliveryStatus.OnTime && Visible(principal, o.ProgramId) && ReportRun.Is(o.PartNumber, i.PartNumber) && (i.ProgramId is null || o.ProgramId == i.ProgramId))
            .OrderBy(o => o.Status switch { DeliveryStatus.Late => 0, DeliveryStatus.AtRisk => 2, _ => 1 }).ThenBy(o => o.DueDate).ThenBy(o => o.OrderNumber)
            .Select(o => new ReportRow([Id(o.OrderNumber), o.PartNumber, o.DueDate, o.ProjectedCompletion, Status(o.Status), o.Explanation], ["order:" + Id(o.OrderNumber), "schedule:" + Id(approved.VersionId)]))
            .ToList();
        var late = rows.Count(r => (string)r.Cells[4]! == Status(DeliveryStatus.Late));
        var risk = rows.Count(r => (string)r.Cells[4]! == Status(DeliveryStatus.AtRisk));
        var headline = rows.Count == 0
            ? $"Nothing is late or at risk in the approved schedule (version {Id(approved.VersionId)})."
            : $"{late} order(s) projected late, {risk} at risk and {rows.Count - late - risk} that cannot be scheduled, in schedule {Id(approved.VersionId)} approved {Local(approved.ApprovedAt, zone)}.";
        return new AnalystAnswer(i, headline, [], columns, rows, [], false, [$"Approved schedule {Id(approved.VersionId)}"]);
    }

    // ------------------------------------------------------------------ machines

    private async Task<AnalystAnswer> MachinesDownAsync(ForgePrincipal principal, Interpretation i, AnalystClock clock, DateTimeZone zone, CancellationToken ct)
    {
        Demand(principal, Permissions.MaintenanceRead);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var at = i.At ?? clock.Now;
        var windows = await uow.Store<IMaintenanceLookup>().ListDowntimeAsync(at, at.AddHours(24), ct);
        var down = windows.Where(w => w.Covers(at) && ReportRun.Is(w.MachineCode, i.Machine)).OrderBy(w => w.MachineCode, StringComparer.Ordinal).ToList();
        var upcoming = windows.Where(w => w.StartsAt > at && ReportRun.Is(w.MachineCode, i.Machine)).OrderBy(w => w.StartsAt).ToList();
        var facts = down.Select(w => new AnalystFact($"{w.MachineCode}: {Kind(w.Kind)} since {Local(w.StartsAt, zone)}" + (w.EndsAt is { } e ? $", until {Local(e, zone)}" : ", open") + $" — {w.Reason}", Trace(w), "bad")).ToList();
        facts.AddRange(upcoming.Select(w => new AnalystFact($"Next 24 h: {w.MachineCode} {Kind(w.Kind)} {Local(w.StartsAt, zone)}" + (w.EndsAt is { } e ? $" – {Local(e, zone)}" : string.Empty) + $" — {w.Reason}", Trace(w), "warn")));
        var headline = down.Count == 0
            ? (at == clock.Now ? "Every machine is available right now." : $"No machine is down at {Local(at, zone)}.")
            : $"{down.Count} machine(s) unavailable {(at == clock.Now ? "now" : "at " + Local(at, zone))}: {string.Join(", ", down.Select(w => w.MachineCode).Distinct(StringComparer.Ordinal))}.";
        return new AnalystAnswer(i, headline, facts, null, null, [], false, ["Downtime records", "PM work orders (overdue lockouts)"]);
    }

    private async Task<AnalystAnswer> BlockersAsync(ForgePrincipal principal, Interpretation i, CancellationToken ct)
    {
        Demand(principal, Permissions.ReadinessRead);
        IReadOnlyList<MachineActivityInfo> activities;
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
        {
            activities = (await uow.Store<IResourcesLookup>().ListMachineActivitiesAsync(ct)).Where(a => a.MachineCode == i.Machine).ToList();
        }

        if (activities.Count == 0)
        {
            return new AnalystAnswer(i, $"{i.Machine} has no activities set up, so there is nothing it is expected to run.", [], null, null, ["Machine activities are set up on the Plant Setup page."], false, ["Plant setup"]);
        }

        var facts = new List<AnalystFact>();
        var runnable = new List<string>();
        foreach (var activity in activities)
        {
            var run = (await readiness.EvaluateMachineAsync(principal, i.Machine!, activity.ActivityCode, ct)).Output;
            if (run.Executable)
            {
                runnable.Add(activity.ActivityCode);
                continue;
            }

            foreach (var d in run.Dimensions.Where(d => d.Outcome != EvidenceOutcome.Pass))
            {
                facts.Add(new($"{activity.ActivityCode} — {d.Name}: {d.Detail}", "machine:" + i.Machine, d.Outcome == EvidenceOutcome.Fail ? "bad" : "warn"));
            }
        }

        var headline = runnable.Count == activities.Count
            ? $"Nothing prevents {i.Machine} from running now: {string.Join(", ", runnable)} can all run."
            : runnable.Count == 0
                ? $"{i.Machine} cannot run now."
                : $"{i.Machine} can run {string.Join(", ", runnable)} but not {string.Join(", ", activities.Select(a => a.ActivityCode).Except(runnable, StringComparer.Ordinal))}.";
        return new AnalystAnswer(i, headline, facts, null, null, ReadinessNotes, false, ["Readiness engine (equipment, qualified operators, safety staffing)"]);
    }

    private static readonly IReadOnlyList<string> ReadinessNotes = ["Readiness is evaluated now, from recorded presence, qualifications, safety rules and downtime."];

    // ------------------------------------------------------------------ people

    private async Task<AnalystAnswer> QualifiedAsync(ForgePrincipal principal, Interpretation i, AnalystClock clock, DateTimeZone zone, CancellationToken ct)
    {
        Demand(principal, Permissions.WorkforceRead);
        Demand(principal, Permissions.CompetencyRead);
        SiteSnapshot snapshot;
        await using (var uow = await unitOfWork.BeginSnapshotAsync(ct))
        {
            snapshot = await SnapshotLoader.LoadAsync(uow, time, ct);
        }

        var at = i.At ?? clock.Now;
        var machines = snapshot.Machines.Where(m => i.Machine is not null ? m.Code == i.Machine : m.WorkCenterCode == i.WorkCenter).Select(m => m.Code).ToHashSet(StringComparer.Ordinal);
        var activities = snapshot.MachineActivities.Where(a => machines.Contains(a.MachineCode)).ToList();
        if (activities.Count == 0)
        {
            return new AnalystAnswer(i, $"{i.Machine ?? i.WorkCenter} has no machine activities with operator requirements set up.", [], null, null, [], false, ["Plant setup"]);
        }

        // The shift the question is about: named, or the one running at that time.
        var shift = i.ShiftCode ?? ShiftAt(snapshot.Shifts, at, zone);
        var columns = Columns(("person", "Person", ReportColumnKind.Text), ("qualifiedFor", "Qualified for", ReportColumnKind.Text), ("home", "Home work center", ReportColumnKind.Text), ("shift", "Shift", ReportColumnKind.Text), ("presentNow", "Present now", ReportColumnKind.Text), ("availableThen", "Available then", ReportColumnKind.Text));
        var rows = new List<ReportRow>();
        foreach (var p in snapshot.People.Where(p => p.Active).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var can = activities.Where(a => a.OperatorQualifications.All(q => p.HoldsAt(q, at))).Select(a => $"{a.MachineCode} {a.ActivityCode}").ToList();
            if (can.Count == 0 || (shift is not null && p.ShiftCode is not null && p.ShiftCode != shift))
            {
                continue;
            }

            var away = snapshot.Unavailability.Any(u => u.PersonId == p.PersonId && u.StartsAt <= at && at < u.EndsAt);
            rows.Add(new ReportRow(
                [p.Name, string.Join(", ", can), p.HomeWorkCenterCode, p.ShiftCode, p.CountsAsPresent ? "Yes" : "No", away ? "No" : "Yes"],
                ["person:" + p.PersonId.ToString("D", CultureInfo.InvariantCulture)]));
        }

        var available = rows.Count(r => (string)r.Cells[5]! == "Yes");
        var headline = rows.Count == 0
            ? $"Nobody holds the qualifications for {i.Machine ?? i.WorkCenter}" + (shift is null ? "." : $" on shift {shift}.")
            : $"{rows.Count} people are qualified for {i.Machine ?? i.WorkCenter}" + (shift is null ? string.Empty : $" on shift {shift}") + $"; {available} available at {Local(at, zone)}.";
        var notes = new List<string> { "Qualified means every operator qualification of the machine activity is certified and unexpired at that time. Reasons for unavailability are not shown." };
        if (shift is not null)
        {
            notes.Add($"People with no shift assigned are included; people on other shifts are not.");
        }

        return new AnalystAnswer(i, headline, [], columns, rows, notes, false, ["Qualification records", "Workers and shifts", "Machine activity requirements"]);
    }

    // ------------------------------------------------------------------ history and constraints

    /// <summary>
    /// "What changed on SL-02?" (V2.12): the recorded events for that subject, newest first, each linked to its
    /// record. Only the period the question gave is used; without one, everything recorded is listed.
    /// </summary>
    private async Task<AnalystAnswer> SubjectChangedAsync(ForgePrincipal principal, Interpretation i, string subject, AnalystClock clock, DateTimeZone zone, CancellationToken ct)
    {
        DateTimeOffset? since = i.At is { } a && a < clock.Now ? a : i.Period is { } p ? clock.ToInstant(p.From, TimeOnly.MinValue) : null;
        EntityHistory h;
        try
        {
            h = await entities.WhatChangedAsync(principal, subject, i.SubjectKind, since, ct);
        }
        catch (CommandRejectedException e)
        {
            return new AnalystAnswer(i, e.Message, [], null, null, ["Name an asset, machine, project, document, change or part by its code."], false, []);
        }

        var facts = h.Events.Take(40).Select(e => new AnalystFact($"{Local(e.At, zone)} {e.Kind}: {e.Title}{(e.Actor is null ? string.Empty : " — " + e.Actor)}", e.Link)).ToList();
        var headline = h.Events.Count == 0
            ? $"Nothing is recorded for {h.Title}{(since is { } s0 ? " since " + Local(s0, zone) : string.Empty)}."
            : $"{h.Events.Count} recorded event{(h.Events.Count == 1 ? string.Empty : "s")} for {h.Title}{(since is { } s1 ? " since " + Local(s1, zone) : string.Empty)}.";
        var notes = h.Notes.ToList();
        if (h.Events.Count > facts.Count)
        {
            notes.Add($"Showing the newest {facts.Count}; the history drawer lists all {h.Events.Count}.");
        }

        return new AnalystAnswer(i, headline, facts, null, null, notes, false, h.Events.Select(e => e.Source).Distinct(StringComparer.Ordinal).ToList());
    }

    private async Task<AnalystAnswer> ChangedAsync(ForgePrincipal principal, Interpretation i, AnalystClock clock, DateTimeZone zone, CancellationToken ct)
    {
        if (i.Subject is { } subject)
        {
            return await SubjectChangedAsync(principal, i, subject, clock, zone, ct);
        }

        var since = i.At is { } a && a < clock.Now ? a : clock.Now.AddDays(-1);
        var comparison = await history.CompareAsync(principal, since, clock.Now, false, ct);
        var facts = comparison.Orders.Take(15).Select(o => new AnalystFact($"Order {o.OrderNumber} ({o.Change}): {o.Explanation}", "order:" + Id(o.OrderNumber), o.Change switch { "Later" => "bad", "Status changed" => "warn", "Earlier" => "ok", _ => null })).ToList();
        facts.AddRange(comparison.SiteEvidence.Where(e => e.Kind is "Schedule approved" or "Downtime").Take(10).Select(e => new AnalystFact($"{Local(e.At, zone)} {e.Kind}: {e.Text}", e.Trace)));
        var notes = new List<string> { $"Compared the approved schedule in force at {Local(since, zone)} with the one in force now. The Reports page (What changed?) shows the full evidence." };
        return new AnalystAnswer(i, comparison.Headline, facts, null, null, notes, false, ["Approved schedules", "Constraint findings", "Downtime", "Shop-floor reports", "NCRs"]);
    }

    private async Task<AnalystAnswer> AffectedAsync(ForgePrincipal principal, Interpretation i, AnalystVocabulary vocabulary, DateTimeZone zone, CancellationToken ct)
    {
        if (i.PersonId is not null)
        {
            return await ScenarioAsync(principal, i with { Intent = AnalystIntent.PersonImpact }, vocabulary, null, zone, ct);
        }

        Demand(principal, Permissions.ScheduleRead);
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var store = uow.Store<IPlanningStore>();
        var approved = await store.LatestAsync(ScheduleStatus.Approved, true, ct);
        var visibleOrders = (await uow.Store<IExecutionLookup>().ListOrdersAsync(ct)).Where(o => Visible(principal, o.ProgramId)).Select(o => o.OrderNumber).ToHashSet();
        var constraints = (await store.ListConstraintsAsync(false, ct))
            .Where(c => (c.AffectedOrders.Count == 0 || c.AffectedOrders.Any(visibleOrders.Contains))
                && (i.Machine is null || c.Subject == i.Machine || c.Evidence.Contains(i.Machine, StringComparison.Ordinal))
                && (i.WorkCenter is null || c.Subject == i.WorkCenter || c.Evidence.Contains(i.WorkCenter, StringComparison.Ordinal))
                && (i.Category is null || c.Category.Contains(i.Category, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var ops = (approved?.Result?.Operations ?? [])
            .Where(o => Visible(principal, o.ProgramId) && o.BindingConstraint is not ("Now" or "Predecessor")
                && (i.Machine is null || o.MachineCode == i.Machine || o.Interruptions.Any(x => x.Detail.Contains(i.Machine, StringComparison.Ordinal)))
                && (i.WorkCenter is null || o.WorkCenterCode == i.WorkCenter)
                && (i.Category is null || Matches(o, i.Category)))
            .ToList();
        var columns = Columns(("order", "Order", ReportColumnKind.Text), ("op", "Op", ReportColumnKind.Text), ("where", "Where", ReportColumnKind.Text), ("start", "Planned start", ReportColumnKind.DateTime), ("constraint", "Held by", ReportColumnKind.Text), ("detail", "Detail", ReportColumnKind.Text));
        var rows = ops.OrderBy(o => o.Start ?? DateTimeOffset.MaxValue).ThenBy(o => o.OrderNumber)
            .Select(o => new ReportRow([Id(o.OrderNumber), o.Sequence.ToString(CultureInfo.InvariantCulture), o.MachineCode ?? o.WorkCenterCode, o.Start, o.BindingConstraint, o.BindingDetail], ["order:" + Id(o.OrderNumber), "schedule:" + Id(approved!.VersionId)]))
            .ToList();
        var facts = constraints.Take(10)
            .Select(c => new AnalystFact($"Open constraint {c.ConstraintId} ({c.Category}, {c.Subject}): {c.Finding}", "constraint:" + Id(c.ConstraintId), "warn"))
            .ToList();
        var orders = rows.Select(r => (string)r.Cells[0]!).Distinct(StringComparer.Ordinal).Count();
        var subject = i.Machine ?? i.WorkCenter ?? (i.Category + " constraints").ToLowerInvariant();
        var headline = approved is null
            ? "No schedule has been approved yet, so no order is affected in a plan."
            : rows.Count == 0 && constraints.Count == 0
                ? $"No order in the approved schedule is held by {subject}."
                : $"{orders} order(s) have operations held by {subject} in schedule {Id(approved.VersionId)}; {constraints.Count} open constraint(s) match.";
        return new AnalystAnswer(i, headline, facts, columns, rows, ["“Held by” is the binding constraint the scheduler recorded for each operation's start."], false, ["Approved schedule", "Constraint findings"]);
    }

    private static bool Matches(ScheduledOperation o, string category) => category switch
    {
        "Labor" => o.BindingConstraint.Contains("Labor", StringComparison.OrdinalIgnoreCase) || o.BindingConstraint.Contains("Crew", StringComparison.OrdinalIgnoreCase) || o.BindingConstraint.Contains("People", StringComparison.OrdinalIgnoreCase) || o.BindingConstraint.Contains("Qualif", StringComparison.OrdinalIgnoreCase),
        "Capacity" => o.BindingConstraint.Contains("Machine", StringComparison.OrdinalIgnoreCase) || o.BindingConstraint.Contains("Capacity", StringComparison.OrdinalIgnoreCase) || o.BindingConstraint.Contains("Shift", StringComparison.OrdinalIgnoreCase),
        _ => o.BindingConstraint.Contains(category, StringComparison.OrdinalIgnoreCase),
    };

    // ------------------------------------------------------------------ scenarios

    /// <summary>
    /// A what-if: the scheduler runs twice on the same snapshot, once as recorded and once with the
    /// change, and the two plans are compared order by order. Nothing is saved (principle 17).
    /// </summary>
    private async Task<AnalystAnswer> ScenarioAsync(ForgePrincipal principal, Interpretation i, AnalystVocabulary vocabulary, AnalystClock? clock, DateTimeZone zone, CancellationToken ct)
    {
        Demand(principal, Permissions.ScheduleRead);
        Demand(principal, Permissions.ReadinessRead);
        if (i.PersonId is not null)
        {
            Demand(principal, Permissions.WorkforceRead);
        }

        SiteSnapshot snapshot;
        List<OrderInfo> orders;
        IReadOnlyList<OrderMaterialStatus> material;
        IReadOnlyDictionary<long, OrderQuality> quality;
        await using (var read = await unitOfWork.BeginSnapshotAsync(ct))
        {
            snapshot = await SnapshotLoader.LoadAsync(read, time, ct);
            var lookup = read.Store<IExecutionLookup>();
            orders = [];
            foreach (var summary in (await lookup.ListOrdersAsync(ct)).Where(o => o.Status == OrderStatus.Released).OrderBy(o => o.OrderNumber))
            {
                orders.Add((await lookup.FindOrderAsync(summary.OrderNumber, ct))!);
            }

            material = (await MaterialPlanningService.RunMaterialAsync(read, time, ct)).Output;
            quality = await ScheduleQuality.LoadAsync(read, orders, ct);
        }

        var now = snapshot.AsOf;
        var start = i.At is { } at && at > now ? at : now;
        var hours = i.Hours ?? (i.Intent == AnalystIntent.Simulate ? 24 : 0);
        string change;
        SiteSnapshot changed;
        string? personName = i.PersonId is { } pid ? vocabulary.People.FirstOrDefault(p => p.PersonId == pid)?.Name : null;
        if (i.Intent == AnalystIntent.Simulate && i.Machine is { } machine)
        {
            var until = start.AddHours((double)hours);
            changed = snapshot with { Downtime = [.. snapshot.Downtime, new DowntimeWindow(-1, machine, DowntimeKind.Unplanned, start, until, "Scenario: machine unavailable")] };
            change = $"{machine} unavailable {Local(start, zone)} – {Local(until, zone)}";
        }
        else if (i.Intent == AnalystIntent.Simulate && i.PersonId is { } person)
        {
            var until = start.AddHours((double)(hours == 0 ? 24 : hours));
            changed = snapshot with { Unavailability = [.. snapshot.Unavailability, new UnavailabilityWindow(person, UnavailabilityKind.Unavailable, start, until)] };
            change = $"{personName} unavailable {Local(start, zone)} – {Local(until, zone)}";
        }
        else
        {
            // Person impact: the plan as recorded against one where the person has no recorded absence.
            var person2 = i.PersonId!.Value;
            changed = snapshot with
            {
                Unavailability = snapshot.Unavailability.Where(u => u.PersonId != person2).ToList(),
                People = snapshot.People.Select(p => p.PersonId == person2 ? p with { Unavailability = null } : p).ToList(),
            };
            change = $"{personName} available throughout (no recorded absence)";
        }

        var request = new ScheduleRequest(now, now.AddDays(14), orders, material, quality);
        var baseline = EngineRunner.Run(new SchedulingEngine(), snapshot, request, now).Output;
        var scenario = EngineRunner.Run(new SchedulingEngine(), changed, request, now).Output;

        // For person impact the recorded plan is "after" (with the absence): orders that are later with it depend on the person.
        var (before, after) = i.Intent == AnalystIntent.PersonImpact ? (scenario, baseline) : (baseline, scenario);
        var beforeOrders = before.Orders.Where(o => Visible(principal, o.ProgramId)).ToDictionary(o => o.OrderNumber);
        var afterOrders = after.Orders.Where(o => Visible(principal, o.ProgramId)).ToDictionary(o => o.OrderNumber);
        var changes = new List<OrderChange>();
        foreach (var number in beforeOrders.Keys.Union(afterOrders.Keys).Order())
        {
            beforeOrders.TryGetValue(number, out var a);
            afterOrders.TryGetValue(number, out var b);
            var ops = HorizonHistoryService.CompareOperations(before.Operations.Where(o => o.OrderNumber == number).ToList(), after.Operations.Where(o => o.OrderNumber == number).ToList());
            var c = HorizonHistoryService.Describe(number, a, b, ops, []);
            if (c.Change != "Unchanged")
            {
                changes.Add(c);
            }
        }

        var worse = changes.Where(c => c.Change is "Later" or "Status changed" && (c.ShiftHours ?? 0) >= 0).ToList();
        var columns = Columns(("order", "Order", ReportColumnKind.Text), ("part", "Part", ReportColumnKind.Text), ("change", "Change", ReportColumnKind.Text), ("shift", "Hours", ReportColumnKind.Hours), ("statusBefore", "Before", ReportColumnKind.Text), ("statusAfter", "After", ReportColumnKind.Text), ("why", "Explanation", ReportColumnKind.Text));
        var rows = changes.OrderByDescending(c => Math.Abs(c.ShiftHours ?? 0)).ThenBy(c => c.OrderNumber)
            .Select(c => new ReportRow([Id(c.OrderNumber), c.PartNumber, c.Change, c.ShiftHours, c.StatusBefore, c.StatusAfter, c.Explanation], ["order:" + Id(c.OrderNumber)]))
            .ToList();
        string headline;
        if (i.Intent == AnalystIntent.PersonImpact)
        {
            headline = changes.Count == 0
                ? $"No order in the next 14 days depends on {personName} being available."
                : $"{worse.Count} order(s) are later or worse off because {personName} is unavailable; {changes.Count} change in all.";
        }
        else
        {
            var late = after.Orders.Count(o => o.Status != DeliveryStatus.OnTime && Visible(principal, o.ProgramId)) - before.Orders.Count(o => o.Status != DeliveryStatus.OnTime && Visible(principal, o.ProgramId));
            headline = changes.Count == 0
                ? $"If {change}, no scheduled order would change."
                : $"If {change}: {worse.Count} order(s) later or worse off, {Math.Max(0, late)} more not on time.";
        }

        var notes = new List<string>
        {
            "Scenario only: the scheduler ran on today's data with and without the change. Nothing was saved and the approved schedule is unchanged.",
            $"Scenario: {change}. Horizon 14 days from now.",
        };
        _ = clock;
        return new AnalystAnswer(i, headline, [], columns, rows, notes, false, ["Scheduling engine (scenario run)", "Released orders", "Material plan", "Downtime, workforce and qualifications as recorded"]);
    }

    // ------------------------------------------------------------------ reports and briefs

    private async Task<AnalystAnswer> ReportAsync(ForgePrincipal principal, Interpretation i, CancellationToken ct)
    {
        var request = i.Report!;
        var result = await reports.RunAsync(principal, request.ReportId, request.Parameters, ct);
        var headline = $"{result.Title}, {result.From:MMM d} – {result.To:MMM d}: {result.Rows.Count} row(s)" + (result.Ratio is { } r && result.Total.Ratio is { } pct ? $", {r.Title.ToLowerInvariant()} {Fmt(pct)}%." : ".");
        var facts = result.Summary.Select(s => new AnalystFact(s)).ToList();
        if (result.Groups.Count > 0)
        {
            var title = result.Columns.First(c => c.Key == result.GroupBy).Title;
            facts.AddRange(result.Groups.Take(12).Select(g => new AnalystFact($"{title} {g.Key}: {g.Rows} row(s)" + (g.Ratio is { } gr ? $", {Fmt(gr)}%" : string.Empty) + string.Concat(g.Sums.Where(s => s.Value != 0).Take(3).Select(s => $", {result.Columns.First(c => c.Key == s.Key).Title.ToLowerInvariant()} {Fmt(s.Value)}")))));
        }

        var notes = request.Notes.ToList();
        if (result.Rows.Count > MaxRows)
        {
            notes.Add($"Showing the first {MaxRows} rows here; the report itself has all {result.Rows.Count}.");
        }

        return new AnalystAnswer(i, headline, facts, result.Columns, result.Rows.Take(MaxRows).ToList(), notes, false, result.Sources);
    }

    private async Task<AnalystAnswer> RecoveryOptionsAsync(ForgePrincipal principal, Interpretation i, CancellationToken ct)
    {
        var saved = await scenarios.ListAsync(principal, ct);
        var columns = new[]
        {
            new ReportColumn("scenario", "Private scenario", ReportColumnKind.Text),
            new ReportColumn("option", "Recovery option", ReportColumnKind.Text),
            new ReportColumn("feasible", "Feasible", ReportColumnKind.Text),
            new ReportColumn("why", "Feasibility", ReportColumnKind.Text),
            new ReportColumn("consequences", "Consequences", ReportColumnKind.Text),
            new ReportColumn("permission", "Required permission", ReportColumnKind.Text),
        };
        var rows = new List<ReportRow>();
        foreach (var scenario in saved.Take(20))
        {
            var projection = await scenarios.RunAsync(scenario.ScenarioId, principal, ct);
            foreach (var option in projection.RecoveryOptions)
            {
                rows.Add(new ReportRow(
                    [scenario.Name, option.Title, option.Feasible ? "Yes" : "No", option.Feasibility, string.Join(" ", option.Consequences), option.RequiredPermission],
                    [$"scenario:{scenario.ScenarioId:D}"]));
            }
        }

        var feasible = rows.Count(r => string.Equals((string)r.Cells[2]!, "Yes", StringComparison.Ordinal));
        var note = saved.Count == 0
            ? "Save a private scenario first. Its recovery options will be evaluated against the current authorized snapshot."
            : $"Evaluated {rows.Count} option(s) across the first {Math.Min(saved.Count, 20)} of {saved.Count} private scenario(s) using current source data and permissions.";
        return new AnalystAnswer(i, saved.Count == 0 ? "No saved private scenarios." : $"{feasible} of {rows.Count} recovery option(s) are feasible across your private scenarios.", [], columns, rows, [note], false, ["Private planning scenarios", "Current schedule, readiness and capacity engines"]);
    }

    private async Task<AnalystAnswer> BriefAsync(ForgePrincipal principal, Interpretation i, CancellationToken ct)
    {
        var brief = await briefs.BriefAsync(principal, i.ShiftCode, ct);
        var facts = brief.Headlines.Select(h => new AnalystFact(h)).ToList();
        return new AnalystAnswer(i, brief.Title, facts, null, null, brief.Omitted.Select(o => $"Not included (no access): {o}.").ToList(), false, ["Approved schedule", "Constraints", "Downtime and maintenance", "Workforce", "Inventory", "Quality", "Qualifications"], Brief: brief);
    }

    // ------------------------------------------------------------------ context

    private async Task<(AnalystVocabulary Vocabulary, AnalystClock Clock, DateTimeZone Zone)> ContextAsync(IKernelUnitOfWork uow, ForgePrincipal principal, CancellationToken ct)
    {
        var resources = uow.Store<IResourcesLookup>();
        var sites = await resources.ListSitesAsync(ct);
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(sites.Count > 0 ? sites[0].TimeZone : "America/New_York") ?? DateTimeZoneProviders.Tzdb["America/New_York"];
        var now = time.GetUtcNow();
        var local = Instant.FromDateTimeOffset(now).InZone(zone).LocalDateTime;
        var clock = new AnalystClock(
            now,
            new DateOnly(local.Year, local.Month, local.Day),
            new TimeOnly(local.Hour, local.Minute),
            (d, t) => zone.AtLeniently(new LocalDateTime(d.Year, d.Month, d.Day, t.Hour, t.Minute)).ToDateTimeOffset().ToUniversalTime());

        var machines = (await resources.ListMachinesAsync(ct)).Where(m => m.Active).Select(m => new VocabularyMachine(m.Code, m.Name, m.WorkCenterCode)).ToList();
        var workCenters = (await resources.ListWorkCentersAsync(ct)).Where(w => w.Active).Select(w => new VocabularyWorkCenter(w.Code, w.Name)).ToList();
        var shifts = (await resources.ListShiftsAsync(ct)).Select(s => new VocabularyShift(s.Code, s.Name, s.Start, s.DurationMinutes, s.Days)).ToList();
        var parts = (await uow.Store<IEngineeringLookup>().ListItemsAsync(ct)).Select(p => p.PartNumber).ToList();
        var programs = (await uow.Store<IProgramsLookup>().ListProgramsAsync(ct)).Where(p => principal.Programs.Contains(new ProgramId(p.ProgramId))).Select(p => new VocabularyProgram(p.ProgramId, p.Code, p.Name)).ToList();
        var people = Permitted(principal, Permissions.WorkforceRead)
            ? (await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(now, ct)).Where(w => w.IsActive).Select(w => new VocabularyPerson(w.PersonId, w.DisplayName)).ToList()
            : [];
        var orders = (await uow.Store<IExecutionLookup>().ListOrdersAsync(ct)).Where(o => Visible(principal, o.ProgramId)).Select(o => o.OrderNumber).ToHashSet();
        var vocabulary = new AnalystVocabulary(machines, workCenters, shifts, parts, programs, people, orders)
        {
            Tools = Permitted(principal, Permissions.ResourcesRead)
                ? (await uow.Store<IToolLookup>().ListToolsAsync(ct)).Select(t => t.Code).ToList()
                : [],
        };
        return (vocabulary, clock, zone);
    }

    // ------------------------------------------------------------------ helpers

    internal static string? ShiftAt(IReadOnlyList<ShiftInfo> shifts, DateTimeOffset at, DateTimeZone zone)
    {
        var local = Instant.FromDateTimeOffset(at).InZone(zone).Date;
        foreach (var date in new[] { local, local.PlusDays(-1) })
        {
            var iso = (int)date.DayOfWeek;
            foreach (var s in shifts.Where(s => s.Days.Contains(iso)).OrderBy(s => s.Start))
            {
                var start = zone.AtLeniently(date.At(new LocalTime(s.Start.Hour, s.Start.Minute))).ToDateTimeOffset().ToUniversalTime();
                if (at >= start && at < start.AddMinutes(s.DurationMinutes))
                {
                    return s.Code;
                }
            }
        }

        return null;
    }

    private static AnalystAnswer NotAuthorized(Interpretation i) =>
        new(i, "Not authorized. You do not have access to the information this question needs.", [], null, null,
            ["Forge Analyst answers with the same access you have on Forge's screens. A security administrator grants roles and program access."], true, []);

    private void Demand(ForgePrincipal principal, string permission)
    {
        var decision = authorizer.Authorize(principal, permission, ResourceRef.Of("Analyst"));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }
    }

    private bool Permitted(ForgePrincipal principal, string permission) =>
        authorizer.Authorize(principal, permission, ResourceRef.Of("Analyst")).IsPermitted;

    private static bool Visible(ForgePrincipal principal, Guid? program) => program is not { } p || principal.Programs.Contains(new ProgramId(p));

    private static IReadOnlyList<ReportColumn> Columns(params (string Key, string Title, ReportColumnKind Kind)[] columns) =>
        columns.Select(c => new ReportColumn(c.Key, c.Title, c.Kind)).ToList();

    internal static string Local(DateTimeOffset? at, DateTimeZone zone) =>
        at is { } t ? Instant.FromDateTimeOffset(t).InZone(zone).ToString("ddd MMM d HH:mm", CultureInfo.InvariantCulture) : "—";

    private static string Binding(ScheduledOperation op) =>
        string.IsNullOrWhiteSpace(op.BindingDetail) ? op.BindingConstraint : $"{op.BindingConstraint} ({op.BindingDetail})";

    private static string Status(DeliveryStatus s) => s switch
    {
        DeliveryStatus.OnTime => "on time",
        DeliveryStatus.AtRisk => "at risk",
        DeliveryStatus.Late => "projected late",
        _ => "not schedulable",
    };

    private static string Kind(DowntimeKind kind) => kind switch
    {
        DowntimeKind.Unplanned => "down (unplanned)",
        DowntimeKind.Planned => "planned maintenance",
        DowntimeKind.MaintenanceOverdue => "locked out, PM overdue",
        DowntimeKind.AssetOutOfService => "asset out of service",
        _ => kind.ToString(),
    };

    private static string Trace(DowntimeWindow w) => w.Kind == DowntimeKind.MaintenanceOverdue || w.Id <= 0 ? "machine:" + w.MachineCode : "downtime:" + Id(w.Id);

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static string Fmt(decimal d) => (d / 1.000000000000000000000000000000000m).ToString("0.##", CultureInfo.InvariantCulture);
}
