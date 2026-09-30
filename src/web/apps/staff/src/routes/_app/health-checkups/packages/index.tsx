import { Badge, Button, Caption1, Field, SearchBox, Switch, Text } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { formatInr, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { checkupQueries, type PackageDto } from '@/modules/health-checkups/api';

export const Route = createFileRoute('/_app/health-checkups/packages/')({
  component: PackagesPage,
});

function audience(p: PackageDto): string {
  const who = p.gender === 'Male' ? 'Men' : p.gender === 'Female' ? 'Women' : 'Everyone';
  const age =
    p.minAgeYears !== null && p.maxAgeYears !== null
      ? `${p.minAgeYears}–${p.maxAgeYears} y`
      : p.minAgeYears !== null
        ? `${p.minAgeYears}+ y`
        : p.maxAgeYears !== null
          ? `≤ ${p.maxAgeYears} y`
          : 'all ages';
  return `${who}, ${age}`;
}

/** Health packages (MHC-01): price, who they are for and what they include. */
function PackagesPage() {
  const navigate = useNavigate();
  const [term, setTerm] = useState('');
  const [showInactive, setShowInactive] = useState(false);
  const search = useDebouncedValue(term.trim(), 300);
  const packages = useQuery(checkupQueries.packages(search, showInactive));

  return (
    <Page>
      <PageHeader
        title="Health packages"
        subtitle="Each package is also a billable service: the patient pays the package price once, the tests are not charged separately."
        actions={
          <Button
            appearance="primary"
            icon={<AddRegular />}
            onClick={() => void navigate({ to: '/health-checkups/packages/$packageId', params: { packageId: 'new' } })}
          >
            New package
          </Button>
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Name or code" />
            </Field>
            <Switch
              checked={showInactive}
              onChange={(_, d) => setShowInactive(d.checked)}
              label="Show packages no longer offered"
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Health packages"
          rows={packages.data}
          loading={packages.isLoading}
          error={packages.error}
          onRetry={() => void packages.refetch()}
          getRowId={(p) => p.id}
          onRowClick={(p) => void navigate({ to: '/health-checkups/packages/$packageId', params: { packageId: p.id } })}
          size="small"
          columns={[
            {
              key: 'name',
              header: 'Package',
              render: (p) => (
                <span>
                  <Text weight="semibold">{p.name}</Text>
                  <Caption1 block>{p.code}</Caption1>
                </span>
              ),
            },
            { key: 'for', header: 'For', render: audience },
            { key: 'items', header: 'Includes', width: 110, align: 'end', render: (p) => `${p.items.length} items` },
            {
              key: 'price',
              header: 'Price',
              width: 120,
              align: 'end',
              render: (p) => formatInr(p.price, { whole: true }),
            },
            {
              key: 'billing',
              header: 'Billing',
              width: 130,
              priority: 'low',
              render: (p) =>
                p.serviceItemId ? (
                  'Registered'
                ) : (
                  <Badge color="warning" appearance="tint">
                    Not billable yet
                  </Badge>
                ),
            },
            {
              key: 'active',
              header: 'Status',
              width: 110,
              render: (p) => (
                <StatusBadge
                  status={p.isActive ? 'Active' : 'Inactive'}
                  tone={p.isActive ? 'success' : 'informative'}
                  label={p.isActive ? 'Offered' : 'Not offered'}
                />
              ),
            },
          ]}
          emptyTitle="No packages"
          emptyDescription="Create a package with its tests and consultations, then book patients on it."
        />
      </Section>
    </Page>
  );
}
