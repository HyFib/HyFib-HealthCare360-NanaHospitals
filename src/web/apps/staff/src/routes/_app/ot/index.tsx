import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  Select,
  Switch,
  Tab,
  TabList,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { CalendarAddRegular, ChevronLeftRegular, ChevronRightRegular, PrintRegular } from '@fluentui/react-icons';
import { formatDateOnly, todayIso, useHotkey, useSession } from '@hc360/core';
import { DataTable, ErrorState, FilterBar, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useLetterhead } from '@/lib/letterhead';
import { usePermission } from '@/lib/permissions';
import { otKeys, otQueries, type OtCalendarBooking } from '@/modules/ot/api';
import { procedureWithSide } from '@/modules/ot/logic';
import { PriorityBadge, SurgeryStatusBadge } from '@/modules/ot/OtBadges';
import { DayBoard, WeekBoard } from '@/modules/ot/OtCalendar';
import { printOtList } from '@/modules/ot/print';
import { addDays, dayLabel, isDateOnly, startOfWeek } from '@/modules/ot/shared/datetime';
import { NoAccess } from '@/modules/ot/shared/feedback';
import { useFacilityId, useLiveInvalidation, useNow } from '@/modules/ot/shared/hooks';
import { UtilisationPanel } from '@/modules/ot/UtilisationPanel';

interface OtSearch {
  date?: string;
  view?: 'week';
  theatreId?: string;
  surgeonId?: string;
}

export const Route = createFileRoute('/_app/ot/')({
  validateSearch: (s: Record<string, unknown>): OtSearch => ({
    date: isDateOnly(s.date) ? s.date : undefined,
    view: s.view === 'week' ? 'week' : undefined,
    theatreId: typeof s.theatreId === 'string' && s.theatreId ? s.theatreId : undefined,
    surgeonId: typeof s.surgeonId === 'string' && s.surgeonId ? s.surgeonId : undefined,
  }),
  component: OtCalendarPage,
});

const useStyles = makeStyles({
  toolbar: { padding: '0 16px 12px' },
  nav: { display: 'flex', gap: '4px', alignItems: 'end' },
  legend: { display: 'flex', gap: '12px', flexWrap: 'wrap', alignItems: 'center', padding: '8px 16px' },
  swatch: {
    display: 'inline-block',
    width: '14px',
    height: '14px',
    borderRadius: '3px',
    marginRight: '6px',
    verticalAlign: 'middle',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

type UtilisationPeriod = 'shown' | '30d';

function OtCalendarPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const facilityId = useFacilityId();
  const letterhead = useLetterhead();
  const practitionerId = useSession((s) => s.user?.practitionerId ?? null);
  const canRead = usePermission('ot.read');
  const canBook = usePermission('ot.bookings.manage');
  const canReport = usePermission('ot.reports.read');
  const now = useNow(60_000);
  const [period, setPeriod] = useState<UtilisationPeriod>('shown');

  const today = todayIso();
  const date = search.date ?? today;
  const view = search.view ?? 'day';
  const theatres = useQuery({ ...otQueries.theatres(), enabled: canRead });
  const calendar = useQuery({
    ...otQueries.calendar({ date, view, theatreId: search.theatreId, surgeonId: search.surgeonId }),
    enabled: canRead,
  });

  useLiveInvalidation(facilityId ? `ot:${facilityId}` : null, 'ot.booking.changed', [
    otKeys.calendars,
    ['ot', 'utilisation'],
  ]);

  const set = (patch: Partial<OtSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }) });
  const step = view === 'week' ? 7 : 1;
  const go = (days: number) => set({ date: addDays(date, days) === today ? undefined : addDays(date, days) });
  useHotkey('alt+arrowleft', () => go(-step));
  useHotkey('alt+arrowright', () => go(step));

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="the OT calendar" />
      </Page>
    );
  }

  const weekStart = startOfWeek(date);
  const rangeFrom = view === 'week' ? weekStart : date;
  const rangeTo = view === 'week' ? addDays(weekStart, 6) : date;
  const openBooking = (booking: OtCalendarBooking) =>
    void navigate({ to: '/ot/bookings/$id', params: { id: booking.id } });
  const title =
    view === 'week' ? `Week of ${formatDateOnly(weekStart)}` : `${dayLabel(date)}${date === today ? ' · today' : ''}`;

  return (
    <Page>
      <PageHeader
        title="OT calendar"
        subtitle={`${title}. Alt+← and Alt+→ move ${view === 'week' ? 'a week' : 'a day'}.`}
        actions={
          <>
            <Button
              icon={<PrintRegular />}
              disabled={!calendar.data || view === 'week'}
              onClick={() =>
                calendar.data &&
                void printOtList(letterhead, calendar.data, dayLabel(date)).catch((error: unknown) =>
                  notify.error(error, 'Printing failed'),
                )
              }
            >
              Print OT list
            </Button>
            {canBook && (
              <Button
                appearance="primary"
                icon={<CalendarAddRegular />}
                onClick={() => void navigate({ to: '/ot/book' })}
              >
                Book surgery
              </Button>
            )}
          </>
        }
      />

      <Section flush>
        <div className={styles.toolbar}>
          <FilterBar>
            <div className={styles.nav}>
              <Tooltip content={`Previous ${view === 'week' ? 'week' : 'day'} (Alt+←)`} relationship="label">
                <Button icon={<ChevronLeftRegular />} onClick={() => go(-step)} />
              </Tooltip>
              <Button onClick={() => set({ date: undefined })} disabled={date === today}>
                Today
              </Button>
              <Tooltip content={`Next ${view === 'week' ? 'week' : 'day'} (Alt+→)`} relationship="label">
                <Button icon={<ChevronRightRegular />} onClick={() => go(step)} />
              </Tooltip>
            </div>
            <Field label="Date">
              <Input
                type="date"
                value={date}
                onChange={(_, d) => set({ date: d.value && d.value !== today ? d.value : undefined })}
              />
            </Field>
            <Field label="Show">
              <TabList
                size="small"
                selectedValue={view}
                onTabSelect={(_, d) => set({ view: d.value === 'week' ? 'week' : undefined })}
              >
                <Tab value="day">Day</Tab>
                <Tab value="week">Week</Tab>
              </TabList>
            </Field>
            <Field label="Theatre">
              <Select value={search.theatreId ?? ''} onChange={(_, d) => set({ theatreId: d.value || undefined })}>
                <option value="">All theatres</option>
                {(theatres.data ?? []).map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.code} · {t.name}
                  </option>
                ))}
              </Select>
            </Field>
            {practitionerId && (
              <Switch
                label="Only my cases"
                checked={search.surgeonId === practitionerId}
                onChange={(_, d) => set({ surgeonId: d.checked ? practitionerId : undefined })}
              />
            )}
          </FilterBar>
        </div>
        {calendar.isLoading ? (
          <LoadingState label="Loading the OT calendar…" />
        ) : calendar.error && !calendar.data ? (
          <div style={{ padding: 16 }}>
            <ErrorState error={calendar.error} onRetry={() => void calendar.refetch()} />
          </div>
        ) : calendar.data ? (
          view === 'week' ? (
            <WeekBoard
              calendar={calendar.data}
              today={today}
              onOpen={openBooking}
              onOpenDay={(day) => set({ date: day === today ? undefined : day, view: undefined })}
            />
          ) : (
            <DayBoard
              calendar={calendar.data}
              date={date}
              now={now}
              canBook={canBook}
              onOpen={openBooking}
              onBookSlot={(theatre, start) =>
                void navigate({ to: '/ot/book', search: { theatreId: theatre.theatreId, start } })
              }
            />
          )
        ) : null}
        <div className={styles.legend} aria-label="Legend">
          <Caption1 className={styles.muted}>Colour shows status:</Caption1>
          <Caption1>
            <span className={styles.swatch} style={{ backgroundColor: tokens.colorBrandBackground2 }} />
            Scheduled
          </Caption1>
          <Caption1>
            <span className={styles.swatch} style={{ backgroundColor: tokens.colorPaletteMarigoldBackground2 }} />
            In pre-op
          </Caption1>
          <Caption1>
            <span className={styles.swatch} style={{ backgroundColor: tokens.colorPaletteGreenBackground2 }} />
            In surgery
          </Caption1>
          <Caption1>
            <span className={styles.swatch} style={{ backgroundColor: tokens.colorNeutralBackground3 }} />
            Completed
          </Caption1>
          <Caption1 className={styles.muted}>
            Left edge: red emergency, orange urgent. SI / TO / SO: WHO Sign-in, Time-out, Sign-out.
          </Caption1>
        </div>
      </Section>

      <Section
        title={`Waiting for a slot${calendar.data ? ` (${calendar.data.awaitingSlot.length})` : ''}`}
        description="Requests and postponed cases for this period, emergencies first. Open one to give it a theatre and time."
        flush
      >
        <DataTable
          ariaLabel="Surgeries waiting for a slot"
          rows={calendar.data?.awaitingSlot}
          loading={calendar.isLoading}
          getRowId={(b) => b.id}
          onRowClick={openBooking}
          size="small"
          columns={[
            {
              key: 'priority',
              header: 'Priority',
              width: 110,
              render: (b) => <PriorityBadge priority={b.priority} showElective />,
            },
            {
              key: 'patient',
              header: 'Patient',
              render: (b) => (
                <span>
                  <Text weight="semibold">{b.patientName}</Text>{' '}
                  <Text size={200}>
                    ({b.age ?? '—'} / {b.gender.charAt(0)})
                  </Text>
                </span>
              ),
            },
            { key: 'procedure', header: 'Procedure', render: (b) => procedureWithSide(b.procedureName, b.laterality) },
            { key: 'surgeon', header: 'Surgeon', priority: 'low', render: (b) => b.surgeonName ?? 'Not chosen' },
            {
              key: 'number',
              header: 'Booking',
              priority: 'low',
              render: (b) => <Text font="monospace">{b.bookingNumber}</Text>,
            },
            { key: 'status', header: 'Status', render: (b) => <SurgeryStatusBadge status={b.status} /> },
          ]}
          rowActions={
            canBook
              ? (b) => (
                  <Button
                    size="small"
                    onClick={() =>
                      void navigate({ to: '/ot/bookings/$id', params: { id: b.id }, search: { action: 'schedule' } })
                    }
                  >
                    Schedule
                  </Button>
                )
              : undefined
          }
          emptyTitle="Nothing is waiting for a slot"
          emptyDescription={canBook ? 'New requests from doctors’ orders appear here automatically.' : undefined}
        />
      </Section>

      {canReport && (
        <>
          <Field label="Utilisation period" style={{ maxWidth: 260 }}>
            <Select value={period} onChange={(_, d) => setPeriod(d.value as UtilisationPeriod)}>
              <option value="shown">{view === 'week' ? 'The week shown' : 'The day shown'}</option>
              <option value="30d">Last 30 days</option>
            </Select>
          </Field>
          <UtilisationPanel
            from={period === '30d' ? addDays(today, -29) : rangeFrom}
            to={period === '30d' ? today : rangeTo}
            periodLabel={period === '30d' ? 'Last 30 days' : view === 'week' ? 'This week' : 'This day'}
            theatreId={search.theatreId}
          />
        </>
      )}
    </Page>
  );
}
