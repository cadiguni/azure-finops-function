using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Gvdasa.FinOpsApi.AzureFunctions.Analyzers;
using Gvdasa.FinOpsApi.AzureFunctions.Application;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        // 🚀 NÍVEL 4: Registrar analyzers e services
        services.AddHttpClient();
        services.AddScoped<UnattachedDiskAnalyzer>();
        services.AddScoped<StorageAccountAnalyzer>();
        services.AddScoped<CostAnalysisOrchestrator>();
        
        Console.WriteLine("✅ NÍVEL 4: Services registrados - UnattachedDiskAnalyzer, StorageAccountAnalyzer, CostAnalysisOrchestrator");
    })
    .Build();

Console.WriteLine("[INFO] 🚀 NÍVEL 4 COMPLETO - Sistema FinOps Multi-Analyzer iniciando...");
host.Run();