import { useState } from 'react';
import { useAccess } from '../auth/AccessContext';
import Card from '../components/Card';
import { useFetch } from '../hooks/useFetch';
import { governanceApi } from '../services/governanceApi';
import type { GovernanceBudget } from '../types/governance';

const emptyDraft = () => ({ name: '', subscriptionId: '', month: new Date().toISOString().slice(0, 7), currency: 'BRL', amount: '', costCenter: '' });
function money(value: number | null, currency: string): string {
  if (value == null) return 'Indisponível';
  try { return new Intl.NumberFormat('pt-BR', { style: 'currency', currency }).format(value); }
  catch { return `${currency} ${value.toFixed(2)}`; }
}

export default function Budgets() {
  const { isAdmin } = useAccess();
  const [selected, setSelected] = useState<string | null>(null);
  const [editing, setEditing] = useState<GovernanceBudget | null>(null);
  const [draft, setDraft] = useState(emptyDraft);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const list = useFetch(() => governanceApi.listBudgets(), []);
  const evaluation = useFetch(() => selected ? governanceApi.evaluateBudget(selected) : Promise.resolve(null), [selected]);

  function edit(budget: GovernanceBudget) {
    setEditing(budget);
    setDraft({ ...budget.definition, amount: String(budget.definition.amount), costCenter: budget.definition.costCenter ?? '' });
    setActionError(null); setMessage(null);
  }

  async function save() {
    if (!isAdmin || saving) return;
    const input = { ...draft, amount: Number(draft.amount), costCenter: draft.costCenter.trim() || null, version: editing?.version ?? null };
    if (!window.confirm(`${editing ? 'Atualizar' : 'Criar'} o orçamento "${input.name}"?\nSubscription: ${input.subscriptionId}\nMês: ${input.month}\nLimite: ${money(input.amount, input.currency)}\nCentro de custo: ${input.costCenter ?? 'Subscription inteira'}\nA operação será auditada. Não configura alertas Azure nem envia notificações.`)) return;
    setSaving(true); setActionError(null); setMessage(null);
    try {
      const saved = await governanceApi.saveBudget(editing?.id ?? crypto.randomUUID(), input);
      setEditing(null); setDraft(emptyDraft()); list.refetch();
      if (selected === saved.id) evaluation.refetch(); else setSelected(saved.id);
      setMessage('Orçamento salvo. Consumo e projeção são avaliados separadamente com os dados disponíveis.');
    } catch (error) { setActionError(error instanceof Error ? error.message : 'Falha ao salvar.'); }
    finally { setSaving(false); }
  }

  async function remove(budget: GovernanceBudget) {
    if (!isAdmin || saving || !window.confirm(`Excluir o orçamento "${budget.definition.name}" (${budget.definition.month}, ${money(budget.definition.amount, budget.definition.currency)})?\nSubscription: ${budget.definition.subscriptionId}\nA exclusão será auditada e não poderá ser desfeita.`)) return;
    setSaving(true); setActionError(null); setMessage(null);
    try {
      await governanceApi.deleteBudget(budget);
      if (selected === budget.id) setSelected(null);
      if (editing?.id === budget.id) { setEditing(null); setDraft(emptyDraft()); }
      list.refetch(); setMessage('Orçamento excluído.');
    } catch (error) { setActionError(error instanceof Error ? error.message : 'Falha ao excluir.'); }
    finally { setSaving(false); }
  }

  const result = !evaluation.loading && !evaluation.error && evaluation.data?.budget.id === selected ? evaluation.data : null;
  return <div className="page">
    <div className="page-header"><h1>Orçamentos e previsão</h1><p className="text-muted">Mês civil por subscription, opcionalmente restrito ao centro de custo importado. Sem conversão de moeda ou notificações automáticas.</p></div>
    <p className="text-muted">Orçamento por time indisponível nesta versão: não há consulta de ownership paginada/limitada. Não é feito rateio implícito entre times.</p>
    {actionError && <div className="alert alert--warning" role="alert">{actionError} Atualize a lista antes de tentar novamente. Em conflito, abra a edição novamente; em falha de rede/auditoria, a alteração pode ter sido aplicada: reconcilie o resultado, não repita a escrita cegamente.</div>}
    {message && <div className="alert" role="status">{message}</div>}
    {isAdmin && <section className="section"><h2>{editing ? 'Editar orçamento' : 'Novo orçamento'}</h2>
      <form className="filters" onSubmit={(event) => { event.preventDefault(); void save(); }}>
        <div className="filter-group"><label htmlFor="budget-name">Nome</label><input id="budget-name" required maxLength={120} value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} /></div>
        <div className="filter-group"><label htmlFor="budget-subscription">Subscription ID</label><input id="budget-subscription" required value={draft.subscriptionId} onChange={(e) => setDraft({ ...draft, subscriptionId: e.target.value })} /></div>
        <div className="filter-group"><label htmlFor="budget-month">Mês civil (UTC)</label><input id="budget-month" type="month" required min="2000-01" max="2100-12" value={draft.month} onChange={(e) => setDraft({ ...draft, month: e.target.value })} /></div>
        <div className="filter-group"><label htmlFor="budget-currency">Moeda</label><input id="budget-currency" required pattern="[A-Za-z]{3}" maxLength={3} value={draft.currency} onChange={(e) => setDraft({ ...draft, currency: e.target.value.toUpperCase() })} /></div>
        <div className="filter-group"><label htmlFor="budget-amount">Limite</label><input id="budget-amount" type="number" required min="0.01" max="1000000000000" step="0.01" value={draft.amount} onChange={(e) => setDraft({ ...draft, amount: e.target.value })} /></div>
        <div className="filter-group"><label htmlFor="budget-center">Centro de custo (opcional)</label><input id="budget-center" maxLength={200} value={draft.costCenter} onChange={(e) => setDraft({ ...draft, costCenter: e.target.value })} placeholder="Vazio = subscription inteira" /></div>
        <button className="btn btn--primary" type="submit" disabled={saving}>{saving ? 'Salvando…' : 'Salvar'}</button>
        {editing && <button className="btn btn--secondary" type="button" disabled={saving} onClick={() => { setEditing(null); setDraft(emptyDraft()); }}>Cancelar edição</button>}
      </form>
    </section>}
    <section className="section"><div className="section-header"><h2>Orçamentos cadastrados</h2><button className="btn btn--secondary" onClick={list.refetch} disabled={list.loading || saving}>Atualizar lista</button></div>
      {list.loading && <p className="loading" role="status">Carregando…</p>}
      {list.error && <div className="alert alert--warning" role="alert">{list.error}</div>}
      {!list.loading && !list.error && (list.data ?? []).length === 0 && <p>Nenhum orçamento cadastrado.</p>}
      {!list.loading && !list.error && (list.data ?? []).length > 0 && <div className="table-container"><table>
        <thead><tr><th>Nome / subscription</th><th>Mês</th><th>Escopo</th><th>Limite</th><th>Ações</th></tr></thead>
        <tbody>{(list.data ?? []).map((budget) => <tr key={budget.id}>
          <td>{budget.definition.name}<div className="text-muted">{budget.definition.subscriptionId}</div></td><td>{budget.definition.month}</td>
          <td>{budget.definition.costCenter ?? 'Subscription inteira'}</td><td>{money(budget.definition.amount, budget.definition.currency)}</td>
          <td><button className="btn btn--secondary" onClick={() => { if (selected === budget.id) evaluation.refetch(); else setSelected(budget.id); }}>Avaliar</button>
            {isAdmin && <><button className="btn btn--secondary" disabled={saving} onClick={() => edit(budget)}>Editar</button><button className="btn btn--secondary" disabled={saving} onClick={() => void remove(budget)}>Excluir</button></>}</td>
        </tr>)}</tbody>
      </table></div>}
    </section>
    {selected && <section className="section"><h2>Consumo e projeção</h2>
      {evaluation.loading && <p className="loading" role="status">Lendo os snapshots do mês…</p>}
      {evaluation.error && <div className="alert alert--warning" role="alert">{evaluation.error}</div>}
      {!evaluation.loading && !evaluation.error && !result && <p>Orçamento não encontrado ou endpoint indisponível. Atualize a lista.</p>}
      {result && <>
        <h3>{result.budget.definition.name} · {result.budget.definition.month}</h3>
        <p>{result.publicationMode} · Dados até {result.dataAsOf ?? 'indisponível'} · {result.observedDays}/{result.expectedDays} dias observados/esperados</p>
        <div className="cards-grid">
          <Card title="Consumo observado" value={money(result.observedCost, result.budget.definition.currency)} subtitle={result.consumptionPercent == null ? 'Sem percentual disponível' : `${result.consumptionPercent}% do orçamento (pode ser parcial)`} />
          <Card title="Estimativa para o fim do mês" value={money(result.estimatedMonthEnd, result.budget.definition.currency)} subtitle={result.forecastReliable ? 'Base completa, mínimo de 7 dias; ainda é estimativa' : 'Não confiável: ver cobertura, atribuição e publicação'} variant={result.forecastReliable ? 'default' : 'warning'} />
          <Card title="Previsão de estouro" value={result.forecastExceedsBudget == null ? 'Indisponível' : result.forecastExceedsBudget ? 'Estouro estimado' : 'Não indicado pela estimativa'} subtitle={result.forecastReliable ? 'Não é garantia' : 'Não usar isoladamente para decisão financeira'} variant={result.forecastExceedsBudget ? 'warning' : 'default'} />
        </div>
        <p>Patamares sobre consumo observado: {[50, 80, 100].map((threshold) => `${threshold}% ${result.observedCost == null ? 'indisponível' : result.reachedThresholds.includes(threshold) ? 'atingido' : 'não atingido no observado'}`).join(' · ')}</p>
        <p>50/80/100% são indicadores nesta tela, calculados ao consultar: não há dispatch, email, webhook ou alerta Azure configurado.</p>
        <p>Dias sem custo validado na moeda solicitada: {(result.missingDates ?? []).length ? result.missingDates.join(', ') : 'Nenhum entre os dias esperados'}</p>
        <div className="alert alert--warning"><ul>{(result.warnings ?? []).map((warning) => <li key={warning}>{warning}</li>)}</ul></div>
      </>}
    </section>}
  </div>;
}