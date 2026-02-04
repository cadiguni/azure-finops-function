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