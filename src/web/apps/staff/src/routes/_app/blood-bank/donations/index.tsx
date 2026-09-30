import { Field, SearchBox, Select, Switch, Text, tokens } from '@fluentui/react-components';
import { formatDateTime, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  bloodKeys,
  bloodQueries,
  DONATION_STATUSES,
  type DonationFilters,
  type DonationStatus,
} from '@/modules/blood-bank/api';
import { bagTypeLabel, donationStatusLabel, donationTypeLabel, groupLabel } from '@/modules/blood-bank/labels';
import { NoAccess } from '@/modules/ot/shared/feedback';
import { useFacilityId, useLiveInvalidation } from '@/modules/ot/shared/hooks';

export const Route = createFileRoute('/_app/blood-bank/donations/')({
  validateSearch: (s: Record<string, unknown>): DonationFilters => ({
    status: DONATION_STATUSES.includes(s.status as DonationStatus) ? (s.status as DonationStatus) : undefined,
    pendingResults: s.pendingResults === true ? true : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: DonationsPage,
});

/** Collected bags: separation and laboratory work to do, and their results. */
function DonationsPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const facilityId = useFacilityId();
  const canManage = usePermission('bloodbank.inventory.manage');
  const [term, setTerm] = useState(filters.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const donations = useQuery({ ...bloodQueries.donations(filters), enabled: canManage });

  useEffect(() => {
    if ((debounced || undefined) !== filters.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, filters.search, navigate]);

  useLiveInvalidation(facilityId ? `facility:${facilityId}` : null, 'bloodbank.changed', [
    ['blood-bank', 'donations'],
    bloodKeys.inventory,
  ]);

  if (!canManage) {
    return (
      <Page>
        <NoAccess what="donations" />
      </Page>
    );
  }

  const set = (patch: Partial<DonationFilters>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Donations"
        subtitle="Separate components within 8 hours and record grouping and TTI results to release units from quarantine."
        breadcrumbs={[
          { label: 'Blood stock', onClick: () => void navigate({ to: '/blood-bank' }) },
          { label: 'Donations' },
        ]}
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Bag number" style={{ flex: '1 1 220px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Scan or type the full bag number"
              />
            </Field>
            <Field label="Status">
              <Select
                value={filters.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as DonationStatus | undefined })}
              >
                <option value="">All</option>
                {DONATION_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {donationStatusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={Boolean(filters.pendingResults)}
              onChange={(_, d) => set({ pendingResults: d.checked || undefined })}
              label="Only those awaiting results"
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Donations"
          rows={donations.data?.items}
          loading={donations.isLoading}
          error={donations.error}
          onRetry={() => void donations.refetch()}
          getRowId={(d) => d.id}
          onRowClick={(d) => void navigate({ to: '/blood-bank/donations/$id', params: { id: d.id } })}
          columns={[
            {
              key: 'bag',
              header: 'Bag',
              render: (d) => <Text style={{ fontFamily: tokens.fontFamilyMonospace }}>{d.bagNumber}</Text>,
            },
            { key: 'donor', header: 'Donor', render: (d) => `${d.donorName} · ${d.donorNumber}` },
            { key: 'when', header: 'Collected', render: (d) => formatDateTime(d.collectedAt) },
            {
              key: 'what',
              header: 'Bag · volume',
              priority: 'low',
              render: (d) => `${bagTypeLabel[d.bagType]} · ${d.volumeMl} ml · ${donationTypeLabel[d.type]}`,
            },
            { key: 'group', header: 'Group', render: (d) => groupLabel(d.bloodGroup) },
            {
              key: 'tti',
              header: 'Screening',
              render: (d) =>
                d.ttiReactive ? (
                  <StatusBadge status="Reactive" tone="danger" label="Reactive" />
                ) : d.ttiComplete ? (
                  <StatusBadge status="Cleared" tone="success" label="Cleared" />
                ) : (
                  <StatusBadge status="Pending" tone="warning" label="Results pending" />
                ),
            },
            { key: 'status', header: 'Status', render: (d) => donationStatusLabel[d.status] },
          ]}
          emptyTitle="No donations match"
          page={filters.page ?? 1}
          pageSize={25}
          totalCount={donations.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
    </Page>
  );
}
