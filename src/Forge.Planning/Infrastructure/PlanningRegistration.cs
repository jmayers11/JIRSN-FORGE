using Forge.Kernel.Postgres;
using Forge.Execution.Contracts;
using Forge.Resources.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Forge.Planning.Infrastructure;

public static class PlanningRegistration
{
    public static IServiceCollection AddForgePlanning(this IServiceCollection services)
    {
        services.AddForgeMigrations(typeof(PlanningRegistration).Assembly, "planning");
        services.AddForgeStore<IPlanningStore>(uow => new PostgresPlanningStore(uow));
        services.AddForgeStore<IResourceUsage>(uow => new ResourceUsage(uow));
        services.AddForgeStore<IDemandStore>(uow => new PostgresDemandStore(uow));
        services.AddForgeStore<IGoalStore>(uow => new PostgresGoalStore(uow));
        services.AddForgeStore<Reporting.IReportDefinitionStore>(uow => new PostgresReportStore(uow));
        services.AddSingleton<DemandService>();
        services.AddSingleton<MaterialPlanningService>();
        services.AddSingleton<ReadinessService>();
        services.AddSingleton<DocumentationService>();
        services.AddSingleton<ChangeImpactService>();
        services.AddSingleton<ObsolescenceImpactService>();
        services.AddSingleton<PurchaseTraceService>();
        services.AddSingleton<MaintenancePlanningService>();
        services.AddSingleton<ProjectPlanningService>();
        services.AddSingleton<GenealogyService>();
        services.AddSingleton<GenealogyGraphService>();
        services.AddSingleton<MaterialFlowService>();
        services.AddSingleton<ProductionActualsQueries>();
        services.AddSingleton<HistoryService>();
        services.AddSingleton<ScheduleService>();
        services.AddSingleton<IPriorityImpactProvider, PriorityImpactProvider>();
        services.AddSingleton<IShiftFactsProvider, ShiftFactsProvider>();
        services.AddSingleton<GoalService>();
        services.AddSingleton<ProgramHorizonService>();
        services.AddSingleton<Reporting.ReportService>();
        services.AddSingleton<Reporting.ReportDefinitionService>();
        services.AddSingleton<Reporting.HorizonHistoryService>();
        services.AddSingleton<Analyst.BriefService>();
        services.AddSingleton<Analyst.AnalystService>();
        services.TryAddSingleton<Analyst.IAnalystNarrator, Analyst.DisabledNarrator>();
        return services;
    }
}
