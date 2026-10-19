import { act, renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { deferred } from '../test/fixtures';
import { useFetch } from './useFetch';

describe('useFetch', () => {
  it('exibe carregamento até a resposta e permite atualizar os dados', async () => {
    const pending = deferred<string>();
    const fetcher = vi.fn().mockReturnValueOnce(pending.promise).mockResolvedValueOnce('atualizado');
    const { result } = renderHook(() => useFetch(fetcher));
    expect(result.current.loading).toBe(true);
    await act(async () => pending.resolve('primeiro'));
    expect(result.current.data).toBe('primeiro');
    expect(result.current.loading).toBe(false);
    await act(async () => result.current.refetch());
    expect(result.current.data).toBe('atualizado');
    expect(fetcher).toHaveBeenCalledTimes(2);
  });

  it('mostra falha e limpa o erro quando uma nova consulta tem sucesso', async () => {
    const fetcher = vi.fn().mockRejectedValueOnce(new Error('API indisponível')).mockResolvedValueOnce(['DEV']);
    const { result } = renderHook(() => useFetch(fetcher));
    await waitFor(() => expect(result.current.error).toBe('API indisponível'));
    expect(result.current.data).toBeNull();
    await act(async () => result.current.refetch());
    expect(result.current.error).toBeNull();
    expect(result.current.data).toEqual(['DEV']);
  });

  it.each(['success', 'failure'] as const)('ignora uma resposta antiga (%s) após trocar os filtros', async (outcome) => {
    const old = deferred<string>();
    const recent = deferred<string>();
    const fetcher = vi.fn((id: string) => id === 'old' ? old.promise : recent.promise);
    const { result, rerender } = renderHook(({ id }) => useFetch(() => fetcher(id), [id]), { initialProps: { id: 'old' } });
    rerender({ id: 'recent' });
    await act(async () => recent.resolve('subscription atual'));
    await act(async () => { if (outcome === 'success') old.resolve('subscription anterior'); else old.reject(new Error('falha antiga')); });
    expect(result.current.data).toBe('subscription atual');
    expect(result.current.error).toBeNull();
    expect(result.current.loading).toBe(false);
  });

  it('uma resposta antiga não encerra o carregamento da consulta atual', async () => {
    const old = deferred<string>();
    const recent = deferred<string>();
    const fetcher = vi.fn().mockReturnValueOnce(old.promise).mockReturnValueOnce(recent.promise);
    const { result } = renderHook(() => useFetch(fetcher));
    act(() => { void result.current.refetch(); });
    await act(async () => old.resolve('antigo'));
    expect(result.current.loading).toBe(true);
    expect(result.current.data).toBeNull();
    await act(async () => recent.resolve('atual'));
    expect(result.current.data).toBe('atual');
  });
});
