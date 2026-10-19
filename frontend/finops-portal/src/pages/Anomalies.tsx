import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import { ChevronDown, ChevronRight } from 'lucide-react';
import StatusBadge from '../components/StatusBadge';
import SubscriptionCostPanel, { type AnomalyDayMarker } from '../components/cost/SubscriptionCostPanel';
import { useAccess } from '../auth/AccessContext';
import { ApiError, api } from '../services/api';
import { useFetch } from '../hooks/useFetch';
import type { CostAnomaly } from '../types/api';

function formatCurrency(value: number): string {
  return value.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' });
}

function formatPercent(value: number): string {
  return `${value >= 0 ? '+' : ''}${value.toFixed(1)}%`;
}

function parseDateOnly(value?: string | null): Date | null {
  if (!value) return null;
  const parsed = new Date(`${value}T00:00:00`);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

function compareDateText(a?: string | null, b?: string | null): number {
  const dateA = parseDateOnly(a);
  const dateB = parseDateOnly(b);
  if (!dateA && !dateB) return 0;
  if (!dateA) return -1;
  if (!dateB) return 1;
  return dateA.getTime() - dateB.getTime();
}

function getCycleRemainingDays(anomaly: CostAnomaly, fallbackReferenceDate: string): number {
  const turnDate = parseDateOnly(anomaly.currentCycleTurnDate);
  const referenceDate = parseDateOnly(anomaly.requestedDate ?? fallbackReferenceDate);
  if (!turnDate || !referenceDate) {
    return anomaly.currentCycleRemainingDays ?? 0;
  }

  const diffMs = turnDate.getTime() - referenceDate.getTime();
  return Math.max(0, Math.floor(diffMs / (24 * 60 * 60 * 1000)));
}

function formatDate(value?: string | null): string {
  const parsed = parseDateOnly(value);
  return parsed?.toLocaleDateString('pt-BR') ?? '—';
}

function getDataStatusLabel(status: CostAnomaly['dataStatus']): string {
  const labels: Record<CostAnomaly['dataStatus'], string> = {
    Consolidated: 'Consolidado',
    InsufficientData: 'Histórico insuficiente',
    NoCostData: 'Sem dados',
    QueryFailed: 'Falha na consulta',
  };
  return labels[status];
}

function hasUsableCostData(anomaly: CostAnomaly): boolean {
  return anomaly.dataStatus !== 'NoCostData' && anomaly.dataStatus !== 'QueryFailed';
}

function getCostSourcePresentation(anomaly: CostAnomaly): {
  label: string;
  className: string;
  description: string;
} {
  const dataAsOf = formatDate(anomaly.costDataAsOf ?? anomaly.analysisDate);

  if (anomaly.dataStatus === 'QueryFailed') {
    return {
      label: 'Fontes falharam',
      className: 'badge badge--danger',
      description: 'O Export não estava disponível e o fallback da Query API também falhou.',
    };
  }

  if (anomaly.costSource === 'CostManagementExport') {
    return {
      label: 'Export',
      className: 'badge badge--info',
      description: `Fonte: Cost Management Export. Dados consolidados até ${dataAsOf}.`,
    };
  }

  if (anomaly.costSource === 'CostManagementQueryApi') {
    const fallbackDetail = anomaly.costFallbackReason
      ? ` Motivo: ${anomaly.costFallbackReason}`
      : '';
    return {
      label: 'Fallback API',
      className: 'badge badge--warning',
      description: `Fonte: fallback da Cost Management Query API. Dados consolidados até ${dataAsOf}.${fallbackDetail}`,
    };
  }

  return {
    label: 'Sem fonte',
    className: 'badge badge--muted',
    description: 'Nenhuma fonte de custo utilizável foi registrada para esta análise.',
  };
}

type SortField =
  | 'subscription'
  | 'team'
  | 'todayCost'
  | 'currentCycleCost'
  | 'currentCycleDailyAverage'
  | 'currentCycleTurnDate'
  | 'averageLastDays'
  | 'increasePercent'
  | 'dailyBudget'
  | 'monthlyProjection'
  | 'severity'
  | 'dataStatus';
type SortDir = 'asc' | 'desc';
type ColumnField = 'subscription' | 'team' | 'todayCost' | 'currentCycleCost' | 'currentCycleDailyAverage' | 'currentCycleTurnDate' | 'averageLastDays' | 'increasePercent' | 'dailyBudget' | 'monthlyProjection' | 'severity' | 'dataStatus';
type ColumnVisibility = Record<ColumnField, boolean>;

const severityOrder: Record<string, number> = { Critical: 0, High: 1, Medium: 2, Low: 3, Info: 4 };

export default function Anomalies() {
  const { isAdmin } = useAccess();
  const [date, setDate] = useState(() => new Date().toISOString().slice(0, 10));
  const [days, setDays] = useState(3);
  const [filterTeam, setFilterTeam] = useState('');
  const [filterSubInput, setFilterSubInput] = useState('');
  const [actionLoading, setActionLoading] = useState(false);
  const [actionMessage, setActionMessage] = useState('');
  const [actionError, setActionError] = useState('');
  const [activeRunId, setActiveRunId] = useState<string | null>(null);
  const [overrideSubscriptionId, setOverrideSubscriptionId] = useState('');
  const [overrideBillingStartDay, setOverrideBillingStartDay] = useState(1);
  const [overrideLoading, setOverrideLoading] = useState(false);
  const [overrideMessage, setOverrideMessage] = useState('');
  const [overrideError, setOverrideError] = useState('');
  const [sortField, setSortField] = useState<SortField>('todayCost');
  const [sortDir, setSortDir] = useState<SortDir>('desc');
  const defaultColumnWidths: Record<ColumnField, number> = {
    subscription: 220,
    team: 150,
    todayCost: 140,
    currentCycleCost: 150,
    currentCycleDailyAverage: 160,
    currentCycleTurnDate: 145,
    averageLastDays: 140,
    increasePercent: 120,
    dailyBudget: 130,
    monthlyProjection: 150,
    severity: 130,
    dataStatus: 170,
  };
  const defaultVisibleColumns: ColumnVisibility = {
    subscription: true,
    team: true,
    todayCost: true,
    currentCycleCost: true,
    currentCycleDailyAverage: true,
    currentCycleTurnDate: true,
    averageLastDays: true,
    increasePercent: true,
    dailyBudget: true,
    monthlyProjection: true,
    severity: true,
    dataStatus: true,
  };
  const [columnWidths, setColumnWidths] = useState<Record<ColumnField, number>>(() => {
    const stored = localStorage.getItem('anomalies-column-widths');
    if (!stored) return defaultColumnWidths;
    try {
      const parsed = JSON.parse(stored) as Partial<Record<ColumnField, number>>;
      return { ...defaultColumnWidths, ...parsed };
    } catch {
      return defaultColumnWidths;
    }
  });
  const [visibleColumns, setVisibleColumns] = useState<ColumnVisibility>(() => {
    const stored = localStorage.getItem('anomalies-visible-columns');
    if (!stored) return defaultVisibleColumns;
    try {
      const parsed = JSON.parse(stored) as Partial<ColumnVisibility>;
      return { ...defaultVisibleColumns, ...parsed };
    } catch {
      return defaultVisibleColumns;
    }
  });
  const [expandedSubscriptionId, setExpandedSubscriptionId] = useState<string | null>(null);
  const [resizingField, setResizingField] = useState<ColumnField | null>(null);
  const [resizeStartX, setResizeStartX] = useState(0);
  const [resizeStartWidth, setResizeStartWidth] = useState(0);
  const [isColumnMenuOpen, setIsColumnMenuOpen] = useState(false);
  const columnMenuRef = useRef<HTMLDivElement | null>(null);

  const { data: anomalies, loading, error, refetch: refetchAnomalies } = useFetch(
    () => api.getAnomalies(date, days),
    [date, days]
  );
  const { data: ownershipConfig } = useFetch(
    () => api.getOwnershipTeams().catch(() => ({
      version: 1,
      updatedAt: '',
      updatedBy: '',
      teamsCount: 0,
      teams: [],
    })),
    []
  );
  const { data: billingOverrides, refetch: refetchBillingOverrides } = useFetch(
    () => isAdmin
      ? api.getBillingPeriodOverrides().catch(() => [])
      : Promise.resolve([]),
    [isAdmin]
  );

  useEffect(() => {
    if (!activeRunId) return;

    let cancelled = false;
    const pollStatus = async () => {
      try {
        const status = await api.getCostAnomalyRunStatus(activeRunId);
        if (cancelled) return;

        setActionMessage(
          `Análise em andamento: ${status.completedSubscriptions}/${status.totalSubscriptions} subscriptions processadas`
          + (status.failedSubscriptions > 0 ? ` (${status.failedSubscriptions} com falha).` : '.')
        );
        if (status.status === 'Completed') {
          await refetchAnomalies();
          if (cancelled) return;
          setActionMessage(
            `Análise concluída: ${status.completedSubscriptions} subscriptions processadas`
            + (status.failedSubscriptions > 0 ? `, ${status.failedSubscriptions} com falha.` : '.')
          );
          setActiveRunId(null);
        }
      } catch (err) {
        if (!cancelled) {
          if (err instanceof ApiError && err.status === 404) {
            setActionMessage('Análise enfileirada. Preparando subscriptions...');
            return;
          }
          if (err instanceof ApiError && (err.status === 401 || err.status === 403)) {
            setActionError(`Erro ao consultar progresso: ${err.message}`);
            setActiveRunId(null);
            return;
          }
          setActionError(`Erro ao consultar progresso: ${err instanceof Error ? err.message : String(err)}`);
        }
      }
    };

    void pollStatus();
    const intervalId = window.setInterval(() => void pollStatus(), 5_000);
    return () => {
      cancelled = true;
      window.clearInterval(intervalId);
    };
  }, [activeRunId, refetchAnomalies]);

  const items = useMemo(() => anomalies ?? [], [anomalies]);
  const ownershipTeams = useMemo(() => ownershipConfig?.teams ?? [], [ownershipConfig?.teams]);
  const overrideBySubscription = useMemo(() => {
    const map = new Map<string, { billingCycleStartDay: number; updatedBy: string; updatedAt: string }>();
    (billingOverrides ?? []).forEach((override) => {
      map.set(override.subscriptionId, {
        billingCycleStartDay: override.billingCycleStartDay,
        updatedBy: override.updatedBy,
        updatedAt: override.updatedAt,
      });
    });
    return map;
  }, [billingOverrides]);

  const teamBySubscription = useMemo(() => {
    const map = new Map<string, { teamId: string; teamName: string }>();
    ownershipTeams.forEach((team) => {
      team.subscriptions.forEach((subscription) => {
        map.set(subscription.subscriptionId, { teamId: team.id, teamName: team.name });
      });
    });
    return map;
  }, [ownershipTeams]);

  // Deduplicate: keep the newest report even when its consolidated cost date is older.
  const { deduplicated, staleSubscriptionIds } = useMemo(() => {
    const map = new Map<string, CostAnomaly>();
    const newestBySubscription = new Map<string, CostAnomaly>();
    const severityRank: Record<string, number> = { None: 0, Low: 1, Medium: 2, High: 3, Critical: 4 };
    items.forEach(a => {
      const newest = newestBySubscription.get(a.subscriptionId);
      if (!newest
        || compareDateText(a.requestedDate ?? a.date, newest.requestedDate ?? newest.date) > 0) {
        newestBySubscription.set(a.subscriptionId, a);
      }

      const existing = map.get(a.subscriptionId);
      if (!existing) {
        map.set(a.subscriptionId, a);
        return;
      }

      const reportDateA = a.requestedDate ?? a.date;
      const reportDateB = existing.requestedDate ?? existing.date;
      const reportDateComparison = compareDateText(reportDateA, reportDateB);
      const analysisDateComparison = compareDateText(a.analysisDate, existing.analysisDate);
      const isSameReportAndAnalysisDate =
        reportDateComparison === 0 && analysisDateComparison === 0;

      // A failed collection carries no cost at all, so letting it win by being newer would erase
      // a perfectly good reading from a previous day.
      const usableA = hasUsableCostData(a);
      const usableExisting = hasUsableCostData(existing);
      if (usableA !== usableExisting) {
        if (usableA) {
          map.set(a.subscriptionId, a);
        }
        return;
      }

      const shouldReplace =
        reportDateComparison > 0
        || (reportDateComparison === 0 && analysisDateComparison > 0)
        || (isSameReportAndAnalysisDate && (
          (a.hasAnomaly && !existing.hasAnomaly)
          || (a.hasAnomaly === existing.hasAnomaly && (severityRank[a.severity] ?? 0) > (severityRank[existing.severity] ?? 0))
          || (a.hasAnomaly === existing.hasAnomaly
            && (severityRank[a.severity] ?? 0) === (severityRank[existing.severity] ?? 0)
            && compareDateText(a.date, existing.date) > 0)
        ));

      if (shouldReplace) {
        map.set(a.subscriptionId, a);
      }
    });

    const stale = new Set<string>();
    map.forEach((chosen, subscriptionId) => {
      const newest = newestBySubscription.get(subscriptionId);
      if (newest && !hasUsableCostData(newest) && hasUsableCostData(chosen)) {
        stale.add(subscriptionId);
      }
    });

    return { deduplicated: [...map.values()], staleSubscriptionIds: stale };
  }, [items]);

  // Build subscription id→name map
  const subNameMap = useMemo(() => {
    const m = new Map<string, string>();
    items.forEach(a => {
      if (a.subscriptionName && !m.has(a.subscriptionId)) {
        m.set(a.subscriptionId, a.subscriptionName);
      }
    });
    return m;
  }, [items]);

  // The report list already carries one entry per analysed day, so day markers cost no extra request.
  const anomalyDaysBySubscription = useMemo(() => {
    const map = new Map<string, AnomalyDayMarker[]>();
    items.forEach((anomaly) => {
      const day = anomaly.analysisDate ?? anomaly.date;
      if (!day) return;
      const days = map.get(anomaly.subscriptionId) ?? [];
      if (!days.some((entry) => entry.date === day)) {
        days.push({ date: day, hasAnomaly: anomaly.hasAnomaly });
      }
      map.set(anomaly.subscriptionId, days);
    });
    return map;
  }, [items]);

  const uniqueTeams = useMemo(() => {
    const map = new Map<string, string>();
    deduplicated.forEach((a) => {
      const teamId = a.teamId ?? teamBySubscription.get(a.subscriptionId)?.teamId ?? '';
      const teamName = a.teamName ?? teamBySubscription.get(a.subscriptionId)?.teamName ?? '';
      if (teamId) {
        map.set(teamId, teamName || teamId);
      }
    });
    return [...map.entries()].sort((a, b) => a[1].localeCompare(b[1]));
  }, [deduplicated, teamBySubscription]);

  const filteredByTeam = filterTeam
    ? deduplicated.filter((a) => (a.teamId ?? teamBySubscription.get(a.subscriptionId)?.teamId ?? '') === filterTeam)
    : deduplicated;

  const uniqueSubs = useMemo(() => [...new Set(filteredByTeam.map(a => a.subscriptionId))], [filteredByTeam]);
  const subOptions = useMemo(
    () => uniqueSubs.map((subscriptionId) => ({
      subscriptionId,
      name: subNameMap.get(subscriptionId) || subscriptionId,
      display: `${subNameMap.get(subscriptionId) || subscriptionId} (${subscriptionId})`,
    })),
    [uniqueSubs, subNameMap]
  );
  const filterSub = useMemo(() => {
    const normalized = filterSubInput.trim();
    if (!normalized) return '';
    const match = subOptions.find((option) =>
      option.subscriptionId.localeCompare(normalized, undefined, { sensitivity: 'accent' }) === 0
      || option.display.localeCompare(normalized, undefined, { sensitivity: 'accent' }) === 0
    );
    return match?.subscriptionId ?? '';
  }, [filterSubInput, subOptions]);

  const filtered = filterSub ? filteredByTeam.filter(a => a.subscriptionId === filterSub) : filteredByTeam;

  // Sort
  const sorted = useMemo(() => {
    const mul = sortDir === 'asc' ? 1 : -1;
    return [...filtered].sort((a, b) => {
      switch (sortField) {
        case 'subscription': return mul * (a.subscriptionName || a.subscriptionId).localeCompare(b.subscriptionName || b.subscriptionId);
        case 'team': {
          const teamA = a.teamName ?? teamBySubscription.get(a.subscriptionId)?.teamName ?? '';
          const teamB = b.teamName ?? teamBySubscription.get(b.subscriptionId)?.teamName ?? '';
          return mul * teamA.localeCompare(teamB);
        }
        case 'todayCost': return mul * (a.todayCost - b.todayCost);
        case 'currentCycleCost': return mul * ((a.currentCycleCost ?? 0) - (b.currentCycleCost ?? 0));
        case 'currentCycleDailyAverage': return mul * ((a.currentCycleDailyAverage ?? 0) - (b.currentCycleDailyAverage ?? 0));
        case 'currentCycleTurnDate': return mul * ((a.currentCycleTurnDate ?? '').localeCompare(b.currentCycleTurnDate ?? ''));
        case 'averageLastDays': return mul * (a.averageLastDays - b.averageLastDays);
        case 'increasePercent': return mul * (a.increasePercent - b.increasePercent);
        case 'dailyBudget': return mul * (a.dailyBudget - b.dailyBudget);
        case 'monthlyProjection': return mul * (a.monthlyProjection - b.monthlyProjection);
        case 'severity': return mul * ((severityOrder[a.severity] ?? 9) - (severityOrder[b.severity] ?? 9));
        case 'dataStatus': return mul * a.dataStatus.localeCompare(b.dataStatus);
        default: return 0;
      }
    });
  }, [filtered, sortField, sortDir, teamBySubscription]);

  const visibleAnomalyCount = sorted.filter((anomaly) => anomaly.hasAnomaly).length;
  const visibleSubscriptionCount = new Set(sorted.map((anomaly) => anomaly.subscriptionId)).size;

  const handleSort = (field: SortField) => {
    if (sortField === field) {
      setSortDir(d => d === 'asc' ? 'desc' : 'asc');
    } else {
      setSortField(field);
      setSortDir('desc');
    }
  };

  const sortIcon = (field: SortField) => {
    if (sortField !== field) return ' ↕';
    return sortDir === 'asc' ? ' ↑' : ' ↓';
  };

  useEffect(() => {
    localStorage.setItem('anomalies-column-widths', JSON.stringify(columnWidths));
  }, [columnWidths]);

  useEffect(() => {
    localStorage.setItem('anomalies-visible-columns', JSON.stringify(visibleColumns));
  }, [visibleColumns]);

  useEffect(() => {
    if (!isColumnMenuOpen) return;

    const onPointerDown = (event: MouseEvent) => {
      if (!columnMenuRef.current) return;
      if (!columnMenuRef.current.contains(event.target as Node)) {
        setIsColumnMenuOpen(false);
      }
    };

    const onEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setIsColumnMenuOpen(false);
      }
    };

    window.addEventListener('mousedown', onPointerDown);
    window.addEventListener('keydown', onEscape);
    return () => {
      window.removeEventListener('mousedown', onPointerDown);
      window.removeEventListener('keydown', onEscape);
    };
  }, [isColumnMenuOpen]);

  useEffect(() => {
    if (!resizingField) return;

    const minimumWidth: Record<ColumnField, number> = {
      subscription: 170,
      team: 120,
      todayCost: 120,
      currentCycleCost: 120,
      currentCycleDailyAverage: 130,
      currentCycleTurnDate: 125,
      averageLastDays: 120,
      increasePercent: 110,
      dailyBudget: 110,
      monthlyProjection: 130,
      severity: 110,
      dataStatus: 150,
    };

    const onMouseMove = (event: MouseEvent) => {
      const delta = event.clientX - resizeStartX;
      setColumnWidths((current) => ({
        ...current,
        [resizingField]: Math.max(minimumWidth[resizingField], resizeStartWidth + delta),
      }));
    };

    const onMouseUp = () => {
      setResizingField(null);
    };

    window.addEventListener('mousemove', onMouseMove);
    window.addEventListener('mouseup', onMouseUp);
    return () => {
      window.removeEventListener('mousemove', onMouseMove);
      window.removeEventListener('mouseup', onMouseUp);
    };
  }, [resizingField, resizeStartWidth, resizeStartX]);

  const columns: [ColumnField, string][] = [
    ['subscription', 'Subscription'],
    ['team', 'Time'],
    ['todayCost', 'Último Custo Consolidado'],
    ['currentCycleCost', 'Acumulado até'],
    ['currentCycleDailyAverage', 'Média Ciclo/Dia'],
    ['currentCycleTurnDate', 'Vira em'],
    ['averageLastDays', `Média ${days > 1 ? `${days}d` : '1d'}`],
    ['increasePercent', 'Variação'],
    ['dailyBudget', 'Meta Diária'],
    ['monthlyProjection', 'Projeção Mensal'],
    ['severity', 'Severidade'],
    ['dataStatus', 'Status dos Dados'],
  ];
  const visibleColumnEntries = columns.filter(([field]) => visibleColumns[field]);
  const visibleColumnCount = Math.max(1, visibleColumnEntries.length);

  const startResize = (field: ColumnField, event: React.MouseEvent<HTMLSpanElement>) => {
    event.preventDefault();
    event.stopPropagation();
    setResizingField(field);
    setResizeStartX(event.clientX);
    setResizeStartWidth(columnWidths[field]);
  };

  const buildCycleTooltip = (a: CostAnomaly): string => {
    const analysisDateText = a.analysisDate ?? a.date ?? '—';
    const requestedDateText = a.requestedDate ?? date;
    const parts = [
      `Início do ciclo: ${a.currentCycleStartDate ?? '—'}`,
      `Data de referência: ${requestedDateText}`,
      `Data analisada: ${analysisDateText}`,
      `Origem: ${a.currentCycleSource ?? '—'}`,
      `Confiança: ${a.currentCycleConfidence ?? '—'}`,
    ];

    if (a.currentCycleNote) {
      parts.push(a.currentCycleNote);
    }

    if (a.currentCycleConfidence === 'Low') {
      parts.push('Período estimado. Configure o ciclo de cobrança da assinatura para maior precisão.');
    }

    return parts.join(' | ');
  };

  const handleSaveBillingOverride = async () => {
    const subscriptionId = overrideSubscriptionId.trim();
    if (!subscriptionId) {
      setOverrideError('Informe a subscription para salvar o override.');
      setOverrideMessage('');
      return;
    }

    if (!Number.isFinite(overrideBillingStartDay) || overrideBillingStartDay < 1 || overrideBillingStartDay > 28) {
      setOverrideError('O dia inicial deve estar entre 1 e 28.');
      setOverrideMessage('');
      return;
    }

    setOverrideLoading(true);
    setOverrideError('');
    setOverrideMessage('');
    try {
      await api.upsertBillingPeriodOverride(subscriptionId, overrideBillingStartDay);
      await refetchBillingOverrides();
      await refetchAnomalies();
      setOverrideMessage('Override de billing period salvo com sucesso.');
    } catch (err) {
      const status = err instanceof ApiError ? err.status : null;
      if (status === 403) {
        setOverrideError('403: acesso negado. Apenas admins podem alterar overrides.');
      } else if (status === 401) {
        setOverrideError('401: sessão expirada ou usuário não autenticado. Faça login novamente.');
      } else {
        setOverrideError(`Erro ao salvar override: ${err instanceof Error ? err.message : String(err)}`);
      }
    } finally {
      setOverrideLoading(false);
    }
  };

  const handleDeleteBillingOverride = async () => {
    const subscriptionId = overrideSubscriptionId.trim();
    if (!subscriptionId) {
      setOverrideError('Informe a subscription para remover o override.');
      setOverrideMessage('');
      return;
    }

    setOverrideLoading(true);
    setOverrideError('');
    setOverrideMessage('');
    try {
      await api.deleteBillingPeriodOverride(subscriptionId);
      await refetchBillingOverrides();
      await refetchAnomalies();
      setOverrideMessage('Override de billing period removido com sucesso.');
    } catch (err) {
      const status = err instanceof ApiError ? err.status : null;
      if (status === 404) {
        setOverrideError('404: não existe override salvo para esta subscription.');
      } else if (status === 403) {
        setOverrideError('403: acesso negado. Apenas admins podem alterar overrides.');
      } else if (status === 401) {
        setOverrideError('401: sessão expirada ou usuário não autenticado. Faça login novamente.');
      } else {
        setOverrideError(`Erro ao remover override: ${err instanceof Error ? err.message : String(err)}`);
      }
    } finally {
      setOverrideLoading(false);
    }
  };

  const handleRunAnalysis = async () => {
    setActionLoading(true);
    setActionMessage('');
    setActionError('');
    try {
      const run = await api.runCostAnomalyAnalysis();
      setActiveRunId(run.runId);
      setActionMessage('Análise de anomalias enfileirada. Preparando subscriptions...');
    } catch (err) {
      const status = err instanceof ApiError ? err.status : null;
      if (status === 401) {
        setActionError('401: sessão expirada ou usuário não autenticado. Faça login novamente.');
      } else if (status === 403) {
        setActionError('403: acesso negado. A execução manual exige FinOps.Admin.');
      } else if (err instanceof Error && err.message.includes('Tempo limite')) {
        setActionError('Tempo limite excedido. A análise pode continuar no backend; aguarde alguns minutos e atualize os dados.');
      } else {
        setActionError(`Erro ao executar análise: ${err instanceof Error ? err.message : String(err)}`);
      }
    } finally {
      setActionLoading(false);
    }
  };

  return (
    <div className="page">
      <div className="page-header">
        <h2>Anomalias de Custo</h2>
        <span className="page-date">
          {visibleAnomalyCount} {visibleAnomalyCount === 1 ? 'anomalia' : 'anomalias'}
          {' · '}
          {visibleSubscriptionCount} {visibleSubscriptionCount === 1 ? 'assinatura' : 'assinaturas'}
        </span>
      </div>

      <div className="filters filters--compact">
        <div className="filter-group">
          <label htmlFor="anom-date">Data</label>
          <input
            id="anom-date"
            type="date"
            value={date}
            onChange={(e) => setDate(e.target.value)}
          />
        </div>
        <div className="filter-group">
          <label htmlFor="anom-days">Período (dias)</label>
          <select
            id="anom-days"
            value={days}
            onChange={(e) => setDays(Number(e.target.value))}
          >
            <option value={1}>1 dia</option>
            <option value={3}>3 dias</option>
            <option value={7}>7 dias</option>
            <option value={14}>14 dias</option>
            <option value={30}>30 dias</option>
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="anom-team">Time</label>
          <select
            id="anom-team"
            value={filterTeam}
            onChange={(e) => {
              setFilterTeam(e.target.value);
              setFilterSubInput('');
            }}
          >
            <option value="">Todos</option>
            {uniqueTeams.map(([teamId, teamName]) => (
              <option key={teamId} value={teamId}>{teamName}</option>
            ))}
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="anom-sub">Subscription</label>
          <input
            id="anom-sub"
            list="anom-sub-options"
            value={filterSubInput}
            onChange={(e) => setFilterSubInput(e.target.value)}
            placeholder="Digite nome ou subscriptionId"
          />
          <datalist id="anom-sub-options">
            {subOptions.map((option) => (
              <option key={`anom-sub-${option.subscriptionId}`} value={option.display} />
            ))}
            {subOptions.map((option) => (
              <option key={`anom-sub-id-${option.subscriptionId}`} value={option.subscriptionId} />
            ))}
          </datalist>
        </div>
        <div className="filter-group recommendations-column-actions">
          <label>Colunas</label>
          <div className="column-picker" ref={columnMenuRef}>
            <button
              type="button"
              className="column-picker-trigger filter-control"
              onClick={() => setIsColumnMenuOpen((current) => !current)}
              aria-haspopup="menu"
              aria-expanded={isColumnMenuOpen}
            >
              Selecionar colunas
            </button>
            {isColumnMenuOpen && (
              <div className="column-picker-menu" role="menu">
                {columns.map(([field, label]) => (
                  <label key={`toggle-anom-${field}`} className="column-picker-item">
                    <input
                      type="checkbox"
                      checked={visibleColumns[field]}
                      onChange={(e) =>
                        setVisibleColumns((current) => {
                          const next = { ...current, [field]: e.target.checked };
                          const enabledCount = Object.values(next).filter(Boolean).length;
                          if (enabledCount === 0) {
                            return current;
                          }
                          return next;
                        })
                      }
                    />
                    <span>{label}</span>
                  </label>
                ))}
              </div>
            )}
          </div>
        </div>
        <div className="filter-group recommendations-column-actions">
          <label>&nbsp;</label>
          <button type="button" className="filter-control filter-control--button" onClick={() => setColumnWidths(defaultColumnWidths)}>
            Resetar larguras
          </button>
        </div>
        {isAdmin && (
          <div className="filter-group recommendations-column-actions">
            <label>&nbsp;</label>
            <button type="button" className="filter-control filter-control--button" onClick={handleRunAnalysis} disabled={actionLoading || activeRunId !== null}>
              {actionLoading ? 'Enfileirando...' : activeRunId ? 'Processando...' : 'Executar análise'}
            </button>
          </div>
        )}
      </div>

      {actionMessage && (
        <div className="alert">
          {actionMessage}
        </div>
      )}
      {actionError && (
        <div className="alert alert--warning">
          {actionError}
        </div>
      )}
      {overrideMessage && (
        <div className="alert alert--success">
          {overrideMessage}
        </div>
      )}
      {overrideError && (
        <div className="alert alert--warning">
          {overrideError}
        </div>
      )}

      {isAdmin && (
        <div className="section">
          <h3>Ajuste manual de billing period (admins)</h3>
          <div className="filters filters--compact">
            <div className="filter-group">
              <label htmlFor="billing-override-subscription">Subscription</label>
              <input
                id="billing-override-subscription"
                className="filter-control"
                list="billing-override-subscriptions"
                value={overrideSubscriptionId}
                onChange={(e) => {
                  const nextSubscriptionId = e.target.value;
                  setOverrideSubscriptionId(nextSubscriptionId);
                  const existingOverride = overrideBySubscription.get(nextSubscriptionId.trim());
                  if (existingOverride) {
                    setOverrideBillingStartDay(existingOverride.billingCycleStartDay);
                  }
                }}
                placeholder="subscriptionId"
              />
              <datalist id="billing-override-subscriptions">
                {uniqueSubs.map((subscriptionId) => (
                  <option key={subscriptionId} value={subscriptionId}>
                    {subNameMap.get(subscriptionId) ?? subscriptionId}
                  </option>
                ))}
              </datalist>
            </div>
            <div className="filter-group">
              <label htmlFor="billing-override-turn-day">Dia de início do ciclo</label>
              <input
                id="billing-override-turn-day"
                className="filter-control"
                type="number"
                min={1}
                max={28}
                value={overrideBillingStartDay}
                onChange={(e) => setOverrideBillingStartDay(Number(e.target.value))}
              />
            </div>
            <div className="filter-group recommendations-column-actions">
              <label>&nbsp;</label>
              <button
                type="button"
                className="filter-control filter-control--button"
                onClick={handleSaveBillingOverride}
                disabled={overrideLoading}
              >
                {overrideLoading ? 'Salvando...' : 'Salvar override'}
              </button>
            </div>
            <div className="filter-group recommendations-column-actions">
              <label>&nbsp;</label>
              <button
                type="button"
                className="filter-control filter-control--button"
                onClick={handleDeleteBillingOverride}
                disabled={overrideLoading}
              >
                {overrideLoading ? 'Removendo...' : 'Remover override'}
              </button>
            </div>
          </div>
          {overrideSubscriptionId.trim() && overrideBySubscription.has(overrideSubscriptionId.trim()) && (
            <small>
              Override atual: dia {overrideBySubscription.get(overrideSubscriptionId.trim())?.billingCycleStartDay}
              {' · '}
              por {overrideBySubscription.get(overrideSubscriptionId.trim())?.updatedBy}
            </small>
          )}
        </div>
      )}

      {loading && <div className="loading">Carregando anomalias...</div>}
      {error && (
        <div className="alert alert--warning">
          Não foi possível carregar anomalias.
          <br />
          <small>{error}</small>
        </div>
      )}

      {!loading && !error && (
        <div className="section">
          {sorted.length === 0 ? (
            <div className="alert alert--warning">
              Nenhuma anomalia encontrada para o período selecionado.
              <br />
              <small>Execute POST /api/cost-anomalies/run para gerar dados de anomalia.</small>
            </div>
          ) : (
            <div className="table-container">
              <table className="anomalies-table anomalies-table--manual">
                <colgroup>
                  <col style={{ width: '38px' }} />
                  {visibleColumnEntries.map(([field]) => (
                    <col key={`anom-col-${field}`} style={{ width: `${columnWidths[field]}px` }} />
                  ))}
                </colgroup>
                <thead>
                  <tr>
                    <th aria-label="Detalhar custo diário" />
                    {visibleColumns.subscription && (
                      <th className="sortable" onClick={() => handleSort('subscription')}>
                        Subscription{sortIcon('subscription')}
                        <span className="column-resize-handle" onMouseDown={(event) => startResize('subscription', event)} />
                      </th>
                    )}
                    {visibleColumns.team && (
                      <th className="sortable" onClick={() => handleSort('team')}>
                        Time{sortIcon('team')}
                        <span className="column-resize-handle" onMouseDown={(event) => startResize('team', event)} />
                      </th>
                    )}
                    {visibleColumns.todayCost && (
                      <th className="sortable" onClick={() => handleSort('todayCost')}>
                        Último Custo Consolidado{sortIcon('todayCost')}
                        <span className="column-resize-handle" onMouseDown={(event) => startResize('todayCost', event)} />
                      </th>
                    )}
                    {visibleColumns.currentCycleCost && (
                      <th className="sortable" onClick={() => handleSort('currentCycleCost')}>
                        Acumulado até{sortIcon('currentCycleCost')}
                        <span className="column-resize-handle" onMouseDown={(event) => startResize('currentCycleCost', event)} />
                      </th>
                    )}
                    {visibleColumns.currentCycleDailyAverage && (
                      <th className="sortable" onClick={() => handleSort('currentCycleDailyAverage')}>
                        Média Ciclo/Dia{sortIcon('currentCycleDailyAverage')}
                        <span className="column-resize-handle" onMouseDown={(event) => startResize('currentCycleDailyAverage', event)} />
                      </th>
                    )}
                    {visibleColumns.currentCycleTurnDate && (
                      <th className="sortable" onClick={() => handleSort('currentCycleTurnDate')}>
                        Vira em{sortIcon('currentCycleTurnDate')}
                        <span className="column-resize-handle" onMouseDown={(event) => startResize('currentCycleTurnDate', event)} />
                      </th>
                    )}
                    {visibleColumns.averageLastDays && (
                      <th className="sortable" onClick={() => handleSort('averageLastDays')}>
                        Média {days > 1 ? `${days}d` : '1d'}{sortIcon('averageLastDays')}
                        <span className="column-resize-handle" onMouseDown={(event) => startResize('averageLastDays', event)} />
                      </th>
                    )}
                    {visibleColumns.increasePercent && (
                      <th className="sortable" onClick={() => handleSort('increasePercent')}>
                        Variação{sortIcon('increasePercent')}
                        <span className="column-resize-handle" onMouseDown={(event) => startResize('increasePercent', event)} />
                      </th>
                    )}
                    {visibleColumns.dailyBudget && (
                      <th className="sortable" onClick={() => handleSort('dailyBudget')}>
                        Meta Diária{sortIcon('dailyBudget')}
                        <span className="column-resize-handle" onMouseDown={(event) => startResize('dailyBudget', event)} />
                      </th>
                    )}
                    {visibleColumns.monthlyProjection && (
                      <th className="sortable" onClick={() => handleSort('monthlyProjection')}>
                        Projeção Mensal{sortIcon('monthlyProjection')}
                        <span className="column-resize-handle" onMouseDown={(event) => startResize('monthlyProjection', event)} />
                      </th>
                    )}
                    {visibleColumns.severity && (
                      <th className="sortable" onClick={() => handleSort('severity')}>
                        Severidade{sortIcon('severity')}
                        <span className="column-resize-handle" onMouseDown={(event) => startResize('severity', event)} />
                      </th>
                    )}
                    {visibleColumns.dataStatus && (
                      <th className="sortable" onClick={() => handleSort('dataStatus')}>
                        Status dos Dados{sortIcon('dataStatus')}
                        <span className="column-resize-handle" onMouseDown={(event) => startResize('dataStatus', event)} />
                      </th>
                    )}
                  </tr>
                </thead>
                <tbody>
                  {sorted.map((a) => {
                    const costSource = getCostSourcePresentation(a);
                    const isExpanded = expandedSubscriptionId === a.subscriptionId;
                    return (
                      <Fragment key={a.subscriptionId}>
                      <tr className={a.hasAnomaly ? 'row--highlight' : ''}>
                      <td>
                        <button
                          type="button"
                          className="anomaly-expand-btn"
                          aria-expanded={isExpanded}
                          title={isExpanded ? 'Ocultar custo diário' : 'Ver custo diário e recursos'}
                          onClick={() => setExpandedSubscriptionId(isExpanded ? null : a.subscriptionId)}
                        >
                          {isExpanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                        </button>
                      </td>
                      {visibleColumns.subscription && (
                        <td title={a.subscriptionId}>
                          {a.subscriptionName || a.subscriptionId.slice(0, 12) + '...'}
                        </td>
                      )}
                      {visibleColumns.team && <td>{a.teamName ?? teamBySubscription.get(a.subscriptionId)?.teamName ?? '—'}</td>}
                      {visibleColumns.todayCost && (
                        <td title={`Data consolidada: ${formatDate(a.analysisDate)}`}>
                          {a.dataStatus === 'NoCostData' || a.dataStatus === 'QueryFailed'
                            ? '—'
                            : `${formatCurrency(a.todayCost)} (${formatDate(a.analysisDate)})`}
                        </td>
                      )}
                      {visibleColumns.currentCycleCost && (
                        <td title={`Acumulado até ${formatDate(a.analysisDate)}`}>
                          {hasUsableCostData(a)
                            ? `${formatCurrency(a.currentCycleCost ?? 0)} (${formatDate(a.analysisDate)})`
                            : '—'}
                        </td>
                      )}
                      {visibleColumns.currentCycleDailyAverage && <td>{hasUsableCostData(a) ? formatCurrency(a.currentCycleDailyAverage ?? 0) : '—'}</td>}
                      {visibleColumns.currentCycleTurnDate && (
                        <td title={buildCycleTooltip(a)}>
                          {a.currentCycleTurnDate ? `${new Date(`${a.currentCycleTurnDate}T00:00:00`).toLocaleDateString('pt-BR')} (${getCycleRemainingDays(a, date)}d)` : '—'}
                        </td>
                      )}
                      {visibleColumns.averageLastDays && <td>{a.dataStatus === 'Consolidated' ? formatCurrency(a.averageLastDays) : '—'}</td>}
                      {visibleColumns.increasePercent && (
                        <td className={a.dataStatus === 'Consolidated' ? (a.increasePercent > 0 ? 'text-danger' : 'text-success') : ''}>
                          {a.dataStatus === 'Consolidated' ? formatPercent(a.increasePercent) : '—'}
                        </td>
                      )}
                      {visibleColumns.dailyBudget && <td>{formatCurrency(a.dailyBudget)}</td>}
                      {visibleColumns.monthlyProjection && <td>{hasUsableCostData(a) ? formatCurrency(a.monthlyProjection) : '—'}</td>}
                      {visibleColumns.severity && <td><StatusBadge severity={a.severity} /></td>}
                      {visibleColumns.dataStatus && (
                        <td title={a.reasons.join(' | ')}>
                          <div className="anomaly-data-status">
                            <StatusBadge severity={a.dataStatus} label={getDataStatusLabel(a.dataStatus)} />
                            <span
                              className={costSource.className}
                              title={costSource.description}
                              aria-label={costSource.description}
                            >
                              {costSource.label}
                            </span>
                            {staleSubscriptionIds.has(a.subscriptionId) && (
                              <span
                                className="badge badge--warning"
                                title={`A coleta mais recente falhou para esta assinatura. Exibindo a última leitura válida, de ${formatDate(a.analysisDate ?? a.date)}.`}
                              >
                                Coleta desatualizada
                              </span>
                            )}
                          </div>
                        </td>
                      )}
                      </tr>
                      {isExpanded && (
                        <tr className="anomaly-detail-row">
                          <td colSpan={visibleColumnCount + 1}>
                            <SubscriptionCostPanel
                              subscriptionId={a.subscriptionId}
                              subscriptionName={a.subscriptionName}
                              referenceDate={a.analysisDate ?? a.date ?? date}
                              expectedDailyCost={a.dailyBudget}
                              cycleTurnDate={a.currentCycleTurnDate}
                              anomalyDays={anomalyDaysBySubscription.get(a.subscriptionId) ?? []}
                            />
                          </td>
                        </tr>
                      )}
                      </Fragment>
                    );
                  })}
                  {sorted.length === 0 && (
                    <tr>
                      <td colSpan={visibleColumnCount + 1} className="text-center">
                        Nenhuma anomalia encontrada para os filtros selecionados.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
