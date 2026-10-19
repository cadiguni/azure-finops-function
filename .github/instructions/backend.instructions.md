---
applyTo: "src/**/*.cs,tests/**/*.cs"
---

# Backend .NET

- Use .NET 8 Azure Functions isolated e os padroes existentes de DI em `Program.cs`.
- Functions HTTP validam Bearer token e roles; `FinOps.Reader` le e `FinOps.Admin` altera.
- Use `DefaultAzureCredential`, clientes registrados em DI e configuracao via App Settings.
- Respostas JSON com anonymous types devem declarar propriedades camelCase explicitamente.
- Reutilize `HttpRetryService`, `CircuitBreakerService` e protecao de throttling para APIs Azure.
- Trabalhos longos ou multi-subscription devem usar Service Bus com mensagens idempotentes.
- Testes usam xUnit, Moq e FluentAssertions; adicione teste focado quando alterar comportamento.
- Valide com build do projeto e o menor filtro de testes que cubra a mudanca.

