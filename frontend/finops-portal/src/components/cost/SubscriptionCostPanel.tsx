import { useMemo, useState } from 'react';
import { api } from '../../services/api';
import { useFetch } from '../../hooks/useFetch';
import CostTrendChart, { type CostTrendPoint } from './CostTrendChart';
import type { CostByResourceRow, CostByServiceRow } from '../../types/api';

export interface AnomalyDayMarker {
  date: string;
  hasAnomaly: boolean;
}

interface SubscriptionCostPanelProps {
  subscriptionId: string;
  subscriptionName: string;
  referenceDate: string;
  expectedDailyCost?: number | null;
  cycleTurnDate?: string | null;
  anomalyDays: AnomalyDayMarker[];
}

interface ServiceDelta {
  label: string;
  totalCost: number;
  previousCost: number | null;
  delta: number | null;
}

const WINDOW_OPTIONS = [14, 30, 60, 90];
const MAX_PROJECTED_DAYS = 45;
const TOP_SERVICES = 8;
const TOP_RESOURCES = 10;

// Dates are handled as local calendar days (yyyy-MM-dd); UTC conversion would shift the day.
function parseDateKey(key: string): Date {
  const [year, month, day] = key.split('-').map(Number);
  return new Date(year, month - 1, day);
}

function toDateKey(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(
    date.getDate()
  ).padStart(2, '0')}`;
}

function shiftDays(key: string, amount: number): string {
  const date = parseDateKey(key);
  date.setDate(date.getDate() + amount);
  return toDateKey(date);
}

function daysBetween(fromKey: string, toKey: string): number {
  const from = parseDateKey(fromKey);
  const to = parseDateKey(toKey);
  return Math.round((to.getTime() - from.getTime()) / 86_400_000);
}

function datesBetween(fromKey: string, toKey: string): string[] {
  const total = daysBetween(fromKey, toKey);
  if (total < 0) return [];
  return Array.from({ length: total + 1 }, (_, index) => shiftDays(fromKey, index));
}

function sumByLabel(rows: CostByServiceRow[] | undefined, currency: string | null): Map<string, number> {
  const totals = new Map<string, number>();
  rows?.forEach((row) => {
    if (currency && row.currency && row.currency.toUpperCase() !== currency.toUpperCase()) return;
    const label = row.label || '(sem serviço)';
    totals.set(label, (totals.get(label) ?? 0) + (row.totalCost ?? 0));
  });
  return totals;
}

export default function SubscriptionCostPanel({
  subscriptionId,
  subscriptionName,
  referenceDate,
  expectedDailyCost,
  cycleTurnDate,
  anomalyDays,
}: SubscriptionCostPanelProps) {
  const [windowDays, setWindowDays] = useState(30);
  const [selectedDate, setSelectedDate] = useState<string | null>(null);

  const rangeEnd = referenceDate;
  const rangeStart = useMemo(() => shiftDays(rangeEnd, -(windowDays - 1)), [rangeEnd, windowDays]);

  const { data: trend, loading: loadingTrend, error: trendError } = useFetch(
    () => api.getCostTrendByService(rangeStart, rangeEnd, subscriptionId),
    [rangeStart, rangeEnd, subscriptionId]
  );

  const dominantCurrency = useMemo(() => {
    const totals = new Map<string, number>();
    trend?.items?.forEach((item) => {
      const currency = item.currency || 'BRL';
      totals.set(currency, (totals.get(currency) ?? 0) + (item.totalCost ?? 0));
    });
    return [...totals.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? 'BRL';
  }, [trend]);

  const currencyFormatter = useMemo(() => {
    try {
      return new Intl.NumberFormat('pt-BR', { style: 'currency', currency: dominantCurrency || 'BRL' });
    } catch {
      return new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 2 });
    }
  }, [dominantCurrency]);

  // Every day in the window is plotted; a day the export never delivered stays null instead of zero.
  const observedPoints = useMemo<CostTrendPoint[]>(() => {
    const byDate = new Map<string, number>();
    const availableDates = new Set(trend?.availableDates ?? []);
    trend?.items?.forEach((item) => {
      if ((item.currency || 'BRL').toUpperCase() !== dominantCurrency.toUpperCase()) return;
      if (!item.date) return;
      byDate.set(item.date, (byDate.get(item.date) ?? 0) + (item.totalCost ?? 0));
    });
    return datesBetween(rangeStart, rangeEnd).map((key) => ({
      date: key,
      value: byDate.has(key) ? byDate.get(key)! : availableDates.has(key) ? 0 : null,
      source: trend?.sourceByDate?.[key] ?? null,
    }));
  }, [trend, dominantCurrency, rangeStart, rangeEnd]);

  const projectedPoints = useMemo<CostTrendPoint[]>(() => {
    if (!cycleTurnDate) return [];
    const withValue = observedPoints.filter((point) => point.value != null);
    if (withValue.length === 0) return [];
    const recent = withValue.slice(-7);
    const average = recent.reduce((sum, point) => sum + (point.value ?? 0), 0) / recent.length;
    const start = shiftDays(rangeEnd, 1);
    const lastCycleDay = shiftDays(cycleTurnDate, -1);
    const span = daysBetween(start, lastCycleDay);
    if (span < 0) return [];
    const end = span > MAX_PROJECTED_DAYS ? shiftDays(start, MAX_PROJECTED_DAYS) : lastCycleDay;
    return datesBetween(start, end).map((date) => ({ date, value: average }));
  }, [cycleTurnDate, observedPoints, rangeEnd]);

  const anomalyDates = useMemo(
    () => new Set(anomalyDays.filter((day) => day.hasAnomaly).map((day) => day.date)),
    [anomalyDays]
  );

  const previousDate = useMemo(
    () => (selectedDate ? shiftDays(selectedDate, -1) : null),
    [selectedDate]
  );

  const { data: breakdown, loading: loadingBreakdown, error: breakdownError } = useFetch(async () => {
    if (!selectedDate) return null;
    const [services, previousServices, resources] = await Promise.all([
      api.getCostByService(selectedDate, subscriptionId),
      previousDate
        ? api.getCostByService(previousDate, subscriptionId).catch(() => null)
        : Promise.resolve(null),
      api.getCostByResource(selectedDate, { subscriptionId }).catch(() => null),
    ]);
    return { services, previousServices, resources };
  }, [selectedDate, previousDate, subscriptionId]);

  const serviceDeltas = useMemo<ServiceDelta[]>(() => {
    if (!breakdown?.services) return [];
    const current = sumByLabel(breakdown.services.items, dominantCurrency);
    // A previous day with no collected rows is unknown, not zero: showing it as zero would
    // report the whole of today's cost as an increase.
    const previous =
      breakdown.previousServices?.items && breakdown.previousServices.items.length > 0
        ? sumByLabel(breakdown.previousServices.items, dominantCurrency)
        : null;
    const labels = new Set([...current.keys(), ...(previous?.keys() ?? [])]);
    const rows = [...labels].map((label) => {
      const totalCost = current.get(label) ?? 0;
      const previousCost = previous ? previous.get(label) ?? 0 : null;
      return {
        label,
        totalCost,
        previousCost,
        delta: previousCost == null ? null : totalCost - previousCost,
      };
    });
    return rows.sort((a, b) =>
      a.delta != null && b.delta != null ? b.delta - a.delta : b.totalCost - a.totalCost
    );
  }, [breakdown, dominantCurrency]);

  const resourceRows = useMemo<CostByResourceRow[]>(() => {
    const items = breakdown?.resources?.items ?? [];
    return [...items]
      .filter((item) => !item.currency || item.currency.toUpperCase() === dominantCurrency.toUpperCase())
      .sort((a, b) => (b.totalCost ?? 0) - (a.totalCost ?? 0))
      .slice(0, TOP_RESOURCES);
  }, [breakdown, dominantCurrency]);

  const selectedTotal = serviceDeltas.reduce((sum, row) => sum + row.totalCost, 0);
  const previousTotal = serviceDeltas.reduce((sum, row) => sum + (row.previousCost ?? 0), 0);
  const hasPreviousDay = serviceDeltas.some((row) => row.previousCost != null);
  const missingDays = observedPoints.filter((point) => point.value == null).length;
  const sourceSummary = useMemo(() => {
    const counts = Object.values(trend?.sourceByDate ?? {}).reduce<Record<string, number>>(
      (result, source) => ({ ...result, [source]: (result[source] ?? 0) + 1 }),
      {}
    );
    return Object.entries(counts)
      .map(([source, count]) => `${source}: ${count}`)
      .join('; ');
  }, [trend?.sourceByDate]);

  const formatDelta = (delta: number | null, previousCost: number | null) => {
    if (delta == null) return '—';
    const percent = previousCost && previousCost > 0 ? (delta / previousCost) * 100 : null;
    const signal = delta > 0 ? '+' : '';
    return `${signal}${currencyFormatter.format(delta)}${
      percent == null ? '' : ` (${signal}${percent.toFixed(1)}%)`
    }`;
  };

  return (
    <div className="cost-panel">
      <div className="cost-panel-header">
        <div>
          <strong>{subscriptionName || subscriptionId}</strong>
          <span className="cost-panel-subtitle">
            Custo diário de {rangeStart} a {rangeEnd}
            {trend?.dataAsOf ? ` · dados até ${trend.dataAsOf.slice(0, 10)}` : ''}
            {trend?.source ? ` · fonte: ${trend.source}` : ''}
            {sourceSummary ? ` (${sourceSummary})` : ''}
            {missingDays > 0 ? ` · ${missingDays} dia(s) sem dados` : ''}
          </span>
        </div>
        <div className="cost-panel-controls">
          {WINDOW_OPTIONS.map((option) => (
            <button
              key={option}
              type="button"
              className={`cost-panel-range${windowDays === option ? ' cost-panel-range--active' : ''}`}
              onClick={() => setWindowDays(option)}
            >
              {option}d
            </button>
          ))}
        </div>
      </div>

      {loadingTrend && <div className="loading">Carregando custos diários...</div>}
      {trendError && (
        <div className="alert alert--warning">
          Não foi possível carregar a série de custos.
          <br />
          <small>{trendError}</small>
        </div>
      )}

      {!loadingTrend && !trendError && observedPoints.every((point) => point.value == null) && (
        <div className="alert alert--warning">
          Nenhum custo por serviço disponível para esta assinatura no período.
        </div>
      )}

      {!loadingTrend && !trendError && observedPoints.some((point) => point.value != null) && (
        <CostTrendChart
          series={[
            { id: 'observed', label: 'Custo observado', points: observedPoints, variant: 'primary' },
          ]}
          projected={projectedPoints}
          expectedDaily={expectedDailyCost}
          anomalyDates={anomalyDates}
          selectedDate={selectedDate}
          currency={dominantCurrency}
          onSelectDate={setSelectedDate}
        />
      )}

      {selectedDate && (
        <div className="cost-breakdown">
          <div className="cost-breakdown-header">
            <strong>Detalhe de {selectedDate}</strong>
            <span>
              Total do dia: {currencyFormatter.format(selectedTotal)}
              {hasPreviousDay && (
                <>
                  {' · '}
                  <span className={selectedTotal - previousTotal > 0 ? 'text-danger' : 'text-success'}>
                    {formatDelta(selectedTotal - previousTotal, previousTotal)}
                  </span>
                  {' vs. '}
                  {previousDate}
                </>
              )}
            </span>
            <button type="button" className="cost-panel-range" onClick={() => setSelectedDate(null)}>
              Fechar detalhe
            </button>
          </div>

          {loadingBreakdown && <div className="loading">Carregando detalhamento do dia...</div>}
          {breakdownError && (
            <div className="alert alert--warning">
              Não foi possível carregar o detalhamento.
              <br />
              <small>{breakdownError}</small>
            </div>
          )}

          {!loadingBreakdown && !breakdownError && (
            <div className="cost-breakdown-grid">
              <div>
                <h4>
                  {hasPreviousDay
                    ? 'Serviços que mais contribuíram para a variação'
                    : 'Serviços por custo no dia'}
                </h4>
                {serviceDeltas.length === 0 ? (
                  <p className="cost-breakdown-empty">Sem custo por serviço registrado neste dia.</p>
                ) : (
                  <table className="cost-breakdown-table">
                    <thead>
                      <tr>
                        <th>Serviço</th>
                        <th>Custo</th>
                        {hasPreviousDay && <th>Variação</th>}
                      </tr>
                    </thead>
                    <tbody>
                      {serviceDeltas.slice(0, TOP_SERVICES).map((row) => (
                        <tr key={row.label}>
                          <td title={row.label}>{row.label}</td>
                          <td>{currencyFormatter.format(row.totalCost)}</td>
                          {hasPreviousDay && (
                            <td
                              className={
                                row.delta == null ? '' : row.delta > 0 ? 'text-danger' : 'text-success'
                              }
                            >
                              {formatDelta(row.delta, row.previousCost)}
                            </td>
                          )}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </div>

              <div>
                <h4>Recursos com maior custo no dia</h4>
                {resourceRows.length === 0 ? (
                  <p className="cost-breakdown-empty">
                    Sem custo por recurso registrado neste dia. O detalhamento depende da coleta
                    diária por recurso.
                  </p>
                ) : (
                  <table className="cost-breakdown-table">
                    <thead>
                      <tr>
                        <th>Recurso</th>
                        <th>Resource Group</th>
                        <th>Custo</th>
                      </tr>
                    </thead>
                    <tbody>
                      {resourceRows.map((row) => (
                        <tr key={row.resourceId || row.label}>
                          <td title={row.resourceId || row.label}>
                            {row.label || row.resourceId}
                            {row.resourceType && (
                              <span className="cost-breakdown-meta">{row.resourceType}</span>
                            )}
                          </td>
                          <td>{row.resourceGroup || '—'}</td>
                          <td>{currencyFormatter.format(row.totalCost)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
