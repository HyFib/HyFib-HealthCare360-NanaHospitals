import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { BuildingRegular, ChevronRightRegular, VideoRegular } from '@fluentui/react-icons';
import { CardLink } from '@/components/links';
import { StatusPill } from '@/components/StatusPill';
import { useT } from '@/i18n';
import { istDateOf } from '@/i18n/format';
import { useFormat } from '@/i18n/useFormat';
import type { PortalAppointment } from '@/lib/types';
import { APPOINTMENT_STATUS, isOpenAppointment, PAYMENT_STATUS } from './labels';

const useStyles = makeStyles({
  card: { alignItems: 'stretch', padding: '12px' },
  tile: {
    flexShrink: 0,
    width: '60px',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground2,
    padding: '6px 0',
    lineHeight: 1.1,
  },
  muted: { backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorNeutralForeground2 },
  tileWeekday: { fontSize: tokens.fontSizeBase200, textTransform: 'uppercase', fontWeight: tokens.fontWeightSemibold },
  tileDay: { fontSize: '24px', fontWeight: tokens.fontWeightBold },
  tileMonth: { fontSize: tokens.fontSizeBase200 },
  body: { display: 'flex', flexDirection: 'column', gap: '4px', flex: 1, minWidth: 0, justifyContent: 'center' },
  doctor: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold, overflowWrap: 'anywhere' },
  line: { color: tokens.colorNeutralForeground2, overflowWrap: 'anywhere' },
  where: { display: 'flex', alignItems: 'center', gap: '6px', color: tokens.colorNeutralForeground2 },
  pills: { display: 'flex', flexWrap: 'wrap', gap: '6px', marginTop: '2px' },
  chevron: { color: tokens.colorNeutralForeground3, fontSize: '20px', alignSelf: 'center', flexShrink: 0 },
});

/** An appointment at a glance: a calendar tile, the doctor, time and place, and its status in words. */
export function AppointmentCard({ appointment }: { appointment: PortalAppointment }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const day = istDateOf(appointment.startsAt);
  const open = isOpenAppointment(appointment.status);
  const status = APPOINTMENT_STATUS[appointment.status];
  const payment = PAYMENT_STATUS[appointment.paymentStatus];
  const specialty = appointment.specialtyName ?? appointment.departmentName;

  return (
    <CardLink to="/appointments/$appointmentId" params={{ appointmentId: appointment.id }} className={styles.card}>
      <span className={mergeClasses(styles.tile, !open && styles.muted)} aria-hidden>
        <span className={styles.tileWeekday}>{f.weekday(day)}</span>
        <span className={styles.tileDay}>{Number(day.slice(8, 10))}</span>
        <span className={styles.tileMonth}>{f.monthShort(day)}</span>
      </span>
      <span className={styles.body}>
        <span className={styles.doctor}>{appointment.practitionerName}</span>
        {specialty && <span className={styles.line}>{specialty}</span>}
        <span className={styles.where}>
          {appointment.isTelehealth ? <VideoRegular aria-hidden /> : <BuildingRegular aria-hidden />}
          {f.dateLong(appointment.startsAt)}, {f.time(appointment.startsAt)} ·{' '}
          {appointment.isTelehealth
            ? t('appointment.mode.video')
            : (appointment.facilityName ?? t('appointment.mode.inPerson'))}
        </span>
        <span className={styles.pills}>
          <StatusPill tone={status.tone}>{t(status.key)}</StatusPill>
          {open && appointment.feeAmount > 0 && <StatusPill tone={payment.tone}>{t(payment.key)}</StatusPill>}
        </span>
      </span>
      <ChevronRightRegular className={styles.chevron} aria-hidden />
    </CardLink>
  );
}
