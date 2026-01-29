using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.Domain.Analyzers;
using Gvdasa.GVmodeloexemploapi.Domain.Configuration;
using Gvdasa.GVmodeloexemploapi.Domain.Services;
using Gvdasa.GVmodeloexemploapi.Infra.Services.FinOps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gvdasa.GVmodeloexemploapi.Domain.DependencyInjection;

[ExcludeFromCodeCoverage]
public static class FinOpsServiceCollectionExtensions
{
    public static void AdicionarFinOps(this IServiceCollection collection, IConfiguration configuration)
    {
        // Registrar configurações
        collection.Configure<AnalyzerOptions>(configuration.GetSection(AnalyzerOptions.SectionName));
        
        // Registrar serviços de infraestrutura
        collection.AddScoped<ICostManagementService, CostManagementService>();
        collection.AddScoped<IMetricsService, MetricsService>();
        collection.AddScoped<IResourceGraphService, ResourceGraphService>();
        
        // Registrar analyzers
        collection.AddScoped<IResourceAnalyzer, VmAnalyzer>();
        collection.AddScoped<IResourceAnalyzer, DiskAnalyzer>();
        collection.AddScoped<IResourceAnalyzer, AppServiceAnalyzer>();
        collection.AddScoped<IResourceAnalyzer, SqlAnalyzer>();
        
        // Registrar orquestrador principal
        collection.AddScoped<ICostAnalysisOrchestrator, CostAnalysisOrchestrator>();
        
        // Configurar HttpClient para APIs do Azure
        collection.AddHttpClient<ICostManagementService, CostManagementService>(client =>
        {
            client.BaseAddress = new Uri("https://management.azure.com/");
            client.DefaultRequestHeaders.Add("User-Agent", "GV-FinOps/1.0");
        });
        
        collection.AddHttpClient<IMetricsService, MetricsService>(client =>
        {
            client.BaseAddress = new Uri("https://management.azure.com/");
            client.DefaultRequestHeaders.Add("User-Agent", "GV-FinOps/1.0");
        });
        
        collection.AddHttpClient<IResourceGraphService, ResourceGraphService>(client =>
        {
            client.BaseAddress = new Uri("https://management.azure.com/");
            client.DefaultRequestHeaders.Add("User-Agent", "GV-FinOps/1.0");
        });
    }
}