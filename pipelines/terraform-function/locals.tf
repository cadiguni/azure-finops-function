locals {
  dominio = "example.com"
  # Os recursos ignoram mudanças de tags (automações de shutdown e o App Insights gravam tags próprias):
  # tags novas só valem para recursos criados depois; nos existentes, aplicar com az tag update --operation Merge.
  tags = {
    Responsável = "seu-email@example.com"
    Setor       = "NAP"
    # Prod usa nomes legados "nap": a tag deixa o ambiente explícito no portal do Azure.
    Ambiente = var.ambiente
  }

  # Região dos recursos; o RG fica sempre em East US 2 (só metadado) porque trocá-lo destruiria
  # o RG inteiro, incluindo o storage do portal gerenciado pelo terraform-frontend.
  localizacao    = coalesce(var.location, "East US 2")
  localizacao_rg = "East US 2"
  # Cosmos pode precisar de outra região quando a principal não tem capacidade para a subscription.
  localizacao_cosmos = coalesce(var.cosmos_location, local.localizacao)
  aplicacao      = var.aplicacao
  setor          = var.setor
  ambiente       = var.ambiente
  is_prod        = var.ambiente == "prod"

  # Prod mantém a nomenclatura legada <aplicacao>-<setor>-* para não recriar recursos existentes.
  # Demais ambientes usam <aplicacao>-<ambiente>-*, o mesmo padrão do template de deploy (<NomeAplicacao>-<Ambiente>-func).
  nome_base         = local.is_prod ? "${local.aplicacao}-${local.setor}" : "${local.aplicacao}-${local.ambiente}"
  nome_base_storage = lower(replace(local.nome_base, "-", ""))

  # Configurações específicas do FinOps - Nomenclatura dos recursos
  finops_settings = {
    function_app_name         = "${local.nome_base}-func"
    app_service_plan_name     = "${local.nome_base}-plan"
    application_insights_name = "${local.nome_base}-ai"
    managed_identity_name     = "${local.nome_base}-identity"
    cosmos_account_name       = var.cosmos_account_name != "" ? var.cosmos_account_name : lower("${local.nome_base}-cosmos")
  }
  
  # NOTA: As configurações de app_settings estão definidas diretamente em azurerm_function_app.tf
  # para maior clareza e controle do que é realmente aplicado.
}