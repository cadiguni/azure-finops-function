locals {
  dominio = "gvdasa.com.br"
  tags = {
    Responsável = "lcadiguni@gvdasa.com.br"
    Setor = "NAP"
  }

  localizacao = "Brazil South"
  aplicacao   = var.aplicacao
  setor       = var.setor

  # Configurações específicas do FinOps
  finops_settings = {
    function_app_name = "${local.aplicacao}-${local.setor}-func"
    app_service_plan_name = "${local.aplicacao}-${local.setor}-plan"
    application_insights_name = "${local.aplicacao}-${local.setor}-ai"
    managed_identity_name = "${local.aplicacao}-${local.setor}-identity"
    
    # Configurações da Function
    function_settings = {
      # Runtime Configuration
      "FUNCTIONS_WORKER_RUNTIME"           = "dotnet-isolated"
      "FUNCTIONS_EXTENSION_VERSION"        = "~4"
      "WEBSITE_USE_PLACEHOLDER_DOTNETISOLATED" = "1"
      
      # ⚠️ AJUSTE CRÍTICO: Azure Functions usa NCRONTAB (6 campos)
      # Analysis Schedules - NCRONTAB format (seconds, minutes, hours, day, month, day-of-week)
      "FinOps__Schedules__CostAnalysis" = "0 0 */4 * * *"     # A cada 4 horas (00:00, 04:00, 08:00, etc)
      "FinOps__Schedules__DailySummary" = "0 0 6 * * *"       # Diário às 06:00 UTC
      
      # 🚀 CONTROLES DE PARALELISMO GLOBAL (Economia de custo)
      "FinOps__Execution__MaxParallelSubscriptions" = "3"
      "FinOps__Execution__MaxParallelAnalyzers" = "5"
      "FinOps__Execution__MaxSubscriptionProcessingTime" = "00:20:00"
      
      # 💰 CONTROLE DE MÉTRICAS (Super importante para custo)
      "FinOps__Analyzer__EnableMetricsDeepAnalysis" = "true"
      
      # 🎯 CONFIGURAÇÕES BÁSICAS DE ANÁLISE
      "FinOps__Analyzer__EnableVmAnalysis" = "true"
      "FinOps__Analyzer__EnableDiskAnalysis" = "true"
      "FinOps__Analyzer__EnableAppServiceAnalysis" = "true"
      "FinOps__Analyzer__EnableSqlAnalysis" = "true"
      
      # 🔍 THRESHOLDS DINÂMICOS POR AMBIENTE
      "FinOps__Thresholds__ProductionCpu" = "15"              # Produção usa threshold maior
      "FinOps__Thresholds__DevCpu" = "5"                      # Dev usa threshold menor
      "FinOps__Analyzer__LowCpuThreshold" = "5.0"             # Padrão (será sobrescrito por ambiente)
      "FinOps__Analyzer__DaysInactiveThreshold" = "7"
      "FinOps__Analyzer__MinimumMonthlySavingsToRecommend" = "30"  # Dinâmico
      
      # 💎 MELHORIAS ENTERPRISE
      # Caching de métricas (evita reprocessamento)
      "FinOps__Caching__MetricsCacheHours" = "24"
      
      # Governança - Tags obrigatórias
      "FinOps__Governance__RequiredTags" = jsonencode(["Responsavel", "Setor", "Ambiente"])
      
      # Classificação de ambiente com fallback
      "FinOps__EnvironmentClassification__UseTagsFallback" = "true"
      
      # Management Groups
      "FinOps__EnvironmentClassification__ProductionManagementGroups" = jsonencode(["Setores"])
      "FinOps__EnvironmentClassification__NonProductionManagementGroups" = jsonencode(["Visual Studio"])
      "FinOps__Scope__RootManagementGroup" = var.root_management_group
      "FinOps__Behavior__DryRunInProduction" = "true"
      "FinOps__Behavior__AllowAutomationInProduction" = "false"
      
      # Queue Configuration
      "FinOps__Queue__MaxProcessingTime" = "00:30:00"
      "FinOps__Queue__MaxRetryAttempts" = "3"
      "FinOps__Queue__BatchSize" = "10"
      
      # Output Configuration
      "FinOps__Storage__ResultsContainerName" = "finops-results"
      "FinOps__Storage__ArchiveAfterDays" = "90"
    }
  }
}