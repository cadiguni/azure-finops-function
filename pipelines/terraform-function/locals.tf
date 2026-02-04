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
      
      # Configurações específicas do FinOps
      "FinOps__Analyzer__EnableVmAnalysis" = "true"
      "FinOps__Analyzer__EnableDiskAnalysis" = "true"
      "FinOps__Analyzer__EnableAppServiceAnalysis" = "true"
      "FinOps__Analyzer__EnableSqlAnalysis" = "true"
      "FinOps__Analyzer__MinimumCostToAnalyze" = "50.0"
      "FinOps__Analyzer__LowCpuThreshold" = "5.0"
      "FinOps__Analyzer__DaysInactiveThreshold" = "7"
      
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
      
      # Analysis Schedules - using NCRON expressions
      "FinOps__Schedules__CostAnalysis" = "0 */4 * * *"     # Every 4 hours
      "FinOps__Schedules__DailySummary" = "0 6 * * *"       # Daily at 6 AM
      
      # Output Configuration
      "FinOps__Storage__ResultsContainerName" = "finops-results"
      "FinOps__Storage__ArchiveAfterDays" = "90"
    }
  }
}