# Log Analytics Workspace - FinOps Platform

resource "azurerm_log_analytics_workspace" "finops_loganalytics" {
  name                = "${local.aplicacao}-${local.setor}-law"
  location            = local.localizacao
  resource_group_name = azurerm_resource_group.rg.name
  tags                = local.tags
  sku                 = "PerGB2018"
  retention_in_days   = 30
  daily_quota_gb      = 0.1  # Quota conservadora para FinOps
  
  lifecycle {
    ignore_changes = [
      tags
    ]
  }
}

# Tabelas específicas para monitoramento do FinOps
resource "azurerm_log_analytics_workspace_table" "finops_AppMetrics" {
  workspace_id            = azurerm_log_analytics_workspace.finops_loganalytics.id
  name                    = "AppMetrics"
  retention_in_days       = 30
  total_retention_in_days = 30
}

resource "azurerm_log_analytics_workspace_table" "finops_AppExceptions" {
  workspace_id            = azurerm_log_analytics_workspace.finops_loganalytics.id
  name                    = "AppExceptions"
  retention_in_days       = 30
  total_retention_in_days = 30
}

resource "azurerm_log_analytics_workspace_table" "finops_AppDependencies" {
  workspace_id            = azurerm_log_analytics_workspace.finops_loganalytics.id
  name                    = "AppDependencies"
  retention_in_days       = 30
  total_retention_in_days = 30
}

resource "azurerm_log_analytics_workspace_table" "finops_AppTraces" {
  workspace_id            = azurerm_log_analytics_workspace.finops_loganalytics.id
  name                    = "AppTraces"
  plan                    = "Basic"  
  total_retention_in_days = 30
}

# Tabela específica para logs de análise de custos
resource "azurerm_log_analytics_workspace_table" "finops_CostAnalysis" {
  workspace_id            = azurerm_log_analytics_workspace.finops_loganalytics.id
  name                    = "AppEvents"
  retention_in_days       = 90  # Maior retenção para dados de custo
  total_retention_in_days = 90
}