# Storage Account para Function App
resource "azurerm_storage_account" "storage" {
  name                     = "${lower(local.aplicacao)}${lower(local.setor)}funcstg"
  resource_group_name      = azurerm_resource_group.rg.name
  location                 = local.localizacao
  account_tier             = "Standard"
  account_replication_type = "LRS"

  tags = local.tags
  
  lifecycle {
    ignore_changes = [
      tags
    ]
  }
}