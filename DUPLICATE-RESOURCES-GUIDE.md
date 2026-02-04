# 🔍 Análise de Recursos Duplicados em Múltiplas Assinaturas

## Como Funciona

A **Opção A** implementada detecta recursos duplicados comparando **nome + tipo** em múltiplas assinaturas simultaneamente.

### Exemplo de Uso

```bash
# POST para a função Azure
curl -X POST "https://your-function-app.azurewebsites.net/api/AnalyzeDuplicateResources?code=YOUR_FUNCTION_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "subscriptionIds": [
      "11111111-1111-1111-1111-111111111111",
      "22222222-2222-2222-2222-222222222222",
      "33333333-3333-3333-3333-333333333333"
    ]
  }'
```

### Resposta Exemplo

```json
{
  "totalDuplicateGroups": 15,
  "totalDuplicateResources": 42,
  "totalPotentialSavings": 1250.00,
  "analysisDate": "2026-02-04T10:30:00Z",
  "subscriptionsAnalyzed": [
    "11111111-1111-1111-1111-111111111111",
    "22222222-2222-2222-2222-222222222222"
  ],
  "topSavingsOpportunities": [
    {
      "resourceName": "vm-web-server",
      "resourceType": "Microsoft.Compute/virtualMachines",
      "count": 3,
      "potentialSavings": 300.00,
      "subscriptions": ["sub1", "sub2", "sub3"],
      "locations": ["East US", "West US"]
    }
  ],
  "duplicateGroups": [
    {
      "name": "vm-web-server",
      "resourceType": "Microsoft.Compute/virtualMachines",
      "count": 3,
      "potentialSavings": 300.00,
      "similarityScore": 1.0,
      "resources": [...]
    }
  ]
}
```

## Vantagens da Opção A

✅ **Funciona em Múltiplas Assinaturas**: Sim, coleta recursos de todas as assinaturas fornecidas
✅ **Simples e Confiável**: Comparação direta nome + tipo
✅ **Rápida Execução**: Não precisa de algoritmos complexos
✅ **Fácil Manutenção**: Lógica clara e direta
✅ **Estimativa de Economia**: Calcula potencial de economia por tipo de recurso

## Limitações

⚠️ **Apenas Nome Idêntico**: Não detecta recursos similares com nomes ligeiramente diferentes
⚠️ **Case Sensitive**: "VM-01" ≠ "vm-01"
⚠️ **Sem Context**: Não considera tags ou localização para refinamento

## Próximos Passos para Implantação

1. **Build e Deploy**: A função já está integrada no pipeline
2. **Permissões**: Certificar que a Managed Identity tem `Reader` nas assinaturas alvo
3. **Teste**: Executar com algumas assinaturas primeiro
4. **Automação**: Pode ser chamada via timer trigger ou manualmente