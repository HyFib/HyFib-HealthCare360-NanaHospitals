import { zodResolver } from '@hookform/resolvers/zod';
import { Button, Field, Input, makeStyles, Spinner, Textarea, tokens } from '@fluentui/react-components';
import { applyServerErrors } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { Sheet } from '@/components/Sheet';
import { Notice } from '@/components/States';
import { useT, type Translate } from '@/i18n';
import { addDays, daysBetween, istDateOf } from '@/i18n/format';
import { useFormat } from '@/i18n/useFormat';
import { describeError } from '@/lib/errors';
import { useToast } from '@/lib/toast';
import type { RefillablePrescription } from '@/lib/types';
import { prescriptionApi } from './api';
import { useUnsavedChanges } from '@/lib/unsavedChanges';

const useStyles = makeStyles({
  lead: { margin: 0, color: tokens.colorNeutralForeground2 },
  medicines: { margin: 0, paddingLeft: '20px' },
});

function refillSchema(t: Translate, today: string) {
  return z.object({
    preferredPickupDate: z
      .string()
      .refine(
        (value) =>
          value === '' ||
          (/^\d{4}-\d{2}-\d{2}$/.test(value) && daysBetween(today, value) >= 0 && daysBetween(today, value) <= 30),
        {
          error: t('rx.refill.dateInvalid'),
        },
      ),
    notes: z.string().trim().max(500),
  });
}

type RefillForm = z.infer<ReturnType<typeof refillSchema>>;

/** Asks the hospital pharmacy to refill a chronic prescription (it checks and approves it). */
export function RefillSheet({
  open,
  onClose,
  refill,
}: {
  open: boolean;
  onClose: () => void;
  refill: RefillablePrescription;
}) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [today] = useState(() => istDateOf(Date.now()));
  const [error, setError] = useState<string | null>(null);
  const schema = useMemo(() => refillSchema(t, today), [t, today]);
  const form = useForm<RefillForm>({
    resolver: zodResolver(schema),
    defaultValues: { preferredPickupDate: '', notes: '' },
  });
  const busy = form.formState.isSubmitting;

  const close = () => {
    setError(null);
    form.reset();
    onClose();
  };
  // What was typed is not thrown away without asking (SRS UX-08).
  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy, open, onClose: close });

  const submit = form.handleSubmit(async (values) => {
    setError(null);
    try {
      const created = await prescriptionApi.requestRefill({
        queueEntryId: refill.queueEntryId,
        preferredPickupDate: values.preferredPickupDate || null,
        notes: values.notes || null,
      });
      toast.success(t('rx.refill.done', { number: created.number }));
      void queryClient.invalidateQueries({ queryKey: ['portal', refill.patientId, 'prescriptions'] });
      close();
    } catch (err) {
      if (!applyServerErrors(err, form.setError)) {
        setError(describeError(err, t, 'errors.saveTitle').message);
      }
    }
  });

  return (
    <Sheet
      open={open}
      title={t('rx.refill.request')}
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
            {t('rx.refill.submit')}
          </Button>
          <Button size="large" onClick={guard.requestClose} disabled={busy}>
            {t('common.notNow')}
          </Button>
        </>
      }
    >
      <p className={styles.lead}>
        {refill.refillValidUntil
          ? t('rx.refill.available', { count: refill.refillsRemaining, date: f.calendarDate(refill.refillValidUntil) })
          : t('rx.refill.availableNoDate', { count: refill.refillsRemaining })}
      </p>
      <ul className={styles.medicines}>
        {refill.medicines.map((medicine, index) => (
          <li key={`${medicine}-${index}`}>{medicine}</li>
        ))}
      </ul>
      {error && <Notice intent="error">{error}</Notice>}
      <form
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
        style={{ display: 'flex', flexDirection: 'column', gap: 16 }}
        noValidate
      >
        <Controller
          control={form.control}
          name="preferredPickupDate"
          render={({ field, fieldState }) => (
            <Field
              label={t('rx.refill.pickup')}
              hint={t('rx.refill.pickupHint')}
              validationState={fieldState.error ? 'error' : 'none'}
              validationMessage={fieldState.error?.message}
            >
              <Input
                type="date"
                size="large"
                value={field.value}
                min={today}
                max={addDays(today, 30)}
                onChange={(_, data) => field.onChange(data.value)}
                disabled={busy}
              />
            </Field>
          )}
        />
        <Controller
          control={form.control}
          name="notes"
          render={({ field }) => (
            <Field label={`${t('rx.refill.notes')} ${t('common.optional')}`}>
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
