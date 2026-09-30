import { Button, Caption1, Field, Input, SearchBox, Select, Text } from '@fluentui/react-components';
import { CartRegular, ReceiptRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatInr, formatTime, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  canteenQueries,
  CUSTOMER_LABEL,
  PAYMENT_LABEL,
  PAYMENT_MODES,
  type CanteenPaymentMode,
  type CanteenSaleStatus,
} from '@/modules/canteen/api';
import { SaleDrawer } from '@/modules/canteen/SaleDrawer';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

interface SalesSearch {
  date?: string;
  status?: CanteenSaleStatus;
  paymentMode?: CanteenPaymentMode;
  employeeCode?: string;
  q?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/canteen/sales')({
  validateSearch: (s: Record<string, unknown>): SalesSearch => ({
    date: typeof s.date === 'string' && DATE.test(s.date) ? s.date : undefined,
    status: s.status === 'Completed' || s.status === 'Cancelled' ? s.status : undefined,
    paymentMode: PAYMENT_MODES.includes(s.paymentMode as CanteenPaymentMode)
      ? (s.paymentMode as CanteenPaymentMode)
      : undefined,
    employeeCode: typeof s.employeeCode === 'string' && s.employeeCode ? s.employeeCode : undefined,
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: CanteenSalesPage,
});

/** Canteen bills of a business day, newest first, with reprint and same-day cancellation. */
function CanteenSalesPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canSell = usePermission('canteen.sales.create');
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [openSale, setOpenSale] = useState<string | null>(null);
  const today = todayIso();
  const date = search.date ?? today;
  const sales = useQuery(
    canteenQueries.sales({
      date,
      status: search.status,
      paymentMode: search.paymentMode,
      employeeCode: search.employeeCode,
      search: search.q,
      page: search.page,
    }),
  );

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const set = (patch: Partial<SalesSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }), replace: true });

  return (
    <Page>
      <PageHeader
        title="Canteen bills"
        icon={<ReceiptRegular />}
        subtitle={date === today ? 'Today' : formatDateOnly(date)}
        actions={
          canSell && (
            <Button appearance="primary" icon={<CartRegular />} onClick={() => void navigate({ to: '/canteen' })}>
              New bill
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Bill number" style={{ flex: '1 1 200px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Full or last digits" />
            </Field>
            <Field label="Date">
              <Input
                type="date"
                value={date}
                max={today}
                onChange={(_, d) => d.value && set({ date: d.value === today ? undefined : d.value })}
              />
            </Field>
            <Field label="Paid by">
              <Select
                value={search.paymentMode ?? ''}
                onChange={(_, d) => set({ paymentMode: (d.value || undefined) as CanteenPaymentMode | undefined })}
              >
                <option value="">Any</option>
                {PAYMENT_MODES.map((m) => (
                  <option key={m} value={m}>
                    {PAYMENT_LABEL[m]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as CanteenSaleStatus | undefined })}
              >
                <option value="">Any</option>
                <option value="Completed">Completed</option>
                <option value="Cancelled">Cancelled</option>
              </Select>
            </Field>
            <Field label="Employee code">
              <Input
                defaultValue={search.employeeCode ?? ''}
                placeholder="Enter to apply"
                style={{ width: 150 }}
                onBlur={(event) => set({ employeeCode: event.target.value.trim() || undefined })}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') {
                    set({ employeeCode: event.currentTarget.value.trim() || undefined });
                  }
                }}
              />
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Canteen bills"
          rows={sales.data?.items}
          loading={sales.isLoading}
          error={sales.error}
          onRetry={() => void sales.refetch()}
          getRowId={(s) => s.id}
          onRowClick={(s) => setOpenSale(s.id)}
          columns={[
            { key: 'time', header: 'Time', width: 90, render: (s) => formatTime(s.soldAt) },
            { key: 'bill', header: 'Bill', width: 180, render: (s) => <Text font="monospace">{s.billNumber}</Text> },
            {
              key: 'customer',
              header: 'Customer',
              render: (s) => (
                <span>
                  {CUSTOMER_LABEL[s.customerType]}
                  {(s.employeeCode || s.customerName) && <Caption1 block>{s.employeeCode ?? s.customerName}</Caption1>}
                </span>
              ),
            },
            { key: 'mode', header: 'Paid by', width: 120, render: (s) => PAYMENT_LABEL[s.paymentMode] },
            {
              key: 'amount',
              header: 'Amount',
              width: 120,
              align: 'end',
              render: (s) => <Text weight="semibold">{formatInr(s.netAmount)}</Text>,
            },
            { key: 'cashier', header: 'Cashier', priority: 'low', render: (s) => s.cashierName ?? '—' },
            {
              key: 'status',
              header: 'Status',
              width: 120,
              render: (s) => <StatusBadge status={s.status} tone={s.status === 'Completed' ? 'success' : 'danger'} />,
            },
          ]}
          emptyTitle="No bills"
          emptyDescription={date === today ? 'Bills appear here as soon as they are charged.' : undefined}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={sales.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
      {openSale && <SaleDrawer saleId={openSale} onClose={() => setOpenSale(null)} />}
    </Page>
  );
}
