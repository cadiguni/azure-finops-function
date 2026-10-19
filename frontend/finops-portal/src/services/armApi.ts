import { InteractionRequiredAuthError } from '@azure/msal-browser';
import { armTokenRequest } from '../auth/authConfig';
import { msalInstance } from '../auth/msalInstance';
import type { ArmOperation, ArmOperationResult, ArmResource, ArmResourceGroup, ArmSubscription } from '../types/migrations';

// O ARM aceita CORS e expõe Location/Retry-After nas respostas 202 de move.
const ARM = 'https://management.azure.com';
const SUBSCRIPTIONS_API = '2022-12-01';
const RESOURCES_API = '2021-04-01';

export class ArmError extends Error {
  status: number;
  code?: string;
  details: string[];

  constructor(status: number, message: string, code?: string, details: string[] = []) {
    super(message);
    this.status = status;
    this.code = code;
    this.details = details;
  }
}

async function getArmToken(): Promise<string> {
  const account = msalInstance.getActiveAccount() ?? msalInstance.getAllAccounts()[0];
  if (!account) {
    await msalInstance.loginRedirect();
    throw new Error('Usuário não autenticado.');
  }
  try {
    return (await msalInstance.acquireTokenSilent({ ...armTokenRequest, account })).accessToken;
  } catch (error) {
    // Primeiro acesso ao ARM pelo portal: pede o consentimento da permissão delegada.
    if (error instanceof InteractionRequiredAuthError) {
      await msalInstance.acquireTokenRedirect({ ...armTokenRequest, account });
    }
    throw error;
  }
}

interface ArmErrorDetail { code?: string; message?: string; target?: string; details?: ArmErrorDetail[] }
interface ArmErrorBody { error?: ArmErrorDetail }

/**
 * Achata os details do ARM. Alguns erros só trazem o que importa em níveis internos: em
 * MissingMoveDependentResources, os IDs dos recursos ausentes vêm em details aninhados.
 */
function flattenDetails(details: ArmErrorDetail[] | undefined, depth = 0): string[] {
  if (!details || depth > 4) return [];
  return details.flatMap(d => {
    const text = [d.code, d.message].filter(Boolean).join(': ');
    const target = d.target && !text.includes(d.target) ? ` (${d.target})` : '';
    return [...(text ? [text + target] : []), ...flattenDetails(d.details, depth + 1)];
  });
}

async function readArmError(res: Response): Promise<ArmError> {
  let body: ArmErrorBody | null = null;
  try { body = await res.json() as ArmErrorBody; } catch { /* corpo vazio ou não JSON */ }
  const error = body?.error;
  const details = flattenDetails(error?.details);
  const message = error?.message ?? (res.status === 403
    ? 'Sem permissão no Azure para esta operação.'
    : `Falha ${res.status} ao chamar o Azure Resource Manager.`);
  return new ArmError(res.status, message, error?.code, details);
}

async function armFetch(urlOrPath: string, init: RequestInit = {}): Promise<Response> {
  const url = new URL(urlOrPath, ARM);
  if (url.origin !== ARM) throw new Error('URL de operação fora do Azure Resource Manager.');
  const token = await getArmToken();
  return fetch(url.toString(), {
    ...init,
    cache: 'no-store',
    headers: { Authorization: `Bearer ${token}`, Accept: 'application/json', ...(init.body ? { 'Content-Type': 'application/json' } : {}) },
  });
}

async function listAll<T>(path: string): Promise<T[]> {
  // O nextLink só é seguido se continuar na mesma listagem: mesmo host e mesmo caminho, mudando só a query (skiptoken).
  const expectedPath = new URL(path, ARM).pathname.toLowerCase();
  const items: T[] = [];
  let next: string | undefined = path;
  while (next) {
    const res = await armFetch(next);
    if (!res.ok) throw await readArmError(res);
    const page = await res.json() as { value?: T[]; nextLink?: string };
    items.push(...(page.value ?? []));
    next = page.nextLink;
    if (next) {
      const link = new URL(next, ARM);
      if (link.origin !== ARM || link.pathname.toLowerCase() !== expectedPath) {
        throw new Error('Paginação do Azure apontou para fora da listagem solicitada.');
      }
    }
  }
  return items;
}

const byName = <T extends { name: string }>(a: T, b: T) => a.name.localeCompare(b.name);

export async function listSubscriptions(): Promise<ArmSubscription[]> {
  const subscriptions = await listAll<ArmSubscription & { state?: string }>(`/subscriptions?api-version=${SUBSCRIPTIONS_API}`);
  return subscriptions
    .filter(s => !s.state || s.state === 'Enabled')
    .map(({ subscriptionId, displayName }) => ({ subscriptionId, displayName }))
    .sort((a, b) => a.displayName.localeCompare(b.displayName));
}

export async function listResourceGroups(subscriptionId: string): Promise<ArmResourceGroup[]> {
  const groups = await listAll<ArmResourceGroup>(`/subscriptions/${encodeURIComponent(subscriptionId)}/resourcegroups?api-version=${RESOURCES_API}`);
  return groups.map(({ id, name, location, tags }) => ({ id, name, location, tags })).sort(byName);
}

/**
 * Recursos de primeiro nível do RG (os filhos acompanham o pai no move). O ARM também lista alguns filhos
 * (Microsoft.Sql/servers/databases, inclusive "master"; Microsoft.ContainerRegistry/registries/webhooks), e
 * o move recusa a requisição inteira com ResourceNotTopLevel se algum deles for enviado.
 */
export async function listResources(subscriptionId: string, resourceGroup: string): Promise<ArmResource[]> {
  return (await listAllResources(subscriptionId, resourceGroup)).filter(({ type }) => isTopLevelType(type));
}

/** Tudo o que o ARM lista no RG, inclusive filhos. Use para checagens de segurança (ex.: antes de excluir). */
async function listAllResources(subscriptionId: string, resourceGroup: string): Promise<ArmResource[]> {
  const resources = await listAll<ArmResource>(
    `/subscriptions/${encodeURIComponent(subscriptionId)}/resourceGroups/${encodeURIComponent(resourceGroup)}/resources?api-version=${RESOURCES_API}`);
  return resources.map(({ id, name, type, location }) => ({ id, name, type, location })).sort(byName);
}

/** "Namespace/tipo" é de primeiro nível; "Namespace/tipo/subtipo" é filho. */
export function isTopLevelType(type: string): boolean {
  return type.split('/').filter(Boolean).length === 2;
}

/**
 * Cria um RG vazio. Falha se o nome já existir, para nunca reaproveitar um RG por engano.
 * As tags (copiadas do RG de origem) atendem às políticas que exigem tags como Setor e Responsável.
 */
export async function createResourceGroup(subscriptionId: string, name: string, location: string,
  tags: Record<string, string> = {}): Promise<ArmResourceGroup> {
  const path = `/subscriptions/${encodeURIComponent(subscriptionId)}/resourcegroups/${encodeURIComponent(name)}?api-version=${RESOURCES_API}`;
  const existing = await armFetch(path);
  if (existing.ok) throw new ArmError(409, `O resource group "${name}" já existe nesta subscription. Selecione-o na lista.`);
  if (existing.status !== 404) throw await readArmError(existing);
  const res = await armFetch(path, { method: 'PUT', body: JSON.stringify({ location, tags }) });
  if (!res.ok) throw await readArmError(res);
  const created = await res.json() as ArmResourceGroup;
  return { id: created.id, name: created.name, location: created.location, tags: created.tags };
}

/**
 * Move em lote: usa o RG de destino com o nome informado se ele já existir; senão, cria com as tags da origem.
 * (No modo de um RG, createResourceGroup continua recusando nome existente para evitar reaproveitar por engano.)
 */
export async function ensureResourceGroup(subscriptionId: string, name: string, location: string,
  tags: Record<string, string> = {}): Promise<ArmResourceGroup> {
  const res = await armFetch(`/subscriptions/${encodeURIComponent(subscriptionId)}/resourcegroups/${encodeURIComponent(name)}?api-version=${RESOURCES_API}`);
  if (res.ok) {
    const existing = await res.json() as ArmResourceGroup;
    return { id: existing.id, name: existing.name, location: existing.location, tags: existing.tags };
  }
  if (res.status !== 404) throw await readArmError(res);
  return createResourceGroup(subscriptionId, name, location, tags);
}

/** Inicia validateMoveResources ou moveResources. Todos os recursos precisam ser do mesmo RG de origem. */
export async function startMove(action: 'validate' | 'move', sourceSubscriptionId: string, sourceResourceGroup: string,
  resourceIds: string[], targetResourceGroupId: string): Promise<ArmOperation> {
  const verb = action === 'validate' ? 'validateMoveResources' : 'moveResources';
  const res = await armFetch(
    `/subscriptions/${encodeURIComponent(sourceSubscriptionId)}/resourceGroups/${encodeURIComponent(sourceResourceGroup)}/${verb}?api-version=${RESOURCES_API}`,
    { method: 'POST', body: JSON.stringify({ resources: resourceIds, targetResourceGroup: targetResourceGroupId }) });
  if (res.status !== 202) throw await readArmError(res);
  const location = res.headers.get('Location');
  if (!location) throw new Error('O Azure aceitou a operação, mas não informou como acompanhá-la (header Location ausente).');
  return { location, retryAfterSeconds: retryAfter(res) };
}

/**
 * Registra um resource provider (ex.: Microsoft.Web) na subscription. O ARM recusa mover recursos para uma
 * subscription que nunca registrou o provider do tipo (MissingRegistrationsForTypes). Exige a permissão
 * "register/action" na subscription (incluída em Contributor).
 */
export async function registerProvider(subscriptionId: string, namespace: string): Promise<void> {
  const res = await armFetch(
    `/subscriptions/${encodeURIComponent(subscriptionId)}/providers/${encodeURIComponent(namespace)}/register?api-version=${RESOURCES_API}`,
    { method: 'POST' });
  if (!res.ok) throw await readArmError(res);
}

/** Estado do registro do provider: "Registered", "Registering", "NotRegistered"… */
export async function getProviderState(subscriptionId: string, namespace: string): Promise<string> {
  const res = await armFetch(
    `/subscriptions/${encodeURIComponent(subscriptionId)}/providers/${encodeURIComponent(namespace)}?api-version=${RESOURCES_API}`);
  if (!res.ok) throw await readArmError(res);
  return ((await res.json()) as { registrationState?: string }).registrationState ?? 'Unknown';
}

export async function pollOperation(operation: ArmOperation): Promise<ArmOperationResult> {
  const res = await armFetch(operation.location);
  if (res.status === 202) return { state: 'running', retryAfterSeconds: retryAfter(res) };
  if (res.ok) return { state: 'succeeded' };
  const error = await readArmError(res);
  return { state: 'failed', message: error.message, details: error.details };
}

function retryAfter(res: Response): number {
  const seconds = Number(res.headers.get('Retry-After'));
  return Number.isFinite(seconds) && seconds > 0 ? Math.min(Math.max(seconds, 5), 60) : 15;
}

/**
 * Exclui um resource group SOMENTE se estiver vazio. Excluir um RG apaga tudo o que houver nele, então a
 * lista de recursos é conferida imediatamente antes do DELETE. Retorna a operação para acompanhar, ou null
 * se o Azure concluiu na hora.
 */
export async function deleteEmptyResourceGroup(subscriptionId: string, name: string): Promise<ArmOperation | null> {
  // Lista completa, sem o filtro de primeiro nível: qualquer item listado impede o DELETE.
  const remaining = await listAllResources(subscriptionId, name);
  if (remaining.length > 0) {
    throw new ArmError(409, `O resource group "${name}" ainda tem ${remaining.length} recurso(s); nada foi excluído.`,
      'ResourceGroupNotEmpty', remaining.map(r => `${r.name} (${r.type})`));
  }
  const res = await armFetch(
    `/subscriptions/${encodeURIComponent(subscriptionId)}/resourcegroups/${encodeURIComponent(name)}?api-version=${RESOURCES_API}`,
    { method: 'DELETE' });
  if (res.status === 200 || res.status === 204) return null;
  if (res.status !== 202) throw await readArmError(res);
  const location = res.headers.get('Location');
  if (!location) throw new Error('O Azure aceitou a exclusão, mas não informou como acompanhá-la (header Location ausente).');
  return { location, retryAfterSeconds: retryAfter(res) };
}

export function portalResourceUrl(resourceId: string): string {
  return `https://portal.azure.com/#@/resource${resourceId}`;
}
