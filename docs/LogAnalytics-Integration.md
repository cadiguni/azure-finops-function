# 📊 Log Analytics Integration para Dashboards FinOps

## 🎯 Visão Geral

A integração com **Log Analytics** permite criar dashboards poderosos para visualizar recomendações de FinOps usando **KQL queries**, **Azure Workbooks** e **Grafana**.

## ⚙️ Configuração Necessária no Azure

### 1️⃣ **Log Analytics Workspace**

```bash
# Criar Log Analytics Workspace
az monitor log-analytics workspace create \
  --resource-group "rg-finops" \
  --workspace-name "law-finops-recommendations" \
  --location "East US"
```

### 2️⃣ **Data Collection Rule (DCR)**

Criar DCR com **Data Collection Endpoint (DCE)** e tabela customizada:

```json
{
  "properties": {
    "dataCollectionEndpointId": "/subscriptions/{subscription-id}/resourceGroups/{rg}/providers/Microsoft.Insights/dataCollectionEndpoints/{dce-name}",
    "streamDeclarations": {
      "Custom-FinOpsRecommendations": {
        "columns": [
          { "name": "TimeGenerated", "type": "datetime" },
          { "name": "analysisId", "type": "string" },
          { "name": "subscriptionId", "type": "string" },
          { "name": "resourceId", "type": "string" },
          { "name": "resourceGroupName", "type": "string" },
          { "name": "resourceName", "type": "string" },
          { "name": "resourceType", "type": "string" },
          { "name": "recommendationType", "type": "string" },
          { "name": "category", "type": "string" },
          { "name": "priority", "type": "string" },
          { "name": "estimatedMonthlySavings", "type": "real" },
          { "name": "action", "type": "string" },
          { "name": "description", "type": "string" },
          { "name": "location", "type": "string" },
          { "name": "resourceTags", "type": "string" },
          { "name": "analysisType", "type": "string" },
          { "name": "metrics", "type": "string" },
          { "name": "confidenceScore", "type": "int" },
          { "name": "currentMonthlyCost", "type": "real" }
        ]
      }
    },
    "destinations": {
      "logAnalytics": [
        {
          "workspaceResourceId": "/subscriptions/{subscription-id}/resourceGroups/{rg}/providers/Microsoft.OperationalInsights/workspaces/{workspace-name}",
          "name": "finops-law"
        }
      ]
    },
    "dataFlows": [
      {
        "streams": ["Custom-FinOpsRecommendations"],
        "destinations": ["finops-law"],
        "transformKql": "source",
        "outputStream": "Custom-FinOpsRecommendations_CL"
      }
    ]
  }
}
```

🎯 **IMPORTANTE:**
- **Stream**: `Custom-FinOpsRecommendations` (sem `_CL`)
- **Tabela criada**: `FinOpsRecommendations_CL` (com `_CL`)

### 3️⃣ **Managed Identity Permissions**

```bash
# Dar permissão RBAC na DCR (não no workspace!)
az role assignment create \
  --assignee "{function-app-managed-identity-id}" \
  --role "Monitoring Metrics Publisher" \
  --scope "/subscriptions/{subscription-id}/resourceGroups/{rg}/providers/Microsoft.Insights/dataCollectionRules/{dcr-name}"
```

⚠️ **CRÍTICO**: A permissão deve ser aplicada **NA DCR**, não no workspace!

## 🔧 Variáveis de Ambiente

Adicionar no `local.settings.json` (local) ou **Application Settings** (Azure):

```json
{
  "Values": {
    "LOG_ANALYTICS_DCE_ENDPOINT": "https://{dce-name}.{region}.ingest.monitor.azure.com",
    "LOG_ANALYTICS_DCR_IMMUTABLE_ID": "{dcr-immutable-id}",
    "LOG_ANALYTICS_STREAM_NAME": "Custom-FinOpsRecommendations"
  }
}
```

### Como obter os valores:

```bash
# DCE Endpoint (Data Collection Endpoint)
az monitor data-collection endpoint show \
  --name "{dce-name}" \
  --resource-group "{rg}" \
  --query "logsIngestion.endpoint"

# DCR Immutable ID  
az monitor data-collection rule show \
  --name "{dcr-name}" \
  --resource-group "{rg}" \
  --query "immutableId"
```

⚠️ **IMPORTANTE:**
- **Stream Name**: `Custom-FinOpsRecommendations` (SEM `_CL`)
- **Tabela no Log Analytics**: `FinOpsRecommendations_CL` (COM `_CL`)
- **Endpoint**: Deve ser `*.ingest.monitor.azure.com` (DCE endpoint, não workspace URL)

## 📊 Exemplos de KQL Queries

### **Top 20 Recursos com Maior Economia**
```kql
FinOpsRecommendations_CL
| top 20 by estimatedMonthlySavings_d desc
| project 
    TimeGenerated, 
    subscriptionId_s, 
    resourceName_s, 
    recommendationType_s, 
    estimatedMonthlySavings_d, 
    priority_s,
    action_s
```

### **Economia Total por Tipo de Recomendação**
```kql
FinOpsRecommendations_CL
| summarize 
    TotalSavings = sum(estimatedMonthlySavings_d),
    Count = count(),
    AvgConfidence = avg(confidenceScore_d)
    by recommendationType_s
| order by TotalSavings desc
```

### **Economia por Subscription**
```kql
FinOpsRecommendations_CL
| summarize 
    TotalSavings = sum(estimatedMonthlySavings_d),
    ResourceCount = count()
    by subscriptionId_s
| order by TotalSavings desc
```

### **Tendência de Economia ao Longo do Tempo**
```kql
FinOpsRecommendations_CL
| summarize 
    DailySavings = sum(estimatedMonthlySavings_d)
    by bin(TimeGenerated, 1d)
| render timechart
```

### **Recursos de Alta Prioridade por Resource Group**
```kql
FinOpsRecommendations_CL
| where priority_s == "High"
| summarize 
    HighPriorityCount = count(),
    TotalHighPrioritySavings = sum(estimatedMonthlySavings_d)
    by resourceGroupName_s, subscriptionId_s
| order by TotalHighPrioritySavings desc
```

### **Análise de Confiança vs Economia**
```kql
FinOpsRecommendations_CL
| where confidenceScore_d >= 80  // Alta confiança
| summarize 
    HighConfidenceSavings = sum(estimatedMonthlySavings_d),
    Count = count()
    by recommendationType_s
| order by HighConfidenceSavings desc
```

## 🎨 Dashboard no Azure Workbook

Exemplo de visualizações:

1. **📊 Cartões de Resumo:**
   - Total economia mensal potencial
   - Número total de recomendações
   - Economia média por recomendação
   - Top subscription com maior economia

2. **📈 Gráficos:**
   - Tendência de economia ao longo do tempo
   - Breakdown por tipo de recomendação
   - Mapa de calor por Resource Group
   - Distribuição de prioridades

3. **📋 Tabelas Detalhadas:**
   - Top recursos para otimizar
   - Recomendações por categoria
   - Status de implementação

## 🚨 Alertas Sugeridos

```kql
// Alerta: Nova recomendação de alta economia (>$500/mês)
FinOpsRecommendations_CL
| where estimatedMonthlySavings_d > 500
| where TimeGenerated > ago(1h)

// Alerta: Muitas recomendações em uma subscription
FinOpsRecommendations_CL
| where TimeGenerated > ago(1d)
| summarize count() by subscriptionId_s
| where count_ > 50
```

## 📝 Estrutura dos Dados Enviados

Cada recomendação gera um registro com:

- **Identificação:** analysisId, subscriptionId, resourceId
- **Categorização:** recommendationType, category, priority  
- **Financeiro:** estimatedMonthlySavings, currentMonthlyCost
- **Ação:** action, description, confidenceScore
- **Metadados:** location, resourceTags, metrics, analysisType

## 🔄 Fluxo de Funcionamento

1. **Function executa análise** (Timer ou Manual)
2. **Gera recomendações** (discos órfãos, VMs idle, etc.)
3. **Converte para FinOpsLogEntry** (formato otimizado para KQL)
4. **Envia para Data Collection Endpoint** via DCR → Stream: `Custom-FinOpsRecommendations`
5. **Dados aparecem na tabela** `FinOpsRecommendations_CL` (com sufixo `_CL`)
6. **Disponível para queries**, dashboards e alertas

## 🎯 **Conceito Importante**

- **Stream Name**: `Custom-FinOpsRecommendations` ← Usado no POST da API
- **Tabela Final**: `FinOpsRecommendations_CL` ← Usado nas queries KQL  
- **Endpoint**: `https://{dce-name}.{region}.ingest.monitor.azure.com` ← DCE endpoint
- **RBAC**: "Monitoring Metrics Publisher" **NA DCR** (não no workspace)

## 🎯 Benefícios

✅ **Dashboards nativos** com Azure Workbooks/Grafana  
✅ **Queries poderosas** com KQL  
✅ **Alertas automáticos** baseados em economia  
✅ **Histórico completo** de recomendações  
✅ **Análises de tendência** ao longo do tempo  
✅ **Integração nativa** com ecossistema Azure Monitor  

## 🛠️ Troubleshooting

### **Erro de Autenticação**
- Verificar se Managed Identity tem role "Monitoring Metrics Publisher"
- Confirmar DCR Immutable ID correto

### **Erro 404**
- Verificar DCR Endpoint e região
- Confirmar se DCR foi criado corretamente

### **Dados não aparecem**
- Aguardar até 5 minutos para ingestão
- Verificar se stream name está correto
- Confirmar formato JSON dos dados enviados