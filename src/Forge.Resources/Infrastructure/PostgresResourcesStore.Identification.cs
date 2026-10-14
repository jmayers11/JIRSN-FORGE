using Forge.Resources.Contracts;

namespace Forge.Resources.Infrastructure;

internal sealed partial class PostgresResourcesStore
{
    // Identification can describe retained inactive records without making them valid execution references.
    public async Task<MachineInfo?> FindRecordedMachineAsync(string code, CancellationToken ct) =>
        (await Read(MachineSql + " WHERE m.code=$1", ReadMachine, ct, code)).SingleOrDefault();
    public async Task<WorkCenterInfo?> FindRecordedWorkCenterAsync(string code, CancellationToken ct) =>
        (await Read(WorkCenterSql + " WHERE code=$1", ReadWorkCenter, ct, code)).SingleOrDefault();
    public async Task<SafetyAreaInfo?> FindRecordedSafetyAreaAsync(string code, CancellationToken ct) =>
        (await Read(AreaSql + " WHERE code=$1", ReadArea, ct, code)).SingleOrDefault();
}
