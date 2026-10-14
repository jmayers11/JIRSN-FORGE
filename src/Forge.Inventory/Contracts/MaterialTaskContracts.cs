namespace Forge.Inventory.Contracts;

/// <summary>What a material task does (V3.4).</summary>
public enum MaterialTaskKind
{
    /// <summary>Material for a production order taken to its staging or point-of-use location.</summary>
    Stage,

    /// <summary>One line of a kit: several parts for an order gathered under one kit code.</summary>
    Kit,

    /// <summary>Received or returned material taken to where it is stored.</summary>
    PutAway,

    /// <summary>A point-of-use location topped up from stores, for no order in particular.</summary>
    Replenish,
}

/// <summary>Requested, then allocated from specific stock, picked (location and lot scanned), delivered (destination scanned).</summary>
public enum MaterialTaskStatus
{
    Requested,
    Allocated,
    Picked,
    Delivered,
    Cancelled,
}

/// <summary>The exact part and physical identity read by a typed material scan; no authority or quantity is implied.</summary>
public sealed record ScannedMaterialIdentity(string PartNumber, string LotNumber);

/// <summary>A piece of material-movement work (V3.4). Only delivery moves stock in the ledger.</summary>
public sealed record MaterialTask(
    long TaskId,
    MaterialTaskKind Kind,
    long? OrderNumber,
    string? KitCode,
    string PartNumber,
    string? LotNumber,
    decimal Quantity,
    string? FromLocation,
    string ToLocation,
    MaterialTaskStatus Status,
    decimal? PickedQuantity,
    string? Reason,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    string? AllocatedBy = null,
    DateTimeOffset? AllocatedAt = null,
    string? PickedBy = null,
    DateTimeOffset? PickedAt = null,
    string? DeliveredBy = null,
    DateTimeOffset? DeliveredAt = null,
    long? TransactionId = null,
    string? CancelledBy = null,
    DateTimeOffset? CancelledAt = null,
    string? CancelReason = null)
{
    /// <summary>Stock this task holds back from other allocations: allocated or picked, not yet delivered.</summary>
    public decimal Reserved => Status switch
    {
        MaterialTaskStatus.Allocated => Quantity,
        MaterialTaskStatus.Picked => PickedQuantity ?? Quantity,
        _ => 0,
    };
}

/// <summary>What is counted how often (V3.4): every balance in a location and the locations in it, or of a part.</summary>
public sealed record CycleCountProgram(
    string ProgramCode,
    string Name,
    string? LocationCode,
    string? PartNumber,
    int FrequencyDays,
    bool Active,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? RetiredBy = null,
    DateTimeOffset? RetiredAt = null);

/// <summary>When a balance was last counted (with or without a variance).</summary>
public sealed record LastCount(string PartNumber, string LocationCode, string? LotNumber, DateTimeOffset CountedAt);

/// <summary>A balance a cycle-count program says is due (V3.4).</summary>
public sealed record CountDue(string ProgramCode, string PartNumber, string LocationCode, string? LotNumber, StockStatus Status, decimal Quantity, DateTimeOffset? LastCountedAt, DateOnly DueOn)
{
    /// <summary>
    /// Every balance in scope of an active program whose last count is older than the program's frequency (or that
    /// was never counted) is due; a balance in scope of several programs is due once, under the most frequent.
    /// </summary>
    public static IReadOnlyList<CountDue> For(
        IEnumerable<CycleCountProgram> programs,
        IEnumerable<StockBalance> balances,
        IReadOnlyList<LocationInfo> locations,
        IEnumerable<LastCount> lastCounts,
        DateOnly today,
        Func<DateTimeOffset, DateOnly> localDate)
    {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(balances);
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(lastCounts);
        ArgumentNullException.ThrowIfNull(localDate);
        var byCode = locations.ToDictionary(l => l.Code, StringComparer.Ordinal);
        bool Within(string location, string ancestor)
        {
            for (var at = byCode.GetValueOrDefault(location); at is not null; at = at.ParentCode is { } p ? byCode.GetValueOrDefault(p) : null)
            {
                if (at.Code == ancestor)
                {
                    return true;
                }
            }

            return location == ancestor;
        }

        var last = lastCounts.GroupBy(c => (c.PartNumber, c.LocationCode, c.LotNumber)).ToDictionary(g => g.Key, g => g.Max(c => c.CountedAt));
        var active = programs.Where(p => p.Active).OrderBy(p => p.FrequencyDays).ThenBy(p => p.ProgramCode, StringComparer.Ordinal).ToList();
        var due = new List<CountDue>();
        foreach (var b in balances.Where(b => b.Quantity != 0 && byCode.GetValueOrDefault(b.LocationCode) is { InCompanyCustody: true }))
        {
            var program = active.FirstOrDefault(p => (p.LocationCode is null || Within(b.LocationCode, p.LocationCode)) && (p.PartNumber is null || p.PartNumber == b.PartNumber));
            if (program is null)
            {
                continue;
            }

            DateTimeOffset? counted = last.TryGetValue((b.PartNumber, b.LocationCode, b.LotNumber), out var at) ? at : null;
            var dueOn = counted is { } c ? localDate(c).AddDays(program.FrequencyDays) : today;
            if (dueOn <= today)
            {
                due.Add(new CountDue(program.ProgramCode, b.PartNumber, b.LocationCode, b.LotNumber, b.Status, b.Quantity, counted, dueOn));
            }
        }

        return due.OrderBy(d => d.DueOn).ThenBy(d => d.LocationCode, StringComparer.Ordinal).ThenBy(d => d.PartNumber, StringComparer.Ordinal).ToList();
    }
}
