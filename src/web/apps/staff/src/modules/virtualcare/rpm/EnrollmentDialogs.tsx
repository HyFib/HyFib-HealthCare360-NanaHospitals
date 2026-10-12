import {
  Button,
  Caption1,
  Combobox,
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
  Option,
  Select,
  Spinner,
  Tag,
  TagGroup,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, useSession } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  CloseRequestScope,
  DateField,
  SelectField,
  TextAreaField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { ignoreEscapeWhile } from '@/lib/dialogFocus';
import { usePractitioners } from '@/lib/lookups';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import { isoToIstLocal, istLocalToIso } from '@/modules/scheduling/dates';
import { useNow, useToday } from '@/modules/scheduling/useNow';
import {
  ENROLLMENT_STATUSES,
  READING_TYPES,
  rpmApi,
  rpmKeys,
  rpmQueries,
  type IngestResult,
  type RpmEnrollmentDetail,
  type RpmReadingType,
} from './api';
import { enrollmentStatusText, programKindLabel, readingTypeInfo } from './labels';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  tags: { marginTop: '6px' },
  readingRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(140px, 1.3fr) minmax(90px, 1fr) minmax(90px, 0.8fr) auto',
    gap: '8px',
    alignItems: 'end',
  },
  presets: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
});

// ------------------------------------------------------------------ care team

/** Searchable multi-select of doctors and nurses, with the chosen ones shown as removable tags. */
function CareTeamField({
  value,
  onChange,
  error,
}: {
  value: string[];
  onChange: (ids: string[]) => void;
  error?: string;
}) {
  const styles = useStyles();
  const practitioners = usePractitioners(null);
  const [query, setQuery] = useState('');
  const all = practitioners.data ?? [];
  const name = (id: string) => all.find((p) => p.id === id)?.name ?? 'Clinician';
  const shown = all.filter((p) => p.name.toLowerCase().includes(query.trim().toLowerCase())).slice(0, 50);

  return (
    <Field
      label="Care team"
      required
      validationMessage={error}
      validationState={error ? 'error' : 'none'}
      hint="They receive the alerts for this patient."
    >
      <Combobox
        multiselect
        placeholder="Type a doctor's or nurse's name"
        value={query}
        selectedOptions={value}
        onChange={(event) => setQuery(event.target.value)}
        onOptionSelect={(_, data) => {
          onChange(data.selectedOptions.slice(0, 10));
          setQuery('');
        }}
      >
        {shown.map((p) => (
          <Option key={p.id} value={p.id}>
            {p.name}
          </Option>
        ))}
      </Combobox>
      {value.length > 0 && (
        <TagGroup
          className={styles.tags}
          onDismiss={(_, data) => onChange(value.filter((id) => id !== data.value))}
          aria-label="Care team"
        >
          {value.map((id) => (
            <Tag key={id} value={id} dismissible dismissIcon={{ 'aria-label': `Remove ${name(id)}` }} size="small">
              {name(id)}
            </Tag>
          ))}
        </TagGroup>
      )}
    </Field>
  );
}

// ------------------------------------------------------------------ enrol

const enrollSchema = z
  .object({
    patient: z.custom<PickedPatient | null>(),
    programId: z.string().nullable(),
    careTeam: z.array(z.string()),
    primaryPractitionerId: z.string().nullable(),
    startDate: z.string().nullable(),
    goals: z.string().trim().max(1000, 'Use at most 1000 characters.'),
    notes: z.string().trim().max(2000, 'Use at most 2000 characters.'),
  })
  .superRefine((v, ctx) => {
    if (!v.patient) {
      ctx.addIssue({ code: 'custom', path: ['patient'], message: 'Choose the patient.' });
    }
    if (!v.programId) {
      ctx.addIssue({ code: 'custom', path: ['programId'], message: 'Choose a programme.' });
    }
    if (v.careTeam.length === 0) {
      ctx.addIssue({ code: 'custom', path: ['careTeam'], message: 'Choose at least one care-team member.' });
    }
  });

type EnrollValues = z.infer<typeof enrollSchema>;

/** Enrols a patient in a monitoring programme; the programme's default limits are applied for review (RPM-01). */
export function EnrollDialog({
  open,
  onClose,
  onEnrolled,
}: {
  open: boolean;
  onClose: () => void;
  onEnrolled: (enrollmentId: string) => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <CloseRequestScope value={closing}>
          {open && <EnrollContent onClose={onClose} onEnrolled={onEnrolled} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function EnrollContent({ onClose, onEnrolled }: { onClose: () => void; onEnrolled: (enrollmentId: string) => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const today = useToday();
  const me = useSession((s) => s.user?.practitionerId);
  const programs = useQuery(rpmQueries.programs(true));
  const form = useForm<EnrollValues>({
    resolver: zodResolver(enrollSchema),
    defaultValues: {
      patient: null,
      programId: null,
      careTeam: me ? [me] : [],
      primaryPractitionerId: me ?? null,
      startDate: today,
      goals: '',
      notes: '',
    },
  });
  const [programId, careTeam] = useWatch({ control: form.control, name: ['programId', 'careTeam'] });
  const program = programs.data?.find((p) => p.id === programId);
  const practitioners = usePractitioners(null);

  const enroll = useMutation({
    mutationFn: (v: EnrollValues) =>
      rpmApi.enroll({
        patientId: v.patient!.id,
        programId: v.programId!,
        careTeamPractitionerIds: v.careTeam,
        primaryPractitionerId:
          v.primaryPractitionerId && v.careTeam.includes(v.primaryPractitionerId) ? v.primaryPractitionerId : null,
        startDate: v.startDate,
        goals: v.goals || null,
        notes: v.notes || null,
      }),
    onSuccess: (created) => {
      notify.success('Patient enrolled', "The programme's standard limits were applied. Review them for this patient.");
      void queryClient.invalidateQueries({ queryKey: rpmKeys.all });
      onEnrolled(created.id);
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not enrol the patient');
      }
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: enroll.isPending, onClose });

  return (
    <form onSubmit={form.handleSubmit((v) => enroll.mutate(v))} noValidate>
      <DialogBody>
        <DialogTitle>Enrol patient in remote monitoring</DialogTitle>
        <DialogContent className={styles.form}>
          <Controller
            control={form.control}
            name="patient"
            render={({ field, fieldState }) => (
              <PatientPicker
                value={field.value ?? null}
                onChange={field.onChange}
                required
                validationMessage={fieldState.error?.message}
              />
            )}
          />
          <SelectField
            control={form.control}
            name="programId"
            label="Programme"
            required
            placeholder={programs.isLoading ? 'Loading…' : 'Choose programme'}
            options={(programs.data ?? []).map((p) => ({
              value: p.id,
              label: `${p.name} (${programKindLabel[p.kind]})`,
            }))}
          />
          {program && (
            <Caption1 className={styles.muted}>
              {program.description ? `${program.description} ` : ''}Expected readings: {program.expectedReadingsPerDay}{' '}
              a day.
            </Caption1>
          )}
          <Controller
            control={form.control}
            name="careTeam"
            render={({ field, fieldState }) => (
              <CareTeamField value={field.value} onChange={field.onChange} error={fieldState.error?.message} />
            )}
          />
          <SelectField
            control={form.control}
            name="primaryPractitionerId"
            label="Primary clinician"
            placeholder="None"
            options={careTeam.map((id) => ({
              value: id,
              label: practitioners.data?.find((p) => p.id === id)?.name ?? 'Clinician',
            }))}
          />
          <DateField control={form.control} name="startDate" label="Monitoring starts" />
          <TextAreaField
            control={form.control}
            name="goals"
            label="Goals (optional)"
            rows={2}
            maxLength={1000}
            placeholder="e.g. BP below 140/90 within 8 weeks"
          />
          <TextAreaField control={form.control} name="notes" label="Notes (optional)" rows={2} maxLength={2000} />
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={enroll.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={enroll.isPending}
            icon={enroll.isPending ? <Spinner size="tiny" /> : undefined}
          >
            Enrol patient
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}

// ------------------------------------------------------------------ edit enrolment

const editSchema = z
  .object({
    status: z.enum(ENROLLMENT_STATUSES),
    statusReason: z.string().trim().max(300, 'Use at most 300 characters.'),
    careTeam: z.array(z.string()),
    primaryPractitionerId: z.string().nullable(),
    goals: z.string().trim().max(1000, 'Use at most 1000 characters.'),
    notes: z.string().trim().max(2000, 'Use at most 2000 characters.'),
  })
  .superRefine((v, ctx) => {
    if (v.careTeam.length === 0) {
      ctx.addIssue({ code: 'custom', path: ['careTeam'], message: 'Choose at least one care-team member.' });
    }
    if (v.status === 'Withdrawn' && !v.statusReason) {
      ctx.addIssue({ code: 'custom', path: ['statusReason'], message: 'Say why the patient is withdrawn.' });
    }
  });

type EditValues = z.infer<typeof editSchema>;

/** Pause, complete or withdraw monitoring, and change the care team, goals and notes. */
export function EditEnrollmentDialog({
  enrollment,
  onClose,
}: {
  enrollment: RpmEnrollmentDetail | null;
  onClose: () => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={enrollment !== null} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 640 }}>
        {enrollment && (
          <CloseRequestScope value={closing}>
            <EditContent key={`${enrollment.id}-${enrollment.version}`} enrollment={enrollment} onClose={onClose} />
          </CloseRequestScope>
        )}
      </DialogSurface>
    </Dialog>
  );
}

function EditContent({ enrollment, onClose }: { enrollment: RpmEnrollmentDetail; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const practitioners = usePractitioners(null);
  const form = useForm<EditValues>({
    resolver: zodResolver(editSchema),
    defaultValues: {
      status: enrollment.status,
      statusReason: enrollment.statusReason ?? '',
      careTeam: enrollment.careTeam.map((m) => m.practitionerId),
      primaryPractitionerId: enrollment.careTeam.find((m) => m.isPrimary)?.practitionerId ?? null,
      goals: enrollment.goals ?? '',
      notes: enrollment.notes ?? '',
    },
  });
  const [status, careTeam] = useWatch({ control: form.control, name: ['status', 'careTeam'] });
  const nameOf = (id: string) =>
    practitioners.data?.find((p) => p.id === id)?.name ??
    enrollment.careTeam.find((m) => m.practitionerId === id)?.name ??
    'Clinician';

  const save = useMutation({
    mutationFn: (v: EditValues) =>
      rpmApi.updateEnrollment(enrollment.id, {
        careTeamPractitionerIds: v.careTeam,
        primaryPractitionerId:
          v.primaryPractitionerId && v.careTeam.includes(v.primaryPractitionerId) ? v.primaryPractitionerId : null,
        status: v.status,
        statusReason: v.statusReason || null,
        goals: v.goals || null,
        notes: v.notes || null,
        version: enrollment.version,
      }),
    onSuccess: () => {
      notify.success('Enrolment saved');
      void queryClient.invalidateQueries({ queryKey: rpmKeys.all });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.isConflict) {
        notify.error(error, 'Someone else changed this enrolment');
        void queryClient.invalidateQueries({ queryKey: rpmKeys.enrollment(enrollment.id) });
        onClose();
        return;
      }
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the enrolment');
      }
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <form
      onSubmit={form.handleSubmit((v) => !save.isPending && save.mutate(v))}
      onKeyDown={ignoreEscapeWhile(save.isPending)}
      noValidate
    >
      <DialogBody>
        <DialogTitle>
          Monitoring for {enrollment.patientName}
          <Caption1 block className={styles.muted}>
            {enrollment.programName}
          </Caption1>
        </DialogTitle>
        <DialogContent className={styles.form}>
          <ChoiceField
            control={form.control}
            name="status"
            label="Status"
            hint="Paused keeps collecting readings but raises no alerts."
            options={ENROLLMENT_STATUSES.map((s) => ({ value: s, label: enrollmentStatusText[s].label }))}
          />
          {status !== 'Active' && (
            <TextAreaField
              control={form.control}
              name="statusReason"
              label="Reason"
              required={status === 'Withdrawn'}
              rows={2}
              maxLength={300}
            />
          )}
          <Controller
            control={form.control}
            name="careTeam"
            render={({ field, fieldState }) => (
              <CareTeamField value={field.value} onChange={field.onChange} error={fieldState.error?.message} />
            )}
          />
          <SelectField
            control={form.control}
            name="primaryPractitionerId"
            label="Primary clinician"
            placeholder="None"
            options={careTeam.map((id) => ({ value: id, label: nameOf(id) }))}
          />
          <TextAreaField control={form.control} name="goals" label="Goals" rows={2} maxLength={1000} />
          <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={2000} />
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            // Keeps focus while saving (a disabled button drops it); Enter can still submit, which is ignored.
            disabledFocusable={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" /> : undefined}
          >
            Save
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}

// ------------------------------------------------------------------ record readings

interface ReadingRow {
  key: number;
  type: RpmReadingType;
  value: string;
  unit: string;
}

const PRESETS: { label: string; types: RpmReadingType[] }[] = [
  { label: 'Blood pressure', types: ['SystolicBp', 'DiastolicBp', 'HeartRate'] },
  { label: 'Blood sugar', types: ['Glucose'] },
  { label: 'Oxygen', types: ['SpO2', 'HeartRate'] },
  { label: 'Weight', types: ['Weight'] },
  { label: 'Temperature', types: ['Temperature'] },
];

/**
 * Readings entered by staff, for example read out by the patient on a follow-up call. They are checked against the
 * patient's limits exactly like device readings (and can raise alerts).
 */
export function RecordReadingsDialog({
  enrollment,
  onClose,
}: {
  enrollment: RpmEnrollmentDetail | null;
  onClose: () => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={enrollment !== null} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <CloseRequestScope value={closing}>
          {enrollment && <RecordContent key={enrollment.id} enrollment={enrollment} onClose={onClose} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function RecordContent({ enrollment, onClose }: { enrollment: RpmEnrollmentDetail; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const now = useNow();
  const firstType = enrollment.thresholds[0]?.type ?? 'SystolicBp';
  const [rows, setRows] = useState<ReadingRow[]>([
    { key: 1, type: firstType, value: '', unit: readingTypeInfo[firstType].unit },
  ]);
  const [measuredAt, setMeasuredAt] = useState(() => isoToIstLocal(new Date(now).toISOString()));
  const [result, setResult] = useState<IngestResult | null>(null);
  const [problem, setProblem] = useState<string | null>(null);

  const setRow = (key: number, patch: Partial<ReadingRow>) =>
    setRows((current) => current.map((row) => (row.key === key ? { ...row, ...patch } : row)));
  const addRows = (types: RpmReadingType[]) =>
    setRows((current) => {
      const blank = current.filter((row) => row.value.trim() !== '');
      const start = Math.max(0, ...current.map((row) => row.key)) + 1;
      return [
        ...blank,
        ...types.map((type, index) => ({ key: start + index, type, value: '', unit: readingTypeInfo[type].unit })),
      ];
    });

  const record = useMutation({
    mutationFn: () => {
      const at = istLocalToIso(measuredAt);
      return rpmApi.recordReadings(
        enrollment.id,
        rows
          .filter((row) => row.value.trim() !== '')
          .map((row) => ({
            type: row.type,
            value: Number(row.value),
            unit: row.unit,
            measuredAt: at,
            textValue: null,
          })),
      );
    },
    onSuccess: (ingest) => {
      void queryClient.invalidateQueries({ queryKey: rpmKeys.all });
      if (ingest.rejected.length > 0) {
        setResult(ingest);
        return;
      }
      notify.success(
        `${ingest.accepted} reading${ingest.accepted === 1 ? '' : 's'} saved`,
        [
          ingest.duplicates > 0 ? `${ingest.duplicates} were already recorded` : null,
          ingest.alertsRaised > 0 ? `${ingest.alertsRaised} alert${ingest.alertsRaised === 1 ? '' : 's'} raised` : null,
        ]
          .filter(Boolean)
          .join(' · ') || undefined,
      );
      onClose();
    },
    onError: (error) => setProblem(isApiError(error) ? error.userMessage : 'The readings could not be saved.'),
  });

  const filled = rows.filter((row) => row.value.trim() !== '');
  const invalid = filled.some((row) => Number.isNaN(Number(row.value)));

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ rows, measuredAt }) && result === null,
    busy: record.isPending,
    onClose,
  });

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (filled.length > 0 && !invalid && istLocalToIso(measuredAt)) {
          setProblem(null);
          record.mutate();
        }
      }}
    >
      <DialogBody>
        <DialogTitle>
          Record readings
          <Caption1 block className={styles.muted}>
            {enrollment.patientName} · {enrollment.uhid}
          </Caption1>
        </DialogTitle>
        <DialogContent className={styles.form}>
          <div className={styles.presets}>
            {PRESETS.map((preset) => (
              <Button
                key={preset.label}
                size="small"
                appearance="outline"
                icon={<AddRegular />}
                onClick={() => addRows(preset.types)}
              >
                {preset.label}
              </Button>
            ))}
          </div>
          {rows.map((row) => (
            <div key={row.key} className={styles.readingRow}>
              <Field label="Reading">
                <Select
                  value={row.type}
                  onChange={(_, d) =>
                    setRow(row.key, {
                      type: d.value as RpmReadingType,
                      unit: readingTypeInfo[d.value as RpmReadingType].unit,
                    })
                  }
                >
                  {READING_TYPES.filter((t) => t !== 'EcgRhythm').map((t) => (
                    <option key={t} value={t}>
                      {readingTypeInfo[t].label}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field
                label="Value"
                validationMessage={row.value && Number.isNaN(Number(row.value)) ? 'Enter a number' : undefined}
              >
                <Input
                  type="number"
                  inputMode="decimal"
                  value={row.value}
                  onChange={(_, d) => setRow(row.key, { value: d.value })}
                />
              </Field>
              <Field label="Unit">
                <Select value={row.unit} onChange={(_, d) => setRow(row.key, { unit: d.value })}>
                  {readingTypeInfo[row.type].inputUnits.map((u) => (
                    <option key={u} value={u}>
                      {u}
                    </option>
                  ))}
                </Select>
              </Field>
              <Button
                appearance="subtle"
                icon={<DeleteRegular />}
                aria-label={`Remove ${readingTypeInfo[row.type].label}`}
                disabled={rows.length === 1}
                onClick={() => setRows((current) => current.filter((r) => r.key !== row.key))}
              />
            </div>
          ))}
          <Button
            size="small"
            appearance="transparent"
            icon={<AddRegular />}
            onClick={() => addRows([firstType])}
            style={{ alignSelf: 'flex-start' }}
          >
            Add another reading
          </Button>
          <Field label="Measured at" required hint="Indian Standard Time">
            <Input type="datetime-local" value={measuredAt} onChange={(_, d) => setMeasuredAt(d.value)} />
          </Field>
          {result && result.rejected.length > 0 && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>
                  {result.accepted} saved, {result.rejected.length} not accepted
                </MessageBarTitle>
                {result.rejected.join(' ')}
              </MessageBarBody>
            </MessageBar>
          )}
          {problem && (
            <MessageBar intent="error">
              <MessageBarBody>{problem}</MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={record.isPending}>
            {result ? 'Close' : 'Cancel'}
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={filled.length === 0 || invalid || record.isPending}
            icon={record.isPending ? <Spinner size="tiny" /> : undefined}
          >
            Save {filled.length > 1 ? `${filled.length} readings` : 'reading'}
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}
