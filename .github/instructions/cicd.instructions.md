---
applyTo: "pipelines/**/*.yml"
---

# Azure DevOps Pipelines

- Build e deploy sao pipelines separados; os artefatos sao por ambiente (`ArtifactName`/`FrontendArtifactName` no `config.yml`) e nao podem conter "prod" no nome.
- Use o pool `self-hosted-agent-pool` e os Variable Groups existentes.
- Secrets pertencem a Variable Groups e devem ser marcados como secret.
- App Settings da Function sao gerenciados pelo Terraform; nao os duplique no `AzureFunctionApp@1`.
- O deploy deve executar Terraform init/plan/apply antes de publicar a Function.
- Preserve o backend remoto e as service connections; nunca inclua access keys no YAML.

