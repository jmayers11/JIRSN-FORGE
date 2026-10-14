using Forge.Inventory.Contracts;

namespace Forge.Inventory.Infrastructure;

internal sealed partial class PostgresInventoryStore
{
    public async Task<bool> IsRecordedRemnantAsync(string partNumber, string lotNumber, CancellationToken ct)
    {
        await using var command = uow.Command("SELECT EXISTS(SELECT 1 FROM inventory.transformation_line WHERE part_number=$1 AND lot_number=$2 AND role='Remnant')", partNumber, lotNumber);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<TransformationOutputInfo?> FindTransformationOutputAsync(long transformationId, int lineNumber, CancellationToken ct) =>
        (await ReadAsync("SELECT t.transformation_id,t.order_number,t.operation_sequence,l.line_number,l.role,l.part_number,l.lot_number,l.quantity,l.source_part,l.source_lot,l.location_code,l.transaction_id FROM inventory.transformation t JOIN inventory.transformation_line l USING(transformation_id) WHERE t.transformation_id=$1 AND l.line_number=$2",
            r => new TransformationOutputInfo(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt32(2),
                new TransformationLine(r.GetInt32(3), Enum.Parse<TransformationRole>(r.GetString(4)), r.GetString(5), NullableText(r,6), Dec(r,7),
                    NullableText(r,8), NullableText(r,9), NullableText(r,10), r.IsDBNull(11) ? null : r.GetInt64(11))), ct, transformationId, lineNumber)).SingleOrDefault();
}
