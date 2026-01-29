# Outputs principais do Terraform para Cost Optimizer
output "function_app_name" {
  description = "Nome da Function App criada"
  value       = azurerm_linux_function_app.cost_optimizer.name
}

output "function_app_url" {
  description = "URL da Function App"
  value       = "https://${azurerm_linux_function_app.cost_optimizer.name}.azurewebsites.net"
}

output "storage_account_name" {
  description = "Nome da Storage Account para resultados"
  value       = azurerm_storage_account.cost_optimizer.name
}

output "managed_identity_principal_id" {
  description = "Principal ID da Managed Identity"
  value       = azurerm_user_assigned_identity.cost_optimizer.principal_id
}

output "managed_identity_client_id" {
  description = "Client ID da Managed Identity"
  value       = azurerm_user_assigned_identity.cost_optimizer.client_id
}

output "application_insights_connection_string" {
  description = "Connection String do Application Insights"
  value       = azurerm_application_insights.cost_optimizer.connection_string
  sensitive   = true
}

output "web-app-name" {
  value = azurerm_linux_web_app.webapp_a.name
  sensitive = true
}

output "action_group_ids" {
  description = "IDs dos grupos de ação criados"
  value = {
    for name, ag in azurerm_monitor_action_group.action_groups : name => ag.id
  }
}

output "alert_ids" {
  description = "IDs dos alertas criados"
  value = {
    api                  = try([for alert in azurerm_monitor_metric_alert.api_alerts : alert.id], [])
    performance_cpu      = try([for alert in azurerm_monitor_metric_alert.performance_cpu_alerts : alert.id], [])
    performance_memory   = try([for alert in azurerm_monitor_metric_alert.performance_memory_alerts : alert.id], [])
    http_5xx             = try([for alert in azurerm_monitor_metric_alert.http_5xx_alerts : alert.id], [])
    http_403             = try([for alert in azurerm_monitor_metric_alert.http_403_alerts : alert.id], [])
    response_time        = try([for alert in azurerm_monitor_metric_alert.response_time_alerts : alert.id], [])
    availability         = try([for alert in azurerm_monitor_metric_alert.availability_alerts : alert.id], [])
    sql_server           = try([for alert in azurerm_monitor_metric_alert.sql_server_alerts : alert.id], [])
    app_service_plan_queue = try([for alert in azurerm_monitor_metric_alert.app_service_plan_queue_alerts : alert.id], [])
  }
}

output "application_name" {
  description = "Nome da aplicação"
  value       = local.aplicacao
}

output "ambiente" {
  description = "Ambiente da aplicação"
  value       = local.ambiente
}

output "location" {
  description = "Localização dos recursos"
  value       = local.localizacao
}

output "resource_group_name" {
  description = "Nome do Resource Group"
  value       = local.ambiente == "prod" ? data.azurerm_resource_group.rg[0].name : azurerm_resource_group.rg[0].name
}
