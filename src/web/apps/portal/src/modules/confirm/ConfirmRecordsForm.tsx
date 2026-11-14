import { Button, Field, Input, makeStyles, Spinner, tokens } from '@fluentui/react-components';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ChoiceCards } from '@/components/ChoiceCards';
import { Notice } from '@/components/States';
import { useT } from '@/i18n';
import { istDateOf } from '@/i18n/format';
import { describeError } from '@/lib/errors';
import { PROFILES_QUERY_KEY } from '@/lib/session';
import { useToast } from '@/lib/toast';
import type { RecordsToConfirm } from '@/lib/types';
import { confirmApi, confirmQueries } from './api';
import {
  checkConfirm,
  confirmErrorKey,
  emptyConfirm,
  refusalNumber,
  toConfirmRequest,
  UHID_MAX,
  waitOf,
  type ConfirmMethod,
  type ConfirmProblem,
  type ConfirmValues,
} from './model';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '16px' },
  lead: { margin: 0, color: tokens.colorNeutralForeground2 },
  date: { display: 'flex', flexDirection: 'column', gap: '6px', border: 'none', margin: 0, padding: 0, minWidth: 0 },
  legend: { padding: 0, marginBottom: '4px', fontWeight: tokens.fontWeightSemibold },
  parts: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr) minmax(0, 1.6fr)', gap: '8px' },
  hint: { margin: 0, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  problem: { margin: 0, color: tokens.colorPaletteRedForeground1, fontSize: tokens.fontSizeBase200 },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
});

/**
 * "Confirm your details" (ASVS V6.6.1): records on this mobile number that nobody has verified are shown only once the
 * account gives the patient's date of birth, or the UHID on their hospital card. Each record the details match is shown
 * from then on. Wrong details count against the number (five tries a day); the form says how many are left, and how long
 * to wait once none are. Nothing about a record is shown before it is confirmed, not even a name.
 */
export function ConfirmRecordsForm({ status, onConfirmed }: { status: RecordsToConfirm; onConfirmed?: () => void }) {
  const styles = useStyles();
  const t = useT();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [values, setValues] = useState<ConfirmValues>(emptyConfirm);
  const [problem, setProblem] = useState<ConfirmProblem | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [attemptsLeft, setAttemptsLeft] = useState<number | null>(null);
  const [retryAfter, setRetryAfter] = useState<number | null>(status.retryAfterSeconds);
  const [busy, setBusy] = useState(false);

  const update = <K extends keyof ConfirmValues>(field: K, value: ConfirmValues[K]) => {
    setValues((current) => ({ ...current, [field]: value }));
    setProblem(null);
    setError(null);
  };

  const submit = async () => {
    const found = checkConfirm(values, istDateOf(Date.now()));
    setProblem(found[0] ?? null);
    if (found.length > 0) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const done = await confirmApi.confirm(toConfirmRequest(values));
      toast.success(done.confirmed === 1 ? t('confirm.done.one') : t('confirm.done.many', { count: done.confirmed }));
      setValues(emptyConfirm());
      setAttemptsLeft(null);
      // The confirmed records are the account's now, everywhere the portal lists its people.
      void queryClient.invalidateQueries({ queryKey: PROFILES_QUERY_KEY });
      void queryClient.invalidateQueries({ queryKey: confirmQueries.all });
      onConfirmed?.();
    } catch (err) {
      const key = confirmErrorKey(err);
      setError(key ? t(key) : describeError(err, t, 'errors.saveTitle').message);
      const left = refusalNumber(err, 'attemptsLeft');
      setAttemptsLeft(left ?? null);
      const wait = refusalNumber(err, 'retryAfterSeconds');
      if (wait) {
        setRetryAfter(wait);
      }
    } finally {
      setBusy(false);
    }
  };

  const stopped = retryAfter !== null && retryAfter > 0;
  const wait = stopped ? waitOf(retryAfter) : null;
  const methods: { value: ConfirmMethod; title: string }[] = [
    { value: 'dateOfBirth', title: t('confirm.method.dateOfBirth') },
    { value: 'uhid', title: t('confirm.method.uhid') },
  ];

  return (
    <form
      className={styles.form}
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        void submit();
      }}
    >
      <p className={styles.lead}>
        {status.unconfirmed === 1 ? t('confirm.lead.one') : t('confirm.lead.many', { count: status.unconfirmed })}
      </p>
      {error && <Notice intent="error">{error}</Notice>}
      {stopped && wait ? (
        <Notice intent="warning">
          {t('confirm.error.throttled')} {t(wait.key, { count: wait.count })}
        </Notice>
      ) : (
        <>
          <ChoiceCards
            label={t('confirm.method')}
            value={values.method}
            options={methods}
            onChange={(method) => update('method', method)}
            twoUp
          />
          {values.method === 'dateOfBirth' ? (
            <fieldset className={styles.date}>
              <legend className={styles.legend}>{t('confirm.dob.label')}</legend>
              <div className={styles.parts}>
                <Field label={t('confirm.dob.day')}>
                  <Input
                    size="large"
                    value={values.day}
                    inputMode="numeric"
                    autoComplete="off"
                    input={{ maxLength: 2 }}
                    onChange={(_, data) => update('day', data.value.replace(/\D/g, ''))}
                  />
                </Field>
                <Field label={t('confirm.dob.month')}>
                  <Input
                    size="large"
                    value={values.month}
                    inputMode="numeric"
                    autoComplete="off"
                    input={{ maxLength: 2 }}
                    onChange={(_, data) => update('month', data.value.replace(/\D/g, ''))}
                  />
                </Field>
                <Field label={t('confirm.dob.year')}>
                  <Input
                    size="large"
                    value={values.year}
                    inputMode="numeric"
                    autoComplete="off"
                    input={{ maxLength: 4 }}
                    onChange={(_, data) => update('year', data.value.replace(/\D/g, ''))}
                  />
                </Field>
              </div>
              {problem?.path === 'dateOfBirth' ? (
                <p className={styles.problem} role="alert">
                  {t(problem.key)}
                </p>
              ) : (
                <p className={styles.hint}>{t('confirm.dob.hint')}</p>
              )}
            </fieldset>
          ) : (
            <Field
              label={t('confirm.uhid.label')}
              hint={t('confirm.uhid.hint')}
              validationState={problem?.path === 'uhid' ? 'error' : 'none'}
              validationMessage={problem?.path === 'uhid' ? t(problem.key) : undefined}
            >
              <Input
                size="large"
                value={values.uhid}
                autoComplete="off"
                autoCapitalize="characters"
                input={{ maxLength: UHID_MAX }}
                onChange={(_, data) => update('uhid', data.value)}
              />
            </Field>
          )}
          {attemptsLeft !== null && attemptsLeft > 0 && (
            <p className={styles.hint}>{t('confirm.attemptsLeft', { count: attemptsLeft })}</p>
          )}
          <div className={styles.actions}>
            <Button
              type="submit"
              appearance="primary"
              size="large"
              disabled={busy}
              icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
            >
              {busy ? t('confirm.submitting') : t('confirm.submit')}
            </Button>
          </div>
        </>
      )}
      <p className={styles.hint}>{t('confirm.frontDesk')}</p>
    </form>
  );
}
