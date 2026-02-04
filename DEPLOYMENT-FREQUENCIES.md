# 🚀 CONFIGURAÇÃO DE FREQUÊNCIAS - AMBIENTE DEVELOPMENT vs PRODUCTION

## 📋 RESUMO EXECUTIVO

Este documento define as frequências profissionais de execução das Azure Functions baseadas em **estratégias FinOps** otimizadas para evitar throttling da Azure Monitor API.

## 🎯 ESTRATÉGIAS PROFISSIONAIS IMPLEMENTADAS

### 🟢 ANÁLISES DIÁRIAS (Resource Graph Only)
- **Public IP órfãos**: Rápido, sem métricas
- **Discos órfãos**: Rápido, sem métricas  
- **VMs paradas**: PowerState via Resource Graph

### 🟡 ANÁLISES 2X SEMANA (Azure Monitor Heavy)
- **Storage Account metrics**: TransactionCount, UsedCapacity, Ingress/Egress ✅ **MetricsQueryClient**
- **App Service Plan metrics**: CPUTime, Memory, HttpRequests ✅ **MetricsQueryClient**
- **VM Idle Analysis**: CPU Percentage, Network In/Out ✅ **MetricsQueryClient - NOVO!**
- **Executam apenas Terça-feira e Sexta-feira às 3:00 AM UTC**

## ⚡ STATUS DA MIGRAÇÃO PARA METRICSQUERYCLIENT

✅ **StorageAccountAnalyzer**: Migrado (V4.0) - Métricas reais de uso  
✅ **AppServiceAnalyzer**: Migrado (V4.0) - Descoberta de Web Apps + métricas reais  
✅ **IdleVmAnalyzer**: **RECÉM MIGRADO (V2.0)** - CPU + Network reais do Azure Monitor!  
⚪ **UnusedPublicIpAnalyzer**: Resource Graph apenas (não precisa métricas)  
⚪ **UnattachedDiskAnalyzer**: Resource Graph apenas (não precisa métricas)

## 📅 CRONOGRAMA DE EXECUÇÃO

### DEVELOPMENT (Testes Rápidos)
```bash
CostAnalysisTimer:  "0 */10 * * * *"  # A cada 10 minutos
DailySummary:       "0 */5 * * * *"   # A cada 5 minutos
```

### PRODUCTION (Frequências Reais)
```bash
CostAnalysisTimer:  "0 0 3 * * *"     # 3:00 AM UTC diariamente
DailySummary:       "0 0 */6 * * *"    # A cada 6 horas (4x/dia)
```

## 🔧 COMO CONFIGURAR PARA PRODUÇÃO

### 1️⃣ Atualizar TimerTriggers

**CostAnalysisTimerFunction.cs:**
```csharp
// ALTERAR DE:
[TimerTrigger("0 */10 * * * *")] TimerInfo timer, // 🧪 DESENVOLVIMENTO

// PARA:
[TimerTrigger("0 0 3 * * *")] TimerInfo timer, // 🚀 PRODUÇÃO
```

**DailySummaryFunction.cs:**
```csharp
// ALTERAR DE:
[TimerTrigger("0 */5 * * * *")] TimerInfo timer, // 🧪 DESENVOLVIMENTO

// PARA:
[TimerTrigger("0 0 */6 * * *")] TimerInfo timer, // 🚀 PRODUÇÃO
```

### 2️⃣ Configurar Environment no local.settings.json
```json
{
  "Values": {
    "ENVIRONMENT": "Production",  // Alterar de "Development"
    "AZURE_SUBSCRIPTION_ID": "sua-subscription-id-real"
  }
}
```

## 🎯 BENEFÍCIOS DA ARQUITETURA

### ✅ Redução de API Calls
- **80-90% menos chamadas** Azure Monitor API
- **Resource Graph first** para pré-filtragem
- **SemaphoreSlim(5,5)** para throttling control

### 📊 Frequências Otimizadas
- **Diário**: Análises leves (Resource Graph only)
- **2x semana**: Análises pesadas (Azure Monitor)
- **Summary**: 4x por dia para dashboards

### 🛡️ Prevenção de Throttling
- **Análise histórica**: Não executa se já tem dados do dia
- **Paralelismo controlado**: Máximo 5 requests simultâneos
- **Fallback inteligente**: Continua operação se uma métrica falhar

## 📈 MÉTRICAS DE SUCESSO

### Desenvolvimento
- Ciclo de testes: **10 minutos** para analysis, **5 minutos** para summary
- Ideal para debugs e validações rápidas

### Produção
- **Daily Analysis**: 1x por dia às 3:00 AM UTC
- **Heavy Analysis**: 2x por semana (Terça e Sexta)
- **Summaries**: 4x por dia (00:00, 06:00, 12:00, 18:00 UTC)

## 🔄 ROTINA DE DEPLOY

1. **Development**: Usar frequências altas para testes
2. **Staging**: Testar com frequências de produção em ambiente controlado
3. **Production**: Ativar frequências otimizadas

## 💡 MONITORAMENTO RECOMENDADO

- **Application Insights**: Monitorar execution time das functions
- **Azure Monitor**: Acompanhar rate limits e throttling
- **Storage Analytics**: Verificar crescimento dos resultados
- **Cost Analysis**: Validar impacto nos custos de execução

---

**📧 Contato**: Equipe FinOps - GVDASA  
**📅 Última atualização**: $(Get-Date -Format "yyyy-MM-dd")