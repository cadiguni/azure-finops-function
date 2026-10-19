# Confiabilidade financeira — evolução incremental

## Escopo implementado

Esta entrega consolida a origem financeira usada por Dashboard, Recomendações e relatórios. Não altera infraestrutura, filas, App Settings ou agendamentos.

### Resumos do dashboard

`SummaryGenerationService` usa exclusivamente o relatório de anomalias da data solicitada para seus totais financeiros. Na ausência desse relatório, não reutiliza silenciosamente o último relatório de outra data e não substitui custos por estimativas de recomendações.

Os mesmos critérios de agregação são aplicados aos resumos global, de anomalias, de times e de assinaturas:

- `Consolidated` e `InsufficientData` permitem exibir os valores observados. Histórico insuficiente para detectar anomalias não equivale a ausência de custo.
- `QueryFailed` e `NoCostData` não contribuem com valores, mesmo se o documento contiver algum número residual.
- Zero e créditos negativos são preservados. Sem valores disponíveis, `totalCost` e `monthlyProjection` retornam `null` no HTTP.
- Assinaturas presentes no relatório entram mesmo sem recomendações. Custos sem time entram em `Sem atribuição`.
- Assinaturas conhecidas pelas recomendações, mas ausentes no relatório, são contadas como ausentes. A contagem global também considera `TotalSubscriptionsAnalyzed`; não é uma auditoria do inventário Azure inteiro.
- Moedas divergentes identificadas no relatório e em suas linhas impedem a soma escalar; não há conversão cambial implícita.
- Os valores são a soma dos **ciclos de faturamento de cada assinatura**, não o mês-calendário. `periodFrom` e `periodTo` só representam limites comuns quando o grupo possui um único intervalo; em ciclos distintos ficam vazios. `periodTo` é inclusivo.

### Contrato `financialSummary`

| Campo | Significado |
| --- | --- |
| `totalCost`, `monthlyProjection` | Valores disponíveis; `null` sem dados agregáveis |
| `currency` | Moeda informada pelo relatório/linhas; `null` se ambígua |
| `availability` | `Available`, `Partial`, `Missing`, `Failed` ou `MixedCurrency` |
| `finality` | `Provisional`; disponibilidade não comprova fechamento/reconciliação |
| `periodKind` | `SubscriptionBillingCycles` |
| `reportDate`, `reportGeneratedAt` | Data solicitada e revisão do relatório de anomalias consumido |
| `earliestDataAsOf`, `latestDataAsOf` | Menor/maior `AnalysisDate` entre assinaturas com custo; não comprovam cobertura contínua |
| `expectedSubscriptions`, `availableSubscriptions`, `failedSubscriptions`, `missingSubscriptions` | Cobertura do escopo conhecido |
| `sources` | Fontes informadas pelas assinaturas com valores |

`reportGeneratedAt` não é timestamp de importação nem versão imutável do CSV. Os campos numéricos antigos dos documentos persistidos permanecem por compatibilidade; as APIs do dashboard usam os valores nullable de `financialSummary`. Documentos legados sem esse objeto não têm seus totais apresentados como verificados.

Na leitura, um snapshot inteiramente sem dados ou com falhas permite buscar o snapshot anterior dentro da janela existente de sete dias, mantendo sua data. Times e bancos seguem a data resolvida pelo resumo global. A tela omite o custo de um time quando a revisão do relatório difere da revisão global. Isso não cria transação atômica entre todos os documentos.

### SQL e Cosmos DB

Os analyzers passam a gerar `SqlDatabaseCostReview` e `CosmosDbCostReview`: sugestões de investigação, não afirmações de subutilização.

- Removidas as estimativas fixas de preço por SKU/tipo/free tier e os percentuais fixos de economia de 20%/18%.
- Custo ausente não é inventado; custo zero associado é preservado.
- Findings registram `evidenceStatus=InsufficientMetrics`, `savingsStatus=NotEstimated` e `costAvailability` no metadata.
- O campo numérico de economia continua zero por compatibilidade do contrato. Para os novos tipos, o portal mostra **Economia não estimada**.
- As ações pedem métricas e contexto antes de dimensionamento, distinguindo investigação de consumo serverless de avaliação de capacidade provisionada/autoscale.
- Custos associados em recomendações podem pertencer ao recurso pai ou a um escopo compartilhado. O dashboard identifica a visão como **custo observado associado aos recursos analisados**, sem afirmar que seja o custo total do inventário.

## Segunda entrega: custos por recurso Export-first

`ICostDataReader` / `ExportFirstCostDataReader` agora também oferecem `ReadResourceCostsAsync`. O `ResourceCostLookupService`, usado pelos analisadores, deixa de carregar custos diretamente da Query API e utiliza essa leitura comum.

- Reutiliza os allowlists, `EnableCostExportImport`, modo shadow/cutover e `COST_EXPORT_ANOMALY_MAX_STALENESS_DAYS` existentes. Sem novas configurações ou APIs HTTP.
- Lê snapshots diários `byResource` no modo correto, incluindo histórico anterior ao início do manifest mais recente. Não completa dias ausentes com zero.
- Export ausente, defasado, vazio ou com erro leva a uma consulta diagnóstica em lote na Query API. A data observada dos snapshots também é verificada: watermark recente sozinho não comprova dados recentes por recurso.
- A ausência de um recurso no lote não dispara uma nova consulta por recurso. Zero/créditos válidos não acionam fallback. Moedas incompatíveis não são somadas.
- Cancelamento é propagado, sem virar fallback ou cache de falha.
- Cache por assinatura durante a execução, compartilhado pelos analisadores nesse escopo. O serviço passou de singleton para scoped para não capturar reader/repository scoped. Mantém expiração de 30 minutos, cache curto de falha/ausência e invalidação na mudança do dia; não compartilha cache entre invocações independentes.
- A descoberta de dimensões permanece disponível para diagnóstico explícito, mas saiu do pré-carregamento dos analisadores.
- Média diária do match considera todos os dias observados, incluindo zero e créditos. Dias sem linha não são presumidos como custo zero. A projeção mensal continua identificada como estimativa.
- Corrigida a leitura de `periodId=yyyy-MM`: é um identificador de mês, sem conversão de fuso que possa levá-lo ao mês anterior.
- Quando o dia corrente é ignorado, a janela pedida ao reader também termina no dia anterior. Assim, o dia ainda provisório não consome indevidamente um dia da tolerância de defasagem e não provoca fallback prematuro para a Query API.

### Proveniência e custo observado

O lookup retorna `ObservedCost` separado de `MonthlyCost` (projeção), além de `Provenance`. SQL, Cosmos, VM, disco, App Service, Function App, Storage e Log Analytics propagam esses campos nos findings e snapshots de recomendações. As respostas de listagem/detalhe e o diagnóstico de match expõem `observedCost` e `costProvenance` de forma aditiva; snapshots antigos continuam sem esses campos.

`costProvenance` contém `source`, `costBasis`, `datasetRevision`, `dailyRevisions`, `dataAsOf`, `importCompletedAt`, `exportRunId`, `publicationMode`, `usedFallback`, `fallbackReason`, período solicitado e `availableDates`. Cada dia versionado aponta para um snapshot imutável. Durante a transição, o reader combina os limites do índice com o watermark legado; datas ainda existentes apenas no layout anterior são lidas e marcadas como `legacy-unversioned`, mesmo quando estiverem antes ou depois da parte já indexada.

`observedCost` é o custo do match, que pode ser do recurso pai ou de um escopo compartilhado. A contabilização usa uma chave de grupo para somar pool/plano/conta uma única vez; recomendações sobrepostas mantêm a evidência, mas somente uma delas recebe a economia contabilizada.

Linhas sem associação ou apenas aproximadas não anulam os custos diretamente associados do restante do relatório. Assinaturas com ciclos de faturamento diferentes também podem compor o total observado associado, desde que cada assinatura possua período, moeda e revisão internamente consistentes. O HTML e o CSV informam separadamente o custo financeiro dos ciclos e o custo observado associado às recomendações.

A economia é contabilizada na moeda do snapshot financeiro. Uma recomendação em outra moeda não zera as recomendações válidas do relatório; ela fica fora daquele total. A interface e os relatórios distinguem `Quantified`, `NotEstimated` e `ExcludedFromTotal`, evitando apresentar ausência de estimativa como economia real de zero.

As inferências legadas por SKU e a divisão por `0,7` foram removidas. Sem custo associado, `observedCost` permanece `null`, a interface mostra indisponibilidade e a economia financeira contabilizada é zero. Dashboard, Recomendações, HTML e CSV usam a mesma data resolvida, período e `snapshotRevision`.

### Revisões retroativas e tombstones

O índice diário escolhe a revisão por `SubmittedTime` e usa `RunId` como desempate, sem regredir o dia mais recente. Runs `OverwritePreviousReport` com janela declarada são autoritativos: se uma data que existia antes deixa de aparecer, o importador publica um tombstone versionado e limpa os espelhos legados daquele dia. Isso distingue custo zero/correção removida de ausência de arquivo e impede a restauração acidental de valores antigos.

Publicar o backend e o portal para esta segunda entrega e executar/aguardar uma nova análise de recursos. Sem Terraform, migração de dados ou reescrita automática de históricos.

Ao terminar uma execução enfileirada de anomalias, a Function agora regenera os summaries e o snapshot imutável de relatórios para a mesma data. O manifesto registra `summaryGeneratedAt`; se a geração falhar depois de a análise ser concluída, uma nova entrega da última mensagem tenta novamente. O `DashboardSummaryTimer` diário permanece como contingência.

## Implantação e verificação da primeira entrega

1. Publicar backend e portal pelos pipelines existentes; não há `terraform apply` nesta entrega. Durante versões desencontradas, custos sem o novo contrato podem aparecer como indisponíveis.
2. Aguardar/concluir uma análise de anomalias para a data desejada. A conclusão passa a gerar o snapshot financeiro automaticamente; o `DashboardSummaryTimer` continua disponível como contingência. Um resumo antigo não é migrado só por abrir o portal.
3. Executar/aguardar a análise de recursos para produzir os novos tipos de investigação SQL/Cosmos. A análise de anomalias de custo não substitui essa execução.
4. Conferir data do snapshot, revisão do relatório, cobertura e fontes. Em um relatório de moeda única, soma dos times (incluindo sem atribuição) e soma das assinaturas devem preservar o total global disponível.
5. Conferir zero, ausência e falha separadamente; comparações precisam usar o mesmo relatório e os mesmos ciclos.

Não foram apagados snapshots, recomendações ou históricos. Os novos tipos de investigação possuem identidade diferente dos tipos antigos de subutilização; decisões antigas não são migradas automaticamente. Relatórios pré-gerados históricos também não são reescritos por esta entrega.

## Próximos incrementos

- Derivar uma visão completa de custo de SQL/Cosmos a partir do inventário financeiro, além do subconjunto presente nas recomendações.
- Implementar referências aprovadas, gráfico diário, métricas técnicas e fluxo de validação da economia realizada.
- Planejar a retirada do fallback legado somente depois de todos os índices diários estarem completos e observados em produção.

## Validação local

- Build .NET e suíte completa de testes unitários.
- `npm run lint` e `npm run build` do portal. Os avisos preexistentes de hooks e tamanho do bundle permanecem não bloqueantes.
