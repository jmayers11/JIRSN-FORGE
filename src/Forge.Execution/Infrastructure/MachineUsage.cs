using Forge.Kernel.Postgres;
using Forge.Maintenance.Contracts;

namespace Forge.Execution.Infrastructure;

/// <summary>
/// Machine run hours from direct labor recorded on the machine. Intervals are merged, so a crew of two on
/// the machine for an hour is one run hour; trainers are not counted separately from their trainee.
/// Work still under way counts up to the time asked about.
/// </summary>
internal sealed class MachineUsage(PostgresUnitOfWork uow) : IMachineUsageLookup
{
    public async Task<decimal> RunHoursAsync(string machineCode, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var total = TimeSpan.Zero;
        foreach (var (start, end) in await MergedAsync(machineCode, from, to, ct))
        {
            total += end - start;
        }

        return decimal.Round((decimal)total.TotalHours, 2, MidpointRounding.AwayFromZero);
    }

    public async Task<DateTimeOffset?> ReachedAsync(string machineCode, DateTimeOffset from, decimal hours, DateTimeOffset asOf, CancellationToken ct)
    {
        var left = TimeSpan.FromHours((double)hours);
        foreach (var (start, end) in await MergedAsync(machineCode, from, asOf, ct))
        {
            if (end - start >= left)
            {
                return start + left;
            }

            left -= end - start;
        }

        return null;
    }

    private async Task<IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>> MergedAsync(string machineCode, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var raw = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        await using (var cmd = uow.Command(
            "SELECT started_at, ended_at FROM execution.labor WHERE machine_code = $1 AND role <> 'Trainer' AND started_at < $3 AND (ended_at IS NULL OR ended_at > $2) ORDER BY started_at",
            machineCode, from, to))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var start = reader.GetFieldValue<DateTimeOffset>(0);
                var end = reader.IsDBNull(1) ? to : reader.GetFieldValue<DateTimeOffset>(1);
                start = start < from ? from : start;
                end = end > to ? to : end;
                if (end > start)
                {
                    raw.Add((start, end));
                }
            }
        }

        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var span in raw.OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, span.End > merged[^1].End ? span.End : merged[^1].End);
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }
}
