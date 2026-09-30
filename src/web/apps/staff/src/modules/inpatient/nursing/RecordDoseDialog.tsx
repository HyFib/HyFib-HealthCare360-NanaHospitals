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
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  mergeClasses,
  Spinner,
  Switch,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import {
  CheckmarkCircleRegular,
  DismissCircleRegular,
  HandRightRegular,
  ShieldKeyholeRegular,
  SubtractCircleRegular,
} from '@fluentui/react-icons';
import { formatTime, isApiError } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent, type ReactElement } from 'react';
import { nursingApi, nursingKeys, type MarOutcome } from '../nursingApi';
import { fromIstInput, toIstInput } from '../time';

export interface DoseToRecord {
  id: string;
  drugName: string;
  dose: string;
  route: string | null;
  scheduledAt: string;
  version: number;
  admissionId: string;
  patientName?: string;
  bedLabel?: string | null;
  isOverdue?: boolean;
  /** Known from the medication order; undefined when unknown (ward round), the server then enforces it. */
  isControlledDrug?: boolean;
  batchNumber?: string | null;
}

const OUTCOMES: { value: MarOutcome; label: string; icon: ReactElement; hint: string }[] = [
  { value: 'Given', label: 'Given', icon: <CheckmarkCircleRegular />, hint: 'The patient took the dose' },
  { value: 'Held', label: 'Held', icon: <HandRightRegular />, hint: 'Withheld on clinical grounds' },
  { value: 'Refused', label: 'Refused', icon: <DismissCircleRegular />, hint: 'The patient declined' },
  { value: 'Missed', label: 'Missed', icon: <SubtractCircleRegular />, hint: 'Not given in time' },
];

const QUICK_REASONS: Record<Exclude<MarOutcome, 'Given'>, string[]> = {
  Held: [
    'Nil by mouth',
    'Low blood pressure',
    'Low pulse',
    'Doctor advised to hold',
    'Medicine not available on the ward',
  ],
  Refused: ['Patient refused', 'Patient vomited', 'Patient asleep, family asked to wait'],
  Missed: ['Patient away for a procedure', 'Patient not on the ward', 'Dose not available in time'],
};

const useStyles = makeStyles({
  outcomes: {
    display: 'grid',
    gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
    gap: '8px',
    '@media (max-width: 520px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' },
  },
  outcome: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '2px',
    minHeight: '64px',
    justifyContent: 'center',
    fontSize: tokens.fontSizeBase300,
    '& svg': { fontSize: '22px' },
  },
  given: {
    backgroundColor: tokens.colorPaletteGreenBackground2,
    color: tokens.colorPaletteGreenForeground2,
    border: `2px solid ${tokens.colorPaletteGreenBorderActive}`,
  },
  notGiven: {
    backgroundColor: tokens.colorPaletteMarigoldBackground2,
    color: tokens.colorPaletteMarigoldForeground2,
    border: `2px solid ${tokens.colorPaletteMarigoldBorderActive}`,
  },
  reasons: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  row: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '12px',
    '@media (max-width: 520px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
});

/**
 * Records one dose on the MAR (given, held, refused or missed). Two taps for the common case: the outcome is chosen
 * on the list, Enter confirms. Anything but "given" needs a reason; controlled drugs need a witness. A dose is recorded
 * once: if someone else recorded it meanwhile, the chart refreshes instead of overwriting.
 */
export function RecordDoseDialog({
  dose,
  initialOutcome = 'Given',
  onClose,
}: {
  dose: DoseToRecord;
  initialOutcome?: MarOutcome;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [outcome, setOutcome] = useState<MarOutcome>(initialOutcome);
  const [reason, setReason] = useState('');
  const [earlier, setEarlier] = useState(false);
  const [limits] = useState(() => ({ now: toIstInput(new Date()) }));
  const [administeredAt, setAdministeredAt] = useState(limits.now);
  const [batch, setBatch] = useState(dose.batchNumber ?? '');
  const [witness, setWitness] = useState('');
  const [notes, setNotes] = useState('');
  const [witnessRequired, setWitnessRequired] = useState(Boolean(dose.isControlledDrug));

  const record = useMutation({
    mutationFn: () =>
      nursingApi.recordDose(dose.id, {
        outcome,
        administeredAt: outcome === 'Given' && earlier ? fromIstInput(administeredAt) : null,
        reason: reason.trim() || null,
        batchNumber: batch.trim() || null,
        witnessName: witness.trim() || null,
        notes: notes.trim() || null,
        version: dose.version,
      }),
    onSuccess: () => {
      notify.success(
        outcome === 'Given'
          ? `${dose.drugName} recorded as given`
          : `${dose.drugName} recorded as ${outcome.toLowerCase()}`,
      );
      void queryClient.invalidateQueries({ queryKey: nursingKeys.all });
      void queryClient.invalidateQueries({ queryKey: ['inpatient', 'board'] });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'mar.witness_required') {
        setWitnessRequired(true);
      }
      if (isApiError(error) && (error.isConflict || error.code === 'mar.already_recorded')) {
        void queryClient.invalidateQueries({ queryKey: nursingKeys.all });
      }
    },
  });

  const needsReason = outcome !== 'Given';
  const needsWitness = outcome === 'Given' && witnessRequired;
  const valid =
    (!needsReason || reason.trim().length > 0) &&
    (!needsWitness || witness.trim().length > 0) &&
    (!earlier || fromIstInput(administeredAt) !== null);
  const alreadyDone =
    isApiError(record.error) && (record.error.isConflict || record.error.code === 'mar.already_recorded');

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (valid && !record.isPending) {
      record.mutate();
    }
  };

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !record.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <form onSubmit={submit}>
          <DialogBody>
            <DialogTitle>
              {dose.drugName} · {dose.dose}
              {dose.route ? ` · ${dose.route}` : ''}
              <Caption1 block className={styles.muted}>
                {dose.patientName ? `${dose.patientName}${dose.bedLabel ? `, bed ${dose.bedLabel}` : ''} · ` : ''}
                Due {formatTime(dose.scheduledAt)}
                {dose.isOverdue ? ' (overdue)' : ''}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.stack}>
              <div className={styles.outcomes} role="radiogroup" aria-label="Outcome">
                {OUTCOMES.map((o) => (
                  <Button
                    key={o.value}
                    role="radio"
                    aria-checked={outcome === o.value}
                    title={o.hint}
                    className={mergeClasses(
                      styles.outcome,
                      outcome === o.value && (o.value === 'Given' ? styles.given : styles.notGiven),
                    )}
                    appearance={outcome === o.value ? 'primary' : 'secondary'}
                    onClick={() => setOutcome(o.value)}
                  >
                    {o.icon}
                    {o.label}
                  </Button>
                ))}
              </div>

              {needsReason && (
                <Field label="Reason" required>
                  <div className={styles.stack}>
                    <div className={styles.reasons}>
                      {QUICK_REASONS[outcome as Exclude<MarOutcome, 'Given'>].map((r) => (
                        <Button
                          key={r}
                          size="small"
                          shape="circular"
                          appearance={reason === r ? 'primary' : 'outline'}
                          onClick={() => setReason(r)}
                        >
                          {r}
                        </Button>
                      ))}
                    </div>
                    <Textarea
                      value={reason}
                      onChange={(_, d) => setReason(d.value)}
                      maxLength={300}
                      rows={2}
                      placeholder="Or type the reason"
                    />
                  </div>
                </Field>
              )}

              {outcome === 'Given' && (
                <>
                  <Switch
                    checked={earlier}
                    onChange={(_, d) => setEarlier(d.checked)}
                    label="Given earlier (not just now)"
                  />
                  {earlier && (
                    <Field label="Given at" required>
                      <Input
                        type="datetime-local"
                        value={administeredAt}
                        max={limits.now}
                        onChange={(_, d) => setAdministeredAt(d.value)}
                      />
                    </Field>
                  )}
                  <div className={styles.row}>
                    <Field label="Batch number">
                      <Input
                        value={batch}
                        onChange={(_, d) => setBatch(d.value)}
                        maxLength={64}
                        placeholder="From the strip or vial"
                      />
                    </Field>
                    <Field
                      label="Witness (second nurse)"
                      required={needsWitness}
                      hint={needsWitness ? 'Required for controlled drugs' : 'Needed for controlled drugs'}
                    >
                      <Input
                        value={witness}
                        onChange={(_, d) => setWitness(d.value)}
                        maxLength={200}
                        contentBefore={needsWitness ? <ShieldKeyholeRegular /> : undefined}
                      />
                    </Field>
                  </div>
                </>
              )}

              <Field label="Notes">
                <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} maxLength={500} rows={2} />
              </Field>

              {record.error && (
                <MessageBar intent={alreadyDone ? 'warning' : 'error'} layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>
                      {alreadyDone
                        ? 'Already recorded'
                        : isApiError(record.error)
                          ? record.error.title
                          : 'Not recorded'}
                    </MessageBarTitle>
                    {alreadyDone
                      ? 'Someone recorded this dose a moment ago. The chart has been refreshed; write a nursing note to correct it if needed.'
                      : isApiError(record.error)
                        ? record.error.userMessage
                        : 'Please try again.'}
                  </MessageBarBody>
                </MessageBar>
              )}
              <Text size={200} className={styles.muted}>
                The entry is signed with your name and the time of recording. It cannot be edited afterwards.
              </Text>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={record.isPending}>
                {alreadyDone ? 'Close' : 'Cancel'}
              </Button>
              {!alreadyDone && (
                <Button
                  type="submit"
                  appearance="primary"
                  disabled={!valid || record.isPending}
                  icon={record.isPending ? <Spinner size="tiny" /> : undefined}
                >
                  Record {outcome.toLowerCase()}
                </Button>
              )}
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
