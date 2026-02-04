# Data source para configuração atual do cliente
data "azurerm_client_config" "current" {}

# Data source para o Management Group "Geral"
data "azurerm_management_group" "root" {
  display_name = "Geral"  # Usando display_name em vez de name
}

# Managed Identity para a Function App
resource "azurerm_user_assigned_identity" "finops_identity" {
  name                = local.finops_settings.managed_identity_name
  resource_group_name = azurerm_resource_group.rg.name
  location            = local.localizacao
  
  tags = local.tags
}

# Role assignment para Reader no Management Group "Geral"
# Isso permite acesso a todas as subscriptions dentro de "Geral"
resource "azurerm_role_assignment" "mg_reader" {
  scope                = data.azurerm_management_group.root.id
  role_definition_name = "Reader"
  principal_id         = azurerm_user_assigned_identity.finops_identity.principal_id
}

# Role assignment para Cost Management Reader no Management Group "Geral"  
# Permite leitura de custos em toda a hierarquia do "Geral"
resource "azurerm_role_assignment" "mg_cost_reader" {
  scope                = data.azurerm_management_group.root.id
  role_definition_name = "Cost Management Reader"
  principal_id         = azurerm_user_assigned_identity.finops_identity.principal_id
}