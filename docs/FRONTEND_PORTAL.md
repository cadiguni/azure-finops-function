# 🖥️ Frontend Portal - FinOps Platform

## Visão Geral

SPA (Single Page Application) em **React 19 + TypeScript + Vite** que consome as APIs do backend Azure Functions para exibir dados de custos, recomendações, anomalias e ownership de times.

**URL de produção**: `https://finops.example.com`

## Stack Tecnológico

| Tecnologia | Versão | Uso |
|---|---|---|
| React | 19.1 | Framework UI |
| TypeScript | 5.7 | Tipagem estática |
| Vite | 6.x | Build tool (Vite 8 incompatível com Node 22) |
| react-router-dom | 7.6 | Roteamento SPA |
| lucide-react | — | Ícones |
| date-fns | — | Manipulação de datas |

> **Nota**: Node.js 22.x requer Vite 6.x. Não atualizar para Vite 8+ sem upgrade do Node.

## Estrutura do Projeto

```
frontend/finops-portal/
├── .env                    # Config local (VITE_API_BASE_URL=http://localhost:7071)
├── .env.production         # Produção usa VITE_API_BASE_URL vazio e /api via Front Door
├── index.html              # Entry point HTML
├── vite.config.ts          # Configuração do Vite
├── tsconfig.json           # Configuração TypeScript
├── package.json            # Dependências
└── src/
    ├── main.tsx            # Bootstrap React + BrowserRouter
    ├── App.tsx             # Definição de rotas
    ├── index.css           # Estilos globais (dark theme)
    ├── components/
    │   ├── Layout.tsx      # Layout com sidebar + Outlet
    │   ├── Card.tsx        # Card reutilizável para métricas
    │   └── StatusBadge.tsx # Badge de severidade/prioridade
    ├── hooks/
    │   └── useFetch.ts     # Hook genérico para chamadas API
    ├── pages/
    │   ├── Dashboard.tsx       # 6 cards com resumo geral
    │   ├── Recommendations.tsx # Tabela detalhada de recomendações
    │   ├── Anomalies.tsx       # Tabela de anomalias de custo
    │   └── Ownership.tsx       # Visualização/administração de ownership por time
    ├── services/
    │   └── api.ts          # Cliente API centralizado
    └── types/
        └── api.ts          # Interfaces TypeScript para respostas da API
```

## Páginas

### 1. Dashboard (`/`)

- **Cards**: custo acumulado nos ciclos das assinaturas, projeção dos ciclos, economias estimadas, recomendações, anomalias e projeção dos bancos analisados.
- **Tabelas**: indicadores de bancos e resumo por time, incluindo `Sem atribuição`.
- **APIs**: `GET /api/dashboard/summary`, `GET /api/dashboard/teams`, `GET /api/dashboard/databases`.
- **Qualidade financeira**: `financialSummary` informa cobertura, fontes, datas de análise e revisão do relatório. Custos ausentes são `null`/`—`, não zero. Valores disponíveis permanecem provisórios.
- **Período**: a soma dos ciclos de faturamento não representa necessariamente o mês-calendário. Os custos de bancos continuam sendo projeções associadas às recomendações, não o inventário completo.
- **Compatibilidade**: summaries antigos sem metadados financeiros exibem aviso até serem regenerados. O fallback mantém a data do snapshot usado e alinha os detalhes à mesma data.
- **Escopo e implantação**: ver [Confiabilidade financeira — primeira entrega](FINANCIAL_DATA_RELIABILITY.md).

### 2. Recomendações (`/recommendations`)
- **Exportação** (substitui a antiga tela Relatórios; `/reports` redireciona para cá):
  - "Abrir relatório HTML" e "Exportar CSV" usam o snapshot publicado da data selecionada
  - escopo: filtro de Subscription; senão filtro de Time; senão global. Demais filtros da tabela não se aplicam ao arquivo
  - APIs: `GET /api/report/html`, `GET /api/report/csv`
- **Economia**: cada recomendação com custo direto (match por ResourceId ou nome + RG + tipo) tem economia na moeda do próprio custo. Totais somam uma única moeda (`savingsCurrency`: a moeda financeira quando há economia nela, senão a mais frequente entre as recomendações); valores em outra moeda aparecem por linha, sem entrar no total.
- **Visão operacional híbrida** (Blob + Cosmos)
- **Abas por status**:
  - Ativas: `New`, `InReview`, `Accepted`, `ApprovedForCleanup`
  - Pausadas/Ignoradas: `FalsePositive`, `Ignored`, `Snoozed`
  - Resolvidas: `Resolved`
- **Tabela**:
  - Recurso, Resource Group, Subscription, Tipo, Time dono
  - Severidade, Economia/mês, Ação sugerida
  - Status, Motivo, UpdatedBy, UpdatedAt, Snooze até
- **Filtros**: Data, Status, Severidade, Subscription, Tipo, Time, busca por nome/id do recurso
- **Colunas**:
  - seletor de colunas (show/hide)
  - redimensionamento por coluna
  - ordenação por clique no header
- **Ações Admin**:
  - altera status via `PATCH /api/recommendations/{id}/status`
  - refetch automático com `GET /api/recommendations` após PATCH
  - feedback visual de sucesso/erro
- **Reader**: visualização sem ações de edição
- **API**: `GET /api/recommendations`, `PATCH /api/recommendations/{id}/status`

### 3. Anomalias (`/anomalies`)
- **Tabela**: Subscription, Time, Custo Atual, Custo no Ciclo, Média Ciclo/Dia, Vira em, Média últimos N dias, Variação %, Meta Diária, Projeção Mensal, Severidade
- **Filtros**: Data, Período (1-30 dias), Time, Subscription
- **API**: `GET /api/cost-anomalies?date=&days=`
- **UX**:
  - seletor de colunas (show/hide)
  - redimensionamento por coluna
  - ordenação por clique nas colunas, incluindo Time, Custo no Ciclo, Média Ciclo/Dia e Vira em
- **Ações Admin**:
  - executar análise manual via `POST /api/cost-anomalies/run`
  - ajustar override de billing period por subscription (salvar/remover)
  - endpoints usados: `GET /api/billing/periods/overrides`, `PUT /api/billing/periods/overrides/{subscriptionId}`, `DELETE /api/billing/periods/overrides/{subscriptionId}`
- **Reader**: visualização sem ações de edição
- **Nota**: Retorna 404 se não há dados — frontend trata com `.catch()` e exibe mensagem

### 4. Ownership (`/ownership`)
- **Visualização**: Reader e Admin visualizam times/subscriptions
- **Administração**: Admin adiciona, edita e remove subscriptions por time
- **APIs**:
  - `GET /api/ownership/teams`
  - `GET /api/ownership/teams/{teamId}`
  - `POST /api/ownership/teams/{teamId}/subscriptions`
  - `PUT /api/ownership/teams/{teamId}/subscriptions/{subscriptionId}`
  - `DELETE /api/ownership/teams/{teamId}/subscriptions/{subscriptionId}`

## APIs Consumidas

| Endpoint | Método | Auth | Uso |
|---|---|---|---|
| `/api/me` | GET | Bearer | Sessão e roles no portal |
| `/api/recommendations` | GET | Bearer + role Reader/Admin | Dashboard, Reports, Recommendations |
| `/api/recommendations/{id}/status` | PATCH | Bearer + role Admin | Atualização de status operacional |
| `/api/cost-anomalies` | GET | Bearer + role Reader/Admin | Dashboard, Anomalies |
| `/api/cost-anomalies/run` | POST | Bearer + role Admin | Anomalies (execução manual) |
| `/api/billing/periods/overrides` | GET | Bearer + role Admin | Lista de overrides manuais de billing period |
| `/api/billing/periods/overrides/{subscriptionId}` | PUT | Bearer + role Admin | Criar/atualizar override manual de billing period |
| `/api/billing/periods/overrides/{subscriptionId}` | DELETE | Bearer + role Admin | Remover override manual de billing period |
| `/api/report/html` | GET | Bearer + role Reader/Admin | Reports (iframe + link) |
| `/api/report/csv` | GET | Bearer + role Reader/Admin | Reports (download) |
| `/api/teams` | GET | Bearer + role Reader/Admin | Reports (filtro), Ownership |
| `/api/ownership/teams` | GET | Bearer + role Reader/Admin | Ownership |
| `/api/ownership/teams/{teamId}` | GET | Bearer + role Reader/Admin | Ownership |
| `/api/ownership/teams/{teamId}/subscriptions` | POST | Bearer + role Admin | Ownership |
| `/api/ownership/teams/{teamId}/subscriptions/{subscriptionId}` | PUT | Bearer + role Admin | Ownership |
| `/api/ownership/teams/{teamId}/subscriptions/{subscriptionId}` | DELETE | Bearer + role Admin | Ownership |

### Contrato da API `/api/recommendations`

```json
{
  "date": "2026-05-22",
  "totalRecommendations": 15,
  "totalEstimatedMonthlySavings": 1234.56,
  "totalEstimatedAnnualSavings": 14814.72,
  "byType": [
    { "type": "UnattachedDisk", "count": 3, "estimatedMonthlySavings": 150.0 }
  ],
  "bySubscription": [
    { "subscriptionId": "abc-123", "count": 5, "estimatedMonthlySavings": 500.0 }
  ],
  "recommendations": [
    {
      "resourceId": "/subscriptions/.../disks/disk1",
      "resourceName": "disk1",
      "resourceType": "Microsoft.Compute/disks",
      "resourceGroup": "rg-prod",
      "subscriptionId": "abc-123",
      "type": "UnattachedDisk",
      "priority": "High",
      "description": "Disco não anexado a nenhuma VM",
      "recommendation": "Excluir disco",
      "estimatedMonthlySavings": 50.0,
      "dailyCost": 1.67,
      "estimatedMonthlyCost": 50.0,
      "confidence": 0.95,
      "impact": "High"
    }
  ]
}
```

> **IMPORTANTE**: Todas as propriedades do JSON devem ser **camelCase**. O backend usa `WriteAsJsonAsync` com anonymous types — usar nomes explícitos: `resourceId = r.ResourceId` (não `r.ResourceId` que serializa como PascalCase).

### Contrato da API `/api/cost-anomalies`

```json
[
  {
    "date": "2026-05-22",
    "dailyBudget": 100.0,
    "subscriptionId": "abc-123",
    "subscriptionName": "MPN - DEV",
    "todayCost": 150.0,
    "averageLastDays": 95.0,
    "increaseAmount": 55.0,
    "increasePercent": 57.9,
    "monthlyProjection": 4500.0,
    "projectedOverBudget": 1500.0,
    "severity": "High",
    "hasAnomaly": true,
    "reasons": ["Custo 57.9% acima da média"]
  }
]
```

### Contrato da API `/api/teams`

```json
{
  "teamsCount": 2,
  "lastUpdated": "2026-05-07T14:00:00Z",
  "teams": [
    {
      "id": "plataforma",
      "name": "Plataforma",
      "email": "plataforma@empresa.com",
      "subscriptionsCount": 2,
      "subscriptionIds": ["abc-123", "def-456"],
      "subscriptionNames": ["MPN - DEV", "MPN - HML"]
    }
  ]
}
```

## Serialização JSON - Gotcha Crítico

O backend .NET 8 isolated usa `System.Text.Json` com `PropertyNamingPolicy = null` (padrão), que **preserva o casing original** das propriedades C#.

### ❌ Errado (serializa como PascalCase)
```csharp
.Select(r => new
{
    r.ResourceId,      // → JSON: "ResourceId"
    r.ResourceName,    // → JSON: "ResourceName"
    r.Description      // → JSON: "Description"
})
```

### ✅ Correto (serializa como camelCase)
```csharp
.Select(r => new
{
    resourceId = r.ResourceId,      // → JSON: "resourceId"
    resourceName = r.ResourceName,  // → JSON: "resourceName"
    description = r.Description     // → JSON: "description"
})
```

O frontend espera **camelCase**. Se o backend retornar PascalCase, propriedades serão `undefined` no JavaScript, causando crash no React (tela preta sem error boundary).

## Build e Deploy

### Desenvolvimento Local
```bash
cd frontend/finops-portal
npm install
npm run dev                    # http://localhost:5173
```

Requer `.env` com:
```
VITE_API_BASE_URL=http://localhost:7071
```

Em produção, `VITE_API_BASE_URL` deve ficar vazio. Assim todas as chamadas usam `/api/...` no próprio domínio `https://finops.example.com`, e o Azure Front Door encaminha `/api/*` para a Function App.

Para SSO:
```
VITE_AZURE_CLIENT_ID=<app-registration-client-id>
VITE_AZURE_TENANT_ID=<tenant-id>
VITE_FINOPS_API_SCOPE=api://<api-app-id>/.default
```

### Build de Produção
```bash
cd frontend/finops-portal
npx tsc -b                     # Verifica tipos
npx vite build                 # Gera dist/
```

### Deploy
O deploy é automatizado via Azure DevOps (`pipelines/deploy-frontend.yml`):
1. **Trigger**: Push em `main` com alterações em `frontend/**`
2. **Build**: `npm ci` + `npm run build` com `VITE_API_BASE_URL` vazio para usar `/api/*` via Front Door
3. **Upload**: `az storage blob upload-batch` para `$web` no Storage Account
4. **Cache**: Purge do Azure Front Door (`finops.example.com`)

### Infraestrutura
- **Hosting**: Azure Storage Static Website (`finopsplatformnapwebstg`)
- **CDN**: Azure Front Door Standard (`frontdoor2-dev-afd`)
- **Domínio**: `finops.example.com`
- **CORS**: Backend configurado com `allowed_origins = ["*"]`

## Estilo Visual

- **Tema**: Dark theme exclusivo (variáveis CSS em `index.css`)
- **Layout**: Sidebar fixa à esquerda + conteúdo principal
- **Responsivo**: Tabelas com scroll horizontal (`table-container`)
- **Badges**: `.badge--danger` (vermelho), `.badge--warning` (amarelo), `.badge--info` (azul), `.badge--muted` (cinza)

## Classificação de Ações (`classifyAction`)

Lógica compartilhada entre Dashboard e Recommendations para classificar recursos:

| Tipo de Recurso | Ação |
|---|---|
| `disk` + "unattached" | **Excluir** |
| `publicipaddresses` | **Excluir** |
| `operationalinsights` / `workspace` | **Revisar** |
| `serverfarms` / `web/sites` | **Investigar** |
| `virtualmachines` | **Investigar** |
| `storageaccounts` | **Investigar** |
| Outros | **Investigar** |

## Planos Futuros

- **Login por usuário**: Cada usuário será separado por time e só poderá ver o relatório do seu time
- **Filtros persistentes**: Salvar último filtro selecionado no localStorage

## Autenticação, SSO e autorização

- Frontend usa MSAL para login SSO no Entra ID e obtenção de token Bearer para API.
- Backend é a fonte da verdade da autorização:
  - não autenticado: `401`
  - autenticado sem role: `403`
- Roles:
  - `FinOps.Reader`: leitura
  - `FinOps.Admin`: leitura + alterações (ex.: PATCH de status, ownership write, execução manual de anomalias e override de billing period).
