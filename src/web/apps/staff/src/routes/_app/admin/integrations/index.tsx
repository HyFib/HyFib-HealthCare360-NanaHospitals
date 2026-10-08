import {
  Button,
  Caption1,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  DocumentTableRegular,
  MoreHorizontalRegular,
  SendRegular,
  SettingsRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatNumber, formatRelative } from '@hc360/core';
import { DataTable, KpiCard, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { IntegrationsTabs } from '@/modules/admin/IntegrationsTabs';
import {
  healthDisplay,
  integrationQueries,
  isIntegrationName,
  providerLabel,
  type IntegrationName,
  type IntegrationStatusDto,
  type LabListenerStatus,
} from '@/modules/admin/integrationsApi';
import { NoAccess } from '@/modules/admin/NoAccess';
import { INTEGRATION_SETTINGS } from '@/modules/admin/settingsForms';
import { TestIntegrationDialog } from '@/modules/admin/TestIntegrationDialog';

export const Route = createFileRoute('/_app/admin/integrations/')({
  component: IntegrationsPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))' },
  muted: { color: tokens.colorNeutralForeground3 },
  error: { color: tokens.colorPaletteRedForeground1, display: 'block', maxWidth: '360px' },
});

const LISTENER_STATES: Record<string, { label: string; tone: 'success' | 'warning' | 'danger' }> = {
  listening: { label: 'Listening', tone: 'success' },
  starting: { label: 'Starting', tone: 'warning' },
  failed: { label: 'Failed', tone: 'danger' },
  connected: { label: 'Connected', tone: 'success' },
  waiting: { label: 'Waiting for agent', tone: 'warning' },
  port_error: { label: 'COM port problem', tone: 'danger' },
  training: { label: 'Training', tone: 'success' },
};

function shorten(text: string, length = 140): string {
  return text.length > length ? `${text.slice(0, length - 1)}…` : text;
}

/** Integrations status (SRS §5 INT-*): is each one set up, switched on and working, with a test for each. */
function IntegrationsPage() {
  const styles = useStyles();
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const navigate = useNavigate();
  const allowed = usePermission('admin.integrations.manage');
  const canConfigure = usePermission('admin.settings.manage');
  const status = useQuery({ ...integrationQueries.status(), enabled: allowed });
  const [testing, setTesting] = useState<{ name: IntegrationName; displayName: string } | null>(null);

  if (!allowed) {
    return (
      <Page>
        <PageHeader title="Integrations" />
        <NoAccess what="integrations" />
      </Page>
    );
  }

  const rows = status.data ?? [];
  const count = (...health: string[]) => rows.filter((row) => health.includes(row.health)).length;
  const listeners = rows.find((row) => row.name === 'lab')?.listeners ?? [];
  const worklist = rows.find((row) => row.name === 'mwl')?.listeners ?? [];
  const hasSettings = (row: IntegrationStatusDto) =>
    isIntegrationName(row.name) && INTEGRATION_SETTINGS[row.name] !== undefined;
  const configure = (row: IntegrationStatusDto) =>
    void navigate({ to: '/admin/integrations/configure/$name', params: { name: row.name } });
  const test = (row: IntegrationStatusDto) =>
    isIntegrationName(row.name) && setTesting({ name: row.name, displayName: row.displayName });

  return (
    <Page>
      <PageHeader
        title="Integrations"
        subtitle="SMS, WhatsApp, e-mail, payments, card/UPI POS, Tally, PACS, lab analysers, labels, ICD lookup and biometric attendance"
        actions={
          <Button icon={<ArrowClockwiseRegular />} onClick={() => void status.refetch()} disabled={status.isFetching}>
            Refresh
          </Button>
        }
      />
      <IntegrationsTabs selected="status" />

      {status.data && (
        <div className={styles.kpis}>
          <KpiCard label="Working" value={count('healthy', 'idle')} hint="Working, or on with no traffic yet" />
          <KpiCard
            label="Need attention"
            value={count('failing', 'degraded')}
            hint="Failing, or failures in the last 24 hours"
          />
          <KpiCard label="Off or not set up" value={count('disabled', 'not_configured')} />
        </div>
      )}

      <Section flush>
        <DataTable
          ariaLabel="Integrations"
          rows={status.data}
          loading={status.isLoading}
          error={status.error}
          onRetry={() => void status.refetch()}
          getRowId={(row) => row.name}
          onRowClick={canConfigure ? (row) => (hasSettings(row) ? configure(row) : test(row)) : undefined}
          columns={[
            {
              key: 'name',
              header: 'Integration',
              render: (row) => (
                <span>
                  <Text weight="semibold">{row.displayName}</Text>
                  <Caption1 block className={styles.muted}>
                    {providerLabel(row.provider)}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'health',
              header: 'Status',
              width: 150,
              render: (row) => {
                const health = healthDisplay(row.health);
                return <StatusBadge status={row.health} tone={health.tone} label={health.label} />;
              },
            },
            {
              key: 'success',
              header: 'Last worked',
              priority: 'low',
              render: (row) =>
                row.lastSuccessAt ? (
                  <span title={formatDateTime(row.lastSuccessAt)}>{formatRelative(row.lastSuccessAt)}</span>
                ) : (
                  'Never'
                ),
            },
            {
              key: 'failure',
              header: 'Last problem',
              priority: 'low',
              render: (row) =>
                row.lastFailureAt ? (
                  <span title={formatDateTime(row.lastFailureAt)}>
                    {formatRelative(row.lastFailureAt)}
                    {row.lastError && <Caption1 className={styles.error}>{shorten(row.lastError)}</Caption1>}
                  </span>
                ) : (
                  '—'
                ),
            },
            {
              key: 'failures',
              header: 'Failures (24 h)',
              width: 120,
              align: 'end',
              priority: 'low',
              render: (row) => formatNumber(row.failuresLast24Hours),
            },
          ]}
          rowActions={(row) => (
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<MoreHorizontalRegular />}
                  aria-label={`Actions for ${row.displayName}`}
                  {...restoreFocusTarget}
                />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem icon={<SendRegular />} onClick={() => test(row)}>
                    Send a test
                  </MenuItem>
                  {canConfigure && hasSettings(row) && (
                    <MenuItem icon={<SettingsRegular />} onClick={() => configure(row)}>
                      Settings
                    </MenuItem>
                  )}
                  <MenuItem
                    icon={<DocumentTableRegular />}
                    onClick={() =>
                      void navigate({
                        to: '/admin/integrations/logs',
                        search: { integration: isIntegrationName(row.name) ? row.name : undefined },
                      })
                    }
                  >
                    View log
                  </MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          )}
          emptyTitle="No integrations reported"
          emptyDescription="The server did not list any integrations. Check that the integrations module is installed."
        />
      </Section>

      {listeners.length > 0 && (
        <Section
          title="Laboratory analysers"
          description="Live status of each network analyser’s port and each serial analyser’s lab agent. Set them up in Lab setup › Analysers."
        >
          <ListenerTable listeners={listeners} />
        </Section>
      )}

      {worklist.length > 0 && (
        <Section
          title="Modality worklist"
          description="The DICOM worklist server that modalities ask for their patients."
        >
          <ListenerTable listeners={worklist} worklist />
        </Section>
      )}

      <Caption1 className={styles.muted}>
        {canConfigure
          ? 'Choose an integration to change its settings. '
          : 'Changing settings needs the “Change system settings” permission. '}
        Attendance terminals are registered under HR → Attendance terminals; these settings are only for the terminals
        HC360 reads, as terminals that send their punches need none. All settings are also listed under{' '}
        <Link to="/admin/settings">Administration → Settings</Link>.
      </Caption1>

      {testing && (
        <TestIntegrationDialog name={testing.name} displayName={testing.displayName} onClose={() => setTesting(null)} />
      )}
    </Page>
  );
}

const PROTOCOLS: Record<string, string> = { astm: 'ASTM', hl7v2: 'HL7 v2', dicom: 'DICOM' };

/** Analyser listeners, or the worklist server (its AE title, queries answered and associations refused). */
function ListenerTable({ listeners, worklist = false }: { listeners: LabListenerStatus[]; worklist?: boolean }) {
  const styles = useStyles();
  return (
    <DataTable
      ariaLabel={worklist ? 'Worklist server' : 'Analyser listeners'}
      rows={listeners}
      getRowId={(listener) => listener.instrumentCode}
      size="small"
      columns={[
        {
          key: 'code',
          header: worklist ? 'AE title' : 'Analyser',
          render: (listener) => <Text weight="semibold">{listener.instrumentCode}</Text>,
        },
        {
          key: 'protocol',
          header: 'Protocol',
          width: 100,
          render: (listener) => PROTOCOLS[listener.protocol.toLowerCase()] ?? listener.protocol,
        },
        {
          key: 'port',
          header: 'Connection',
          render: (listener) =>
            listener.transport === 'simulator'
              ? 'Training analyser (simulator)'
              : listener.transport === 'serial'
                ? `${listener.serialPort ?? 'Serial'}${listener.agent ? ` on ${listener.agent}` : ''}`
                : `Port ${listener.port}`,
        },
        {
          key: 'state',
          header: 'State',
          width: 150,
          render: (listener) => {
            const state = LISTENER_STATES[listener.state] ?? { label: listener.state, tone: 'warning' as const };
            return <StatusBadge status={listener.state} tone={state.tone} label={state.label} />;
          },
        },
        {
          key: 'connections',
          header: worklist ? 'Open' : 'Connected',
          width: 100,
          align: 'end',
          priority: 'low',
          render: (listener) => listener.activeConnections,
        },
        {
          key: 'messages',
          header: worklist ? 'Answered / refused' : 'Accepted / refused',
          priority: 'low',
          render: (listener) =>
            `${formatNumber(listener.messagesAccepted)} / ${formatNumber(listener.messagesRefused)}`,
        },
        {
          key: 'last',
          header: worklist ? 'Last query' : 'Last message',
          priority: 'low',
          render: (listener) => (listener.lastMessageAt ? formatRelative(listener.lastMessageAt) : 'None yet'),
        },
        {
          key: 'error',
          header: 'Last error',
          priority: 'low',
          render: (listener) =>
            listener.lastError ? (
              <span title={listener.lastErrorAt ? formatDateTime(listener.lastErrorAt) : undefined}>
                <Caption1 className={styles.error}>{shorten(listener.lastError)}</Caption1>
              </span>
            ) : (
              '—'
            ),
        },
      ]}
    />
  );
}
