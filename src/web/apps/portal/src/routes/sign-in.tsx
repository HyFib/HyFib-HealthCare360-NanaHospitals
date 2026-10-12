import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  CalendarLtrRegular,
  DocumentTextRegular,
  LockClosedRegular,
  PeopleRegular,
  PhoneRegular,
  QrCodeRegular,
  VideoRegular,
} from '@fluentui/react-icons';
import { auth, featureOf, isApiError, isFeatureDisabled } from '@hc360/core';
import { useQueryClient } from '@tanstack/react-query';
import { createFileRoute, redirect, useNavigate } from '@tanstack/react-router';
import { useRef, useState, type FormEvent } from 'react';
import { PortalUnavailable } from '@/components/FeatureOff';
import { TextLink } from '@/components/links';
import { formatCountdown } from '@/i18n/format';
import { useT } from '@/i18n';
import { describeError, errorCode, fieldError } from '@/lib/errors';
import { usePortalFeatures } from '@/lib/features';
import { useFeatureRefusals } from '@/lib/featureSwitches';
import { useNow } from '@/lib/hooks';
import {
  isValidMobile,
  OTP_LENGTH,
  otpDeadline,
  resendCooldownSeconds,
  sanitizeCode,
  sanitizePhoneInput,
  secondsUntil,
  THROTTLED_COOLDOWN_SECONDS,
  toE164,
} from '@/lib/otp';
import { useProfileStore } from '@/lib/profileStore';
import { safeRedirect } from '@/lib/redirect';
import { ensurePortalSession } from '@/lib/session';
import { IDLE_SIGN_OUT_MINUTES, isSessionEndReason, type SessionEndReason } from '@/lib/sessionEnd';
import { profileQueries } from '@/modules/profiles/api';
import { LogoMark } from '@/shell/BrandMark';
import { useHospitalName } from '@/shell/hooks';
import { LanguageChoice } from '@/shell/LanguageMenu';
import { ConnectionBanner } from '@/shell/ConnectionBanner';

interface SignInSearch {
  redirect?: string;
  ended?: SessionEndReason;
}

export const Route = createFileRoute('/sign-in')({
  validateSearch: (search: Record<string, unknown>): SignInSearch => ({
    redirect: typeof search.redirect === 'string' ? search.redirect : undefined,
    ended: isSessionEndReason(search.ended) ? search.ended : undefined,
  }),
  beforeLoad: async ({ search }) => {
    const state = await ensurePortalSession();
    if (state === 'patient') {
      throw redirect({ href: safeRedirect(search.redirect), replace: true });
    }
  },
  component: SignInPage,
});

const useStyles = makeStyles({
  root: {
    minHeight: '100dvh',
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr)',
    backgroundColor: tokens.colorNeutralBackground2,
    '@media (min-width: 960px)': { gridTemplateColumns: 'minmax(0, 1.05fr) minmax(420px, 1fr)' },
  },
  brand: {
    display: 'none',
    '@media (min-width: 960px)': {
      display: 'flex',
      flexDirection: 'column',
      justifyContent: 'space-between',
      gap: '32px',
      padding: '48px',
      color: '#fff',
      // Brand 50 to 70, as on the staff sign-in page: white text keeps at least 5.8:1 everywhere on it, and the 85–90 %
      // white below at least 4.6:1 (WCAG AA). The gradient used to end in brand 100, where white text had 3.2:1.
      background: 'linear-gradient(150deg, #064F6B 0%, #045E80 55%, #016D95 100%)',
    },
  },
  brandTop: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '12px',
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  brandTitle: { margin: 0, fontSize: '40px', lineHeight: '48px', fontWeight: tokens.fontWeightSemibold },
  brandText: {
    margin: 0,
    maxWidth: '520px',
    color: 'rgba(255,255,255,0.9)',
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
  },
  benefits: { listStyle: 'none', margin: 0, padding: 0, display: 'grid', gap: '14px' },
  benefit: { display: 'flex', alignItems: 'center', gap: '12px', fontSize: tokens.fontSizeBase400 },
  benefitIcon: {
    width: '40px',
    height: '40px',
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: 'rgba(255,255,255,0.16)',
    fontSize: '22px',
    flexShrink: 0,
  },
  brandFooter: { color: 'rgba(255,255,255,0.85)', fontSize: tokens.fontSizeBase300 },
  panel: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  panelInner: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '16px',
    padding: '20px 16px 32px',
    '@media (min-width: 960px)': { justifyContent: 'center', padding: '40px 32px' },
  },
  topBar: {
    width: '100%',
    maxWidth: '440px',
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
  },
  mobileBrand: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
    '@media (min-width: 960px)': { visibility: 'hidden' },
  },
  card: {
    width: '100%',
    maxWidth: '440px',
    boxSizing: 'border-box',
    display: 'flex',
    flexDirection: 'column',
    gap: '18px',
    padding: '24px 20px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusXLarge,
    boxShadow: tokens.shadow16,
    '@media (min-width: 480px)': { padding: '32px' },
  },
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
  small: { color: tokens.colorNeutralForeground3, display: 'flex', alignItems: 'center', gap: '6px' },
  links: { display: 'flex', flexDirection: 'column', gap: '10px', alignItems: 'flex-start' },
  linkRow: { display: 'inline-flex', alignItems: 'center', gap: '8px', minHeight: '32px' },
  emergency: {
    width: '100%',
    maxWidth: '440px',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
    textAlign: 'center',
  },
});

type Step = 'phone' | 'code';

interface Challenge {
  id: string;
  maskedPhone: string;
  deadline: number;
}

function SignInPage() {
  const styles = useStyles();
  const t = useT();
  const { redirect: target, ended } = Route.useSearch();
  const hospital = useHospitalName();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  // The hospital may have turned the portal (or video consultations) off: then there is nothing to sign in to.
  const features = usePortalFeatures();

  const [step, setStep] = useState<Step>('phone');
  const [phone, setPhone] = useState('');
  const [challenge, setChallenge] = useState<Challenge | null>(null);
  const [sent, setSent] = useState(0);
  const [resendAt, setResendAt] = useState(0);
  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState<'send' | 'verify' | null>(null);
  const codeInput = useRef<HTMLInputElement>(null);
  const phoneInput = useRef<HTMLInputElement>(null);
  const now = useNow(1000, step === 'code');

  const digits = sanitizePhoneInput(phone);
  const resendIn = secondsUntil(resendAt, now);
  const expiresIn = challenge ? secondsUntil(challenge.deadline, now) : 0;

  const requestCode = async (resend: boolean) => {
    if (!isValidMobile(digits)) {
      setError(t('auth.phone.invalid'));
      phoneInput.current?.focus();
      return;
    }
    setBusy('send');
    setError(null);
    setNotice(null);
    try {
      const result = await auth.requestOtp(toE164(digits));
      const receivedAt = Date.now();
      const count = sent + 1;
      setSent(count);
      setChallenge({
        id: result.challengeId,
        maskedPhone: result.maskedPhone,
        deadline: otpDeadline(result.expiresAt, receivedAt),
      });
      setResendAt(receivedAt + resendCooldownSeconds(count) * 1000);
      setCode('');
      setStep('code');
      if (resend) {
        setNotice(t('auth.resent'));
        codeInput.current?.focus();
      }
    } catch (err) {
      if (isFeatureDisabled(err)) {
        useFeatureRefusals.getState().refuse(featureOf(err));
        return;
      }
      const throttled = errorCode(err) === 'auth.otp_throttled' || (isApiError(err) && err.status === 429);
      if (throttled) {
        setResendAt(Date.now() + THROTTLED_COOLDOWN_SECONDS * 1000);
      }
      setError(
        throttled
          ? t('auth.throttled')
          : fieldError(err, 'phone')
            ? t('auth.phone.invalid')
            : describeError(err, t).message,
      );
    } finally {
      setBusy(null);
    }
  };

  const verify = async (value: string) => {
    if (!challenge) {
      return;
    }
    if (value.length !== OTP_LENGTH) {
      setError(t('auth.code.incomplete'));
      codeInput.current?.focus();
      return;
    }
    setBusy('verify');
    setError(null);
    setNotice(null);
    try {
      await auth.verifyOtp(challenge.id, value);
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
      await navigate({ href: safeRedirect(target), replace: true });
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
        setResendAt(Math.min(resendAt, Date.now()));
      }
      codeInput.current?.focus();
    } finally {
      setBusy(null);
    }
  };

  const onPhoneSubmit = (event: FormEvent) => {
    event.preventDefault();
    void requestCode(false);
  };

  const onCodeSubmit = (event: FormEvent) => {
    event.preventDefault();
    void verify(code);
  };

  const changeNumber = () => {
    setStep('phone');
    setChallenge(null);
    setCode('');
    setError(null);
    setNotice(null);
  };

  const endedNotice =
    ended === 'idle'
      ? t('shell.idleSignedOut', { minutes: IDLE_SIGN_OUT_MINUTES })
      : ended === 'signed-out'
        ? t('shell.signedOut')
        : null;

  return (
    <div className={styles.root}>
      <section className={styles.brand} aria-hidden="true">
        <span className={styles.brandTop}>
          <LogoMark size={44} />
          {t('app.name')}
        </span>
        <div style={{ display: 'grid', gap: 20 }}>
          <h2 className={styles.brandTitle}>{hospital}</h2>
          <p className={styles.brandText}>{t('auth.intro')}</p>
          <ul className={styles.benefits}>
            <li className={styles.benefit}>
              <span className={styles.benefitIcon}>
                <CalendarLtrRegular />
              </span>
              {t('nav.appointments')} · {t('nav.findDoctor')}
            </li>
            {features.video && (
              <li className={styles.benefit}>
                <span className={styles.benefitIcon}>
                  <VideoRegular />
                </span>
                {t('nav.video')}
              </li>
            )}
            <li className={styles.benefit}>
              <span className={styles.benefitIcon}>
                <DocumentTextRegular />
              </span>
              {t('nav.reports')} · {t('nav.prescriptions')} · {t('nav.bills')}
            </li>
            <li className={styles.benefit}>
              <span className={styles.benefitIcon}>
                <PeopleRegular />
              </span>
              {t('shell.profile.hint')}
            </li>
          </ul>
        </div>
        <span className={styles.brandFooter}>{t('shell.emergency')}</span>
      </section>

      <div className={styles.panel}>
        <ConnectionBanner />
        <main className={styles.panelInner} id="main">
          <div className={styles.topBar}>
            <span className={styles.mobileBrand}>
              <LogoMark size={36} />
              <span>{hospital}</span>
            </span>
            <LanguageChoice />
          </div>

          <div className={styles.card}>
            {!features.portal ? (
              <>
                <h1 className={styles.title}>{t('app.fullName', { hospital })}</h1>
                <PortalUnavailable />
              </>
            ) : step === 'phone' || !challenge ? (
              <>
                <div className={styles.heading}>
                  <h1 className={styles.title}>{t('auth.welcome', { hospital })}</h1>
                  <Text className={styles.muted}>{t('auth.noPassword')}</Text>
                </div>
                {endedNotice && (
                  <MessageBar intent="info" layout="multiline">
                    <MessageBarBody>{endedNotice}</MessageBarBody>
                  </MessageBar>
                )}
                <form className={styles.form} onSubmit={onPhoneSubmit} noValidate>
                  <Field
                    label={t('auth.phone.label')}
                    hint={t('auth.phone.hint')}
                    required
                    validationState={error ? 'error' : 'none'}
                    validationMessage={error ?? undefined}
                  >
                    <Input
                      ref={phoneInput}
                      value={phone}
                      onChange={(_, data) => {
                        setPhone(sanitizePhoneInput(data.value));
                        setError(null);
                      }}
                      type="tel"
                      inputMode="numeric"
                      autoComplete="tel-national"
                      size="large"
                      contentBefore={
                        <span aria-hidden className={styles.small}>
                          <PhoneRegular />
                          {t('auth.phone.prefix')}
                        </span>
                      }
                      disabled={busy !== null}
                    />
                  </Field>
                  <Button
                    type="submit"
                    appearance="primary"
                    size="large"
                    disabled={busy !== null || resendIn > 0}
                    icon={busy === 'send' ? <Spinner size="tiny" /> : undefined}
                  >
                    {busy === 'send'
                      ? t('common.sending')
                      : resendIn > 0
                        ? t('auth.resendIn', { time: formatCountdown(resendIn * 1000) })
                        : t('auth.sendCode')}
                  </Button>
                  <Caption1 className={styles.muted}>
                    {t('auth.privacyNote')} <TextLink to="/privacy">{t('auth.privacyLink')}</TextLink>
                  </Caption1>
                </form>
              </>
            ) : (
              <>
                <div className={styles.heading}>
                  <h1 className={styles.title}>{t('auth.code.title')}</h1>
                  <Text className={styles.muted}>{t('auth.code.sentTo', { phone: challenge.maskedPhone })}</Text>
                </div>
                {notice && (
                  <MessageBar intent="success">
                    <MessageBarBody>{notice}</MessageBarBody>
                  </MessageBar>
                )}
                <form className={styles.form} onSubmit={onCodeSubmit} noValidate>
                  <Field
                    label={t('auth.code.label')}
                    required
                    validationState={error ? 'error' : 'none'}
                    validationMessage={error ?? undefined}
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
                        if (next.length === OTP_LENGTH && busy === null) {
                          void verify(next);
                        }
                      }}
                      inputMode="numeric"
                      autoComplete="one-time-code"
                      autoFocus
                      size="large"
                      input={{ className: styles.code, maxLength: 12 }}
                      disabled={busy === 'verify'}
                    />
                  </Field>
                  <Button
                    type="submit"
                    appearance="primary"
                    size="large"
                    disabled={busy !== null || code.length !== OTP_LENGTH}
                    icon={busy === 'verify' ? <Spinner size="tiny" /> : undefined}
                  >
                    {busy === 'verify' ? t('auth.verifying') : t('auth.verify')}
                  </Button>
                  <div className={styles.row}>
                    <Button appearance="subtle" onClick={changeNumber} disabled={busy !== null}>
                      {t('auth.changeNumber')}
                    </Button>
                    <Button
                      appearance="secondary"
                      onClick={() => void requestCode(true)}
                      disabled={busy !== null || resendIn > 0}
                    >
                      {resendIn > 0 ? t('auth.resendIn', { time: formatCountdown(resendIn * 1000) }) : t('auth.resend')}
                    </Button>
                  </div>
                </form>
              </>
            )}
            {features.portal && (
              <div className={styles.links}>
                <Caption1 className={styles.small}>
                  <LockClosedRegular aria-hidden />
                  {t('auth.secure')}
                </Caption1>
                <span className={styles.linkRow}>
                  <QrCodeRegular aria-hidden />
                  <TextLink to="/verify">{t('auth.checkDocument')}</TextLink>
                </span>
              </div>
            )}
          </div>
          <p className={styles.emergency}>{t('shell.emergency')}</p>
        </main>
      </div>
    </div>
  );
}
