# FinOps Portal

Portal web para visualização de custos Azure, recomendações de economia e anomalias de custo.

## Stack

- React 19 + TypeScript 5.7 + Vite 6.x
- Autenticação: Microsoft Entra ID (MSAL)
- Hosting: Azure Storage Static Website + Azure Front Door

## Configuração

### Variáveis de ambiente

Copie `.env.example` para `.env.development` (local) ou `.env.production` (build de produção):

```env
VITE_ENTRA_CLIENT_ID=<client-id-da-app-registration>
VITE_ENTRA_TENANT_ID=<tenant-id>
VITE_ENTRA_REDIRECT_URI=http://localhost:5173/
VITE_API_BASE_URL=http://localhost:7071
```

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

### Limitação atual

O login do portal está implementado, mas a **API (Azure Function) ainda não valida tokens**. A próxima etapa será:

1. Criar App Registration separada para a Function (API)
2. Configurar a Function para validar Bearer Tokens
3. O frontend enviará o token no header `Authorization: Bearer <token>`
4. Implementar autorização por time/role
      // Enable lint rules for React DOM
      reactDom.configs.recommended,
    ],
    languageOptions: {
      parserOptions: {
        project: ['./tsconfig.node.json', './tsconfig.app.json'],
        tsconfigRootDir: import.meta.dirname,
      },
      // other options...
    },
  },
])
```
