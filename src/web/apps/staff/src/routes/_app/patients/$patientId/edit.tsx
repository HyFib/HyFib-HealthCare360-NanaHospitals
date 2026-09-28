import { Button, Field, Input, Switch } from '@fluentui/react-components';
import { isApiError } from '@hc360/core';
import { applyServerErrors, ErrorState, FormGrid, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import type { UseFormReturn } from 'react-hook-form';
import { patientApi, patientKeys, patientQueries, type PatientDetail } from '@/modules/patients/api';
import { PatientForm } from '@/modules/patients/PatientForm';
import { fromPatientDetail, toPatientPayload, type PatientFormValues } from '@/modules/patients/validation';

/** ISO instant → value for <input type="datetime-local"> in the workstation's time zone. */
function toLocalInput(iso: string | null): string {
  if (!iso) {
    return '';
  }
  const date = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

export const Route = createFileRoute('/_app/patients/$patientId/edit')({
  component: EditPatientPage,
});

function EditPatientPage() {
  const { patientId } = Route.useParams();
  const patient = useQuery(patientQueries.detail(patientId));

  if (patient.isLoading) {
    return <LoadingState />;
  }
  if (patient.error || !patient.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={patient.error} onRetry={() => void patient.refetch()} />
      </Page>
    );
  }
  // Keyed by version so a reload after a conflict starts from the latest data.
  return <EditForm key={patient.data.version} patient={patient.data} />;
}

function EditForm({ patient }: { patient: PatientDetail }) {
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [form, setForm] = useState<UseFormReturn<PatientFormValues> | null>(null);
  const [defaults] = useState(() => fromPatientDetail(patient));
  const [isActive, setIsActive] = useState(patient.isActive);
  const [isDeceased, setIsDeceased] = useState(patient.isDeceased);
  const [deceasedAt, setDeceasedAt] = useState(toLocalInput(patient.deceasedAt));
  const [removeAbha, setRemoveAbha] = useState(false);
  const [removeAadhaar, setRemoveAadhaar] = useState(false);

  const save = useMutation({
    mutationFn: (values: PatientFormValues) => {
      const payload = toPatientPayload(values);
      return patientApi.update(patient.id, {
        ...payload,
        // null keeps the stored identifier, "" removes it, a value replaces it.
        abhaNumber: removeAbha ? '' : payload.abhaNumber,
        aadhaarNumber: removeAadhaar ? '' : payload.aadhaarNumber,
        isDeceased,
        deceasedAt: isDeceased && deceasedAt ? new Date(deceasedAt).toISOString() : null,
        isActive,
        version: patient.version,
      });
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: patientKeys.all });
      notify.success('Patient details saved');
      await navigate({ to: '/patients/$patientId', params: { patientId: patient.id } });
    },
    onError: (error) => {
      if (isApiError(error) && error.isConflict && error.code !== 'patient.abha_in_use' && error.code !== 'patient.aadhaar_in_use') {
        notify.warning('Someone else updated this record', 'The latest details have been loaded. Please make your change again.');
        void queryClient.invalidateQueries({ queryKey: patientKeys.detail(patient.id) });
        return;
      }
      if (!(form && applyServerErrors(error, form.setError))) {
        notify.error(error, 'Could not save the patient');
      }
    },
  });

  return (
    <Page maxWidth={1400}>
      <PageHeader
        title={`Edit ${patient.fullName}`}
        subtitle={`UHID ${patient.uhid}`}
        breadcrumbs={[
          { label: 'Patients', onClick: () => void navigate({ to: '/patients' }) },
          { label: patient.fullName, onClick: () => void navigate({ to: '/patients/$patientId', params: { patientId: patient.id } }) },
          { label: 'Edit' },
        ]}
      />
      <PatientForm
        defaultValues={defaults}
        onSubmit={(values) => save.mutate(values)}
        busy={save.isPending}
        submitLabel="Save changes"
        onFormReady={setForm}
        identifierHints={{ abha: patient.abhaNumberMasked, aadhaarLast4: patient.aadhaarLast4 }}
        secondaryActions={
          <Button appearance="subtle" onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId: patient.id } })}>
            Cancel
          </Button>
        }
        extraSections={
          <Section title="Record status">
            <FormGrid columns={3}>
              {patient.abhaNumberMasked && (
                <Switch checked={removeAbha} onChange={(_, d) => setRemoveAbha(d.checked)} label={`Remove ABHA ${patient.abhaNumberMasked}`} />
              )}
              {patient.aadhaarLast4 && (
                <Switch checked={removeAadhaar} onChange={(_, d) => setRemoveAadhaar(d.checked)} label={`Remove Aadhaar ending ${patient.aadhaarLast4}`} />
              )}
              <Switch checked={isActive} onChange={(_, d) => setIsActive(d.checked)} label="Record is active" />
              <Switch checked={isDeceased} onChange={(_, d) => setIsDeceased(d.checked)} label="Patient has died" />
              {isDeceased && (
                <Field label="Date and time of death" required>
                  <Input type="datetime-local" value={deceasedAt} onChange={(_, d) => setDeceasedAt(d.value)} />
                </Field>
              )}
            </FormGrid>
          </Section>
        }
      />
    </Page>
  );
}
