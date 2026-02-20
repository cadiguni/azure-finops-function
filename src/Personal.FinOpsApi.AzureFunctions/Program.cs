using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
// using Azure.Storage.Queues; // DESABILITADO: Conflito com System.ComponentModel no .NET 8 isolated
using Azure.Storage.Blobs;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.ResourceManager;
using Personal.FinOpsApi.AzureFunctions.Analyzers;
using Personal.FinOpsApi.AzureFunctions.Application;
using Personal.FinOpsApi.AzureFunctions.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        Console.WriteLine("🚀 RESTAURANDO TUDO: Todas as funcionalidades desenvolvidas...");
        
        // 🔧 Services básicos
        services.AddHttpClient();
        services.AddScoped<HttpRetryService>();
        services.AddSingleton<AzureApiThrottleService>();

        
        // 🧪 ANALYZERS - Todas as análises desenvolvidas
        try {
            services.AddScoped<UnattachedDiskAnalyzer>();
            services.AddScoped<StorageAccountAnalyzer>();
            services.AddScoped<UnusedPublicIpAnalyzer>();
            services.AddScoped<IdleVmAnalyzer>();
            services.AddScoped<AppServiceAnalyzer>();
            services.AddScoped<DuplicateResourceAnalyzer>();
            Console.WriteLine("✅ Todos os ANALYZERS registrados");
        } catch (Exception ex) {
            Console.WriteLine($"❌ Erro nos ANALYZERS: {ex.Message}");
        }
        
        // 🎯 ORCHESTRATION & AGGREGATION
        try {
            services.AddScoped<CostAnalysisOrchestrator>();
            services.AddScoped<FinOpsResultAggregator>();
            services.AddScoped<DailySummaryService>();
            services.AddScoped<GrafanaDataService>(); // 📊 Novo serviço para Grafana
            Console.WriteLine("✅ Orchestration services registrados");
        } catch (Exception ex) {
            Console.WriteLine($"❌ Erro em Orchestration: {ex.Message}");
        }
        
        // 📊 AZURE CREDENTIALS & CLIENTS
        try {
            services.AddSingleton<DefaultAzureCredential>();
            services.AddSingleton(sp =>
            {
                var credential = sp.GetRequiredService<DefaultAzureCredential>();
                return new MetricsQueryClient(credential);
            });
            services.AddSingleton(sp =>
            {
                var credential = sp.GetRequiredService<DefaultAzureCredential>();
                return new ArmClient(credential);
            });
            Console.WriteLine("✅ Azure SDK clients registrados");
        } catch (Exception ex) {
            Console.WriteLine($"❌ Erro em Azure SDK: {ex.Message}");
        }
        
        // ⚡ AZURE METRICS & MONITORING
        try {
            services.AddScoped<AzureMetricsService>();
            Console.WriteLine("✅ AzureMetricsService registrado");
        } catch (Exception ex) {
            Console.WriteLine($"❌ Erro em AzureMetricsService: {ex.Message}");
        }
        
        // 🛡️ ENTERPRISE SERVICES - Circuit Breaker & Observability
        try {
            services.AddSingleton<CircuitBreakerService>();
            services.AddSingleton<ObservabilityService>();
            Console.WriteLine("✅ Enterprise services (Circuit Breaker, Observability) registrados");
        } catch (Exception ex) {
            Console.WriteLine($"❌ Erro em Enterprise services: {ex.Message}");
        }
        
        // � STORAGE SERVICES
        try {
            services.AddSingleton(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var connectionString = config["AzureWebJobsStorage"];
                return new BlobServiceClient(connectionString);
            });
            services.AddScoped<AnalysisStorageService>();
            // services.AddScoped<FinOpsDataService>();      // 🎯 REMOVIDO: serviço deletado
            // services.AddScoped<FinOpsSummaryBuilder>();   // 🧱 REMOVIDO: Builder deletado
            Console.WriteLine("✅ Storage services registrados");
        } catch (Exception ex) {
            Console.WriteLine($"❌ Erro em Storage services: {ex.Message}");
        }

        // 📊 LOG ANALYTICS DATA COLLECTOR SERVICE - Para dashboards FinOps (Opção A - simples)
        try {
            services.AddScoped<LogAnalyticsDataCollectorService>();
            Console.WriteLine("✅ LogAnalyticsDataCollectorService registrado para dashboards (Data Collector API)");
        } catch (Exception ex) {
            Console.WriteLine($"❌ Erro em LogAnalyticsDataCollectorService: {ex.Message}");
        }
        
        // � SUBSCRIPTION DISCOVERY SERVICE
        try {
            services.AddScoped<SubscriptionDiscoveryService>();
            Console.WriteLine("✅ SubscriptionDiscoveryService registrado");
        } catch (Exception ex) {
            Console.WriteLine($"❌ Erro em SubscriptionDiscoveryService: {ex.Message}");
        }
                // 🚀 SERVICE BUS QUEUE PROCESSING - Funcionalidade HÍBRIDA Nova
        // 🎯 HÍBRIDO: Suporte a queues OU execução direta via feature flag
        try {
            services.AddSingleton(sp => {
                var config = sp.GetRequiredService<IConfiguration>();
                var connectionString = config["ServiceBusConnection"];
                return new Azure.Messaging.ServiceBus.ServiceBusClient(connectionString);
            });
            services.AddScoped<QueueService>();
            Console.WriteLine("✅ Service Bus Queue services registrados (HÍBRIDO)");
        } catch (Exception ex) {
            Console.WriteLine($"❌ Erro em Service Bus services: {ex.Message}");
        }
                // �📦 QUEUE PROCESSING - Funcionalidade IMPORTANTE que foi removida
        // NOTA: Comentado temporariamente devido ao conflito AZFD0005, mas pode ser reativado
        try {
            // services.AddScoped<QueueProcessingService>(); 
            // services.AddSingleton(sp => {
            //     var config = sp.GetRequiredService<IConfiguration>();
            //     var connectionString = config["AzureWebJobsStorage"];
            //     return new Azure.Storage.Queues.QueueServiceClient(connectionString);
            // });
            Console.WriteLine("⚠️  Queue Processing DESABILITADO temporariamente (conflito AZFD0005)");
        } catch (Exception ex) {
            Console.WriteLine($"❌ Erro em Queue services: {ex.Message}");
        }
        
        Console.WriteLine("🎯 TODAS as funcionalidades restauradas (exceto queues temporariamente)");
    })
    .Build();

Console.WriteLine("[INFO] Sistema FinOps iniciando...");
host.Run();