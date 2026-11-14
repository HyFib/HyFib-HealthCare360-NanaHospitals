import {
  Avatar,
  Button,
  Divider,
  Field,
  Input,
  Link,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  Title3,
  tokens,
} from '@fluentui/react-components';
import { DocumentTableRegular, FingerprintRegular, LockClosedRegular } from '@fluentui/react-icons';
import {
  assertionJson,
  auth,
  deviceUnlockLabel,
  forgetUnlockKeys,
  isApiError,
  rememberedUnlockKeys,
  requestOptions,
  serverStatus,
  toBase64Url,
  useConnection,
  useSession,
  webAuthnErrorMessage,
  webAuthnUnavailableReason,
  type AuthResult,
  type AuthUser,
  type WebAuthnCeremony,
} from '@hc360/core';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { api } from '@/lib/api';
import { copyOf, useOfflineCopy } from '@/lib/offline/offlineCopy';
import {
  checkOfflineSecret,
  rememberOfflineSecret,
  useOfflineUnlock,
  type OfflineCheck,
  type OfflineSecret,
} from '@/lib/offline/offlineUnlock';
import { wipeOfflineCopy } from '@/lib/offline/wipe';
import { LockedOfflineCopy } from './OfflineCopy';

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
  device: { display: 'flex', flexDirection: 'column', gap: '8px', width: '100%' },
  outage: { width: '100%' },
  muted: { color: tokens.colorNeutralForeground3, textAlign: 'center' },
  error: { color: tokens.colorPaletteRedForeground1, textAlign: 'center' },
  pin: { letterSpacing: '0.5em', textAlign: 'center', fontSize: tokens.fontSizeBase500 },
});

/**
 * Idle lock (SRS UX-11, SEC-05; ASVS V7.3.1): hides patient data on shared ward/OPD workstations after inactivity. The
 * server keeps the lock too, so hiding this screen opens nothing: until the PIN (or password) is entered, every request
 * of the session is refused. The server also locks a session nobody has used even if the app did not. Too many wrong
 * attempts end the session.
 *
 * The screens stay as they were behind it, with whatever was typed and not yet saved, and what they asked the server
 * for while locked is answered once unlocked. Where the person set this device up for it, Windows Hello (or the
 * device's fingerprint or face) unlocks it in one step; the PIN and the password always work too.
 *
 * During an outage (the server cannot be reached, or answers but its database is down) nothing can unlock the screen,
 * and signing out would wipe the offline copy. So the screen offers the copy instead: the PIN or password is checked on
 * this computer against the verifier kept at the user's last sign-in or unlock (`offlineUnlock`), and the copy opens
 * read only over the lock, which stays: nothing is sent to the server, and the screens behind stay hidden.
 */
export function LockScreen() {
  const locked = useSession((s) => s.locked);
  const user = useSession((s) => s.user);
  // Mounted fresh each time the screen locks, so nothing typed earlier survives.
  return locked && user ? <LockPanel user={user} /> : null;
}

/** The server's refusals that mean this device will not unlock this screen, however often it is tried. */
const DEVICE_REFUSALS = new Set([
  'auth.biometric_unlock_none',
  'auth.biometric_unlock_off',
  'auth.biometric_unlock_shared_device',
]);

/**
 * The keys of the user this browser holds, when the hospital allows unlocking with them and this page can use them:
 * the device is asked only when it has one.
 */
function usableDeviceKeys(user: AuthUser): string[] {
  return user.biometricUnlock && webAuthnUnavailableReason() === null ? rememberedUnlockKeys(user.id) : [];
}

/** What the screen says when the PIN or password did not open the offline copy. */
function offlineRefusal(outcome: Exclude<OfflineCheck, { result: 'opened' }>): string {
  switch (outcome.result) {
    case 'wrong':
      return `That did not match. ${outcome.triesLeft} more ${outcome.triesLeft === 1 ? 'try' : 'tries'} before the offline copy cannot be opened here.`;
    case 'locked-out':
      return 'Too many wrong tries: the offline copy cannot be opened here until HealthCare360 is back. Work from the printed pre-downtime pack.';
    default:
      return 'The offline copy cannot be opened here: work from the printed pre-downtime pack.';
  }
}

const OUTAGE_TITLES = {
  unreachable: 'Cannot reach the HealthCare360 server',
  'database-down': 'HealthCare360 cannot reach its database',
} as const;

const NO_SECRETS: readonly OfflineSecret[] = [];

function LockPanel({ user }: { user: AuthUser }) {
  const styles = useStyles();
  const setLocked = useSession((s) => s.setLocked);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [usePassword, setUsePassword] = useState(false);
  const [secret, setSecret] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [deviceKeys, setDeviceKeys] = useState(() => usableDeviceKeys(user));
  const [deviceError, setDeviceError] = useState<string | null>(null);
  const [reading, setReading] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const pinMode = user.hasPin && !usePassword;
  const kind: OfflineSecret = pinMode ? 'pin' : 'password';
  const deviceLabel = deviceUnlockLabel();
  const server = useConnection(serverStatus);
  const copyHeld = useOfflineCopy((s) => copyOf(s, user.id) !== null);
  const offlineSecrets = useOfflineUnlock((s) => (s.userId === user.id ? s.secrets : NO_SECRETS));
  // Only during an outage, only the user's own copy, and only where their PIN or password can be checked here.
  const offerCopy = server !== 'online' && copyHeld && offlineSecrets.length > 0;

  useEffect(() => {
    inputRef.current?.focus();
  }, [pinMode, reading]);

  // Lock the session on the server as well (already done when the server locked it; locking twice changes nothing).
  // Should this fail, the server still locks it at the next refresh, which says how long nobody has used the app.
  useEffect(() => {
    api.post('/api/v1/auth/lock').catch(() => undefined);
  }, []);

  // Signing out a locked session ends it: the screens behind the lock are not asked about (they are not this
  // person's to keep or discard).
  const signOut = async () => {
    await wipeOfflineCopy();
    await auth.logout();
    queryClient.clear();
    await navigate({ to: '/login', search: {}, ignoreBlocker: true });
  };

  // The server ended the session (too many wrong tries, or it expired while locked, perhaps through an outage): it ends
  // here too, but nobody signed out, so the drafts typed in it stay for the same user's next sign-in (SRS DED-10), which
  // the sign-in page leads back to. The offline copy goes, as whenever a session ends while the server can be reached.
  const ended = async () => {
    await wipeOfflineCopy();
    auth.ended();
    queryClient.clear();
  };

  const unlocked = (result: AuthResult) => {
    auth.apply(result);
    setLocked(false);
    // What the screens asked for while locked was refused: ask again.
    void queryClient.invalidateQueries();
  };

  const unlock = async (event?: FormEvent) => {
    event?.preventDefault();
    if (!secret) {
      return;
    }
    setBusy(true);
    setError(null);
    setDeviceError(null);
    try {
      unlocked(await api.post<AuthResult>('/api/v1/auth/unlock', pinMode ? { pin: secret } : { password: secret }));
      // The server accepted it: it opens the offline copy here should the server be down when the screen next locks.
      void rememberOfflineSecret(user.id, kind, secret);
    } catch (err) {
      if (isApiError(err) && err.status === 401) {
        await ended();
        return;
      }
      if (isApiError(err) && (err.isNetworkError || err.status >= 500)) {
        // Not a wrong PIN: the server could not answer. What was typed stays, to open the offline copy with.
        setError('HealthCare360 could not unlock the screen: try again once it is back.');
        return;
      }
      setError(isApiError(err) ? err.userMessage : 'Could not unlock. Try again.');
      setSecret('');
    } finally {
      setBusy(false);
    }
  };

  /** Checks the PIN or password on this computer, and opens the offline copy over the lock. Nothing goes to the server. */
  const openOfflineCopy = async (event?: FormEvent) => {
    event?.preventDefault();
    if (!secret) {
      setError(pinMode ? 'Enter your PIN to open the offline copy.' : 'Enter your password to open the offline copy.');
      return;
    }
    if (!offlineSecrets.includes(kind)) {
      // Signed in with the password, say, and not unlocked with the PIN since: only the password's verifier is kept.
      setSecret('');
      if (pinMode || user.hasPin) {
        setError(
          pinMode
            ? 'Use your password: your PIN has not been used on this computer since you signed in.'
            : 'Use your PIN: your password cannot open the offline copy on this computer.',
        );
        setUsePassword(pinMode);
      } else {
        setError(offlineRefusal({ result: 'unavailable' }));
      }
      return;
    }
    setBusy(true);
    setError(null);
    setDeviceError(null);
    const outcome = await checkOfflineSecret(user.id, kind, secret);
    setBusy(false);
    setSecret('');
    if (outcome.result === 'opened') {
      setReading(true);
    } else {
      setError(offlineRefusal(outcome));
    }
  };

  /**
   * Windows Hello: the server's challenge, signed by this device's key once the device has recognised the person. It
   * resumes this session only, and only while the server still has it open.
   */
  const unlockWithDevice = async () => {
    setBusy(true);
    setError(null);
    setDeviceError(null);
    try {
      const ceremony = await api.post<WebAuthnCeremony>('/api/v1/auth/unlock/webauthn/options');
      const options = requestOptions(ceremony.options);
      const onServer = (options.allowCredentials ?? []).map((c) => toBase64Url(c.id as ArrayBuffer));
      // Keys removed since, or set up on another device, are forgotten here; the device is asked only for its own.
      forgetUnlockKeys(user.id, onServer);
      const here = rememberedUnlockKeys(user.id);
      options.allowCredentials = (options.allowCredentials ?? []).filter((_, i) => here.includes(onServer[i]!));
      if (here.length === 0) {
        setDeviceKeys([]);
        setDeviceError(`This device is no longer set up for ${deviceLabel}. Use your PIN or password.`);
        return;
      }
      let credential: Credential | null;
      try {
        credential = await navigator.credentials.get({ publicKey: options });
      } catch (err) {
        setDeviceError(webAuthnErrorMessage(err));
        return;
      }
      if (!credential) {
        setDeviceError(webAuthnErrorMessage(null));
        return;
      }
      unlocked(
        await api.post<AuthResult>('/api/v1/auth/unlock/webauthn', {
          challengeId: ceremony.challengeId,
          credential: assertionJson(credential as PublicKeyCredential),
        }),
      );
    } catch (err) {
      if (isApiError(err) && err.status === 401) {
        await ended();
        return;
      }
      // No key on the server, turned off, or a kiosk or display board, which nobody's Windows Hello may unlock: the
      // device is not offered again on this screen.
      if (isApiError(err) && err.code && DEVICE_REFUSALS.has(err.code)) {
        if (err.code === 'auth.biometric_unlock_none') {
          forgetUnlockKeys(user.id);
        }
        setDeviceKeys([]);
      }
      setDeviceError(isApiError(err) ? err.userMessage : 'Could not unlock. Try again.');
    } finally {
      setBusy(false);
    }
  };

  if (reading) {
    return (
      <div className={styles.overlay} role="dialog" aria-modal="true" aria-label="Offline copy (read only)">
        <LockedOfflineCopy userId={user.id} onClose={() => setReading(false)} />
      </div>
    );
  }

  // The device cannot unlock the screen without the server either: during an outage it is not offered.
  const offerDevice = deviceKeys.length > 0 && server === 'online';
  return (
    <div className={styles.overlay} role="dialog" aria-modal="true" aria-label="Screen locked">
      <div className={styles.card}>
        <Avatar name={user.displayName} size={72} color="colorful" badge={{ icon: <LockClosedRegular /> }} />
        <Title3 as="h2">{user.displayName}</Title3>
        <Text className={styles.muted}>The screen was locked to protect patient information.</Text>
        {server !== 'online' && (
          <MessageBar intent="warning" layout="multiline" className={styles.outage}>
            <MessageBarBody>
              <MessageBarTitle>{OUTAGE_TITLES[server]}</MessageBarTitle>
              The screen unlocks once it is back.{' '}
              {offerCopy
                ? 'Meanwhile your PIN or password opens the offline copy, read only.'
                : copyHeld
                  ? 'The offline copy cannot be opened here: work from the printed pre-downtime pack.'
                  : 'There is no offline copy on this computer: work from the printed pre-downtime pack.'}
            </MessageBarBody>
          </MessageBar>
        )}
        {offerDevice && (
          <div className={styles.device}>
            <Button
              appearance="primary"
              size="large"
              icon={busy ? <Spinner size="tiny" aria-hidden /> : <FingerprintRegular />}
              disabled={busy}
              onClick={() => void unlockWithDevice()}
            >
              Unlock with {deviceLabel}
            </Button>
            <Divider>or</Divider>
          </div>
        )}
        {deviceError && (
          <Text role="alert" className={styles.error}>
            {deviceError}
          </Text>
        )}
        {/* During an outage, Enter opens the offline copy: unlocking cannot work until the server is back. */}
        <form className={styles.form} onSubmit={offerCopy ? openOfflineCopy : unlock}>
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
          {offerCopy && (
            <Button
              type="submit"
              appearance="primary"
              size="large"
              disabled={busy || !secret}
              icon={busy ? <Spinner size="tiny" aria-hidden /> : <DocumentTableRegular />}
            >
              Open the offline copy
            </Button>
          )}
          <Button
            type={offerCopy ? 'button' : 'submit'}
            onClick={offerCopy ? () => void unlock() : undefined}
            appearance={offerDevice || offerCopy ? 'secondary' : 'primary'}
            size="large"
            disabled={busy || !secret}
            icon={busy && !offerDevice && !offerCopy ? <Spinner size="tiny" aria-hidden /> : undefined}
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
