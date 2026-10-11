using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Identity;

namespace Forge.Resources.Contracts;

/// <summary>A tool used by another context (V3.8): judged at that moment, and the use counted.</summary>
public static class ToolUse
{
    /// <summary>
    /// For other contexts (an inspection's gauge, a shop-floor start): checks the tool is valid for the use now and
    /// records the use in the same transaction. Throws <c>tool_not_valid</c> with the reasons when it is not.
    /// </summary>
    public static async Task<ToolValidity> RecordAsync(IKernelUnitOfWork uow, string rawCode, string? machine, string? activity, string reference, DateTimeOffset now, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var code = BusinessCode.Require(rawCode, "Tool");
        var lookup = uow.Store<IToolLookup>();
        var tool = await lookup.FindToolAsync(code, ct) ?? throw new CommandRejectedException("tool_not_found", $"Tool {code} does not exist.");
        var type = (await lookup.ListToolTypesAsync(ct)).First(t => t.Code == tool.TypeCode);
        var validity = ToolValidity.At(tool, type, await lookup.ListToolEventsAsync(code, ct), now, machine, activity);
        if (!validity.Valid)
        {
            throw new CommandRejectedException("tool_not_valid", string.Join(" ", validity.Problems));
        }

        var store = uow.Store<IToolStore>();
        await store.UpdateToolAsync(tool with { UsageCount = tool.UsageCount + 1, UsesSinceService = tool.UsesSinceService + 1, ChangedAt = now, ChangedBy = actor }, ct);
        await store.InsertToolEventAsync(new ToolEvent(0, code, ToolEventKind.Used, now, actor, Uses: 1, Reference: reference), ct);
        return validity;
    }
}
