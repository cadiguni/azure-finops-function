# Governança: saúde dos dados, orçamento e previsão

Revisão e validação local: 2026-09-30. Esta entrega não aplica infraestrutura, não amplia permissões e não configura serviços externos de alerta. A integração de DI descrita abaixo deve ser feita pelo responsável pela composição da aplicação; o arquivo de composição foi preservado nesta tarefa.

## Funcionalidades

### Saúde dos dados

- `GET /api/governance/data-health?subscriptionId=<GUID>&from=yyyy-MM-dd&to=yyyy-MM-dd`.
- Uma subscription por consulta; janela de 1–31 dias. Sem datas: 14 dias até UTC menos `COST_DATA_DELAY_DAYS`.
- Lê índice versionado e watermark de publicação, sem varredura do tenant, consulta ARM ou fallback para custo legado.
- `coveragePercent`, `availableDates` e `missingDates` medem **referências no índice**, não integridade dos snapshots nem sucesso dos coletores.
- `status`: `unknown` sem índice, `unavailable` quando uma fonte falha, `degraded` quando a última publicação não está Completed, `incomplete` para gaps/defasagem, `covered` para janela referenciada. Não é um status geral de saúde da plataforma.
- Última importação/publicação e última data de uso são evidências financeiras. **Não equivalem à última execução de um timer**.
- `queueStatus = unavailable` e `timerStatus = unavailable`: não há consulta real de backlog, dead-letter, execução ou atraso de timers. `lastCollectorRunAt` e `historicalFailureCount` permanecem `null`. A UI exibe a indisponibilidade; nenhum zero, execução ou estado saudável é fabricado.

#### Visão consolidada (admin)

- `GET /api/governance/data-health/overview` — **somente `FinOps.Admin`** (Reader recebe 403), pois avalia todas as subscriptions conhecidas.
- Lista de subscriptions: último relatório de anomalias de custo (com nomes); sem relatório, `COST_EXPORT_SUBSCRIPTIONS`. Sem varredura do tenant/ARM.
- Janela fixa de 14 dias até UTC menos `COST_DATA_DELAY_DAYS`. Cada linha reutiliza a avaliação por subscription acima (índice + watermark), com até 8 leituras paralelas e limite de 300 subscriptions. Falha inesperada numa subscription vira linha `unavailable`, sem derrubar a visão.
- Portal: tela "Saúde dos dados · Admin" mostra a tabela automaticamente (problemas primeiro, filtro por status) e carrega os dias faltantes de uma subscription sob demanda pelo endpoint individual.

### Orçamento e projeção

> Situação no portal: a tela **Orçamentos saiu do menu** até existir demanda por orçamento por time com alertas reais. Rota (`/budgets`) e APIs continuam disponíveis por URL direta.

| Operação | Endpoint | Papel |
|---|---|---|
| Listar definições | `GET /api/governance/budgets` | Reader ou Admin |
| Avaliar consumo/projeção | `GET /api/governance/budgets/{id}` | Reader ou Admin |
| Criar/atualizar | `PUT /api/governance/budgets/{id}` | Admin |
| Excluir | `DELETE /api/governance/budgets/{id}?version=<ETag codificado>` | Admin |

- Orçamento **local ao portal**, não um recurso Azure Cost Management Budget. Nenhuma chamada cria/edita/exclui orçamento ou alerta Azure.
- Mês civil UTC, uma subscription e uma moeda; opcionalmente um centro de custo do snapshot importado. Nome até 120 caracteres, centro até 200, mês entre 2000-01 e 2100-12, valor positivo até 1 trilhão e até duas casas decimais.
- Consumo usa `ActualCost` versionado e confere subscription, modo, revisão e data do snapshot. Moedas distintas nunca são somadas ou convertidas. Um orçamento BRL ignora valores USD, e a ausência de BRL não é zero.
- Centro de custo usa `CostCenter` ou a tag importada, via parser existente. Não aplica rateio manual/ownership. Atribuição incompleta torna a projeção não confiável; ausência de correspondência só é zero quando toda a atribuição fecha com os totais de serviço. Tombstone só comprova zero para sua moeda explícita.
- Estimativa linear = consumo observado / dias observados × dias do mês. Exibe gaps e data de referência; menos de sete dias, cobertura/atribuição incompleta ou Shadow tornam `forecastReliable = false`. Mesmo com base completa não há garantia de gasto futuro.
- **50/80/100% são indicadores calculados sob demanda e mostrados na UI**, não alertas despachados. `notificationDelivery = notConfigured`. Não há timer de verificação, deduplicação de alertas, email, webhook, Action Group, fila ou integração externa de envio.
- Criar, atualizar e excluir exigem confirmação explícita na UI com resumo do escopo, período e valor. Cancelar não envia a escrita. Essa confirmação não substitui a autorização no backend e não é prova criptográfica de consentimento de clientes externos.

## Autorização, entrada e cache

- Reutiliza `ICurrentUserService`/`AuthorizationService`, Bearer validado e `FinOps.Reader`/`FinOps.Admin`. `AuthorizationLevel.Anonymous` não dispensa o token.
- Escritas exigem `CurrentUserContext.UserId` não vazio, obtido pelo serviço existente de `oid`, NameIdentifier ou `sub` do token validado. Não há fallback para UPN, nome, `unknown` ou ator enviado no JSON. Admin sem identidade estável recebe 403.
- JSON de PUT limitado a **8192 bytes**, lendo no máximo 8193 bytes, independentemente de Content-Length. Excesso: 413. Profundidade máxima 8; objeto raiz obrigatório; números JSON estritos; campos desconhecidos, propriedades duplicadas (inclusive diferença de caixa), comentários/trailing commas e JSON inválido são rejeitados com 400. `teamId` não é aceito silenciosamente.
- IDs são GUIDs; versões são ETags entre aspas, ASCII sem controles/aspas internas, até 128 caracteres. `version = null` cria com `If-None-Match: *`; atualizar/excluir exige `If-Match` exato. Não há exclusão incondicional. Conflito: 409; orçamento ausente: 404.
- Todas as respostas produzidas pelos endpoints, incluindo 401/403/400/404/409/413/503, usam `Cache-Control: no-store`. JSON de resposta mantém camelCase.

## Auditoria durável e concorrência

### Por que um repositório especializado

[GovernanceAuditLogRepository](../src/Personal.FinOpsApi.AzureFunctions/Services/GovernanceAuditLogRepository.cs) reutiliza `IAuditLogRepository` e `AuditLogDocument`, expostos por `IGovernanceAuditLogRepository` para evitar o binding acidental ao provider compartilhado.

O [provider Blob compartilhado](../src/Personal.FinOpsApi.AzureFunctions/Services/Repositories/Blob/BlobAuditLogRepository.cs) lê e regrava uma lista completa sem CAS: pode perder appends concorrentes e faz I/O crescente. Não foi alterado fora do escopo. O provider Cosmos existente cria itens independentes, mas não oferece transação conjunta com os orçamentos Blob. Não se adicionou dependência obrigatória de Cosmos nem permissão nova.

Na governança cada evento é um blob independente em `GovernanceAuditBlobPrefix/<budgetId>/<reverseUtcTicks>-<eventId>.json`, criado com `If-None-Match: *`. Não há read-modify-write de lista, overwrite de evento, criação de container ou exclusão de auditoria. A leitura por entidade limita o retorno a 100 eventos, com paginação do SDK e ordenação de chave do mais recente para o mais antigo. O histórico não é exposto por um novo endpoint nesta entrega.

### Protocolo de escrita

1. Autenticar/autorizar e validar entrada. Payload inválido/sem autorização não altera orçamento nem gera evento financeiro.
2. Gerar `operationId` e persistir `Intent` **antes de qualquer mutação**. Registra ação Create/Update/Delete, ID do orçamento, `PerformedBy` com ID do token, horário UTC, parâmetros normalizados, ETag esperado e horário solicitado. Não registra bearer token, secrets ou texto bruto de erro do provider.
3. Em update/delete, ler a definição anterior e conferir ETag. A mutação ainda usa CAS no storage: a leitura não elimina a proteção contra mudança entre read e write.
4. Persistir resultado em outro evento correlacionado: `Succeeded` com definição anterior (update/delete) e posterior (create/update); delete deixa evento e definição anterior duráveis mesmo após remover o orçamento. 404/409/412 produzem `Rejected`; timeout/cancelamento/outros resultados não comprovados produzem `Indeterminate`, com tipo do erro e status do storage quando disponível.
5. O evento terminal usa token independente da requisição, com timeout de 10 segundos, para que um cliente desconectado não impeça a tentativa de registrar o resultado. Não há retry manual da mutação nem rollback que sobrescreva outra sessão.

### Semântica de falha e reconciliação

**Não há transação atômica entre orçamento e auditoria.** Não se promete exactly-once ou histórico WORM resistente a administradores do storage.

| Situação | Comportamento |
|---|---|
| Intenção não confirmada pelo storage | Fail-closed: nenhuma mutação tentada; 503 com `applied=false` e `operationId`. A intenção pode ter sido persistida se houve perda da resposta. |
| Mutação confirmada, auditoria terminal falha | 503 com `applied=true` e `operationId`; intenção permanece durável, sem falso sucesso nem tentativa de rollback. |
| Resultado da mutação desconhecido | Evento `Indeterminate` quando possível; 503 com `applied=null` e `operationId`. |
| Falha ao auditar rejeição | 503, sem mascarar a falha de auditoria como simples conflito. |
| Processo encerra após a intenção | Pode restar somente `Intent`; **resultado pendente/indeterminado**, nunca inferido como sucesso ou falha. |

`AuditIncomplete` identifica respostas 503 de confirmação incompleta; uma falha de rede pode impedir até essa resposta de chegar ao cliente. Antes de repetir, recarregar a lista/definição, inspecionar os eventos pelo orçamento e `operationId` e reconciliar versões/resultados. Não inferir o autor de uma exclusão apenas pela ausência do blob ou por `updatedBy`. Se houver escritas posteriores e evidência insuficiente, manter o resultado como indeterminado, sem inventar um resultado terminal. Não foi criado dispatcher/reconciliador automático nem política de retenção.

## Limites de I/O

- Saúde: uma leitura de índice e uma de watermark; até 31 datas calculadas.
- Avaliação: um orçamento, até um índice e até 31 snapshots sequenciais de uma subscription; mês futuro não lê custos. Não avalia todos os orçamentos no endpoint de listagem.
- Listagem: até 100 entradas varridas e até 100 downloads de orçamento; a 101ª entrada gera erro explícito, sem retornar inventário truncado. Entradas não JSON e documentos removidos durante a varredura também contam para o limite. Página solicitada ao SDK: 101.
- Mutação: dois appends de auditoria e uma escrita/exclusão, mais uma leitura anterior em update/delete. O limite de 100 da lista **não é quota transacional de criação**: mais registros podem existir e impedir a listagem; paginação de inventário fica pendente.
- Esses limites controlam o número de operações. O tamanho dos índices/snapshots já existentes é responsabilidade dos readers financeiros compartilhados; esta entrega não impõe um novo limite de bytes a esses downloads. Não se afirma memória constante para snapshots arbitrariamente grandes.

## Orçamento por time: não implementado

Foram avaliados [ITeamSubscriptionsLookupService](../src/Personal.FinOpsApi.AzureFunctions/Services/ITeamSubscriptionsLookupService.cs), [OwnershipTeamSubscriptionsLookupService](../src/Personal.FinOpsApi.AzureFunctions/Services/OwnershipTeamSubscriptionsLookupService.cs), [OwnershipStorageService](../src/Personal.FinOpsApi.AzureFunctions/Services/OwnershipStorageService.cs) e [TeamSubscriptionsService](../src/Personal.FinOpsApi.AzureFunctions/Services/TeamSubscriptionsService.cs).

Limitações concretas:

1. `GetAllSubscriptionTeamMappingsAsync()` só retorna o mapa **completo**, sem filtro por time, paginação, limite de resultado, versão do mapa ou CancellationToken.
2. O lookup lê todo o ownership e todo o mapa legado antes de filtrar. Colocar `Take(N)` depois da chamada não limita I/O/memória inicial.
3. `OwnershipStorageService.GetConfigAsync()` pode criar container/configuração ausente durante uma leitura. Não seria correto introduzir esse efeito em GET de governança/read-only. A leitura legada também não aceita cancelamento.
4. Avaliar N subscriptions multiplica índices e até 31 snapshots por N. Não existe agregado materializado por time/mês/moeda com cobertura verificável para essa função.
5. O mapa representa associação **da subscription inteira** a um time, não percentual de alocação de cada recurso. Não permite inventar divisão por times, reconciliar ownership de recurso com tags ou atribuir retrospectivamente uma associação atual ao mês histórico. Também precisa de regra explícita para duplicidade/conflito de associação.

Portanto nenhum `teamId`, orçamento de time ou agregação multissubscription foi implementado, e nenhuma dependência de lookup foi adicionada à governança. O escopo entregue continua subscription/centro importado.

Para implementar com segurança: reader de membership por time paginado, cancelável, sem escrita e versionado; limite explícito de subscriptions antes do fan-out ou agregação assíncrona/materializada; regra histórica de membership; deduplicação de subscriptions; cobertura e gaps por subscription/data; agregados **separados por moeda**, sem conversão nem soma BRL+USD e sem interpretar moeda ausente como zero. Mistura Shadow/Cutover e cobertura parcial precisam impedir confiança indevida. Essas alterações excedem o escopo autorizado atual.

## Settings e composição DI

| Setting | Uso/default |
|---|---|
| `CONFIG_CONTAINER_NAME` | **Obrigatório** nos repositórios de orçamento e auditoria; container existente. |
| `BudgetsBlobPrefix` | `operational/budgets`. Prefixo exclusivo das definições. |
| `GovernanceAuditBlobPrefix` | Novo, opcional: `operational/governance-audit`. Prefixo exclusivo, separado de budgets. |
| `COST_STORAGE_CONTAINER` | Reader financeiro existente; default `finops-analysis`. |
| `COST_DATA_DELAY_DAYS` | Default 2; governança limita entre 1 e 7. |
| `CostAllocationTagName` | Default `CentrodeCusto`; atribuição importada. |
| `EnableCostExportImport` | Deve estar habilitado para modo Cutover segundo o roteamento existente. |
| `COST_EXPORT_SUBSCRIPTIONS` / `COST_EXPORT_IMPORT_ALL_SUBSCRIPTIONS` | Escopo de ingestão utilizado pelo roteamento existente. |
| `COST_EXPORT_CUTOVER_SUBSCRIPTIONS` | Allowlist de publicação oficial; fora dela é Shadow. |

Não foram alterados App Settings, Terraform, pipelines, credenciais nem roles. Não foi adicionado setting de dispatch de alertas. O prefixo de auditoria de governança não usa `AuditLogsBlobPath`/`AuditLogProvider` do repositório compartilhado.

Registro realizado por `AddFinOpsGovernance()` em `Program.cs`, com lifetime scoped:

- `IGovernanceBudgetRepository` → `BlobGovernanceBudgetRepository`.
- **`IGovernanceAuditLogRepository` → `GovernanceAuditLogRepository`** (nova dependência obrigatória de `GovernanceBudgetService`; não mapear ao Blob de lista compartilhada).
- `GovernanceDataHealthService`.
- `GovernanceBudgetService`.

Dependências preexistentes: `BlobServiceClient` autenticado via Managed Identity/credencial padrão, `ICostExportVersionStore`, `ICostStorageRepository`, `IConfiguration`, logging, `ICurrentUserService` e `AuthorizationService`. Não há dependência nova de `ITeamSubscriptionsLookupService`, Cosmos ou cliente Azure de budgets. Sem DI a funcionalidade não deve ser considerada pronta para uso, mesmo com build verde.

O portal usa `configureGovernanceApi` com o transporte autenticado central existente. A continuação de 30/09/2026 conectou a composição DI, antes ausente no host, e adicionou teste de resolução de serviços sem I/O Azure. Rotas e navegação já presentes no trabalho local foram preservadas.

## Validações executadas

- Build backend .NET: sucesso; 10 warnings de nullability em arquivos fora de Governance.
- Testes xUnit/VSTest com filtro `FullyQualifiedName~Governance`: **100 aprovados, 0 falhas**.
- Portal: lint e build de produção aprovados; Vite mantém aviso de chunk acima de 500 kB.
- Cobertura dos testes: autorização e identidade, no-store inclusive erros, JSON limitado/estrito, CAS e path traversal, appends concorrentes independentes, sequência intenção→mutação→resultado, create/update/delete auditados, fail-closed, falha após commit, cancelamento/resultado indeterminado, limites de varredura, dados ausentes/gaps, moedas separadas, tombstone, centros de custo, projeção e sinais indisponíveis.
- Não executados: teste integrado com storage Azure/Azurite, autenticação real, teste E2E de confirmação no navegador ou implantação. Os testes de auditoria usam mocks dos clientes Blob; confirmação UI foi revisada no código e compilada, não automatizada em navegador.
