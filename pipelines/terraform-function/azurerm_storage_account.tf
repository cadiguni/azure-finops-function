# Storage Account para Function App
resource "azurerm_storage_account" "storage" {
  name                     = "${local.nome_base_storage}funcstg"
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

resource "azurerm_storage_container" "cost_exports" {
  name                  = var.cost_export_container_name
  storage_account_name  = azurerm_storage_account.storage.name
  container_access_type = "private"
}