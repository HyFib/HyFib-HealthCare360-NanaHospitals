import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  Select,
  Tab,
  TabList,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  ArrowForwardRegular,
  BedRegular,
  CalendarLtrRegular,
  CheckmarkRegular,
  MegaphoneLoudRegular,
  PeopleQueueRegular,
  PlayRegular,
} from '@fluentui/react-icons';
import {
  formatDateOnly,
  formatRelative,
  formatTime,
  todayIso,
  useHotkey,
  useRealtimeEvent,
  useSession,
} from '@hc360/core';
import { DataTable, ErrorState, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import {
  emrKeys,
  emrQueries,
  noteTypeLabel,
  type WorklistAppointment,
  type WorklistParams,
  type WorklistQueueEntry,
  type WorklistResult,
} from '@/modules/emr/api';
import { ResultReviewDialog } from '@/modules/emr/ResultReviewDialog';
import { useStartEncounter } from '@/modules/emr/useStartEncounter';
import { WardRoundList } from '@/modules/emr/WardRoundList';
import {
  appointmentSummaryText,
  parseWorklistView,
  summariseAppointments,
  summariseRound,
  type WorklistView,
} from '@/modules/emr/worklist';
import { WorklistAppointments } from '@/modules/emr/WorklistAppointments';
import { WorklistPatient } from '@/modules/emr/WorklistParts';
import { queueApi } from '@/modules/front-office/api';
import { inpatientQueries } from '@/modules/inpatient/api';
import { useNow } from '@/modules/inpatient/hooks';
import { deficiencyLabel, mrdApi } from '@/modules/mrd/api';
import { PatientPicker } from '@/modules/patients/PatientPicker';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

interface WorklistSearch extends WorklistParams {
  view?: WorklistView;
}

export const Route = createFileRoute('/_app/emr/')({
  validateSearch: (s: Record<string, unknown>): WorklistSearch => ({
    date: typeof s.date === 'string' && DATE.test(s.date) ? s.date : undefined,
    practitionerId: typeof s.practitionerId === 'string' && s.practitionerId ? s.practitionerId : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    view: parseWorklistView(s.view),
  }),
  component: WorklistPage,
});

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(260px, 340px)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  side: { display: 'flex', flexDirection: 'column', gap: '16px' },
  list: { display: 'flex', flexDirection: 'column', gap: '2px', margin: 0, padding: 0, listStyle: 'none' },
  item: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  itemRow: { display: 'flex', gap: '6px', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  waitLong: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  finder: { minWidth: '280px', flex: '1 1 280px' },
  tabs: { padding: '4px 8px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  listCaption: { display: 'block', padding: '12px 16px 8px' },
});

function ClickItem({ onOpen, children }: { onOpen: () => void; children: ReactNode }) {
  const styles = useStyles();
  return (
    <li
      className={styles.item}
      role="button"
      tabIndex={0}
      onClick={onOpen}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          onOpen();
        }
      }}
    >
      {children}
    </li>
  );
}

/**
 * The physician desktop home (EMR-01): today's queue, the doctor's appointments and ward round, results to review and
 * everything still to sign.
 */
function WorklistPage() {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const search = Route.useSearch();
  const userPractitionerId = useSession((s) => s.user?.practitionerId ?? undefined);
  const canQueue = usePermission('queue.manage');
  const canWrite = usePermission('clinical.emr.write');
  const canSeeRounds = usePermission('inpatient.admissions.read');
  const canRecordVisit = usePermission('nursing.chart');
  const userName = useSession((s) => s.user?.displayName ?? null);
  const now = useNow();
  const today = todayIso();
  const params: WorklistParams = {
    date: search.date,
    practitionerId: search.practitionerId,
    departmentId: search.departmentId,
  };
  const worklist = useQuery(emrQueries.worklist(params));
  const departments = useDepartments('Clinical');
  const departmentId = search.departmentId ?? worklist.data?.departmentId ?? undefined;
  const practitioners = usePractitioners(departmentId ?? null);
  // The round is the chosen doctor's (the signed-in one by default), or the department's when there is no doctor.
  const roundDoctorId = search.practitionerId ?? userPractitionerId;
  const rounds = useQuery({
    ...inpatientQueries.rounds({
      practitionerId: search.practitionerId,
      departmentId: roundDoctorId ? undefined : departmentId,
    }),
    enabled: canSeeRounds && Boolean(roundDoctorId ?? departmentId),
  });
  const view: WorklistView = search.view === 'rounds' && !canSeeRounds ? 'queue' : (search.view ?? 'queue');
  const start = useStartEncounter();
  const [reviewing, setReviewing] = useState<WorklistResult | null>(null);

  useRealtimeEvent(departmentId ? `queue:${departmentId}` : null, null, () => {
    void queryClient.invalidateQueries({ queryKey: ['emr', 'worklist'] });
  });

  const set = (patch: Partial<WorklistSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });
  const refresh = () => void queryClient.invalidateQueries({ queryKey: ['emr', 'worklist'] });

  const queueAction = useMutation({
    mutationFn: ({ action, entry }: { action: 'call' | 'complete'; entry: WorklistQueueEntry }) =>
      action === 'call' ? queueApi.call(entry.tokenId, null) : queueApi.complete(entry.tokenId),
    onSuccess: refresh,
    onError: (error) => {
      notify.error(error, 'The queue could not be updated');
      refresh();
    },
  });

  const callNext = useMutation({
    mutationFn: () => queueApi.callNext(departmentId ?? '', search.practitionerId ?? userPractitionerId ?? null, null),
    onSuccess: (token) => {
      if (!token) {
        notify.info('No one is waiting', 'New check-ins appear here automatically.');
      }
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not call the next patient'),
  });

  const resolveDeficiency = useMutation({
    mutationFn: (id: string) => mrdApi.resolveDeficiency(id, 'Completed by the doctor'),
    onSuccess: () => {
      notify.success('Marked as done', 'Medical records will review the chart.');
      void queryClient.invalidateQueries({ queryKey: emrKeys.all });
    },
    onError: (error) => notify.error(error, 'Could not update the deficiency'),
  });

  useHotkey('f2', () => canQueue && departmentId && !callNext.isPending && callNext.mutate(), { allowInInputs: true });

  const consultAppointment = (appointment: WorklistAppointment) => {
    const entry = queue.find((q) => q.visitId === appointment.visitId);
    if (entry) {
      consult(entry);
    } else if (appointment.encounterId) {
      void navigate({ to: '/emr/encounters/$encounterId', params: { encounterId: appointment.encounterId } });
    } else if (appointment.visitId) {
      start.mutate({ patientId: appointment.patientId, visitId: appointment.visitId });
    }
  };

  const consult = (entry: WorklistQueueEntry) => {
    if (entry.encounterId) {
      void navigate({ to: '/emr/encounters/$encounterId', params: { encounterId: entry.encounterId } });
      return;
    }
    if (canQueue && entry.tokenStatus !== 'InService') {
      void queueApi.start(entry.tokenId).catch(() => undefined);
    }
    start.mutate({ patientId: entry.patientId, visitId: entry.visitId });
  };

  const openChart = (patientId: string) => void navigate({ to: '/emr/patients/$patientId', params: { patientId } });
  const w = worklist.data;
  const queue = w?.queue ?? [];
  const waiting = queue.filter((q) => q.tokenStatus === 'Waiting').length;
  const isToday = !search.date || search.date === today;
  const roundPatients = rounds.data?.patients ?? [];
  const notSeen = summariseRound(roundPatients).notSeen;
  const roundDoctor =
    roundDoctorId && roundDoctorId === userPractitionerId
      ? { id: roundDoctorId, code: '', name: userName ?? 'Me' }
      : (practitioners.data?.find((p) => p.id === roundDoctorId) ?? null);
  const subtitle = [
    isToday ? 'Today' : formatDateOnly(search.date),
    `${waiting} waiting`,
    canSeeRounds && roundPatients.length > 0
      ? notSeen > 0
        ? `${notSeen} of ${roundPatients.length} in-patients to see`
        : `all ${roundPatients.length} in-patients seen`
      : null,
    `${w?.resultsToReview.length ?? 0} ${w?.resultsToReview.length === 1 ? 'result' : 'results'} to review`,
    canQueue ? 'F2 calls the next patient' : null,
  ]
    .filter(Boolean)
    .join(' · ');

  return (
    <Page>
      <PageHeader
        title="My worklist"
        subtitle={subtitle}
        actions={
          canQueue &&
          isToday && (
            <Button
              appearance="primary"
              icon={<ArrowForwardRegular />}
              disabled={!departmentId || callNext.isPending}
              onClick={() => callNext.mutate()}
              aria-keyshortcuts="F2"
            >
              Call next patient (F2)
            </Button>
          )
        }
      />
      <Section>
        <FilterBar>
          <div className={styles.finder}>
            <PatientPicker label="Open a patient's chart" value={null} onChange={(p) => p && openChart(p.id)} />
          </div>
          <Field label="Date">
            <Input
              type="date"
              value={search.date ?? today}
              max={today}
              onChange={(_, d) => set({ date: d.value && d.value !== today ? d.value : undefined })}
            />
          </Field>
          <Field label="Department">
            <Select
              value={departmentId ?? ''}
              onChange={(_, d) => set({ departmentId: d.value || undefined, practitionerId: undefined })}
            >
              <option value="">My department</option>
              {toOptions(departments.data).map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Doctor">
            <Select
              value={search.practitionerId ?? userPractitionerId ?? ''}
              onChange={(_, d) => set({ practitionerId: d.value || undefined })}
            >
              <option value="">{userPractitionerId ? 'Me' : 'All doctors'}</option>
              {toOptions(practitioners.data)
                .filter((o) => o.value !== userPractitionerId)
                .map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
            </Select>
          </Field>
        </FilterBar>
      </Section>

      {worklist.error && !w ? (
        <ErrorState error={worklist.error} onRetry={() => void worklist.refetch()} />
      ) : (
        <div className={styles.layout}>
          <Section flush>
            <TabList
              className={styles.tabs}
              selectedValue={view}
              onTabSelect={(_, d) => set({ view: d.value === 'queue' ? undefined : (d.value as WorklistView) })}
              aria-label="Worklist"
            >
              <Tab value="queue" icon={<PeopleQueueRegular />}>
                Queue ({queue.length})
              </Tab>
              <Tab value="appointments" icon={<CalendarLtrRegular />}>
                Appointments ({w?.appointments.length ?? 0})
              </Tab>
              {canSeeRounds && (
                <Tab value="rounds" icon={<BedRegular />}>
                  Ward round{rounds.data ? ` (${roundPatients.length})` : ''}
                </Tab>
              )}
            </TabList>
            {view === 'queue' && (
              <>
                <Caption1 className={mergeClasses(styles.listCaption, styles.muted)}>
                  In service first, then called, then waiting by priority.
                </Caption1>
                <DataTable
                  ariaLabel="Today's queue"
                  actionsWidth={160}
                  rows={w?.queue}
                  loading={worklist.isLoading}
                  getRowId={(e) => e.tokenId}
                  onRowClick={(e) => openChart(e.patientId)}
                  size="small"
                  columns={[
                    {
                      key: 'token',
                      header: 'Token',
                      width: 72,
                      render: (e) => <Text weight="semibold">{e.tokenLabel}</Text>,
                    },
                    {
                      key: 'patient',
                      header: 'Patient',
                      render: (e) => (
                        <WorklistPatient
                          name={e.patientName}
                          age={e.age}
                          gender={e.gender}
                          uhid={e.uhid}
                          hasActiveAllergies={e.hasActiveAllergies}
                          hasCriticalAlerts={e.hasCriticalAlerts}
                        >
                          {e.priority !== 'Normal' && (
                            <Badge size="small" color={e.priority === 'Emergency' ? 'danger' : 'warning'}>
                              {e.priority}
                            </Badge>
                          )}
                          {e.isFollowUp && (
                            <Badge size="small" appearance="outline">
                              Follow-up
                            </Badge>
                          )}
                        </WorklistPatient>
                      ),
                    },
                    { key: 'reason', header: 'Reason', priority: 'low', render: (e) => e.chiefComplaint ?? '—' },
                    {
                      key: 'wait',
                      header: 'Waiting',
                      width: 84,
                      priority: 'low',
                      render: (e) =>
                        e.tokenStatus === 'Waiting' ? (
                          <span className={e.waitingMinutes >= 45 ? styles.waitLong : undefined}>
                            {e.waitingMinutes} min
                          </span>
                        ) : (
                          formatTime(e.issuedAt)
                        ),
                    },
                    {
                      key: 'status',
                      header: 'Status',
                      width: 112,
                      render: (e) =>
                        e.encounterStatus ? (
                          <StatusBadge
                            status={e.encounterStatus === 'InProgress' ? 'InProgress' : 'Completed'}
                            label={e.encounterStatus === 'InProgress' ? 'Note open' : 'Note signed'}
                          />
                        ) : (
                          <StatusBadge status={e.tokenStatus} />
                        ),
                    },
                  ]}
                  rowActions={(e) => (
                    <span style={{ display: 'inline-flex', gap: 4 }}>
                      {canQueue && e.tokenStatus === 'Waiting' && (
                        <Tooltip content="Call to the room" relationship="label">
                          <Button
                            size="small"
                            icon={<MegaphoneLoudRegular />}
                            onClick={() => queueAction.mutate({ action: 'call', entry: e })}
                            disabled={queueAction.isPending}
                          >
                            Call
                          </Button>
                        </Tooltip>
                      )}
                      {canWrite && !['Completed', 'Cancelled', 'NoShow'].includes(e.tokenStatus) && (
                        <Button
                          size="small"
                          appearance="primary"
                          icon={<PlayRegular />}
                          onClick={() => consult(e)}
                          disabled={start.isPending}
                        >
                          {e.encounterId ? 'Continue' : 'Start'}
                        </Button>
                      )}
                      {canWrite && e.encounterId && ['Completed', 'Cancelled', 'NoShow'].includes(e.tokenStatus) && (
                        <Button size="small" onClick={() => consult(e)}>
                          Open note
                        </Button>
                      )}
                      {canQueue && e.tokenStatus === 'InService' && (
                        <Tooltip content="Consultation over: free the queue for the next patient" relationship="label">
                          <Button
                            size="small"
                            icon={<CheckmarkRegular />}
                            onClick={() => queueAction.mutate({ action: 'complete', entry: e })}
                            disabled={queueAction.isPending}
                          >
                            Finish
                          </Button>
                        </Tooltip>
                      )}
                    </span>
                  )}
                  emptyTitle={isToday ? 'No one in your queue yet' : 'No queue on this day'}
                  emptyDescription="Checked-in patients for your department appear here automatically. Use the patient finder above to open any chart."
                />
              </>
            )}
            {view === 'appointments' && (
              <>
                <Caption1 className={mergeClasses(styles.listCaption, styles.muted)} aria-live="polite">
                  {w ? appointmentSummaryText(summariseAppointments(w.appointments)) : ' '}
                </Caption1>
                <WorklistAppointments
                  appointments={w?.appointments}
                  loading={worklist.isLoading}
                  isToday={isToday}
                  showDoctor={!roundDoctorId}
                  canWrite={canWrite}
                  busy={start.isPending}
                  now={new Date(now)}
                  onOpenChart={openChart}
                  onConsult={consultAppointment}
                  onOpenVideo={() => void navigate({ to: '/telehealth' })}
                />
              </>
            )}
            {view === 'rounds' &&
              (roundDoctorId || departmentId ? (
                <WardRoundList
                  round={rounds.data}
                  loading={rounds.isLoading}
                  error={rounds.error}
                  onRetry={() => void rounds.refetch()}
                  isToday={isToday}
                  isDepartment={!roundDoctorId}
                  canRecordVisit={canRecordVisit}
                  practitioner={roundDoctor}
                  onOpenChart={openChart}
                />
              ) : (
                <Caption1 className={mergeClasses(styles.listCaption, styles.muted)}>
                  Choose a department or a doctor above to see their in-patients.
                </Caption1>
              ))}
          </Section>

          <div className={styles.side}>
            <Section
              title={`Results to review (${w?.resultsToReview.length ?? 0})`}
              description="Critical first. Acknowledge after reading."
            >
              {(w?.resultsToReview.length ?? 0) === 0 ? (
                <Text className={styles.muted}>{worklist.isLoading ? 'Loading…' : 'Nothing to review.'}</Text>
              ) : (
                <ul className={styles.list}>
                  {w?.resultsToReview.map((r) => (
                    <ClickItem key={r.id} onOpen={() => setReviewing(r)}>
                      <span className={styles.itemRow}>
                        <Text weight="semibold">{r.title}</Text>
                        {r.hasCriticalFindings && (
                          <Badge color="danger" size="small">
                            Critical
                          </Badge>
                        )}
                      </span>
                      <Caption1 className={styles.muted}>
                        {r.patientName} · {r.uhid} · {formatRelative(r.releasedAt)}
                      </Caption1>
                    </ClickItem>
                  ))}
                </ul>
              )}
            </Section>

            <Section title={`Unsigned encounters (${w?.openEncounters.length ?? 0})`}>
              {(w?.openEncounters.length ?? 0) === 0 ? (
                <Text className={styles.muted}>All encounters are signed.</Text>
              ) : (
                <ul className={styles.list}>
                  {w?.openEncounters.map((e) => (
                    <ClickItem
                      key={e.id}
                      onOpen={() =>
                        void navigate({ to: '/emr/encounters/$encounterId', params: { encounterId: e.id } })
                      }
                    >
                      <Text weight="semibold">{e.patientName}</Text>
                      <Caption1 className={styles.muted}>
                        {e.uhid} · started {formatRelative(e.startedAt)}
                        {e.chiefComplaint ? ` · ${e.chiefComplaint}` : ''}
                      </Caption1>
                    </ClickItem>
                  ))}
                </ul>
              )}
            </Section>

            {(w?.draftPrescriptions.length ?? 0) > 0 && (
              <Section
                title={`Draft prescriptions (${w?.draftPrescriptions.length ?? 0})`}
                description="Not yet sent to the pharmacy"
              >
                <ul className={styles.list}>
                  {w?.draftPrescriptions.map((p) => (
                    <ClickItem
                      key={p.id}
                      onOpen={() =>
                        void navigate({ to: '/emr/prescriptions/$prescriptionId', params: { prescriptionId: p.id } })
                      }
                    >
                      <Text weight="semibold">{p.patientName}</Text>
                      <Caption1 className={styles.muted}>
                        {p.prescriptionNumber} · {p.itemCount} {p.itemCount === 1 ? 'medicine' : 'medicines'} ·{' '}
                        {formatRelative(p.prescribedAt)}
                      </Caption1>
                    </ClickItem>
                  ))}
                </ul>
              </Section>
            )}

            {(w?.unsignedNotes.length ?? 0) > 0 && (
              <Section title={`Unsigned notes (${w?.unsignedNotes.length ?? 0})`}>
                <ul className={styles.list}>
                  {w?.unsignedNotes.map((n) => (
                    <ClickItem
                      key={n.id}
                      onOpen={() =>
                        void navigate({
                          to: '/emr/patients/$patientId',
                          params: { patientId: n.patientId },
                          search: { tab: 'notes' },
                        })
                      }
                    >
                      <Text weight="semibold">{n.patientName}</Text>
                      <Caption1 className={styles.muted}>
                        {n.title ?? noteTypeLabel[n.type]} · {formatRelative(n.createdAt)}
                      </Caption1>
                    </ClickItem>
                  ))}
                </ul>
              </Section>
            )}

            {(w?.chartDeficiencies.length ?? 0) > 0 && (
              <Section
                title={`Chart deficiencies (${w?.chartDeficiencies.length ?? 0})`}
                description="Raised by medical records; complete them in the chart"
              >
                <ul className={styles.list}>
                  {w?.chartDeficiencies.map((d) => (
                    <li key={d.id} className={styles.item}>
                      <span className={styles.itemRow}>
                        <Text weight="semibold">{d.patientName}</Text>
                        {canWrite && (
                          <Button
                            size="small"
                            appearance="subtle"
                            icon={<CheckmarkRegular />}
                            disabled={resolveDeficiency.isPending}
                            onClick={() => resolveDeficiency.mutate(d.id)}
                          >
                            Done
                          </Button>
                        )}
                      </span>
                      <Caption1>{deficiencyLabel[d.type]}</Caption1>
                      <Caption1 className={styles.muted}>
                        {d.description}
                        {d.dueDate ? ` · due ${formatDateOnly(d.dueDate)}` : ''}
                      </Caption1>
                      <Button size="small" appearance="transparent" onClick={() => openChart(d.patientId)}>
                        Open chart
                      </Button>
                    </li>
                  ))}
                </ul>
              </Section>
            )}
          </div>
        </div>
      )}
      <ResultReviewDialog result={reviewing} onClose={() => setReviewing(null)} />
    </Page>
  );
}
