# Application Insights para monitoramento da Function App
resource "azurerm_application_insights" "cost_optimizer" {
  name                = local.finops_settings.application_insights_name
  location            = local.localizacao
  resource_group_name = azurerm_resource_group.rg.name
  application_type    = "web"
  
  tags = local.tags

  lifecycle {
    ignore_changes = [
      tags
    ]
  }
}