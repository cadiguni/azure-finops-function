terraform {
  backend "azurerm" {
    storage_account_name = "personalterraformstate"
    container_name       = "personal"
  }
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = ">=4.0.0, <5.0.0"
    }
  }
}