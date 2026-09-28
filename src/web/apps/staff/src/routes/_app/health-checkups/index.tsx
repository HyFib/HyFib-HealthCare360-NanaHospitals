import { Badge, Button, Caption1, Field, Input, makeStyles, ProgressBar, SearchBox, Select, Text, tokens, Tooltip } from '@fluentui/react-components';
import { CalendarAddRegular, ChevronLeftRegular, ChevronRightRegular, PersonAvailableRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatTime, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { addDaysIso } from '@/modules/emr/encounterForm';
import { checkupQueries, type BookingDto, type BookingListParams } from '@/modules/health-checkups/api';
import { CheckInCheckupDialog } from '@/modules/health-checkups/CheckInCheckupDialog';
import { BOOKING_STATUSES, bookingStatusLabel, canCheckIn, countByStatus, nextStop, progress, type BookingStatus } from '@/modules/health-checkups/itinerary';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

export const Route = createFileRoute('/_app/health-checkups/')({
  validateSearch: (s: Record<string, unknown>): BookingListParams => ({
    date: typeof s.date === 'string' && DATE.test(s.date) ? s.date : undefined,
    status: BOOKING_STATUSES.includes(s.status as BookingStatus) ? (s.status as BookingStatus) : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: CheckupDayPage,
});

const useStyles = makeStyles({
  counts: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  dateNav: { display: 'flex', gap: '4px', alignItems: 'flex-end' },
  progress: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: '120px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

function statusTone(status: BookingStatus): 'warning' | 'brand' | 'success' | 'danger' | 'informative' {
  switch (status) {
    case 'Booked':
      return 'warning';
    case 'CheckedIn':
    case 'InProgress':
      return 'brand';
    case 'Completed':
      return 'success';
    case 'NoShow':
    case 'Cancelled':
      return 'danger';
  }
}

/** The check-up desk's day (MHC-02/03): who is expected, who is on the floor and where they go next. */
function CheckupDayPage() {
  const styles = useStyles();
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission('healthcheck.manage');
  const today = todayIso();
  const date = filters.date ?? today;
  const [term, setTerm] = useState(filters.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [checkingIn, setCheckingIn] = useState<BookingDto | null>(null);
  const bookings = useQuery(checkupQueries.bookings({ ...filters, date }));
  const counts = countByStatus(bookings.data?.items ?? []);

  useEffect(() => {
    if ((debounced || undefined) !== filters.search) {
      void navigate({ search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, filters.search, navigate]);

  const set = (patch: Partial<BookingListParams>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }), replace: true });
  const setDate = (value: string) => set({ date: value === today ? undefined : value });
  const open = (b: BookingDto) => void navigate({ to: '/health-checkups/bookings/$bookingId', params: { bookingId: b.id } });

  return (
    <Page>
      <PageHeader
        title="Health check-ups"
        subtitle={date === today ? "Today's bookings, live" : formatDateOnly(date)}
        actions={
          canManage && (
            <Button appearance="primary" icon={<CalendarAddRegular />} onClick={() => void navigate({ to: '/health-checkups/book' })}>
              Book check-up
            </Button>
          )
        }
      />
      <div className={styles.counts} aria-label="Bookings by status">
        {BOOKING_STATUSES.filter((s) => counts[s] > 0).map((s) => (
          <Badge key={s} appearance="tint" color={statusTone(s)} size="large">
            {bookingStatusLabel[s]}: {counts[s]}
          </Badge>
        ))}
      </div>
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <div className={styles.dateNav}>
              <Button appearance="subtle" icon={<ChevronLeftRegular />} aria-label="Previous day" onClick={() => setDate(addDaysIso(date, -1))} />
              <Field label="Date">
                <Input type="date" value={date} onChange={(_, d) => d.value && setDate(d.value)} />
              </Field>
              <Button appearance="subtle" icon={<ChevronRightRegular />} aria-label="Next day" onClick={() => setDate(addDaysIso(date, 1))} />
              {date !== today && (
                <Button appearance="subtle" onClick={() => setDate(today)}>
                  Today
                </Button>
              )}
            </div>
            <Field label="Search" style={{ flex: '1 1 220px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Name, UHID or booking number" />
            </Field>
            <Field label="Status">
              <Select value={filters.status ?? ''} onChange={(_, d) => set({ status: (d.value || undefined) as BookingStatus | undefined })}>
                <option value="">All</option>
                {BOOKING_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {bookingStatusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Health check-up bookings"
          rows={bookings.data?.items}
          loading={bookings.isLoading}
          error={bookings.error}
          onRetry={() => void bookings.refetch()}
          getRowId={(b) => b.id}
          onRowClick={open}
          size="small"
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              render: (b) => (
                <span>
                  <Text weight="semibold">{b.patientName ?? '—'}</Text> <Text size={200}>· {b.uhid ?? ''}</Text>
                  {b.corporateName && <Caption1 block className={styles.muted}>{b.corporateName}</Caption1>}
                </span>
              ),
            },
            { key: 'package', header: 'Package', render: (b) => b.packageName },
            {
              key: 'progress',
              header: 'Stations',
              width: 160,
              render: (b) => {
                const p = progress(b.itinerary);
                return (
                  <span className={styles.progress}>
                    <Caption1>
                      {p.done + p.skipped} of {p.total} done
                    </Caption1>
                    <ProgressBar value={p.ratio} aria-label={`${p.done + p.skipped} of ${p.total} stations done`} />
                  </span>
                );
              },
            },
            {
              key: 'next',
              header: 'Next station',
              priority: 'low',
              render: (b) => {
                if (b.status !== 'CheckedIn' && b.status !== 'InProgress') {
                  return '—';
                }
                const stop = nextStop(b.itinerary);
                return stop ? `${stop.departmentName}: ${stop.description}` : "Doctor's review";
              },
            },
            { key: 'in', header: 'Checked in', width: 100, priority: 'low', render: (b) => (b.checkedInAt ? formatTime(b.checkedInAt) : '—') },
            { key: 'number', header: 'Booking', width: 140, priority: 'low', render: (b) => <Text font="monospace">{b.bookingNumber}</Text> },
            { key: 'status', header: 'Status', width: 120, render: (b) => <StatusBadge status={b.status} tone={statusTone(b.status)} label={bookingStatusLabel[b.status]} /> },
          ]}
          rowActions={
            canManage
              ? (b) =>
                  canCheckIn(b.status) && date === today ? (
                    <Tooltip content="Check in and add the package fee to the bill" relationship="description">
                      <Button size="small" appearance="primary" icon={<PersonAvailableRegular />} onClick={() => setCheckingIn(b)}>
                        Check in
                      </Button>
                    </Tooltip>
                  ) : null
              : undefined
          }
          emptyTitle={date === today ? 'No check-ups today' : 'No check-ups on this day'}
          emptyDescription={canManage ? 'Use Book check-up to book one, or Corporate batches for a company drive.' : undefined}
          page={filters.page ?? 1}
          pageSize={50}
          totalCount={bookings.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
      <CheckInCheckupDialog booking={checkingIn} onClose={() => setCheckingIn(null)} onCheckedIn={open} />
    </Page>
  );
}
