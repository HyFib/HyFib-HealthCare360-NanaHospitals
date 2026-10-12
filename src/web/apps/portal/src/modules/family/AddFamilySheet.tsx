import { Button, Field, Input, makeStyles, Select, Spinner, tokens } from '@fluentui/react-components';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Sheet } from '@/components/Sheet';
import { Notice } from '@/components/States';
import { useT } from '@/i18n';
import { describeError } from '@/lib/errors';
import { sanitizeCode, sanitizePhoneInput } from '@/lib/otp';
import { PROFILES_QUERY_KEY } from '@/lib/session';
import { useToast } from '@/lib/toast';
import type { FamilyCodeSent, FamilyRelation } from '@/lib/types';
import { familyApi, familyQueries } from './api';
import {
  checkAddFamily,
  CODE_LENGTH,
  codeMinutes,
  emptyAddFamily,
  FAMILY_RELATIONS,
  familyErrorKey,
  isCodeSpent,
  phoneEnding,
  RELATION_KEY,
  toCodeRequest,
  UHID_MAX,
  type AddFamilyValues,
} from './model';
import { useUnsavedChanges } from '@/lib/unsavedChanges';
import { sameValues } from '@hc360/ui';

const useStyles = makeStyles({
  lead: { margin: 0, color: tokens.colorNeutralForeground2 },
  form: { display: 'flex', flexDirection: 'column', gap: '16px' },
  prefix: { color: tokens.colorNeutralForeground3 },
  code: { letterSpacing: '0.3em', fontVariantNumeric: 'tabular-nums' },
});

type Problems = Partial<Record<keyof AddFamilyValues, string>>;

/**
 * Adds a family member who has their own mobile number (PP-01): their UHID, their number and how they are related,
 * then the code the hospital sent to their number, which they give you if they agree. The first answer is the same
 * whether or not the UHID and the number belong together, so the sheet never says which.
 */
export function AddFamilySheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  const styles = useStyles();
  const t = useT();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [values, setValues] = useState<AddFamilyValues>(emptyAddFamily);
  const [problems, setProblems] = useState<Problems>({});
  const [sent, setSent] = useState<{ codeId: string; minutes: number } | null>(null);
  const [code, setCode] = useState('');
  const [spent, setSpent] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<'send' | 'verify' | null>(null);

  const describe = (err: unknown) => {
    const key = familyErrorKey(err);
    return key ? t(key) : describeError(err, t, 'errors.saveTitle').message;
  };

  const close = () => {
    setValues(emptyAddFamily());
    setProblems({});
    setSent(null);
    setCode('');
    setSpent(false);
    setError(null);
    onClose();
  };
  // What was typed is not thrown away without asking (SRS UX-08).
  const guard = useUnsavedChanges({
    dirty: !sameValues(values, emptyAddFamily()) || code !== '',
    busy: busy !== null,
    open,
    onClose: close,
  });

  const update = <K extends keyof AddFamilyValues>(field: K, value: AddFamilyValues[K]) => {
    setValues((current) => ({ ...current, [field]: value }));
    setProblems((current) => ({ ...current, [field]: undefined }));
  };

  const send = async () => {
    const found = checkAddFamily(values);
    setProblems(Object.fromEntries(found.map((problem) => [problem.path, t(problem.key)])));
    if (found.length > 0) {
      return;
    }
    setBusy('send');
    setError(null);
    try {
      const answer: FamilyCodeSent = await familyApi.requestCode(toCodeRequest(values));
      setSent({ codeId: answer.codeId, minutes: codeMinutes(answer.expiresAt, Date.now()) });
      setCode('');
      setSpent(false);
    } catch (err) {
      setError(describe(err));
    } finally {
      setBusy(null);
    }
  };

  const verify = async () => {
    if (!sent || code.length !== CODE_LENGTH) {
      return;
    }
    setBusy('verify');
    setError(null);
    try {
      const relative = await familyApi.verifyCode(sent.codeId, code);
      toast.success(t('family.added', { name: relative.fullName }));
      // The new relative's records appear everywhere the portal lists the account's people.
      void queryClient.invalidateQueries({ queryKey: familyQueries.all });
      void queryClient.invalidateQueries({ queryKey: PROFILES_QUERY_KEY });
      close();
    } catch (err) {
      setSpent(isCodeSpent(err));
      setError(describe(err));
      setCode('');
    } finally {
      setBusy(null);
    }
  };

  // Back to the details, as typed, for a new code.
  const askAgain = () => {
    setSent(null);
    setCode('');
    setSpent(false);
    setError(null);
  };

  const footer = sent ? (
    <>
      <Button
        appearance="primary"
        size="large"
        onClick={() => void verify()}
        disabled={busy !== null || spent || code.length !== CODE_LENGTH}
        icon={busy === 'verify' ? <Spinner size="tiny" aria-hidden /> : undefined}
      >
        {busy === 'verify' ? t('auth.verifying') : t('family.code.verify')}
      </Button>
      <Button size="large" onClick={askAgain} disabled={busy !== null}>
        {t('family.code.again')}
      </Button>
    </>
  ) : (
    <>
      <Button
        appearance="primary"
        size="large"
        onClick={() => void send()}
        disabled={busy !== null}
        icon={busy === 'send' ? <Spinner size="tiny" aria-hidden /> : undefined}
      >
        {busy === 'send' ? t('common.sending') : t('family.add.send')}
      </Button>
      <Button size="large" onClick={guard.requestClose} disabled={busy !== null}>
        {t('common.notNow')}
      </Button>
    </>
  );

  return (
    <Sheet
      open={open}
      title={sent ? t('family.code.title') : t('family.add')}
      onClose={close}
      guard={guard}
      dismissible={busy === null}
      footer={footer}
    >
      {sent ? (
        <>
          <p className={styles.lead}>
            {t('family.code.lead', { ending: phoneEnding(values.phone), minutes: sent.minutes })}
          </p>
          {error && <Notice intent="error">{error}</Notice>}
          <form
            className={styles.form}
            noValidate
            onSubmit={(event) => {
              event.preventDefault();
              void verify();
            }}
          >
            <Field label={t('family.code.label')} required>
              <Input
                size="large"
                value={code}
                onChange={(_, data) => {
                  setCode(sanitizeCode(data.value));
                  setError(null);
                }}
                inputMode="numeric"
                // The code arrives on the relative's phone, not this one: no one-time-code autofill.
                autoComplete="off"
                input={{ className: styles.code, maxLength: 12 }}
                disabled={busy !== null || spent}
              />
            </Field>
          </form>
        </>
      ) : (
        <>
          <p className={styles.lead}>{t('family.add.lead')}</p>
          {error && <Notice intent="error">{error}</Notice>}
          <form
            className={styles.form}
            noValidate
            onSubmit={(event) => {
              event.preventDefault();
              void send();
            }}
          >
            <Field
              label={t('family.add.uhid')}
              hint={t('family.add.uhid.hint')}
              required
              validationState={problems.uhid ? 'error' : 'none'}
              validationMessage={problems.uhid}
            >
              <Input
                size="large"
                value={values.uhid}
                onChange={(_, data) => update('uhid', data.value)}
                maxLength={UHID_MAX}
                autoComplete="off"
                autoCapitalize="characters"
                disabled={busy !== null}
              />
            </Field>
            <Field
              label={t('family.add.phone')}
              hint={t('family.add.phone.hint')}
              required
              validationState={problems.phone ? 'error' : 'none'}
              validationMessage={problems.phone}
            >
              <Input
                size="large"
                type="tel"
                inputMode="numeric"
                autoComplete="off"
                value={values.phone}
                onChange={(_, data) => update('phone', sanitizePhoneInput(data.value))}
                contentBefore={
                  <span aria-hidden className={styles.prefix}>
                    {t('auth.phone.prefix')}
                  </span>
                }
                disabled={busy !== null}
              />
            </Field>
            <Field
              label={t('family.add.relation')}
              required
              validationState={problems.relation ? 'error' : 'none'}
              validationMessage={problems.relation}
            >
              <Select
                size="large"
                value={values.relation}
                onChange={(_, data) => update('relation', data.value as FamilyRelation | '')}
                disabled={busy !== null}
              >
                <option value="">{t('family.add.relation.choose')}</option>
                {FAMILY_RELATIONS.map((relation) => (
                  <option key={relation} value={relation}>
                    {t(RELATION_KEY[relation])}
                  </option>
                ))}
              </Select>
            </Field>
          </form>
        </>
      )}
    </Sheet>
  );
}
