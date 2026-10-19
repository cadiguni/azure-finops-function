# Roadmap — Azure FinOps Platform

Este roadmap prioriza confiabilidade do dado financeiro, operações seguras e redução de trabalho manual. As fases devem ser planejadas por resultado e não por uma data fixa; cada item só avança após seus critérios de aceite e observabilidade estarem atendidos.

## Situação da implementação — 30/09/2026

Estado verificado no código local, incluindo trabalho ainda não commitado. Implementado não significa provisionado, publicado ou validado no Azure.

| Entrega | Estado atual | Próxima lacuna |
|---|---|---|
| Saúde dos dados | API e tela implementadas para cobertura/frescor de Export | Saúde de timers/filas e notificações operacionais |
| Orçamento/previsão | API, persistência, auditoria e tela por subscription/centro importado | Agregação por time e envio de alertas |
| Migração de recursos | Fluxo simples no portal: origem, recursos, destino (existente ou novo), validação e move pelo ARM com o token do usuário, lote de vários RGs, registro de resource provider e exclusão do RG de origem vazio ([MIGRATIONS.md](MIGRATIONS.md)) | Reconciliação do Terraform dos recursos movidos (state rm + import com os IDs novos) |
| Reconciliação Terraform | Pipeline NAP reconcilia state com snapshot e lease/ETag; modo manualPlan publica recibo v3 e aguarda validação manual no portal | Homologar em DEV; depois integrar plan sem apply à pipeline de infraestrutura do projeto e registrar resultado |
| Cutover | Aprovação por plano/evidência e pré-check de Library implementados | Escrita controlada da Library e validação do ambiente após cutover |
| Limpeza do RG antigo | Ainda não implementada | Comprovar reconciliação, validação final e RG vazio antes de exclusão explícita |

Decisão de 08/10/2026: o módulo avançado (perfis, jobs, fila e reconciliação via DevOps) foi substituído pelo fluxo simples de move; o código está no branch `archive/migracoes-avancado`. As linhas de Reconciliação, Cutover e Limpeza abaixo descrevem esse módulo arquivado.

Decisão anterior, de 01/10/2026: homologar move e reconciliação central em DEV isolado com plan manual do operador → integrar validação à pipeline de infraestrutura do projeto sem apply automático → cutover e validação da aplicação → limpeza opcional do RG de origem. Recibo de state não comprova plan nem conclusão da migração.

## Princípios de execução

- O portal informa fatos financeiros observados; estimativas e dados incompletos são explicitamente identificados.
- Operações demoradas ou de múltiplas subscriptions são assíncronas, idempotentes e rastreáveis por Service Bus.
- Escritas operacionais exigem `FinOps.Admin`, confirmação explícita e trilha de auditoria.
- Produção não é alvo de automações destrutivas. Migração entre subscriptions começa exclusivamente em DEV e HML.
- Terraform continua sendo aplicado pelo Azure DevOps. O portal orquestra e acompanha; não acessa state remoto nem credenciais de pipeline diretamente.

## Fase 1 — Confiabilidade e experiência de custos

Objetivo: tornar os valores comparáveis ao Cost Management e manter as telas responsivas conforme o volume cresce.

- Concluir o backfill dos rollups diários de bancos e acompanhar dias sem consolidação por dashboard/alerta.
- Medir latência e tamanho das respostas dos endpoints de custo; definir SLO inicial para abertura do portal.
- Evoluir paginação e filtros server-side para as demais grades de grande volume.
- Adicionar testes de contrato para paginação, filtros e janelas financeiras incompletas.
- Mostrar data de atualização, cobertura por subscription e motivo de qualquer fallback em todas as telas financeiras.

Critério de aceite: nenhuma tela apresenta variação como comparável quando uma das janelas tem lacunas; consultas volumosas não renderizam milhares de linhas no cliente.

## Fase 2 — Governança e operação FinOps

Objetivo: transformar os dados existentes em processo recorrente de decisão e acompanhamento.

- Orçamento por subscription, time e centro de custo, com alertas de consumo e previsão de término do orçamento.
- Workflow de recomendações: responsável, prazo, justificativa, aceite/rejeição e medição do custo observado antes/depois.
- Relatório de cobertura de tags e ownership, com metas por time e exceções aprovadas.
- Exportação CSV/PDF agendada para responsáveis e consolidação mensal por billing period.
- Painel de saúde operacional: importações, filas, timers, falhas de export e frescor dos snapshots.

### Entregas priorizadas desta fase

1. **Saúde dos dados** — painel com última coleta/importação, dias sem consolidação, falhas de timer ou fila, cobertura por subscription e alertas quando o custo estiver parcial.
2. **Orçamento e previsão** — budget por subscription, time e centro de custo; consumo acumulado, projeção para o fim do período e alertas em 50%, 80%, 100% e previsão de estouro.
3. **Workflow de recomendações** — responsável, prazo, aceite/rejeição, justificativa, evidências e comparação do custo observado antes/depois da ação.
4. **Tags e ownership** — scorecard por time para owner, centro de custo, projeto e ambiente; recursos sem responsável devem entrar em uma fila de tratamento.
5. **Showback/chargeback** — demonstrativo mensal por time, centro de custo e produto, separando custo alocado, compartilhado e sem alocação.
6. **Anomalias acionáveis** — investigação por serviço/recurso, comparação histórica, responsável e status de tratativa.
7. **Compromissos Azure** — elegibilidade e cobertura de Reservations, Savings Plans e Azure Hybrid Benefit, distinguindo potencial de economia realizada.
8. **Runbooks de custo para DEV/HML** — desligamento programado, limpeza de recursos órfãos, expiração de ambientes temporários e validação de recursos sem uso.

Ordem recomendada: saúde dos dados → orçamento/previsão → workflow de recomendações → tags/ownership → showback/chargeback. As demais entregas dependem desse alicerce de dados confiáveis e responsabilização.

Critério de aceite: cada recomendação e anomalia relevante tem responsável, status e histórico; falhas de coleta são detectadas antes de afetarem relatórios gerenciais.

## Fase 3 — Migração assistida entre subscriptions (DEV/HML)

### Objetivo

Automatizar e padronizar a migração de recursos entre subscriptions Azure para DEV e HML, reduzindo alterações manuais em Azure DevOps e Terraform, sem permitir migração de produção.

### Limites e premissas

- Aceitar apenas ambientes `DEV` e `HML`; rejeitar `PRD`/produção no backend, na interface e na pipeline.
- Os mesmos arquivos Terraform são usados por ambiente e não possuem `subscription_id` fixo.
- A subscription do Terraform é definida pela Service Connection referenciada por parâmetro/template, cujo valor vem de uma Library/Variable Group.
- O fluxo padrão não altera YAML; o cutover atualiza a variável da Library somente após validação do Terraform.
- A integração com Azure DevOps deve usar uma identidade dedicada, com permissões mínimas por projeto, Variable Group e pipeline. Tokens, PATs e secrets nunca são persistidos pelo FinOps.
- O portal não executa `terraform apply` localmente nem altera state de produção. A reconciliação e o `plan` acontecem numa pipeline aprovada, com artefatos publicados de volta ao processo.

### Fluxo alvo

```text
Rascunho → Análise → Plano aprovado → Movimento Azure → Reconciliação do state
        → terraform plan limpo → Cutover da Library → Validação de pipeline → Concluída
                                      ↘ Falhou / requer intervenção ↗
```

Cada transição é registrada. Após o início do movimento Azure, rollback é um plano explícito e guiado, não uma promessa de reversão automática: nem todo tipo de recurso suporta mover ou voltar sem impacto.

### Incremento 3.1 — Descoberta e plano (somente leitura)

- Nova área “Migrações de subscriptions” no portal, visível para `FinOps.Admin`.
- Criar um rascunho com projeto Azure DevOps, ambiente, subscription de origem e destino, Resource Groups e recursos candidatos.
- Descobrir dependências via Azure Resource Graph e consultar a capacidade de `moveResources` do Azure Resource Manager por tipo/recurso.
- Identificar bloqueios: tipo não suportado, dependência fora do escopo, região/tenant incompatível, locks, policy, RBAC, recursos compartilhados e serviços que exigem procedimento específico.
- Localizar o repositório, backend Terraform, pipeline, Variable Group e nome da variável de Service Connection por configuração declarativa e versionada — nunca por inferência frágil de texto em YAML.
- Produzir plano imutável: inventário com IDs antigos, destino esperado, dependências, pré-requisitos, riscos, responsáveis e hash/revisão do Terraform.

Critério de aceite: uma migração não pode ser executada se houver bloqueio não reconhecido, identificação ambígua de Variable Group/Service Connection ou ambiente fora de DEV/HML.

### Incremento 3.2 — Orquestração segura e aprovação

- Persistir `MigrationJob`, etapas, tentativas, logs sanitizados, aprovações e correlação com execução de pipeline em Cosmos DB/audit log.
- Enfileirar cada etapa por Service Bus; usar chave idempotente formada pelo ID da migração e etapa.
- Exigir aprovação em dois momentos: antes do movimento Azure e antes do cutover da Library.
- Implementar bloqueio de concorrência por projeto+ambiente para impedir duas migrações do mesmo state.
- Permitir dry-run completo, cancelamento antes do movimento e retomada segura de etapas idempotentes.

Critério de aceite: toda ação de escrita possui ator, horário, parâmetros, resultado e link para evidência; repetição de mensagem não duplica a operação.

### Incremento 3.3 — Movimento e reconciliação Terraform

- Executar o move pelo ARM somente para o plano aprovado e para o conjunto validado de Resource Groups/recursos.
- Após o move, obter os Resource IDs reais no destino e comparar com o inventário planejado.
- Acionar pipeline dedicada de reconciliação: checkout na revisão planejada, inicialização do backend permitido para DEV/HML, atualização controlada de endereços/IDs no state e publicação de artefatos de evidência.
- Executar `terraform plan` na revisão aprovada, com o provider apontando para a subscription de destino e acesso separado ao backend do state. A Library permanece inalterada até o cutover; registrar o plano como artefato.
- Bloquear cutover se o plan indicar criação, destruição ou substituição inesperada. Itens permitidos precisam de aprovação explícita e regra documentada.

Critério de aceite: o state referencia os IDs de destino e o plan validado não contém recriações/destruições inesperadas.

### Incremento 3.4 — Cutover e validação

- Atualizar, exclusivamente após o plan aprovado, a variável de Service Connection no Variable Group identificado no plano.
- Registrar a versão anterior da variável como metadado de rollback, sem expor segredo.
- Disparar ou aguardar pipeline de validação do ambiente; verificar status, health checks configurados e recursos no destino.
- Marcar como concluída apenas com todas as evidências anexadas: move, reconciliação, plan, mudança da Library e pipeline validada.
- Oferecer rollback assistido: restaurar a referência anterior da Library somente se os critérios de retorno forem atendidos; deslocamento físico de recursos exige plano específico por tipo.

Critério de aceite: uma migração concluída tem evidências navegáveis de todas as etapas e uma migração interrompida não troca a Service Connection sem aprovação.

### Incremento 3.5 — Limpeza do Resource Group de origem

- Oferecer exclusão opcional e explicitamente aprovada somente após reconciliação do state, plan verificado, cutover e validação final do ambiente.
- Proteger o Storage do backend e backups: não permitir limpeza se estiverem no RG de origem. Verificar se o Terraform ainda gerencia o RG antigo ou contém referências a ele.
- Fazer backup recuperável do state antes da reconciliação e registrar identidade do blob, versão/ETag, lineage e serial sem publicar conteúdo sensível no portal ou nos logs.
- Verificar novamente todo o inventário do RG antes da exclusão; qualquer recurso residual ou falha de leitura bloqueia a operação. Manter congelamento de alterações durante a janela: a checagem de vazio não é uma exclusão condicional atômica do Azure.
- Persistir intenção, acompanhar a operação assíncrona e confirmar ausência do RG. Falha ou resultado incerto exige investigação, sem repetir DELETE automaticamente.
- Registrar conclusão e liberar lock somente quando as etapas obrigatórias tiverem evidências; limpeza omitida deve constar explicitamente no histórico.

Critério de aceite: falha de reconciliação/plan/cutover, backend localizado na origem ou RG com recursos impede exclusão; nenhum teste de desenvolvimento executa limpeza no Azure real.

### Modelo mínimo de dados

`MigrationJob` deve conter: `id`, projeto, ambiente, subscriptions origem/destino, solicitante, status, plano/revisão, Resource Groups, recursos e capacidades de move, dependências, Variable Group/variável, Service Connection anterior/nova (somente identificadores), runs de pipeline, aprovações, etapas, timestamps, erro sanitizado e links de evidência.

### Riscos a tratar no discovery

- Mover recursos não significa mover identidades, RBAC, DNS, private endpoints, diagnósticos, chaves ou integrações externas.
- Nem todos os provedores/tipos Azure permitem `moveResources`, e alguns exigem mover o conjunto inteiro de dependências.
- State Terraform pode ter endereços de módulo, `for_each` e imports que impedem uma troca mecânica de ID.
- Alterar uma Variable Group pode afetar mais de um pipeline/ambiente; o plano deve listar todos os consumidores.
- A Service Connection de destino precisa existir e ter RBAC validado antes de qualquer cutover.

## Fase 4 — Escala e controles avançados

- FinOps por Management Group, hierarquia organizacional e showback/chargeback por unidade.
- Detecção de anomalia com baseline sazonal e workflow de investigação.
- Políticas de governança preventivas: tags obrigatórias, SKUs restritos e alertas de recursos sem owner.
- Catálogo de runbooks reutilizáveis para operações cloud, incluindo novos tipos de migração após a maturação de DEV/HML.
- Avaliar suporte a produção apenas mediante desenho de segurança, janelas de mudança, aprovação formal e testes de recuperação específicos.

## Métricas de sucesso

| Área | Métrica inicial |
|---|---|
| Dados financeiros | % de dias consolidados por janela e tempo até a consolidação |
| Portal | p95 de latência por tela e tamanho médio de resposta |
| Governança | % de custo com owner/tag/centro de custo válido |
| Recomendações | % tratada dentro do SLA e economia observada após ação |
| Migrações | tempo de ponta a ponta, % de plans limpos, intervenções manuais e falhas de cutover |
