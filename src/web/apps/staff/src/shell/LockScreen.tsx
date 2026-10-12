import {
  Avatar,
  Button,
  Field,
  Input,
  Link,
  makeStyles,
  Spinner,
  Text,
  Title3,
  tokens,
} from '@fluentui/react-components';
import { LockClosedRegular } from '@fluentui/react-icons';
import { auth, isApiError, useSession, type AuthResult, type AuthUser } from '@hc360/core';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { api } from '@/lib/api';

const useStyles = makeStyles({
  overlay: {
    position: 'fixed',
    inset: 0,
    zIndex: 1000,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: tokens.colorBackgroundOverlay,
    backdropFilter: 'blur(14px)',
  },
  card: {
    width: 'min(360px, calc(100vw - 32px))',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '14px',
    padding: '28px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow28,
    boxSizing: 'border-box',
  },
  form: { display: 'flex', flexDirection: 'column', gap: '12px', width: '100%' },
  muted: { color: tokens.colorNeutralForeground3, textAlign: 'center' },
  pin: { letterSpacing: '0.5em', textAlign: 'center', fontSize: tokens.fontSizeBase500 },
});

/**
 * Idle lock (SRS UX-11, SEC-05; ASVS V7.3.1): hides patient data on shared ward/OPD workstations after inactivity. The
 * server keeps the lock too, so hiding this screen opens nothing: until the PIN (or password) is entered, every request
 * of the session is refused. The server also locks a session nobody has used even if the app did not. Too many wrong
 * attempts end the session.
 */
export function LockScreen() {
  const locked = useSession((s) => s.locked);
  const user = useSession((s) => s.user);
  // Mounted fresh each time the screen locks, so nothing typed earlier survives.
  return locked && user ? <LockPanel user={user} /> : null;
}

function LockPanel({ user }: { user: AuthUser }) {
  const styles = useStyles();
  const setLocked = useSession((s) => s.setLocked);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [usePassword, setUsePassword] = useState(false);
  const [secret, setSecret] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const pinMode = user.hasPin && !usePassword;

  useEffect(() => {
    inputRef.current?.focus();
  }, [pinMode]);

  // Lock the session on the server as well (already done when the server locked it; locking twice changes nothing).
  // Should this fail, the server still locks it at the next refresh, which says how long nobody has used the app.
  useEffect(() => {
    api.post('/api/v1/auth/lock').catch(() => undefined);
  }, []);

  // Signing out a locked session ends it: the screens behind the lock are not asked about (they are not this
  // person's to keep or discard).
  const signOut = async () => {
    await auth.logout();
    queryClient.clear();
    await navigate({ to: '/login', search: {}, ignoreBlocker: true });
  };

  const unlock = async (event: FormEvent) => {
    event.preventDefault();
    if (!secret) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      auth.apply(await api.post<AuthResult>('/api/v1/auth/unlock', pinMode ? { pin: secret } : { password: secret }));
      setLocked(false);
      // What the screens asked for while locked was refused: ask again.
      void queryClient.invalidateQueries();
    } catch (err) {
      if (isApiError(err) && err.status === 401) {
        await signOut();
        return;
      }
      setError(isApiError(err) ? err.userMessage : 'Could not unlock. Try again.');
      setSecret('');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.overlay} role="dialog" aria-modal="true" aria-label="Screen locked">
      <div className={styles.card}>
        <Avatar name={user.displayName} size={72} color="colorful" badge={{ icon: <LockClosedRegular /> }} />
        <Title3 as="h2">{user.displayName}</Title3>
        <Text className={styles.muted}>The screen was locked to protect patient information.</Text>
        <form className={styles.form} onSubmit={unlock}>
          <Field
            label={pinMode ? 'PIN' : 'Password'}
            validationMessage={error ?? undefined}
            validationState={error ? 'error' : 'none'}
          >
            <Input
              ref={inputRef}
              type="password"
              value={secret}
              onChange={(_, d) => setSecret(pinMode ? d.value.replace(/\D/g, '').slice(0, 8) : d.value)}
              inputMode={pinMode ? 'numeric' : undefined}
              autoComplete={pinMode ? 'off' : 'current-password'}
              input={{ className: pinMode ? styles.pin : undefined }}
              size="large"
              disabled={busy}
            />
          </Field>
          <Button
            type="submit"
            appearance="primary"
            size="large"
            disabled={busy || !secret}
            icon={busy ? <Spinner size="tiny" /> : undefined}
          >
            Unlock
          </Button>
        </form>
        {user.hasPin && (
          <Link as="button" onClick={() => setUsePassword((v) => !v)}>
            {usePassword ? 'Use my PIN instead' : 'Use my password instead'}
          </Link>
        )}
        <Link as="button" onClick={() => void signOut()}>
          Not {user.displayName.split(' ')[0]}? Sign out
        </Link>
      </div>
    </div>
  );
}
