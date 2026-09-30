using Microsoft.Extensions.DependencyInjection;

namespace Forge.Planning.Infrastructure;

public static class PlanningRegistration
{
    public static IServiceCollection AddForgePlanning(this IServiceCollection services)
    {
        services.AddSingleton<ReadinessService>();
        return services;
    }
}
