variable "aplicacao" {
  type = string
}
variable "ambiente" {
  type = string
}

variable "webhookUrlTeams" {
  type        = string
  description = "URL do Webhook do Teams. Pode ser nulo ou vazio."
  default     = null
}
