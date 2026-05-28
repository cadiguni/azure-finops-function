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
├── .env.example            # Config produção (VITE_API_BASE_URL=https://...azurewebsites.net)
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
    │   ├── Reports.tsx         # Visualizador de relatórios HTML/CSV
    │   ├── Recommendations.tsx # Tabela detalhada de recomendações
    │   ├── Anomalies.tsx       # Tabela de anomalias de custo
    │   └── Ownership.tsx       # Visualização de times (read-only)
    ├── services/
    │   └── api.ts          # Cliente API centralizado
    └── types/
        └── api.ts          # Interfaces TypeScript para respostas da API
```

## Páginas

### 1. Dashboard (`/`)
- **6 cards**: Economia Potencial Mensal, Economia Potencial Anual, Total de Recomendações, Recursos para Revisar, Recursos para Excluir, Anomalias de Custo
- **Tabelas**: Economia por Tipo de Recurso e por Subscription
- **APIs**: `GET /api/recommendations`, `GET /api/cost-anomalies`
- **Lógica**: `classifyAction()` classifica recursos como Excluir (discos/IPs) ou Revisar

### 2. Relatórios (`/reports`)
- **Filtros**: Data, Subscription, Time
- **Ações**: Abrir HTML (nova aba), Download CSV, Copiar link
- **Preview**: iframe com relatório HTML embutido
- **APIs**: `GET /api/report/html`, `GET /api/report/csv`, `GET /api/teams`, `GET /api/recommendations`
- **Nota**: Filtro de subscription e time são mutuamente exclusivos

### 3. Recomendações (`/recommendations`)
- **Tabela**: Subscription, Resource Group, Recurso, Tipo, Ação, Prioridade, Economia/mês, Descrição
- **Filtros**: Data, Subscription, Tipo, Ação, Prioridade, Ordenação
- **API**: `GET /api/recommendations`
- **Lógica**: `classifyAction()` classifica em Excluir, Revisar ou Investigar com base no tipo de recurso

### 4. Anomalias (`/anomalies`)
- **Tabela**: Subscription, Custo Atual, Média últimos N dias, Variação %, Meta Diária, Projeção Mensal, Severidade
- **Filtros**: Data, Período (1-30 dias), Subscription
- **API**: `GET /api/cost-anomalies?date=&days=`
- **Nota**: Retorna 404 se não há dados — frontend trata com `.catch()` e exibe mensagem

### 5. Ownership (`/ownership`)
- **Tabela**: Time, ID, Contato, Subscriptions (badges com nomes)
- **API**: `GET /api/teams`
- **Read-only**: Gerenciamento via `POST /api/teams` (Function auth)
- **Futuro**: Login por usuário com filtro por time

## APIs Consumidas

| Endpoint | Método | Auth | Uso |
|---|---|---|---|
| `/api/recommendations` | GET | Anonymous | Dashboard, Reports, Recommendations |
| `/api/cost-anomalies` | GET | Anonymous | Dashboard, Anomalies |
| `/api/report/html` | GET | Anonymous | Reports (iframe + link) |
| `/api/report/csv` | GET | Anonymous | Reports (download) |
| `/api/teams` | GET | Anonymous | Reports (filtro), Ownership |

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

### Build de Produção
```bash
cd frontend/finops-portal
npx tsc -b                     # Verifica tipos
npx vite build                 # Gera dist/
```

### Deploy
O deploy é automatizado via Azure DevOps (`pipelines/deploy-frontend.yml`):
1. **Trigger**: Push em `main` com alterações em `frontend/**`
2. **Build**: `npm ci` + `npm run build` com `VITE_API_BASE_URL` de produção
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
