variable "aplicacao" {
  type = string
}
variable "setor" {
  type = string
}

variable "root_management_group" {
  description = "Management Group raiz onde estão todas as subscriptions (dentro do Tenant Root Group)"
  type        = string
  default     = "Geral"
}
