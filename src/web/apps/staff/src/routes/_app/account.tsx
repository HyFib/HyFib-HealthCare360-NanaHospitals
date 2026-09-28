import {
  Avatar,
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DesktopRegular, PhoneRegular, SignOutRegular } from '@fluentui/react-icons';
import { formatDateTime, formatRelative, humanize, useSession } from '@hc360/core';
import { ConfirmDialog, DataTable, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { api } from '@/lib/api';
import { ChangePasswordForm, MfaSetup, PinForm, reloadCurrentUser } from '@/modules/account/security';

export const Route = createFileRoute('/_app/account')({
  component: AccountPage,
});

interface SessionInfo {
  id: string;
  clientType: string;
  createdAt: string;
  lastSeenAt: string;
  expiresAt: string;
  ipAddress: string | null;
  userAgent: string | null;
  deviceName: string | null;
  isCurrent: boolean;
}

const useStyles = makeStyles({
  profile: { display: 'flex', gap: '16px', alignItems: 'center', flexWrap: 'wrap' },
  profileText: { display: 'flex', flexDirection: 'column', gap: '4px' },
  badges: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  stack: { display: 'flex', flexDirection: 'column', gap: '12px', maxWidth: '420px' },
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
      <PageHeader title="My account" subtitle="Your profile, password, two-step verification, PIN and signed-in devices" />

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
          <Tab value="pin">Unlock PIN</Tab>
          <Tab value="sessions">Signed-in devices</Tab>
        </TabList>
        <div style={{ paddingTop: 16 }}>
          {tab === 'password' && <ChangePasswordForm />}
          {tab === 'mfa' && (user.mfaEnabled ? <DisableMfa /> : <MfaSetup onDone={() => void reloadCurrentUser()} />)}
          {tab === 'pin' && <PinForm />}
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
      <Text>Two-step verification is on. You will be asked for a code from your authenticator app when you sign in.</Text>
      <Field label="Password (to turn it off)">
        <Input type="password" value={password} onChange={(_, d) => setPassword(d.value)} autoComplete="current-password" />
      </Field>
      <div>
        <Button onClick={() => disable.mutate()} disabled={!password || disable.isPending}>
          Turn off
        </Button>
      </div>
    </div>
  );
}

function Sessions() {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [revoking, setRevoking] = useState<SessionInfo | null>(null);
  const sessions = useQuery({ queryKey: ['auth', 'sessions'], queryFn: () => api.get<SessionInfo[]>('/api/v1/auth/sessions') });

  const revoke = useMutation({
    mutationFn: (id: string) => api.delete(`/api/v1/auth/sessions/${id}`),
    onSuccess: () => {
      setRevoking(null);
      notify.success('Signed out of that device');
      void queryClient.invalidateQueries({ queryKey: ['auth', 'sessions'] });
    },
    onError: (error) => notify.error(error, 'Could not sign out that device'),
  });

  return (
    <>
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
              </span>
            ),
          },
          { key: 'ip', header: 'Network address', render: (s) => s.ipAddress ?? '—', priority: 'low' },
          { key: 'signedIn', header: 'Signed in', render: (s) => formatDateTime(s.createdAt), priority: 'low' },
          { key: 'lastSeen', header: 'Last active', render: (s) => formatRelative(s.lastSeenAt) },
        ]}
        rowActions={(s) =>
          s.isCurrent ? null : (
            <Button size="small" icon={<SignOutRegular />} onClick={() => setRevoking(s)}>
              Sign out
            </Button>
          )
        }
        emptyTitle="No other devices"
      />
      <ConfirmDialog
        open={revoking !== null}
        title="Sign out this device?"
        confirmLabel="Sign out"
        destructive
        busy={revoke.isPending}
        onConfirm={() => revoking && revoke.mutate(revoking.id)}
        onCancel={() => setRevoking(null)}
      >
        Anyone using HealthCare360 with your account on that device will be signed out immediately.
      </ConfirmDialog>
    </>
  );
}
