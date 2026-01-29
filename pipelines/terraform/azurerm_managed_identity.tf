# Managed Identity para a Function App
resource "azurerm_user_assigned_identity" "finops_identity" {
  name                = local.finops_settings.managed_identity_name
  resource_group_name = local.ambiente == "prod" ? data.azurerm_resource_group.rg[0].name : azurerm_resource_group.rg[0].name
  location            = local.localizacao
  
  tags = local.tags
}

# Data source para obter o Management Group (se existir)
data "azurerm_management_group" "root" {
  count = local.ambiente == "prod" ? 1 : 0
  name  = "mg-gvdasa-root"
}

# Role assignment para Cost Management Reader no Management Group (produção)
resource "azurerm_role_assignment" "cost_management_reader_mg" {
  count                = local.ambiente == "prod" ? 1 : 0
  scope                = data.azurerm_management_group.root[0].id
  role_definition_name = "Cost Management Reader"
  principal_id         = azurerm_user_assigned_identity.finops_identity.principal_id
}

# Role assignment para Reader no Management Group (produção)
resource "azurerm_role_assignment" "reader_mg" {
  count                = local.ambiente == "prod" ? 1 : 0
  scope                = data.azurerm_management_group.root[0].id
  role_definition_name = "Reader"
  principal_id         = azurerm_user_assigned_identity.finops_identity.principal_id
}

# Role assignment para Cost Management Reader na subscription (dev/hml)
resource "azurerm_role_assignment" "cost_management_reader_sub" {
  count                = local.ambiente != "prod" ? 1 : 0
  scope                = "/subscriptions/${data.azurerm_client_config.current.subscription_id}"
  role_definition_name = "Cost Management Reader"
  principal_id         = azurerm_user_assigned_identity.finops_identity.principal_id
}

# Role assignment para Reader na subscription (dev/hml)
resource "azurerm_role_assignment" "reader_sub" {
  count                = local.ambiente != "prod" ? 1 : 0
  scope                = "/subscriptions/${data.azurerm_client_config.current.subscription_id}"
  role_definition_name = "Reader"
  principal_id         = azurerm_user_assigned_identity.finops_identity.principal_id
}

# Data source para configuração atual do cliente
data "azurerm_client_config" "current" {}