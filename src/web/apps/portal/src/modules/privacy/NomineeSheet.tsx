import { zodResolver } from '@hookform/resolvers/zod';
import { Button, Field, Input, makeStyles, Spinner, tokens } from '@fluentui/react-components';
import { useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { Sheet } from '@/components/Sheet';
import { Notice } from '@/components/States';
import { useT, type Translate } from '@/i18n';
import { describeError } from '@/lib/errors';
import { sanitizePhoneInput } from '@/lib/otp';
import { useToast } from '@/lib/toast';
import type { NomineeDto } from '@/lib/types';
import { privacyApi, privacyQueries } from './api';
import {
  buildNomineeRequest,
  checkNominee,
  EMAIL_MAX,
  mapServerErrors,
  NAME_MAX,
  NOMINEE_FIELDS,
  nomineeFormValues,
  RELATIONSHIP_MAX,
  type NomineeFormValues,
} from './model';
import { useUnsavedChanges } from '@/lib/unsavedChanges';

const useStyles = makeStyles({
  lead: { margin: 0, color: tokens.colorNeutralForeground2 },
  form: { display: 'flex', flexDirection: 'column', gap: '16px' },
  note: { margin: 0, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
});

function nomineeSchema(t: Translate) {
  return z
    .object({ name: z.string(), relationship: z.string(), phone: z.string(), email: z.string() })
    .superRefine((value, ctx) => {
      for (const problem of checkNominee(value)) {
        ctx.addIssue({ code: 'custom', path: [problem.path], message: t(problem.key) });
      }
    });
}

const isNomineeField = (field: string): field is (typeof NOMINEE_FIELDS)[number] =>
  (NOMINEE_FIELDS as readonly string[]).includes(field);

/**
 * Names or replaces the patient's nominee (DPDP Act s14): name, relationship, and a mobile number or an e-mail
 * address. The form starts from the current nominee, so a change keeps what is still right.
 */
export function NomineeSheet({
  open,
  onClose,
  patientId,
  nominee,
}: {
  open: boolean;
  onClose: () => void;
  patientId: string;
  nominee: NomineeDto | null;
}) {
  const styles = useStyles();
  const t = useT();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [error, setError] = useState<string | null>(null);
  const schema = useMemo(() => nomineeSchema(t), [t]);
  const form = useForm<NomineeFormValues>({ resolver: zodResolver(schema), defaultValues: nomineeFormValues(nominee) });
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
      await privacyApi.saveNominee(buildNomineeRequest(values, patientId));
      toast.success(t('myPrivacy.nominee.saved'));
      void queryClient.invalidateQueries({ queryKey: privacyQueries.all(patientId) });
      close();
    } catch (err) {
      const mapped = mapServerErrors(err, (field) => (isNomineeField(field) ? field : null));
      if (!mapped) {
        setError(describeError(err, t, 'errors.saveTitle').message);
        return;
      }
      for (const { path, message } of mapped.fields) {
        form.setError(path as keyof NomineeFormValues, { type: 'server', message });
      }
      if (mapped.other.length > 0) {
        setError(mapped.other.join(' '));
      }
    }
  });

  return (
    <Sheet
      open={open}
      title={nominee ? t('myPrivacy.nominee.change') : t('myPrivacy.nominee.add')}
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
            icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            {busy ? t('common.saving') : t('myPrivacy.nominee.save')}
          </Button>
          <Button size="large" onClick={guard.requestClose} disabled={busy}>
            {t('common.notNow')}
          </Button>
        </>
      }
    >
      <p className={styles.lead}>{t('myPrivacy.nominee.contactHint')}</p>
      {error && <Notice intent="error">{error}</Notice>}
      <form
        className={styles.form}
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <Controller
          control={form.control}
          name="name"
          render={({ field, fieldState }) => (
            <Field
              label={t('myPrivacy.nominee.name')}
              required
              validationState={fieldState.error ? 'error' : 'none'}
              validationMessage={fieldState.error?.message}
            >
              <Input
                ref={field.ref}
                size="large"
                name={field.name}
                value={field.value}
                onChange={(_, data) => field.onChange(data.value)}
                onBlur={field.onBlur}
                maxLength={NAME_MAX}
                autoComplete="off"
                disabled={busy}
              />
            </Field>
          )}
        />
        <Controller
          control={form.control}
          name="relationship"
          render={({ field, fieldState }) => (
            <Field
              label={t('myPrivacy.nominee.relationship')}
              hint={t('myPrivacy.nominee.relationship.hint')}
              required
              validationState={fieldState.error ? 'error' : 'none'}
              validationMessage={fieldState.error?.message}
            >
              <Input
                ref={field.ref}
                size="large"
                name={field.name}
                value={field.value}
                onChange={(_, data) => field.onChange(data.value)}
                onBlur={field.onBlur}
                maxLength={RELATIONSHIP_MAX}
                autoComplete="off"
                disabled={busy}
              />
            </Field>
          )}
        />
        <Controller
          control={form.control}
          name="phone"
          render={({ field, fieldState }) => (
            <Field
              label={t('profile.phone')}
              validationState={fieldState.error ? 'error' : 'none'}
              validationMessage={fieldState.error?.message}
            >
              <Input
                ref={field.ref}
                type="tel"
                inputMode="numeric"
                autoComplete="off"
                size="large"
                contentBefore={<span>+91</span>}
                name={field.name}
                value={field.value}
                onChange={(_, data) => field.onChange(sanitizePhoneInput(data.value))}
                onBlur={field.onBlur}
                maxLength={14}
                disabled={busy}
              />
            </Field>
          )}
        />
        <Controller
          control={form.control}
          name="email"
          render={({ field, fieldState }) => (
            <Field
              label={t('myPrivacy.field.email')}
              validationState={fieldState.error ? 'error' : 'none'}
              validationMessage={fieldState.error?.message}
            >
              <Input
                ref={field.ref}
                type="email"
                inputMode="email"
                autoComplete="off"
                size="large"
                name={field.name}
                value={field.value}
                onChange={(_, data) => field.onChange(data.value)}
                onBlur={field.onBlur}
                maxLength={EMAIL_MAX}
                disabled={busy}
              />
            </Field>
          )}
        />
        <p className={styles.note}>{t('myPrivacy.nominee.tell')}</p>
      </form>
    </Sheet>
  );
}
