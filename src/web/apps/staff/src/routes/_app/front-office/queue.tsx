import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  Select,
  Switch,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  ArrowForwardRegular,
  ArrowUndoRegular,
  CheckmarkRegular,
  MegaphoneLoudRegular,
  PersonRegular,
  PlayRegular,
  SkipForwardTabRegular,
} from '@fluentui/react-icons';
import { formatTime, useHotkey, useRealtimeEvent, useSession } from '@hc360/core';
import { DataTable, EmptyState, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useMemo, useState } from 'react';
import { toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { frontOfficeQueries, queueApi, type QueueEntry } from '@/modules/front-office/api';

export const Route = createFileRoute('/_app/front-office/queue')({
  component: QueuePage,
});

const PREFS_KEY = 'hc360:queue-desk';

interface DeskPrefs {
  departmentId: string;
  practitionerId: string;
  room: string;
}

function loadPrefs(): DeskPrefs {
  try {
    return {
      departmentId: '',
      practitionerId: '',
      room: '',
      ...(JSON.parse(localStorage.getItem(PREFS_KEY) ?? '{}') as Partial<DeskPrefs>),
    };
  } catch {
    return { departmentId: '', practitionerId: '', room: '' };
  }
}

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(280px, 360px) minmax(0, 1fr)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1000px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  serving: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '6px',
    textAlign: 'center',
    padding: '8px 0',
  },
  token: {
    fontSize: '64px',
    lineHeight: '72px',
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorBrandForeground1,
    letterSpacing: '2px',
  },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', justifyContent: 'center', marginTop: '8px' },
  callNext: { width: '100%', minHeight: '56px', fontSize: tokens.fontSizeBase500 },
  muted: { color: tokens.colorNeutralForeground3 },
  waitLong: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
});

/** Doctor / front-desk queue console (SRS FO-04, OA-05): call next, start, finish, skip, recall. */
function QueuePage() {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const userPractitionerId = useSession((s) => s.user?.practitionerId);
  const canManage = usePermission('queue.manage');
  const [prefs, setPrefs] = useState<DeskPrefs>(() => {
    const saved = loadPrefs();
    return { ...saved, practitionerId: saved.practitionerId || userPractitionerId || '' };
  });
  const [showFinished, setShowFinished] = useState(false);
  const departments = useDepartments('Clinical');
  // With a single clinical department there is nothing to choose.
  const departmentId = prefs.departmentId || (departments.data?.length === 1 ? departments.data[0]!.id : '');
  const practitioners = usePractitioners(departmentId || null);

  useEffect(() => {
    try {
      localStorage.setItem(PREFS_KEY, JSON.stringify(prefs));
    } catch {
      // Preferences are a convenience only.
    }
  }, [prefs]);

  const queue = useQuery({
    ...frontOfficeQueries.queue(departmentId, prefs.practitionerId || null, showFinished),
    enabled: Boolean(departmentId),
  });

  useRealtimeEvent(departmentId ? `queue:${departmentId}` : null, null, () => {
    void queryClient.invalidateQueries({ queryKey: ['front-office', 'queue', departmentId] });
  });

  const refresh = () => void queryClient.invalidateQueries({ queryKey: ['front-office', 'queue'] });

  const act = useMutation({
    mutationFn: async ({
      action,
      entry,
    }: {
      action: 'call' | 'start' | 'complete' | 'skip' | 'requeue';
      entry: QueueEntry;
    }) => {
      switch (action) {
        case 'call':
          return queueApi.call(entry.tokenId, prefs.room || null);
        case 'start':
          return queueApi.start(entry.tokenId);
        case 'complete':
          return queueApi.complete(entry.tokenId);
        case 'skip':
          return queueApi.skip(entry.tokenId);
        case 'requeue':
          return queueApi.requeue(entry.tokenId);
      }
    },
    onSuccess: refresh,
    onError: (error) => {
      notify.error(error, 'The queue could not be updated');
      refresh();
    },
  });

  const callNext = useMutation({
    mutationFn: () => queueApi.callNext(departmentId, prefs.practitionerId || null, prefs.room || null),
    onSuccess: (token) => {
      if (!token) {
        notify.info('No one is waiting', 'New check-ins will appear here automatically.');
      }
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not call the next patient'),
  });

  useHotkey('f2', () => canManage && departmentId && !callNext.isPending && callNext.mutate(), { allowInInputs: true });

  const entries = useMemo(() => queue.data ?? [], [queue.data]);
  const serving = entries.filter(
    (e) =>
      (e.status === 'Called' || e.status === 'InService') &&
      (!prefs.practitionerId || !e.practitionerId || e.practitionerId === prefs.practitionerId),
  );
  const current = serving.find((e) => e.status === 'InService') ?? serving[0];
  const waiting = entries.filter((e) => e.status === 'Waiting');
  const finished = entries.filter((e) => ['Completed', 'Skipped', 'NoShow', 'Cancelled'].includes(e.status));

  return (
    <Page>
      <PageHeader
        title="Patient queue"
        subtitle="Press F2 to call the next patient. The waiting-area screen updates automatically."
      />
      <Section>
        <FilterBar>
          <Field label="Department">
            <Select
              value={departmentId}
              onChange={(_, d) => setPrefs((p) => ({ ...p, departmentId: d.value, practitionerId: '' }))}
            >
              <option value="">Choose department</option>
              {toOptions(departments.data).map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Doctor">
            <Select
              value={prefs.practitionerId}
              onChange={(_, d) => setPrefs((p) => ({ ...p, practitionerId: d.value }))}
              disabled={!departmentId}
            >
              <option value="">All doctors</option>
              {toOptions(practitioners.data).map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Room / counter">
            <Input
              value={prefs.room}
              onChange={(_, d) => setPrefs((p) => ({ ...p, room: d.value.slice(0, 20) }))}
              placeholder="e.g. Room 3"
            />
          </Field>
          <Switch checked={showFinished} onChange={(_, d) => setShowFinished(d.checked)} label="Show finished" />
        </FilterBar>
      </Section>

      {!departmentId ? (
        <EmptyState
          title="Choose your department"
          description="The queue for the selected department and doctor will appear here."
        />
      ) : (
        <div className={styles.layout}>
          <Section title="Now serving">
            {current ? (
              <div className={styles.serving}>
                <span className={styles.token} aria-live="polite">
                  {current.label}
                </span>
                <Text size={400} weight="semibold">
                  {current.patientName}
                </Text>
                <Caption1>
                  {current.age ?? '—'} / {current.gender.charAt(0)} · {current.uhid}
                </Caption1>
                <StatusBadge status={current.status} />
                {current.chiefComplaint && <Caption1 className={styles.muted}>{current.chiefComplaint}</Caption1>}
                {canManage && (
                  <div className={styles.actions}>
                    {current.status === 'Called' && (
                      <>
                        <Button
                          icon={<PlayRegular />}
                          appearance="primary"
                          onClick={() => act.mutate({ action: 'start', entry: current })}
                        >
                          Start consultation
                        </Button>
                        <Tooltip content="Announce this token again" relationship="description">
                          <Button
                            icon={<MegaphoneLoudRegular />}
                            onClick={() => act.mutate({ action: 'call', entry: current })}
                          >
                            Recall
                          </Button>
                        </Tooltip>
                        <Button
                          icon={<SkipForwardTabRegular />}
                          onClick={() => act.mutate({ action: 'skip', entry: current })}
                        >
                          Not present
                        </Button>
                      </>
                    )}
                    {current.status === 'InService' && (
                      <Button
                        icon={<CheckmarkRegular />}
                        appearance="primary"
                        onClick={() => act.mutate({ action: 'complete', entry: current })}
                      >
                        Finish
                      </Button>
                    )}
                    <Button
                      icon={<PersonRegular />}
                      appearance="subtle"
                      onClick={() =>
                        void navigate({ to: '/patients/$patientId', params: { patientId: current.patientId } })
                      }
                    >
                      Open patient
                    </Button>
                  </div>
                )}
              </div>
            ) : (
              <EmptyState title="No one called yet" description="Call the next patient to start." />
            )}
            {canManage && (
              <Button
                className={styles.callNext}
                appearance="primary"
                icon={<ArrowForwardRegular />}
                disabled={callNext.isPending}
                onClick={() => callNext.mutate()}
                aria-keyshortcuts="F2"
              >
                Call next patient ({waiting.length} waiting)
              </Button>
            )}
          </Section>

          <Section title={`Waiting (${waiting.length})`} flush>
            <DataTable
              ariaLabel="Waiting patients"
              rows={showFinished ? [...waiting, ...finished] : waiting}
              loading={queue.isLoading}
              error={queue.error}
              onRetry={() => void queue.refetch()}
              getRowId={(e) => e.tokenId}
              columns={[
                { key: 'token', header: 'Token', width: 90, render: (e) => <Text weight="semibold">{e.label}</Text> },
                {
                  key: 'patient',
                  header: 'Patient',
                  render: (e) => (
                    <span>
                      {e.patientName}{' '}
                      <Text size={200}>
                        ({e.age ?? '—'} / {e.gender.charAt(0)})
                      </Text>
                      {e.priority !== 'Normal' && (
                        <Badge
                          size="small"
                          color={e.priority === 'Emergency' ? 'danger' : 'warning'}
                          style={{ marginLeft: 6 }}
                        >
                          {e.priority}
                        </Badge>
                      )}
                      {e.isFollowUp && (
                        <Badge size="small" appearance="outline" style={{ marginLeft: 6 }}>
                          Follow-up
                        </Badge>
                      )}
                    </span>
                  ),
                },
                {
                  key: 'issued',
                  header: 'Checked in',
                  width: 100,
                  priority: 'low',
                  render: (e) => formatTime(e.issuedAt),
                },
                {
                  key: 'wait',
                  header: 'Waiting',
                  width: 100,
                  render: (e) =>
                    e.status === 'Waiting' ? (
                      <span className={e.waitingMinutes >= 45 ? styles.waitLong : undefined}>
                        {e.waitingMinutes} min
                      </span>
                    ) : (
                      <StatusBadge status={e.status} />
                    ),
                },
                { key: 'complaint', header: 'Reason', priority: 'low', render: (e) => e.chiefComplaint ?? '—' },
              ]}
              rowActions={
                canManage
                  ? (e) =>
                      e.status === 'Waiting' ? (
                        <Button
                          size="small"
                          icon={<MegaphoneLoudRegular />}
                          onClick={() => act.mutate({ action: 'call', entry: e })}
                        >
                          Call
                        </Button>
                      ) : e.status === 'Skipped' || e.status === 'NoShow' ? (
                        <Button
                          size="small"
                          icon={<ArrowUndoRegular />}
                          onClick={() => act.mutate({ action: 'requeue', entry: e })}
                        >
                          Put back
                        </Button>
                      ) : null
                  : undefined
              }
              emptyTitle="The queue is empty"
              emptyDescription="Checked-in patients appear here in order, with priority patients first."
              size="small"
            />
          </Section>
        </div>
      )}
    </Page>
  );
}
