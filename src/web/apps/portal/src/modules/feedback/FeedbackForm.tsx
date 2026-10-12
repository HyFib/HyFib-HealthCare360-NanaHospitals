import {
  Button,
  Checkbox,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  Rating,
  Spinner,
  Textarea,
  tokens,
  useId,
} from '@fluentui/react-components';
import { isApiError } from '@hc360/core';
import { useState } from 'react';
import { Controller, useForm, useWatch, type Control, type Resolver } from 'react-hook-form';
import { Notice } from '@/components/States';
import { useT } from '@/i18n';
import { describeError } from '@/lib/errors';
import { sanitizePhoneInput } from '@/lib/otp';
import type { FeedbackAspect, SubmitFeedbackRequest } from '@/lib/types';
import {
  ASPECT_KEY,
  checkAnswers,
  COMMENT_MAX,
  emptyAnswers,
  toFeedbackRequest,
  type FeedbackAnswers,
  type FeedbackQuestions,
} from './model';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '24px' },
  intro: {
    margin: 0,
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    color: tokens.colorNeutralForeground2,
    whiteSpace: 'pre-line',
  },
  group: { border: 'none', margin: 0, padding: 0, minWidth: 0, display: 'flex', flexDirection: 'column', gap: '12px' },
  legend: { padding: 0, marginBottom: '4px', fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400 },
  hint: { margin: 0, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  aspect: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '4px 12px',
    padding: '10px 0',
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  aspectName: { fontSize: tokens.fontSizeBase400, minWidth: '140px', flex: '1 1 140px' },
  aspectControl: { display: 'flex', alignItems: 'center', gap: '4px' },
  scale: {
    display: 'grid',
    gridTemplateColumns: 'repeat(6, minmax(44px, 1fr))',
    gap: '6px',
    '@media (min-width: 640px)': { gridTemplateColumns: 'repeat(11, minmax(40px, 1fr))' },
  },
  scaleItem: {
    position: 'relative',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '48px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1,
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-within': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
  scaleChecked: {
    backgroundColor: tokens.colorBrandBackground,
    color: tokens.colorNeutralForegroundOnBrand,
    border: `1px solid ${tokens.colorBrandBackground}`,
    ':hover': { backgroundColor: tokens.colorBrandBackgroundHover },
  },
  scaleInput: { position: 'absolute', inset: 0, margin: 0, opacity: 0, cursor: 'pointer' },
  srOnly: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    overflow: 'hidden',
    clip: 'rect(0 0 0 0)',
    whiteSpace: 'nowrap',
  },
  scaleEnds: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: '12px',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  callback: { display: 'flex', flexDirection: 'column', gap: '8px' },
  submit: { width: '100%', '@media (min-width: 640px)': { width: 'auto', alignSelf: 'flex-start', minWidth: '220px' } },
  note: { margin: 0, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
});

const NPS_VALUES = Array.from({ length: 11 }, (_, index) => index);
const FORM_FIELDS = new Set(['ratings', 'nps', 'comment', 'contactPhone']);

export interface FeedbackFormProps {
  questions: FeedbackQuestions;
  /** The hospital's own introduction for this form (plain text). */
  intro?: string | null;
  /** Who will see the answers (anonymous, or linked to the visit). */
  privacyNote: string;
  defaultPhone?: string | null;
  /** Sends the answers; throw to show the problem on the form. */
  onSubmit: (body: SubmitFeedbackRequest) => Promise<void>;
  /** Lets the page handle an error itself (for example a link that was used meanwhile). Return true when handled. */
  onError?: (error: unknown) => boolean;
}

/** Star ratings for the form's aspects, the 0–10 recommendation score, a comment and an optional call-back. */
export function FeedbackForm({ questions, intro, privacyNote, defaultPhone, onSubmit, onError }: FeedbackFormProps) {
  const styles = useStyles();
  const t = useT();
  const [formError, setFormError] = useState<string | null>(null);

  const resolver: Resolver<FeedbackAnswers> = async (values) => {
    const problems = checkAnswers(values, questions);
    const errors: Record<string, { type: string; message: string }> = {};
    if (problems.comment) {
      errors.comment = { type: 'validate', message: t(problems.comment) };
    }
    if (problems.contactPhone) {
      errors.contactPhone = { type: 'validate', message: t(problems.contactPhone) };
    }
    return Object.keys(errors).length ? { values: {}, errors } : { values, errors: {} };
  };

  const form = useForm<FeedbackAnswers>({
    resolver,
    defaultValues: emptyAnswers(sanitizePhoneInput(defaultPhone ?? '')),
  });
  const wantsCallback = useWatch({ control: form.control, name: 'wantsCallback' });
  const busy = form.formState.isSubmitting;

  const submit = form.handleSubmit(async (values) => {
    setFormError(null);
    const problems = checkAnswers(values, questions);
    if (problems.form) {
      setFormError(t(problems.form));
      return;
    }
    try {
      await onSubmit(toFeedbackRequest(values, questions));
    } catch (error) {
      if (onError?.(error)) {
        return;
      }
      if (isApiError(error) && error.isValidation) {
        const general: string[] = [];
        for (const [field, messages] of Object.entries(error.fieldErrors)) {
          const message = messages[0] ?? t('errors.generic');
          if (FORM_FIELDS.has(field)) {
            form.setError(field as 'ratings' | 'nps' | 'comment' | 'contactPhone', { type: 'server', message });
          } else {
            general.push(message);
          }
        }
        if (general.length) {
          setFormError(general.join(' '));
        }
        return;
      }
      setFormError(describeError(error, t, 'errors.saveTitle').message);
    }
  });

  return (
    <form
      className={styles.form}
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        void submit();
      }}
    >
      {intro?.trim() && <p className={styles.intro}>{intro.trim()}</p>}

      {questions.aspects.length > 0 && (
        <fieldset className={styles.group} disabled={busy}>
          <legend className={styles.legend}>{t('feedback.ratings')}</legend>
          <p className={styles.hint}>{t('feedback.ratings.hint')}</p>
          <div>
            {questions.aspects.map((aspect) => (
              <AspectRating key={aspect} aspect={aspect} control={form.control} disabled={busy} />
            ))}
          </div>
          {form.formState.errors.ratings?.message && (
            <Notice intent="error">{form.formState.errors.ratings.message}</Notice>
          )}
        </fieldset>
      )}

      {questions.includesNps && (
        <Controller
          control={form.control}
          name="nps"
          render={({ field, fieldState }) => (
            <fieldset className={styles.group} disabled={busy}>
              <legend className={styles.legend}>{t('feedback.nps')}</legend>
              <div className={styles.scale}>
                {NPS_VALUES.map((value) => (
                  <label
                    key={value}
                    className={mergeClasses(styles.scaleItem, field.value === value && styles.scaleChecked)}
                  >
                    <input
                      type="radio"
                      className={styles.scaleInput}
                      name={field.name}
                      value={value}
                      checked={field.value === value}
                      disabled={busy}
                      onChange={() => field.onChange(value)}
                    />
                    <span aria-hidden>{value}</span>
                    <span className={styles.srOnly}>
                      {value === 0 ? t('feedback.nps.low') : value === 10 ? t('feedback.nps.high') : value}
                    </span>
                  </label>
                ))}
              </div>
              <div className={styles.scaleEnds} aria-hidden>
                <span>{t('feedback.nps.low')}</span>
                <span>{t('feedback.nps.high')}</span>
              </div>
              {field.value !== null && (
                <div>
                  <Button appearance="subtle" size="small" onClick={() => field.onChange(null)} disabled={busy}>
                    {t('feedback.nps.clear')}
                  </Button>
                </div>
              )}
              {fieldState.error?.message && <Notice intent="error">{fieldState.error.message}</Notice>}
            </fieldset>
          )}
        />
      )}

      {questions.allowsComment && (
        <Controller
          control={form.control}
          name="comment"
          render={({ field, fieldState }) => (
            <Field
              label={`${t('feedback.comment')} ${t('common.optional')}`}
              hint={t('feedback.comment.hint')}
              validationState={fieldState.error ? 'error' : 'none'}
              validationMessage={fieldState.error?.message}
            >
              <Textarea
                name={field.name}
                value={field.value}
                onChange={(_, data) => field.onChange(data.value)}
                onBlur={field.onBlur}
                rows={4}
                maxLength={COMMENT_MAX}
                resize="vertical"
                disabled={busy}
              />
            </Field>
          )}
        />
      )}

      <div className={styles.callback}>
        <Controller
          control={form.control}
          name="wantsCallback"
          render={({ field }) => (
            <Checkbox
              size="large"
              label={t('feedback.callback')}
              checked={field.value}
              onChange={(_, data) => field.onChange(data.checked === true)}
              disabled={busy}
            />
          )}
        />
        {wantsCallback && (
          <Controller
            control={form.control}
            name="contactPhone"
            render={({ field, fieldState }) => (
              <Field
                label={t('feedback.phone')}
                required
                validationState={fieldState.error ? 'error' : 'none'}
                validationMessage={fieldState.error?.message}
              >
                <Input
                  type="tel"
                  inputMode="numeric"
                  autoComplete="tel-national"
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
        )}
      </div>

      {formError && <Notice intent="error">{formError}</Notice>}
      <p className={styles.note}>{privacyNote}</p>
      <Button
        type="submit"
        appearance="primary"
        size="large"
        className={styles.submit}
        disabled={busy}
        icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
      >
        {busy ? t('common.sending') : t('feedback.submit')}
      </Button>
    </form>
  );
}

function AspectRating({
  aspect,
  control,
  disabled,
}: {
  aspect: FeedbackAspect;
  control: Control<FeedbackAnswers>;
  disabled: boolean;
}) {
  const styles = useStyles();
  const t = useT();
  const labelId = useId('aspect');
  return (
    <Controller
      control={control}
      name={`ratings.${aspect}`}
      render={({ field }) => (
        <div className={styles.aspect}>
          <span id={labelId} className={styles.aspectName}>
            {t(ASPECT_KEY[aspect])}
          </span>
          <span className={styles.aspectControl}>
            <Rating
              aria-labelledby={labelId}
              name={`rating-${aspect}`}
              color="marigold"
              size="extra-large"
              value={field.value ?? 0}
              itemLabel={(count) => t('feedback.stars', { count })}
              onChange={(_, data) => field.onChange(data.value)}
            />
            {field.value ? (
              <Button
                appearance="subtle"
                size="small"
                onClick={() => field.onChange(null)}
                disabled={disabled}
                aria-label={`${t('feedback.star.clear')}: ${t(ASPECT_KEY[aspect])}`}
              >
                {t('feedback.nps.clear')}
              </Button>
            ) : null}
          </span>
        </div>
      )}
    />
  );
}
