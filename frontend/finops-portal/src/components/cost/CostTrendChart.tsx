import { useMemo, useState } from 'react';

export interface CostTrendPoint {
  date: string;
  value: number | null;
  source?: string | null;
}

export interface CostChartSeries {
  id: string;
  label: string;
  points: CostTrendPoint[];
  variant: 'primary' | 'secondary';
}

interface CostTrendChartProps {
  /** Every series must carry the same dates, in the same order: the first one drives the axis. */
  series: CostChartSeries[];
  projected?: CostTrendPoint[];
  expectedDaily?: number | null;
  expectedLabel?: string;
  anomalyDates?: ReadonlySet<string>;
  selectedDate?: string | null;
  currency: string;
  onSelectDate?: (date: string) => void;
}

const VIEW_WIDTH = 820;
const VIEW_HEIGHT = 250;
const PADDING = { top: 16, right: 18, bottom: 34, left: 62 };
const PLOT_WIDTH = VIEW_WIDTH - PADDING.left - PADDING.right;
const PLOT_HEIGHT = VIEW_HEIGHT - PADDING.top - PADDING.bottom;
const MAX_DATE_LABELS = 7;

function niceCeil(value: number): number {
  if (value <= 0) return 0;
  const magnitude = 10 ** Math.floor(Math.log10(value));
  return Math.ceil(value / magnitude) * magnitude;
}

function formatDayLabel(date: string): string {
  const [, month, day] = date.split('-');
  return month && day ? `${day}/${month}` : date;
}

export default function CostTrendChart({
  series,
  projected = [],
  expectedDaily,
  expectedLabel = 'Meta diária',
  anomalyDates,
  selectedDate,
  currency,
  onSelectDate,
}: CostTrendChartProps) {
  const [hoveredDate, setHoveredDate] = useState<string | null>(null);

  const currencyFormatter = useMemo(() => {
    try {
      return new Intl.NumberFormat('pt-BR', { style: 'currency', currency: currency || 'BRL' });
    } catch {
      return new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 2 });
    }
  }, [currency]);
  const axisFormatter = useMemo(
    () => new Intl.NumberFormat('pt-BR', { notation: 'compact', maximumFractionDigits: 1 }),
    []
  );

  const baseDates = useMemo(
    () => [...(series[0]?.points ?? []).map((point) => point.date), ...projected.map((point) => point.date)],
    [series, projected]
  );

  const scaleMax = useMemo(() => {
    const highest = Math.max(
      ...series.flatMap((item) => item.points.map((point) => point.value ?? 0)),
      ...projected.map((point) => point.value ?? 0),
      expectedDaily ?? 0,
      0
    );
    return niceCeil(highest * 1.1) || 1;
  }, [series, projected, expectedDaily]);

  const xFor = (index: number) =>
    baseDates.length <= 1
      ? PADDING.left + PLOT_WIDTH / 2
      : PADDING.left + (index * PLOT_WIDTH) / (baseDates.length - 1);
  const yFor = (value: number) => PADDING.top + PLOT_HEIGHT - (value / scaleMax) * PLOT_HEIGHT;

  // A day without data is a gap, not a zero: the line breaks instead of dropping to the floor.
  const segmentsBySeries = useMemo(
    () =>
      series.map((item) => {
        const segments: string[][] = [];
        let current: string[] = [];
        item.points.forEach((point, index) => {
          if (point.value == null) {
            if (current.length > 0) segments.push(current);
            current = [];
            return;
          }
          current.push(`${xFor(index)},${yFor(point.value)}`);
        });
        if (current.length > 0) segments.push(current);
        return { id: item.id, variant: item.variant, segments };
      }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [series, baseDates.length, scaleMax]
  );

  const projectedPath = useMemo(() => {
    const primary = series[0]?.points ?? [];
    if (projected.length === 0 || primary.length === 0) return '';
    const lastObservedIndex = primary.reduce(
      (last, point, index) => (point.value == null ? last : index),
      -1
    );
    const anchor =
      lastObservedIndex >= 0 ? [`${xFor(lastObservedIndex)},${yFor(primary[lastObservedIndex].value ?? 0)}`] : [];
    return [
      ...anchor,
      ...projected.map((point, index) => `${xFor(primary.length + index)},${yFor(point.value ?? 0)}`),
    ].join(' ');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [series, projected, baseDates.length, scaleMax]);

  const labelStep = Math.max(1, Math.ceil(baseDates.length / MAX_DATE_LABELS));
  const bandWidth = baseDates.length > 1 ? PLOT_WIDTH / (baseDates.length - 1) : PLOT_WIDTH;
  const focusedDate = hoveredDate ?? selectedDate ?? null;
  const focusedIndex = focusedDate ? baseDates.indexOf(focusedDate) : -1;
  const isFocusProjected = projected.some((point) => point.date === focusedDate);

  const selectableDates = (series[0]?.points ?? [])
    .filter((point) => point.value != null)
    .map((point) => point.date);

  const moveSelection = (offset: number) => {
    if (selectableDates.length === 0 || !onSelectDate) return;
    const currentIndex = selectedDate ? selectableDates.indexOf(selectedDate) : -1;
    const nextIndex =
      currentIndex < 0
        ? selectableDates.length - 1
        : Math.min(selectableDates.length - 1, Math.max(0, currentIndex + offset));
    onSelectDate(selectableDates[nextIndex]);
  };

  const handleKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
    if (!onSelectDate || selectableDates.length === 0) return;
    if (event.key === 'ArrowRight') {
      event.preventDefault();
      moveSelection(1);
    } else if (event.key === 'ArrowLeft') {
      event.preventDefault();
      moveSelection(-1);
    } else if (event.key === 'Home') {
      event.preventDefault();
      onSelectDate(selectableDates[0]);
    } else if (event.key === 'End') {
      event.preventDefault();
      onSelectDate(selectableDates[selectableDates.length - 1]);
    }
  };

  return (
    <div className="cost-chart">
      <div className="cost-chart-legend">
        {series.map((item) => (
          <span
            key={item.id}
            className={`cost-chart-legend-item cost-chart-legend-item--${item.variant}`}
          >
            {item.label}
          </span>
        ))}
        {expectedDaily != null && expectedDaily > 0 && (
          <span className="cost-chart-legend-item cost-chart-legend-item--expected">{expectedLabel}</span>
        )}
        {projected.length > 0 && (
          <span className="cost-chart-legend-item cost-chart-legend-item--projected">
            Projeção até o fim do ciclo
          </span>
        )}
        {anomalyDates && anomalyDates.size > 0 && (
          <span className="cost-chart-legend-item cost-chart-legend-item--anomaly">Dia com anomalia</span>
        )}
        <span className="cost-chart-legend-currency">valores em {currency || 'BRL'}</span>
      </div>

      <div
        className="cost-chart-surface"
        tabIndex={0}
        role="group"
        aria-label={`Custo diário. Use as setas para navegar entre os dias.${
          selectedDate ? ` Dia selecionado: ${selectedDate}.` : ''
        }`}
        onKeyDown={handleKeyDown}
        onMouseLeave={() => setHoveredDate(null)}
      >
        <svg viewBox={`0 0 ${VIEW_WIDTH} ${VIEW_HEIGHT}`} className="cost-chart-svg" role="presentation">
          {[0, 0.5, 1].map((ratio) => {
            const value = scaleMax * ratio;
            const y = yFor(value);
            return (
              <g key={`grid-${ratio}`}>
                <line
                  x1={PADDING.left}
                  y1={y}
                  x2={PADDING.left + PLOT_WIDTH}
                  y2={y}
                  className="cost-chart-grid"
                />
                <text x={PADDING.left - 10} y={y + 4} className="cost-chart-axis-label" textAnchor="end">
                  {axisFormatter.format(value)}
                </text>
              </g>
            );
          })}

          {expectedDaily != null && expectedDaily > 0 && expectedDaily <= scaleMax && (
            <line
              x1={PADDING.left}
              y1={yFor(expectedDaily)}
              x2={PADDING.left + PLOT_WIDTH}
              y2={yFor(expectedDaily)}
              className="cost-chart-expected"
            />
          )}

          {segmentsBySeries.map((item) =>
            item.segments.map((segment) => (
              <polyline
                key={`${item.id}-${segment[0]}`}
                points={segment.join(' ')}
                className={`cost-chart-line cost-chart-line--${item.variant}`}
              />
            ))
          )}

          {projectedPath && <polyline points={projectedPath} className="cost-chart-projection" />}

          {series.map((item) =>
            item.points.map((point, index) => {
              if (point.value == null) return null;
              const isAnomaly = item.variant === 'primary' && (anomalyDates?.has(point.date) ?? false);
              const isSelected = point.date === selectedDate;
              const isFocused = point.date === focusedDate;
              return (
                <circle
                  key={`dot-${item.id}-${point.date}`}
                  cx={xFor(index)}
                  cy={yFor(point.value)}
                  r={isSelected || isFocused ? 5 : isAnomaly ? 4 : 2.5}
                  className={`cost-chart-dot cost-chart-dot--${item.variant}${
                    isAnomaly ? ' cost-chart-dot--anomaly' : ''
                  }${isSelected ? ' cost-chart-dot--selected' : ''}`}
                />
              );
            })
          )}

          {focusedIndex >= 0 && (
            <line
              x1={xFor(focusedIndex)}
              y1={PADDING.top}
              x2={xFor(focusedIndex)}
              y2={PADDING.top + PLOT_HEIGHT}
              className="cost-chart-guide"
            />
          )}

          {baseDates.map((date, index) =>
            index % labelStep === 0 || index === baseDates.length - 1 ? (
              <text
                key={`label-${date}`}
                x={xFor(index)}
                y={VIEW_HEIGHT - 12}
                className="cost-chart-axis-label"
                textAnchor="middle"
              >
                {formatDayLabel(date)}
              </text>
            ) : null
          )}

          {(series[0]?.points ?? []).map((point, index) => (
            <rect
              key={`band-${point.date}`}
              x={Math.max(PADDING.left, xFor(index) - bandWidth / 2)}
              y={PADDING.top}
              width={bandWidth}
              height={PLOT_HEIGHT}
              className="cost-chart-band"
              onMouseEnter={() => setHoveredDate(point.date)}
              onClick={() => point.value != null && onSelectDate?.(point.date)}
            >
              <title>
                {`${formatDayLabel(point.date)}: ${
                  point.value == null ? 'sem dados' : currencyFormatter.format(point.value)
                }${point.source ? ` · fonte: ${point.source}` : ''}`}
              </title>
            </rect>
          ))}
        </svg>
      </div>

      <div className="cost-chart-caption">
        {focusedDate ? (
          <>
            <strong>{formatDayLabel(focusedDate)}</strong>
            {isFocusProjected ? (
              <>
                {' · '}
                {currencyFormatter.format(
                  projected.find((point) => point.date === focusedDate)?.value ?? 0
                )}{' '}
                (projetado)
              </>
            ) : (
              series.map((item) => {
                const point = item.points.find((entry) => entry.date === focusedDate);
                return (
                  <span key={`caption-${item.id}`}>
                    {' · '}
                    {series.length > 1 ? `${item.label}: ` : ''}
                    {point?.value == null ? 'sem dados' : currencyFormatter.format(point.value)}
                    {point?.source ? ` · fonte: ${point.source}` : ''}
                  </span>
                );
              })
            )}
            {anomalyDates?.has(focusedDate) && ' · anomalia registrada'}
          </>
        ) : (
          'Passe o mouse ou use as setas do teclado para ver um dia; clique para detalhar.'
        )}
      </div>
    </div>
  );
}
