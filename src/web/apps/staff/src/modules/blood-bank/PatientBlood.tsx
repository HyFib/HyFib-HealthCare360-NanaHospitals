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
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { formatDateTime, formatTime, isApiError } from '@hc360/core';
import { StatusBadge, useNotify, useUnsavedChanges, useValuesChanged, type StatusTone } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import type { WitnessConfirmed } from '@/modules/witness/api';
import { WitnessPanel } from '@/modules/witness/WitnessPanel';
import {
  bedsideApi,
  bedsideKeys,
  bedsideQueries,
  type TransfusionDto,
  type TransfusionObservationValues,
  type WardBloodUnit,
} from './bedsideApi';
import { componentLabel, groupLabel } from './labels';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  unit: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  head: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  facts: {
    display: 'grid',
    gridTemplateColumns: 'max-content minmax(0, 1fr)',
    columnGap: '12px',
    rowGap: '2px',
  },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(3, minmax(0, 1fr))',
    gap: '12px',
    '@media (max-width: 520px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' },
  },
  pair: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '12px',
    '@media (max-width: 520px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
});

// ---------------------------------------------------------------- observations

interface ObservationInput {
  temperatureC: string;
  pulsePerMinute: string;
  systolicMmHg: string;
  diastolicMmHg: string;
  respiratoryRate: string;
  spO2Percent: string;
}

const EMPTY_OBSERVATION: ObservationInput = {
  temperatureC: '',
  pulsePerMinute: '',
  systolicMmHg: '',
  diastolicMmHg: '',
  respiratoryRate: '',
  spO2Percent: '',
};

const number = (value: string) => (value.trim() === '' || Number.isNaN(Number(value)) ? null : Number(value));

function toValues(input: ObservationInput): TransfusionObservationValues {
  return {
    temperatureC: number(input.temperatureC),
    pulsePerMinute: number(input.pulsePerMinute),
    systolicMmHg: number(input.systolicMmHg),
    diastolicMmHg: number(input.diastolicMmHg),
    respiratoryRate: number(input.respiratoryRate),
    spO2Percent: number(input.spO2Percent),
  };
}

/** Temperature, pulse, blood pressure and respiratory rate are taken every time; oxygen saturation when measured. */
function complete(input: ObservationInput) {
  const v = toValues(input);
  return (
    v.temperatureC !== null &&
    v.pulsePerMinute !== null &&
    v.systolicMmHg !== null &&
    v.diastolicMmHg !== null &&
    v.respiratoryRate !== null
  );
}

function ObservationFields({
  value,
  onChange,
}: {
  value: ObservationInput;
  onChange: (value: ObservationInput) => void;
}) {
  const styles = useStyles();
  const field = (key: keyof ObservationInput, label: string, required: boolean, step = '1') => (
    <Field label={label} required={required}>
      <Input
        type="number"
        inputMode="decimal"
        step={step}
        value={value[key]}
        onChange={(_, d) => onChange({ ...value, [key]: d.value })}
      />
    </Field>
  );
  return (
    <div className={styles.grid}>
      {field('temperatureC', 'Temperature (°C)', true, '0.1')}
      {field('pulsePerMinute', 'Pulse (/min)', true)}
      {field('respiratoryRate', 'Respiratory rate (/min)', true)}
      {field('systolicMmHg', 'BP systolic (mmHg)', true)}
      {field('diastolicMmHg', 'BP diastolic (mmHg)', true)}
      {field('spO2Percent', 'SpO₂ (%)', false)}
    </div>
  );
}

function ErrorBar({ error, title }: { error: unknown; title: string }) {
  if (!error) {
    return null;
  }
  return (
    <MessageBar intent="error" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>{isApiError(error) ? error.title : title}</MessageBarTitle>
        {isApiError(error) ? error.userMessage : 'Please try again.'}
      </MessageBarBody>
    </MessageBar>
  );
}

// ---------------------------------------------------------------- the ward's list

const STATUS: Record<string, { label: string; tone: StatusTone }> = {
  waiting: { label: 'Not started', tone: 'warning' },
  InProgress: { label: 'Running', tone: 'brand' },
  Completed: { label: 'Transfused', tone: 'success' },
  Stopped: { label: 'Stopped', tone: 'danger' },
  returned: { label: 'Returned', tone: 'subtle' },
};

function statusOf(unit: WardBloodUnit) {
  if (unit.transfusion) {
    return STATUS[unit.transfusion.status]!;
  }
  return unit.returnedAt ? STATUS.returned! : STATUS.waiting!;
}

/**
 * A patient's units of blood on the ward: each unit issued, the bedside check by two staff that starts it, the
 * observations while it runs, and its end.
 */
export function PatientBlood({ patientId, canTransfuse }: { patientId: string; canTransfuse: boolean }) {
  const styles = useStyles();
  const units = useQuery(bedsideQueries.units(patientId));
  const [checking, setChecking] = useState<WardBloodUnit | null>(null);
  const [observing, setObserving] = useState<{ unit: WardBloodUnit; transfusion: TransfusionDto } | null>(null);
  const [ending, setEnding] = useState<{ unit: WardBloodUnit; transfusion: TransfusionDto } | null>(null);

  if (units.isPending) {
    return <Spinner label="Loading the patient's blood units…" />;
  }
  if (units.isError) {
    return <ErrorBar error={units.error} title="Could not load the patient's blood units" />;
  }
  if (units.data.units.length === 0) {
    return <Text className={styles.muted}>No blood has been issued for this patient.</Text>;
  }

  return (
    <div className={styles.stack}>
      {units.data.units.map((unit) => {
        const status = statusOf(unit);
        const transfusion = unit.transfusion;
        const waiting = !transfusion && !unit.returnedAt;
        return (
          <div key={unit.issueId} className={styles.unit}>
            <div className={styles.head}>
              <Text weight="semibold">{unit.unitNumber}</Text>
              <Text>
                {componentLabel[unit.componentType]} · {groupLabel(unit.unitBloodGroup)} · {unit.volumeMl} ml
              </Text>
              <StatusBadge status={status.label} tone={status.tone} />
              {unit.isEmergencyRelease && <StatusBadge status="Emergency release" tone="danger" />}
            </div>
            <Caption1 className={styles.muted}>
              Issued {formatDateTime(unit.issuedAt)} to {unit.issuedToName} · request {unit.requestNumber}
              {waiting ? ` · start by ${formatTime(unit.startBy)}` : ''}
            </Caption1>
            {transfusion && (
              <Caption1>
                Started {formatDateTime(transfusion.startedAt)} by {transfusion.startedByName}, checked with{' '}
                {transfusion.witnessName}
                {transfusion.endedAt
                  ? ` · ${transfusion.status === 'Stopped' ? 'stopped' : 'ended'} ${formatDateTime(transfusion.endedAt)}, ${transfusion.volumeMl ?? 0} ml given`
                  : ''}
                {transfusion.stopReason ? ` (${transfusion.stopReason})` : ''}
              </Caption1>
            )}
            {canTransfuse && (waiting || transfusion?.status === 'InProgress') && (
              <div className={styles.actions}>
                {waiting && (
                  <Button appearance="primary" onClick={() => setChecking(unit)}>
                    Bedside check
                  </Button>
                )}
                {transfusion?.status === 'InProgress' && (
                  <>
                    <Button onClick={() => setObserving({ unit, transfusion })}>Observations</Button>
                    <Button onClick={() => setEnding({ unit, transfusion })}>End transfusion</Button>
                  </>
                )}
              </div>
            )}
          </div>
        );
      })}
      {checking && (
        <BedsideCheckDialog
          unit={checking}
          patientId={patientId}
          patientBloodGroup={units.data.patientBloodGroup}
          onClose={() => setChecking(null)}
        />
      )}
      {observing && (
        <ObservationsDialog
          patientId={patientId}
          unit={observing.unit}
          transfusion={observing.transfusion}
          onClose={() => setObserving(null)}
        />
      )}
      {ending && (
        <EndTransfusionDialog
          patientId={patientId}
          unit={ending.unit}
          transfusion={ending.transfusion}
          onClose={() => setEnding(null)}
        />
      )}
    </div>
  );
}

// ---------------------------------------------------------------- the bedside check

/**
 * The check at the bedside before a unit is started: the patient's wristband and the bag are read (scanned, or typed
 * from them), the unit's facts are compared with the slip, observations are taken, and the second member of staff
 * confirms at this screen with their own user name and password.
 */
export function BedsideCheckDialog({
  unit,
  patientId,
  patientBloodGroup,
  onClose,
}: {
  unit: WardBloodUnit;
  patientId: string;
  patientBloodGroup: WardBloodUnit['unitBloodGroup'];
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [wristband, setWristband] = useState('');
  const [bag, setBag] = useState('');
  const [baseline, setBaseline] = useState(EMPTY_OBSERVATION);
  const [deferredReason, setDeferredReason] = useState('');
  const [witness, setWitness] = useState<WitnessConfirmed | null>(null);
  const [now] = useState(() => Date.now());

  const start = useMutation({
    mutationFn: () =>
      bedsideApi.start(unit.issueId, {
        wristbandUhid: wristband.trim(),
        bagUnitNumber: bag.trim(),
        witnessConfirmationId: witness!.confirmationId,
        baseline: toValues(baseline),
        consentDeferredReason: unit.consentCapturedAt ? null : deferredReason.trim() || null,
      }),
    onSuccess: () => {
      notify.success(`Unit ${unit.unitNumber} started`);
      void queryClient.invalidateQueries({ queryKey: bedsideKeys.units(patientId) });
      onClose();
    },
    onError: (error) => {
      // Run out, or used meanwhile: the witness confirms again.
      if (isApiError(error) && error.code === 'witness.confirmation_unusable') {
        setWitness(null);
      }
    },
  });

  const tooLate = now > new Date(unit.startBy).getTime();
  const consented = unit.consentCapturedAt !== null || (unit.isEmergencyRelease && deferredReason.trim().length > 0);
  const valid =
    !tooLate &&
    wristband.trim().length > 0 &&
    bag.trim().length > 0 &&
    complete(baseline) &&
    consented &&
    witness !== null;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (valid && !start.isPending) {
      start.mutate();
    }
  };

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ wristband, bag, baseline, deferredReason, witness: witness?.confirmationId ?? '' }),
    busy: start.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <form onSubmit={submit}>
          <DialogBody>
            <DialogTitle>
              Bedside check · {unit.unitNumber}
              <Caption1 block className={styles.muted}>
                Two staff check the patient and the unit together, here at the bedside.
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.stack}>
              <div className={styles.facts}>
                <Text className={styles.muted}>Unit</Text>
                <Text>
                  {componentLabel[unit.componentType]}, {groupLabel(unit.unitBloodGroup)}, {unit.volumeMl} ml
                </Text>
                <Text className={styles.muted}>Patient&apos;s group</Text>
                <Text>{groupLabel(patientBloodGroup)}</Text>
                <Text className={styles.muted}>Expires</Text>
                <Text>{formatDateTime(unit.expiresAt)}</Text>
                <Text className={styles.muted}>Cross-match</Text>
                <Text>
                  {unit.isEmergencyRelease
                    ? 'None: released in an emergency'
                    : unit.crossMatchValidUntil
                      ? `Compatible, stands until ${formatDateTime(unit.crossMatchValidUntil)}`
                      : 'Not recorded'}
                </Text>
                <Text className={styles.muted}>Issued</Text>
                <Text>
                  {formatDateTime(unit.issuedAt)}; start by {formatTime(unit.startBy)}
                </Text>
              </div>

              {tooLate && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Too late to start</MessageBarTitle>
                    The unit left the blood bank more than 4 hours ago and cannot be given in time. Return it to the
                    blood bank to be discarded.
                  </MessageBarBody>
                </MessageBar>
              )}

              <div className={styles.pair}>
                <Field label="Patient's wristband (UHID)" required hint="Scan it, or type it from the wristband">
                  <Input value={wristband} onChange={(_, d) => setWristband(d.value)} maxLength={32} autoFocus />
                </Field>
                <Field label="Unit number on the bag" required hint="Scan it, or type it from the bag">
                  <Input value={bag} onChange={(_, d) => setBag(d.value)} maxLength={32} />
                </Field>
              </div>

              {unit.consentCapturedAt ? (
                <Text size={200}>Consent to transfusion recorded {formatDateTime(unit.consentCapturedAt)}.</Text>
              ) : unit.isEmergencyRelease ? (
                <Field
                  label="Why consent cannot be taken now"
                  required
                  hint="Released in an emergency: the unit may be started, and consent taken as soon as it can be."
                >
                  <Textarea
                    value={deferredReason}
                    onChange={(_, d) => setDeferredReason(d.value)}
                    maxLength={500}
                    rows={2}
                  />
                </Field>
              ) : (
                <MessageBar intent="warning" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>No consent for this stay</MessageBarTitle>
                    Record the patient&apos;s consent to transfusion under the patient&apos;s consents before starting.
                  </MessageBarBody>
                </MessageBar>
              )}

              <Text weight="semibold">Observations before the start</Text>
              <ObservationFields value={baseline} onChange={setBaseline} />

              <WitnessPanel
                purpose="blood.bedside_check"
                patientId={patientId}
                witness={witness}
                onChange={setWitness}
              />

              <ErrorBar error={start.error} title="Not started" />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={start.isPending}>
                Cancel
              </Button>
              <Button appearance="primary" type="submit" disabled={!valid || start.isPending}>
                {start.isPending ? 'Starting…' : 'Start transfusion'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

// ---------------------------------------------------------------- while it runs, and its end

function ObservationsDialog({
  patientId,
  unit,
  transfusion,
  onClose,
}: {
  patientId: string;
  unit: WardBloodUnit;
  transfusion: TransfusionDto;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const taken = transfusion.observations.some((o) => o.kind === 'FifteenMinutes');
  const [kind, setKind] = useState<'FifteenMinutes' | 'Later'>(taken ? 'Later' : 'FifteenMinutes');
  const [values, setValues] = useState(EMPTY_OBSERVATION);

  const save = useMutation({
    mutationFn: () =>
      bedsideApi.observe(transfusion.id, {
        kind,
        recordedAt: null,
        values: toValues(values),
        version: transfusion.version,
      }),
    onSuccess: () => {
      notify.success('Observations recorded');
      void queryClient.invalidateQueries({ queryKey: bedsideKeys.units(patientId) });
      onClose();
    },
  });

  const guard = useUnsavedChanges({ dirty: useValuesChanged({ kind, values }), busy: save.isPending, onClose });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (complete(values) && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              Observations · {unit.unitNumber}
              <Caption1 block className={styles.muted}>
                Started {formatTime(transfusion.startedAt)}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.stack}>
              <RadioGroup
                layout="horizontal"
                value={kind}
                onChange={(_, d) => setKind(d.value as 'FifteenMinutes' | 'Later')}
                aria-label="When"
              >
                <Radio value="FifteenMinutes" label="At 15 minutes" />
                <Radio value="Later" label="Later" />
              </RadioGroup>
              <ObservationFields value={values} onChange={setValues} />
              <ErrorBar error={save.error} title="Not recorded" />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button appearance="primary" type="submit" disabled={!complete(values) || save.isPending}>
                Record
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

function EndTransfusionDialog({
  patientId,
  unit,
  transfusion,
  onClose,
}: {
  patientId: string;
  unit: WardBloodUnit;
  transfusion: TransfusionDto;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [stopped, setStopped] = useState(false);
  const [reason, setReason] = useState('');
  const [volume, setVolume] = useState(String(unit.volumeMl));
  const [values, setValues] = useState(EMPTY_OBSERVATION);

  const end = useMutation({
    mutationFn: () =>
      bedsideApi.end(transfusion.id, {
        endedAt: null,
        stopped,
        stopReason: stopped ? reason.trim() : null,
        volumeMl: Number(volume),
        observation: toValues(values),
        version: transfusion.version,
      }),
    onSuccess: () => {
      notify.success(stopped ? `Unit ${unit.unitNumber} stopped` : `Unit ${unit.unitNumber} completed`);
      void queryClient.invalidateQueries({ queryKey: bedsideKeys.units(patientId) });
      onClose();
    },
  });

  const valid =
    complete(values) && volume.trim() !== '' && Number(volume) >= 0 && (!stopped || reason.trim().length > 0);
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ stopped, reason, volume, values }),
    busy: end.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !end.isPending) {
              end.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              End transfusion · {unit.unitNumber}
              <Caption1 block className={styles.muted}>
                Started {formatTime(transfusion.startedAt)}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.stack}>
              <RadioGroup
                layout="horizontal"
                value={stopped ? 'stopped' : 'completed'}
                onChange={(_, d) => setStopped(d.value === 'stopped')}
                aria-label="Outcome"
              >
                <Radio value="completed" label="Completed" />
                <Radio value="stopped" label="Stopped" />
              </RadioGroup>
              {stopped && (
                <Field
                  label="Why it was stopped"
                  required
                  hint="If it was a reaction, report the reaction as well, from the blood request."
                >
                  <Textarea value={reason} onChange={(_, d) => setReason(d.value)} maxLength={500} rows={2} />
                </Field>
              )}
              <Field label="Volume given (ml)" required>
                <Input type="number" inputMode="numeric" value={volume} onChange={(_, d) => setVolume(d.value)} />
              </Field>
              <Text weight="semibold">Observations at the end</Text>
              <ObservationFields value={values} onChange={setValues} />
              <ErrorBar error={end.error} title="Not saved" />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={end.isPending}>
                Cancel
              </Button>
              <Button appearance="primary" type="submit" disabled={!valid || end.isPending}>
                {stopped ? 'Stop transfusion' : 'End transfusion'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
