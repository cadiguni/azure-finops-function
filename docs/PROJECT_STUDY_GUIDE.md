# Guia de estudo do projeto Azure FinOps Platform

Este projeto é uma plataforma FinOps para Azure feita com Azure Functions em .NET 8 isolated worker. O objetivo central é encontrar oportunidades de economia, coletar custos reais do Azure Cost Management, salvar resultados no Blob Storage e expor dados para consultas, relatórios e dashboards.

## Visão geral

O projeto principal está em:

- `src/Personal.FinOpsApi.AzureFunctions`

A aplicação funciona como um conjunto de Azure Functions, não como uma API ASP.NET tradicional. Cada arquivo em `Functions/` define uma ou mais funções acionadas por HTTP, Timer ou Service Bus.

Os principais blocos são:

- `Functions/`: pontos de entrada da aplicação.
- `Application/`: orquestração das análises.
- `Analyzers/`: regras FinOps que detectam recursos subutilizados ou abandonados.
- `Services/`: integrações com Azure, storage, filas, métricas, Cost Management, relatórios e observabilidade.
- `Models/`: contratos de entrada, saída e dados persistidos.
- `tests/Unit/`: testes unitários de serviços e funções.
- `pipelines/`: CI/CD e infraestrutura Terraform.

## Stack usada

- .NET 8.
- Azure Functions v4 isolated worker.
- Azure Blob Storage para persistência.
- Azure Service Bus para processamento assíncrono.
- Azure Resource Graph para descobrir recursos.
- Azure Monitor Metrics para métricas de uso.
- Azure Cost Management API para custo real.
- Log Analytics para envio opcional de recomendações.
- Application Insights para telemetria.

O arquivo `Program.cs` configura a injeção de dependência. Ele registra analyzers, serviços de storage, Cost Management, Service Bus, Azure SDK clients, geração de relatórios e serviços auxiliares.

## Como a aplicação inicia

O ponto de entrada é `Program.cs`.

Fluxo simplificado:

1. Cria um `HostBuilder`.
2. Ativa `ConfigureFunctionsWorkerDefaults()`.
3. Registra serviços no container de DI.
4. Cria clientes Azure, como `DefaultAzureCredential`, `ArmClient`, `MetricsQueryClient` e `BlobServiceClient`.
5. Chama `host.Run()`.

Exemplo conceitual:

```text
Azure Function Runtime
        |
        v
Program.cs
        |
        v
DI container registra Functions, Services e Analyzers
        |
        v
Triggers HTTP / Timer / Service Bus executam a lógica
```

## Principais fluxos da aplicação

### 1. Análise manual por HTTP

Entrada principal:

- Function: `analyze-costs`
- Arquivo: `Functions/HealthFunction.cs`
- Serviço central: `CostAnalysisOrchestrator`

Essa função recebe uma requisição HTTP `GET` ou `POST`, monta um `CostAnalysisRequest` e chama:

```csharp
_orchestrator.ExecuteAnalysisAsync(request)
```

Depois tenta salvar o resultado em dois formatos:

- formato padronizado em Blob Storage via `AnalysisStorageService`;
- formato legado via `FinOpsResultAggregator`.

Por segurança, o `GET` sem `dryRun` explícito força `dryRun=true`. Isso evita que uma chamada acidental seja interpretada como execução real.

### 2. Análise agendada por Timer

Entrada principal:

- Function: `CostAnalysisTimer`
- Arquivo: `Functions/CostAnalysisTimerFunction.cs`
- Schedule: variável `CostAnalysisSchedule`

Esse fluxo roda automaticamente conforme configuração de ambiente.

Ele faz:

1. Descobre subscriptions com `SubscriptionDiscoveryService`.
2. Para cada subscription, decide se usa fila ou execução direta.
3. Se `ENABLE_QUEUE_PROCESSING=true`, envia mensagem para Service Bus.
4. Se filas estiverem desabilitadas, executa diretamente:

```csharp
_orchestrator.AnalyzeSubscriptionAsync(subscriptionId, "complete", false)
```

Existe uma regra especial para uma subscription de produção, que é roteada para fila dedicada.

### 3. Processamento por Service Bus em etapas

Esse é o fluxo mais importante para subscriptions grandes.

Arquivos principais:

- `Functions/SubscriptionAnalysisStepStarterFunction.cs`
- `Functions/SubscriptionAnalysisStepFunction.cs`
- `Services/QueueService.cs`
- `Services/AnalysisStorageService.cs`

O problema resolvido aqui é timeout. Em Azure Functions Consumption Plan, uma análise completa pode demorar demais. Então o projeto divide a análise em steps menores.

Fluxo:

```text
Mensagem inicial
        |
        v
Step "orchestrate"
        |
        v
Enfileira steps independentes:
  - storage
  - vm
  - appservice
  - functionapp
  - loganalytics
  - publicip
        |
        v
Cada step executa um analyzer específico
        |
        v
Cada step salva resultado parcial no Blob
        |
        v
Step "consolidate" junta tudo
        |
        v
Salva recommendations.json final
```

Cada step é idempotente. Antes de executar, a função verifica se o step já foi concluído usando `completed-steps.json`. Isso evita retrabalho se a mesma mensagem for processada mais de uma vez.

O resultado parcial fica em:

```text
steps/{analysisId}/{stepType}-results.json
steps/{analysisId}/completed-steps.json
```

O resultado final fica em:

```text
analyses/year=YYYY/month=MM/day=DD/{subscriptionId}/recommendations.json
```

### 4. Coleta de custo real por serviço

Entrada principal:

- Function: `CostByServiceDailyTimer`
- Arquivo: `Functions/CostByServiceDailyTimerFunction.cs`
- Serviço: `CostManagementClient`
- Storage: `CostStorageRepository`

Esse fluxo coleta o custo real do dia anterior agrupado por serviço Azure.

Ele chama a API:

```text
POST https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/query
```

Agrupa por `ServiceName` e salva:

```text
cost/byService/date=YYYY-MM-DD/subscriptionId={sub}/byService.json
cost/byService/date=YYYY-MM-DD/subscriptionId={sub}/raw.json
```

### 5. Coleta de custo real por recurso

Arquivos relacionados:

- `Functions/CostByResourceDailyTimerFunction.cs`
- `Functions/CostByResourceQueueStarterFunction.cs`
- `Functions/CostByResourceQueueFunction.cs`
- `Services/CostManagementClient.cs`
- `Services/CostStorageRepository.cs`

Esse fluxo consulta Cost Management agrupando por `ResourceId` e `ServiceName`. Ele permite descobrir quais recursos específicos custaram mais.

O resultado é salvo em:

```text
cost/byResource/date=YYYY-MM-DD/subscriptionId={sub}/byResource.json
cost/byResource/date=YYYY-MM-DD/subscriptionId={sub}/raw.json
```

### 6. APIs para Grafana

Arquivo:

- `Functions/GrafanaApiFunction.cs`

Endpoints principais:

- `GET /api/grafana/health`
- `GET /api/GrafanaCostByService?date=YYYY-MM-DD&subscription=all`
- `GET /api/GrafanaCostTrendByService?from=YYYY-MM-DD&to=YYYY-MM-DD&subscription=all&service=...`
- `GET /api/GrafanaCostByResource?date=YYYY-MM-DD&subscription=all&service=...`
- `GET /api/GrafanaCostTrendByResource?from=YYYY-MM-DD&to=YYYY-MM-DD&subscription=all&resource=...`

Esses endpoints não consultam a Azure em tempo real. Eles leem dados já salvos no Blob Storage. Isso é bom para dashboards porque reduz custo, latência e risco de rate limit.

## Orquestrador central

Arquivo:

- `Application/CostAnalysisOrchestrator.cs`

Essa classe coordena os analyzers.

Ela tem três responsabilidades principais:

1. Executar análise com base em `CostAnalysisRequest`.
2. Executar análise completa de uma subscription.
3. Executar análises isoladas para o processamento em steps.

Exemplos de métodos:

- `ExecuteAnalysisAsync`
- `AnalyzeSubscriptionAsync`
- `AnalyzeStorageAccountsOnlyAsync`
- `AnalyzeVirtualMachinesOnlyAsync`
- `AnalyzeAppServicesOnlyAsync`
- `AnalyzeFunctionAppsOnlyAsync`
- `AnalyzePublicIpsOnlyAsync`
- `AnalyzeLogAnalyticsOnlyAsync`

O método `ExecuteAnalysisAsync` valida a requisição, decide quais analyzers devem rodar e consolida as recomendações em um resumo.

O método `AnalyzeSubscriptionAsync` roda uma análise completa, salva no Blob Storage e envia dados para Log Analytics quando configurado.

## Analyzers

Os analyzers são classes especializadas que produzem recomendações FinOps no formato `StandardAnalyzerResult`.

### UnattachedDiskAnalyzer

Arquivo:

- `Analyzers/UnattachedDiskAnalyzer.cs`

Busca discos gerenciados não anexados.

Fonte de dados:

- Azure Resource Graph.

Regra principal:

- recurso do tipo `microsoft.compute/disks`;
- `managedBy` vazio ou nulo;
- `diskState = Unattached`.

Para custo:

1. tenta usar custo real via `ResourceCostLookupService`;
2. se não houver dado, usa estimativa por SKU e tamanho.

Economia estimada:

- aproximadamente 98% do custo mensal, assumindo remoção após validação.

### IdleVmAnalyzer

Arquivo:

- `Analyzers/IdleVmAnalyzer.cs`

Busca VMs ligadas, mas com baixo uso.

Fonte de dados:

- Azure Resource Graph para listar VMs em execução;
- Azure Monitor para CPU, rede e memória quando disponível;
- Cost Management para custo real.

Regra principal:

- CPU média menor que 5%;
- tráfego médio de rede menor que 0.1 GB por dia.

Economia estimada:

- aproximadamente 85% do custo mensal, assumindo desligamento ou ajuste de uso.

### StorageAccountAnalyzer

Arquivo:

- `Analyzers/StorageAccountAnalyzer.cs`

Busca Storage Accounts subutilizadas.

Estratégia:

1. Usa Resource Graph para listar storages.
2. Aplica um filtro inicial para reduzir chamadas ao Azure Monitor.
3. Para candidatos suspeitos, consulta métricas reais.
4. Gera finding se houver baixo uso.

Regras de suspeita:

- nomes contendo padrões como `test`, `temp`, `dev`, `old`, `backup`, `log` fora de produção;
- SKU básico;
- tier frio;
- resource group de desenvolvimento/teste.

Regra de subutilização:

- poucas transações por dia;
- baixa capacidade usada;
- baixo tráfego.

### Outros analyzers

Também existem analyzers para:

- IP público não utilizado;
- App Services subutilizados;
- Function Apps;
- Log Analytics workspaces;
- recursos duplicados.

Todos seguem a mesma ideia: descobrir recursos, avaliar uso/custo e retornar recomendações padronizadas.

## Modelos importantes

### CostAnalysisRequest

Arquivo:

- `Models/CostAnalysisRequest.cs`

Representa a entrada de uma análise.

Campos importantes:

- `Scope`: normalmente `subscription`.
- `SubscriptionId`: subscription Azure analisada.
- `ManagementGroupId`: preparado para escopo de management group.
- `AnalysisPeriodDays`: período analisado.
- `DryRun`: padrão seguro `true`.
- `AnalysisOptions`: define quais analyzers rodarão.

### StandardAnalyzerResult e StandardFinding

Arquivo:

- `Models/StandardAnalyzerContract.cs`

Esses modelos formam o contrato padrão dos analyzers.

Um `StandardAnalyzerResult` representa a execução de um analyzer.

Um `StandardFinding` representa uma oportunidade FinOps, com dados como:

- tipo da recomendação;
- recurso;
- subscription;
- custo diário;
- custo mensal estimado;
- economia mensal estimada;
- prioridade;
- confiança;
- descrição;
- recomendação;
- tags;
- metadados.

## Persistência no Blob Storage

Existem dois repositórios principais.

### AnalysisStorageService

Arquivo:

- `Services/AnalysisStorageService.cs`

Usado para recomendações de otimização.

Caminho principal:

```text
analyses/year=YYYY/month=MM/day=DD/{subscriptionId}/recommendations.json
```

Ele também controla resultados parciais dos steps:

```text
steps/{analysisId}/{stepType}-results.json
steps/{analysisId}/completed-steps.json
```

Um detalhe importante: o serviço evita sobrescrever um `recommendations.json` existente com uma lista vazia quando já existe conteúdo. Isso protege resultados bons contra uma execução posterior que falhou ou retornou vazio.

### CostStorageRepository

Arquivo:

- `Services/CostStorageRepository.cs`

Usado para custo real por serviço e por recurso.

Caminhos:

```text
cost/byService/date=YYYY-MM-DD/subscriptionId={sub}/byService.json
cost/byResource/date=YYYY-MM-DD/subscriptionId={sub}/byResource.json
```

## Integração com Azure Cost Management

Arquivo:

- `Services/CostManagementClient.cs`

Esse serviço monta requisições para a API de Cost Management.

Ele permite consultar custo:

- por serviço, agrupando por `ServiceName`;
- por recurso, agrupando por `ResourceId` e `ServiceName`.

Ele também tem retry para falhas transitórias:

- HTTP 429;
- erros 5xx.

Se a resposta tiver `Retry-After`, o código respeita o valor. Caso contrário, usa backoff exponencial com jitter.

## Filas e feature flag

Arquivo:

- `Services/QueueService.cs`

A aplicação pode operar em dois modos:

- execução direta;
- execução via Service Bus.

A variável que controla isso é:

```text
ENABLE_QUEUE_PROCESSING
```

Quando `false`, o timer executa a análise direto no processo atual. Quando `true`, o timer apenas envia mensagens para filas e as functions de queue fazem o processamento.

Filas relevantes:

- `subscription-analysis`
- `subscription-analysis-production`
- `subscription-analysis-steps`
- `cost-by-resource-analysis`
- `cost-by-resource-starter`

## Relatórios

Arquivos principais:

- `Functions/ReportFunction.cs`
- `Services/RecommendationReportService.cs`
- `Services/HtmlReportBuilder.cs`
- `Services/CsvReportBuilder.cs`

O projeto gera relatórios HTML e CSV a partir das recomendações salvas. O README menciona que PDF foi substituído/simplificado por HTML/CSV, embora ainda exista dependência `iText7` no `.csproj`.

## Configurações principais

Variáveis importantes:

- `AzureWebJobsStorage`: storage usado pela Function e Blob.
- `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`.
- `ServiceBusConnection`: conexão do Service Bus.
- `AZURE_SUBSCRIPTION_ID`: subscription fallback.
- `AZURE_SUBSCRIPTION_IDS`: lista de subscriptions em alguns fluxos.
- `RESULTS_CONTAINER_NAME`: container para análises.
- `COST_STORAGE_CONTAINER`: container para custos.
- `COST_SUBSCRIPTIONS`: subscriptions usadas na coleta de custos.
- `CostAnalysisSchedule`: cron do timer de análise.
- `CostByServiceDailySchedule`: cron de custo por serviço.
- `ENABLE_QUEUE_PROCESSING`: ativa processamento por filas.
- `ENABLE_RAW_ANALYSIS_STORAGE`: salva payload bruto de análise.
- `LOG_ANALYTICS_WORKSPACE_ID`, `LOG_ANALYTICS_SHARED_KEY`, `LOG_ANALYTICS_LOG_TYPE`: integração opcional com Log Analytics.

## Como estudar o projeto

Sugestão de ordem:

1. Leia `README.md` para comandos e variáveis.
2. Leia `Program.cs` para entender quais serviços existem.
3. Leia `Models/CostAnalysisRequest.cs` e `Models/StandardAnalyzerContract.cs`.
4. Leia `Functions/HealthFunction.cs`, principalmente `analyze-costs`.
5. Leia `Application/CostAnalysisOrchestrator.cs`.
6. Leia um analyzer simples, como `UnattachedDiskAnalyzer.cs`.
7. Leia um analyzer com métricas, como `IdleVmAnalyzer.cs`.
8. Leia `AnalysisStorageService.cs` para entender onde os resultados ficam.
9. Leia `SubscriptionAnalysisStepFunction.cs` para entender o fluxo assíncrono.
10. Leia `GrafanaApiFunction.cs` para entender como os dados viram dashboard.

## Conceitos de programação usados

### Injeção de dependência

Os serviços são registrados no `Program.cs` e recebidos pelos construtores das classes.

Exemplo:

```csharp
public CostByServiceDailyTimerFunction(
    ICostManagementClient costManagementClient,
    ICostStorageRepository costStorageRepository,
    SubscriptionDiscoveryService subscriptionDiscoveryService,
    IConfiguration configuration,
    ILogger<CostByServiceDailyTimerFunction> logger)
```

Isso facilita testes e separa responsabilidades.

### Programação assíncrona

Quase todo acesso externo usa `async` e `await`, porque chamadas para Azure, Blob Storage, HTTP e Service Bus são operações de I/O.

### Separação por responsabilidade

As Functions recebem eventos e delegam trabalho.

Os Services integram com infraestrutura.

Os Analyzers contêm regras de negócio FinOps.

O Orchestrator coordena tudo.

### Resiliência

O projeto usa várias técnicas para lidar com falhas:

- retry em HTTP;
- tratamento de 429;
- Service Bus para reprocessamento;
- steps idempotentes;
- fallback de custo estimado quando Cost Management não retorna custo real;
- proteção para não sobrescrever resultado válido com vazio.

### Contrato padronizado

Os analyzers retornam um formato comum. Isso permite consolidar diferentes tipos de recomendação no mesmo storage, relatório e dashboard.

## Pontos de atenção

- Há bastante código com comentários de restauração, compatibilidade e funcionalidades antigas. Algumas partes parecem ter evoluído por iterações rápidas.
- Existem dois formatos de persistência: um mais novo e um legado. Isso aparece em `AnalysisStorageService` e `FinOpsResultAggregator`.
- O escopo `managementGroup` aparece nos modelos, mas a análise principal ainda é mais focada em subscription.
- O processamento direto pode sofrer timeout em subscriptions grandes; o fluxo por steps é o caminho mais robusto.
- Algumas funções HTTP usam `AuthorizationLevel.Anonymous`. Em produção, isso exige controle externo de segurança ou alteração do nível de autorização.
- Alguns logs usam `Console.WriteLine` misturado com `ILogger`. O ideal seria padronizar em `ILogger`.

## Resumo mental

Pense no projeto assim:

```text
Azure Functions recebem gatilhos
        |
        v
Orquestrador decide quais análises rodar
        |
        v
Analyzers consultam Resource Graph, Metrics e Cost Management
        |
        v
Findings são normalizados
        |
        v
Blob Storage guarda recommendations.json e dados de custo
        |
        v
APIs, relatórios e Grafana leem esses dados
```

Em outras palavras: a plataforma coleta dados da Azure, identifica oportunidades de economia e transforma isso em arquivos e APIs consumíveis por relatórios e dashboards.
