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
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, PersonArrowRightRegular, PrintRegular } from '@fluentui/react-icons';
import { formatInr, formatTime } from '@hc360/core';
import type { AppointmentDetail } from './api';
import type { AppointmentDesk } from './useAppointmentDesk';
import { appointmentActions } from './appointmentRules';
import { formatDayLong, istDate } from './dates';
import { paymentLabel, visitTypeLabel } from './labels';

const useStyles = makeStyles({
  done: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '6px',
    padding: '8px 0',
    textAlign: 'center',
  },
  time: {
    fontSize: '40px',
    lineHeight: '48px',
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorBrandForeground1,
  },
  number: { fontFamily: tokens.fontFamilyMonospace, letterSpacing: '1px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * Confirmation after a booking: the time in large type (to read out on the phone), the slip to print, and — for
 * today's patients standing at the desk — check-in straight away.
 */
export function BookingSuccessDialog({
  appointment,
  desk,
  onClose,
  onNewBooking,
}: {
  appointment: AppointmentDetail | null;
  desk: AppointmentDesk;
  onClose: () => void;
  onNewBooking?: () => void;
}) {
  const styles = useStyles();
  const canCheckIn = appointment ? appointmentActions(appointment, desk.context).checkIn.allowed : false;

  return (
    <Dialog open={appointment !== null} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        {appointment && (
          <DialogBody>
            <DialogTitle>Appointment booked</DialogTitle>
            <DialogContent>
              <div className={styles.done} role="status">
                <CheckmarkCircleRegular fontSize={40} color={tokens.colorPaletteGreenForeground1} />
                <Text weight="semibold">{appointment.patientName}</Text>
                <Text>{formatDayLong(istDate(appointment.startsAt))}</Text>
                <span className={styles.time}>{formatTime(appointment.startsAt)}</span>
                <Text>
                  {appointment.practitionerName}
                  {appointment.isTelehealth
                    ? ' · Video consultation'
                    : appointment.room
                      ? ` · Room ${appointment.room}`
                      : ''}
                </Text>
                <Caption1 className={styles.muted}>
                  {visitTypeLabel[appointment.visitType]} · Fee {formatInr(appointment.feeAmount)} (
                  {paymentLabel[appointment.paymentStatus].toLowerCase()})
                </Caption1>
                <Caption1 className={styles.muted}>
                  Appointment number <span className={styles.number}>{appointment.number}</span>
                </Caption1>
                {appointment.isTelehealth && (
                  <Caption1>The video link is sent to the patient by SMS/WhatsApp.</Caption1>
                )}
              </div>
            </DialogContent>
            <DialogActions>
              {onNewBooking && <Button onClick={onNewBooking}>New booking</Button>}
              {canCheckIn && (
                <Button
                  icon={<PersonArrowRightRegular />}
                  onClick={() => {
                    onClose();
                    desk.run('checkIn', appointment);
                  }}
                >
                  Check in now
                </Button>
              )}
              <Button
                appearance="primary"
                icon={<PrintRegular />}
                autoFocus
                onClick={() => desk.run('print', appointment)}
              >
                Print slip
              </Button>
              <Button onClick={onClose}>Done</Button>
            </DialogActions>
          </DialogBody>
        )}
      </DialogSurface>
    </Dialog>
  );
}
