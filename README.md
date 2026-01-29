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

## ⚙️ Configuração

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