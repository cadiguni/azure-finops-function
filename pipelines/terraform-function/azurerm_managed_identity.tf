# Data source para o Management Group raiz
data "azurerm_management_group" "root" {
  name = var.root_management_group
}

# Managed Identity para a Function App
resource "azurerm_user_assigned_identity" "finops_identity" {
  name                = local.finops_settings.managed_identity_name
  resource_group_name = azurerm_resource_group.rg.name
  location            = local.localizacao
  
  tags = local.tags
}

# Role assignment para Reader no Management Group raiz
# Isso automaticamente cobre: Setores, VisualStudio, Todos os MPNs, Todas as subscriptions
resource "azurerm_role_assignment" "root_reader" {
  scope                = data.azurerm_management_group.root.id
  role_definition_name = "Reader"
  principal_id         = azurerm_user_assigned_identity.finops_identity.principal_id
}

# Role assignment para Cost Management Reader no Management Group raiz  
# Permite leitura de custos em toda a hierarquia
resource "azurerm_role_assignment" "root_cost_reader" {
  scope                = data.azurerm_management_group.root.id
  role_definition_name = "Cost Management Reader"
  principal_id         = azurerm_user_assigned_identity.finops_identity.principal_id
}

# Data source para configuração atual do cliente
data "azurerm_client_config" "current" {}