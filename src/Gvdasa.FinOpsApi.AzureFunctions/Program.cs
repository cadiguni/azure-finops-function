using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Azure.Storage.Blobs;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.ResourceManager;
using Gvdasa.FinOpsApi.AzureFunctions.Analyzers;
using Gvdasa.FinOpsApi.AzureFunctions.Application;
using Gvdasa.FinOpsApi.AzureFunctions.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        // 🚀 NÍVEL 4: Registrar analyzers e services
        services.AddHttpClient();
        services.AddScoped<UnattachedDiskAnalyzer>();
        services.AddScoped<StorageAccountAnalyzer>();
        services.AddScoped<UnusedPublicIpAnalyzer>();
        services.AddScoped<IdleVmAnalyzer>();
        services.AddScoped<AppServiceAnalyzer>();
        services.AddScoped<CostAnalysisOrchestrator>();
        services.AddScoped<FinOpsResultAggregator>();
        services.AddScoped<DailySummaryService>();
        
        // 📊 Azure Monitor REAL para métricas autênticas  
        services.AddSingleton<DefaultAzureCredential>();
        services.AddSingleton(sp =>
        {
            var credential = sp.GetRequiredService<DefaultAzureCredential>();
            return new MetricsQueryClient(credential);
        });
        
        // 🔍 Azure ARM Client para descoberta de recursos
        services.AddSingleton(sp =>
        {
            var credential = sp.GetRequiredService<DefaultAzureCredential>();
            return new ArmClient(credential);
        });
        
        services.AddScoped<AzureMetricsService>();
        
        // 🔗 OPÇÃO B: Azure Storage Client (funciona local + Azure)
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var connectionString = config["AzureWebJobsStorage"];
            return new BlobServiceClient(connectionString);
        });
        services.AddScoped<AnalysisStorageService>();
        
        Console.WriteLine("✅ NÍVEL 4: Services registrados - UnattachedDiskAnalyzer, StorageAccountAnalyzer, UnusedPublicIpAnalyzer, CostAnalysisOrchestrator, FinOpsResultAggregator, AnalysisStorageService, DailySummaryService");
    })
    .Build();

Console.WriteLine("[INFO] 🚀 NÍVEL 4 COMPLETO - Sistema FinOps Multi-Analyzer iniciando...");
host.Run();