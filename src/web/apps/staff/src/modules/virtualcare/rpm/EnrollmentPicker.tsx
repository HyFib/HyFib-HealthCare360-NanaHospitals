import { Combobox, Field, Option, Spinner, type FieldProps } from '@fluentui/react-components';
import { useDebouncedValue } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { rpmQueries } from './api';

export interface PickedEnrollment {
  id: string;
  patientName: string;
  uhid: string;
  programName: string;
}

/** Finds an active or paused monitoring enrolment by patient name, UHID or mobile. */
export function EnrollmentPicker({
  value,
  onChange,
  label = 'Monitored patient',
  hint,
  required,
}: {
  value: PickedEnrollment | null;
  onChange: (enrollment: PickedEnrollment | null) => void;
  label?: string;
  hint?: FieldProps['hint'];
  required?: boolean;
}) {
  const [term, setTerm] = useState('');
  const search = useDebouncedValue(term.trim(), 300);
  const results = useQuery({
    ...rpmQueries.enrollments({ search: search || undefined, pageSize: 10 }),
    refetchInterval: false,
  });
  const items = results.data?.items ?? [];
  const text = (e: PickedEnrollment) => `${e.patientName} (${e.uhid}) · ${e.programName}`;

  return (
    <Field label={label} hint={hint} required={required}>
      <Combobox
        placeholder="Patient name, UHID or mobile"
        value={term || (value ? text(value) : '')}
        selectedOptions={value ? [value.id] : []}
        onChange={(event) => {
          setTerm(event.target.value);
          if (!event.target.value) {
            onChange(null);
          }
        }}
        onOptionSelect={(_, data) => {
          const picked = items.find((e) => e.id === data.optionValue);
          onChange(
            picked
              ? { id: picked.id, patientName: picked.patientName, uhid: picked.uhid, programName: picked.programName }
              : null,
          );
          setTerm('');
        }}
        expandIcon={results.isFetching ? <Spinner size="extra-tiny" /> : undefined}
        clearable
      >
        {items.length === 0 && !results.isFetching && (
          <Option value="" disabled text="No match">
            No monitored patient found
          </Option>
        )}
        {items.map((e) => (
          <Option key={e.id} value={e.id} text={text(e)}>
            {e.patientName} · {e.uhid} · {e.programName}
          </Option>
        ))}
      </Combobox>
    </Field>
  );
}
