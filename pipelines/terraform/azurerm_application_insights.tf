# Application Insights para monitoramento da Function App
resource "azurerm_application_insights" "cost_optimizer" {
  name                = local.cost_optimizer_settings.application_insights_name
  location            = local.localizacao
  resource_group_name = local.ambiente == "prod" ? data.azurerm_resource_group.rg[0].name : azurerm_resource_group.rg[0].name
  application_type    = "web"
  
  tags = local.tags

  lifecycle {
    ignore_changes = [
      tags
    ]
  }
}