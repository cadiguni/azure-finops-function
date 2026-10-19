# Testes automatizados

## Backend

Na raiz do repositório, com o SDK .NET 8 instalado:

```powershell
dotnet build src/Personal.FinOpsApi.AzureFunctions/Personal.FinOpsApi.AzureFunctions.csproj
dotnet test tests/Unit/Personal.FinOpsApi.AzureFunctions.UnitTests.csproj
```

Para validar somente uma área durante o desenvolvimento:

```powershell
dotnet test tests/Unit/Personal.FinOpsApi.AzureFunctions.UnitTests.csproj --filter "FullyQualifiedName~GovernanceDataHealthServiceTests"
```

A suíte utiliza xUnit, Moq e FluentAssertions. Os novos cenários verificam bloqueios de autenticação/configuração, limites e campos não permitidos nas solicitações, concorrência por ETag, validação do RG criado, mudanças no inventário após aprovação e cobertura de dados consolidada. Os clientes Azure são simulados: estes testes não movimentam recursos nem alteram states reais.

## Frontend

No diretório `frontend/finops-portal`, utilizando Node 22 atualizado ou Node 24:

```powershell
npm ci
npm test
npm run lint
npm run build
```

Para desenvolvimento e cobertura:

```powershell
npm run test:watch
npm run test:coverage
npm test -- src/pages/Migrations.test.tsx src/services/armApi.test.ts
```

Vitest, Testing Library e jsdom executam os testes sem navegador externo. A configuração reutiliza os aliases do Vite 6. O relatório HTML fica em `coverage/index.html`; também são gerados resumo no terminal e `coverage/lcov.info`. A pasta de relatórios não é versionada nem analisada pelo ESLint.

Os cenários iniciais cobrem:

- Migrações: acesso só para Admin, validação antes do move, nova validação a cada mudança de seleção, criação do RG de destino, erros do ARM e acompanhamento sem reenvio do move.
- Orçamentos: dados incompletos, permissões, edição/exclusão com versão, conflito e prevenção de submissões duplicadas.
- Saúde dos dados: consulta explícita por assinatura/período, lacunas, índice ausente e recuperação de erros.
- Transporte HTTP: token Bearer, sessão ausente/expirada, erros estruturados, timeout e ETag.
- `useFetch`: carregamento, atualização, falha e descarte de respostas de consultas anteriores.

O setup em `src/test/setup.ts` limpa componentes e mocks globais, e rejeita chamadas `fetch` não simuladas. Os testes de transporte simulam MSAL e HTTP; os testes de páginas simulam os serviços, mantendo os componentes e hooks reais. Fixtures compartilhadas ficam em `src/test/fixtures.ts`.

Ao adicionar testes, prefira comportamentos observáveis e situações de risco a detalhes internos da implementação. Use consultas por papel/nome acessível e controle explicitamente promessas para testar concorrência, sem esperas arbitrárias.

## Limites

A cobertura inclui todo o código do portal, inclusive áreas ainda sem testes. Esta é uma base inicial, não cobertura completa nem validação ponta a ponta. Não comprova permissões reais no Entra/Azure, execução da pipeline NAP ou reconciliação de um Terraform State; esses fluxos precisam de testes operacionais controlados em DEV/HML.

Nenhuma pipeline de CI/CD foi alterada nesta entrega. Os comandos acima podem ser incorporados à etapa de validação existente.
