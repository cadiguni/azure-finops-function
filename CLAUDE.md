# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Canonical rules also live in `AGENTS.md` — do not duplicate them; this file adds architecture context and command reference. Area-specific rules in `.github/instructions/*.instructions.md` apply only when editing matching paths (see below).

## Project

Azure FinOps platform: collects real Azure costs (Cost Management), detects underutilized resources and cost anomalies, generates reports, and manages subscription-to-team ownership. Backend is a .NET 8 Azure Functions (isolated worker) app; frontend is a React SPA.

- Backend: `src/Personal.FinOpsApi.AzureFunctions/`
- Backend tests: `tests/Unit/` (xUnit/Moq/FluentAssertions), `tests/PowerShell/` (Pester)
- Frontend: `frontend/finops-portal/` (React 19, TypeScript 5.7, Vite 6)
- IaC: `pipelines/terraform-function/`, `pipelines/terraform-frontend/`
- CI/CD: Azure DevOps pipelines in `pipelines/*.yml`
- Bootstrap script: `scripts/Initialize-CostExports.ps1`

## Commands

### Backend

```bash
dotnet restore src/Personal.FinOpsApi.AzureFunctions/Personal.FinOpsApi.AzureFunctions.csproj
dotnet build src/Personal.FinOpsApi.AzureFunctions/Personal.FinOpsApi.AzureFunctions.csproj
func start --script-root src/Personal.FinOpsApi.AzureFunctions   # requires local.settings.json + Azurite
dotnet test tests/Unit/Personal.FinOpsApi.AzureFunctions.UnitTests.csproj
dotnet test tests/Unit/Personal.FinOpsApi.AzureFunctions.UnitTests.csproj --filter "FullyQualifiedName~NomeDoTeste"  # single test
```

### Frontend (`frontend/finops-portal/`)

```bash
npm install
npm run dev       # http://localhost:5173
npm run build     # tsc -b && vite build
npm run lint
```

### PowerShell scripts

```powershell
Invoke-Pester tests/PowerShell
```

Only run the checks relevant to the files you changed — see `AGENTS.md`.

## Architecture

### Dependency injection (`Program.cs`)

All services are registered in one large `ConfigureServices` block in `src/Personal.FinOpsApi.AzureFunctions/Program.cs`, grouped by concern (Analyzers, Orchestration, Report services, Team management, Cost Anomaly, Azure SDK clients, Storage, Cosmos DB, Cost Management, Service Bus). When adding a new service, register it in the matching group rather than creating a new one. Each group is wrapped in its own `try/catch` that only logs to `Console.WriteLine` — a failed registration does not stop startup, so a missing service surfaces later as a DI resolution error at call time, not at boot.

### Hybrid storage: Blob vs. Cosmos, chosen by config at request time

This is the central architectural pattern and spans most features (ownership, recommendation status, audit logs, cost anomaly reports, dashboard summaries). For each of these domains there is:

- An interface in `Services/Repositories/` (e.g. `IOwnershipRepository`).
- A Blob implementation in `Services/Repositories/Blob/`.
- A Cosmos implementation in `Services/Repositories/Cosmos/`.
- A DI factory in `Program.cs` that reads an App Setting (`OwnershipProvider`, `RecommendationStatusProvider`, `AuditLogProvider`, `CostAnomalyProvider`, `DashboardSummaryProvider` — default `"Blob"` or `"Cosmos"` depending on the domain) and returns the matching implementation, falling back to Blob (or a `NoOpDashboardSummaryRepository` for dashboard summaries) when Cosmos isn't configured.

When touching one of these domains, check both implementations and the provider default in `Program.cs` — changing behavior in only one backing store will silently diverge between environments depending on which provider is configured. Blob remains the source of truth for immutable snapshots and reports (`analyses/*`, `reports/*`, `cost-anomalies/*`, `cost/*`); Cosmos holds operational/editable data (recommendation status, audit logs, ownership in its Cosmos phase). See `docs/Hybrid-Blob-Cosmos-Migration.md`.

### Cost data pipeline

Cost Management data flows: **Cost Management Exports → Blob (`cost-exports` container, CSV manifests) → `CostExportImportTimerFunction` imports on a schedule → `ExportFirstCostDataReader`/`CostStorageRepository` (container `finops-analysis`) → HTTP APIs and downstream analyzers.** `ICostDataReader`'s default implementation prefers Export-sourced data over the legacy Cost Management Query API (`CostManagementClient`) — see `Services/CostCollectionRouting.cs` and `docs/COST_EXPORT_MIGRATION.md` for what still reads from which source. `COST_DATA_DELAY_DAYS` (default 2) exists because Cost Management data isn't fully consolidated until D-2.

Checkpointing for imports lives in `ICostExportVersionStore`/`BlobCostExportVersionStore` — reprocessed manifests are skipped. A new manifest import triggers anomaly analysis via Service Bus (`ICostAnomalyQueueService`) rather than inline processing.

### Analysis pipeline

`CostAnalysisOrchestrator` (in `Application/`) coordinates the per-resource-type `Analyzers/` (VM idle, unattached disk, unused public IP, storage account, App Service, Function App, SQL DB, Cosmos DB, Log Analytics), each implementing the contract validated by `AnalyzerContractValidator`/`StandardAnalyzerContract`. Results are combined by `FinOpsResultAggregator`. Large multi-subscription runs go through Service Bus queue functions (`SubscriptionAnalysis*QueueFunction`/`SubscriptionAnalysisStepFunction`) with idempotent, short steps rather than one long-running function — required because Azure Functions consumption plans have execution time limits and Azure Resource Graph/Cost Management APIs throttle.

### Authorization model

Authorization is enforced entirely in the backend, even for HTTP functions declared `AuthorizationLevel.Anonymous` — the anonymous level only means the Functions host doesn't require a function key, not that the endpoint is unauthenticated. Every HTTP function validates a Bearer token (Entra ID) and a role:

- `FinOps.Reader`: read-only (dashboard, recommendations, anomalies, reports, ownership read).
- `FinOps.Admin`: read + operational writes (recommendation status, ownership, manual anomaly runs, billing period overrides).
- No token → `401`. Valid token without the required role → `403`.

`CurrentUserService`/`AuthorizationService` implement this check; reuse them rather than re-parsing claims. The frontend only adapts UX around roles — it must never be the sole authorization boundary.

### Resilience for Azure API calls

`HttpRetryService`, `CircuitBreakerService`, and `AzureApiThrottleService` wrap outbound calls to Azure Resource Manager/Cost Management/Resource Graph. Reuse these rather than adding ad-hoc retry loops — they're what keeps the multi-subscription timers from tripping Azure-side throttling.

### .NET isolated worker JSON quirk

Anonymous-typed responses do NOT get automatic camelCase conversion in .NET 8 isolated worker Functions (unlike ASP.NET Core). Any anonymous type returned as JSON to the frontend must explicitly name properties in camelCase, or the portal will receive PascalCase fields it doesn't expect.

### Frontend structure

`frontend/finops-portal/src/`: `services/api.ts` centralizes all HTTP calls to the backend, `types/api.ts` holds shared response types, `hooks/useFetch` wraps declarative data fetching. New pages must be registered both as a route in `App.tsx` and as navigation in `components/Layout.tsx`. Global styling lives in `src/index.css` (no CSS modules/styled-components). Endpoints that can 404 without data need explicit fallbacks — keep external data access null-safe.

## Area-specific instruction files

These apply automatically by path in Copilot but are useful context here too — read the matching file before editing:

- `.github/instructions/backend.instructions.md` — `src/**/*.cs`, `tests/**/*.cs`
- `.github/instructions/frontend.instructions.md` — `frontend/**`
- `.github/instructions/cicd.instructions.md` — `pipelines/**/*.yml`
- `.github/instructions/infrastructure.instructions.md` — `pipelines/terraform-function/**`

## CI/CD

Build and deploy are separate Azure DevOps pipelines per app (`pipelines/build-function.yml` + `deploy-function.yml` for the backend, `pipelines/build-frontend.yml` + `deploy-frontend.yml` for the portal), triggered on path filters (`src/**`, `frontend/**`). The environment comes from the branch in `pipelines/config.yml` (`main` → `prod`, any other branch such as `homolog` → `hml`); each environment has its own Library (`FinOps-<ambiente>-Personal`), state key and `environments/<ambiente>.tfvars`. HML reads prod's Cost Management Exports read-only — see `docs/AMBIENTE_HML.md`. Function App settings are owned entirely by Terraform (`pipelines/terraform-function/`) — do not add `AppSettingsDeploy` entries in the deploy pipeline for anything Terraform already manages. Never run Terraform locally against the production state; always go through the pipeline (`infra.yml`/`infra-frontend.yml`).
