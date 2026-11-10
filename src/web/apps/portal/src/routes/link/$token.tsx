import { Button, makeStyles, Spinner, Text, tokens } from '@fluentui/react-components';
import { PersonRegular, PhoneRegular } from '@fluentui/react-icons';
import { featureOf, isApiError, isFeatureDisabled } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, redirect } from '@tanstack/react-router';
import { useEffect, useMemo, useState } from 'react';
import { CheckingBrowser } from '@/components/CheckingBrowser';
import { PortalUnavailable } from '@/components/FeatureOff';
import { ButtonLink, TextLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, Notice, PageSpinner } from '@/components/States';
import { formatCountdown } from '@/i18n/format';
import { useT } from '@/i18n';
import { describeError, errorCode } from '@/lib/errors';
import { usePortalFeatures } from '@/lib/features';
import { useFeatureRefusals } from '@/lib/featureSwitches';
import { useNow } from '@/lib/hooks';
import { forgetLinkToken, keepLinkToken, linkTokenFor, TOKEN_PLACEHOLDER } from '@/lib/linkToken';
import { otpDeadline, resendCooldownSeconds, secondsUntil, throttledWaitSeconds } from '@/lib/otp';
import { prepareProof } from '@/lib/proofOfWork';
import { safeRedirect } from '@/lib/redirect';
import { ensurePortalSession } from '@/lib/session';
import { publicPortalLinkApi, publicPortalLinkQueries } from '@/modules/signIn/api';
import { CodeStep, type CodeChallenge } from '@/modules/signIn/CodeStep';
import { PortalFrame } from '@/shell/PortalFrame';

/**
 * A link to the portal that the hospital sent to the patient's registered number by SMS or WhatsApp:
 * {portal}/portal/link/{token}. The page shows the hospital and the number masked; "Send code" sends a sign-in code to
 * that number, and the code signs the patient in as on the sign-in page (the same code step). So the link is no way in
 * by itself: only the person with the phone gets the code. Opening the page changes nothing (SMS apps and link scanners
 * open links by themselves). The token is taken out of the address bar first, and forgotten once the patient is in.
 */
export const Route = createFileRoute('/link/$token')({
  beforeLoad: async ({ params }) => {
    if (params.token !== TOKEN_PLACEHOLDER && keepLinkToken('portal', params.token)) {
      throw redirect({ to: '/link/$token', params: { token: TOKEN_PLACEHOLDER }, replace: true });
    }
    // Signed in already on this phone: the link has nothing to add, so on to where the sign-in goes.
    if ((await ensurePortalSession()) === 'patient') {
      forgetLinkToken('portal');
      throw redirect({ href: safeRedirect(undefined), replace: true });
    }
  },
  component: PortalLinkPage,
});

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'stretch',
    gap: '16px',
    padding: '20px 16px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    '@media (min-width: 480px)': { padding: '24px' },
  },
  hospital: { margin: 0, color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase400 },
  number: { display: 'flex', alignItems: 'center', gap: '10px', fontSize: tokens.fontSizeBase400 },
  icon: { fontSize: '24px', color: tokens.colorBrandForeground1, flexShrink: 0 },
  muted: { color: tokens.colorNeutralForeground2 },
});

function PortalLinkPage() {
  const styles = useStyles();
  const t = useT();
  const { token: param } = Route.useParams();
  const features = usePortalFeatures();
  const token = useMemo(() => linkTokenFor('portal', param), [param]);
  const link = useQuery({ ...publicPortalLinkQueries.link(token ?? ''), enabled: token !== null });
  const [challenge, setChallenge] = useState<CodeChallenge | null>(null);
  const [sent, setSent] = useState(0);
  const [resendAt, setResendAt] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [sending, setSending] = useState(false);
  // The anti-automation check (SRS SEC-06), solved while the page is read; said only when it takes longer than usual.
  const [checking, setChecking] = useState(false);
  // The link stopped working after the page opened (a newer one was sent, it expired): the code request says so.
  const [gone, setGone] = useState(false);
  const now = useNow(1000);
  const resendIn = secondsUntil(resendAt, now);

  const ready = features.portal && Boolean(link.data) && !gone;
  useEffect(() => {
    if (ready) {
      prepareProof('otp');
    }
  }, [ready]);

  const requestCode = async (resend: boolean) => {
    if (!token) {
      return;
    }
    setSending(true);
    setError(null);
    setNotice(null);
    try {
      const result = await publicPortalLinkApi.requestCode(token, setChecking);
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
      if (resend) {
        setNotice(t('auth.resent'));
      }
    } catch (err) {
      if (isFeatureDisabled(err)) {
        useFeatureRefusals.getState().refuse(featureOf(err));
        return;
      }
      if (isApiError(err) && err.status === 404) {
        setGone(true);
        return;
      }
      const throttled = errorCode(err) === 'auth.otp_throttled' || (isApiError(err) && err.status === 429);
      if (throttled) {
        setResendAt(Date.now() + throttledWaitSeconds(isApiError(err) ? err.data : undefined) * 1000);
      }
      setError(throttled ? t('auth.throttled') : describeError(err, t).message);
    } finally {
      setSending(false);
    }
  };

  const broken = token === null || gone || (isApiError(link.error) && link.error.status === 404);
  let content;
  if (!features.portal) {
    content = <PortalUnavailable />;
  } else if (broken) {
    content = (
      <>
        <Notice intent="warning" title={t('auth.link.invalid.title')}>
          {t('auth.link.invalid.body')}
        </Notice>
        <ButtonLink
          appearance="primary"
          icon={<PersonRegular />}
          to="/sign-in"
          onClick={() => forgetLinkToken('portal')}
        >
          {t('auth.link.signIn')}
        </ButtonLink>
      </>
    );
  } else if (challenge) {
    content = (
      <div className={styles.card}>
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
          onExpired={() => setResendAt((at) => Math.min(at, Date.now()))}
          onSignedIn={() => forgetLinkToken('portal')}
          headingLevel="h2"
        />
      </div>
    );
  } else if (link.isPending) {
    content = <PageSpinner />;
  } else if (!link.data) {
    content = <ErrorBlock error={link.error} onRetry={() => void link.refetch()} />;
  } else {
    content = (
      <div className={styles.card}>
        <p className={styles.hospital}>{link.data.hospital}</p>
        <Text className={styles.number}>
          <PhoneRegular className={styles.icon} aria-hidden />
          {t('auth.link.sendTo', { phone: link.data.maskedPhone })}
        </Text>
        {error && <Notice intent="error">{error}</Notice>}
        <Button
          appearance="primary"
          size="large"
          onClick={() => void requestCode(false)}
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
        <Text className={styles.muted}>
          <TextLink to="/sign-in" onClick={() => forgetLinkToken('portal')}>
            {t('auth.link.notYours')}
          </TextLink>
        </Text>
      </div>
    );
  }

  return (
    <PortalFrame navigation={false} brandTarget="/sign-in">
      <PortalPage title={t('auth.link.title')} width="narrow">
        {content}
      </PortalPage>
    </PortalFrame>
  );
}
