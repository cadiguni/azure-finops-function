# Data source para configuração atual do cliente
data "azurerm_client_config" "current" {}

# Managed Identity para a Function App
resource "azurerm_user_assigned_identity" "finops_identity" {
  name                = local.finops_settings.managed_identity_name
  resource_group_name = azurerm_resource_group.rg.name
  location            = local.localizacao
  
  tags = local.tags
}

# ========================================================================================
# PERMISSÕES MANUAIS NECESSÁRIAS APÓS O DEPLOY:
# ========================================================================================
# 
# Depois do deploy, configure manualmente estas permissões para a Managed Identity:
#
# 1. No Azure Portal, vá para "Management Groups" → "Geral"
# 2. Clique em "Access control (IAM)" → "Add role assignment" 
# 3. Adicione estas roles para a Managed Identity "finopsplatform-nap-identity":
#    - Reader
#    - Cost Management Reader
#
# OU via Azure CLI:
# az role assignment create --assignee $(az identity show --name finopsplatform-nap-identity --resource-group finopsplatform-nap-rg --query principalId -o tsv) --role "Reader" --scope "/providers/Microsoft.Management/managementGroups/Geral"
# az role assignment create --assignee $(az identity show --name finopsplatform-nap-identity --resource-group finopsplatform-nap-rg --query principalId -o tsv) --role "Cost Management Reader" --scope "/providers/Microsoft.Management/managementGroups/Geral"
#
# ========================================================================================