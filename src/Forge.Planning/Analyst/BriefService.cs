using System.Globalization;
using Forge.Competency.Contracts;
using Forge.Execution.Contracts;
using Forge.Inventory.Contracts;
using Forge.Kernel.Access;
using Forge.Kernel.Identity;
using Forge.Kernel.Security;
using Forge.Maintenance.Contracts;
using Forge.Planning.Engines;
using Forge.Planning.Reporting;
using Forge.Quality.Contracts;
using Forge.Resources.Contracts;
using Forge.Workforce.Contracts;
using NodaTime;

namespace Forge.Planning.Analyst;

/// <summary>
/// Operational brief (spec §67) for a shift: orders needing attention, what changed since the previous
/// shift, constraints and the projected bottleneck in the shift, staffing, machines and maintenance,
/// material arrivals, quality holds and qualification expiries. Each section is built only when the
/// reader may read what it draws on; otherwise it is named as omitted. Every line comes from records.
/// </summary>
public sealed class BriefService(IKernelUnitOfWorkFactory unitOfWork, IForgeAuthorizer authorizer, TimeProvider time, HorizonHistoryService history)
{
    public async Task<OperationalBrief> BriefAsync(ForgePrincipal principal, string? shiftCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Demand(principal, Permissions.AnalystUse);
        var now = time.GetUtcNow();
        await using var uow = await unitOfWork.BeginSnapshotAsync(ct);
        var resources = uow.Store<IResourcesLookup>();
        var sites = await resources.ListSitesAsync(ct);
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(sites.Count > 0 ? sites[0].TimeZone : "America/New_York") ?? DateTimeZoneProviders.Tzdb["America/New_York"];
        var shifts = await resources.ListShiftsAsync(ct);

        // The shift window: the named shift's current or next occurrence; otherwise the shift running now; otherwise the next 12 hours.
        var (from, to, shift) = Window(shifts, shiftCode, now, zone);
        var since = shift is null ? now.AddHours(-12) : from - (to - from) < now.AddHours(-24) ? now.AddHours(-24) : from - (to - from);
        if (since > now)
        {
            since = now.AddHours(-12);
        }

        var title = shift is null
            ? $"Operational brief — {AnalystService.Local(from, zone)} to {AnalystService.Local(to, zone)}"
            : $"{shift.Name} operational brief — {AnalystService.Local(from, zone)} to {AnalystService.Local(to, zone)}";
        var sections = new List<BriefSection>();
        var headlines = new List<string>();
        var omitted = new List<string>();
        bool Can(string permission, string section)
        {
            var ok = authorizer.Authorize(principal, permission, ResourceRef.Of("Analyst")).IsPermitted;
            if (!ok)
            {
                omitted.Add(section);
            }

            return ok;
        }

        var orders = (await uow.Store<IExecutionLookup>().ListOrdersAsync(ct)).ToDictionary(o => o.OrderNumber);
        bool Visible(Guid? program) => program is not { } p || principal.Programs.Contains(new ProgramId(p));
        bool OrderVisible(long n) => !orders.TryGetValue(n, out var o) || Visible(o.ProgramId);
        string L(DateTimeOffset? at) => AnalystService.Local(at, zone);

        // ---- schedule: attention, constraints, bottleneck
        if (Can(Permissions.ScheduleRead, "orders, changes and constraints (needs schedule access)"))
        {
            var store = uow.Store<IPlanningStore>();
            var approved = await store.LatestAsync(ScheduleStatus.Approved, true, ct);
            var attention = (approved?.Result?.Orders ?? []).Where(o => o.Status != DeliveryStatus.OnTime && Visible(o.ProgramId))
                .OrderBy(o => o.Status == DeliveryStatus.Late ? 0 : o.Status == DeliveryStatus.AtRisk ? 2 : 1).ThenBy(o => o.DueDate).ToList();
            sections.Add(new BriefSection(
                "Orders needing attention",
                attention.Count == 0 ? "ok" : "bad",
                attention.Take(10).Select(o => new AnalystFact($"Order {o.OrderNumber} ({o.PartNumber}), due {o.DueDate:yyyy-MM-dd}: {o.Explanation}", "order:" + Id(o.OrderNumber), o.Status == DeliveryStatus.AtRisk ? "warn" : "bad")).ToList(),
                approved is null ? "No schedule has been approved yet." : attention.Count == 0 ? "Every order in the approved schedule is on time." : null));
            headlines.Add(attention.Count == 0 ? "No orders require attention." : $"{attention.Count} order(s) require attention.");

            var comparison = await history.CompareAsync(principal, since, now, false, ct);
            var priorities = comparison.Orders.Where(o => o.PriorityBefore is not null && o.PriorityAfter is not null && o.PriorityBefore != o.PriorityAfter).ToList();
            var changeFacts = comparison.Orders.Take(8).Select(o => new AnalystFact($"Order {o.OrderNumber}: {o.Explanation}", "order:" + Id(o.OrderNumber), o.Change == "Later" ? "bad" : o.Change == "Earlier" ? "ok" : "warn")).ToList();
            changeFacts.AddRange(comparison.ApprovedBetween.Select(v => new AnalystFact($"Schedule {v.VersionId} approved {L(v.ApprovedAt)}: {v.Summary}", "schedule:" + Id(v.VersionId))));
            sections.Add(new BriefSection($"Changes since {L(since)}", changeFacts.Count == 0 ? "ok" : "info", changeFacts, comparison.Headline));
            if (priorities.Count > 0)
            {
                headlines.Add($"{priorities.Count} priorit{(priorities.Count == 1 ? "y" : "ies")} changed since {L(since)}.");
            }

            var upcoming = (await store.ListConstraintsAsync(false, ct))
                .Where(c => c.WindowFrom < to && c.WindowUntil > from && (c.AffectedOrders.Count == 0 || c.AffectedOrders.Any(OrderVisible)))
                .OrderBy(c => c.WindowFrom).ToList();
            var bottleneck = (approved?.Result?.Operations ?? [])
                .Where(op => Visible(op.ProgramId) && op.Start is { } s && s >= from && s < to && op.BindingConstraint is not ("Now" or "Predecessor"))
                .GroupBy(op => (op.BindingConstraint, Where: op.MachineCode ?? op.WorkCenterCode))
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Min(op => op.Start)).FirstOrDefault();
            var constraintFacts = upcoming.Take(8).Select(c => new AnalystFact($"{c.Category} at {c.Subject} from {L(c.WindowFrom)}: {c.Finding}", "constraint:" + Id(c.ConstraintId), "warn")).ToList();
            if (bottleneck is not null)
            {
                var first = bottleneck.Min(op => op.Start);
                constraintFacts.Insert(0, new AnalystFact($"Projected bottleneck: {bottleneck.Key.BindingConstraint} at {bottleneck.Key.Where}, beginning about {L(first)} ({bottleneck.Count()} operation(s) held).", "schedule:" + Id(approved!.VersionId), "warn"));
                headlines.Add($"Highest projected constraint: {bottleneck.Key.BindingConstraint} at {bottleneck.Key.Where} beginning about {L(first)}.");
            }

            sections.Add(new BriefSection("Constraints in the shift", constraintFacts.Count == 0 ? "ok" : "warn", constraintFacts, constraintFacts.Count == 0 ? "No open constraints fall in this shift." : null));
        }

        // ---- staffing
        if (Can(Permissions.WorkforceRead, "staffing (needs workforce access)"))
        {
            var workforce = uow.Store<IWorkforceLookup>();
            var workers = (await workforce.GetWorkerStatesAsync(now, ct)).Where(w => w.IsActive).ToDictionary(w => w.PersonId);
            var away = (await workforce.ListUnavailabilityAsync(from, to, ct)).Where(u => workers.ContainsKey(u.PersonId)).GroupBy(u => u.PersonId).ToList();
            var onShift = shift is null ? workers.Values.ToList() : workers.Values.Where(w => w.ShiftCode == shift.Code).ToList();
            var facts = away.Select(g => new AnalystFact($"{workers[g.Key].DisplayName} ({workers[g.Key].HomeWorkCenterCode}) unavailable {L(g.Min(u => u.StartsAt))} – {L(g.Max(u => u.EndsAt))}.", "person:" + g.Key.ToString("D", CultureInfo.InvariantCulture), "warn")).ToList();
            sections.Add(new BriefSection("Staffing", away.Count == 0 ? "ok" : "warn", facts,
                $"{onShift.Count} active worker(s) {(shift is null ? "in total" : "on this shift")}; {away.Count} with recorded unavailability in the window. Reasons are not shown."));
            if (away.Count > 0)
            {
                headlines.Add($"{away.Count} person(s) unavailable during the shift.");
            }
        }

        // ---- machines and maintenance
        if (Can(Permissions.MaintenanceRead, "machines and maintenance (needs maintenance access)"))
        {
            var maintenance = uow.Store<IMaintenanceLookup>();
            var windows = await maintenance.ListDowntimeAsync(since, to, ct);
            var facts = new List<AnalystFact>();
            facts.AddRange(windows.Where(w => w.Covers(now)).Select(w => new AnalystFact($"{w.MachineCode} unavailable now ({w.Kind}) since {L(w.StartsAt)} — {w.Reason}", "machine:" + w.MachineCode, "bad")));
            facts.AddRange(windows.Where(w => w.StartsAt > now && w.StartsAt < to).Select(w => new AnalystFact($"{w.MachineCode} {w.Kind} from {L(w.StartsAt)}" + (w.EndsAt is { } e ? $" to {L(e)}" : string.Empty) + $" — {w.Reason}", "machine:" + w.MachineCode, "warn")));
            facts.AddRange(windows.Where(w => w.EndsAt is { } e && e > since && e <= now).Select(w => new AnalystFact($"{w.MachineCode} back in service {L(w.EndsAt)} after {w.Reason}", "machine:" + w.MachineCode, "ok")));
            var done = (await maintenance.ListWorkOrdersAsync(true, ct)).Where(o => o.CompletedAt is { } c && c > since && c <= now).ToList();
            facts.AddRange(done.Select(o => new AnalystFact($"{o.MachineCode} maintenance completed {L(o.CompletedAt)}: {o.Title}", "work-order:" + Id(o.WorkOrderId), "ok")));
            sections.Add(new BriefSection("Machines and maintenance", facts.Any(f => f.Tone == "bad") ? "bad" : facts.Count == 0 ? "ok" : "info", facts, facts.Count == 0 ? "No machine changes." : null));
            foreach (var o in done.Take(2))
            {
                headlines.Add($"{o.MachineCode} maintenance completed at {L(o.CompletedAt)}.");
            }
        }

        // ---- material arrivals
        if (Can(Permissions.InventoryRead, "material arrivals (needs inventory access)"))
        {
            var received = (await uow.Store<IInventoryLookup>().ListTransactionsBetweenAsync(since, now, ct))
                .Where(t => t.Kind is InventoryTransactionKind.Receipt && (t.OrderNumber is not { } n || OrderVisible(n))).ToList();
            var facts = received.Take(10).Select(t => new AnalystFact($"{Fmt(t.Quantity)} {t.PartNumber} received at {L(t.At)}" + (t.PoNumber is { } po ? $" (PO {po})" : string.Empty) + (t.ToLocation is { } loc ? $" into {loc}" : string.Empty), "transaction:" + Id(t.TransactionId), "ok")).ToList();
            sections.Add(new BriefSection("Material arrivals", "info", facts, received.Count == 0 ? "No receipts since the previous shift." : null));
        }

        // ---- quality holds
        if (Can(Permissions.QualityRead, "quality holds (needs quality access)"))
        {
            var open = (await uow.Store<IQualityLookup>().ListNcrsAsync(false, ct)).Where(n => n.OrderNumber is not { } o || OrderVisible(o)).ToList();
            var holds = open.Where(n => n.Status == NcrStatus.Open).ToList();
            var facts = open.Take(10).Select(n => new AnalystFact($"NCR {n.NcrId} ({n.Status}): {n.Defect} on {n.PartNumber}" + (n.OrderNumber is { } o ? $", order {o}" : string.Empty) + (n.Status == NcrStatus.Open ? " — holds the order" : string.Empty), "ncr:" + Id(n.NcrId), n.Status == NcrStatus.Open ? "bad" : "warn")).ToList();
            sections.Add(new BriefSection("Quality holds", holds.Count > 0 ? "bad" : "ok", facts, open.Count == 0 ? "No open nonconformances." : null));
            if (holds.Count > 0)
            {
                headlines.Add($"{holds.Count} quality hold(s) open.");
            }
        }

        // ---- qualification expiries
        if (Can(Permissions.CompetencyRead, "qualification expiries (needs competency access)"))
        {
            var workers = (await uow.Store<IWorkforceLookup>().GetWorkerStatesAsync(now, ct)).Where(w => w.IsActive).ToDictionary(w => w.PersonId);
            var expiring = (await uow.Store<ICompetencyLookup>().GetQualificationsAsync(workers.Keys.Select(id => new PersonId(id)).ToList(), ct))
                .Where(q => q.Status == QualificationStatus.Qualified && q.ExpiresAt is { } e && e > now && e <= now.AddDays(14))
                .OrderBy(q => q.ExpiresAt).ToList();
            var facts = expiring.Take(10).Select(q => new AnalystFact($"{workers[q.PersonId].DisplayName}: {q.Code} expires {L(q.ExpiresAt)}", "person:" + q.PersonId.ToString("D", CultureInfo.InvariantCulture), "warn")).ToList();
            sections.Add(new BriefSection("Training expirations (next 14 days)", expiring.Count > 0 ? "warn" : "ok", facts, expiring.Count == 0 ? "No qualifications expire in the next 14 days." : null));
            if (expiring.Count > 0)
            {
                headlines.Add($"{expiring.Count} qualification(s) expire in the next 14 days.");
            }
        }

        return new OperationalBrief(title, shift?.Code, from, to, since, now, headlines, sections, omitted);
    }

    internal static (DateTimeOffset From, DateTimeOffset To, ShiftInfo? Shift) Window(IReadOnlyList<ShiftInfo> shifts, string? shiftCode, DateTimeOffset now, DateTimeZone zone)
    {
        var today = Instant.FromDateTimeOffset(now).InZone(zone).Date;
        var occurrences = new List<(DateTimeOffset Start, DateTimeOffset End, ShiftInfo Shift)>();
        for (var d = today.PlusDays(-1); d <= today.PlusDays(2); d = d.PlusDays(1))
        {
            foreach (var s in shifts.Where(s => s.Days.Contains((int)d.DayOfWeek)))
            {
                var start = zone.AtLeniently(d.At(new LocalTime(s.Start.Hour, s.Start.Minute))).ToDateTimeOffset().ToUniversalTime();
                occurrences.Add((start, start.AddMinutes(s.DurationMinutes), s));
            }
        }

        var candidates = occurrences.Where(o => o.End > now && (shiftCode is null || string.Equals(o.Shift.Code, shiftCode, StringComparison.OrdinalIgnoreCase))).OrderBy(o => o.Start).ToList();
        if (shiftCode is null)
        {
            // The shift running now, else the next one to start.
            var current = candidates.Where(o => o.Start <= now).OrderBy(o => o.End).ToList();
            candidates = current.Count > 0 ? current : candidates;
        }

        return candidates.Count > 0 ? (candidates[0].Start, candidates[0].End, candidates[0].Shift) : (now, now.AddHours(12), null);
    }

    private void Demand(ForgePrincipal principal, string permission)
    {
        var decision = authorizer.Authorize(principal, permission, ResourceRef.Of("Analyst"));
        if (!decision.IsPermitted)
        {
            throw new ForgeAuthorizationException(decision);
        }
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static string Fmt(decimal d) => (d / 1.000000000000000000000000000000000m).ToString("0.##", CultureInfo.InvariantCulture);
}
