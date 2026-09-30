import { Badge, Button, Caption1, Field, SearchBox, Select, Switch, Text } from '@fluentui/react-components';
import { PersonAddRegular } from '@fluentui/react-icons';
import { formatRelative, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { NoAccess } from '@/modules/ot/shared/feedback';
import { useFacilityId, useLiveInvalidation } from '@/modules/ot/shared/hooks';
import {
  physioKeys,
  physioQueries,
  REFERRAL_STATUSES,
  type PhysioReferralStatus,
  type ReferralFilters,
} from '@/modules/physio/api';
import {
  orderPriorityLabel,
  orderPriorityTone,
  referralStatusLabel,
  referralStatusTone,
} from '@/modules/physio/labels';
import { NewReferralDialog } from '@/modules/physio/PhysioDialogs';

export const Route = createFileRoute('/_app/physio/referrals/')({
  validateSearch: (s: Record<string, unknown>): ReferralFilters => ({
    status: REFERRAL_STATUSES.includes(s.status as PhysioReferralStatus)
      ? (s.status as PhysioReferralStatus)
      : undefined,
    closed: s.closed === true ? true : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: PhysioReferralsPage,
});

function PhysioReferralsPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const facilityId = useFacilityId();
  const canRead = usePermission('physio.read');
  const canRefer = usePermission('physio.referrals.manage');
  const [term, setTerm] = useState(filters.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [referring, setReferring] = useState(false);
  const referrals = useQuery({ ...physioQueries.referrals(filters), enabled: canRead });

  useEffect(() => {
    if ((debounced || undefined) !== filters.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, filters.search, navigate]);

  useLiveInvalidation(facilityId ? `facility:${facilityId}` : null, 'physio.worklist.changed', [
    physioKeys.referralLists,
  ]);

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="physiotherapy referrals" />
      </Page>
    );
  }

  const set = (patch: Partial<ReferralFilters>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Physiotherapy referrals"
        subtitle="From doctors' orders and manual referrals; immediate and urgent first."
        breadcrumbs={[{ label: 'Worklist', onClick: () => void navigate({ to: '/physio' }) }, { label: 'Referrals' }]}
        actions={
          canRefer && (
            <Button appearance="primary" icon={<PersonAddRegular />} onClick={() => setReferring(true)}>
              New referral
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Name, UHID or mobile" />
            </Field>
            <Field label="Status">
              <Select
                value={filters.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as PhysioReferralStatus | undefined })}
              >
                <option value="">{filters.closed ? 'All' : 'All open'}</option>
                {REFERRAL_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {referralStatusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={Boolean(filters.closed)}
              onChange={(_, d) => set({ closed: d.checked || undefined })}
              label="Include completed and cancelled"
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Physiotherapy referrals"
          rows={referrals.data?.items}
          loading={referrals.isLoading}
          error={referrals.error}
          onRetry={() => void referrals.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) => void navigate({ to: '/physio/referrals/$id', params: { id: r.id } })}
          columns={[
            {
              key: 'priority',
              header: 'Priority',
              width: 120,
              render: (r) => (
                <Badge appearance="tint" color={orderPriorityTone[r.priority]}>
                  {orderPriorityLabel[r.priority]}
                </Badge>
              ),
            },
            {
              key: 'patient',
              header: 'Patient',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.patientName}</Text>{' '}
                  <Text size={200}>
                    ({r.age ?? '—'} / {r.gender.charAt(0)})
                  </Text>
                  {r.isInpatient && (
                    <Badge size="small" appearance="outline" style={{ marginLeft: 6 }}>
                      In-patient
                    </Badge>
                  )}
                </span>
              ),
            },
            {
              key: 'reason',
              header: 'Reason',
              render: (r) => (
                <span>
                  {r.reason ?? r.serviceName ?? '—'}
                  {r.sessionsOrdered && <Caption1 block>{r.sessionsOrdered} sessions prescribed</Caption1>}
                </span>
              ),
            },
            { key: 'by', header: 'Referred by', priority: 'low', render: (r) => r.referredByName ?? '—' },
            { key: 'when', header: 'Referred', priority: 'low', render: (r) => formatRelative(r.referredAt) },
            {
              key: 'status',
              header: 'Status',
              render: (r) => (
                <StatusBadge
                  status={r.status}
                  tone={referralStatusTone[r.status]}
                  label={referralStatusLabel[r.status]}
                />
              ),
            },
          ]}
          emptyTitle={filters.search ? 'No referral matches your search' : 'No open referrals'}
          emptyDescription="Physiotherapy orders placed by doctors appear here automatically."
          page={filters.page ?? 1}
          pageSize={25}
          totalCount={referrals.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
      {referring && <NewReferralDialog onClose={() => setReferring(false)} />}
    </Page>
  );
}
