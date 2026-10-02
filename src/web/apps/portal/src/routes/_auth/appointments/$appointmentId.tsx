import { Button, Field, makeStyles, Spinner, Textarea, tokens } from '@fluentui/react-components';
import {
  CalendarCancelRegular,
  CalendarEditRegular,
  InfoRegular,
  PaymentRegular,
  VideoRegular,
} from '@fluentui/react-icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { InfoList } from '@/components/InfoList';
import { BackLink, ButtonLink } from '@/components/links';
import { OwnerGuard } from '@/components/OwnerGuard';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, Notice, PageSpinner } from '@/components/States';
import { StatusPill } from '@/components/StatusPill';
import { useLanguage, useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { usePortalFeatures } from '@/lib/features';
import { isGuid } from '@/lib/ids';
import { useToast } from '@/lib/toast';
import type { PortalAppointment } from '@/lib/types';
import { appointmentApi, appointmentQueries } from '@/modules/appointments/api';
import { APPOINTMENT_STATUS, isOpenAppointment, PAYMENT_STATUS, VISIT_TYPE } from '@/modules/appointments/labels';
import { payAppointment } from '@/modules/appointments/payment';
import { videoQueries } from '@/modules/video/api';
import { useHospitalName } from '@/shell/hooks';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/appointments/$appointmentId')({
  component: AppointmentPage,
});

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    padding: '16px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    '@media (min-width: 900px)': { padding: '24px' },
  },
  pills: { display: 'flex', flexWrap: 'wrap', gap: '8px' },
  actions: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    '@media (min-width: 600px)': { flexDirection: 'row', flexWrap: 'wrap' },
  },
  tip: { display: 'flex', gap: '8px', alignItems: 'flex-start', color: tokens.colorNeutralForeground2 },
});

function AppointmentPage() {
  const { appointmentId } = Route.useParams();
  const t = useT();
  const { profile } = useActiveProfile();
  const appointment = useQuery({
    ...appointmentQueries.detail(profile.id, appointmentId),
    enabled: isGuid(appointmentId),
  });
  const back = <BackLink to="/appointments">{t('nav.appointments')}</BackLink>;

  if (!isGuid(appointmentId)) {
    return (
      <PortalPage title={t('nav.appointments')} back={back} width="narrow">
        <Notice intent="warning">{t('errors.notFound')}</Notice>
      </PortalPage>
    );
  }
  if (appointment.isPending) {
    return <PageSpinner />;
  }
  if (appointment.isError) {
    return (
      <PortalPage title={t('nav.appointments')} back={back} width="narrow">
        <ErrorBlock error={appointment.error} onRetry={() => void appointment.refetch()} />
      </PortalPage>
    );
  }
  return (
    <OwnerGuard ownerId={appointment.data.patientId}>
      <AppointmentDetail appointment={appointment.data} back={back} />
    </OwnerGuard>
  );
}

function AppointmentDetail({ appointment, back }: { appointment: PortalAppointment; back: ReactNode }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const { language } = useLanguage();
  const toast = useToast();
  const hospital = useHospitalName();
  const queryClient = useQueryClient();
  const { profile } = useActiveProfile();
  const [cancelOpen, setCancelOpen] = useState(false);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState<'cancel' | 'pay' | null>(null);
  const [paymentNote, setPaymentNote] = useState<string | null>(null);

  const open = isOpenAppointment(appointment.status);
  // While the hospital has video consultations turned off, a video appointment can only be cancelled here.
  const { video } = usePortalFeatures();
  const videoOff = appointment.isTelehealth && !video;
  const sessions = useQuery({
    ...videoQueries.sessions(profile.id),
    enabled: appointment.isTelehealth && open && video,
  });
  const session = sessions.data?.find((item) => item.appointmentNumber === appointment.number);
  const status = APPOINTMENT_STATUS[appointment.status];
  const payment = PAYMENT_STATUS[appointment.paymentStatus];
  const refresh = () => void queryClient.invalidateQueries({ queryKey: appointmentQueries.all(profile.id) });

  const cancel = async () => {
    setBusy('cancel');
    try {
      await appointmentApi.cancel(appointment.id, reason.trim() || null);
      toast.success(t('appointment.cancelled'));
      setCancelOpen(false);
      refresh();
    } catch (error) {
      toast.failure(error, t);
    } finally {
      setBusy(null);
    }
  };

  const pay = async () => {
    setBusy('pay');
    setPaymentNote(null);
    try {
      const result = await payAppointment(appointment.id, null, {
        hospital,
        description: `${appointment.practitionerName} · ${f.dateTime(appointment.startsAt)}`,
        language,
      });
      if (result.status === 'paid') {
        toast.success(t('payment.success'), t('appointment.paid'));
      } else if (result.status === 'not-paid') {
        setPaymentNote(t(result.messageKey));
      } else {
        toast.failure(result.error, t);
      }
      refresh();
    } finally {
      setBusy(null);
    }
  };

  const changeClosed =
    open && !appointment.canCancel && !appointment.canReschedule && appointment.status !== 'InConsultation';

  return (
    <PortalPage
      title={appointment.practitionerName}
      subtitle={`${f.dateLong(appointment.startsAt)}, ${f.time(appointment.startsAt)}`}
      back={back}
    >
      <section className={styles.card} aria-label={t('common.details')}>
        <div className={styles.pills}>
          <StatusPill tone={status.tone}>{t(status.key)}</StatusPill>
          {appointment.feeAmount > 0 && <StatusPill tone={payment.tone}>{t(payment.key)}</StatusPill>}
          {appointment.isTelehealth && (
            <StatusPill tone="brand" icon={<VideoRegular />}>
              {t('appointment.mode.video')}
            </StatusPill>
          )}
        </div>
        <InfoList
          items={[
            {
              label: t('appointment.when'),
              value: `${f.dateLong(appointment.startsAt)}, ${f.time(appointment.startsAt)}`,
            },
            {
              label: t('appointment.where'),
              value: appointment.isTelehealth
                ? t('booking.confirm.videoWhere')
                : (appointment.facilityName ?? hospital),
            },
            {
              label: t('common.department'),
              value: appointment.specialtyName ?? appointment.departmentName,
              hideEmpty: true,
            },
            { label: t('appointment.visitType'), value: t(VISIT_TYPE[appointment.visitType]) },
            { label: t('booking.confirm.patient'), value: appointment.patientName },
            { label: t('appointment.number'), value: appointment.number },
            { label: t('appointment.fee'), value: f.inr(appointment.feeAmount), hideEmpty: appointment.feeAmount <= 0 },
            { label: t('appointment.reason'), value: appointment.reason, hideEmpty: true },
          ]}
        />
      </section>

      {paymentNote && <Notice intent="warning">{paymentNote}</Notice>}
      {open && videoOff && <Notice intent="info">{t('features.videoOff.booked')}</Notice>}
      {changeClosed && <Notice intent="info">{t('appointment.changeClosed')}</Notice>}

      {open && (
        <div className={styles.actions}>
          {appointment.isTelehealth &&
            !videoOff &&
            (session ? (
              <ButtonLink
                appearance="primary"
                icon={<VideoRegular />}
                to="/video/$sessionId"
                params={{ sessionId: session.id }}
              >
                {t('appointment.joinVideo')}
              </ButtonLink>
            ) : (
              <ButtonLink appearance="primary" icon={<VideoRegular />} to="/video">
                {t('appointment.joinVideo')}
              </ButtonLink>
            ))}
          {appointment.canPay && (
            <Button
              appearance={appointment.isTelehealth ? 'secondary' : 'primary'}
              size="large"
              icon={busy === 'pay' ? <Spinner size="tiny" /> : <PaymentRegular />}
              disabled={busy !== null}
              onClick={() => void pay()}
            >
              {busy === 'pay' ? t('payment.opening') : t('appointment.pay', { amount: f.inr(appointment.feeAmount) })}
            </Button>
          )}
          {appointment.canReschedule && !videoOff && (
            <ButtonLink
              appearance="secondary"
              icon={<CalendarEditRegular />}
              to="/doctors/$doctorId"
              params={{ doctorId: appointment.practitionerId }}
              search={{ reschedule: appointment.id }}
            >
              {t('appointment.reschedule')}
            </ButtonLink>
          )}
          {appointment.canCancel && (
            <Button
              appearance="subtle"
              size="large"
              icon={<CalendarCancelRegular />}
              disabled={busy !== null}
              onClick={() => setCancelOpen(true)}
            >
              {t('appointment.cancel')}
            </Button>
          )}
        </div>
      )}

      {open && !videoOff && (
        <p className={styles.tip}>
          <InfoRegular aria-hidden />
          {appointment.isTelehealth ? t('appointment.videoTip') : t('appointment.arrive')}
        </p>
      )}

      <ConfirmDialog
        open={cancelOpen}
        title={t('appointment.cancel.title')}
        confirmLabel={t('appointment.cancel.confirm')}
        dismissLabel={t('appointment.cancel.keep')}
        destructive
        busy={busy === 'cancel'}
        onConfirm={() => void cancel()}
        onDismiss={() => setCancelOpen(false)}
      >
        <p style={{ marginTop: 0 }}>{t('appointment.cancel.body')}</p>
        <Field label={t('appointment.cancel.reason')}>
          <Textarea
            value={reason}
            onChange={(_, data) => setReason(data.value)}
            maxLength={300}
            rows={2}
            resize="vertical"
          />
        </Field>
      </ConfirmDialog>
    </PortalPage>
  );
}
