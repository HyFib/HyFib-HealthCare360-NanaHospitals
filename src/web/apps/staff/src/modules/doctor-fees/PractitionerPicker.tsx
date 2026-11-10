import { Combobox, Field, Option, Spinner } from '@fluentui/react-components';
import { useDebouncedValue } from '@hc360/core';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { searchPractitioners } from '@/lib/lookups';

export interface PickedDoctor {
  id: string;
  name: string;
}

/** Find a doctor by name or code (the practitioner master; any staff may search it). */
export function PractitionerPicker({
  value,
  onChange,
  label = 'Doctor',
  required,
  hint,
  autoFocus,
}: {
  value: PickedDoctor | null;
  onChange: (doctor: PickedDoctor | null) => void;
  label?: string;
  required?: boolean;
  hint?: string;
  autoFocus?: boolean;
}) {
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 250);
  const results = useQuery({
    queryKey: ['doctor-fees', 'doctor-search', debounced],
    queryFn: () => searchPractitioners(debounced),
    staleTime: 5 * 60_000,
    // The list keeps its options while the next search runs, so an option is never taken away under the pointer.
    placeholderData: keepPreviousData,
  });
  const items = results.data ?? [];

  return (
    <Field label={label} required={required} hint={hint}>
      <Combobox
        placeholder="Type a doctor's name or code"
        value={term || value?.name || ''}
        selectedOptions={value ? [value.id] : []}
        onChange={(event) => {
          setTerm(event.target.value);
          if (!event.target.value) {
            onChange(null);
          }
        }}
        onOptionSelect={(_, data) => {
          // The doctor comes from the option chosen, not from the results as they are by now: the search runs 250 ms
          // after the last key, and a choice made as its results arrived used to find nothing and pick no one.
          onChange(data.optionValue ? { id: data.optionValue, name: data.optionText ?? '' } : null);
          setTerm('');
        }}
        expandIcon={results.isFetching ? <Spinner size="extra-tiny" aria-hidden /> : undefined}
        clearable
        autoFocus={autoFocus}
      >
        {items.map((d) => (
          <Option key={d.id} value={d.id} text={d.name}>
            {d.name} ({d.code})
          </Option>
        ))}
      </Combobox>
    </Field>
  );
}
