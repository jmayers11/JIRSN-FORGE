using System.Globalization;
using Forge.Engineering.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Projects.Contracts;
using Forge.Resources.Contracts;

namespace Forge.Planning;

/// <summary>One recorded event about a subject, with the record it comes from.</summary>
/// <param name="Link">The record, as kind:id (asset:SL-02, item:PCB-1, work-order:57).</param>
public sealed record HistoryEvent(DateTimeOffset At, string Kind, string Title, string? Actor, string Link, string Source);

public sealed record EntityHistory(string SubjectKind, string Subject, string Title, IReadOnlyList<HistoryEvent> Events, IReadOnlyList<string> Notes);

/// <summary>Something the records say will happen: an effective date, an expiry, a due date, an approved window.</summary>
public sealed record FutureEvent(DateTimeOffset At, string Kind, string Title, string Link, string Source);

/// <summary>
/// "What changed?" for one subject, and Horizon's future events (V2.12). Both list only what is recorded:
/// asset history, work orders, outages and downtime, item and document revisions, engineering changes,
/// alternates, obsolescence records and projects — each event linked to its record. Nothing is inferred.
/// </summary>
public sealed class HistoryService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time)
{
    public static readonly string[] Kinds = ["asset", "machine", "project", "document", "change", "item"];

    public async Task<EntityHistory> WhatChangedAsync(ForgePrincipal principal, string subject, string? kind, DateTimeOffset? since, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var code = (subject ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length == 0)
        {
            throw new CommandRejectedException("subject_required", "Name what to look at: an asset, machine, project, document, change or part.");
        }

        var now = time.GetUtcNow();
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var found = await ResolveAsync(uow, code, kind?.Trim().ToLowerInvariant(), ct)
            ?? throw new CommandRejectedException("subject_not_found", $"Nothing called {code} is recorded{(kind is null ? string.Empty : $" as {kind}")}.");
        Demand(principal, found switch
        {
            "asset" or "machine" => Permissions.MaintenanceRead,
            "project" => Permissions.ProjectsRead,
            _ => Permissions.EngineeringRead,
        });
        var history = found switch
        {
            "asset" => await AssetAsync(uow, principal, code, now, ct),
            "machine" => await MachineAsync(uow, principal, code, now, ct),
            "project" => await ProjectAsync(uow, code, ct),
            "document" => await DocumentAsync(uow, principal, code, ct),
            "change" => await ChangeAsync(uow, principal, code, ct),
            _ => await ItemAsync(uow, principal, code, ct),
        };
        var events = history.Events.Where(e => since is null || e.At >= since).OrderByDescending(e => e.At).ThenBy(e => e.Kind, StringComparer.Ordinal).ToList();
        return history with { Events = events };
    }

    /// <summary>Which kind of record a code names, in this order: asset, machine, project, document, change, item.</summary>
    public static async Task<string?> ResolveAsync(IKernelUnitOfWork uow, string code, string? kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        foreach (var k in kind is null ? Kinds : Kinds.Where(x => x == kind))
        {
            var exists = k switch
            {
                "asset" => await uow.Store<IAssetLookup>().FindAssetAsync(code, ct) is not null,
                "machine" => await uow.Store<IResourcesLookup>().FindMachineAsync(code, ct) is not null,
                "project" => await uow.Store<IProjectLookup>().FindProjectAsync(code, ct) is not null,
                "document" => await uow.Store<IDocumentLookup>().FindDocumentAsync(code, ct) is not null,
                "change" => await uow.Store<IChangeLookup>().FindChangeAsync(code, ct) is not null,
                _ => await uow.Store<IEngineeringLookup>().FindItemAsync(code, ct) is not null,
            };
            if (exists)
            {
                return k;
            }
        }

        return null;
    }

    /// <summary>Future events in [now, now + days) that the reader may see.</summary>
    public async Task<IReadOnlyList<FutureEvent>> FutureAsync(ForgePrincipal principal, int days, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var now = time.GetUtcNow();
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        return await CollectFutureAsync(uow, principal, now, now.AddDays(Math.Clamp(days, 1, 366)), ct);
    }

    /// <summary>
    /// Dated events in [from, until) as the records state them, limited to what the reader may see: each
    /// context only with its read permission, and programs the reader has access to. Dates without a time
    /// are taken as the start of that day (UTC).
    /// </summary>
    public static async Task<IReadOnlyList<FutureEvent>> CollectFutureAsync(IKernelUnitOfWork uow, ForgePrincipal principal, DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(principal);
        var now = from;
        bool In(DateTimeOffset at) => at >= now && at < until;
        static bool Can(ForgePrincipal who, string permission) => who.IsActive && who.HasPermission(permission);
        static DateTimeOffset Day(DateOnly d) => new(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var list = new List<FutureEvent>();
        if (Can(principal, Permissions.EngineeringRead))
        {
            var engineering = uow.Store<IEngineeringLookup>();
            foreach (var item in await engineering.ListItemsAsync(ct))
            {
                foreach (var r in await engineering.ListRevisionsAsync(item.PartNumber, ct))
                {
                    if (r.Status == RevisionStatus.Released && r.EffectiveFrom is { } effective && In(effective))
                    {
                        list.Add(new FutureEvent(effective, "Item revision", $"{item.PartNumber} rev {r.Revision} takes effect", "item:" + item.PartNumber, "Item revisions"));
                    }
                }
            }

            foreach (var d in (await uow.Store<IDocumentLookup>().ListDocumentsAsync(ct)).Where(d => Visible(principal, d.ProgramId)))
            {
                foreach (var r in d.Revisions)
                {
                    if (r.Status is DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable && r.EffectiveFrom is { } f && In(Day(f)))
                    {
                        list.Add(new FutureEvent(Day(f), "Document revision", $"{d.Number} rev {r.Revision} takes effect", "document:" + d.Number, "Document revisions"));
                    }

                    if (r.EffectiveTo is { } t && In(Day(t)))
                    {
                        list.Add(new FutureEvent(Day(t), "Document revision", $"{d.Number} rev {r.Revision} stops applying", "document:" + d.Number, "Document revisions"));
                    }

                    if (r.Status is DocumentRevisionStatus.Released or DocumentRevisionStatus.Applicable && r.ReviewBy is { } review && In(Day(review)))
                    {
                        list.Add(new FutureEvent(Day(review), "Document review", $"{d.Number} rev {r.Revision} is due for review", "document:" + d.Number, "Document revisions"));
                    }
                }
            }

            foreach (var c in (await uow.Store<IChangeLookup>().ListChangesAsync(ct)).Where(c => Visible(principal, c.ProgramId) && c.Status != ChangeStatus.Cancelled))
            {
                if ((c.EffectiveFrom ?? c.RequestedEffective) is { } eff && In(Day(eff)))
                {
                    list.Add(new FutureEvent(Day(eff), "Engineering change", $"{c.Number} {c.Title} {(c.EffectiveFrom is null ? "requested effective" : "effective")}", "change:" + c.Number, "Engineering changes"));
                }
            }

            foreach (var a in (await uow.Store<IAlternateLookup>().ListAlternatesAsync(ct)).Where(a => a.Status == AlternateStatus.Approved && Visible(principal, a.ProgramId)))
            {
                if (a.ValidTo is { } to && In(Day(to)))
                {
                    list.Add(new FutureEvent(Day(to), "Alternate", $"{a.AlternatePart} for {a.PrimaryPart} (alternate {a.AlternateId.ToString(CultureInfo.InvariantCulture)}) ends", "item:" + a.PrimaryPart, "Alternates"));
                }

                if (a.ValidFrom is { } vf && In(Day(vf)))
                {
                    list.Add(new FutureEvent(Day(vf), "Alternate", $"{a.AlternatePart} for {a.PrimaryPart} (alternate {a.AlternateId.ToString(CultureInfo.InvariantCulture)}) starts", "item:" + a.PrimaryPart, "Alternates"));
                }
            }

            foreach (var o in await uow.Store<IObsolescenceLookup>().ListObsolescenceAsync(true, ct))
            {
                foreach (var (date, what) in new[] { (o.LastTimeBuy, "last-time buy"), (o.EndOfLife, "end of life"), (o.EndOfSupport, "end of support") })
                {
                    if (date is { } d && In(Day(d)))
                    {
                        list.Add(new FutureEvent(Day(d), "Obsolescence", $"{o.PartNumber} {what}", "item:" + o.PartNumber, "Obsolescence records"));
                    }
                }
            }
        }

        if (Can(principal, Permissions.MaintenanceRead))
        {
            var maintenance = uow.Store<IMaintenanceLookup>();
            var outages = await maintenance.ListOutagesAsync(true, ct);
            foreach (var o in outages.Where(o => o.Status is OutageStatus.Proposed or OutageStatus.Approved && In(o.StartsAt)))
            {
                list.Add(new FutureEvent(o.StartsAt, "Outage", $"{o.MachineCode} {(o.Status == OutageStatus.Approved ? "out" : "proposed out")} until {o.EndsAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}Z: {o.Reason}", "machine:" + o.MachineCode, "Outages"));
            }

            var fromOutages = outages.Where(o => o.DowntimeId is not null).Select(o => o.DowntimeId!.Value).ToHashSet();
            foreach (var d in (await maintenance.ListDowntimeAsync(now, until, ct)).Where(d => In(d.StartsAt) && !fromOutages.Contains(d.Id)))
            {
                list.Add(new FutureEvent(d.StartsAt, "Planned downtime", $"{d.MachineCode} {d.Kind.ToString().ToLowerInvariant()} downtime{(d.EndsAt is { } e ? " until " + e.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z" : string.Empty)}: {d.Reason}", "machine:" + d.MachineCode, "Downtime"));
            }

            foreach (var w in (await maintenance.ListWorkOrdersAsync(false, ct)).Where(w => w.Status is WorkOrderStatus.Open or WorkOrderStatus.Scheduled && w.DueDate is { } due && In(Day(due))))
            {
                list.Add(new FutureEvent(Day(w.DueDate!.Value), "Work order due", $"Work order {w.WorkOrderId.ToString(CultureInfo.InvariantCulture)} ({w.Kind}) on {w.Target}: {w.Title}", "work-order:" + w.WorkOrderId.ToString(CultureInfo.InvariantCulture), "Work orders"));
            }
        }

        if (Can(principal, Permissions.ProjectsRead))
        {
            var projects = uow.Store<IProjectLookup>();
            foreach (var p in (await projects.ListProjectsAsync(ct)).Where(p => p.Status is ProjectStatus.Draft or ProjectStatus.Approved))
            {
                if (In(Day(p.EndDate)))
                {
                    list.Add(new FutureEvent(Day(p.EndDate), "Project", $"{p.Code} {p.Title} planned to end", "project:" + p.Code, "Projects"));
                }

                foreach (var t in (await projects.ListTasksAsync(p.Code, ct)).Where(t => t.CompletedOn is null && In(Day(t.DueDate))))
                {
                    list.Add(new FutureEvent(Day(t.DueDate), t.Milestone ? "Milestone" : "Project task", $"{p.Code}: {t.Title}", "project:" + p.Code, "Projects"));
                }
            }
        }

        return list.OrderBy(e => e.At).ThenBy(e => e.Kind, StringComparer.Ordinal).ToList();
    }

    private static async Task<EntityHistory> AssetAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string code, DateTimeOffset now, CancellationToken ct)
    {
        var assets = uow.Store<IAssetLookup>();
        var asset = (await assets.FindAssetAsync(code, ct))!;
        var events = (await assets.ListAssetHistoryAsync(code, ct))
            .Select(e => new HistoryEvent(e.At, e.Kind, e.Detail + (e.Reason is null ? string.Empty : $" — {e.Reason}"), e.Actor, "asset:" + code, "Asset history")).ToList();
        var maintenance = uow.Store<IMaintenanceLookup>();
        events.AddRange(WorkOrderEvents((await maintenance.ListWorkOrdersAsync(true, ct)).Where(w => w.AssetCode == code || (asset.MachineCode is { } m && w.MachineCode == m))));
        if (asset.MachineCode is { } machine)
        {
            events.AddRange(await MachineEventsAsync(uow, machine, now, ct));
        }

        var parts = (await assets.ListComponentsAsync(code, null, true, ct)).Select(c => c.PartNumber).ToHashSet(StringComparer.Ordinal);
        events.AddRange(ObsolescenceEvents((await uow.Store<IObsolescenceLookup>().ListObsolescenceAsync(false, ct)).Where(o => parts.Contains(o.PartNumber))));
        events.AddRange(AlternateEvents((await uow.Store<IAlternateLookup>().ListAlternatesAsync(ct)).Where(a => a.AssetCode == code && Visible(principal, a.ProgramId))));
        return new EntityHistory("asset", code, $"Asset {code} — {asset.Name}", events, ["Asset history, its work orders, its machine's downtime and outages, obsolescence of parts installed on it, and alternates approved for it."]);
    }

    private static async Task<EntityHistory> MachineAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string code, DateTimeOffset now, CancellationToken ct)
    {
        var machine = (await uow.Store<IResourcesLookup>().FindMachineAsync(code, ct))!;
        var events = await MachineEventsAsync(uow, code, now, ct);
        events.AddRange(WorkOrderEvents((await uow.Store<IMaintenanceLookup>().ListWorkOrdersAsync(true, ct)).Where(w => w.MachineCode == code)));
        if ((await uow.Store<IAssetLookup>().ListAssetsAsync(ct)).FirstOrDefault(a => a.MachineCode == code) is { } asset)
        {
            events.AddRange((await uow.Store<IAssetLookup>().ListAssetHistoryAsync(asset.AssetCode, ct))
                .Select(e => new HistoryEvent(e.At, e.Kind, $"Asset {asset.AssetCode}: {e.Detail}", e.Actor, "asset:" + asset.AssetCode, "Asset history")));
        }

        events.AddRange(AlternateEvents((await uow.Store<IAlternateLookup>().ListAlternatesAsync(ct)).Where(a => a.MachineCode == code && Visible(principal, a.ProgramId))));
        return new EntityHistory("machine", code, $"Machine {code} — {machine.Name}", events, ["Downtime, outages, work orders, its asset's history and alternates approved for it."]);
    }

    private static async Task<EntityHistory> ProjectAsync(IKernelUnitOfWork uow, string code, CancellationToken ct)
    {
        var projects = uow.Store<IProjectLookup>();
        var p = (await projects.FindProjectAsync(code, ct))!;
        var link = "project:" + code;
        var events = new List<HistoryEvent> { new(p.CreatedAt, "Created", $"{p.Type} project created as a draft, {p.Funding.ToString().ToLowerInvariant()} funding", p.CreatedBy, link, "Projects") };
        if (p.ApprovedAt is { } approved)
        {
            events.Add(new HistoryEvent(approved, "Approved", $"Approved{(p.Budget is { } b ? $" with a budget of {b.ToString("N2", CultureInfo.InvariantCulture)}" : string.Empty)}", p.ApprovedBy, link, "Projects"));
        }

        if (p.ClosedAt is { } closed)
        {
            events.Add(new HistoryEvent(closed, p.Status.ToString(), p.CloseReason ?? p.Status.ToString(), p.ClosedBy, link, "Projects"));
        }

        foreach (var c in await projects.ListChangesAsync(code, false, ct))
        {
            var what = c.Kind switch
            {
                ProjectChangeKind.Header => $"budget {c.Budget?.ToString("N2", CultureInfo.InvariantCulture) ?? "none"}, ends {c.EndDate:yyyy-MM-dd}, reference {c.FinancialReference ?? "none"}",
                ProjectChangeKind.AddLine => $"add {c.Quantity?.ToString("0.###", CultureInfo.InvariantCulture)} {c.PartNumber}",
                ProjectChangeKind.ChangeLine => $"line {c.LineNumber} to {c.Quantity?.ToString("0.###", CultureInfo.InvariantCulture)} {c.PartNumber}",
                _ => $"cancel line {c.LineNumber}",
            };
            events.Add(new HistoryEvent(c.RequestedAt, "Change requested", $"Change {c.ChangeId.ToString(CultureInfo.InvariantCulture)}: {what} — {c.Reason}", c.RequestedBy, link, "Project changes"));
            if (c.DecidedAt is { } decided)
            {
                events.Add(new HistoryEvent(decided, $"Change {c.Status.ToString().ToLowerInvariant()}", $"Change {c.ChangeId.ToString(CultureInfo.InvariantCulture)}: {c.DecisionReason}", c.DecidedBy, link, "Project changes"));
            }
        }

        foreach (var t in (await projects.ListTasksAsync(code, ct)).Where(t => t.CompletedOn is not null))
        {
            events.Add(new HistoryEvent(new DateTimeOffset(t.CompletedOn!.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), t.Milestone ? "Milestone met" : "Task done", t.Title, t.CompletedBy, link, "Project tasks"));
        }

        foreach (var o in (await uow.Store<IMaintenanceLookup>().ListOutagesAsync(true, ct)).Where(o => o.ProjectCode == code))
        {
            events.Add(new HistoryEvent(o.ProposedAt, "Outage proposed", $"{o.MachineCode} {o.StartsAt:yyyy-MM-dd HH:mm}–{o.EndsAt:HH:mm}: {o.Reason}", o.ProposedBy, "machine:" + o.MachineCode, "Outages"));
            if (o.DecidedAt is { } d)
            {
                events.Add(new HistoryEvent(d, $"Outage {o.Status.ToString().ToLowerInvariant()}", $"{o.MachineCode}: {o.DecisionReason}", o.DecidedBy, "machine:" + o.MachineCode, "Outages"));
            }
        }

        return new EntityHistory("project", code, $"Project {code} — {p.Title}", events, ["Approval, change requests and decisions, completed tasks and the outages it proposed."]);
    }

    private static async Task<EntityHistory> DocumentAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string code, CancellationToken ct)
    {
        var d = (await uow.Store<IDocumentLookup>().FindDocumentAsync(code, ct))!;
        if (!Visible(principal, d.ProgramId))
        {
            throw new CommandRejectedException("subject_not_found", $"Nothing called {code} is recorded.");
        }

        var link = "document:" + code;
        var events = new List<HistoryEvent> { new(d.CreatedAt, "Created", $"{d.Type} {d.Title} registered", d.CreatedBy, link, "Documents") };
        foreach (var r in d.Revisions)
        {
            events.Add(new HistoryEvent(r.AuthoredAt, "Revision drafted", $"Rev {r.Revision}{(r.ChangeNote is null ? string.Empty : ": " + r.ChangeNote)}", r.Author, link, "Document revisions"));
            if (r.SubmittedAt is { } s)
            {
                events.Add(new HistoryEvent(s, "Submitted", $"Rev {r.Revision} submitted for approval", r.SubmittedBy, link, "Document revisions"));
            }

            if (r.ApprovedAt is { } a)
            {
                events.Add(new HistoryEvent(a, "Approved", $"Rev {r.Revision} approved", r.ApprovedBy, link, "Document revisions"));
            }

            if (r.ReleasedAt is { } rel)
            {
                events.Add(new HistoryEvent(rel, "Released", $"Rev {r.Revision} released{(r.EffectiveFrom is { } f ? $", effective {f:yyyy-MM-dd}" : string.Empty)}", r.ReleasedBy, link, "Document revisions"));
            }

            if (r.ObsoletedAt is { } o)
            {
                events.Add(new HistoryEvent(o, "Obsoleted", $"Rev {r.Revision} made obsolete", r.ObsoletedBy, link, "Document revisions"));
            }
        }

        events.AddRange(ChangeEvents((await uow.Store<IChangeLookup>().ListChangesAsync(ct)).Where(c => Visible(principal, c.ProgramId) && c.ActiveLines.Any(l => l.DocumentNumber == code))));
        return new EntityHistory("document", code, $"Document {code} — {d.Title}", events, ["Revisions as drafted, approved and released, and the engineering changes that carried them."]);
    }

    private static async Task<EntityHistory> ChangeAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string code, CancellationToken ct)
    {
        var c = (await uow.Store<IChangeLookup>().FindChangeAsync(code, ct))!;
        if (!Visible(principal, c.ProgramId))
        {
            throw new CommandRejectedException("subject_not_found", $"Nothing called {code} is recorded.");
        }

        var events = ChangeEvents([c]);
        events.AddRange(c.Lines.Select(l => new HistoryEvent(l.AddedAt, "Line added", $"{l.Subject} {l.FromRevision ?? "new"} → {l.ToRevision}", l.AddedBy, "change:" + code, "Change lines")));
        events.AddRange(c.Reviews.Where(r => r.DecidedAt is not null).Select(r => new HistoryEvent(r.DecidedAt!.Value, "Review", $"Round {r.Round} {r.Kind} review: {r.Decision}{(r.Notes is null ? string.Empty : " — " + r.Notes)}", r.Reviewer, "change:" + code, "Change reviews")));
        return new EntityHistory("change", code, $"Change {code} — {c.Title}", events, ["The change as created, reviewed, approved, released and closed."]);
    }

    private static async Task<EntityHistory> ItemAsync(IKernelUnitOfWork uow, ForgePrincipal principal, string code, CancellationToken ct)
    {
        var engineering = uow.Store<IEngineeringLookup>();
        var item = (await engineering.FindItemAsync(code, ct))!;
        var link = "item:" + code;
        var events = new List<HistoryEvent>();
        foreach (var r in await engineering.ListRevisionsAsync(code, ct))
        {
            if (r.EffectiveFrom is { } from)
            {
                events.Add(new HistoryEvent(from, "Revision in force", $"Rev {r.Revision} in force", null, link, "Item revisions"));
            }

            if (r.EffectiveTo is { } to)
            {
                events.Add(new HistoryEvent(to, "Revision ended", $"Rev {r.Revision} no longer in force", null, link, "Item revisions"));
            }
        }

        events.AddRange(ChangeEvents((await uow.Store<IChangeLookup>().ListChangesAsync(ct)).Where(c => Visible(principal, c.ProgramId) && c.ActiveLines.Any(l => l.PartNumber == code))));
        events.AddRange(AlternateEvents((await uow.Store<IAlternateLookup>().ListAlternatesAsync(ct)).Where(a => (a.PrimaryPart == code || a.AlternatePart == code) && Visible(principal, a.ProgramId))));
        events.AddRange(ObsolescenceEvents((await uow.Store<IObsolescenceLookup>().ListObsolescenceAsync(false, ct)).Where(o => o.PartNumber == code)));
        return new EntityHistory("item", code, $"Part {code} — {item.Description}", events, ["Revisions in force, engineering changes, alternates and obsolescence records."]);
    }

    /// <summary>Downtime that has begun (a planned window still ahead is on Horizon, not here), and outages as proposed and decided.</summary>
    private static async Task<List<HistoryEvent>> MachineEventsAsync(IKernelUnitOfWork uow, string machine, DateTimeOffset now, CancellationToken ct)
    {
        var maintenance = uow.Store<IMaintenanceLookup>();
        var link = "machine:" + machine;
        var events = (await maintenance.ListDowntimeAsync(DateTimeOffset.UnixEpoch, new DateTimeOffset(2200, 1, 1, 0, 0, 0, TimeSpan.Zero), ct))
            .Where(d => d.MachineCode == machine && d.StartsAt <= now)
            .Select(d => new HistoryEvent(d.StartsAt, $"Downtime ({d.Kind})", $"{d.Reason}{(d.EndsAt is { } e ? $" until {e:yyyy-MM-dd HH:mm}" : " — still down")}", null, link, "Downtime"))
            .ToList();
        foreach (var o in (await maintenance.ListOutagesAsync(true, ct)).Where(o => o.MachineCode == machine))
        {
            events.Add(new HistoryEvent(o.ProposedAt, "Outage proposed", $"{o.StartsAt:yyyy-MM-dd HH:mm}–{o.EndsAt:yyyy-MM-dd HH:mm}: {o.Reason}", o.ProposedBy, link, "Outages"));
            if (o.DecidedAt is { } d)
            {
                events.Add(new HistoryEvent(d, $"Outage {o.Status.ToString().ToLowerInvariant()}", $"{o.DecisionReason}{(o.Impact is null ? string.Empty : " (impact seen: " + o.Impact + ")")}", o.DecidedBy, link, "Outages"));
            }
        }

        return events;
    }

    private static IEnumerable<HistoryEvent> WorkOrderEvents(IEnumerable<WorkOrderInfo> orders)
    {
        foreach (var w in orders)
        {
            var link = "work-order:" + w.WorkOrderId.ToString(CultureInfo.InvariantCulture);
            var name = $"Work order {w.WorkOrderId.ToString(CultureInfo.InvariantCulture)} ({w.Kind})";
            yield return new HistoryEvent(w.CreatedAt, "Work order raised", $"{name}: {w.Title}", w.CreatedBy, link, "Work orders");
            if (w.StartedAt is { } s)
            {
                yield return new HistoryEvent(s, "Work started", name, w.StartedBy, link, "Work orders");
            }

            if (w.CompletedAt is { } c)
            {
                yield return new HistoryEvent(c, "Work completed", $"{name}{(w.Findings is null ? string.Empty : ": " + w.Findings)}", w.CompletedBy, link, "Work orders");
            }

            if (w.ClosedAt is { } x)
            {
                yield return new HistoryEvent(x, w.Status == WorkOrderStatus.Cancelled ? "Work order cancelled" : "Work signed off", $"{name}{(w.CancelReason is null ? string.Empty : ": " + w.CancelReason)}", w.ClosedBy, link, "Work orders");
            }
        }
    }

    private static List<HistoryEvent> ChangeEvents(IEnumerable<ChangeInfo> changes)
    {
        var list = new List<HistoryEvent>();
        foreach (var c in changes)
        {
            var link = "change:" + c.Number;
            list.Add(new HistoryEvent(c.CreatedAt, "Change raised", $"{c.Number} {c.Title}", c.Originator, link, "Engineering changes"));
            if (c.ApprovedAt is { } a)
            {
                list.Add(new HistoryEvent(a, "Change approved", $"{c.Number} {c.Title}", c.ApprovedBy, link, "Engineering changes"));
            }

            if (c.ReleasedAt is { } r)
            {
                list.Add(new HistoryEvent(r, "Change released", $"{c.Number} {c.Title}{(c.EffectiveFrom is { } f ? $", effective {f:yyyy-MM-dd}" : string.Empty)}", c.ReleasedBy, link, "Engineering changes"));
            }

            if (c.ClosedAt is { } x)
            {
                list.Add(new HistoryEvent(x, $"Change {c.Status.ToString().ToLowerInvariant()}", $"{c.Number}{(c.ClosedReason is null ? string.Empty : ": " + c.ClosedReason)}", c.ClosedBy, link, "Engineering changes"));
            }
        }

        return list;
    }

    private static IEnumerable<HistoryEvent> AlternateEvents(IEnumerable<AlternateInfo> alternates)
    {
        foreach (var a in alternates)
        {
            var link = "item:" + a.PrimaryPart;
            var name = $"{a.AlternatePart} for {a.PrimaryPart} ({Alternates.Label(a.Kind).ToLowerInvariant()} {a.AlternateId.ToString(CultureInfo.InvariantCulture)})";
            yield return new HistoryEvent(a.ProposedAt, "Alternate proposed", name, a.ProposedBy, link, "Alternates");
            if (a.DecidedAt is { } d)
            {
                yield return new HistoryEvent(d, a.Status == AlternateStatus.Rejected ? "Alternate rejected" : "Alternate approved", $"{name}{(a.DecisionReason is null ? string.Empty : ": " + a.DecisionReason)}", a.DecidedBy, link, "Alternates");
            }

            if (a.ExpiredAt is { } x)
            {
                yield return new HistoryEvent(x, "Alternate expired", $"{name}{(a.ExpiredReason is null ? string.Empty : ": " + a.ExpiredReason)}", a.ExpiredBy, link, "Alternates");
            }
        }
    }

    private static IEnumerable<HistoryEvent> ObsolescenceEvents(IEnumerable<ObsolescenceInfo> records)
    {
        foreach (var o in records)
        {
            var link = "item:" + o.PartNumber;
            yield return new HistoryEvent(o.RecordedAt, "Obsolescence recorded", $"{o.PartNumber} {o.Status}{(o.EndOfSupport is { } e ? $", end of support {e:yyyy-MM-dd}" : string.Empty)}: {o.Evidence}", o.RecordedBy, link, "Obsolescence records");
            if (o.ReplacementDecidedAt is { } d)
            {
                yield return new HistoryEvent(d, $"Replacement {o.Replacement.ToString().ToLowerInvariant()}", $"{o.PartNumber} → {o.ReplacementPart}{(o.ReplacementReason is null ? string.Empty : ": " + o.ReplacementReason)}", o.ReplacementDecidedBy, link, "Obsolescence records");
            }
        }
    }

    private static bool Visible(ForgePrincipal principal, Guid? program) => program is not { } p || principal.Programs.Contains(new ProgramId(p));

    private void Demand(ForgePrincipal principal, string permission)
    {
        var decision = authorizer.Authorize(principal, permission, ResourceRef.Of("History"));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }
    }
}
