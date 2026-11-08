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
import { applyServerErrors, DateField, TextAreaField, TextField, useNotify, useUnsavedChanges } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch, type Control } from 'react-hook-form';
import { z } from 'zod';
import { PendingGrantNotice } from '@/modules/admin/PendingGrantNotice';
import {
  againstAdviceMissing,
  againstAdviceRequest,
  EMPTY_AGAINST_ADVICE,
  type AgainstAdviceState,
  useDoctorSignedIn,
} from '@/modules/witness/againstAdvice';
import { AgainstAdviceFields } from '@/modules/witness/AgainstAdviceFields';
import {
  admissionApi,
  DISCHARGE_TYPE_LABEL,
  DISCHARGE_TYPES,
  dischargeIsAgainstAdvice,
  dischargeNeedsClearance,
  dischargeNeedsNotes,
  dischargeWaitsForApproval,
  inpatientKeys,
  inpatientQueries,
  isApprovalRequest,
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

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: initiate.isPending, open, onClose });

  return (
    <Dialog open={open} onOpenChange={guard.onOpenChange}>
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
              <Button onClick={guard.requestClose} disabled={initiate.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                icon={initiate.isPending ? <Spinner size="tiny" aria-hidden /> : <ArrowExitRegular />}
                disabled={initiate.isPending}
              >
                Start discharge
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

const TYPE_HINT: Record<DischargeType, string> = {
  Normal: 'Routine discharge home after billing clearance.',
  Referred: 'Sent to another hospital for further care.',
  Lama: 'The patient left against medical advice: who signed the form, the doctor who explained the risks and a witness.',
  Dama: 'The family asked for discharge against advice: who signed the DAMA form, the doctor and a witness.',
  Absconded:
    'The patient left without informing staff. The nurse in charge approves closing the stay; billing clearance is skipped and the dues are recorded.',
  Expired:
    'Death. Closed with the death certificate once a second doctor confirms it; billing clearance is skipped and the dues are recorded.',
};

const completeSchema = z
  .object({
    dischargeType: z.enum(DISCHARGE_TYPES),
    earlier: z.boolean(),
    dischargedAt: z.string(),
    notes: z.string().trim().max(2000),
    notifyPatient: z.boolean(),
    lastSeenAt: z.string(),
    searchedBy: z.string().trim().max(300),
    policeInformed: z.boolean(),
    policeStation: z.string().trim().max(200),
    policeReference: z.string().trim().max(100),
    relativesInformed: z.boolean(),
    relativeInformed: z.string().trim().max(200),
    certificatePendingPostMortem: z.boolean(),
    medicoLegal: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (dischargeNeedsNotes(v.dischargeType) && !v.notes) {
      ctx.addIssue({ code: 'custom', path: ['notes'], message: 'Record who was informed and which form was signed.' });
    }
    if (v.dischargeType === 'Expired' && v.certificatePendingPostMortem && !v.notes) {
      ctx.addIssue({
        code: 'custom',
        path: ['notes'],
        message: 'Record who declared the death and the police intimation.',
      });
    }
    if (v.earlier && !fromIstInput(v.dischargedAt)) {
      ctx.addIssue({ code: 'custom', path: ['dischargedAt'], message: 'Enter the time the patient left.' });
    }
    if (v.dischargeType !== 'Absconded') {
      return;
    }
    if (!fromIstInput(v.lastSeenAt)) {
      ctx.addIssue({ code: 'custom', path: ['lastSeenAt'], message: 'Enter when the patient was last seen.' });
    }
    if (!v.searchedBy) {
      ctx.addIssue({ code: 'custom', path: ['searchedBy'], message: 'Say who searched for the patient.' });
    }
    if ((v.policeInformed || v.medicoLegal) && !v.policeStation) {
      ctx.addIssue({ code: 'custom', path: ['policeStation'], message: 'Name the police station that was told.' });
    }
    if (v.medicoLegal && !v.policeReference) {
      ctx.addIssue({
        code: 'custom',
        path: ['policeReference'],
        message: 'A medico-legal case: record the police reference (diary or GD entry).',
      });
    }
    if (v.relativesInformed && !v.relativeInformed) {
      ctx.addIssue({ code: 'custom', path: ['relativeInformed'], message: 'Name the relative who was told.' });
    }
  });

type CompleteValues = z.infer<typeof completeSchema>;

/**
 * The patient's death certificate, as the ward sees it before closing the stay as expired: none yet, waiting for a
 * second doctor's confirmation, or enough to close the stay (maker-checker, slice 8).
 */
export function DeathCertificateStatus({ admissionId }: { admissionId: string }) {
  const certificate = useQuery({ ...inpatientQueries.deathCertificate(admissionId), refetchInterval: 60_000 });
  if (certificate.isLoading) {
    return <Spinner size="tiny" label="Checking the death certificate…" labelPosition="after" />;
  }
  if (!certificate.data) {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>
          The death certificate could not be checked.{' '}
          {isApiError(certificate.error) ? certificate.error.userMessage : ''}
        </MessageBarBody>
      </MessageBar>
    );
  }
  const c = certificate.data;
  return (
    <MessageBar intent={c.settled ? 'success' : 'warning'} layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>
          {c.settled
            ? `Death certificate ${c.serialNumber ?? ''} confirmed`
            : c.serialNumber
              ? `Death certificate ${c.serialNumber} waiting for a second doctor`
              : 'No death certificate yet'}
        </MessageBarTitle>
        {c.status}
      </MessageBarBody>
      {!c.settled && (
        <MessageBarActions>
          <Button
            size="small"
            icon={<ArrowClockwiseRegular />}
            onClick={() => void certificate.refetch()}
            disabledFocusable={certificate.isFetching}
          >
            Check again
          </Button>
        </MessageBarActions>
      )}
    </MessageBar>
  );
}

/**
 * Complete the discharge. Routine types need the discharge started and Billing's clearance. By type (maker-checker,
 * slice 8): LAMA and DAMA record who signed, the doctor who explained the risks and a witness; Absconded is asked for,
 * with the report, and the nurse in charge approves; Expired closes with the death certificate a second doctor confirmed,
 * or, for a medico-legal death whose certificate waits for the post-mortem, is asked for and a second doctor approves. A
 * billing refusal is shown in the dialog with the outstanding amount, not as a passing toast.
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
  const doctorSignedIn = useDoctorSignedIn();
  const initiated = admission.status === 'DischargeInitiated';
  const [nowInput] = useState(() => toIstInput(new Date()));
  const [againstAdvice, setAgainstAdvice] = useState<AgainstAdviceState>(EMPTY_AGAINST_ADVICE);
  const [declarationProblem, setDeclarationProblem] = useState<string | null>(null);
  const form = useForm<CompleteValues>({
    resolver: zodResolver(completeSchema),
    defaultValues: {
      dischargeType: initiated ? 'Normal' : 'Expired',
      earlier: false,
      dischargedAt: nowInput,
      notes: '',
      notifyPatient: true,
      lastSeenAt: nowInput,
      searchedBy: '',
      policeInformed: admission.isMlc,
      policeStation: '',
      policeReference: '',
      relativesInformed: false,
      relativeInformed: '',
      certificatePendingPostMortem: false,
      medicoLegal: admission.isMlc,
    },
  });
  const [type, earlier, policeInformed, relativesInformed, pendingPostMortem] = useWatch({
    control: form.control,
    name: ['dischargeType', 'earlier', 'policeInformed', 'relativesInformed', 'certificatePendingPostMortem'],
  });
  const routine = dischargeNeedsClearance(type);
  const asksApproval = dischargeWaitsForApproval(type, pendingPostMortem);
  const certificate = useQuery({
    ...inpatientQueries.deathCertificate(admission.id),
    enabled: open && type === 'Expired',
  });
  const canWaitForPostMortem = admission.isMlc && certificate.data?.deathId === null;

  const complete = useMutation({
    mutationFn: (values: CompleteValues) =>
      admissionApi.completeDischarge(admission.id, {
        dischargeType: values.dischargeType,
        dischargedAt: values.earlier ? fromIstInput(values.dischargedAt) : null,
        notes: values.notes || null,
        notifyPatient: dischargeNeedsClearance(values.dischargeType) && values.notifyPatient,
        againstAdvice: dischargeIsAgainstAdvice(values.dischargeType) ? againstAdviceRequest(againstAdvice) : null,
        absconded:
          values.dischargeType === 'Absconded'
            ? {
                lastSeenAt: fromIstInput(values.lastSeenAt)!,
                searchedBy: values.searchedBy,
                policeInformed: values.policeInformed,
                policeStation: values.policeStation || null,
                policeReference: values.policeReference || null,
                relativesInformed: values.relativesInformed,
                relativeInformed: values.relativesInformed ? values.relativeInformed : null,
              }
            : null,
        certificatePendingPostMortem: values.dischargeType === 'Expired' && values.certificatePendingPostMortem,
      }),
    onSuccess: (result) => {
      if (isApprovalRequest(result)) {
        notify.success(
          'Asked for approval',
          type === 'Absconded'
            ? 'The nurse in charge decides it. Until then the patient stays admitted.'
            : 'A second doctor decides it. Until then the patient stays admitted.',
        );
      } else {
        notify.success(
          type === 'Expired' ? 'Death recorded' : 'Discharge completed',
          'The bed has been sent for cleaning.',
        );
      }
      void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
      form.reset();
      setAgainstAdvice(EMPTY_AGAINST_ADVICE);
      onClose();
    },
    onError: (error) => {
      applyServerErrors(error, form.setError);
    },
  });

  const submit = (values: CompleteValues) => {
    if (complete.isPending) {
      return;
    }
    const missing = dischargeIsAgainstAdvice(values.dischargeType)
      ? againstAdviceMissing(againstAdvice, doctorSignedIn)
      : null;
    setDeclarationProblem(missing);
    if (!missing) {
      complete.mutate(values);
    }
  };

  const error = complete.error;
  const billingPending = isApiError(error) && error.code === 'discharge.billing_pending';
  const outstanding =
    isApiError(error) && error.code === 'discharge.billing_pending'
      ? ((error.data as { outstanding?: number } | undefined)?.outstanding ?? null)
      : null;
  const close = () => {
    complete.reset();
    setAgainstAdvice(EMPTY_AGAINST_ADVICE);
    setDeclarationProblem(null);
    onClose();
  };

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: complete.isPending, open, onClose: close });

  return (
    <Dialog open={open} onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 680 }}>
        <form onSubmit={form.handleSubmit(submit)} noValidate>
          <DialogBody>
            <DialogTitle>Complete discharge of {admission.patientName}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {initiated && routine && <ClearanceStatus admissionId={admission.id} />}
              {admission.pendingDischarge && (
                <PendingGrantNotice
                  approvalId={admission.pendingDischarge.approvalId}
                  what={admission.pendingDischarge.summary}
                  requestedBy={admission.pendingDischarge.requestedBy}
                  requestedAt={admission.pendingDischarge.requestedAt}
                  decides="Closing this stay is already asked for; withdraw the request to ask again."
                />
              )}
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

              {type === 'Expired' && (
                <>
                  <DeathCertificateStatus admissionId={admission.id} />
                  {canWaitForPostMortem && (
                    <Controller
                      control={form.control}
                      name="certificatePendingPostMortem"
                      render={({ field }) => (
                        <Switch
                          checked={field.value}
                          onChange={(_, d) => field.onChange(d.checked)}
                          label="Medico-legal death: the certificate waits for the post-mortem (a second doctor approves closing the stay)"
                        />
                      )}
                    />
                  )}
                </>
              )}

              {dischargeIsAgainstAdvice(type) && (
                <AgainstAdviceFields
                  value={againstAdvice}
                  onChange={(next) => {
                    setAgainstAdvice(next);
                    setDeclarationProblem(null);
                  }}
                  patientId={admission.patientId}
                  witnessPurpose="discharge.against_advice"
                  witnessWho="a nurse or doctor of the ward"
                />
              )}

              {type === 'Absconded' && (
                <AbscondedFields
                  control={form.control}
                  max={nowInput}
                  medicoLegal={admission.isMlc}
                  police={policeInformed}
                  relatives={relativesInformed}
                />
              )}

              <TextAreaField
                control={form.control}
                name="notes"
                label={dischargeNeedsNotes(type) ? 'Who was informed and which form was signed' : 'Discharge notes'}
                required={dischargeNeedsNotes(type) || (type === 'Expired' && pendingPostMortem)}
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
              {declarationProblem && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>The declaration is not complete</MessageBarTitle>
                    {declarationProblem}
                  </MessageBarBody>
                </MessageBar>
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
              <Button onClick={guard.requestClose} disabled={complete.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                icon={complete.isPending ? <Spinner size="tiny" aria-hidden /> : <CheckmarkCircleRegular />}
                disabled={complete.isPending}
                // Keeps focus while recording (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={complete.isPending}
                style={
                  type === 'Expired' && !asksApproval
                    ? { backgroundColor: tokens.colorPaletteRedBackground3 }
                    : undefined
                }
              >
                {asksApproval ? 'Ask for approval' : type === 'Expired' ? 'Record death' : 'Complete discharge'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

/** An absconded patient's report: when last seen, who searched, and whether the police and the relatives were told. */
function AbscondedFields({
  control,
  max,
  medicoLegal,
  police,
  relatives,
}: {
  control: Control<CompleteValues>;
  max: string;
  medicoLegal: boolean;
  police: boolean;
  relatives: boolean;
}) {
  return (
    <>
      {medicoLegal && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Medico-legal case</MessageBarTitle>
            Tell the police the patient has gone, and record the station and the reference, before the stay is closed.
          </MessageBarBody>
        </MessageBar>
      )}
      <Controller
        control={control}
        name="lastSeenAt"
        render={({ field, fieldState }) => (
          <Field
            label="Last seen at"
            required
            validationMessage={fieldState.error?.message}
            validationState={fieldState.error ? 'error' : 'none'}
          >
            <Input type="datetime-local" value={field.value} max={max} onChange={(_, d) => field.onChange(d.value)} />
          </Field>
        )}
      />
      <TextField control={control} name="searchedBy" label="Who searched, and where" required maxLength={300} />
      <Controller
        control={control}
        name="policeInformed"
        render={({ field }) => (
          <Switch
            checked={field.value}
            disabled={medicoLegal}
            onChange={(_, d) => field.onChange(d.checked)}
            label="The police were told"
          />
        )}
      />
      {(police || medicoLegal) && (
        <>
          <TextField control={control} name="policeStation" label="Police station" required maxLength={200} />
          <TextField
            control={control}
            name="policeReference"
            label="Police reference (diary or GD entry)"
            required={medicoLegal}
            maxLength={100}
          />
        </>
      )}
      <Controller
        control={control}
        name="relativesInformed"
        render={({ field }) => (
          <Switch
            checked={field.value}
            onChange={(_, d) => field.onChange(d.checked)}
            label="The relatives were told"
          />
        )}
      />
      {relatives && (
        <TextField control={control} name="relativeInformed" label="Relative told" required maxLength={200} />
      )}
    </>
  );
}
