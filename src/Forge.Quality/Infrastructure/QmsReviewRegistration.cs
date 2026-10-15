using Forge.Kernel.Postgres;
using Forge.Quality.Contracts;
using Forge.Quality.Review;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Quality.Infrastructure;

public static class QmsReviewRegistration
{
    public static IServiceCollection AddForgeQmsReview(this IServiceCollection services)
    {
        services.AddForgeStore<IQmsReviewLookup>(uow => new PostgresQmsReviewStore(uow));
        services.AddForgeStore<IQmsReviewStore>(uow => new PostgresQmsReviewStore(uow));
        services.AddSingleton<QmsReviewService>();
        services.AddSingleton<ManagementReviewService>();
        services.AddSingleton<EvidenceMappingService>();
        return services;
    }
}
