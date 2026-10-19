import { useEffect, useMemo, useRef, useState } from 'react';
import { ArrowRight, ShieldAlert, X } from 'lucide-react';
import { useAccess } from '../auth/AccessContext';
import { SearchSelect } from '../components/SearchSelect';
import { useFetch } from '../hooks/useFetch';
import {
  ArmError, createResourceGroup, deleteEmptyResourceGroup, ensureResourceGroup, getProviderState, listResourceGroups, listResources,
  listSubscriptions, pollOperation, portalResourceUrl, registerProvider, startMove,
} from '../services/armApi';
import type { ArmOperation, ArmResource, ArmResourceGroup } from '../types/migrations';

/** Um RG de origem dentro do lote: os recursos marcados vão para um RG de destino. */
interface PlanItem {
  source: ArmResourceGroup;
  resourceIds: string[];
  /** Destino já existente, ou nome a criar (com a região e as tags da origem). */
  target: { kind: 'existing'; group: ArmResourceGroup } | { kind: 'create'; name: string };
}

/** Item validado: o destino já existe (foi criado na validação, se preciso). */
interface ReadyItem { sourceName: string; resourceIds: string[]; targetId: string }
interface ItemFailure { sourceName: string; message: string; details: string[] }

type Phase =
  | { kind: 'idle' }
  | { kind: 'validating'; current: string; done: number; total: number }
  // Registro de resource providers pedido pelo Azure (MissingRegistrationsForTypes).
  | { kind: 'registering'; pending: PendingRegistration[] }
  | { kind: 'validated'; key: string; ready: ReadyItem[] }
  // Validação: nada é movido enquanto algum RG falhar.
  | { kind: 'validationFailed'; key: string; failures: ItemFailure[]; okCount: number }
  | { kind: 'moving'; current: string; done: number; total: number }
  | { kind: 'moved'; moved: ReadyItem[] }
  // Um move falhou (o ARM desfaz aquele move inteiro); os anteriores ficaram movidos e os seguintes não começaram.
  | { kind: 'moveFailed'; moved: ReadyItem[]; failure: ItemFailure; notStarted: string[] }
  // O POST de um move foi aceito, mas o acompanhamento falhou: estado real desconhecido.
  | { kind: 'moveUnknown'; operation: ArmOperation; ready: ReadyItem[]; index: number; moved: ReadyItem[]; message: string };

interface Inventory { loading: boolean; error: string | null; resources: ArmResource[] }

const NEW_GROUP = '__new__';
const RG_NAME = /^[-\w._()]{1,90}$/;
// Só subscriptions MPN (desenvolvimento/homologação) aparecem para origem e destino. É uma proteção de uso,
// não de segurança: a permissão real é o RBAC do usuário no Azure.
const MIGRATION_SUBSCRIPTIONS = /^MPN\s*-/i;
// Consultas de recursos simultâneas ao adicionar muitos RGs (ex.: todos da subscription).
const MAX_PARALLEL_INVENTORY = 6;

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Falha ao chamar o Azure Resource Manager.';
}

const errorDetails = (error: unknown): string[] => error instanceof ArmError ? error.details : [];
const total = (items: { resourceIds: string[] }[]) => items.reduce((sum, item) => sum + item.resourceIds.length, 0);

interface PendingRegistration { subscriptionId: string; namespaces: string[] }

/**
 * Providers que o Azure pede para registrar (detalhes MissingRegistrationsForTypes), agrupados por subscription.
 * O Azure escreve alguns tipos em minúsculas (microsoft.insights/actiongroups): a busca ignora maiúsculas e
 * deduplica sem diferenciar caixa (os namespaces do ARM não diferenciam).
 */
function missingRegistrations(details: string[]): PendingRegistration[] {
  const bySubscription = new Map<string, Map<string, string>>();
  for (const detail of details.filter(d => d.startsWith('MissingRegistrationsForTypes'))) {
    const subscriptionId = detail.match(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i)?.[0]?.toLowerCase();
    if (!subscriptionId) continue;
    const namespaces = bySubscription.get(subscriptionId) ?? new Map<string, string>();
    for (const match of detail.matchAll(/\b(microsoft\.[a-z0-9]+)\//gi)) {
      const lower = match[1].toLowerCase();
      // Prefere a grafia com maiúscula (Microsoft.Insights) quando aparecem as duas.
      if (!namespaces.has(lower) || /^Microsoft\./.test(match[1])) namespaces.set(lower, match[1]);
    }
    bySubscription.set(subscriptionId, namespaces);
  }
  return [...bySubscription].map(([subscriptionId, namespaces]) => ({ subscriptionId, namespaces: [...namespaces.values()] }))
    .filter(item => item.namespaces.length > 0);
}

/**
 * Tipos que o ARM recusa mover (detalhes ResourceMoveNotSupported). Vários tipos podem vir na mesma
 * string entre aspas, separados por vírgula: 'Microsoft.ManagedIdentity/userAssignedIdentities, microsoft.insights/metricalerts'.
 */
function unsupportedTypes(details: string[]): string[] {
  return [...new Set(details.filter(d => d.startsWith('ResourceMoveNotSupported'))
    .flatMap(d => [...d.matchAll(/'([^']+)'/g)].flatMap(m => m[1].split(',')))
    .map(type => type.trim())
    .filter(type => /^[a-z0-9]+\.[a-z0-9]+(?:\/[a-z0-9]+)+$/i.test(type)))];
}

const unique = (items: string[]) => [...new Set(items)];

const wait = (seconds: number) => new Promise(resolve => window.setTimeout(resolve, seconds * 1000));

export default function Migrations() {
  const { isAdmin } = useAccess();
  return isAdmin ? <MoveResources /> : <div role="alert">Acesso exclusivo para FinOps.Admin.</div>;
}

function MoveResources() {
  const [sourceSubscription, setSourceSubscription] = useState('');
  // RGs de origem do lote, na ordem em que foram adicionados (também é a ordem dos moves).
  const [sourceNames, setSourceNames] = useState<string[]>([]);
  const [inventory, setInventory] = useState<Record<string, Inventory>>({});
  const [selection, setSelection] = useState<Record<string, string[]>>({});
  const [targetSubscription, setTargetSubscription] = useState('');
  // Só com um RG de origem: destino escolhido (existente ou novo).
  const [targetGroupChoice, setTargetGroupChoice] = useState('');
  // null = acompanha o nome do RG de origem; texto = nome digitado pelo usuário.
  const [customGroupName, setCustomGroupName] = useState<string | null>(null);
  // Confirmação vinculada à seleção: mudou a seleção, a confirmação deixa de valer.
  const [confirmedKey, setConfirmedKey] = useState<string | null>(null);
  const [phase, setPhase] = useState<Phase>({ kind: 'idle' });
  const [notice, setNotice] = useState<string | null>(null);
  // RGs criados nesta tela: valem como destino até a lista do ARM refletir a criação.
  const [createdGroups, setCreatedGroups] = useState<ArmResourceGroup[]>([]);
  // RGs de origem excluídos na etapa opcional após o move: saem do lote na próxima migração.
  const [deletedSources, setDeletedSources] = useState<string[]>([]);
  const [inventoryVersion, setInventoryVersion] = useState(0);
  const mounted = useRef(true);
  useEffect(() => () => { mounted.current = false; }, []);

  const subscriptions = useFetch(() => listSubscriptions(), []);
  const subscriptionOptions = useMemo(() => (subscriptions.data ?? [])
    .filter(s => MIGRATION_SUBSCRIPTIONS.test(s.displayName))
    .map(s => ({ value: s.subscriptionId, label: s.displayName, hint: s.subscriptionId })), [subscriptions.data]);
  const sourceGroups = useFetch(() => sourceSubscription ? listResourceGroups(sourceSubscription) : Promise.resolve([]), [sourceSubscription]);
  const targetGroups = useFetch(() => targetSubscription ? listResourceGroups(targetSubscription) : Promise.resolve([]), [targetSubscription]);

  // Carrega os recursos de cada RG adicionado; por padrão todos ficam marcados.
  useEffect(() => {
    let slots = MAX_PARALLEL_INVENTORY - Object.values(inventory).filter(i => i.loading).length;
    for (const name of sourceNames) {
      if (slots <= 0) break;
      if (inventory[name]) continue;
      slots--;
      setInventory(current => ({ ...current, [name]: { loading: true, error: null, resources: [] } }));
      listResources(sourceSubscription, name)
        .then(resources => {
          if (!mounted.current) return;
          setInventory(current => current[name] ? { ...current, [name]: { loading: false, error: null, resources } } : current);
          setSelection(current => ({ ...current, [name]: resources.map(r => r.id) }));
        })
        .catch(error => mounted.current && setInventory(current => current[name]
          ? { ...current, [name]: { loading: false, error: errorMessage(error), resources: [] } } : current));
    }
  }, [sourceNames, sourceSubscription, inventory, inventoryVersion]);

  const single = sourceNames.length === 1;
  const sources = sourceNames.map(name => sourceGroups.data?.find(g => g.name === name)).filter((g): g is ArmResourceGroup => !!g);
  const knownTargets = [...(targetGroups.data ?? []), ...createdGroups.filter(c => !targetGroups.data?.some(g => g.id === c.id))];
  const creatingGroup = single && targetGroupChoice === NEW_GROUP;
  const newGroupName = creatingGroup ? customGroupName ?? sourceNames[0] : '';
  const singleTarget = single && !creatingGroup ? knownTargets.find(g => g.id === targetGroupChoice) : undefined;

  const plan: PlanItem[] = sources.map(source => ({
    source,
    resourceIds: selection[source.name] ?? [],
    target: single
      ? creatingGroup ? { kind: 'create', name: newGroupName.trim() } : singleTarget ? { kind: 'existing', group: singleTarget } : { kind: 'create', name: '' }
      : (() => {
        const existing = knownTargets.find(g => g.name.toLowerCase() === source.name.toLowerCase());
        return existing ? { kind: 'existing', group: existing } : { kind: 'create', name: source.name };
      })(),
  }));

  // RGs sem nenhum recurso marcado (inclusive RGs vazios) ficam fora do lote em vez de bloqueá-lo.
  const loaded = (name: string) => inventory[name] && !inventory[name].loading;
  const active = plan.filter(item => item.resourceIds.length > 0);
  const skipped = plan.filter(item => item.resourceIds.length === 0 && loaded(item.source.name));

  const busy = phase.kind === 'validating' || phase.kind === 'moving' || phase.kind === 'registering';
  const locked = busy || phase.kind === 'moved' || phase.kind === 'moveUnknown' || phase.kind === 'moveFailed';

  // Validação, recusa e confirmação valem só para a seleção em que foram feitas.
  const keyOf = (singleTargetId?: string) => JSON.stringify([sourceSubscription, targetSubscription,
    active.map(item => [item.source.name, [...item.resourceIds].sort(),
      single ? singleTargetId ?? (creatingGroup ? `new:${newGroupName.trim()}` : targetGroupChoice) : `same:${item.source.name}`])]);
  const selectionKey = keyOf();
  // Validação aprovada só vale para a seleção validada. Recusas continuam visíveis (para corrigir e registrar),
  // com aviso de que a seleção mudou.
  const view: Phase = phase.kind === 'validated' && phase.key !== selectionKey ? { kind: 'idle' } : phase;
  const staleFailure = view.kind === 'validationFailed' && view.key !== selectionKey;
  const confirmed = confirmedKey === selectionKey;
  const selectedCount = total(active);

  const loadingInventory = sourceNames.some(name => inventory[name]?.loading ?? true);
  const selectionProblem = sourceNames.length === 0 ? 'Selecione a subscription e ao menos um resource group de origem.'
    : loadingInventory ? 'Aguarde o carregamento dos recursos.'
    : active.length === 0 ? (single ? 'Marque ao menos um recurso.' : 'Marque ao menos um recurso em algum resource group.')
    : !targetSubscription ? 'Selecione a subscription de destino.'
    : single && !targetGroupChoice ? 'Selecione a subscription e o resource group de destino.'
    : creatingGroup && !RG_NAME.test(newGroupName.trim()) ? 'Informe um nome válido para o novo resource group (até 90 caracteres: letras, números, -, _, ., parênteses).'
    : single && singleTarget?.id.toLowerCase() === sources[0]?.id.toLowerCase() ? 'O destino precisa ser um resource group diferente da origem.'
    : !single && targetSubscription === sourceSubscription ? 'Com vários resource groups, cada um vai para um RG com o mesmo nome: o destino precisa ser outra subscription.'
    : null;

  async function follow(operation: ArmOperation): Promise<{ state: 'succeeded' } | { state: 'failed'; message: string; details: string[] }> {
    let next = operation.retryAfterSeconds;
    for (;;) {
      await wait(next);
      if (!mounted.current) return { state: 'failed', message: 'Página fechada durante o acompanhamento.', details: [] };
      const result = await pollOperation(operation);
      if (result.state !== 'running') return result;
      next = result.retryAfterSeconds;
    }
  }

  /** O ARM exige que o RG de destino exista já na validação. Tags da origem: atendem às políticas de Setor/Responsável. */
  async function resolveTarget(item: PlanItem): Promise<ArmResourceGroup> {
    if (item.target.kind === 'existing') return item.target.group;
    const tags = item.source.tags ?? {};
    // Um RG: nome novo nunca reaproveita um RG existente. Lote: usa o de mesmo nome se já existir.
    const created = single
      ? await createResourceGroup(targetSubscription, item.target.name, item.source.location, tags)
      : await ensureResourceGroup(targetSubscription, item.target.name, item.source.location, tags);
    setCreatedGroups(current => current.some(g => g.id === created.id) ? current : [...current, created]);
    if (single) {
      setTargetGroupChoice(created.id);
      setCustomGroupName(null);
    }
    return created;
  }

  /** Registra de uma vez todos os providers pendentes e espera todos ficarem Registered. */
  async function registerProviders(pending: PendingRegistration[]) {
    setNotice(null);
    setPhase({ kind: 'registering', pending });
    const all = pending.flatMap(item => item.namespaces.map(namespace => ({ subscriptionId: item.subscriptionId, namespace })));
    try {
      // O POST de registro só enfileira; o registro em si é assíncrono no Azure (normalmente 1–5 minutos, em paralelo).
      for (const { subscriptionId, namespace } of all) await registerProvider(subscriptionId, namespace);
      const deadline = Date.now() + 15 * 60_000;
      for (;;) {
        const states = await Promise.all(all.map(({ subscriptionId, namespace }) => getProviderState(subscriptionId, namespace)));
        if (states.every(state => state === 'Registered')) break;
        if (Date.now() > deadline) throw new Error('O registro ainda não terminou após 15 minutos. Valide de novo mais tarde.');
        await wait(10);
        if (!mounted.current) return;
      }
      if (!mounted.current) return;
      setPhase({ kind: 'idle' });
      setNotice(`${all.map(item => item.namespace).join(', ')} registrado(s) na subscription. Valide novamente.`);
    } catch (error) {
      if (mounted.current) setPhase({ kind: 'validationFailed', key: selectionKey, okCount: 0,
        failures: [{ sourceName: 'Registro de resource providers', message: errorMessage(error), details: errorDetails(error) }] });
    }
  }

  /** Valida todos os RGs, um por vez. Nada é movido se algum falhar. */
  async function validate() {
    setNotice(null);
    const items = active;
    const ready: ReadyItem[] = [];
    const failures: ItemFailure[] = [];
    for (const [index, item] of items.entries()) {
      // Tela fechada no meio do lote: não cria RGs nem valida os próximos sem ninguém acompanhando.
      if (!mounted.current) return;
      setPhase({ kind: 'validating', current: item.source.name, done: index, total: items.length });
      try {
        const target = await resolveTarget(item);
        const result = await follow(await startMove('validate', sourceSubscription, item.source.name, item.resourceIds, target.id));
        if (!mounted.current) return;
        if (result.state === 'succeeded') ready.push({ sourceName: item.source.name, resourceIds: item.resourceIds, targetId: target.id });
        else failures.push({ sourceName: item.source.name, message: result.message, details: result.details });
      } catch (error) {
        if (!mounted.current) return;
        failures.push({ sourceName: item.source.name, message: errorMessage(error), details: errorDetails(error) });
      }
    }
    if (!mounted.current) return;
    const key = keyOf(single ? ready[0]?.targetId : undefined);
    setPhase(failures.length === 0 ? { kind: 'validated', key, ready } : { kind: 'validationFailed', key, failures, okCount: ready.length });
  }

  /** Move os RGs em fila. Depois que um POST de move é aceito, ele nunca é reenviado: só acompanhado. */
  async function runMoves(ready: ReadyItem[], start: number, moved: ReadyItem[], pending?: ArmOperation) {
    for (let index = start; index < ready.length; index++) {
      // Tela fechada: não inicia o próximo move da fila.
      if (!mounted.current) return;
      const item = ready[index];
      setPhase({ kind: 'moving', current: item.sourceName, done: index, total: ready.length });
      let operation = index === start ? pending : undefined;
      if (!operation) {
        try {
          operation = await startMove('move', sourceSubscription, item.sourceName, item.resourceIds, item.targetId);
        } catch (error) {
          if (mounted.current) setPhase({ kind: 'moveFailed', moved, notStarted: ready.slice(index + 1).map(i => i.sourceName),
            failure: { sourceName: item.sourceName, message: errorMessage(error), details: errorDetails(error) } });
          return;
        }
      }
      try {
        const result = await follow(operation);
        if (!mounted.current) return;
        if (result.state === 'failed') {
          setPhase({ kind: 'moveFailed', moved, notStarted: ready.slice(index + 1).map(i => i.sourceName),
            failure: { sourceName: item.sourceName, message: result.message, details: result.details } });
          return;
        }
        moved = [...moved, item];
      } catch (error) {
        if (mounted.current) setPhase({ kind: 'moveUnknown', operation, ready, index, moved, message: errorMessage(error) });
        return;
      }
    }
    if (mounted.current) setPhase({ kind: 'moved', moved });
  }

  function move() {
    if (view.kind !== 'validated' || !confirmed) return;
    void runMoves(view.ready, 0, []);
  }

  function addSource(name: string) {
    if (!name || sourceNames.includes(name)) return;
    setSourceNames(current => [...current, name]);
  }

  function addAllSources() {
    const all = (sourceGroups.data ?? []).map(g => g.name);
    setSourceNames(current => [...current, ...all.filter(name => !current.includes(name))]);
  }

  function clearSources() {
    setSourceNames([]);
    setInventory({});
    setSelection({});
  }

  function removeSource(name: string) {
    setSourceNames(current => current.filter(n => n !== name));
    setInventory(current => Object.fromEntries(Object.entries(current).filter(([n]) => n !== name)));
    setSelection(current => Object.fromEntries(Object.entries(current).filter(([n]) => n !== name)));
  }

  function changeSourceSubscription(value: string) {
    setSourceSubscription(value);
    setSourceNames([]);
    setInventory({});
    setSelection({});
  }

  function reset() {
    setConfirmedKey(null);
    setPhase({ kind: 'idle' });
    if (deletedSources.length) {
      setSourceNames(current => current.filter(n => !deletedSources.includes(n)));
      setDeletedSources([]);
      sourceGroups.refetch();
    }
    // Recarrega o inventário (e a seleção padrão) dos RGs que continuam no lote.
    setInventory({});
    setSelection({});
    setInventoryVersion(v => v + 1);
    targetGroups.refetch();
  }

  const toggle = (group: string, id: string) => setSelection(current => {
    const ids = current[group] ?? [];
    return { ...current, [group]: ids.includes(id) ? ids.filter(i => i !== id) : [...ids, id] };
  });

  const sourceOptions = groupOptions((sourceGroups.data ?? []).filter(g => !sourceNames.includes(g.name)), g => g.name);
  const movedTargets = view.kind === 'moved' ? view.moved : [];

  return <div className="page migrations-page">
    <header className="page-header"><div><span className="migration-eyebrow">Admin</span><h1>Migração de recursos</h1>
      <p className="text-muted">Move recursos entre resource groups e subscriptions, como o <code>az resource move</code>.</p></div>
      <ShieldAlert size={32} aria-hidden="true" /></header>

    <div className="alert alert--warning">
      As chamadas usam <strong>a sua permissão no Azure</strong>: só é possível mover o que você já pode mover pelo portal ou pelo CLI.
      O Terraform e as pipelines dos recursos movidos <strong>não</strong> são atualizados.
      Só aparecem subscriptions <strong>MPN</strong>, na origem e no destino.
    </div>

    {subscriptions.error && <div role="alert" className="alert alert--warning">{subscriptions.error}
      <button className="btn btn--secondary" onClick={subscriptions.refetch}>Consultar novamente</button></div>}

    <div className="migration-route">
      <fieldset className="migration-panel" disabled={locked}>
        <legend>Origem</legend>
        <SearchSelect id="source-subscription" label="Subscription" ariaLabel="Subscription de origem" value={sourceSubscription}
          options={subscriptionOptions} loading={subscriptions.loading} placeholder="Buscar subscription MPN…"
          onChange={changeSourceSubscription} />
        <SearchSelect id="source-group" label={sourceNames.length ? 'Adicionar outro resource group' : 'Resource group'}
          ariaLabel="Resource group de origem" value="" options={sourceOptions}
          loading={!!sourceSubscription && sourceGroups.loading} disabled={!sourceSubscription} error={sourceGroups.error}
          placeholder="Buscar resource group…" onChange={addSource} />
        {sourceSubscription && !sourceGroups.loading && <div className="migration-actions">
          <button type="button" className="btn btn--secondary" disabled={sourceOptions.length === 0} onClick={addAllSources}>
            Adicionar todos os resource groups ({sourceOptions.length})</button>
          {sourceNames.length > 0 && <button type="button" className="btn btn--ghost" onClick={clearSources}>
            Remover todos ({sourceNames.length})</button>}
        </div>}
        {sourceNames.length > 0 && <ul className="migration-sources" aria-label="Resource groups de origem">
          {sourceNames.map(name => <li key={name}>
            <span>{name}</span>
            <button type="button" className="btn btn--ghost" aria-label={`Remover ${name}`} onClick={() => removeSource(name)}>
              <X size={14} aria-hidden="true" /></button>
          </li>)}
        </ul>}
      </fieldset>

      <ArrowRight size={28} aria-hidden="true" />

      <fieldset className="migration-panel" disabled={locked}>
        <legend>Destino</legend>
        <SearchSelect id="target-subscription" label="Subscription" ariaLabel="Subscription de destino" value={targetSubscription}
          options={subscriptionOptions} loading={subscriptions.loading} placeholder="Buscar subscription MPN…"
          onChange={value => { setTargetSubscription(value); setTargetGroupChoice(''); }} />
        {sourceNames.length <= 1 && <>
          <SearchSelect id="target-group" label="Resource group" ariaLabel="Resource group de destino" value={targetGroupChoice}
            options={[{ value: NEW_GROUP, label: '+ Criar novo resource group', pinned: true }, ...groupOptions(knownTargets, g => g.id)]}
            loading={!!targetSubscription && targetGroups.loading} disabled={!targetSubscription} error={targetGroups.error}
            placeholder="Buscar resource group…" onChange={value => { setTargetGroupChoice(value); setCustomGroupName(null); }} />
          {creatingGroup && <div className="filter-group">
            <label htmlFor="new-group">Nome do novo resource group</label>
            <input id="new-group" value={newGroupName} maxLength={90} onChange={e => setCustomGroupName(e.target.value)} />
            <p className="text-muted">Preenchido com o nome do RG de origem. Criado ao validar, na região
              {sources[0] ? ` (${sources[0].location})` : ''} e com as tags do RG de origem.</p>
          </div>}
        </>}
        {sourceNames.length > 1 && <div className="migration-mapping">
          <p className="text-muted">Com vários resource groups, cada um vai para um RG <strong>com o mesmo nome</strong> na subscription
            de destino. Os que não existem são criados ao validar, na região e com as tags do RG de origem.</p>
          {targetSubscription && <ul className="migration-list">{active.map(item => <li key={item.source.name}>
            {item.source.name} → {item.source.name}{' '}
            <small>{item.target.kind === 'existing' ? '(já existe)' : '(será criado)'}</small></li>)}</ul>}
        </div>}
      </fieldset>
    </div>

    {sources.map(source => {
      const inv = inventory[source.name];
      const resources = inv?.resources ?? [];
      const chosen = selection[source.name] ?? [];
      const allChosen = resources.length > 0 && resources.every(r => chosen.includes(r.id));
      const titleId = `resources-title-${source.name}`;
      return <section key={source.name} className="migration-panel" aria-labelledby={titleId}>
        <div className="migration-section-heading">
          <h2 id={titleId}>Recursos de {source.name}</h2>
          {resources.length > 0 && <label className="migration-check">
            <input type="checkbox" checked={allChosen} disabled={locked}
              onChange={() => setSelection(current => ({ ...current, [source.name]: allChosen ? [] : resources.map(r => r.id) }))} />
            Selecionar todos
          </label>}
        </div>
        {(!inv || inv.loading) && <p role="status">Carregando recursos…</p>}
        {inv?.error && <p role="alert">{inv.error}</p>}
        {inv && !inv.loading && !inv.error && resources.length === 0 && <p>Este resource group não tem recursos.</p>}
        {resources.length > 0 && <div className="table-container"><table>
          <thead><tr><th scope="col" aria-label="Mover"></th><th scope="col">Nome</th><th scope="col">Tipo</th><th scope="col">Região</th></tr></thead>
          <tbody>{resources.map(r => <tr key={r.id}>
            <td><input type="checkbox" aria-label={`Mover ${r.name}`} checked={chosen.includes(r.id)} disabled={locked}
              onChange={() => toggle(source.name, r.id)} /></td>
            <td>{r.name}</td><td>{r.type}</td><td>{r.location}</td>
          </tr>)}</tbody>
        </table></div>}
      </section>;
    })}
    {sources.length > 0 && <p className="text-muted">Recursos dependentes precisam ser movidos juntos; a validação do Azure aponta o que falta.</p>}
    {sourceNames.length > 1 && skipped.length > 0 && <p className="text-muted" role="status">
      Fora do lote (nenhum recurso marcado): {skipped.map(item => item.source.name).join(', ')}.</p>}

    <section className="migration-panel" aria-labelledby="move-title">
      <h2 id="move-title">Validar e mover</h2>
      {selectionProblem && !locked && <p>{selectionProblem}</p>}

      {view.kind === 'validating' && <p role="status" className="migration-status">
        Validando {view.total > 1 ? `${view.current} (${view.done + 1} de ${view.total})` : ''} no Azure… pode levar alguns minutos.</p>}
      {notice && view.kind === 'idle' && <p role="status" className="alert alert--success">{notice}</p>}
      {view.kind === 'registering' && <p role="status" className="migration-status">
        Registrando {view.pending.map(item => `${item.namespaces.join(', ')} na subscription ${item.subscriptionId}`).join('; ')}…
        pode levar alguns minutos.</p>}
      {view.kind === 'validationFailed' && <div role="alert" className="migration-status migration-status--blocked">
        <div><strong>O Azure não permite este move{view.failures.length > 1 || view.okCount > 0 ? ` (${view.failures.length} resource group(s) com problema; nada será movido até todos validarem)` : ''}:</strong>
          {staleFailure && <p>A seleção mudou desde esta validação: valide novamente.</p>}
          <RegisterProvidersHint details={view.failures.flatMap(f => f.details)} onRegister={registerProviders} />
          {view.failures.map(failure => {
            const blockedTypes = unsupportedTypes(failure.details);
            const chosen = selection[failure.sourceName] ?? [];
            const blocked = (inventory[failure.sourceName]?.resources ?? [])
              .filter(r => chosen.includes(r.id) && blockedTypes.some(t => t.toLowerCase() === r.type.toLowerCase()));
            return <div key={failure.sourceName} className="migration-failure">
              {(view.failures.length > 1 || view.okCount > 0) && <h3>{failure.sourceName}</h3>}
              {failure.message}
              {failure.details.length > 0 && <ul className="migration-list">{unique(failure.details).map(d => <li key={d}>{d}</li>)}</ul>}
              {blocked.length > 0 && <p>{blocked.map(r => r.name).join(', ')}: o Azure não move esse tipo de recurso.{' '}
                <button className="btn btn--secondary" onClick={() => setSelection(current => ({ ...current,
                  [failure.sourceName]: (current[failure.sourceName] ?? []).filter(id => !blocked.some(r => r.id === id)) }))}>
                  Desmarcar os recursos que não podem ser movidos</button></p>}
            </div>;
          })}</div></div>}
      {view.kind === 'validated' && <p role="status" className="alert alert--success">
        Validação concluída: o Azure aceita mover {total(view.ready)} recurso(s){view.ready.length > 1 ? ` de ${view.ready.length} resource groups` : ''}.</p>}

      {view.kind === 'validated' && <div className="migration-confirm">
        <label className="migration-check">
          <input type="checkbox" checked={confirmed} onChange={e => setConfirmedKey(e.target.checked ? selectionKey : null)} />
          <span>Entendo que {selectedCount} recurso(s) mudarão de resource group{targetSubscription !== sourceSubscription ? ' e de subscription' : ''},
            que os IDs dos recursos mudam e que, durante o move, a origem e o destino ficam bloqueados para alterações.</span>
        </label>
      </div>}

      {view.kind === 'moving' && <p role="status" className="migration-status">
        Movendo {view.total > 1 ? `${view.current} (${view.done + 1} de ${view.total})` : 'recursos'}… o Azure pode levar de minutos a horas.
        O move continua mesmo se você fechar esta página.</p>}
      {view.kind === 'moved' && <div role="status" className="alert alert--success">
        {total(view.moved)} recurso(s) movido(s).{' '}
        {view.moved.length === 1
          ? <a href={portalResourceUrl(view.moved[0].targetId)} target="_blank" rel="noreferrer">Abrir o resource group de destino no portal</a>
          : <ul className="migration-list">{view.moved.map(item => <li key={item.sourceName}>{item.sourceName}:{' '}
            <a href={portalResourceUrl(item.targetId)} target="_blank" rel="noreferrer">abrir o destino no portal</a></li>)}</ul>}
      </div>}
      {view.kind === 'moved' && movedTargets.map((item, index) => <SourceGroupCleanup key={item.sourceName} index={index}
        subscriptionId={sourceSubscription} groupName={item.sourceName}
        onDeleted={() => setDeletedSources(current => [...current, item.sourceName])} />)}
      {view.kind === 'moveFailed' && <div role="alert" className="migration-status migration-status--blocked">
        <div><strong>O move de {view.failure.sourceName} falhou e foi desfeito pelo Azure:</strong> {view.failure.message}
          {view.failure.details.length > 0 && <ul className="migration-list">{unique(view.failure.details).map(d => <li key={d}>{d}</li>)}</ul>}
          {view.moved.length > 0 && <p>Já movidos antes da falha: {view.moved.map(i => i.sourceName).join(', ')}.</p>}
          {view.notStarted.length > 0 && <p>Não iniciados: {view.notStarted.join(', ')}.</p>}</div></div>}
      {view.kind === 'moveUnknown' && <div role="alert" className="migration-status migration-status--blocked">
        <div><strong>Não foi possível acompanhar o move:</strong> {view.message}. O move de {view.ready[view.index].sourceName} pode
          estar em andamento no Azure. Não inicie outro move destes recursos antes de confirmar o resultado.</div>
        <button className="btn btn--secondary" onClick={() => runMoves(view.ready, view.index, view.moved, view.operation)}>
          Consultar o andamento</button>
      </div>}

      <div className="migration-actions">
        {(view.kind === 'idle' || view.kind === 'validating' || view.kind === 'validationFailed') &&
          <button className="btn btn--secondary" disabled={!!selectionProblem || busy} onClick={validate}>
            {view.kind === 'validating' ? 'Validando…' : creatingGroup ? 'Criar RG de destino e validar' : 'Validar'}</button>}
        {view.kind === 'validated' &&
          <button className="btn btn--primary" disabled={!confirmed} onClick={move}>Mover {total(view.ready)} recurso(s)</button>}
        {(view.kind === 'moved' || view.kind === 'moveFailed') && <button className="btn btn--secondary" onClick={reset}>Nova migração</button>}
      </div>
    </section>
  </div>;
}

/**
 * Etapa opcional depois do move: excluir o RG de origem, só se estiver vazio. A lista é conferida ao abrir a etapa e
 * de novo imediatamente antes do DELETE (em deleteEmptyResourceGroup), e o usuário precisa digitar o nome do RG.
 */
function SourceGroupCleanup({ index, subscriptionId, groupName, onDeleted }: {
  index: number; subscriptionId: string; groupName: string; onDeleted: () => void;
}) {
  type State =
    | { kind: 'checking' } | { kind: 'empty' } | { kind: 'notEmpty'; count: number }
    | { kind: 'deleting' } | { kind: 'deleted' } | { kind: 'failed'; message: string; details: string[] };
  const [state, setState] = useState<State>({ kind: 'checking' });
  const [typed, setTyped] = useState('');
  const mounted = useRef(true);
  useEffect(() => () => { mounted.current = false; }, []);

  useEffect(() => {
    listResources(subscriptionId, groupName)
      .then(remaining => mounted.current && setState(remaining.length ? { kind: 'notEmpty', count: remaining.length } : { kind: 'empty' }))
      .catch(error => mounted.current && setState({ kind: 'failed', message: errorMessage(error), details: errorDetails(error) }));
  }, [subscriptionId, groupName]);

  async function remove() {
    if (typed !== groupName) return;
    setState({ kind: 'deleting' });
    try {
      const operation = await deleteEmptyResourceGroup(subscriptionId, groupName);
      let next = operation?.retryAfterSeconds ?? 0;
      while (operation) {
        await wait(next);
        if (!mounted.current) return;
        const result = await pollOperation(operation);
        if (result.state === 'succeeded') break;
        if (result.state === 'failed') throw new ArmError(409, result.message, undefined, result.details);
        next = result.retryAfterSeconds;
      }
      if (!mounted.current) return;
      setState({ kind: 'deleted' });
      onDeleted();
    } catch (error) {
      if (mounted.current) setState({ kind: 'failed', message: errorMessage(error), details: errorDetails(error) });
    }
  }

  const inputId = `confirm-delete-group-${index}`;
  return <div className="migration-cleanup">
    <h3>Resource group de origem: {groupName}</h3>
    {state.kind === 'checking' && <p role="status">Conferindo se {groupName} ficou vazio…</p>}
    {state.kind === 'notEmpty' && <p>{groupName} ainda tem {state.count} recurso(s); por isso não pode ser excluído por aqui.</p>}
    {(state.kind === 'empty' || state.kind === 'deleting') && <>
      <p><strong>{groupName}</strong> ficou vazio. Se não for mais usado, pode ser excluído. A exclusão é definitiva e
        confere de novo, antes de apagar, que não há nenhum recurso nele.</p>
      <div className="filter-group">
        <label htmlFor={inputId}>Digite o nome do resource group para confirmar</label>
        <input id={inputId} value={typed} autoComplete="off" disabled={state.kind === 'deleting'}
          onChange={e => setTyped(e.target.value)} />
      </div>
      <div className="migration-actions">
        <button className="btn btn--danger" disabled={typed !== groupName || state.kind === 'deleting'} onClick={remove}>
          {state.kind === 'deleting' ? 'Excluindo…' : 'Excluir resource group de origem'}</button>
      </div>
    </>}
    {state.kind === 'deleted' && <p role="status" className="alert alert--success">{groupName} foi excluído.</p>}
    {state.kind === 'failed' && <div role="alert" className="migration-status migration-status--blocked">
      <div><strong>Não foi possível excluir:</strong> {state.message}
        {state.details.length > 0 && <ul className="migration-list">{state.details.map(d => <li key={d}>{d}</li>)}</ul>}</div></div>}
  </div>;
}

function groupOptions(groups: ArmResourceGroup[] | null, valueOf: (group: ArmResourceGroup) => string) {
  return (groups ?? []).map(g => ({ value: valueOf(g), label: g.name, hint: g.location }));
}

/** Um único bloco com todos os resource providers pendentes do lote, registrados de uma vez. */
function RegisterProvidersHint({ details, onRegister }: {
  details: string[]; onRegister: (pending: PendingRegistration[]) => void;
}) {
  const pending = missingRegistrations(details);
  if (pending.length === 0) return null;
  const count = pending.reduce((sum, item) => sum + item.namespaces.length, 0);
  return <div className="migration-register">
    {pending.map(item => <p key={item.subscriptionId}>A subscription <code>{item.subscriptionId}</code> ainda não registrou:{' '}
      {item.namespaces.join(', ')}.</p>)}
    <p>O registro é feito uma única vez por subscription e exige permissão de Contributor nela.{' '}
      <button className="btn btn--secondary" onClick={() => onRegister(pending)}>
        {count === 1 ? `Registrar ${pending[0].namespaces[0]}` : `Registrar todos (${count})`}</button></p>
  </div>;
}
