import { Badge, Button, Caption1, Field, makeStyles, mergeClasses, Select, Switch, Text, tokens } from '@fluentui/react-components';
import { AddRegular, CalendarCancelRegular, EditRegular, VideoRegular } from '@fluentui/react-icons';
import { DataTable, EmptyState, ErrorState, FilterBar, LoadingState, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { AffectedAppointmentsDialog } from '@/modules/scheduling/AffectedAppointmentsDialog';
import { schedulingQueries, WEEKDAYS, type AffectedAppointment, type ScheduleDto, type ScheduleExceptionDto, type ScheduleSaved } from '@/modules/scheduling/api';
import { addDays, formatClock, formatDayMedium } from '@/modules/scheduling/dates';
import { ExceptionDrawer } from '@/modules/scheduling/ExceptionDrawer';
import { exceptionTypeText, weekdayShort } from '@/modules/scheduling/labels';
import { ScheduleDrawer } from '@/modules/scheduling/ScheduleDrawer';
import { sortSessions } from '@/modules/scheduling/scheduleRules';
import { useToday } from '@/modules/scheduling/useNow';

interface SchedulesSearch {
  departmentId?: string;
  practitionerId?: string;
  inactive?: boolean;
}

export const Route = createFileRoute('/_app/scheduling/schedules')({
  validateSearch: (s: Record<string, unknown>): SchedulesSearch => ({
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    practitionerId: typeof s.practitionerId === 'string' && s.practitionerId ? s.practitionerId : undefined,
    inactive: s.inactive === true || s.inactive === 'true' ? true : undefined,
  }),
  component: SchedulesPage,
});

const useStyles = makeStyles({
  doctors: { display: 'flex', flexDirection: 'column', gap: '16px' },
  week: {
    display: 'grid',
    gridTemplateColumns: 'repeat(7, minmax(0, 1fr))',
    gap: '6px',
    '@media (max-width: 900px)': { gridTemplateColumns: 'repeat(auto-fill, minmax(140px, 1fr))' },
  },
  day: { display: 'flex', flexDirection: 'column', gap: '4px', minWidth: 0 },
  dayName: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, fontWeight: tokens.fontWeightSemibold },
  session: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderLeft: `3px solid ${tokens.colorBrandStroke1}`,
    backgroundColor: tokens.colorNeutralBackground2,
    color: tokens.colorNeutralForeground1,
    textAlign: 'start',
    fontFamily: 'inherit',
    fontSize: tokens.fontSizeBase200,
    minWidth: 0,
  },
  sessionButton: {
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground2Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  inactive: { opacity: 0.55, borderLeft: `3px solid ${tokens.colorNeutralStroke1}` },
  empty: { color: tokens.colorNeutralForeground4, fontSize: tokens.fontSizeBase200 },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Doctors' weekly OPD sessions and their leave, blocked time and extra sessions (PMS-01). */
function SchedulesPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission('scheduling.schedules.manage');
  const today = useToday();
  const departments = useDepartments('Clinical');
  const doctors = usePractitioners(search.departmentId ?? null);
  const [editing, setEditing] = useState<{ schedule: ScheduleDto | null; defaults?: { practitionerId?: string; departmentId?: string } } | null>(null);
  const [editingException, setEditingException] = useState<{ exception: ScheduleExceptionDto | null; defaults?: { practitionerId?: string } } | null>(null);
  const [affected, setAffected] = useState<AffectedAppointment[] | null>(null);

  const schedules = useQuery(
    schedulingQueries.schedules({ departmentId: search.departmentId, practitionerId: search.practitionerId, activeOnly: search.inactive ? false : undefined }),
  );
  const exceptions = useQuery(schedulingQueries.exceptions({ practitionerId: search.practitionerId ?? null, from: today, to: addDays(today, 90) }));

  const byDoctor = useMemo(() => {
    const groups = new Map<string, { name: string; departments: Set<string>; sessions: ScheduleDto[] }>();
    for (const s of schedules.data ?? []) {
      const group = groups.get(s.practitionerId) ?? { name: s.practitionerName, departments: new Set<string>(), sessions: [] };
      if (s.departmentName) {
        group.departments.add(s.departmentName);
      }
      group.sessions.push(s);
      groups.set(s.practitionerId, group);
    }
    return [...groups.entries()].map(([id, g]) => ({ id, ...g, sessions: sortSessions(g.sessions) })).sort((a, b) => a.name.localeCompare(b.name));
  }, [schedules.data]);

  // A department filter narrows exceptions to its doctors (exceptions are stored per doctor, not per department).
  const doctorIds = useMemo(() => (search.departmentId ? new Set((doctors.data ?? []).map((d) => d.id)) : null), [search.departmentId, doctors.data]);
  const visibleExceptions = (exceptions.data ?? []).filter((e) => !doctorIds || doctorIds.has(e.practitionerId));

  const set = (patch: Partial<SchedulesSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });
  const saved = (result: ScheduleSaved | null) => {
    if (result && result.affectedAppointments.length > 0) {
      setAffected(result.affectedAppointments);
    }
  };

  return (
    <Page>
      <PageHeader
        title="Doctor schedules"
        subtitle="Weekly OPD sessions, leave, blocked time and extra sessions. Slots for booking come from here."
        breadcrumbs={[{ label: 'Appointments', onClick: () => void navigate({ to: '/scheduling' }) }, { label: 'Doctor schedules' }]}
        actions={
          canManage && (
            <>
              <Button icon={<CalendarCancelRegular />} onClick={() => setEditingException({ exception: null, defaults: { practitionerId: search.practitionerId } })}>
                Add leave or block
              </Button>
              <Button
                appearance="primary"
                icon={<AddRegular />}
                onClick={() => setEditing({ schedule: null, defaults: { practitionerId: search.practitionerId, departmentId: search.departmentId } })}
              >
                Add session
              </Button>
            </>
          )
        }
      />

      <Section>
        <FilterBar>
          <Field label="Department">
            <Select value={search.departmentId ?? ''} onChange={(_, d) => set({ departmentId: d.value || undefined, practitionerId: undefined })}>
              <option value="">All departments</option>
              {toOptions(departments.data).map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Doctor">
            <Select value={search.practitionerId ?? ''} onChange={(_, d) => set({ practitionerId: d.value || undefined })}>
              <option value="">All doctors</option>
              {toOptions(doctors.data).map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </Select>
          </Field>
          <Switch checked={Boolean(search.inactive)} onChange={(_, d) => set({ inactive: d.checked || undefined })} label="Show inactive sessions" />
        </FilterBar>
      </Section>

      <Section title="Weekly sessions" description="Click a session to change it. Changes never move booked patients; they are listed for the desk.">
        {schedules.isLoading ? (
          <LoadingState />
        ) : schedules.error ? (
          <ErrorState error={schedules.error} onRetry={() => void schedules.refetch()} />
        ) : byDoctor.length === 0 ? (
          <EmptyState
            title="No sessions set up"
            description={canManage ? 'Add each doctor\'s OPD sessions so patients can be booked.' : 'Ask an administrator to set up the doctors\' OPD sessions.'}
            action={
              canManage ? (
                <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing({ schedule: null, defaults: { departmentId: search.departmentId, practitionerId: search.practitionerId } })}>
                  Add session
                </Button>
              ) : undefined
            }
          />
        ) : (
          <div className={styles.doctors}>
            {byDoctor.map((doctor) => (
              <div key={doctor.id}>
                <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap', marginBottom: 6 }}>
                  <Text weight="semibold">{doctor.name}</Text>
                  <Caption1 className={styles.muted}>{[...doctor.departments].join(', ')}</Caption1>
                  {canManage && (
                    <Button size="small" appearance="transparent" icon={<AddRegular />} onClick={() => setEditing({ schedule: null, defaults: { practitionerId: doctor.id, departmentId: doctor.sessions[0]?.departmentId } })}>
                      Add session
                    </Button>
                  )}
                </div>
                <div className={styles.week}>
                  {WEEKDAYS.map((day) => {
                    const sessions = doctor.sessions.filter((s) => s.weekday === day);
                    return (
                      <div key={day} className={styles.day}>
                        <span className={styles.dayName}>{weekdayShort[day]}</span>
                        {sessions.length === 0 ? (
                          <span className={styles.empty}>—</span>
                        ) : (
                          sessions.map((s) => {
                            const content = (
                              <>
                                <Text weight="semibold" size={200}>
                                  {formatClock(s.startTime)} – {formatClock(s.endTime)}
                                </Text>
                                <span>
                                  {s.slotsPerSession} × {s.slotMinutes} min{s.capacityPerSlot > 1 ? ` · ${s.capacityPerSlot}/slot` : ''}
                                  {s.room ? ` · ${s.room}` : ''}
                                </span>
                                <span>
                                  {s.mode !== 'InPerson' && (
                                    <Badge size="small" appearance="tint" color="brand" icon={<VideoRegular />}>
                                      {s.mode === 'Both' ? 'Video too' : 'Video only'}
                                    </Badge>
                                  )}
                                  {!s.isActive && (
                                    <Badge size="small" appearance="tint">
                                      Inactive
                                    </Badge>
                                  )}
                                  {s.effectiveTo && <Caption1 className={styles.muted}> until {formatDayMedium(s.effectiveTo)}</Caption1>}
                                  {s.effectiveFrom > today && <Caption1 className={styles.muted}> from {formatDayMedium(s.effectiveFrom)}</Caption1>}
                                </span>
                              </>
                            );
                            return canManage ? (
                              <button
                                key={s.id}
                                type="button"
                                className={mergeClasses(styles.session, styles.sessionButton, !s.isActive && styles.inactive)}
                                onClick={() => setEditing({ schedule: s })}
                                aria-label={`${s.practitionerName}, ${s.weekday} ${formatClock(s.startTime)} to ${formatClock(s.endTime)}. Edit.`}
                              >
                                {content}
                              </button>
                            ) : (
                              <div key={s.id} className={mergeClasses(styles.session, !s.isActive && styles.inactive)}>
                                {content}
                              </div>
                            );
                          })
                        )}
                      </div>
                    );
                  })}
                </div>
              </div>
            ))}
          </div>
        )}
      </Section>

      <Section title="Leave, blocked time and extra sessions" description="Next 90 days" flush>
        <DataTable
          ariaLabel="Leave, blocked time and extra sessions"
          rows={visibleExceptions}
          loading={exceptions.isLoading}
          error={exceptions.error}
          onRetry={() => void exceptions.refetch()}
          getRowId={(e) => e.id}
          onRowClick={canManage ? (e) => setEditingException({ exception: e }) : undefined}
          columns={[
            { key: 'doctor', header: 'Doctor', render: (e) => e.practitionerName },
            {
              key: 'type',
              header: 'What',
              width: 140,
              render: (e) => <StatusBadge status={e.type} tone={exceptionTypeText[e.type].tone} label={exceptionTypeText[e.type].label} />,
            },
            {
              key: 'dates',
              header: 'Dates',
              render: (e) => (e.fromDate === e.toDate ? formatDayMedium(e.fromDate) : `${formatDayMedium(e.fromDate)} – ${formatDayMedium(e.toDate)}`),
            },
            { key: 'time', header: 'Time', width: 160, render: (e) => (e.isAllDay ? 'Whole day' : `${formatClock(e.startTime)} – ${formatClock(e.endTime)}`) },
            { key: 'reason', header: 'Reason', priority: 'low', render: (e) => e.reason },
            { key: 'where', header: 'Applies at', priority: 'low', width: 130, render: (e) => (e.facilityId ? 'This branch' : 'All branches') },
          ]}
          rowActions={
            canManage
              ? (e) => (
                  <Button size="small" appearance="subtle" icon={<EditRegular />} aria-label={`Edit ${exceptionTypeText[e.type].label.toLowerCase()} of ${e.practitionerName}`} onClick={() => setEditingException({ exception: e })} />
                )
              : undefined
          }
          emptyTitle="No leave or blocked time planned"
          emptyDescription={canManage ? 'Add leave before it starts so that no one is booked in it.' : undefined}
          size="small"
        />
      </Section>

      <ScheduleDrawer open={editing !== null} schedule={editing?.schedule ?? null} defaults={editing?.defaults} today={today} onClose={() => setEditing(null)} onSaved={saved} />
      <ExceptionDrawer
        open={editingException !== null}
        exception={editingException?.exception ?? null}
        defaults={editingException?.defaults}
        today={today}
        onClose={() => setEditingException(null)}
        onSaved={saved}
      />
      <AffectedAppointmentsDialog affected={affected} onClose={() => setAffected(null)} />
    </Page>
  );
}
