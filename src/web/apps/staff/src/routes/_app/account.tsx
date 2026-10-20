import {
  Avatar,
  Badge,
  Button,
  Caption1,
  Divider,
  Field,
  Input,
  makeStyles,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DesktopRegular, LockClosedRegular, PhoneRegular, SignOutRegular } from '@fluentui/react-icons';
import { formatDateTime, formatRelative, humanize, useSession } from '@hc360/core';
import { DataTable, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { api } from '@/lib/api';
import { DeviceUnlock } from '@/modules/account/DeviceUnlock';
import { PasswordConfirmDialog } from '@/modules/account/PasswordConfirmDialog';
import { ChangePasswordForm, MfaSetup, PinForm, reloadCurrentUser } from '@/modules/account/security';

export const Route = createFileRoute('/_app/account')({
  component: AccountPage,
});

interface SessionInfo {
  id: string;
  clientType: string;
  createdAt: string;
  /** When someone last used it: background refreshes do not count. */
  lastActiveAt: string;
  expiresAt: string;
  ipAddress: string | null;
  userAgent: string | null;
  deviceName: string | null;
  isCurrent: boolean;
  isLocked: boolean;
}

const useStyles = makeStyles({
  profile: { display: 'flex', gap: '16px', alignItems: 'center', flexWrap: 'wrap' },
  profileText: { display: 'flex', flexDirection: 'column', gap: '4px' },
  badges: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  stack: { display: 'flex', flexDirection: 'column', gap: '12px', maxWidth: '420px' },
  sessions: { display: 'flex', flexDirection: 'column', gap: '12px' },
  lockScreen: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

type TabId = 'password' | 'mfa' | 'pin' | 'sessions';

function AccountPage() {
  const styles = useStyles();
  const user = useSession((s) => s.user);
  const [tab, setTab] = useState<TabId>('password');

  if (!user) {
    return null;
  }

  return (
    <Page maxWidth={1100}>
      <PageHeader
        title="My account"
        subtitle="Your profile, password, two-step verification, lock screen and signed-in devices"
      />

      <Section>
        <div className={styles.profile}>
          <Avatar name={user.displayName} size={64} color="colorful" />
          <div className={styles.profileText}>
            <Text size={500} weight="semibold">
              {user.displayName}
            </Text>
            <Caption1 className={styles.muted}>
              {user.userName}
              {user.facilityName ? ` · ${user.facilityName}` : ''}
            </Caption1>
            <div className={styles.badges}>
              {user.roles.map((role) => (
                <Badge key={role} appearance="tint" color="brand">
                  {humanize(role)}
                </Badge>
              ))}
              <Badge appearance="tint" color={user.mfaEnabled ? 'success' : 'warning'}>
                {user.mfaEnabled ? 'Two-step verification on' : 'Two-step verification off'}
              </Badge>
            </div>
          </div>
        </div>
      </Section>

      <Section>
        <TabList selectedValue={tab} onTabSelect={(_, data) => setTab(data.value as TabId)}>
          <Tab value="password">Password</Tab>
          <Tab value="mfa">Two-step verification</Tab>
          <Tab value="pin">Lock screen</Tab>
          <Tab value="sessions">Signed-in devices</Tab>
        </TabList>
        <div style={{ paddingTop: 16 }}>
          {tab === 'password' && <ChangePasswordForm />}
          {tab === 'mfa' && (user.mfaEnabled ? <DisableMfa /> : <MfaSetup onDone={() => void reloadCurrentUser()} />)}
          {tab === 'pin' && (
            <div className={styles.lockScreen}>
              <Text weight="semibold">PIN</Text>
              <PinForm />
              <Divider />
              <Text weight="semibold">Windows Hello or fingerprint</Text>
              <DeviceUnlock />
            </div>
          )}
          {tab === 'sessions' && <Sessions />}
        </div>
      </Section>
    </Page>
  );
}

function DisableMfa() {
  const styles = useStyles();
  const notify = useNotify();
  const [password, setPassword] = useState('');
  const disable = useMutation({
    mutationFn: () => api.post('/api/v1/auth/mfa/disable', { password }),
    onSuccess: async () => {
      setPassword('');
      await reloadCurrentUser();
      notify.success('Two-step verification turned off');
    },
    onError: (error) => notify.error(error, 'Could not turn off two-step verification'),
  });

  return (
    <div className={styles.stack}>
      <Text>
        Two-step verification is on. You will be asked for a code from your authenticator app when you sign in. Turning
        it off signs out your other devices.
      </Text>
      <Field label="Password (to turn it off)">
        <Input
          type="password"
          value={password}
          onChange={(_, d) => setPassword(d.value)}
          autoComplete="current-password"
        />
      </Field>
      <div>
        <Button onClick={() => disable.mutate()} disabled={!password || disable.isPending}>
          Turn off
        </Button>
      </div>
    </div>
  );
}

/** Signing out a device asks for the password again: whoever finds this one signed in must not sign the owner out. */
function Sessions() {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [ending, setEnding] = useState<SessionInfo | 'others' | null>(null);
  const sessions = useQuery({
    queryKey: ['auth', 'sessions'],
    queryFn: () => api.get<SessionInfo[]>('/api/v1/auth/sessions'),
  });
  const others = (sessions.data ?? []).filter((s) => !s.isCurrent).length;

  const end = useMutation({
    mutationFn: async ({ target, password }: { target: SessionInfo | 'others'; password: string }) =>
      target === 'others'
        ? (await api.post<{ ended: number }>('/api/v1/auth/sessions/end-others', { password })).ended
        : (await api.post(`/api/v1/auth/sessions/${target.id}/end`, { password }), 1),
    onSuccess: (ended, { target }) => {
      setEnding(null);
      notify.success(target === 'others' ? `Signed out of ${ended} other device(s)` : 'Signed out of that device');
      void queryClient.invalidateQueries({ queryKey: ['auth', 'sessions'] });
    },
  });

  const open = (target: SessionInfo | 'others') => {
    end.reset();
    setEnding(target);
  };

  return (
    <div className={styles.sessions}>
      <div>
        <Button icon={<SignOutRegular />} disabled={others === 0} onClick={() => open('others')}>
          Sign out everywhere else
        </Button>
      </div>
      <DataTable
        ariaLabel="Signed-in devices"
        rows={sessions.data}
        loading={sessions.isLoading}
        error={sessions.error}
        onRetry={() => void sessions.refetch()}
        getRowId={(s) => s.id}
        columns={[
          {
            key: 'device',
            header: 'Device',
            render: (s) => (
              <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center' }}>
                {s.clientType === 'portal' ? <PhoneRegular /> : <DesktopRegular />}
                {s.deviceName ?? (s.clientType === 'desktop' ? 'HC360 Desktop' : 'Web browser')}
                {s.isCurrent && (
                  <Badge appearance="tint" color="success">
                    This device
                  </Badge>
                )}
                {s.isLocked && (
                  <Badge appearance="tint" color="informative" icon={<LockClosedRegular />}>
                    Locked
                  </Badge>
                )}
              </span>
            ),
          },
          { key: 'ip', header: 'Network address', render: (s) => s.ipAddress ?? '—', priority: 'low' },
          { key: 'signedIn', header: 'Signed in', render: (s) => formatDateTime(s.createdAt), priority: 'low' },
          {
            key: 'lastActive',
            header: 'Last used',
            render: (s) => (s.isCurrent ? 'Now' : formatRelative(s.lastActiveAt)),
          },
        ]}
        rowActions={(s) =>
          s.isCurrent ? null : (
            <Button size="small" icon={<SignOutRegular />} onClick={() => open(s)}>
              Sign out
            </Button>
          )
        }
        emptyTitle="No other devices"
      />
      <PasswordConfirmDialog
        open={ending !== null}
        title={ending === 'others' ? 'Sign out everywhere else?' : 'Sign out this device?'}
        confirmLabel="Sign out"
        busy={end.isPending}
        error={end.error}
        onConfirm={(password) => ending && end.mutate({ target: ending, password })}
        onCancel={() => setEnding(null)}
      >
        {ending === 'others'
          ? `Anyone using HealthCare360 with your account on your ${others} other device(s) will be signed out immediately. This device stays signed in.`
          : 'Anyone using HealthCare360 with your account on that device will be signed out immediately.'}
      </PasswordConfirmDialog>
    </div>
  );
}
