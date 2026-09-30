import { Badge, Field, Input, SearchBox, Select, Text } from '@fluentui/react-components';
import { formatTime, humanize, todayIso, useDebouncedValue, useRealtimeEvent, useSession } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { frontOfficeQueries, type VisitFilters } from '@/modules/front-office/api';
import type { VisitStatus, VisitType } from '@/modules/patients/api';

const VISIT_TYPES: VisitType[] = ['OutPatient', 'Emergency', 'DayCare', 'Telehealth', 'HealthCheckup', 'InPatient'];
const VISIT_STATUSES: VisitStatus[] = ['Open', 'InProgress', 'Completed', 'Cancelled'];
const DATE = /^\d{4}-\d{2}-\d{2}$/;

export const Route = createFileRoute('/_app/front-office/visits')({
  validateSearch: (s: Record<string, unknown>): VisitFilters => ({
    from: typeof s.from === 'string' && DATE.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DATE.test(s.to) ? s.to : undefined,
    type: VISIT_TYPES.includes(s.type as VisitType) ? (s.type as VisitType) : undefined,
    status: VISIT_STATUSES.includes(s.status as VisitStatus) ? (s.status as VisitStatus) : undefined,
    departmentId: typeof s.departmentId === 'string' ? s.departmentId : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: VisitsPage,
});

function VisitsPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const facilityId = useSession((s) => s.user?.facilityId);
  const departments = useDepartments('Clinical');
  const [term, setTerm] = useState(filters.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const visits = useQuery(frontOfficeQueries.visits(filters));

  useEffect(() => {
    if ((debounced || undefined) !== filters.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, filters.search, navigate]);

  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'visits.changed', () => {
    void queryClient.invalidateQueries({ queryKey: ['visits', 'list'] });
  });

  const set = (patch: Partial<VisitFilters>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const today = todayIso();

  return (
    <Page>
      <PageHeader
        title="Visits"
        subtitle={
          filters.from && filters.from !== today
            ? `From ${filters.from}${filters.to ? ` to ${filters.to}` : ''}`
            : "Today's check-ins, live"
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 240px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Name, UHID or visit number" />
            </Field>
            <Field label="From">
              <Input
                type="date"
                value={filters.from ?? today}
                max={today}
                onChange={(_, d) => set({ from: d.value || undefined })}
              />
            </Field>
            <Field label="To">
              <Input
                type="date"
                value={filters.to ?? filters.from ?? today}
                max={today}
                onChange={(_, d) => set({ to: d.value || undefined })}
              />
            </Field>
            <Field label="Department">
              <Select
                value={filters.departmentId ?? ''}
                onChange={(_, d) => set({ departmentId: d.value || undefined })}
              >
                <option value="">All</option>
                {toOptions(departments.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Type">
              <Select
                value={filters.type ?? ''}
                onChange={(_, d) => set({ type: (d.value || undefined) as VisitType | undefined })}
              >
                <option value="">All</option>
                {VISIT_TYPES.map((t) => (
                  <option key={t} value={t}>
                    {humanize(t)}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Status">
              <Select
                value={filters.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as VisitStatus | undefined })}
              >
                <option value="">All</option>
                {VISIT_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {humanize(s)}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Visits"
          rows={visits.data?.items}
          loading={visits.isLoading}
          error={visits.error}
          onRetry={() => void visits.refetch()}
          getRowId={(v) => v.id}
          onRowClick={(v) =>
            void navigate({ to: '/patients/$patientId', params: { patientId: v.patientId }, search: { tab: 'visits' } })
          }
          columns={[
            { key: 'time', header: 'Time', width: 90, render: (v) => formatTime(v.visitedAt) },
            {
              key: 'token',
              header: 'Token',
              width: 90,
              render: (v) => (v.tokenLabel ? <Text weight="semibold">{v.tokenLabel}</Text> : '—'),
            },
            {
              key: 'patient',
              header: 'Patient',
              render: (v) => (
                <span>
                  <Text weight="semibold">{v.patientName}</Text>{' '}
                  <Text size={200}>
                    ({v.age ?? '—'} / {v.gender.charAt(0)})
                  </Text>
                  {v.isMlc && (
                    <Badge size="small" color="danger" style={{ marginLeft: 6 }}>
                      MLC
                    </Badge>
                  )}
                </span>
              ),
            },
            { key: 'uhid', header: 'UHID', priority: 'low', render: (v) => <Text font="monospace">{v.uhid}</Text> },
            { key: 'dept', header: 'Department', render: (v) => v.departmentName ?? '—' },
            { key: 'doctor', header: 'Doctor', priority: 'low', render: (v) => v.practitionerName ?? 'Any' },
            {
              key: 'type',
              header: 'Type',
              priority: 'low',
              render: (v) =>
                `${humanize(v.type)}${v.isFollowUp ? ' · follow-up' : ''}${v.isFreeFollowUp ? ' (free)' : ''}`,
            },
            { key: 'payer', header: 'Payer', priority: 'low', render: (v) => humanize(v.payerType) },
            {
              key: 'status',
              header: 'Status',
              render: (v) => <StatusBadge status={v.tokenStatus && v.status === 'Open' ? v.tokenStatus : v.status} />,
            },
          ]}
          emptyTitle="No visits match"
          emptyDescription="Patients appear here as soon as they are checked in."
          page={filters.page ?? 1}
          pageSize={25}
          totalCount={visits.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
    </Page>
  );
}
