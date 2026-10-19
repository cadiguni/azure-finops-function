// Tipos do fluxo simples de migração: o portal chama o Azure Resource Manager com o token do usuário.

export interface ArmSubscription {
  subscriptionId: string;
  displayName: string;
}

export interface ArmResourceGroup {
  id: string;
  name: string;
  location: string;
  tags?: Record<string, string>;
}

export interface ArmResource {
  id: string;
  name: string;
  type: string;
  location: string;
}

/** Operação assíncrona do ARM (validateMoveResources/moveResources), acompanhada pelo header Location. */
export interface ArmOperation {
  location: string;
  retryAfterSeconds: number;
}

export type ArmOperationResult =
  | { state: 'running'; retryAfterSeconds: number }
  | { state: 'succeeded' }
  | { state: 'failed'; message: string; details: string[] };
