import { Badge, Button, Caption1, Field, Input, makeStyles, mergeClasses, SearchBox, Select, Text, tokens, Tooltip } from '@fluentui/react-components';
import { CalendarAddRegular, CalendarLtrRegular, ChevronLeftRegular, ChevronRightRegular, VideoRegular } from '@fluentui/react-icons';
import { formatInr, formatTime, useDebouncedValue, useHotkey, useRealtimeEvent, useSession } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useRef, useState } from 'react';
import { toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import {
  APPOINTMENT_STATUSES,
  APPOINTMENTS_PAGE_SIZE,
  schedulingKeys,
  schedulingQueries,
  type AppointmentDaySummary,
  type AppointmentListItem,
  type AppointmentStatus,
} from '@/modules/scheduling/api';
import { AppointmentRowActions } from '@/modules/scheduling/AppointmentActions';
import { AppointmentDrawer } from '@/modules/scheduling/AppointmentDrawer';
import { isOpen } from '@/modules/scheduling/appointmentRules';
import { addDays, formatDayLong, isIsoDate, relativeDayLabel } from '@/modules/scheduling/dates';
import { appointmentStatusText, channelLabel, paymentLabel, visitTypeLabel } from '@/modules/scheduling/labels';
import { useAppointmentDesk } from '@/modules/scheduling/useAppointmentDesk';
import { useToday } from '@/modules/scheduling/useNow';

interface DeskSearch {
  date?: string;
  departmentId?: string;
  practitionerId?: string;
  status?: AppointmentStatus;
  q?: string;
  page?: number;
  /** Appointment shown in the side panel (an id only, never patient data). */
  open?: string;
}

export const Route = createFileRoute('/_app/scheduling/')({
  validateSearch: (s: Record<string, unknown>): DeskSearch => ({
    date: isIsoDate(s.date) ? s.date : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    practitionerId: typeof s.practitionerId === 'string' && s.practitionerId ? s.practitionerId : undefined,
    status: APPOINTMENT_STATUSES.includes(s.status as AppointmentStatus) ? (s.status as AppointmentStatus) : undefined,
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
    open: typeof s.open === 'string' && s.open ? s.open : undefined,
  }),
  component: AppointmentsDeskPage,
});

const useStyles = makeStyles({
  toolbar: { padding: '0 16px 12px', display: 'flex', flexDirection: 'column', gap: '12px' },
  dateNav: { display: 'flex', alignItems: 'end', gap: '4px' },
  statuses: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  statusPill: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '4px 10px',
    borderRadius: tokens.borderRadiusCircular,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    cursor: 'pointer',
    fontFamily: 'inherit',
    fontSize: tokens.fontSizeBase200,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  statusPillActive: {
    backgroundColor: tokens.colorBrandBackground2,
    border: `1px solid ${tokens.colorBrandStroke1}`,
    color: tokens.colorBrandForeground2,
  },
  count: { fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  late: { color: tokens.colorPaletteRedForeground1 },
  cellStack: { display: 'flex', flexDirection: 'column', minWidth: 0 },
});

type StatusFilter = { key: AppointmentStatus | 'all'; label: string; count: (s: AppointmentDaySummary) => number };

const STATUS_FILTERS: StatusFilter[] = [
  { key: 'all', label: 'All', count: (s) => s.total },
  { key: 'Booked', label: 'Booked', count: (s) => s.booked },
  { key: 'Confirmed', label: 'Confirmed', count: (s) => s.confirmed },
  { key: 'CheckedIn', label: 'Checked in', count: (s) => s.checkedIn },
  { key: 'InConsultation', label: 'With doctor', count: (s) => s.inConsultation },
  { key: 'Completed', label: 'Completed', count: (s) => s.completed },
  { key: 'NoShow', label: 'No-show', count: (s) => s.noShow },
  { key: 'Cancelled', label: 'Cancelled', count: (s) => s.cancelled },
];

/** Front-desk appointment board for a day (PMS-02/03): live, filterable, with check-in, confirm, reschedule and cancel. */
function AppointmentsDeskPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const facilityId = useSession((s) => s.user?.facilityId);
  const canBook = usePermission('scheduling.appointments.book');
  const desk = useAppointmentDesk();
  const today = useToday();
  const date = search.date ?? today;
  const departments = useDepartments('Clinical');
  const doctors = usePractitioners(search.departmentId ?? null);
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const searchBox = useRef<HTMLInputElement>(null);

  const filters = { date, departmentId: search.departmentId, practitionerId: search.practitionerId, status: search.status, q: search.q, page: search.page };
  const appointments = useQuery(schedulingQueries.appointments(filters));
  const summary = useQuery(schedulingQueries.summary(date, search.departmentId));

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: schedulingKeys.appointments });
    void queryClient.invalidateQueries({ queryKey: schedulingKeys.summary });
    void queryClient.invalidateQueries({ queryKey: ['scheduling', 'appointment'] });
  };
  // Changes from other desks, the portal and check-ins made on the visits screen.
  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'appointments.changed', refresh);
  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'visits.changed', refresh);

  const set = (patch: Partial<DeskSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const setDate = (value: string) => set({ date: value === today ? undefined : value });
  const book = () =>
    canBook && void navigate({ to: '/scheduling/book', search: { departmentId: search.departmentId, practitionerId: search.practitionerId, date: date > today ? date : undefined } });

  useHotkey('f2', book, { allowInInputs: true });
  useHotkey('alt+arrowleft', () => setDate(addDays(date, -1)));
  useHotkey('alt+arrowright', () => setDate(addDays(date, 1)));
  useHotkey('/', () => searchBox.current?.focus());

  const openAppointment = (id: string | undefined) => void navigate({ search: (prev) => ({ ...prev, open: id }), replace: true });

  return (
    <Page>
      <PageHeader
        title="Appointments"
        subtitle={`${formatDayLong(date)}${date === today ? ' · updates live' : ''}`}
        actions={
          <>
            <Button icon={<CalendarLtrRegular />} onClick={() => void navigate({ to: '/scheduling/calendar', search: { date: search.date, departmentId: search.departmentId, practitionerId: search.practitionerId } })}>
              Calendar
            </Button>
            {canBook && (
              <Tooltip content="Book appointment (F2)" relationship="description">
                <Button appearance="primary" icon={<CalendarAddRegular />} onClick={book} aria-keyshortcuts="F2">
                  Book appointment
                </Button>
              </Tooltip>
            )}
          </>
        }
      />

      <Section flush>
        <div className={styles.toolbar}>
          <FilterBar>
            <div className={styles.dateNav}>
              <Tooltip content="Previous day (Alt+Left)" relationship="label">
                <Button icon={<ChevronLeftRegular />} onClick={() => setDate(addDays(date, -1))} />
              </Tooltip>
              <Field label="Date">
                <Input type="date" value={date} onChange={(_, d) => isIsoDate(d.value) && setDate(d.value)} />
              </Field>
              <Tooltip content="Next day (Alt+Right)" relationship="label">
                <Button icon={<ChevronRightRegular />} onClick={() => setDate(addDays(date, 1))} />
              </Tooltip>
              {date !== today && (
                <Button appearance="subtle" onClick={() => setDate(today)}>
                  Today
                </Button>
              )}
            </div>
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
            <Field label="Search" style={{ flex: '1 1 240px' }}>
              <SearchBox
                ref={searchBox}
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') {
                    void navigate({ search: (prev) => ({ ...prev, q: term.trim() || undefined, page: undefined }), replace: true });
                  }
                }}
                placeholder="Name, UHID, mobile or appointment number ( / )"
              />
            </Field>
          </FilterBar>

          <div className={styles.statuses} role="group" aria-label="Filter by status">
            {STATUS_FILTERS.map((f) => {
              const active = (search.status ?? 'all') === f.key;
              return (
                <button
                  key={f.key}
                  type="button"
                  aria-pressed={active}
                  className={mergeClasses(styles.statusPill, active && styles.statusPillActive)}
                  onClick={() => set({ status: f.key === 'all' ? undefined : f.key })}
                >
                  {f.label}
                  <span className={styles.count}>{summary.data ? f.count(summary.data) : '…'}</span>
                </button>
              );
            })}
            {summary.data && summary.data.telehealth > 0 && (
              <Badge appearance="tint" color="brand" icon={<VideoRegular />}>
                {summary.data.telehealth} video
              </Badge>
            )}
          </div>
        </div>

        <DataTable
          ariaLabel={`Appointments on ${formatDayLong(date)}`}
          rows={appointments.data?.items}
          loading={appointments.isLoading}
          error={appointments.error}
          onRetry={() => void appointments.refetch()}
          getRowId={(a) => a.id}
          onRowClick={(a) => openAppointment(a.id)}
          columns={[
            {
              key: 'time',
              header: 'Time',
              width: 110,
              render: (a) => <TimeCell appointment={a} now={desk.context.now} />,
            },
            {
              key: 'patient',
              header: 'Patient',
              render: (a) => (
                <span className={styles.cellStack}>
                  <Text weight="semibold" truncate>
                    {a.patientName}{' '}
                    <Text size={200} weight="regular">
                      ({a.age ?? '—'} / {a.gender.charAt(0)})
                    </Text>
                  </Text>
                  <Caption1 className={styles.muted}>
                    {a.uhid}
                    {a.phoneMasked ? ` · ${a.phoneMasked}` : ''}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'doctor',
              header: 'Doctor',
              render: (a) => (
                <span className={styles.cellStack}>
                  <Text truncate>{a.practitionerName}</Text>
                  <Caption1 className={styles.muted}>{[a.departmentName, a.room ? `Room ${a.room}` : null].filter(Boolean).join(' · ')}</Caption1>
                </span>
              ),
            },
            {
              key: 'type',
              header: 'Visit',
              priority: 'low',
              render: (a) => (
                <span className={styles.cellStack}>
                  <Text>
                    {visitTypeLabel[a.visitType]}
                    {a.isTelehealth && (
                      <Badge size="small" appearance="tint" color="brand" icon={<VideoRegular />} style={{ marginLeft: 6 }}>
                        Video
                      </Badge>
                    )}
                  </Text>
                  <Caption1 className={styles.muted}>{channelLabel[a.channel]}</Caption1>
                </span>
              ),
            },
            {
              key: 'payment',
              header: 'Fee',
              priority: 'low',
              width: 130,
              render: (a) => (
                <span className={styles.cellStack}>
                  <Text>{formatInr(a.feeAmount, { whole: true })}</Text>
                  <Caption1 className={styles.muted}>{paymentLabel[a.paymentStatus]}</Caption1>
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Status',
              width: 130,
              render: (a) => <StatusBadge status={a.status} tone={appointmentStatusText[a.status].tone} label={appointmentStatusText[a.status].label} />,
            },
          ]}
          rowActions={(a) => <AppointmentRowActions appointment={a} desk={desk} />}
          emptyTitle={
            search.q || search.status || search.practitionerId || search.departmentId
              ? 'No appointments match'
              : date === today
                ? 'No appointments today'
                : `No appointments on ${relativeDayLabel(date, today)}`
          }
          emptyDescription={canBook ? 'Book an appointment, or change the filters above.' : 'Change the filters above to see other days or doctors.'}
          emptyAction={
            canBook ? (
              <Button appearance="primary" icon={<CalendarAddRegular />} onClick={book}>
                Book appointment
              </Button>
            ) : undefined
          }
          page={search.page ?? 1}
          pageSize={APPOINTMENTS_PAGE_SIZE}
          totalCount={appointments.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>

      <AppointmentDrawer appointmentId={search.open ?? null} onClose={() => openAppointment(undefined)} onOpenAppointment={openAppointment} desk={desk} />
      {desk.dialogs}
    </Page>
  );
}

/** Appointment time; open bookings more than 15 minutes past their time are marked "Not arrived". */
function TimeCell({ appointment, now }: { appointment: AppointmentListItem; now: number }) {
  const styles = useStyles();
  const late = isOpen(appointment.status) && now - Date.parse(appointment.startsAt) > 15 * 60_000;
  return (
    <span className={styles.cellStack}>
      <Text weight="semibold">{formatTime(appointment.startsAt)}</Text>
      {late && <Caption1 className={styles.late}>Not arrived</Caption1>}
    </span>
  );
}
