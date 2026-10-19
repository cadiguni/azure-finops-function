import { useCallback, useMemo, useState } from 'react';

export type SortDirection = 'asc' | 'desc';

/** Valor comparável extraído de uma linha. `null` significa ausência, não zero. */
export type SortValue = string | number | null | undefined;

export type SortAccessors<T, K extends string> = Record<K, (row: T) => SortValue>;

interface SortableHeaderProps {
  className: string;
  onClick: () => void;
  role: 'button';
  tabIndex: 0;
  'aria-sort': 'ascending' | 'descending' | 'none';
  onKeyDown: (event: React.KeyboardEvent<HTMLTableCellElement>) => void;
}

export interface TableSort<T, K extends string> {
  sorted: T[];
  sortKey: K;
  direction: SortDirection;
  toggle: (key: K) => void;
  /** Seta do cabeçalho: ↕ quando a coluna não ordena, ↑/↓ quando ordena. */
  indicator: (key: K) => string;
  sortableHeader: (key: K) => SortableHeaderProps;
}

/**
 * Ordenação de tabela com o mesmo comportamento das telas de Anomalias e Recomendações:
 * clique alterna a direção, trocar de coluna reinicia na direção padrão dela.
 *
 * Ausência de valor vai sempre para o fim, nas duas direções: um recurso sem custo anterior não é
 * "o mais barato", é um dado que não existe, e deixá-lo no topo de uma ordenação crescente faria
 * parecer que existe.
 */
export function useTableSort<T, K extends string>(
  rows: readonly T[],
  accessors: SortAccessors<T, K>,
  initial: { key: K; direction?: SortDirection },
  /** Colunas que começam decrescentes ao serem escolhidas — dinheiro e contagem. */
  descendingFirst: readonly K[] = []
): TableSort<T, K> {
  const [sortKey, setSortKey] = useState<K>(initial.key);
  const [direction, setDirection] = useState<SortDirection>(initial.direction ?? 'desc');

  const toggle = useCallback((key: K) => {
    setSortKey((currentKey) => {
      if (currentKey === key) {
        setDirection((current) => (current === 'asc' ? 'desc' : 'asc'));
        return currentKey;
      }

      setDirection(descendingFirst.includes(key) ? 'desc' : 'asc');
      return key;
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [descendingFirst.join('|')]);

  const sorted = useMemo(() => {
    const accessor = accessors[sortKey];
    const factor = direction === 'asc' ? 1 : -1;
    return [...rows].sort((left, right) => {
      const leftValue = accessor(left);
      const rightValue = accessor(right);
      const leftMissing = leftValue == null || leftValue === '';
      const rightMissing = rightValue == null || rightValue === '';
      if (leftMissing || rightMissing) {
        return leftMissing && rightMissing ? 0 : leftMissing ? 1 : -1;
      }

      if (typeof leftValue === 'number' && typeof rightValue === 'number') {
        return (leftValue - rightValue) * factor;
      }

      return String(leftValue).localeCompare(String(rightValue), 'pt-BR', { numeric: true }) * factor;
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rows, sortKey, direction]);

  const indicator = useCallback(
    (key: K) => (sortKey !== key ? ' ↕' : direction === 'asc' ? ' ↑' : ' ↓'),
    [sortKey, direction]
  );

  const sortableHeader = useCallback(
    (key: K): SortableHeaderProps => ({
      className: 'sortable',
      onClick: () => toggle(key),
      role: 'button',
      tabIndex: 0,
      'aria-sort': sortKey !== key ? 'none' : direction === 'asc' ? 'ascending' : 'descending',
      onKeyDown: (event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          toggle(key);
        }
      },
    }),
    [sortKey, direction, toggle]
  );

  return { sorted, sortKey, direction, toggle, indicator, sortableHeader };
}
