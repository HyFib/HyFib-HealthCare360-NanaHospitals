import {
  Badge,
  Button,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DeleteRegular, FingerprintRegular } from '@fluentui/react-icons';
import {
  attestationJson,
  creationOptions,
  deviceUnlockLabel,
  forgetUnlockKeys,
  formatDateTime,
  formatRelative,
  isApiError,
  platformAuthenticatorAvailable,
  rememberedUnlockKeys,
  rememberUnlockKey,
  useSession,
  webAuthnErrorMessage,
  webAuthnUnavailableReason,
  type WebAuthnCeremony,
  type WebAuthnCredentialInfo,
} from '@hc360/core';
import { ConfirmDialog, DataTable, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { api } from '@/lib/api';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  intro: { maxWidth: '640px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const CREDENTIALS_KEY = ['auth', 'webauthn', 'credentials'] as const;

/** A device's answer that did not come: what to tell the person, kept apart from the server's refusals. */
class DeviceError extends Error {}

/**
 * Quick unlock with the device's Windows Hello, fingerprint or face (SRS UX-11): set up this device after giving the
 * password, see the devices set up, and remove any of them. A device set up here only unlocks a screen that locked
 * while its owner was signed in; signing in always needs the password.
 */
export function DeviceUnlock() {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const user = useSession((s) => s.user);
  const [platform, setPlatform] = useState<boolean | null>(null);
  const [remembered, setRemembered] = useState<string[]>(() => (user ? rememberedUnlockKeys(user.id) : []));
  const [settingUp, setSettingUp] = useState(false);
  const [removing, setRemoving] = useState<WebAuthnCredentialInfo | null>(null);
  const unavailable = webAuthnUnavailableReason();
  const allowed = user?.biometricUnlock === true;
  const label = deviceUnlockLabel();

  useEffect(() => {
    void platformAuthenticatorAvailable().then(setPlatform);
  }, []);

  const credentials = useQuery({
    queryKey: CREDENTIALS_KEY,
    queryFn: () => api.get<WebAuthnCredentialInfo[]>('/api/v1/auth/webauthn/credentials'),
  });

  const setUp = useMutation({
    mutationFn: async ({ name, password }: { name: string; password: string }) => {
      const ceremony = await api.post<WebAuthnCeremony>('/api/v1/auth/webauthn/credentials/options', { password });
      let credential: Credential | null;
      try {
        credential = await navigator.credentials.create({ publicKey: creationOptions(ceremony.options) });
      } catch (error) {
        throw new DeviceError(webAuthnErrorMessage(error));
      }
      if (!credential) {
        throw new DeviceError(webAuthnErrorMessage(null));
      }
      return api.post<WebAuthnCredentialInfo>('/api/v1/auth/webauthn/credentials', {
        challengeId: ceremony.challengeId,
        name,
        credential: attestationJson(credential as PublicKeyCredential),
      });
    },
    onSuccess: (added) => {
      if (user) {
        rememberUnlockKey(user.id, added.credentialId);
        setRemembered(rememberedUnlockKeys(user.id));
      }
      setSettingUp(false);
      notify.success(`${label} set up`, 'Use it to unlock this screen after a break.');
      void queryClient.invalidateQueries({ queryKey: CREDENTIALS_KEY });
    },
  });

  const remove = useMutation({
    mutationFn: (credential: WebAuthnCredentialInfo) =>
      api.delete(`/api/v1/auth/webauthn/credentials/${credential.id}`),
    onSuccess: (_, credential) => {
      if (user) {
        forgetUnlockKeys(
          user.id,
          rememberedUnlockKeys(user.id).filter((id) => id !== credential.credentialId),
        );
        setRemembered(rememberedUnlockKeys(user.id));
      }
      setRemoving(null);
      notify.success('Device removed', 'It no longer unlocks your screen.');
      void queryClient.invalidateQueries({ queryKey: CREDENTIALS_KEY });
    },
    onError: (error) => notify.error(error, 'Could not remove the device'),
  });

  const blocked = !allowed
    ? 'Your hospital has turned this off. Unlock your screen with your PIN or password.'
    : (unavailable ??
      (platform === false
        ? 'This device has no Windows Hello, fingerprint reader or face camera that HealthCare360 can use.'
        : null));
  const thisDeviceSetUp = (credentials.data ?? []).some((c) => c.usableHere && remembered.includes(c.credentialId));

  return (
    <div className={styles.stack}>
      <Text className={styles.intro}>
        Unlock your screen after a break with this device's {label} instead of typing your PIN. It only unlocks a screen
        that locked while you were signed in: signing in still needs your password, and your PIN and password always
        work too.
      </Text>
      {blocked ? (
        <MessageBar intent="info" layout="multiline">
          <MessageBarBody>{blocked}</MessageBarBody>
        </MessageBar>
      ) : (
        <div>
          <Button
            appearance="primary"
            icon={<FingerprintRegular />}
            disabled={platform !== true || thisDeviceSetUp}
            onClick={() => {
              setUp.reset();
              setSettingUp(true);
            }}
          >
            {thisDeviceSetUp ? 'This device is set up' : 'Set up this device'}
          </Button>
        </div>
      )}
      <DataTable
        ariaLabel="Devices that unlock your screen"
        rows={credentials.data}
        loading={credentials.isLoading}
        error={credentials.error}
        onRetry={() => void credentials.refetch()}
        getRowId={(c) => c.id}
        columns={[
          {
            key: 'name',
            header: 'Device',
            render: (c) => (
              <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center' }}>
                <FingerprintRegular aria-hidden />
                {c.name}
                {c.usableHere && remembered.includes(c.credentialId) && (
                  <Badge appearance="tint" color="success">
                    This device
                  </Badge>
                )}
              </span>
            ),
          },
          {
            key: 'server',
            header: 'Server name',
            render: (c) => <span className={c.usableHere ? undefined : styles.muted}>{c.serverName}</span>,
            priority: 'low',
          },
          { key: 'created', header: 'Set up', render: (c) => formatDateTime(c.createdAt), priority: 'low' },
          {
            key: 'used',
            header: 'Last used',
            render: (c) => (c.lastUsedAt ? formatRelative(c.lastUsedAt) : 'Not yet'),
          },
        ]}
        rowActions={(c) => (
          <Button size="small" icon={<DeleteRegular />} onClick={() => setRemoving(c)}>
            Remove
          </Button>
        )}
        emptyTitle="No devices set up"
      />
      <SetUpDialog
        open={settingUp}
        label={label}
        busy={setUp.isPending}
        error={setUp.error}
        onConfirm={(name, password) => setUp.mutate({ name, password })}
        onCancel={() => setSettingUp(false)}
      />
      <ConfirmDialog
        open={removing !== null}
        title="Remove this device?"
        confirmLabel="Remove"
        destructive
        busy={remove.isPending}
        onConfirm={() => removing && remove.mutate(removing)}
        onCancel={() => setRemoving(null)}
      >
        {removing ? `"${removing.name}" will no longer unlock your screen. You can set it up again later.` : null}
      </ConfirmDialog>
    </div>
  );
}

/** What this device is called, as the person would say it. */
function defaultDeviceName(): string {
  const agent = typeof navigator === 'undefined' ? '' : navigator.userAgent;
  return /Windows/i.test(agent) ? 'This Windows computer' : /Mac/i.test(agent) ? 'This Mac' : 'This device';
}

function SetUpDialog({
  open,
  label,
  busy,
  error,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  label: string;
  busy: boolean;
  error: unknown;
  onConfirm: (name: string, password: string) => void;
  onCancel: () => void;
}) {
  const styles = useStyles();
  const [name, setName] = useState(defaultDeviceName);
  const [password, setPassword] = useState('');
  const [missing, setMissing] = useState(false);

  // Start afresh each time the dialog opens.
  const [wasOpen, setWasOpen] = useState(open);
  if (wasOpen !== open) {
    setWasOpen(open);
    if (open) {
      setName(defaultDeviceName());
      setPassword('');
      setMissing(false);
    }
  }

  const passwordError = missing
    ? 'Enter your password.'
    : isApiError(error)
      ? (error.fieldErrors.password?.[0] ?? undefined)
      : undefined;
  const otherError =
    error instanceof DeviceError
      ? error.message
      : isApiError(error) && !error.fieldErrors.password
        ? error.userMessage
        : null;
  const confirm = () => (password ? onConfirm(name.trim(), password) : setMissing(true));

  return (
    <ConfirmDialog
      open={open}
      title={`Set up ${label}`}
      confirmLabel="Set up"
      busy={busy}
      onConfirm={confirm}
      onCancel={onCancel}
    >
      <form
        className={styles.stack}
        onSubmit={(event) => {
          event.preventDefault();
          if (!busy) {
            confirm();
          }
        }}
      >
        <Text>
          After your password, {label} asks you to show your face, touch the fingerprint reader or enter your Windows
          Hello PIN. Nothing about your face or finger leaves this device.
        </Text>
        <Field label="Name for this device" hint="For example, the room or desk it is in.">
          <Input value={name} maxLength={64} onChange={(_, d) => setName(d.value)} />
        </Field>
        <Field
          label="Your password"
          required
          validationMessage={passwordError}
          validationState={passwordError ? 'error' : 'none'}
        >
          <Input
            type="password"
            value={password}
            onChange={(_, d) => {
              setPassword(d.value);
              setMissing(false);
            }}
            autoComplete="current-password"
          />
        </Field>
        {otherError && (
          <MessageBar intent="error" layout="multiline">
            <MessageBarBody>{otherError}</MessageBarBody>
          </MessageBar>
        )}
      </form>
    </ConfirmDialog>
  );
}
