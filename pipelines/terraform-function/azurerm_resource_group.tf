# Resource Group para FinOps NAP
resource "azurerm_resource_group" "rg" {
  name     = "${local.aplicacao}-${local.setor}-rg"
  location = local.localizacao
  tags     = local.tags
  
  lifecycle {
    ignore_changes = [
      tags
    ]
  }
}