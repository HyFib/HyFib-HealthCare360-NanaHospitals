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
  Title2,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowLeftRegular,
  CheckmarkCircleRegular,
  EyeOffRegular,
  EyeRegular,
  LockClosedRegular,
  PersonRegular,
  ShieldCheckmarkRegular,
  WifiWarningRegular,
} from '@fluentui/react-icons';
import { auth, bridge, isApiError, useConnection, useSession } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, redirect, useNavigate } from '@tanstack/react-router';
import { useState, type FormEvent, type KeyboardEvent } from 'react';
import { safeRedirect } from '@/lib/redirect';
import { BrandMark, LogoMark } from '@/shell/BrandMark';

interface LoginSearch {
  redirect?: string;
}

interface SystemInfo {
  product: string;
  version: string;
  serverName: string;
  facilityName: string | null;
}

export const Route = createFileRoute('/login')({
  validateSearch: (search: Record<string, unknown>): LoginSearch => ({
    redirect: typeof search.redirect === 'string' ? search.redirect : undefined,
  }),
  beforeLoad: async ({ search }) => {
    const { status } = useSession.getState();
    const signedIn = status === 'authenticated' || (status === 'unknown' && (await auth.restore()));
    if (signedIn) {
      throw redirect({ href: safeRedirect(search.redirect) });
    }
  },
  component: LoginPage,
});

const useStyles = makeStyles({
  root: {
    minHeight: '100vh',
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.1fr) minmax(360px, 1fr)',
    '@media (max-width: 900px)': { gridTemplateColumns: '1fr' },
  },
  brand: {
    background: `linear-gradient(145deg, #064F6B 0%, #0A7CA7 55%, #4A9AC0 100%)`,
    color: '#fff',
    padding: '48px',
    display: 'flex',
    flexDirection: 'column',
    justifyContent: 'space-between',
    gap: '32px',
    '@media (max-width: 900px)': { display: 'none' },
  },
  brandTitle: { fontSize: '40px', lineHeight: '48px', fontWeight: tokens.fontWeightSemibold, margin: 0 },
  brandText: { color: 'rgba(255,255,255,0.85)', fontSize: tokens.fontSizeBase400, lineHeight: tokens.lineHeightBase400, maxWidth: '520px' },
  bullets: { display: 'grid', gap: '14px', margin: 0, padding: 0, listStyle: 'none' },
  bullet: { display: 'flex', gap: '10px', alignItems: 'center', fontSize: tokens.fontSizeBase400 },
  footer: { color: 'rgba(255,255,255,0.7)', fontSize: tokens.fontSizeBase200 },
  panel: { display: 'flex', alignItems: 'center', justifyContent: 'center', padding: '32px 20px' },
  card: {
    width: '100%',
    maxWidth: '400px',
    display: 'flex',
    flexDirection: 'column',
    gap: '18px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusXLarge,
    boxShadow: tokens.shadow16,
    padding: '32px',
    boxSizing: 'border-box',
  },
  mobileBrand: { display: 'none', '@media (max-width: 900px)': { display: 'block' } },
  heading: { display: 'flex', flexDirection: 'column', gap: '4px' },
  muted: { color: tokens.colorNeutralForeground3 },
  form: { display: 'flex', flexDirection: 'column', gap: '14px' },
  code: { letterSpacing: '0.4em', fontSize: tokens.fontSizeBase500, textAlign: 'center' },
  status: { display: 'flex', alignItems: 'center', gap: '6px', color: tokens.colorNeutralForeground3 },
});

function LoginPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const { redirect: target } = Route.useSearch();
  const reachable = useConnection((s) => s.serverReachable && s.browserOnline);

  const info = useQuery({
    queryKey: ['system-info'],
    queryFn: async () => {
      const response = await fetch('/api/v1/system/info', { headers: { Accept: 'application/json' } });
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      return (await response.json()) as SystemInfo;
    },
    staleTime: 5 * 60_000,
    retry: 1,
  });

  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [capsLock, setCapsLock] = useState(false);
  const [challengeId, setChallengeId] = useState<string | null>(null);
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const finish = () => void navigate({ href: safeRedirect(target), replace: true });

  const onSignIn = async (event: FormEvent) => {
    event.preventDefault();
    if (!userName.trim() || !password) {
      setError('Enter your user name and password.');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const device = bridge.isDesktop ? await bridge.deviceInfo().catch(() => null) : null;
      const result = await auth.login(userName.trim(), password, {
        deviceId: device?.deviceId ?? undefined,
        deviceSecret: device?.deviceSecret ?? undefined,
      });
      if (result.mfaRequired && result.challengeId) {
        setChallengeId(result.challengeId);
        setPassword('');
      } else {
        finish();
      }
    } catch (err) {
      setError(isApiError(err) ? err.userMessage : 'Sign-in failed. Try again.');
    } finally {
      setBusy(false);
    }
  };

  const onVerify = async (event: FormEvent) => {
    event.preventDefault();
    if (!challengeId || code.length !== 6) {
      setError('Enter the 6-digit code from your authenticator app.');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await auth.verifyMfa(challengeId, code);
      finish();
    } catch (err) {
      setError(isApiError(err) ? err.userMessage : 'Verification failed. Try again.');
      setCode('');
    } finally {
      setBusy(false);
    }
  };

  const trackCaps = (event: KeyboardEvent) => setCapsLock(event.getModifierState('CapsLock'));
  const facility = info.data?.facilityName ?? 'Nana Hospitals';

  return (
    <div className={styles.root}>
      <section className={styles.brand} aria-hidden="true">
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 12 }}>
          <LogoMark size={40} />
          <Text size={500} weight="semibold" style={{ color: '#fff' }}>
            HyFib HealthCare360
          </Text>
        </span>
        <div style={{ display: 'grid', gap: 20 }}>
          <h1 className={styles.brandTitle}>{facility}</h1>
          <p className={styles.brandText}>
            One connected system for the whole hospital, from the front desk and the doctor's desk to wards, labs,
            pharmacy, billing and reports.
          </p>
          <ul className={styles.bullets}>
            <li className={styles.bullet}>
              <ShieldCheckmarkRegular fontSize={22} /> Encrypted connection and audited access to every record
            </li>
            <li className={styles.bullet}>
              <CheckmarkCircleRegular fontSize={22} /> Work is never lost if the WiFi drops. It saves and retries on its own.
            </li>
            <li className={styles.bullet}>
              <PersonRegular fontSize={22} /> Each role sees only the screens it needs
            </li>
          </ul>
        </div>
        <span className={styles.footer}>
          {info.data ? `Server ${info.data.serverName} · version ${info.data.version}` : 'HyFib Technologies'}
        </span>
      </section>

      <main className={styles.panel}>
        <div className={styles.card}>
          <div className={styles.mobileBrand}>
            <BrandMark facility={facility} />
          </div>

          {!challengeId ? (
            <>
              <div className={styles.heading}>
                <Title2 as="h2">Sign in</Title2>
                <Text className={styles.muted}>Use your hospital user name and password.</Text>
              </div>
              <form className={styles.form} onSubmit={onSignIn} noValidate>
                <Field label="User name" required>
                  <Input
                    value={userName}
                    onChange={(_, d) => setUserName(d.value)}
                    autoComplete="username"
                    autoFocus
                    contentBefore={<PersonRegular />}
                    size="large"
                    disabled={busy}
                  />
                </Field>
                <Field
                  label="Password"
                  required
                  validationState={capsLock ? 'warning' : 'none'}
                  validationMessage={capsLock ? 'Caps Lock is on' : undefined}
                >
                  <Input
                    value={password}
                    onChange={(_, d) => setPassword(d.value)}
                    type={showPassword ? 'text' : 'password'}
                    autoComplete="current-password"
                    contentBefore={<LockClosedRegular />}
                    contentAfter={
                      <Button
                        appearance="transparent"
                        size="small"
                        icon={showPassword ? <EyeOffRegular /> : <EyeRegular />}
                        onClick={() => setShowPassword((v) => !v)}
                        aria-label={showPassword ? 'Hide password' : 'Show password'}
                      />
                    }
                    onKeyDown={trackCaps}
                    onKeyUp={trackCaps}
                    size="large"
                    disabled={busy}
                  />
                </Field>
                {error && (
                  <MessageBar intent="error">
                    <MessageBarBody>{error}</MessageBarBody>
                  </MessageBar>
                )}
                <Button type="submit" appearance="primary" size="large" disabled={busy} icon={busy ? <Spinner size="tiny" /> : undefined}>
                  {busy ? 'Signing in…' : 'Sign in'}
                </Button>
                <Caption1 className={styles.muted}>Forgot your password? Ask the IT help desk to reset it.</Caption1>
              </form>
            </>
          ) : (
            <>
              <div className={styles.heading}>
                <Title2 as="h2">Two-step verification</Title2>
                <Text className={styles.muted}>Enter the 6-digit code shown in your authenticator app.</Text>
              </div>
              <form className={styles.form} onSubmit={onVerify} noValidate>
                <Field label="Verification code" required>
                  <Input
                    value={code}
                    onChange={(_, d) => setCode(d.value.replace(/\D/g, '').slice(0, 6))}
                    inputMode="numeric"
                    autoComplete="one-time-code"
                    autoFocus
                    size="large"
                    input={{ className: styles.code }}
                    disabled={busy}
                  />
                </Field>
                {error && (
                  <MessageBar intent="error">
                    <MessageBarBody>{error}</MessageBarBody>
                  </MessageBar>
                )}
                <Button type="submit" appearance="primary" size="large" disabled={busy || code.length !== 6}>
                  {busy ? 'Verifying…' : 'Verify'}
                </Button>
                <Button
                  appearance="subtle"
                  icon={<ArrowLeftRegular />}
                  onClick={() => {
                    setChallengeId(null);
                    setCode('');
                    setError(null);
                  }}
                >
                  Use a different account
                </Button>
              </form>
            </>
          )}

          <div className={styles.status} role="status">
            {reachable && !info.isError ? (
              <>
                <CheckmarkCircleRegular color={tokens.colorPaletteGreenForeground1} />
                <Caption1>Connected to {info.data?.serverName ?? 'the HealthCare360 server'}</Caption1>
              </>
            ) : (
              <>
                <WifiWarningRegular color={tokens.colorPaletteMarigoldForeground1} />
                <Caption1>Cannot reach the server. Check the WiFi connection.</Caption1>
              </>
            )}
          </div>
        </div>
      </main>
    </div>
  );
}
