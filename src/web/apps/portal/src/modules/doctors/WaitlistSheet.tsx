import { zodResolver } from '@hookform/resolvers/zod';
import { Button, Field, Input, makeStyles, Select, Spinner, Textarea, tokens } from '@fluentui/react-components';
import { CheckmarkCircleRegular } from '@fluentui/react-icons';
import { applyServerErrors } from '@hc360/ui';
import { useToast } from '@/lib/toast';
import { useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { ChoiceCards } from '@/components/ChoiceCards';
import { Sheet } from '@/components/Sheet';
import { Notice } from '@/components/States';
import { useT, type Translate } from '@/i18n';
import { addDays, daysBetween, istDateOf } from '@/i18n/format';
import { describeError } from '@/lib/errors';
import { PREFERRED_SESSIONS, type PreferredSession, type PublicDoctor } from '@/lib/types';
import { useActiveProfile } from '@/shell/profileContext';
import { appointmentApi, appointmentQueries } from '../appointments/api';
import { PREFERRED_SESSION } from '../appointments/labels';
import { useUnsavedChanges } from '@/lib/unsavedChanges';

const useStyles = makeStyles({
  lead: { margin: 0, color: tokens.colorNeutralForeground2 },
  dates: { display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: '12px' },
  doneIcon: { fontSize: '56px', color: tokens.colorStatusSuccessForeground1, alignSelf: 'center' },
});

const MAX_DAYS = 60;

function waitlistSchema(t: Translate, today: string) {
  return z
    .object({
      patientId: z.string().min(1),
      preferredFrom: z.string().regex(/^\d{4}-\d{2}-\d{2}$/, { error: t('waitlist.dateRequired') }),
      preferredTo: z.string().regex(/^\d{4}-\d{2}-\d{2}$/, { error: t('waitlist.dateRequired') }),
      preferredSession: z.enum(PREFERRED_SESSIONS),
      notes: z.string().trim().max(500),
    })
    .refine((value) => daysBetween(today, value.preferredFrom) >= 0, {
      path: ['preferredFrom'],
      error: t('waitlist.dateRequired'),
    })
    .refine((value) => daysBetween(value.preferredFrom, value.preferredTo) >= 0, {
      path: ['preferredTo'],
      error: t('waitlist.datesInvalid'),
    })
    .refine((value) => daysBetween(value.preferredFrom, value.preferredTo) <= MAX_DAYS, {
      path: ['preferredTo'],
      error: t('waitlist.rangeTooLong'),
    });
}

type WaitlistForm = z.infer<ReturnType<typeof waitlistSchema>>;

/** "Tell me when a time opens up" for a doctor whose times are full; the hospital offers freed times by SMS/WhatsApp. */
export function WaitlistSheet({
  open,
  onClose,
  doctor,
  video,
}: {
  open: boolean;
  onClose: () => void;
  doctor: PublicDoctor;
  video: boolean;
}) {
  const styles = useStyles();
  const t = useT();
  const toast = useToast();
  const queryClient = useQueryClient();
  const { profile, profiles } = useActiveProfile();
  const [today] = useState(() => istDateOf(Date.now()));
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const schema = useMemo(() => waitlistSchema(t, today), [t, today]);
  const form = useForm<WaitlistForm>({
    resolver: zodResolver(schema),
    defaultValues: {
      patientId: profile.id,
      preferredFrom: today,
      preferredTo: addDays(today, 14),
      preferredSession: 'Any',
      notes: '',
    },
  });
  const busy = form.formState.isSubmitting;

  const close = () => {
    setDone(false);
    setError(null);
    form.reset();
    onClose();
  };
  // What was typed is not thrown away without asking (SRS UX-08); once on the list there is nothing to lose.
  const guard = useUnsavedChanges({ dirty: form.formState.isDirty && !done, busy, open, onClose: close });

  const submit = form.handleSubmit(async (values) => {
    setError(null);
    try {
      await appointmentApi.joinWaitlist({
        patientId: values.patientId,
        practitionerId: doctor.id,
        preferredFrom: values.preferredFrom,
        preferredTo: values.preferredTo,
        preferredSession: values.preferredSession as PreferredSession,
        isTelehealth: video,
        notes: values.notes || null,
      });
      void queryClient.invalidateQueries({ queryKey: appointmentQueries.all(values.patientId) });
      setDone(true);
      toast.success(t('waitlist.done'));
    } catch (err) {
      if (!applyServerErrors(err, form.setError)) {
        setError(describeError(err, t, 'errors.saveTitle').message);
      }
    }
  });

  if (done) {
    return (
      <Sheet
        open={open}
        title={t('waitlist.title')}
        onClose={close}
        footer={
          <Button appearance="primary" size="large" onClick={close}>
            {t('booking.done.close')}
          </Button>
        }
      >
        <CheckmarkCircleRegular className={styles.doneIcon} aria-hidden />
        <Notice intent="success">{t('waitlist.done')}</Notice>
      </Sheet>
    );
  }

  return (
    <Sheet
      open={open}
      title={t('waitlist.title')}
      onClose={close}
      guard={guard}
      dismissible={!busy}
      footer={
        <>
          <Button
            appearance="primary"
            size="large"
            onClick={() => void submit()}
            disabled={busy}
            icon={busy ? <Spinner size="tiny" /> : undefined}
          >
            {t('waitlist.submit')}
          </Button>
          <Button size="large" onClick={guard.requestClose} disabled={busy}>
            {t('common.notNow')}
          </Button>
        </>
      }
    >
      <p className={styles.lead}>{t('waitlist.body')}</p>
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
                <Select
                  size="large"
                  value={field.value}
                  onChange={(_, data) => field.onChange(data.value)}
                  disabled={busy}
                >
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
        <div className={styles.dates}>
          <Controller
            control={form.control}
            name="preferredFrom"
            render={({ field, fieldState }) => (
              <Field
                label={t('waitlist.from')}
                required
                validationState={fieldState.error ? 'error' : 'none'}
                validationMessage={fieldState.error?.message}
              >
                <Input
                  type="date"
                  size="large"
                  value={field.value}
                  min={today}
                  onChange={(_, data) => field.onChange(data.value)}
                  disabled={busy}
                />
              </Field>
            )}
          />
          <Controller
            control={form.control}
            name="preferredTo"
            render={({ field, fieldState }) => (
              <Field
                label={t('waitlist.to')}
                required
                validationState={fieldState.error ? 'error' : 'none'}
                validationMessage={fieldState.error?.message}
              >
                <Input
                  type="date"
                  size="large"
                  value={field.value}
                  min={today}
                  max={addDays(today, MAX_DAYS)}
                  onChange={(_, data) => field.onChange(data.value)}
                  disabled={busy}
                />
              </Field>
            )}
          />
        </div>
        <Controller
          control={form.control}
          name="preferredSession"
          render={({ field }) => (
            <ChoiceCards
              label={t('waitlist.session')}
              value={field.value}
              onChange={field.onChange}
              twoUp
              options={PREFERRED_SESSIONS.map((session) => ({ value: session, title: t(PREFERRED_SESSION[session]) }))}
            />
          )}
        />
        <Controller
          control={form.control}
          name="notes"
          render={({ field }) => (
            <Field label={t('waitlist.notes')}>
              <Textarea
                {...field}
                value={field.value ?? ''}
                rows={2}
                maxLength={500}
                resize="vertical"
                disabled={busy}
              />
            </Field>
          )}
        />
      </form>
    </Sheet>
  );
}
