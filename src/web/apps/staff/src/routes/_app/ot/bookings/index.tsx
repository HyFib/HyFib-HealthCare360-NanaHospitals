import { Button, Field, Input, SearchBox, Select, Text } from '@fluentui/react-components';
import { CalendarAddRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { otKeys, otQueries, SURGERY_STATUSES, type BookingFilters, type SurgeryStatus } from '@/modules/ot/api';
import { surgeryStatusLabel } from '@/modules/ot/labels';
import { PriorityBadge, SideBadge, SurgeryStatusBadge, WhoProgress } from '@/modules/ot/OtBadges';
import { isDateOnly } from '@/modules/ot/shared/datetime';
import { ChargeBadge, NoAccess } from '@/modules/ot/shared/feedback';
import { useFacilityId, useLiveInvalidation } from '@/modules/ot/shared/hooks';

const SORTS = ['scheduledStart', 'bookingNumber', 'status', 'priority', 'createdAt'] as const;

export const Route = createFileRoute('/_app/ot/bookings/')({
  validateSearch: (s: Record<string, unknown>): BookingFilters => ({
    from: isDateOnly(s.from) ? s.from : undefined,
    to: isDateOnly(s.to) ? s.to : undefined,
    status: SURGERY_STATUSES.includes(s.status as SurgeryStatus) ? (s.status as SurgeryStatus) : undefined,
    theatreId: typeof s.theatreId === 'string' && s.theatreId ? s.theatreId : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    sort: SORTS.includes(s.sort as (typeof SORTS)[number]) ? (s.sort as string) : undefined,
    desc: s.desc === true ? true : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: SurgeriesPage,
});

function SurgeriesPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const facilityId = useFacilityId();
  const canRead = usePermission('ot.read');
  const canBook = usePermission('ot.bookings.manage');
  const [term, setTerm] = useState(filters.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const theatres = useQuery({ ...otQueries.theatres(true), enabled: canRead });
  const bookings = useQuery({ ...otQueries.bookings(filters), enabled: canRead });

  useEffect(() => {
    if ((debounced || undefined) !== filters.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, filters.search, navigate]);

  useLiveInvalidation(facilityId ? `ot:${facilityId}` : null, 'ot.booking.changed', [otKeys.bookingLists]);

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="surgery records" />
      </Page>
    );
  }

  const set = (patch: Partial<BookingFilters>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Surgeries"
        subtitle="Every booking with its WHO checklist progress. Search by patient name, UHID, mobile or booking number."
        breadcrumbs={[{ label: 'OT calendar', onClick: () => void navigate({ to: '/ot' }) }, { label: 'Surgeries' }]}
        actions={
          canBook && (
            <Button
              appearance="primary"
              icon={<CalendarAddRegular />}
              onClick={() => void navigate({ to: '/ot/book' })}
            >
              Book surgery
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Name, UHID, mobile or booking number"
              />
            </Field>
            <Field label="From">
              <Input type="date" value={filters.from ?? ''} onChange={(_, d) => set({ from: d.value || undefined })} />
            </Field>
            <Field label="To">
              <Input
                type="date"
                value={filters.to ?? ''}
                min={filters.from}
                onChange={(_, d) => set({ to: d.value || undefined })}
              />
            </Field>
            <Field label="Status">
              <Select
                value={filters.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as SurgeryStatus | undefined })}
              >
                <option value="">All</option>
                {SURGERY_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {surgeryStatusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Theatre">
              <Select value={filters.theatreId ?? ''} onChange={(_, d) => set({ theatreId: d.value || undefined })}>
                <option value="">All</option>
                {(theatres.data ?? []).map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.code}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Surgeries"
          rows={bookings.data?.items}
          loading={bookings.isLoading}
          error={bookings.error}
          onRetry={() => void bookings.refetch()}
          getRowId={(b) => b.id}
          onRowClick={(b) => void navigate({ to: '/ot/bookings/$id', params: { id: b.id } })}
          sort={filters.sort ? { key: filters.sort, desc: Boolean(filters.desc) } : undefined}
          onSortChange={(sort) => set({ sort: sort.key, desc: sort.desc || undefined })}
          columns={[
            {
              key: 'scheduledStart',
              header: 'When',
              sortable: true,
              width: 170,
              render: (b) =>
                b.scheduledStart
                  ? formatDateTime(b.scheduledStart)
                  : b.preferredDate
                    ? `Prefers ${formatDateOnly(b.preferredDate)}`
                    : 'No slot yet',
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
            {
              key: 'procedure',
              header: 'Procedure',
              render: (b) => (
                <span style={{ display: 'inline-flex', gap: 6, alignItems: 'center' }}>
                  {b.procedureName} <SideBadge laterality={b.laterality} size="small" />
                </span>
              ),
            },
            { key: 'theatre', header: 'Theatre', width: 90, priority: 'low', render: (b) => b.theatreCode ?? '—' },
            { key: 'surgeon', header: 'Surgeon', priority: 'low', render: (b) => b.surgeonName ?? '—' },
            {
              key: 'priority',
              header: 'Priority',
              sortable: true,
              width: 110,
              render: (b) => <PriorityBadge priority={b.priority} showElective />,
            },
            {
              key: 'who',
              header: 'WHO checklist',
              priority: 'low',
              render: (b) => (
                <WhoProgress
                  signInDone={b.signInDone}
                  timeOutDone={b.timeOutDone}
                  signOutDone={b.signOutDone}
                  compact
                />
              ),
            },
            {
              key: 'status',
              header: 'Status',
              sortable: true,
              render: (b) => <SurgeryStatusBadge status={b.status} />,
            },
            {
              key: 'bookingNumber',
              header: 'Booking',
              sortable: true,
              priority: 'low',
              render: (b) => <Text font="monospace">{b.bookingNumber}</Text>,
            },
            {
              key: 'billing',
              header: 'Billing',
              priority: 'low',
              render: (b) => <ChargeBadge status={b.chargeStatus} />,
            },
          ]}
          emptyTitle={filters.search ? 'No surgery matches your search' : 'No surgeries match these filters'}
          emptyDescription="Clear the dates or status to see more."
          page={filters.page ?? 1}
          pageSize={25}
          totalCount={bookings.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
    </Page>
  );
}
