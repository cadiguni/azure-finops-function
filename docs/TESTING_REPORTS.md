# 🧪 Guia de Teste - Novos Endpoints de Relatório

## 📋 Pré-requisitos

### 1. Ferramentas Necessárias
```bash
# Verificar se você tem as ferramentas instaladas
dotnet --version          # Deve ser 8.0+
func --version           # Azure Functions Core Tools v4
azurite --version        # Storage Emulator
```

### 2. Configuração do `local.settings.json`
```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "ServiceBusConnection": "[Sua connection string do Service Bus]",
    "AZURE_SUBSCRIPTION_ID": "[ID da subscription test]",
    "AZURE_TENANT_ID": "[Tenant ID]",
    "AZURE_CLIENT_ID": "[Managed Identity ou Service Principal]",
    "RESULTS_CONTAINER_NAME": "finops-analysis",
    "CostAnalysisSchedule": "0 0 3 * * *",
    "COST_SUBSCRIPTIONS": "subscription-id-1,subscription-id-2"
  }
}
```

## 🚀 Executando Localmente

### 1. Iniciar Azurite (Storage Emulator)
```bash
# Em um terminal separado
azurite --silent --location c:\temp\azurite --debug c:\temp\azurite\debug.log
```

### 2. Iniciar Azure Function
```bash
cd "c:\Projetos\Personal\azure-finops-platform\src\Personal.FinOpsApi.AzureFunctions"
func start --script-root . --verbose
```

**Saída esperada:**
```
Azure Functions Core Tools
Version: 4.x
Functions:
    GenerateHtmlReport: [GET] http://localhost:7071/api/report/html
    GenerateCsvReport: [GET] http://localhost:7071/api/report/csv
    TeamOwnershipDiscover: [POST] http://localhost:7071/api/ownership/discover  
    GetTeamOwnership: [GET] http://localhost:7071/api/ownership
    GetReportTeams: [GET] http://localhost:7071/api/report/teams
    GenerateCsvReport: [GET] http://localhost:7071/api/report/csv
    HealthCheck: [GET] http://localhost:7071/api/health
    [... outras funções]

Host: http://localhost:7071
```

## 🧪 Cenários de Teste

### 1. **Teste Básico - Health Check**
```bash
# Verificar se a Function está rodando
curl http://localhost:7071/api/health
```

**Resposta esperada:**
```json
{
  "status": "healthy",
  "timestamp": "2024-04-23T14:30:00Z"
}
```

### 2. **Teste dos Endpoints de Relatório**

#### 🎨 Relatório HTML
```bash
# Relatório HTML básico (usa data de ontem por padrão)
http://localhost:7071/api/report/html

# Com data específica
http://localhost:7071/api/report/html?date=2024-04-22

# Filtrado por Management Group
http://localhost:7071/api/report/html?date=2024-04-22&managementGroup=NAP

# Filtrado por Subscription
http://localhost:7071/api/report/html?date=2024-04-22&subscription=xxx-xxx-xxx
```

**Resposta esperada (HTML):**
- Browser abre com relatório estilizado
- Sumário executivo no topo
- Seções por ação: "Excluir", "Reduzir", "Revisar"
- Lista detalhada de recursos
- Responsivo (mobile-friendly)

#### 📊 Relatório CSV
```bash
# Relatório CSV básico
http://localhost:7071/api/report/csv

# Com parâmetros
http://localhost:7071/api/report/csv?date=2024-04-22&managementGroup=NAP
```

**Resposta esperada (CSV):**
```csv
ManagementGroup,Subscription,ResourceGroup,ResourceId,ResourceName,ResourceType,Category,Description,Action,Priority,Confidence,MonthlySavings
NAP,sub-123,rg-test,/subscriptions/.../vm1,vm-idle,Virtual Machine,IdleVm,"VM with low CPU utilization",Revisar,Medium,85%,R$ 450.00
```

### 3. **Teste com Dados Reais**

Para testar com dados reais, você precisa:

#### A. **Gerar dados primeiro** (execute análise manual)
```bash
# Executar análise de custo para gerar dados
curl -X POST http://localhost:7071/api/ManualCostAnalysis

# Ou forçar coleta de dados
curl -X POST http://localhost:7071/api/collect-costs
```

#### B. **Verificar se dados estão no Storage**
```bash
# Listar containers no Azurite
# Acesse: http://127.0.0.1:10000/devstoreaccount1?comp=list

# Procurar por:
# - Container: finops-analysis
# - Paths: cost/byService/date=2024-04-22/
```

#### C. **Testar relatórios com dados reais**
```bash
# Usar data dos dados gerados
http://localhost:7071/api/report/html?date=2024-04-22
```

## 🔍 Troubleshooting

### ❌ Problema: "No data found"
**Solução:**
1. Verificar se análise foi executada: `curl http://localhost:7071/api/ManualCostAnalysis`
2. Confirmar data correta: usar data de quando análise rodou
3. Verificar Storage: dados devem estar em `finops-analysis` container

### ❌ Problema: "Azure authentication failed"
**Solução:**
```bash
# Login no Azure CLI
az login
az account set --subscription "sua-subscription-id"
```

### ❌ Problema: "Status 401 Unauthorized"  
**✅ RESOLVIDO:** Endpoints agora usam `AuthorizationLevel.Anonymous`
```bash
# Agora funcionam sem autenticação
curl -X POST "http://localhost:7071/api/ownership/discover"
curl "http://localhost:7071/api/ownership"
```

## 🎯 NOVOS ENDPOINTS - Team Ownership (2024)

### 🔍 1. Discovery de Teams
```bash
# Descobrir teams automaticamente via Management Groups
curl -X POST "http://localhost:7071/api/ownership/discover" \
     -H "Content-Type: application/json"
```

**Resposta esperada:**
```json
{
  "success": true,
  "message": "Team ownership discovery completed successfully",
  "teamsCount": 3,
  "managementGroupsCount": 3,
  "subscriptionsCount": 8,
  "generatedAt": "2024-04-30T14:30:00Z",
  "overridesFileCreated": true,
  "teams": [
    "plataforma (1 MGs, 3 subs)",
    "nap (1 MGs, 2 subs)",
    "ti (1 MGs, 3 subs)"
  ]
}
```

### 📋 2. Visualizar Ownership Atual  
```bash
# Ver configuração completa de teams
curl "http://localhost:7071/api/ownership"
```

### 📊 3. Relatórios por Teams
```bash
# Relatório agregado por teams
curl "http://localhost:7071/api/report/teams?date=2024-04-29"
```

### 🎯 4. Filtros por Team nos Relatórios Existentes

#### HTML com filtro por team:
```bash
# Relatório HTML só do time "plataforma"
curl "http://localhost:7071/api/report/html?date=2024-04-29&team=plataforma" > report-plataforma.html
```

#### CSV com filtro por team:
```bash
# Dados CSV só do time "ti"
curl "http://localhost:7071/api/report/csv?date=2024-04-29&team=ti" > report-ti.csv
```

#### PDF com filtro por team:
```bash
# PDF só do time "plataforma"
curl "http://localhost:7071/api/report/pdf?date=2024-04-29&team=plataforma" \
     -H "Accept: application/pdf" \
     -o "report-plataforma.pdf"
```

### ⚙️ 5. Workflow Completo de Teste

```bash
# 1. Descobrir teams primeiro
curl -X POST "http://localhost:7071/api/ownership/discover"

# 2. Verificar teams criados  
curl "http://localhost:7071/api/ownership"

# 3. Ver relatório por teams
curl "http://localhost:7071/api/report/teams"

# 4. Gerar relatório específico de um team
curl "http://localhost:7071/api/report/html?team=plataforma" > plataforma.html

# 5. Abrir no browser
start plataforma.html  # Windows
```

---
**Documentação atualizada: 30/04/2024 - Team Ownership System** 🚀
2. Confirmar data correta: usar data de quando análise rodou
3. Verificar Storage: dados devem estar em `finops-analysis` container

### ❌ Problema: "Azure authentication failed"
**Solução:**
```bash
# Login no Azure CLI
az login
az account set --subscription "sua-subscription-id"

# Verificar identidade
az account show
```

### ❌ Problema: "Function timeout"
**Solução:**
1. Aumentar timeout no `host.json`:
```json
{
  "version": "2.0",
  "functionTimeout": "00:10:00"
}
```

### ❌ Problema: Relatório HTML vazio
**Solução:**
1. Verificar logs da Function
2. Testar Management Group Service:
```bash
curl http://localhost:7071/api/real/management-groups
```

## 📝 Checklist de Validação

### ✅ Funcionalidades Básicas
- [ ] Health check responde
- [ ] Endpoint HTML retorna content-type: text/html
- [ ] Endpoint CSV retorna content-type: text/csv
- [ ] Parâmetros de query funcionam (date, managementGroup, subscription)

### ✅ Conteúdo dos Relatórios
- [ ] HTML tem CSS inline e é responsivo
- [ ] CSV tem cabeçalhos corretos
- [ ] Ações classificadas: "Excluir", "Reduzir", "Revisar", "Monitorar"
- [ ] Valores monetários formatados corretamente
- [ ] Hierarquia organizacional respeitada

### ✅ Filtros e Parâmetros
- [ ] Filtro por data funciona
- [ ] Filtro por Management Group funciona  
- [ ] Filtro por Subscription funciona
- [ ] Data padrão (ontem) funciona quando não especificada

### ✅ Performance e Erro
- [ ] Relatórios respondem em < 30 segundos
- [ ] Errors retornam HTTP status codes apropriados
- [ ] Logs informativos aparecem no console

## 🔧 Comandos Úteis para Debug

```bash
# Verificar logs detalhados
func start --verbose

# Testar com curl e ver headers
curl -v http://localhost:7071/api/report/html

# Verificar storage local
# Browse: http://127.0.0.1:10000/devstoreaccount1/finops-analysis?restype=container&comp=list

# Verificar Service Bus (se usando)
az servicebus queue show --resource-group [rg] --namespace-name [namespace] --name [queue]
```

## 🎯 Próximos Passos (Produção)

1. **Deploy para Azure**: Usar pipeline `deploy-function.yml`
2. **Configurar RBAC**: Managed Identity com permissões Cost Management Reader
3. **Testar em produção**: Endpoints públicos com authentication
4. **Monitoramento**: Application Insights para métricas e alertas

---

**💡 Dica:** Comece sempre com o health check e dados simulados antes de partir para dados reais do Azure!