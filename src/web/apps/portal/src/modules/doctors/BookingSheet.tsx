import { zodResolver } from '@hookform/resolvers/zod';
import { Button, Field, makeStyles, Select, Spinner, Textarea, tokens } from '@fluentui/react-components';
import { BuildingRegular, CheckmarkCircleRegular, PaymentRegular, VideoRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { applyServerErrors } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useNotify } from '@hc360/ui';
import { useMemo, useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { ChoiceCards } from '@/components/ChoiceCards';
import { InfoList } from '@/components/InfoList';
import { Sheet } from '@/components/Sheet';
import { Notice } from '@/components/States';
import { useLanguage, useT, type MessageKey, type Translate } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { describeError } from '@/lib/errors';
import { useNow } from '@/lib/hooks';
import type { AppointmentVisitType, PortalAppointment, PublicDoctor, PublicSlot } from '@/lib/types';
import { describeWhen } from '@/lib/when';
import { useActiveProfile } from '@/shell/profileContext';
import { useHospitalName } from '@/shell/hooks';
import { appointmentApi, appointmentQueries } from '../appointments/api';
import { payAppointment } from '../appointments/payment';

const useStyles = makeStyles({
  summary: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '14px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  doneIcon: { fontSize: '56px', color: tokens.colorStatusSuccessForeground1, alignSelf: 'center' },
  doneTitle: { margin: 0, textAlign: 'center', fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  doneText: { margin: 0, textAlign: 'center', color: tokens.colorNeutralForeground2 },
  fee: { display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', fontSize: tokens.fontSizeBase400 },
  feeAmount: { fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase500 },
  secure: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
});

function bookingSchema(t: Translate) {
  return z.object({
    patientId: z.string().min(1),
    visitType: z.enum(['New', 'FollowUp']),
    reason: z.string().trim().max(300, { error: t('booking.reason.tooLong') }),
    payment: z.enum(['online', 'hospital']),
  });
}

type BookingForm = z.infer<ReturnType<typeof bookingSchema>>;

type Phase = { name: 'form' } | { name: 'working'; label: MessageKey } | { name: 'done'; appointment: PortalAppointment; paid: boolean; note: string | null };

/**
 * Confirming a chosen time: for whom, first visit or follow-up, a short reason, and pay now or at the hospital.
 * The booking is made first; online payment follows, and a payment problem never loses the slot.
 */
export function BookingSheet({
  open,
  onClose,
  doctor,
  slot,
  video,
  branchName,
  onSlotsChanged,
}: {
  open: boolean;
  onClose: () => void;
  doctor: PublicDoctor;
  slot: PublicSlot | null;
  video: boolean;
  branchName: string | null;
  /** The free times changed (booked here, or taken by someone else): the page refreshes them. */
  onSlotsChanged: () => void;
}) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const { language } = useLanguage();
  const hospital = useHospitalName();
  const { profile, profiles } = useActiveProfile();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const notify = useNotify();
  const now = useNow(60_000, open);
  const [phase, setPhase] = useState<Phase>({ name: 'form' });
  const [error, setError] = useState<string | null>(null);
  const schema = useMemo(() => bookingSchema(t), [t]);

  const form = useForm<BookingForm>({
    resolver: zodResolver(schema),
    defaultValues: { patientId: profile.id, visitType: 'New', reason: '', payment: 'online' },
  });

  const visitType = useWatch({ control: form.control, name: 'visitType' });
  const fee = visitType === 'FollowUp' ? doctor.feeFollowUp : doctor.feeNew;
  const working = phase.name === 'working';

  const close = () => {
    const booked = phase.name === 'done';
    setPhase({ name: 'form' });
    setError(null);
    form.reset();
    onClose();
    if (booked) {
      onSlotsChanged();
    }
  };

  const submit = form.handleSubmit(async (values) => {
    if (!slot) {
      return;
    }
    const chosenFee = values.visitType === 'FollowUp' ? doctor.feeFollowUp : doctor.feeNew;
    const payOnline = values.payment === 'online' && chosenFee > 0;
    setError(null);
    setPhase({ name: 'working', label: 'booking.booking' });
    try {
      const result = await appointmentApi.book({
        patientId: values.patientId,
        practitionerId: doctor.id,
        startsAt: slot.startsAt,
        visitType: values.visitType as AppointmentVisitType,
        isTelehealth: video,
        reason: values.reason || null,
        payOnline,
      });
      void queryClient.invalidateQueries({ queryKey: appointmentQueries.all(values.patientId) });
      void queryClient.invalidateQueries({ queryKey: ['public', 'doctors'] });

      let paid = result.appointment.paymentStatus === 'PaidOnline';
      let note: string | null = null;
      if (result.payment) {
        setPhase({ name: 'working', label: 'payment.opening' });
        const payment = await payAppointment(result.appointment.id, result.payment, {
          hospital,
          description: `${doctor.name} · ${f.dateTime(slot.startsAt)}`,
          language,
        });
        if (payment.status === 'paid') {
          paid = true;
          void queryClient.invalidateQueries({ queryKey: appointmentQueries.all(values.patientId) });
        } else if (payment.status === 'not-paid') {
          note = `${t(payment.messageKey)} ${t('booking.done.payLater')}`;
        } else {
          note = `${describeError(payment.error, t).message} ${t('booking.done.payLater')}`;
        }
      } else if (payOnline) {
        note = t('payment.unavailable');
      }
      setPhase({ name: 'done', appointment: result.appointment, paid, note });
    } catch (err) {
      setPhase({ name: 'form' });
      if (applyServerErrors(err, form.setError)) {
        return;
      }
      if (isApiError(err) && err.status === 409) {
        // The seat went to someone else a moment ago: back to the times, which are refreshed.
        notify.warning(t('booking.slotGone'), err.detail);
        close();
        onSlotsChanged();
        return;
      }
      setError(describeError(err, t, 'errors.saveTitle').message);
    }
  });

  if (!slot) {
    return null;
  }

  if (phase.name === 'done') {
    const { appointment, paid, note } = phase;
    return (
      <Sheet
        open={open}
        title={t('booking.done.title')}
        onClose={close}
        footer={
          <>
            <Button
              appearance="primary"
              size="large"
              onClick={() => {
                close();
                void navigate({ to: '/appointments/$appointmentId', params: { appointmentId: appointment.id } });
              }}
            >
              {t('booking.done.view')}
            </Button>
            <Button size="large" onClick={close}>
              {t('booking.done.close')}
            </Button>
          </>
        }
      >
        <CheckmarkCircleRegular className={styles.doneIcon} aria-hidden />
        <h2 className={styles.doneTitle}>{t('booking.done.title')}</h2>
        <p className={styles.doneText} role="status">
          {appointment.isTelehealth ? t('booking.done.videoBody') : t('booking.done.body')}
        </p>
        <div className={styles.summary}>
          <InfoList
            singleColumn
            items={[
              { label: t('common.doctor'), value: appointment.practitionerName },
              { label: t('booking.confirm.when'), value: f.dateLong(appointment.startsAt) + ', ' + f.time(appointment.startsAt) },
              { label: t('booking.confirm.patient'), value: appointment.patientName },
              { label: t('appointment.number'), value: appointment.number },
            ]}
          />
        </div>
        {paid ? <Notice intent="success">{t('booking.done.paid')}</Notice> : note && <Notice intent="warning">{note}</Notice>}
      </Sheet>
    );
  }

  return (
    <Sheet
      open={open}
      title={t('booking.confirm.title')}
      onClose={close}
      dismissible={!working}
      footer={
        <>
          <Button appearance="primary" size="large" onClick={() => void submit()} disabled={working} icon={working ? <Spinner size="tiny" /> : undefined}>
            {phase.name === 'working' ? t(phase.label) : t('booking.book')}
          </Button>
          <Button size="large" onClick={close} disabled={working}>
            {t('common.notNow')}
          </Button>
        </>
      }
    >
      <div className={styles.summary}>
        <InfoList
          singleColumn
          items={[
            { label: t('common.doctor'), value: doctor.name },
            { label: t('booking.confirm.when'), value: describeWhen(slot.startsAt, f, t, now) },
            {
              label: t('booking.confirm.where'),
              value: (
                <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
                  {video ? <VideoRegular aria-hidden /> : <BuildingRegular aria-hidden />}
                  {video ? t('booking.confirm.videoWhere') : (branchName ?? hospital)}
                </span>
              ),
            },
          ]}
        />
      </div>

      {error && <Notice intent="error">{error}</Notice>}

      <form
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
        style={{ display: 'flex', flexDirection: 'column', gap: 16 }}
        noValidate
      >
        {profiles.length > 1 && (
          <Controller
            control={form.control}
            name="patientId"
            render={({ field }) => (
              <Field label={t('booking.confirm.patient')} required>
                <Select size="large" value={field.value} onChange={(_, data) => field.onChange(data.value)} disabled={working}>
                  {profiles.map((person) => (
                    <option key={person.id} value={person.id}>
                      {person.fullName}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
          />
        )}

        <Controller
          control={form.control}
          name="visitType"
          render={({ field }) => (
            <ChoiceCards
              label={t('booking.visit.label')}
              value={field.value}
              onChange={field.onChange}
              options={[
                { value: 'New', title: t('booking.visit.New'), hint: f.inr(doctor.feeNew, { whole: true }) },
                { value: 'FollowUp', title: t('booking.visit.FollowUp'), hint: f.inr(doctor.feeFollowUp, { whole: true }) },
              ]}
            />
          )}
        />

        <Controller
          control={form.control}
          name="reason"
          render={({ field, fieldState }) => (
            <Field
              label={t('booking.reason.label')}
              hint={t('booking.reason.hint')}
              validationState={fieldState.error ? 'error' : 'none'}
              validationMessage={fieldState.error?.message}
            >
              <Textarea {...field} value={field.value ?? ''} resize="vertical" rows={2} maxLength={300} disabled={working} />
            </Field>
          )}
        />

        {fee > 0 && (
          <Controller
            control={form.control}
            name="payment"
            render={({ field }) => (
              <ChoiceCards
                label={t('booking.payment.label')}
                value={field.value}
                onChange={field.onChange}
                options={[
                  { value: 'online', title: t('booking.payment.online'), hint: t('booking.payment.onlineHint'), icon: <PaymentRegular /> },
                  { value: 'hospital', title: t('booking.payment.hospital'), hint: t('booking.payment.hospitalHint'), icon: <BuildingRegular /> },
                ]}
              />
            )}
          />
        )}

        <div className={styles.fee}>
          <span>{t('booking.fee')}</span>
          <span className={styles.feeAmount}>{f.inr(fee)}</span>
        </div>
        <span className={styles.secure}>{t('payment.secure')}</span>
      </form>
    </Sheet>
  );
}
