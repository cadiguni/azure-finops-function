# Migração parcial para modelo híbrido (Blob + Cosmos DB)

## 1) Estado atual identificado

### Onde Blob Storage é usado hoje
- **Recomendações de análise**: `AnalysisStorageService` grava/lê em `analyses/year=YYYY/month=MM/day=DD/{subscriptionId}/recommendations.json` e opcional `raw-analysis/.../raw-findings.json`.
- **Relatórios HTML pré-gerados**: `PreGeneratedReportStorageService` em `reports/yyyy-MM-dd/general.html`, `reports/yyyy-MM-dd/subscriptions/{subscriptionId}.html`, `reports/yyyy-MM-dd/teams/{teamId}.html`.
- **Custos por serviço/recurso**: `CostStorageRepository` em `cost/byService/...` e `cost/byResource/...` com `raw.json`.
- **Anomalias**: `CostAnomalyStorageService` em `cost-anomalies/{yyyy}/{MM}/{dd}/subscription-anomalies.json`.
- **Ownership/Times atuais**:
  - `OwnershipStorageService`: `finops-config/config/ownership.json`.
  - `TeamSubscriptionsService`: `finops-config/config/team-subscriptions.json`.
- **Billing period override manual**:
  - `SubscriptionBillingCycleService`: `finops-config/config/billing-period-overrides.json` (configurável por `BillingPeriodOverridesBlobPath`).

### Onde ownership é lido/salvo
- `OwnershipManagementFunction` usa `OwnershipStorageService` (Blob) para CRUD de `/api/ownership/teams/...`.
- `TeamManagementFunction` usa `TeamSubscriptionsService` (Blob) para `/api/teams...`.

### Onde recomendações são geradas
- Pipeline principal em `CostAnalysisOrchestrator` + `AnalysisStorageService` (snapshots em Blob).
- Endpoint para portal: `FrontendApiFunction.GetRecommendations` (`GET /api/recommendations`), agora enriquecível com status operacional.

### Onde relatórios HTML são gerados
- `PreGeneratedReportTimerFunction` gera HTML diário usando `RecommendationReportService` + `HtmlReportBuilder`, persistindo no Blob via `PreGeneratedReportStorageService`.
- `ReportFunction` mantém `GET /api/report/html` lendo o HTML pré-gerado do Blob.

### Endpoints atuais relevantes
- Recomendações: `GET /api/recommendations`.
- Ownership atual (times/subscriptions): `GET/POST/PUT/DELETE /api/ownership/teams...`.
- Times: `GET /api/teams` para filtros de recomendações e relatórios. As APIs de consulta individual, escrita e mapeamento de subscriptions deste grupo foram retiradas.
- Relatórios: `GET /api/report/html`, `GET /api/report/csv`.

## 2) Arquitetura híbrida proposta (iniciada)

- **Blob continua fonte de snapshots e relatórios** (sem substituição):
  - `reports/...`
  - `analyses/.../recommendations.json` (+ raw)
  - `cost-anomalies/...`
  - `cost/byService`, `cost/byResource`, `cost/allocation` e APIs de custo do portal.
- **Cosmos DB passa a armazenar dados operacionais/editáveis**:
  - Ownership operacional (`ownership`)
  - Status de recomendação (`recommendationStatus`)
  - Auditoria (`auditLogs`)
  - Futuro: `cleanupRequests`.
- **Blob (config)** permanece para overrides manuais simples de billing period (baixo volume e baixa concorrência).

## 3) Terraform Cosmos DB (baixo custo inicial)

Arquivos adicionados/alterados em `pipelines/terraform-function/`:
- **Novo**: `azurerm_cosmosdb.tf`
  - `azurerm_cosmosdb_account` com `EnableServerless`.
  - 1 região, `zone_redundant = false`, sem multi-write.
  - `azurerm_cosmosdb_sql_database` (`finops` por default).
  - Containers:
    - `ownership` (PK `/teamId`)
    - `recommendationStatus` (PK `/resourceId`)
    - `auditLogs` (PK `/entityId`)
    - `cleanupRequests` opcional por variável (PK `/teamId`).
  - `azurerm_cosmosdb_sql_role_assignment` para Managed Identity da Function (Data Contributor).
- **Alterados**:
  - `inputs.tf`: `cosmos_account_name`, `cosmos_database_name`, `enable_cleanup_requests_container`.
  - `locals.tf`: naming padrão do account.
  - `azurerm_function_app.tf`: app settings de provider híbrido e Cosmos.
  - `outputs.tf`: `cosmos_account_name`, `cosmos_endpoint`, `cosmos_database_name`, `cosmos_container_names`.

## 4) Modelagem e partition keys

- `ownership` (**PK `/teamId`**): consultas e edição no portal tendem a ser por time; mantém agregação de ownership por domínio de gestão.
- `recommendationStatus` (**PK `/resourceId`**): chave de status determinística por `resourceId + recommendationType`; favorece lookup por recomendação/recurso.
- `auditLogs` (**PK `/entityId`**): histórico natural por entidade editada (ownership/status/cleanup request).
- `cleanupRequests` (futuro, **PK `/teamId`**): fluxo de aprovação e operação normalmente particionado por time.

## 5) Base de código preparada para modelo híbrido

### Novas abstrações
- `IOwnershipRepository`
- `IRecommendationStatusRepository`
- `IAuditLogRepository`

### Implementações Blob (fallback/compatibilidade)
- `BlobOwnershipRepository`
- `BlobRecommendationStatusRepository`
- `BlobAuditLogRepository`

### Implementações Cosmos
- `CosmosOwnershipRepository`
- `CosmosRecommendationStatusRepository`
- `CosmosAuditLogRepository`

### Integração em DI/configuração
- `Program.cs` agora resolve provider por app settings:
  - `StorageProvider`
  - `OwnershipProvider`
  - `RecommendationStatusProvider`
  - `AuditLogProvider`
- Cosmos com Managed Identity (`CosmosUseManagedIdentity=true`) e fallback para Blob se endpoint não estiver configurado.

### Endpoints novos/ajustados
- `GET /api/recommendations`: lê snapshot Blob e cruza com status operacional do repositório.
- `PATCH /api/recommendations/{id}/status`: exige `FinOps.Admin`, persiste status e grava audit log.
- `GET /api/ownership/teams` e os endpoints por time/subscription: camada operacional autenticada via repositório. As APIs genéricas legadas `/api/ownership` foram removidas.
- `GET /api/cost-anomalies`: consulta relatório de anomalias.
- `POST /api/cost-anomalies/run`: execução manual (somente Admin).
- `GET/PUT/DELETE /api/billing/periods/overrides...`: gestão de override manual de billing period (somente Admin).

### Autorização (backend como fonte da verdade)
- `FinOps.Reader` e `FinOps.Admin` podem consultar dados (`GET`).
- Somente `FinOps.Admin` altera estado operacional (`PATCH/PUT/POST` administrativos).
- Usuário sem autenticação recebe `401`; autenticado sem role recebe `403`.
- Frontend oculta ações para Reader, mas a proteção efetiva está no backend.

### Billing period override e estratégia de armazenamento

- Armazenamento atual: Blob config (`config/billing-period-overrides.json`).
- Motivo: operação simples, baixa cardinalidade e baixo volume de escrita.
- Prioridade de resolução do billing period:
  1. Azure BillingPeriods API (oficial)
  2. Override manual persistido no blob
  3. Override legado via app settings (`CostAnomalySubscriptionTurnDayOverrides`)
  4. Fallback mês calendário

### Regra de expiração de Snooze
- `Snoozed` fica ativo até `snoozeUntil`.
- Após expirar (`snoozeUntil <= agora UTC`), a recomendação volta a aparecer como `New` (elegível novamente).
- A expiração é calculada **na leitura/enriquecimento** (`GET /api/recommendations`), sem alterar persistência.
- `GET` não cria, não atualiza e não remove documentos no Cosmos, e não gera audit log.
- Alteração persistente de status acontece somente via `PATCH /api/recommendations/{id}/status`.

## 6) Variáveis de configuração necessárias

Backend/Function:
- `StorageProvider` (`Blob` recomendado inicialmente)
- `OwnershipProvider` (`Blob` fase inicial; `Cosmos` na fase de ownership operacional)
- `RecommendationStatusProvider` (`Cosmos`)
- `AuditLogProvider` (`Cosmos`)
- `CosmosUseManagedIdentity` (`true`)
- `CosmosEndpoint`
- `CosmosDatabaseName`
- `CosmosOwnershipContainerName`
- `CosmosRecommendationStatusContainerName`
- `CosmosAuditLogsContainerName`
- Fallback Blob:
  - `OwnershipDocumentsBlobPath`
  - `RecommendationStatusBlobPath`
  - `AuditLogsBlobPath`

## 7) Plano de migração por fases

1. **Fase 1 (base)**: Terraform Cosmos + containers + repositórios + seleção por provider.
2. **Fase 2 (status)**: `recommendationStatus` no Cosmos; `GET /api/recommendations` enriquecido; `PATCH status` para Admin.
3. **Fase 3 (ownership operacional)**: edição de ownership pelo repositório Cosmos; manter legados Blob enquanto necessário.
4. **Fase 4 (relatórios enriquecidos)**: pipeline diário passa a gerar enriched snapshot/HTML com status.
5. **Fase 5 (cleanup workflow)**: `cleanupRequests`, approvals e execução controlada.

## 8) Testes sugeridos

### Automatizados (incluídos/incrementais)
- `RecommendationStatusKeyBuilderTests`: chave determinística e case-insensitive.
- Executar suíte unitária existente do backend.

### Manuais
1. `GET /api/recommendations` com usuário Reader/Admin retorna 200 e dados enriquecidos (`status`).
2. `PATCH /api/recommendations/{id}/status`:
   - Admin: 200 e status atualizado.
   - Reader: 403.
   - Não autenticado: 401.
3. Verificar registro de auditoria após PATCH.
4. `GET /api/report/html` segue funcional lendo Blob pré-gerado.
5. Timer/report pipeline continua gerando HTML em `reports/...` sem regressão.
6. Frontend recomendações:
   - Reader visualiza status.
   - Admin altera status (incluindo Snooze 30 dias) e vê atualização.

### Melhorias de UX já aplicadas no portal
- **Recomendações**:
  - visão operacional com abas por grupo de status;
  - filtros por status/severidade/subscription/tipo/time + busca por nome/id;
  - ordenação por coluna, seleção de colunas e redimensionamento;
  - feedback visual pós-PATCH sem recarregar página inteira.
- **Anomalias**:
  - ordenação habilitada para Time, Custo no Ciclo, Média Ciclo/Dia e Vira em;
  - seleção de colunas e redimensionamento preservados.
