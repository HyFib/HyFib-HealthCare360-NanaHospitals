import {
  Button,
  Caption1,
  Dialog,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Spinner,
  Switch,
  Text,
  tokens,
  Tooltip,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, MoreHorizontalRegular, SaveRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError, useDebouncedValue } from '@hc360/core';
import { DataTable, FormGrid, LoadingState, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { lazy, Suspense, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { emrApi, emrKeys, emrQueries, type VitalsDto } from './api';
import { VitalChipList } from './ChartSummary';
import { ReasonDialog } from './ui';
import {
  celsiusToFahrenheit,
  computeBmi,
  emptyVitals,
  fahrenheitToCelsius,
  flagsFor,
  formatBp,
  formatTemperature,
  hasAnyMeasurement,
  limitFor,
  parameterFor,
  toVitalsPayload,
  TREND_METRICS,
  validateVitals,
  vitalChips,
  worstFlag,
  type GlucoseTiming,
  type RecordVitalsPayload,
  type TrendMetric,
  type VitalField,
  type VitalFlag,
  type VitalsValues,
} from './vitals';

const VitalsTrendChart = lazy(() => import('./VitalsTrendChart'));

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  inline: { display: 'flex', gap: '8px', alignItems: 'flex-end' },
  bp: { display: 'grid', gridTemplateColumns: '1fr auto 1fr', alignItems: 'end', gap: '4px' },
  slash: { paddingBottom: '6px', color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '8px', justifyContent: 'flex-end', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  flagged: { fontWeight: tokens.fontWeightSemibold },
  high: { color: tokens.colorPaletteMarigoldForeground1 },
  critical: { color: tokens.colorPaletteRedForeground1 },
  toolbar: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap', marginBottom: '12px' },
});

type FieldState = { message?: string; state: 'error' | 'warning' | 'none' };

function fieldState(
  field: VitalField,
  errors: Partial<Record<VitalField, string>>,
  flags: readonly VitalFlag[],
): FieldState {
  const error = errors[field];
  if (error) {
    return { message: error, state: 'error' };
  }
  const flag = worstFlag(
    flagsFor(flags, field === 'systolicBp' || field === 'diastolicBp' ? field : parameterFor(field)),
  );
  if (!flag || field === 'heightCm') {
    return { state: 'none' };
  }
  return { message: flag.message, state: flag.severity === 'Critical' ? 'error' : 'warning' };
}

/**
 * Vital-sign entry with live abnormal flags: values are checked against the patient's age-aware ranges on the server
 * (preview endpoint, nothing saved) while the nurse or doctor types. Enter saves.
 */
export function VitalsForm({
  patientId,
  encounterId,
  onSaved,
  onCancel,
  autoFocus = true,
}: {
  patientId: string;
  encounterId: string | null;
  onSaved?: (vitals: VitalsDto) => void;
  onCancel?: () => void;
  autoFocus?: boolean;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [values, setValues] = useState<VitalsValues>(emptyVitals);
  const [recordedAt, setRecordedAt] = useState('');
  const [serverErrors, setServerErrors] = useState<Record<string, string>>({});
  const [saved, setSaved] = useState<VitalsDto | null>(null);

  const errors = validateVitals(values);
  const ready = hasAnyMeasurement(values) && Object.keys(errors).length === 0;
  const payloadKey = useDebouncedValue(JSON.stringify(toVitalsPayload(values, encounterId)), 350);
  const preview = useQuery({
    queryKey: ['emr', 'vitals-preview', patientId, payloadKey],
    queryFn: ({ signal }) => emrApi.previewVitals(patientId, JSON.parse(payloadKey) as RecordVitalsPayload, signal),
    enabled: ready,
    staleTime: Infinity,
    retry: false,
  });
  const flags = ready ? (preview.data ?? []) : [];
  const bmi = computeBmi(values.heightCm, values.weightKg);

  const set = <K extends keyof VitalsValues>(key: K, value: VitalsValues[K]) => {
    setValues((current) => ({ ...current, [key]: value }));
    setServerErrors((current) =>
      key in current ? Object.fromEntries(Object.entries(current).filter(([k]) => k !== key)) : current,
    );
  };

  const switchUnit = (unit: VitalsValues['temperatureUnit']) => {
    setValues((current) => {
      if (current.temperatureUnit === unit || current.temperature === null) {
        return { ...current, temperatureUnit: unit };
      }
      const converted =
        unit === 'Celsius' ? fahrenheitToCelsius(current.temperature) : celsiusToFahrenheit(current.temperature);
      return { ...current, temperatureUnit: unit, temperature: converted };
    });
  };

  const save = useMutation({
    mutationFn: () =>
      emrApi.recordVitals(
        patientId,
        toVitalsPayload(values, encounterId, recordedAt ? new Date(recordedAt).toISOString() : null),
      ),
    onSuccess: (dto) => {
      setSaved(dto);
      setValues((current) => ({ ...emptyVitals, temperatureUnit: current.temperatureUnit }));
      setRecordedAt('');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
      notify.success('Vitals recorded');
      onSaved?.(dto);
    },
    onError: (error) => {
      if (isApiError(error) && error.isValidation) {
        setServerErrors(
          Object.fromEntries(Object.entries(error.fieldErrors).map(([name, messages]) => [name, messages[0] ?? ''])),
        );
      } else {
        notify.error(error, 'Could not record the vitals');
      }
    },
  });

  const numberInput = (
    field: VitalField,
    label: string,
    options: { step?: number; placeholder?: string; autoFocus?: boolean } = {},
  ) => {
    const limit = limitFor(field, values.temperatureUnit);
    const state = serverErrors[field]
      ? { message: serverErrors[field], state: 'error' as const }
      : fieldState(field, errors, flags);
    const unit = limit.unit === '/10' ? '0–10' : limit.unit.replace(/^\//, 'per ');
    return (
      <Field
        label={`${label}${unit ? ` (${unit})` : ''}`}
        validationMessage={state.message}
        validationState={state.state}
      >
        <Input
          type="number"
          inputMode="decimal"
          value={values[field] === null ? '' : String(values[field])}
          onChange={(_, data) => set(field, data.value === '' ? null : Number(data.value))}
          min={limit.min}
          max={limit.max}
          step={options.step ?? 1}
          placeholder={options.placeholder}
          autoFocus={options.autoFocus}
        />
      </Field>
    );
  };

  const systolic = fieldState('systolicBp', errors, flags);
  const diastolic = fieldState('diastolicBp', errors, flags);
  const bpMessage = [systolic.message, diastolic.message].filter(Boolean).join(' · ');
  const bpState =
    systolic.state === 'error' || diastolic.state === 'error'
      ? 'error'
      : systolic.state === 'warning' || diastolic.state === 'warning'
        ? 'warning'
        : 'none';
  const critical = flags.filter((f) => f.severity === 'Critical');

  return (
    <form
      className={styles.form}
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        if (ready && !save.isPending) {
          save.mutate();
        }
      }}
    >
      {saved && saved.flags.some((f) => f.severity === 'Critical') && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Critical values recorded</MessageBarTitle>
            {saved.flags
              .filter((f) => f.severity === 'Critical')
              .map((f) => f.message)
              .join('; ')}
            . The treating doctor has been notified.
          </MessageBarBody>
        </MessageBar>
      )}
      <FormGrid columns={4} minColumnWidth={150}>
        <div className={styles.inline}>
          <div style={{ flex: 1 }}>
            {numberInput('temperature', 'Temperature', {
              step: 0.1,
              autoFocus,
              placeholder: values.temperatureUnit === 'Fahrenheit' ? '98.6' : '37.0',
            })}
          </div>
          <Field label="Unit">
            <Select
              value={values.temperatureUnit}
              onChange={(_, d) => switchUnit(d.value as VitalsValues['temperatureUnit'])}
              aria-label="Temperature unit"
            >
              <option value="Fahrenheit">°F</option>
              <option value="Celsius">°C</option>
            </Select>
          </Field>
        </div>
        {numberInput('pulse', 'Pulse')}
        <Field label="Blood pressure (mmHg)" validationMessage={bpMessage || undefined} validationState={bpState}>
          <div className={styles.bp}>
            <Input
              type="number"
              aria-label="Systolic"
              placeholder="120"
              value={values.systolicBp === null ? '' : String(values.systolicBp)}
              onChange={(_, d) => set('systolicBp', d.value === '' ? null : Number(d.value))}
            />
            <span className={styles.slash}>/</span>
            <Input
              type="number"
              aria-label="Diastolic"
              placeholder="80"
              value={values.diastolicBp === null ? '' : String(values.diastolicBp)}
              onChange={(_, d) => set('diastolicBp', d.value === '' ? null : Number(d.value))}
            />
          </div>
        </Field>
        <div>
          {numberInput('spO2', 'SpO₂')}
          <Switch
            label="On oxygen"
            checked={values.onSupplementalOxygen}
            onChange={(_, d) => set('onSupplementalOxygen', d.checked)}
          />
        </div>
        {numberInput('respiratoryRate', 'Respiratory rate')}
        {numberInput('weightKg', 'Weight', { step: 0.1 })}
        {numberInput('heightCm', 'Height', { step: 0.5 })}
        <Field label="BMI (calculated)" hint="Asian-Indian cut-offs: 23 overweight, 25 obese">
          <Input readOnly value={bmi === null ? '' : String(bmi)} placeholder="—" />
        </Field>
        <div className={styles.inline}>
          <div style={{ flex: 1 }}>{numberInput('bloodGlucose', 'Blood glucose')}</div>
          <Field label="Taken">
            <Select
              value={values.glucoseTiming}
              onChange={(_, d) => set('glucoseTiming', d.value as GlucoseTiming)}
              aria-label="Glucose timing"
            >
              <option value="Random">Random</option>
              <option value="Fasting">Fasting</option>
              <option value="PostPrandial">After food</option>
            </Select>
          </Field>
        </div>
        {numberInput('painScore', 'Pain score')}
        {numberInput('gcs', 'GCS')}
        <Field label="Recorded at" hint="Leave blank for now">
          <Input type="datetime-local" value={recordedAt} onChange={(_, d) => setRecordedAt(d.value)} />
        </Field>
      </FormGrid>
      <Field label="Notes">
        <Input
          value={values.notes}
          onChange={(_, d) => set('notes', d.value.slice(0, 500))}
          placeholder="e.g. taken after nebulisation"
        />
      </Field>
      {critical.length > 0 && (
        <MessageBar intent="error">
          <MessageBarBody>
            <MessageBarTitle>Critical</MessageBarTitle>
            {critical.map((f) => f.message).join('; ')}
          </MessageBarBody>
        </MessageBar>
      )}
      <div className={styles.actions}>
        {preview.isFetching && <Spinner size="extra-tiny" label="Checking ranges…" labelPosition="after" />}
        {!hasAnyMeasurement(values) && <Caption1 className={styles.muted}>Enter at least one measurement.</Caption1>}
        {onCancel && (
          <Button onClick={onCancel} disabled={save.isPending}>
            Close
          </Button>
        )}
        <Button
          type="submit"
          appearance="primary"
          icon={save.isPending ? <Spinner size="tiny" /> : <SaveRegular />}
          disabled={!ready || save.isPending}
        >
          Record vitals
        </Button>
      </div>
    </form>
  );
}

export function RecordVitalsDialog({
  patientId,
  encounterId,
  open,
  onClose,
}: {
  patientId: string;
  encounterId: string | null;
  open: boolean;
  onClose: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 880 }}>
        <DialogBody>
          <DialogTitle>Record vitals</DialogTitle>
          <DialogContent>
            {open && (
              <VitalsForm patientId={patientId} encounterId={encounterId} onCancel={onClose} onSaved={onClose} />
            )}
          </DialogContent>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/** A value with its flag: coloured, marked with an arrow and explained in a tooltip. */
function FlaggedValue({ value, flag }: { value: string; flag: VitalFlag | undefined }) {
  const styles = useStyles();
  if (!flag) {
    return <>{value}</>;
  }
  return (
    <Tooltip content={flag.message} relationship="description">
      <span className={mergeClasses(styles.flagged, flag.severity === 'Critical' ? styles.critical : styles.high)}>
        {value} {flag.severity === 'Critical' ? '‼' : flag.direction === 'High' ? '↑' : '↓'}
      </span>
    </Tooltip>
  );
}

/** Chart tab: trend chart, all recordings with their flags, and marking a wrong entry "entered in error". */
export function VitalsTab({ patientId }: { patientId: string }) {
  const styles = useStyles();
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRecord = usePermission('clinical.vitals.record');
  const [page, setPage] = useState(1);
  const [metric, setMetric] = useState<TrendMetric>('bp');
  const [recording, setRecording] = useState(false);
  const [voiding, setVoiding] = useState<VitalsDto | null>(null);
  const list = useQuery(emrQueries.vitals(patientId, null, page, 20));
  const trend = useQuery(emrQueries.vitals(patientId, null, 1, 100));

  const markError = useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) => emrApi.vitalsEnteredInError(id, reason),
    onSuccess: () => {
      setVoiding(null);
      notify.success('Marked as entered in error');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
    },
    onError: (error) => notify.error(error, 'Could not update the vitals'),
  });

  const flag = (v: VitalsDto, ...parameters: string[]) =>
    worstFlag(v.flags.filter((f) => parameters.includes(f.parameter)));
  const readings = trend.data?.items ?? [];

  return (
    <>
      <div className={styles.toolbar}>
        {canRecord && (
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setRecording(true)}>
            Record vitals
          </Button>
        )}
        <Field label="Trend">
          <Select value={metric} onChange={(_, d) => setMetric(d.value as TrendMetric)}>
            {TREND_METRICS.map((m) => (
              <option key={m.id} value={m.id}>
                {m.label}
              </option>
            ))}
          </Select>
        </Field>
      </div>
      <Section
        title={TREND_METRICS.find((m) => m.id === metric)?.label ?? 'Trend'}
        description="Last 100 recordings. Dashed lines show the usual adult range."
      >
        {trend.isLoading ? (
          <LoadingState />
        ) : readings.length < 2 ? (
          <Text className={styles.muted}>A trend appears once at least two recordings exist.</Text>
        ) : (
          <Suspense fallback={<LoadingState label="Loading chart…" />}>
            <VitalsTrendChart readings={readings} metric={metric} />
          </Suspense>
        )}
      </Section>
      <div style={{ height: 16 }} />
      <DataTable
        ariaLabel="Vital signs"
        rows={list.data?.items}
        loading={list.isLoading}
        error={list.error}
        onRetry={() => void list.refetch()}
        getRowId={(v) => v.id}
        size="small"
        columns={[
          { key: 'at', header: 'Recorded', width: 170, render: (v) => formatDateTime(v.recordedAt) },
          {
            key: 'bp',
            header: 'BP',
            render: (v) => (
              <FlaggedValue value={formatBp(v.systolicBp, v.diastolicBp)} flag={flag(v, 'systolicBp', 'diastolicBp')} />
            ),
          },
          {
            key: 'pulse',
            header: 'Pulse',
            render: (v) => (v.pulse === null ? '—' : <FlaggedValue value={`${v.pulse}`} flag={flag(v, 'pulse')} />),
          },
          {
            key: 'spo2',
            header: 'SpO₂',
            render: (v) =>
              v.spO2 === null ? (
                '—'
              ) : (
                <FlaggedValue value={`${v.spO2}%${v.onSupplementalOxygen ? ' (O₂)' : ''}`} flag={flag(v, 'spo2')} />
              ),
          },
          {
            key: 'temp',
            header: 'Temperature',
            render: (v) =>
              v.temperatureCelsius === null ? (
                '—'
              ) : (
                <FlaggedValue value={formatTemperature(v.temperatureCelsius)} flag={flag(v, 'temperature')} />
              ),
          },
          {
            key: 'rr',
            header: 'RR',
            priority: 'low',
            render: (v) =>
              v.respiratoryRate === null ? (
                '—'
              ) : (
                <FlaggedValue value={`${v.respiratoryRate}`} flag={flag(v, 'respiratoryRate')} />
              ),
          },
          {
            key: 'weight',
            header: 'Weight / BMI',
            priority: 'low',
            render: (v) =>
              v.weightKg === null ? (
                '—'
              ) : (
                <FlaggedValue value={`${v.weightKg} kg${v.bmi ? ` · ${v.bmi}` : ''}`} flag={flag(v, 'bmi')} />
              ),
          },
          {
            key: 'glucose',
            header: 'Glucose',
            priority: 'low',
            render: (v) =>
              v.bloodGlucose === null ? (
                '—'
              ) : (
                <FlaggedValue value={`${v.bloodGlucose}`} flag={flag(v, 'bloodGlucose')} />
              ),
          },
          {
            key: 'pain',
            header: 'Pain',
            priority: 'low',
            render: (v) =>
              v.painScore === null ? '—' : <FlaggedValue value={`${v.painScore}/10`} flag={flag(v, 'painScore')} />,
          },
          { key: 'notes', header: 'Notes', priority: 'low', render: (v) => v.notes ?? '' },
        ]}
        rowActions={
          canRecord
            ? (v) => (
                <Menu>
                  <MenuTrigger disableButtonEnhancement>
                    <Button
                      size="small"
                      appearance="subtle"
                      icon={<MoreHorizontalRegular />}
                      aria-label={`Actions for vitals at ${formatDateTime(v.recordedAt)}`}
                      {...restoreFocusTarget}
                    />
                  </MenuTrigger>
                  <MenuPopover>
                    <MenuList>
                      <MenuItem icon={<DeleteRegular />} onClick={() => setVoiding(v)}>
                        Mark as entered in error
                      </MenuItem>
                    </MenuList>
                  </MenuPopover>
                </Menu>
              )
            : undefined
        }
        emptyTitle="No vitals recorded"
        emptyDescription={
          canRecord ? 'Use Record vitals to add the first set.' : 'Vitals recorded by nurses and doctors appear here.'
        }
        page={page}
        pageSize={20}
        totalCount={list.data?.totalCount}
        onPageChange={setPage}
      />
      <RecordVitalsDialog
        patientId={patientId}
        encounterId={null}
        open={recording}
        onClose={() => setRecording(false)}
      />
      <ReasonDialog
        open={voiding !== null}
        title="Mark these vitals as entered in error?"
        description={
          voiding
            ? `Recorded ${formatDateTime(voiding.recordedAt)}: ${vitalChips(voiding)
                .map((c) => `${c.label} ${c.value}`)
                .join(', ')}. The entry stays in the audit trail but no longer appears in summaries and trends.`
            : undefined
        }
        label="What was wrong"
        confirmLabel="Mark as entered in error"
        destructive
        busy={markError.isPending}
        onConfirm={(reason) => voiding && markError.mutate({ id: voiding.id, reason })}
        onCancel={() => setVoiding(null)}
      />
    </>
  );
}

/** Vitals recorded during one encounter, newest first (encounter workspace). */
export function EncounterVitalsList({ patientId, encounterId }: { patientId: string; encounterId: string }) {
  const styles = useStyles();
  const vitals = useQuery(emrQueries.vitals(patientId, encounterId, 1, 10));
  const items = vitals.data?.items ?? [];
  if (items.length === 0) {
    return null;
  }
  return (
    <div className={styles.form}>
      {items.map((v) => (
        <div key={v.id}>
          <Caption1 className={styles.muted}>{formatDateTime(v.recordedAt)}</Caption1>
          <VitalChipList chips={vitalChips(v)} />
        </div>
      ))}
    </div>
  );
}
