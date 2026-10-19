variable "aplicacao" {
  type = string
}

variable "setor" {
  type = string
}

variable "ambiente" {
  description = "Ambiente alvo. prod mantém a nomenclatura legada <aplicacao><setor>webstg; os demais usam <aplicacao><ambiente>webstg."
  type        = string
  nullable    = false
  validation {
    condition     = contains(["dev", "hml", "prod"], var.ambiente)
    error_message = "ambiente deve ser dev, hml ou prod."
  }
}
