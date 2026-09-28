import { Field, Input, SearchBox, Select, Text } from '@fluentui/react-components';
import { formatDateOnly, formatTime, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { dialysisKeys, dialysisQueries, SESSION_STATUSES, type DialysisSessionStatus, type SessionFilters } from '@/modules/dialysis/api';
import { SessionStatusBadge } from '@/modules/dialysis/DialysisBadges';
import { sessionStatusLabel, shiftLabel } from '@/modules/dialysis/labels';
import { isDateOnly } from '@/modules/ot/shared/datetime';
import { ChargeBadge, NoAccess } from '@/modules/ot/shared/feedback';
import { useFacilityId, useLiveInvalidation } from '@/modules/ot/shared/hooks';

export const Route = createFileRoute('/_app/dialysis/sessions/')({
  validateSearch: (s: Record<string, unknown>): SessionFilters => ({
    from: isDateOnly(s.from) ? s.from : undefined,
    to: isDateOnly(s.to) ? s.to : undefined,
    status: SESSION_STATUSES.includes(s.status as DialysisSessionStatus) ? (s.status as DialysisSessionStatus) : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: DialysisSessionsPage,
});

function DialysisSessionsPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const facilityId = useFacilityId();
  const canRead = usePermission('dialysis.read');
  const [term, setTerm] = useState(filters.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const sessions = useQuery({ ...dialysisQueries.sessions(filters), enabled: canRead });

  useEffect(() => {
    if ((debounced || undefined) !== filters.search) {
      void navigate({ search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, filters.search, navigate]);

  useLiveInvalidation(facilityId ? `facility:${facilityId}` : null, 'dialysis.board.changed', [dialysisKeys.sessionLists]);

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="dialysis sessions" />
      </Page>
    );
  }

  const set = (patch: Partial<SessionFilters>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Dialysis sessions"
        subtitle="Search by patient name, UHID or session number."
        breadcrumbs={[{ label: 'Dialysis board', onClick: () => void navigate({ to: '/dialysis' }) }, { label: 'Sessions' }]}
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Name, UHID or session number" />
            </Field>
            <Field label="From">
              <Input type="date" value={filters.from ?? ''} onChange={(_, d) => set({ from: d.value || undefined })} />
            </Field>
            <Field label="To">
              <Input type="date" value={filters.to ?? ''} min={filters.from} onChange={(_, d) => set({ to: d.value || undefined })} />
            </Field>
            <Field label="Status">
              <Select value={filters.status ?? ''} onChange={(_, d) => set({ status: (d.value || undefined) as DialysisSessionStatus | undefined })}>
                <option value="">All</option>
                {SESSION_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {sessionStatusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Dialysis sessions"
          rows={sessions.data?.items}
          loading={sessions.isLoading}
          error={sessions.error}
          onRetry={() => void sessions.refetch()}
          getRowId={(s) => s.id}
          onRowClick={(s) => void navigate({ to: '/dialysis/sessions/$id', params: { id: s.id } })}
          columns={[
            { key: 'date', header: 'Date', width: 130, render: (s) => formatDateOnly(s.scheduledDate) },
            { key: 'shift', header: 'Shift', width: 100, render: (s) => shiftLabel[s.shift] },
            { key: 'patient', header: 'Patient', render: (s) => <Text weight="semibold">{s.patientName}</Text> },
            { key: 'uhid', header: 'UHID', priority: 'low', render: (s) => <Text font="monospace">{s.uhid}</Text> },
            { key: 'machine', header: 'Machine', width: 100, render: (s) => s.machineCode ?? '—' },
            { key: 'time', header: 'On / off', priority: 'low', render: (s) => (s.startedAt ? `${formatTime(s.startedAt)} – ${s.endedAt ? formatTime(s.endedAt) : 'running'}` : '—') },
            { key: 'status', header: 'Status', render: (s) => <SessionStatusBadge status={s.status} /> },
            { key: 'number', header: 'Session', priority: 'low', render: (s) => <Text font="monospace">{s.sessionNumber}</Text> },
            { key: 'billing', header: 'Billing', priority: 'low', render: (s) => <ChargeBadge status={s.chargeStatus} /> },
          ]}
          emptyTitle="No sessions match"
          emptyDescription="Change the dates or status, or book a session from the board."
          page={filters.page ?? 1}
          pageSize={25}
          totalCount={sessions.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
    </Page>
  );
}
