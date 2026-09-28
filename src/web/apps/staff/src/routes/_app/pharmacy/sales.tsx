import { Button, Caption1, Field, Input, SearchBox, Select, Switch, Text, tokens } from '@fluentui/react-components';
import { CartRegular } from '@fluentui/react-icons';
import { formatDate, formatDateOnly, formatInr, formatTime, humanize, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { StorePicker } from '@/modules/inventory/StorePicker';
import { useWorkingStore } from '@/modules/inventory/stores';
import { pharmacyQueries, SALE_STATUSES, type PharmacySaleListItem, type SaleFilters, type SaleStatus, type SaleType } from '@/modules/pharmacy/api';
import { PHARMACY_PERMISSIONS as P } from '@/modules/pharmacy/permissions';
import { SaleDrawer } from '@/modules/pharmacy/SaleDrawer';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

interface SalesSearch extends SaleFilters {
  id?: string;
}

export const Route = createFileRoute('/_app/pharmacy/sales')({
  validateSearch: (s: Record<string, unknown>): SalesSearch => ({
    from: typeof s.from === 'string' && DATE.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DATE.test(s.to) ? s.to : undefined,
    status: SALE_STATUSES.includes(s.status as SaleStatus) ? (s.status as SaleStatus) : undefined,
    type: s.type === 'Prescription' || s.type === 'Counter' ? (s.type as SaleType) : undefined,
    storeId: typeof s.storeId === 'string' && s.storeId ? s.storeId : undefined,
    withDues: s.withDues === true || s.withDues === 'true' ? true : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
    id: typeof s.id === 'string' && s.id ? s.id : undefined,
  }),
  component: SalesPage,
});

const STATUS_LABEL: Record<SaleStatus, string> = { Paid: 'Paid', PartiallyPaid: 'Part paid', Credit: 'On credit', Cancelled: 'Cancelled' };

/** Pharmacy invoices: reprint, returns, same-day cancellation and collection of dues. */
function SalesPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canSell = usePermission(P.sales);
  const working = useWorkingStore('pharmacy-op', { type: 'Pharmacy' });
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const today = todayIso();

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({ search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.search, navigate]);

  const { id, ...filters } = search;
  const sales = useQuery(pharmacyQueries.sales(filters));
  const set = (patch: Partial<SalesSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const from = search.from ?? today;
  const to = search.to ?? search.from ?? today;

  return (
    <Page>
      <PageHeader
        title="Invoices and returns"
        subtitle={from === today && to === today ? "Today's pharmacy invoices. Open one to reprint, take back items or collect a balance." : `From ${formatDateOnly(from)} to ${formatDateOnly(to)}`}
        actions={
          canSell && (
            <Button appearance="primary" icon={<CartRegular />} onClick={() => void navigate({ to: '/pharmacy/sale' })}>
              New counter sale
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 240px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Invoice number, customer name or UHID" />
            </Field>
            <Field label="From">
              <Input type="date" value={from} max={today} onChange={(_, d) => set({ from: d.value || undefined })} />
            </Field>
            <Field label="To">
              <Input type="date" value={to} min={from} max={today} onChange={(_, d) => set({ to: d.value || undefined })} />
            </Field>
            {working.stores.length > 1 && (
              <StorePicker stores={working.stores} value={search.storeId ?? null} onChange={(storeId) => set({ storeId: storeId ?? undefined })} allowAll allLabel="All pharmacies" label="Pharmacy" />
            )}
            <Field label="Type">
              <Select value={search.type ?? ''} onChange={(_, d) => set({ type: (d.value || undefined) as SaleType | undefined })}>
                <option value="">All</option>
                <option value="Prescription">Prescriptions</option>
                <option value="Counter">Counter sales</option>
              </Select>
            </Field>
            <Field label="Status">
              <Select value={search.status ?? ''} onChange={(_, d) => set({ status: (d.value || undefined) as SaleStatus | undefined })}>
                <option value="">All</option>
                {SALE_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch checked={search.withDues === true} onChange={(_, d) => set({ withDues: d.checked || undefined })} label="Only with balance due" />
          </FilterBar>
        </div>
        <DataTable<PharmacySaleListItem>
          ariaLabel="Pharmacy invoices"
          rows={sales.data?.items}
          loading={sales.isLoading}
          error={sales.error}
          onRetry={() => void sales.refetch()}
          getRowId={(s) => s.id}
          onRowClick={(s) => void navigate({ search: (prev) => ({ ...prev, id: s.id }) })}
          size="small"
          columns={[
            {
              key: 'time',
              header: 'Time',
              width: 110,
              render: (s) => (
                <span>
                  {formatTime(s.soldAt)}
                  {from !== to && <Caption1 block>{formatDate(s.soldAt)}</Caption1>}
                </span>
              ),
            },
            { key: 'invoice', header: 'Invoice', render: (s) => <Text font="monospace">{s.invoiceNumber}</Text> },
            {
              key: 'customer',
              header: 'Customer',
              render: (s) => (
                <span>
                  {s.customerName ?? 'Walk-in'}
                  {s.prescriptionNumber && (
                    <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                      Rx {s.prescriptionNumber}
                    </Caption1>
                  )}
                </span>
              ),
            },
            { key: 'type', header: 'Type', priority: 'low', render: (s) => (s.type === 'Prescription' ? 'Prescription' : 'Counter') },
            { key: 'net', header: 'Amount', align: 'end', render: (s) => formatInr(s.netAmount) },
            {
              key: 'due',
              header: 'Due',
              align: 'end',
              render: (s) => (s.dueAmount > 0 ? <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{formatInr(s.dueAmount)}</Text> : '—'),
            },
            { key: 'returned', header: 'Returned', align: 'end', priority: 'low', render: (s) => (s.returnedAmount > 0 ? formatInr(s.returnedAmount) : '—') },
            { key: 'status', header: 'Status', render: (s) => <StatusBadge status={s.status} label={STATUS_LABEL[s.status] ?? humanize(s.status)} /> },
          ]}
          emptyTitle="No invoices match"
          emptyDescription="Change the dates or the filters. Invoices appear here as soon as a sale or a prescription is billed."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={sales.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>
      {id && <SaleDrawer saleId={id} onClose={() => void navigate({ search: (prev) => ({ ...prev, id: undefined }) })} />}
    </Page>
  );
}
