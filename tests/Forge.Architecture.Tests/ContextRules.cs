using System.Reflection;
using NetArchTest.Rules;

namespace Forge.Architecture.Tests;

/// <summary>Bounded-context boundaries (architecture sections 4 and 5), enforced as tests.</summary>
public class ContextRules
{
    private static readonly Assembly Competency = typeof(Forge.Competency.CompetencyService).Assembly;
    private static readonly Assembly Resources = typeof(Forge.Resources.ResourcesService).Assembly;
    private static readonly Assembly Workforce = typeof(Forge.Workforce.WorkforceService).Assembly;
    private static readonly Assembly SafetyRules = typeof(Forge.SafetyRules.SafetyRuleService).Assembly;
    private static readonly Assembly Planning = typeof(Forge.Planning.ReadinessService).Assembly;
    private static readonly Assembly Engineering = typeof(Forge.Engineering.EngineeringService).Assembly;
    private static readonly Assembly Execution = typeof(Forge.Execution.ProductionOrderService).Assembly;
    private static readonly Assembly Maintenance = typeof(Forge.Maintenance.MaintenanceService).Assembly;
    private static readonly Assembly Procurement = typeof(Forge.Procurement.PurchasingService).Assembly;
    private static readonly Assembly Inventory = typeof(Forge.Inventory.InventoryService).Assembly;
    private static readonly Assembly Quality = typeof(Forge.Quality.QualityService).Assembly;

    public static TheoryData<string> Contexts => ["Competency", "Resources", "Workforce", "SafetyRules", "Engineering", "Execution", "Maintenance", "Quality", "Procurement", "Inventory", "Planning"];

    private static Assembly Get(string name) => name switch
    {
        "Competency" => Competency,
        "Resources" => Resources,
        "Workforce" => Workforce,
        "SafetyRules" => SafetyRules,
        "Engineering" => Engineering,
        "Execution" => Execution,
        "Maintenance" => Maintenance,
        "Procurement" => Procurement,
        "Inventory" => Inventory,
        "Quality" => Quality,
        _ => Planning,
    };

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Only_infrastructure_touches_the_database(string context)
    {
        var result = Types.InAssembly(Get(context))
            .That().DoNotResideInNamespaceEndingWith(".Infrastructure")
            .ShouldNot().HaveDependencyOnAny("Npgsql", "Microsoft.AspNetCore")
            .GetResult();
        Assert.True(result.IsSuccessful, "Violations: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Contexts_use_each_other_only_through_contracts(string context)
    {
        var assembly = Get(context);
        var others = new[] { Competency, Resources, Workforce, SafetyRules, Engineering, Execution, Maintenance, Quality, Procurement, Inventory, Planning }.Where(a => a != assembly);

        // Every public or internal type of another context that is not in its Contracts namespace is off limits.
        var forbidden = others
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Namespace is { } ns && !ns.EndsWith(".Contracts", StringComparison.Ordinal) && t.FullName is not null && !t.FullName.Contains('<', StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .ToArray();

        // Planning and the ReadinessService may use Workforce's assignment validation; that is the one documented exception.
        var allowed = context == "Planning" ? new[] { "Forge.Workforce.WorkforceService", "Forge.Workforce.AssignWork" } : [];
        var result = Types.InAssembly(assembly)
            .ShouldNot().HaveDependencyOnAny(forbidden.Except(allowed).ToArray())
            .GetResult();
        Assert.True(result.IsSuccessful, "Violations: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [InlineData("Planning", "Forge.Planning.Engines")]
    [InlineData("Execution", "Forge.Execution.Engines")]
    public void Engines_are_pure(string context, string ns)
    {
        var result = Types.InAssembly(Get(context))
            .That().ResideInNamespace(ns)
            .ShouldNot().HaveDependencyOnAny("Npgsql", "System.Data", "Forge.Kernel.Access", "Forge.Kernel.Postgres", "System.Net.Http", "Microsoft.Extensions")
            .GetResult();
        Assert.True(result.IsSuccessful, "Violations: " + string.Join(", ", result.FailingTypeNames ?? []));
    }
}
