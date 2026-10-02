import { Combobox, Field, Option, Spinner } from '@fluentui/react-components';
import { useDebouncedValue } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { inpatientQueries, type AdmissionListItem } from './api';

export interface PickedAdmission {
  admissionId: string;
  patientId: string;
  admissionNumber: string;
  patientName: string;
  uhid: string;
  wardName: string | null;
  bedLabel: string | null;
}

function toPickedAdmission(a: AdmissionListItem): PickedAdmission {
  return {
    admissionId: a.id,
    patientId: a.patientId,
    admissionNumber: a.admissionNumber,
    patientName: a.patientName,
    uhid: a.uhid,
    wardName: a.wardName,
    bedLabel: a.bedLabel,
  };
}

/** Find a patient who is in hospital now, by name, UHID, mobile or IP number (min. 3 characters). */
export function AdmissionPicker({
  value,
  onChange,
  label = 'Admitted patient',
  required,
  wardId,
}: {
  value: PickedAdmission | null;
  onChange: (admission: PickedAdmission | null) => void;
  label?: string;
  required?: boolean;
  wardId?: string;
}) {
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 300);
  const results = useQuery({
    ...inpatientQueries.admissions({ search: debounced, activeOnly: true, wardId, pageSize: 8 }),
    enabled: debounced.length >= 3,
  });
  const items = debounced.length >= 3 ? (results.data?.items ?? []) : [];

  return (
    <Field label={label} required={required} hint="Name, UHID, mobile or IP number">
      <Combobox
        placeholder="Type at least 3 characters"
        value={term || (value ? `${value.patientName} (${value.bedLabel ?? value.admissionNumber})` : '')}
        selectedOptions={value ? [value.admissionId] : []}
        onChange={(event) => {
          setTerm(event.target.value);
          if (!event.target.value) {
            onChange(null);
          }
        }}
        onOptionSelect={(_, data) => {
          const picked = items.find((a) => a.id === data.optionValue);
          onChange(picked ? toPickedAdmission(picked) : null);
          setTerm('');
        }}
        expandIcon={results.isFetching ? <Spinner size="extra-tiny" /> : undefined}
        clearable
      >
        {debounced.length >= 3 && items.length === 0 && !results.isFetching && (
          <Option value="" disabled text="No match">
            No admitted patient found
          </Option>
        )}
        {items.map((a) => (
          <Option key={a.id} value={a.id} text={`${a.patientName} (${a.bedLabel ?? a.admissionNumber})`}>
            {a.patientName} · {a.uhid} · {[a.wardName, a.bedLabel].filter(Boolean).join(' ')}
          </Option>
        ))}
      </Combobox>
    </Field>
  );
}
