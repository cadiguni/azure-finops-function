# Service Bus para Paralelismo Otimizado - FinOps Platform
# 🚀 SOLUÇÃO: Queue-based processing para evitar timeouts

resource "azurerm_servicebus_namespace" "finops" {
  name                = "${local.aplicacao}-${local.setor}-servicebus"
  location            = local.localizacao
  resource_group_name = azurerm_resource_group.rg.name
  sku                 = "Basic"  # Basic: ~$5/mês vs Standard ~$15/mês
  
  tags = local.tags
}

# Queue para análises de subscriptions individuais
resource "azurerm_servicebus_queue" "subscription_analysis" {
  name         = "subscription-analysis"
  namespace_id = azurerm_servicebus_namespace.finops.id
  
  # ⚡ BASIC TIER SETTINGS (sem features avançadas)
  max_delivery_count                    = 10      # 🚨 AUMENTADO: 3→10 para evitar DLQ durante estabilização
  lock_duration                        = "PT5M"   # 5 minutos lock
  default_message_ttl                  = "P14D"   # 14 dias TTL
  
  # 📊 SIZE & BATCHING (Basic suporta até 1GB)
  max_size_in_megabytes               = 1024     # 1GB queue size
  
  # Basic não suporta duplicate detection nem auto delete
}

# Queue para Storage Account analysis (heavy workload)
resource "azurerm_servicebus_queue" "storage_analysis" {
  name         = "storage-analysis"
  namespace_id = azurerm_servicebus_namespace.finops.id
  
  # ⚡ OPTIMIZED for heavy Azure Monitor calls (Basic tier: max 5min lock)
  max_delivery_count                    = 10      # 🚨 AUMENTADO: 2→10 para subscriptions grandes
  lock_duration                        = "PT5M"   # 5 minutos (Basic tier maximum)
  default_message_ttl                  = "P7D"    # 7 dias TTL
  
  max_size_in_megabytes               = 1024     # Basic max: 1GB
}

# Queue para VM analysis (medium workload) 
resource "azurerm_servicebus_queue" "vm_analysis" {
  name         = "vm-analysis"
  namespace_id = azurerm_servicebus_namespace.finops.id
  
  max_delivery_count                    = 10      # 🚨 AUMENTADO: 3→10 para evitar DLQ
  lock_duration                        = "PT5M"   # 5 minutos (Basic tier maximum)
  default_message_ttl                  = "P7D"
  
  max_size_in_megabytes               = 1024
}

# Queue para App Service analysis (light workload)
resource "azurerm_servicebus_queue" "appservice_analysis" {
  name         = "appservice-analysis" 
  namespace_id = azurerm_servicebus_namespace.finops.id
  
  max_delivery_count                    = 10      # 🚨 AUMENTADO: 3→10 para evitar DLQ
  lock_duration                        = "PT5M"   # 5 minutos para App Service
  default_message_ttl                  = "P7D"
  
  max_size_in_megabytes               = 1024     # Basic tier max
}

# 📊 Queue para resultados consolidados (Basic não tem Topics)
resource "azurerm_servicebus_queue" "analysis_results" {
  name         = "analysis-results"
  namespace_id = azurerm_servicebus_namespace.finops.id
  
  # 📊 RESULTS QUEUE for consolidation
  max_delivery_count        = 10                    # 🚨 AUMENTADO: 3→10 para evitar DLQ
  lock_duration            = "PT2M"   # 2 minutos para consolidação
  default_message_ttl      = "P1D"    # 1 dia para resultados
  max_size_in_megabytes    = 1024
}

# 🚀 QUEUE EXCLUSIVA PARA PRODUÇÃO: Subscription grande com configurações otimizadas
resource "azurerm_servicebus_queue" "subscription_analysis_production" {
  name         = "subscription-analysis-production"
  namespace_id = azurerm_servicebus_namespace.finops.id
  
  # 🎯 CONFIGURAÇÕES ESPECÍFICAS PARA PRODUÇÃO (subscription: 504a622c-3995-46c5-8ba7-8edb365ed17b)
  max_delivery_count                    = 20      # 🚨 MUITO AUMENTADO: produção precisa de mais tentativas
  lock_duration                        = "PT5M"   # 5 minutos (Basic tier maximum)
  default_message_ttl                  = "P14D"   # 14 dias TTL (Basic tier maximum)
  
  # 📊 SIZE & BATCHING (produção pode ser maior)
  max_size_in_megabytes               = 1024     # 1GB queue size (Basic tier max)
  
  # NOTA: Esta queue vai rodar com concurrency=1, prefetch=0 para reduzir 429s
}
# 🔄 Queue para PROCESSAMENTO EM ETAPAS - Solução para timeouts
resource "azurerm_servicebus_queue" "subscription_analysis_steps" {
  name         = "subscription-analysis-steps"
  namespace_id = azurerm_servicebus_namespace.finops.id
  
  # ⚡ OTIMIZADO PARA STEPS RÁPIDOS (2-5 minutos cada)
  max_delivery_count                    = 5       # Steps são rápidos, menos retries necessários
  lock_duration                        = "PT2M"   # 2 minutos (steps são menores)
  default_message_ttl                  = "P7D"    # 7 dias TTL
  
  # 📊 SIZE menor (mínimo válido: 1024MB)
  max_size_in_megabytes               = 1024     # 1GB (mínimo válido para Basic tier)
}
# 🔐 RBAC: Dar permissões para a Managed Identity acessar Service Bus
# NOTA: Este role assignment precisa ser criado manualmente ou com permissões Owner/User Access Administrator
# resource "azurerm_role_assignment" "finops_servicebus_data_owner" {
#   scope                = azurerm_servicebus_namespace.finops.id
#   role_definition_name = "Azure Service Bus Data Owner"
#   principal_id         = azurerm_user_assigned_identity.finops_identity.principal_id
# }

# 📊 Outputs para connection strings
output "servicebus_connection_string" {
  description = "Service Bus connection string para Functions"
  value       = azurerm_servicebus_namespace.finops.default_primary_connection_string
  sensitive   = true
}

output "servicebus_namespace_name" {
  description = "Nome do Service Bus namespace"
  value       = azurerm_servicebus_namespace.finops.name
}