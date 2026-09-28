import { Combobox, Field, Option } from '@fluentui/react-components';
import { formatInr, useDebouncedValue } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '@/lib/api';

/** Billable service from the platform service catalogue (masters). */
export interface ServiceItem {
  id: string;
  code: string;
  name: string;
  shortName: string | null;
  category: string;
  basePrice: number;
  taxRate: number;
  isActive: boolean;
}

export type ServiceCategory = 'Laboratory' | 'Radiology' | 'Cardiology';

/**
 * Billable service linked to a diagnostic master (lab test, imaging procedure). Its price is what the patient is
 * charged when the service is ordered. Needs the masters.read permission; without it the current link still shows.
 */
export function ServiceLookup({
  category,
  value,
  onChange,
  required,
  validationMessage,
  hint = 'The service on the bill; its price is charged when this is ordered',
}: {
  category: ServiceCategory;
  value: string | null;
  onChange: (id: string | null) => void;
  required?: boolean;
  validationMessage?: string;
  hint?: string;
}) {
  const [term, setTerm] = useState('');
  const [picked, setPicked] = useState<ServiceItem | null>(null);
  const search = useDebouncedValue(term.trim(), 250);
  const current = useQuery({
    queryKey: ['masters', 'service', value],
    queryFn: ({ signal }) => api.get<ServiceItem>(`/api/v1/masters/services/${value}`, { signal }),
    enabled: Boolean(value) && picked?.id !== value,
    staleTime: 10 * 60_000,
    retry: false,
  });
  const results = useQuery({
    queryKey: ['masters', 'services', category, search],
    queryFn: () => api.get<ServiceItem[]>('/api/v1/masters/lookups/services', { query: { category, search } }),
    enabled: search.length >= 2,
    staleTime: 60_000,
  });
  const selected = picked?.id === value ? picked : current.data;
  const label = selected ? `${selected.name} (${selected.code}) · ${formatInr(selected.basePrice, { whole: true })}` : value ? 'Linked service' : '';

  return (
    <Field label="Billable service" required={required} hint={hint} validationMessage={validationMessage} validationState={validationMessage ? 'error' : 'none'}>
      <Combobox
        placeholder="Type 2 letters of the service name or code"
        value={term || label}
        selectedOptions={value ? [value] : []}
        onChange={(event) => {
          setTerm(event.target.value);
          if (!event.target.value) {
            setPicked(null);
            onChange(null);
          }
        }}
        onOptionSelect={(_, data) => {
          const item = results.data?.find((service) => service.id === data.optionValue) ?? null;
          setPicked(item);
          setTerm('');
          onChange(item?.id ?? null);
        }}
        clearable
      >
        {(results.data ?? []).map((service) => (
          <Option key={service.id} value={service.id} text={service.name}>
            {service.name} · {service.code} · {formatInr(service.basePrice, { whole: true })}
          </Option>
        ))}
        {search.length >= 2 && results.data?.length === 0 && (
          <Option value="" disabled text="No match">
            No {category.toLowerCase()} service found
          </Option>
        )}
      </Combobox>
    </Field>
  );
}
