import { Button, Field, Input, SearchBox, Select, Text } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatInr, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { StorePicker } from '@/modules/inventory/StorePicker';
import { DocStatus } from '@/modules/inventory/StockBits';
import { useWorkingStore } from '@/modules/inventory/stores';
import {
  ORDER_STATUSES,
  orderStatusLabel,
  purchaseQueries,
  type OrderFilters,
  type PurchaseOrderListItem,
  type PurchaseOrderStatus,
} from '@/modules/purchase/api';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

export const Route = createFileRoute('/_app/purchase/orders/')({
  validateSearch: (s: Record<string, unknown>): OrderFilters => ({
    status: ORDER_STATUSES.includes(s.status as PurchaseOrderStatus) ? (s.status as PurchaseOrderStatus) : undefined,
    vendorId: typeof s.vendorId === 'string' && s.vendorId ? s.vendorId : undefined,
    storeId: typeof s.storeId === 'string' && s.storeId ? s.storeId : undefined,
    from: typeof s.from === 'string' && DATE.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DATE.test(s.to) ? s.to : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: OrdersPage,
});

/** Purchase orders: draft → approval → sent to the vendor → received (GRN) → closed. */
function OrdersPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission(S.purchaseManage);
  const working = useWorkingStore('purchase');
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, search.search, navigate]);

  const orders = useQuery(purchaseQueries.orders(search));
  const set = (patch: Partial<OrderFilters>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Purchase orders"
        subtitle="Rates exclude GST; each order shows CGST + SGST or IGST from the vendor's state."
        actions={
          canManage && (
            <Button
              appearance="primary"
              icon={<AddRegular />}
              onClick={() => void navigate({ to: '/purchase/orders/new' })}
            >
              New purchase order
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Order number" style={{ flex: '0 1 200px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Exact number" />
            </Field>
            <StorePicker
              stores={working.stores}
              value={search.storeId ?? null}
              onChange={(id) => set({ storeId: id ?? undefined })}
              allowAll
            />
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as PurchaseOrderStatus | undefined })}
              >
                <option value="">All</option>
                {ORDER_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {orderStatusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="From">
              <Input
                type="date"
                value={search.from ?? ''}
                max={search.to ?? todayIso()}
                onChange={(_, d) => set({ from: d.value || undefined })}
              />
            </Field>
            <Field label="To">
              <Input
                type="date"
                value={search.to ?? ''}
                max={todayIso()}
                onChange={(_, d) => set({ to: d.value || undefined })}
              />
            </Field>
          </FilterBar>
        </div>
        <DataTable<PurchaseOrderListItem>
          ariaLabel="Purchase orders"
          rows={orders.data?.items}
          loading={orders.isLoading}
          error={orders.error}
          onRetry={() => void orders.refetch()}
          getRowId={(o) => o.id}
          onRowClick={(o) => void navigate({ to: '/purchase/orders/$orderId', params: { orderId: o.id } })}
          size="small"
          columns={[
            { key: 'no', header: 'Order', render: (o) => <Text font="monospace">{o.number}</Text> },
            { key: 'vendor', header: 'Vendor', render: (o) => <Text weight="semibold">{o.vendorName}</Text> },
            { key: 'store', header: 'Deliver to', priority: 'low', render: (o) => o.storeName },
            { key: 'date', header: 'Ordered', render: (o) => formatDateOnly(o.orderDate) },
            {
              key: 'expected',
              header: 'Expected',
              priority: 'low',
              render: (o) => formatDateOnly(o.expectedDeliveryDate),
            },
            { key: 'lines', header: 'Items', align: 'end', width: 70, priority: 'low', render: (o) => o.lineCount },
            { key: 'net', header: 'Value', align: 'end', render: (o) => formatInr(o.netAmount) },
            {
              key: 'status',
              header: 'Status',
              render: (o) => <DocStatus status={o.status} label={orderStatusLabel[o.status]} />,
            },
          ]}
          emptyTitle="No purchase orders match"
          emptyDescription={canManage ? 'Create an order for a vendor, or from an approved requisition.' : undefined}
          emptyAction={
            canManage ? (
              <Button
                appearance="primary"
                icon={<AddRegular />}
                onClick={() => void navigate({ to: '/purchase/orders/new' })}
              >
                New purchase order
              </Button>
            ) : undefined
          }
          page={search.page ?? 1}
          pageSize={25}
          totalCount={orders.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>
    </Page>
  );
}
