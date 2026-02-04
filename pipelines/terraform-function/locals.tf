locals {
  dominio = "gvdasa.com.br"
  tags = {
    Responsável = "tbauer@gvdasa.com.br"
    Setor = "NAP"
  }

  localizacao = "Brazil South"
  aplicacao   = var.aplicacao
  setor       = var.setor

  # Configurações específicas do FinOps
  finops_settings = {
    function_app_name = "${local.aplicacao}-${local.setor}-func"
    app_service_plan_name = "${local.aplicacao}-${local.setor}-plan"
    application_insights_name = "${local.aplicacao}-${local.setor}-ai"
    managed_identity_name = "${local.aplicacao}-${local.setor}-identity"
    
    # Configurações da Function
    function_settings = {
      "FUNCTIONS_WORKER_RUNTIME" = "dotnet-isolated"
      "WEBSITE_RUN_FROM_PACKAGE" = "1"
      "FUNCTIONS_EXTENSION_VERSION" = "~4"
      "WEBSITE_USE_PLACEHOLDER_DOTNETISOLATED" = "1"
      
      # Configurações específicas do FinOps
      "FinOps__Analyzer__EnableVmAnalysis" = "true"
      "FinOps__Analyzer__EnableDiskAnalysis" = "true"
      "FinOps__Analyzer__EnableAppServiceAnalysis" = "true"
      "FinOps__Analyzer__EnableSqlAnalysis" = "true"
      "FinOps__Analyzer__MinimumCostToAnalyze" = "50.0"
      "FinOps__Analyzer__LowCpuThreshold" = "5.0"
      "FinOps__Analyzer__DaysInactiveThreshold" = "7"
      
      # Classificação de ambiente e comportamento
      "FinOps__EnvironmentClassification__ProductionManagementGroups" = jsonencode(["Setores"])
      "FinOps__EnvironmentClassification__NonProductionManagementGroups" = jsonencode(["Visual Studio"])
      "FinOps__Scope__RootManagementGroup" = var.root_management_group
      "FinOps__Behavior__DryRunInProduction" = "true"
      "FinOps__Behavior__AllowAutomationInProduction" = "false"
    }
  }

  # Habilitar alertas apenas para produção
  alerts_enabled = true  # Sempre habilitado para NAP

  # Configurações para alertas
  alert_settings = {
    enabled = local.alerts_enabled
    interval = {
      frequency = "PT5M"
      window_size = "PT15M"
    }
    
    action_groups = local.alerts_enabled ? {
      "${local.aplicacao}-${local.setor}-ag" = {
        name = "${local.aplicacao}-${local.setor}-ag"
        short_name = substr("${local.aplicacao}-${local.setor}-ag", 0, 12)
        email_receivers = [ ]
        webhook_receivers = [
          {
            name = "TeamsLogicApp"
          }
        ]
      }
    } : {}

    # Alertas específicos para Cost Optimizer Function
    function_alerts = local.alerts_enabled ? {
      "function-failures" = {
        name = "function-failures"
        description = "Alerta para falhas na Function do Cost Optimizer"
        severity = "1"
        threshold = 5
      },
      "function-duration" = {
        name = "function-duration" 
        description = "Alerta para execuções longas da Function"
        severity = "2"
        threshold = 300000 # 5 minutos em ms
      }
    } : {}

    # Lista de alertas de performance
    performance_cpu_alerts = local.alerts_enabled ? {
      "high-cpu" = {
        name = "high-cpu"
        description = "Alerta de CPU alta"
        severity = "3"
        threshold = 80
        resource_id = azurerm_service_plan.app_plan.id
      }
    } : {}

    performance_memory_alerts = local.alerts_enabled ? {
      "high-memory" = {
        name = "high-memory"
        description = "Alerta de memória alta"
        severity = "3"
        threshold = 90
        resource_id = azurerm_service_plan.app_plan.id
      }
    } : {}

    # Lista de alertas HTTP
    http_5xx_alerts = local.alerts_enabled ? {
      "server-errors" = {
        name = "server-errors"
        description = "Alerta de erros 5xx"
        severity = "1"
        threshold = 5  # Threshold para NAP
        resource_id = azurerm_linux_web_app.webapp_a.id
      }
    } : {}

    http_403_alerts = local.alerts_enabled ? {
      "forbidden-errors" = {
        name = "forbidden-errors"
        description = "Alerta de erros 403"
        severity = "2"
        threshold = 20
        resource_id = azurerm_linux_web_app.webapp_a.id
      }
    } : {}

    response_time_alerts = local.alerts_enabled ? {
      "slow-response" = {
        name = "slow-response"
        description = "Alerta de resposta lenta"
        severity = "2"
        threshold = 5000
        resource_id = azurerm_linux_web_app.webapp_a.id
      },
      "slow-response2" = {
        name = "slow-response"
        description = "Alerta de resposta lenta"
        severity = "1"
        threshold = 500
        resource_id = azurerm_linux_web_app.webapp_a.id
      }
    } : {}

    # Lista de alertas de disponibilidade
    availability_alerts = local.alerts_enabled ? {
      "availability" = {
        name = "availability"
        description = "Alerta de disponibilidade"
        severity = "1"
        threshold = 99.9
        resource_id = azurerm_linux_web_app.webapp_a.id
      }
    } : {}

    # Lista de alertas de App Service Plan
    app_service_plan_queue_alerts = local.alerts_enabled ? {
      "http-queue-length" = {
        name = "http-queue-length"
        description = "Alerta de fila HTTP longa, indicando possível gargalo de requisições"
        severity = "2"
        threshold = 10
        resource_id = azurerm_service_plan.app_plan.id
      }
    } : {}
  }

  # Variável auxiliar para action group
  action_group_id = try(azurerm_monitor_action_group.action_groups[keys(local.alert_settings.action_groups)[0]].id, null)
}

# Data source para obter informações da assinatura atual
data "azurerm_subscription" "current" {}
