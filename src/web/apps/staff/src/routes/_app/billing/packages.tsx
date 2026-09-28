import { Button, Caption1, Switch, Text, tokens } from '@fluentui/react-components';
import { AddRegular, BoxMultipleRegular } from '@fluentui/react-icons';
import { DataTable, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { mastersQueries, type PackageDetail } from '@/modules/billing/mastersApi';
import { PackageDrawer } from '@/modules/billing/PackageDrawer';
import { flagParam } from '@/modules/billing/searchParams';
import { Amount, NoAccess } from '@/modules/billing/ui';

export const Route = createFileRoute('/_app/billing/packages')({
  validateSearch: (s: Record<string, unknown>): { inactive?: true } => ({ inactive: flagParam(s.inactive) }),
  component: PackagesPage,
});

/** Treatment packages (MB-02, IPB-04): fixed price, expected stay and inclusions with caps. */
function PackagesPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRead = usePermission(['billing.tariffs.manage', 'billing.ip.manage', 'billing.charges.read']);
  const canManage = usePermission('billing.tariffs.manage');
  const [editing, setEditing] = useState<PackageDetail | 'new' | null>(null);
  const [opening, setOpening] = useState<string | null>(null);
  const packages = useQuery({ ...mastersQueries.packages(Boolean(search.inactive)), enabled: canRead });
  const plans = useQuery({ ...mastersQueries.tariffPlans(true), enabled: canRead });

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="Packages" />
        <NoAccess what="packages" />
      </Page>
    );
  }

  const open = async (id: string) => {
    setOpening(id);
    try {
      const detail = await queryClient.fetchQuery(mastersQueries.package(id));
      setEditing(detail);
    } catch (error) {
      notify.error(error, 'Could not open the package');
    } finally {
      setOpening(null);
    }
  };

  return (
    <Page>
      <PageHeader
        title="Packages"
        subtitle="Fixed-price treatments such as normal delivery or a planned surgery"
        icon={<BoxMultipleRegular />}
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              New package
            </Button>
          )
        }
      />
      <Section flush actions={<Switch checked={Boolean(search.inactive)} onChange={(_, d) => void navigate({ search: { inactive: d.checked ? true : undefined } })} label="Show inactive" />} title="All packages">
        <DataTable
          ariaLabel="Packages"
          rows={packages.data}
          loading={packages.isLoading}
          error={packages.error}
          onRetry={() => void packages.refetch()}
          getRowId={(p) => p.id}
          onRowClick={canManage ? (p) => void open(p.id) : undefined}
          columns={[
            {
              key: 'name',
              header: 'Package',
              render: (p) => (
                <span>
                  <Text weight="semibold">{p.name}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {p.code}
                    {opening === p.id ? ' · opening…' : ''}
                  </Caption1>
                </span>
              ),
            },
            { key: 'price', header: 'Price', align: 'end', render: (p) => <Amount value={p.price} strong /> },
            { key: 'stay', header: 'Stay', align: 'end', render: (p) => (p.lengthOfStayDays ? `${p.lengthOfStayDays} days` : '—') },
            { key: 'inclusions', header: 'Inclusions', align: 'end', priority: 'low', render: (p) => p.inclusionCount },
            { key: 'plan', header: 'Tariff', priority: 'low', render: (p) => plans.data?.find((plan) => plan.id === p.tariffPlanId)?.name ?? 'Any' },
            { key: 'status', header: 'Status', render: (p) => <StatusBadge status={p.isActive ? 'Active' : 'Inactive'} tone={p.isActive ? 'success' : 'informative'} /> },
          ]}
          emptyTitle="No packages yet"
          emptyDescription="Create a package, then assign it from the IP account of an admitted patient."
          size="small"
        />
      </Section>
      {editing && <PackageDrawer packageDetail={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Page>
  );
}
