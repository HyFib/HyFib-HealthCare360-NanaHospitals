import { Badge, Button, Caption1, Field, SearchBox, Switch, Text } from '@fluentui/react-components';
import { AddRegular, StarRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatPhone, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { gstStateName } from '@/modules/inventory/gst';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { purchaseQueries, type VendorListItem } from '@/modules/purchase/api';
import { VendorDrawer } from '@/modules/purchase/VendorDrawer';

interface VendorSearch {
  q?: string;
  inactive?: boolean;
  page?: number;
  edit?: string;
}

export const Route = createFileRoute('/_app/purchase/vendors')({
  validateSearch: (s: Record<string, unknown>): VendorSearch => ({
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    inactive: s.inactive === true || s.inactive === 'true' ? true : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
    edit: typeof s.edit === 'string' && s.edit ? s.edit : undefined,
  }),
  component: VendorsPage,
});

/** Vendor master shared by all facilities: GSTIN, drug licence and credit terms. */
function VendorsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission(S.purchaseManage);
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const vendors = useQuery(
    purchaseQueries.vendors({ search: search.q, includeInactive: search.inactive, page: search.page }),
  );
  const set = (patch: Partial<VendorSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Vendors"
        subtitle="Suppliers with their GSTIN (decides CGST + SGST or IGST), drug licence and credit terms"
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => set({ edit: 'new' })}>
              Add vendor
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 280px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Name, code or GSTIN"
                autoFocus
              />
            </Field>
            <Switch
              checked={search.inactive === true}
              onChange={(_, d) => set({ inactive: d.checked || undefined })}
              label="Show inactive"
            />
          </FilterBar>
        </div>
        <DataTable<VendorListItem>
          ariaLabel="Vendors"
          rows={vendors.data?.items}
          loading={vendors.isLoading}
          error={vendors.error}
          onRetry={() => void vendors.refetch()}
          getRowId={(v) => v.id}
          onRowClick={(v) => set({ edit: v.id })}
          size="small"
          columns={[
            { key: 'code', header: 'Code', width: 110, render: (v) => <Text font="monospace">{v.code}</Text> },
            {
              key: 'name',
              header: 'Vendor',
              render: (v) => (
                <span>
                  <Text weight="semibold">{v.name}</Text>
                  {v.city && <Caption1 block>{v.city}</Caption1>}
                </span>
              ),
            },
            {
              key: 'gstin',
              header: 'GSTIN',
              render: (v) =>
                v.gstin ? (
                  <span>
                    <Text font="monospace">{v.gstin}</Text>
                    <Caption1 block>{gstStateName(v.gstin)}</Caption1>
                  </span>
                ) : (
                  <Caption1>Unregistered</Caption1>
                ),
            },
            {
              key: 'licence',
              header: 'Drug licence',
              priority: 'low',
              render: (v) =>
                v.drugLicenseExpired ? (
                  <Badge color="danger">Expired {formatDateOnly(v.drugLicenseValidUntil)}</Badge>
                ) : v.drugLicenseNumber ? (
                  <span>
                    {v.drugLicenseNumber}
                    {v.drugLicenseValidUntil && <Caption1 block>to {formatDateOnly(v.drugLicenseValidUntil)}</Caption1>}
                  </span>
                ) : (
                  '—'
                ),
            },
            {
              key: 'contact',
              header: 'Contact',
              priority: 'low',
              render: (v) => (
                <span>
                  {v.contactPerson ?? '—'}
                  {v.phone && <Caption1 block>{formatPhone(v.phone)}</Caption1>}
                </span>
              ),
            },
            {
              key: 'terms',
              header: 'Credit',
              align: 'end',
              width: 80,
              priority: 'low',
              render: (v) => (v.paymentTermsDays ? `${v.paymentTermsDays} d` : 'Cash'),
            },
            {
              key: 'rating',
              header: 'Rating',
              width: 80,
              priority: 'low',
              render: (v) =>
                v.rating > 0 ? (
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: 2 }}>
                    <StarRegular aria-hidden /> {v.rating}
                  </span>
                ) : (
                  '—'
                ),
            },
            {
              key: 'status',
              header: 'Status',
              width: 100,
              render: (v) => (
                <StatusBadge
                  status={v.isActive ? 'Active' : 'Inactive'}
                  tone={v.isActive ? 'success' : 'informative'}
                />
              ),
            },
          ]}
          emptyTitle={search.q ? 'No vendor matches' : 'No vendors yet'}
          emptyDescription={canManage ? 'Add suppliers with their GSTIN and drug licence before ordering.' : undefined}
          emptyAction={
            canManage ? (
              <Button appearance="primary" icon={<AddRegular />} onClick={() => set({ edit: 'new' })}>
                Add vendor
              </Button>
            ) : undefined
          }
          page={search.page ?? 1}
          pageSize={25}
          totalCount={vendors.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>
      {search.edit && (
        <VendorDrawer
          key={search.edit}
          vendorId={search.edit === 'new' ? null : search.edit}
          canManage={canManage}
          onClose={() => set({ edit: undefined })}
        />
      )}
    </Page>
  );
}
