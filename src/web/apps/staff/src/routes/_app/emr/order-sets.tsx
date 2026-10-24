import { Button, Caption1, Field, SearchBox, Select, Switch, Text } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { useSpecialtyOptions } from '@/modules/emr/catalogue';
import { OrderSetDrawer } from '@/modules/emr/OrderSetDrawer';
import { orderQueries } from '@/modules/emr/ordersApi';

interface OrderSetsSearch {
  specialty?: string;
  inactive?: boolean;
}

export const Route = createFileRoute('/_app/emr/order-sets')({
  validateSearch: (s: Record<string, unknown>): OrderSetsSearch => ({
    specialty: typeof s.specialty === 'string' && s.specialty ? s.specialty : undefined,
    inactive: s.inactive === true ? true : undefined,
  }),
  component: OrderSetsPage,
});

/**
 * The order sets (EMR-05): named groups of tests and services a doctor orders in one go, by speciality or for every
 * speciality. Staff who maintain them add and edit them here.
 */
function OrderSetsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission('clinical.templates.manage');
  const [term, setTerm] = useState('');
  const [editing, setEditing] = useState<string | 'new' | null>(null);
  const specialties = useSpecialtyOptions(search.specialty);
  const sets = useQuery(
    orderQueries.orderSetCatalogue({
      specialtyCode: search.specialty ?? null,
      includeInactive: search.inactive ?? false,
    }),
  );
  const needle = term.trim().toLowerCase();
  const rows = sets.data?.filter((s) => !needle || `${s.name} ${s.code}`.toLowerCase().includes(needle));
  const set = (patch: OrderSetsSearch) => void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });
  // The drawer edits the set as the list has it now, so a reload after someone else's change brings their version.
  const editingSet = editing && editing !== 'new' ? (sets.data?.find((s) => s.id === editing) ?? null) : null;

  return (
    <Page>
      <PageHeader
        title="Order sets"
        subtitle="Groups of tests and services ordered together from an encounter, e.g. a diabetes work-up."
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              New order set
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '12px 16px' }}>
          <FilterBar>
            <Field label="Order set" style={{ flex: '1 1 260px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Name or code" />
            </Field>
            <Field label="Speciality" hint={search.specialty ? 'Sets for every speciality are listed too.' : undefined}>
              <Select value={search.specialty ?? ''} onChange={(_, d) => set({ specialty: d.value || undefined })}>
                <option value="">All specialities</option>
                {specialties.options.map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              label="Show inactive"
              checked={search.inactive ?? false}
              onChange={(_, d) => set({ inactive: d.checked || undefined })}
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Order sets"
          rows={rows}
          loading={sets.isLoading}
          error={sets.error}
          onRetry={() => void sets.refetch()}
          getRowId={(s) => s.id}
          onRowClick={canManage ? (s) => setEditing(s.id) : undefined}
          size="small"
          columns={[
            {
              key: 'name',
              header: 'Order set',
              render: (s) => (
                <span style={{ display: 'flex', flexDirection: 'column' }}>
                  <Text weight="semibold">{s.name}</Text>
                  <Caption1>{s.code}</Caption1>
                </span>
              ),
            },
            {
              key: 'specialty',
              header: 'Speciality',
              render: (s) => (s.specialtyCode ? specialties.nameOf(s.specialtyCode) : 'Every speciality'),
            },
            {
              key: 'services',
              header: 'Services',
              render: (s) => (
                <span style={{ display: 'flex', flexDirection: 'column' }}>
                  <Text>
                    {s.services.length} {s.services.length === 1 ? 'service' : 'services'}
                  </Text>
                  <Caption1>
                    {s.services
                      .slice(0, 3)
                      .map((x) => x.name)
                      .join(', ')}
                    {s.services.length > 3 ? '…' : ''}
                  </Caption1>
                </span>
              ),
            },
            { key: 'description', header: 'Description', priority: 'low', render: (s) => s.description ?? '—' },
            {
              key: 'active',
              header: 'Active',
              width: 90,
              render: (s) => (
                <StatusBadge
                  status={s.isActive ? 'Active' : 'Inactive'}
                  tone={s.isActive ? 'success' : 'informative'}
                />
              ),
            },
          ]}
          emptyTitle="No order sets match"
          emptyDescription={canManage ? 'Add an order set so doctors can order a work-up in one go.' : undefined}
          emptyAction={
            canManage ? (
              <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
                New order set
              </Button>
            ) : undefined
          }
        />
      </Section>
      <OrderSetDrawer
        open={editing === 'new' || editingSet !== null}
        orderSet={editingSet}
        onClose={() => setEditing(null)}
        onReload={() => void sets.refetch()}
      />
    </Page>
  );
}
