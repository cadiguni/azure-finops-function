# Storage Account para Frontend Static Website
# O RG é criado pelo terraform-function, referenciamos via data source
data "azurerm_resource_group" "rg" {
  name = "${local.aplicacao}-${local.setor}-rg"
}

resource "azurerm_storage_account" "frontend" {
  name                     = "${lower(local.aplicacao)}${lower(local.setor)}webstg"
  resource_group_name      = data.azurerm_resource_group.rg.name
  location                 = local.localizacao
  account_tier             = "Standard"
  account_replication_type = "LRS"

  static_website {
    index_document     = "index.html"
    error_404_document = "index.html"
  }

  tags = local.tags

  lifecycle {
    ignore_changes = [
      tags
    ]
  }
}

output "static_website_url" {
  value = azurerm_storage_account.frontend.primary_web_endpoint
}

output "storage_account_name" {
  value = azurerm_storage_account.frontend.name
}
