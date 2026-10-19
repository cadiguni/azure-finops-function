using Microsoft.Extensions.DependencyInjection;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public static class GovernanceServiceRegistration
{
    public static IServiceCollection AddFinOpsGovernance(this IServiceCollection services)
    {
        services.AddScoped<IGovernanceBudgetRepository, BlobGovernanceBudgetRepository>();
        services.AddScoped<IGovernanceAuditLogRepository, GovernanceAuditLogRepository>();
        services.AddScoped<GovernanceDataHealthService>();
        services.AddScoped<GovernanceDataHealthOverviewService>();
        services.AddScoped<GovernanceBudgetService>();
        return services;
    }
}
