# Migração de recursos

Tela **Migração de recursos** do portal (`/migrations`, visível para `FinOps.Admin`). Move recursos entre resource groups e subscriptions, como o `az resource move`.

## Fluxo

1. **Origem:** subscription → resource group → recursos a mover (seleção individual ou "Selecionar todos"). Subscription e RG são campos pesquisáveis (nome, ID ou região), e só aparecem subscriptions `MPN - …`.
2. **Destino:** subscription → resource group existente, ou "Criar novo resource group". O nome vem preenchido com o do RG de origem. O RG novo é criado ao validar, na região e com as **tags** do RG de origem (atende às políticas que exigem `Setor`/`Responsável`), porque o ARM exige que o destino exista já na validação. Se o nome já existir, a criação é recusada.
3. **Validar:** `validateMoveResources`. Em caso de recusa, a tela mostra a mensagem e os detalhes do ARM (por exemplo, recursos dependentes que precisam ir junto). Se o motivo for `MissingRegistrationsForTypes`, a tela junta os resource providers pendentes de **todos** os RGs (sem diferenciar maiúsculas, como `microsoft.insights/actiongroups`) e oferece "Registrar todos" de uma vez, na subscription citada pelo Azure, acompanhando até todos ficarem `Registered`. Se o motivo for `ResourceMoveNotSupported` (tipo que o Azure não move, como `frontdoorWebApplicationFirewallPolicies`), a tela oferece desmarcar esses recursos. Detalhes repetidos pelo Azure aparecem uma vez só, e a recusa continua visível quando a seleção muda, com aviso para validar de novo.
4. **Mover:** liberado só depois de uma validação bem-sucedida **da mesma seleção** e de uma confirmação explícita. Qualquer mudança na seleção exige validar de novo. Chama `moveResources` e acompanha a operação até terminar.

5. **Excluir o RG de origem (opcional):** depois do move, se o RG de origem ficou vazio, a tela oferece excluí-lo. Exige digitar o nome do RG e confere de novo, imediatamente antes do DELETE, que não há nenhum recurso nele.

### Vários resource groups (lote)

Na origem, é possível adicionar RGs um por um pela busca, ou todos os RGs da subscription de uma vez ("Adicionar todos os resource groups"); "Remover todos" esvazia o lote. Cada RG mostra os recursos dele, todos marcados por padrão, e dá para desmarcar algum. RGs sem nenhum recurso marcado (inclusive RGs vazios) ficam fora do lote automaticamente, com aviso. Os recursos são carregados com no máximo 6 consultas simultâneas.

- **Destino:** cada RG de origem vai para um RG **com o mesmo nome** na subscription de destino, que precisa ser outra subscription. Os que não existem são criados ao validar, com a região e as tags da origem; os que já existem são reaproveitados.
- **Validação:** todos os RGs são validados, um por vez. **Nada é movido até todos validarem**; a recusa mostra o motivo de cada RG com problema.
- **Move:** em fila, um RG por vez, porque o ARM exige um RG de origem por `moveResources`. Se um move falhar, o Azure desfaz aquele move, os anteriores ficam movidos e os seguintes não começam; a tela lista cada grupo.
- **Depois:** a exclusão opcional do RG de origem vazio é oferecida para cada RG movido.

O move continua no Azure mesmo se a página for fechada. Se o acompanhamento falhar no meio, a tela oferece "Consultar o andamento", que só consulta a operação e **nunca reenvia o move**.

## Como funciona

- O portal chama o **Azure Resource Manager direto do navegador**, com o token do usuário logado (scope `https://management.azure.com/user_impersonation`). O backend do FinOps não participa.
- O usuário só move o que já pode mover pelo portal do Azure ou pelo CLI. A autorização é o RBAC dele: `moveResources` no RG de origem e escrita no RG de destino. O Activity Log registra o nome dele.
- Todos os recursos de um move vêm do mesmo RG de origem (exigência do ARM).
- Código: [armApi.ts](../frontend/finops-portal/src/services/armApi.ts) e [Migrations.tsx](../frontend/finops-portal/src/pages/Migrations.tsx).

## Pré-requisito no Entra ID

No app registration do portal (**finops-app**) → *API permissions* → *Add a permission* → **Azure Service Management** → *Delegated* → `user_impersonation`, e conceder o consentimento de administrador (*Grant admin consent*). Sem isso, a tela pede consentimento no primeiro acesso, ou falha se o tenant não permitir consentimento de usuário.

## O que não faz

- Não atualiza Terraform (state, código), pipelines nem Libraries dos recursos movidos.
- Não reconcilia o Terraform dos recursos movidos: os IDs mudam (a subscription faz parte do ID), então o state do projeto precisa de `terraform state rm` + `terraform import` com os IDs novos antes do próximo `apply`.
- Não guarda histórico próprio: o registro é o Activity Log do Azure.

## Histórico

O módulo anterior (perfis por projeto, jobs no Cosmos, fila no Service Bus, reconciliação do tfstate via pipeline do Azure DevOps e segunda aprovação) foi substituído por este fluxo. O código está preservado no branch `archive/migracoes-avancado`. Os recursos de Azure que ele usava foram retirados do Terraform sem exclusão; veja o comentário em [migrations.tf](../pipelines/terraform-function/migrations.tf).
