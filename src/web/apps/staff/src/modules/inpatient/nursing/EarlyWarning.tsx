import {
  Badge,
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
import { AlertUrgentRegular, HeartPulseRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, formatTime, isApiError } from '@hc360/core';
import { applyServerErrors, EmptyState, ErrorState, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { inpatientKeys } from '../api';
import {
  CONSCIOUSNESS_LABEL,
  CONSCIOUSNESS_LEVELS,
  ESCALATION_OUTCOME_LABEL,
  news2Advice,
  news2Label,
  news2Tone,
  wardCareApi,
  wardCareKeys,
  wardCareQueries,
  type EscalationDto,
  type EscalationOutcome,
  type WardVitalsDto,
} from '../wardCareApi';

const useStyles = makeStyles({
  form: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '12px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: '10px' },
  row: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap', justifyContent: 'space-between' },
  list: { display: 'flex', flexDirection: 'column', gap: '8px' },
  escalation: {
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  emergency: { borderLeft: `4px solid ${tokens.colorPaletteRedBorderActive}` },
  urgent: { borderLeft: `4px solid ${tokens.colorPaletteDarkOrangeBorderActive}` },
  muted: { color: tokens.colorNeutralForeground3 },
});

const optionalNumber = (min: number, max: number, label: string) =>
  z
    .string()
    .trim()
    .refine((v) => v === '' || (!Number.isNaN(Number(v)) && Number(v) >= min && Number(v) <= max), {
      message: `${label} must be between ${min} and ${max}.`,
    });

const schema = z.object({
  respiratoryRate: optionalNumber(4, 80, 'Respiration rate'),
  spO2: optionalNumber(30, 100, 'SpO₂'),
  scale2: z.boolean(),
  onOxygen: z.boolean(),
  systolicBp: optionalNumber(40, 300, 'Systolic pressure'),
  diastolicBp: optionalNumber(20, 200, 'Diastolic pressure'),
  pulse: optionalNumber(20, 300, 'Pulse'),
  consciousness: z.string(),
  temperature: optionalNumber(25, 45, 'Temperature'),
});

type ObservationValues = z.infer<typeof schema>;

const EMPTY: ObservationValues = {
  respiratoryRate: '',
  spO2: '',
  scale2: false,
  onOxygen: false,
  systolicBp: '',
  diastolicBp: '',
  pulse: '',
  consciousness: '',
  temperature: '',
};

const toNumber = (v: string): number | null => (v.trim() === '' ? null : Number(v));

/**
 * The NEWS2 observation chart of a stay: record the seven parameters (respiration, SpO₂ on scale 1 or 2, air or oxygen,
 * systolic pressure, pulse, ACVPU and temperature) and see each entry's score. A score of 5 or more, or 3 in one
 * parameter, raises an escalation to the ward's doctor and the nurses in charge, answered below.
 */
export function PatientObservations({
  admissionId,
  patientId,
  admittedAt,
  canWrite,
}: {
  admissionId: string;
  patientId: string;
  admittedAt: string;
  canWrite: boolean;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const vitals = useQuery(wardCareQueries.vitals(patientId, admittedAt.slice(0, 10)));
  const escalations = useQuery(wardCareQueries.escalations(admissionId));
  const form = useForm<ObservationValues>({ resolver: zodResolver(schema), defaultValues: EMPTY });

  const record = useMutation({
    mutationFn: (v: ObservationValues) =>
      wardCareApi.recordObservations(patientId, {
        encounterId: null,
        recordedAt: null,
        temperature: toNumber(v.temperature),
        temperatureUnit: 'Celsius',
        pulse: toNumber(v.pulse),
        respiratoryRate: toNumber(v.respiratoryRate),
        systolicBp: toNumber(v.systolicBp),
        diastolicBp: toNumber(v.diastolicBp),
        spO2: toNumber(v.spO2),
        onSupplementalOxygen: v.onOxygen,
        heightCm: null,
        weightKg: null,
        painScore: null,
        bloodGlucose: null,
        glucoseTiming: 'Random',
        gcs: null,
        notes: null,
        consciousness: v.consciousness ? (v.consciousness as WardVitalsDto['consciousness']) : null,
        spO2Scale: v.scale2 ? 'Scale2' : 'Scale1',
      }),
    onSuccess: (saved) => {
      form.reset(EMPTY);
      const score = saved.news2;
      if (score && score.response !== 'Routine') {
        notify.warning(`${news2Label(score.total, score.isComplete)}: escalated`, news2Advice(score.response));
      } else {
        notify.success(
          score ? `Observations saved: ${news2Label(score.total, score.isComplete)}` : 'Observations saved',
        );
      }
      void queryClient.invalidateQueries({ queryKey: wardCareKeys.all });
      void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the observations');
      }
    },
  });

  const number = (name: keyof ObservationValues, label: string, unit?: string) => (
    <Controller
      control={form.control}
      name={name}
      render={({ field, fieldState }) => (
        <Field
          label={label}
          validationMessage={fieldState.error?.message}
          validationState={fieldState.error ? 'error' : 'none'}
        >
          <Input
            inputMode="decimal"
            value={String(field.value)}
            onChange={(_, d) => field.onChange(d.value)}
            contentAfter={unit ? <Caption1>{unit}</Caption1> : undefined}
          />
        </Field>
      )}
    />
  );

  const open = (escalations.data ?? []).filter((e) => e.status === 'Open');
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      {open.length > 0 && (
        <EscalationList escalations={open} canRespond={canWrite} title="Escalations waiting for a response" />
      )}
      {canWrite && (
        <form
          onSubmit={form.handleSubmit((v) => record.mutate(v))}
          noValidate
          className={styles.form}
          aria-label="Record observations"
        >
          <div className={styles.grid}>
            {number('respiratoryRate', 'Respiration', '/min')}
            {number('spO2', 'SpO₂', '%')}
            {number('systolicBp', 'Systolic BP', 'mmHg')}
            {number('diastolicBp', 'Diastolic BP', 'mmHg')}
            {number('pulse', 'Pulse', '/min')}
            {number('temperature', 'Temperature', '°C')}
            <Controller
              control={form.control}
              name="consciousness"
              render={({ field }) => (
                <Field label="Consciousness (ACVPU)">
                  <Select value={field.value} onChange={(_, d) => field.onChange(d.value)}>
                    <option value="">Not assessed</option>
                    {CONSCIOUSNESS_LEVELS.map((c) => (
                      <option key={c} value={c}>
                        {CONSCIOUSNESS_LABEL[c]}
                      </option>
                    ))}
                  </Select>
                </Field>
              )}
            />
          </div>
          <div className={styles.row}>
            <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}>
              <Controller
                control={form.control}
                name="onOxygen"
                render={({ field }) => (
                  <Switch
                    checked={field.value}
                    onChange={(_, d) => field.onChange(d.checked)}
                    label="On supplemental oxygen"
                  />
                )}
              />
              <Controller
                control={form.control}
                name="scale2"
                render={({ field }) => (
                  <Switch
                    checked={field.value}
                    onChange={(_, d) => field.onChange(d.checked)}
                    label="SpO₂ scale 2 (doctor prescribed target 88–92%)"
                  />
                )}
              />
            </div>
            <Button
              type="submit"
              appearance="primary"
              icon={record.isPending ? <Spinner size="tiny" aria-hidden /> : <HeartPulseRegular />}
              disabled={record.isPending}
            >
              Save observations
            </Button>
          </div>
        </form>
      )}

      <Section
        title="Observations this stay"
        description="NEWS2 for adults (16 and over). Children are not scored: a paediatric score is not part of this release."
      >
        {vitals.error ? (
          <ErrorState error={vitals.error} onRetry={() => void vitals.refetch()} />
        ) : vitals.isLoading ? (
          <Spinner size="small" label="Loading observations" />
        ) : (vitals.data ?? []).filter((v) => !v.isEnteredInError).length === 0 ? (
          <EmptyState title="No observations yet" description="Record the first set above." />
        ) : (
          <ObservationTable vitals={(vitals.data ?? []).filter((v) => !v.isEnteredInError)} />
        )}
      </Section>

      {(escalations.data ?? []).some((e) => e.status === 'Responded') && (
        <EscalationList
          escalations={(escalations.data ?? []).filter((e) => e.status === 'Responded')}
          canRespond={false}
          title="Escalations answered"
        />
      )}
    </div>
  );
}

function ObservationTable({ vitals }: { vitals: WardVitalsDto[] }) {
  return (
    <div style={{ overflowX: 'auto' }}>
      <Table size="small" aria-label="Observations with NEWS2">
        <TableHeader>
          <TableRow>
            <TableHeaderCell>Time</TableHeaderCell>
            <TableHeaderCell>Resp.</TableHeaderCell>
            <TableHeaderCell>SpO₂</TableHeaderCell>
            <TableHeaderCell>Air / O₂</TableHeaderCell>
            <TableHeaderCell>BP</TableHeaderCell>
            <TableHeaderCell>Pulse</TableHeaderCell>
            <TableHeaderCell>ACVPU</TableHeaderCell>
            <TableHeaderCell>Temp.</TableHeaderCell>
            <TableHeaderCell>NEWS2</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {vitals.map((v) => (
            <TableRow key={v.id}>
              <TableCell>{formatDateTime(v.recordedAt)}</TableCell>
              <TableCell>{v.respiratoryRate ?? '—'}</TableCell>
              <TableCell>
                {v.spO2 ?? '—'}
                {v.spO2 !== null && v.spO2Scale === 'Scale2' ? ' (scale 2)' : ''}
              </TableCell>
              <TableCell>{v.onSupplementalOxygen ? 'O₂' : 'Air'}</TableCell>
              <TableCell>{v.systolicBp !== null ? `${v.systolicBp}/${v.diastolicBp ?? '—'}` : '—'}</TableCell>
              <TableCell>{v.pulse ?? '—'}</TableCell>
              <TableCell>{v.consciousness ? CONSCIOUSNESS_LABEL[v.consciousness].charAt(0) : '—'}</TableCell>
              <TableCell>{v.temperatureCelsius ?? '—'}</TableCell>
              <TableCell>
                {v.news2 ? (
                  <Badge
                    appearance={v.news2.response === 'Routine' ? 'tint' : 'filled'}
                    color={news2Tone(v.news2.response)}
                    title={v.news2.parts.map((p) => `${p.parameter} ${p.value}: ${p.points}`).join('; ')}
                  >
                    {news2Label(v.news2.total, v.news2.isComplete)}
                  </Badge>
                ) : (
                  '—'
                )}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

/** Escalations as cards: what scored, when, and the response (or a button to record it). */
export function EscalationList({
  escalations,
  canRespond,
  title,
  onOpenPatient,
}: {
  escalations: EscalationDto[];
  canRespond: boolean;
  title: string;
  onOpenPatient?: (admissionId: string) => void;
}) {
  const styles = useStyles();
  const [responding, setResponding] = useState<EscalationDto | null>(null);
  return (
    <Section title={title}>
      <div className={styles.list}>
        {escalations.map((e) => (
          <div
            key={e.id}
            className={`${styles.escalation} ${e.level === 'Emergency' ? styles.emergency : styles.urgent}`}
          >
            <div className={styles.row}>
              <Text weight="semibold">
                <Badge
                  appearance="filled"
                  color={e.level === 'Emergency' ? 'danger' : 'warning'}
                  icon={<AlertUrgentRegular />}
                >
                  {e.level === 'Emergency' ? 'Emergency' : 'Urgent'}
                </Badge>{' '}
                {e.summary}
              </Text>
              {e.status === 'Open' && canRespond && (
                <Button size="small" appearance="primary" onClick={() => setResponding(e)}>
                  Record response
                </Button>
              )}
              {onOpenPatient && (
                <Button size="small" onClick={() => onOpenPatient(e.admissionId)}>
                  Open {e.bedLabel ? `bed ${e.bedLabel}` : e.uhid}
                </Button>
              )}
            </div>
            <Caption1 className={styles.muted}>
              {e.uhid}
              {e.bedLabel ? ` · bed ${e.bedLabel}` : ''} · observations at {formatTime(e.vitalsRecordedAt)}
              {e.status === 'Open' && e.openMinutes !== null ? ` · waiting ${e.openMinutes} min` : ''}
            </Caption1>
            {e.status === 'Responded' && (
              <Caption1>
                {e.respondedByName ?? 'Someone'}
                {e.respondedByDoctor ? ' (doctor)' : ''} responded at {e.respondedAt ? formatTime(e.respondedAt) : '—'}
                {e.minutesToRespond !== null ? `, after ${e.minutesToRespond} min` : ''}:{' '}
                {e.outcome ? ESCALATION_OUTCOME_LABEL[e.outcome] : ''}. {e.response}
              </Caption1>
            )}
          </div>
        ))}
      </div>
      {responding && <RespondDialog escalation={responding} onClose={() => setResponding(null)} />}
    </Section>
  );
}

function RespondDialog({ escalation, onClose }: { escalation: EscalationDto; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [outcome, setOutcome] = useState<EscalationOutcome>('ReviewedContinueObservation');
  const [response, setResponse] = useState('');
  const respond = useMutation({
    mutationFn: () => wardCareApi.respond(escalation.id, { outcome, response, version: escalation.version }),
    onSuccess: () => {
      notify.success('Response recorded');
      void queryClient.invalidateQueries({ queryKey: wardCareKeys.all });
      void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
      onClose();
    },
  });
  const error = respond.error;
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !respond.isPending && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Response to {escalation.summary}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Caption1>
              Recorded as you, now. Whoever took the observations cannot record the response: the escalation brings in a
              second clinician.
            </Caption1>
            <Field label="Outcome" required>
              <Select value={outcome} onChange={(_, d) => setOutcome(d.value as EscalationOutcome)}>
                {(Object.keys(ESCALATION_OUTCOME_LABEL) as EscalationOutcome[]).map((o) => (
                  <option key={o} value={o}>
                    {ESCALATION_OUTCOME_LABEL[o]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="What was done" required>
              <Textarea value={response} onChange={(_, d) => setResponse(d.value)} maxLength={1000} rows={3} />
            </Field>
            {error && (
              <MessageBar intent="error" layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>{isApiError(error) ? error.title : 'Could not record the response'}</MessageBarTitle>
                  {isApiError(error) ? error.userMessage : 'Please try again.'}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={respond.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={respond.isPending || response.trim().length < 3}
              onClick={() => respond.mutate()}
              icon={respond.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
            >
              Record response
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/** The ward's open escalations, newest first, for the ward overview. */
export function WardEscalations({
  wardId,
  onOpenPatient,
}: {
  wardId: string;
  onOpenPatient: (admissionId: string) => void;
}) {
  const escalations = useQuery(wardCareQueries.wardEscalations(wardId));
  if (escalations.error) {
    return <ErrorState error={escalations.error} onRetry={() => void escalations.refetch()} />;
  }
  const open = escalations.data ?? [];
  if (open.length === 0) {
    return null;
  }
  return (
    <EscalationList
      escalations={open}
      canRespond
      title="NEWS2 escalations waiting for a response"
      onOpenPatient={onOpenPatient}
    />
  );
}
