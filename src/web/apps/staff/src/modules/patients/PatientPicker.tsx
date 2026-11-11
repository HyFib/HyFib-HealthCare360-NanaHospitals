import { Combobox, Field, Option, Spinner, type FieldProps } from '@fluentui/react-components';
import { useDebouncedValue, type PagedResult } from '@hc360/core';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '@/lib/api';
import { useShownItems } from '@/lib/useShownItems';
import type { PatientListItem } from './api';

export interface PickedPatient {
  id: string;
  uhid: string;
  fullName: string;
}

/** Find a patient by UHID, mobile or name (min. 3 characters). Reused by billing, pharmacy, visitor passes… */
export function PatientPicker({
  value,
  onChange,
  label = 'Patient',
  required,
  validationMessage,
  hint,
  autoFocus,
}: {
  value: PickedPatient | null;
  onChange: (patient: PickedPatient | null) => void;
  label?: string;
  required?: boolean;
  validationMessage?: string;
  hint?: FieldProps['hint'];
  /** Focus the search box on mount (counter screens where finding the patient is the first step). */
  autoFocus?: boolean;
}) {
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 300);
  const results = useQuery({
    queryKey: ['patients', 'picker', debounced],
    queryFn: ({ signal }) =>
      api.get<PagedResult<PatientListItem>>('/api/v1/patients', { query: { search: debounced, pageSize: 8 }, signal }),
    enabled: debounced.length >= 3,
    staleTime: 15_000,
    // The list keeps its options while the next search runs, so an option is never taken away under the pointer.
    placeholderData: keepPreviousData,
  });

  // Below 3 characters there is no search, and the last one's results are not offered either.
  const items = debounced.length >= 3 ? (results.data?.items ?? []) : [];
  // The patient comes from the options shown, not from the results as they are by the time one is chosen.
  const shown = useShownItems(items, (p) => p.id);

  return (
    <Field
      label={label}
      required={required}
      validationMessage={validationMessage}
      validationState={validationMessage ? 'error' : 'none'}
      hint={hint}
    >
      <Combobox
        placeholder="UHID, mobile number or name"
        // Opt-in, for counter screens whose first step is finding the patient.
        autoFocus={autoFocus}
        value={term || (value ? `${value.fullName} (${value.uhid})` : '')}
        selectedOptions={value ? [value.id] : []}
        onChange={(event) => {
          setTerm(event.target.value);
          if (!event.target.value) {
            onChange(null);
          }
        }}
        onOptionSelect={(_, data) => {
          const picked = shown(data.optionValue);
          onChange(picked ? { id: picked.id, uhid: picked.uhid, fullName: picked.fullName } : null);
          setTerm('');
        }}
        expandIcon={results.isFetching ? <Spinner size="extra-tiny" aria-hidden /> : undefined}
        clearable
      >
        {debounced.length >= 3 && items.length === 0 && !results.isFetching && (
          <Option value="" disabled text="No match">
            No patient found
          </Option>
        )}
        {items.map((p) => (
          <Option key={p.id} value={p.id} text={`${p.fullName} (${p.uhid})`}>
            {p.fullName} · {p.uhid} · {p.age ?? '—'}/{p.gender.charAt(0)}
            {p.phoneMasked ? ` · ${p.phoneMasked}` : ''}
          </Option>
        ))}
      </Combobox>
    </Field>
  );
}
