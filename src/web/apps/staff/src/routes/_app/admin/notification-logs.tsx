import { Caption1, Field, makeStyles, SearchBox, Select, Text, tokens } from '@fluentui/react-components';
import { formatDateTime, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { pageFromUrl } from '@/modules/admin/integrationsApi';
import { NoAccess } from '@/modules/admin/NoAccess';
import {
  CHANNEL_LABELS,
  deliveryStatusDisplay,
  isNotificationStatus,
  NOTIFICATION_CHANNELS,
  NOTIFICATION_LOG_PAGE_SIZE,
  NOTIFICATION_STATUSES,
  notificationLogQueries,
  type NotificationChannel,
  type NotificationStatus,
} from '@/modules/admin/notificationTemplatesApi';
import { NotificationsTabs } from '@/modules/admin/NotificationsTabs';

interface DeliveryLogSearch {
  channel?: NotificationChannel;
  status?: NotificationStatus;
  q?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/admin/notification-logs')({
  validateSearch: (s: Record<string, unknown>): DeliveryLogSearch => ({
    channel: NOTIFICATION_CHANNELS.includes(s.channel as NotificationChannel)
      ? (s.channel as NotificationChannel)
      : undefined,
    status: isNotificationStatus(s.status) ? s.status : undefined,
    q: typeof s.q === 'string' && s.q.trim() ? s.q.trim().slice(0, 100) : undefined,
    page: pageFromUrl(s.page),
  }),
  component: DeliveryLogPage,
});

const useStyles = makeStyles({
  toolbar: { padding: '0 16px 12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  status: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: '2px', minWidth: 0 },
  error: { color: tokens.colorPaletteRedForeground1, wordBreak: 'break-word' },
});

/**
 * The delivery log of the SMS, WhatsApp, e-mail and in-app messages HC360 sent (HMS-04), newest first: whether each was
 * sent, delivered, failed, skipped or suppressed, and why. The recipient's number or address is masked by the server.
 */
function DeliveryLogPage() {
  const styles = useStyles();
  const allowed = usePermission('admin.notifications.manage');
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const page = search.page ?? 1;
  const logs = useQuery({
    ...notificationLogQueries.list({ channel: search.channel, status: search.status, search: search.q, page }),
    enabled: allowed,
  });

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const filter = (patch: Partial<DeliveryLogSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  if (!allowed) {
    return (
      <Page>
        <PageHeader title="Notifications" />
        <NoAccess what="the notification delivery log" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Notifications"
        subtitle="Every message sent to patients and staff, newest first. Mobile numbers and e-mail addresses are masked."
      />
      <NotificationsTabs selected="log" />
      <Section flush>
        <div className={styles.toolbar}>
          <FilterBar>
            <Field label="Message key" style={{ flex: '1 1 220px' }}>
              <SearchBox value={term} onChange={(_, data) => setTerm(data.value)} placeholder="e.g. lab.report_ready" />
            </Field>
            <Field label="Channel">
              <Select
                value={search.channel ?? ''}
                onChange={(_, data) =>
                  filter({
                    channel: NOTIFICATION_CHANNELS.includes(data.value as NotificationChannel)
                      ? (data.value as NotificationChannel)
                      : undefined,
                  })
                }
              >
                <option value="">All channels</option>
                {NOTIFICATION_CHANNELS.map((channel) => (
                  <option key={channel} value={channel}>
                    {CHANNEL_LABELS[channel]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, data) => filter({ status: isNotificationStatus(data.value) ? data.value : undefined })}
              >
                <option value="">All</option>
                {NOTIFICATION_STATUSES.map((status) => (
                  <option key={status} value={status}>
                    {deliveryStatusDisplay(status).label}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Notification delivery log"
          rows={logs.data?.items}
          loading={logs.isLoading}
          error={logs.error}
          onRetry={() => void logs.refetch()}
          getRowId={(row) => row.id}
          size="small"
          columns={[
            { key: 'when', header: 'When', width: 170, render: (row) => formatDateTime(row.createdAt) },
            {
              key: 'message',
              header: 'Message',
              render: (row) => (
                <Text font="monospace" weight="semibold">
                  {row.templateKey}
                </Text>
              ),
            },
            { key: 'channel', header: 'Channel', width: 100, render: (row) => CHANNEL_LABELS[row.channel] },
            {
              key: 'recipient',
              header: 'To',
              width: 160,
              priority: 'low',
              render: (row) => (row.recipient ? <Text font="monospace">{row.recipient}</Text> : '—'),
            },
            {
              key: 'status',
              header: 'Status',
              render: (row) => {
                const display = deliveryStatusDisplay(row.status);
                return (
                  <span className={styles.status} title={display.hint}>
                    <StatusBadge status={row.status} tone={display.tone} label={display.label} />
                    {row.error && (
                      <Caption1 className={row.status === 'Failed' ? styles.error : styles.muted}>{row.error}</Caption1>
                    )}
                  </span>
                );
              },
            },
            {
              key: 'sent',
              header: 'Sent',
              width: 170,
              priority: 'low',
              render: (row) => (row.sentAt ? formatDateTime(row.sentAt) : '—'),
            },
          ]}
          emptyTitle="No messages for these filters"
          emptyDescription="Messages appear here as HC360 sends them: report-ready SMS, appointment reminders, bills by e-mail and more."
          page={page}
          pageSize={NOTIFICATION_LOG_PAGE_SIZE}
          totalCount={logs.data?.totalCount}
          onPageChange={(next) => void navigate({ search: (prev) => ({ ...prev, page: next > 1 ? next : undefined }) })}
        />
      </Section>
    </Page>
  );
}
