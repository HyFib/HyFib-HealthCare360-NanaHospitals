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
import { ChoiceField, FormGrid, FullRow, NumberField, SelectField, SwitchField, TextAreaField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { fromIstInput, nowIstInput } from '@/modules/ot/shared/datetime';
import { applyFormErrors } from '@/modules/ot/shared/errors';
import { ActionError } from '@/modules/ot/shared/feedback';
import { CheckboxGroupField, DateTimeField } from '@/modules/ot/shared/fields';
import { bloodApi, bloodKeys, REACTION_SEVERITIES, REACTION_SYMPTOMS, REACTION_TYPES, type BloodIssueDto, type TransfusionReactionDto } from './api';
import { reactionTypeLabel, severityLabel, symptomLabel } from './labels';

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
export function ReportReactionDialog({ issue, patientName, onClose }: { issue: BloodIssueDto; patientName: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<ReportValues>({
    resolver: zodResolver(reportSchema),
    defaultValues: { onsetAt: nowIstInput(), symptoms: [], vitals: '', transfusionStopped: true, volumeTransfusedMl: null, actionsTaken: '', severity: 'Mild' },
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

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
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
                  Keep the line open with normal saline, check the patient’s identity against the unit label, inform the doctor, and send the bag with a fresh
                  blood and urine sample to the blood bank.
                </MessageBarBody>
              </MessageBar>
              <FormGrid columns={3}>
                <DateTimeField control={form.control} name="onsetAt" label="Reaction started" required />
                <NumberField control={form.control} name="volumeTransfusedMl" label="Volume transfused (ml)" min={0} max={1000} />
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
                  <TextField control={form.control} name="vitals" label="Vital signs at the time" maxLength={500} placeholder="e.g. T 39.2 °C, P 124, BP 86/50, SpO₂ 91%" />
                </FullRow>
                <FullRow>
                  <TextAreaField control={form.control} name="actionsTaken" label="Actions taken" rows={2} maxLength={2000} />
                </FullRow>
                <FullRow>
                  <ChoiceField control={form.control} name="severity" label="Severity" options={REACTION_SEVERITIES.map((s) => ({ value: s, label: severityLabel[s] }))} />
                </FullRow>
              </FormGrid>
              <ActionError error={report.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={report.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={report.isPending} icon={report.isPending ? <Spinner size="tiny" /> : <HeartPulseWarningRegular />}>
                Report reaction
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

const closeSchema = z.object({
  // `: boolean` keeps TypeScript from inferring a type predicate, which would narrow the output type away from the form's input type.
  type: z.enum(REACTION_TYPES).refine((t): boolean => t !== 'Unclassified', 'Classify the reaction.'),
  severity: z.enum(REACTION_SEVERITIES),
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
      findings: reaction.investigationFindings ?? '',
      reportedToHaemovigilance: reaction.reportedToHaemovigilance,
    },
  });
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

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
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
                  options={REACTION_TYPES.filter((t) => t !== 'Unclassified').map((t) => ({ value: t, label: reactionTypeLabel[t] }))}
                />
                <SelectField control={form.control} name="severity" label="Severity" options={REACTION_SEVERITIES.map((s) => ({ value: s, label: severityLabel[s] }))} />
                <FullRow>
                  <TextAreaField control={form.control} name="findings" label="Investigation findings" required rows={5} maxLength={4000} placeholder="Clerical check, repeat grouping, DAT, culture, conclusion" />
                </FullRow>
                <FullRow>
                  <SwitchField control={form.control} name="reportedToHaemovigilance" label="Reported to the Haemovigilance Programme of India" />
                </FullRow>
              </FormGrid>
              <ActionError error={close.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={close.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={close.isPending} icon={close.isPending ? <Spinner size="tiny" /> : undefined}>
                Close investigation
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
