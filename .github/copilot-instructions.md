# Azure FinOps Platform Guidelines

## Architecture

This is a **production-grade Azure FinOps platform** built on Azure Functions (.NET 8 isolated) with comprehensive cost analysis and optimization capabilities.

### Core Components
- **Azure Functions**: 20+ HTTP APIs, 5 Timer Functions, 8 Queue Functions  
- **Cost Analyzers**: 8 specialized analyzers (Idle VMs, Orphan Disks, Storage, App Services, Function Apps, Log Analytics, Public IPs, Duplicates)
- **Cost Anomaly Detection**: Daily cost monitoring with budget tracking and anomaly alerts
- **Frontend Portal**: React 19 SPA (TypeScript + Vite) hosted on Azure Storage Static Website + Front Door
- **Integration Layer**: Azure Cost Management API, Service Bus, Blob Storage, Log Analytics
- **Infrastructure**: Terraform-managed (Consumption Plan, Service Bus, Storage, App Insights)
- **CI/CD**: Azure DevOps with automated build/deploy pipelines

### Key Business Logic
- **Daily Pipeline**: Automated cost analysis (3:00-4:00 AM UTC) → findings → Log Analytics
- **Cost Anomaly Detection**: Daily anomaly check (8:00 AM UTC) → compares vs baseline & budget
- **Essential APIs**: 23 core endpoints
- **Queue Processing**: Scalable async processing for large subscriptions (avoids timeouts)
- **Resilience**: Circuit breaker, retry logic, throttling protection

## Essential APIs (Post-Optimization)

### Production Core (20 APIs)
**Health & Analysis:**
- `/api/health` - Verifica status da Function App e dependências
- `/api/analyze-costs` - Dispara análise de custos (POST com payload de configuração)
- `/api/collect-costs` - Força coleta de dados de custos do Cost Management API
- `/api/force-consolidate` - Força consolidação de resultados de análise pendentes

**Grafana Integration:**
- `/api/grafana/health` - Health check específico para datasource Grafana
- `/api/GrafanaCostByService` - Custos agrupados por tipo de serviço Azure (para painéis)
- `/api/GrafanaCostTrendByService` - Tendência de custos ao longo do tempo por serviço
- `/api/GrafanaCostByResource` - Custos por recurso individual (granularidade máxima)
- `/api/GrafanaCostTrendByResource` - Tendência de custos ao longo do tempo por recurso

**Manual Analysis:**
- `/api/CostByServiceManualRun` - Executa análise de custos agrupada por tipo de serviço Azure
- `/api/CostByResourceManualRun` - Executa análise de custos por recurso individual (híbrido queue/direto)
- `/api/ManualDailySummary` - Gera resumo diário consolidado das análises
- `/api/ManualCostAnalysis` - Executa análise completa de custos com todos os analyzers

**Reporting & Data:**
- `/api/report/html` - Relatório HTML com recomendações de economia (suporta `?team=` filter)
- `/api/report/csv` - Relatório CSV exportável (suporta `?team=` filter)
- `/api/finops/data` - Acesso programático aos resultados de análise (Function auth)
- `/api/AnalyzeDuplicateResources` - Analisa recursos duplicados no ambiente

**Cost Anomaly Detection:**
- `/api/cost-anomalies` (GET) - Consulta relatório de anomalias (`?date=2026-05-14`)
- `/api/cost-anomalies/run` (POST) - Execução manual da análise de anomalias

**Debug:**
- `/api/debug/loganalytics` (GET) - Lista workspaces Log Analytics (`?subscriptionId=xxx`)

**Team Management:**
- `/api/teams` (GET) - Lista todos os times cadastrados
- `/api/teams` (POST) - Cria ou atualiza um time
- `/api/teams/{teamId}` (GET) - Obtém detalhes de um time
- `/api/teams/{teamId}` (DELETE) - Remove um time
- `/api/teams/subscriptions` - Lista mapeamento subscription → team

### Removed APIs (11 total)
- Demo APIs: `DemoOrganizationalStructure`, `DemoPdfStructure`
- Test APIs: `TestManagementGroupMapping`, `TestRealManagementGroupMapping`
- Redundant Grafana: `GrafanaSavingsByType`, `GrafanaSavingsBySubscription`, `GrafanaResourceDetails`
- Management Groups: `ListManagementGroups`, `ListRealManagementGroups` (não mais necessários)
- Team Ownership: `TeamOwnershipDiscover`, `GetTeamOwnership` (substituídos por `/api/teams`)
- Hierarchy: `report/hierarchy` (não implementado)

## Code Style

### Project Structure
```
src/Personal.FinOpsApi.AzureFunctions/     # Main project - always use this path
├─ Functions/           # Azure Function entry points (HTTP/Timer/Queue)  
├─ Services/           # Business logic and Azure API integration (23+ services)
├─ Analyzers/          # Cost optimization analyzers (6 types)
├─ Models/             # DTOs and data structures
├─ Application/        # Orchestration layer (CostAnalysisOrchestrator)
└─ Program.cs          # Dependency injection and startup configuration

frontend/finops-portal/src/              # Frontend SPA
├─ App.tsx              # Rotas (react-router-dom)
├─ index.css            # Dark theme global
├─ components/          # Layout, Card, StatusBadge
├─ hooks/useFetch.ts    # Hook genérico de fetch com loading/error
├─ pages/               # Dashboard, Reports, Recommendations, Anomalies, Ownership
├─ services/api.ts      # Cliente API centralizado
└─ types/api.ts         # Interfaces TypeScript para respostas da API
```

### Naming Conventions
- **Functions**: `[Purpose][TriggerType]Function.cs` (e.g., `CostAnalysisTimerFunction`)  
- **Services**: `[Domain]Service.cs` or `[Azure]Client.cs` (e.g., `CostManagementClient`)
- **Models**: Business-focused names (e.g., `CostAnalysisRequest`, `FinOpsAnalysisResult`)
- **Azure Resources**: Terraform locals pattern: `{aplicacao}-{setor}-{resource}` (e.g., `finopsplatform-nap-func`)

### Authentication Pattern  
Always use **Managed Identity** with `DefaultAzureCredential`:
```csharp
// Register in Program.cs
builder.Services.AddSingleton(new ArmClient(new DefaultAzureCredential()));
```
Never hardcode connection strings - use App Settings.

## Build and Test

### Local Development
```bash
# Prerequisites: .NET 8 SDK, Azure Functions Core Tools v4, Azurite
cd src/Personal.FinOpsApi.AzureFunctions
dotnet restore
dotnet build
func start --script-root .
```

### Required Configuration (`local.settings.json`)
```json
{
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "ServiceBusConnection": "[Service Bus connection string]",
    "AZURE_SUBSCRIPTION_ID": "[target subscription]",
    "AZURE_TENANT_ID": "[tenant ID]",
    "AZURE_CLIENT_ID": "[managed identity client ID]"
  }
}
```

### Build Commands
```bash
# Build only
dotnet build src/Personal.FinOpsApi.AzureFunctions/Personal.FinOpsApi.AzureFunctions.csproj

# Publish for deployment  
dotnet publish src/Personal.FinOpsApi.AzureFunctions/Personal.FinOpsApi.AzureFunctions.csproj -c Release -o ./publish

# Run tests
dotnet test tests/Unit/Personal.FinOpsApi.AzureFunctions.UnitTests.csproj
```

### Infrastructure Deployment
```bash
# From pipelines/terraform-function/
terraform init
terraform plan -var="aplicacao=finopsplatform" -var="setor=nap"  
terraform apply
```

## Conventions

### Function Patterns  
- **HTTP Functions**: Use `[Function("FunctionName")]` attribute, support both GET/POST
- **Timer Functions**: Schedule via environment variables (e.g., `%CostAnalysisSchedule%`)  
- **Queue Functions**: Use Service Bus triggers, implement idempotency via analysisId pattern
- **Authorization**: Most APIs use `AuthorizationLevel.Anonymous` (for Grafana), sensitive APIs use `Function`

### Service Bus Queue Architecture
Critical pattern for scalability - breaks large operations into steps:
```csharp
// Starter Function → Create analysisId → Queue messages
// Worker Functions → Process individual steps → Store results
var analysisId = $"{subscriptionId}-{date:yyyy-MM-dd}";
```

### Error Handling & Resilience
Always implement:
- **Circuit Breaker**: Use `CircuitBreakerService` for Azure API calls
- **Retry Logic**: `HttpRetryService` with exponential backoff  
- **Throttling Protection**: `AzureApiThrottleService` prevents 429 errors
- **Fallback Data**: In-memory defaults when APIs unavailable

### Storage Patterns
- **Container**: `finops-analysis` (configurable via `RESULTS_CONTAINER_NAME`)
- **Path Structure**: `cost/byService/date=YYYY-MM-DD/subscriptionId={id}/byService.json`
- **Blob Naming**: Use ISO date format, include subscription context

### Environment Variables Pattern
Configuration follows hierarchy:
1. **Schedules**: `%[FunctionName]Schedule%` (e.g., `%CostAnalysisSchedule%`)
2. **Azure Resources**: `AZURE_[SERVICE]_[PROPERTY]` 
3. **Feature Flags**: `ENABLE_[FEATURE]` (boolean)
4. **Lists**: CSV format (e.g., `COST_SUBSCRIPTIONS=sub1,sub2,sub3`)

### Cost Analysis Integration
- **Cost Management API**: Always use v2023-03-01, implement pagination
- **Azure Monitor Metrics**: 30-day retention, aggregate before querying
- **Management Groups**: Map subscriptions via `AzureManagementGroupService`  
- **Cost Lookup**: Use `ResourceCostLookupService` for real Azure costs with 30-min cache
- **Cost Fields**: All findings include both `DailyCost` (real average) and `EstimatedMonthlyCost` (daily × 30)
- **HTML Report**: Shows "Custo Diário" and "~Custo Mensal" (~ indicates projection)

### API Response Patterns
Standardize on:
```csharp  
// Success with data
return new OkObjectResult(data);

// Error with details
return new BadRequestObjectResult($"Error: {details}");

// Health checks  
return new OkObjectResult(new { status = "healthy", timestamp = DateTimeOffset.UtcNow });
```

### Log Analytics Integration
When implementing LA ingestion:
- Use Data Collection Rules (DCR) - see `docs/LogAnalytics-Integration.md`
- Custom table: `FinOpsRecommendations_CL`
- HMAC authentication required for Data Collector API

## Critical Gotchas

### Azure Function Timeouts
- **Problem**: Large subscriptions timeout at 8-10 minutes  
- **Solution**: Use Step Functions pattern with Service Bus queues
- **Implementation**: Break analysis into 2-5 minute steps

### API Throttling  
Azure Cost Management API is heavily throttled:
- Implement circuit breaker before making calls
- Use queue-based processing for multiple subscriptions  
- Cache results in Blob Storage, check before re-querying

### Managed Identity RBAC
Post-deployment, manually assign:
```bash
az role assignment create --assignee <identity-principal-id> \
  --role "Cost Management Reader" \
  --scope "/providers/Microsoft.Management/managementGroups/{mgId}"
```

### Service Bus Message Handling
- Set `MaxConcurrentCalls = 1` to prevent parallel processing of same subscription
- Use `AutoCompleteMessages = false` for better error recovery
- Implement message TTL (7-14 days) to prevent poison messages

### Terraform State Management
- Backend uses Azure Storage: `terraform-state-finops-{setor}`
- Never run Terraform locally against production state
- Use Azure DevOps pipelines with proper service connections

### Container Names & Paths  
- Function runtime requires: `azure-webjobs-*` containers in storage
- Analysis results: Custom container (default: `finops-analysis`)  
- Team config: `finops-config` container (configurable via `CONFIG_CONTAINER_NAME`)
- **Never hardcode paths** - use environment variables for all container names

### JSON Serialization (WriteAsJsonAsync)
.NET 8 isolated Functions usam `System.Text.Json` com `PropertyNamingPolicy = null` (padrão).
Anonymous types preservam o casing original das propriedades C#.

```csharp
// ❌ PascalCase no JSON — frontend recebe undefined → crash
new { r.ResourceId, r.Description }

// ✅ camelCase explícito — frontend funciona
new { resourceId = r.ResourceId, description = r.Description }
```

**Sempre usar nomes explícitos em camelCase** ao criar anonymous types para respostas JSON.

### Frontend (Vite + React)
- Node.js 22.x requer Vite 6.x (Vite 8+ é incompatível)
- Deploy automático via push em `main` com alterações em `frontend/**`
- Frontend SPA sem error boundary — qualquer crash no render = tela preta
- Usar `.catch()` em endpoints que retornam 404 (ex: `/api/cost-anomalies`)
- Propriedades de objetos da API devem ter null-check: `(r.prop ?? '').toLowerCase()`

## Team-Based Reports

### Overview
Sistema simplificado para filtrar relatórios por time, baseado em mapeamento **team → subscriptions**.

Filosofia: Ownership está nas subscriptions, não nos recursos individuais.

### Storage Structure
```
Container: finops-config (CONFIG_CONTAINER_NAME)
└── config/team-subscriptions.json
```

### JSON Structure
```json
{
  "teams": [
    {
      "id": "plataforma",
      "name": "Plataforma",
      "email": "plataforma@empresa.com",
      "subscriptionIds": [
        "a0f90da6-21a5-47d1-bbf5-d1978e812a8c",
        "bfdadc61-7044-4556-a633-122d5cf2a947"
      ],
      "subscriptionNames": ["MPN - DEV", "MPN - HML"]
    }
  ],
  "lastUpdated": "2026-05-07T14:00:00Z"
}
```

### Team Management APIs
```http
GET  /api/teams                    # Lista todos os times
GET  /api/teams/{teamId}           # Obtém um time
POST /api/teams                    # Cria/atualiza um time
DELETE /api/teams/{teamId}         # Remove um time
GET  /api/teams/subscriptions      # Lista mapeamento subscription → team
```

### Report with Team Filter
```http
# Relatório geral (sem filtro)
GET /api/report/html

# Relatório filtrado por subscription
GET /api/report/html?subscriptionId=xxx

# Relatório filtrado por time
GET /api/report/html?team=plataforma
```

### Creating a Team (Example)
```bash
curl -X POST http://localhost:7071/api/teams \
  -H "Content-Type: application/json" \
  -d '{
    "id": "plataforma",
    "name": "Plataforma",
    "email": "plataforma@empresa.com",
    "subscriptionIds": [
      "a0f90da6-21a5-47d1-bbf5-d1978e812a8c"
    ]
  }'
```

### Key Services
| Service | Purpose |
|---------|---------|
| `TeamSubscriptionsService` | CRUD de times e mapeamento team → subscriptions |
| `TeamManagementFunction` | APIs HTTP para gerenciar times |
| `RecommendationReportService` | `GenerateReportByTeamAsync()` filtra por subscriptions do time |
| `ResourceCostLookupService` | Lookup de custos reais por recurso via Cost Management (cache 30min) |

### How Team Filter Works
```csharp
// 1. Obtém subscriptions do time
var teamSubscriptionIds = await _teamSubscriptionsService.GetTeamSubscriptionIdsAsync(teamFilter);

// 2. Filtra recommendations que pertencem às subscriptions
filteredRecommendations = allRecommendations
    .Where(r => teamSubscriptionIds.Contains(r.SubscriptionId))
    .ToList();

// 3. Gera relatório com dados filtrados
return GenerateReport(filteredRecommendations);
```

### Adding Environment Variable
```json
{
  "Values": {
    "CONFIG_CONTAINER_NAME": "finops-config"
  }
}
```