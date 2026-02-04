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
    azurerm_user_assigned_identity.finops_identity
  ]

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.finops_identity.id]
  }

  site_config {
    # Configuração mínima e estável
    always_on = false  # Consumption plan não suporta always_on = true
    
    application_stack {
      dotnet_version              = "8.0"
      use_dotnet_isolated_runtime = true
    }
  }

  # Configuração essencial - FinOps será configurado via Pipeline
  app_settings = {
    # Runtime essencial
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
    
    # Identity e configurações base
    "AZURE_CLIENT_ID"                    = azurerm_user_assigned_identity.finops_identity.client_id
    "AZURE_SUBSCRIPTION_ID"             = data.azurerm_client_config.current.subscription_id
    "FinOps__SubscriptionId"             = data.azurerm_client_config.current.subscription_id
    "FinOps__TenantId"                   = data.azurerm_client_config.current.tenant_id
    "FinOps__StorageAccountName"         = azurerm_storage_account.storage.name
  }
  
  tags = local.tags
  
  lifecycle {
    ignore_changes = [
      tags
    ]
  }
}