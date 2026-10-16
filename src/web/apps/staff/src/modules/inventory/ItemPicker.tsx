import {
  Caption1,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  Spinner,
  Text,
  tokens,
  type FieldProps,
} from '@fluentui/react-components';
import { BarcodeScannerRegular } from '@fluentui/react-icons';
import { formatInr, useDebouncedValue } from '@hc360/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useState, type KeyboardEvent, type ReactNode, type Ref } from 'react';
import { api } from '@/lib/api';
import type { ItemLookup } from './api';
import { bestItemMatch, formatExpiry, formatQty } from './format';
import type { ItemType } from './labels';
import { ItemFlags } from './StockBits';

const useStyles = makeStyles({
  wrap: { position: 'relative', width: '100%' },
  popup: {
    position: 'absolute',
    top: 'calc(100% + 2px)',
    left: 0,
    right: 0,
    zIndex: 20,
    maxHeight: '320px',
    overflowY: 'auto',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow16,
    padding: '4px',
  },
  option: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '6px 10px',
    borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer',
  },
  active: { backgroundColor: tokens.colorNeutralBackground1Selected },
  empty: { padding: '10px', color: tokens.colorNeutralForeground3 },
  grow: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' },
  side: { textAlign: 'end', whiteSpace: 'nowrap', display: 'flex', flexDirection: 'column' },
  muted: { color: tokens.colorNeutralForeground3 },
  out: { color: tokens.colorPaletteRedForeground1 },
});

export interface SearchPickerProps<T> {
  label?: string;
  hint?: FieldProps['hint'];
  placeholder?: string;
  /** Distinguishes the search source in the query cache. */
  queryKey: readonly unknown[];
  fetch: (term: string, signal?: AbortSignal) => Promise<T[]>;
  getKey: (item: T) => string;
  renderOption: (item: T) => ReactNode;
  onPick: (item: T) => void;
  /** Best match for Enter (barcode scanners type the code and press Enter). Defaults to the highlighted result. */
  bestMatch?: (items: T[], typed: string) => T | undefined;
  onNoMatch?: (typed: string) => void;
  minLength?: number;
  autoFocus?: boolean;
  disabled?: boolean;
  emptyText?: string;
  required?: boolean;
  validationMessage?: string;
  /** Keep the chosen text in the box (for single-choice pickers). */
  selectedText?: string | null;
  inputRef?: Ref<HTMLInputElement>;
  /** Text to search for when the picker opens (e.g. the generic name when substituting). */
  initialText?: string;
}

/**
 * Type-ahead search with keyboard control: arrows move, Enter picks, Esc closes. A barcode scanner (keyboard
 * wedge) types the code and presses Enter, which picks the exact match straight away.
 */
export function SearchPicker<T>({
  label,
  hint,
  placeholder,
  queryKey,
  fetch,
  getKey,
  renderOption,
  onPick,
  bestMatch,
  onNoMatch,
  minLength = 2,
  autoFocus,
  disabled,
  emptyText,
  required,
  validationMessage,
  selectedText,
  inputRef,
  initialText,
}: SearchPickerProps<T>) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const listId = useId();
  const [text, setText] = useState(initialText ?? '');
  const [open, setOpen] = useState(Boolean(initialText));
  const [active, setActive] = useState(0);
  const term = useDebouncedValue(text.trim(), 250);
  const enabled = term.length >= minLength;
  const results = useQuery({
    queryKey: [...queryKey, term],
    queryFn: ({ signal }) => fetch(term, signal),
    enabled,
    staleTime: 15_000,
  });
  const items = enabled ? (results.data ?? []) : [];
  const activeIndex = Math.min(active, Math.max(items.length - 1, 0));

  const pick = (item: T) => {
    onPick(item);
    setText('');
    setOpen(false);
    setActive(0);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setOpen(true);
      setActive(Math.min(activeIndex + 1, Math.max(items.length - 1, 0)));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setActive(Math.max(activeIndex - 1, 0));
    } else if (event.key === 'Escape') {
      if (open) {
        event.stopPropagation();
      }
      setOpen(false);
    } else if (event.key === 'Enter') {
      // Enter here picks; it never submits the form around the picker, even with too little typed to search.
      event.preventDefault();
      const typed = text.trim();
      if (typed.length < minLength) {
        return;
      }
      if (typed === term && results.data && !results.isFetching) {
        const best = open && items[activeIndex] ? items[activeIndex] : (bestMatch?.(items, typed) ?? items[0]);
        if (best) {
          pick(best);
        } else {
          onNoMatch?.(typed);
        }
        return;
      }
      void queryClient
        .fetchQuery({
          queryKey: [...queryKey, typed],
          queryFn: ({ signal }) => fetch(typed, signal),
          staleTime: 15_000,
        })
        .then((found) => {
          const best = bestMatch?.(found, typed) ?? found[0];
          if (best) {
            pick(best);
          } else {
            onNoMatch?.(typed);
          }
        })
        .catch(() => onNoMatch?.(typed));
    }
  };

  const showPopup = open && enabled;
  const value = text || (open ? '' : (selectedText ?? ''));

  return (
    <Field
      label={label}
      hint={hint}
      required={required}
      validationMessage={validationMessage}
      validationState={validationMessage ? 'error' : 'none'}
    >
      <div className={styles.wrap}>
        <Input
          ref={inputRef}
          role="combobox"
          aria-expanded={showPopup}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={showPopup && items.length > 0 ? `${listId}-${activeIndex}` : undefined}
          value={value}
          placeholder={placeholder ?? 'Type to search, or scan a barcode'}
          autoFocus={autoFocus}
          disabled={disabled}
          autoComplete="off"
          contentBefore={<BarcodeScannerRegular aria-hidden />}
          contentAfter={results.isFetching ? <Spinner size="extra-tiny" aria-label="Searching" /> : undefined}
          onChange={(_, d) => {
            setText(d.value);
            setOpen(true);
            setActive(0);
          }}
          onFocus={() => setOpen(true)}
          onBlur={() => setOpen(false)}
          onKeyDown={onKeyDown}
          style={{ width: '100%' }}
        />
        {showPopup && (
          <div id={listId} role="listbox" className={styles.popup}>
            {items.length === 0 && (
              <div className={styles.empty}>
                {results.isFetching || results.isLoading ? 'Searching…' : (emptyText ?? 'Nothing matches')}
              </div>
            )}
            {items.map((item, index) => (
              <div
                key={getKey(item)}
                id={`${listId}-${index}`}
                role="option"
                aria-selected={index === activeIndex}
                className={mergeClasses(styles.option, index === activeIndex && styles.active)}
                onMouseDown={(event) => event.preventDefault()}
                onMouseEnter={() => setActive(index)}
                onClick={() => pick(item)}
              >
                {renderOption(item)}
              </div>
            ))}
          </div>
        )}
      </div>
    </Field>
  );
}

/** One search result row: name, generic and strength, flags, and stock in the chosen store. */
export function ItemOption({
  name,
  code,
  genericName,
  strength,
  schedule,
  isControlled,
  isColdChain,
  available,
  unit,
  mrp,
  nearestExpiry,
  showStock,
}: {
  name: string;
  code: string;
  genericName: string | null;
  strength: string | null;
  schedule: ItemLookup['schedule'] | null;
  isControlled: boolean;
  isColdChain: boolean;
  available: number | null;
  unit: string;
  mrp: number | null;
  nearestExpiry: string | null;
  showStock: boolean;
}) {
  const styles = useStyles();
  return (
    <>
      <div className={styles.grow}>
        <Text weight="semibold" truncate wrap={false}>
          {name}
        </Text>
        <Caption1 className={styles.muted}>{[code, genericName, strength].filter(Boolean).join(' · ')}</Caption1>
      </div>
      <ItemFlags schedule={schedule} isControlled={isControlled} isColdChain={isColdChain} />
      {showStock && (
        <div className={styles.side}>
          <Text weight="semibold" className={available !== null && available <= 0 ? styles.out : undefined}>
            {available === null ? '—' : available <= 0 ? 'Out of stock' : formatQty(available, unit)}
          </Text>
          <Caption1 className={styles.muted}>
            {mrp !== null ? `MRP ${formatInr(mrp)}` : ''}
            {nearestExpiry ? ` · exp ${formatExpiry(nearestExpiry)}` : ''}
          </Caption1>
        </div>
      )}
    </>
  );
}

/** Item master search (catalogue readers) with stock in `storeId` when given. */
export function ItemPicker({
  storeId,
  type,
  onPick,
  label = 'Add an item',
  hint,
  placeholder,
  autoFocus,
  disabled,
  showStock = true,
}: {
  storeId?: string | null;
  type?: ItemType;
  onPick: (item: ItemLookup) => void;
  label?: string;
  hint?: FieldProps['hint'];
  placeholder?: string;
  autoFocus?: boolean;
  disabled?: boolean;
  showStock?: boolean;
}) {
  return (
    <SearchPicker<ItemLookup>
      label={label}
      hint={hint}
      placeholder={placeholder ?? 'Item name, generic name, code or barcode'}
      queryKey={['inventory', 'items', 'picker', storeId ?? null, type ?? null]}
      fetch={(search, signal) =>
        api.get<ItemLookup[]>('/api/v1/inventory/items/lookup', { query: { search, storeId, type, take: 20 }, signal })
      }
      getKey={(item) => item.id}
      bestMatch={bestItemMatch}
      autoFocus={autoFocus}
      disabled={disabled}
      emptyText="No item matches. Check the spelling or the item master."
      onPick={onPick}
      renderOption={(item) => (
        <ItemOption
          name={item.name}
          code={item.code}
          genericName={item.genericName}
          strength={item.strength}
          schedule={item.schedule}
          isControlled={item.isControlled}
          isColdChain={item.isColdChain}
          available={item.available}
          unit={item.baseUnit}
          mrp={item.mrp}
          nearestExpiry={item.nearestExpiry}
          showStock={showStock && Boolean(storeId)}
        />
      )}
    />
  );
}
