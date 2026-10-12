import { Combobox, Field, Option, Spinner } from '@fluentui/react-components';
import { useDebouncedValue } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { labQueries, type LabTestListItem } from './api';
import { sectionLabel } from './labels';

export interface PickedTestRef {
  id: string;
  code: string;
  name: string;
  unit?: string | null;
}

/**
 * Single test look-up from the test master (QC lots, panel components). `filter` narrows the choice, e.g. numeric
 * analytes only for quality control.
 */
export function TestLookup({
  label,
  value,
  onChange,
  filter,
  required,
  hint,
  validationMessage,
  disabled,
  placeholder = 'Type 2 letters of the name or code',
}: {
  label: string;
  value: PickedTestRef | null;
  onChange: (test: LabTestListItem | null) => void;
  filter?: (test: LabTestListItem) => boolean;
  required?: boolean;
  hint?: string;
  validationMessage?: string;
  disabled?: boolean;
  placeholder?: string;
}) {
  const [term, setTerm] = useState('');
  const search = useDebouncedValue(term.trim(), 250);
  const results = useQuery({ ...labQueries.tests({ search, pageSize: 30 }), enabled: search.length >= 2 });
  const items = (results.data?.items ?? []).filter((test) => !filter || filter(test));

  return (
    <Field
      label={label}
      required={required}
      hint={hint}
      validationMessage={validationMessage}
      validationState={validationMessage ? 'error' : 'none'}
    >
      <Combobox
        placeholder={placeholder}
        disabled={disabled}
        value={term || (value ? `${value.name} (${value.code})` : '')}
        selectedOptions={value ? [value.id] : []}
        onChange={(event) => {
          setTerm(event.target.value);
          if (!event.target.value) {
            onChange(null);
          }
        }}
        onOptionSelect={(_, data) => {
          onChange(items.find((test) => test.id === data.optionValue) ?? null);
          setTerm('');
        }}
        expandIcon={results.isFetching ? <Spinner size="extra-tiny" aria-hidden /> : undefined}
        clearable
      >
        {items.map((test) => (
          <Option key={test.id} value={test.id} text={`${test.name} (${test.code})`}>
            {test.name} · {test.code} · {sectionLabel[test.section]}
            {test.unit ? ` · ${test.unit}` : ''}
          </Option>
        ))}
        {search.length >= 2 && items.length === 0 && !results.isFetching && (
          <Option value="" disabled text="No match">
            No matching test
          </Option>
        )}
      </Combobox>
    </Field>
  );
}
