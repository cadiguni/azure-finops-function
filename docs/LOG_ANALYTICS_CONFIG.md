# 📊 Configuração Log Analytics - Data Collector API

## ✅ Opção A (Data Collector API) - SIMPLES & DIRETA

Reversão da Opção B (DCR/DCE) para uma abordagem mais simples.

### 🔧 Configuração no Azure Function App

No Azure Portal, vá em **Function App → Configuration → Application Settings**:

```bash
# 📊 LOG ANALYTICS (Data Collector API)
LOG_ANALYTICS_WORKSPACE_ID = "bd651986-64f7-4512-bbc5-3e7bc7d79016"
LOG_ANALYTICS_SHARED_KEY = "56l2/gC9xF6lQhMEfTqev6y90H7LeJi70DnwMCAL8beY7PVAGsC3aq9Nd/f7aNmUNn3oZb8Hi+XzsyvQFrufXQ=="
LOG_ANALYTICS_LOG_TYPE = "FinOpsRecommendations"
```

### 📈 Como funciona

```
Azure Function
   ↓ (HTTP POST + HMAC Auth)
Log Analytics Workspace  
   ↓ (Cria automaticamente)
Tabela: FinOpsRecommendations_CL
   ↓
Dashboards / KQL / Workbooks
```

### ⚡ Vantagens vs Opção B (DCR)

| **Data Collector API (A)** | **DCR/DCE (B)** |
|---------------------------|------------------|
| ✅ Setup 2 minutos | ❌ Setup 20+ minutos |
| ✅ Apenas 2 variáveis | ❌ DCR + DCE + Managed Identity |
| ✅ Funciona imediatamente | ❌ Pode dar erro de permissão |
| ✅ Documentação clara | ❌ Documentação espalhada |
| ✅ Estável há anos | ⚠️ API nova (pode mudar) |

### 🔍 Consultas KQL (iguais em ambas opções)

```kql
// 📊 Top 10 recomendações por economia
FinOpsRecommendations_CL
| where TimeGenerated > ago(7d)
| summarize TotalSavings = sum(EstimatedMonthlySavings_d) by RecommendationType_s
| top 10 by TotalSavings desc

// 📈 Economia por subscription 
FinOpsRecommendations_CL
| where TimeGenerated > ago(30d) 
| summarize 
    Recommendations = count(),
    TotalSavings = sum(EstimatedMonthlySavings_d),
    AvgSavings = avg(EstimatedMonthlySavings_d)
  by SubscriptionId_s
| order by TotalSavings desc

// 🚨 High Priority por Resource Group
FinOpsRecommendations_CL
| where Priority_s == "High" 
| summarize count() by ResourceGroupName_s, RecommendationType_s
| order by count_ desc
```

### 🔄 Tabela criada automaticamente

Nome: **`FinOpsRecommendations_CL`**

Colunas geradas automaticamente:
- `RecommendationType_s` (string)
- `EstimatedMonthlySavings_d` (decimal)  
- `Priority_s` (string)
- `SubscriptionId_s` (string)
- `ResourceId_s` (string)
- `TimeGenerated` (datetime - automático)
- E todas as outras propriedades do `FinOpsLogEntry`

### 🛠️ Monitoramento

**Logs da Function:**
```bash
# Verificar se está enviando
✅ 15 recomendações enviadas com sucesso para Log Analytics
📈 UnderUtilizedVM: 8 recomendações, $342.50/mês economia
```

**Log Analytics:**
```kql
// Verificar se dados estão chegando
FinOpsRecommendations_CL
| where TimeGenerated > ago(1h)
| count
```

### 🚨 Troubleshooting

1. **Erro 403 (Forbidden)**: 
   - Verificar SHARED_KEY correto
   - Verificar WORKSPACE_ID correto

2. **Erro 400 (Bad Request)**:
   - JSON malformado (raro, JSON é validado)

3. **Dados não aparecem**:
   - Aguardar até 5-10 minutos (latência normal)
   - Verificar logs da Function

### 🔐 Segurança 

⚠️ **SHARED_KEY é sensível** - tratar como senha:
- ✅ Use Azure Key Vault em produção  
- ✅ Rotacionar chave regularmente
- ❌ Não commitar no código
- ❌ Não logar a chave

**Configuração com Key Vault (opcional):**
```bash
LOG_ANALYTICS_SHARED_KEY = "@Microsoft.KeyVault(SecretUri=https://vault.vault.azure.net/secrets/log-analytics-key/)"
```