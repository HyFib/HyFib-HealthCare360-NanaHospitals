import {
  Button,
  Caption1,
  Checkbox,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  OverlayDrawer,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { DeleteRegular, DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError } from '@hc360/core';
import {
  applyServerErrors,
  CheckboxGroup,
  ChoiceField,
  CloseRequestScope,
  ConfirmDialog,
  DateField,
  FormGrid,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import {
  schedulingApi,
  schedulingQueries,
  WEEKDAYS,
  type ConsultationMode,
  type ScheduleDto,
  type ScheduleSaved,
  type Weekday,
} from './api';
import { formatClock, toInputTime, toServerTime, type IsoDate } from './dates';
import { modeLabel, weekdayShort } from './labels';
import { findOverlaps, sessionProblem, slotsPerSession, SLOT_LENGTHS, WEEKDAY_PRESETS } from './scheduleRules';

const MODES: ConsultationMode[] = ['InPerson', 'Both', 'Telehealth'];

const schema = z
  .object({
    practitionerId: z.string().nullable(),
    departmentId: z.string().nullable(),
    weekdays: z.array(z.enum(WEEKDAYS)),
    startTime: z.string(),
    endTime: z.string(),
    slotMinutes: z.string().nullable(),
    capacityPerSlot: z.number().nullable(),
    mode: z.enum(['InPerson', 'Telehealth', 'Both']),
    room: z.string().trim().max(32, 'Use at most 32 characters.'),
    effectiveFrom: z.string().nullable(),
    effectiveTo: z.string().nullable(),
    isActive: z.boolean(),
    notes: z.string().trim().max(500, 'Use at most 500 characters.'),
  })
  .superRefine((v, ctx) => {
    if (!v.practitionerId) {
      ctx.addIssue({ code: 'custom', path: ['practitionerId'], message: 'Choose a doctor.' });
    }
    if (!v.departmentId) {
      ctx.addIssue({ code: 'custom', path: ['departmentId'], message: 'Choose a department.' });
    }
    if (v.weekdays.length === 0) {
      ctx.addIssue({ code: 'custom', path: ['weekdays'], message: 'Choose at least one weekday.' });
    }
    if (!v.effectiveFrom) {
      ctx.addIssue({ code: 'custom', path: ['effectiveFrom'], message: 'Choose the first date.' });
    }
    const problem = sessionProblem({
      startTime: v.startTime,
      endTime: v.endTime,
      slotMinutes: Number(v.slotMinutes),
      capacityPerSlot: v.capacityPerSlot ?? 0,
      effectiveFrom: v.effectiveFrom,
      effectiveTo: v.effectiveTo,
    });
    if (problem) {
      ctx.addIssue({ code: 'custom', path: [problem.field], message: problem.message });
    }
  });

type ScheduleValues = z.infer<typeof schema>;

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  days: { display: 'flex', gap: '4px 12px', flexWrap: 'wrap' },
  presets: { display: 'flex', gap: '6px', flexWrap: 'wrap', marginTop: '4px' },
  muted: { color: tokens.colorNeutralForeground3 },
  footer: { display: 'flex', gap: '8px', width: '100%' },
  spacer: { flex: 1 },
});

export interface ScheduleDrawerProps {
  open: boolean;
  /** The session being edited; null adds new sessions. */
  schedule: ScheduleDto | null;
  defaults?: { practitionerId?: string; departmentId?: string };
  today: IsoDate;
  onClose: () => void;
  /** Called after a change; `result` lists appointments the change strands (edit and delete only). */
  onSaved: (result: ScheduleSaved | null) => void;
}

/** Adds a doctor's weekly OPD session on several weekdays at once, or edits/removes one session (PMS-01). */
export function ScheduleDrawer(props: ScheduleDrawerProps) {
  const closing = useCloseRequests({ onClose: props.onClose });
  return (
    <OverlayDrawer open={props.open} position="end" size="medium" onOpenChange={closing.onOpenChange}>
      <CloseRequestScope value={closing}>
        {props.open && <ScheduleForm key={props.schedule?.id ?? 'new'} {...props} />}
      </CloseRequestScope>
    </OverlayDrawer>
  );
}

function ScheduleForm({ schedule, defaults, today, onClose, onSaved }: ScheduleDrawerProps) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const form = useForm<ScheduleValues>({
    resolver: zodResolver(schema),
    defaultValues: schedule
      ? {
          practitionerId: schedule.practitionerId,
          departmentId: schedule.departmentId,
          weekdays: [schedule.weekday],
          startTime: toInputTime(schedule.startTime),
          endTime: toInputTime(schedule.endTime),
          slotMinutes: String(schedule.slotMinutes),
          capacityPerSlot: schedule.capacityPerSlot,
          mode: schedule.mode,
          room: schedule.room ?? '',
          effectiveFrom: schedule.effectiveFrom,
          effectiveTo: schedule.effectiveTo,
          isActive: schedule.isActive,
          notes: schedule.notes ?? '',
        }
      : {
          practitionerId: defaults?.practitionerId ?? null,
          departmentId: defaults?.departmentId ?? null,
          weekdays: [],
          startTime: '09:00',
          endTime: '13:00',
          slotMinutes: '15',
          capacityPerSlot: 1,
          mode: 'InPerson',
          room: '',
          effectiveFrom: today,
          effectiveTo: null,
          isActive: true,
          notes: '',
        },
  });
  const [
    practitionerId,
    departmentId,
    startTime,
    endTime,
    slotMinutes,
    capacity,
    weekdays,
    effectiveFrom,
    effectiveTo,
    isActive,
  ] = useWatch({
    control: form.control,
    name: [
      'practitionerId',
      'departmentId',
      'startTime',
      'endTime',
      'slotMinutes',
      'capacityPerSlot',
      'weekdays',
      'effectiveFrom',
      'effectiveTo',
      'isActive',
    ],
  });
  const departments = useDepartments('Clinical');
  const doctors = usePractitioners(departmentId);
  const doctorSessions = useQuery({
    ...schedulingQueries.schedules({ practitionerId: practitionerId ?? undefined }),
    enabled: Boolean(practitionerId),
  });

  const perSession = slotsPerSession(startTime, endTime, Number(slotMinutes));
  const clashes =
    practitionerId && effectiveFrom
      ? findOverlaps(
          { id: schedule?.id, practitionerId, weekdays, startTime, endTime, effectiveFrom, effectiveTo, isActive },
          doctorSessions.data ?? [],
        )
      : [];

  const handleError = (error: unknown) => {
    if (isApiError(error) && error.isConflict && error.code !== 'schedule.overlap') {
      notify.error(error, 'This session was changed by someone else');
      void queryClient.invalidateQueries({ queryKey: ['scheduling', 'schedules'] });
      onClose();
      return;
    }
    if (!applyServerErrors(error, form.setError)) {
      setProblem(isApiError(error) ? error.userMessage : 'The session could not be saved.');
    }
  };

  const save = useMutation({
    mutationFn: async (v: ScheduleValues): Promise<ScheduleSaved | null> => {
      const common = {
        departmentId: v.departmentId!,
        startTime: toServerTime(v.startTime),
        endTime: toServerTime(v.endTime),
        slotMinutes: Number(v.slotMinutes),
        capacityPerSlot: v.capacityPerSlot ?? 1,
        mode: v.mode,
        room: v.room || null,
        effectiveFrom: v.effectiveFrom!,
        effectiveTo: v.effectiveTo || null,
        notes: v.notes || null,
      };
      if (schedule) {
        return schedulingApi.updateSchedule(schedule.id, {
          ...common,
          weekday: v.weekdays[0]!,
          isActive: v.isActive,
          version: schedule.version,
        });
      }
      await schedulingApi.createSchedules({ ...common, practitionerId: v.practitionerId!, weekdays: v.weekdays });
      return null;
    },
    onSuccess: (result, v) => {
      notify.success(
        schedule ? 'Session saved' : v.weekdays.length > 1 ? `${v.weekdays.length} sessions added` : 'Session added',
      );
      void queryClient.invalidateQueries({ queryKey: ['scheduling'] });
      onSaved(result);
      onClose();
    },
    onError: handleError,
  });

  const remove = useMutation({
    mutationFn: () => schedulingApi.deleteSchedule(schedule!.id),
    onSuccess: (result) => {
      notify.success('Session removed');
      void queryClient.invalidateQueries({ queryKey: ['scheduling'] });
      setConfirmDelete(false);
      onSaved(result);
      onClose();
    },
    onError: (error) => {
      setConfirmDelete(false);
      handleError(error);
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending || remove.isPending, onClose });

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={
            <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
          }
        >
          {schedule ? `${schedule.practitionerName} · ${schedule.weekday}` : 'Add OPD session'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.form}>
          {problem && (
            <MessageBar intent="error" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Not saved</MessageBarTitle>
                {problem}
              </MessageBarBody>
            </MessageBar>
          )}
          <SelectField
            control={form.control}
            name="departmentId"
            label="Department"
            required
            placeholder="Choose department"
            options={toOptions(departments.data)}
          />
          <SelectField
            control={form.control}
            name="practitionerId"
            label="Doctor"
            required
            disabled={Boolean(schedule)}
            placeholder={departmentId ? 'Choose doctor' : 'Choose a department first'}
            options={
              schedule && !doctors.data?.some((d) => d.id === schedule.practitionerId)
                ? [{ value: schedule.practitionerId, label: schedule.practitionerName }, ...toOptions(doctors.data)]
                : toOptions(doctors.data)
            }
          />
          <Controller
            control={form.control}
            name="weekdays"
            render={({ field, fieldState }) => (
              <CheckboxGroup
                label={schedule ? 'Day' : 'Days'}
                required
                validationMessage={fieldState.error?.message}
                validationState={fieldState.error ? 'error' : 'none'}
              >
                <div className={styles.days}>
                  {WEEKDAYS.map((day) => (
                    <Checkbox
                      key={day}
                      label={weekdayShort[day]}
                      checked={field.value.includes(day)}
                      onChange={(_, d) => {
                        const next: Weekday[] = schedule
                          ? d.checked
                            ? [day]
                            : field.value
                          : d.checked
                            ? [...field.value, day]
                            : field.value.filter((x) => x !== day);
                        field.onChange(WEEKDAYS.filter((w) => next.includes(w)));
                      }}
                    />
                  ))}
                </div>
                {!schedule && (
                  <div className={styles.presets}>
                    {WEEKDAY_PRESETS.map((preset) => (
                      <Button
                        key={preset.label}
                        size="small"
                        appearance="outline"
                        onClick={() => field.onChange(preset.days)}
                      >
                        {preset.label}
                      </Button>
                    ))}
                  </div>
                )}
              </CheckboxGroup>
            )}
          />
          <FormGrid columns={2} minColumnWidth={150}>
            <TextField control={form.control} name="startTime" label="Starts" type="time" required />
            <TextField control={form.control} name="endTime" label="Ends" type="time" required />
            <SelectField
              control={form.control}
              name="slotMinutes"
              label="Slot length"
              required
              options={SLOT_LENGTHS.map((m) => ({ value: String(m), label: `${m} minutes` }))}
            />
            <NumberField
              control={form.control}
              name="capacityPerSlot"
              label="Patients per slot"
              required
              min={1}
              max={20}
            />
          </FormGrid>
          <Caption1 className={styles.muted}>
            {perSession > 0
              ? `${perSession} slots from ${formatClock(startTime)} to ${formatClock(endTime)} · up to ${perSession * (capacity ?? 1)} patients per session`
              : 'Choose the start, end and slot length.'}
          </Caption1>
          <ChoiceField
            control={form.control}
            name="mode"
            label="Consultation"
            options={MODES.map((m) => ({ value: m, label: modeLabel[m] }))}
          />
          <FormGrid columns={2} minColumnWidth={150}>
            <TextField control={form.control} name="room" label="Room" maxLength={32} placeholder="e.g. OPD 3" />
            <div />
            <DateField control={form.control} name="effectiveFrom" label="From" required />
            <DateField
              control={form.control}
              name="effectiveTo"
              label="Until (optional)"
              min={effectiveFrom ?? undefined}
            />
          </FormGrid>
          {schedule && (
            <SwitchField
              control={form.control}
              name="isActive"
              label="Session is active"
              hint="Inactive sessions produce no slots; existing bookings stay."
            />
          )}
          <TextAreaField control={form.control} name="notes" label="Notes (optional)" rows={2} maxLength={500} />
          {clashes.length > 0 && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Overlaps another session</MessageBarTitle>
                {clashes
                  .map(
                    (c) =>
                      `${c.weekday} ${formatClock(c.startTime)}–${formatClock(c.endTime)}${c.departmentName ? ` (${c.departmentName})` : ''}`,
                  )
                  .join(', ')}
                . A doctor cannot run two sessions at the same time; change the times or days.
              </MessageBarBody>
            </MessageBar>
          )}
          {schedule && (
            <MessageBar intent="info">
              <MessageBarBody>
                Appointments that no longer fit after this change are listed after saving so they can be rescheduled.
              </MessageBarBody>
            </MessageBar>
          )}
        </div>
      </DrawerBody>
      <DrawerFooter>
        <div className={styles.footer}>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            {schedule ? 'Save' : weekdays.length > 1 ? `Add ${weekdays.length} sessions` : 'Add session'}
          </Button>
          <Button onClick={guard.requestClose}>Cancel</Button>
          <span className={styles.spacer} />
          {schedule && (
            <Button appearance="subtle" icon={<DeleteRegular />} onClick={() => setConfirmDelete(true)}>
              Remove
            </Button>
          )}
        </div>
      </DrawerFooter>
      <ConfirmDialog
        open={confirmDelete}
        title="Remove this session?"
        confirmLabel="Remove session"
        destructive
        busy={remove.isPending}
        onConfirm={() => remove.mutate()}
        onCancel={() => setConfirmDelete(false)}
      >
        No new appointments can be booked in it. Future appointments already booked in this session are listed next so
        you can reschedule them.
      </ConfirmDialog>
      {guard.dialogs}
    </form>
  );
}
