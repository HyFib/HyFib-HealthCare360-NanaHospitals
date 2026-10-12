import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowDownloadRegular,
  DismissRegular,
  DocumentArrowLeftRegular,
  EditRegular,
  SaveRegular,
  SignatureRegular,
} from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, formatRelative, isApiError, useHotkey } from '@hc360/core';
import { ConfirmDialog, DateField, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch, type Control } from 'react-hook-form';
import { z } from 'zod';
import { DocumentButtons } from '@/lib/DocumentButtons';
import { usePermission } from '@/lib/permissions';
import { useFormDraft } from '@/lib/useFormDraft';
import { IcdCodePicker } from '@/modules/mrd/CodePicker';
import {
  dischargeSummaryDocument,
  emrApi,
  emrKeys,
  type DischargeContent,
  type DischargeSummaryDto,
  type DischargeSummaryPrefill,
  type PatientAdmissionItem,
} from './api';
import { AmendDialog } from './EncounterNote';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '16px' },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(min(100%, 420px), 1fr))', gap: '16px' },
  codes: { display: 'flex', gap: '6px', flexWrap: 'wrap', alignItems: 'center' },
  bar: {
    position: 'sticky',
    bottom: 0,
    display: 'flex',
    justifyContent: 'flex-end',
    alignItems: 'center',
    gap: '8px',
    flexWrap: 'wrap',
    padding: '12px 16px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow8,
    borderRadius: tokens.borderRadiusLarge,
    zIndex: 1,
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

const schema = z.object({
  diagnoses: z.string().max(4000),
  diagnosisCodes: z.array(z.string().max(16)).max(20, 'At most 20 diagnosis codes.'),
  presentingComplaints: z.string().max(4000),
  courseInHospital: z.string().max(16000),
  procedures: z.string().max(4000),
  investigationsSummary: z.string().max(8000),
  conditionAtDischarge: z.string().max(2000),
  medications: z.string().max(4000),
  advice: z.string().max(4000),
  followUpDate: z.string().nullable(),
  followUpInstructions: z.string().max(1000),
});

type DischargeValues = z.infer<typeof schema>;

type TextKey = Exclude<keyof DischargeValues, 'diagnosisCodes' | 'followUpDate'>;

const FIELDS: { key: TextKey; label: string; rows: number; max: number; hint?: string }[] = [
  { key: 'diagnoses', label: 'Final diagnosis', rows: 3, max: 4000 },
  { key: 'presentingComplaints', label: 'Presenting complaints', rows: 3, max: 4000 },
  {
    key: 'courseInHospital',
    label: 'Course in hospital',
    rows: 8,
    max: 16000,
    hint: 'Summary of treatment and response, in plain words',
  },
  { key: 'procedures', label: 'Procedures and surgeries', rows: 3, max: 4000 },
  { key: 'investigationsSummary', label: 'Key investigations', rows: 5, max: 8000 },
  { key: 'conditionAtDischarge', label: 'Condition at discharge', rows: 2, max: 2000 },
  {
    key: 'medications',
    label: 'Medicines on discharge',
    rows: 5,
    max: 4000,
    hint: 'Name, dose, how often and for how long',
  },
  { key: 'advice', label: 'Advice', rows: 4, max: 4000, hint: 'Diet, activity, wound care and warning signs' },
  { key: 'followUpInstructions', label: 'Follow-up instructions', rows: 2, max: 1000 },
];

function toValues(summary: DischargeSummaryDto | null): DischargeValues {
  return {
    diagnoses: summary?.diagnoses ?? '',
    diagnosisCodes: summary?.diagnosisCodes ?? [],
    presentingComplaints: summary?.presentingComplaints ?? '',
    courseInHospital: summary?.courseInHospital ?? '',
    procedures: summary?.procedures ?? '',
    investigationsSummary: summary?.investigationsSummary ?? '',
    conditionAtDischarge: summary?.conditionAtDischarge ?? '',
    medications: summary?.medications ?? '',
    advice: summary?.advice ?? '',
    followUpDate: summary?.followUpDate ?? null,
    followUpInstructions: summary?.followUpInstructions ?? '',
  };
}

function toContent(values: DischargeValues): DischargeContent {
  const t = (value: string) => value.trim() || null;
  return {
    diagnoses: t(values.diagnoses),
    diagnosisCodes: values.diagnosisCodes,
    presentingComplaints: t(values.presentingComplaints),
    courseInHospital: t(values.courseInHospital),
    procedures: t(values.procedures),
    investigationsSummary: t(values.investigationsSummary),
    conditionAtDischarge: t(values.conditionAtDischarge),
    medications: t(values.medications),
    advice: t(values.advice),
    followUpDate: values.followUpDate || null,
    followUpInstructions: t(values.followUpInstructions),
  };
}

/** Prefill only fills what is still empty; the doctor's text is never overwritten. */
function mergePrefill(values: DischargeValues, prefill: DischargeSummaryPrefill): DischargeValues {
  const fill = (current: string, incoming: string | null) => (current.trim() ? current : (incoming ?? ''));
  return {
    ...values,
    diagnoses: fill(values.diagnoses, prefill.diagnoses),
    diagnosisCodes: values.diagnosisCodes.length > 0 ? values.diagnosisCodes : prefill.diagnosisCodes,
    presentingComplaints: fill(values.presentingComplaints, prefill.presentingComplaints),
    procedures: fill(values.procedures, prefill.procedures),
    investigationsSummary: fill(values.investigationsSummary, prefill.investigationsSummary),
    medications: fill(values.medications, prefill.medications),
    followUpDate: values.followUpDate ?? prefill.followUpDate,
    followUpInstructions: fill(values.followUpInstructions, prefill.followUpInstructions),
  };
}

function TextSection({
  control,
  name,
  label,
  rows,
  max,
  hint,
  disabled,
}: {
  control: Control<DischargeValues>;
  name: TextKey;
  label: string;
  rows: number;
  max: number;
  hint?: string;
  disabled: boolean;
}) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => (
        <Field
          label={label}
          hint={hint}
          validationMessage={fieldState.error?.message}
          validationState={fieldState.error ? 'error' : 'none'}
        >
          <Textarea {...field} rows={rows} resize="vertical" maxLength={max} disabled={disabled} />
        </Field>
      )}
    />
  );
}

/**
 * Discharge summary of one admission: prefilled from the structured record (diagnoses, procedures, investigations,
 * discharge medicines), kept on this computer while typing, signed by the consultant and amended with a reason. The
 * saved summary prints, or downloads as a PDF, from the server: the same document the patient receives.
 */
export function DischargeSummaryEditor({
  patientId,
  admissionId,
  admission,
  summary,
}: {
  patientId: string;
  admissionId: string;
  admission: PatientAdmissionItem | undefined;
  summary: DischargeSummaryDto | null;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canWrite = usePermission('clinical.discharge_summary.write');
  const [current, setCurrent] = useState<DischargeSummaryDto | null>(summary);
  const [defaults] = useState<DischargeValues>(() => toValues(summary));
  const form = useForm<DischargeValues>({ resolver: zodResolver(schema), defaultValues: defaults });
  const draft = useFormDraft(form, `emr-discharge:${admissionId}`);
  const codes = useWatch({ control: form.control, name: 'diagnosisCodes' });
  const [amending, setAmending] = useState(false);
  const [askAmendReason, setAskAmendReason] = useState(false);
  const [confirmSign, setConfirmSign] = useState(false);
  const signedOrAmended = current !== null && current.status !== 'Draft';
  const locked = !canWrite || (signedOrAmended && !amending);
  const { isDirty } = form.formState;

  const publish = (dto: DischargeSummaryDto, values: DischargeValues) => {
    setCurrent(dto);
    queryClient.setQueryData(emrKeys.discharge(admissionId), dto);
    form.reset(values);
    draft.discard();
    void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
  };

  const conflict = (error: unknown) => {
    if (isApiError(error) && error.isConflict) {
      notify.warning(
        'This summary was changed in another window',
        'Your text is kept here. Reload the page to see the other version before saving again.',
      );
      return true;
    }
    return false;
  };

  const saveDraft = async (values: DischargeValues) =>
    emrApi.saveDischargeSummary(admissionId, {
      patientId,
      encounterId: current?.encounterId ?? null,
      practitionerId: current?.practitionerId ?? null,
      admittedAt: current?.admittedAt ?? admission?.admittedAt ?? null,
      dischargedAt: current?.dischargedAt ?? admission?.dischargedAt ?? null,
      version: current?.version ?? null,
      ...toContent(values),
    });

  const save = useMutation({
    mutationFn: async () => {
      if (!(await form.trigger())) {
        return null;
      }
      const values = form.getValues();
      return { dto: await saveDraft(values), values };
    },
    onSuccess: (result) => {
      if (result) {
        publish(result.dto, result.values);
        notify.success('Draft saved');
      }
    },
    onError: (error) => !conflict(error) && notify.error(error, 'Could not save the discharge summary'),
  });

  const prefill = useMutation({
    mutationFn: () => emrApi.prefillDischargeSummary(admissionId, patientId),
    onSuccess: (data) => {
      form.reset(mergePrefill(form.getValues(), data), { keepDefaultValues: true });
      notify.success('Filled from the record', 'Only empty sections were filled. Review everything before signing.');
    },
    onError: (error) => notify.error(error, 'Could not fetch the admission details'),
  });

  const sign = useMutation({
    mutationFn: async () => {
      if (!(await form.trigger())) {
        return null;
      }
      const values = form.getValues();
      const saved = !current || isDirty ? await saveDraft(values) : current;
      return { dto: await emrApi.signDischargeSummary(saved.id, saved.version), values };
    },
    onSuccess: (result) => {
      setConfirmSign(false);
      if (result) {
        publish(result.dto, result.values);
        notify.success('Discharge summary signed', 'The medical records chart is updated.');
      }
    },
    onError: (error) => {
      setConfirmSign(false);
      if (!conflict(error)) {
        notify.error(error, 'Could not sign the discharge summary');
      }
    },
  });

  const amend = useMutation({
    mutationFn: async (reason: string) => {
      if (!current || !(await form.trigger())) {
        return null;
      }
      const values = form.getValues();
      return {
        dto: await emrApi.amendDischargeSummary(current.id, reason, toContent(values), current.version),
        values,
      };
    },
    onSuccess: (result) => {
      setAskAmendReason(false);
      if (result) {
        publish(result.dto, result.values);
        setAmending(false);
        notify.success('Amendment recorded');
      }
    },
    onError: (error) => !conflict(error) && notify.error(error, 'Could not record the amendment'),
  });

  useHotkey('mod+s', () => !locked && !signedOrAmended && !save.isPending && save.mutate(), { allowInInputs: true });

  const restorable =
    draft.saved && JSON.stringify(toContent(draft.saved.value)) !== JSON.stringify(toContent(defaults));

  return (
    <div className={styles.stack}>
      {restorable && draft.saved && (
        <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
          <MessageBarBody>
            Unsaved changes from {formatRelative(new Date(draft.saved.savedAt).toISOString())} were found on this
            computer.
          </MessageBarBody>
          <MessageBarActions>
            <Button onClick={draft.restore}>Restore them</Button>
            <Button appearance="subtle" onClick={draft.discard}>
              Discard
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}
      {current && signedOrAmended && (
        <MessageBar intent={amending ? 'warning' : 'success'} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>
              {amending ? 'Amending a signed summary' : current.status === 'Amended' ? 'Signed and amended' : 'Signed'}
            </MessageBarTitle>
            Signed {formatDateTime(current.signedAt)}
            {current.amendedAt
              ? ` · last amended ${formatDateTime(current.amendedAt)}: ${current.amendmentReason ?? ''}`
              : ''}
            {current.amendmentCount > 1 ? ` (${current.amendmentCount} amendments)` : ''}.
            {amending ? ' Make the corrections, then record the amendment with a reason.' : ''}
          </MessageBarBody>
        </MessageBar>
      )}

      <Section
        title="Diagnosis"
        actions={
          !locked && (
            <Button
              icon={prefill.isPending ? <Spinner size="tiny" aria-hidden /> : <ArrowDownloadRegular />}
              disabled={prefill.isPending}
              onClick={() => prefill.mutate()}
            >
              Fill from the record
            </Button>
          )
        }
      >
        <div className={styles.stack}>
          <TextSection
            control={form.control}
            name="diagnoses"
            label="Final diagnosis"
            rows={3}
            max={4000}
            disabled={locked}
          />
          <div>
            <Caption1 className={styles.muted}>ICD-10 codes</Caption1>
            <div className={styles.codes}>
              {codes.length === 0 && <Text className={styles.muted}>No codes yet.</Text>}
              {codes.map((code) => (
                <Badge key={code} appearance="tint" size="large">
                  {code}
                  {!locked && (
                    <Button
                      size="small"
                      appearance="transparent"
                      icon={<DismissRegular />}
                      aria-label={`Remove ${code}`}
                      onClick={() =>
                        form.setValue(
                          'diagnosisCodes',
                          codes.filter((c) => c !== code),
                          { shouldDirty: true },
                        )
                      }
                    />
                  )}
                </Badge>
              ))}
            </div>
          </div>
          {!locked && codes.length < 20 && (
            <IcdCodePicker
              label="Add ICD-10 code"
              onPick={(code) => {
                if (!codes.includes(code.code)) {
                  form.setValue('diagnosisCodes', [...codes, code.code], { shouldDirty: true });
                }
              }}
            />
          )}
        </div>
      </Section>

      <div className={styles.grid}>
        {FIELDS.filter((f) => f.key !== 'diagnoses').map((f) => (
          <Section key={f.key}>
            <TextSection
              control={form.control}
              name={f.key}
              label={f.label}
              rows={f.rows}
              max={f.max}
              hint={f.hint}
              disabled={locked}
            />
            {f.key === 'followUpInstructions' && (
              <DateField control={form.control} name="followUpDate" label="Follow-up date" disabled={locked} />
            )}
          </Section>
        ))}
      </div>

      <div className={styles.bar}>
        {current && (
          <StatusBadge
            status={current.status === 'Draft' ? 'Draft' : 'Signed'}
            label={current.status === 'Amended' ? 'Amended' : undefined}
          />
        )}
        {!current && <Caption1 className={styles.muted}>Not saved yet</Caption1>}
        {isDirty && <Caption1 className={styles.muted}>Unsaved changes (kept on this computer)</Caption1>}
        {current && (
          <DocumentButtons path={dischargeSummaryDocument(current.id)} printLabel="Print (A4)" disabled={isDirty} />
        )}
        {canWrite && !signedOrAmended && (
          <>
            <Button
              icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
              disabled={save.isPending || sign.isPending}
              onClick={() => save.mutate()}
            >
              Save draft (Ctrl+S)
            </Button>
            <Button
              appearance="primary"
              icon={<SignatureRegular />}
              disabled={sign.isPending || save.isPending}
              onClick={() => setConfirmSign(true)}
            >
              Sign
            </Button>
          </>
        )}
        {canWrite && signedOrAmended && !amending && (
          <Button icon={<EditRegular />} onClick={() => setAmending(true)}>
            Amend
          </Button>
        )}
        {amending && (
          <>
            <Button
              onClick={() => {
                form.reset(toValues(current));
                setAmending(false);
              }}
            >
              Cancel amendment
            </Button>
            <Button
              appearance="primary"
              icon={<SignatureRegular />}
              disabled={!isDirty || amend.isPending}
              onClick={() => setAskAmendReason(true)}
            >
              Record amendment
            </Button>
          </>
        )}
      </div>

      <ConfirmDialog
        open={confirmSign}
        title="Sign the discharge summary?"
        confirmLabel="Sign"
        busy={sign.isPending}
        onConfirm={() => sign.mutate()}
        onCancel={() => setConfirmSign(false)}
      >
        Signing makes this the official summary given to the patient and closes the medical-records deficiency. Later
        corrections are recorded as amendments with a reason.
      </ConfirmDialog>
      <AmendDialog
        open={askAmendReason}
        busy={amend.isPending}
        onConfirm={(reason) => amend.mutate(reason)}
        onCancel={() => setAskAmendReason(false)}
      />
    </div>
  );
}
