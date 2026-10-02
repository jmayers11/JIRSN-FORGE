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
        services.AddSingleton<ReadinessService>();
        services.AddSingleton<ScheduleService>();
        return services;
    }
}
