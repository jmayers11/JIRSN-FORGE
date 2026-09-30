using System.Reflection;
using Forge.Kernel.Engines;
using NetArchTest.Rules;

namespace Forge.Architecture.Tests;

/// <summary>Architecture section 5, dependency rules, enforced as tests.</summary>
public class DependencyRules
{
    private static readonly Assembly Kernel = typeof(EvidenceNode).Assembly;
    private static readonly Assembly KernelPostgres = typeof(Forge.Kernel.Postgres.MigrationRunner).Assembly;

    [Fact]
    public void Kernel_has_no_infrastructure_dependencies()
    {
        var result = Types.InAssembly(Kernel)
            .ShouldNot()
            .HaveDependencyOnAny("Npgsql", "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Forge.Kernel.Postgres", "Forge.Host")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Engines_do_not_depend_on_persistence_or_security_infrastructure()
    {
        var result = Types.InAssembly(Kernel)
            .That().ResideInNamespace("Forge.Kernel.Engines")
            .ShouldNot()
            .HaveDependencyOnAny("Forge.Kernel.Access", "Forge.Kernel.Audit", "System.Data", "System.Net.Http")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Persistence_does_not_depend_on_the_web_host()
    {
        var result = Types.InAssembly(KernelPostgres)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.AspNetCore", "Forge.Host")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Kernel_references_only_framework_assemblies()
    {
        var references = Kernel.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        var foreign = references.Where(n => !n.StartsWith("System", StringComparison.Ordinal) && n != "netstandard" && n != "mscorlib").ToList();
        Assert.True(foreign.Count == 0, "Forge.Kernel references: " + string.Join(", ", foreign));
    }

    private static string Describe(TestResult result) =>
        result.IsSuccessful ? string.Empty : "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);
}
