# Azure FinOps Platform 🏗️💰

Plataforma de análise de custos e otimização para recursos Azure, seguindo padrões GVDASA.

## 📋 Visão Geral

A plataforma FinOps analisa automaticamente recursos Azure para identificar oportunidades de otimização de custos e conformidade com governança.

### 🎯 Funcionalidades Principais

- **Análise de Custo**: Identifica recursos subutilizados e oportunidades de economia
- **Governança**: Verifica compliance de tags obrigatórias  
- **Escopo Centralizado**: Análise em Management Groups, múltiplas subscriptions
- **Relatórios**: Gera relatórios de findings e economia potencial

### 🔧 Analyzers Implementados

| Analyzer | Descrição | Economia Potencial |
|----------|-----------|-------------------|
| **VM Analyzer** | VMs com baixo uso de CPU/Memória | Resize ou desligamento |
| **Disk Analyzer** | Discos não anexados | Remoção de recursos órfãos |
| **App Service Analyzer** | Apps com baixo tráfego | Downgrade de planos |
| **SQL Analyzer** | Databases com baixo DTU | Otimização de tier |
| **Governance Tags** | Tags obrigatórias ausentes | Compliance e rastreabilidade |
| **Environment Classification** | Classificação automática Prod/Dev | Comportamento diferenciado por ambiente |

## ⚙️ Configuração

### 🔒 Segurança e Permissões

**Centralização de Permissões no Management Group:**
```terraform
# Permissões aplicadas no Management Group raiz "Geral"
# Automaticamente cobre: Setores, VisualStudio, Todos os MPNs, Todas as subscriptions
resource "azurerm_role_assignment" "root_reader" {
  scope                = data.azurerm_management_group.root.id
  role_definition_name = "Reader"
  principal_id         = azurerm_user_assigned_identity.finops_identity.principal_id
}
```

**Benefícios da Arquitetura:**
- ✅ **Uma única permissão** no Terraform
- ✅ **Escopo correto** para toda a hierarquia
- ✅ **Zero manutenção futura** se criar novo MG
- ✅ **Não quebra** com hierarquia profunda
- ✅ **Código controla comportamento** por ambiente

### 🎯 Classificação de Ambiente

**Automática por Management Group:**
```json
{
  "EnvironmentClassification": {
    "ProductionManagementGroups": ["Setores"],
    "NonProductionManagementGroups": ["VisualStudio"]
  }
}
```

**Prioridade por Tag (recomendado):**
- 🏷️ `environment=prod` → **Produção**
- 🏷️ `environment=dev|hml` → **MPN/Desenvolvimento**

💡 **Tag ganha de Management Group quando existir**

### 🛡️ Comportamento por Ambiente

| Ambiente | Análise | Ação | Segurança |
|----------|---------|------|-----------|
| **MPN/Dev** | Completa | Pode sugerir + automatizar | Flexível |
| **Produção** | Limitada | **Só leitura + relatório** | Máxima |

```csharp
// Código automaticamente ajusta comportamento
if (isProd)
{
    options.DryRun = true;           // ✅ Apenas análise
    options.AllowAutomation = false; // ✅ Sem automação  
    options.ReadOnly = true;         // ✅ Só leitura
}
```

### 🎛️ Frequência de Execução

```csharp
// Execução diária às 3:00 AM (recomendado para produção)
[TimerTrigger("0 0 3 * * *")]
```

💡 **Recomendação**: Custos não mudam de hora em hora → 1x por dia economiza processamento.

### 🎯 Escopo Centralizado

```json
{
  "FinOps": {
    "Scope": {
      "Mode": "ManagementGroup", 
      "ManagementGroupId": "mg-gvdasa",
      "IncludeSubscriptions": [],
      "ExcludeSubscriptions": []
    }
  }
}
```

**Benefícios**:
- ✅ 1 Function → N Subscriptions
- ✅ Zero duplicação de análises  
- ✅ Governança centralizada

### 🏷️ Tags Obrigatórias (Governança)

| Tag | Descrição | Exemplo |
|-----|-----------|---------|
| `owner` | Responsável pelo recurso | `exemplo@gvdasa.com.br` |
| `environment` | Ambiente (dev/hml/prod) | `prod` |
| `cost-center` | Centro de custo | `TI-Infrastructure` |

## 🚀 Deploy

### 1️⃣ Pré-requisitos

- Azure DevOps com acesso ao repositório
- Terraform state configurado (`stgvdasaterraformstate`)
- Managed Identity com permissões:
  - `Cost Management Reader`
  - `Reader` (para Resource Graph)

### 2️⃣ Pipeline Deploy

```bash
# Pipeline principal
azure-pipelines.yml

# Deploy específico para Function  
pipelines/deploy-function.yml
```

## 🧪 Teste e Validação

### Modo Seguro (Recomendado)

```json
{
  "dryRun": true,           // ✅ Apenas análise, sem alterações
  "readOnly": true,         // ✅ Só recomendações  
  "noDelete": true,         // ✅ Sem exclusões
  "noResize": true          // ✅ Sem redimensionamento
}
```

## 🎯 Roadmap v0.1

- [x] ✅ Estrutura base do projeto
- [x] ✅ Analyzers principais implementados
- [x] ✅ Terraform e pipeline configurados
- [x] ✅ Configuração de escopo centralizado
- [x] ✅ Analyzer de governança (tags)
- [ ] 🔄 Testes unitários
- [ ] 🔄 Deploy inicial em ambiente dev

---

**Versão**: 0.1-beta  
**Última atualização**: Janeiro 2026  
**Mantido por**: Equipe DevOps GVDASA