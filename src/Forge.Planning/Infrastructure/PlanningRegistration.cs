using Forge.Kernel.Postgres;
using Forge.Resources.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Planning.Infrastructure;

public static class PlanningRegistration
{
    public static IServiceCollection AddForgePlanning(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(PlanningRegistration).Assembly, "planning");
        services.AddForgeStore<IPlanningStore>(uow => new PostgresPlanningStore(uow));
        services.AddForgeStore<IResourceUsage>(uow => new ResourceUsage(uow));
        services.AddForgeStore<IDemandStore>(uow => new PostgresDemandStore(uow));
        services.AddSingleton<DemandService>();
        services.AddSingleton<MaterialPlanningService>();
        services.AddSingleton<ReadinessService>();
        services.AddSingleton<ScheduleService>();
        return services;
    }
}
