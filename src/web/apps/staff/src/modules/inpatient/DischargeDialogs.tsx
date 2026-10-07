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
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Radio,
  RadioGroup,
  Spinner,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwiseRegular, ArrowExitRegular, CheckmarkCircleRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatInr, isApiError, todayIso } from '@hc360/core';
import { applyServerErrors, DateField, TextAreaField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import {
  admissionApi,
  DISCHARGE_TYPE_LABEL,
  DISCHARGE_TYPES,
  dischargeNeedsClearance,
  dischargeNeedsNotes,
  inpatientKeys,
  inpatientQueries,
  type AdmissionDetail,
  type DischargeType,
} from './api';
import { fromIstInput, toIstInput } from './time';

/** Billing clearance for a discharge in progress, with refresh (Billing clears it from the final bill). */
export function ClearanceStatus({ admissionId }: { admissionId: string }) {
  const clearance = useQuery({ ...inpatientQueries.clearance(admissionId), refetchInterval: 60_000 });
  if (clearance.isLoading) {
    return <Spinner size="tiny" label="Checking billing clearance…" labelPosition="after" />;
  }
  if (clearance.error || !clearance.data) {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>
          Billing clearance could not be checked. {isApiError(clearance.error) ? clearance.error.userMessage : ''}
        </MessageBarBody>
        <MessageBarActions>
          <Button size="small" icon={<ArrowClockwiseRegular />} onClick={() => void clearance.refetch()}>
            Check again
          </Button>
        </MessageBarActions>
      </MessageBar>
    );
  }
  const c = clearance.data;
  return c.cleared ? (
    <MessageBar intent="success">
      <MessageBarBody>
        <MessageBarTitle>Billing cleared</MessageBarTitle>
        The final bill is settled. The discharge can be completed.
      </MessageBarBody>
    </MessageBar>
  ) : (
    <MessageBar intent="warning" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>Waiting for billing clearance</MessageBarTitle>
        {c.reason ?? `Outstanding amount ${formatInr(c.outstanding)}.`} Ask the patient's family to visit the IP billing
        counter.
      </MessageBarBody>
      <MessageBarActions>
        <Button
          size="small"
          icon={<ArrowClockwiseRegular />}
          onClick={() => void clearance.refetch()}
          disabled={clearance.isFetching}
          // Keeps focus while checking: a disabled button drops it.
          disabledFocusable={clearance.isFetching}
        >
          Check again
        </Button>
      </MessageBarActions>
    </MessageBar>
  );
}

const initiateSchema = z.object({
  expectedDischargeDate: z.string().nullable(),
  notes: z.string().trim().max(2000),
});

type InitiateValues = z.infer<typeof initiateSchema>;

/** Start the discharge: Billing prepares the final bill (all bed days up to now are posted first). */
export function InitiateDischargeDialog({
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
  const form = useForm<InitiateValues>({
    resolver: zodResolver(initiateSchema),
    defaultValues: { expectedDischargeDate: admission.expectedDischargeDate ?? todayIso(), notes: '' },
  });

  const initiate = useMutation({
    mutationFn: (values: InitiateValues) =>
      admissionApi.initiateDischarge(admission.id, {
        expectedDischargeDate: values.expectedDischargeDate,
        notes: values.notes || null,
      }),
    onSuccess: (result) => {
      notify.success(
        'Discharge started',
        result.chargesPosted > 0
          ? `${result.chargesPosted} pending bed and nursing day charges were posted for the final bill.`
          : 'Billing will prepare the final bill.',
      );
      void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
      form.reset();
      onClose();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not start the discharge');
      }
    },
  });

  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && !initiate.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form onSubmit={form.handleSubmit((values) => initiate.mutate(values))} noValidate>
          <DialogBody>
            <DialogTitle>Start discharge of {admission.patientName}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Text>
                Billing is told to prepare the final bill, and every bed and nursing day up to now is charged. The
                patient stays in the bed until the discharge is completed.
              </Text>
              <DateField
                control={form.control}
                name="expectedDischargeDate"
                label="Expected discharge date"
                min={todayIso()}
              />
              <TextAreaField
                control={form.control}
                name="notes"
                label="Discharge notes for billing and nursing"
                rows={3}
                maxLength={2000}
                placeholder="e.g. Discharge after evening dressing; TPA final approval awaited"
              />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={initiate.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                icon={initiate.isPending ? <Spinner size="tiny" /> : <ArrowExitRegular />}
                disabled={initiate.isPending}
              >
                Start discharge
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

const TYPE_HINT: Record<DischargeType, string> = {
  Normal: 'Routine discharge home after billing clearance.',
  Referred: 'Sent to another hospital for further care.',
  Lama: 'The patient left against medical advice. Record who was informed and the form signed.',
  Dama: 'The family asked for discharge against advice and signed the DAMA form.',
  Absconded: 'The patient left without informing staff. Recorded at once; no SMS is sent.',
  Expired: 'Death. Recorded at once without billing clearance; no SMS is sent.',
};

const completeSchema = z
  .object({
    dischargeType: z.enum(DISCHARGE_TYPES),
    earlier: z.boolean(),
    dischargedAt: z.string(),
    notes: z.string().trim().max(2000),
    notifyPatient: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (dischargeNeedsNotes(v.dischargeType) && !v.notes) {
      ctx.addIssue({ code: 'custom', path: ['notes'], message: 'Record who was informed and which form was signed.' });
    }
    if (v.earlier && !fromIstInput(v.dischargedAt)) {
      ctx.addIssue({ code: 'custom', path: ['dischargedAt'], message: 'Enter the time the patient left.' });
    }
  });

type CompleteValues = z.infer<typeof completeSchema>;

/**
 * Complete the discharge. Routine types need the discharge started and Billing's clearance; death and abscondence are
 * recorded immediately. A billing refusal is shown in the dialog with the outstanding amount, not as a passing toast.
 */
export function CompleteDischargeDialog({
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
  const initiated = admission.status === 'DischargeInitiated';
  const [nowInput] = useState(() => toIstInput(new Date()));
  const form = useForm<CompleteValues>({
    resolver: zodResolver(completeSchema),
    defaultValues: {
      dischargeType: initiated ? 'Normal' : 'Expired',
      earlier: false,
      dischargedAt: nowInput,
      notes: '',
      notifyPatient: true,
    },
  });
  const [type, earlier] = useWatch({ control: form.control, name: ['dischargeType', 'earlier'] });
  const routine = dischargeNeedsClearance(type);

  const complete = useMutation({
    mutationFn: (values: CompleteValues) =>
      admissionApi.completeDischarge(admission.id, {
        dischargeType: values.dischargeType,
        dischargedAt: values.earlier ? fromIstInput(values.dischargedAt) : null,
        notes: values.notes || null,
        notifyPatient: dischargeNeedsClearance(values.dischargeType) && values.notifyPatient,
      }),
    onSuccess: () => {
      notify.success(
        type === 'Expired' ? 'Death recorded' : 'Discharge completed',
        'The bed has been sent for cleaning.',
      );
      void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
      form.reset();
      onClose();
    },
    onError: (error) => {
      applyServerErrors(error, form.setError);
    },
  });

  const error = complete.error;
  const billingPending = isApiError(error) && error.code === 'discharge.billing_pending';
  const outstanding =
    isApiError(error) && error.code === 'discharge.billing_pending'
      ? ((error.data as { outstanding?: number } | undefined)?.outstanding ?? null)
      : null;
  const close = () => {
    complete.reset();
    onClose();
  };

  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && !complete.isPending && close()}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <form onSubmit={form.handleSubmit((values) => !complete.isPending && complete.mutate(values))} noValidate>
          <DialogBody>
            <DialogTitle>Complete discharge of {admission.patientName}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {initiated && routine && <ClearanceStatus admissionId={admission.id} />}
              <Controller
                control={form.control}
                name="dischargeType"
                render={({ field }) => (
                  <Field label="How did the stay end?" required>
                    <RadioGroup value={field.value} onChange={(_, d) => field.onChange(d.value)}>
                      {DISCHARGE_TYPES.map((t) => {
                        const disabled = !initiated && dischargeNeedsClearance(t);
                        return (
                          <Radio
                            key={t}
                            value={t}
                            disabled={disabled}
                            label={
                              <span>
                                {DISCHARGE_TYPE_LABEL[t]}
                                <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                                  {disabled
                                    ? 'Start the discharge first so billing can prepare the final bill.'
                                    : TYPE_HINT[t]}
                                </Caption1>
                              </span>
                            }
                          />
                        );
                      })}
                    </RadioGroup>
                  </Field>
                )}
              />
              <TextAreaField
                control={form.control}
                name="notes"
                label={dischargeNeedsNotes(type) ? 'Who was informed and which form was signed' : 'Discharge notes'}
                required={dischargeNeedsNotes(type)}
                rows={3}
                maxLength={2000}
              />
              <Controller
                control={form.control}
                name="earlier"
                render={({ field }) => (
                  <Switch
                    checked={field.value}
                    onChange={(_, d) => field.onChange(d.checked)}
                    label="The patient left earlier (within the last 48 hours)"
                  />
                )}
              />
              {earlier && (
                <Controller
                  control={form.control}
                  name="dischargedAt"
                  render={({ field, fieldState }) => (
                    <Field
                      label="Left at"
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
              {routine && (
                <Controller
                  control={form.control}
                  name="notifyPatient"
                  render={({ field }) => (
                    <Switch
                      checked={field.value}
                      onChange={(_, d) => field.onChange(d.checked)}
                      label="Send the discharge SMS to the patient"
                    />
                  )}
                />
              )}
              {billingPending && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Billing clearance is pending</MessageBarTitle>
                    {outstanding !== null ? `${formatInr(outstanding)} is still outstanding. ` : ''}
                    {isApiError(error) ? error.userMessage : ''}
                  </MessageBarBody>
                </MessageBar>
              )}
              {error && !billingPending && !(isApiError(error) && error.isValidation) && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>
                      {isApiError(error) ? error.title : 'Could not complete the discharge'}
                    </MessageBarTitle>
                    {isApiError(error) ? error.userMessage : 'Please try again.'}
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={close} disabled={complete.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                icon={complete.isPending ? <Spinner size="tiny" /> : <CheckmarkCircleRegular />}
                disabled={complete.isPending}
                // Keeps focus while recording (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={complete.isPending}
                style={type === 'Expired' ? { backgroundColor: tokens.colorPaletteRedBackground3 } : undefined}
              >
                {type === 'Expired'
                  ? 'Record death'
                  : type === 'Absconded'
                    ? 'Record abscondence'
                    : 'Complete discharge'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
