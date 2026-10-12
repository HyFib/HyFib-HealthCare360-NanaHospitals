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
  Radio,
  RadioGroup,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular } from '@fluentui/react-icons';
import { isApiError, useSession } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useStoredState } from '@/modules/inpatient/hooks';
import { cssdApi, cssdKeys, cssdQueries, type WashCycleDto } from './api';
import { REQUIRED_A0, washA0 } from './rules';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  two: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '12px',
    '@media (max-width: 560px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  line: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) 90px auto', gap: '8px', alignItems: 'end' },
  muted: { color: tokens.colorNeutralForeground3 },
});

interface TrayLine {
  setId: string;
  quantity: number | '';
}

/** Trays go into the washer-disinfector (CSSD-04); the cycle gets "{FAC}CW{YY}…" and is completed from its printout. */
export function StartWashDialog({
  onStarted,
  onClose,
}: {
  onStarted: (wash: WashCycleDto) => void;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const userName = useSession((s) => s.user?.displayName ?? '');
  const sets = useQuery(cssdQueries.sets());
  const [lastWasher, setLastWasher] = useStoredState('hc360:cssd:washer');
  const [washer, setWasher] = useState(lastWasher);
  const [program, setProgram] = useState('');
  const [operator, setOperator] = useState(userName);
  const [notes, setNotes] = useState('');
  const [lines, setLines] = useState<TrayLine[]>([{ setId: '', quantity: 1 }]);

  const filled = lines.filter((l) => l.setId && l.quantity !== '' && l.quantity > 0);
  const total = filled.reduce((sum, l) => sum + Number(l.quantity), 0);
  const valid =
    washer.trim().length > 0 &&
    filled.length > 0 &&
    total <= 200 &&
    lines.every((l) => !l.setId || (l.quantity !== '' && l.quantity >= 1 && l.quantity <= 50));

  const start = useMutation({
    mutationFn: () =>
      cssdApi.startWash({
        washerName: washer.trim(),
        program: program.trim() || null,
        operatorName: operator.trim() || null,
        notes: notes.trim() || null,
        trays: filled.map((l) => ({ instrumentSetId: l.setId, quantity: Number(l.quantity) })),
      }),
    onSuccess: (wash) => {
      setLastWasher(washer.trim());
      notify.success(`Wash cycle ${wash.cycleNumber} started`, 'Record the result from the printout when it ends.');
      void queryClient.invalidateQueries({ queryKey: cssdKeys.all });
      onStarted(wash);
    },
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ washer, program, operator, notes, lines }),
    busy: start.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !start.isPending) {
              start.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>New wash cycle</DialogTitle>
            <DialogContent className={styles.stack}>
              <div className={styles.two}>
                <Field label="Washer-disinfector" required hint="Remembered on this computer">
                  <Input
                    value={washer}
                    onChange={(_, d) => setWasher(d.value)}
                    maxLength={100}
                    placeholder="e.g. Washer-disinfector 1"
                    autoFocus={!washer}
                  />
                </Field>
                <Field label="Programme">
                  <Input
                    value={program}
                    onChange={(_, d) => setProgram(d.value)}
                    maxLength={100}
                    placeholder="e.g. Instruments 90 °C / 5 min"
                  />
                </Field>
              </div>
              <Field label={`Trays in this cycle (${total}, at most 200)`} required>
                <div className={styles.stack}>
                  {lines.map((line, index) => (
                    <div key={index} className={styles.line}>
                      <Select
                        aria-label={`Set ${index + 1}`}
                        value={line.setId}
                        onChange={(_, d) => setLines(lines.map((l, i) => (i === index ? { ...l, setId: d.value } : l)))}
                      >
                        <option value="">Choose a set</option>
                        {(sets.data ?? []).map((s) => (
                          <option key={s.id} value={s.id}>
                            {s.name} ({s.code})
                          </option>
                        ))}
                      </Select>
                      <Input
                        type="number"
                        min={1}
                        max={50}
                        aria-label={`Trays of set ${index + 1}`}
                        value={String(line.quantity)}
                        onChange={(_, d) =>
                          setLines(
                            lines.map((l, i) =>
                              i === index ? { ...l, quantity: d.value === '' ? '' : Number(d.value) } : l,
                            ),
                          )
                        }
                      />
                      <Button
                        appearance="subtle"
                        icon={<DeleteRegular />}
                        aria-label={`Remove set ${index + 1}`}
                        disabled={lines.length === 1}
                        onClick={() => setLines(lines.filter((_, i) => i !== index))}
                      />
                    </div>
                  ))}
                  <div>
                    <Button
                      size="small"
                      icon={<AddRegular />}
                      onClick={() => setLines([...lines, { setId: '', quantity: 1 }])}
                    >
                      Add another set
                    </Button>
                  </div>
                </div>
              </Field>
              <div className={styles.two}>
                <Field label="Operator">
                  <Input value={operator} onChange={(_, d) => setOperator(d.value)} maxLength={200} />
                </Field>
                <Field label="Notes">
                  <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} maxLength={500} rows={1} />
                </Field>
              </div>
              {start.error && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {isApiError(start.error) ? start.error.userMessage : 'The wash cycle could not be started.'}
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={start.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || start.isPending}
                icon={start.isPending ? <Spinner size="tiny" /> : undefined}
              >
                Start wash ({total} trays)
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

/**
 * Record the washer's printout and the inspection of the trays. Below A0 3000, or with trays not clean, the cycle fails
 * and the trays are washed again.
 */
export function CompleteWashDialog({ wash, onClose }: { wash: WashCycleDto; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [temperature, setTemperature] = useState('90');
  const [minutes, setMinutes] = useState('');
  const [inspection, setInspection] = useState<'clean' | 'not-clean' | ''>('');
  const t = Number(temperature);
  const m = Number(minutes);
  const haveNumbers =
    temperature !== '' &&
    minutes !== '' &&
    Number.isFinite(t) &&
    Number.isFinite(m) &&
    t >= 20 &&
    t <= 100 &&
    m >= 0 &&
    m <= 120;
  const a0 = haveNumbers ? washA0(t, m) : null;
  const valid = haveNumbers && inspection !== '';
  const willFail = (a0 !== null && a0 < REQUIRED_A0) || inspection === 'not-clean';

  const complete = useMutation({
    mutationFn: () =>
      cssdApi.completeWash(wash.id, { temperatureC: t, holdMinutes: m, inspectedClean: inspection === 'clean' }),
    onSuccess: (result) => {
      if (result.status === 'Failed') {
        notify.warning(`Wash cycle ${result.cycleNumber} failed`, result.failureReason ?? 'Wash the trays again.');
      } else {
        notify.success(`Wash cycle ${result.cycleNumber} passed`, 'The trays can be packed and loaded.');
      }
      void queryClient.invalidateQueries({ queryKey: cssdKeys.all });
      onClose();
    },
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ temperature, minutes, inspection }),
    busy: complete.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !complete.isPending) {
              complete.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              Record wash cycle {wash.cycleNumber}
              <Caption1 block className={styles.muted}>
                Disinfection must reach A0 {REQUIRED_A0}, for example 90 °C held for 5 minutes
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.stack}>
              <div className={styles.two}>
                <Field label="Disinfection temperature (°C)" required>
                  <Input
                    type="number"
                    inputMode="decimal"
                    step={0.1}
                    min={20}
                    max={100}
                    value={temperature}
                    onChange={(_, d) => setTemperature(d.value)}
                  />
                </Field>
                <Field label="Held for (minutes)" required>
                  <Input
                    type="number"
                    inputMode="decimal"
                    step={0.1}
                    min={0}
                    max={120}
                    value={minutes}
                    onChange={(_, d) => setMinutes(d.value)}
                    autoFocus
                  />
                </Field>
              </div>
              <Field label="Inspection of the trays" required>
                <RadioGroup
                  layout="horizontal"
                  value={inspection}
                  onChange={(_, d) => setInspection(d.value as 'clean' | 'not-clean')}
                >
                  <Radio value="clean" label="Clean" />
                  <Radio value="not-clean" label="Not clean (soil or residue left)" />
                </RadioGroup>
              </Field>
              {a0 !== null && a0 < REQUIRED_A0 && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>
                      A0 {a0} is below {REQUIRED_A0}
                    </MessageBarTitle>
                    The cycle fails. Check the printout; if it is correct, wash the trays again.
                  </MessageBarBody>
                </MessageBar>
              )}
              {a0 !== null && a0 >= REQUIRED_A0 && (
                <MessageBar intent={inspection === 'not-clean' ? 'warning' : 'success'}>
                  <MessageBarBody>
                    A0 {a0} reaches {REQUIRED_A0}.
                    {inspection === 'not-clean' && ' Trays that are not clean fail the cycle all the same.'}
                  </MessageBarBody>
                </MessageBar>
              )}
              {complete.error && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {isApiError(complete.error) ? complete.error.userMessage : 'The wash cycle could not be recorded.'}
                  </MessageBarBody>
                </MessageBar>
              )}
              <Text size={200} className={styles.muted}>
                Enter the values from the washer printout. The server applies the same checks.
              </Text>
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={complete.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || complete.isPending}
                icon={complete.isPending ? <Spinner size="tiny" /> : undefined}
                style={willFail ? { backgroundColor: tokens.colorPaletteRedBackground3 } : undefined}
              >
                {willFail ? 'Record as failed' : 'Record result'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
