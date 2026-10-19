import { useId, useMemo, useRef, useState } from 'react';
import { ChevronDown } from 'lucide-react';

export interface SearchSelectOption {
  value: string;
  label: string;
  /** Texto secundário exibido e também usado na busca (ex.: ID da subscription, região do RG). */
  hint?: string;
  /** Opção sempre visível no topo, independente da busca (ex.: "Criar novo"). */
  pinned?: boolean;
}

interface SearchSelectProps {
  id: string;
  label: string;
  /** Nome acessível quando o rótulo visível se repete na página (ex.: "Subscription de origem"). */
  ariaLabel?: string;
  options: SearchSelectOption[];
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  disabled?: boolean;
  loading?: boolean;
  error?: string | null;
}

/** Campo único de busca + seleção (combobox ARIA): digitar filtra a lista; setas, Enter e Esc navegam. */
export function SearchSelect({ id, label, ariaLabel, options, value, onChange, placeholder = 'Digite para buscar…',
  disabled, loading, error }: SearchSelectProps) {
  const listId = useId();
  const input = useRef<HTMLInputElement>(null);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [active, setActive] = useState(0);

  const selected = options.find(o => o.value === value);
  const term = query.trim().toLowerCase();
  const filtered = useMemo(() => options.filter(o => o.pinned || !term
    || o.label.toLowerCase().includes(term) || (o.hint ?? '').toLowerCase().includes(term)), [options, term]);

  function openList() {
    if (disabled || loading) return;
    setQuery('');
    setActive(Math.max(0, filtered.findIndex(o => o.value === value)));
    setOpen(true);
  }

  function choose(option: SearchSelectOption) {
    onChange(option.value);
    setOpen(false);
    setQuery('');
  }

  function onKeyDown(event: React.KeyboardEvent<HTMLInputElement>) {
    if (!open && (event.key === 'ArrowDown' || event.key === 'Enter')) { event.preventDefault(); openList(); return; }
    if (!open) return;
    if (event.key === 'ArrowDown') { event.preventDefault(); setActive(i => Math.min(i + 1, filtered.length - 1)); }
    else if (event.key === 'ArrowUp') { event.preventDefault(); setActive(i => Math.max(i - 1, 0)); }
    else if (event.key === 'Enter') { event.preventDefault(); if (filtered[active]) choose(filtered[active]); }
    else if (event.key === 'Escape') { event.preventDefault(); setOpen(false); setQuery(''); }
  }

  const shown = open ? query : selected ? selected.label : '';
  const status = loading ? 'Carregando…' : open && filtered.length === 0 ? 'Nenhum resultado' : null;

  return <div className="filter-group search-select">
    <label htmlFor={id}>{label}</label>
    <div className="search-select__control">
      <input ref={input} id={id} type="text" role="combobox" autoComplete="off" spellCheck={false}
        aria-label={ariaLabel} aria-expanded={open} aria-controls={listId} aria-autocomplete="list"
        aria-activedescendant={open && filtered[active] ? `${listId}-${active}` : undefined}
        value={shown} placeholder={loading ? 'Carregando…' : placeholder} disabled={disabled || loading}
        onFocus={openList} onClick={() => !open && openList()}
        onChange={e => { setQuery(e.target.value); setActive(0); setOpen(true); }}
        onBlur={() => { setOpen(false); setQuery(''); }} onKeyDown={onKeyDown} />
      <ChevronDown size={16} aria-hidden="true" className="search-select__chevron" />
    </div>
    {open && <ul id={listId} role="listbox" className="search-select__list" aria-label={ariaLabel ?? label}>
      {status && <li className="search-select__empty" role="presentation">{status}</li>}
      {filtered.map((option, index) => <li key={option.value} id={`${listId}-${index}`} role="option"
        aria-selected={option.value === value}
        className={`search-select__option${index === active ? ' is-active' : ''}${option.value === value ? ' is-selected' : ''}`}
        // mousedown antes do blur do input: sem isso a lista fecha antes do clique.
        onMouseDown={e => { e.preventDefault(); choose(option); }} onMouseEnter={() => setActive(index)}>
        <span>{option.label}</span>{option.hint && <small>{option.hint}</small>}
      </li>)}
    </ul>}
    {error && <p role="alert">{error}</p>}
  </div>;
}
