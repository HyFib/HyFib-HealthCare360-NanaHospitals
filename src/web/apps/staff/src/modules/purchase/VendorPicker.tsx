import { Badge, Caption1, Text, type FieldProps } from '@fluentui/react-components';
import type { PagedResult } from '@hc360/core';
import { api } from '@/lib/api';
import { SearchPicker } from '@/modules/inventory/ItemPicker';
import type { VendorListItem } from './api';

/** Find a vendor by name, code or GSTIN; shows the drug-licence state so an expired licence is caught before ordering. */
export function VendorPicker({
  value,
  onPick,
  label = 'Vendor',
  required,
  hint,
  autoFocus,
  disabled,
}: {
  value: { name: string; code?: string } | null;
  onPick: (vendor: VendorListItem) => void;
  label?: string;
  required?: boolean;
  hint?: FieldProps['hint'];
  autoFocus?: boolean;
  disabled?: boolean;
}) {
  return (
    <SearchPicker<VendorListItem>
      label={label}
      required={required}
      hint={hint}
      autoFocus={autoFocus}
      disabled={disabled}
      placeholder="Vendor name, code or GSTIN"
      queryKey={['purchase', 'vendors', 'picker']}
      fetch={(search, signal) =>
        api
          .get<PagedResult<VendorListItem>>('/api/v1/purchase/vendors', { query: { search, pageSize: 10 }, signal })
          .then((r) => r.items)
      }
      getKey={(v) => v.id}
      bestMatch={(items, typed) =>
        items.find(
          (v) => v.code.toUpperCase() === typed.trim().toUpperCase() || v.gstin === typed.trim().toUpperCase(),
        ) ?? items[0]
      }
      selectedText={value ? (value.code ? `${value.name} (${value.code})` : value.name) : null}
      onPick={onPick}
      minLength={2}
      emptyText="No vendor matches. Add the vendor under Purchase, then Vendors."
      renderOption={(v) => (
        <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, width: '100%' }}>
          <span>
            <Text weight="semibold">{v.name}</Text>
            <Caption1 block>
              {v.code}
              {v.gstin ? ` · GSTIN ${v.gstin}` : ' · no GSTIN'}
              {v.city ? ` · ${v.city}` : ''}
            </Caption1>
          </span>
          {v.drugLicenseExpired ? (
            <Badge size="small" color="danger">
              Licence expired
            </Badge>
          ) : !v.drugLicenseNumber ? (
            <Badge size="small" appearance="tint">
              No drug licence
            </Badge>
          ) : null}
        </div>
      )}
    />
  );
}
