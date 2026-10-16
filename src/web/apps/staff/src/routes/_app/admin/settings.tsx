import {
  Badge,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  SearchBox,
  Switch,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, LockClosedRegular } from '@fluentui/react-icons';
import { formatRelative } from '@hc360/core';
import {
  DataTable,
  FilterBar,
  Page,
  PageHeader,
  Section,
  useNotify,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, Link } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { api } from '@/lib/api';
import { PUBLIC_SETTINGS_KEY } from '@/lib/lookups';
import { adminQueries, type SettingDto } from '@/modules/admin/api';
import { isFeatureSettingKey, isValidSettingKey, settingWarning } from '@/modules/admin/settingsForms';

export const Route = createFileRoute('/_app/admin/settings')({
  component: SettingsPage,
});

/**
 * Well-known settings with a friendly explanation (any other key can still be added). Feature switches
 * (`features.*`) have their own screen, Hospital setup > Features.
 */
const KNOWN: Record<string, string> = {
  'ui.idleLockMinutes':
    'Minutes of inactivity before a workstation locks (10 if not set, 0 turns it off). The server keeps the lock.',
  'ui.biometricUnlock':
    "Whether staff may unlock their locked screen with the device's Windows Hello, fingerprint or face (off if not set, true turns it on, for the hospital or one facility). Only where each staff member has their own Windows login: on a shared Windows login, whoever can unlock it with Windows Hello could unlock a colleague's screen. The PIN and the password always work.",
  'security.maxSessionsPerUser':
    'How many sessions one staff account may have at once (3 if not set, 0 for no limit). Signing in once more signs out the one used least recently.',
  'alerts.oncallPhone': 'Mobile number that gets an SMS when the server WiFi drops or backups fail.',
  'security.antiAutomation.enabled':
    'Ask the patient portal’s sign-in and feedback forms for a proof of work, against scripts (on unless set to false).',
  'security.antiAutomation.bits':
    'Work asked of every browser, in bits (14 if not set: under a second on a mid-range phone). Each bit doubles it; at most 20.',
  'security.antiAutomation.maxBits':
    'The most work asked of an address or number that keeps failing, in bits (18 if not set; at most 20, which a mid-range phone does in under a minute).',
};

const FEATURE_SWITCH_NOTE = 'A feature switch. Turn it on or off under Hospital setup, Features.';

function explanation(key: string): string | undefined {
  return KNOWN[key] ?? (isFeatureSettingKey(key) ? FEATURE_SWITCH_NOTE : undefined);
}

function display(value: unknown): string {
  if (value === null || value === undefined) {
    return '—';
  }
  return typeof value === 'string' ? value : JSON.stringify(value);
}

/**
 * Key/value configuration (SRS MNT-03). Values are JSON; secrets (API keys for SMS/WhatsApp/payment gateways) are
 * encrypted on the server and never sent back to the screen.
 */
function SettingsPage() {
  const settings = useQuery(adminQueries.settings());
  const [term, setTerm] = useState('');
  const [editing, setEditing] = useState<SettingDto | 'new' | null>(null);

  const rows = useMemo(() => {
    const t = term.trim().toLowerCase();
    return (settings.data ?? []).filter(
      (s) => !t || s.key.toLowerCase().includes(t) || (s.description ?? '').toLowerCase().includes(t),
    );
  }, [settings.data, term]);

  return (
    <Page>
      <PageHeader
        title="Settings"
        subtitle="Hospital-wide configuration. Changes take effect within five minutes."
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
            Add setting
          </Button>
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Setting name or description" />
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Settings"
          rows={rows}
          loading={settings.isLoading}
          error={settings.error}
          onRetry={() => void settings.refetch()}
          getRowId={(s) => s.id}
          onRowClick={setEditing}
          columns={[
            {
              key: 'key',
              header: 'Setting',
              render: (s) => (
                <span>
                  <Text font="monospace" weight="semibold">
                    {s.key}
                  </Text>
                  {(s.description ?? explanation(s.key)) && (
                    <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                      {s.description ?? explanation(s.key)}
                    </Caption1>
                  )}
                </span>
              ),
            },
            {
              key: 'value',
              header: 'Value',
              render: (s) =>
                s.isSecret ? (
                  <Badge appearance="tint" icon={<LockClosedRegular />}>
                    {s.hasValue ? 'Set (hidden)' : 'Not set'}
                  </Badge>
                ) : (
                  <Text font="monospace">{display(s.value)}</Text>
                ),
            },
            {
              key: 'scope',
              header: 'Applies to',
              priority: 'low',
              width: 130,
              render: (s) => (s.facilityId ? 'This facility' : 'All facilities'),
            },
            {
              key: 'updated',
              header: 'Changed',
              priority: 'low',
              width: 130,
              render: (s) => (s.updatedAt ? formatRelative(s.updatedAt) : '—'),
            },
          ]}
          emptyTitle="No settings yet"
          emptyDescription="Defaults are used until a setting is added here."
          size="small"
        />
      </Section>
      {editing && <SettingDialog setting={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Page>
  );
}

function SettingDialog({ setting, onClose }: { setting: SettingDto | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [key, setKey] = useState(setting?.key ?? '');
  const [secret, setSecret] = useState(setting?.isSecret ?? false);
  const [raw, setRaw] = useState(
    setting && !setting.isSecret && setting.value !== undefined && setting.value !== null
      ? JSON.stringify(setting.value, null, 2)
      : '',
  );
  const [description, setDescription] = useState(setting?.description ?? '');

  // Anything that parses as JSON is stored as JSON; other text is stored as a plain string (no quotes needed).
  const value = useMemo<unknown>(() => {
    const text = raw.trim();
    if (!text) {
      return null;
    }
    try {
      return JSON.parse(text) as unknown;
    } catch {
      return text;
    }
  }, [raw]);

  const keyValid = isValidSettingKey(key);
  const keepSecret = secret && Boolean(setting?.hasValue) && !raw;

  const save = useMutation({
    mutationFn: () =>
      api.put(`/api/v1/admin/settings/${encodeURIComponent(key)}`, {
        value,
        facilityId: setting?.facilityId ?? null,
        secret,
        description: description.trim() || null,
      }),
    onSuccess: () => {
      notify.success('Setting saved');
      void queryClient.invalidateQueries({ queryKey: ['admin', 'settings'] });
      void queryClient.invalidateQueries({ queryKey: PUBLIC_SETTINGS_KEY });
      onClose();
    },
    onError: (error) => notify.error(error, 'Could not save the setting'),
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ key, secret, raw, description }),
    busy: save.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <DialogBody>
          <DialogTitle>{setting ? setting.key : 'Add setting'}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            {!setting && (
              <Field
                label="Setting name"
                required
                hint="Dotted name, e.g. ui.idleLockMinutes or integrations.sms.apiKey"
                validationMessage={
                  key && !keyValid ? 'Use letters, digits, dots, dashes or underscores, like section.name' : undefined
                }
                validationState={key && !keyValid ? 'error' : 'none'}
              >
                <Input value={key} onChange={(_, d) => setKey(d.value.trim())} />
              </Field>
            )}
            {KNOWN[key] && <Caption1>{KNOWN[key]}</Caption1>}
            {settingWarning(key) && (
              <MessageBar intent="warning" layout="multiline">
                <MessageBarBody>{settingWarning(key)}</MessageBarBody>
              </MessageBar>
            )}
            {isFeatureSettingKey(key) && (
              <MessageBar intent="info" layout="multiline">
                <MessageBarBody>
                  Feature switches are turned on and off under Hospital setup,{' '}
                  <Link to="/setup/features" onClick={onClose}>
                    Features
                  </Link>
                  , which explains what each one turns off. A value saved here for one facility overrides it there.
                </MessageBarBody>
              </MessageBar>
            )}
            <Switch
              checked={secret}
              onChange={(_, d) => setSecret(d.checked)}
              label="Secret (API key or password). Stored encrypted and never shown again."
            />
            <Field label="Value" hint={secret ? 'Enter the new secret.' : 'Text, a number, true/false, or JSON'}>
              {secret ? (
                <Input type="password" value={raw} onChange={(_, d) => setRaw(d.value)} autoComplete="off" />
              ) : (
                <Textarea
                  value={raw}
                  onChange={(_, d) => setRaw(d.value)}
                  rows={4}
                  textarea={{ style: { fontFamily: 'Consolas, monospace' } }}
                />
              )}
            </Field>
            <Field label="Description">
              <Input value={description} onChange={(_, d) => setDescription(d.value)} maxLength={500} />
            </Field>
            {keepSecret && (
              <MessageBar intent="info">
                <MessageBarBody>The current secret stays as it is unless you type a new one.</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={guard.requestClose}>Cancel</Button>
            <Button
              appearance="primary"
              disabled={!keyValid || save.isPending || keepSecret}
              onClick={() => save.mutate()}
            >
              Save
            </Button>
          </DialogActions>
        </DialogBody>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
