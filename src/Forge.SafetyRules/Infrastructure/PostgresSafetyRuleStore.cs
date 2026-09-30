using Forge.Kernel.Postgres;
using Forge.SafetyRules.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Forge.SafetyRules.Infrastructure;

internal sealed class PostgresSafetyRuleStore(PostgresUnitOfWork uow) : ISafetyRuleStore, ISafetyRuleLookup
{
    private const string Select =
        """
        SELECT r.code, r.version, r.title, r.scope_kind, r.scope_code, r.activity_codes, r.minimum_physical_people, r.prohibit_lone_work,
               r.status, r.authored_by, r.approved_by, r.effective_from, r.effective_to,
               s.ordinal, s.name, s.required_count, s.satisfied_by, s.trainee_may_fill
        FROM safety.rule r
        LEFT JOIN safety.rule_slot s ON s.code = r.code AND s.version = r.version
        """;

    public Task<IReadOnlyList<SafetyRuleDefinition>> VersionsAsync(string code, CancellationToken ct) =>
        ReadAsync(Select + " WHERE r.code = $1 ORDER BY r.code, r.version, s.ordinal", ct, code);

    public Task<IReadOnlyList<SafetyRuleDefinition>> ListAllAsync(CancellationToken ct) =>
        ReadAsync(Select + " ORDER BY r.code, r.version, s.ordinal", ct);

    public Task<IReadOnlyList<SafetyRuleDefinition>> GetEffectiveRulesAsync(DateTimeOffset at, CancellationToken ct) =>
        ReadAsync(
            Select + " WHERE r.status <> 'Draft' AND r.effective_from <= $1 AND (r.effective_to IS NULL OR r.effective_to > $1) ORDER BY r.code, r.version, s.ordinal",
            ct,
            at);

    public async Task InsertDraftAsync(SafetyRuleDefinition rule, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        await using (var cmd = uow.Command(
            """
            INSERT INTO safety.rule (code, version, title, scope_kind, scope_code, activity_codes, minimum_physical_people, prohibit_lone_work, status, authored_by, authored_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, 'Draft', $9, $10)
            """,
            rule.Code, rule.Version, rule.Title, rule.ScopeKind.ToString(), rule.ScopeCode, TextArray(rule.ActivityCodes),
            rule.MinimumPhysicalPeople, rule.ProhibitLoneWork, rule.AuthoredBy, at))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        for (var i = 0; i < rule.Slots.Count; i++)
        {
            var slot = rule.Slots[i];
            await using var cmd = uow.Command(
                "INSERT INTO safety.rule_slot (code, version, ordinal, name, required_count, satisfied_by, trainee_may_fill) VALUES ($1, $2, $3, $4, $5, $6, $7)",
                rule.Code, rule.Version, i, slot.Name, slot.Count, TextArray(slot.SatisfiedBy), slot.TraineeMayFill);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task DeleteDraftAsync(string code, int version, CancellationToken ct)
    {
        await using var cmd = uow.Command("DELETE FROM safety.rule WHERE code = $1 AND version = $2 AND status = 'Draft'", code, version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetStatusAsync(string code, int version, RuleStatus status, string? approvedBy, DateTimeOffset? effectiveFrom, DateTimeOffset? effectiveTo, CancellationToken ct)
    {
        await using var cmd = uow.Command(
            "UPDATE safety.rule SET status = $3, approved_by = $4, effective_from = $5, effective_to = $6 WHERE code = $1 AND version = $2",
            code, version, status.ToString(),
            new NpgsqlParameter { Value = (object?)approvedBy ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text },
            new NpgsqlParameter { Value = (object?)effectiveFrom ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz },
            new NpgsqlParameter { Value = (object?)effectiveTo ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<SafetyRuleDefinition>> ReadAsync(string sql, CancellationToken ct, params object?[] args)
    {
        var rules = new List<SafetyRuleDefinition>();
        await using var cmd = uow.Command(sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        SafetyRuleDefinition? current = null;
        List<RoleSlot> slots = [];
        while (await reader.ReadAsync(ct))
        {
            var code = reader.GetString(0);
            var version = reader.GetInt32(1);
            if (current is null || current.Code != code || current.Version != version)
            {
                if (current is not null)
                {
                    rules.Add(current with { Slots = slots });
                }

                slots = [];
                current = new SafetyRuleDefinition(
                    code,
                    version,
                    reader.GetString(2),
                    Enum.Parse<RuleScopeKind>(reader.GetString(3)),
                    reader.GetString(4),
                    reader.GetFieldValue<string[]>(5),
                    reader.GetInt32(6),
                    reader.GetBoolean(7),
                    [],
                    Enum.Parse<RuleStatus>(reader.GetString(8)),
                    reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                    reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12));
            }

            if (!reader.IsDBNull(13))
            {
                slots.Add(new RoleSlot(reader.GetString(14), reader.GetInt32(15), reader.GetFieldValue<string[]>(16), reader.GetBoolean(17)));
            }
        }

        if (current is not null)
        {
            rules.Add(current with { Slots = slots });
        }

        return rules;
    }

    private static NpgsqlParameter TextArray(IEnumerable<string> values) =>
        new() { Value = values.ToArray(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text };
}

public static class SafetyRulesRegistration
{
    public static IServiceCollection AddForgeSafetyRules(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(SafetyRulesRegistration).Assembly, "safety");
        services.AddForgeStore<ISafetyRuleStore>(uow => new PostgresSafetyRuleStore(uow));
        services.AddForgeStore<ISafetyRuleLookup>(uow => new PostgresSafetyRuleStore(uow));
        services.AddSingleton<SafetyRuleService>();
        return services;
    }
}
