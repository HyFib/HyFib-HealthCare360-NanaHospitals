import { Button, Field, Input, SearchBox, Select, Text } from '@fluentui/react-components';
import { BoxArrowLeftRegular } from '@fluentui/react-icons';
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
  GRN_STATUSES,
  purchaseQueries,
  type GoodsReceiptListItem,
  type GoodsReceiptStatus,
} from '@/modules/purchase/api';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

interface GrnSearch {
  status?: GoodsReceiptStatus;
  storeId?: string;
  from?: string;
  to?: string;
  q?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/purchase/grns/')({
  validateSearch: (s: Record<string, unknown>): GrnSearch => ({
    status: GRN_STATUSES.includes(s.status as GoodsReceiptStatus) ? (s.status as GoodsReceiptStatus) : undefined,
    storeId: typeof s.storeId === 'string' && s.storeId ? s.storeId : undefined,
    from: typeof s.from === 'string' && DATE.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DATE.test(s.to) ? s.to : undefined,
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: GrnsPage,
});

const STATUS_LABEL: Record<GoodsReceiptStatus, string> = {
  Draft: 'Draft (not in stock yet)',
  Posted: 'Posted',
  Cancelled: 'Cancelled',
};

/** Goods receipts (INV-03): vendor invoices received into a store with batch, expiry and MRP. */
function GrnsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canReceive = usePermission(S.grnReceive);
  const working = useWorkingStore('purchase');
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const grns = useQuery(
    purchaseQueries.grns({
      status: search.status,
      storeId: search.storeId,
      from: search.from,
      to: search.to,
      search: search.q,
      page: search.page,
    }),
  );
  const set = (patch: Partial<GrnSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Goods receipts"
        subtitle="Each receipt records a vendor invoice. Posting adds the batches to stock."
        actions={
          canReceive && (
            <Button
              appearance="primary"
              icon={<BoxArrowLeftRegular />}
              onClick={() => void navigate({ to: '/purchase/grns/new' })}
            >
              Receive goods
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="GRN or invoice number" style={{ flex: '0 1 240px' }}>
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
                onChange={(_, d) => set({ status: (d.value || undefined) as GoodsReceiptStatus | undefined })}
              >
                <option value="">All</option>
                {GRN_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Received from">
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
        <DataTable<GoodsReceiptListItem>
          ariaLabel="Goods receipts"
          rows={grns.data?.items}
          loading={grns.isLoading}
          error={grns.error}
          onRetry={() => void grns.refetch()}
          getRowId={(g) => g.id}
          onRowClick={(g) => void navigate({ to: '/purchase/grns/$grnId', params: { grnId: g.id } })}
          size="small"
          columns={[
            { key: 'no', header: 'GRN', render: (g) => <Text font="monospace">{g.number}</Text> },
            { key: 'received', header: 'Received', render: (g) => formatDateOnly(g.receivedOn) },
            { key: 'vendor', header: 'Vendor', render: (g) => <Text weight="semibold">{g.vendorName}</Text> },
            {
              key: 'invoice',
              header: 'Vendor invoice',
              render: (g) =>
                `${g.vendorInvoiceNumber}${g.vendorInvoiceDate ? ` · ${formatDateOnly(g.vendorInvoiceDate)}` : ''}`,
            },
            { key: 'store', header: 'Store', priority: 'low', render: (g) => g.storeName },
            { key: 'net', header: 'Value', align: 'end', render: (g) => formatInr(g.netAmount) },
            {
              key: 'status',
              header: 'Status',
              render: (g) => <DocStatus status={g.status} label={g.status === 'Draft' ? 'Draft' : undefined} />,
            },
          ]}
          emptyTitle="No goods receipts match"
          emptyDescription={
            canReceive ? 'Receive goods against a purchase order or directly from a vendor invoice.' : undefined
          }
          page={search.page ?? 1}
          pageSize={25}
          totalCount={grns.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>
    </Page>
  );
}
