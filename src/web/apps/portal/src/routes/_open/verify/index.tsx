import { Button, Field, Input, makeStyles, tokens } from '@fluentui/react-components';
import { QrCodeRegular } from '@fluentui/react-icons';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { Controller, useForm } from 'react-hook-form';
import { ChoiceCards } from '@/components/ChoiceCards';
import { PortalPage } from '@/components/PortalPage';
import { useT } from '@/i18n';
import { VERIFY_KIND_TEXT } from '@/modules/verify/labels';
import { isVerifyKind, parseVerificationInput, VERIFY_KINDS, type VerifyKind } from '@/modules/verify/verification';

/** Checking a printed prescription or report by its QR link or printed code (open to everyone). */
export const Route = createFileRoute('/_open/verify/')({
  validateSearch: (search: Record<string, unknown>): { kind?: VerifyKind } => ({ kind: isVerifyKind(search.kind) ? search.kind : undefined }),
  component: VerifyPage,
});

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '20px' },
  submit: { alignSelf: 'flex-start', minWidth: '200px' },
  note: { margin: 0, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
});

interface VerifyForm {
  kind: VerifyKind;
  input: string;
}

function VerifyPage() {
  const styles = useStyles();
  const t = useT();
  const search = Route.useSearch();
  const navigate = useNavigate();
  const form = useForm<VerifyForm>({ defaultValues: { kind: search.kind ?? 'prescription', input: '' } });

  const submit = form.handleSubmit((values) => {
    const parsed = parseVerificationInput(values.input, values.kind);
    if ('error' in parsed) {
      form.setError('input', { type: 'validate', message: t(parsed.error === 'empty' ? 'verify.code.empty' : 'verify.code.unrecognised') });
      return;
    }
    void navigate({ to: '/verify/$kind/$code', params: { kind: parsed.kind, code: parsed.code } });
  });

  return (
    <PortalPage title={t('verify.title')} subtitle={t('verify.subtitle')} width="narrow">
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
          name="kind"
          render={({ field }) => (
            <ChoiceCards
              label={t('verify.kind')}
              name={field.name}
              value={field.value}
              onChange={(value) => field.onChange(value)}
              twoUp
              options={VERIFY_KINDS.map((kind) => ({ value: kind, title: t(VERIFY_KIND_TEXT[kind].title), hint: t(VERIFY_KIND_TEXT[kind].hint) }))}
            />
          )}
        />
        <Controller
          control={form.control}
          name="input"
          render={({ field, fieldState }) => (
            <Field
              label={t('verify.code')}
              hint={t('verify.code.hint')}
              validationState={fieldState.error ? 'error' : 'none'}
              validationMessage={fieldState.error?.message}
            >
              <Input
                size="large"
                name={field.name}
                value={field.value}
                onChange={(_, data) => field.onChange(data.value)}
                onBlur={field.onBlur}
                autoComplete="off"
                autoCapitalize="off"
                autoCorrect="off"
                spellCheck={false}
                maxLength={300}
                contentBefore={<QrCodeRegular aria-hidden />}
              />
            </Field>
          )}
        />
        <Button type="submit" appearance="primary" size="large" className={styles.submit}>
          {t('verify.check')}
        </Button>
      </form>
      <p className={styles.note}>{t('verify.privacy')}</p>
    </PortalPage>
  );
}
