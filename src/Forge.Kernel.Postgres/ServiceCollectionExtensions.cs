using Forge.Kernel.Access;
using Forge.Kernel.Commands;
using Forge.Kernel.Holds;
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
        services.AddSingleton<CommandPipeline>();
        services.AddSingleton<AccessService>();
        services.AddSingleton(new MigrationSource(typeof(MigrationRunner).Assembly, string.Empty));
        services.AddSingleton<KernelReadStore>();
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<OutboxRelay>();
        services.AddSingleton<SubscriptionPump>();
        services.AddForgeStore<IHoldLookup>(uow => new PostgresHoldStore(uow));
        services.AddForgeStore<IHoldStore>(uow => new PostgresHoldStore(uow));
        services.AddSingleton<HoldService>();
        return services;
    }

    /// <summary>Registers a bounded context's store so commands can reach it through the unit of work.</summary>
    public static IServiceCollection AddForgeStore<TStore>(this IServiceCollection services, Func<PostgresUnitOfWork, TStore> create)
        where TStore : class
    {
        ArgumentNullException.ThrowIfNull(create);
        services.AddSingleton(new StoreRegistration(typeof(TStore), uow => create(uow)));
        return services;
    }

    /// <summary>Registers a bounded context's migrations. Call in dependency order.</summary>
    public static IServiceCollection AddForgeMigrations(this IServiceCollection services, System.Reflection.Assembly assembly, string prefix)
    {
        services.AddSingleton(new MigrationSource(assembly, prefix));
        return services;
    }

    public static IServiceCollection AddForgeKernelBackgroundServices(this IServiceCollection services)
    {
        services.AddHostedService<OutboxRelayService>();
        services.AddHostedService<SubscriptionPumpService>();
        return services;
    }
}
