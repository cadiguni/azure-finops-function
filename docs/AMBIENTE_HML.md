# Ambiente HML

Ambiente para testar telas novas e correções com dados de custo reais, sem risco para prod.

## O que é compartilhado e o que é isolado

| | Prod | HML |
|---|---|---|
| Branch | `main` | `homolog` (qualquer branch ≠ `main`) |
| Library | `FinOps-prod-Personal` | `FinOps-hml-Personal` |
| State | `nap/prod/finopsplatform*.tfstate` | `nap/hml/finopsplatform*.tfstate` |
| Recursos | `finopsplatform-nap-*` | `finopsplatform-hml-*` |
| Variáveis Terraform | `environments/prod.tfvars` | `environments/hml.tfvars` |
| Cost Management Exports | storage próprio | **lê o de prod** (Storage Blob Data Reader só no container `cost-exports`) |
| Checkpoints, análises, relatórios, ownership, status, auditoria | storage/Cosmos de prod | storage/Cosmos de HML |
| Leitura de recursos (Reader / Cost Management Reader) | Management Group `Geral` (manual) | só `reader_subscription_ids` (via Terraform) |
| Portal (Front Door `frontdoor2-dev-afd`) | `finops.example.com` → endpoint `finopsplatform-dev-fde` | `finops-hml.example.com` → endpoint `finopsplatform-hml-fde` |
| Migrações | portal chama o ARM com o token do usuário | igual (não depende de recursos do ambiente) |

O Azure nega qualquer escrita de HML nos exports de prod pelo RBAC. A proteção não depende do código.

## Como os triggers escolhem o ambiente

O ambiente vem da branch, em `pipelines/config.yml`. Os builds e as infras disparam sozinhos só em `main` e `homolog`, e cada pipeline só dispara pelos paths dela.

| Pipeline | Dispara quando | Ambiente |
|---|---|---|
| `infra.yml` / `infra-frontend.yml` | push em `pipelines/terraform-function/**` / `pipelines/terraform-frontend/**` | da branch |
| `build-function.yml` / `build-frontend.yml` | push em `src/**` / `frontend/**` | da branch |
| `deploy-function.yml` / `deploy-frontend.yml` | build terminar (resource trigger) | o mesmo do build, porque roda no mesmo commit e branch |

Uma branch de feature só vai para HML rodando o build manualmente nela. Ela não dispara sozinha.

## Primeira subida

0. Pré-requisito, já feito: o service principal da service connection (`<service-connection>`) tem **Role Based Access Control Administrator** na subscription, com condição. Ele só pode atribuir Service Bus Data Sender/Receiver, Storage Blob Data Contributor/Reader, Reader e Cost Management Reader, e somente a service principals. Por isso toda `azurerm_role_assignment` precisa declarar `principal_type = "ServicePrincipal"`; sem isso o Azure responde 403.
1. Na `FinOps-hml-Personal`:
   - Preencha `VITE_ENTRA_CLIENT_ID`, `VITE_ENTRA_TENANT_ID` e `VITE_FINOPS_API_SCOPE` com os mesmos valores de prod, como secret. Assim HML reutiliza os app registrations e as roles `FinOps.Reader`/`FinOps.Admin`. O build falha com uma mensagem clara se alguma estiver vazia.
   - Apague `VITE_ENTRA_REDIRECT_URI` nas duas Libraries. Agora o redirect é derivado de `PortalDomain` no `config.yml`.
2. Crie a branch `homolog` a partir de `main`.
3. Rode `infra.yml` na `homolog` e revise o plan. Ele só deve criar recursos `finopsplatform-hml-*` e role assignments da identidade de HML. Depois rode `infra-frontend.yml`.
4. No profile `frontdoor2-dev-afd`, crie o endpoint `finopsplatform-hml-fde` igual ao de prod:
   - Rota `/*` para `finopsplatformhmlwebstg` (static website).
   - Rota `/api/*` para `finopsplatform-hml-func.azurewebsites.net`.
   - Custom domain `finops-hml.example.com`.

   Se usar outros nomes, ajuste `FrontDoorEndpointName` e `PortalDomain` no `config.yml`.
5. Adicione `https://finops-hml.example.com/` como Redirect URI (tipo SPA) no app registration do portal.
6. Rode o build da Function e o build do frontend na `homolog`. Os deploys disparam em seguida, e o deploy do frontend faz o purge do endpoint de HML.
7. Opcional: copie `finops-config/ownership.json` de prod para o storage de HML, para as telas de times terem dados.

O primeiro import de exports processa até 100 manifests por execução, a cada 2h. Para acelerar, rode o import manualmente.

## Limitações conhecidas

- Os recursos de HML ficam em **East US** (`location` no `hml.tfvars`), porque a quota "Total Regional VMs" de App Service em East US 2 está esgotada na MPN. O resource group continua em East US 2: é só metadado, e trocá-lo destruiria o RG com o storage do portal (`infra-frontend`) dentro. O storage do portal de HML e os exports de prod continuam em East US 2. O Cosmos de HML também fica em East US 2 (`cosmos_location`), porque East US recusou a criação por falta de capacidade para a subscription.

- Recomendações e analyzers cobrem só as subscriptions de `reader_subscription_ids`. Custos e anomalias cobrem todas, porque vêm dos exports.
- Sem leitura do Management Group, a sincronização de ownership fica desligada e a hierarquia de Management Groups pode aparecer vazia.
- Quando os exports estão defasados, o fallback de anomalias para a Query API só funciona nas subscriptions com Cost Management Reader.

## Prod após a separação

- O `infra.yml` em prod roda com `Ambiente=prod`, então o template passa a ter um stage de aprovação manual (`ManualValidation`, aprovado por quem disparou a execução) antes do apply.
- O deploy da Function continua sem aprovação, como antes.
- Build e deploy do frontend usam os templates do DevOps (`frontend/v4.6.0`). Em prod, o deploy do frontend passa a ter aprovação manual.
- O deploy do frontend usa `cleanStrategy: prune`. O `$web` nunca é limpo antes do upload, e arquivos que saíram do artefato são apagados após 7 dias. O primeiro deploy em prod remove os assets acumulados desde que o upload deixou de limpar o container.
- Os states antigos `nap/finops/finopsplatform*.tfstate` podem ser removidos depois que a primeira execução do `infra.yml` e do `infra-frontend.yml` na `main` mostrar *No changes*.
