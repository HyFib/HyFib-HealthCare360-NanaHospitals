import { Button, Caption1, Field, SearchBox, Select, Switch, Text, tokens } from '@fluentui/react-components';
import { PersonAddRegular } from '@fluentui/react-icons';
import { formatDate, formatDateOnly, todayIso, useDebouncedValue, type BloodGroup } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { bloodQueries, KNOWN_GROUPS, type DonorFilters, type DonorListItem } from '@/modules/blood-bank/api';
import { DonorDrawer } from '@/modules/blood-bank/DonorDialogs';
import { groupLabel } from '@/modules/blood-bank/labels';
import { NoAccess } from '@/modules/ot/shared/feedback';

export const Route = createFileRoute('/_app/blood-bank/donors/')({
  validateSearch: (s: Record<string, unknown>): DonorFilters => ({
    bloodGroup: (KNOWN_GROUPS as readonly string[]).includes(s.bloodGroup as string)
      ? (s.bloodGroup as BloodGroup)
      : undefined,
    eligibleOnly: s.eligibleOnly === true ? true : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: DonorsPage,
});

function donorStatus(d: DonorListItem, today: string) {
  if (d.isPermanentlyDeferred) {
    return <StatusBadge status="Deferred" tone="danger" label="Permanently deferred" />;
  }
  if (d.deferredUntil && d.deferredUntil > today) {
    return <StatusBadge status="Deferred" tone="warning" label={`Deferred until ${formatDateOnly(d.deferredUntil)}`} />;
  }
  if (d.nextEligibleOn && d.nextEligibleOn > today) {
    return (
      <StatusBadge status="Waiting" tone="informative" label={`Can donate from ${formatDateOnly(d.nextEligibleOn)}`} />
    );
  }
  return <StatusBadge status="Eligible" tone="success" label="Can donate" />;
}

/** Donor register (confidential: phone numbers masked; every donor record opened is audited). */
function DonorsPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission('bloodbank.donors.manage');
  const [term, setTerm] = useState(filters.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [registering, setRegistering] = useState(false);
  const donors = useQuery({ ...bloodQueries.donors(filters), enabled: canManage });
  const today = todayIso();

  useEffect(() => {
    if ((debounced || undefined) !== filters.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, filters.search, navigate]);

  if (!canManage) {
    return (
      <Page>
        <NoAccess what="the donor register" />
      </Page>
    );
  }

  const set = (patch: Partial<DonorFilters>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Blood donors"
        subtitle="Search by donor number, name or mobile. Donor details are confidential."
        breadcrumbs={[
          { label: 'Blood stock', onClick: () => void navigate({ to: '/blood-bank' }) },
          { label: 'Donors' },
        ]}
        actions={
          <Button appearance="primary" icon={<PersonAddRegular />} onClick={() => setRegistering(true)}>
            Register donor
          </Button>
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Donor number, name or mobile"
              />
            </Field>
            <Field label="Group">
              <Select
                value={filters.bloodGroup ?? ''}
                onChange={(_, d) => set({ bloodGroup: (d.value || undefined) as BloodGroup | undefined })}
              >
                <option value="">All</option>
                {KNOWN_GROUPS.map((g) => (
                  <option key={g} value={g}>
                    {groupLabel(g)}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={Boolean(filters.eligibleOnly)}
              onChange={(_, d) => set({ eligibleOnly: d.checked || undefined })}
              label="Only donors not deferred"
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Blood donors"
          rows={donors.data?.items}
          loading={donors.isLoading}
          error={donors.error}
          onRetry={() => void donors.refetch()}
          getRowId={(d) => d.id}
          onRowClick={(d) => void navigate({ to: '/blood-bank/donors/$id', params: { id: d.id } })}
          columns={[
            {
              key: 'number',
              header: 'Donor',
              width: 130,
              render: (d) => <Text style={{ fontFamily: tokens.fontFamilyMonospace }}>{d.donorNumber}</Text>,
            },
            {
              key: 'name',
              header: 'Name',
              render: (d) => (
                <span>
                  <Text weight="semibold">{d.fullName}</Text>{' '}
                  <Text size={200}>
                    ({d.age} / {d.gender.charAt(0)})
                  </Text>
                </span>
              ),
            },
            {
              key: 'group',
              header: 'Group',
              width: 130,
              render: (d) => (
                <span>
                  {groupLabel(d.bloodGroup)}
                  {!d.bloodGroupConfirmed && d.bloodGroup !== 'Unknown' && (
                    <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                      not lab-confirmed
                    </Caption1>
                  )}
                </span>
              ),
            },
            { key: 'phone', header: 'Mobile', width: 140, priority: 'low', render: (d) => d.phoneMasked },
            {
              key: 'count',
              header: 'Donations',
              align: 'end',
              width: 100,
              priority: 'low',
              render: (d) => d.donationCount,
            },
            {
              key: 'last',
              header: 'Last donated',
              priority: 'low',
              render: (d) => (d.lastDonationAt ? formatDate(d.lastDonationAt) : 'Never'),
            },
            { key: 'status', header: 'Status', render: (d) => donorStatus(d, today) },
          ]}
          emptyTitle={filters.search ? 'No donor matches' : 'No donors registered yet'}
          emptyDescription="Register a donor to screen them and record a donation."
          page={filters.page ?? 1}
          pageSize={25}
          totalCount={donors.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
      {registering && (
        <DonorDrawer
          donor={null}
          onClose={() => setRegistering(false)}
          onSaved={(id) => void navigate({ to: '/blood-bank/donors/$id', params: { id } })}
        />
      )}
    </Page>
  );
}
