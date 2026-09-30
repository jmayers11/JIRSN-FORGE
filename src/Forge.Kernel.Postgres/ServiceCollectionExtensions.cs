using Forge.Kernel.Access;
using Forge.Kernel.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Forge.Kernel.Postgres;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the kernel with PostgreSQL persistence. Background relays are added separately.</summary>
    public static IServiceCollection AddForgeKernel(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddSingleton<IKernelUnitOfWorkFactory, PostgresUnitOfWorkFactory>();
        services.AddSingleton<IForgeAuthorizer, ForgeAuthorizer>();
        services.AddSingleton<AccessService>();
        services.AddSingleton<KernelReadStore>();
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<OutboxRelay>();
        services.AddSingleton<SubscriptionPump>();
        return services;
    }

    public static IServiceCollection AddForgeKernelBackgroundServices(this IServiceCollection services)
    {
        services.AddHostedService<OutboxRelayService>();
        services.AddHostedService<SubscriptionPumpService>();
        return services;
    }
}
