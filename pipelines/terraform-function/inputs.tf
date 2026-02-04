variable "aplicacao" {
  type = string
}
variable "setor" {
  type = string
}

variable "webhookUrlTeams" {
  type        = string
  description = "URL do Webhook do Teams. Pode ser nulo ou vazio."
  default     = null
}

variable "root_management_group" {
  description = "Management Group raiz onde estão todas as subscriptions"
  type        = string
  default     = "Geral"
}
