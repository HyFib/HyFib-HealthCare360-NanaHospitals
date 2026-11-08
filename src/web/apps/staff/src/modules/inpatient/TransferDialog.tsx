import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Switch,
  tokens,
} from '@fluentui/react-components';
import { ArrowSwapRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError } from '@hc360/core';
import { applyServerErrors, FormGrid, SelectField, TextAreaField, useNotify, useUnsavedChanges } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import { admissionApi, inpatientKeys, inpatientQueries, type AdmissionDetail, type AvailableBedDto } from './api';
import { BedPicker } from './BedPicker';
import { fromIstInput, toIstInput } from './time';
import { isCriticalCareWard, wardCareApi, wardCareKeys } from './wardCareApi';

const REASONS = [
  'Needs ICU care',
  'Step down from ICU',
  'Isolation needed',
  'Patient asked for a room change',
  'Bed needs maintenance',
  'Moved to the right speciality ward',
];

const schema = z
  .object({
    toBedId: z.string().nullable(),
    reason: z.string().trim().min(3, 'Give the reason for the transfer.').max(300),
    departmentId: z.string().nullable(),
    consultantPractitionerId: z.string().nullable(),
    earlier: z.boolean(),
    transferredAt: z.string(),
    clinicalSummary: z.string().trim().max(2000),
    urgent: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (!v.toBedId) {
      ctx.addIssue({ code: 'custom', path: ['toBedId'], message: 'Choose the bed the patient is moving to.' });
    }
    if (v.earlier && !fromIstInput(v.transferredAt)) {
      ctx.addIssue({ code: 'custom', path: ['transferredAt'], message: 'Enter when the patient moved.' });
    }
  });

type TransferValues = z.infer<typeof schema>;

/**
 * Move an admitted patient to another bed of the ward at once, or ask another ward to take them: the bed is held and the
 * receiving ward accepts (an urgent step-up or step-down to or from critical care moves at once and is accepted within
 * the hour). The bed tariff follows the new ward from the next census.
 */
export function TransferDialog({
  admission,
  open,
  onClose,
}: {
  admission: AdmissionDetail;
  open: boolean;
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [picked, setPicked] = useState<AvailableBedDto | null>(null);
  const [nowInput] = useState(() => toIstInput(new Date()));
  const form = useForm<TransferValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      toBedId: null,
      reason: '',
      departmentId: admission.departmentId,
      consultantPractitionerId: admission.consultantPractitionerId,
      earlier: false,
      transferredAt: nowInput,
      clinicalSummary: '',
      urgent: false,
    },
  });
  const [departmentId, earlier, urgent] = useWatch({
    control: form.control,
    name: ['departmentId', 'earlier', 'urgent'],
  });
  const wards = useQuery(inpatientQueries.wards());
  const currentWardType = wards.data?.find((w) => w.id === admission.wardId)?.type ?? null;
  // A bed of another ward is asked of the receiving ward; a bed of the same ward is a direct move.
  const otherWard = picked !== null && picked.wardId !== admission.wardId;
  const urgentAllowed = otherWard && (isCriticalCareWard(picked.wardType) || isCriticalCareWard(currentWardType));
  const departments = useDepartments('Clinical');
  const practitioners = usePractitioners(departmentId);

  const finish = () => {
    form.reset();
    setPicked(null);
    onClose();
  };

  const transfer = useMutation({
    mutationFn: async (values: TransferValues) => {
      if (otherWard) {
        if (values.clinicalSummary.trim().length < 3) {
          form.setError('clinicalSummary', { message: 'Write a summary for the receiving ward.' });
          throw new Error('summary');
        }
        const asked = await wardCareApi.requestTransfer(admission.id, {
          toBedId: values.toBedId!,
          reason: values.reason,
          clinicalSummary: values.clinicalSummary,
          departmentId: values.departmentId !== admission.departmentId ? values.departmentId : null,
          consultantPractitionerId:
            values.consultantPractitionerId !== admission.consultantPractitionerId
              ? values.consultantPractitionerId
              : null,
          isUrgent: urgentAllowed && values.urgent,
        });
        return asked.status;
      }
      await admissionApi.transfer(admission.id, {
        toBedId: values.toBedId!,
        reason: values.reason,
        departmentId: values.departmentId !== admission.departmentId ? values.departmentId : null,
        consultantPractitionerId:
          values.consultantPractitionerId !== admission.consultantPractitionerId
            ? values.consultantPractitionerId
            : null,
        transferredAt: values.earlier ? fromIstInput(values.transferredAt) : null,
      });
      return 'Moved' as const;
    },
    onSuccess: (outcome) => {
      if (outcome === 'Pending') {
        notify.success(
          'Transfer requested',
          picked ? `${picked.wardName} is asked to accept; bed ${picked.label} is held.` : undefined,
        );
      } else if (outcome === 'MovedAwaitingAcceptance') {
        notify.success('Moved as urgent', 'The receiving ward records its acceptance within the hour.');
      } else {
        notify.success('Patient transferred', picked ? `Now in ${picked.wardName}, bed ${picked.label}.` : undefined);
      }
      void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
      void queryClient.invalidateQueries({ queryKey: wardCareKeys.all });
      finish();
    },
    onError: (error) => {
      if (error instanceof Error && error.message === 'summary') {
        return;
      }
      if (isApiError(error) && (error.code === 'bed.taken' || error.code === 'bed.unavailable')) {
        void queryClient.invalidateQueries({ queryKey: ['inpatient', 'available'] });
        form.setValue('toBedId', null);
        setPicked(null);
      }
      applyServerErrors(error, form.setError);
    },
  });

  const close = () => {
    transfer.reset();
    finish();
  };

  const serverError =
    transfer.error && !(isApiError(transfer.error) && transfer.error.isValidation) ? transfer.error : null;

  const guard = useUnsavedChanges({
    dirty: form.formState.isDirty || picked !== null,
    busy: transfer.isPending,
    open,
    onClose: close,
  });

  return (
    <Dialog open={open} onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 960, width: '100%' }}>
        <form onSubmit={form.handleSubmit((values) => transfer.mutate(values))} noValidate>
          <DialogBody>
            <DialogTitle>
              Transfer {admission.patientName}
              <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                Now in {admission.wardName ?? 'no ward'}
                {admission.bedLabel ? `, bed ${admission.bedLabel}` : ''}
              </Caption1>
            </DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
              <Controller
                control={form.control}
                name="toBedId"
                render={({ field, fieldState }) => (
                  <BedPicker
                    label="Move to bed"
                    patientId={admission.patientId}
                    excludeBedId={admission.bedId}
                    value={field.value}
                    onChange={(bed) => {
                      setPicked(bed);
                      field.onChange(bed?.bedId ?? null);
                    }}
                    validationMessage={fieldState.error?.message}
                  />
                )}
              />
              <div>
                <TextAreaField
                  control={form.control}
                  name="reason"
                  label="Reason for transfer"
                  required
                  rows={2}
                  maxLength={300}
                />
                <div
                  role="group"
                  aria-label="Common reasons"
                  style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginTop: 6 }}
                >
                  {REASONS.map((reason) => (
                    <Button
                      key={reason}
                      size="small"
                      shape="circular"
                      appearance="outline"
                      onClick={() => form.setValue('reason', reason, { shouldValidate: true })}
                    >
                      {reason}
                    </Button>
                  ))}
                </div>
              </div>
              {otherWard && (
                <>
                  <MessageBar intent="info" layout="multiline">
                    <MessageBarBody>
                      Moving to another ward: {picked?.wardName} is asked to accept the patient, and bed {picked?.label}{' '}
                      is held until it decides.
                    </MessageBarBody>
                  </MessageBar>
                  <TextAreaField
                    control={form.control}
                    name="clinicalSummary"
                    label="Clinical summary for the receiving ward"
                    required
                    rows={3}
                    maxLength={2000}
                    placeholder="Diagnosis, condition now, lines and drains, what is pending"
                  />
                  {urgentAllowed && (
                    <Controller
                      control={form.control}
                      name="urgent"
                      render={({ field }) => (
                        <Switch
                          checked={field.value}
                          onChange={(_, d) => field.onChange(d.checked)}
                          label="Urgent step-up or step-down: move now, the receiving ward accepts within the hour"
                        />
                      )}
                    />
                  )}
                </>
              )}
              <FormGrid columns={2}>
                <SelectField
                  control={form.control}
                  name="departmentId"
                  label="Department"
                  placeholder="Keep current department"
                  options={toOptions(departments.data)}
                />
                <SelectField
                  control={form.control}
                  name="consultantPractitionerId"
                  label="Consultant in charge"
                  placeholder="Keep current consultant"
                  options={toOptions(practitioners.data)}
                />
              </FormGrid>
              {!otherWard && (
                <Controller
                  control={form.control}
                  name="earlier"
                  render={({ field }) => (
                    <Switch
                      checked={field.value}
                      onChange={(_, d) => field.onChange(d.checked)}
                      label="The patient moved earlier (within the last 24 hours)"
                    />
                  )}
                />
              )}
              {earlier && !otherWard && (
                <Controller
                  control={form.control}
                  name="transferredAt"
                  render={({ field, fieldState }) => (
                    <Field
                      label="Moved at"
                      required
                      validationMessage={fieldState.error?.message}
                      validationState={fieldState.error ? 'error' : 'none'}
                    >
                      <Input
                        type="datetime-local"
                        value={field.value}
                        max={nowInput}
                        onChange={(_, d) => field.onChange(d.value)}
                      />
                    </Field>
                  )}
                />
              )}
              {serverError !== null && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>
                      {isApiError(serverError) ? serverError.title : 'Could not transfer'}
                    </MessageBarTitle>
                    {isApiError(serverError) ? serverError.userMessage : 'Please try again.'}
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={transfer.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                icon={transfer.isPending ? <Spinner size="tiny" aria-hidden /> : <ArrowSwapRegular />}
                disabled={transfer.isPending}
              >
                {picked
                  ? otherWard
                    ? urgent && urgentAllowed
                      ? `Move now to ${picked.label}`
                      : `Ask ${picked.wardName} to accept`
                    : `Transfer to ${picked.label}`
                  : 'Transfer'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
