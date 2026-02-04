variable "aplicacao" {
  type = string
}
variable "setor" {
  type = string
}

variable "root_management_group" {
  description = "Management Group raiz onde estão todas as subscriptions"
  type        = string
  default     = "Geral"
}
