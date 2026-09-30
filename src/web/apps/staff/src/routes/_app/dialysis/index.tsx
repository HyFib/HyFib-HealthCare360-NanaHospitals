import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  Tab,
  TabList,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { BroomRegular, CalendarAddRegular, ChevronLeftRegular, ChevronRightRegular } from '@fluentui/react-icons';
import { todayIso, useHotkey } from '@hc360/core';
import {
  DataTable,
  EmptyState,
  ErrorState,
  FilterBar,
  KpiCard,
  LoadingState,
  Page,
  PageHeader,
  Section,
} from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  dialysisKeys,
  dialysisQueries,
  DIALYSIS_SHIFTS,
  SERO_STATUSES,
  type DialysisBoardMachine,
  type DialysisDuePatient,
  type DialysisSessionListItem,
  type DialysisShift,
} from '@/modules/dialysis/api';
import {
  DisinfectionState,
  MachineStatusBadge,
  SeroBadge,
  SessionStatusBadge,
} from '@/modules/dialysis/DialysisBadges';
import { AssignMachineDialog, DisinfectionDialog, ScheduleSessionDialog } from '@/modules/dialysis/DialysisDialogs';
import { seroLabel, shiftLabel } from '@/modules/dialysis/labels';
import { addDays, dayLabel, isDateOnly } from '@/modules/ot/shared/datetime';
import { NoAccess } from '@/modules/ot/shared/feedback';
import { useFacilityId, useLiveInvalidation } from '@/modules/ot/shared/hooks';

interface BoardSearch {
  date?: string;
  shift?: DialysisShift;
}

export const Route = createFileRoute('/_app/dialysis/')({
  validateSearch: (s: Record<string, unknown>): BoardSearch => ({
    date: isDateOnly(s.date) ? s.date : undefined,
    shift: DIALYSIS_SHIFTS.includes(s.shift as DialysisShift) ? (s.shift as DialysisShift) : undefined,
  }),
  component: DialysisBoardPage,
});

const useStyles = makeStyles({
  toolbar: { padding: '0 16px 12px' },
  nav: { display: 'flex', gap: '4px', alignItems: 'end' },
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))' },
  tiles: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(230px, 1fr))' },
  tile: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    padding: '12px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  tileDirty: { border: `2px solid ${tokens.colorPaletteDarkOrangeBorder2}` },
  tileOff: { opacity: 0.7, backgroundColor: tokens.colorNeutralBackground3 },
  tileHead: { display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: '8px' },
  code: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightBold },
  sessions: { display: 'flex', flexDirection: 'column', gap: '4px' },
  session: {
    display: 'grid',
    gridTemplateColumns: 'auto minmax(0, 1fr) auto',
    gap: '6px',
    alignItems: 'center',
    textAlign: 'start',
    padding: '4px 6px',
    borderRadius: tokens.borderRadiusMedium,
    border: 'none',
    backgroundColor: tokens.colorNeutralBackground2,
    color: tokens.colorNeutralForeground1,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  truncate: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  group: { display: 'flex', flexDirection: 'column', gap: '8px' },
  groupHead: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
});

function DialysisBoardPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const facilityId = useFacilityId();
  const canRead = usePermission('dialysis.read');
  const canManage = usePermission('dialysis.sessions.manage');
  const today = todayIso();
  const date = search.date ?? today;
  const board = useQuery({ ...dialysisQueries.board(date, search.shift), enabled: canRead });
  const [booking, setBooking] = useState<{
    patient?: { id: string; uhid: string; fullName: string };
    shift?: DialysisShift;
  } | null>(null);
  const [assigning, setAssigning] = useState<DialysisSessionListItem | null>(null);
  const [disinfecting, setDisinfecting] = useState<{ id: string; code: string } | null>(null);

  useLiveInvalidation(facilityId ? `facility:${facilityId}` : null, 'dialysis.board.changed', [
    dialysisKeys.boards,
    dialysisKeys.sessionLists,
  ]);

  const set = (patch: Partial<BoardSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }) });
  const go = (days: number) => {
    const next = addDays(date, days);
    set({ date: next === today ? undefined : next });
  };
  useHotkey('alt+arrowleft', () => go(-1));
  useHotkey('alt+arrowright', () => go(1));

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="the dialysis board" />
      </Page>
    );
  }

  const data = board.data;
  const sessions = [...(data?.machines.flatMap((m) => m.sessions) ?? []), ...(data?.unassigned ?? [])];
  const count = (status: string) => sessions.filter((s) => s.status === status).length;
  const dirty = data?.machines.filter((m) => m.needsDisinfection).length ?? 0;
  const openSession = (s: DialysisSessionListItem) =>
    void navigate({ to: '/dialysis/sessions/$id', params: { id: s.id } });

  return (
    <Page>
      <PageHeader
        title="Dialysis board"
        subtitle={`${dayLabel(date)}${date === today ? ' · today' : ''}${search.shift ? ` · ${shiftLabel[search.shift]} shift` : ''}. Machines are grouped by the sero category they are dedicated to.`}
        actions={
          canManage && (
            <Button appearance="primary" icon={<CalendarAddRegular />} onClick={() => setBooking({})}>
              Book session
            </Button>
          )
        }
      />

      <Section flush>
        <div className={styles.toolbar}>
          <FilterBar>
            <div className={styles.nav}>
              <Tooltip content="Previous day (Alt+←)" relationship="label">
                <Button icon={<ChevronLeftRegular />} onClick={() => go(-1)} />
              </Tooltip>
              <Button onClick={() => set({ date: undefined })} disabled={date === today}>
                Today
              </Button>
              <Tooltip content="Next day (Alt+→)" relationship="label">
                <Button icon={<ChevronRightRegular />} onClick={() => go(1)} />
              </Tooltip>
            </div>
            <Field label="Date">
              <Input
                type="date"
                value={date}
                onChange={(_, d) => set({ date: d.value && d.value !== today ? d.value : undefined })}
              />
            </Field>
            <Field label="Shift">
              <TabList
                size="small"
                selectedValue={search.shift ?? 'all'}
                onTabSelect={(_, d) => set({ shift: d.value === 'all' ? undefined : (d.value as DialysisShift) })}
              >
                <Tab value="all">All shifts</Tab>
                {DIALYSIS_SHIFTS.map((s) => (
                  <Tab key={s} value={s}>
                    {shiftLabel[s]}
                  </Tab>
                ))}
              </TabList>
            </Field>
          </FilterBar>
        </div>
      </Section>

      {board.isLoading ? (
        <LoadingState label="Loading the board…" />
      ) : board.error && !data ? (
        <ErrorState error={board.error} onRetry={() => void board.refetch()} />
      ) : data ? (
        <>
          <div className={styles.kpis}>
            <KpiCard label="Scheduled" value={count('Scheduled')} />
            <KpiCard label="On dialysis" value={count('InProgress')} />
            <KpiCard label="Completed" value={count('Completed')} />
            <KpiCard label="Missed" value={count('Missed')} hint="Did not come" />
            <KpiCard label="Need disinfection" value={dirty} icon={<BroomRegular />} hint="Before the next patient" />
          </div>

          {data.machines.length === 0 ? (
            <EmptyState
              title="No active dialysis machines"
              description="Add machines under Dialysis → Machines, each dedicated to one sero category."
            />
          ) : (
            SERO_STATUSES.filter((sero) => data.machines.some((m) => m.seroDedication === sero)).map((sero) => (
              <Section
                key={sero}
                title={
                  <span className={styles.groupHead}>
                    <SeroBadge sero={sero} long size="large" />
                    <span>machines</span>
                  </span>
                }
                description={`Only ${seroLabel[sero].toLowerCase()} patients may be dialysed on these machines.`}
              >
                <div className={styles.tiles}>
                  {data.machines
                    .filter((m) => m.seroDedication === sero)
                    .map((machine) => (
                      <MachineTile
                        key={machine.id}
                        machine={machine}
                        canManage={canManage}
                        onOpen={openSession}
                        onDisinfect={() => setDisinfecting({ id: machine.id, code: machine.code })}
                      />
                    ))}
                </div>
              </Section>
            ))
          )}

          <Section
            title={`Waiting for a machine (${data.unassigned.length})`}
            description="Booked sessions without a machine. Assign one of the patient’s sero category."
            flush
          >
            <DataTable
              ariaLabel="Sessions without a machine"
              rows={data.unassigned}
              getRowId={(s) => s.id}
              onRowClick={openSession}
              size="small"
              columns={[
                { key: 'patient', header: 'Patient', render: (s) => <Text weight="semibold">{s.patientName}</Text> },
                { key: 'uhid', header: 'UHID', priority: 'low', render: (s) => <Text font="monospace">{s.uhid}</Text> },
                { key: 'shift', header: 'Shift', render: (s) => shiftLabel[s.shift] },
                { key: 'status', header: 'Status', render: (s) => <SessionStatusBadge status={s.status} /> },
              ]}
              rowActions={
                canManage
                  ? (s) =>
                      s.status === 'Scheduled' ? (
                        <Button size="small" onClick={() => setAssigning(s)}>
                          Assign machine
                        </Button>
                      ) : null
                  : undefined
              }
              emptyTitle="Every booked session has a machine"
            />
          </Section>

          <Section
            title={`Due today, not booked (${data.dueNotScheduled.length})`}
            description="Enrolled patients whose weekly schedule includes this day."
            flush
          >
            <DataTable<DialysisDuePatient>
              ariaLabel="Patients due but not booked"
              rows={data.dueNotScheduled}
              getRowId={(p) => `${p.patientId}-${p.shift}`}
              onRowClick={(p) =>
                void navigate({ to: '/dialysis/patients/$patientId', params: { patientId: p.patientId } })
              }
              size="small"
              columns={[
                { key: 'patient', header: 'Patient', render: (p) => <Text weight="semibold">{p.patientName}</Text> },
                { key: 'uhid', header: 'UHID', priority: 'low', render: (p) => <Text font="monospace">{p.uhid}</Text> },
                {
                  key: 'sero',
                  header: 'Sero status',
                  render: (p) => (
                    <span>
                      <SeroBadge sero={p.seroStatus} />
                      {p.seroRescreeningDue && (
                        <Caption1 block className={styles.muted}>
                          Re-screening due
                        </Caption1>
                      )}
                    </span>
                  ),
                },
                { key: 'shift', header: 'Usual shift', render: (p) => shiftLabel[p.shift] },
              ]}
              rowActions={
                canManage
                  ? (p) => (
                      <Button
                        size="small"
                        icon={<CalendarAddRegular />}
                        onClick={() =>
                          setBooking({
                            patient: { id: p.patientId, uhid: p.uhid, fullName: p.patientName },
                            shift: p.shift,
                          })
                        }
                      >
                        Book
                      </Button>
                    )
                  : undefined
              }
              emptyTitle="Everyone due is booked"
            />
          </Section>
        </>
      ) : null}

      {booking && (
        <ScheduleSessionDialog
          onClose={() => setBooking(null)}
          preset={{ patient: booking.patient, shift: booking.shift, date: date < today ? today : date }}
        />
      )}
      {assigning && <AssignMachineDialog session={assigning} onClose={() => setAssigning(null)} />}
      {disinfecting && <DisinfectionDialog machine={disinfecting} onClose={() => setDisinfecting(null)} />}
    </Page>
  );
}

function MachineTile({
  machine,
  canManage,
  onOpen,
  onDisinfect,
}: {
  machine: DialysisBoardMachine;
  canManage: boolean;
  onOpen: (session: DialysisSessionListItem) => void;
  onDisinfect: () => void;
}) {
  const styles = useStyles();
  const off = machine.status === 'Maintenance' || machine.status === 'Retired';
  const sessions = [...machine.sessions].sort(
    (a, b) => DIALYSIS_SHIFTS.indexOf(a.shift) - DIALYSIS_SHIFTS.indexOf(b.shift),
  );
  return (
    <div
      className={mergeClasses(styles.tile, machine.needsDisinfection && styles.tileDirty, off && styles.tileOff)}
      aria-label={`Machine ${machine.code}`}
    >
      <div className={styles.tileHead}>
        <div>
          <span className={styles.code}>{machine.code}</span>
          {machine.station && (
            <Caption1 block className={styles.muted}>
              {machine.station}
            </Caption1>
          )}
        </div>
        <MachineStatusBadge status={machine.status} />
      </div>
      <SeroBadge sero={machine.seroDedication} size="small" long />
      <DisinfectionState needsDisinfection={machine.needsDisinfection} />
      <div className={styles.sessions}>
        {sessions.length === 0 ? (
          <Caption1 className={styles.muted}>{off ? 'Out of service' : 'Free all day'}</Caption1>
        ) : (
          sessions.map((s) => (
            <button
              key={s.id}
              type="button"
              className={styles.session}
              onClick={() => onOpen(s)}
              aria-label={`${shiftLabel[s.shift]}: ${s.patientName}`}
            >
              <Caption1 className={styles.muted}>{shiftLabel[s.shift]}</Caption1>
              <Text className={styles.truncate} weight="semibold">
                {s.patientName}
              </Text>
              <SessionStatusBadge status={s.status} />
            </button>
          ))
        )}
      </div>
      {canManage && machine.needsDisinfection && machine.status !== 'InUse' && (
        <Button size="small" icon={<BroomRegular />} onClick={onDisinfect}>
          Record disinfection
        </Button>
      )}
    </div>
  );
}
