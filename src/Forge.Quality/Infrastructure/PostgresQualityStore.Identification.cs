using Forge.Quality.Contracts;

namespace Forge.Quality.Infrastructure;

internal sealed partial class PostgresQualityStore
{
    public async Task<InspectionInfo?> FindInspectionAsync(long inspectionId, CancellationToken ct)
    {
        var heads = await ReadAsync("SELECT inspection_id,order_number,sequence,part_number,quantity_inspected,result,gauge_code,notes,inspector,at,ncr_id FROM quality.inspection WHERE inspection_id=$1",
            r => new InspectionInfo(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), Dec(r,4), Enum.Parse<InspectionResult>(r.GetString(5)),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.GetFieldValue<DateTimeOffset>(9), [], r.IsDBNull(10) ? null : r.GetInt64(10)), ct, inspectionId);
        var head = heads.SingleOrDefault();
        if (head is null) return null;
        var results = await ReadAsync("SELECT number,name,kind,lower_limit,upper_limit,measured,accepted,pass FROM quality.inspection_result WHERE inspection_id=$1 ORDER BY number",
            r => new CharacteristicResult(r.GetInt32(0), r.GetString(1), Enum.Parse<CharacteristicKind>(r.GetString(2)), NDec(r,3), NDec(r,4), NDec(r,5),
                r.IsDBNull(6) ? null : r.GetBoolean(6), r.GetBoolean(7)), ct, inspectionId);
        return head with { Results = results };
    }
}
