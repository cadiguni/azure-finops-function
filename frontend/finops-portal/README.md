# FinOps Portal

Portal web (SPA) para visualização de custos Azure, recomendações de economia e anomalias de custo. Consome as APIs do backend em [`../../src/Personal.FinOpsApi.AzureFunctions`](../../src/Personal.FinOpsApi.AzureFunctions) — veja o [README do projeto](../../README.md) para a visão geral da plataforma.

**URL de produção**: `https://finops.example.com`

## Stack

- React 19 + TypeScript 5.7 + Vite 6.x (Node 22.x requer Vite 6.x — não atualizar para Vite 8+ sem upgrade do Node)
- Roteamento: react-router-dom 7
- Autenticação: Microsoft Entra ID (MSAL — `@azure/msal-browser` + `@azure/msal-react`)
- Hosting: Azure Storage Static Website + Azure Front Door

## Estrutura do projeto

```
frontend/finops-portal/
├── .env.example        # Modelo de variáveis de ambiente
├── index.html           # Entry point HTML
├── vite.config.ts
├── tsconfig.json
└── src/
    ├── main.tsx          # Bootstrap React + BrowserRouter
    ├── App.tsx           # Definição de rotas
    ├── index.css         # Estilos globais
    ├── auth/             # Configuração MSAL
    ├── components/       # Componentes reutilizáveis (Layout, Card, StatusBadge, ...)
    ├── hooks/            # Hooks customizados
    ├── pages/            # Páginas/rotas (dashboard, ownership, anomalias, ...)
    ├── services/         # Clientes das APIs do backend
    └── types/            # Tipos TypeScript compartilhados
```

## Scripts disponíveis

| Comando | Descrição |
|---|---|
| `npm run dev` | Inicia o servidor de desenvolvimento (`http://localhost:5173`) |
| `npm run build` | Type-check (`tsc -b`) + build de produção em `dist/` |
| `npm run lint` | Executa o ESLint |
| `npm test` | Executa os testes unitários e de componentes |
| `npm run test:watch` | Executa testes novamente ao editar os arquivos |
| `npm run test:coverage` | Gera cobertura de testes em `coverage/` |
| `npm run preview` | Serve o build de produção localmente |

Veja o [guia de testes](../../docs/TESTING.md) para executar e ampliar as suítes do backend e do portal.

## Configuração

### Variáveis de ambiente

Copie `.env.example` para `.env.development` (local) ou `.env.production` (build de produção):

```env
VITE_ENTRA_CLIENT_ID=<client-id-da-app-registration>
VITE_ENTRA_TENANT_ID=<tenant-id>
VITE_ENTRA_REDIRECT_URI=http://localhost:5173/
VITE_API_BASE_URL=http://localhost:7071
```

Em produção, deixe `VITE_API_BASE_URL` vazio. O portal usa paths relativos (`/api/...`) e o Azure Front Door roteia `https://finops.example.com/api/*` para a Function App.

### Microsoft Entra ID — Redirect URIs

Na App Registration (tipo **Single-page application**), cadastrar:

| Ambiente   | Redirect URI                        |
|------------|-------------------------------------|
| Local      | `http://localhost:5173/`             |
| Produção   | `https://finops.example.com/`     |

### Execução local

```bash
cd frontend/finops-portal
npm install
npm run dev
```

Acesse `http://localhost:5173/`.

### Build de produção

```bash
npm run build
```

O output fica em `dist/`.

## Autenticação

O portal usa **MSAL.js** (`@azure/msal-browser` + `@azure/msal-react`) para login via Microsoft Entra ID.

- Login via redirect (não popup)
- Exibe nome e email do usuário na sidebar
- Usuário não autenticado vê tela de login
- Nenhum secret ou chave de API é armazenado no frontend

## Ownership Admin (Reader/Admin)

O portal possui tela de Ownership em `/ownership` com dois perfis:

- `FinOps.Reader`: visualização (somente leitura)
- `FinOps.Admin`: visualização e gerenciamento (adicionar/editar/remover subscriptions)

### Endpoints usados

- `GET /api/ownership/teams`
- `GET /api/ownership/teams/{teamId}`
- `POST /api/ownership/teams/{teamId}/subscriptions`
- `PUT /api/ownership/teams/{teamId}/subscriptions/{subscriptionId}`
- `DELETE /api/ownership/teams/{teamId}/subscriptions/{subscriptionId}`

### Responsabilidade de autorização

A autorização é validada no backend (Azure Function), não no frontend.

- Sem autenticação: `401`
- Autenticado sem role necessária: `403`

### Armazenamento

O ownership é persistido no Blob Storage:

- Container: `CONFIG_CONTAINER_NAME` (default `finops-config`)
- Blob: `config/ownership.json` (configurável por `OwnershipConfigBlobPath`)

### Grupos sugeridos no Entra ID

- `GRP-FinOps-Readers` (app role `FinOps.Reader`)
- `GRP-FinOps-Admins` (app role `FinOps.Admin`)
