import {
  Button,
  Caption1,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  AddRegular,
  ArrowSyncRegular,
  CopyRegular,
  DatabaseArrowRightRegular,
  DeleteRegular,
  EditRegular,
  HistoryRegular,
  MoreHorizontalRegular,
  PauseRegular,
  PlayRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatRelative, useSession } from '@hc360/core';
import {
  ConfirmDialog,
  DataTable,
  EmptyState,
  KpiCard,
  Page,
  PageHeader,
  Section,
  StatusBadge,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { analyticsApi, analyticsKeys, analyticsQueries, type ScheduleDto } from '@/modules/analytics/api';
import { DeliveriesDrawer } from '@/modules/analytics/DeliveriesDrawer';
import { ANALYTICS_PERMISSIONS as A, EXTRACT_STATE_LABELS, EXTRACT_STATE_TONES } from '@/modules/analytics/labels';
import { scheduleToRequest } from '@/modules/analytics/schedule-form';
import { ScheduleDrawer } from '@/modules/analytics/ScheduleDrawer';

export const Route = createFileRoute('/_app/analytics/extracts')({
  component: ExtractsPage,
});

const useStyles = makeStyles({
  stats: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))' },
  muted: { color: tokens.colorNeutralForeground3 },
  error: {
    color: tokens.colorPaletteRedForeground1,
    maxWidth: '260px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  cell: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  file: {
    display: 'block',
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  folder: { display: 'flex', flexDirection: 'column', gap: '10px' },
  paths: {
    display: 'grid',
    gridTemplateColumns: 'max-content minmax(0, 1fr) auto',
    gap: '6px 12px',
    alignItems: 'center',
    '@media (max-width: 600px)': { gridTemplateColumns: 'minmax(0, 1fr) auto', rowGap: '2px' },
  },
  pathText: { fontFamily: tokens.fontFamilyMonospace, overflowWrap: 'anywhere' },
  pathLabel: {
    color: tokens.colorNeutralForeground3,
    '@media (max-width: 600px)': { gridColumn: '1 / -1', marginTop: '6px' },
  },
});

type Pending = { kind: 'refresh' | 'delete' | 'pause'; extract: ScheduleDto };

/**
 * BI-05: reports written on a schedule as CSV files to the folder IT shares with Power BI and similar tools, with how
 * fresh each file is, a refresh history, "refresh now", pause and delete.
 */
function ExtractsPage() {
  const styles = useStyles();
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission(A.schedulesManage);
  const facilityName = useSession((s) => s.user?.facilityName);
  const catalogue = useQuery(analyticsQueries.catalogue());
  const schedules = useQuery({ ...analyticsQueries.schedules('ExtractFolder'), enabled: canManage });
  const folder = useQuery({ ...analyticsQueries.extractFolder(), enabled: canManage });
  const status = useQuery({ ...analyticsQueries.status(), enabled: canManage });
  const [editing, setEditing] = useState<{ extract: ScheduleDto | null } | null>(null);
  const [history, setHistory] = useState<ScheduleDto | null>(null);
  const [pending, setPending] = useState<Pending | null>(null);

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: analyticsKeys.schedules });
    void queryClient.invalidateQueries({ queryKey: ['analytics', 'status'] });
  };

  const act = useMutation({
    mutationFn: async ({ kind, extract }: Pending) => {
      if (kind === 'refresh') {
        return analyticsApi.sendNow(extract.id);
      }
      if (kind === 'delete') {
        return analyticsApi.deleteSchedule(extract.id, extract.version);
      }
      return analyticsApi.updateSchedule(extract.id, scheduleToRequest(extract, !extract.isActive));
    },
    onSuccess: (_, { kind, extract }) => {
      setPending(null);
      refresh();
      if (kind === 'refresh') {
        notify.success('Extract refreshed', `${extract.extractFile ?? 'The file'} now has the latest data.`);
      } else if (kind === 'delete') {
        notify.success('BI extract deleted', 'Its file was removed from the extract folder.');
      } else {
        notify.success(extract.isActive ? 'Refreshes paused' : 'Refreshes resumed');
      }
    },
    onError: (error, { kind }) => {
      setPending(null);
      refresh();
      notify.error(error, kind === 'refresh' ? 'Could not refresh the extract' : 'The change was not saved');
    },
  });

  const copy = async (text: string, what: string) => {
    try {
      await navigator.clipboard.writeText(text);
      notify.success(`${what} copied`);
    } catch {
      notify.warning('Could not copy', 'Select the path and copy it with Ctrl+C.');
    }
  };

  if (!canManage) {
    return (
      <Page maxWidth={900}>
        <PageHeader title="BI extracts" />
        <Section>
          <EmptyState
            title="You cannot manage BI extracts"
            description="Ask your administrator for the analytics schedules permission."
          />
        </Section>
      </Page>
    );
  }

  const list = schedules.data;
  const s = status.data;
  const where = folder.data;
  const togglePause = (extract: ScheduleDto) =>
    extract.isMine ? act.mutate({ kind: 'pause', extract }) : setPending({ kind: 'pause', extract });
  const create = () => setEditing({ extract: null });

  return (
    <Page>
      <PageHeader
        title="BI extracts"
        icon={<DatabaseArrowRightRegular />}
        subtitle="Reports written as CSV files to a shared folder for Power BI and similar tools, refreshed on a schedule. Times are in IST."
        breadcrumbs={[
          { label: 'Analytics', onClick: () => void navigate({ to: '/analytics' }) },
          { label: 'BI extracts' },
        ]}
        actions={
          <Button appearance="primary" icon={<AddRegular />} disabled={!catalogue.data} onClick={create}>
            New extract
          </Button>
        }
      />

      {where?.problem && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>HC360 cannot write the extract folder</MessageBarTitle>
            {where.problem} Ask IT to check the folder and its permissions. Extracts are not refreshed until this is
            fixed.
          </MessageBarBody>
        </MessageBar>
      )}

      {s && (
        <div className={styles.stats}>
          <KpiCard
            label="Active extracts"
            value={s.activeExtracts.toLocaleString('en-IN')}
            icon={<DatabaseArrowRightRegular />}
          />
          <KpiCard
            label="Late"
            value={
              <span style={s.lateExtracts > 0 ? { color: tokens.colorPaletteDarkOrangeForeground1 } : undefined}>
                {s.lateExtracts}
              </span>
            }
            hint={s.lateExtracts > 0 ? 'Not refreshed for longer than their schedule allows' : 'All refreshed on time'}
          />
          <KpiCard
            label="Not refreshing"
            value={
              <span style={s.failingExtracts > 0 ? { color: tokens.colorPaletteRedForeground1 } : undefined}>
                {s.failingExtracts}
              </span>
            }
            hint={s.failingExtracts > 0 ? 'Open the refresh history of the extract to see why' : 'No failed refreshes'}
          />
          <KpiCard
            label="Last refresh"
            value={s.lastExtractRefreshAt ? formatRelative(s.lastExtractRefreshAt) : 'None yet'}
            hint={s.lastExtractRefreshAt ? formatDateTime(s.lastExtractRefreshAt) : undefined}
          />
        </div>
      )}

      {where && (
        <Section
          title="Extract folder"
          description="IT shares this folder, read-only, with the BI tool. In Power BI Desktop, choose Get data › Folder and enter the folder's path, or Get data › Text/CSV for one file; a published report refreshes through the on-premises data gateway."
        >
          <div className={styles.folder}>
            <div className={styles.paths}>
              {where.facilityFolder && (
                <PathLine
                  label={`Files of ${facilityName ?? 'this facility'}`}
                  path={where.facilityFolder}
                  onCopy={() => void copy(where.facilityFolder ?? '', 'Folder path')}
                />
              )}
              <PathLine
                label="Refresh log"
                path={where.refreshLog}
                onCopy={() => void copy(where.refreshLog, 'Refresh log path')}
              />
            </div>
            <Caption1 className={styles.muted}>
              The refresh log lists every extract with when it was last refreshed, so a BI report can show how fresh its
              data is. Each file has the facility code in its first column, so the files of several facilities can be
              combined.
            </Caption1>
          </div>
        </Section>
      )}

      <Section flush>
        <DataTable
          ariaLabel="BI extracts"
          rows={list}
          loading={schedules.isLoading}
          error={schedules.error}
          onRetry={() => void schedules.refetch()}
          getRowId={(row) => row.id}
          onRowClick={(row) => setEditing({ extract: row })}
          emptyTitle="No BI extracts yet"
          emptyDescription="Write reports such as revenue by department or bed occupancy to the extract folder, for Power BI to read."
          emptyAction={
            <Button icon={<AddRegular />} disabled={!catalogue.data} onClick={create}>
              Create an extract
            </Button>
          }
          columns={[
            {
              key: 'name',
              header: 'Extract',
              render: (row) => (
                <span className={styles.cell}>
                  <Text weight="semibold">{row.name}</Text>
                  <Caption1 className={styles.muted}>{row.reportTitle}</Caption1>
                </span>
              ),
            },
            {
              key: 'file',
              header: 'File',
              priority: 'low',
              width: 240,
              render: (row) => (
                <Text className={styles.file} title={row.extractFile ?? undefined}>
                  {row.extractFile ?? '—'}
                </Text>
              ),
            },
            {
              key: 'when',
              header: 'Refreshed',
              render: (row) => (
                <span className={styles.cell}>
                  <Text>{row.when}</Text>
                  <Caption1 className={styles.muted}>
                    {row.isActive && row.nextRunAt ? `Next ${formatDateTime(row.nextRunAt)}` : 'Paused'}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'fresh',
              header: 'Data as of',
              render: (row) => (
                <span className={styles.cell}>
                  {row.lastSucceededAt ? (
                    <Text title={formatDateTime(row.lastSucceededAt)}>{formatRelative(row.lastSucceededAt)}</Text>
                  ) : (
                    <Caption1 className={styles.muted}>Not written yet</Caption1>
                  )}
                  {row.extractState === 'Failing' && row.lastError && (
                    <Caption1 className={styles.error} title={row.lastError}>
                      {row.lastError}
                    </Caption1>
                  )}
                </span>
              ),
            },
            { key: 'owner', header: 'Owner', priority: 'low', render: (row) => (row.isMine ? 'You' : row.ownerName) },
            {
              key: 'state',
              header: 'Status',
              width: 150,
              render: (row) =>
                row.extractState && (
                  <StatusBadge
                    status={row.extractState}
                    tone={EXTRACT_STATE_TONES[row.extractState]}
                    label={EXTRACT_STATE_LABELS[row.extractState]}
                  />
                ),
            },
          ]}
          rowActions={(row) => (
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<MoreHorizontalRegular />}
                  aria-label={`Actions for ${row.name}`}
                  {...restoreFocusTarget}
                />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem icon={<EditRegular />} onClick={() => setEditing({ extract: row })}>
                    Change
                  </MenuItem>
                  <MenuItem icon={<ArrowSyncRegular />} onClick={() => setPending({ kind: 'refresh', extract: row })}>
                    Refresh now
                  </MenuItem>
                  <MenuItem icon={<HistoryRegular />} onClick={() => setHistory(row)}>
                    Refresh history
                  </MenuItem>
                  <MenuItem icon={row.isActive ? <PauseRegular /> : <PlayRegular />} onClick={() => togglePause(row)}>
                    {row.isActive ? 'Pause refreshes' : 'Resume refreshes'}
                  </MenuItem>
                  <MenuItem icon={<DeleteRegular />} onClick={() => setPending({ kind: 'delete', extract: row })}>
                    Delete
                  </MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          )}
        />
      </Section>

      {editing && catalogue.data && (
        <ScheduleDrawer
          catalogue={catalogue.data}
          schedule={editing.extract}
          destination="ExtractFolder"
          onClose={() => setEditing(null)}
        />
      )}
      {history && <DeliveriesDrawer schedule={history} onClose={() => setHistory(null)} />}

      <ConfirmDialog
        open={pending !== null}
        title={
          pending?.kind === 'refresh'
            ? `Refresh “${pending.extract.name}” now?`
            : pending?.kind === 'delete'
              ? `Delete “${pending.extract.name}”?`
              : `${pending?.extract.isActive ? 'Pause' : 'Resume'} “${pending?.extract.name ?? ''}”?`
        }
        confirmLabel={
          pending?.kind === 'refresh'
            ? 'Refresh now'
            : pending?.kind === 'delete'
              ? 'Delete'
              : pending?.extract.isActive
                ? 'Pause'
                : 'Resume'
        }
        destructive={pending?.kind === 'delete'}
        busy={act.isPending}
        onConfirm={() => pending && act.mutate(pending)}
        onCancel={() => setPending(null)}
      >
        {pending?.kind === 'refresh'
          ? `The report runs now and replaces ${pending.extract.extractFile ?? 'the file'} in the extract folder. The regular schedule does not change.`
          : pending?.kind === 'delete'
            ? `${pending.extract.extractFile ?? 'The file'} is removed from the extract folder, so BI reports that read it stop refreshing.`
            : 'This makes you the owner of the extract: the report will run with your access to reports and facilities.'}
      </ConfirmDialog>
    </Page>
  );
}

/** One row of the folder grid: what it is, the path, and a button to copy it. */
function PathLine({ label, path, onCopy }: { label: string; path: string; onCopy: () => void }) {
  const styles = useStyles();
  return (
    <>
      <Text className={styles.pathLabel}>{label}</Text>
      <Text className={styles.pathText}>{path}</Text>
      <Button
        size="small"
        appearance="subtle"
        icon={<CopyRegular />}
        aria-label={`Copy the path: ${label}`}
        onClick={onCopy}
      />
    </>
  );
}
