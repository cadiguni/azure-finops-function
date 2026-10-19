import { Fragment, useCallback, useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent } from 'react';
import { Download, ExternalLink } from 'lucide-react';
import { useAccess } from '../auth/AccessContext';
import { useFetch } from '../hooks/useFetch';
import { api } from '../services/api';
import type { RecommendationDetailResponse, RecommendationHistoryEntry, RecommendationItem } from '../types/api';

type StatusGroup = 'active' | 'actions' | 'resolved' | 'all';
type ColumnKey =
  | 'resource'
  | 'resourceGroup'
  | 'subscription'
  | 'resourceType'
  | 'team'
  | 'severity'
  | 'savings'
  | 'action'
  | 'status'
  | 'reason'
  | 'updatedBy'
  | 'updatedAt'
  | 'snoozeUntil'
  | 'details'
  | 'adminActions';

type ColumnDef = {
  key: ColumnKey;
  label: string;
  adminOnly?: boolean;
  sortable?: boolean;
};

type SortDir = 'asc' | 'desc';
type GroupBy = 'none' | 'team' | 'subscription';

const ACTIVE_STATUSES = new Set(['active', 'new', 'inreview', 'accepted', 'approvedforcleanup']);
const ACTION_STATUSES = new Set(['falsepositive', 'ignored', 'snoozed']);
const RESOLVED_STATUSES = new Set(['resolved']);

const STATUS_OPTIONS = [
  { value: 'Active', label: 'Ativo' },
  { value: 'New', label: 'New' },
  { value: 'InReview', label: 'Em análise' },
  { value: 'Accepted', label: 'Aceita' },
  { value: 'FalsePositive', label: 'Falso positivo' },
  { value: 'Ignored', label: 'Ignorada' },
  { value: 'Snoozed', label: 'Snooze 30 dias' },
  { value: 'ApprovedForCleanup', label: 'Aprovada para limpeza' },
  { value: 'Resolved', label: 'Resolvida' },
];

const DEFAULT_COLUMN_WIDTHS: Record<ColumnKey, number> = {
  resource: 300,
  resourceGroup: 180,
  subscription: 180,
  resourceType: 150,
  team: 130,
  severity: 120,
  savings: 140,
  action: 150,
  status: 130,
  reason: 220,
  updatedBy: 170,
  updatedAt: 150,
  snoozeUntil: 150,
  details: 120,
  adminActions: 180,
};

const DEFAULT_VISIBLE_COLUMNS: Record<ColumnKey, boolean> = {
  resource: true,
  resourceGroup: true,
  subscription: true,
  resourceType: true,
  team: true,
  severity: true,
  savings: true,
  action: true,
  status: true,
  reason: true,
  updatedBy: true,
  updatedAt: true,
  snoozeUntil: true,
  details: true,
  adminActions: true,
};

const COLUMN_DEFS: ColumnDef[] = [
  { key: 'resource', label: 'Recurso', sortable: true },
  { key: 'resourceGroup', label: 'Resource Group', sortable: true },
  { key: 'subscription', label: 'Subscription', sortable: true },
  { key: 'resourceType', label: 'Tipo', sortable: true },
  { key: 'team', label: 'Time', sortable: true },
  { key: 'severity', label: 'Severidade', sortable: true },
  { key: 'savings', label: 'Economia/mês', sortable: true },
  { key: 'action', label: 'Ação sugerida', sortable: true },
  { key: 'status', label: 'Status', sortable: true },
  { key: 'reason', label: 'Motivo', sortable: true },
  { key: 'updatedBy', label: 'UpdatedBy', sortable: true },
  { key: 'updatedAt', label: 'UpdatedAt', sortable: true },
  { key: 'snoozeUntil', label: 'Snooze até', sortable: true },
  { key: 'details', label: 'Detalhes' },
  { key: 'adminActions', label: 'Ações (Admin)', adminOnly: true },
];

function formatCurrency(value: number, currency = 'BRL'): string {
  return value.toLocaleString('pt-BR', { style: 'currency', currency });
}

function formatDate(value?: string | null): string {
  if (!value) {
    return '-';
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return '-';
  }

  return date.toLocaleString('pt-BR');
}

function formatCostPeriod(item: RecommendationItem): string {
  if (!item.costPeriodFrom || !item.costPeriodTo) return '-';
  return `${item.costPeriodFrom.slice(0, 10)} a ${item.costPeriodTo.slice(0, 10)}`;
}

function getCostOrigin(item: RecommendationItem): string {
  const source = item.costProvenance?.source;
  if (source === 'Unavailable') return 'Fontes temporariamente indisponíveis';
  return source || 'Desconhecida';
}

function getDaysSince(value?: string | null): number {
  if (!value) return 0;
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return 0;
  const diffMs = Date.now() - date.getTime();
  return Math.max(0, Math.floor(diffMs / (24 * 60 * 60 * 1000)));
}

function normalizeStatus(status?: string | null): string {
  return (status ?? 'New').trim();
}

type RecommendationStatusContext = {
  id: string;
  resourceId: string;
  subscriptionId: string;
  resourceGroup: string;
  teamId?: string | null;
  recommendationType: string;
};

function normalizeSeverity(item: RecommendationItem): string {
  const severity = (item.severity ?? item.priority ?? '').trim();
  return severity || 'Low';
}

function classifyAction(item: RecommendationItem): string {
  const desc = (item.description ?? '').toLowerCase();
  const type = (item.resourceType ?? '').toLowerCase();

  if (type.includes('disk') && desc.includes('unattached')) return 'Investigar';
  if (type.includes('publicipaddresses')) return 'Investigar';
  if (type.includes('operationalinsights') || type.includes('workspace')) return 'Revisar';
  if (type.includes('serverfarms') || type.includes('web/sites')) return 'Investigar';
  if (type.includes('virtualmachines')) return 'Investigar';
  if (type.includes('storageaccounts')) return 'Investigar';
  return 'Investigar';
}

function isDatabaseRecommendation(item: RecommendationItem): boolean {
  const type = (item.resourceType ?? '').toLowerCase();
  const recommendationType = (item.type ?? '').toLowerCase();
  return type.includes('microsoft.sql')
    || type.includes('microsoft.documentdb')
    || type.includes('cosmos')
    || recommendationType.includes('sql')
    || recommendationType.includes('cosmos');
}

function buildCostWarning(item: RecommendationItem): string | null {
  if (!isDatabaseRecommendation(item)) return null;

  const source = (item.costSource ?? '').toLowerCase();
  const confidence = (item.costMatchConfidence ?? '').toLowerCase();
  const hasCost = item.costAmount != null;

  if (!hasCost) {
    return 'Custo não encontrado para este recurso no período.';
  }

  if (source === 'notmatched') {
    return 'Custo não associado diretamente ao recurso no Cost Management; economia não contabilizada.';
  }

  if (confidence === 'low' || source === 'serviceandmeter') {
    return 'Custo aproximado (match por serviço/meter).';
  }

  if (confidence === 'medium' || source === 'parentresource') {
    return 'Custo associado via parent resource.';
  }

  return null;
}

function isCostReview(item: RecommendationItem): boolean {
  return item.type === 'SqlDatabaseCostReview' || item.type === 'CosmosDbCostReview';
}

function hasQuantifiedSavings(item: RecommendationItem): boolean {
  return item.savingsStatus === 'Quantified'
    || (!item.savingsStatus && item.estimatedMonthlySavings > 0);
}

function formatFinancialPeriod(from?: string, to?: string): string {
  return from && to ? `${from} a ${to}` : 'ciclos de faturamento variam por assinatura';
}

function statusToGroup(status?: string | null): StatusGroup {
  const normalized = normalizeStatus(status).toLowerCase();
  if (ACTIVE_STATUSES.has(normalized)) return 'active';
  if (ACTION_STATUSES.has(normalized)) return 'actions';
  if (RESOLVED_STATUSES.has(normalized)) return 'resolved';
  return 'all';
}

function statusBadgeClass(status?: string): string {
  const normalized = normalizeStatus(status).toLowerCase();
  if (normalized === 'falsepositive' || normalized === 'ignored') return 'badge--muted';
  if (normalized === 'snoozed' || normalized === 'inreview') return 'badge--warning';
  if (normalized === 'resolved') return 'badge--info';
  if (normalized === 'approvedforcleanup') return 'badge--critical';
  if (normalized === 'accepted') return 'badge--danger';
  return 'badge--info';
}

function severityBadgeClass(severity: string): string {
  const normalized = severity.toLowerCase();
  if (normalized === 'critical') return 'badge--critical';
  if (normalized === 'high') return 'badge--danger';
  if (normalized === 'medium') return 'badge--warning';
  return 'badge--info';
}

export default function Recommendations() {
  const { isAdmin } = useAccess();
  const [date, setDate] = useState(() => new Date().toISOString().slice(0, 10));
  const [activeTab, setActiveTab] = useState<StatusGroup>('active');
  const [onlyManualActions, setOnlyManualActions] = useState(false);
  const [filterStatus, setFilterStatus] = useState('');
  const [filterSeverity, setFilterSeverity] = useState('');
  const [filterSubInput, setFilterSubInput] = useState('');
  const [filterResourceGroup, setFilterResourceGroup] = useState('');
  const [filterResourceType, setFilterResourceType] = useState('');
  const [filterTeam, setFilterTeam] = useState('');
  const [searchText, setSearchText] = useState('');
  const [sortField, setSortField] = useState<ColumnKey>('savings');
  const [sortDir, setSortDir] = useState<SortDir>('desc');
  const [groupBy, setGroupBy] = useState<GroupBy>('none');
  const [refreshToken, setRefreshToken] = useState(0);
  const [updatingId, setUpdatingId] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [actionSuccess, setActionSuccess] = useState<string | null>(null);
  const [exporting, setExporting] = useState<'html' | 'csv' | null>(null);
  const [exportError, setExportError] = useState<string | null>(null);
  const [selectedRecommendationId, setSelectedRecommendationId] = useState<string | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [detailData, setDetailData] = useState<RecommendationDetailResponse | null>(null);
  const [detailHistory, setDetailHistory] = useState<RecommendationHistoryEntry[]>([]);
  const [detailStatus, setDetailStatus] = useState<string>('New');
  const [detailReason, setDetailReason] = useState('');
  const [columnWidths, setColumnWidths] = useState<Record<ColumnKey, number>>(() => {
    const stored = localStorage.getItem('recommendations-column-widths');
    if (!stored) return DEFAULT_COLUMN_WIDTHS;
    try {
      const parsed = JSON.parse(stored) as Partial<Record<ColumnKey, number>>;
      return { ...DEFAULT_COLUMN_WIDTHS, ...parsed };
    } catch {
      return DEFAULT_COLUMN_WIDTHS;
    }
  });
  const [resizingField, setResizingField] = useState<ColumnKey | null>(null);
  const [resizeStartX, setResizeStartX] = useState(0);
  const [resizeStartWidth, setResizeStartWidth] = useState(0);
  const [visibleColumns, setVisibleColumns] = useState<Record<ColumnKey, boolean>>(() => {
    const stored = localStorage.getItem('recommendations-visible-columns');
    if (!stored) return DEFAULT_VISIBLE_COLUMNS;
    try {
      const parsed = JSON.parse(stored) as Partial<Record<ColumnKey, boolean>>;
      return { ...DEFAULT_VISIBLE_COLUMNS, ...parsed };
    } catch {
      return DEFAULT_VISIBLE_COLUMNS;
    }
  });
  const [isColumnMenuOpen, setIsColumnMenuOpen] = useState(false);
  const columnMenuRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    localStorage.setItem('recommendations-column-widths', JSON.stringify(columnWidths));
  }, [columnWidths]);

  useEffect(() => {
    localStorage.setItem('recommendations-visible-columns', JSON.stringify(visibleColumns));
  }, [visibleColumns]);

  useEffect(() => {
    if (!resizingField) return;

    const minimumWidth = 110;
    const onMouseMove = (event: MouseEvent) => {
      const delta = event.clientX - resizeStartX;
      setColumnWidths((current) => ({
        ...current,
        [resizingField]: Math.max(minimumWidth, resizeStartWidth + delta),
      }));
    };

    const onMouseUp = () => setResizingField(null);

    window.addEventListener('mousemove', onMouseMove);
    window.addEventListener('mouseup', onMouseUp);
    return () => {
      window.removeEventListener('mousemove', onMouseMove);
      window.removeEventListener('mouseup', onMouseUp);
    };
  }, [resizingField, resizeStartX, resizeStartWidth]);

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

  const { data: summary, loading, error } = useFetch(
    () => api.getRecommendations(date),
    [date, refreshToken]
  );
  const aggregateCurrency = summary?.financialSummary?.currency
    ?? (summary?.snapshotRevision ? null : 'BRL');
  // Savings come from resource-level cost rows and can use another currency than the financial
  // summary. Mirrors RecommendationCostAccounting.SavingsCurrency: prefer the financial currency,
  // otherwise the most frequent currency among quantified recommendations.
  const savingsCurrency = useMemo(() => {
    const counts = new Map<string, number>();
    (summary?.recommendations ?? []).forEach((item) => {
      const currency = (item.costCurrency ?? '').trim().toUpperCase();
      if (currency && hasQuantifiedSavings(item)) counts.set(currency, (counts.get(currency) ?? 0) + 1);
    });
    const preferred = aggregateCurrency?.toUpperCase();
    if (preferred && counts.has(preferred)) return preferred;
    return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))[0]?.[0]
      ?? aggregateCurrency ?? null;
  }, [aggregateCurrency, summary?.recommendations]);
  const totalledSavings = useCallback((item: RecommendationItem) => savingsCurrency
    && (item.costCurrency || 'BRL').toUpperCase() === savingsCurrency
    ? item.estimatedMonthlySavings ?? 0
    : 0, [savingsCurrency]);
  const formatSavingsTotal = (value: number) => savingsCurrency
    ? formatCurrency(value, savingsCurrency)
    : '—';

  const { data: teamsData } = useFetch(
    () => api.getTeams(),
    []
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

  const teamBySubscription = useMemo(() => {
    const mapping = new Map<string, { teamId: string; teamName: string }>();
    (ownershipConfig?.teams ?? []).forEach((team) => {
      team.subscriptions.forEach((subscription) => {
        mapping.set(subscription.subscriptionId, { teamId: team.id, teamName: team.name });
      });
    });
    (teamsData?.teams ?? []).forEach((team) => {
      team.subscriptionIds.forEach((subscriptionId) => {
        if (!mapping.has(subscriptionId)) {
          mapping.set(subscriptionId, { teamId: team.id, teamName: team.name });
        }
      });
    });
    return mapping;
  }, [ownershipConfig, teamsData]);

  const subscriptionNameById = useMemo(() => {
    const mapping = new Map<string, string>();
    (ownershipConfig?.teams ?? []).forEach((team) => {
      team.subscriptions.forEach((subscription) => {
        if (subscription.subscriptionName) {
          mapping.set(subscription.subscriptionId, subscription.subscriptionName);
        }
      });
    });
    (teamsData?.teams ?? []).forEach((team) => {
      team.subscriptionIds.forEach((subscriptionId, index) => {
        const subscriptionName = team.subscriptionNames?.[index];
        if (subscriptionName && !mapping.has(subscriptionId)) {
          mapping.set(subscriptionId, subscriptionName);
        }
      });
    });
    return mapping;
  }, [ownershipConfig, teamsData]);

  const recommendations = useMemo(() => summary?.recommendations ?? [], [summary?.recommendations]);

  const enriched = useMemo(() => {
    return recommendations.map((item) => {
      const mappedTeam = teamBySubscription.get(item.subscriptionId);
      const severity = normalizeSeverity(item);
      const status = normalizeStatus(item.status);
      const teamId = item.teamId ?? mappedTeam?.teamId ?? null;
      const teamName = mappedTeam?.teamName ?? teamId ?? '-';
      const firstDetectedAt = item.firstDetectedAt ?? null;
      const occurrenceCount = item.occurrenceCount ?? 0;
      const daysSinceFirstDetected = getDaysSince(firstDetectedAt);

      return {
        ...item,
        status,
        severity,
        teamId,
        teamName,
        firstDetectedAt,
        occurrenceCount,
        isRecurring: occurrenceCount > 1,
        daysSinceFirstDetected,
        isLongRunning: daysSinceFirstDetected >= 7,
        action: classifyAction(item),
        statusGroup: statusToGroup(status),
      };
    });
  }, [recommendations, teamBySubscription]);

  const tabCounts = useMemo(() => {
    return {
      active: enriched.filter((item) => item.statusGroup === 'active').length,
      actions: enriched.filter((item) => item.statusGroup === 'actions').length,
      resolved: enriched.filter((item) => item.statusGroup === 'resolved').length,
      all: enriched.length,
    } as Record<StatusGroup, number>;
  }, [enriched]);

  const statusCounts = useMemo(() => {
    const counts = new Map<string, number>();
    enriched.forEach((item) => {
      counts.set(item.status, (counts.get(item.status) ?? 0) + 1);
    });
    return [...counts.entries()].sort((a, b) => a[0].localeCompare(b[0]));
  }, [enriched]);

  const severityCounts = useMemo(() => {
    const severityOrder: Record<string, number> = { Critical: 0, High: 1, Medium: 2, Low: 3 };
    const counts = new Map<string, number>();
    enriched.forEach((item) => {
      counts.set(item.severity, (counts.get(item.severity) ?? 0) + 1);
    });
    return [...counts.entries()].sort((a, b) => (severityOrder[a[0]] ?? 9) - (severityOrder[b[0]] ?? 9));
  }, [enriched]);

  const opportunityByTeam = useMemo(() => {
    const totals = new Map<string, { count: number; savings: number }>();
    enriched.forEach((item) => {
      const key = item.teamName || '-';
      const current = totals.get(key) ?? { count: 0, savings: 0 };
      current.count += 1;
      current.savings += totalledSavings(item);
      totals.set(key, current);
    });
    return [...totals.entries()]
      .sort((a, b) => b[1].savings - a[1].savings || b[1].count - a[1].count)
      .slice(0, 5);
  }, [enriched, totalledSavings]);

  const uniqueSeverities = useMemo(
    () => [...new Set(enriched.map((item) => item.severity))].sort((a, b) => a.localeCompare(b)),
    [enriched]
  );
  const uniqueSubscriptions = useMemo(
    () => [...new Set(enriched.map((item) => item.subscriptionId))].sort((a, b) => a.localeCompare(b)),
    [enriched]
  );
  const subscriptionOptions = useMemo(
    () => uniqueSubscriptions.map((subscriptionId) => ({
      subscriptionId,
      name: subscriptionNameById.get(subscriptionId) ?? subscriptionId,
      display: `${subscriptionNameById.get(subscriptionId) ?? subscriptionId} (${subscriptionId})`,
    })),
    [uniqueSubscriptions, subscriptionNameById]
  );
  const filterSub = useMemo(() => {
    const normalized = filterSubInput.trim();
    if (!normalized) return '';
    const match = subscriptionOptions.find((option) =>
      option.subscriptionId.localeCompare(normalized, undefined, { sensitivity: 'accent' }) === 0
      || option.display.localeCompare(normalized, undefined, { sensitivity: 'accent' }) === 0
    );
    return match?.subscriptionId ?? '';
  }, [filterSubInput, subscriptionOptions]);
  const uniqueResourceGroups = useMemo(
    () => [...new Set(enriched.map((item) => item.resourceGroup).filter(Boolean))].sort((a, b) => a.localeCompare(b)),
    [enriched]
  );
  const uniqueResourceTypes = useMemo(
    () => [...new Set(enriched.map((item) => item.resourceType).filter(Boolean))].sort((a, b) => a.localeCompare(b)),
    [enriched]
  );
  const uniqueTeams = useMemo(
    () => [...new Set(enriched.map((item) => item.teamName).filter(Boolean))].sort((a, b) => a.localeCompare(b)),
    [enriched]
  );

  const filtered = useMemo(() => {
    const term = searchText.trim().toLowerCase();
    const severityOrder: Record<string, number> = { Critical: 0, High: 1, Medium: 2, Low: 3 };
    const dir = sortDir === 'asc' ? 1 : -1;

    return enriched
      .filter((item) => {
        if (activeTab === 'all') return true;
        if (activeTab === 'actions') return item.statusGroup === 'actions';
        return item.statusGroup === activeTab;
      })
      .filter((item) => !onlyManualActions || !!item.hasManualAction)
      .filter((item) => !filterStatus || item.status === filterStatus)
      .filter((item) => !filterSeverity || item.severity === filterSeverity)
      .filter((item) => !filterSub || item.subscriptionId === filterSub)
      .filter((item) => !filterResourceGroup || item.resourceGroup === filterResourceGroup)
      .filter((item) => !filterResourceType || item.resourceType === filterResourceType)
      .filter((item) => !filterTeam || item.teamName === filterTeam)
      .filter((item) => {
        if (!term) return true;
        return (
          (item.resourceName ?? '').toLowerCase().includes(term) ||
          (item.resourceId ?? '').toLowerCase().includes(term)
        );
      })
      .sort((a, b) => {
        let comparison = 0;
        switch (sortField) {
          case 'resource':
            comparison = (a.resourceName ?? a.resourceId).localeCompare(b.resourceName ?? b.resourceId);
            break;
          case 'resourceGroup':
            comparison = (a.resourceGroup ?? '').localeCompare(b.resourceGroup ?? '');
            break;
          case 'subscription': {
            const subA = subscriptionNameById.get(a.subscriptionId) ?? a.subscriptionId;
            const subB = subscriptionNameById.get(b.subscriptionId) ?? b.subscriptionId;
            comparison = subA.localeCompare(subB);
            break;
          }
          case 'resourceType':
            comparison = (a.resourceType ?? a.type).localeCompare(b.resourceType ?? b.type);
            break;
          case 'team':
            comparison = (a.teamName ?? '').localeCompare(b.teamName ?? '');
            break;
          case 'severity':
            comparison = (severityOrder[a.severity] ?? 9) - (severityOrder[b.severity] ?? 9);
            break;
          case 'savings':
            comparison = (a.estimatedMonthlySavings ?? 0) - (b.estimatedMonthlySavings ?? 0);
            break;
          case 'action':
            comparison = (a.action ?? '').localeCompare(b.action ?? '');
            break;
          case 'status':
            comparison = (a.status ?? 'New').localeCompare(b.status ?? 'New');
            break;
          case 'reason':
            comparison = (a.statusReason ?? '').localeCompare(b.statusReason ?? '');
            break;
          case 'updatedBy':
            comparison = (a.statusUpdatedBy ?? '').localeCompare(b.statusUpdatedBy ?? '');
            break;
          case 'updatedAt':
            comparison = (a.statusUpdatedAt ?? '').localeCompare(b.statusUpdatedAt ?? '');
            break;
          case 'snoozeUntil':
            comparison = (a.snoozeUntil ?? '').localeCompare(b.snoozeUntil ?? '');
            break;
          default:
            comparison = (severityOrder[a.severity] ?? 9) - (severityOrder[b.severity] ?? 9);
            break;
        }

        if (comparison === 0) {
          comparison = (a.estimatedMonthlySavings ?? 0) - (b.estimatedMonthlySavings ?? 0);
        }

        return comparison * dir;
      });
  }, [
    enriched,
    activeTab,
    filterStatus,
    onlyManualActions,
    filterSeverity,
    filterSub,
    filterResourceGroup,
    filterResourceType,
    filterTeam,
    searchText,
    sortField,
    sortDir,
    subscriptionNameById,
  ]);

  const totalFilteredSavings = filtered.reduce((sum, item) => sum + totalledSavings(item), 0);
  const observedCurrency = aggregateCurrency ?? savingsCurrency;
  const totalFilteredObservedCost = useMemo(() => {
    const values = new Map<string, number>();
    filtered.forEach((item) => {
      if (item.observedCost == null
        || !['resourceid', 'resourcenameandgroup', 'parentresource'].includes((item.costSource ?? '').toLowerCase())
        || (observedCurrency && item.costCurrency
          && item.costCurrency.toUpperCase() !== observedCurrency.toUpperCase())) return;
      const key = (item.costMatchedResourceId || item.resourceId).trim().replace(/\/$/, '').toLowerCase();
      values.set(key, Math.max(values.get(key) ?? 0, item.observedCost));
    });
    return values.size > 0 ? [...values.values()].reduce((sum, value) => sum + value, 0) : null;
  }, [observedCurrency, filtered]);

  const groupedRecommendations = useMemo(() => {
    if (groupBy === 'none') {
      return [{ key: 'all', label: 'Todas', items: filtered, savings: totalFilteredSavings }];
    }

    const groups = new Map<string, { label: string; items: typeof filtered; savings: number }>();
    filtered.forEach((item) => {
      const label = groupBy === 'team'
        ? (item.teamName || '-')
        : (subscriptionNameById.get(item.subscriptionId) ?? item.subscriptionId);
      const current = groups.get(label) ?? { label, items: [], savings: 0 };
      current.items.push(item);
      current.savings += totalledSavings(item);
      groups.set(label, current);
    });

    return [...groups.entries()]
      .map(([key, value]) => ({ key, ...value }))
      .sort((a, b) => b.savings - a.savings || b.items.length - a.items.length);
  }, [filtered, groupBy, subscriptionNameById, totalFilteredSavings, totalledSavings]);

  // Exports read the published snapshot, which exists per subscription, per team or global.
  // The remaining table filters (type, severity, search…) are not applied to the file.
  const exportTeamId = filterTeam
    ? enriched.find((item) => item.teamName === filterTeam)?.teamId ?? undefined
    : undefined;
  const exportArgs = [date, filterSub || undefined, filterSub ? undefined : exportTeamId] as const;
  const exportScopeLabel = filterSub
    ? `subscription ${subscriptionNameById.get(filterSub) ?? filterSub}`
    : filterTeam ? `time ${filterTeam}` : 'todas as subscriptions';

  const handleOpenHtml = async () => {
    setExportError(null);
    setExporting('html');
    // Open synchronously so the popup is tied to the click, then fill it once the report arrives.
    const reportWindow = window.open('', '_blank');
    try {
      const html = await api.getReportHtml(...exportArgs);
      const url = URL.createObjectURL(new Blob([html], { type: 'text/html' }));
      if (reportWindow) reportWindow.location.href = url;
      else window.open(url, '_blank', 'noopener,noreferrer');
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (error) {
      reportWindow?.close();
      setExportError(error instanceof Error ? error.message : 'Erro ao gerar relatório HTML');
    } finally {
      setExporting(null);
    }
  };

  const handleDownloadCsv = async () => {
    setExportError(null);
    setExporting('csv');
    try {
      const blob = await api.getReportCsv(...exportArgs);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `finops-recommendations-${date}.csv`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    } catch (error) {
      setExportError(error instanceof Error ? error.message : 'Erro ao baixar CSV');
    } finally {
      setExporting(null);
    }
  };

  const updateStatus = async (
    context: RecommendationStatusContext,
    status: string,
    reason?: string,
    options?: { clearSnooze?: boolean }
  ) => {
    if (!isAdmin || !context.id) return;

    setActionError(null);
    setActionSuccess(null);
    setUpdatingId(context.id);

    const snoozeUntil =
      status === 'Snoozed' ? new Date(Date.now() + 30 * 24 * 60 * 60 * 1000).toISOString() : undefined;

    try {
      await api.updateRecommendationStatus(context.id, {
        status,
        reason,
        snoozeUntil,
        clearSnooze: options?.clearSnooze,
        resourceId: context.resourceId,
        subscriptionId: context.subscriptionId,
        resourceGroup: context.resourceGroup,
        teamId: context.teamId,
        recommendationType: context.recommendationType,
      });
      setActionSuccess(`Status atualizado para ${status}.`);
      setRefreshToken((current) => current + 1);
    } catch (err) {
      setActionError(err instanceof Error ? err.message : 'Falha ao atualizar status.');
    } finally {
      setUpdatingId(null);
    }
  };

  const loadRecommendationDetail = async (recommendationId: string) => {
    setDetailLoading(true);
    setDetailError(null);
    try {
      const [detail, history] = await Promise.all([
        api.getRecommendationDetail(recommendationId, summary?.date ?? date, 60, summary?.snapshotRevision),
        api.getRecommendationHistory(recommendationId, 100),
      ]);
      setDetailData(detail);
      setDetailHistory(history.items ?? detail.auditLogs ?? []);
      setDetailStatus(detail.recommendation?.status ?? 'New');
      setDetailReason(detail.recommendation?.statusReason ?? '');
    } catch (err) {
      setDetailError(err instanceof Error ? err.message : 'Falha ao carregar detalhe da recomendação.');
      setDetailData(null);
      setDetailHistory([]);
    } finally {
      setDetailLoading(false);
    }
  };

  const openRecommendationDetail = (recommendationId: string) => {
    setSelectedRecommendationId(recommendationId);
    void loadRecommendationDetail(recommendationId);
  };

  const closeRecommendationDetail = () => {
    setSelectedRecommendationId(null);
    setDetailData(null);
    setDetailHistory([]);
    setDetailError(null);
  };

  const updateStatusFromDetail = async () => {
    if (!isAdmin || !detailData?.recommendation) return;
    const recommendation = detailData.recommendation;
    await updateStatus(
      {
        id: recommendation.id,
        resourceId: recommendation.resourceId,
        subscriptionId: recommendation.subscriptionId,
        resourceGroup: recommendation.resourceGroup,
        teamId: recommendation.teamId,
        recommendationType: recommendation.type,
      },
      detailStatus,
      detailReason.trim() || undefined
    );
    await loadRecommendationDetail(recommendation.id);
  };

  const startResize = (field: ColumnKey, event: ReactMouseEvent<HTMLSpanElement>) => {
    event.preventDefault();
    event.stopPropagation();
    setResizingField(field);
    setResizeStartX(event.clientX);
    setResizeStartWidth(columnWidths[field]);
  };

  const handleSort = (field: ColumnKey) => {
    if (sortField === field) {
      setSortDir((current) => (current === 'asc' ? 'desc' : 'asc'));
      return;
    }

    setSortField(field);
    setSortDir(field === 'savings' ? 'desc' : 'asc');
  };

  const sortIcon = (field: ColumnKey) => {
    if (sortField !== field) return ' ↕';
    return sortDir === 'asc' ? ' ↑' : ' ↓';
  };

  const visibleColumnDefs = COLUMN_DEFS
    .filter((column) => !column.adminOnly || isAdmin)
    .filter((column) => visibleColumns[column.key]);

  const renderCell = (item: (typeof enriched)[number], key: ColumnKey) => {
    switch (key) {
      case 'resource':
        return (
          <td key={key} className="recommendations-cell--resource" title={item.resourceId}>
            <div className="recommendations-resource-name">{item.resourceName || item.resourceId}</div>
            <div className="recommendations-resource-id">{item.resourceId}</div>
          </td>
        );
      case 'resourceGroup':
        return (
          <td key={key} className="recommendations-cell--truncate" title={item.resourceGroup}>
            {item.resourceGroup || '-'}
          </td>
        );
      case 'subscription': {
        const subscriptionName = subscriptionNameById.get(item.subscriptionId);
        return (
          <td key={key} className="recommendations-cell--truncate" title={`${subscriptionName ?? '-'} (${item.subscriptionId})`}>
            {subscriptionName ?? item.subscriptionId}
          </td>
        );
      }
      case 'resourceType':
        return (
          <td key={key} className="recommendations-cell--truncate" title={item.resourceType}>
            {item.resourceType || item.type}
          </td>
        );
      case 'team':
        return <td key={key} className="recommendations-cell--truncate">{item.teamName}</td>;
      case 'severity':
        return (
          <td key={key}>
            <span className={`badge ${severityBadgeClass(item.severity)} badge--table`}>{item.severity}</span>
          </td>
        );
      case 'savings':
        return (
          <td key={key} className="recommendations-cell--money">
            {hasQuantifiedSavings(item) ? (
              <div className={isCostReview(item) ? 'text-muted' : 'text-success'}>
                {formatCurrency(item.estimatedMonthlySavings, item.costCurrency || aggregateCurrency || 'BRL')}
              </div>
            ) : (
              // Sem economia calculada, o número que ajuda a priorizar é o valor em jogo: o custo
              // projetado do recurso. É custo exposto, não economia — e não entra em nenhum total.
              <div className={item.estimatedMonthlyCost > 0 ? '' : 'text-muted'}>
                {item.estimatedMonthlyCost > 0
                  ? `Custo exposto: ${formatCurrency(item.estimatedMonthlyCost, item.costCurrency || 'BRL')}/mês`
                  : 'Custo indisponível'}
                <span className="cost-breakdown-meta">economia ainda não estimada</span>
              </div>
            )}
            <div className="text-muted" style={{ marginTop: 4, fontSize: '0.75rem' }}>
              Observado: {item.observedCost != null ? formatCurrency(item.observedCost, item.costCurrency || 'BRL') : '-'}
              {hasQuantifiedSavings(item) && item.estimatedMonthlyCost > 0
                ? ` | Projeção mensal: ${formatCurrency(item.estimatedMonthlyCost, item.costCurrency || 'BRL')}`
                : ''}
            </div>
            <div className="text-muted" style={{ marginTop: 4, fontSize: '0.75rem' }}>
              Origem: {getCostOrigin(item)}
            </div>
            {buildCostWarning(item) && (
              <div className="text-danger" style={{ marginTop: 4, fontSize: '0.75rem' }}>
                {buildCostWarning(item)}
              </div>
            )}
          </td>
        );
      case 'action':
        return (
          <td key={key}>
            <span className={`badge ${classifyAction(item) === 'Excluir' ? 'badge--danger' : 'badge--warning'} badge--table`}>
              {item.action}
            </span>
          </td>
        );
      case 'status':
        return (
          <td key={key}>
            <span className={`badge ${statusBadgeClass(item.status)} badge--table`}>{item.status}</span>
            {item.statusSource && (
              <div className="text-muted" style={{ marginTop: 4, fontSize: '0.75rem' }}>
                {item.statusSource}
              </div>
            )}
          </td>
        );
      case 'reason':
        return (
          <td key={key} className="recommendations-cell--truncate" title={item.statusReason ?? ''}>
            {item.statusReason ?? '-'}
          </td>
        );
      case 'updatedBy':
        return <td key={key} className="recommendations-cell--truncate">{item.statusUpdatedBy ?? '-'}</td>;
      case 'updatedAt':
        return <td key={key} className="recommendations-cell--truncate">{formatDate(item.statusUpdatedAt)}</td>;
      case 'snoozeUntil':
        return <td key={key} className="recommendations-cell--truncate">{formatDate(item.snoozeUntil)}</td>;
      case 'details':
        return (
          <td key={key}>
            <button type="button" className="btn btn-secondary" onClick={() => openRecommendationDetail(item.id)}>
              Ver detalhes
            </button>
          </td>
        );
      case 'adminActions':
        return (
          <td key={key} className="recommendations-cell--truncate">
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              <select
                className="recommendation-status-select"
                value={item.status}
                disabled={updatingId === item.id}
                onChange={(event) => updateStatus(
                  {
                    id: item.id,
                    resourceId: item.resourceId,
                    subscriptionId: item.subscriptionId,
                    resourceGroup: item.resourceGroup,
                    teamId: item.teamId,
                    recommendationType: item.type,
                  },
                  event.target.value
                )}
              >
                {STATUS_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </select>
              <button
                type="button"
                className="btn btn--secondary"
                disabled={updatingId === item.id || item.status === 'Active' || item.status === 'New'}
                onClick={() => updateStatus(
                  {
                    id: item.id,
                    resourceId: item.resourceId,
                    subscriptionId: item.subscriptionId,
                    resourceGroup: item.resourceGroup,
                    teamId: item.teamId,
                    recommendationType: item.type,
                  },
                  'Active',
                  'Revertido manualmente pelo administrador.',
                  { clearSnooze: true }
                )}
              >
                Reverter para Ativo
              </button>
            </div>
          </td>
        );
      default:
        return null;
    }
  };

  return (
    <div className="page">
      <div className="page-header">
        <h2>Recomendações Operacionais</h2>
        <span className="page-date">
          {filtered.length} de {enriched.length} recomendações | Economia quantificada: {formatSavingsTotal(totalFilteredSavings)}/mês
        </span>
      </div>

      <div className="report-actions">
        <button
          type="button"
          className="btn btn--secondary"
          onClick={() => void handleOpenHtml()}
          disabled={exporting !== null || !summary?.snapshotRevision}
          title={`Relatório do snapshot para ${exportScopeLabel}`}
        >
          <ExternalLink size={16} />
          {exporting === 'html' ? 'Gerando…' : 'Abrir relatório HTML'}
        </button>
        <button
          type="button"
          className="btn btn--secondary"
          onClick={() => void handleDownloadCsv()}
          disabled={exporting !== null || !summary?.snapshotRevision}
          title={`CSV do snapshot para ${exportScopeLabel}`}
        >
          <Download size={16} />
          {exporting === 'csv' ? 'Baixando…' : 'Exportar CSV'}
        </button>
        <span className="text-muted" style={{ alignSelf: 'center', fontSize: '0.8rem' }}>
          Exporta {exportScopeLabel} na data selecionada (filtros de subscription ou time).
        </span>
      </div>
      {exportError && (
        <div className="alert alert--warning" role="alert">
          Não foi possível exportar o relatório.<br />
          <small>{exportError}</small>
        </div>
      )}

      {summary && (
        <div className="alert alert--warning">
          Dados de {summary.date}; período financeiro: {formatFinancialPeriod(summary.periodFrom, summary.periodTo)}.
          {' '}Snapshot: <span title={summary.snapshotRevision ?? undefined}>
            {summary.snapshotRevision?.slice(0, 12) ?? 'não versionado'}
          </span>.
          {summary.requestedDate && summary.requestedDate !== summary.date
            ? ` Data solicitada: ${summary.requestedDate}; foi usado o último snapshot válido.`
            : ''}
          <br />
          Fonte financeira: {summary.financialSummary?.sources.join(', ') || '—'};
          {' '}situação: {summary.financialSummary?.availability ?? 'indisponível'};
          {' '}dados até {summary.financialSummary?.latestDataAsOf ?? '—'}.
        </div>
      )}

      <div className="recommendations-tabs">
        <button
          type="button"
          className={`recommendations-tab ${activeTab === 'active' ? 'is-active' : ''}`}
          onClick={() => setActiveTab('active')}
        >
          Ativas ({tabCounts.active})
        </button>
        <button
          type="button"
          className={`recommendations-tab ${activeTab === 'actions' ? 'is-active' : ''}`}
          onClick={() => setActiveTab('actions')}
        >
          Ações aplicadas ({tabCounts.actions})
        </button>
        <button
          type="button"
          className={`recommendations-tab ${activeTab === 'resolved' ? 'is-active' : ''}`}
          onClick={() => setActiveTab('resolved')}
        >
          Resolvidas ({tabCounts.resolved})
        </button>
        <button
          type="button"
          className={`recommendations-tab ${activeTab === 'all' ? 'is-active' : ''}`}
          onClick={() => setActiveTab('all')}
        >
          Todas ({tabCounts.all})
        </button>
      </div>

      <div className="recommendations-summary-grid">
        <div className="recommendations-summary-card">
          <span className="recommendations-summary-label">Economia quantificada</span>
          <strong>{formatSavingsTotal(totalFilteredSavings)}/mês</strong>
        </div>
        <div className="recommendations-summary-card">
          <span className="recommendations-summary-label">Custo observado associado</span>
          <strong>{totalFilteredObservedCost == null || !observedCurrency ? '—' : formatCurrency(totalFilteredObservedCost, observedCurrency)}</strong>
        </div>
        <div className="recommendations-summary-card">
          <span className="recommendations-summary-label">Status</span>
          <div className="recommendations-summary-badges">
            {statusCounts.map(([status, count]) => (
              <button
                key={status}
                type="button"
                className={`badge ${statusBadgeClass(status)} badge--button`}
                onClick={() => setFilterStatus((current) => current === status ? '' : status)}
              >
                {status}: {count}
              </button>
            ))}
          </div>
        </div>
        <div className="recommendations-summary-card">
          <span className="recommendations-summary-label">Severidade</span>
          <div className="recommendations-summary-badges">
            {severityCounts.map(([severity, count]) => (
              <button
                key={severity}
                type="button"
                className={`badge ${severityBadgeClass(severity)} badge--button`}
                onClick={() => setFilterSeverity((current) => current === severity ? '' : severity)}
              >
                {severity}: {count}
              </button>
            ))}
          </div>
        </div>
        <div className="recommendations-summary-card">
          <span className="recommendations-summary-label">Top times</span>
          <div className="recommendations-top-list">
            {opportunityByTeam.map(([team, totals]) => (
              <button key={team} type="button" onClick={() => setFilterTeam(team === '-' ? '' : team)}>
                <span>{team}</span>
                <strong>{formatSavingsTotal(totals.savings)}</strong>
              </button>
            ))}
          </div>
        </div>
      </div>

      <div className="filters filters--compact">
        <div className="filter-group">
          <label htmlFor="rec-date">Data</label>
          <input id="rec-date" type="date" value={date} onChange={(event) => setDate(event.target.value)} />
        </div>
        <div className="filter-group">
          <label htmlFor="rec-status">Status</label>
          <select id="rec-status" value={filterStatus} onChange={(event) => setFilterStatus(event.target.value)}>
            <option value="">Todos</option>
            <option value="Active">Active</option>
            <option value="New">New</option>
            <option value="InReview">InReview</option>
            <option value="Accepted">Accepted</option>
            <option value="FalsePositive">FalsePositive</option>
            <option value="Ignored">Ignored</option>
            <option value="Snoozed">Snoozed</option>
            <option value="ApprovedForCleanup">ApprovedForCleanup</option>
            <option value="Resolved">Resolved</option>
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="rec-manual-only">Ações manuais</label>
          <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <input
              id="rec-manual-only"
              type="checkbox"
              checked={onlyManualActions}
              onChange={(event) => setOnlyManualActions(event.target.checked)}
            />
            Somente ações manuais
          </label>
        </div>
        <div className="filter-group">
          <label htmlFor="rec-severity">Severidade</label>
          <select id="rec-severity" value={filterSeverity} onChange={(event) => setFilterSeverity(event.target.value)}>
            <option value="">Todas</option>
            {uniqueSeverities.map((severity) => (
              <option key={severity} value={severity}>
                {severity}
              </option>
            ))}
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="rec-subscription">Subscription</label>
          <input
            id="rec-subscription"
            list="rec-subscription-options"
            value={filterSubInput}
            onChange={(event) => setFilterSubInput(event.target.value)}
            placeholder="Digite nome ou subscriptionId"
          />
          <datalist id="rec-subscription-options">
            {subscriptionOptions.map((option) => (
              <option key={`rec-sub-${option.subscriptionId}`} value={option.display} />
            ))}
            {subscriptionOptions.map((option) => (
              <option key={`rec-sub-id-${option.subscriptionId}`} value={option.subscriptionId} />
            ))}
          </datalist>
        </div>
        <div className="filter-group">
          <label htmlFor="rec-resource-group">Resource Group</label>
          <select
            id="rec-resource-group"
            value={filterResourceGroup}
            onChange={(event) => setFilterResourceGroup(event.target.value)}
          >
            <option value="">Todos</option>
            {uniqueResourceGroups.map((resourceGroup) => (
              <option key={resourceGroup} value={resourceGroup}>
                {resourceGroup}
              </option>
            ))}
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="rec-resource-type">Tipo do recurso</label>
          <select
            id="rec-resource-type"
            value={filterResourceType}
            onChange={(event) => setFilterResourceType(event.target.value)}
          >
            <option value="">Todos</option>
            {uniqueResourceTypes.map((resourceType) => (
              <option key={resourceType} value={resourceType}>
                {resourceType}
              </option>
            ))}
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="rec-team">Time</label>
          <select id="rec-team" value={filterTeam} onChange={(event) => setFilterTeam(event.target.value)}>
            <option value="">Todos</option>
            {uniqueTeams.map((team) => (
              <option key={team} value={team}>
                {team}
              </option>
            ))}
          </select>
        </div>
        <div className="filter-group">
          <label htmlFor="rec-search">Busca (nome/id)</label>
          <input
            id="rec-search"
            type="search"
            value={searchText}
            onChange={(event) => setSearchText(event.target.value)}
            placeholder="Ex.: vm-prod ou /subscriptions/..."
          />
        </div>
        <div className="filter-group">
          <label htmlFor="rec-group-by">Agrupar por</label>
          <select id="rec-group-by" value={groupBy} onChange={(event) => setGroupBy(event.target.value as GroupBy)}>
            <option value="none">Sem agrupamento</option>
            <option value="team">Time</option>
            <option value="subscription">Subscription</option>
          </select>
        </div>
        <div className="filter-group">
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
                {COLUMN_DEFS.filter((column) => !column.adminOnly || isAdmin).map((column) => (
                  <label key={`toggle-${column.key}`} className="column-picker-item">
                    <input
                      type="checkbox"
                      checked={visibleColumns[column.key]}
                      onChange={(event) => {
                        const next = { ...visibleColumns, [column.key]: event.target.checked };
                        const enabled = COLUMN_DEFS
                          .filter((col) => !col.adminOnly || isAdmin)
                          .filter((col) => next[col.key]).length;
                        if (enabled > 0) {
                          setVisibleColumns(next);
                        }
                      }}
                    />
                    <span>{column.label}</span>
                  </label>
                ))}
              </div>
            )}
          </div>
        </div>
        <div className="filter-group recommendations-column-actions">
          <label>&nbsp;</label>
          <button type="button" className="filter-control filter-control--button" onClick={() => setColumnWidths(DEFAULT_COLUMN_WIDTHS)}>
            Resetar colunas
          </button>
        </div>
      </div>

      {loading && <div className="loading">Carregando recomendações...</div>}
      {error && (
        <div className="alert alert--warning">
          Não foi possível carregar recomendações.
          <br />
          <small>{error}</small>
        </div>
      )}
      {actionError && (
        <div className="alert alert--warning">
          Falha ao atualizar status.
          <br />
          <small>{actionError}</small>
        </div>
      )}
      {actionSuccess && <div className="alert alert--success">{actionSuccess}</div>}
      {!isAdmin && (
        <div className="alert alert--warning">
          Perfil Reader: visualização habilitada. Alterações de status requerem FinOps.Admin.
        </div>
      )}

      {!loading && !error && (
        <div className="section">
          <div className="table-container">
            <table className="recommendations-table recommendations-table--manual">
              <colgroup>
                {visibleColumnDefs.map((column) => (
                  <col key={`col-${column.key}`} style={{ width: `${columnWidths[column.key]}px` }} />
                ))}
              </colgroup>
              <thead>
                <tr>
                  {visibleColumnDefs.map((column) => (
                    <th
                      key={`header-${column.key}`}
                      className={column.sortable ? 'sortable' : undefined}
                      onClick={column.sortable ? () => handleSort(column.key) : undefined}
                    >
                      {column.label}{column.sortable ? sortIcon(column.key) : ''}
                      <span className="column-resize-handle" onMouseDown={(event) => startResize(column.key, event)} />
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {groupedRecommendations.map((group) => (
                  <Fragment key={`group-fragment-${group.key}`}>
                    {groupBy !== 'none' && (
                      <tr key={`group-${group.key}`} className="recommendations-group-row">
                        <td colSpan={visibleColumnDefs.length}>
                          <strong>{group.label}</strong>
                          <span>{group.items.length} recomendações | {formatSavingsTotal(group.savings)}/mês</span>
                        </td>
                      </tr>
                    )}
                    {group.items.map((item) => (
                      <tr
                        key={`${group.key}-${item.id}-${item.resourceId}`}
                        className={[
                          item.severity === 'Critical' || item.severity === 'High' ? 'row--highlight' : '',
                          item.isLongRunning ? 'row--long-running' : '',
                        ].filter(Boolean).join(' ')}
                      >
                        {visibleColumnDefs.map((column) => renderCell(item, column.key))}
                      </tr>
                    ))}
                  </Fragment>
                ))}
                {filtered.length === 0 && (
                  <tr>
                    <td colSpan={visibleColumnDefs.length} className="text-center">
                      Nenhuma recomendação encontrada para este grupo/filtro.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {selectedRecommendationId && (
        <div className="modal-overlay" onClick={closeRecommendationDetail}>
          <div className="modal recommendation-detail-modal" onClick={(event) => event.stopPropagation()}>
            <h3>Detalhes da recomendação</h3>
            {detailLoading && <div className="loading">Carregando detalhes...</div>}
            {detailError && (
              <div className="alert alert--warning">
                Não foi possível carregar o detalhe.
                <br />
                <small>{detailError}</small>
              </div>
            )}
            {!detailLoading && detailData?.recommendation && (
              <div className="recommendation-detail-body">
                <div className="recommendation-detail-grid">
                  <div><strong>Recurso</strong><span>{detailData.recommendation.resourceName || '-'}</span></div>
                  <div><strong>Tipo</strong><span>{detailData.recommendation.resourceType || detailData.recommendation.type}</span></div>
                  <div><strong>Subscription</strong><span>{detailData.recommendation.subscriptionId}</span></div>
                  <div><strong>Resource Group</strong><span>{detailData.recommendation.resourceGroup || '-'}</span></div>
                  <div><strong>Time</strong><span>{detailData.recommendation.teamId || '-'}</span></div>
                  <div><strong>Severidade</strong><span>{detailData.recommendation.severity || detailData.recommendation.priority}</span></div>
                  <div><strong>Economia estimada</strong><span>{!hasQuantifiedSavings(detailData.recommendation) ? 'Não estimada — ver custo exposto' : formatCurrency(detailData.recommendation.estimatedMonthlySavings, detailData.recommendation.costCurrency || aggregateCurrency || 'BRL')}</span></div>
                  {!hasQuantifiedSavings(detailData.recommendation) && (
                    <div><strong>Custo exposto</strong><span>{detailData.recommendation.estimatedMonthlyCost > 0 ? `${formatCurrency(detailData.recommendation.estimatedMonthlyCost, detailData.recommendation.costCurrency || 'BRL')}/mês` : '-'}</span></div>
                  )}
                  <div><strong>Custo observado</strong><span>{detailData.recommendation.observedCost != null ? formatCurrency(detailData.recommendation.observedCost, detailData.recommendation.costCurrency || 'BRL') : '-'}</span></div>
                  <div><strong>Projeção mensal</strong><span>{detailData.recommendation.estimatedMonthlyCost > 0 ? formatCurrency(detailData.recommendation.estimatedMonthlyCost, detailData.recommendation.costCurrency || 'BRL') : '-'}</span></div>
                  <div><strong>Período observado</strong><span>{formatCostPeriod(detailData.recommendation)}</span></div>
                  <div><strong>Origem dos dados</strong><span>{getCostOrigin(detailData.recommendation)}</span></div>
                  <div><strong>Base de custo</strong><span>{detailData.recommendation.costProvenance?.costBasis || '-'}</span></div>
                  <div><strong>Dados consolidados em</strong><span>{formatDate(detailData.recommendation.costProvenance?.dataAsOf)}</span></div>
                  <div><strong>Revisão do dataset</strong><span>{detailData.recommendation.costProvenance?.datasetRevision || '-'}</span></div>
                  <div><strong>Estratégia de associação</strong><span>{detailData.recommendation.costSource ?? 'NotMatched'}</span></div>
                  <div><strong>Confiança do match</strong><span>{detailData.recommendation.costMatchConfidence ?? 'Low'}</span></div>
                  <div><strong>ResourceId de custo</strong><span>{detailData.recommendation.costMatchedResourceId || '-'}</span></div>
                  <div><strong>Status atual</strong><span className={`badge ${statusBadgeClass(detailData.recommendation.status)} badge--table`}>{detailData.recommendation.status}</span></div>
                  <div><strong>Motivo</strong><span>{detailData.recommendation.statusReason || '-'}</span></div>
                  <div><strong>Primeira detecção</strong><span>{formatDate(detailData.recommendation.firstDetectedAt)}</span></div>
                  <div><strong>Última detecção</strong><span>{formatDate(detailData.recommendation.lastDetectedAt)}</span></div>
                  <div><strong>Ocorrências</strong><span>{detailData.recommendation.occurrenceCount ?? 0}</span></div>
                  <div><strong>Última alteração por</strong><span>{detailData.recommendation.lastStatusUpdatedBy || '-'}</span></div>
                  <div><strong>Data da alteração</strong><span>{formatDate(detailData.recommendation.lastStatusUpdatedAt)}</span></div>
                </div>

                {(detailData.recommendation.occurrenceCount ?? 0) > 1 && (
                  <div className="alert alert--success">Recomendação recorrente nos snapshots recentes.</div>
                )}
                {getDaysSince(detailData.recommendation.firstDetectedAt) >= 7 && (
                  <div className="alert alert--warning">Recomendação presente há muitos dias ({getDaysSince(detailData.recommendation.firstDetectedAt)} dias).</div>
                )}
                {detailData.hasSufficientHistory === false && (
                  <div className="alert alert--warning">Histórico insuficiente para análise completa de recorrência.</div>
                )}
                {buildCostWarning(detailData.recommendation) && (
                  <div className="alert alert--warning">{buildCostWarning(detailData.recommendation)}</div>
                )}
                {!!detailData.recommendation.costNote && (
                  <div className="alert alert--warning">{detailData.recommendation.costNote}</div>
                )}

                {isAdmin && (
                  <div className="recommendation-detail-actions">
                    <select
                      className="recommendation-status-select"
                      value={detailStatus}
                      disabled={updatingId === detailData.recommendation.id}
                      onChange={(event) => setDetailStatus(event.target.value)}
                    >
                      {STATUS_OPTIONS.map((option) => (
                        <option key={option.value} value={option.value}>
                          {option.label}
                        </option>
                      ))}
                    </select>
                    <input
                      className="filter-control"
                      value={detailReason}
                      onChange={(event) => setDetailReason(event.target.value)}
                      placeholder="Motivo da alteração (opcional)"
                    />
                    <button
                      type="button"
                      className="btn btn-primary"
                      disabled={updatingId === detailData.recommendation.id}
                      onClick={() => void updateStatusFromDetail()}
                    >
                      Salvar status
                    </button>
                    <button
                      type="button"
                      className="btn btn--secondary"
                      disabled={
                        updatingId === detailData.recommendation.id
                        || detailData.recommendation.status === 'Active'
                        || detailData.recommendation.status === 'New'
                      }
                      onClick={() => void updateStatus(
                        {
                          id: detailData.recommendation.id,
                          resourceId: detailData.recommendation.resourceId,
                          subscriptionId: detailData.recommendation.subscriptionId,
                          resourceGroup: detailData.recommendation.resourceGroup,
                          teamId: detailData.recommendation.teamId,
                          recommendationType: detailData.recommendation.type,
                        },
                        'Active',
                        'Revertido manualmente pelo administrador.',
                        { clearSnooze: true }
                      )}
                    >
                      Reverter para Ativo
                    </button>
                  </div>
                )}

                <h4>Ocorrências recentes</h4>
                <div className="recommendation-timeline">
                  {(detailData.occurrencesRecent ?? []).length === 0 && <div className="text-muted">Sem ocorrências recentes.</div>}
                  {(detailData.occurrencesRecent ?? []).map((occurrence, index) => (
                    <div key={`${occurrence.detectedAt}-${index}`} className="recommendation-timeline-item">
                      <div className="recommendation-timeline-header">
                        <strong>{occurrence.severity || '-'}</strong>
                        <span>{formatDate(occurrence.detectedAt)}</span>
                      </div>
                      <div className="text-muted">{occurrence.description || '-'}</div>
                    </div>
                  ))}
                </div>

                <h4>Histórico de alterações</h4>
                <div className="recommendation-timeline">
                  {detailHistory.length === 0 && <div className="text-muted">Sem histórico de alterações.</div>}
                  {detailHistory.map((entry) => (
                    <div key={entry.id} className="recommendation-timeline-item">
                      <div className="recommendation-timeline-header">
                        <strong>{entry.action}</strong>
                        <span>{formatDate(entry.performedAt)}</span>
                      </div>
                      <div className="text-muted">{entry.performedBy || '-'}</div>
                    </div>
                  ))}
                </div>
              </div>
            )}

            <div className="modal-actions">
              <button type="button" className="btn btn-secondary" onClick={closeRecommendationDetail}>
                Fechar
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
