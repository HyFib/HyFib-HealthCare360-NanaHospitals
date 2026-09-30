import {
  Badge,
  Button,
  Caption1,
  Divider,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Subtitle2,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  CalendarArrowRightRegular,
  CheckmarkRegular,
  DismissCircleRegular,
  DismissRegular,
  PersonArrowRightRegular,
  PersonProhibitedRegular,
  PersonRegular,
  PrintRegular,
  VideoRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatInr, formatPhone, formatTime, humanize } from '@hc360/core';
import { ErrorState, LoadingState, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import type { ReactElement, ReactNode } from 'react';
import { schedulingQueries, type AppointmentDetail } from './api';
import type { AppointmentDesk } from './useAppointmentDesk';
import { appointmentActions, type AppointmentAction } from './appointmentRules';
import { formatDayLong, istDate } from './dates';
import { appointmentStatusText, cancellationReasonLabel, channelLabel, paymentLabel, visitTypeLabel } from './labels';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '16px', paddingBottom: '16px' },
  grid: { display: 'grid', gridTemplateColumns: 'minmax(110px, auto) 1fr', gap: '6px 12px', alignItems: 'baseline' },
  label: { color: tokens.colorNeutralForeground3 },
  when: { display: 'flex', flexDirection: 'column', gap: '2px' },
  time: {
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
  },
  footer: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  history: { display: 'flex', flexDirection: 'column', gap: '6px' },
  mono: { fontFamily: tokens.fontFamilyMonospace },
});

function Row({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <>
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text>{children ?? '—'}</Text>
    </>
  );
}

/** Everything about one appointment, with the actions its state allows (who/when shown for every change). */
export function AppointmentDrawer({
  appointmentId,
  onClose,
  onOpenAppointment,
  desk,
}: {
  appointmentId: string | null;
  onClose: () => void;
  onOpenAppointment: (id: string) => void;
  desk: AppointmentDesk;
}) {
  const detail = useQuery({ ...schedulingQueries.appointment(appointmentId ?? ''), enabled: appointmentId !== null });

  return (
    <OverlayDrawer
      open={appointmentId !== null}
      position="end"
      size="medium"
      onOpenChange={(_, d) => !d.open && onClose()}
    >
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
        >
          {detail.data ? `Appointment ${detail.data.number}` : 'Appointment'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      {detail.isLoading ? (
        <DrawerBody>
          <LoadingState />
        </DrawerBody>
      ) : detail.error || !detail.data ? (
        <DrawerBody>
          <ErrorState error={detail.error} onRetry={() => void detail.refetch()} />
        </DrawerBody>
      ) : (
        <AppointmentDetailView
          appointment={detail.data}
          desk={desk}
          onOpenAppointment={onOpenAppointment}
          onClose={onClose}
        />
      )}
    </OverlayDrawer>
  );
}

function AppointmentDetailView({
  appointment: a,
  desk,
  onOpenAppointment,
  onClose,
}: {
  appointment: AppointmentDetail;
  desk: AppointmentDesk;
  onOpenAppointment: (id: string) => void;
  onClose: () => void;
}) {
  const styles = useStyles();
  const navigate = useNavigate();
  const actions = appointmentActions(a, desk.context);
  const status = appointmentStatusText[a.status];

  const action = (key: AppointmentAction, label: string, icon: ReactElement, primary = false) =>
    actions[key].allowed ? (
      <Button key={key} appearance={primary ? 'primary' : 'secondary'} icon={icon} onClick={() => desk.run(key, a)}>
        {label}
      </Button>
    ) : null;

  return (
    <>
      <DrawerBody>
        <div className={styles.body}>
          <div className={styles.when}>
            <div>
              <StatusBadge status={a.status} tone={status.tone} label={status.label} />{' '}
              {a.isTelehealth && (
                <Badge appearance="tint" color="brand" icon={<VideoRegular />}>
                  Video consultation
                </Badge>
              )}
            </div>
            <Text>{formatDayLong(istDate(a.startsAt))}</Text>
            <span className={styles.time}>
              {formatTime(a.startsAt)} – {formatTime(a.endsAt)}
            </span>
            <Text weight="semibold">{a.practitionerName}</Text>
            <Caption1 className={styles.label}>
              {[a.departmentName, a.room ? `Room ${a.room}` : null].filter(Boolean).join(' · ')}
            </Caption1>
          </div>

          {a.status === 'Rescheduled' && a.rescheduledToId && (
            <MessageBar intent="info">
              <MessageBarBody>
                This booking was moved to a new time.{' '}
                <Button size="small" appearance="transparent" onClick={() => onOpenAppointment(a.rescheduledToId!)}>
                  Open the new appointment
                </Button>
              </MessageBarBody>
            </MessageBar>
          )}

          <Divider />
          <Subtitle2 as="h3">Patient</Subtitle2>
          <div className={styles.grid}>
            <Row label="Name">
              {a.patientName} ({a.age ?? '—'} / {a.gender.charAt(0)})
            </Row>
            <Row label="UHID">
              <span className={styles.mono}>{a.uhid}</span>
            </Row>
            <Row label="Mobile">{a.phone ? formatPhone(a.phone) : null}</Row>
          </div>

          <Divider />
          <Subtitle2 as="h3">Booking</Subtitle2>
          <div className={styles.grid}>
            <Row label="Visit">{visitTypeLabel[a.visitType]}</Row>
            <Row label="Booked through">{channelLabel[a.channel]}</Row>
            <Row label="Booked">{formatDateTime(a.bookedAt)}</Row>
            {a.confirmedAt && <Row label="Confirmed">{formatDateTime(a.confirmedAt)}</Row>}
            <Row label="Reason">{a.reason}</Row>
            {a.notes && <Row label="Notes">{a.notes}</Row>}
            <Row label="Reminders">
              {[
                a.reminder24hSentAt ? `24 h reminder ${formatDateTime(a.reminder24hSentAt)}` : null,
                a.reminder2hSentAt ? `2 h reminder ${formatDateTime(a.reminder2hSentAt)}` : null,
              ]
                .filter(Boolean)
                .join(' · ') || 'Not sent yet'}
            </Row>
          </div>

          <Divider />
          <Subtitle2 as="h3">Payment</Subtitle2>
          <div className={styles.grid}>
            <Row label="Consultation fee">{formatInr(a.feeAmount)}</Row>
            <Row label="Status">{paymentLabel[a.paymentStatus]}</Row>
            {a.amountPaid !== null && (
              <Row label="Paid">
                {formatInr(a.amountPaid)}
                {a.paidAt ? ` on ${formatDateTime(a.paidAt)}` : ''}
                {a.paymentProvider ? ` via ${a.paymentProvider}` : ''}
              </Row>
            )}
          </div>

          {(a.checkedInAt || a.completedAt || a.noShowAt || a.cancelledAt) && (
            <>
              <Divider />
              <Subtitle2 as="h3">What happened</Subtitle2>
              <div className={styles.grid}>
                {a.checkedInAt && <Row label="Checked in">{formatDateTime(a.checkedInAt)}</Row>}
                {a.completedAt && <Row label="Completed">{formatDateTime(a.completedAt)}</Row>}
                {a.noShowAt && <Row label="No-show recorded">{formatDateTime(a.noShowAt)}</Row>}
                {a.cancelledAt && (
                  <Row label={a.status === 'Rescheduled' ? 'Moved' : 'Cancelled'}>
                    {formatDateTime(a.cancelledAt)}
                    {a.cancelledBy ? ` by ${humanize(a.cancelledBy).toLowerCase()}` : ''}
                  </Row>
                )}
                {a.cancelReasonCode && <Row label="Reason">{cancellationReasonLabel[a.cancelReasonCode]}</Row>}
                {a.cancelReason && <Row label="Details">{a.cancelReason}</Row>}
              </div>
            </>
          )}

          {a.history.length > 0 && (
            <>
              <Divider />
              <Subtitle2 as="h3">Earlier times</Subtitle2>
              <div className={styles.history}>
                {a.history.map((h) => (
                  <Caption1 key={h.id}>
                    {formatDateTime(h.startsAt)} · {h.number}
                    {h.note ? ` · ${h.note}` : ''}
                  </Caption1>
                ))}
              </div>
            </>
          )}
        </div>
      </DrawerBody>
      <DrawerFooter>
        <div className={styles.footer}>
          {action('checkIn', 'Check in', <PersonArrowRightRegular />, true)}
          {action('confirm', 'Confirm', <CheckmarkRegular />)}
          {action('reschedule', 'Reschedule', <CalendarArrowRightRegular />)}
          {action('noShow', 'No-show', <PersonProhibitedRegular />)}
          {action('cancel', 'Cancel', <DismissCircleRegular />)}
          <Button icon={<PrintRegular />} onClick={() => desk.run('print', a)}>
            Print slip
          </Button>
          <Button
            appearance="subtle"
            icon={<PersonRegular />}
            onClick={() => {
              onClose();
              void navigate({
                to: '/patients/$patientId',
                params: { patientId: a.patientId },
                search: a.visitId ? { tab: 'visits' } : {},
              });
            }}
          >
            Patient profile
          </Button>
        </div>
      </DrawerFooter>
    </>
  );
}
