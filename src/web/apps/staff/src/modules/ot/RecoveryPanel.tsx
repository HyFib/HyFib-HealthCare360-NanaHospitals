import {
  Button,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Spinner,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { formatDateTime, formatTime } from '@hc360/core';
import { ErrorState, LoadingState, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { WitnessPanel } from '@/modules/witness/WitnessPanel';
import type { WitnessConfirmed } from '@/modules/witness/api';
import {
  AIRWAY_DEVICES,
  BREATHING,
  CONSCIOUSNESS_LEVELS,
  OXYGEN_DELIVERIES,
  PONV,
  RECOVERY_DESTINATIONS,
  otApi,
  otKeys,
  otQueries,
  type AirwayDevice,
  type OxygenDelivery,
  type Ponv,
  type RecoveryBreathing,
  type RecoveryConsciousness,
  type RecoveryDestination,
  type RecoveryRecordDto,
  type SurgeryDetail,
} from './api';
import {
  airwayLabel,
  aldreteSignLabel,
  breathingLabel,
  consciousnessLabel,
  destinationLabel,
  oxygenLabel,
  ponvLabel,
} from './labels';
import { isSignedOut } from './logic';
import { ActionError, Detail, DetailGrid } from './shared/feedback';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '16px' },
  form: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  row: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))', gap: '12px', alignItems: 'end' },
  actions: { display: 'flex', justifyContent: 'flex-end', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  total: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  scroll: { overflowX: 'auto' },
});

/** A number typed in a field: null when empty or not a number. */
function numberOrNull(value: string): number | null {
  const trimmed = value.trim();
  if (trimmed === '') {
    return null;
  }
  const n = Number(trimmed);
  return Number.isFinite(n) ? n : null;
}

/**
 * The recovery room (PACU): the nurse receives the patient after the WHO Sign-out, the anaesthetist hands over (from their
 * own sign-in, or at this screen with their own credentials), the chart is kept at intervals with the modified Aldrete
 * score, and the patient goes back to the ward at 9 or more, or on the anaesthetist's override with a reason; to ICU or HDU
 * at any score.
 */
export function RecoveryPanel({ booking }: { booking: SurgeryDetail }) {
  const styles = useStyles();
  const record = useQuery(otQueries.recovery(booking.id));
  const canRecord = usePermission('ot.checklists.record');

  if (record.isLoading) {
    return <LoadingState label="Loading the recovery record…" />;
  }
  if (record.error) {
    return <ErrorState error={record.error} onRetry={() => void record.refetch()} />;
  }

  const data = record.data ?? null;
  if (!data) {
    if (!isSignedOut(booking)) {
      return (
        <MessageBar intent="info">
          <MessageBarBody>The patient comes to recovery after the WHO Sign-out.</MessageBarBody>
        </MessageBar>
      );
    }
    if (!booking.inRecovery) {
      return (
        <Text className={styles.muted}>No recovery record: the patient left recovery before it was kept here.</Text>
      );
    }
    return canRecord ? (
      <ReceiveForm booking={booking} />
    ) : (
      <Text className={styles.muted}>Waiting for the recovery nurse to receive the patient.</Text>
    );
  }

  const open = !data.dischargedAt;
  return (
    <div className={styles.stack}>
      {!open && <Discharged record={data} />}
      <Section title="In recovery">
        <DetailGrid>
          <Detail label="Time in">{formatDateTime(data.receivedAt)}</Detail>
          <Detail label="Received by">{data.receivedByName}</Detail>
          <Detail label="Airway">{airwayLabel[data.airway]}</Detail>
          <Detail label="Oxygen">
            {oxygenLabel[data.oxygen]}
            {data.oxygenLitresPerMinute ? `, ${data.oxygenLitresPerMinute} L/min` : ''}
          </Detail>
          <Detail label="Systolic before anaesthesia">
            {data.preAnaesthesiaSystolic ? `${data.preAnaesthesiaSystolic} mmHg` : null}
          </Detail>
          <Detail label="Time out">{data.dischargedAt ? formatDateTime(data.dischargedAt) : null}</Detail>
        </DetailGrid>
      </Section>
      <Handover booking={booking} record={data} canRecord={canRecord && open} />
      <Score record={data} />
      <Observations booking={booking} record={data} canRecord={canRecord && open} />
      {open && canRecord && <Discharge booking={booking} record={data} />}
    </div>
  );
}

function useSaved(booking: SurgeryDetail) {
  const queryClient = useQueryClient();
  return (saved: RecoveryRecordDto) => {
    queryClient.setQueryData(otKeys.recovery(booking.id), saved);
    // Exactly the booking: the recovery record's own key sits under it and was just set.
    void queryClient.invalidateQueries({ queryKey: otKeys.booking(booking.id), exact: true });
  };
}

function ReceiveForm({ booking }: { booking: SurgeryDetail }) {
  const styles = useStyles();
  const notify = useNotify();
  const saved = useSaved(booking);
  const [airway, setAirway] = useState<AirwayDevice>('None');
  const [oxygen, setOxygen] = useState<OxygenDelivery>('FaceMask');
  const [litres, setLitres] = useState('');
  const [systolic, setSystolic] = useState('');

  const receive = useMutation({
    mutationFn: () =>
      otApi.receiveInRecovery(booking.id, {
        airway,
        oxygen,
        oxygenLitresPerMinute: oxygen === 'RoomAir' ? null : numberOrNull(litres),
        preAnaesthesiaSystolic: numberOrNull(systolic),
      }),
    onSuccess: (record) => {
      notify.success('Patient received in recovery');
      saved(record);
    },
  });

  return (
    <div className={styles.form} role="group" aria-label="Receive the patient in recovery">
      <Text weight="semibold">Receive the patient in recovery</Text>
      <Text className={styles.muted}>
        You are recorded as the receiving nurse, at the time you receive the patient.
      </Text>
      <div className={styles.row}>
        <Field label="Airway">
          <Select value={airway} onChange={(_, d) => setAirway(d.value as AirwayDevice)}>
            {AIRWAY_DEVICES.map((a) => (
              <option key={a} value={a}>
                {airwayLabel[a]}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Oxygen">
          <Select value={oxygen} onChange={(_, d) => setOxygen(d.value as OxygenDelivery)}>
            {OXYGEN_DELIVERIES.map((o) => (
              <option key={o} value={o}>
                {oxygenLabel[o]}
              </option>
            ))}
          </Select>
        </Field>
        {oxygen !== 'RoomAir' && (
          <Field label="Litres per minute">
            <Input type="number" inputMode="decimal" value={litres} onChange={(_, d) => setLitres(d.value)} />
          </Field>
        )}
        <Field
          label="Systolic before anaesthesia (mmHg)"
          hint="Left empty: the first systolic of the anaesthesia chart."
        >
          <Input type="number" inputMode="numeric" value={systolic} onChange={(_, d) => setSystolic(d.value)} />
        </Field>
      </div>
      <ActionError error={receive.error} />
      <div className={styles.actions}>
        <Button
          appearance="primary"
          disabled={receive.isPending}
          icon={receive.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          onClick={() => receive.mutate()}
        >
          Receive in recovery
        </Button>
      </div>
    </div>
  );
}

function Handover({
  booking,
  record,
  canRecord,
}: {
  booking: SurgeryDetail;
  record: RecoveryRecordDto;
  canRecord: boolean;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const saved = useSaved(booking);
  const isAnaesthetist = usePermission('ot.anaesthesia.record');
  const [witness, setWitness] = useState<WitnessConfirmed | null>(null);
  const [notes, setNotes] = useState('');

  const handover = useMutation({
    mutationFn: (confirmationId: string | null) =>
      otApi.recoveryHandover(booking.id, confirmationId, notes.trim() || null),
    onSuccess: (updated) => {
      notify.success('Handover recorded');
      setWitness(null);
      saved(updated);
    },
    onError: () => setWitness(null),
  });

  return (
    <Section title="Handover from the anaesthetist">
      {record.handoverAt ? (
        <Text>
          Handed over by {record.handedOverByName} at {formatTime(record.handoverAt)}
          {record.handoverConfirmedAtScreen ? ', confirmed at the recovery nurse’s screen' : ''}.
          {record.handoverNotes ? ` ${record.handoverNotes}` : ''}
        </Text>
      ) : !canRecord ? (
        <Text className={styles.muted}>Not recorded.</Text>
      ) : (
        <div className={styles.form} role="group" aria-label="Record the handover">
          {!record.handoverRequired && (
            <Text className={styles.muted}>
              The surgeon gave the anaesthetic, so no handover is needed before the discharge.
            </Text>
          )}
          <Field label="Handover notes">
            <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} resize="vertical" />
          </Field>
          {isAnaesthetist ? (
            <div className={styles.actions}>
              <Button appearance="primary" disabled={handover.isPending} onClick={() => handover.mutate(null)}>
                Record my handover
              </Button>
            </div>
          ) : (
            <>
              <WitnessPanel
                purpose="ot.recovery_handover"
                patientId={booking.patientId}
                witness={witness}
                onChange={setWitness}
                legend="The anaesthetist, at this screen"
                description="The anaesthetist hands over by typing their own user name and password here. Nothing is signed in, and the password is not kept."
                confirmedLabel="Handed over by"
                who="Anaesthetist"
              />
              <div className={styles.actions}>
                <Button
                  appearance="primary"
                  disabled={!witness || handover.isPending}
                  onClick={() => handover.mutate(witness?.confirmationId ?? null)}
                >
                  {witness ? 'Record the handover' : 'Record the handover (anaesthetist first)'}
                </Button>
              </div>
            </>
          )}
          <ActionError error={handover.error} />
        </div>
      )}
    </Section>
  );
}

function Score({ record }: { record: RecoveryRecordDto }) {
  const styles = useStyles();
  const { score } = record;
  return (
    <Section
      title="Modified Aldrete score"
      description="From the latest observations. 9 or more of 10 to go back to the ward; ICU or HDU at any score."
    >
      <Text className={styles.total} role="status">
        {score.total !== null ? `${score.total} of 10` : 'Not complete'}
        {score.at ? ` (observations at ${formatTime(score.at)})` : ''}
      </Text>
      <Table aria-label="Modified Aldrete score" size="small">
        <TableHeader>
          <TableRow>
            <TableHeaderCell>Sign</TableHeaderCell>
            <TableHeaderCell>Points</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {score.components.map((c) => (
            <TableRow key={c.sign}>
              <TableCell>{aldreteSignLabel[c.sign]}</TableCell>
              <TableCell>{c.points !== null ? `${c.points} of 2` : `Missing: ${c.missing ?? ''}`}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {!record.dischargedAt && score.wardBlockedBy && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Not yet fit for the ward on the score</MessageBarTitle>
            {score.wardBlockedBy}
          </MessageBarBody>
        </MessageBar>
      )}
    </Section>
  );
}

function Observations({
  booking,
  record,
  canRecord,
}: {
  booking: SurgeryDetail;
  record: RecoveryRecordDto;
  canRecord: boolean;
}) {
  const styles = useStyles();
  return (
    <Section title="Observations">
      {record.observations.length === 0 ? (
        <Text className={styles.muted}>No observations yet. Chart them every 5 minutes at first, then every 15.</Text>
      ) : (
        <div className={styles.scroll}>
          <Table aria-label="Recovery observations" size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Time</TableHeaderCell>
                <TableHeaderCell>Pulse</TableHeaderCell>
                <TableHeaderCell>BP</TableHeaderCell>
                <TableHeaderCell>SpO₂</TableHeaderCell>
                <TableHeaderCell>Resp.</TableHeaderCell>
                <TableHeaderCell>Temp.</TableHeaderCell>
                <TableHeaderCell>Consciousness</TableHeaderCell>
                <TableHeaderCell>Breathing</TableHeaderCell>
                <TableHeaderCell>Limbs</TableHeaderCell>
                <TableHeaderCell>Pain</TableHeaderCell>
                <TableHeaderCell>PONV</TableHeaderCell>
                <TableHeaderCell>By</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {record.observations.map((o) => (
                <TableRow key={o.id}>
                  <TableCell>{formatTime(o.at)}</TableCell>
                  <TableCell>{o.heartRate ?? '—'}</TableCell>
                  <TableCell>{o.systolic ? `${o.systolic}/${o.diastolic ?? '—'}` : '—'}</TableCell>
                  <TableCell>{o.spO2 ? `${o.spO2}%${o.onOxygen ? ' on O₂' : ''}` : '—'}</TableCell>
                  <TableCell>{o.respiratoryRate ?? '—'}</TableCell>
                  <TableCell>{o.temperatureC ?? '—'}</TableCell>
                  <TableCell>{o.consciousness ? consciousnessLabel[o.consciousness] : '—'}</TableCell>
                  <TableCell>{o.breathing ? breathingLabel[o.breathing] : '—'}</TableCell>
                  <TableCell>{o.limbsMoving ?? '—'}</TableCell>
                  <TableCell>{o.painScore ?? '—'}</TableCell>
                  <TableCell>{o.ponv ? ponvLabel[o.ponv] : '—'}</TableCell>
                  <TableCell>{o.recordedByName}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
      {canRecord && <ObservationForm booking={booking} />}
    </Section>
  );
}

function ObservationForm({ booking }: { booking: SurgeryDetail }) {
  const styles = useStyles();
  const notify = useNotify();
  const saved = useSaved(booking);
  const [values, setValues] = useState({
    heartRate: '',
    systolic: '',
    diastolic: '',
    spO2: '',
    respiratoryRate: '',
    temperatureC: '',
    limbsMoving: '',
    painScore: '',
  });
  const [onOxygen, setOnOxygen] = useState(false);
  const [consciousness, setConsciousness] = useState<RecoveryConsciousness | ''>('');
  const [breathing, setBreathing] = useState<RecoveryBreathing | ''>('');
  const [ponv, setPonv] = useState<Ponv | ''>('');
  const set = (key: keyof typeof values) => (_: unknown, d: { value: string }) =>
    setValues((v) => ({ ...v, [key]: d.value }));

  const add = useMutation({
    mutationFn: () =>
      otApi.addRecoveryObservation(booking.id, {
        heartRate: numberOrNull(values.heartRate),
        systolic: numberOrNull(values.systolic),
        diastolic: numberOrNull(values.diastolic),
        spO2: numberOrNull(values.spO2),
        onOxygen,
        respiratoryRate: numberOrNull(values.respiratoryRate),
        temperatureC: numberOrNull(values.temperatureC),
        consciousness: consciousness || null,
        breathing: breathing || null,
        limbsMoving: numberOrNull(values.limbsMoving),
        painScore: numberOrNull(values.painScore),
        ponv: ponv || null,
      }),
    onSuccess: (record) => {
      notify.success('Observations recorded');
      setValues({
        heartRate: '',
        systolic: '',
        diastolic: '',
        spO2: '',
        respiratoryRate: '',
        temperatureC: '',
        limbsMoving: '',
        painScore: '',
      });
      saved(record);
    },
  });

  return (
    <div className={styles.form} role="group" aria-label="Record observations">
      <Text weight="semibold">Record observations now</Text>
      <div className={styles.row}>
        <Field label="Pulse (/min)">
          <Input type="number" inputMode="numeric" value={values.heartRate} onChange={set('heartRate')} />
        </Field>
        <Field label="Systolic (mmHg)">
          <Input type="number" inputMode="numeric" value={values.systolic} onChange={set('systolic')} />
        </Field>
        <Field label="Diastolic (mmHg)">
          <Input type="number" inputMode="numeric" value={values.diastolic} onChange={set('diastolic')} />
        </Field>
        <Field label="SpO₂ (%)">
          <Input type="number" inputMode="numeric" value={values.spO2} onChange={set('spO2')} />
        </Field>
        <Switch checked={onOxygen} onChange={(_, d) => setOnOxygen(d.checked)} label="On oxygen" />
        <Field label="Respiration (/min)">
          <Input type="number" inputMode="numeric" value={values.respiratoryRate} onChange={set('respiratoryRate')} />
        </Field>
        <Field label="Temperature (°C)">
          <Input type="number" inputMode="decimal" value={values.temperatureC} onChange={set('temperatureC')} />
        </Field>
        <Field label="Consciousness">
          <Select value={consciousness} onChange={(_, d) => setConsciousness(d.value as RecoveryConsciousness | '')}>
            <option value="">Not assessed</option>
            {CONSCIOUSNESS_LEVELS.map((c) => (
              <option key={c} value={c}>
                {consciousnessLabel[c]}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Breathing">
          <Select value={breathing} onChange={(_, d) => setBreathing(d.value as RecoveryBreathing | '')}>
            <option value="">Not assessed</option>
            {BREATHING.map((b) => (
              <option key={b} value={b}>
                {breathingLabel[b]}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Limbs moved on command (0–4)">
          <Input
            type="number"
            inputMode="numeric"
            min={0}
            max={4}
            value={values.limbsMoving}
            onChange={set('limbsMoving')}
          />
        </Field>
        <Field label="Pain score (0–10)">
          <Input
            type="number"
            inputMode="numeric"
            min={0}
            max={10}
            value={values.painScore}
            onChange={set('painScore')}
          />
        </Field>
        <Field label="Nausea and vomiting">
          <Select value={ponv} onChange={(_, d) => setPonv(d.value as Ponv | '')}>
            <option value="">Not assessed</option>
            {PONV.map((p) => (
              <option key={p} value={p}>
                {ponvLabel[p]}
              </option>
            ))}
          </Select>
        </Field>
      </div>
      <ActionError error={add.error} />
      <div className={styles.actions}>
        <Button appearance="primary" disabled={add.isPending} onClick={() => add.mutate()}>
          Record observations
        </Button>
      </div>
    </div>
  );
}

function Discharge({ booking, record }: { booking: SurgeryDetail; record: RecoveryRecordDto }) {
  const styles = useStyles();
  const notify = useNotify();
  const saved = useSaved(booking);
  const isAnaesthetist = usePermission('ot.anaesthesia.record');
  const [destination, setDestination] = useState<RecoveryDestination>('Ward');
  const [reason, setReason] = useState('');
  const blocked = destination === 'Ward' ? record.score.wardBlockedBy : null;
  const handoverMissing = record.handoverRequired && !record.handoverAt;

  const onDone = (updated: RecoveryRecordDto) => {
    notify.success(`Discharged from recovery to the ${destinationLabel[updated.destination ?? destination]}`);
    saved(updated);
  };
  const discharge = useMutation({
    mutationFn: () => otApi.dischargeFromRecovery(booking.id, destination),
    onSuccess: onDone,
  });
  const override = useMutation({
    mutationFn: () => otApi.overrideRecoveryDischarge(booking.id, reason.trim()),
    onSuccess: onDone,
  });

  return (
    <Section title="Discharge from recovery">
      <div className={styles.form} role="group" aria-label="Discharge from recovery">
        <Field label="To">
          <Select value={destination} onChange={(_, d) => setDestination(d.value as RecoveryDestination)}>
            {RECOVERY_DESTINATIONS.map((d) => (
              <option key={d} value={d}>
                {destinationLabel[d]}
              </option>
            ))}
          </Select>
        </Field>
        {handoverMissing && (
          <Text className={styles.muted}>Record the anaesthetist’s handover before the patient leaves recovery.</Text>
        )}
        {blocked && (
          <Text className={styles.muted}>
            The ward takes the patient at a score of 9 or more, or on the anaesthetist’s override.
          </Text>
        )}
        <ActionError error={discharge.error} />
        <div className={styles.actions}>
          <Button
            appearance="primary"
            disabled={Boolean(blocked) || handoverMissing || discharge.isPending}
            onClick={() => discharge.mutate()}
          >
            Discharge to the {destinationLabel[destination]}
          </Button>
        </div>
        {blocked && isAnaesthetist && !handoverMissing && (
          <div className={styles.form} role="group" aria-label="Anaesthetist’s override">
            <Text weight="semibold">Anaesthetist’s override</Text>
            <Text className={styles.muted}>
              You send the patient to the ward below the score, from your own sign-in. It is recorded with your reason.
            </Text>
            <Field label="Reason" required>
              <Textarea value={reason} onChange={(_, d) => setReason(d.value)} resize="vertical" />
            </Field>
            <ActionError error={override.error} />
            <div className={styles.actions}>
              <Button disabled={!reason.trim() || override.isPending} onClick={() => override.mutate()}>
                Send to the ward below the score
              </Button>
            </div>
          </div>
        )}
      </div>
    </Section>
  );
}

function Discharged({ record }: { record: RecoveryRecordDto }) {
  return (
    <MessageBar intent={record.overrideReason ? 'warning' : 'success'} layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>
          Discharged to the {record.destination ? destinationLabel[record.destination] : 'ward'}
          {record.dischargedAt ? ` at ${formatTime(record.dischargedAt)}` : ''}
        </MessageBarTitle>
        By {record.dischargedByName}
        {record.dischargeScore !== null ? `, modified Aldrete score ${record.dischargeScore} of 10` : ''}.
        {record.overrideReason ? ` Override by ${record.overrideByName}: ${record.overrideReason}` : ''}
      </MessageBarBody>
    </MessageBar>
  );
}
