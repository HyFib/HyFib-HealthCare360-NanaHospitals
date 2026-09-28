import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, BroomRegular, CheckmarkRegular, PlayRegular, SaveRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, formatNumber, formatTime } from '@hc360/core';
import { ConfirmDialog, DataTable, FormGrid, FullRow, LoadingState, NumberField, TextAreaField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { fromIstInput, nowIstInput } from '@/modules/ot/shared/datetime';
import { applyFormErrors } from '@/modules/ot/shared/errors';
import { ActionError, Detail, DetailGrid } from '@/modules/ot/shared/feedback';
import { CheckboxGroupField, DateTimeField } from '@/modules/ot/shared/fields';
import { TrendChart } from '@/modules/ot/shared/TrendChart';
import { COMPLICATIONS, dialysisApi, dialysisKeys, dialysisQueries, type DialysisSessionDetail, type DialyzerDto } from './api';
import { MachinePicker } from './DialysisBadges';
import { DisinfectionDialog } from './DialysisDialogs';
import { complicationLabel } from './labels';
import { checkDialyzer, machineChoices, nextUseLabel, suggestedUfMl, weightAboveDry, weightLossMl } from './logic';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  footer: { display: 'flex', gap: '8px', justifyContent: 'flex-end', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  readings: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(120px, 1fr))', gap: '8px 12px', alignItems: 'end' },
  wide: { gridColumn: 'span 2' },
  dialyzer: { padding: '8px 10px', borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}` },
  blocked: { backgroundColor: tokens.colorNeutralBackground3 },
  reason: { color: tokens.colorPaletteRedForeground1, paddingLeft: '28px', display: 'block' },
});

const inRange = (value: number | null, min: number, max: number) => value === null || (value >= min && value <= max);

// ---------------------------------------------------------------- before dialysis

const preSchema = z
  .object({
    weightKg: z.number().nullable(),
    systolic: z.number().nullable(),
    diastolic: z.number().nullable(),
    pulse: z.number().nullable(),
    temperatureC: z.number().nullable(),
    targetUfMl: z.number().nullable(),
  })
  .superRefine((v, ctx) => {
    const need: [keyof typeof v, string, number, number][] = [
      ['weightKg', 'Enter the weight (0.5–350 kg).', 0.5, 350],
      ['systolic', 'Enter the systolic pressure (40–300).', 40, 300],
      ['diastolic', 'Enter the diastolic pressure (20–200).', 20, 200],
      ['pulse', 'Enter the pulse (20–250).', 20, 250],
    ];
    for (const [key, message, min, max] of need) {
      const value = v[key];
      if (value === null || value < min || value > max) {
        ctx.addIssue({ code: 'custom', path: [key], message });
      }
    }
    if (!inRange(v.temperatureC, 30, 45)) {
      ctx.addIssue({ code: 'custom', path: ['temperatureC'], message: 'Enter a temperature between 30 and 45 °C.' });
    }
    if (!inRange(v.targetUfMl, 0, 6000)) {
      ctx.addIssue({ code: 'custom', path: ['targetUfMl'], message: 'Target ultrafiltration must be between 0 and 6000 ml.' });
    }
  });

type PreValues = z.infer<typeof preSchema>;

/** Pre-dialysis weight, blood pressure, pulse and temperature, and the ultrafiltration goal. */
export function PreAssessmentPanel({ session, canRecord }: { session: DialysisSessionDetail; canRecord: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const editable = canRecord && session.status === 'Scheduled';
  const form = useForm<PreValues>({
    resolver: zodResolver(preSchema),
    defaultValues: {
      weightKg: session.preWeightKg,
      systolic: session.preSystolic,
      diastolic: session.preDiastolic,
      pulse: session.prePulse,
      temperatureC: session.preTemperatureC,
      targetUfMl: session.targetUfMl,
    },
  });
  const [weightKg] = useWatch({ control: form.control, name: ['weightKg'] });
  const gain = weightAboveDry(weightKg, session.dryWeightKg);
  const suggestion = suggestedUfMl(weightKg, session.dryWeightKg);

  const save = useMutation({
    mutationFn: (v: PreValues) =>
      dialysisApi.preAssessment(session.id, {
        weightKg: v.weightKg ?? 0,
        systolic: v.systolic ?? 0,
        diastolic: v.diastolic ?? 0,
        pulse: v.pulse ?? 0,
        temperatureC: v.temperatureC,
        targetUfMl: v.targetUfMl,
      }),
    onSuccess: () => {
      notify.success('Pre-dialysis assessment saved');
      void queryClient.invalidateQueries({ queryKey: dialysisKeys.session(session.id) });
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  if (!editable) {
    return session.preAssessedAt ? (
      <DetailGrid>
        <Detail label="Weight">{session.preWeightKg !== null ? `${session.preWeightKg} kg` : null}</Detail>
        <Detail label="Above dry weight">{weightAboveDry(session.preWeightKg, session.dryWeightKg) !== null ? `${weightAboveDry(session.preWeightKg, session.dryWeightKg)} kg` : null}</Detail>
        <Detail label="Blood pressure">{session.preSystolic ? `${session.preSystolic}/${session.preDiastolic} mmHg` : null}</Detail>
        <Detail label="Pulse">{session.prePulse ? `${session.prePulse}/min` : null}</Detail>
        <Detail label="Temperature">{session.preTemperatureC !== null ? `${session.preTemperatureC} °C` : null}</Detail>
        <Detail label="Target UF">{session.targetUfMl !== null ? `${formatNumber(session.targetUfMl)} ml` : null}</Detail>
        <Detail label="Recorded">{formatDateTime(session.preAssessedAt)}</Detail>
      </DetailGrid>
    ) : (
      <Text className={styles.muted}>Not recorded.</Text>
    );
  }

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate className={styles.stack}>
      <FormGrid columns={3}>
        <NumberField control={form.control} name="weightKg" label="Weight before (kg)" required min={0.5} max={350} step={0.1} />
        <NumberField control={form.control} name="systolic" label="Systolic BP" required min={40} max={300} />
        <NumberField control={form.control} name="diastolic" label="Diastolic BP" required min={20} max={200} />
        <NumberField control={form.control} name="pulse" label="Pulse (/min)" required min={20} max={250} />
        <NumberField control={form.control} name="temperatureC" label="Temperature (°C)" min={30} max={45} step={0.1} />
        <div>
          <NumberField control={form.control} name="targetUfMl" label="Target ultrafiltration (ml)" min={0} max={6000} step={100} />
          {suggestion !== null && (
            <Button size="small" appearance="subtle" onClick={() => form.setValue('targetUfMl', suggestion, { shouldDirty: true })}>
              Use {formatNumber(suggestion)} ml
            </Button>
          )}
        </div>
      </FormGrid>
      <Caption1 className={styles.muted}>
        {session.dryWeightKg !== null ? `Dry weight ${session.dryWeightKg} kg` : 'Dry weight not set'}
        {gain !== null ? ` · ${gain >= 0 ? `${gain} kg above` : `${Math.abs(gain)} kg below`} dry weight (1 kg ≈ 1,000 ml)` : ''}. The goal is a clinical decision.
      </Caption1>
      <ActionError error={save.error} />
      <div className={styles.footer}>
        <Button type="submit" appearance="primary" icon={save.isPending ? <Spinner size="tiny" /> : <SaveRegular />} disabled={save.isPending}>
          Save pre-dialysis assessment
        </Button>
      </div>
    </form>
  );
}

// ---------------------------------------------------------------- connect and start

type DialyzerChoice = 'new' | string;

/**
 * Connects the patient. The machine must be of the patient's sero category, in service, free and disinfected; the
 * dialyzer must be the patient's own with uses left (HBsAg-positive patients are always single use).
 */
export function StartPanel({ session, canRecord }: { session: DialysisSessionDetail; canRecord: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const board = useQuery(dialysisQueries.board(session.scheduledDate));
  const dialyzers = useQuery(dialysisQueries.dialyzers(session.patientId));
  const profile = useQuery(dialysisQueries.profile(session.patientId));
  const [machineId, setMachineId] = useState<string | null>(session.machineId);
  const [dialyzer, setDialyzer] = useState<DialyzerChoice | null>(null);
  const [newSerial, setNewSerial] = useState('');
  const [newBrand, setNewBrand] = useState('');
  const [newModel, setNewModel] = useState('');
  const [newArea, setNewArea] = useState('');
  const [heparinLoading, setHeparinLoading] = useState('');
  const [heparinHourly, setHeparinHourly] = useState('');
  const [disinfecting, setDisinfecting] = useState<{ id: string; code: string } | null>(null);

  const sero = session.seroStatus;
  const choices = machineChoices(board.data, sero, session.shift, 'start', session.id);
  const chosenMachine = choices.find((c) => c.id === machineId);
  const usable = (dialyzers.data ?? []).filter((d) => !d.isDiscarded);
  const singleUse = (profile.data?.effectiveMaxDialyzerUses ?? 1) === 1;

  const start = useMutation({
    mutationFn: () =>
      dialysisApi.start(session.id, {
        machineId: machineId ?? '',
        dialyzerId: dialyzer && dialyzer !== 'new' ? dialyzer : null,
        newDialyzer:
          dialyzer === 'new'
            ? { serialNumber: newSerial.trim().toUpperCase(), brand: newBrand.trim() || null, model: newModel.trim() || null, surfaceAreaM2: newArea ? Number(newArea) : null }
            : null,
        heparinLoadingUnits: heparinLoading ? Number(heparinLoading) : null,
        heparinHourlyUnits: heparinHourly ? Number(heparinHourly) : null,
      }),
    onSuccess: () => {
      notify.success('Dialysis started', 'The machine is now in use.');
      void queryClient.invalidateQueries({ queryKey: dialysisKeys.all });
    },
  });

  if (!session.preAssessedAt) {
    return (
      <MessageBar intent="info">
        <MessageBarBody>Record the pre-dialysis weight and blood pressure first.</MessageBarBody>
      </MessageBar>
    );
  }
  if (!canRecord) {
    return <Text className={styles.muted}>The dialysis nurse or technician starts the session.</Text>;
  }
  if (board.isLoading || dialyzers.isLoading) {
    return <LoadingState label="Checking the machine and dialyzers…" />;
  }

  const areaValid = !newArea || (Number(newArea) >= 0.1 && Number(newArea) <= 3);
  const heparinValid =
    (!heparinLoading || (Number(heparinLoading) >= 0 && Number(heparinLoading) <= 10000)) && (!heparinHourly || (Number(heparinHourly) >= 0 && Number(heparinHourly) <= 5000));
  const ready =
    Boolean(chosenMachine?.check.eligible) && dialyzer !== null && (dialyzer !== 'new' || (newSerial.trim().length > 0 && areaValid)) && heparinValid;

  return (
    <div className={styles.stack}>
      <Field label="Machine" hint="Only a machine of the patient's sero category that is free and disinfected can be connected.">
        <MachinePicker choices={choices} value={machineId} onChange={setMachineId} />
      </Field>
      {chosenMachine && !chosenMachine.check.eligible && chosenMachine.check.seroMatch && chosenMachine.check.blockers.some((b) => b.includes('disinfection')) && (
        <div>
          <Button icon={<BroomRegular />} onClick={() => setDisinfecting({ id: chosenMachine.id, code: chosenMachine.code })}>
            Record disinfection of {chosenMachine.code}
          </Button>
        </div>
      )}

      <Field label="Dialyzer" hint={singleUse ? 'Single use for this patient (HBsAg positive or single-use policy): register a new dialyzer.' : 'Reuse the patient’s own dialyzer, or register a new one.'}>
        <RadioGroup value={dialyzer ?? ''} onChange={(_, d) => setDialyzer(d.value)} aria-label="Dialyzer">
          {usable.map((d: DialyzerDto) => {
            const check = checkDialyzer(d, session.patientId);
            return (
              <div key={d.id} className={mergeClasses(styles.dialyzer, !check.usable && styles.blocked)}>
                <Radio
                  value={d.id}
                  disabled={!check.usable}
                  label={`${d.serialNumber}${d.brand ? ` · ${d.brand}` : ''}${d.model ? ` ${d.model}` : ''} — ${check.usable ? nextUseLabel(d) : 'not usable'}`}
                />
                {check.reason && <Caption1 className={styles.reason}>{check.reason}</Caption1>}
              </div>
            );
          })}
          <div className={styles.dialyzer}>
            <Radio value="new" label="Register a new dialyzer (first use)" />
          </div>
        </RadioGroup>
      </Field>
      {dialyzer === 'new' && (
        <FormGrid columns={4}>
          <Field label="Serial number" required>
            <InputLike value={newSerial} onChange={setNewSerial} placeholder="Scan or type" />
          </Field>
          <Field label="Brand">
            <InputLike value={newBrand} onChange={setNewBrand} />
          </Field>
          <Field label="Model">
            <InputLike value={newModel} onChange={setNewModel} />
          </Field>
          <Field label="Surface area (m²)" validationState={areaValid ? 'none' : 'error'} validationMessage={areaValid ? undefined : 'Between 0.1 and 3 m².'}>
            <InputLike value={newArea} onChange={setNewArea} type="number" />
          </Field>
        </FormGrid>
      )}

      <FormGrid columns={2}>
        <Field label="Heparin loading dose (units)">
          <InputLike value={heparinLoading} onChange={setHeparinLoading} type="number" />
        </Field>
        <Field label="Heparin per hour (units)" validationState={heparinValid ? 'none' : 'error'} validationMessage={heparinValid ? undefined : 'Loading 0–10,000; hourly 0–5,000 units.'}>
          <InputLike value={heparinHourly} onChange={setHeparinHourly} type="number" />
        </Field>
      </FormGrid>

      <ActionError error={start.error} />
      <div className={styles.footer}>
        <Button appearance="primary" size="large" icon={start.isPending ? <Spinner size="tiny" /> : <PlayRegular />} disabled={!ready || start.isPending} onClick={() => start.mutate()}>
          Connect and start dialysis
        </Button>
      </div>
      {disinfecting && <DisinfectionDialog machine={disinfecting} onClose={() => setDisinfecting(null)} />}
    </div>
  );
}

/** A controlled text or number input for the few start-panel fields kept in local state. */
function InputLike({ value, onChange, type = 'text', placeholder }: { value: string; onChange: (value: string) => void; type?: 'text' | 'number'; placeholder?: string }) {
  return <Input value={value} onChange={(_, data) => onChange(data.value)} type={type} placeholder={placeholder} />;
}

// ---------------------------------------------------------------- during dialysis

const readingSchema = z
  .object({
    at: z.string().nullable(),
    systolic: z.number().nullable(),
    diastolic: z.number().nullable(),
    pulse: z.number().nullable(),
    bloodFlowMlMin: z.number().nullable(),
    ufRateMlHr: z.number().nullable(),
    venousPressure: z.number().nullable(),
    arterialPressure: z.number().nullable(),
    transmembranePressure: z.number().nullable(),
    notes: z.string().trim().max(300),
  })
  .superRefine((v, ctx) => {
    const at = fromIstInput(v.at);
    if (!at) {
      ctx.addIssue({ code: 'custom', path: ['at'], message: 'Enter the time.' });
    } else if (Date.parse(at) > Date.now() + 5 * 60_000) {
      ctx.addIssue({ code: 'custom', path: ['at'], message: 'The time cannot be in the future.' });
    }
    const ranges: [keyof typeof v, number, number, string][] = [
      ['systolic', 40, 300, 'Systolic 40–300.'],
      ['diastolic', 20, 200, 'Diastolic 20–200.'],
      ['pulse', 20, 250, 'Pulse 20–250.'],
      ['bloodFlowMlMin', 0, 600, 'Blood flow 0–600 ml/min.'],
      ['ufRateMlHr', 0, 3000, 'UF rate 0–3000 ml/h.'],
      ['venousPressure', -500, 500, 'Venous −500 to 500.'],
      ['arterialPressure', -500, 500, 'Arterial −500 to 500.'],
      ['transmembranePressure', -500, 800, 'TMP −500 to 800.'],
    ];
    for (const [key, min, max, message] of ranges) {
      const value = v[key];
      if (typeof value === 'number' && (value < min || value > max)) {
        ctx.addIssue({ code: 'custom', path: [key], message });
      }
    }
  });

type ReadingValues = z.infer<typeof readingSchema>;

const emptyReading = (): ReadingValues => ({
  at: nowIstInput(),
  systolic: null,
  diastolic: null,
  pulse: null,
  bloodFlowMlMin: null,
  ufRateMlHr: null,
  venousPressure: null,
  arterialPressure: null,
  transmembranePressure: null,
  notes: '',
});

/** Hourly intra-dialytic observations with a trend chart. */
export function ReadingsPanel({ session, canRecord }: { session: DialysisSessionDetail; canRecord: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const editable = canRecord && session.status === 'InProgress';
  const form = useForm<ReadingValues>({ resolver: zodResolver(readingSchema), defaultValues: emptyReading() });

  const add = useMutation({
    mutationFn: (v: ReadingValues) => dialysisApi.addReadings(session.id, [{ ...v, at: fromIstInput(v.at) ?? '', notes: v.notes || null }]),
    onSuccess: () => {
      form.reset(emptyReading());
      notify.success('Reading added');
      void queryClient.invalidateQueries({ queryKey: dialysisKeys.session(session.id) });
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  const readings = session.readings;
  const series = [
    { name: 'Systolic', points: readings.map((r) => ({ at: r.at, value: r.systolic })) },
    { name: 'Diastolic', points: readings.map((r) => ({ at: r.at, value: r.diastolic })) },
    { name: 'Pulse', points: readings.map((r) => ({ at: r.at, value: r.pulse })) },
    { name: 'Venous pressure', points: readings.map((r) => ({ at: r.at, value: r.venousPressure })), dashed: true },
  ];

  return (
    <div className={styles.stack}>
      {editable && (
        <form onSubmit={form.handleSubmit((v) => add.mutate(v))} noValidate>
          <div className={styles.readings}>
            <div className={styles.wide}>
              <DateTimeField control={form.control} name="at" label="Time" required />
            </div>
            <NumberField control={form.control} name="systolic" label="Systolic" min={40} max={300} />
            <NumberField control={form.control} name="diastolic" label="Diastolic" min={20} max={200} />
            <NumberField control={form.control} name="pulse" label="Pulse" min={20} max={250} />
            <NumberField control={form.control} name="bloodFlowMlMin" label="Blood flow (ml/min)" min={0} max={600} />
            <NumberField control={form.control} name="ufRateMlHr" label="UF rate (ml/h)" min={0} max={3000} />
            <NumberField control={form.control} name="venousPressure" label="Venous pressure" min={-500} max={500} />
            <NumberField control={form.control} name="arterialPressure" label="Arterial pressure" min={-500} max={500} />
            <NumberField control={form.control} name="transmembranePressure" label="TMP" min={-500} max={800} />
            <div className={styles.wide}>
              <TextField control={form.control} name="notes" label="Notes" maxLength={300} />
            </div>
            <Button type="submit" appearance="primary" icon={add.isPending ? <Spinner size="tiny" /> : <AddRegular />} disabled={add.isPending}>
              Add reading
            </Button>
          </div>
          <ActionError error={add.error} />
        </form>
      )}
      {readings.length > 1 && <TrendChart series={series} ariaLabel="Intra-dialytic blood pressure, pulse and venous pressure" yName="mmHg / bpm" height={240} />}
      <DataTable
        ariaLabel="Intra-dialytic readings"
        rows={readings}
        getRowId={(r) => `${r.at}-${r.systolic}-${r.pulse}`}
        size="small"
        columns={[
          { key: 'at', header: 'Time', width: 100, render: (r) => formatTime(r.at) },
          { key: 'bp', header: 'BP', align: 'end', render: (r) => (r.systolic || r.diastolic ? `${r.systolic ?? '—'}/${r.diastolic ?? '—'}` : '—') },
          { key: 'pulse', header: 'Pulse', align: 'end', render: (r) => r.pulse ?? '—' },
          { key: 'bfr', header: 'Blood flow', align: 'end', render: (r) => r.bloodFlowMlMin ?? '—' },
          { key: 'uf', header: 'UF rate', align: 'end', render: (r) => r.ufRateMlHr ?? '—' },
          { key: 'vp', header: 'VP', align: 'end', priority: 'low', render: (r) => r.venousPressure ?? '—' },
          { key: 'ap', header: 'AP', align: 'end', priority: 'low', render: (r) => r.arterialPressure ?? '—' },
          { key: 'tmp', header: 'TMP', align: 'end', priority: 'low', render: (r) => r.transmembranePressure ?? '—' },
          { key: 'notes', header: 'Notes', priority: 'low', render: (r) => r.notes ?? '' },
        ]}
        emptyTitle="No readings yet"
        emptyDescription={editable ? 'Record blood pressure, pulse and machine pressures every hour.' : undefined}
      />
    </div>
  );
}

// ---------------------------------------------------------------- after dialysis

const completeSchema = z
  .object({
    weightKg: z.number().nullable(),
    systolic: z.number().nullable(),
    diastolic: z.number().nullable(),
    pulse: z.number().nullable(),
    ufAchievedMl: z.number().nullable(),
    complications: z.array(z.enum(COMPLICATIONS)),
    complicationNotes: z.string().trim().max(1000),
  })
  .superRefine((v, ctx) => {
    if (v.weightKg === null || v.weightKg < 0.5 || v.weightKg > 350) {
      ctx.addIssue({ code: 'custom', path: ['weightKg'], message: 'Enter the weight after dialysis (0.5–350 kg).' });
    }
    if (v.systolic === null || v.systolic < 40 || v.systolic > 300) {
      ctx.addIssue({ code: 'custom', path: ['systolic'], message: 'Enter the systolic pressure (40–300).' });
    }
    if (v.diastolic === null || v.diastolic < 20 || v.diastolic > 200) {
      ctx.addIssue({ code: 'custom', path: ['diastolic'], message: 'Enter the diastolic pressure (20–200).' });
    }
    if (!inRange(v.pulse, 20, 250)) {
      ctx.addIssue({ code: 'custom', path: ['pulse'], message: 'Enter a pulse between 20 and 250.' });
    }
    if (!inRange(v.ufAchievedMl, 0, 8000)) {
      ctx.addIssue({ code: 'custom', path: ['ufAchievedMl'], message: 'Between 0 and 8000 ml.' });
    }
    if (v.complications.includes('Other') && !v.complicationNotes) {
      ctx.addIssue({ code: 'custom', path: ['complicationNotes'], message: "Describe the complication marked as 'Other'." });
    }
  });

type CompleteValues = z.infer<typeof completeSchema>;

/** Post-dialysis weight and BP, fluid removed and complications. Completing frees the machine and posts the charge. */
export function CompletePanel({ session, canRecord }: { session: DialysisSessionDetail; canRecord: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [confirming, setConfirming] = useState<CompleteValues | null>(null);
  const form = useForm<CompleteValues>({
    resolver: zodResolver(completeSchema),
    defaultValues: { weightKg: null, systolic: null, diastolic: null, pulse: null, ufAchievedMl: null, complications: [], complicationNotes: '' },
  });
  const [weightKg] = useWatch({ control: form.control, name: ['weightKg'] });
  const lost = weightLossMl(session.preWeightKg, weightKg);

  const complete = useMutation({
    mutationFn: (v: CompleteValues) =>
      dialysisApi.complete(session.id, {
        weightKg: v.weightKg ?? 0,
        systolic: v.systolic ?? 0,
        diastolic: v.diastolic ?? 0,
        pulse: v.pulse,
        ufAchievedMl: v.ufAchievedMl,
        complications: v.complications.length ? v.complications : null,
        complicationNotes: v.complicationNotes || null,
      }),
    onSuccess: () => {
      setConfirming(null);
      notify.success('Dialysis completed', 'Disinfect the machine before the next patient.');
      void queryClient.invalidateQueries({ queryKey: dialysisKeys.all });
    },
    onError: (error) => {
      setConfirming(null);
      applyFormErrors(error, form.setError);
    },
  });

  if (!canRecord || session.status !== 'InProgress') {
    return null;
  }

  return (
    <form onSubmit={form.handleSubmit((v) => setConfirming(v))} noValidate className={styles.stack}>
      <FormGrid columns={3}>
        <NumberField control={form.control} name="weightKg" label="Weight after (kg)" required min={0.5} max={350} step={0.1} />
        <NumberField control={form.control} name="systolic" label="Systolic BP" required min={40} max={300} />
        <NumberField control={form.control} name="diastolic" label="Diastolic BP" required min={20} max={200} />
        <NumberField control={form.control} name="pulse" label="Pulse (/min)" min={20} max={250} />
        <NumberField
          control={form.control}
          name="ufAchievedMl"
          label="Fluid removed (ml)"
          min={0}
          max={8000}
          step={50}
          hint={lost !== null ? `Leave blank to use the weight lost: ${formatNumber(lost)} ml.` : 'From the machine; blank uses the weight lost.'}
        />
        <FullRow>
          <CheckboxGroupField
            control={form.control}
            name="complications"
            label="Complications during the session"
            layout="columns"
            options={COMPLICATIONS.map((c) => ({ value: c, label: complicationLabel[c] }))}
          />
        </FullRow>
        <FullRow>
          <TextAreaField control={form.control} name="complicationNotes" label="Notes on complications" rows={2} maxLength={1000} />
        </FullRow>
      </FormGrid>
      <ActionError error={complete.error} />
      <div className={styles.footer}>
        <Button type="submit" appearance="primary" size="large" icon={<CheckmarkRegular />} disabled={complete.isPending}>
          Complete dialysis
        </Button>
      </div>
      <ConfirmDialog
        open={confirming !== null}
        title="Complete this dialysis session?"
        confirmLabel="Complete session"
        busy={complete.isPending}
        onConfirm={() => confirming && complete.mutate(confirming)}
        onCancel={() => setConfirming(null)}
      >
        The end time is recorded as now, the machine is freed (it then needs disinfection) and the session is billed.
      </ConfirmDialog>
    </form>
  );
}

/** Summary of a finished session. Kt/V is not available: the server does not record pre and post urea. */
export function SessionSummary({ session }: { session: DialysisSessionDetail }) {
  const lost = weightLossMl(session.preWeightKg, session.postWeightKg);
  return (
    <DetailGrid>
      <Detail label="Started">{session.startedAt ? formatDateTime(session.startedAt) : null}</Detail>
      <Detail label="Ended">{session.endedAt ? formatDateTime(session.endedAt) : null}</Detail>
      <Detail label="Duration">{session.durationMinutes !== null ? `${session.durationMinutes} min` : null}</Detail>
      <Detail label="Weight before / after">
        {session.preWeightKg !== null && session.postWeightKg !== null ? `${session.preWeightKg} → ${session.postWeightKg} kg` : null}
      </Detail>
      <Detail label="Fluid removed">
        {session.ufAchievedMl !== null ? `${formatNumber(session.ufAchievedMl)} ml${session.targetUfMl !== null ? ` of ${formatNumber(session.targetUfMl)} ml target` : ''}` : null}
      </Detail>
      <Detail label="Weight lost">{lost !== null ? `${formatNumber(lost)} ml` : null}</Detail>
      <Detail label="BP after">{session.postSystolic ? `${session.postSystolic}/${session.postDiastolic} mmHg` : null}</Detail>
      <Detail label="Pulse after">{session.postPulse ? `${session.postPulse}/min` : null}</Detail>
      <Detail label="Dialyzer">
        {session.dialyzerSerial ? `${session.dialyzerSerial} · use ${session.dialyzerUseNumber ?? '—'} of ${session.dialyzerMaxUses ?? '—'}` : null}
      </Detail>
      <Detail label="Heparin">
        {session.heparinLoadingUnits !== null || session.heparinHourlyUnits !== null
          ? `${session.heparinLoadingUnits ?? 0} units loading · ${session.heparinHourlyUnits ?? 0} units/h`
          : null}
      </Detail>
      <Detail label="Complications">{session.complications.length ? session.complications.map((c) => complicationLabel[c]).join(', ') : 'None'}</Detail>
      <Detail label="Complication notes">{session.complicationNotes}</Detail>
      <Detail label="Performed by">{session.performedByName}</Detail>
      <Detail label="Kt/V">Not recorded (pre and post urea are not captured)</Detail>
    </DetailGrid>
  );
}
