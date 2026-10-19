locals {
  # Ver terraform-function/locals.tf: tags novas só valem para recursos criados depois.
  tags = {
    Responsável = "seu-email@example.com"
    Setor       = "NAP"
    Ambiente    = var.ambiente
  }

  localizacao = "East US 2"
  aplicacao   = var.aplicacao
  setor       = var.setor

  # Mesmo critério do terraform-function: prod mantém <aplicacao>-<setor>, demais <aplicacao>-<ambiente>.
  nome_base         = var.ambiente == "prod" ? "${local.aplicacao}-${local.setor}" : "${local.aplicacao}-${var.ambiente}"
  nome_base_storage = lower(replace(local.nome_base, "-", ""))
}
