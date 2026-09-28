import { Field, Select, Spinner } from '@fluentui/react-components';
import { formatDate, humanize } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { patientQueries, type VisitListItem } from '@/modules/patients/api';

/** One of the patient's ten most recent visits (cancelled visits left out). */
export function VisitSelect({
  patientId,
  value,
  onChange,
  required,
  label = 'Visit',
  hint,
  validationMessage,
  emptyLabel = 'Not about a particular visit',
}: {
  patientId: string;
  value: string | null;
  onChange: (visit: VisitListItem | null) => void;
  required?: boolean;
  label?: string;
  hint?: string;
  validationMessage?: string;
  emptyLabel?: string;
}) {
  const visits = useQuery(patientQueries.visits(patientId, 1));
  const items = (visits.data?.items ?? []).filter((v) => v.status !== 'Cancelled');
  const message = validationMessage ?? (visits.error ? 'Could not load the visits.' : undefined);
  return (
    <Field
      label={label}
      required={required}
      hint={hint ?? (visits.isSuccess && items.length === 0 ? 'No visits on record for this patient.' : undefined)}
      validationMessage={message}
      validationState={message ? 'error' : 'none'}
    >
      {visits.isPending ? (
        <Spinner size="tiny" label="Loading visits…" labelPosition="after" />
      ) : (
        <Select value={value ?? ''} onChange={(_, d) => onChange(items.find((v) => v.id === d.value) ?? null)}>
          <option value="">{required ? 'Choose the visit' : emptyLabel}</option>
          {items.map((v) => (
            <option key={v.id} value={v.id}>
              {[v.visitNumber, formatDate(v.visitedAt), humanize(v.type), v.departmentName].filter(Boolean).join(' · ')}
            </option>
          ))}
        </Select>
      )}
    </Field>
  );
}
