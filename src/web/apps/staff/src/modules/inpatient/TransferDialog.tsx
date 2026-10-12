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
import { applyServerErrors, FormGrid, SelectField, TextAreaField, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import { admissionApi, inpatientKeys, type AdmissionDetail, type AvailableBedDto } from './api';
import { BedPicker } from './BedPicker';
import { fromIstInput, toIstInput } from './time';

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

/** Move an admitted patient to another bed or ward (the bed tariff follows the new ward from the next census). */
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
    },
  });
  const [departmentId, earlier] = useWatch({ control: form.control, name: ['departmentId', 'earlier'] });
  const departments = useDepartments('Clinical');
  const practitioners = usePractitioners(departmentId);

  const finish = () => {
    form.reset();
    setPicked(null);
    onClose();
  };

  const transfer = useMutation({
    mutationFn: (values: TransferValues) =>
      admissionApi.transfer(admission.id, {
        toBedId: values.toBedId!,
        reason: values.reason,
        departmentId: values.departmentId !== admission.departmentId ? values.departmentId : null,
        consultantPractitionerId:
          values.consultantPractitionerId !== admission.consultantPractitionerId
            ? values.consultantPractitionerId
            : null,
        transferredAt: values.earlier ? fromIstInput(values.transferredAt) : null,
      }),
    onSuccess: () => {
      notify.success('Patient transferred', picked ? `Now in ${picked.wardName}, bed ${picked.label}.` : undefined);
      void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
      finish();
    },
    onError: (error) => {
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

  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && !transfer.isPending && close()}>
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
              {earlier && (
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
              <Button onClick={close} disabled={transfer.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                icon={transfer.isPending ? <Spinner size="tiny" aria-hidden /> : <ArrowSwapRegular />}
                disabled={transfer.isPending}
              >
                {picked ? `Transfer to ${picked.label}` : 'Transfer'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
