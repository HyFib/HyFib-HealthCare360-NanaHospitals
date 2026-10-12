import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  DeleteRegular,
  DocumentArrowLeftRegular,
  LockClosedRegular,
  SaveRegular,
  SignatureRegular,
} from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, formatRelative, formatTime, useHotkey } from '@hc360/core';
import {
  ConfirmDialog,
  DataTable,
  ErrorState,
  FormGrid,
  FullRow,
  LEAVE_KEEPING_DRAFT,
  LoadingState,
  NumberField,
  Section,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useFieldArray, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import { useFormDraft } from '@/lib/useFormDraft';
import {
  AIRWAY_DEVICES,
  ANAESTHESIA_TYPES,
  ASA_GRADES,
  otApi,
  otKeys,
  otQueries,
  type AnaesthesiaRecordDto,
  type AnaesthesiaVitals,
  type SurgeryDetail,
} from './api';
import { airwayLabel, anaesthesiaLabel, asaLabel } from './labels';
import { anaesthesiaGate } from './logic';
import { fromIstInput, nowIstInput, toIstInput } from './shared/datetime';
import { applyFormErrors } from './shared/errors';
import { ActionError, Detail, DetailGrid, WhoWhen } from './shared/feedback';
import { DateTimeField } from './shared/fields';
import { TrendChart } from './shared/TrendChart';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '16px' },
  rows: { display: 'flex', flexDirection: 'column', gap: '8px' },
  row: {
    display: 'grid',
    gridTemplateColumns: 'minmax(170px, 1fr) minmax(140px, 2fr) minmax(90px, 1fr) minmax(90px, 1fr) auto',
    gap: '8px',
    alignItems: 'start',
  },
  fluidRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(170px, 1fr) minmax(160px, 2fr) minmax(110px, 1fr) auto',
    gap: '8px',
    alignItems: 'start',
  },
  footer: { display: 'flex', gap: '8px', justifyContent: 'flex-end', flexWrap: 'wrap', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
  vitalsForm: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(120px, 1fr))',
    gap: '8px 12px',
    alignItems: 'end',
  },
  wide: { gridColumn: 'span 2' },
  removeButton: { marginTop: '24px' },
});

const drugRow = z.object({
  at: z.string().nullable(),
  drug: z.string().trim().min(1, 'Enter the drug.').max(100),
  dose: z.string().trim().min(1, 'Enter the dose.').max(50),
  route: z.string().trim().max(30),
});

const fluidRow = z.object({
  at: z.string().nullable(),
  fluid: z.string().trim().min(1, 'Enter the fluid.').max(100),
  volumeMl: z.number().nullable(),
});

const recordSchema = z
  .object({
    asaGrade: z.enum(ASA_GRADES).nullable(),
    asaEmergency: z.boolean(),
    technique: z.enum(ANAESTHESIA_TYPES),
    airway: z.enum(AIRWAY_DEVICES),
    airwayNotes: z.string().trim().max(1000),
    anaesthesiaStartAt: z.string().nullable(),
    anaesthesiaEndAt: z.string().nullable(),
    drugs: z.array(drugRow).max(200),
    fluids: z.array(fluidRow).max(100),
    estimatedBloodLossMl: z.number().nullable(),
    urineOutputMl: z.number().nullable(),
    complications: z.string().trim().max(2000),
    recoveryNotes: z.string().trim().max(2000),
  })
  .superRefine((v, ctx) => {
    const start = fromIstInput(v.anaesthesiaStartAt);
    const end = fromIstInput(v.anaesthesiaEndAt);
    if (start && end && Date.parse(end) <= Date.parse(start)) {
      ctx.addIssue({ code: 'custom', path: ['anaesthesiaEndAt'], message: 'Anaesthesia end must be after its start.' });
    }
    for (const [key, value] of [
      ['estimatedBloodLossMl', v.estimatedBloodLossMl],
      ['urineOutputMl', v.urineOutputMl],
    ] as const) {
      if (value !== null && (value < 0 || value > 20_000)) {
        ctx.addIssue({ code: 'custom', path: [key], message: 'Enter between 0 and 20,000 ml.' });
      }
    }
    v.drugs.forEach((row, index) => {
      if (!fromIstInput(row.at)) {
        ctx.addIssue({ code: 'custom', path: ['drugs', index, 'at'], message: 'Enter the time given.' });
      }
    });
    v.fluids.forEach((row, index) => {
      if (!fromIstInput(row.at)) {
        ctx.addIssue({ code: 'custom', path: ['fluids', index, 'at'], message: 'Enter the time started.' });
      }
      if (row.volumeMl === null || row.volumeMl < 1 || row.volumeMl > 10_000) {
        ctx.addIssue({ code: 'custom', path: ['fluids', index, 'volumeMl'], message: 'Enter 1 to 10,000 ml.' });
      }
    });
  });

type RecordValues = z.infer<typeof recordSchema>;

function toValues(record: AnaesthesiaRecordDto | null, booking: SurgeryDetail): RecordValues {
  return {
    asaGrade: record?.asaGrade ?? null,
    asaEmergency: record?.asaEmergency ?? booking.priority === 'Emergency',
    technique: record?.technique ?? booking.anaesthesiaType,
    airway: record?.airway ?? (booking.anaesthesiaType === 'GA' ? 'EndotrachealTube' : 'None'),
    airwayNotes: record?.airwayNotes ?? '',
    anaesthesiaStartAt: toIstInput(record?.anaesthesiaStartAt) || null,
    anaesthesiaEndAt: toIstInput(record?.anaesthesiaEndAt) || null,
    drugs: (record?.drugs ?? []).map((d) => ({
      at: toIstInput(d.at),
      drug: d.drug,
      dose: d.dose,
      route: d.route ?? '',
    })),
    fluids: (record?.fluids ?? []).map((f) => ({ at: toIstInput(f.at), fluid: f.fluid, volumeMl: f.volumeMl })),
    estimatedBloodLossMl: record?.estimatedBloodLossMl ?? null,
    urineOutputMl: record?.urineOutputMl ?? null,
    complications: record?.complications ?? '',
    recoveryNotes: record?.recoveryNotes ?? '',
  };
}

const orNull = (value: string) => (value.trim() ? value.trim() : null);

/**
 * Anaesthesia record (OT-04): ASA grade, technique and airway, drugs and fluids, blood loss, recovery, and the
 * intra-operative vitals chart. Signing locks the record (medico-legal); the ASA grade is needed to sign.
 */
export function AnaesthesiaPanel({ booking }: { booking: SurgeryDetail }) {
  const styles = useStyles();
  const record = useQuery(otQueries.anaesthesia(booking.id));
  const canRecord = usePermission('ot.anaesthesia.record');
  const gate = anaesthesiaGate(booking);

  if (record.isLoading) {
    return <LoadingState label="Loading the anaesthesia record…" />;
  }
  if (record.error) {
    return <ErrorState error={record.error} onRetry={() => void record.refetch()} />;
  }

  const data = record.data ?? null;
  const editable = canRecord && gate.allowed && !data?.signedAt;

  return (
    <div className={styles.stack}>
      {!gate.allowed && (
        <MessageBar intent="info">
          <MessageBarBody>{gate.reason}</MessageBarBody>
        </MessageBar>
      )}
      {data?.signedAt ? (
        <SignedRecord record={data} />
      ) : editable ? (
        <RecordForm booking={booking} record={data} />
      ) : (
        <Section title="Anaesthesia record">
          <Text className={styles.muted}>
            {data ? 'Draft in progress by the anaesthetist.' : 'Not started yet.'} Only the anaesthetist writes this
            record.
          </Text>
          {data && <RecordSummary record={data} />}
        </Section>
      )}
      <VitalsSection booking={booking} record={data} canRecord={editable} />
    </div>
  );
}

function RecordForm({ booking, record }: { booking: SurgeryDetail; record: AnaesthesiaRecordDto | null }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [confirmSign, setConfirmSign] = useState(false);
  const form = useForm<RecordValues>({ resolver: zodResolver(recordSchema), defaultValues: toValues(record, booking) });
  const draft = useFormDraft(form, `ot-anaesthesia-${booking.id}`);
  const drugs = useFieldArray({ control: form.control, name: 'drugs' });
  const fluids = useFieldArray({ control: form.control, name: 'fluids' });
  const [asaGrade] = useWatch({ control: form.control, name: ['asaGrade'] });

  const save = useMutation({
    mutationFn: ({ values, sign }: { values: RecordValues; sign: boolean }) => {
      const latest = queryClient.getQueryData<AnaesthesiaRecordDto | null>(otKeys.anaesthesia(booking.id)) ?? record;
      return otApi.saveAnaesthesia(booking.id, {
        asaGrade: values.asaGrade,
        asaEmergency: values.asaEmergency,
        technique: values.technique,
        airway: values.airway,
        airwayNotes: orNull(values.airwayNotes),
        anaesthesiaStartAt: fromIstInput(values.anaesthesiaStartAt),
        anaesthesiaEndAt: fromIstInput(values.anaesthesiaEndAt),
        drugs: values.drugs.map((d) => ({
          at: fromIstInput(d.at) ?? '',
          drug: d.drug,
          dose: d.dose,
          route: orNull(d.route),
        })),
        fluids: values.fluids.map((f) => ({ at: fromIstInput(f.at) ?? '', fluid: f.fluid, volumeMl: f.volumeMl ?? 0 })),
        estimatedBloodLossMl: values.estimatedBloodLossMl,
        urineOutputMl: values.urineOutputMl,
        complications: orNull(values.complications),
        recoveryNotes: orNull(values.recoveryNotes),
        sign,
        version: latest?.version ?? null,
      });
    },
    onSuccess: (saved, { sign }) => {
      queryClient.setQueryData(otKeys.anaesthesia(booking.id), saved);
      draft.clear();
      setConfirmSign(false);
      notify.success(sign ? 'Anaesthesia record signed and locked' : 'Anaesthesia record saved');
    },
    onError: (error) => {
      setConfirmSign(false);
      applyFormErrors(error, form.setError);
    },
  });

  const submit = (sign: boolean) => form.handleSubmit((values) => save.mutate({ values, sign }))();
  useHotkey('mod+s', () => void submit(false), { allowInInputs: true, enabled: !save.isPending });

  const reload = async () => {
    const latest = await queryClient.fetchQuery(otQueries.anaesthesia(booking.id));
    form.reset(toValues(latest, booking));
    save.reset();
  };

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: form.formState.isDirty, busy: save.isPending, leaveQuestion: LEAVE_KEEPING_DRAFT });

  return (
    <Section
      title="Anaesthesia record"
      description={record ? `Draft · last saved version ${record.version}` : 'Not started yet. Press Ctrl+S to save.'}
    >
      <form onSubmit={(event) => event.preventDefault()} noValidate className={styles.stack}>
        {draft.saved && (
          <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
            <MessageBarBody>
              Unsaved changes from {formatRelative(new Date(draft.saved.savedAt).toISOString())} were found on this
              computer.
            </MessageBarBody>
            <MessageBarActions>
              <Button onClick={draft.restore}>Restore them</Button>
              <Button appearance="subtle" onClick={draft.discard}>
                Discard
              </Button>
            </MessageBarActions>
          </MessageBar>
        )}
        <FormGrid columns={3}>
          <SelectField
            control={form.control}
            name="asaGrade"
            label="ASA physical status"
            placeholder="Not graded yet"
            options={ASA_GRADES.map((g) => ({ value: g, label: asaLabel[g] }))}
            hint="Needed before signing."
          />
          <SwitchField control={form.control} name="asaEmergency" label="Emergency surgery (ASA 'E')" />
          <SelectField
            control={form.control}
            name="technique"
            label="Technique"
            options={ANAESTHESIA_TYPES.map((t) => ({ value: t, label: anaesthesiaLabel[t] }))}
          />
          <SelectField
            control={form.control}
            name="airway"
            label="Airway"
            options={AIRWAY_DEVICES.map((a) => ({ value: a, label: airwayLabel[a] }))}
          />
          <DateTimeField control={form.control} name="anaesthesiaStartAt" label="Anaesthesia started" />
          <DateTimeField control={form.control} name="anaesthesiaEndAt" label="Anaesthesia ended" />
          <FullRow>
            <TextField
              control={form.control}
              name="airwayNotes"
              label="Airway details"
              maxLength={1000}
              placeholder="Mallampati, tube size, Cormack–Lehane grade, attempts"
            />
          </FullRow>
        </FormGrid>

        <div className={styles.rows}>
          <Text weight="semibold">Drugs given</Text>
          {drugs.fields.length === 0 && <Caption1 className={styles.muted}>No drugs recorded yet.</Caption1>}
          {drugs.fields.map((row, index) => (
            <div key={row.id} className={styles.row}>
              <DateTimeField control={form.control} name={`drugs.${index}.at`} label="Time" required />
              <TextField control={form.control} name={`drugs.${index}.drug`} label="Drug" required maxLength={100} />
              <TextField control={form.control} name={`drugs.${index}.dose`} label="Dose" required maxLength={50} />
              <TextField control={form.control} name={`drugs.${index}.route`} label="Route" maxLength={30} />
              <Button
                className={styles.removeButton}
                appearance="subtle"
                icon={<DeleteRegular />}
                aria-label={`Remove drug row ${index + 1}`}
                onClick={() => drugs.remove(index)}
              />
            </div>
          ))}
          <div>
            <Button
              icon={<AddRegular />}
              onClick={() => drugs.append({ at: nowIstInput(), drug: '', dose: '', route: 'IV' })}
            >
              Add drug
            </Button>
          </div>
        </div>

        <div className={styles.rows}>
          <Text weight="semibold">Fluids and blood</Text>
          {fluids.fields.length === 0 && <Caption1 className={styles.muted}>No fluids recorded yet.</Caption1>}
          {fluids.fields.map((row, index) => (
            <div key={row.id} className={styles.fluidRow}>
              <DateTimeField control={form.control} name={`fluids.${index}.at`} label="Time" required />
              <TextField control={form.control} name={`fluids.${index}.fluid`} label="Fluid" required maxLength={100} />
              <NumberField
                control={form.control}
                name={`fluids.${index}.volumeMl`}
                label="Volume (ml)"
                required
                min={1}
                max={10000}
              />
              <Button
                className={styles.removeButton}
                appearance="subtle"
                icon={<DeleteRegular />}
                aria-label={`Remove fluid row ${index + 1}`}
                onClick={() => fluids.remove(index)}
              />
            </div>
          ))}
          <div>
            <Button
              icon={<AddRegular />}
              onClick={() => fluids.append({ at: nowIstInput(), fluid: '', volumeMl: null })}
            >
              Add fluid
            </Button>
          </div>
        </div>

        <FormGrid columns={2}>
          <NumberField
            control={form.control}
            name="estimatedBloodLossMl"
            label="Estimated blood loss (ml)"
            min={0}
            max={20000}
          />
          <NumberField control={form.control} name="urineOutputMl" label="Urine output (ml)" min={0} max={20000} />
          <TextAreaField control={form.control} name="complications" label="Complications" rows={2} maxLength={2000} />
          <TextAreaField
            control={form.control}
            name="recoveryNotes"
            label="Recovery and handover notes"
            rows={2}
            maxLength={2000}
          />
        </FormGrid>

        <ActionError error={save.error} onReload={() => void reload()} />

        <div className={styles.footer}>
          <Caption1 className={styles.muted}>Signing locks the record. It cannot be changed afterwards.</Caption1>
          <Button
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
            disabled={save.isPending}
            onClick={() => void submit(false)}
          >
            Save draft
          </Button>
          <Button
            appearance="primary"
            icon={<SignatureRegular />}
            disabled={save.isPending || !asaGrade}
            onClick={() => setConfirmSign(true)}
          >
            Sign and lock
          </Button>
        </div>
      </form>

      <ConfirmDialog
        open={confirmSign}
        title="Sign the anaesthesia record?"
        confirmLabel="Sign and lock"
        busy={save.isPending}
        onConfirm={() => void submit(true)}
        onCancel={() => setConfirmSign(false)}
      >
        The record is saved with your name and the time, and locked. Check the drugs, fluids and vitals first — nothing
        can be changed after signing.
      </ConfirmDialog>
    </Section>
  );
}

function RecordSummary({ record }: { record: AnaesthesiaRecordDto }) {
  return (
    <DetailGrid>
      <Detail label="ASA">
        {record.asaGrade ? `${asaLabel[record.asaGrade]}${record.asaEmergency ? ' (E)' : ''}` : null}
      </Detail>
      <Detail label="Technique">{anaesthesiaLabel[record.technique]}</Detail>
      <Detail label="Airway">{airwayLabel[record.airway]}</Detail>
      <Detail label="Airway details">{record.airwayNotes}</Detail>
      <Detail label="Started">{record.anaesthesiaStartAt ? formatDateTime(record.anaesthesiaStartAt) : null}</Detail>
      <Detail label="Ended">{record.anaesthesiaEndAt ? formatDateTime(record.anaesthesiaEndAt) : null}</Detail>
      <Detail label="Drugs">
        {record.drugs.map((d) => `${formatTime(d.at)} ${d.drug} ${d.dose}${d.route ? ` ${d.route}` : ''}`).join('; ')}
      </Detail>
      <Detail label="Fluids">
        {record.fluids.map((f) => `${formatTime(f.at)} ${f.fluid} ${f.volumeMl} ml`).join('; ')}
      </Detail>
      <Detail label="Blood loss">
        {record.estimatedBloodLossMl !== null ? `${record.estimatedBloodLossMl} ml` : null}
      </Detail>
      <Detail label="Urine output">{record.urineOutputMl !== null ? `${record.urineOutputMl} ml` : null}</Detail>
      <Detail label="Complications">{record.complications}</Detail>
      <Detail label="Recovery notes">{record.recoveryNotes}</Detail>
    </DetailGrid>
  );
}

function SignedRecord({ record }: { record: AnaesthesiaRecordDto }) {
  return (
    <Section
      title="Anaesthesia record"
      actions={<WhoWhen prefix="Signed by" by={record.signedByName} at={record.signedAt} locked />}
    >
      <MessageBar intent="success" icon={<LockClosedRegular />}>
        <MessageBarBody>
          <MessageBarTitle>Signed and locked</MessageBarTitle>
          This record can no longer be changed.
        </MessageBarBody>
      </MessageBar>
      <div style={{ height: 12 }} />
      <RecordSummary record={record} />
    </Section>
  );
}

// ---------------------------------------------------------------- intra-operative vitals

const vitalsSchema = z
  .object({
    at: z.string().nullable(),
    heartRate: z.number().nullable(),
    systolic: z.number().nullable(),
    diastolic: z.number().nullable(),
    spO2: z.number().nullable(),
    etCo2: z.number().nullable(),
    temperatureC: z.number().nullable(),
    respiratoryRate: z.number().nullable(),
  })
  .superRefine((v, ctx) => {
    const at = fromIstInput(v.at);
    if (!at) {
      ctx.addIssue({ code: 'custom', path: ['at'], message: 'Enter the time.' });
    } else if (Date.parse(at) > Date.now() + 5 * 60_000) {
      ctx.addIssue({ code: 'custom', path: ['at'], message: 'The time cannot be in the future.' });
    }
    const ranges: [keyof typeof v, number, number, string][] = [
      ['heartRate', 20, 250, 'Heart rate 20–250'],
      ['systolic', 40, 300, 'Systolic 40–300'],
      ['diastolic', 20, 200, 'Diastolic 20–200'],
      ['spO2', 40, 100, 'SpO₂ 40–100'],
      ['etCo2', 0, 150, 'EtCO₂ 0–150'],
      ['temperatureC', 30, 45, 'Temperature 30–45 °C'],
      ['respiratoryRate', 0, 80, 'Resp. rate 0–80'],
    ];
    let any = false;
    for (const [key, min, max, label] of ranges) {
      const value = v[key];
      if (typeof value === 'number') {
        any = true;
        if (value < min || value > max) {
          ctx.addIssue({ code: 'custom', path: [key], message: `${label}.` });
        }
      }
    }
    if (!any) {
      ctx.addIssue({ code: 'custom', path: ['heartRate'], message: 'Enter at least one value.' });
    }
  });

type VitalsValues = z.infer<typeof vitalsSchema>;

const emptyVitals = (): VitalsValues => ({
  at: nowIstInput(),
  heartRate: null,
  systolic: null,
  diastolic: null,
  spO2: null,
  etCo2: null,
  temperatureC: null,
  respiratoryRate: null,
});

function VitalsSection({
  booking,
  record,
  canRecord,
}: {
  booking: SurgeryDetail;
  record: AnaesthesiaRecordDto | null;
  canRecord: boolean;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<VitalsValues>({ resolver: zodResolver(vitalsSchema), defaultValues: emptyVitals() });
  const vitals = record?.vitals ?? [];

  const add = useMutation({
    mutationFn: (values: VitalsValues) => {
      const row: AnaesthesiaVitals = { ...values, at: fromIstInput(values.at) ?? '' };
      return otApi.addVitals(booking.id, [row]);
    },
    onSuccess: (saved) => {
      queryClient.setQueryData(otKeys.anaesthesia(booking.id), saved);
      form.reset(emptyVitals());
      notify.success('Vitals added');
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  const series = [
    { name: 'Heart rate', points: vitals.map((v) => ({ at: v.at, value: v.heartRate })) },
    { name: 'Systolic', points: vitals.map((v) => ({ at: v.at, value: v.systolic })) },
    { name: 'Diastolic', points: vitals.map((v) => ({ at: v.at, value: v.diastolic })) },
    { name: 'SpO₂', points: vitals.map((v) => ({ at: v.at, value: v.spO2 })) },
    { name: 'EtCO₂', points: vitals.map((v) => ({ at: v.at, value: v.etCo2 })), dashed: true },
  ];

  return (
    <Section
      title="Intra-operative vitals"
      description="Chart every 5–15 minutes. Late entries are placed in time order."
    >
      {canRecord && (
        <form onSubmit={form.handleSubmit((values) => add.mutate(values))} noValidate>
          <div className={styles.vitalsForm}>
            <div className={styles.wide}>
              <DateTimeField control={form.control} name="at" label="Time" required />
            </div>
            <NumberField control={form.control} name="heartRate" label="Heart rate" min={20} max={250} />
            <NumberField control={form.control} name="systolic" label="Systolic BP" min={40} max={300} />
            <NumberField control={form.control} name="diastolic" label="Diastolic BP" min={20} max={200} />
            <NumberField control={form.control} name="spO2" label="SpO₂ %" min={40} max={100} />
            <NumberField control={form.control} name="etCo2" label="EtCO₂" min={0} max={150} />
            <NumberField control={form.control} name="temperatureC" label="Temp °C" min={30} max={45} step={0.1} />
            <NumberField control={form.control} name="respiratoryRate" label="Resp. rate" min={0} max={80} />
            <Button
              type="submit"
              appearance="primary"
              icon={add.isPending ? <Spinner size="tiny" aria-hidden /> : <AddRegular />}
              disabled={add.isPending}
            >
              Add reading
            </Button>
          </div>
          <ActionError error={add.error} />
        </form>
      )}
      {vitals.length > 1 && (
        <div style={{ marginTop: 12 }}>
          <TrendChart
            series={series}
            ariaLabel="Intra-operative vital signs over time"
            yName="bpm / mmHg / %"
            height={260}
          />
        </div>
      )}
      <DataTable
        ariaLabel="Intra-operative vitals"
        rows={vitals}
        getRowId={(v) => `${v.at}-${v.heartRate}-${v.systolic}`}
        size="small"
        columns={[
          { key: 'at', header: 'Time', width: 110, render: (v) => formatTime(v.at) },
          { key: 'hr', header: 'Heart rate', align: 'end', render: (v) => v.heartRate ?? '—' },
          {
            key: 'bp',
            header: 'BP',
            align: 'end',
            render: (v) => (v.systolic || v.diastolic ? `${v.systolic ?? '—'}/${v.diastolic ?? '—'}` : '—'),
          },
          { key: 'spo2', header: 'SpO₂ %', align: 'end', render: (v) => v.spO2 ?? '—' },
          { key: 'etco2', header: 'EtCO₂', align: 'end', render: (v) => v.etCo2 ?? '—' },
          { key: 'temp', header: 'Temp °C', align: 'end', priority: 'low', render: (v) => v.temperatureC ?? '—' },
          { key: 'rr', header: 'Resp. rate', align: 'end', priority: 'low', render: (v) => v.respiratoryRate ?? '—' },
        ]}
        emptyTitle="No vitals charted yet"
        emptyDescription={
          canRecord ? 'Add the first reading above.' : 'Readings appear here as the anaesthetist charts them.'
        }
      />
    </Section>
  );
}
