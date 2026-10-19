# Azure FinOps Platform

Fonte canonica de instrucoes para agentes neste repositorio. Regras especificas por area ficam em `.github/instructions/`; nao as duplique aqui.

## Escopo e arquitetura

- Backend principal: `src/Personal.FinOpsApi.AzureFunctions/` (.NET 8, Azure Functions v4 isolated).
- Testes: `tests/Unit/` (xUnit, Moq, FluentAssertions).
- Portal: `frontend/finops-portal/` (React 19, TypeScript 5.7, Vite 6).
- Infraestrutura: `pipelines/terraform-function/` (Terraform/Azure).
- CI/CD: Azure DevOps em `pipelines/`.
- Integracoes: Cost Management, Resource Graph, Service Bus, Blob Storage, Cosmos DB, Application Insights e Log Analytics.

## Regras obrigatorias

- Faca alteracoes pequenas e focadas; preserve comportamento fora do pedido.
- Pesquise implementacoes existentes antes de criar helpers, modelos ou servicos.
- Use Managed Identity/`DefaultAzureCredential`; nunca grave secrets ou connection strings no codigo.
- Configuracoes e nomes de containers devem vir de App Settings/Terraform.
- APIs HTTP usam token Bearer e roles `FinOps.Reader`/`FinOps.Admin`, mesmo com `AuthorizationLevel.Anonymous`.
- Em respostas com anonymous types no .NET isolated, nomeie propriedades explicitamente em camelCase.
- Operacoes grandes por subscription devem usar Service Bus, idempotencia e etapas curtas; proteja APIs Azure contra retry, throttling e timeout.
- Nao execute Terraform contra o state de producao localmente; use o pipeline.
- Nao altere dependencias, pipelines ou infraestrutura sem necessidade direta.

## Validacao minima

Execute apenas os comandos relacionados aos arquivos alterados:

```powershell
dotnet build src\Personal.FinOpsApi.AzureFunctions\Personal.FinOpsApi.AzureFunctions.csproj
dotnet test tests\Unit\Personal.FinOpsApi.AzureFunctions.UnitTests.csproj
Set-Location frontend\finops-portal; npm run lint; npm run build
```

Para testes focados, prefira `dotnet test ... --filter "FullyQualifiedName~NomeDoTeste"`.

## Uso eficiente do Copilot

- Referencie arquivos com `@arquivo`; nao cole conteudo extenso no chat.
- Use `/context` para identificar peso de instrucoes, ferramentas MCP e historico.
- Use `/compact` em sessoes longas para resumir o historico.
- Use `/chronicle cost tips` periodicamente para analisar consumo entre sessoes.
- Mantenha apenas MCP servers necessarios para a tarefa atual.
- Revise estas instrucoes quando a stack ou arquitetura mudar e, no minimo, a cada tres meses.

