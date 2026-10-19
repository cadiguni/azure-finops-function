# O módulo avançado de migrações (perfis, fila, jobs no Cosmos, reconciliação via DevOps) foi substituído
# pelo fluxo simples do portal, que chama o ARM com o token do próprio usuário e não usa recursos do FinOps.
#
# Os blocos abaixo tiram esses recursos do state SEM apagá-los no Azure (destroy = false), para o plan
# não conter destroy. Depois de aplicado em prod e hml, os recursos órfãos podem ser removidos à mão:
# container Cosmos "migration-jobs", fila Service Bus "migration-steps" e as role assignments da identidade
# do FinOps nessa fila e no container "finops-config". Em seguida este arquivo pode ser apagado.

removed {
  from = azurerm_cosmosdb_sql_container.migration_jobs
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_servicebus_queue.migration_steps
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_role_assignment.migration_sender
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_role_assignment.migration_receiver
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_role_assignment.migration_profiles
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_role_definition.migration_source
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_role_assignment.migration_source
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_role_assignment.migration_source_reader
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_role_definition.migration_target
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_role_assignment.migration_target
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_role_assignment.migration_state_blob
  lifecycle {
    destroy = false
  }
}
