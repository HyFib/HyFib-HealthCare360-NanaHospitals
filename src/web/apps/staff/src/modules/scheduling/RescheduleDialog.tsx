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
  Select,
  Spinner,
  Switch,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, PrintRegular } from '@fluentui/react-icons';
import { formatDateTime, formatTime, isApiError, todayIso, useSession } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { toOptions, usePractitioners } from '@/lib/lookups';
import {
  schedulingApi,
  schedulingKeys,
  schedulingQueries,
  type AppointmentDetail,
  type AppointmentRef,
  type SlotDto,
} from './api';
import { addDays, formatDayMedium, istDate, maxDate } from './dates';
import { printAppointmentSlip } from './print';
import { SlotPicker } from './SlotPicker';
import type { VisitMode } from './slots';

const useStyles = makeStyles({
  layout: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap', alignItems: 'end' },
  muted: { color: tokens.colorNeutralForeground3 },
  done: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '6px',
    padding: '12px 0',
    textAlign: 'center',
  },
  time: {
    fontSize: tokens.fontSizeHero700,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightHero700,
  },
});

/**
 * Moves an open appointment to another slot (same or another doctor of the department). The server books the new slot
 * and marks the old booking "Rescheduled" in one transaction, so the history is kept and the freed slot is offered to
 * the waitlist.
 */
export function RescheduleDialog({
  appointment,
  onClose,
}: {
  appointment: AppointmentRef | null;
  onClose: () => void;
}) {
  return (
    <Dialog open={appointment !== null} onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 880, width: '100%' }}>
        {appointment && <RescheduleContent key={appointment.id} appointment={appointment} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

function RescheduleContent({ appointment, onClose }: { appointment: AppointmentRef; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const today = todayIso();
  const [practitionerId, setPractitionerId] = useState(appointment.practitionerId);
  const [mode, setMode] = useState<VisitMode>(appointment.isTelehealth ? 'Telehealth' : 'InPerson');
  const [date, setDate] = useState(() => maxDate(today, istDate(appointment.startsAt)));
  const [slot, setSlot] = useState<SlotDto | null>(null);
  const [reason, setReason] = useState('');
  const [notifyPatient, setNotifyPatient] = useState(true);
  const [done, setDone] = useState<AppointmentDetail | null>(null);
  const doctors = usePractitioners(appointment.departmentId);
  // Same key as the slot picker's first window, so this costs no extra request.
  const availability = useQuery(schedulingQueries.slots({ practitionerId, from: today, to: addDays(today, 13) }));
  const doctor = availability.data?.find((d) => d.practitionerId === practitionerId);
  const offersVideo = Boolean(doctor?.offersTelehealth) || appointment.isTelehealth;
  const doctorName =
    doctor?.practitionerName ??
    (practitionerId === appointment.practitionerId ? appointment.practitionerName : undefined);

  const reschedule = useMutation({
    mutationFn: (chosen: SlotDto) =>
      schedulingApi.reschedule(appointment.id, {
        startsAt: chosen.startsAt,
        practitionerId: practitionerId !== appointment.practitionerId ? practitionerId : null,
        isTelehealth: (mode === 'Telehealth') !== appointment.isTelehealth ? mode === 'Telehealth' : null,
        reason: reason.trim() || null,
        overbook: chosen.available <= 0,
        notifyPatient,
      }),
    onSuccess: (moved) => {
      setDone(moved);
      void queryClient.invalidateQueries({ queryKey: schedulingKeys.all });
    },
    onError: (error) => {
      notify.error(error, 'Could not reschedule');
      if (isApiError(error) && error.isConflict) {
        setSlot(null);
        void queryClient.invalidateQueries({ queryKey: schedulingKeys.slots });
      }
    },
  });

  if (done) {
    return (
      <DialogBody>
        <DialogTitle>Appointment moved</DialogTitle>
        <DialogContent>
          <div className={styles.done} role="status">
            <CheckmarkCircleRegular fontSize={40} color={tokens.colorPaletteGreenForeground1} />
            <Text>{formatDayMedium(istDate(done.startsAt))}</Text>
            <span className={styles.time}>{formatTime(done.startsAt)}</span>
            <Text>
              {done.practitionerName}
              {done.isTelehealth ? ' · Video consultation' : done.room ? ` · Room ${done.room}` : ''}
            </Text>
            <Caption1 className={styles.muted}>New appointment number {done.number}</Caption1>
          </div>
        </DialogContent>
        <DialogActions>
          <Button
            icon={<PrintRegular />}
            onClick={() =>
              void printAppointmentSlip(facilityName, done).catch((error: unknown) =>
                notify.error(error, 'Printing failed'),
              )
            }
          >
            Print new slip
          </Button>
          <Button appearance="primary" onClick={onClose} autoFocus>
            Done
          </Button>
        </DialogActions>
      </DialogBody>
    );
  }

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (slot && !reschedule.isPending) {
          reschedule.mutate(slot);
        }
      }}
    >
      <DialogBody>
        <DialogTitle>
          Reschedule appointment
          <Caption1 block className={styles.muted}>
            {appointment.patientName} · now {formatDateTime(appointment.startsAt)} with {appointment.practitionerName}
          </Caption1>
        </DialogTitle>
        <DialogContent>
          <div className={styles.layout}>
            <div className={styles.row}>
              <Field label="Doctor" style={{ minWidth: 240 }}>
                <Select
                  value={practitionerId}
                  onChange={(_, d) => {
                    setPractitionerId(d.value);
                    setSlot(null);
                  }}
                >
                  {!doctors.data?.some((d) => d.id === appointment.practitionerId) && (
                    <option value={appointment.practitionerId}>{appointment.practitionerName}</option>
                  )}
                  {toOptions(doctors.data).map((o) => (
                    <option key={o.value} value={o.value}>
                      {o.label}
                    </option>
                  ))}
                </Select>
              </Field>
              {offersVideo && (
                <Field label="Visit">
                  <RadioGroup
                    layout="horizontal"
                    value={mode}
                    onChange={(_, d) => {
                      setMode(d.value as VisitMode);
                      setSlot(null);
                    }}
                  >
                    <Radio value="InPerson" label="In person" />
                    <Radio value="Telehealth" label="Video consultation" />
                  </RadioGroup>
                </Field>
              )}
            </div>
            <SlotPicker
              practitionerId={practitionerId}
              practitionerName={doctorName}
              mode={mode}
              date={date}
              onDateChange={setDate}
              value={slot}
              onChange={setSlot}
              allowOverbook
              currentStartsAt={practitionerId === appointment.practitionerId ? appointment.startsAt : null}
              autoAdvance={false}
            />
            {slot && slot.available <= 0 && (
              <MessageBar intent="warning">
                <MessageBarBody>
                  This slot is full. Moving the patient here books one extra patient (overbooking).
                </MessageBarBody>
              </MessageBar>
            )}
            <Field label="Reason for the change (optional)">
              <Textarea
                value={reason}
                onChange={(_, d) => setReason(d.value.slice(0, 300))}
                rows={2}
                resize="vertical"
              />
            </Field>
            <Switch
              checked={notifyPatient}
              onChange={(_, d) => setNotifyPatient(d.checked)}
              label="Send the new time to the patient by SMS/WhatsApp"
            />
          </div>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={reschedule.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={!slot || reschedule.isPending}
            icon={reschedule.isPending ? <Spinner size="tiny" /> : undefined}
          >
            {slot ? `Move to ${formatDayMedium(slot.date)}, ${formatTime(slot.startsAt)}` : 'Choose a new slot'}
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
