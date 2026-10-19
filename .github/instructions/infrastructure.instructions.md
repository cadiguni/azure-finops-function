---
applyTo: "pipelines/terraform-function/**"
---

# Terraform e Azure

- Nao execute Terraform localmente contra o state de producao; provisionamento ocorre pelo Azure DevOps.
- Preserve o backend remoto existente e o padrao de nomes via `local.nome_base`: `{aplicacao}-{setor}-{recurso}` em prod e `{aplicacao}-{ambiente}-{recurso}` nos demais ambientes (nome de storage com setor+ambiente passaria de 24 caracteres).
- Valores por ambiente ficam em `environments/<ambiente>.tfvars`; state em `<ambiente>/<aplicacao>.tfstate`. Fora de prod nao conceda RBAC no Management Group.
- Function App: Linux, .NET 8 isolated, Consumption Y1 e User Assigned Managed Identity.
- Use Managed Identity e RBAC minimo; nunca grave secrets, access keys ou connection strings em `.tf`.
- Centralize App Settings no Terraform e use variaveis/locals; nao hardcode containers, schedules ou IDs.
- Recursos principais: Function App, Storage, Service Bus, Cosmos DB, Application Insights e Log Analytics.
- Antes de aplicar, revise o plan do pipeline; alteracoes destrutivas exigem justificativa explicita.

