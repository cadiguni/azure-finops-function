# Storage Account para resultados das análises e logs
resource "azurerm_storage_account" "finops_storage" {
  name                     = local.finops_settings.storage_account_name
  resource_group_name      = local.ambiente == "prod" ? data.azurerm_resource_group.rg[0].name : azurerm_resource_group.rg[0].name
  location                 = local.localizacao
  account_tier             = "Standard"
  account_replication_type = "LRS"
  
  tags = local.tags
}

# Container para armazenar resultados das análises
resource "azurerm_storage_container" "analysis_results" {
  name                  = "analysis-results"
  storage_account_name  = azurerm_storage_account.finops_storage.name
  container_access_type = "private"
}

# Container para logs
resource "azurerm_storage_container" "logs" {
  name                  = "logs"
  storage_account_name  = azurerm_storage_account.finops_storage.name
  container_access_type = "private"
}