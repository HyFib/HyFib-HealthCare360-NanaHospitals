import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, todayIso, useSession } from '@hc360/core';
import {
  ChoiceField,
  DateField,
  FormGrid,
  FullRow,
  LookupField,
  NumberField,
  SelectField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { Controller, useFieldArray, useForm, useWatch, type FieldValues, type UseFormReturn } from 'react-hook-form';
import { z } from 'zod';
import { searchPractitioners } from '@/lib/lookups';
import { LATERALITIES } from '@/modules/ot/api';
import { fromIstInput, nowIstInput, toIstInput } from '@/modules/ot/shared/datetime';
import { applyFormErrors } from '@/modules/ot/shared/errors';
import { ActionError } from '@/modules/ot/shared/feedback';
import { CheckboxGroupField, DateTimeField } from '@/modules/ot/shared/fields';
import { PatientPicker } from '@/modules/patients/PatientPicker';
import {
  ASSESSMENT_TYPES,
  MODALITIES,
  ORDER_PRIORITIES,
  physioApi,
  physioKeys,
  type PhysioReferralDetail,
  type PhysioSessionDto,
  type PhysioWorklistItem,
  type TreatmentPlanDto,
} from './api';
import { assessmentTypeLabel, modalityLabel, MRC_GRADES, orderPriorityLabel } from './labels';
import { defaultIntervalDays, scheduleLimit, sessionTimes } from './logic';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  rows: { display: 'flex', flexDirection: 'column', gap: '8px' },
  rom: {
    display: 'grid',
    gridTemplateColumns:
      'minmax(120px, 2fr) minmax(120px, 2fr) minmax(110px, 1.4fr) minmax(80px, 1fr) minmax(80px, 1fr) auto',
    gap: '8px',
    alignItems: 'start',
  },
  strength: {
    display: 'grid',
    gridTemplateColumns: 'minmax(160px, 2fr) minmax(110px, 1.4fr) minmax(200px, 2fr) auto',
    gap: '8px',
    alignItems: 'start',
  },
  remove: { marginTop: '24px' },
  muted: { color: tokens.colorNeutralForeground3 },
  preview: { display: 'flex', flexWrap: 'wrap', gap: '4px 12px' },
});

const sideOptions = LATERALITIES.map((l) => ({ value: l, label: l === 'NotApplicable' ? 'Not applicable' : l }));

/** Shared dialog frame: title, body, error, Cancel + primary submit. */
function FormDialog<T extends FieldValues>({
  title,
  subtitle,
  form,
  onSubmit,
  onClose,
  busy,
  error,
  submitLabel,
  maxWidth = 640,
  children,
}: {
  title: string;
  subtitle?: string;
  form: UseFormReturn<T>;
  onSubmit: (values: T) => void;
  onClose: () => void;
  busy: boolean;
  error: unknown;
  submitLabel: string;
  maxWidth?: number;
  children: ReactNode;
}) {
  const styles = useStyles();
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !busy && onClose()}>
      <DialogSurface style={{ maxWidth }}>
        <form onSubmit={form.handleSubmit((values) => !busy && onSubmit(values))} noValidate>
          <DialogBody>
            <DialogTitle>
              {title}
              {subtitle && (
                <Caption1 block className={styles.muted}>
                  {subtitle}
                </Caption1>
              )}
            </DialogTitle>
            <DialogContent className={styles.body}>
              {children}
              <ActionError error={error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={busy}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={busy}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={busy}
                icon={busy ? <Spinner size="tiny" /> : undefined}
              >
                {submitLabel}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

// ---------------------------------------------------------------- referral

const referralSchema = z
  .object({
    patient: z.object({ id: z.string(), uhid: z.string(), fullName: z.string() }).nullable(),
    priority: z.enum(ORDER_PRIORITIES),
    reason: z.string().trim().min(1, 'Describe the problem and what physiotherapy should address.').max(1000),
    sessionsOrdered: z.number().nullable(),
    referredByPractitionerId: z.string().nullable(),
  })
  .superRefine((v, ctx) => {
    if (!v.patient) {
      ctx.addIssue({ code: 'custom', path: ['patient'], message: 'Choose the patient.' });
    }
    if (v.sessionsOrdered !== null && (v.sessionsOrdered < 1 || v.sessionsOrdered > 100)) {
      ctx.addIssue({ code: 'custom', path: ['sessionsOrdered'], message: 'Enter 1 to 100 sessions.' });
    }
  });

type ReferralValues = z.infer<typeof referralSchema>;

/** Manual referral (most referrals arrive automatically from doctors' physiotherapy orders). */
export function NewReferralDialog({ onClose }: { onClose: () => void }) {
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const practitionerId = useSession((s) => s.user?.practitionerId ?? null);
  const form = useForm<ReferralValues>({
    resolver: zodResolver(referralSchema),
    defaultValues: {
      patient: null,
      priority: 'Routine',
      reason: '',
      sessionsOrdered: null,
      referredByPractitionerId: practitionerId,
    },
  });
  const create = useMutation({
    mutationFn: (v: ReferralValues) =>
      physioApi.createReferral({
        patientId: v.patient?.id ?? '',
        visitId: null,
        admissionId: null,
        referredByPractitionerId: v.referredByPractitionerId,
        priority: v.priority,
        reason: v.reason,
        sessionsOrdered: v.sessionsOrdered,
      }),
    onSuccess: (created) => {
      notify.success('Referral created');
      void queryClient.invalidateQueries({ queryKey: physioKeys.all });
      onClose();
      void navigate({ to: '/physio/referrals/$id', params: { id: created.id } });
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  return (
    <FormDialog
      title="Refer to physiotherapy"
      form={form}
      onSubmit={(v) => create.mutate(v)}
      onClose={onClose}
      busy={create.isPending}
      error={create.error}
      submitLabel="Create referral"
    >
      <Controller
        control={form.control}
        name="patient"
        render={({ field, fieldState }) => (
          <PatientPicker
            value={field.value}
            onChange={field.onChange}
            required
            validationMessage={fieldState.error?.message}
          />
        )}
      />
      <ChoiceField
        control={form.control}
        name="priority"
        label="Priority"
        options={ORDER_PRIORITIES.map((p) => ({ value: p, label: orderPriorityLabel[p] }))}
      />
      <TextAreaField
        control={form.control}
        name="reason"
        label="Reason for referral"
        required
        rows={3}
        maxLength={1000}
        placeholder="Diagnosis, problem, and what you want physiotherapy to achieve"
      />
      <FormGrid columns={2}>
        <NumberField
          control={form.control}
          name="sessionsOrdered"
          label="Sessions prescribed"
          min={1}
          max={100}
          hint="Optional. Becomes the default for the plan."
        />
        <LookupField
          control={form.control}
          name="referredByPractitionerId"
          label="Referred by"
          load={searchPractitioners}
          placeholder="Type a name"
        />
      </FormGrid>
    </FormDialog>
  );
}

// ---------------------------------------------------------------- assessment

const assessmentSchema = z
  .object({
    type: z.enum(ASSESSMENT_TYPES),
    chiefComplaint: z.string().trim().min(1, 'Enter the chief complaint.').max(1000),
    painScore: z.number().nullable(),
    painLocation: z.string().trim().max(200),
    rangeOfMotion: z
      .array(
        z.object({
          joint: z.string().trim().min(1, 'Joint?').max(50),
          movement: z.string().trim().min(1, 'Movement?').max(50),
          side: z.enum(LATERALITIES),
          activeDegrees: z.number().nullable(),
          passiveDegrees: z.number().nullable(),
        }),
      )
      .max(60),
    strength: z
      .array(
        z.object({
          muscleGroup: z.string().trim().min(1, 'Muscle group?').max(50),
          side: z.enum(LATERALITIES),
          mrcGrade: z.string(),
        }),
      )
      .max(60),
    functionalStatus: z.string().trim().max(2000),
    goals: z.string().trim().max(2000),
    notes: z.string().trim().max(2000),
  })
  .superRefine((v, ctx) => {
    if (v.painScore !== null && (v.painScore < 0 || v.painScore > 10)) {
      ctx.addIssue({ code: 'custom', path: ['painScore'], message: 'Pain score is 0 to 10.' });
    }
    v.rangeOfMotion.forEach((row, i) => {
      for (const key of ['activeDegrees', 'passiveDegrees'] as const) {
        const value = row[key];
        if (value !== null && (value < 0 || value > 360)) {
          ctx.addIssue({ code: 'custom', path: ['rangeOfMotion', i, key], message: '0–360°' });
        }
      }
    });
  });

type AssessmentValues = z.infer<typeof assessmentSchema>;

/** Assessment (initial, review or discharge): complaint, pain 0–10, goniometry and MRC muscle power. */
export function AssessmentDialog({ referral, onClose }: { referral: PhysioReferralDetail; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<AssessmentValues>({
    resolver: zodResolver(assessmentSchema),
    defaultValues: {
      type: referral.assessments.length === 0 ? 'Initial' : 'Review',
      chiefComplaint: referral.assessments.at(-1)?.chiefComplaint ?? '',
      painScore: null,
      painLocation: referral.assessments.at(-1)?.painLocation ?? '',
      rangeOfMotion: [],
      strength: [],
      functionalStatus: '',
      goals: referral.assessments.at(-1)?.goals ?? '',
      notes: '',
    },
  });
  const rom = useFieldArray({ control: form.control, name: 'rangeOfMotion' });
  const strength = useFieldArray({ control: form.control, name: 'strength' });

  const save = useMutation({
    mutationFn: (v: AssessmentValues) =>
      physioApi.recordAssessment(referral.id, {
        type: v.type,
        chiefComplaint: v.chiefComplaint,
        painScore: v.painScore,
        painLocation: v.painLocation || null,
        rangeOfMotion: v.rangeOfMotion.length ? v.rangeOfMotion : null,
        strength: v.strength.length
          ? v.strength.map((s) => ({ muscleGroup: s.muscleGroup, side: s.side, mrcGrade: Number(s.mrcGrade) }))
          : null,
        functionalStatus: v.functionalStatus || null,
        goals: v.goals || null,
        notes: v.notes || null,
      }),
    onSuccess: () => {
      notify.success('Assessment recorded');
      void queryClient.invalidateQueries({ queryKey: physioKeys.all });
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  return (
    <FormDialog
      title="Record assessment"
      subtitle={referral.patient ? `${referral.patient.fullName} · ${referral.patient.uhid}` : undefined}
      form={form}
      onSubmit={(v) => save.mutate(v)}
      onClose={onClose}
      busy={save.isPending}
      error={save.error}
      submitLabel="Save assessment"
      maxWidth={900}
    >
      <FormGrid columns={3}>
        <ChoiceField
          control={form.control}
          name="type"
          label="Assessment"
          options={ASSESSMENT_TYPES.map((t) => ({ value: t, label: assessmentTypeLabel[t] }))}
        />
        <NumberField
          control={form.control}
          name="painScore"
          label="Pain now (0–10)"
          min={0}
          max={10}
          hint="0 no pain · 10 worst imaginable"
        />
        <TextField control={form.control} name="painLocation" label="Pain location" maxLength={200} />
        <FullRow>
          <TextAreaField
            control={form.control}
            name="chiefComplaint"
            label="Chief complaint"
            required
            rows={2}
            maxLength={1000}
          />
        </FullRow>
      </FormGrid>

      <div className={styles.rows}>
        <Text weight="semibold">Range of motion (goniometer, degrees)</Text>
        {rom.fields.map((row, index) => (
          <div key={row.id} className={styles.rom}>
            <TextField control={form.control} name={`rangeOfMotion.${index}.joint`} label="Joint" maxLength={50} />
            <TextField
              control={form.control}
              name={`rangeOfMotion.${index}.movement`}
              label="Movement"
              maxLength={50}
            />
            <SelectField
              control={form.control}
              name={`rangeOfMotion.${index}.side`}
              label="Side"
              options={sideOptions}
            />
            <NumberField
              control={form.control}
              name={`rangeOfMotion.${index}.activeDegrees`}
              label="Active °"
              min={0}
              max={360}
            />
            <NumberField
              control={form.control}
              name={`rangeOfMotion.${index}.passiveDegrees`}
              label="Passive °"
              min={0}
              max={360}
            />
            <Button
              className={styles.remove}
              appearance="subtle"
              icon={<DeleteRegular />}
              aria-label={`Remove range of motion row ${index + 1}`}
              onClick={() => rom.remove(index)}
            />
          </div>
        ))}
        <div>
          <Button
            icon={<AddRegular />}
            onClick={() =>
              rom.append({ joint: '', movement: '', side: 'Left', activeDegrees: null, passiveDegrees: null })
            }
          >
            Add joint movement
          </Button>
        </div>
      </div>

      <div className={styles.rows}>
        <Text weight="semibold">Muscle power (MRC scale)</Text>
        {strength.fields.map((row, index) => (
          <div key={row.id} className={styles.strength}>
            <TextField
              control={form.control}
              name={`strength.${index}.muscleGroup`}
              label="Muscle group"
              maxLength={50}
            />
            <SelectField control={form.control} name={`strength.${index}.side`} label="Side" options={sideOptions} />
            <SelectField
              control={form.control}
              name={`strength.${index}.mrcGrade`}
              label="Grade"
              options={MRC_GRADES.map((g) => ({ value: String(g.value), label: g.label }))}
            />
            <Button
              className={styles.remove}
              appearance="subtle"
              icon={<DeleteRegular />}
              aria-label={`Remove muscle power row ${index + 1}`}
              onClick={() => strength.remove(index)}
            />
          </div>
        ))}
        <div>
          <Button
            icon={<AddRegular />}
            onClick={() => strength.append({ muscleGroup: '', side: 'Left', mrcGrade: '5' })}
          >
            Add muscle group
          </Button>
        </div>
      </div>

      <FormGrid columns={2}>
        <TextAreaField
          control={form.control}
          name="functionalStatus"
          label="Functional status"
          rows={2}
          maxLength={2000}
          placeholder="Walking, stairs, transfers, self-care"
        />
        <TextAreaField control={form.control} name="goals" label="Goals" rows={2} maxLength={2000} />
        <FullRow>
          <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={2000} />
        </FullRow>
      </FormGrid>
    </FormDialog>
  );
}

// ---------------------------------------------------------------- treatment plan

const planSchema = z
  .object({
    modalities: z.array(z.enum(MODALITIES)).min(1, 'Choose at least one modality.'),
    exerciseProgramme: z.string().trim().max(4000),
    sessionsPlanned: z.number().nullable(),
    sessionsPerWeek: z.number().nullable(),
    sessionMinutes: z.number().nullable(),
    startDate: z.string().nullable(),
    precautions: z.string().trim().max(1000),
    goals: z.string().trim().max(2000),
  })
  .superRefine((v, ctx) => {
    if (v.sessionsPlanned !== null && (v.sessionsPlanned < 1 || v.sessionsPlanned > 100)) {
      ctx.addIssue({ code: 'custom', path: ['sessionsPlanned'], message: 'Enter 1 to 100 sessions.' });
    }
    if (v.sessionsPerWeek === null || v.sessionsPerWeek < 1 || v.sessionsPerWeek > 14) {
      ctx.addIssue({ code: 'custom', path: ['sessionsPerWeek'], message: 'Enter 1 to 14 sessions a week.' });
    }
    if (v.sessionMinutes === null || v.sessionMinutes < 10 || v.sessionMinutes > 180) {
      ctx.addIssue({ code: 'custom', path: ['sessionMinutes'], message: 'Enter 10 to 180 minutes.' });
    }
    if (!v.startDate) {
      ctx.addIssue({ code: 'custom', path: ['startDate'], message: 'Choose the start date.' });
    }
  });

type PlanValues = z.infer<typeof planSchema>;

/** Treatment plan (PHY-02). Needs an assessment first; revising it keeps the booked sessions. */
export function TreatmentPlanDialog({ referral, onClose }: { referral: PhysioReferralDetail; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const plan: TreatmentPlanDto | null = referral.plan;
  const booked = referral.sessions.filter((s) => s.status !== 'Cancelled').length;
  const form = useForm<PlanValues>({
    resolver: zodResolver(planSchema),
    defaultValues: {
      modalities: plan?.modalities ?? [],
      exerciseProgramme: plan?.exerciseProgramme ?? '',
      sessionsPlanned: plan?.sessionsPlanned ?? referral.sessionsOrdered ?? null,
      sessionsPerWeek: plan?.sessionsPerWeek ?? 3,
      sessionMinutes: plan?.sessionMinutes ?? 30,
      startDate: plan?.startDate ?? todayIso(),
      precautions: plan?.precautions ?? '',
      goals: plan?.goals ?? referral.assessments.at(-1)?.goals ?? '',
    },
  });
  const [sessionsPlanned] = useWatch({ control: form.control, name: ['sessionsPlanned'] });

  const save = useMutation({
    mutationFn: (v: PlanValues) =>
      physioApi.savePlan(referral.id, {
        modalities: v.modalities,
        exerciseProgramme: v.exerciseProgramme || null,
        sessionsPlanned: v.sessionsPlanned,
        sessionsPerWeek: v.sessionsPerWeek ?? 3,
        sessionMinutes: v.sessionMinutes ?? 30,
        startDate: v.startDate ?? todayIso(),
        precautions: v.precautions || null,
        goals: v.goals || null,
        version: plan?.version ?? null,
      }),
    onSuccess: () => {
      notify.success(plan ? 'Treatment plan revised' : 'Treatment plan saved');
      void queryClient.invalidateQueries({ queryKey: physioKeys.all });
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  return (
    <FormDialog
      title={plan ? `Revise treatment plan (revision ${plan.revision + 1})` : 'Treatment plan'}
      form={form}
      onSubmit={(v) => save.mutate(v)}
      onClose={onClose}
      busy={save.isPending}
      error={save.error}
      submitLabel="Save plan"
      maxWidth={820}
    >
      <CheckboxGroupField
        control={form.control}
        name="modalities"
        label="Modalities"
        required
        layout="columns"
        options={MODALITIES.map((m) => ({ value: m, label: modalityLabel[m] }))}
      />
      <FormGrid columns={4}>
        <NumberField
          control={form.control}
          name="sessionsPlanned"
          label="Sessions planned"
          min={Math.max(1, booked)}
          max={100}
          hint={
            referral.sessionsOrdered ? `${referral.sessionsOrdered} prescribed` : 'Blank uses the number prescribed'
          }
        />
        <NumberField control={form.control} name="sessionsPerWeek" label="Sessions a week" required min={1} max={14} />
        <NumberField
          control={form.control}
          name="sessionMinutes"
          label="Minutes per session"
          required
          min={10}
          max={180}
          step={5}
        />
        <DateField control={form.control} name="startDate" label="Start date" required />
      </FormGrid>
      {booked > 0 && sessionsPlanned !== null && sessionsPlanned < booked && (
        <MessageBar intent="warning">
          <MessageBarBody>{booked} sessions are already booked; the plan cannot allow fewer.</MessageBarBody>
        </MessageBar>
      )}
      <TextAreaField
        control={form.control}
        name="exerciseProgramme"
        label="Exercise programme"
        rows={4}
        maxLength={4000}
      />
      <FormGrid columns={2}>
        <TextAreaField control={form.control} name="precautions" label="Precautions" rows={2} maxLength={1000} />
        <TextAreaField control={form.control} name="goals" label="Goals" rows={2} maxLength={2000} />
      </FormGrid>
    </FormDialog>
  );
}

// ---------------------------------------------------------------- sessions

const scheduleSchema = z
  .object({ firstSessionAt: z.string().nullable(), count: z.number().nullable(), intervalDays: z.number().nullable() })
  .superRefine((v, ctx) => {
    if (!fromIstInput(v.firstSessionAt)) {
      ctx.addIssue({
        code: 'custom',
        path: ['firstSessionAt'],
        message: 'Choose the date and time of the first session.',
      });
    }
    if (v.count === null || v.count < 1 || v.count > 30) {
      ctx.addIssue({ code: 'custom', path: ['count'], message: 'Book 1 to 30 sessions at a time.' });
    }
    if (v.intervalDays !== null && (v.intervalDays < 1 || v.intervalDays > 30)) {
      ctx.addIssue({ code: 'custom', path: ['intervalDays'], message: '1 to 30 days apart.' });
    }
  });

type ScheduleValues = z.infer<typeof scheduleSchema>;

/** Books sessions of the plan; never more than the plan allows (the server refuses extra ones too). */
export function ScheduleSessionsDialog({ referral, onClose }: { referral: PhysioReferralDetail; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const limit = scheduleLimit(referral.plan, referral.sessions);
  const interval = defaultIntervalDays(referral.plan?.sessionsPerWeek ?? 3);
  const form = useForm<ScheduleValues>({
    resolver: zodResolver(scheduleSchema),
    defaultValues: {
      firstSessionAt: `${todayIso(1)}T10:00`,
      count: Math.min(limit, 5) || null,
      intervalDays: interval,
    },
  });
  const [first, count, intervalDays] = useWatch({
    control: form.control,
    name: ['firstSessionAt', 'count', 'intervalDays'],
  });
  const firstIso = fromIstInput(first);
  const preview = firstIso && count ? sessionTimes(firstIso, Math.min(count, 30), intervalDays ?? interval) : [];
  const tooMany = count !== null && count > limit;

  const schedule = useMutation({
    mutationFn: (v: ScheduleValues) =>
      physioApi.scheduleSessions(referral.id, fromIstInput(v.firstSessionAt) ?? '', v.count ?? 1, v.intervalDays),
    onSuccess: (ids) => {
      notify.success(`${ids.length} session${ids.length === 1 ? '' : 's'} booked`);
      void queryClient.invalidateQueries({ queryKey: physioKeys.all });
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  return (
    <FormDialog
      title="Book sessions"
      form={form}
      onSubmit={(v) => !tooMany && schedule.mutate(v)}
      onClose={onClose}
      busy={schedule.isPending}
      error={schedule.error}
      submitLabel="Book sessions"
    >
      <Text>
        The plan allows {limit} more session{limit === 1 ? '' : 's'}. To add more, revise the plan first.
      </Text>
      <FormGrid columns={3}>
        <DateTimeField control={form.control} name="firstSessionAt" label="First session" required />
        <NumberField
          control={form.control}
          name="count"
          label="Number of sessions"
          required
          min={1}
          max={Math.max(1, limit)}
        />
        <NumberField
          control={form.control}
          name="intervalDays"
          label="Days apart"
          min={1}
          max={30}
          hint={`Plan frequency gives every ${interval} day${interval === 1 ? '' : 's'}.`}
        />
      </FormGrid>
      {tooMany && (
        <MessageBar intent="warning">
          <MessageBarBody>Only {limit} more can be booked under the current plan.</MessageBarBody>
        </MessageBar>
      )}
      {preview.length > 0 && !tooMany && (
        <div>
          <Caption1 className={styles.muted}>Sessions to be booked:</Caption1>
          <div className={styles.preview}>
            {preview.map((at, i) => (
              <Caption1 key={at}>
                {i + 1}. {formatDateTime(at)}
              </Caption1>
            ))}
          </div>
        </div>
      )}
    </FormDialog>
  );
}

const attendSchema = z
  .object({
    modalities: z.array(z.enum(MODALITIES)).min(1, 'Select the modalities given in this session.'),
    progressNote: z.string().trim().min(1, 'Write a progress note for this session.').max(4000),
    painBefore: z.number().nullable(),
    painAfter: z.number().nullable(),
    durationMinutes: z.number().nullable(),
  })
  .superRefine((v, ctx) => {
    for (const key of ['painBefore', 'painAfter'] as const) {
      const value = v[key];
      if (value !== null && (value < 0 || value > 10)) {
        ctx.addIssue({ code: 'custom', path: [key], message: 'Pain score is 0 to 10.' });
      }
    }
    if (v.durationMinutes !== null && (v.durationMinutes < 1 || v.durationMinutes > 240)) {
      ctx.addIssue({ code: 'custom', path: ['durationMinutes'], message: '1 to 240 minutes.' });
    }
  });

type AttendValues = z.infer<typeof attendSchema>;

/** Records a session as given: modalities (billed per modality), progress note and pain before and after. */
export function AttendDialog({
  session,
  plan,
  patientName,
  onClose,
}: {
  session:
    | Pick<PhysioSessionDto, 'id' | 'sessionNumber' | 'scheduledAt'>
    | Pick<PhysioWorklistItem, 'sessionId' | 'sessionNumber' | 'scheduledAt'>;
  plan: Pick<TreatmentPlanDto, 'modalities' | 'sessionMinutes'> | null;
  patientName: string;
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const sessionId = 'id' in session ? session.id : session.sessionId;
  const form = useForm<AttendValues>({
    resolver: zodResolver(attendSchema),
    defaultValues: {
      modalities: plan?.modalities ?? [],
      progressNote: '',
      painBefore: null,
      painAfter: null,
      durationMinutes: plan?.sessionMinutes ?? null,
    },
  });
  const attend = useMutation({
    mutationFn: (v: AttendValues) =>
      physioApi.attend(sessionId, {
        modalities: v.modalities,
        progressNote: v.progressNote,
        painBefore: v.painBefore,
        painAfter: v.painAfter,
        durationMinutes: v.durationMinutes,
      }),
    onSuccess: () => {
      notify.success('Session recorded', 'The modalities given are billed.');
      void queryClient.invalidateQueries({ queryKey: physioKeys.all });
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  return (
    <FormDialog
      title={`Record session ${session.sessionNumber}`}
      subtitle={`${patientName} · booked for ${formatDateTime(session.scheduledAt)}`}
      form={form}
      onSubmit={(v) => attend.mutate(v)}
      onClose={onClose}
      busy={attend.isPending}
      error={attend.error}
      submitLabel="Record session"
      maxWidth={760}
    >
      <CheckboxGroupField
        control={form.control}
        name="modalities"
        label="Given in this session"
        required
        layout="columns"
        options={MODALITIES.map((m) => ({ value: m, label: modalityLabel[m] }))}
      />
      <FormGrid columns={3}>
        <NumberField control={form.control} name="painBefore" label="Pain before (0–10)" min={0} max={10} />
        <NumberField control={form.control} name="painAfter" label="Pain after (0–10)" min={0} max={10} />
        <NumberField control={form.control} name="durationMinutes" label="Duration (minutes)" min={1} max={240} />
      </FormGrid>
      <TextAreaField
        control={form.control}
        name="progressNote"
        label="Progress note"
        required
        rows={4}
        maxLength={4000}
        placeholder="Treatment given, response, home exercises advised"
      />
    </FormDialog>
  );
}

const rescheduleSchema = z.object({ scheduledAt: z.string().nullable() }).superRefine((v, ctx) => {
  if (!fromIstInput(v.scheduledAt)) {
    ctx.addIssue({ code: 'custom', path: ['scheduledAt'], message: 'Choose the new date and time.' });
  }
});

type RescheduleValues = z.infer<typeof rescheduleSchema>;

export function RescheduleDialog({
  sessionId,
  scheduledAt,
  onClose,
}: {
  sessionId: string;
  scheduledAt: string;
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<RescheduleValues>({
    resolver: zodResolver(rescheduleSchema),
    defaultValues: { scheduledAt: toIstInput(scheduledAt) || nowIstInput() },
  });
  const move = useMutation({
    mutationFn: (v: RescheduleValues) => physioApi.reschedule(sessionId, fromIstInput(v.scheduledAt) ?? ''),
    onSuccess: () => {
      notify.success('Session moved');
      void queryClient.invalidateQueries({ queryKey: physioKeys.all });
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });
  return (
    <FormDialog
      title="Move session"
      form={form}
      onSubmit={(v) => move.mutate(v)}
      onClose={onClose}
      busy={move.isPending}
      error={move.error}
      submitLabel="Move session"
      maxWidth={480}
    >
      <DateTimeField control={form.control} name="scheduledAt" label="New date and time" required />
    </FormDialog>
  );
}
