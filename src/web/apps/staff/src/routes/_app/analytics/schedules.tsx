import { Button, Caption1, makeStyles, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Text, tokens, Tooltip } from '@fluentui/react-components';
import {
  AddRegular,
  DeleteRegular,
  EditRegular,
  HistoryRegular,
  MailClockRegular,
  MoreHorizontalRegular,
  PauseRegular,
  PlayRegular,
  SendRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatRelative } from '@hc360/core';
import { ConfirmDialog, DataTable, EmptyState, KpiCard, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { analyticsApi, analyticsKeys, analyticsQueries, type DeliveryDto, type ReportInputs, type SaveScheduleRequest, type ScheduleDto } from '@/modules/analytics/api';
import { DeliveriesDrawer } from '@/modules/analytics/DeliveriesDrawer';
import { ANALYTICS_PERMISSIONS as A, RUN_STATUS_LABELS } from '@/modules/analytics/labels';
import { sanitizeInputs } from '@/modules/analytics/params';
import { ScheduleDrawer, type ScheduleDraft } from '@/modules/analytics/ScheduleDrawer';

interface SchedulesSearch {
  /** Opens a new schedule for this report (from the report screen's "E-mail regularly"). */
  create?: string;
  inputs?: ReportInputs;
}

export const Route = createFileRoute('/_app/analytics/schedules')({
  validateSearch: (s: Record<string, unknown>): SchedulesSearch => ({
    create: typeof s.create === 'string' && /^[a-z0-9-]{1,64}$/.test(s.create) ? s.create : undefined,
    inputs: s.inputs && typeof s.inputs === 'object' && !Array.isArray(s.inputs) ? sanitizeInputs(s.inputs as Record<string, unknown>, new Set()) : undefined,
  }),
  component: SchedulesPage,
});

const useStyles = makeStyles({
  stats: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))' },
  muted: { color: tokens.colorNeutralForeground3 },
  error: { color: tokens.colorPaletteRedForeground1, maxWidth: '260px', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  cell: { display: 'flex', flexDirection: 'column', minWidth: 0 },
});

type Pending = { kind: 'send' | 'delete' | 'pause'; schedule: ScheduleDto };

function toRequest(schedule: ScheduleDto, isActive = schedule.isActive): SaveScheduleRequest {
  return {
    name: schedule.name,
    reportId: schedule.reportId,
    parameters: schedule.parameters,
    format: schedule.format,
    frequency: schedule.frequency,
    time: schedule.time,
    dayOfWeek: schedule.dayOfWeek,
    dayOfMonth: schedule.dayOfMonth,
    recipients: schedule.recipients,
    isActive,
    version: schedule.version,
  };
}

/** BI-04: reports e-mailed on a schedule (IST), with delivery history, "send now", pause and delete. */
function SchedulesPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission(A.schedulesManage);
  const catalogue = useQuery(analyticsQueries.catalogue());
  const schedules = useQuery({ ...analyticsQueries.schedules(), enabled: canManage });
  const status = useQuery({ ...analyticsQueries.status(), enabled: canManage });
  const [editing, setEditing] = useState<{ schedule: ScheduleDto | null; draft: ScheduleDraft | null } | null>(() =>
    search.create ? { schedule: null, draft: { reportId: search.create, inputs: search.inputs ?? {} } } : null,
  );
  const [history, setHistory] = useState<ScheduleDto | null>(null);
  const [pending, setPending] = useState<Pending | null>(null);

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: analyticsKeys.schedules });
    void queryClient.invalidateQueries({ queryKey: ['analytics', 'status'] });
  };

  const act = useMutation({
    mutationFn: async ({ kind, schedule }: Pending) => {
      if (kind === 'send') {
        return analyticsApi.sendNow(schedule.id);
      }
      if (kind === 'delete') {
        return analyticsApi.deleteSchedule(schedule.id, schedule.version);
      }
      return analyticsApi.updateSchedule(schedule.id, toRequest(schedule, !schedule.isActive));
    },
    onSuccess: (result, { kind, schedule }) => {
      setPending(null);
      refresh();
      if (kind === 'send') {
        const delivery = result as DeliveryDto;
        notify.success('Report sent', `${delivery.recipients.length} e-mail(s) are on their way with ${delivery.fileName ?? 'the file'}.`);
      } else if (kind === 'delete') {
        notify.success('Schedule deleted');
      } else {
        notify.success(schedule.isActive ? 'E-mails paused' : 'E-mails resumed');
      }
    },
    onError: (error, { kind }) => {
      setPending(null);
      refresh();
      notify.error(error, kind === 'send' ? 'Could not send the report' : 'The change was not saved');
    },
  });

  const closeEditor = () => {
    setEditing(null);
    if (search.create) {
      void navigate({ search: {}, replace: true });
    }
  };

  if (!canManage) {
    return (
      <Page maxWidth={900}>
        <PageHeader title="Scheduled e-mails" />
        <Section>
          <EmptyState title="You cannot manage scheduled e-mails" description="Ask your administrator for the analytics schedules permission." />
        </Section>
      </Page>
    );
  }

  const list = schedules.data;
  const s = status.data;
  const togglePause = (schedule: ScheduleDto) => (schedule.isMine ? act.mutate({ kind: 'pause', schedule }) : setPending({ kind: 'pause', schedule }));

  return (
    <Page>
      <PageHeader
        title="Scheduled e-mails"
        icon={<MailClockRegular />}
        subtitle="Reports e-mailed automatically as Excel or CSV files. Times are in IST."
        breadcrumbs={[{ label: 'Analytics', onClick: () => void navigate({ to: '/analytics' }) }, { label: 'Scheduled e-mails' }]}
        actions={
          <Button appearance="primary" icon={<AddRegular />} disabled={!catalogue.data} onClick={() => setEditing({ schedule: null, draft: null })}>
            New schedule
          </Button>
        }
      />

      {s && (
        <div className={styles.stats}>
          <KpiCard label="Active schedules" value={s.activeSchedules.toLocaleString('en-IN')} icon={<MailClockRegular />} />
          <KpiCard label="Last e-mail" value={s.lastDeliveryAt ? formatRelative(s.lastDeliveryAt) : 'None yet'} hint={s.lastDeliveryAt ? formatDateTime(s.lastDeliveryAt) : undefined} />
          <KpiCard
            label="Failed in the last 7 days"
            value={<span style={s.failedDeliveriesLast7Days > 0 ? { color: tokens.colorPaletteRedForeground1 } : undefined}>{s.failedDeliveriesLast7Days}</span>}
            hint={s.failedDeliveriesLast7Days > 0 ? 'Open the delivery history of the schedule to see why' : 'All files were produced'}
          />
          <KpiCard
            label="Reports read from"
            value={<span style={{ fontSize: tokens.fontSizeBase500 }}>{s.dataSource}</span>}
            hint={s.usesReplica && s.replicaLagSeconds !== null ? `About ${Math.round(s.replicaLagSeconds)} s behind live data` : undefined}
          />
        </div>
      )}

      <Section flush>
        <DataTable
          ariaLabel="Scheduled e-mails"
          rows={list}
          loading={schedules.isLoading}
          error={schedules.error}
          onRetry={() => void schedules.refetch()}
          getRowId={(row) => row.id}
          onRowClick={(row) => setEditing({ schedule: row, draft: null })}
          emptyTitle="No scheduled e-mails yet"
          emptyDescription="Send reports such as the daily census or yesterday's collections to managers automatically."
          emptyAction={
            <Button icon={<AddRegular />} disabled={!catalogue.data} onClick={() => setEditing({ schedule: null, draft: null })}>
              Create a schedule
            </Button>
          }
          columns={[
            {
              key: 'name',
              header: 'Schedule',
              render: (row) => (
                <span className={styles.cell}>
                  <Text weight="semibold">{row.name}</Text>
                  <Caption1 className={styles.muted}>{row.reportTitle}</Caption1>
                </span>
              ),
            },
            {
              key: 'when',
              header: 'When',
              render: (row) => (
                <span className={styles.cell}>
                  <Text>{row.when}</Text>
                  <Caption1 className={styles.muted}>{row.isActive && row.nextRunAt ? `Next ${formatDateTime(row.nextRunAt)}` : 'Paused'}</Caption1>
                </span>
              ),
            },
            {
              key: 'recipients',
              header: 'Send to',
              priority: 'low',
              render: (row) => (
                <Tooltip content={row.recipients.join(', ')} relationship="description">
                  <Text>
                    {row.recipients[0] ?? '—'}
                    {row.recipients.length > 1 ? ` +${row.recipients.length - 1} more` : ''}
                  </Text>
                </Tooltip>
              ),
            },
            {
              key: 'last',
              header: 'Last sent',
              render: (row) =>
                row.lastRunAt && row.lastStatus ? (
                  <span className={styles.cell}>
                    <span>
                      <StatusBadge status={row.lastStatus === 'Succeeded' ? 'Delivered' : row.lastStatus} label={RUN_STATUS_LABELS[row.lastStatus]} />{' '}
                      <Caption1 className={styles.muted}>{formatRelative(row.lastRunAt)}</Caption1>
                    </span>
                    {row.lastError && (
                      <Caption1 className={styles.error} title={row.lastError}>
                        {row.lastError}
                      </Caption1>
                    )}
                  </span>
                ) : (
                  <Caption1 className={styles.muted}>Not yet</Caption1>
                ),
            },
            { key: 'owner', header: 'Owner', priority: 'low', render: (row) => (row.isMine ? 'You' : row.ownerName) },
            {
              key: 'status',
              header: 'Status',
              width: 110,
              render: (row) => <StatusBadge status={row.isActive ? 'Active' : 'Paused'} tone={row.isActive ? 'success' : 'informative'} />,
            },
          ]}
          rowActions={(row) => (
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button size="small" appearance="subtle" icon={<MoreHorizontalRegular />} aria-label={`Actions for ${row.name}`} />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem icon={<EditRegular />} onClick={() => setEditing({ schedule: row, draft: null })}>
                    Change
                  </MenuItem>
                  <MenuItem icon={<SendRegular />} onClick={() => setPending({ kind: 'send', schedule: row })}>
                    Send now
                  </MenuItem>
                  <MenuItem icon={<HistoryRegular />} onClick={() => setHistory(row)}>
                    Delivery history
                  </MenuItem>
                  <MenuItem icon={row.isActive ? <PauseRegular /> : <PlayRegular />} onClick={() => togglePause(row)}>
                    {row.isActive ? 'Pause e-mails' : 'Resume e-mails'}
                  </MenuItem>
                  <MenuItem icon={<DeleteRegular />} onClick={() => setPending({ kind: 'delete', schedule: row })}>
                    Delete
                  </MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          )}
        />
      </Section>

      {editing && catalogue.data && (
        <ScheduleDrawer catalogue={catalogue.data} schedule={editing.schedule} draft={editing.draft} onClose={closeEditor} />
      )}
      {history && <DeliveriesDrawer schedule={history} onClose={() => setHistory(null)} />}

      <ConfirmDialog
        open={pending !== null}
        title={
          pending?.kind === 'send'
            ? `Send “${pending.schedule.name}” now?`
            : pending?.kind === 'delete'
              ? `Delete “${pending.schedule.name}”?`
              : `${pending?.schedule.isActive ? 'Pause' : 'Resume'} “${pending?.schedule.name ?? ''}”?`
        }
        confirmLabel={pending?.kind === 'send' ? 'Send now' : pending?.kind === 'delete' ? 'Delete' : pending?.schedule.isActive ? 'Pause' : 'Resume'}
        destructive={pending?.kind === 'delete'}
        busy={act.isPending}
        onConfirm={() => pending && act.mutate(pending)}
        onCancel={() => setPending(null)}
      >
        {pending?.kind === 'send'
          ? `The report is generated now and e-mailed to ${pending.schedule.recipients.length} recipient(s). The regular schedule does not change.`
          : pending?.kind === 'delete'
            ? 'No more e-mails will be sent. Files already delivered stay in the recipients’ mailboxes.'
            : 'This makes you the owner of the schedule: the report will run with your access to reports and facilities.'}
      </ConfirmDialog>
    </Page>
  );
}
