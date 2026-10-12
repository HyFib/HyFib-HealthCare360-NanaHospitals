import {
  Button,
  Caption1,
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
  ChoiceField,
  ConfirmDialog,
  DateField,
  FormGrid,
  NumberField,
  SelectField,
  SwitchField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import {
  EXCEPTION_TYPES,
  schedulingApi,
  type ConsultationMode,
  type ScheduleExceptionDto,
  type ScheduleSaved,
} from './api';
import { daysBetween, timeToMinutes, toInputTime, toServerTime, type IsoDate } from './dates';
import { exceptionTypeText, modeLabel } from './labels';
import { sessionProblem, slotsPerSession, SLOT_LENGTHS } from './scheduleRules';

const MAX_RANGE_DAYS = 366;
const MODES: ConsultationMode[] = ['InPerson', 'Both', 'Telehealth'];

const schema = z
  .object({
    practitionerId: z.string().nullable(),
    type: z.enum(EXCEPTION_TYPES),
    fromDate: z.string().nullable(),
    toDate: z.string().nullable(),
    allDay: z.boolean(),
    startTime: z.string(),
    endTime: z.string(),
    reason: z.string().trim().max(300, 'Use at most 300 characters.'),
    allFacilities: z.boolean(),
    departmentId: z.string().nullable(),
    slotMinutes: z.string().nullable(),
    capacityPerSlot: z.number().nullable(),
    mode: z.enum(['InPerson', 'Telehealth', 'Both']),
    room: z.string().trim().max(32, 'Use at most 32 characters.'),
  })
  .superRefine((v, ctx) => {
    const issue = (path: string, message: string) => ctx.addIssue({ code: 'custom', path: [path], message });
    if (!v.practitionerId) {
      issue('practitionerId', 'Choose a doctor.');
    }
    if (!v.reason) {
      issue('reason', 'Enter a reason.');
    }
    if (!v.fromDate) {
      issue('fromDate', 'Choose the first date.');
    }
    if (!v.toDate) {
      issue('toDate', 'Choose the last date.');
    } else if (v.fromDate && v.toDate < v.fromDate) {
      issue('toDate', 'The end date must be on or after the start date.');
    } else if (v.fromDate && daysBetween(v.fromDate, v.toDate) >= MAX_RANGE_DAYS) {
      issue('toDate', 'An exception can cover at most one year.');
    }
    const extra = v.type === 'ExtraSession';
    if (!v.allDay || extra) {
      const start = timeToMinutes(v.startTime);
      const end = timeToMinutes(v.endTime);
      if (Number.isNaN(start)) {
        issue('startTime', 'Enter the start time.');
      } else if (Number.isNaN(end)) {
        issue('endTime', 'Enter the end time.');
      } else if (end <= start) {
        issue('endTime', 'The end time must be after the start time.');
      }
    }
    if (extra) {
      if (!v.departmentId) {
        issue('departmentId', 'Choose the department of the extra session.');
      }
      const problem = sessionProblem({
        startTime: v.startTime,
        endTime: v.endTime,
        slotMinutes: Number(v.slotMinutes),
        capacityPerSlot: v.capacityPerSlot ?? 0,
      });
      if (problem && problem.field !== 'startTime' && problem.field !== 'endTime') {
        issue(problem.field, problem.message);
      }
    }
  });

type ExceptionValues = z.infer<typeof schema>;

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  footer: { display: 'flex', gap: '8px', width: '100%' },
  spacer: { flex: 1 },
});

export interface ExceptionDrawerProps {
  open: boolean;
  exception: ScheduleExceptionDto | null;
  defaults?: { practitionerId?: string; departmentId?: string; type?: ScheduleExceptionDto['type'] };
  today: IsoDate;
  onClose: () => void;
  onSaved: (result: ScheduleSaved | null) => void;
}

/** Leave, blocked time or an extra OPD session on specific dates (PMS-01). */
export function ExceptionDrawer(props: ExceptionDrawerProps) {
  return (
    <OverlayDrawer open={props.open} position="end" size="medium" onOpenChange={(_, d) => !d.open && props.onClose()}>
      {props.open && <ExceptionForm key={props.exception?.id ?? 'new'} {...props} />}
    </OverlayDrawer>
  );
}

function ExceptionForm({ exception, defaults, today, onClose, onSaved }: ExceptionDrawerProps) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [problem, setProblem] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const initialType = exception?.type ?? defaults?.type ?? 'Leave';
  const form = useForm<ExceptionValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      practitionerId: exception?.practitionerId ?? defaults?.practitionerId ?? null,
      type: initialType,
      fromDate: exception?.fromDate ?? today,
      toDate: exception?.toDate ?? today,
      allDay: exception ? exception.isAllDay : initialType === 'Leave',
      startTime: toInputTime(exception?.startTime) || '09:00',
      endTime: toInputTime(exception?.endTime) || '13:00',
      reason: exception?.reason ?? '',
      allFacilities: exception ? exception.facilityId === null : initialType === 'Leave',
      departmentId: exception?.departmentId ?? defaults?.departmentId ?? null,
      slotMinutes: String(exception?.slotMinutes ?? 15),
      capacityPerSlot: exception?.capacityPerSlot ?? 1,
      mode: exception?.mode ?? 'InPerson',
      room: exception?.room ?? '',
    },
  });
  const [type, allDay, startTime, endTime, slotMinutes] = useWatch({
    control: form.control,
    name: ['type', 'allDay', 'startTime', 'endTime', 'slotMinutes'],
  });
  const extra = type === 'ExtraSession';
  const departments = useDepartments('Clinical');
  const doctors = usePractitioners(null);

  const save = useMutation({
    mutationFn: (v: ExceptionValues) =>
      schedulingApi.saveException(exception?.id ?? null, {
        practitionerId: v.practitionerId!,
        type: v.type,
        fromDate: v.fromDate!,
        toDate: v.toDate!,
        startTime: v.allDay && !extra ? null : toServerTime(v.startTime),
        endTime: v.allDay && !extra ? null : toServerTime(v.endTime),
        reason: v.reason,
        allFacilities: extra ? false : v.allFacilities,
        departmentId: extra ? v.departmentId : null,
        slotMinutes: extra ? Number(v.slotMinutes) : null,
        capacityPerSlot: extra ? v.capacityPerSlot : null,
        mode: extra ? v.mode : null,
        room: extra ? v.room || null : null,
        version: exception?.version ?? null,
      }),
    onSuccess: (result, v) => {
      notify.success(exception ? 'Saved' : `${exceptionTypeText[v.type].label} added`);
      void queryClient.invalidateQueries({ queryKey: ['scheduling'] });
      onSaved(result);
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.isConflict) {
        notify.error(error, 'This entry was changed by someone else');
        void queryClient.invalidateQueries({ queryKey: ['scheduling', 'exceptions'] });
        onClose();
        return;
      }
      if (!applyServerErrors(error, form.setError)) {
        setProblem(isApiError(error) ? error.userMessage : 'Could not save.');
      }
    },
  });

  const remove = useMutation({
    mutationFn: () => schedulingApi.deleteException(exception!.id),
    onSuccess: () => {
      notify.success('Removed');
      void queryClient.invalidateQueries({ queryKey: ['scheduling'] });
      setConfirmDelete(false);
      onSaved(null);
      onClose();
    },
    onError: (error) => {
      setConfirmDelete(false);
      notify.error(error, 'Could not remove');
    },
  });

  const perSession = slotsPerSession(startTime, endTime, Number(slotMinutes));

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
        >
          {exception
            ? `${exceptionTypeText[exception.type].label} · ${exception.practitionerName}`
            : 'Leave, blocked time or extra session'}
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
          <ChoiceField
            control={form.control}
            name="type"
            label="What is it?"
            disabled={Boolean(exception)}
            options={EXCEPTION_TYPES.map((t) => ({ value: t, label: exceptionTypeText[t].label }))}
          />
          <Caption1 className={styles.muted}>{exceptionTypeText[type].description}</Caption1>
          <SelectField
            control={form.control}
            name="practitionerId"
            label="Doctor"
            required
            disabled={Boolean(exception)}
            placeholder="Choose doctor"
            options={
              exception && !doctors.data?.some((d) => d.id === exception.practitionerId)
                ? [{ value: exception.practitionerId, label: exception.practitionerName }, ...toOptions(doctors.data)]
                : toOptions(doctors.data)
            }
          />
          <FormGrid columns={2} minColumnWidth={150}>
            <DateField control={form.control} name="fromDate" label="From" required />
            <DateField control={form.control} name="toDate" label="To" required />
          </FormGrid>
          {!extra && (
            <SwitchField
              control={form.control}
              name="allDay"
              label="Whole day"
              hint={type === 'Leave' ? 'Turn off for half-day leave' : undefined}
            />
          )}
          {(!allDay || extra) && (
            <FormGrid columns={2} minColumnWidth={150}>
              <TextField control={form.control} name="startTime" label="From time" type="time" required />
              <TextField control={form.control} name="endTime" label="To time" type="time" required />
            </FormGrid>
          )}
          <TextField
            control={form.control}
            name="reason"
            label="Reason"
            required
            maxLength={300}
            placeholder={
              type === 'Leave'
                ? 'e.g. Conference in Mumbai'
                : type === 'Block'
                  ? 'e.g. Surgery list'
                  : 'e.g. Health camp'
            }
          />
          {extra ? (
            <>
              <SelectField
                control={form.control}
                name="departmentId"
                label="Department"
                required
                placeholder="Choose department"
                options={toOptions(departments.data)}
              />
              <FormGrid columns={2} minColumnWidth={150}>
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
                <TextField control={form.control} name="room" label="Room" maxLength={32} />
              </FormGrid>
              <ChoiceField
                control={form.control}
                name="mode"
                label="Consultation"
                options={MODES.map((m) => ({ value: m, label: modeLabel[m] }))}
              />
              <Caption1 className={styles.muted}>
                {perSession > 0
                  ? `${perSession} slots on each date, at this branch.`
                  : 'Choose the times and slot length.'}
              </Caption1>
            </>
          ) : (
            <SwitchField
              control={form.control}
              name="allFacilities"
              label="Applies at every branch"
              hint={
                type === 'Leave'
                  ? 'Leave usually applies everywhere: the doctor is away from all branches.'
                  : 'Turn off to block time at this branch only.'
              }
            />
          )}
          {!extra && (
            <MessageBar intent="info">
              <MessageBarBody>
                Appointments already booked in this time are listed after saving so they can be moved.
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
            {exception ? 'Save' : 'Add'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
          <span className={styles.spacer} />
          {exception && (
            <Button appearance="subtle" icon={<DeleteRegular />} onClick={() => setConfirmDelete(true)}>
              Remove
            </Button>
          )}
        </div>
      </DrawerFooter>
      <ConfirmDialog
        open={confirmDelete}
        title={`Remove this ${exception ? exceptionTypeText[exception.type].label.toLowerCase() : 'entry'}?`}
        confirmLabel="Remove"
        destructive
        busy={remove.isPending}
        onConfirm={() => remove.mutate()}
        onCancel={() => setConfirmDelete(false)}
      >
        {exception?.type === 'ExtraSession'
          ? 'Its slots disappear. Check the appointment desk for bookings made in this session.'
          : 'The doctor becomes bookable again in this time.'}
      </ConfirmDialog>
    </form>
  );
}
