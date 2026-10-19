---
applyTo: "frontend/**"
---

# Frontend

- Stack: React 19, TypeScript 5.7 strict, Vite 6 e react-router-dom 7; nao atualize para Vite 8.
- Centralize chamadas HTTP em `src/services/api.ts`, tipos em `src/types/api.ts` e chamadas declarativas em `useFetch`.
- Endpoints que podem retornar 404 sem dados devem ter fallback explicito; mantenha acessos a dados externos null-safe.
- Use as classes globais de `src/index.css`; nao introduza CSS modules ou styled-components.
- Ao criar pagina, registre rota em `App.tsx` e navegacao em `components/Layout.tsx`.
- O backend .NET isolated nao converte anonymous types automaticamente: respostas consumidas pelo portal devem usar nomes camelCase explicitos.
- Valide com `npm run lint` e `npm run build` em `frontend/finops-portal`.

