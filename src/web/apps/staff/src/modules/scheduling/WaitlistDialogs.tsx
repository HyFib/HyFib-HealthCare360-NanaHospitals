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
  makeStyles,
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  Spinner,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, formatTime } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  DateField,
  FormGrid,
  SelectField,
  SwitchField,
  TextAreaField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { useFeatureOn } from '@/lib/features';
import { toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import {
  PREFERRED_SESSIONS,
  schedulingApi,
  schedulingKeys,
  STAFF_CHANNELS,
  type AppointmentDetail,
  type AppointmentVisitType,
  type SlotDto,
  type WaitlistEntryDto,
} from './api';
import { addDays, daysBetween, formatDayMedium, isIsoDate, istDate, maxDate, type IsoDate } from './dates';
import { channelLabel, preferredSessionLabel } from './labels';
import { SlotPicker } from './SlotPicker';
import { useToday } from './useNow';

const MAX_WINDOW_DAYS = 60;

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

// ------------------------------------------------------------------ add to waitlist

export interface WaitlistDefaults {
  patient?: PickedPatient | null;
  departmentId?: string | null;
  practitionerId?: string | null;
  isTelehealth?: boolean;
  from?: IsoDate;
}

function waitlistSchema(today: IsoDate) {
  return z
    .object({
      patient: z.custom<PickedPatient | null>(),
      departmentId: z.string().nullable(),
      practitionerId: z.string().nullable(),
      preferredFrom: z.string().nullable(),
      preferredTo: z.string().nullable(),
      preferredSession: z.enum(PREFERRED_SESSIONS),
      isTelehealth: z.boolean(),
      channel: z.enum(STAFF_CHANNELS),
      notes: z.string().trim().max(500, 'Use at most 500 characters.'),
    })
    .superRefine((v, ctx) => {
      if (!v.patient) {
        ctx.addIssue({ code: 'custom', path: ['patient'], message: 'Choose the patient.' });
      }
      if (!v.practitionerId) {
        ctx.addIssue({ code: 'custom', path: ['practitionerId'], message: 'Choose a doctor.' });
      }
      if (!v.preferredFrom || !isIsoDate(v.preferredFrom)) {
        ctx.addIssue({ code: 'custom', path: ['preferredFrom'], message: 'Choose the first date.' });
      } else if (v.preferredFrom < today) {
        ctx.addIssue({ code: 'custom', path: ['preferredFrom'], message: 'Choose today or a later date.' });
      }
      if (!v.preferredTo || !isIsoDate(v.preferredTo)) {
        ctx.addIssue({ code: 'custom', path: ['preferredTo'], message: 'Choose the last date.' });
      } else if (v.preferredFrom && v.preferredTo < v.preferredFrom) {
        ctx.addIssue({
          code: 'custom',
          path: ['preferredTo'],
          message: 'The last date must be on or after the first date.',
        });
      } else if (v.preferredFrom && daysBetween(v.preferredFrom, v.preferredTo) >= MAX_WINDOW_DAYS) {
        ctx.addIssue({
          code: 'custom',
          path: ['preferredTo'],
          message: `A waitlist request can cover at most ${MAX_WINDOW_DAYS} days.`,
        });
      }
    });
}

type WaitlistValues = z.infer<ReturnType<typeof waitlistSchema>>;

/** Puts a patient on a doctor's waitlist; a freed matching slot is offered by SMS/WhatsApp (never booked automatically). */
export function AddToWaitlistDialog({
  open,
  defaults,
  onClose,
}: {
  open: boolean;
  defaults?: WaitlistDefaults;
  onClose: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 640 }}>
        {open && <AddToWaitlistContent defaults={defaults} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

function AddToWaitlistContent({ defaults, onClose }: { defaults?: WaitlistDefaults; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const today = useToday();
  const videoOn = useFeatureOn('telehealth');
  const [schema] = useState(() => waitlistSchema(today));
  const from = defaults?.from && defaults.from > today ? defaults.from : today;
  const form = useForm<WaitlistValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      patient: defaults?.patient ?? null,
      departmentId: defaults?.departmentId ?? null,
      practitionerId: defaults?.practitionerId ?? null,
      preferredFrom: from,
      preferredTo: addDays(from, 13),
      preferredSession: 'Any',
      isTelehealth: videoOn && (defaults?.isTelehealth ?? false),
      channel: 'Phone',
      notes: '',
    },
  });
  const departmentId = useWatch({ control: form.control, name: 'departmentId' });
  const departments = useDepartments('Clinical');
  const doctors = usePractitioners(departmentId);

  const add = useMutation({
    mutationFn: (v: WaitlistValues) =>
      schedulingApi.addToWaitlist({
        patientId: v.patient!.id,
        practitionerId: v.practitionerId!,
        preferredFrom: v.preferredFrom!,
        preferredTo: v.preferredTo!,
        preferredSession: v.preferredSession,
        isTelehealth: videoOn && v.isTelehealth,
        channel: v.channel,
        notes: v.notes || null,
      }),
    onSuccess: (entry) => {
      notify.success(
        'Added to the waitlist',
        `${entry.patientName} will be offered the first matching slot that opens with ${entry.practitionerName}.`,
      );
      void queryClient.invalidateQueries({ queryKey: schedulingKeys.waitlist });
      onClose();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not add the patient to the waitlist');
      }
    },
  });

  return (
    <form onSubmit={form.handleSubmit((v) => add.mutate(v))} noValidate>
      <DialogBody>
        <DialogTitle>
          Add to waitlist
          <Caption1 block className={styles.muted}>
            When a matching slot is freed, the patient is told by SMS/WhatsApp and the desk books it.
          </Caption1>
        </DialogTitle>
        <DialogContent>
          <div className={styles.form}>
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
            <FormGrid columns={2}>
              <SelectField
                control={form.control}
                name="departmentId"
                label="Department"
                placeholder="All departments"
                options={toOptions(departments.data)}
              />
              <SelectField
                control={form.control}
                name="practitionerId"
                label="Doctor"
                required
                placeholder="Choose doctor"
                options={toOptions(doctors.data)}
              />
              <DateField control={form.control} name="preferredFrom" label="From" required min={today} />
              <DateField
                control={form.control}
                name="preferredTo"
                label="To"
                required
                min={today}
                max={addDays(today, MAX_WINDOW_DAYS + 30)}
              />
            </FormGrid>
            <ChoiceField
              control={form.control}
              name="preferredSession"
              label="Preferred time"
              options={PREFERRED_SESSIONS.map((s) => ({ value: s, label: preferredSessionLabel[s] }))}
            />
            <FormGrid columns={2}>
              <SelectField
                control={form.control}
                name="channel"
                label="Request came by"
                options={STAFF_CHANNELS.map((c) => ({ value: c, label: channelLabel[c] }))}
              />
            </FormGrid>
            {videoOn && (
              <SwitchField
                control={form.control}
                name="isTelehealth"
                label="Video consultation"
                hint="Only for doctors who offer video consultations"
              />
            )}
            <TextAreaField control={form.control} name="notes" label="Notes (optional)" rows={2} maxLength={500} />
          </div>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={add.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={add.isPending}
            icon={add.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Add to waitlist
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}

// ------------------------------------------------------------------ book from the waitlist

/** Books a slot for a waitlisted patient, starting from the slot that was offered to them (OA-05). */
export function BookFromWaitlistDialog({
  entry,
  onClose,
  onBooked,
}: {
  entry: WaitlistEntryDto | null;
  onClose: () => void;
  onBooked: (appointment: AppointmentDetail) => void;
}) {
  return (
    <Dialog open={entry !== null} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 880, width: '100%' }}>
        {entry && <BookFromWaitlistContent key={entry.id} entry={entry} onClose={onClose} onBooked={onBooked} />}
      </DialogSurface>
    </Dialog>
  );
}

function BookFromWaitlistContent({
  entry,
  onClose,
  onBooked,
}: {
  entry: WaitlistEntryDto;
  onClose: () => void;
  onBooked: (appointment: AppointmentDetail) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('scheduling.appointments.manage');
  const videoOn = useFeatureOn('telehealth');
  const videoOff = entry.isTelehealth && !videoOn;
  const today = useToday();
  const first = maxDate(today, entry.preferredFrom);
  const offeredDate = entry.offeredSlotStartsAt ? istDate(entry.offeredSlotStartsAt) : null;
  const [date, setDate] = useState<IsoDate>(offeredDate && offeredDate >= first ? offeredDate : first);
  const [slot, setSlot] = useState<SlotDto | null>(null);
  const [visitType, setVisitType] = useState<AppointmentVisitType>('New');
  const [reason, setReason] = useState('');

  const book = useMutation({
    mutationFn: (chosen: SlotDto) =>
      schedulingApi.bookFromWaitlist(entry.id, {
        startsAt: chosen.startsAt,
        visitType,
        reason: reason.trim() || null,
        overbook: chosen.available <= 0,
      }),
    onSuccess: (appointment) => {
      void queryClient.invalidateQueries({ queryKey: schedulingKeys.all });
      onBooked(appointment);
    },
    onError: (error) => {
      notify.error(error, 'Could not book the slot');
      setSlot(null);
      void queryClient.invalidateQueries({ queryKey: schedulingKeys.all });
    },
  });

  const expired = entry.preferredTo < today;

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (slot && !book.isPending) {
          book.mutate(slot);
        }
      }}
    >
      <DialogBody>
        <DialogTitle>
          Book for {entry.patientName}
          <Caption1 block className={styles.muted}>
            {entry.practitionerName} · {preferredSessionLabel[entry.preferredSession].toLowerCase()} ·{' '}
            {formatDayMedium(entry.preferredFrom)} to {formatDayMedium(entry.preferredTo)}
            {entry.isTelehealth ? ' · video consultation' : ''}
          </Caption1>
        </DialogTitle>
        <DialogContent>
          <div className={styles.form}>
            {entry.offeredSlotStartsAt && (
              <MessageBar intent="info">
                <MessageBarBody>
                  Slot offered to the patient: {formatDateTime(entry.offeredSlotStartsAt)}
                  {entry.offeredAt ? ` (message sent ${formatTime(entry.offeredAt)})` : ''}. It is selected below if it
                  is still free.
                </MessageBarBody>
              </MessageBar>
            )}
            {videoOff ? (
              <MessageBar intent="info" layout="multiline">
                <MessageBarBody>
                  Video consultations are turned off, so this request can't be booked as one. Book the patient an
                  in-person appointment instead, then cancel this request.
                </MessageBarBody>
              </MessageBar>
            ) : expired ? (
              <MessageBar intent="warning">
                <MessageBarBody>
                  The patient's preferred dates have passed. Book a new appointment for them instead.
                </MessageBarBody>
              </MessageBar>
            ) : (
              <SlotPicker
                practitionerId={entry.practitionerId}
                practitionerName={entry.practitionerName}
                mode={entry.isTelehealth ? 'Telehealth' : 'InPerson'}
                date={date}
                onDateChange={setDate}
                value={slot}
                onChange={setSlot}
                minDate={first}
                maxDate={entry.preferredTo}
                days={Math.min(31, Math.max(1, daysBetween(first, entry.preferredTo) + 1))}
                allowOverbook={canManage}
                preferredSession={entry.preferredSession}
                preselectStartsAt={entry.offeredSlotStartsAt ?? undefined}
                autoAdvance={!entry.offeredSlotStartsAt}
              />
            )}
            <Field label="Visit">
              <RadioGroup
                layout="horizontal"
                value={visitType}
                onChange={(_, d) => setVisitType(d.value as AppointmentVisitType)}
              >
                <Radio value="New" label="New visit" />
                <Radio value="FollowUp" label="Follow-up" />
              </RadioGroup>
            </Field>
            <Field label="Reason for visit (optional)">
              <Textarea
                value={reason}
                onChange={(_, d) => setReason(d.value.slice(0, 300))}
                rows={2}
                resize="vertical"
              />
            </Field>
          </div>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={book.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={!slot || book.isPending || expired || videoOff}
            icon={book.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            {slot ? `Book ${formatDayMedium(slot.date)}, ${formatTime(slot.startsAt)}` : 'Choose a slot'}
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
