import {
  Button,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Switch,
  Textarea,
} from '@fluentui/react-components';
import { isApiError } from '@hc360/core';
import { applyServerErrors, ErrorState, FormGrid, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { useWatch, type UseFormReturn } from 'react-hook-form';
import { usePermission } from '@/lib/permissions';
import { askedForApproval } from '@/modules/admin/api';
import { approvalKeys } from '@/modules/approvals/api';
import { patientApi, patientKeys, patientQueries, type PatientDetail } from '@/modules/patients/api';
import { PatientChangesWaiting } from '@/modules/patients/PatientChangesWaiting';
import { PatientForm } from '@/modules/patients/PatientForm';
import { photoChange } from '@/modules/patients/photo';
import {
  asksForApproval,
  fromPatientDetail,
  recordChangesAsked,
  toPatientPayload,
  type PatientFormValues,
  type RecordChangesAsked,
} from '@/modules/patients/validation';

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
  // The patient's page offers Edit only to registration staff; anyone else who opens the address finds no form whose
  // save the server would refuse them.
  const canRegister = usePermission('patients.register');

  if (!canRegister) {
    return (
      <Page maxWidth={900}>
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>You cannot edit patients&apos; details</MessageBarTitle>
            Changing a patient&apos;s registration is for the front desk. Ask them to make the change.
          </MessageBarBody>
        </MessageBar>
      </Page>
    );
  }
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
  // The details came masked: saving them back would replace the real ones, and the server refuses it (SRS PRV-03).
  if (patient.data.contactsMasked) {
    return (
      <Page maxWidth={900}>
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>You cannot edit this patient&apos;s details</MessageBarTitle>
            Editing shows the phone numbers, email and address in full, which are for the staff who contact or bill
            patients. Ask the front desk to make the change.
          </MessageBarBody>
        </MessageBar>
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
  const [changeReason, setChangeReason] = useState('');
  const [reasonError, setReasonError] = useState<string | null>(null);
  const [changes, setChanges] = useState<RecordChangesAsked | null>(null);
  const correctable = patient.identityCorrectableUntil != null;
  const asks = changes !== null && asksForApproval(changes, correctable);

  const save = useMutation({
    mutationFn: (values: PatientFormValues) => {
      const payload = toPatientPayload(values);
      return patientApi.update(patient.id, {
        ...payload,
        // null keeps the stored identifier, "" removes it, a value replaces it.
        abhaNumber: removeAbha ? '' : payload.abhaNumber,
        aadhaarNumber: removeAadhaar ? '' : payload.aadhaarNumber,
        // Editing the details never touches the photo unless it was changed here.
        ...photoChange(patient.photoFileId, values.photoFileId),
        isDeceased,
        deceasedAt: isDeceased && deceasedAt ? new Date(deceasedAt).toISOString() : null,
        isActive,
        version: patient.version,
        changeReason: asks ? changeReason.trim() : null,
      });
    },
    onSuccess: async (saved) => {
      await queryClient.invalidateQueries({ queryKey: patientKeys.all });
      const asked = askedForApproval(saved);
      if (asked) {
        void queryClient.invalidateQueries({ queryKey: approvalKeys.all });
        notify.success(
          'Asked for approval',
          `${asked.summary}. Another person decides it; until then the record keeps its present details. The rest is saved.`,
        );
      } else {
        notify.success('Patient details saved');
      }
      await navigate({ to: '/patients/$patientId', params: { patientId: patient.id } });
    },
    onError: (error) => {
      if (
        isApiError(error) &&
        error.isConflict &&
        error.code !== 'patient.abha_in_use' &&
        error.code !== 'patient.aadhaar_in_use'
      ) {
        notify.warning(
          'Someone else updated this record',
          'The latest details have been loaded. Please make your change again.',
        );
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
          {
            label: patient.fullName,
            onClick: () => void navigate({ to: '/patients/$patientId', params: { patientId: patient.id } }),
          },
          { label: 'Edit' },
        ]}
      />
      <PatientChangesWaiting changes={patient.pendingChanges} />
      <PatientForm
        defaultValues={defaults}
        onSubmit={(values) => {
          if (asks && changeReason.trim().length === 0) {
            setReasonError('Say why: the change goes to someone else for approval, and they read your reason.');
            return;
          }
          save.mutate(values);
        }}
        busy={save.isPending}
        submitLabel={asks ? 'Ask for approval' : 'Save changes'}
        onFormReady={setForm}
        identifierHints={{ abha: patient.abhaNumberMasked, aadhaarLast4: patient.aadhaarLast4 }}
        changePhone={{
          patientId: patient.id,
          otherChanges:
            removeAbha ||
            removeAadhaar ||
            isActive !== patient.isActive ||
            isDeceased !== patient.isDeceased ||
            deceasedAt !== toLocalInput(patient.deceasedAt),
          // The record's version moved on: the form reloads with the new number (keyed by version).
          onChanged: () => void queryClient.invalidateQueries({ queryKey: patientKeys.all }),
        }}
        secondaryActions={
          <Button
            appearance="subtle"
            onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId: patient.id } })}
          >
            Cancel
          </Button>
        }
        extraSections={
          <>
            {form && (
              <ChangesWatcher
                form={form}
                patient={patient}
                options={{
                  removeAbha,
                  removeAadhaar,
                  isDeceased,
                  deceasedAtChanged: deceasedAt !== toLocalInput(patient.deceasedAt),
                }}
                onChange={setChanges}
              />
            )}
            <Section title="Record status">
              <FormGrid columns={3}>
                {patient.abhaNumberMasked && (
                  <Switch
                    checked={removeAbha}
                    onChange={(_, d) => setRemoveAbha(d.checked)}
                    label={`Remove ABHA ${patient.abhaNumberMasked}`}
                  />
                )}
                {patient.aadhaarLast4 && (
                  <Switch
                    checked={removeAadhaar}
                    onChange={(_, d) => setRemoveAadhaar(d.checked)}
                    label={`Remove Aadhaar ending ${patient.aadhaarLast4}`}
                  />
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
            {asks && changes && (
              <Section
                title="Asked for approval"
                description="Saving keeps the rest at once. What follows goes to someone else, and the record keeps its present details until they approve it."
              >
                <MessageBar intent="info" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Who decides</MessageBarTitle>
                    {approvalWords(changes, correctable)}
                  </MessageBarBody>
                </MessageBar>
                <Field
                  label="Reason for the change"
                  required
                  validationMessage={reasonError ?? undefined}
                  hint="The person who approves it reads this."
                >
                  <Textarea
                    value={changeReason}
                    maxLength={500}
                    rows={2}
                    onChange={(_, d) => {
                      setChangeReason(d.value);
                      setReasonError(null);
                    }}
                  />
                </Field>
              </Section>
            )}
          </>
        }
      />
    </Page>
  );
}

/** Tells the page, as the form is filled in, what the save would ask a second person for. */
function ChangesWatcher({
  form,
  patient,
  options,
  onChange,
}: {
  form: UseFormReturn<PatientFormValues>;
  patient: PatientDetail;
  options: Parameters<typeof recordChangesAsked>[2];
  onChange: (changes: RecordChangesAsked) => void;
}) {
  const values = useWatch({ control: form.control }) as PatientFormValues;
  const { removeAbha, removeAadhaar, isDeceased, deceasedAtChanged } = options;
  const changes = recordChangesAsked({ ...form.getValues(), ...values }, patient, {
    removeAbha,
    removeAadhaar,
    isDeceased,
    deceasedAtChanged,
  });
  const key = JSON.stringify(changes);
  useEffect(() => onChange(JSON.parse(key) as RecordChangesAsked), [key, onChange]);
  return null;
}

/** What waits, in words: the identity past the correction window, a restriction lifted, the deceased status. */
function approvalWords(changes: RecordChangesAsked, correctable: boolean): string {
  const parts: string[] = [];
  if (changes.identity.length > 0 && !correctable) {
    parts.push(
      `The ${changes.identity.join(', ')}: someone who approves identity changes (medical records) decides it. Only the desk that registered a patient corrects these at once, within a day and before the patient is seen.`,
    );
  }
  if (changes.liftsRestriction) {
    parts.push('Lifting the VIP restriction: someone else who may open restricted records decides it.');
  }
  if (changes.deceased) {
    parts.push(
      'The deceased status: someone who approves identity changes decides it. A death a doctor certified is taken back only by marking its certificate entered in error.',
    );
  }
  return parts.join(' ');
}
