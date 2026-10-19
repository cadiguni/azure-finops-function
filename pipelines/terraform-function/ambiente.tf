# Recursos que existem para permitir ambientes além de prod (hml/dev) sem tocar nos dados de prod.
# Em prod todas as variáveis abaixo ficam vazias/null e nada aqui é criado.

# Exports lidos de outro ambiente (ex.: hml lendo o storage de prod).
# Somente Storage Blob Data Reader no container: escrita/exclusão é negada pelo Azure, não pelo código.
data "azurerm_storage_account" "cost_export_source" {
  count               = var.cost_export_source_storage_account != null ? 1 : 0
  name                = var.cost_export_source_storage_account.name
  resource_group_name = var.cost_export_source_storage_account.resource_group_name
}

resource "azurerm_role_assignment" "cost_export_source_reader" {
  count                = var.cost_export_source_storage_account != null ? 1 : 0
  scope                = "${data.azurerm_storage_account.cost_export_source[0].id}/blobServices/default/containers/${var.cost_export_container_name}"
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = azurerm_user_assigned_identity.finops_identity.principal_id
  principal_type       = "ServicePrincipal"
}

# Leitura limitada a subscriptions escolhidas, em vez de Reader/Cost Management Reader no Management Group (modelo de prod).
resource "azurerm_role_assignment" "subscription_reader" {
  for_each             = toset([for id in var.reader_subscription_ids : lower(id)])
  scope                = "/subscriptions/${each.value}"
  role_definition_name = "Reader"
  principal_id         = azurerm_user_assigned_identity.finops_identity.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "subscription_cost_reader" {
  for_each             = toset([for id in var.reader_subscription_ids : lower(id)])
  scope                = "/subscriptions/${each.value}"
  role_definition_name = "Cost Management Reader"
  principal_id         = azurerm_user_assigned_identity.finops_identity.principal_id
  principal_type       = "ServicePrincipal"
}

# Em prod estes containers já existem fora do state; em ambientes novos são criados aqui
# porque partes do backend (governança, perfis de migração) não usam CreateIfNotExists.
resource "azurerm_storage_container" "config" {
  count                 = local.is_prod ? 0 : 1
  name                  = var.config_container_name
  storage_account_name  = azurerm_storage_account.storage.name
  container_access_type = "private"
}

resource "azurerm_storage_container" "analysis" {
  count                 = local.is_prod ? 0 : 1
  name                  = "finops-analysis"
  storage_account_name  = azurerm_storage_account.storage.name
  container_access_type = "private"
}

locals {
  cost_export_storage_account_uri = (
    var.cost_export_source_storage_account != null
    ? data.azurerm_storage_account.cost_export_source[0].primary_blob_endpoint
    : var.cost_export_storage_account_uri
  )

  # Só publica AZURE_SUBSCRIPTION_IDS quando há lista explícita, para não alterar os App Settings de prod.
  ambiente_app_settings = length(var.reader_subscription_ids) > 0 ? {
    "AZURE_SUBSCRIPTION_IDS" = join(",", sort([for id in var.reader_subscription_ids : lower(id)]))
  } : {}
}
