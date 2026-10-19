# Azure FinOps Platform

Plataforma FinOps para Azure: coleta custos reais via Cost Management, identifica oportunidades de economia, detecta anomalias, gera relatórios e organiza ownership de subscriptions por time — com um backend serverless em .NET e um portal web em React.

## Visão geral

- **Backend**: Azure Functions (.NET 8, isolated worker) — coleta de custos, análise de subutilização, detecção de anomalias, geração de relatórios e APIs para o portal.
- **Frontend**: React 19 + TypeScript + Vite (`frontend/finops-portal`) — portal para visualizar custos, recomendações, anomalias e administrar ownership.
- **Armazenamento**: modelo híbrido — Blob Storage para snapshots/relatórios/exports, Cosmos DB para dados operacionais editáveis (status de recomendação, ownership, audit logs).
- **Integrações Azure**: Cost Management (Exports + Query API), Resource Graph, Service Bus, Application Insights, Log Analytics, Entra ID (SSO).

## Estrutura do repositório

```
src/
  Personal.FinOpsApi.AzureFunctions/   # Projeto principal do backend (Functions)
tests/
  Unit/                              # Testes xUnit/Moq/FluentAssertions do backend
  PowerShell/                        # Testes Pester dos scripts (Initialize-CostExports, Terraform)
frontend/
  finops-portal/                     # Portal React (SPA)
scripts/
  Initialize-CostExports.ps1         # Bootstrap dos Cost Management Exports
pipelines/                           # Pipelines Azure DevOps (build/deploy/infra) + Terraform
docs/                                # Documentação detalhada por feature/migração
wiki/                                # Índice adicional de documentação
```

## Como começar

### Pré-requisitos

- .NET SDK 8
- Azure Functions Core Tools v4
- Azurite (ou uma connection string válida em `AzureWebJobsStorage`)
- Node.js 22.x (para o frontend — Vite 6.x; **não atualizar para Vite 8+** sem upgrade do Node)
- Azure CLI (`az login`) para testes que dependem de APIs do Azure

### Backend (Azure Functions)

```bash
dotnet restore src/Personal.FinOpsApi.AzureFunctions/Personal.FinOpsApi.AzureFunctions.csproj
dotnet build src/Personal.FinOpsApi.AzureFunctions/Personal.FinOpsApi.AzureFunctions.csproj
func start --script-root src/Personal.FinOpsApi.AzureFunctions
```

Copie `local.settings.sample.json` para `local.settings.json` dentro de `src/Personal.FinOpsApi.AzureFunctions/` e ajuste os valores (veja [Configuração](#configuração)).

### Frontend (portal)

```bash
cd frontend/finops-portal
npm install
npm run dev
```

Acesse `http://localhost:5173/`. Copie `.env.example` para `.env.development` e ajuste as variáveis do Entra ID e `VITE_API_BASE_URL` (aponte para a Function local, ex.: `http://localhost:7071`). Detalhes de configuração e Redirect URIs em [frontend/finops-portal/README.md](frontend/finops-portal/README.md).

## Configuração

Principais App Settings do backend (local: `local.settings.json`; Azure: Application Settings, via Terraform):

| Variável | Descrição |
|---|---|
| `AzureWebJobsStorage` | Connection string do storage da Function |
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` |
| `ServiceBusConnection` | Connection string do Service Bus |
| `AZURE_SUBSCRIPTION_ID` | Subscription usada por padrão |
| `RESULTS_CONTAINER_NAME` | Container de resultados de análise |
| `ReportGenerationSchedule` | Cron para pré-gerar relatórios HTML |
| `CostByServiceDailySchedule` | Cron da coleta diária de custo por serviço |
| `COST_DATA_DELAY_DAYS` | Atraso (D-N) para aguardar consolidação do Cost Management (default `2`) |
| `COST_EXPORT_CONTAINER` | Container dos arquivos brutos dos Cost Management Exports (default `cost-exports`) |
| `EnableCostExportImport` | Habilita a ingestão agendada dos CSVs de Export |
| `COST_EXPORT_IMPORT_ALL_SUBSCRIPTIONS` | Descobre automaticamente todos os prefixos `subscriptions/<id>/` no container |
| `CostExportImportSchedule` | Cron da importação dos Exports (default a cada 2h) |
| `COST_SUBSCRIPTIONS` | Lista CSV de subscriptions (vazio = discovery automático) |
| `COST_STORAGE_CONTAINER` | Container de análises/custos (default `finops-analysis`) |
| `CONFIG_CONTAINER_NAME` | Container de configuração — ownership e billing overrides (default `finops-config`) |

Opcional (Log Analytics):

| Variável | Descrição |
|---|---|
| `LOG_ANALYTICS_WORKSPACE_ID` | Workspace de destino |
| `LOG_ANALYTICS_SHARED_KEY` | Chave compartilhada |
| `LOG_ANALYTICS_LOG_TYPE` | Nome do log customizado |

Providers do modelo híbrido de storage (primeiro deploy seguro):

```json
{
  "StorageProvider": "Blob",
  "OwnershipProvider": "Blob",
  "RecommendationStatusProvider": "Cosmos",
  "AuditLogProvider": "Cosmos",
  "BillingPeriodOverridesBlobPath": "config/billing-period-overrides.json"
}
```

## Autenticação e autorização

- O portal usa **SSO via Microsoft Entra ID (MSAL)** e envia um token Bearer para a API.
- O **backend** é a autoridade de autorização (o frontend só ajusta a experiência de uso):
  - `FinOps.Reader`: leitura (dashboard, recomendações, anomalias, relatórios, ownership).
  - `FinOps.Admin`: leitura + alterações operacionais (status de recomendação, ownership, execução manual de anomalias, overrides de billing period).
- Regras: não autenticado → `401`; autenticado sem role → `403`.
- Grupos sugeridos no Entra ID: `GRP-FinOps-Readers` (role `FinOps.Reader`) e `GRP-FinOps-Admins` (role `FinOps.Admin`).

## Funcionalidades e APIs

### Custo por serviço e por recurso

- `CostByServiceDailyTimer` / `CostByResourceDailyTimerFunction` (timers): coletam custo real do dia anterior via Cost Management API/Exports.
- `GET /api/cost/by-service?date=YYYY-MM-DD&subscription=all`
- `GET /api/cost/trend-by-service?from=YYYY-MM-DD&to=YYYY-MM-DD&subscription=all&service=<nome>`
- `GET /api/cost/health`
- `CostExportImportTimerFunction`: importa CSVs de Cost Management Exports do Blob a cada 2h (checkpoints evitam reprocessamento); ao importar manifesto novo, dispara análise de anomalias via Service Bus.

Storage: container `finops-analysis`, paths `cost/byService/date=YYYY-MM-DD/subscriptionId=<sub>/{byService,raw}.json`.

### Relatórios versionados

`PreGeneratedReportTimerFunction` gera HTML por timer e salva no Blob:

```text
reports/YYYY-MM-DD/general.html
reports/YYYY-MM-DD/subscriptions/{subscriptionId}.html
reports/YYYY-MM-DD/teams/{teamId}.html
```

`GET /api/report/html` e `GET /api/report/csv` resolvem o mesmo snapshot imutável usado pelo Dashboard e renderizam o formato solicitado (`?date=`, `&subscription=` ou `&team=`). A data resolvida e a revisão são devolvidas nos headers `X-FinOps-*`. O timer mantém cópias HTML pré-geradas para distribuição/arquivo, mas elas não são a fonte canônica das APIs.

### Ownership Admin

Vínculo entre subscription e time, com autorização por role (`FinOps.Reader` lê, `FinOps.Admin` altera):

- `GET /api/ownership/teams`
- `GET /api/ownership/teams/{teamId}`
- `POST /api/ownership/teams/{teamId}/subscriptions`
- `PUT /api/ownership/teams/{teamId}/subscriptions/{subscriptionId}`
- `DELETE /api/ownership/teams/{teamId}/subscriptions/{subscriptionId}`

Persistência: Blob, container `CONFIG_CONTAINER_NAME`, blob `config/ownership.json`. Sincronização automática por Management Group está documentada em [docs/OWNERSHIP_MANAGEMENT_GROUP_SYNC.md](docs/OWNERSHIP_MANAGEMENT_GROUP_SYNC.md).

### Anomalias de custo e override de billing period

- `GET /api/cost-anomalies`
- `POST /api/cost-anomalies/run` (somente `FinOps.Admin`)
- `GET /api/billing/periods/overrides` (somente `FinOps.Admin`)
- `PUT /api/billing/periods/overrides/{subscriptionId}` — body `{ "billingCycleStartDay": 1..28 }` (somente `FinOps.Admin`)
- `DELETE /api/billing/periods/overrides/{subscriptionId}` (somente `FinOps.Admin`)

Persistência do override: Blob, container `CONFIG_CONTAINER_NAME`, blob `config/billing-period-overrides.json`.

### Pipeline de timers em produção (horários UTC)

> Todos os timers rodam após 9h UTC (6h BRT), quando a automação de start/stop garante que os recursos estão ativos.

| Horário UTC | Horário BRT | Timer | Descrição |
|-------------|-------------|-------|-----------|
| 09:10 | 06:10 | `CostByServiceDailyTimer` | Coleta custo por serviço |
| 09:20 | 06:20 | `CostByResourceDailyTimer` | Coleta custo por recurso |
| 09:30 | 06:30 | `CostAnalysisTimer` | Análise completa (analyzers de subutilização) |
| 11:00 | 08:00 | `CostAnomalyDailyTimer` | Detecção de anomalias |
| 11:30 | 08:30 | `DailySummary` | Resumo diário |
| 11:45 | 08:45 | `DatabaseCostRollupTimer` | Consolida o custo diário de SQL/Cosmos para o portal |
| 12:00 | 09:00 | `PreGeneratedReportTimer` | Pré-gera relatórios HTML (com anomalias) |

### Bootstrap dos Cost Management Exports

Um administrador com `Cost Management Contributor` nas subscriptions e permissão para atribuir roles no Storage central executa:

```powershell
.\scripts\Initialize-CostExports.ps1 `
  -StorageSubscriptionId "<finops-subscription-id>" `
  -StorageResourceGroup "<finops-resource-group>" `
  -StorageAccountName "<finops-storage-account>" `
  -SubscriptionIds "<subscription-id-1>","<subscription-id-2>" `
  -WhatIf
```

Remova `-WhatIf` para criar os exports inicialmente inativos; depois de validar, reexecute com `-Activate -Force`. Para todas as subscriptions, use `-AllSubscriptions -ManagementGroupId "<management-group-id>"` explicitamente. Exports existentes só são alterados com `-Force`. O importador (`EnableCostExportImport`) permanece desabilitado por padrão até ser aprovado no pipeline. Detalhes da migração em [docs/COST_EXPORT_MIGRATION.md](docs/COST_EXPORT_MIGRATION.md).

## Testes

```powershell
# Backend
dotnet build src\Personal.FinOpsApi.AzureFunctions\Personal.FinOpsApi.AzureFunctions.csproj
dotnet test tests\Unit\Personal.FinOpsApi.AzureFunctions.UnitTests.csproj

# Scripts (Pester)
Invoke-Pester tests\PowerShell

# Frontend
Set-Location frontend\finops-portal; npm run lint; npm run build
```

Para um teste focado no backend: `dotnet test ... --filter "FullyQualifiedName~NomeDoTeste"`.

Guia rápido de testes manuais contra Azure real em [docs/AZURE_TESTING.md](docs/AZURE_TESTING.md) e [docs/TESTING_REPORTS.md](docs/TESTING_REPORTS.md).

## CI/CD e deploy

Pipelines Azure DevOps em `pipelines/`:

| Pipeline | Gatilho | O que faz |
|---|---|---|
| `build-function.yml` | push em `src/**` | Restore/build/publish do backend, publica artefato |
| `deploy-function.yml` | artefato do build acima | Deploy da Function App (`functionAppLinux`) |
| `build-frontend.yml` | push em `frontend/**` | `npm ci` + `npm run build`, publica `dist/` |
| `deploy-frontend.yml` | artefato do build acima | Upload para Storage Static Website (`$web`) + purge do Azure Front Door |
| `infra.yml` / `infra-frontend.yml` | mudanças em `pipelines/terraform-*/**` | Terraform da infraestrutura (backend/frontend) |

Infraestrutura como código em `pipelines/terraform-function/` e `pipelines/terraform-frontend/`. **Não** execute Terraform contra o state de produção localmente — use sempre o pipeline. URL de produção do portal: `https://finops.example.com`.

### Ambientes e nomes dos recursos

A branch define o ambiente (`main` → prod; `homolog` e demais → hml). Detalhes em [AMBIENTE_HML.md](docs/AMBIENTE_HML.md).

| Ambiente | Recursos Azure | Front Door (`frontdoor2-dev-afd`) | Portal |
|---|---|---|---|
| prod | `finopsplatform-nap-*` (nome legado: **`nap` = prod**) | endpoint `finopsplatform-dev-fde` (nome legado: **`dev` = prod**) | `https://finops.example.com` |
| hml | `finopsplatform-hml-*` | endpoint `finopsplatform-hml-fde` | `https://finops-hml.example.com` |

Os nomes de prod vêm de quando havia um ambiente só e não foram trocados: recursos Azure não podem ser renomeados, só recriados. Todos os recursos têm a tag `Ambiente` (`prod` ou `hml`), que é a referência confiável no portal do Azure.

## Documentação adicional

Aprofundamentos por feature/migração ficam em `docs/`:

- [Roadmap do produto e migração assistida entre subscriptions (DEV/HML)](docs/ROADMAP.md)

- [PROJECT_STUDY_GUIDE.md](docs/PROJECT_STUDY_GUIDE.md) — guia de estudo geral do projeto
- [FRONTEND_PORTAL.md](docs/FRONTEND_PORTAL.md) — arquitetura detalhada do portal
- [COST_EXPORT_MIGRATION.md](docs/COST_EXPORT_MIGRATION.md) — migração para Cost Management Exports
- [Hybrid-Blob-Cosmos-Migration.md](docs/Hybrid-Blob-Cosmos-Migration.md) — modelo híbrido de storage
- [FINANCIAL_DATA_RELIABILITY.md](docs/FINANCIAL_DATA_RELIABILITY.md) — confiabilidade dos dados financeiros
- [OWNERSHIP_MANAGEMENT_GROUP_SYNC.md](docs/OWNERSHIP_MANAGEMENT_GROUP_SYNC.md) — sincronização de ownership por Management Group
- [ORGANIZATIONAL_HIERARCHY_AND_PDF.md](docs/ORGANIZATIONAL_HIERARCHY_AND_PDF.md) — hierarquia organizacional e geração de PDF
- [LogAnalytics-Integration.md](docs/LogAnalytics-Integration.md) e [LOG_ANALYTICS_CONFIG.md](docs/LOG_ANALYTICS_CONFIG.md) — integração e configuração do Log Analytics
- [AZURE_TESTING.md](docs/AZURE_TESTING.md) / [TESTING_REPORTS.md](docs/TESTING_REPORTS.md) — testes manuais contra Azure

Regras e convenções para contribuição estão em [AGENTS.md](AGENTS.md).
