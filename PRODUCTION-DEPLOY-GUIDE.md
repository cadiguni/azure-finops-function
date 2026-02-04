# 🚀 GUIA DE DEPLOY PRODUÇÃO - Azure Functions FinOps

## ✅ **CONFIGURAÇÕES PROFISSIONAIS IMPLEMENTADAS**

### 🔧 **1. Timer Triggers com Variáveis de Ambiente**

✅ **Implementado**: CRON expressions agora usam variáveis de ambiente
```csharp
[TimerTrigger("%CostAnalysisSchedule%")]  // ← Profissional!
[TimerTrigger("%DailySummarySchedule%")]  // ← Profissional!
```

### ⏱️ **2. Timeout Configurado**

✅ **host.json**: 10 minutos de timeout (suficiente para múltiplas subscriptions)
```json
"functionTimeout": "00:10:00"
```

### 🏗️ **3. Configuração DEV vs PROD**

#### 🧪 **DESENVOLVIMENTO** (local.settings.json)
```json
{
  "CostAnalysisSchedule": "0 */10 * * * *",    // 10 minutos
  "DailySummarySchedule": "0 */5 * * * *"      // 5 minutos  
}
```

#### 🚀 **PRODUÇÃO** (Azure Application Settings)
```json
{
  "CostAnalysisSchedule": "0 0 3 * * *",       // 3:00 AM UTC diário
  "DailySummarySchedule": "0 0 */6 * * *"      // A cada 6 horas
}
```

## 📋 **CHECKLIST DE DEPLOY**

### ✅ **ANTES DO DEPLOY - Azure Portal**

1. **Criar Application Settings**:
   ```
   CostAnalysisSchedule = 0 0 3 * * *
   DailySummarySchedule = 0 0 */6 * * *
   AZURE_SUBSCRIPTION_ID = <sua-subscription-real>
   ENVIRONMENT = Production
   ```

2. **Configurar Managed Identity**:
   - Habilitar System Assigned Identity
   - Dar permissões: Reader + Monitoring Reader

3. **Configurar Storage Account**:
   - Criar container `finops-analysis`
   - Configurar connection string

### ✅ **APÓS DEPLOY - Verificações**

1. **Test Manual**:
   ```bash
   # Trigger manual para testar
   POST https://<function-app>.azurewebsites.net/admin/functions/CostAnalysisTimer
   ```

2. **Verificar Logs**:
   - Application Insights → Live Metrics
   - Verificar se não há errors de timeout
   - Confirmar frequências corretas

3. **Monitor First Run**:
   - Primeira execução deve demorar ~10min (múltiplas subs)
   - Verificar se análises 2x semana só rodam Terça/Sexta

## 📊 **FREQUÊNCIAS FINAIS EM PRODUÇÃO**

### 🗓️ **Cronograma Semanal**

#### **SEGUNDA a DOMINGO** (Diário - 3:00 AM UTC)
🟢 **Análises Leves** (~2-3 min):
- Public IP órfãos
- Discos órfãos  
- VMs paradas (PowerState)

#### **TERÇA e SEXTA** (3:00 AM UTC)
🟡 **Análises Pesadas** (~8-10 min):
- Storage Account metrics ✅ MetricsQueryClient
- App Service metrics ✅ MetricsQueryClient  
- VM Idle analysis ✅ MetricsQueryClient

#### **DIÁRIO** (00:00, 06:00, 12:00, 18:00 UTC)
📊 **Summary Generation**:
- Consolidação de dados
- Top 10 recommendations  
- Dashboard updates

## 🎯 **BENEFÍCIOS DA CONFIGURAÇÃO PROFISSIONAL**

✅ **Custo Otimizado**: 80-90% menos execuções vs desenvolvimento  
✅ **API Throttling**: Controle inteligente de chamadas Azure Monitor  
✅ **Configuração Flexível**: DEV vs PROD por variáveis de ambiente  
✅ **Timeout Adequado**: 10 minutos para múltiplas subscriptions  
✅ **Métricas Reais**: 3 analyzers usando MetricsQueryClient  
✅ **Scheduling Inteligente**: Análises pesadas só 2x por semana  

## 🚨 **ALERTAS IMPORTANTES**

⚠️ **NUNCA deploy com CRON fixo no código!**  
⚠️ **Sempre configurar Application Settings antes do deploy**  
⚠️ **Monitorar primeira execução para validar timeout**  
⚠️ **Verificar permissões de Managed Identity**  

---

**🎉 Sistema pronto para produção enterprise com configurações profissionais Azure Functions!**