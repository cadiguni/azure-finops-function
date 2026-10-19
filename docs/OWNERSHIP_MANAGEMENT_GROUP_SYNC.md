# Sincronizacao de Ownership por Management Group

## Objetivo

`config/ownership.json` e a fonte canonica para o vinculo entre subscription e time. O arquivo legado `config/team-subscriptions.json` permanece somente como fallback para subscriptions ainda ausentes no Ownership.

A sincronizacao consulta o Parent Management Group de cada subscription e usa:

- `ManagementGroupId` como ID do time;
- `ManagementGroupDisplayName` como nome do time;
- `managementGroup` como origem da atribuicao automatica.

## Regras de reconciliacao

- Um time ausente e criado somente quando existe uma subscription ainda nao atribuida naquele Parent Management Group.
- Nomes de MG no formato `PREFIXO - TIME` podem ser associados a um unico time existente pelo segmento final. Por exemplo, `MPN - MOBILE` e associado a `Mobile`.
- O ID do Management Group fica salvo como alias do time canonico para que as proximas execucoes usem correspondencia exata.
- Uma subscription sem atribuicao e adicionada ao time correspondente.
- Uma atribuicao existente nunca e movida nem sobrescrita.
- A excecao sao atribuicoes com origem `managementGroup`: elas podem ser consolidadas no time canonico equivalente.
- Times criados automaticamente sao removidos quando ficam vazios apos a consolidacao.
- Se a atribuicao existente aponta para outro time, o item e contabilizado como conflito.
- Subscriptions desabilitadas continuam bloqueando o fallback legado.
- Execucoes repetidas sao idempotentes: nao duplicam times nem subscriptions.
- Cada gravacao gera um backup em `config/history/ownership-{timestamp}.json`.

## Execucao manual

No portal, acesse **Ownership** como administrador e selecione **Sincronizar por Parent Management Group**.

O resultado apresenta:

- `Descobertas`: subscriptions retornadas pela descoberta;
- `Com parent`: subscriptions com Parent Management Group identificado;
- `Times criados`: novos times gerados a partir dos MGs;
- `Adicionadas`: subscriptions novas no Ownership;
- `Ja mapeadas`: subscriptions preservadas por ja possuirem atribuicao;
- `Conflitos`: atribuicoes existentes que diferem do Parent MG;
- `Sem time correspondente`: Parent MG sem ID e sem nome utilizavel;
- `Sem parent`: subscriptions sem Parent Management Group.

Antes da criacao automatica de times, um resultado como `60 descobertas / 60 sem time correspondente` indicava que os nomes dos nove times existentes nao coincidiam com os Parent MGs. Apos a atualizacao, uma nova execucao deve criar apenas os times necessarios, adicionar subscriptions ainda nao atribuidas e listar as atribuicoes manuais divergentes como conflitos.

## Execucao automatica

A Function `OwnershipManagementGroupSyncTimer` usa os App Settings:

- `EnableOwnershipManagementGroupSync`: habilita ou desabilita o timer;
- `OwnershipManagementGroupSyncSchedule`: expressao NCRONTAB do Azure Functions.

O Terraform habilita a execucao semanal as segundas-feiras, `09:05 UTC`, antes das coletas de custo. O ambiente local de exemplo mantem a automacao desabilitada.

## Observabilidade

No Application Insights, consulte mensagens com os marcadores:

- `[OWNERSHIP-SYNC-TIMER]`: inicio, desabilitacao e resultado do timer;
- `[OWNERSHIP-SYNC]`: falhas da execucao manual;
- `[OWNERSHIP]`: leitura e gravacao do arquivo de configuracao.

Falhas do timer aparecem como excecoes da Function `OwnershipManagementGroupSyncTimer`. Falhas manuais retornam alerta HTTP no portal. Os contadores e a data da ultima sincronizacao permanecem visiveis na pagina Ownership.

## Conflitos e rollback

Conflitos nao sao corrigidos automaticamente. Um administrador pode:

- usar `Revisar` para mover a subscription ao time sugerido;
- usar `Manter atual` para registrar uma excecao intencional;
- usar `Manter todos atuais` quando todas as atribuicoes listadas estiverem corretas.

Uma excecao aceita permanece no time atual e deixa de ser reportada nas sincronizacoes seguintes. Editar ou mover essa subscription limpa a excecao e reativa a comparacao com o Parent Management Group.

Para rollback, restaure o backup anterior de `config/history/` para `config/ownership.json` e reinicie a Function App para invalidar o cache em memoria. Desabilite temporariamente `EnableOwnershipManagementGroupSync` antes da restauracao para impedir uma nova execucao automatica.

## Desativacao do legado

O fallback de `config/team-subscriptions.json` pode ser removido quando:

1. todas as subscriptions esperadas existirem em `ownership.json`;
2. Anomalias e summaries estiverem validados com os mesmos times;
3. os conflitos da ultima sincronizacao tiverem sido revisados;
4. pelo menos um ciclo automatico tiver concluido sem subscriptions inesperadas.
