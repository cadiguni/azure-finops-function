# Usado para desenvolvimento e homologação
resource "azurerm_resource_group" "rg" {
  count    = local.ambiente == "prod" ? 0 : 1
  name     = "${local.aplicacao}-${local.ambiente}-rg"
  location = local.localizacao
  tags     = local.tags
}

# Usado para produção
data "azurerm_resource_group" "rg" {
  count = local.ambiente == "prod" ? 1 : 0
  name  = "${local.aplicacao}-${local.ambiente}-rg"
}