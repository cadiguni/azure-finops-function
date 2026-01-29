# Storage Account para Function App
resource "azurerm_storage_account" "storage" {
  name                     = "${lower(local.aplicacao)}${lower(local.ambiente)}funcstg"
  resource_group_name      = local.ambiente == "prod" ? data.azurerm_resource_group.rg[0].name : azurerm_resource_group.rg[0].name
  location                 = local.localizacao
  account_tier             = "Standard"
  account_replication_type = "LRS"

  tags = local.tags
}