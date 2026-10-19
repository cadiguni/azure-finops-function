using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
// using Azure.Storage.Queues; // DESABILITADO: Conflito com System.ComponentModel no .NET 8 isolated
using Azure.Storage.Blobs;
using Azure.Identity;
using Azure.Core;
using Microsoft.Azure.Cosmos;
using Azure.Monitor.Query;
using Azure.ResourceManager;
using Personal.FinOpsApi.AzureFunctions.Analyzers;
using Personal.FinOpsApi.AzureFunctions.Application;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories.Blob;
using Personal.FinOpsApi.AzureFunctions.Services.Repositories.Cosmos;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        Console.WriteLine(" RESTAURANDO TUDO: Todas as funcionalidades desenvolvidas...");
        
        //  Services básicos
        services.AddHttpClient();
        services.AddScoped<HttpRetryService>();
        services.AddSingleton<AzureApiThrottleService>();

        services.AddFinOpsGovernance();

        
        //  ANALYZERS - Todas as análises desenvolvidas
        try {
            services.AddScoped<UnattachedDiskAnalyzer>();
            services.AddScoped<StorageAccountAnalyzer>();
            services.AddScoped<UnusedPublicIpAnalyzer>();
            services.AddScoped<IdleVmAnalyzer>();
            services.AddScoped<AppServiceAnalyzer>();
            services.AddScoped<FunctionAppAnalyzer>();
            services.AddScoped<LogAnalyticsAnalyzer>();
            services.AddScoped<SqlDatabaseAnalyzer>();
            services.AddScoped<CosmosDatabaseAnalyzer>();
            Console.WriteLine(" Todos os ANALYZERS registrados");
        } catch (Exception ex) {
            Console.WriteLine($" Erro nos ANALYZERS: {ex.Message}");
        }
        
        //  ORCHESTRATION & AGGREGATION
        try {
            services.AddScoped<CostAnalysisOrchestrator>();
            services.AddScoped<FinOpsResultAggregator>();
            services.AddScoped<DailySummaryService>();
            Console.WriteLine(" Orchestration services registrados");
        } catch (Exception ex) {
            Console.WriteLine($" Erro em Orchestration: {ex.Message}");
        }
        
        //  REPORT SERVICES - HTML/CSV Generation (replaced PDF)
        try {
            services.AddScoped<IRecommendationReportService, RecommendationReportService>();
            services.AddScoped<HtmlReportBuilder>();
            services.AddScoped<CsvReportBuilder>();
            services.AddScoped<PreGeneratedReportStorageService>();
            Console.WriteLine(" Report services (HTML/CSV) registrados");
        } catch (Exception ex) {
            Console.WriteLine($" Erro em Report services: {ex.Message}");
        }

        //  TEAM MANAGEMENT SERVICES - Simplified team -> subscriptions mapping
        try {
            services.AddScoped<TeamSubscriptionsService>();
            services.AddScoped<OwnershipStorageService>();
            services.AddScoped<IOwnershipConfigReader>(sp => sp.GetRequiredService<OwnershipStorageService>());
            services.AddScoped<ILegacyTeamSubscriptionsLookupService>(sp => sp.GetRequiredService<TeamSubscriptionsService>());
            services.AddScoped<ITeamSubscriptionsLookupService, OwnershipTeamSubscriptionsLookupService>();
            services.AddScoped<OwnershipManagementGroupSyncService>();
            services.AddScoped<CurrentUserService>();
            services.AddScoped<ICurrentUserService>(sp => sp.GetRequiredService<CurrentUserService>());
            services.AddScoped<AuthorizationService>();
            Console.WriteLine(" Team management services registrados");
        } catch (Exception ex) {
            Console.WriteLine($" Erro em Team management services: {ex.Message}");
        }

        //  COST ANOMALY DETECTION SERVICES
        try {
            services.AddScoped<CostAnomalyDailyCostService>();
            services.AddScoped<CostAnomalyAnalysisService>();
            services.AddScoped<CostAnomalyStorageService>();
            services.AddScoped<SubscriptionBillingCycleService>();
            services.AddScoped<IBillingPeriodResolver>(sp => sp.GetRequiredService<SubscriptionBillingCycleService>());
            Console.WriteLine(" Cost Anomaly Detection services registrados");
        } catch (Exception ex) {
            Console.WriteLine($" Erro em Cost Anomaly services: {ex.Message}");
        }
        
        //  AZURE CREDENTIALS & CLIENTS
        try {
            services.AddSingleton<DefaultAzureCredential>();
            services.AddSingleton<TokenCredential>(sp => sp.GetRequiredService<DefaultAzureCredential>());
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
            Console.WriteLine(" Azure SDK clients registrados");
        } catch (Exception ex) {
            Console.WriteLine($" Erro em Azure SDK: {ex.Message}");
        }
        
        //  AZURE METRICS & MONITORING
        try {
            services.AddScoped<AzureMetricsService>();
            Console.WriteLine(" AzureMetricsService registrado");
        } catch (Exception ex) {
            Console.WriteLine($" Erro em AzureMetricsService: {ex.Message}");
        }
        
        //  ENTERPRISE SERVICES - Circuit Breaker & Observability
        try {
            services.AddSingleton<CircuitBreakerService>();
            services.AddSingleton<ObservabilityService>();
            Console.WriteLine(" Enterprise services (Circuit Breaker, Observability) registrados");
        } catch (Exception ex) {
            Console.WriteLine($" Erro em Enterprise services: {ex.Message}");
        }
        
        //  STORAGE SERVICES
        try {
            services.AddSingleton(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var connectionString = config["AzureWebJobsStorage"];
                return new BlobServiceClient(connectionString);
            });
            services.AddScoped<AnalysisStorageService>();
            services.AddScoped<IAnalysisSnapshotService>(sp => sp.GetRequiredService<AnalysisStorageService>());
            services.AddScoped<CostStorageRepository>();
            services.AddSingleton<CostExportCsvParser>();
            services.AddSingleton<CostExportManifestParser>();
            services.AddScoped<ICostExportBlobSource, BlobCostExportBlobSource>();
            services.AddScoped<ICostExportImportService, CostExportImportService>();
            services.AddScoped<BlobOwnershipRepository>();
            services.AddScoped<BlobRecommendationStatusRepository>();
            services.AddScoped<BlobAuditLogRepository>();
            services.AddScoped<BlobCostAnomalyReportRepository>();
            // services.AddScoped<FinOpsDataService>();      //  REMOVIDO: serviço deletado
            // services.AddScoped<FinOpsSummaryBuilder>();   //  REMOVIDO: Builder deletado
            Console.WriteLine(" Storage services registrados");
        } catch (Exception ex) {
            Console.WriteLine($" Erro em Storage services: {ex.Message}");
        }

        //  COSMOS DB (híbrido Blob + Cosmos)
        try
        {
            services.AddSingleton<CosmosClient>(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var endpoint = config["CosmosEndpoint"];
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    throw new InvalidOperationException("CosmosEndpoint não configurado.");
                }

                var options = new CosmosClientOptions
                {
                    SerializerOptions = new CosmosSerializationOptions
                    {
                        PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                    }
                };

                var useManagedIdentity = config.GetValue("CosmosUseManagedIdentity", true);
                if (useManagedIdentity)
                {
                    var credential = sp.GetRequiredService<TokenCredential>();
                    return new CosmosClient(endpoint, credential, options);
                }

                var key = config["CosmosKey"];
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new InvalidOperationException("CosmosKey não configurado para acesso sem Managed Identity.");
                }

                return new CosmosClient(endpoint, key, options);
            });

            services.AddScoped<CosmosOwnershipRepository>();
            services.AddScoped<CosmosRecommendationStatusRepository>();
            services.AddScoped<CosmosAuditLogRepository>();
            services.AddScoped<CosmosCostAnomalyReportRepository>();
            services.AddScoped<CosmosDashboardSummaryRepository>();
            services.AddScoped<CosmosDatabaseCostRollupRepository>();
            services.AddScoped<CosmosCostAllocationCoverageRepository>();
            Console.WriteLine(" Cosmos DB services registrados");
        }
        catch (Exception ex)
        {
            Console.WriteLine($" Erro em Cosmos DB services: {ex.Message}");
        }

        //  REPOSITÓRIOS HÍBRIDOS
        services.AddScoped<IOwnershipRepository>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var provider = config["OwnershipProvider"] ?? "Blob";
            var hasCosmos = !string.IsNullOrWhiteSpace(config["CosmosEndpoint"]);
            return provider.Equals("Cosmos", StringComparison.OrdinalIgnoreCase) && hasCosmos
                ? sp.GetRequiredService<CosmosOwnershipRepository>()
                : sp.GetRequiredService<BlobOwnershipRepository>();
        });

        services.AddScoped<IRecommendationStatusRepository>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var provider = config["RecommendationStatusProvider"] ?? "Cosmos";
            var hasCosmos = !string.IsNullOrWhiteSpace(config["CosmosEndpoint"]);
            return provider.Equals("Cosmos", StringComparison.OrdinalIgnoreCase) && hasCosmos
                ? sp.GetRequiredService<CosmosRecommendationStatusRepository>()
                : sp.GetRequiredService<BlobRecommendationStatusRepository>();
        });

        services.AddScoped<IAuditLogRepository>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var provider = config["AuditLogProvider"] ?? "Cosmos";
            var hasCosmos = !string.IsNullOrWhiteSpace(config["CosmosEndpoint"]);
            return provider.Equals("Cosmos", StringComparison.OrdinalIgnoreCase) && hasCosmos
                ? sp.GetRequiredService<CosmosAuditLogRepository>()
                : sp.GetRequiredService<BlobAuditLogRepository>();
        });

        services.AddScoped<ICostAnomalyReportRepository>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var provider = config["CostAnomalyProvider"] ?? "Cosmos";
            var hasCosmos = !string.IsNullOrWhiteSpace(config["CosmosEndpoint"]);
            return provider.Equals("Cosmos", StringComparison.OrdinalIgnoreCase) && hasCosmos
                ? sp.GetRequiredService<CosmosCostAnomalyReportRepository>()
                : sp.GetRequiredService<BlobCostAnomalyReportRepository>();
        });

        services.AddScoped<IDashboardSummaryRepository>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var provider = config["DashboardSummaryProvider"] ?? "Cosmos";
            var hasCosmos = !string.IsNullOrWhiteSpace(config["CosmosEndpoint"]);
            if (provider.Equals("Cosmos", StringComparison.OrdinalIgnoreCase) && hasCosmos)
            {
                return sp.GetRequiredService<CosmosDashboardSummaryRepository>();
            }

            var loggerFactory = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("DashboardSummaryRepositoryConfig");
            logger.LogWarning(
                "Dashboard summary repository em modo NoOp. provider={provider}, hasCosmosEndpoint={hasCosmos}. O dashboard retornará valores zerados até o provider Cosmos ser configurado corretamente.",
                provider,
                hasCosmos);
            return new NoOpDashboardSummaryRepository();
        });
        services.AddScoped<IDatabaseCostRollupRepository>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var provider = config["DatabaseCostRollupProvider"] ?? "Cosmos";
            var hasCosmos = !string.IsNullOrWhiteSpace(config["CosmosEndpoint"]);
            return provider.Equals("Cosmos", StringComparison.OrdinalIgnoreCase) && hasCosmos
                ? sp.GetRequiredService<CosmosDatabaseCostRollupRepository>()
                : new NoOpDatabaseCostRollupRepository();
        });
        services.AddScoped<ICostAllocationCoverageRepository>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var provider = config["DatabaseCostRollupProvider"] ?? "Cosmos";
            var hasCosmos = !string.IsNullOrWhiteSpace(config["CosmosEndpoint"]);
            return provider.Equals("Cosmos", StringComparison.OrdinalIgnoreCase) && hasCosmos
                ? sp.GetRequiredService<CosmosCostAllocationCoverageRepository>()
                : new NoOpCostAllocationCoverageRepository();
        });
        services.AddScoped<CostAllocationCoverageService>();
        services.AddScoped<CostAllocationRulesStorageService>();
        services.AddScoped<CostAllocationService>();
        services.AddScoped<DatabaseCostRollupService>();
        services.AddScoped<SummaryGenerationService>();

        //  COST MANAGEMENT SERVICES
        try {
            services.AddScoped<ICostManagementClient, CostManagementClient>();
            services.AddScoped<ICostStorageRepository, CostStorageRepository>();
            services.AddScoped<ICostExportVersionStore, BlobCostExportVersionStore>();
    services.AddScoped<IReportSnapshotStore, BlobReportSnapshotStore>();
            services.AddScoped<ICostDataReader, ExportFirstCostDataReader>();
            services.AddSingleton<IResourceCostResolver, ResourceCostResolver>();
            services.AddScoped<ResourceCostLookupService>(); // Cache em lote na execução, sem capturar reader/repository scoped em singleton
            Console.WriteLine(" CostManagement services registrados");
        } catch (Exception ex) {
            Console.WriteLine($" Erro em CostManagement services: {ex.Message}");
        }

        //  LOG ANALYTICS DATA COLLECTOR SERVICE - Para dashboards FinOps (Opção A - simples)
        try {
            services.AddScoped<LogAnalyticsDataCollectorService>();
            Console.WriteLine(" LogAnalyticsDataCollectorService registrado para dashboards (Data Collector API)");
        } catch (Exception ex) {
            Console.WriteLine($" Erro em LogAnalyticsDataCollectorService: {ex.Message}");
        }
        
        //  SUBSCRIPTION DISCOVERY SERVICE
        try {
            services.AddScoped<SubscriptionDiscoveryService>();
            Console.WriteLine(" SubscriptionDiscoveryService registrado");
        } catch (Exception ex) {
            Console.WriteLine($" Erro em SubscriptionDiscoveryService: {ex.Message}");
        }
                //  SERVICE BUS QUEUE PROCESSING - Funcionalidade HÍBRIDA Nova
        //  HÍBRIDO: Suporte a queues OU execução direta via feature flag
        try {
            services.AddSingleton(sp => {
                var config = sp.GetRequiredService<IConfiguration>();
                var connectionString = config["ServiceBusConnection"];
                return new Azure.Messaging.ServiceBus.ServiceBusClient(connectionString);
            });
            services.AddScoped<QueueService>();
            services.AddScoped<ICostAnomalyQueueService, CostAnomalyQueueService>();
            services.AddScoped<IExportTechnicalAnalysisQueue, ExportTechnicalAnalysisQueue>();
            Console.WriteLine(" Service Bus Queue services registrados (HÍBRIDO)");
        } catch (Exception ex) {
            Console.WriteLine($" Erro em Service Bus services: {ex.Message}");
        }
                //  QUEUE PROCESSING - Funcionalidade IMPORTANTE que foi removida
        // NOTA: Comentado temporariamente devido ao conflito AZFD0005, mas pode ser reativado
        try {
            // services.AddScoped<QueueProcessingService>(); 
            // services.AddSingleton(sp => {
            //     var config = sp.GetRequiredService<IConfiguration>();
            //     var connectionString = config["AzureWebJobsStorage"];
            //     return new Azure.Storage.Queues.QueueServiceClient(connectionString);
            // });
            Console.WriteLine("  Queue Processing DESABILITADO temporariamente (conflito AZFD0005)");
        } catch (Exception ex) {
            Console.WriteLine($" Erro em Queue services: {ex.Message}");
        }
        
        //  AZURE MANAGEMENT GROUP SERVICE - Para descoberta de subscriptions
        try {
            services.AddScoped<AzureManagementGroupService>();
            Console.WriteLine(" AzureManagementGroup service registrado");
        } catch (Exception ex) {
            Console.WriteLine($" Erro em AzureManagementGroup service: {ex.Message}");
        }
        
        Console.WriteLine(" TODAS as funcionalidades restauradas + Hierarquia Organizacional (relatórios simplificados)");
    })
    .Build();

Console.WriteLine("[INFO] Sistema FinOps iniciando...");
host.Run();
