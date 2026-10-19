# Resource Group para FinOps NAP
resource "azurerm_resource_group" "rg" {
  name     = "${local.nome_base}-rg"
  location = local.localizacao_rg
  tags     = local.tags
  
  lifecycle {
    ignore_changes = [
      tags
    ]
  }
}