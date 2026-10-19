resource "azurerm_cosmosdb_account" "finops" {
  name                = local.finops_settings.cosmos_account_name
  location            = local.localizacao_cosmos
  resource_group_name = azurerm_resource_group.rg.name
  offer_type          = "Standard"
  kind                = "GlobalDocumentDB"

  consistency_policy {
    consistency_level = "Session"
  }

  geo_location {
    location          = local.localizacao_cosmos
    failover_priority = 0
    zone_redundant    = false
  }

  capabilities {
    name = "EnableServerless"
  }

  tags = local.tags

  lifecycle {
    ignore_changes = [
      tags
    ]
  }
}

resource "azurerm_cosmosdb_sql_database" "finops" {
  name                = var.cosmos_database_name
  resource_group_name = azurerm_resource_group.rg.name
  account_name        = azurerm_cosmosdb_account.finops.name
}

resource "azurerm_cosmosdb_sql_container" "ownership" {
  name                = "ownership"
  resource_group_name = azurerm_resource_group.rg.name
  account_name        = azurerm_cosmosdb_account.finops.name
  database_name       = azurerm_cosmosdb_sql_database.finops.name
  partition_key_paths = ["/teamId"]
}

resource "azurerm_cosmosdb_sql_container" "recommendation_status" {
  name                = "recommendationStatus"
  resource_group_name = azurerm_resource_group.rg.name
  account_name        = azurerm_cosmosdb_account.finops.name
  database_name       = azurerm_cosmosdb_sql_database.finops.name
  partition_key_paths = ["/resourceId"]
}

resource "azurerm_cosmosdb_sql_container" "audit_logs" {
  name                = "auditLogs"
  resource_group_name = azurerm_resource_group.rg.name
  account_name        = azurerm_cosmosdb_account.finops.name
  database_name       = azurerm_cosmosdb_sql_database.finops.name
  partition_key_paths = ["/entityId"]
}

resource "azurerm_cosmosdb_sql_container" "cost_anomalies" {
  name                = "costAnomalies"
  resource_group_name = azurerm_resource_group.rg.name
  account_name        = azurerm_cosmosdb_account.finops.name
  database_name       = azurerm_cosmosdb_sql_database.finops.name
  partition_key_paths = ["/requestedDate"]
}

resource "azurerm_cosmosdb_sql_container" "portal_summaries" {
  name                = "portalSummaries"
  resource_group_name = azurerm_resource_group.rg.name
  account_name        = azurerm_cosmosdb_account.finops.name
  database_name       = azurerm_cosmosdb_sql_database.finops.name
  partition_key_paths = ["/scopeKey"]
}

resource "azurerm_cosmosdb_sql_container" "cleanup_requests" {
  count               = var.enable_cleanup_requests_container ? 1 : 0
  name                = "cleanupRequests"
  resource_group_name = azurerm_resource_group.rg.name
  account_name        = azurerm_cosmosdb_account.finops.name
  database_name       = azurerm_cosmosdb_sql_database.finops.name
  partition_key_paths = ["/teamId"]
}

resource "azurerm_cosmosdb_sql_role_assignment" "function_data_contributor" {
  resource_group_name = azurerm_resource_group.rg.name
  account_name        = azurerm_cosmosdb_account.finops.name
  role_definition_id  = "${azurerm_cosmosdb_account.finops.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
  principal_id        = azurerm_user_assigned_identity.finops_identity.principal_id
  scope               = azurerm_cosmosdb_account.finops.id
}
