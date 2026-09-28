import { Button, Caption1, Field, ProgressBar, SearchBox, Select, Switch, Text, tokens } from '@fluentui/react-components';
import { DropRegular } from '@fluentui/react-icons';
import { formatDateTime, formatRelative, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { bloodKeys, bloodQueries, REQUEST_STATUSES, type BloodRequestStatus, type RequestFilters } from '@/modules/blood-bank/api';
import { RequestStatusBadge, UrgencyBadge } from '@/modules/blood-bank/BloodBadges';
import { componentShort, groupLabel, requestStatusLabel } from '@/modules/blood-bank/labels';
import { NoAccess } from '@/modules/ot/shared/feedback';
import { useFacilityId, useLiveInvalidation, useNow } from '@/modules/ot/shared/hooks';

export const Route = createFileRoute('/_app/blood-bank/requests/')({
  validateSearch: (s: Record<string, unknown>): RequestFilters => ({
    status: REQUEST_STATUSES.includes(s.status as BloodRequestStatus) ? (s.status as BloodRequestStatus) : undefined,
    closed: s.closed === true ? true : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: BloodRequestsPage,
});

/** Blood requests, emergencies first; the blood bank's work queue and the ward's view of what was asked for. */
function BloodRequestsPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const facilityId = useFacilityId();
  const now = useNow(60_000);
  const canRead = usePermission('bloodbank.read');
  const canRequest = usePermission('bloodbank.requests.create');
  const [term, setTerm] = useState(filters.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const requests = useQuery({ ...bloodQueries.requests(filters), enabled: canRead });

  useEffect(() => {
    if ((debounced || undefined) !== filters.search) {
      void navigate({ search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, filters.search, navigate]);

  useLiveInvalidation(facilityId ? `facility:${facilityId}` : null, 'bloodbank.changed', [bloodKeys.requestLists]);

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="blood requests" />
      </Page>
    );
  }

  const set = (patch: Partial<RequestFilters>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Blood requests"
        subtitle="Emergency requests first. Search by request number, patient name, UHID or mobile."
        breadcrumbs={[{ label: 'Blood stock', onClick: () => void navigate({ to: '/blood-bank' }) }, { label: 'Requests' }]}
        actions={
          canRequest && (
            <Button appearance="primary" icon={<DropRegular />} onClick={() => void navigate({ to: '/blood-bank/requests/new' })}>
              Request blood
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Request number, name, UHID or mobile" />
            </Field>
            <Field label="Status">
              <Select value={filters.status ?? ''} onChange={(_, d) => set({ status: (d.value || undefined) as BloodRequestStatus | undefined })}>
                <option value="">{filters.closed ? 'All' : 'All open'}</option>
                {REQUEST_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {requestStatusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch checked={Boolean(filters.closed)} onChange={(_, d) => set({ closed: d.checked || undefined })} label="Include issued and cancelled" />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Blood requests"
          rows={requests.data?.items}
          loading={requests.isLoading}
          error={requests.error}
          onRetry={() => void requests.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) => void navigate({ to: '/blood-bank/requests/$id', params: { id: r.id } })}
          columns={[
            { key: 'urgency', header: 'Urgency', width: 120, render: (r) => <UrgencyBadge urgency={r.urgency} /> },
            {
              key: 'patient',
              header: 'Patient',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.patientName}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {r.uhid} · {r.requestNumber}
                  </Caption1>
                </span>
              ),
            },
            { key: 'what', header: 'Asked for', render: (r) => `${r.unitsRequested} × ${componentShort[r.componentType]}` },
            {
              key: 'progress',
              header: 'Progress',
              render: (r) => (
                <span style={{ display: 'flex', flexDirection: 'column', gap: 2, minWidth: 120 }}>
                  <Caption1>
                    {r.unitsIssued} issued · {r.unitsReserved} reserved
                  </Caption1>
                  <ProgressBar value={r.unitsRequested ? r.unitsIssued / r.unitsRequested : 0} aria-label={`${r.unitsIssued} of ${r.unitsRequested} issued`} />
                </span>
              ),
            },
            { key: 'group', header: 'Patient group', priority: 'low', render: (r) => groupLabel(r.patientBloodGroup) },
            {
              key: 'required',
              header: 'Needed by',
              priority: 'low',
              render: (r) =>
                r.requiredBy ? (
                  <Text style={Date.parse(r.requiredBy) < now && r.status !== 'Issued' ? { color: tokens.colorPaletteRedForeground1, fontWeight: 600 } : undefined}>
                    {formatDateTime(r.requiredBy)}
                  </Text>
                ) : (
                  '—'
                ),
            },
            { key: 'created', header: 'Requested', priority: 'low', render: (r) => formatRelative(r.createdAt) },
            { key: 'status', header: 'Status', render: (r) => <RequestStatusBadge status={r.status} /> },
          ]}
          emptyTitle={filters.search ? 'No request matches' : 'No open blood requests'}
          emptyDescription="Requests from doctors’ orders and the wards appear here."
          page={filters.page ?? 1}
          pageSize={25}
          totalCount={requests.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
    </Page>
  );
}
