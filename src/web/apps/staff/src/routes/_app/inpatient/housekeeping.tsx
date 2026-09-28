import { Badge, Button, Caption1, Field, Select, Spinner, Text, tokens } from '@fluentui/react-components';
import { BroomRegular, CheckmarkCircleRegular, PlayRegular } from '@fluentui/react-icons';
import { formatTime } from '@hc360/core';
import { DataTable, FilterBar, KpiCard, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { bedApi, HOUSEKEEPING_TRIGGER_LABEL, inpatientKeys, inpatientQueries, type HousekeepingTaskDto } from '@/modules/inpatient/api';
import { useInpatientPermissions, useInpatientRealtime, useNow } from '@/modules/inpatient/hooks';
import { ReasonDialog } from '@/modules/inpatient/ReasonDialog';
import { formatDuration, minutesSince } from '@/modules/inpatient/time';

export const Route = createFileRoute('/_app/inpatient/housekeeping')({
  validateSearch: (s: Record<string, unknown>): { wardId?: string } => ({ wardId: typeof s.wardId === 'string' && s.wardId ? s.wardId : undefined }),
  component: HousekeepingPage,
});

/** Beds waiting longer than this are flagged (the server's turnaround target). */
const TARGET_MINUTES = 60;

/** Housekeeping queue (WB-05): beds to clean, oldest first. Start, then mark clean; the bed becomes vacant at once. */
function HousekeepingPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const permissions = useInpatientPermissions();
  const now = useNow(30_000);
  useInpatientRealtime();
  const wards = useQuery(inpatientQueries.wards());
  const tasks = useQuery(inpatientQueries.housekeeping(search.wardId ?? null));
  const [completing, setCompleting] = useState<HousekeepingTaskDto | null>(null);

  const refresh = () => void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
  const start = useMutation({
    mutationFn: (task: HousekeepingTaskDto) => bedApi.startCleaning(task.id),
    onSuccess: (task) => {
      notify.success(`Cleaning of ${task.bedLabel} started`);
      refresh();
    },
    onError: (error) => {
      notify.error(error, 'Could not start cleaning');
      refresh();
    },
  });
  const complete = useMutation({
    mutationFn: ({ task, notes }: { task: HousekeepingTaskDto; notes: string }) => bedApi.completeCleaning(task.id, notes || null),
    onSuccess: (task) => {
      notify.success(`${task.bedLabel} is clean and vacant`, task.turnaroundMinutes !== null ? `Turnaround ${formatDuration(task.turnaroundMinutes)}.` : undefined);
      setCompleting(null);
      refresh();
    },
  });

  const rows = tasks.data ?? [];
  // Waiting time runs from the request; it is recomputed locally so the list stays current between refreshes.
  const waiting = (t: HousekeepingTaskDto) => minutesSince(t.requestedAt, now);
  const pending = rows.filter((t) => t.status === 'Pending').length;
  const inProgress = rows.filter((t) => t.status === 'InProgress').length;
  const delayed = rows.filter((t) => waiting(t) > TARGET_MINUTES).length;
  const canWork = permissions.housekeeping;

  return (
    <Page>
      <PageHeader title="Housekeeping" icon={<BroomRegular />} subtitle="Beds waiting for terminal cleaning, oldest first. Target: clean within 60 minutes." />
      <div style={{ display: 'grid', gap: 12, gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))' }}>
        <KpiCard label="Waiting" value={tasks.data ? pending : '…'} icon={<BroomRegular />} hint="Not started" />
        <KpiCard label="Being cleaned" value={tasks.data ? inProgress : '…'} icon={<PlayRegular />} />
        <KpiCard label="Over 60 minutes" value={tasks.data ? delayed : '…'} icon={<BroomRegular />} hint={delayed ? 'Clean these first' : 'All within target'} />
      </div>
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Ward">
              <Select value={search.wardId ?? ''} onChange={(_, d) => void navigate({ search: { wardId: d.value || undefined }, replace: true })}>
                <option value="">All wards</option>
                {wards.data?.map((w) => (
                  <option key={w.id} value={w.id}>
                    {w.name}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Beds to clean"
          rows={rows}
          loading={tasks.isLoading}
          error={tasks.error}
          onRetry={() => void tasks.refetch()}
          getRowId={(t) => t.id}
          columns={[
            {
              key: 'bed',
              header: 'Bed',
              render: (t) => (
                <span>
                  <Text weight="semibold">{t.bedLabel}</Text> <Caption1>{t.wardName}</Caption1>
                </span>
              ),
            },
            { key: 'why', header: 'Reason', priority: 'low', render: (t) => HOUSEKEEPING_TRIGGER_LABEL[t.trigger] ?? t.trigger },
            { key: 'requested', header: 'Requested', width: 110, priority: 'low', render: (t) => formatTime(t.requestedAt) },
            {
              key: 'waiting',
              header: 'Waiting',
              width: 130,
              render: (t) => {
                const minutes = waiting(t);
                return minutes > TARGET_MINUTES ? (
                  <Badge appearance="tint" color="danger">
                    {formatDuration(minutes)} · late
                  </Badge>
                ) : (
                  <Text>{formatDuration(minutes)}</Text>
                );
              },
            },
            {
              key: 'status',
              header: 'Status',
              width: 150,
              render: (t) =>
                t.status === 'InProgress' ? (
                  <StatusBadge status="InProgress" label={`Cleaning since ${formatTime(t.startedAt)}`} />
                ) : (
                  <StatusBadge status="Waiting" label="Waiting" />
                ),
            },
            { key: 'notes', header: 'Notes', priority: 'low', render: (t) => t.notes ?? '—' },
          ]}
          rowActions={
            canWork
              ? (t) => (
                  <span style={{ display: 'inline-flex', gap: 6 }}>
                    {t.status === 'Pending' && (
                      <Button
                        size="small"
                        icon={start.isPending && start.variables?.id === t.id ? <Spinner size="tiny" /> : <PlayRegular />}
                        disabled={start.isPending}
                        onClick={() => start.mutate(t)}
                      >
                        Start
                      </Button>
                    )}
                    <Button size="small" appearance="primary" icon={<CheckmarkCircleRegular />} onClick={() => setCompleting(t)}>
                      Mark clean
                    </Button>
                  </span>
                )
              : undefined
          }
          emptyTitle="No beds waiting for cleaning"
          emptyDescription="Beds appear here automatically after a discharge, transfer or repair."
        />
      </Section>
      {!canWork && tasks.data && rows.length > 0 && (
        <Caption1 style={{ color: tokens.colorNeutralForeground3 }}>Only housekeeping staff can start and complete cleaning.</Caption1>
      )}
      <ReasonDialog
        open={completing !== null}
        title={completing ? `Mark ${completing.bedLabel} as clean?` : 'Mark as clean?'}
        description="The bed becomes vacant and appears on the admission desk immediately."
        label="Cleaning note"
        placeholder="e.g. Mattress replaced, linen changed"
        optional
        confirmLabel="Mark clean"
        busy={complete.isPending}
        error={complete.error}
        onConfirm={(notes) => completing && complete.mutate({ task: completing, notes })}
        onCancel={() => {
          setCompleting(null);
          complete.reset();
        }}
      />
    </Page>
  );
}
