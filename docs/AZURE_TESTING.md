# 🧪 Teste dos Novos Endpoints - Azure 

## 🌐 URLs dos Novos Relatórios

### 📊 **Endpoints Criados:**
```bash
# Relatório HTML (interativo)
GET https://finopsplatform-nap-func.azurewebsites.net/api/report/html

# Relatório CSV (para análise)  
GET https://finopsplatform-nap-func.azurewebsites.net/api/report/csv
```

### ✅ **1. Health Check Primeiro**
```bash
# Verificar se a Function está online
curl https://finopsplatform-nap-func.azurewebsites.net/api/health
```

### 🎨 **2. Teste de Relatório HTML**
```bash
# Acesse no browser (relatório visual)
https://finopsplatform-nap-func.azurewebsites.net/api/report/html

# Com filtros
https://finopsplatform-nap-func.azurewebsites.net/api/report/html?date=2024-04-22&managementGroup=NAP
```

### 📄 **3. Teste de Relatório CSV** 
```bash
# Download direto (abrirá salvar arquivo)
https://finopsplatform-nap-func.azurewebsites.net/api/report/csv

# Com filtros específicos
https://finopsplatform-nap-func.azurewebsites.net/api/report/csv?date=2024-04-22
```

### 🔧 **4. APIs Existentes (Para Comparar)**
```bash
# Management Groups (deve funcionar)
https://finopsplatform-nap-func.azurewebsites.net/api/real/management-groups

# Manual Test (sua função de teste)
https://finopsplatform-nap-func.azurewebsites.net/api/ManualCostAnalysis

# Grafana Health
https://finopsplatform-nap-func.azurewebsites.net/api/grafana/health
```

## 📋 **Checklist de Teste**

### ✅ Primeiro Passo - Verificar Básico
- [ ] Health check responde `200 OK`
- [ ] Management Groups funciona (valida Azure auth)
- [ ] Sem erros 500 nos logs

### ✅ Segundo Passo - Novos Endpoints
- [ ] `/api/report/html` retorna HTML com CSS
- [ ] `/api/report/csv` retorna CSV válido
- [ ] Headers corretos (Content-Type)
- [ ] Sem timeout (< 30s)

### ✅ Terceiro Passo - Dados
- [ ] Relatório não está vazio
- [ ] Ações classificadas ("Excluir", "Reduzir", etc.)
- [ ] Valores monetários aparecem formatados
- [ ] Hierarquia organizacional funciona

## 🔍 **Se Algo Der Errado**

### ❌ Error 500 / Function Error
```bash
# Ver logs detalhados
az functionapp logs tail --name finopsplatform-nap-func --resource-group finopsplatform-nap-rg
```

### ❌ "No data found for date"
- Dados podem não existir para a data
- Tente sem parâmetros (usa ontem por padrão)
- Execute análise manual primeiro: `/api/ManualCostAnalysis`

### ❌ Authorization Error
- Managed Identity pode precisar permissões
- Verificar se análises já existem no Storage

---

## 🎯 **Próximo Passo Imediato**

**Teste agora mesmo** clicando aqui:
👆 https://finopsplatform-nap-func.azurewebsites.net/api/health

Se saírem dados, estamos prontos para os relatórios! 🚀