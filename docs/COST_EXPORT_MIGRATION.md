# Migração para Cost Management Exports

## Objetivo e escopo

Este documento registra a migração gradual da coleta de custos da Azure Cost Management Query API para Cost Management Exports entregues em Blob Storage.

A migração mantém dashboard, recomendações, `ResourceCostLookupService` e analyzers nas fontes atuais enquanto os Exports são validados. Anomalias já usam Export como fonte principal por subscription, inclusive em shadow, com Query API como fallback temporário.

O importador do export atual valida o contrato `Usage + MonthToDate + Daily`, compatível com subscriptions WebDirect. O nome legado `finops-actual-cost-current-billing` permanece somente para atualizar o mesmo recurso de forma idempotente.

O timer de importação roda a cada duas horas por padrão. Quando pelo menos um manifest novo é promovido, ele publica uma solicitação na fila existente de análise de anomalias; o timer diário da análise permanece como safety net.

Runs tardios de períodos anteriores continuam publicando snapshots históricos, mas não podem regredir o `maxUsageDate` do watermark. Assim, o custo atual usa o último dia consolidado, enquanto o acumulado do billing cycle soma os snapshots disponíveis desde o início oficial do ciclo, inclusive quando atravessa dois meses.

## Arquitetura atual

Existem dois produtores capazes de gravar os mesmos snapshots de custo:

1. Os coletores legados consultam a Cost Management Query API por subscription e gravam dados processados no container configurado por `COST_STORAGE_CONTAINER`.
2. O importador lê `manifest.json` e todas as partições CSV do container `COST_EXPORT_CONTAINER`, agrega os registros e grava no mesmo repositório de dados processados.

Os contratos persistidos oficiais que devem permanecer estáveis são:

```text
cost/byService/date=YYYY-MM-DD/subscriptionId=<id>/byService.json
cost/byResource/date=YYYY-MM-DD/subscriptionId=<id>/byResource.json
```

Os dois arquivos continuam sendo arrays JSON compatíveis com os modelos atuais e passam a alimentar as APIs genéricas de custo do portal. Metadados de origem e disponibilidade ficam nos watermarks e nos envelopes HTTP.

Antes do cutover, o importador grava cópias isoladas para comparação:

```text
cost/shadow/byService/date=YYYY-MM-DD/subscriptionId=<id>/byService.json
cost/shadow/byResource/date=YYYY-MM-DD/subscriptionId=<id>/byResource.json
```

O importador mantém artefatos operacionais separados:

```text
cost/imports/checkpoints/mode=<shadow|cutover>/subscriptionId=<id>/exportName=<export>/runId=<runId>.json
cost/imports/checkpoints/mode=<shadow|cutover>/subscriptionId=<id>/exportName=<export>/latest.json
cost/imports/watermarks/mode=<shadow|cutover>/subscriptionId=<id>/latest.json
cost/imports/locks/mode=<shadow|cutover>/subscriptionId=<id>.lock
cost/imports/cursors/manifests.json
```

## Semântica de `date=`

`date=YYYY-MM-DD` representa exclusivamente a `UsageDate` do registro de custo.

Para CSVs que contenham `UsageDateTime`, o importador interpreta o valor e usa sua componente de data. Cada data distinta encontrada em todas as partições do manifesto gera uma publicação independente. Um export Month-to-Date com UsageDates de 01 a 18 publica ou sobrescreve os dias 01 a 18; ele não publica uma pasta com a data em que a execução ocorreu.

As três datas relevantes não são intercambiáveis:

- Data de execução/processamento: instante em que Azure ou Function executou o trabalho. Serve para observabilidade.
- `submittedTime`: instante informado pelo manifesto, usado somente para desempatar runs com o mesmo `maxUsageDate`.
- `UsageDate`: dia ao qual o consumo/custo pertence. É a única fonte para o segmento `date=`.

O maior `UsageDate` publicado pode ser anterior à data de execução devido ao atraso de consolidação do Cost Management. O watermark registra `minUsageDate` e `maxUsageDate` para tornar esse atraso explícito.

## Fluxo Cost Management Export

O export suportado nesta fase é `finops-actual-cost-current-billing`, com:

- API de configuração `2025-03-01`;
- tipo `Usage`;
- timeframe `MonthToDate`;
- granularidade `Daily`;
- CSV particionado com `CompressionMode=None` ou `CompressionMode=gzip`, conforme o export;
- `OverwritePreviousReport`;
- manifesto com todas as partições da execução.

O importador:

1. descobre os prefixos `subscriptions/<id>/` quando `COST_EXPORT_IMPORT_ALL_SUBSCRIPTIONS=true`; caso contrário, usa somente `COST_EXPORT_SUBSCRIPTIONS`, sempre limitado por `COST_EXPORT_MAX_MANIFESTS_PER_RUN`;
2. valida o manifesto e os paths das partições;
3. ignora runs que já possuem checkpoint `Completed`;
4. processa todas as partições e valida contagens de arquivos e linhas;
5. agrega custos por UsageDate, serviço/recurso e moeda;
6. adquire Blob Lease por subscription e modo e revalida a ordem por `maxUsageDate`, usando `submittedTime`/`runId` apenas como desempate;
7. sob o lease, publica todas as UsageDates encontradas no path shadow ou oficial, grava o checkpoint imutável do run e promove `latest.json` e watermark do mesmo run;
8. libera o lease e persiste o cursor da última subscription visitada, iniciando a próxima execução na subscription seguinte.

Checkpoint e watermark registram origem, modo (`Shadow`/`Cutover`), export, run, `submittedTime`, min/max UsageDate, linhas, partições, totais por moeda e blobs publicados. Em retry, um checkpoint de run já existente não reprocessa CSVs: ele reconcilia `latest.json` e watermark. Checkpoints antigos continuam válidos e são interpretados como cutover porque foram produzidos antes da existência dos paths shadow.

No importador de Exports, `byService` inclui todas as linhas válidas e `byResource` inclui todo registro que possua `InstanceId`/`ResourceId`. `COST_RESOURCE_SERVICE` permanece restrito aos coletores legados e não reduz o dataset importado.

## Agendamento e Service Bus

O fluxo operacional atual usa `CostExportImportSchedule=0 0 */2 * * *`, portanto o
importador executa a cada duas horas em UTC. A execução lista os manifests disponíveis
e usa os checkpoints para não reler runs já concluídos. Quando
`ManifestsProcessed > 0` e a análise de anomalias está habilitada, o importador envia
uma solicitação com origem `cost-export-import` para `QUEUE_COST_ANOMALY_RUN`.

O Service Bus transporta somente mensagens de orquestração, não os manifests nem os
CSVs. O consumidor da fila cria a execução de anomalias e a divide em mensagens na
fila `QUEUE_COST_ANOMALY_SUBSCRIPTION`. O timer diário `CostAnomalyDailyTimer`
continua ativo como contingência caso nenhuma importação dispare a análise.

Event Grid ainda não faz parte da arquitetura atual. Uma evolução futura pode reagir
à criação de `manifest.json` no Blob Storage e enfileirar somente a subscription
afetada. Essa opção exige infraestrutura, filtro de eventos e idempotência adicionais;
para o volume atual, o timer de duas horas com checkpoints é o mecanismo adotado.

## Anomalias: Export como fonte principal

Para subscriptions com `EnableCostExportImport=true` e elegíveis por descoberta global ou `COST_EXPORT_SUBSCRIPTIONS`, a análise de anomalias lê primeiro os snapshots processados do Export. Antes do cutover, lê `cost/shadow/byService`; depois do cutover, lê o path oficial. O relatório derivado continua sendo persistido pelo `ICostAnomalyReportRepository`, normalmente no Cosmos DB, e os contratos HTTP da tela são preservados.

A Query API permanece como fallback temporário quando o watermark estiver ausente/incompleto, o histórico não cobrir a janela solicitada, `maxUsageDate` exceder a tolerância de defasagem, nenhum custo for encontrado ou ocorrer falha de leitura. `COST_EXPORT_ANOMALY_MAX_STALENESS_DAYS` controla essa tolerância e tem padrão de três dias. Quando a análise ignora o dia corrente, a validação termina no dia anterior para não consumir artificialmente um dia dessa tolerância. Logs e resultados registram `CostManagementExport` ou `CostManagementQueryApi`; a API de anomalias também expõe o motivo do fallback e moedas diferentes nunca são somadas.

O `CostExportImportTimer` apenas importa manifests já entregues no Blob; ele não solicita ao Azure a criação de um novo run. Para manter Export como fonte predominante, cada export de origem deve estar ativo com recorrência diária e continuar publicando no container/prefixo esperado. Aumentar a tolerância reduz chamadas à Query API, mas aceita dados mais antigos e não corrige um export parado; por isso deve ser apenas uma medida temporária e monitorada.

## Fluxo legado da Query API

O fluxo legado permanece no código apenas para rollback controlado e fica desabilitado por padrão:

- byService consulta a Query API e grava `byService.json` e, quando disponível, `raw.json`;
- byResource usa timer, Service Bus starter e uma mensagem por subscription antes de gravar `byResource.json`;
- as APIs manuais de coleta foram retiradas; o rollback usa os coletores agendados;
- os demais consumidores da Query API permanecem inalterados nesta fase.

As flags independentes são:

```text
EnableLegacyCostByServiceCollection=false
EnableLegacyCostByResourceCollection=false
```

Reativar uma dessas flags volta a habilitar somente o coletor correspondente. As filas sem fluxo ativo `storage-analysis`, `vm-analysis`, `appservice-analysis` e `analysis-results` foram removidas da infraestrutura; o processamento ativo continua nas filas de subscriptions, etapas e anomalias.

As APIs operacionais antigas `analyze-costs`, `collect-costs`, `analysis-status`, `trigger-analysis`, `force-consolidate`, `ManualCostAnalysis`, `debug/loganalytics`, `finops/data`, `AnalyzeDuplicateResources` e `GET/PUT/PATCH /api/ownership` foram removidas após 30 dias sem chamadas. O health check `/api/health` e as APIs autenticadas atuais do portal permanecem ativos.

Em 09/09/2026, o segundo lote retirou nove APIs sem consumidores no portal: `CostByServiceManualRun`, `CostByResourceManualRun`, `ManualDailySummary`, `ManualReportGeneration` (`POST /api/generate-reports`), `GetTeam`, `UpsertTeam`, `DeleteTeam`, `ListTeamSubscriptions` e `BillingPeriodCurrentGet`.

Os timers `DailySummary` e `PreGeneratedReportTimer` continuam gerando resumos e relatórios. `GET /api/teams` mantém o contrato usado pelos filtros do portal, e o cálculo interno de billing period e seus overrides permanecem disponíveis. A tela de ownership continua usando `/api/ownership/teams`; essa configuração não é uma migração automática do Blob legado de times.

Este lote requer deploy do backend e do portal (remoção do cliente HTTP sem consumidores), sem alteração Terraform. Após o deploy, verificar a ausência das nove Functions no inventário publicado, a presença de `ListTeams`, dos timers e dos overrides de billing, além do carregamento dos filtros de times e das ações de ownership no portal. As rotas removidas deixam de ser atendidas pela Function App. O código anterior permanece recuperável pelo histórico Git.

Ingestão e cutover são decisões independentes:

- `COST_EXPORT_IMPORT_ALL_SUBSCRIPTIONS=true` descobre automaticamente as subscriptions pelos prefixos existentes no container, sem listar GUIDs no App Setting;
- `COST_EXPORT_SUBSCRIPTIONS` é a allowlist usada quando a descoberta global está desabilitada. Lista vazia continua significando nenhuma subscription nesse modo;
- `COST_EXPORT_CUTOVER_SUBSCRIPTIONS` controla exclusivamente quais subscriptions deixam os escritores legados e passam a receber publicação oficial.

O cutover somente é efetivo quando `EnableCostExportImport=true`, a subscription é elegível pela descoberta global ou allowlist e também pertence à lista de cutover. Com descoberta global desabilitada, o Terraform exige que o cutover seja subconjunto da allowlist. Somente subscriptions que atendem a essas condições são excluídas de timers, starters e workers legados. Uma allowlist de cutover vazia significa **nenhuma subscription em cutover**, nunca todas. O worker também ignora mensagens quando o coletor legado está desabilitado.

Com `EnableCostExportImport=false`, nenhuma subscription é excluída do legado. Mesmo com importação e descoberta global habilitadas, cutover vazio mantém todos os escritores legados e isola a publicação em shadow.

O script de inicialização usa `Usage + MonthToDate + Daily` por padrão para o export principal. `ActualCost` e `AmortizedCost` somente são usados quando solicitados explicitamente por `-ExportType`; o importador atual aceita o contrato `Usage + MonthToDate`. O export opcional habilitado por `-IncludeLastBillingPeriod` continua usando `TheLastBillingMonth`.

## Storage dos Exports

Na infraestrutura atual, `AzureWebJobsStorage`, o container de resultados e o container de Exports pertencem à mesma Storage Account Terraform. Nesse cenário, o importador reutiliza o `BlobServiceClient` atual.

Para um Storage Account central diferente, pode-se configurar:

```text
COST_EXPORT_STORAGE_ACCOUNT_URI=https://<account>.blob.core.windows.net
```

Essa opção usa `DefaultAzureCredential`/Managed Identity e tem precedência. Para desenvolvimento local existe o fallback opcional:

```text
COST_EXPORT_STORAGE_CONNECTION_STRING=<connection-string-local>
```

Se nenhuma das duas opções estiver definida, o importador reutiliza `AzureWebJobsStorage`. A connection string independente é destinada apenas à compatibilidade local; ambientes Azure devem usar a URI e RBAC mínimo no container de Exports.

## Shadow mode

O shadow mode é o modo padrão de uma subscription ingerida que ainda não entrou em cutover:

1. Os paths oficiais continuam sendo produzidos pelo legado; anomalias podem ler o shadow como fonte principal com fallback para Query API.
2. O Export publica exclusivamente sob `cost/shadow/` e não sobrescreve os snapshots oficiais.
3. As duas fontes são comparadas por subscription, UsageDate, moeda, serviço e recurso.
4. Checkpoints e watermarks shadow ficam separados dos controles de cutover.
5. As APIs de snapshot continuam sem ler shadow. As exceções intencionais são a leitura Export-first de anomalias e a tendência histórica por assinatura (`/api/cost/trend-by-service`), que combina revisões shadow/cutover com o histórico oficial sem consultar o Azure durante o acesso.

As comparações devem incluir total diário, total acumulado do ciclo, subscriptions ausentes, maior UsageDate disponível, moedas diferentes, serviços sem correspondência e recursos sem `ResourceId`.

## Rollout por descoberta automática e cutover explícito

Como todas as subscriptions já possuem export, a ingestão pode descobrir os prefixos automaticamente, mantendo o cutover gradual:

1. habilitar `EnableCostExportImport=true` e `COST_EXPORT_IMPORT_ALL_SUBSCRIPTIONS=true`, mantendo cutover vazio;
2. o importador descobre somente subscriptions que já possuem prefixo no container e publica em shadow;
   - para cada subscription, os manifests são priorizados pelo fim do período no path e depois por `LastModified`, evitando que um fechamento tardio do mês anterior deixe o watermark preso em runs antigos;
3. validar snapshots, checkpoints, watermarks, atualidade e fallback por subscription;
4. adicionar subscriptions validadas a `COST_EXPORT_CUTOVER_SUBSCRIPTIONS` em lotes;
5. confirmar publicação oficial e ausência de colisão com timers e mensagens legadas;
6. após todas as subscriptions estarem estáveis em cutover, considerar desabilitar globalmente os coletores legados.

Não se deve habilitar uma subscription na rota de Export antes de existir entrega válida e recorrente no Storage central.

Para ativar a recorrência diária e solicitar o preenchimento do período atual e do último billing period, execute o bootstrap pelo operador responsável, substituindo os parâmetros do ambiente:

```powershell
.\scripts\Initialize-CostExports.ps1 `
  -StorageSubscriptionId "<finops-subscription-id>" `
  -StorageResourceGroup "<finops-resource-group>" `
  -StorageAccountName "<finops-storage-account>" `
  -AllSubscriptions `
  -ManagementGroupId "<management-group-id>" `
  -IncludeLastBillingPeriod `
  -Activate `
  -Force `
  -RunNow
```

O comando altera recursos Azure e deve ser executado somente após a validação dos parâmetros e permissões; não faz parte do build nem do Terraform local.

## Critérios futuros de fallback

Quando consumidores começarem a preferir Exports, o fallback para a Query API deve ser decidido por subscription, nunca globalmente. O Export só será elegível quando:

- existir watermark com `source=CostManagementExport` e `status=Completed`;
- existir checkpoint concluído para o mesmo `runId`;
- `maxUsageDate` atender à tolerância de atraso definida;
- todos os blobs declarados como publicados existirem e forem desserializáveis;
- a moeda for compatível com a agregação solicitada;
- a subscription for elegível por descoberta automática ou pela allowlist ativa.

Ausência, stale data, checkpoint incompleto, falha de leitura, moeda incompatível ou divergência acima do limite devem acionar fallback somente para a subscription afetada. Resultados de Export e Query API da mesma subscription/data não podem ser somados.

Quando o `minUsageDate` do manifest mais recente é posterior ao início da janela solicitada — situação comum na virada do mês com `MonthToDate` — o leitor também consulta os snapshots diários anteriormente persistidos. Lacunas não invalidam os demais dias, e o custo acumulado do billing cycle é calculado mesmo quando o baseline da análise ainda for classificado como insuficiente.

## Contratos HTTP preservados

Esta fase não altera rotas, autenticação, status HTTP ou payloads de:

- `/api/dashboard/summary`;
- `/api/dashboard/teams`;
- `/api/dashboard/databases`;
- `/api/cost/health`;
- `/api/cost/by-service`;
- `/api/cost/trend-by-service`;
- `/api/cost/by-resource`;
- `/api/cost/trend-by-resource`;
- APIs de anomalias, recomendações, relatórios e diagnóstico de custos.

`byService.json` mantém seu contrato. `byResource.json` recebe apenas campos aditivos de dimensão do Export; `dataAsOf` e `source` ficam no envelope HTTP e não nos arrays persistidos.

## APIs genéricas de custo do portal

As rotas antigas específicas de ferramenta foram removidas por não possuírem consumidores ativos. As APIs de leitura exigem Bearer token e role `FinOps.Reader` ou `FinOps.Admin`:

```text
GET /api/cost/health
GET /api/cost/by-service
GET /api/cost/trend-by-service
GET /api/cost/by-resource
GET /api/cost/trend-by-resource
```

Filtros suportados incluem `date` ou `from`/`to`, `subscription`, `service`, `resource`, `resourceGroup`, `resourceType`, `location` e `currency`, conforme o endpoint. `service` é opcional; o importador não aplica `COST_RESOURCE_SERVICE` e preserva todos os recursos com `InstanceId`/`ResourceId`.

As respostas usam envelope com `dataAsOf`, `source`, `items` e, nos snapshots, `allocation`. Em `/api/cost/trend-by-service`, quando há filtro por subscription, a leitura histórica é local e diária: primeiro o snapshot versionado do Export (priorizando o modo atual e consultando shadow/cutover para preservar o histórico), depois o snapshot oficial legado anteriormente persistido pela Query API e, se ambos estiverem ausentes, uma lacuna. A resposta também expõe `sourceByDate`, `availableDates` e `missingDates`; `dataAsOf` é a última data realmente disponível e `source` é `Mixed` quando o período combina fontes. Os demais endpoints resolvem `dataAsOf` e `source` pelo watermark de cutover; dados legados sem watermark são identificados como `CostManagementQueryApi`. Agregações sempre mantêm moedas separadas.

O importador preserva `ResourceGroup`, `ResourceLocation`, `ResourceType`, `InstanceId`/`ResourceId`, `ServiceName`, `Currency` e `Tags`; `PreTaxCost` é agregado no campo contratual `TotalCost`. Para cada UsageDate e moeda também publica:

```text
cost/allocation/date=YYYY-MM-DD/subscriptionId=<id>/allocation.json
cost/shadow/allocation/date=YYYY-MM-DD/subscriptionId=<id>/allocation.json
```

Cada registro contém `totalCost`, `allocatedResourceCost` e `unallocatedCost`, mantendo a identidade `totalCost = allocatedResourceCost + unallocatedCost`.

## Fora do escopo desta fase

- migrar dashboard ou seus summaries;
- migrar anomalias e projeção mensal;
- migrar recomendações;
- substituir `ResourceCostLookupService`;
- alterar analyzers;
- remover a Query API;
- executar Terraform, deploy ou qualquer operação no Azure.

## Configuração e Terraform desta fase

As configurações novas ou formalizadas são:

```text
COST_EXPORT_CUTOVER_SUBSCRIPTIONS=
COST_EXPORT_IMPORT_ALL_SUBSCRIPTIONS=true
COST_EXPORT_SUBSCRIPTIONS=
COST_EXPORT_MAX_MANIFESTS_PER_RUN=100
COST_EXPORT_ANOMALY_MAX_STALENESS_DAYS=3
COST_EXPORT_STORAGE_ACCOUNT_URI=
EnableLegacyCostByServiceCollection=false
EnableLegacyCostByResourceCollection=false
```

`COST_EXPORT_STORAGE_ACCOUNT_URI` só é necessário quando o Storage central difere do `AzureWebJobsStorage`; em Azure usa `DefaultAzureCredential`/Managed Identity. A connection string separada permanece apenas como compatibilidade local.

Os arquivos Terraform alterados nesta fase são:

- `pipelines/terraform-function/inputs.tf`;
- `pipelines/terraform-function/azurerm_function_app.tf`;
- `pipelines/terraform-function/terraform.tfvars.example`.

Os defaults usam `enable_cost_export_import=true` e `cost_export_import_all_subscriptions=true`, com `cost_export_subscription_ids=[]`, `cost_export_cutover_subscription_ids=[]` e os dois coletores legados desabilitados. Isso mantém a ingestão e a leitura de anomalias baseadas em Export para os prefixos descobertos; as flags legadas permanecem disponíveis somente para rollback. As preconditions fazem o plan falhar se houver cutover com importação desligada e, quando import-all estiver desabilitado, se o cutover não for subconjunto da allowlist.

Ao executar `Initialize-CostExports.ps1 -AllSubscriptions`, subscriptions em estado diferente de `Enabled` são inelegíveis e ignoradas sem entrar em `Failures`. O resumo apresenta habilitadas processadas, inelegíveis ignoradas, inacessíveis/outro tenant e falhas reais. Para `-SubscriptionIds`, o comportamento é estrito: uma subscription explicitamente solicitada e desativada continua sendo falha.
