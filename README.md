# Azure FinOps Platform 🏗️💰

**Plataforma Azure Functions para análise de custos e otimização de recursos Azure com arquitetura queue-based para alta performance e escala.**

> **Nota**: Este é um projeto pessoal desenvolvido para demonstrar conhecimentos em arquitetura Azure, Azure Functions, .NET, e práticas de FinOps. Todas as referências corporativas foram removidas para uso como portfolio.

## 🎯 **Projeto Focado em Azure Functions**

Este projeto foi otimizado para conter **apenas** os componentes essenciais:
- ✅ **Azure Functions** - Processamento serverless de análises FinOps
- ✅ **Analyzers** - Lógica de análise de recursos Azure
- ✅ **Queue Processing** - Arquitetura assíncrona e escalável  
- ✅ **Testes Unitários** - Coverage completo dos analyzers
- ✅ **Terraform** - IaC para deployment
- ✅ **Pipelines** - CI/CD automatizado

## 📊 Status do Projeto

✅ **PRODUCTION READY** - Arquitetura enterprise implementada  
✅ **Queue-Based Processing** - Paralelismo automático para múltiplas subscriptions  
✅ **Circuit Breaker Pattern** - Proteção contra throttling Azure Monitor API  
✅ **MetricsQueryClient** - 3 analyzers usando métricas reais  
✅ **Feature Flags** - Controle granular de analyzers  
✅ **Observability** - Métricas de negócio e health checks  

## 🏗️ Arquitetura Enterprise

### 🚀 Queue-Based Processing
```
Timer Function → Queue Storage → Parallel Queue Functions → Results
     ↓                ↓                    ↓                ↓
  1 execução      1 msg/subscription   N Functions      N Results
```

**Benefícios:**
- **100 subscriptions** = **100 execuções paralelas** automáticas
- **Escala horizontal** - Azure gerencia automaticamente  
- **80-90% mais barato** - só paga pelo que usa
- **Resiliente** - falha em 1 subscription não afeta outras

### 🛡️ Proteções Enterprise

**Circuit Breaker Service:**
- Protege Azure Monitor API de throttling
- Ajusta paralelismo automaticamente baseado na saúde
- Rate limiting inteligente

**Observability Service:**
- Métricas de negócio: `TotalSavingsFound`, `AnalyzerExecutionTime`
- Health checks automáticos
- Dashboard de monitoramento

## 🎯 Analyzers Implementados

| Analyzer | Tipo | Frequência | Status MetricsQueryClient |
|----------|------|------------|--------------------------|
| **Storage Account** | 🟡 Pesado | 2x semana | ✅ V4.0 - Métricas reais |
| **App Service** | 🟡 Pesado | 2x semana | ✅ V4.0 - CPU/Memory/HTTP |  
| **VM Idle** | 🟡 Pesado | 2x semana | ✅ V2.0 - CPU + Network reais |
| **Public IP órfãos** | 🟢 Leve | Diário | Resource Graph only |
| **Discos órfãos** | 🟢 Leve | Diário | Resource Graph only |

## 📅 Estratégia de Frequências FinOps

### 🟢 ANÁLISES DIÁRIAS (Resource Graph Only)
**Executam todos os dias às 3:00 AM UTC:**
- Public IP órfãos - Rápido, sem métricas
- Discos órfãos - Rápido, sem métricas  
- VMs paradas - PowerState via Resource Graph

### 🟡 ANÁLISES 2X SEMANA (Azure Monitor Heavy)  
**Executam apenas Terça-feira e Sexta-feira às 3:00 AM UTC:**
- Storage Account metrics - TransactionCount, UsedCapacity
- App Service Plan metrics - CPUTime, Memory, HttpRequests
- VM Idle analysis - CPU Percentage, Network In/Out

**Benefícios da estratégia:**
- **80-90% menos chamadas** Azure Monitor API
- **Resource Graph first** para pré-filtragem
- **Controle de throttling** via SemaphoreSlim

## ⚙️ Configuração e Deploy

### 🎚️ Configuração por Setor

O projeto agora usa **setor** em vez de múltiplos ambientes:

```yaml
# config.yml - Simplificado para NAP
variables:
  Setor: 'nap'
  NomeAplicacao: 'finops-api'
  ArtifactName: 'FinOpsBuildFunction-nap'
```

**Recursos criados:**
- Function App: `finops-api-nap-func`
- Resource Group: `finops-api-nap-rg`
- Storage Account: `finopsapinapfuncstg`

### 🔧 Configuração DEV vs PROD

#### 🧪 DESENVOLVIMENTO (local.settings.json)
```json
{
  "ENVIRONMENT": "Development",
  "CostAnalysisSchedule": "0 */10 * * * *",    // 10 minutos
  "DailySummarySchedule": "0 */5 * * * *"      // 5 minutos
}
```

#### 🚀 PRODUÇÃO (Azure Application Settings)
```json
{
  "ENVIRONMENT": "Production", 
  "CostAnalysisSchedule": "0 0 3 * * *",       // 3:00 AM UTC diário
  "DailySummarySchedule": "0 0 */6 * * *"      // A cada 6 horas
}
```

### 🎛️ Feature Flags
```json
{
  "EnableStorageAnalyzer": true,
  "EnableVmAnalyzer": true,
  "EnableAppServiceAnalyzer": true,
  "EnablePublicIpAnalyzer": true,
  "EnableDiskAnalyzer": true
}
```

### 🔒 Segurança e Permissões

**Service Principal único com acesso a múltiplas subscriptions:**

```csharp
// DefaultAzureCredential - funciona com Managed Identity (produção)
services.AddSingleton<DefaultAzureCredential>();
services.AddSingleton<ArmClient>(); // Para descoberta de recursos
services.AddSingleton<MetricsQueryClient>(); // Para métricas reais
```

**Permissões necessárias:**
- ✅ `Reader` - Resource Graph queries
- ✅ `Monitoring Reader` - MetricsQueryClient  
- ✅ `Storage Blob Data Contributor` - salvar resultados
- ✅ `Cost Management Reader` - (futuro para custos reais)

## 🚀 Pipeline de Deploy

### 📦 Build Pipeline
```yaml
# Estrutura robusta baseada no EDUmessenger
- task: DotNetCoreCLI@2
  inputs:
    command: publish
    modifyOutputPath: true
    zipAfterPublish: True
    arguments: 'src/Personal.FinOpsApi.AzureFunctions/Personal.FinOpsApi.AzureFunctions.csproj'
```

### 🔄 Deploy Pipeline
```yaml
variables:
  - group: FinOps-func-Personal  # Variable group específico do setor
  - name: tfvars
    value: '-var "aplicacao=$(NomeAplicacao)" -var "setor=$(Setor)"'

# Deploy usando terraform-function com backend state específico
backendAzureRmKey: "nap/finops-api-function.tfstate"
```

## ✅ Checklist de Deploy Produção

### 🔧 ANTES DO DEPLOY - Azure Portal

1. **Criar Variable Group** `FinOps-func-Personal`:
   ```
   ServiceConnection = <service-connection>
   TerraformAccessKey = <terraform-state-key>
   AZURE_SUBSCRIPTION_ID = <subscription-id>
   CostAnalysisSchedule = 0 0 3 * * *
   DailySummarySchedule = 0 0 */6 * * *
   ```

2. **Configurar Terraform Backend**:
   - Resource Group: `terraform-rg`
   - Storage Account: `personalterraformstate`
   - Container: `servicosbase`
   - Key: `personal/finops-api-function.tfstate`

### 🎯 APÓS DEPLOY - Verificações

1. **Testar Function App**:
   ```bash
   # Health check endpoint
   GET https://finops-api-nap-func.azurewebsites.net/api/SystemHealth
   ```

2. **Verificar Timer Triggers**:
   - Logs no Application Insights
   - Confirmar frequências corretas (3:00 AM UTC)
   - Validar análises 2x semana (Terça/Sexta)

3. **Monitor Métricas**:
   - Queue processing paralelo funcionando
   - Circuit breaker protegendo API calls
   - Observability metrics sendo coletadas

## 🎯 Cronograma Semanal Final

### 📅 **Segunda a Domingo** (3:00 AM UTC)
🟢 **Análises Leves** (~2-3 min):
- Public IP órfãos, Discos órfãos, VMs paradas

### 📅 **Terça e Sexta** (3:00 AM UTC)  
🟡 **Análises Pesadas** (~8-10 min):
- Storage + App Service + VM Idle (com Azure Monitor)

### 📅 **Diário** (00:00, 06:00, 12:00, 18:00 UTC)
📊 **Summary Generation**:
- Consolidação de dados, Top 10 recommendations

## 🎊 Arquitetura de Classe Enterprise

### ✅ Implementado
- **Queue-Based Processing** - Paralelismo automático
- **Circuit Breaker Pattern** - Proteção Azure Monitor API
- **Feature Flags System** - Controle granular
- **Observability Service** - Métricas de negócio
- **MetricsQueryClient Migration** - 3 analyzers com métricas reais
- **Professional CRON** - Baseado em variáveis de ambiente
- **Setor-Based Configuration** - NAP focused

### 🚀 Performance Esperada
- **10 subscriptions** = 10x mais rápido (paralelo)
- **100 subscriptions** = 100x mais rápido (paralelo)  
- **Custo 80-90% menor** - métricas pesadas só 2x/semana
- **Resiliente** - falha individual não afeta outras subscriptions

---

**Versão**: 2.0-enterprise  
**Última atualização**: Fevereiro 2026  
**Mantido por**: Personal Portfolio Project  
**Status**: ✅ Production Ready com arquitetura enterprise