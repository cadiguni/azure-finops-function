# Azure Function App - FinOps Platform

# App Service Plan para a Function App
resource "azurerm_service_plan" "finops" {
  name                = local.finops_settings.app_service_plan_name
  location            = local.localizacao
  resource_group_name = azurerm_resource_group.rg.name
  os_type             = "Linux"
  sku_name            = "Y1"  # Consumption plan
  
  tags = local.tags
  
  lifecycle {
    ignore_changes = [
      tags
    ]
  }
}

# Function App
resource "azurerm_linux_function_app" "finops" {
  name                       = local.finops_settings.function_app_name
  location                   = local.localizacao
  resource_group_name        = azurerm_resource_group.rg.name
  service_plan_id           = azurerm_service_plan.finops.id
  storage_account_name       = azurerm_storage_account.storage.name
  storage_account_access_key = azurerm_storage_account.storage.primary_access_key
  
  # Dependências explícitas para evitar problemas de criação
  depends_on = [
    azurerm_service_plan.finops,
    azurerm_storage_account.storage,
    azurerm_application_insights.cost_optimizer,
    azurerm_user_assigned_identity.finops_identity,
    azurerm_cosmosdb_sql_role_assignment.function_data_contributor,
    azurerm_role_assignment.cost_export_source_reader
  ]

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.finops_identity.id]
  }

  site_config {
    # Configuração mínima e estável
    always_on = false  # Consumption plan não suporta always_on = true
    
    # ⚡ PERFORMANCE & TIMEOUT SETTINGS
    http2_enabled = true
    minimum_tls_version = "1.2"
    
    application_stack {
      dotnet_version              = "8.0"
      use_dotnet_isolated_runtime = true  # ISOLATED .NET 8
    }
    
    # 🔧 CORS Configuration for API calls
    cors {
      allowed_origins = ["*"]
      support_credentials = false
    }
  }

  # Terraform é a fonte dos App Settings; migrações mantêm bindings resolvidos mesmo inativas.
  app_settings = merge({
    # Runtime essencial - ISOLATED
    "FUNCTIONS_WORKER_RUNTIME"           = "dotnet-isolated"
    "FUNCTIONS_EXTENSION_VERSION"        = "~4"
    "WEBSITE_RUN_FROM_PACKAGE"           = "1"
    "WEBSITE_USE_PLACEHOLDER_DOTNETISOLATED" = "1"
    
    # Storage obrigatório
    "AzureWebJobsStorage"                = azurerm_storage_account.storage.primary_connection_string
    "WEBSITE_CONTENTAZUREFILECONNECTIONSTRING" = azurerm_storage_account.storage.primary_connection_string
    "WEBSITE_CONTENTSHARE"               = "finops-content"
    
    # Application Insights
    "APPINSIGHTS_INSTRUMENTATIONKEY"     = azurerm_application_insights.cost_optimizer.instrumentation_key
    "APPLICATIONINSIGHTS_CONNECTION_STRING" = azurerm_application_insights.cost_optimizer.connection_string
    
    # 🚨 CRITICAL: Cold Start & Performance Settings
    "WEBSITE_WARM_UP_PATH"               = "/api/test-status"  # Warm-up endpoint
    "WEBSITE_SWAP_WARMUP_PING_PATH"      = "/api/test-status"
    "WEBSITE_SWAP_WARMUP_PING_STATUSES"  = "200"
    "WEBSITE_PRELOAD_ENABLED"            = "1"
    "FUNCTIONS_WORKER_PROCESS_COUNT"     = "1"
    "WEBSITE_MAX_DYNAMIC_APPLICATION_SCALE_OUT" = "5"
    
    # 🔧 HTTP & Timeout Settings
    "WEBSITE_HTTPLOGGING_RETENTION_DAYS" = "3"
    "WEBSITE_LOAD_CERTIFICATES"          = "*"
    
    # 📊 Memory & Resource Management
    "WEBSITE_MEMORY_LIMIT_MB"            = "1024"  # 1GB limit for stability
    "FUNCTIONS_WORKER_SHARED_MEMORY_DATA_TRANSFER_ENABLED" = "1"
    
    # Identity e configurações base - Azure SDK Padrão
    "AZURE_CLIENT_ID"                    = azurerm_user_assigned_identity.finops_identity.client_id
    "AZURE_TENANT_ID"                    = data.azurerm_client_config.current.tenant_id  # ADICIONADO: Faltava para autenticação
    "AZURE_SUBSCRIPTION_ID"             = data.azurerm_client_config.current.subscription_id
    
    # ✨ MÚLTIPLAS SUBSCRIPTIONS - Estratégias de Discovery
    # "AZURE_SUBSCRIPTION_IDS"            = "${data.azurerm_client_config.current.subscription_id},00000000-0000-0000-0000-00000000a002"
    
    # 🏢 MANAGEMENT GROUP DISCOVERY (ATIVO - descobre subscriptions automaticamente)
    "AZURE_MANAGEMENT_GROUP_ID"         = var.root_management_group
    
    # 🌐 TENANT-WIDE DISCOVERY (opcional - descobre TODAS as subscriptions acessíveis)
    "AZURE_DISCOVER_ALL_SUBSCRIPTIONS"  = "false"  # false = usa Management Group
    
    # Timer Configuration - PRODUÇÃO
    # ⚠️ App start/stop automation: resources sobem às 6h BRT (9h UTC)
    # Pipeline sequencial: todos os timers APÓS 9h UTC para evitar catch-up simultâneo
    # coleta → análise → anomalias → summary → relatórios
    "CostByServiceDailySchedule"         = "0 10 9 * * *"    # 09:10 UTC (06:10 BRT) - coleta custo por serviço
    "CostByResourceDailySchedule"        = "0 20 9 * * *"    # 09:20 UTC (06:20 BRT) - coleta custo por recurso
    "CostAnalysisSchedule"               = "0 30 9 * * *"    # 09:30 UTC (06:30 BRT) - análise completa (analyzers)
    "CostAnomalySchedule"                = "0 0 11 * * *"    # 11:00 UTC (08:00 BRT) - anomalias (após análise concluir)
    "DailySummarySchedule"               = "0 30 11 * * *"   # 11:30 UTC (08:30 BRT) - resumo diário
    "DashboardSummarySchedule"           = "0 40 11 * * *"   # 11:40 UTC (08:40 BRT) - consolida summaries para dashboard
    "DatabaseCostRollupSchedule"         = "0 45 11 * * *"   # 11:45 UTC (08:45 BRT) - consolida custo diário de SQL/Cosmos
    "DatabaseCostRollupProvider"         = "Cosmos"
    "CostAllocationTagName"              = "CentrodeCusto"  # tag de rateio lida do Cost Management Export
    "ReportGenerationSchedule"           = "0 0 12 * * *"    # 12:00 UTC (09:00 BRT) - pré-gera relatórios HTML com anomalias
    "ReportGenerationDateOffsetDays"     = "0"               # 0 = data atual UTC; use -1 se relatórios forem do dia anterior
    "EnableOwnershipManagementGroupSync" = tostring(var.enable_ownership_management_group_sync)
    "OwnershipManagementGroupSyncSchedule" = var.ownership_management_group_sync_schedule # Segunda 09:05 UTC, antes da coleta de custos
    
    # 📊 COST BY SERVICE/RESOURCE - Configuração de coleta de custos
    "COST_RESOURCE_SERVICE"              = "all"                # Grade de recursos deve cobrir todos os serviços
    "COST_STORAGE_CONTAINER"             = "finops-analysis"    # Container para dados de custo
    "COST_DATA_DELAY_DAYS"               = "2"                  # Aguarda consolidação do Cost Management (D-2)
    "COST_EXPORT_CONTAINER"                = var.cost_export_container_name
    "COST_EXPORT_STORAGE_ACCOUNT_URI"      = local.cost_export_storage_account_uri
    "COST_EXPORT_IMPORT_ALL_SUBSCRIPTIONS" = tostring(var.cost_export_import_all_subscriptions)
    "COST_EXPORT_SUBSCRIPTIONS"            = join(",", sort(tolist(var.cost_export_subscription_ids)))
    "COST_EXPORT_CUTOVER_SUBSCRIPTIONS"    = join(",", sort(tolist(var.cost_export_cutover_subscription_ids)))
    "COST_EXPORT_MAX_MANIFESTS_PER_RUN"    = tostring(var.cost_export_max_manifests_per_run)
    "COST_EXPORT_ANOMALY_MAX_STALENESS_DAYS" = tostring(var.cost_export_anomaly_max_staleness_days)
    "EnableCostExportImport"               = tostring(var.enable_cost_export_import)
    "EnableLegacyCostByServiceCollection"  = tostring(var.enable_legacy_cost_by_service_collection)
    "EnableLegacyCostByResourceCollection" = tostring(var.enable_legacy_cost_by_resource_collection)
    "CostExportImportSchedule"           = var.cost_export_import_schedule
    
    # 📊 COST ANOMALY DETECTION - Monitoramento de variações de custo diário
    "EnableCostAnomalyAnalysis"          = "true"            # Feature flag
    "CostAnomalyStorageContainer"        = "finops-analysis" # Container dedicado para anomalias
    "CostAnomalyExcludedSubscriptions"   = "00000000-0000-0000-0000-00000000a004,00000000-0000-0000-0000-00000000a001" # Subscriptions ignoradas
    "CostAnomalyMonthlyBudget"           = "860"             # Budget mensal em BRL
    "CostAnomalyIgnoreCurrentDay"        = "true"            # Não usar dia atual como principal por padrão
    "CostAnomalyLookbackDays"            = "7"               # Janela de busca de custos diários
    "CostAnomalyBaselineDays"            = "3"               # Dias de histórico para baseline
    "CostAnomalyMinimumIncreaseAmount"   = "10"              # Mínimo R$ para evitar falso positivo
    "CostAnomalyMediumPercent"           = "30"              # Threshold Medium: +30%
    "CostAnomalyHighPercent"             = "60"              # Threshold High: +60%
    "CostAnomalyCriticalPercent"         = "100"             # Threshold Critical: +100%
    "CostAnomalyUseAzureBillingPeriod"   = "true"            # Usa billing period real da subscription quando disponível
    "CostAnomalyBillingPeriodsApiVersion"  = var.cost_anomaly_billing_periods_api_version
    "CostAnomalySubscriptionTurnDayOverrides" = ""           # Ex.: sub1=15,sub2=22 (fallback manual opcional)
    "OwnershipConfigBlobPath"            = "config/ownership.json" # JSON de ownership (portal admin)
    "OwnershipProvider"                  = "Blob"
    "OwnershipAuthAllowedAudiences"      = join(",", sort([for audience in var.ownership_auth_allowed_audiences : trimspace(audience)]))
    "RecommendationStatusProvider"       = "Cosmos"
    "AuditLogProvider"                   = "Cosmos"
    "CostAnomalyProvider"                = "Cosmos"
    "CosmosUseManagedIdentity"           = "true"
    "CosmosEndpoint"                     = azurerm_cosmosdb_account.finops.endpoint
    "CosmosDatabaseName"                 = azurerm_cosmosdb_sql_database.finops.name
    "CosmosOwnershipContainerName"       = azurerm_cosmosdb_sql_container.ownership.name
    "CosmosRecommendationStatusContainerName" = azurerm_cosmosdb_sql_container.recommendation_status.name
    "CosmosAuditLogsContainerName"       = azurerm_cosmosdb_sql_container.audit_logs.name
    "CosmosCostAnomaliesContainerName"   = azurerm_cosmosdb_sql_container.cost_anomalies.name
    "CosmosPortalSummariesContainerName" = azurerm_cosmosdb_sql_container.portal_summaries.name
    "OwnershipDocumentsBlobPath"         = "operational/ownership-documents.json"
    "RecommendationStatusBlobPath"       = "operational/recommendation-status.json"
    "AuditLogsBlobPath"                  = "operational/audit-logs.json"
    "DashboardSummaryProvider"          = "Cosmos"
    
    # Storage Configuration
    "RESULTS_CONTAINER_NAME"             = "finops-analysis"  # Container para resultados de análise
    "CONFIG_CONTAINER_NAME"              = var.config_container_name # Reutiliza ownership; prefixo de budgets definido no backend
    
    # 🚨 FUNCTION-SPECIFIC HTTP LIMITS
    "AzureFunctionsJobHost__extensions__http__maxOutstandingRequests" = "200"
    "AzureFunctionsJobHost__extensions__http__maxConcurrentRequests" = "100"
    "AzureWebJobs.SubscriptionAnalysisStepStarter.Disabled" = "true" # Desabilita listener legado para queue starter não provisionada
    
    # 🌐 Network & Connectivity Settings
    "WEBSITE_VNET_ROUTE_ALL"             = "0"
    "WEBSITE_DNS_SERVER"                 = "168.63.129.16"  # Azure DNS
    "WEBSITE_ENABLE_SYNC_UPDATE_SITE"    = "1"
    
    # 🚀 SERVICE BUS QUEUE-BASED PROCESSING (BASIC TIER)
    "ServiceBusConnection"               = azurerm_servicebus_namespace.finops.default_primary_connection_string
    "ENABLE_QUEUE_PROCESSING"            = "true"  # Feature flag para Service Bus
    
    # 📊 QUEUE NAMES (Basic tier - apenas queues simples)
    "QUEUE_SUBSCRIPTION_ANALYSIS"        = "subscription-analysis"
    "QUEUE_SUBSCRIPTION_ANALYSIS_PROD"   = "subscription-analysis-production"  # 🎯 NOVA: Queue exclusiva para produção
    "QUEUE_COST_ANOMALY_RUN"             = azurerm_servicebus_queue.cost_anomalies_run.name
    "QUEUE_COST_ANOMALY_SUBSCRIPTION"    = azurerm_servicebus_queue.cost_anomalies_subscription.name
    
    # 🎯 PRODUCTION CONFIGURATION 
    "PRODUCTION_SUBSCRIPTION_ID"         = "00000000-0000-0000-0000-00000000a004"  # Subscription de produção
  }, local.ambiente_app_settings)
  
  tags = local.tags
  
  # ✅ IMPORTANT: Prevent recreation on minor changes
  lifecycle {
    ignore_changes = [
      tags,
      app_settings["WEBSITE_RUN_FROM_PACKAGE"],
      app_settings["WEBSITE_CONTENTSHARE"]
    ]

    precondition {
      condition     = var.cost_export_source_storage_account == null || var.cost_export_storage_account_uri == ""
      error_message = "Use cost_export_source_storage_account ou cost_export_storage_account_uri, não ambos."
    }

    precondition {
      condition     = length(var.cost_export_cutover_subscription_ids) == 0 || var.enable_cost_export_import
      error_message = "cost_export_cutover_subscription_ids exige enable_cost_export_import=true."
    }

    precondition {
      condition = var.cost_export_import_all_subscriptions || (
        length(setsubtract(var.cost_export_cutover_subscription_ids, var.cost_export_subscription_ids)) == 0
      )
      error_message = "Com import-all desabilitado, cost_export_cutover_subscription_ids deve ser subconjunto de cost_export_subscription_ids."
    }
  }
}
