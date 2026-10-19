variable "aplicacao" {
  type = string
}
variable "setor" {
  type = string
}

variable "ambiente" {
  description = "Ambiente alvo. prod mantém a nomenclatura legada <aplicacao>-<setor>-*; os demais usam <aplicacao>-<ambiente>-*."
  type        = string
  nullable    = false
  validation {
    condition     = contains(["dev", "hml", "prod"], var.ambiente)
    error_message = "ambiente deve ser dev, hml ou prod."
  }
}

variable "location" {
  description = "Região dos recursos do ambiente. null usa East US 2 (prod). O resource group permanece em East US 2."
  type        = string
  default     = null
}

variable "cosmos_location" {
  description = "Região da conta Cosmos DB. null usa a mesma região dos demais recursos (location)."
  type        = string
  default     = null
}

variable "cost_export_source_storage_account" {
  description = "Storage de outro ambiente cujos Cost Management Exports são lidos somente leitura (Storage Blob Data Reader no container de exports). null usa cost_export_storage_account_uri/AzureWebJobsStorage."
  type = object({
    name                = string
    resource_group_name = string
  })
  default = null
}

variable "reader_subscription_ids" {
  description = "Subscriptions em que a identidade recebe Reader e Cost Management Reader via Terraform e que são publicadas em AZURE_SUBSCRIPTION_IDS. Vazio mantém o modelo de prod (roles manuais no Management Group)."
  type        = set(string)
  default     = []
  nullable    = false
  validation {
    condition     = alltrue([for id in var.reader_subscription_ids : can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", id))])
    error_message = "Informe GUIDs de subscriptions."
  }
}

variable "root_management_group" {
  description = "Management Group raiz onde estão todas as subscriptions (dentro do Tenant Root Group)"
  type        = string
  default     = "Geral"
}

variable "cosmos_account_name" {
  description = "Nome da conta do Cosmos DB (opcional)."
  type        = string
  default     = ""
}

variable "cosmos_database_name" {
  description = "Nome do database Cosmos para dados operacionais FinOps."
  type        = string
  default     = "finops"
}

variable "enable_cleanup_requests_container" {
  description = "Cria container opcional cleanupRequests."
  type        = bool
  default     = false
}

variable "ownership_auth_allowed_audiences" {
  description = "Audiences exatas (claim aud) aceitas nos access tokens da API Entra. Não usar scope /FinOps.Access nem o client ID do SPA por inferência."
  type        = set(string)
  default     = []
  nullable    = false
  validation {
    condition     = alltrue([for audience in var.ownership_auth_allowed_audiences : trimspace(audience) != "" && !strcontains(audience, ",")])
    error_message = "Informe audiences não vazias e sem vírgulas."
  }
}

variable "config_container_name" {
  description = "Container Blob operacional existente, compartilhado por ownership e governance. Não cria nem migra containers."
  type        = string
  default     = "finops-config"
  nullable    = false

  validation {
    condition     = can(regex("^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$", var.config_container_name)) && !strcontains(var.config_container_name, "--")
    error_message = "config_container_name deve ser um nome válido de container Blob privado existente (3–63 caracteres)."
  }
}

variable "cost_export_container_name" {
  description = "Container central que recebe os arquivos brutos do Cost Management Export."
  type        = string
  default     = "cost-exports"
}

variable "enable_cost_export_import" {
  description = "Habilita a importação dos Cost Management Exports."
  type        = bool
  default     = true
}

variable "cost_export_import_schedule" {
  description = "Expressão NCRONTAB do timer que importa Cost Management Exports."
  type        = string
  default     = "0 0 */2 * * *"

  validation {
    condition     = trimspace(var.cost_export_import_schedule) != ""
    error_message = "cost_export_import_schedule não pode ser vazio."
  }
}

variable "enable_ownership_management_group_sync" {
  description = "Habilita a sincronização automática de Ownership a partir dos Parent Management Groups."
  type        = bool
  default     = true
}

variable "ownership_management_group_sync_schedule" {
  description = "Expressão NCRONTAB da sincronização automática de Ownership."
  type        = string
  default     = "0 5 9 * * 1"

  validation {
    condition     = trimspace(var.ownership_management_group_sync_schedule) != ""
    error_message = "ownership_management_group_sync_schedule não pode ser vazio."
  }
}

variable "cost_export_import_all_subscriptions" {
  description = "Descobre e importa todas as subscriptions que possuem prefixo no container central de Exports."
  type        = bool
  default     = true
}

variable "enable_legacy_cost_by_service_collection" {
  description = "Permite reativar temporariamente o coletor legado de custos por serviço."
  type        = bool
  default     = false
}

variable "enable_legacy_cost_by_resource_collection" {
  description = "Permite reativar temporariamente o coletor legado de custos por recurso."
  type        = bool
  default     = false
}

variable "cost_export_subscription_ids" {
  description = "Allowlist usada quando cost_export_import_all_subscriptions=false."
  type        = set(string)
  default     = []

  validation {
    condition = alltrue([
      for subscription_id in var.cost_export_subscription_ids :
      can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", subscription_id))
    ])
    error_message = "Todos os IDs em cost_export_subscription_ids devem ser GUIDs válidos."
  }
}

variable "cost_export_cutover_subscription_ids" {
  description = "Subscriptions que deixam explicitamente os coletores legados e publicam exports nos paths oficiais."
  type        = set(string)
  default     = []

  validation {
    condition = alltrue([
      for subscription_id in var.cost_export_cutover_subscription_ids :
      can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", subscription_id))
    ])
    error_message = "Todos os IDs em cost_export_cutover_subscription_ids devem ser GUIDs válidos."
  }
}

variable "cost_export_storage_account_uri" {
  description = "URI HTTPS opcional do Blob Storage central de Exports; vazio reutiliza AzureWebJobsStorage."
  type        = string
  default     = ""

  validation {
    condition     = var.cost_export_storage_account_uri == "" || can(regex("^https://", var.cost_export_storage_account_uri))
    error_message = "cost_export_storage_account_uri deve estar vazia ou ser uma URI HTTPS."
  }
}

variable "cost_export_max_manifests_per_run" {
  description = "Limite de manifests processados por execução do timer de exports."
  type        = number
  default     = 100

  validation {
    condition     = var.cost_export_max_manifests_per_run > 0
    error_message = "cost_export_max_manifests_per_run deve ser maior que zero."
  }
}

variable "cost_export_anomaly_max_staleness_days" {
  description = "Defasagem máxima do watermark de Export antes do fallback de anomalias para a Query API."
  type        = number
  default     = 3

  validation {
    condition     = var.cost_export_anomaly_max_staleness_days >= 0
    error_message = "cost_export_anomaly_max_staleness_days não pode ser negativo."
  }
}

variable "cost_anomaly_billing_periods_api_version" {
  description = "Versão da Azure Billing Periods API usada para resolver ciclos oficiais."
  type        = string
  default     = "2018-03-01-preview"
}
