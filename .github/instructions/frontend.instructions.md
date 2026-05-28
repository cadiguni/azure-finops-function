---
applyTo: "frontend/**"
---

# Frontend Portal - Convenções e Regras

## Stack

- React 19 + TypeScript 5.7 + Vite 6.x + react-router-dom 7.x
- Node.js 22.x (Vite 8 é incompatível — manter Vite 6)
- Hosting: Azure Storage Static Website + Azure Front Door

## Estrutura de Arquivos

```
frontend/finops-portal/src/
├── App.tsx              # Rotas (react-router-dom)
├── index.css            # Dark theme global
├── components/          # Componentes reutilizáveis (Layout, Card, StatusBadge)
├── hooks/useFetch.ts    # Hook genérico para chamadas API
├── pages/               # Uma página por rota
├── services/api.ts      # Cliente API centralizado
└── types/api.ts         # Interfaces TypeScript para respostas da API
```

## Convenções de Código

### Novas Páginas
1. Criar arquivo em `src/pages/NomePagina.tsx`
2. Adicionar rota em `App.tsx`
3. Adicionar item de navegação em `components/Layout.tsx` (ícone via lucide-react)

### Chamadas à API
- Usar `api.ts` para todas as chamadas — nunca fetch direto nos componentes
- Usar hook `useFetch()` para chamadas declarativas com loading/error/refetch
- Sempre adicionar `.catch()` para endpoints que podem falhar (ex: anomalias, teams)
- Tipar respostas com interfaces em `types/api.ts`

### Tratamento de Erros
- `useFetch` captura erros e expõe via `error` state
- Para endpoints que retornam 404 quando não há dados (ex: `/api/cost-anomalies`), usar `.catch()` no fetcher para retornar array/objeto vazio
- Acessos a propriedades de objetos da API devem usar null-safe: `(r.property ?? '').toLowerCase()`
- Sem error boundary — crash no render = tela preta

### Estilos
- Usar classes CSS definidas em `index.css` (não CSS modules, não styled-components)
- Classes de layout: `.page`, `.page-header`, `.section`, `.filters`, `.filter-group`
- Classes de tabela: `.table-container`, `table`, `.font-mono`, `.text-center`
- Classes de badge: `.badge`, `.badge--danger`, `.badge--warning`, `.badge--info`, `.badge--muted`
- Classes de alerta: `.alert`, `.alert--warning`
- Classes de texto: `.text-success`, `.text-danger`, `.text-muted`

## Serialização JSON - Regra Crítica

O backend .NET 8 isolated NÃO aplica camelCase automaticamente. Ao criar anonymous types no C#:

```csharp
// ❌ ERRADO: serializa como PascalCase → frontend recebe undefined
new { r.ResourceId, r.Description }

// ✅ CORRETO: serializa como camelCase → frontend funciona
new { resourceId = r.ResourceId, description = r.Description }
```

O frontend espera **sempre camelCase**. PascalCase causa `undefined.toLowerCase()` → crash.

## Build e Deploy

```bash
# Desenvolvimento
cd frontend/finops-portal
npm run dev

# Verificação de tipos + build
npx tsc -b && npx vite build

# Deploy automático via push em main (frontend/**)
# Pipeline: pipelines/deploy-frontend.yml
```

## APIs Consumidas

- `GET /api/recommendations?date=YYYY-MM-DD` — Dashboard, Reports, Recommendations
- `GET /api/cost-anomalies?date=&days=` — Dashboard, Anomalies (retorna 404 se sem dados)
- `GET /api/report/html?date=&subscription=&team=` — Reports
- `GET /api/report/csv?date=&subscription=&team=` — Reports
- `GET /api/teams` — Reports (filtro por time), Ownership

Todas as APIs usam `AuthorizationLevel.Anonymous` (sem chave de função).
