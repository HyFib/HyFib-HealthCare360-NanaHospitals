import {
  Button,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { auth, featureOf, isApiError, isFeatureDisabled } from '@hc360/core';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useRef, useState, type FormEvent } from 'react';
import { CheckingBrowser } from '@/components/CheckingBrowser';
import { formatCountdown } from '@/i18n/format';
import { useLanguage, useT } from '@/i18n';
import { describeError, errorCode, fieldError } from '@/lib/errors';
import { useFeatureRefusals } from '@/lib/featureSwitches';
import { useNow } from '@/lib/hooks';
import { OTP_LENGTH, sanitizeCode, secondsUntil } from '@/lib/otp';
import { useProfileStore } from '@/lib/profileStore';
import { prepareProof, withProof } from '@/lib/proofOfWork';
import { safeRedirect } from '@/lib/redirect';
import { profileQueries } from '@/modules/profiles/api';

const useStyles = makeStyles({
  heading: { display: 'flex', flexDirection: 'column', gap: '6px' },
  title: {
    margin: 0,
    fontSize: tokens.fontSizeBase600,
    lineHeight: tokens.lineHeightBase600,
    fontWeight: tokens.fontWeightSemibold,
  },
  muted: { color: tokens.colorNeutralForeground2 },
  form: { display: 'flex', flexDirection: 'column', gap: '16px' },
  code: { letterSpacing: '0.5em', fontSize: '24px', textAlign: 'center', fontVariantNumeric: 'tabular-nums' },
  row: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
});

/** A sign-in code sent: the challenge the code is checked against, the number masked, and when it expires here. */
export interface CodeChallenge {
  id: string;
  maskedPhone: string;
  /** When the code stops working, on this device's clock (lib/otp `otpDeadline`). */
  deadline: number;
}

interface CodeStepProps {
  challenge: CodeChallenge;
  /** Above the form: a new code is on its way. */
  notice: string | null;
  /** Why asking for a new code failed (too many codes…), shown on the field until the patient types. */
  problem: string | null;
  onDismissProblem: () => void;
  /** Seconds before another code may be asked for. */
  resendIn: number;
  /** A new code is being asked for. */
  sending: boolean;
  /** The anti-automation check for the new code takes longer than usual. */
  checkingResend: boolean;
  onResend: () => void;
  /** Back to the number (the sign-in page); a link to the portal sends codes to one number only, so it has none. */
  onChangeNumber?: () => void;
  /** The code expired or was tried too often: another may be asked for at once. */
  onExpired: () => void;
  /** Where the sign-in goes once the code is right (the sign-in page's `redirect`). */
  redirect?: string;
  /** Called once the code is right, before the patient is taken on (a link forgets its token). */
  onSignedIn?: () => void;
  /** The step's heading: the page's own (h1) on the sign-in page, under the page's title (h2) elsewhere. */
  headingLevel?: 'h1' | 'h2';
}

/**
 * The second step of signing in to the portal: the 6-digit code sent by SMS, checked with the portal's own sign-in
 * (`POST /api/v1/auth/otp/verify`, with a proof of work), then on to where the sign-in goes, with the family member to
 * show chosen. The sign-in page shows it once a code is sent to the number typed, and a link to the portal sent to the
 * patient's number once a code is sent through the link: the link itself signs no one in. Mount it with the challenge's
 * id as its key, so a new code starts from an empty field.
 */
export function CodeStep({
  challenge,
  notice,
  problem,
  onDismissProblem,
  resendIn,
  sending,
  checkingResend,
  onResend,
  onChangeNumber,
  onExpired,
  redirect,
  onSignedIn,
  headingLevel = 'h1',
}: CodeStepProps) {
  const styles = useStyles();
  const t = useT();
  const { language } = useLanguage();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [verifying, setVerifying] = useState(false);
  // The anti-automation check (SRS SEC-06) is solved in the background while the SMS arrives; the page says it is
  // checking only when that takes longer than usual.
  const [checking, setChecking] = useState(false);
  const codeInput = useRef<HTMLInputElement>(null);
  const now = useNow(1000);
  const expiresIn = secondsUntil(challenge.deadline, now);
  const busy = verifying || sending;
  const shown = error ?? problem;
  const Heading = headingLevel;

  const verify = async (value: string) => {
    if (value.length !== OTP_LENGTH) {
      setError(t('auth.code.incomplete'));
      codeInput.current?.focus();
      return;
    }
    setVerifying(true);
    setError(null);
    onDismissProblem();
    try {
      // No number with the code's proof: the server asks the address's work only, and finds the number from the
      // challenge when the code is wrong.
      await withProof('otp', (headers) => auth.verifyOtp(challenge.id, value, { language, headers }), {
        onSlow: setChecking,
      });
      // A new person may be signing in on this tab: nothing cached from an earlier session may be shown.
      queryClient.removeQueries({ queryKey: ['portal'] });
      let profiles: { id: string }[] = [];
      try {
        profiles = await queryClient.fetchQuery(profileQueries.list());
      } catch {
        // The signed-in layout loads (and retries) the family list itself.
      }
      const store = useProfileStore.getState();
      if (!profiles.some((profile) => profile.id === store.profileId)) {
        store.select(profiles.length === 1 ? (profiles[0]?.id ?? null) : null);
      }
      onSignedIn?.();
      await navigate({ href: safeRedirect(redirect), replace: true });
    } catch (err) {
      if (isFeatureDisabled(err)) {
        useFeatureRefusals.getState().refuse(featureOf(err));
        return;
      }
      setCode('');
      const expired = errorCode(err) === 'auth.challenge_expired' || (isApiError(err) && err.status === 401);
      setError(
        expired
          ? t('auth.code.expired')
          : fieldError(err, 'code')
            ? t('auth.code.wrong')
            : isApiError(err) && err.status === 429
              ? t('auth.throttled')
              : describeError(err, t).message,
      );
      if (expired) {
        onExpired();
      }
      prepareProof('otp');
      codeInput.current?.focus();
    } finally {
      setVerifying(false);
    }
  };

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    void verify(code);
  };

  return (
    <>
      <div className={styles.heading}>
        <Heading className={styles.title}>{t('auth.code.title')}</Heading>
        <Text className={styles.muted}>{t('auth.code.sentTo', { phone: challenge.maskedPhone })}</Text>
      </div>
      {notice && (
        <MessageBar intent="success">
          <MessageBarBody>{notice}</MessageBarBody>
        </MessageBar>
      )}
      <form className={styles.form} onSubmit={onSubmit} noValidate>
        <Field
          label={t('auth.code.label')}
          required
          validationState={shown ? 'error' : 'none'}
          validationMessage={shown ?? undefined}
          hint={
            expiresIn > 0
              ? t('auth.code.expiresIn', { time: formatCountdown(expiresIn * 1000) })
              : t('auth.code.expiredShort')
          }
        >
          <Input
            ref={codeInput}
            value={code}
            onChange={(_, data) => {
              const next = sanitizeCode(data.value);
              setCode(next);
              setError(null);
              onDismissProblem();
              if (next.length === OTP_LENGTH && !busy) {
                void verify(next);
              }
            }}
            inputMode="numeric"
            autoComplete="one-time-code"
            autoFocus
            size="large"
            input={{ className: styles.code, maxLength: 12 }}
            disabled={verifying}
          />
        </Field>
        <Button
          type="submit"
          appearance="primary"
          size="large"
          disabled={busy || code.length !== OTP_LENGTH}
          icon={verifying ? <Spinner size="tiny" aria-hidden /> : undefined}
        >
          {verifying ? t('auth.verifying') : t('auth.verify')}
        </Button>
        <CheckingBrowser active={checking || checkingResend} />
        <div className={styles.row}>
          {onChangeNumber ? (
            <Button appearance="subtle" onClick={onChangeNumber} disabled={busy}>
              {t('auth.changeNumber')}
            </Button>
          ) : (
            <span />
          )}
          <Button appearance="secondary" onClick={onResend} disabled={busy || resendIn > 0}>
            {resendIn > 0 ? t('auth.resendIn', { time: formatCountdown(resendIn * 1000) }) : t('auth.resend')}
          </Button>
        </div>
      </form>
    </>
  );
}
