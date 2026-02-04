# Azure Function App - FinOps Platform

# App Service Plan para a Function App
resource "azurerm_service_plan" "finops" {
  name                = local.finops_settings.app_service_plan_name
  location            = local.localizacao
  resource_group_name = azurerm_resource_group.rg.name
  os_type             = "Linux"
  sku_name            = "Y1"  # Consumption plan
  
  tags = local.tags
}

# Function App
resource "azurerm_linux_function_app" "finops" {
  name                       = local.finops_settings.function_app_name
  location                   = local.localizacao
  resource_group_name        = azurerm_resource_group.rg.name
  service_plan_id           = azurerm_service_plan.finops.id
  storage_account_name       = azurerm_storage_account.storage.name
  storage_account_access_key = azurerm_storage_account.storage.primary_access_key

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.finops_identity.id]
  }

  site_config {
    application_stack {
      dotnet_version              = "8.0"
      use_dotnet_isolated_runtime = true
    }
    
    application_insights_key               = azurerm_application_insights.cost_optimizer.instrumentation_key
    application_insights_connection_string = azurerm_application_insights.cost_optimizer.connection_string
  }

  app_settings = {
    # Azure Configuration
    "AZURE_CLIENT_ID"                    = azurerm_user_assigned_identity.finops_identity.client_id
    "WEBSITE_RUN_FROM_PACKAGE"           = "1"
    
    # Application Insights
    "APPINSIGHTS_INSTRUMENTATIONKEY"     = azurerm_application_insights.cost_optimizer.instrumentation_key
    "APPLICATIONINSIGHTS_CONNECTION_STRING" = azurerm_application_insights.cost_optimizer.connection_string
    
    # Storage Configuration
    "AZURE_STORAGE_CONNECTION_STRING"    = azurerm_storage_account.storage.primary_connection_string
    
    # FinOps Configuration
    "FinOps__SubscriptionId"             = data.azurerm_client_config.current.subscription_id
    "FinOps__TenantId"                   = data.azurerm_client_config.current.tenant_id
    "FinOps__StorageAccountName"         = azurerm_storage_account.storage.name
    "FinOps__StorageContainerName"       = "analysis-results"
    
    # Scope Configuration - Centralizar escopo para produção
    "FinOps__Scope__Mode"                = "ManagementGroup"
    "FinOps__Scope__ManagementGroupId"   = "mg-gvdasa"
    "FinOps__Scope__IncludeSubscriptions" = "[]"
    "FinOps__Scope__ExcludeSubscriptions" = "[]"
    
    # Azure API Endpoints
    "FinOps__CostManagementApiUrl"       = "https://management.azure.com"
    "FinOps__ResourceGraphApiUrl"        = "https://management.azure.com/providers/Microsoft.ResourceGraph/resources"
    "FinOps__MonitorApiUrl"              = "https://management.azure.com"
    
    # Analysis Configuration
    "FinOps__VmCpuThreshold"             = "2.0"
    "FinOps__VmMemoryThreshold"          = "10.0"
    "FinOps__UnusedDiskDays"             = "7"
    "FinOps__LowTrafficThreshold"        = "100"
    "FinOps__SqlDtuThreshold"            = "20.0"
    
    # Notification Configuration (opcional)
    "FinOps__NotificationEnabled"        = "false"
    "FinOps__EmailRecipients"            = ""
  }
  
  tags = local.tags
}