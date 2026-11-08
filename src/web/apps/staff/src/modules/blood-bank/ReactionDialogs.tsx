import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { HeartPulseWarningRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime } from '@hc360/core';
import {
  ChoiceField,
  FormGrid,
  FullRow,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { fromIstInput, nowIstInput } from '@/modules/ot/shared/datetime';
import { applyFormErrors } from '@/modules/ot/shared/errors';
import { ActionError } from '@/modules/ot/shared/feedback';
import { CheckboxGroupField, DateTimeField } from '@/modules/ot/shared/fields';
import {
  bloodApi,
  bloodKeys,
  REACTION_IMPUTABILITIES,
  REACTION_SEVERITIES,
  REACTION_SYMPTOMS,
  REACTION_TYPES,
  type BloodIssueDto,
  type TransfusionReactionDto,
} from './api';
import { imputabilityLabel, reactionTypeLabel, severityLabel, symptomLabel } from './labels';
import { haemovigilanceReportable, reactionLowers } from './rules';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const reportSchema = z
  .object({
    onsetAt: z.string().nullable(),
    symptoms: z.array(z.enum(REACTION_SYMPTOMS)).min(1, 'Select the signs and symptoms observed.'),
    vitals: z.string().trim().max(500),
    transfusionStopped: z.boolean(),
    volumeTransfusedMl: z.number().nullable(),
    actionsTaken: z.string().trim().max(2000),
    severity: z.enum(REACTION_SEVERITIES),
  })
  .superRefine((v, ctx) => {
    const onset = fromIstInput(v.onsetAt);
    if (!onset) {
      ctx.addIssue({ code: 'custom', path: ['onsetAt'], message: 'Enter when the reaction started.' });
    } else if (Date.parse(onset) > Date.now() + 5 * 60_000) {
      ctx.addIssue({ code: 'custom', path: ['onsetAt'], message: 'The time cannot be in the future.' });
    }
    if (v.volumeTransfusedMl !== null && (v.volumeTransfusedMl < 0 || v.volumeTransfusedMl > 1000)) {
      ctx.addIssue({ code: 'custom', path: ['volumeTransfusedMl'], message: '0 to 1000 ml.' });
    }
  });

type ReportValues = z.infer<typeof reportSchema>;

/** Reports an adverse transfusion reaction from the ward; blood bank officers are alerted at once. */
export function ReportReactionDialog({
  issue,
  patientName,
  onClose,
}: {
  issue: BloodIssueDto;
  patientName: string;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<ReportValues>({
    resolver: zodResolver(reportSchema),
    defaultValues: {
      onsetAt: nowIstInput(),
      symptoms: [],
      vitals: '',
      transfusionStopped: true,
      volumeTransfusedMl: null,
      actionsTaken: '',
      severity: 'Mild',
    },
  });
  const report = useMutation({
    mutationFn: (v: ReportValues) =>
      bloodApi.reportReaction(issue.id, {
        onsetAt: fromIstInput(v.onsetAt) ?? '',
        symptoms: v.symptoms,
        vitals: v.vitals || null,
        transfusionStopped: v.transfusionStopped,
        volumeTransfusedMl: v.volumeTransfusedMl,
        actionsTaken: v.actionsTaken || null,
        severity: v.severity,
      }),
    onSuccess: () => {
      notify.success('Reaction reported', 'The blood bank has been alerted to investigate.');
      void queryClient.invalidateQueries({ queryKey: ['blood-bank', 'reactions'] });
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: report.isPending, onClose });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 820 }}>
        <form onSubmit={form.handleSubmit((v) => report.mutate(v))} noValidate>
          <DialogBody>
            <DialogTitle>
              Report a transfusion reaction
              <Caption1 block className={styles.muted}>
                {patientName} · unit {issue.unitNumber} · issued {formatDateTime(issue.issuedAt)}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.body}>
              <MessageBar intent="error" layout="multiline" icon={<HeartPulseWarningRegular />}>
                <MessageBarBody>
                  <MessageBarTitle>First: stop the transfusion</MessageBarTitle>
                  Keep the line open with normal saline, check the patient’s identity against the unit label, inform the
                  doctor, and send the bag with a fresh blood and urine sample to the blood bank.
                </MessageBarBody>
              </MessageBar>
              <FormGrid columns={3}>
                <DateTimeField control={form.control} name="onsetAt" label="Reaction started" required />
                <NumberField
                  control={form.control}
                  name="volumeTransfusedMl"
                  label="Volume transfused (ml)"
                  min={0}
                  max={1000}
                />
                <SwitchField control={form.control} name="transfusionStopped" label="Transfusion stopped" />
                <FullRow>
                  <CheckboxGroupField
                    control={form.control}
                    name="symptoms"
                    label="Signs and symptoms"
                    required
                    layout="columns"
                    options={REACTION_SYMPTOMS.map((s) => ({ value: s, label: symptomLabel[s] }))}
                  />
                </FullRow>
                <FullRow>
                  <TextField
                    control={form.control}
                    name="vitals"
                    label="Vital signs at the time"
                    maxLength={500}
                    placeholder="e.g. T 39.2 °C, P 124, BP 86/50, SpO₂ 91%"
                  />
                </FullRow>
                <FullRow>
                  <TextAreaField
                    control={form.control}
                    name="actionsTaken"
                    label="Actions taken"
                    rows={2}
                    maxLength={2000}
                  />
                </FullRow>
                <FullRow>
                  <ChoiceField
                    control={form.control}
                    name="severity"
                    label="Severity"
                    options={REACTION_SEVERITIES.map((s) => ({ value: s, label: severityLabel[s] }))}
                  />
                </FullRow>
              </FormGrid>
              <ActionError error={report.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={report.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={report.isPending}
                icon={report.isPending ? <Spinner size="tiny" aria-hidden /> : <HeartPulseWarningRegular />}
              >
                Report reaction
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

const closeSchema = z.object({
  // `: boolean` keeps TypeScript from inferring a type predicate, which would narrow the output type away from the form's input type.
  type: z.enum(REACTION_TYPES).refine((t): boolean => t !== 'Unclassified', 'Classify the reaction.'),
  severity: z.enum(REACTION_SEVERITIES),
  imputability: z
    .enum(REACTION_IMPUTABILITIES)
    .refine((i): boolean => i !== 'NotAssessed', 'Assess the imputability before closing the investigation.'),
  findings: z.string().trim().min(1, 'Record the investigation findings.').max(4000),
  reportedToHaemovigilance: z.boolean(),
});

type CloseValues = z.infer<typeof closeSchema>;

/** Closes the investigation with the Haemovigilance Programme of India classification. */
export function CloseReactionDialog({ reaction, onClose }: { reaction: TransfusionReactionDto; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<CloseValues>({
    resolver: zodResolver(closeSchema),
    defaultValues: {
      type: reaction.type === 'Unclassified' ? 'FebrileNonHaemolytic' : reaction.type,
      severity: reaction.severity,
      imputability: reaction.imputability === 'NotAssessed' ? 'Probable' : reaction.imputability,
      findings: reaction.investigationFindings ?? '',
      reportedToHaemovigilance: reaction.reportedToHaemovigilance,
    },
  });
  const [closeSeverity, closeImputability] = useWatch({ control: form.control, name: ['severity', 'imputability'] });
  const lowered = reactionLowers(reaction.severity, reaction.imputability, closeSeverity, closeImputability);
  const close = useMutation({
    mutationFn: (v: CloseValues) => bloodApi.closeReaction(reaction.id, v),
    onSuccess: () => {
      notify.success('Investigation closed');
      void queryClient.invalidateQueries({ queryKey: bloodKeys.all });
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: close.isPending, onClose });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 680 }}>
        <form onSubmit={form.handleSubmit((v) => close.mutate(v))} noValidate>
          <DialogBody>
            <DialogTitle>
              Close the investigation
              <Caption1 block className={styles.muted}>
                {reaction.patientName} · unit {reaction.unitNumber} · reported {formatDateTime(reaction.reportedAt)}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.body}>
              <FormGrid columns={2}>
                <SelectField
                  control={form.control}
                  name="type"
                  label="Classification (HvPI)"
                  required
                  options={REACTION_TYPES.filter((t) => t !== 'Unclassified').map((t) => ({
                    value: t,
                    label: reactionTypeLabel[t],
                  }))}
                />
                <SelectField
                  control={form.control}
                  name="severity"
                  label="Severity"
                  options={REACTION_SEVERITIES.map((s) => ({ value: s, label: severityLabel[s] }))}
                />
                <SelectField
                  control={form.control}
                  name="imputability"
                  label="Imputability"
                  required
                  options={REACTION_IMPUTABILITIES.filter((i) => i !== 'NotAssessed').map((i) => ({
                    value: i,
                    label: imputabilityLabel[i],
                  }))}
                />
                {lowered.length > 0 && (
                  <FullRow>
                    <MessageBar intent="warning">
                      <MessageBarBody>
                        <MessageBarTitle>This lowers the reaction</MessageBarTitle>
                        {lowered.join('; ')}. Lowering waits for the blood bank medical officer: record the
                        classification with your reason first, and close once it is approved.
                      </MessageBarBody>
                    </MessageBar>
                  </FullRow>
                )}
                <FullRow>
                  <TextAreaField
                    control={form.control}
                    name="findings"
                    label="Investigation findings"
                    required
                    rows={5}
                    maxLength={4000}
                    placeholder="Clerical check, repeat grouping, DAT, culture, conclusion"
                  />
                </FullRow>
                <FullRow>
                  <SwitchField
                    control={form.control}
                    name="reportedToHaemovigilance"
                    label="Reported to the Haemovigilance Programme of India"
                  />
                </FullRow>
              </FormGrid>
              <ActionError error={close.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={close.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={close.isPending}
                icon={close.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Close investigation
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

const classifySchema = z.object({
  type: z.enum(REACTION_TYPES),
  severity: z.enum(REACTION_SEVERITIES),
  imputability: z.enum(REACTION_IMPUTABILITIES),
  reason: z.string().trim().max(1000),
});

type ClassifyValues = z.infer<typeof classifySchema>;

/**
 * The blood bank's classification while it investigates (maker-checker, slice 15). Raising the severity or imputability,
 * or a first assessment, is made at once; lowering either, which can take the reaction out of haemovigilance reporting,
 * needs the reason and waits for the blood bank medical officer, never the officer who asked.
 */
export function ClassifyReactionDialog({
  reaction,
  onClose,
}: {
  reaction: TransfusionReactionDto;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<ClassifyValues>({
    resolver: zodResolver(classifySchema),
    defaultValues: {
      type: reaction.type,
      severity: reaction.severity,
      imputability: reaction.imputability,
      reason: '',
    },
  });
  const [askedSeverity, askedImputability] = useWatch({ control: form.control, name: ['severity', 'imputability'] });
  const lowered = reactionLowers(reaction.severity, reaction.imputability, askedSeverity, askedImputability);
  const leavesReporting =
    haemovigilanceReportable(reaction.imputability) && !haemovigilanceReportable(askedImputability);
  const save = useMutation({
    mutationFn: (v: ClassifyValues) =>
      bloodApi.classifyReaction(reaction.id, {
        ...v,
        reason: v.reason.length > 0 ? v.reason : null,
        version: reaction.version,
      }),
    onSuccess: (asked) => {
      if (asked) {
        notify.success('Lowering asked for', `${asked.summary}. It waits for the blood bank medical officer.`);
      } else {
        notify.success('Classification recorded');
      }

      void queryClient.invalidateQueries({ queryKey: bloodKeys.all });
      onClose();
    },
    onError: (error) => applyFormErrors(error, form.setError),
  });

  const submit = (v: ClassifyValues) => {
    if (lowered.length > 0 && v.reason.length < 5) {
      form.setError('reason', { message: 'Say why the reaction is lowered: the findings that support it.' });
      return;
    }

    save.mutate(v);
  };

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <form onSubmit={form.handleSubmit(submit)} noValidate>
          <DialogBody>
            <DialogTitle>
              Classify the reaction
              <Caption1 block className={styles.muted}>
                {reaction.patientName} · unit {reaction.unitNumber} · reported{' '}
                {severityLabel[reaction.severity].toLowerCase()}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.body}>
              <FormGrid columns={2}>
                <SelectField
                  control={form.control}
                  name="type"
                  label="Classification (HvPI)"
                  options={REACTION_TYPES.map((t) => ({ value: t, label: reactionTypeLabel[t] }))}
                />
                <SelectField
                  control={form.control}
                  name="severity"
                  label="Severity"
                  options={REACTION_SEVERITIES.map((s) => ({ value: s, label: severityLabel[s] }))}
                />
                <SelectField
                  control={form.control}
                  name="imputability"
                  label="Imputability"
                  options={REACTION_IMPUTABILITIES.map((i) => ({ value: i, label: imputabilityLabel[i] }))}
                />
                {lowered.length > 0 && (
                  <FullRow>
                    <MessageBar intent="warning">
                      <MessageBarBody>
                        <MessageBarTitle>Waits for the blood bank medical officer</MessageBarTitle>
                        This lowers the reaction ({lowered.join('; ')})
                        {leavesReporting ? ' and takes it out of haemovigilance reporting' : ''}. The classification in
                        force stands until the medical officer approves; you cannot approve it yourself.
                      </MessageBarBody>
                    </MessageBar>
                  </FullRow>
                )}
                <FullRow>
                  <TextAreaField
                    control={form.control}
                    name="reason"
                    label="Why it is lowered"
                    required={lowered.length > 0}
                    rows={3}
                    maxLength={1000}
                    placeholder="The investigation's findings that support it"
                  />
                </FullRow>
              </FormGrid>
              <ActionError error={save.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                {lowered.length > 0 ? 'Ask the medical officer' : 'Record classification'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
