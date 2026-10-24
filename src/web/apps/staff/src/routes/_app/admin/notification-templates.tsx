import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  SearchBox,
  Select,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { useDebouncedValue, useSession } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { NoAccess } from '@/modules/admin/NoAccess';
import { NotificationsTabs } from '@/modules/admin/NotificationsTabs';
import { NotificationTemplateDrawer } from '@/modules/admin/NotificationTemplateEditor';
import {
  CHANNEL_LABELS,
  filterTemplates,
  languageLabel,
  NOTIFICATION_CHANNELS,
  notificationTemplateQueries,
  templateScope,
  type NotificationChannel,
  type NotificationTemplateDto,
} from '@/modules/admin/notificationTemplatesApi';

interface TemplateSearch {
  channel?: NotificationChannel;
  q?: string;
}

export const Route = createFileRoute('/_app/admin/notification-templates')({
  validateSearch: (s: Record<string, unknown>): TemplateSearch => ({
    channel: NOTIFICATION_CHANNELS.includes(s.channel as NotificationChannel)
      ? (s.channel as NotificationChannel)
      : undefined,
    q: typeof s.q === 'string' && s.q.trim() ? s.q.trim().slice(0, 100) : undefined,
  }),
  component: NotificationTemplatesPage,
});

const useStyles = makeStyles({
  toolbar: { padding: '0 16px 12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  message: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  channel: { display: 'inline-flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' },
});

/**
 * The messages HC360 sends by SMS, WhatsApp, e-mail and in the app (HMS-04): their text, the DLT id of SMS templates,
 * and for WhatsApp the approved Meta template and whether it carries a document header (the report's PDF).
 */
function NotificationTemplatesPage() {
  const styles = useStyles();
  const allowed = usePermission('admin.notifications.manage');
  const facilityId = useSession((s) => s.user?.facilityId ?? null);
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [editing, setEditing] = useState<NotificationTemplateDto | 'new' | null>(null);
  const templates = useQuery({ ...notificationTemplateQueries.list(), enabled: allowed });

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  if (!allowed) {
    return (
      <Page>
        <PageHeader title="Notification templates" />
        <NoAccess what="notification templates" />
      </Page>
    );
  }

  const rows = templates.data ? filterTemplates(templates.data, search.channel, search.q) : undefined;

  return (
    <Page>
      <PageHeader
        title="Notification templates"
        subtitle="The SMS, WhatsApp, e-mail and in-app messages HC360 sends. Placeholders such as {{name}} are filled in for each message."
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
            New template
          </Button>
        }
      />
      <NotificationsTabs selected="templates" />
      <Section flush>
        <div className={styles.toolbar}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 220px' }}>
              <SearchBox
                value={term}
                onChange={(_, data) => setTerm(data.value)}
                placeholder="Message key or description"
              />
            </Field>
            <Field label="Channel">
              <Select
                value={search.channel ?? ''}
                onChange={(_, data) =>
                  void navigate({
                    search: (prev) => ({
                      ...prev,
                      channel: (data.value || undefined) as NotificationChannel | undefined,
                    }),
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
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Notification templates"
          rows={rows}
          loading={templates.isLoading}
          error={templates.error}
          onRetry={() => void templates.refetch()}
          getRowId={(t) => t.id}
          onRowClick={(t) => setEditing(t)}
          columns={[
            {
              key: 'message',
              header: 'Message',
              render: (t) => (
                <span className={styles.message}>
                  <Text font="monospace" weight="semibold">
                    {t.key}
                  </Text>
                  {t.description && <Caption1 className={styles.muted}>{t.description}</Caption1>}
                </span>
              ),
            },
            {
              key: 'channel',
              header: 'Channel',
              width: 190,
              render: (t) => (
                <span className={styles.channel}>
                  {CHANNEL_LABELS[t.channel]}
                  {t.channel === 'WhatsApp' && t.whatsAppDocumentHeader && (
                    <Badge appearance="tint" color="informative" size="small">
                      Document header
                    </Badge>
                  )}
                </span>
              ),
            },
            { key: 'language', header: 'Language', width: 110, render: (t) => languageLabel(t.language) },
            {
              key: 'scope',
              header: 'Used by',
              width: 150,
              priority: 'low',
              render: (t) => templateScope(t, facilityId),
            },
            {
              key: 'status',
              header: 'Status',
              width: 110,
              render: (t) => (
                <StatusBadge status={t.isActive ? 'Active' : 'Inactive'} label={t.isActive ? 'In use' : 'Not in use'} />
              ),
            },
          ]}
          emptyTitle="No templates match"
          emptyDescription="Clear the search or choose another channel, or add a template."
          emptyAction={
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              New template
            </Button>
          }
        />
      </Section>
      {editing && (
        <NotificationTemplateDrawer template={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />
      )}
    </Page>
  );
}
