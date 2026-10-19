# HML: ambiente de teste de telas e correções (finopsplatform-hml-*).
# Lê os Cost Management Exports reais de prod somente leitura; tudo o que grava
# (checkpoints, análises, relatórios, ownership, status, auditoria) fica no storage/Cosmos de HML.

# Quota "Total Regional VMs" de App Service em East US 2 esgotada (2 de 2) e sem ajuste disponível na MPN:
# todos os recursos de HML ficam em East US (o RG continua em East US 2, só metadado).
location = "East US"
# Cosmos em East US recusado por capacidade ("high demand in East US region") para a MPN.
cosmos_location = "East US 2"

# Exports de prod: Storage Blob Data Reader apenas no container cost-exports.
cost_export_source_storage_account = {
  name                = "finopsplatformnapfuncstg"
  resource_group_name = "finopsplatform-nap-rg"
}

# Sem Reader no Management Group: leitura de recursos (analyzers) apenas nestas subscriptions.
# Custos continuam vindo dos exports de todas as subscriptions.
reader_subscription_ids = [
  "00000000-0000-0000-0000-00000000a002",
]

# Depende de leitura do Management Group, que HML não tem.
enable_ownership_management_group_sync = false
