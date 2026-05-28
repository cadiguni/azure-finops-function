# Infraestrutura Azure - FinOps Platform

## Visão Geral

A infraestrutura do projeto é gerenciada via **Terraform** e provisionada no Azure. Todos os recursos ficam em um único Resource Group e seguem um padrão de nomenclatura consistente.

## Localização dos Arquivos

```
pipelines/terraform-function/
├── backend.tf                    # Configuração do state remoto
├── provider.tf                   # Provider Azure
├── inputs.tf                     # Variáveis de entrada
├── locals.tf                     # Configurações locais e settings
├── outputs.tf                    # Outputs para diagnóstico
├── azurerm_resource_group.tf     # Resource Group
├── azurerm_function_app.tf       # Function App + App Service Plan
├── azurerm_storage_account.tf    # Storage Account
├── azurerm_service_bus.tf        # Service Bus + Queues
├── azurerm_managed_identity.tf   # User Assigned Identity
├── azurerm_application_insights.tf   # App Insights
├── azurerm_log_analytics_workspace.tf # Log Analytics
└── terraform.tfvars.example      # Exemplo de variáveis
```

## Recursos Provisionados

### 1. Resource Group
- **Nome**: `{aplicacao}-{setor}-rg` (ex: `finopsplatform-nap-rg`)
- **Região**: East US 2

### 2. Azure Function App
- **Tipo**: Linux Function App (.NET 8 Isolated)
- **Plano**: Consumption (Y1) - paga por execução
- **Runtime**: `dotnet-isolated` v4
- **Identidade**: User Assigned Managed Identity

### 3. App Service Plan
- **SKU**: Y1 (Consumption)
- **Always On**: Desabilitado (não suportado em Consumption)

### 4. Storage Account
- **Nome**: `{aplicacao}{setor}funcstg` (sem hífens)
- **Tier**: Standard LRS
- **Uso**: 
  - WebJobs storage (obrigatório para Functions)
  - Resultados de análises (`finops-analysis`)
  - Configuração de times (`finops-config`)

### 5. Service Bus
- **SKU**: Basic (~$5/mês)
- **Queues**:
  - `subscription-analysis` - Análise de subscriptions
  - `storage-analysis` - Análise de Storage Accounts (heavy)
  - `vm-analysis` - Análise de VMs
  - `appservice-analysis` - Análise de App Services
- **Configurações**:
  - Lock Duration: 5 minutos
  - Max Delivery Count: 10
  - TTL: 7-14 dias

### 6. Application Insights
- **Tipo**: Web
- **Integrado com**: Log Analytics Workspace
- **Métricas**: Telemetria automática da Function

### 7. Log Analytics Workspace
- **SKU**: PerGB2018
- **Retenção**: 30 dias
- **Quota diária**: 0.1 GB
- **Tabelas customizadas**: AppMetrics, AppExceptions, AppDependencies

### 8. User Assigned Managed Identity
- **Nome**: `{aplicacao}-{setor}-identity`
- **Uso**: Autenticação com Azure APIs (Cost Management, Resource Graph)

## Padrão de Nomenclatura

```
{aplicacao}-{setor}-{recurso}
```

Exemplos:
- `finopsplatform-nap-rg` (Resource Group)
- `finopsplatform-nap-func` (Function App)
- `finopsplatform-nap-plan` (App Service Plan)
- `finopsplatform-nap-ai` (Application Insights)
- `finopsplatform-nap-servicebus` (Service Bus)
- `finopsplatform-nap-identity` (Managed Identity)
- `finopsplatformnap-funcstg` (Storage - sem hífens)

## Backend do Terraform

O state do Terraform é armazenado remotamente no Azure:

```hcl
terraform {
  backend "azurerm" {
    storage_account_name = "personalterraformstate"
    container_name       = "nap"
    key                  = "finops/{aplicacao}.tfstate"
  }
}
```

⚠️ **NUNCA** execute Terraform localmente contra o state de produção. Use sempre os pipelines Azure DevOps.

## Variáveis de Entrada

| Variável | Tipo | Descrição |
|----------|------|-----------|
| `aplicacao` | string | Nome da aplicação (ex: `finopsplatform`) |
| `setor` | string | Setor/time (ex: `nap`) |
| `root_management_group` | string | MG raiz para discovery (default: `Geral`) |

## App Settings da Function

A Function App recebe configurações via `locals.tf`:

### Runtime
- `FUNCTIONS_WORKER_RUNTIME`: dotnet-isolated
- `FUNCTIONS_EXTENSION_VERSION`: ~4
- `WEBSITE_RUN_FROM_PACKAGE`: 1

### Schedules (NCRONTAB - 6 campos)
- `FinOps__Schedules__CostAnalysis`: Análise de custos
- `FinOps__Schedules__DailySummary`: Resumo diário

### Paralelismo
- `FinOps__Execution__MaxParallelSubscriptions`: 3
- `FinOps__Execution__MaxParallelAnalyzers`: 5
- `FinOps__Execution__MaxSubscriptionProcessingTime`: 00:20:00

### Analyzers
- `FinOps__Analyzer__EnableVmAnalysis`: true/false
- `FinOps__Analyzer__EnableDiskAnalysis`: true/false
- `FinOps__Analyzer__EnableAppServiceAnalysis`: true/false
- `FinOps__Analyzer__EnableStorageAnalyzer`: true/false

### Thresholds
- `FinOps__Analyzer__LowCpuThreshold`: 5.0
- `FinOps__Analyzer__DaysInactiveThreshold`: 7
- `FinOps__Analyzer__MinimumMonthlySavingsToRecommend`: 30

## Permissões Manuais Pós-Deploy

Após o deploy do Terraform, configure manualmente as permissões da Managed Identity:

```bash
# No Management Group "Geral":
az role assignment create \
  --assignee $(az identity show --name finopsplatform-nap-identity \
    --resource-group finopsplatform-nap-rg --query principalId -o tsv) \
  --role "Reader" \
  --scope "/providers/Microsoft.Management/managementGroups/Geral"

az role assignment create \
  --assignee $(az identity show --name finopsplatform-nap-identity \
    --resource-group finopsplatform-nap-rg --query principalId -o tsv) \
  --role "Cost Management Reader" \
  --scope "/providers/Microsoft.Management/managementGroups/Geral"
```

## Outputs Úteis

| Output | Descrição |
|--------|-----------|
| `function_app_url` | URL base da Function App |
| `function_health_check_url` | URL para health check |
| `managed_identity_principal_id` | ID para role assignments |
| `storage_account_name` | Nome do Storage |
| `scm_url` | URL do Kudu para diagnósticos |

## Comandos Terraform

⚠️ **Execute apenas via pipeline** - nunca localmente em produção.

```bash
# Desenvolvimento local (apenas para testes)
cd pipelines/terraform-function

terraform init \
  -backend-config="access_key=<KEY>" \
  -backend-config="key=finops/test.tfstate"

terraform plan \
  -var="aplicacao=finopsplatform" \
  -var="setor=nap"

terraform apply \
  -var="aplicacao=finopsplatform" \
  -var="setor=nap"
```

## Custos Estimados

| Recurso | Custo Mensal |
|---------|--------------|
| Function App (Consumption) | ~$0-20 (por execução) |
| Storage Account (LRS) | ~$2-5 |
| Service Bus (Basic) | ~$5 |
| Log Analytics | ~$2-5 (0.1GB/dia) |
| Application Insights | Incluído |
| **Total Estimado** | **~$10-35/mês** |
