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
  Switch,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular } from '@fluentui/react-icons';
import { isApiError, useSession } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useStoredState } from '@/modules/inpatient/hooks';
import { cssdApi, cssdKeys, cssdQueries, type LoadDto } from './api';
import { checkParameters, METHOD_HINT, METHOD_LABEL, METHODS, type SterilizationMethod } from './rules';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  two: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '12px',
    '@media (max-width: 560px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  three: {
    display: 'grid',
    gridTemplateColumns: 'repeat(3, minmax(0, 1fr))',
    gap: '12px',
    '@media (max-width: 560px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  line: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) 90px auto', gap: '8px', alignItems: 'end' },
  muted: { color: tokens.colorNeutralForeground3 },
});

interface PackLine {
  setId: string;
  quantity: number | '';
}

/** Load a sterilizer: sets must suit the method; each pack gets "{cycle}-001…" and a label. */
export function CreateLoadDialog({ onCreated, onClose }: { onCreated: (load: LoadDto) => void; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const userName = useSession((s) => s.user?.displayName ?? '');
  const sets = useQuery(cssdQueries.sets());
  const [lastSterilizer, setLastSterilizer] = useStoredState('hc360:cssd:sterilizer');
  const [sterilizer, setSterilizer] = useState(lastSterilizer);
  const [method, setMethod] = useState<SterilizationMethod>('Steam');
  const [program, setProgram] = useState('');
  const [implants, setImplants] = useState(false);
  const [operator, setOperator] = useState(userName);
  const [notes, setNotes] = useState('');
  const [lines, setLines] = useState<PackLine[]>([{ setId: '', quantity: 1 }]);

  const methodSets = (sets.data ?? []).filter((s) => s.method === method);
  const filled = lines.filter((l) => l.setId && l.quantity !== '' && l.quantity > 0);
  const total = filled.reduce((sum, l) => sum + Number(l.quantity), 0);
  const valid =
    sterilizer.trim().length > 0 &&
    filled.length > 0 &&
    total <= 200 &&
    lines.every((l) => !l.setId || (l.quantity !== '' && l.quantity >= 1 && l.quantity <= 50));

  const create = useMutation({
    mutationFn: () =>
      cssdApi.createLoad({
        sterilizerName: sterilizer.trim(),
        method,
        program: program.trim() || null,
        containsImplants: implants,
        operatorName: operator.trim() || null,
        notes: notes.trim() || null,
        packs: filled.map((l) => ({ instrumentSetId: l.setId, quantity: Number(l.quantity) })),
      }),
    onSuccess: (load) => {
      setLastSterilizer(sterilizer.trim());
      notify.success(
        `Load ${load.cycleNumber} created`,
        `${load.packs.length} packs numbered. Print the labels before sealing.`,
      );
      void queryClient.invalidateQueries({ queryKey: cssdKeys.all });
      onCreated(load);
    },
  });

  const changeMethod = (next: SterilizationMethod) => {
    setMethod(next);
    // Sets of another method cannot go in this load.
    setLines([{ setId: '', quantity: 1 }]);
  };

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !create.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 680 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !create.isPending) {
              create.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>New sterilizer load</DialogTitle>
            <DialogContent className={styles.stack}>
              <div className={styles.two}>
                <Field label="Sterilizer" required hint="Remembered on this computer">
                  <Input
                    value={sterilizer}
                    onChange={(_, d) => setSterilizer(d.value)}
                    maxLength={100}
                    placeholder="e.g. Autoclave 1"
                    autoFocus={!sterilizer}
                  />
                </Field>
                <Field label="Programme / cycle">
                  <Input
                    value={program}
                    onChange={(_, d) => setProgram(d.value)}
                    maxLength={100}
                    placeholder="e.g. 134 °C wrapped"
                  />
                </Field>
              </div>
              <Field label="Method" hint={METHOD_HINT[method]}>
                <RadioGroup
                  layout="horizontal"
                  value={method}
                  onChange={(_, d) => changeMethod(d.value as SterilizationMethod)}
                >
                  {METHODS.map((m) => (
                    <Radio key={m} value={m} label={METHOD_LABEL[m]} />
                  ))}
                </RadioGroup>
              </Field>
              <Field label={`Sets in this load (${total} packs, at most 200)`} required>
                <div className={styles.stack}>
                  {lines.map((line, index) => (
                    <div key={index} className={styles.line}>
                      <Select
                        aria-label={`Set ${index + 1}`}
                        value={line.setId}
                        onChange={(_, d) => setLines(lines.map((l, i) => (i === index ? { ...l, setId: d.value } : l)))}
                      >
                        <option value="">Choose a set</option>
                        {methodSets.map((s) => (
                          <option key={s.id} value={s.id}>
                            {s.name} ({s.code})
                          </option>
                        ))}
                      </Select>
                      <Input
                        type="number"
                        min={1}
                        max={50}
                        aria-label={`Packs of set ${index + 1}`}
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
                  {sets.data && methodSets.length === 0 && (
                    <Caption1 className={styles.muted}>
                      No sets use this method. Check the instrument set masters.
                    </Caption1>
                  )}
                </div>
              </Field>
              <Switch
                checked={implants}
                onChange={(_, d) => setImplants(d.checked)}
                label="Load contains implants (packs stay in quarantine until the biological indicator passes)"
              />
              <div className={styles.two}>
                <Field label="Operator">
                  <Input value={operator} onChange={(_, d) => setOperator(d.value)} maxLength={200} />
                </Field>
                <Field label="Notes">
                  <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} maxLength={500} rows={1} />
                </Field>
              </div>
              {create.error && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {isApiError(create.error) ? create.error.userMessage : 'The load could not be created.'}
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={create.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || create.isPending}
                icon={create.isPending ? <Spinner size="tiny" /> : undefined}
              >
                Create load ({total} packs)
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/** Record the cycle printout. Out-of-range parameters or a failed chemical indicator fail the load at once. */
export function CompleteLoadDialog({ load, onClose }: { load: LoadDto; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [temperature, setTemperature] = useState(load.method === 'Steam' ? '134' : '');
  const [pressure, setPressure] = useState('');
  const [minutes, setMinutes] = useState('');
  const [ci, setCi] = useState<'Pass' | 'Fail' | ''>('');
  const t = Number(temperature);
  const m = Number(minutes);
  const haveNumbers =
    temperature !== '' &&
    minutes !== '' &&
    Number.isFinite(t) &&
    Number.isInteger(m) &&
    t >= 0 &&
    t <= 200 &&
    m >= 0 &&
    m <= 1440;
  const check = haveNumbers ? checkParameters(load.method, t, m) : null;
  const pressureOk = pressure === '' || (Number(pressure) >= 0 && Number(pressure) <= 1000);
  const valid = haveNumbers && ci !== '' && pressureOk;
  const willFail = (check && !check.ok) || ci === 'Fail';

  const complete = useMutation({
    mutationFn: () =>
      cssdApi.completeLoad(load.id, {
        temperatureC: t,
        pressureKpa: pressure === '' ? null : Number(pressure),
        exposureMinutes: m,
        chemicalIndicator: ci as 'Pass' | 'Fail',
      }),
    onSuccess: (result) => {
      if (result.status === 'Failed') {
        notify.warning(
          `Load ${result.cycleNumber} failed`,
          result.failureReason ?? 'Send the packs back for reprocessing.',
        );
      } else {
        notify.success(
          `Cycle ${result.cycleNumber} complete`,
          result.containsImplants
            ? 'Implant load: release after the biological indicator passes.'
            : 'Ready to release.',
        );
      }
      void queryClient.invalidateQueries({ queryKey: cssdKeys.all });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !complete.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
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
              Record cycle {load.cycleNumber}
              <Caption1 block className={styles.muted}>
                {METHOD_LABEL[load.method]} · {METHOD_HINT[load.method]}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.stack}>
              <div className={styles.three}>
                <Field label="Temperature (°C)" required>
                  <Input
                    type="number"
                    inputMode="decimal"
                    step={0.1}
                    min={0}
                    max={200}
                    value={temperature}
                    onChange={(_, d) => setTemperature(d.value)}
                    autoFocus
                  />
                </Field>
                <Field label="Exposure (minutes)" required>
                  <Input
                    type="number"
                    inputMode="numeric"
                    min={0}
                    max={1440}
                    value={minutes}
                    onChange={(_, d) => setMinutes(d.value)}
                  />
                </Field>
                <Field
                  label="Pressure (kPa)"
                  validationMessage={pressureOk ? undefined : '0 to 1000 kPa'}
                  validationState={pressureOk ? 'none' : 'error'}
                >
                  <Input
                    type="number"
                    inputMode="decimal"
                    min={0}
                    max={1000}
                    value={pressure}
                    onChange={(_, d) => setPressure(d.value)}
                  />
                </Field>
              </div>
              <Field label="Chemical indicator" required>
                <RadioGroup layout="horizontal" value={ci} onChange={(_, d) => setCi(d.value as 'Pass' | 'Fail')}>
                  <Radio value="Pass" label="Passed (colour changed)" />
                  <Radio value="Fail" label="Failed" />
                </RadioGroup>
              </Field>
              {check && !check.ok && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>These parameters fail the load</MessageBarTitle>
                    {check.message} Check the printout; if it is correct, the packs must be reprocessed.
                  </MessageBarBody>
                </MessageBar>
              )}
              {check?.ok && ci === 'Pass' && (
                <MessageBar intent="success">
                  <MessageBarBody>Within the validated range.</MessageBarBody>
                </MessageBar>
              )}
              {complete.error && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {isApiError(complete.error) ? complete.error.userMessage : 'The cycle could not be recorded.'}
                  </MessageBarBody>
                </MessageBar>
              )}
              <Text size={200} className={styles.muted}>
                Enter the values from the sterilizer printout. The server applies the same checks.
              </Text>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={complete.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || complete.isPending}
                icon={complete.isPending ? <Spinner size="tiny" /> : undefined}
                style={willFail ? { backgroundColor: tokens.colorPaletteRedBackground3 } : undefined}
              >
                {willFail ? 'Record as failed' : 'Record cycle'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/** Biological indicator (read 1–48 h after the cycle). A failure on a released load triggers the recall. */
export function BiologicalIndicatorDialog({ load, onClose }: { load: LoadDto; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [result, setResult] = useState<'Pass' | 'Fail' | ''>('');
  const [lot, setLot] = useState('');
  const released = load.status === 'Released';

  const record = useMutation({
    mutationFn: () => cssdApi.recordBiological(load.id, { result: result as 'Pass' | 'Fail', lot: lot.trim() || null }),
    onSuccess: (updated) => {
      if (result === 'Fail') {
        notify.warning(
          released ? `Recall started for ${updated.cycleNumber}` : `Load ${updated.cycleNumber} failed`,
          released
            ? 'Every issue of this load is flagged and the recall team alerted.'
            : 'Send the packs for reprocessing.',
        );
      } else {
        notify.success(
          'Biological indicator passed',
          updated.containsImplants && updated.status === 'Completed'
            ? 'The implant load can now be released.'
            : undefined,
        );
      }
      void queryClient.invalidateQueries({ queryKey: cssdKeys.all });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !record.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (result && !record.isPending) {
              record.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Biological indicator for {load.cycleNumber}</DialogTitle>
            <DialogContent className={styles.stack}>
              <Field label="Result" required>
                <RadioGroup
                  layout="horizontal"
                  value={result}
                  onChange={(_, d) => setResult(d.value as 'Pass' | 'Fail')}
                >
                  <Radio value="Pass" label="Passed (no growth)" />
                  <Radio value="Fail" label="Failed (growth)" />
                </RadioGroup>
              </Field>
              <Field label="Indicator lot">
                <Input value={lot} onChange={(_, d) => setLot(d.value)} maxLength={64} />
              </Field>
              {result === 'Fail' && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>{released ? 'This starts a recall' : 'The load will fail'}</MessageBarTitle>
                    {released
                      ? 'Every pack of this load is recalled: shelf packs are withdrawn, departments are alerted and patients already treated with these packs are listed for infection-control review.'
                      : 'The packs cannot be released and go back for reprocessing.'}
                  </MessageBarBody>
                </MessageBar>
              )}
              {record.error && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {isApiError(record.error) ? record.error.userMessage : 'The result could not be saved.'}
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={record.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!result || record.isPending}
                icon={record.isPending ? <Spinner size="tiny" /> : undefined}
                style={result === 'Fail' ? { backgroundColor: tokens.colorPaletteRedBackground3 } : undefined}
              >
                {result === 'Fail' ? (released ? 'Record failure and recall' : 'Record failure') : 'Record result'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
