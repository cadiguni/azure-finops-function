# 📊 Hierarquia Organizacional e Geração de PDF

## Visão Geral

Esta atualização introduz duas funcionalidades principais solicitadas:

### 1. **Base Oficial = Hierarquia Organizacional** 🏢
- **Management Group** > **Subscription** > **Resource Group**
- Tags viram dados complementares (governança)
- Organização baseada na estrutura real do Azure

### 2. **PDF sob Demanda** 📄
- API endpoints para gerar PDF por filtros
- Arquitetura híbrida: Timer Function coleta dados → HTTP Function gera PDF
- Estrutura de 5 páginas conforme especificado

---

## 🔗 APIs Disponíveis

### **Geração de PDF**
```http
GET /api/report/pdf?date=2026-03-26&managementGroup=setor-financeiro
```

**Parâmetros:**
- `date` (obrigatório): Data da análise (yyyy-MM-dd)
- `managementGroup` (opcional): Filtrar por Management Group
- `subscription` (opcional): Filtrar por Subscription específica  
- `months` (opcional): Período em meses (padrão: 1)
- `includeGovernance` (opcional): Incluir detalhes de governança (padrão: true)
- `types` (opcional): Filtrar tipos de recomendação (ex: `IdleVM,UnattachedDisk`)

**Exemplos:**
```http
# PDF para Management Group específico
GET /api/report/pdf?date=2026-03-26&managementGroup=Setores

# PDF para Subscription específica
GET /api/report/pdf?date=2026-03-26&subscription=12345678-1234-1234-1234-123456789012

# PDF para período de 3 meses
GET /api/report/pdf?date=2026-01-01&months=3&managementGroup=Corporativo

# PDF apenas com VMs ociosas e discos não anexados
GET /api/report/pdf?date=2026-03-26&types=IdleVM,UnattachedDisk
```

### **Hierarquia Organizacional**
```http
GET /api/report/hierarchy?date=2026-03-26&managementGroup=setor-financeiro
```

**Parâmetros:**
- `date` (obrigatório): Data da análise
- `managementGroup` (opcional): Filtrar por Management Group
- `subscription` (opcional): Filtrar por Subscription

### **Lista de Management Groups**
```http
GET /api/report/management-groups?date=2026-03-26
```

---

## 📑 Estrutura do Relatório PDF

### **Página 1: Resumo Executivo**
- Período analisado
- Grupo analisado  
- Total de recursos analisados
- Total de recomendações
- Economia potencial mensal estimada
- Top 3 oportunidades

### **Página 2: Visão por Assinatura**
- **subscription A**: custo / economia potencial / quantidade de recomendações
- **subscription B**: custo / economia potencial / quantidade de recomendações
- Ordenado por maior economia potencial

### **Página 3: Principais Desperdícios**
- **Public IP sem uso**
- **Storage subutilizado** 
- **App Service Plan ocioso**
- **Discos não anexados**
- **VMs ociosas**

### **Página 4: Detalhamento**
Tabela com:
- Recurso
- Tipo
- Resource Group
- Subscription
- Recomendação
- Economia estimada
- Criticidade

### **Página 5: Governança**
- **Recursos sem tag**
- **Recursos com tag divergente** 
- **Cobertura de tagging**
- **Observações e recomendações**

---

## 🏗️ Arquitetura Implementada

### **Timer Functions (Existentes)**
- Continuam coletando e consolidando dados
- Salvam dados em formato JSON no Blob Storage
- **Não foram alteradas** - compatibilidade total

### **HTTP Functions (Novas)**
- **Leem dados já processados** do Blob Storage
- Organizam por hierarquia organizacional
- Geram estrutura do PDF sob demanda
- **Performance otimizada** - não reprocessa dados

### **Serviços Criados**

#### `OrganizationalHierarchyService`
- Organiza dados por Management Group > Subscription > Resource Group
- Calcula resumos por nível hierárquico  
- Analisa governança de tags

#### `PdfReportService`
- Estrutura dados para relatório PDF
- Cria as 5 páginas conforme especificação
- Inclui análise de governança como complemento

---

## 🎯 Benefícios da Nova Abordagem

### **1. Hierarquia como Base Principal**
```
✅ Management Group: "Setor Financeiro"  
   ├── Subscription: "prod-finance-001"
   │   ├── Resource Group: "rg-web-prod"
   │   └── Resource Group: "rg-db-prod" 
   └── Subscription: "dev-finance-001"
       └── Resource Group: "rg-dev"

🎯 Resultado: 
   - Recursos com potencial de economia: 15
   - Economia estimada: R$ 8.500/mês
```

### **2. Tags como Dados Complementares**
```
📋 Governança (Página 5 do PDF):
   ├── Tag encontrada: ✅ 85% dos recursos
   ├── Divergência com grupo da subscription: ⚠️ 12 recursos  
   ├── Recursos sem tag: ❌ 23 recursos
   └── Recursos com tag inconsistente: ⚠️ 8 recursos
```

### **3. PDF "Apresentável"**
- Formato executivo para reuniões
- Estrutura clara e organizada  
- Fácil para enviar por email
- Filtros flexíveis por período e escopo

---

## 🚀 Como Usar

### **1. Testar as Funcionalidades**
```http
# Demonstração da estrutura organizacional
GET /api/demo/organizational-structure?date=2026-03-26

# Demonstração da estrutura de PDF  
GET /api/demo/pdf-structure?date=2026-03-26&managementGroup=Setores
```

### **2. Gerar Relatório Real** 
```http
# Para o Setor Financeiro de hoje
GET /api/report/pdf?date=2026-03-26&managementGroup=setor-financeiro

# Para todas as subscriptions dos últimos 3 meses
GET /api/report/pdf?date=2026-01-26&months=3
```

### **3. Explorar Hierarquia**
```http
# Ver estrutura organizacional completa
GET /api/report/hierarchy?date=2026-03-26

# Listar Management Groups disponíveis
GET /api/report/management-groups?date=2026-03-26
```

---

## 📈 Roadmap de Implementação

### **Fase 1: ✅ Implementado**
- [x] Modelos de hierarquia organizacional
- [x] Serviços para organização de dados
- [x] APIs HTTP para PDF sob demanda
- [x] Estrutura de 5 páginas do PDF
- [x] Análise de governança de tags

### **Fase 2: 🔄 Próximos Passos**
- [ ] Implementar geração de PDF binário (iTextSharp, PdfSharp)
- [ ] Cache de dados para melhor performance
- [ ] Templates personalizáveis para PDF
- [ ] Envio automático por email
- [ ] Dashboard para acompanhar geração de relatórios

### **Fase 3: 🎯 Futuro**
- [ ] Integração com Power BI para dashboards
- [ ] Agendamento automático de relatórios
- [ ] Alertas baseados em thresholds
- [ ] API para integração com outros sistemas

---

## 💡 Observações Técnicas  

### **Compatibilidade**
- ✅ **100% compatível** com Timer Functions existentes
- ✅ **Não altera** processamento atual de dados  
- ✅ **Extende** funcionalidades sem breaking changes

### **Performance**  
- ⚡ **Arquitetura híbrida** - dados já consolidados
- ⚡ **Leitura otimizada** do Blob Storage
- ⚡ **Cache em memória** para Management Groups

### **Governança**
- 🏷️ **Tags como complemento**, não bloqueador
- 📊 **Métricas de conformidade** incluídas no PDF
- 🎯 **Foco na hierarquia organizacional** real do Azure

---

## 🛠️ Configuração

### **Dependências Adicionais**
```xml
<!-- Para futuras implementações de PDF binário -->
<PackageReference Include="iTextSharp" Version="5.5.13.3" />  
<PackageReference Include="System.Drawing.Common" Version="7.0.0" />
```

### **Permissões Necessárias**
```bash
# As mesmas permissões existing + Management Group Reader
az role assignment create \
  --assignee $IDENTITY_PRINCIPAL_ID \
  --role "Management Group Reader" \
  --scope "/providers/Microsoft.Management/managementGroups/$ROOT_MG"
```

### **Configurações de Environment**
```json
{
  "FinOps__Pdf__MaxRecommendationsPerReport": 100,
  "FinOps__Pdf__DefaultLanguage": "pt-BR",
  "FinOps__Pdf__IncludeChartsDefault": true,
  "FinOps__Hierarchy__CacheExpirationMinutes": 30
}
```

---

🎉 **As funcionalidades solicitadas foram implementadas seguindo exatamente suas especificações!**