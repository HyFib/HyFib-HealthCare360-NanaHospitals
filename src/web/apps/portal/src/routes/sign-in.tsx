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
import { createFileRoute, redirect } from '@tanstack/react-router';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { CheckingBrowser } from '@/components/CheckingBrowser';
import { PortalUnavailable } from '@/components/FeatureOff';
import { TextLink } from '@/components/links';
import { formatCountdown } from '@/i18n/format';
import { useLanguage, useT } from '@/i18n';
import { describeError, errorCode, fieldError } from '@/lib/errors';
import { usePortalFeatures } from '@/lib/features';
import { useFeatureRefusals } from '@/lib/featureSwitches';
import { useNow } from '@/lib/hooks';
import {
  isValidMobile,
  otpDeadline,
  resendCooldownSeconds,
  sanitizePhoneInput,
  secondsUntil,
  throttledWaitSeconds,
  toE164,
} from '@/lib/otp';
import { prepareProof, withProof } from '@/lib/proofOfWork';
import { safeRedirect } from '@/lib/redirect';
import { ensurePortalSession } from '@/lib/session';
import { IDLE_SIGN_OUT_MINUTES, isSessionEndReason, type SessionEndReason } from '@/lib/sessionEnd';
import { CodeStep, type CodeChallenge } from '@/modules/signIn/CodeStep';
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

function SignInPage() {
  const styles = useStyles();
  const t = useT();
  const { language } = useLanguage();
  const { redirect: target, ended } = Route.useSearch();
  const hospital = useHospitalName();
  // The hospital may have turned the portal (or video consultations) off: then there is nothing to sign in to.
  const features = usePortalFeatures();

  const [step, setStep] = useState<Step>('phone');
  const [phone, setPhone] = useState('');
  const [challenge, setChallenge] = useState<CodeChallenge | null>(null);
  const [sent, setSent] = useState(0);
  const [resendAt, setResendAt] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [sending, setSending] = useState(false);
  // The anti-automation check (SRS SEC-06) is solved in the background while the number is typed; the page says it is
  // checking only when that takes longer than usual.
  const [checking, setChecking] = useState(false);
  const phoneInput = useRef<HTMLInputElement>(null);
  // The clock runs on both steps. A wait can start on the number's step ("too many codes", or a resend wait carried back
  // by "Change number"); a clock stopped there would hold its countdown, and the Send code button, until a reload.
  const now = useNow(1000);

  useEffect(() => {
    if (features.portal) {
      prepareProof('otp');
    }
  }, [features.portal]);

  const digits = sanitizePhoneInput(phone);
  const resendIn = secondsUntil(resendAt, now);

  const requestCode = async (resend: boolean) => {
    if (!isValidMobile(digits)) {
      setError(t('auth.phone.invalid'));
      phoneInput.current?.focus();
      return;
    }
    setSending(true);
    setError(null);
    setNotice(null);
    try {
      const result = await withProof('otp', (headers) => auth.requestOtp(toE164(digits), { language, headers }), {
        subject: toE164(digits),
        onSlow: setChecking,
      });
      // Ready for the code, solved while the SMS arrives.
      prepareProof('otp');
      const receivedAt = Date.now();
      const count = sent + 1;
      setSent(count);
      setChallenge({
        id: result.challengeId,
        maskedPhone: result.maskedPhone,
        deadline: otpDeadline(result.expiresAt, receivedAt),
      });
      setResendAt(receivedAt + resendCooldownSeconds(count) * 1000);
      setStep('code');
      if (resend) {
        // The code step starts again for the new code (keyed by its challenge), with its field focused.
        setNotice(t('auth.resent'));
      }
    } catch (err) {
      if (isFeatureDisabled(err)) {
        useFeatureRefusals.getState().refuse(featureOf(err));
        return;
      }
      const throttled = errorCode(err) === 'auth.otp_throttled' || (isApiError(err) && err.status === 429);
      if (throttled) {
        setResendAt(Date.now() + throttledWaitSeconds(isApiError(err) ? err.data : undefined) * 1000);
      }
      setError(
        throttled
          ? t('auth.throttled')
          : fieldError(err, 'phone')
            ? t('auth.phone.invalid')
            : describeError(err, t).message,
      );
    } finally {
      setSending(false);
    }
  };

  const onPhoneSubmit = (event: FormEvent) => {
    event.preventDefault();
    void requestCode(false);
  };

  const changeNumber = () => {
    setStep('phone');
    setChallenge(null);
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
                      disabled={sending}
                    />
                  </Field>
                  <Button
                    type="submit"
                    appearance="primary"
                    size="large"
                    disabled={sending || resendIn > 0}
                    icon={sending ? <Spinner size="tiny" aria-hidden /> : undefined}
                  >
                    {sending
                      ? t('common.sending')
                      : resendIn > 0
                        ? t('auth.resendIn', { time: formatCountdown(resendIn * 1000) })
                        : t('auth.sendCode')}
                  </Button>
                  <CheckingBrowser active={checking} />
                  <Caption1 className={styles.muted}>
                    {t('auth.privacyNote')} <TextLink to="/privacy">{t('auth.privacyLink')}</TextLink>
                  </Caption1>
                  <Caption1 className={styles.muted}>
                    <TextLink to="/rates">{t('rates.link')}</TextLink>
                  </Caption1>
                </form>
              </>
            ) : (
              <CodeStep
                key={challenge.id}
                challenge={challenge}
                notice={notice}
                problem={error}
                onDismissProblem={() => setError(null)}
                resendIn={resendIn}
                sending={sending}
                checkingResend={checking}
                onResend={() => void requestCode(true)}
                onChangeNumber={changeNumber}
                onExpired={() => setResendAt((at) => Math.min(at, Date.now()))}
                redirect={target}
              />
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
