# Outputs principais do Terraform para FinOps Function
output "function_app_name" {
  description = "Nome da Function App criada"
  value       = azurerm_linux_function_app.finops.name
}

output "function_app_url" {
  description = "URL da Function App"
  value       = "https://${azurerm_linux_function_app.finops.name}.azurewebsites.net"
}

output "storage_account_name" {
  description = "Nome da Storage Account para resultados"
  value       = azurerm_storage_account.storage.name
}

output "cost_export_container_name" {
  description = "Container central dos Cost Management Exports"
  value       = azurerm_storage_container.cost_exports.name
}

output "managed_identity_principal_id" {
  description = "Principal ID da Managed Identity"
  value       = azurerm_user_assigned_identity.finops_identity.principal_id
}

output "managed_identity_client_id" {
  description = "Client ID da Managed Identity"
  value       = azurerm_user_assigned_identity.finops_identity.client_id
}

output "application_insights_connection_string" {
  description = "Connection String do Application Insights"
  value       = azurerm_application_insights.cost_optimizer.connection_string
  sensitive   = true
}

# 🔍 DIAGNOSTIC URLs for troubleshooting 502 errors
output "function_health_check_url" {
  description = "URL para health check da Function App"
  value       = "https://${azurerm_linux_function_app.finops.name}.azurewebsites.net/api/test-status"
}

output "function_manual_analysis_url" {
  description = "URL para análise manual (teste)"
  value       = "https://${azurerm_linux_function_app.finops.name}.azurewebsites.net/api/ManualCostAnalysis"
}

output "function_discovery_test_url" {
  description = "URL para teste de discovery de subscriptions"
  value       = "https://${azurerm_linux_function_app.finops.name}.azurewebsites.net/api/TestDiscovery"
}

output "scm_url" {
  description = "URL do Kudu/SCM para diagnósticos avançados"
  value       = "https://${azurerm_linux_function_app.finops.name}.scm.azurewebsites.net"
}

output "cosmos_account_name" {
  description = "Nome da conta Cosmos DB"
  value       = azurerm_cosmosdb_account.finops.name
}

output "cosmos_endpoint" {
  description = "Endpoint da conta Cosmos DB"
  value       = azurerm_cosmosdb_account.finops.endpoint
}

output "cosmos_database_name" {
  description = "Nome do database Cosmos DB"
  value       = azurerm_cosmosdb_sql_database.finops.name
}

output "cosmos_container_names" {
  description = "Containers criados no Cosmos DB"
  value = {
    ownership            = azurerm_cosmosdb_sql_container.ownership.name
    recommendationStatus = azurerm_cosmosdb_sql_container.recommendation_status.name
    auditLogs            = azurerm_cosmosdb_sql_container.audit_logs.name
    costAnomalies        = azurerm_cosmosdb_sql_container.cost_anomalies.name
    portalSummaries      = azurerm_cosmosdb_sql_container.portal_summaries.name
    cleanupRequests      = var.enable_cleanup_requests_container ? azurerm_cosmosdb_sql_container.cleanup_requests[0].name : null
  }
}