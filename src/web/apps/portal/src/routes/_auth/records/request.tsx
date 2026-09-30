import { zodResolver } from '@hookform/resolvers/zod';
import { Button, Field, makeStyles, Spinner, Textarea, tokens } from '@fluentui/react-components';
import { CheckmarkCircleRegular } from '@fluentui/react-icons';
import { applyServerErrors } from '@hc360/ui';
import { createFileRoute } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { ChoiceCards } from '@/components/ChoiceCards';
import { BackLink, ButtonLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { Notice } from '@/components/States';
import { useT, type Translate } from '@/i18n';
import { describeError } from '@/lib/errors';
import { RECORD_REQUEST_PURPOSES, type RecordRequestCreated } from '@/lib/types';
import { recordApi } from '@/modules/records/api';
import { REQUEST_PURPOSE } from '@/modules/records/labels';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/records/request')({
  component: RecordRequestPage,
});

const DETAILS_MAX = 1000;

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '20px' },
  lead: {
    margin: 0,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
  person: { margin: 0, fontWeight: tokens.fontWeightSemibold },
  policy: { margin: 0, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  submit: { alignSelf: 'flex-start', minWidth: '200px' },
  done: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '12px',
    padding: '24px 16px',
    textAlign: 'center',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  doneIcon: { fontSize: '56px', color: tokens.colorStatusSuccessForeground1 },
  doneTitle: { margin: 0, fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  doneText: {
    margin: 0,
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
  },
});

function requestSchema(t: Translate) {
  return z
    .object({
      purpose: z
        .enum(RECORD_REQUEST_PURPOSES)
        .nullable()
        .refine((value) => value !== null, { error: t('recordRequest.purposeRequired') }),
      details: z
        .string()
        .trim()
        .max(DETAILS_MAX, { error: t('recordRequest.detailsTooLong') }),
    })
    .superRefine((value, ctx) => {
      if (value.purpose === 'Other' && !value.details) {
        ctx.addIssue({ code: 'custom', path: ['details'], message: t('recordRequest.detailsRequired') });
      }
    });
}

type RequestForm = z.input<ReturnType<typeof requestSchema>>;

function RecordRequestPage() {
  const styles = useStyles();
  const t = useT();
  const { profile } = useActiveProfile();
  const [created, setCreated] = useState<RecordRequestCreated | null>(null);
  const [error, setError] = useState<string | null>(null);
  const schema = useMemo(() => requestSchema(t), [t]);
  const form = useForm<RequestForm>({ resolver: zodResolver(schema), defaultValues: { purpose: null, details: '' } });
  const busy = form.formState.isSubmitting;
  const back = <BackLink to="/records">{t('records.title')}</BackLink>;

  const submit = form.handleSubmit(async (values) => {
    if (!values.purpose) {
      return;
    }
    setError(null);
    try {
      const result = await recordApi.requestCopy({
        patientId: profile.id,
        purpose: values.purpose,
        details: values.details.trim() || null,
      });
      setCreated(result);
    } catch (err) {
      if (!applyServerErrors(err, form.setError)) {
        setError(describeError(err, t, 'errors.saveTitle').message);
      }
    }
  });

  if (created) {
    return (
      <PortalPage title={t('recordRequest.title')} back={back} width="narrow">
        <div className={styles.done} role="status">
          <CheckmarkCircleRegular className={styles.doneIcon} aria-hidden />
          <h2 className={styles.doneTitle}>{t('recordRequest.done.title')}</h2>
          <p className={styles.doneText}>{t('recordRequest.done.body', { number: created.requestNumber })}</p>
          <ButtonLink to="/records" appearance="primary">
            {t('recordRequest.done.back')}
          </ButtonLink>
        </div>
      </PortalPage>
    );
  }

  return (
    <PortalPage title={t('recordRequest.title')} back={back} width="narrow">
      <p className={styles.lead}>{t('recordRequest.lead')}</p>
      <p className={styles.person}>{t('common.forPerson', { name: profile.fullName })}</p>
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
          name="purpose"
          render={({ field, fieldState }) => (
            <Field validationState={fieldState.error ? 'error' : 'none'} validationMessage={fieldState.error?.message}>
              <ChoiceCards
                label={t('recordRequest.purpose')}
                name={field.name}
                value={field.value}
                onChange={(value) => field.onChange(value)}
                twoUp
                options={RECORD_REQUEST_PURPOSES.flatMap((purpose) => {
                  const label = REQUEST_PURPOSE[purpose];
                  return label ? [{ value: purpose, title: t(label.title), hint: t(label.hint), disabled: busy }] : [];
                })}
              />
            </Field>
          )}
        />
        <Controller
          control={form.control}
          name="details"
          render={({ field, fieldState }) => (
            <Field
              label={t('recordRequest.details')}
              hint={t('recordRequest.detailsHint')}
              validationState={fieldState.error ? 'error' : 'none'}
              validationMessage={fieldState.error?.message}
            >
              <Textarea
                name={field.name}
                value={field.value}
                onChange={(_, data) => field.onChange(data.value)}
                onBlur={field.onBlur}
                rows={4}
                maxLength={DETAILS_MAX}
                resize="vertical"
                disabled={busy}
              />
            </Field>
          )}
        />
        {error && <Notice intent="error">{error}</Notice>}
        <p className={styles.policy}>{t('recordRequest.policy')}</p>
        <Button
          type="submit"
          appearance="primary"
          size="large"
          className={styles.submit}
          disabled={busy}
          icon={busy ? <Spinner size="tiny" /> : undefined}
        >
          {busy ? t('common.sending') : t('recordRequest.submit')}
        </Button>
      </form>
    </PortalPage>
  );
}
