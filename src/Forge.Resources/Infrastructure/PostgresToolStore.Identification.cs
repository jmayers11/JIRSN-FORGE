using Forge.Resources.Contracts;

namespace Forge.Resources.Infrastructure;

internal sealed partial class PostgresToolStore
{
    public async Task<ToolTypeInfo?> FindToolTypeAsync(string code, CancellationToken ct) =>
        (await Read("SELECT code,name,kind,calibration_days,usage_limit,service_uses,active FROM resources.tool_type WHERE code=$1",
            r => new ToolTypeInfo(r.GetString(0), r.GetString(1), Enum.Parse<ToolKind>(r.GetString(2)), r.IsDBNull(3) ? null : r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetInt32(5), r.GetBoolean(6)), ct, code)).SingleOrDefault();
}
