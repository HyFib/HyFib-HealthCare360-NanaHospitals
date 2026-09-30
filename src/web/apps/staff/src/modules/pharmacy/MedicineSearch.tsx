import type { FieldProps } from '@fluentui/react-components';
import type { ReactNode, Ref } from 'react';
import { api } from '@/lib/api';
import { bestItemMatch } from '@/modules/inventory/format';
import { ItemOption, SearchPicker } from '@/modules/inventory/ItemPicker';
import type { PharmacyStockRow } from './api';

/**
 * Medicine search at the pharmacy counter: name, generic name, code or barcode, with what the store can sell now,
 * the MRP and the nearest expiry. A barcode scan picks the item straight away.
 */
export function MedicineSearch({
  storeId,
  onPick,
  label = 'Find medicine',
  hint,
  placeholder,
  autoFocus,
  disabled,
  inputRef,
  onNoMatch,
  initialText,
  renderExtra,
}: {
  storeId: string;
  onPick: (row: PharmacyStockRow) => void;
  label?: string;
  hint?: FieldProps['hint'];
  placeholder?: string;
  autoFocus?: boolean;
  disabled?: boolean;
  inputRef?: Ref<HTMLInputElement>;
  onNoMatch?: (typed: string) => void;
  initialText?: string;
  /** Extra content per result (e.g. whether it is a generic equivalent). */
  renderExtra?: (row: PharmacyStockRow) => ReactNode;
}) {
  return (
    <SearchPicker<PharmacyStockRow>
      label={label}
      hint={hint}
      placeholder={placeholder ?? 'Scan a barcode, or type a medicine or generic name'}
      queryKey={['pharmacy', 'stock', 'search', storeId]}
      fetch={(search, signal) =>
        api.get<PharmacyStockRow[]>('/api/v1/pharmacy/stock', { query: { storeId, search, take: 25 }, signal })
      }
      getKey={(row) => row.itemId}
      bestMatch={bestItemMatch}
      onPick={onPick}
      onNoMatch={onNoMatch}
      autoFocus={autoFocus}
      disabled={disabled}
      inputRef={inputRef}
      initialText={initialText}
      emptyText="No medicine matches. Check the spelling, or search by the generic name."
      renderOption={(row) => (
        <>
          <ItemOption
            name={row.name}
            code={row.code}
            genericName={row.genericName}
            strength={row.strength}
            schedule={row.schedule}
            isControlled={row.isControlled}
            isColdChain={row.isColdChain}
            available={row.available}
            unit={row.baseUnit}
            mrp={row.mrp}
            nearestExpiry={row.nearestExpiry}
            showStock
          />
          {renderExtra?.(row)}
        </>
      )}
    />
  );
}
