using Forge.Kernel.Postgres;
using Forge.Quality.Contracts;

namespace Forge.Quality.Infrastructure;

internal sealed class PostgresCommercialCaseStore(PostgresUnitOfWork uow):ICommercialCaseLookup
{
    public async Task<CommercialCaseQuality> QualityForAsync(IReadOnlyCollection<long> orders,IReadOnlyCollection<string> partLotKeys,CancellationToken ct)
    {
        var inspections=await IdsAsync("SELECT inspection_id FROM quality.inspection WHERE order_number=ANY($1) ORDER BY inspection_id",[orders.ToArray()],ct);
        var ncrs=await IdsAsync("SELECT ncr_id FROM quality.ncr WHERE order_number=ANY($1) OR part_number||'/'||coalesce(lot_number,'')=ANY($2) ORDER BY ncr_id",[orders.ToArray(),partLotKeys.ToArray()],ct);
        var capas=await IdsAsync("SELECT capa_id FROM quality.capa WHERE ncr_id=ANY($1) ORDER BY capa_id",[ncrs.ToArray()],ct);
        return new(inspections,ncrs,capas);
    }
    private async Task<IReadOnlyList<long>> IdsAsync(string sql,object[] values,CancellationToken ct)
    {
        var result=new List<long>();await using var command=uow.Command(sql,values);await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))result.Add(reader.GetInt64(0));return result;
    }
}

