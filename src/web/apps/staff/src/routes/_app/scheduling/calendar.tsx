import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  ProgressBar,
  Select,
  Tab,
  TabList,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { CalendarAddRegular, ChevronLeftRegular, ChevronRightRegular, VideoRegular } from '@fluentui/react-icons';
import { formatTime, useHotkey, useRealtimeEvent, useSession } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo, useState, type CSSProperties } from 'react';
import { toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { schedulingKeys, schedulingQueries, type AppointmentStatus, type CalendarAppointment, type ScheduleExceptionDto, type SlotDto } from '@/modules/scheduling/api';
import { useAppointmentDesk } from '@/modules/scheduling/useAppointmentDesk';
import { AppointmentDrawer } from '@/modules/scheduling/AppointmentDrawer';
import { assignLanes, hourMarks, utilisation, visibleRange, type MinuteRange } from '@/modules/scheduling/calendarLayout';
import { addDays, dateRange, formatDayLong, formatDayShort, formatHour, isIsoDate, istDateTime, startOfWeek, timeToMinutes, type IsoDate } from '@/modules/scheduling/dates';
import { appointmentStatusText } from '@/modules/scheduling/labels';
import { restrictionsOn } from '@/modules/scheduling/slots';
import { useNow, useToday } from '@/modules/scheduling/useNow';

type CalendarView = 'day' | 'week';

interface CalendarSearch {
  view?: CalendarView;
  date?: string;
  departmentId?: string;
  practitionerId?: string;
}

export const Route = createFileRoute('/_app/scheduling/calendar')({
  validateSearch: (s: Record<string, unknown>): CalendarSearch => ({
    view: s.view === 'week' ? 'week' : undefined,
    date: isIsoDate(s.date) ? s.date : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    practitionerId: typeof s.practitionerId === 'string' && s.practitionerId ? s.practitionerId : undefined,
  }),
  component: CalendarPage,
});

/** Pixels per minute: a 15-minute slot is 30 px tall, enough for a name and a time. */
const PX = 2;

const STATUS_COLOUR: Partial<Record<AppointmentStatus, string>> = {
  Booked: tokens.colorPaletteMarigoldBorderActive,
  Confirmed: tokens.colorBrandStroke1,
  CheckedIn: tokens.colorPaletteBlueBorderActive,
  InConsultation: tokens.colorPaletteBlueBorderActive,
  Completed: tokens.colorPaletteGreenBorderActive,
  NoShow: tokens.colorPaletteRedBorderActive,
};

const useStyles = makeStyles({
  toolbar: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'end' },
  dateNav: { display: 'flex', gap: '4px', alignItems: 'end' },
  scroller: {
    overflow: 'auto',
    maxHeight: 'calc(100vh - 290px)',
    minHeight: '360px',
    position: 'relative',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  grid: { display: 'grid', position: 'relative', minWidth: 'fit-content' },
  corner: { position: 'sticky', top: '0', left: '0', zIndex: 4, backgroundColor: tokens.colorNeutralBackground1 },
  header: {
    position: 'sticky',
    top: '0',
    zIndex: 3,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    borderLeft: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: '8px',
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    minWidth: 0,
  },
  headerToday: { backgroundColor: tokens.colorBrandBackground2 },
  gutter: { position: 'sticky', left: '0', zIndex: 2, backgroundColor: tokens.colorNeutralBackground1 },
  hourLabel: { position: 'absolute', right: '6px', transform: 'translateY(-50%)', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100 },
  column: { position: 'relative', borderLeft: `1px solid ${tokens.colorNeutralStroke2}` },
  hourLine: { position: 'absolute', left: '0', right: '0', borderTop: `1px solid ${tokens.colorNeutralStroke3}` },
  slot: {
    position: 'absolute',
    left: '2px',
    right: '2px',
    borderRadius: tokens.borderRadiusSmall,
    border: `1px dashed ${tokens.colorNeutralStroke1}`,
    backgroundColor: 'transparent',
    padding: '0 4px',
    textAlign: 'start',
    fontFamily: 'inherit',
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    cursor: 'default',
  },
  slotBookable: {
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground2, border: `1px dashed ${tokens.colorBrandStroke1}` },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  slotFull: { border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground3 },
  block: {
    position: 'absolute',
    zIndex: 1,
    overflow: 'hidden',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '0',
    padding: '1px 4px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderLeftWidth: '4px',
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow2,
    color: tokens.colorNeutralForeground1,
    cursor: 'pointer',
    fontFamily: 'inherit',
    textAlign: 'start',
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    ':hover': { boxShadow: tokens.shadow8, zIndex: 2 },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, zIndex: 2 },
  },
  blockDone: { opacity: 0.7 },
  blockName: { fontWeight: tokens.fontWeightSemibold, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', maxWidth: '100%' },
  blocked: {
    position: 'absolute',
    left: '0',
    right: '0',
    zIndex: 0,
    backgroundImage: `repeating-linear-gradient(135deg, ${tokens.colorNeutralBackground4} 0 6px, transparent 6px 12px)`,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    padding: '2px 4px',
    overflow: 'hidden',
  },
  nowLine: { position: 'absolute', left: '0', right: '0', zIndex: 2, borderTop: `2px solid ${tokens.colorPaletteRedBorder2}`, pointerEvents: 'none' },
  legend: { display: 'flex', gap: '12px', flexWrap: 'wrap', alignItems: 'center', padding: '8px 16px', color: tokens.colorNeutralForeground3 },
  swatch: { display: 'inline-block', width: '10px', height: '10px', borderRadius: '2px', marginRight: '4px', verticalAlign: 'middle' },
  muted: { color: tokens.colorNeutralForeground3 },
});

interface CalendarColumn {
  key: string;
  title: string;
  subtitle?: string;
  date: IsoDate;
  practitionerId: string | null;
  slots: SlotDto[];
  appointments: CalendarAppointment[];
  exceptions: ScheduleExceptionDto[];
}

/** Day grid per doctor, or a doctor's week, with booked blocks, free and full slots, leave and blocked time. */
function CalendarPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const facilityId = useSession((s) => s.user?.facilityId);
  const canBook = usePermission('scheduling.appointments.book');
  const desk = useAppointmentDesk();
  const today = useToday();
  const date = search.date ?? today;
  const view: CalendarView = search.view === 'week' && search.practitionerId ? 'week' : 'day';
  const from = view === 'week' ? startOfWeek(date) : date;
  const to = view === 'week' ? addDays(from, 6) : date;
  const departments = useDepartments('Clinical');
  const doctorOptions = usePractitioners(search.departmentId ?? null);
  const [openId, setOpenId] = useState<string | null>(null);

  const scope = search.practitionerId ? { practitionerId: search.practitionerId } : search.departmentId ? { departmentId: search.departmentId } : null;
  const slots = useQuery({ ...schedulingQueries.slots({ ...scope, from, to }), enabled: scope !== null });
  const appointments = useQuery({ ...schedulingQueries.calendar({ ...scope, from, to }), enabled: scope !== null });
  const exceptions = useQuery({ ...schedulingQueries.exceptions({ practitionerId: search.practitionerId ?? null, from, to }), enabled: scope !== null, retry: false });

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['scheduling', 'calendar'] });
    void queryClient.invalidateQueries({ queryKey: schedulingKeys.slots });
  };
  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'appointments.changed', refresh);

  const set = (patch: Partial<CalendarSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });
  const step = view === 'week' ? 7 : 1;
  const move = (days: number) => set({ date: addDays(date, days) });
  useHotkey('alt+arrowleft', () => move(-step));
  useHotkey('alt+arrowright', () => move(step));

  const columns = useMemo<CalendarColumn[]>(() => {
    const availability = slots.data ?? [];
    const booked = appointments.data ?? [];
    const leave = exceptions.data ?? [];
    if (view === 'week') {
      const doctor = availability[0];
      return dateRange(from, 7).map((day) => ({
        key: day,
        title: formatDayShort(day),
        date: day,
        practitionerId: search.practitionerId ?? null,
        slots: (doctor?.slots ?? []).filter((s) => s.date === day),
        appointments: booked.filter((a) => istDateTime(a.startsAt).date === day),
        exceptions: leave.filter((e) => e.fromDate <= day && e.toDate >= day),
      }));
    }
    const ids = new Set(availability.map((d) => d.practitionerId));
    const extra = [...new Set(booked.map((a) => a.practitionerId))].filter((id) => !ids.has(id));
    return [
      ...availability.map((d) => ({
        key: d.practitionerId,
        title: d.practitionerName,
        subtitle: d.departmentName ?? undefined,
        date,
        practitionerId: d.practitionerId,
        slots: d.slots,
        appointments: booked.filter((a) => a.practitionerId === d.practitionerId),
        exceptions: leave.filter((e) => e.practitionerId === d.practitionerId),
      })),
      ...extra.map((id) => ({
        key: id,
        title: doctorOptions.data?.find((o) => o.id === id)?.name ?? 'Doctor',
        date,
        practitionerId: id,
        slots: [],
        appointments: booked.filter((a) => a.practitionerId === id),
        exceptions: [],
      })),
    ];
  }, [slots.data, appointments.data, exceptions.data, view, from, date, search.practitionerId, doctorOptions.data]);

  const range = useMemo(() => {
    const spans: MinuteRange[] = [];
    for (const column of columns) {
      for (const slot of column.slots) {
        spans.push({ start: timeToMinutes(slot.localStart), end: timeToMinutes(slot.localStart) + minutesBetween(slot.startsAt, slot.endsAt) });
      }
      for (const a of column.appointments) {
        const start = istDateTime(a.startsAt).minutes;
        spans.push({ start, end: start + minutesBetween(a.startsAt, a.endsAt) });
      }
    }
    return visibleRange(spans.filter((s) => !Number.isNaN(s.start)));
  }, [columns]);

  const loading = scope !== null && (slots.isLoading || appointments.isLoading);
  const error = slots.error ?? appointments.error;

  return (
    <Page>
      <PageHeader
        title="Appointment calendar"
        subtitle={view === 'week' ? `Week of ${formatDayLong(from)}` : formatDayLong(date)}
        breadcrumbs={[{ label: 'Appointments', onClick: () => void navigate({ to: '/scheduling' }) }, { label: 'Calendar' }]}
        actions={
          canBook && (
            <Button
              appearance="primary"
              icon={<CalendarAddRegular />}
              onClick={() => void navigate({ to: '/scheduling/book', search: { departmentId: search.departmentId, practitionerId: search.practitionerId, date: date > today ? date : undefined } })}
            >
              Book appointment
            </Button>
          )
        }
      />

      <Section flush>
        <div style={{ padding: '0 16px 12px' }} className={styles.toolbar}>
          <TabList selectedValue={view} onTabSelect={(_, d) => set({ view: d.value === 'week' ? 'week' : undefined })} size="small">
            <Tab value="day">Day</Tab>
            <Tab value="week" disabled={!search.practitionerId}>
              Week
            </Tab>
          </TabList>
          <div className={styles.dateNav}>
            <Tooltip content={`Previous ${view} (Alt+Left)`} relationship="label">
              <Button icon={<ChevronLeftRegular />} onClick={() => move(-step)} />
            </Tooltip>
            <Field label="Date">
              <Input type="date" value={date} onChange={(_, d) => isIsoDate(d.value) && set({ date: d.value })} />
            </Field>
            <Tooltip content={`Next ${view} (Alt+Right)`} relationship="label">
              <Button icon={<ChevronRightRegular />} onClick={() => move(step)} />
            </Tooltip>
            <Button appearance="subtle" onClick={() => set({ date: undefined })} disabled={date === today}>
              Today
            </Button>
          </div>
          <Field label="Department">
            <Select value={search.departmentId ?? ''} onChange={(_, d) => set({ departmentId: d.value || undefined, practitionerId: undefined, view: undefined })}>
              <option value="">Choose department</option>
              {toOptions(departments.data).map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Doctor" hint={search.practitionerId ? undefined : 'Choose a doctor for the week view'}>
            <Select value={search.practitionerId ?? ''} onChange={(_, d) => set({ practitionerId: d.value || undefined, view: d.value ? search.view : undefined })}>
              <option value="">All doctors</option>
              {toOptions(doctorOptions.data).map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </Select>
          </Field>
        </div>

        {scope === null ? (
          <EmptyState title="Choose a department or a doctor" description="The calendar shows each doctor's sessions, bookings and free slots." />
        ) : error ? (
          <div style={{ padding: 16 }}>
            <ErrorState error={error} onRetry={refresh} />
          </div>
        ) : loading ? (
          <LoadingState label="Loading the calendar…" />
        ) : columns.length === 0 ? (
          <EmptyState title="No OPD sessions" description="No doctor in this selection has sessions on these days. Set them up under Doctor schedules." />
        ) : (
          <>
            <CalendarGrid
              columns={columns}
              range={range}
              today={today}
              canBook={canBook}
              onOpen={setOpenId}
              onBook={(column, slot) =>
                void navigate({
                  to: '/scheduling/book',
                  search: {
                    departmentId: search.departmentId ?? slot.departmentId,
                    practitionerId: column.practitionerId ?? undefined,
                    date: slot.date,
                    video: slot.mode === 'Telehealth' ? true : undefined,
                    startsAt: slot.startsAt,
                  },
                })
              }
            />
            <Legend />
          </>
        )}
      </Section>

      <AppointmentDrawer appointmentId={openId} onClose={() => setOpenId(null)} onOpenAppointment={setOpenId} desk={desk} />
      {desk.dialogs}
    </Page>
  );
}

function minutesBetween(startIso: string, endIso: string): number {
  return Math.max(5, Math.round((Date.parse(endIso) - Date.parse(startIso)) / 60_000));
}

function CalendarGrid({
  columns,
  range,
  today,
  canBook,
  onOpen,
  onBook,
}: {
  columns: CalendarColumn[];
  range: MinuteRange;
  today: IsoDate;
  canBook: boolean;
  onOpen: (id: string) => void;
  onBook: (column: CalendarColumn, slot: SlotDto) => void;
}) {
  const styles = useStyles();
  const now = useNow();
  const nowParts = istDateTime(new Date(now).toISOString());
  const height = (range.end - range.start) * PX;
  const marks = hourMarks(range);
  const top = (minutes: number) => (minutes - range.start) * PX;
  const weekView = columns.some((c) => c.date !== columns[0]?.date);

  return (
    <div className={styles.scroller} role="region" aria-label="Appointment calendar" tabIndex={0}>
      <div className={styles.grid} style={{ gridTemplateColumns: `56px repeat(${columns.length}, minmax(150px, 1fr))` }}>
        <div className={styles.corner} />
        {columns.map((column) => {
          const use = utilisation(column.slots);
          const restrictions = restrictionsOn(column.date, column.exceptions, column.practitionerId);
          return (
            <div key={`h-${column.key}`} className={mergeClasses(styles.header, weekView && column.date === today && styles.headerToday)}>
              <Text weight="semibold" truncate wrap={false}>
                {column.title}
              </Text>
              {column.subtitle && <Caption1 className={styles.muted}>{column.subtitle}</Caption1>}
              {restrictions.some((r) => r.kind === 'leave' && r.allDay) ? (
                <Badge appearance="tint" color="danger" size="small">
                  On leave
                </Badge>
              ) : use.capacity > 0 ? (
                <>
                  <Caption1>
                    {use.booked} of {use.capacity} booked ({use.percent}%)
                  </Caption1>
                  <ProgressBar
                    value={Math.min(1, use.capacity ? use.booked / use.capacity : 0)}
                    color={use.percent >= 100 ? 'error' : use.percent >= 80 ? 'warning' : 'brand'}
                    thickness="large"
                    aria-label={`${column.title}: ${use.booked} of ${use.capacity} seats booked`}
                  />
                </>
              ) : (
                <Caption1 className={styles.muted}>No OPD</Caption1>
              )}
            </div>
          );
        })}

        <div className={styles.gutter} style={{ height }}>
          {marks.map((minute) => (
            <span key={minute} className={styles.hourLabel} style={{ top: top(minute) }}>
              {formatHour(minute)}
            </span>
          ))}
        </div>

        {columns.map((column) => {
          const lanes = assignLanes(
            column.appointments.map((a) => {
              const start = istDateTime(a.startsAt).minutes;
              return { id: a.id, start, end: start + minutesBetween(a.startsAt, a.endsAt) };
            }),
          );
          const blocked = restrictionsOn(column.date, column.exceptions, column.practitionerId);
          return (
            <div key={`c-${column.key}`} className={styles.column} style={{ height }}>
              {marks.map((minute) => (
                <div key={minute} className={styles.hourLine} style={{ top: top(minute) }} />
              ))}

              {blocked.map((r, index) => {
                const start = r.allDay ? range.start : timeToMinutes(r.start);
                const end = r.allDay ? range.end : timeToMinutes(r.end);
                return (
                  <div key={`b-${index}`} className={styles.blocked} style={{ top: top(Math.max(start, range.start)), height: Math.max(0, (Math.min(end, range.end) - Math.max(start, range.start)) * PX) }}>
                    {r.kind === 'leave' ? 'On leave' : 'Blocked'}: {r.reason}
                  </div>
                );
              })}

              {column.slots.map((slot) => {
                const start = timeToMinutes(slot.localStart);
                const style: CSSProperties = { top: top(start) + 1, height: minutesBetween(slot.startsAt, slot.endsAt) * PX - 2 };
                const full = slot.available <= 0;
                if (full || !canBook) {
                  return <div key={slot.startsAt} className={mergeClasses(styles.slot, full && styles.slotFull)} style={style} aria-hidden />;
                }
                return (
                  <button
                    key={slot.startsAt}
                    type="button"
                    className={mergeClasses(styles.slot, styles.slotBookable)}
                    style={style}
                    aria-label={`Free slot ${formatTime(slot.startsAt)} with ${column.title}. Book it.`}
                    onClick={() => onBook(column, slot)}
                  >
                    {formatTime(slot.startsAt)}
                    {slot.capacity > 1 ? ` · ${slot.available} left` : ''}
                  </button>
                );
              })}

              {column.appointments.map((a) => {
                const start = istDateTime(a.startsAt).minutes;
                const placement = lanes.get(a.id) ?? { lane: 0, lanes: 1 };
                const status = appointmentStatusText[a.status];
                const style: CSSProperties = {
                  top: top(start) + 1,
                  height: Math.max(18, minutesBetween(a.startsAt, a.endsAt) * PX - 2),
                  left: `calc(${(placement.lane / placement.lanes) * 100}% + 2px)`,
                  width: `calc(${100 / placement.lanes}% - 4px)`,
                  borderLeftColor: STATUS_COLOUR[a.status] ?? tokens.colorNeutralStroke1,
                };
                return (
                  <Tooltip
                    key={a.id}
                    relationship="description"
                    content={`${formatTime(a.startsAt)} · ${a.patientName} (${a.uhid}) · ${status.label}${a.isTelehealth ? ' · video' : ''}`}
                  >
                    <button
                      type="button"
                      className={mergeClasses(styles.block, (a.status === 'Completed' || a.status === 'NoShow') && styles.blockDone)}
                      style={style}
                      aria-label={`${formatTime(a.startsAt)}, ${a.patientName}, ${status.label}`}
                      onClick={() => onOpen(a.id)}
                    >
                      <span className={styles.blockName}>
                        {a.isTelehealth && <VideoRegular aria-hidden style={{ verticalAlign: 'middle', marginRight: 2 }} />}
                        {a.patientName}
                      </span>
                      <span>
                        {formatTime(a.startsAt)} · {status.label}
                      </span>
                    </button>
                  </Tooltip>
                );
              })}

              {column.date === nowParts.date && nowParts.minutes >= range.start && nowParts.minutes <= range.end && (
                <div className={styles.nowLine} style={{ top: top(nowParts.minutes) }} aria-hidden />
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function Legend() {
  const styles = useStyles();
  const items: { label: string; colour: string; dashed?: boolean }[] = [
    { label: 'Booked', colour: STATUS_COLOUR.Booked! },
    { label: 'Confirmed', colour: STATUS_COLOUR.Confirmed! },
    { label: 'Checked in / with doctor', colour: STATUS_COLOUR.CheckedIn! },
    { label: 'Completed', colour: STATUS_COLOUR.Completed! },
    { label: 'No-show', colour: STATUS_COLOUR.NoShow! },
  ];
  return (
    <div className={styles.legend} aria-label="Legend">
      {items.map((item) => (
        <Caption1 key={item.label}>
          <span className={styles.swatch} style={{ backgroundColor: item.colour }} />
          {item.label}
        </Caption1>
      ))}
      <Caption1>
        <span className={styles.swatch} style={{ border: `1px dashed ${tokens.colorNeutralStroke1}` }} />
        Free slot (click to book)
      </Caption1>
      <Caption1>
        <span className={styles.swatch} style={{ backgroundColor: tokens.colorNeutralBackground3, border: `1px solid ${tokens.colorNeutralStroke2}` }} />
        Full slot
      </Caption1>
    </div>
  );
}
