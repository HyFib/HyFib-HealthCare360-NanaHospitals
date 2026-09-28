import {
  Button,
  Caption1,
  Field,
  Input,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  SearchBox,
  Select,
  Text,
  tokens,
} from '@fluentui/react-components';
import { MoreHorizontalRegular, PrintRegular, ReceiptRegular } from '@fluentui/react-icons';
import { formatDateTime, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { billingQueries } from '@/modules/billing/api';
import { useBillingLive, useBillingPrint } from '@/modules/billing/hooks';
import {
  modeSummaryLabel,
  RECEIPT_PURPOSE_LABEL,
  RECEIPT_PURPOSES,
  type ReceiptPurpose,
} from '@/modules/billing/labels';
import { sumRupees } from '@/modules/billing/money';
import { dateParam, enumParam, pageParam, textParam } from '@/modules/billing/searchParams';
import { Amount, NoAccess } from '@/modules/billing/ui';

interface ReceiptsSearch {
  q?: string;
  from?: string;
  to?: string;
  purpose?: ReceiptPurpose;
  counter?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/billing/receipts')({
  validateSearch: (s: Record<string, unknown>): ReceiptsSearch => ({
    q: textParam(s.q, 40),
    from: dateParam(s.from),
    to: dateParam(s.to),
    purpose: enumParam(s.purpose, RECEIPT_PURPOSES),
    counter: textParam(s.counter, 64),
    page: pageParam(s.page),
  }),
  component: ReceiptsPage,
});

/** Receipts of a day (or range): reprint on the receipt printer or A4, open the bill. */
function ReceiptsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canRead = usePermission(['billing.charges.read', 'billing.payments.collect']);
  const { printReceiptById } = useBillingPrint();
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  useBillingLive();

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const today = todayIso();
  const receipts = useQuery({
    ...billingQueries.receipts({
      search: search.q,
      from: search.from ?? today,
      to: search.to ?? search.from ?? today,
      purpose: search.purpose,
      counter: search.counter,
      page: search.page ?? 1,
    }),
    enabled: canRead,
  });
  const set = (patch: Partial<ReceiptsSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="Receipts" />
        <NoAccess what="receipts" />
      </Page>
    );
  }

  const pageTotal = receipts.data ? sumRupees(receipts.data.items.map((r) => r.amount)) : null;

  return (
    <Page>
      <PageHeader
        title="Receipts"
        subtitle="Money received at the counters, terminals and online"
        icon={<ReceiptRegular />}
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Receipt number" style={{ flex: '1 1 220px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="e.g. NHRC260000123" />
            </Field>
            <Field label="From">
              <Input
                type="date"
                value={search.from ?? today}
                max={today}
                onChange={(_, d) => set({ from: d.value || undefined })}
              />
            </Field>
            <Field label="To">
              <Input
                type="date"
                value={search.to ?? search.from ?? today}
                max={today}
                onChange={(_, d) => set({ to: d.value || undefined })}
              />
            </Field>
            <Field label="For">
              <Select
                value={search.purpose ?? ''}
                onChange={(_, d) => set({ purpose: (d.value || undefined) as ReceiptPurpose | undefined })}
              >
                <option value="">Everything</option>
                {RECEIPT_PURPOSES.map((p) => (
                  <option key={p} value={p}>
                    {RECEIPT_PURPOSE_LABEL[p]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Counter">
              <Input
                value={search.counter ?? ''}
                onChange={(_, d) => set({ counter: d.value || undefined })}
                placeholder="All counters"
              />
            </Field>
          </FilterBar>
          {pageTotal !== null && receipts.data && receipts.data.items.length > 0 && (
            <Caption1 style={{ color: tokens.colorNeutralForeground3 }}>
              {receipts.data.totalCount} receipt{receipts.data.totalCount === 1 ? '' : 's'} · this page{' '}
              <Amount value={pageTotal} strong />
            </Caption1>
          )}
        </div>
        <DataTable
          ariaLabel="Receipts"
          rows={receipts.data?.items}
          loading={receipts.isLoading}
          error={receipts.error}
          onRetry={() => void receipts.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) =>
            r.billId
              ? void navigate({ to: '/billing/bills/$billId', params: { billId: r.billId } })
              : void printReceiptById(r.id)
          }
          columns={[
            {
              key: 'no',
              header: 'Receipt',
              width: 160,
              render: (r) => <Text font="monospace">{r.receiptNumber}</Text>,
            },
            { key: 'at', header: 'Received', width: 170, render: (r) => formatDateTime(r.receivedAt) },
            {
              key: 'patient',
              header: 'Patient',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.patientName ?? '—'}</Text>
                  <Caption1 block>{r.uhid}</Caption1>
                </span>
              ),
            },
            { key: 'purpose', header: 'For', priority: 'low', render: (r) => RECEIPT_PURPOSE_LABEL[r.purpose] },
            { key: 'mode', header: 'Mode', render: (r) => modeSummaryLabel(r.modes) },
            {
              key: 'by',
              header: 'Received by',
              priority: 'low',
              render: (r) => `${r.receivedByName ?? '—'}${r.counter ? ` · ${r.counter}` : ''}`,
            },
            { key: 'amount', header: 'Amount', align: 'end', render: (r) => <Amount value={r.amount} strong /> },
          ]}
          rowActions={(r) => (
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<MoreHorizontalRegular />}
                  aria-label={`Reprint ${r.receiptNumber}`}
                />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem icon={<PrintRegular />} onClick={() => void printReceiptById(r.id, 'Receipt80')}>
                    Receipt printer (80 mm)
                  </MenuItem>
                  <MenuItem icon={<PrintRegular />} onClick={() => void printReceiptById(r.id, 'A4')}>
                    A4 receipt
                  </MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          )}
          emptyTitle="No receipts for these filters"
          emptyDescription="Change the dates, or search by the full receipt number."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={receipts.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
    </Page>
  );
}
