import { Badge, Button, Caption1, Field, Input, SearchBox, Select, Switch, Text } from '@fluentui/react-components';
import { ReceiptAddRegular } from '@fluentui/react-icons';
import { formatDateTime, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { Can, usePermission } from '@/lib/permissions';
import { billingQueries, type BillFilters } from '@/modules/billing/api';
import { useBillingLive } from '@/modules/billing/hooks';
import {
  BILL_STATUS_LABEL,
  BILL_STATUSES,
  BILL_TYPE_LABEL,
  BILL_TYPES,
  type BillStatus,
  type BillType,
} from '@/modules/billing/labels';
import { dateParam, enumParam, flagParam, pageParam, textParam } from '@/modules/billing/searchParams';
import { Amount, BillStatusBadge, NoAccess } from '@/modules/billing/ui';

const SORTS = ['createdAt', 'finalizedAt', 'billNumber', 'netAmount', 'dueAmount'] as const;
type SortKey = (typeof SORTS)[number];

interface BillsSearch {
  q?: string;
  from?: string;
  to?: string;
  type?: BillType;
  status?: BillStatus;
  due?: true;
  sort?: SortKey;
  asc?: true;
  page?: number;
}

export const Route = createFileRoute('/_app/billing/bills/')({
  validateSearch: (s: Record<string, unknown>): BillsSearch => ({
    q: textParam(s.q),
    from: dateParam(s.from),
    to: dateParam(s.to),
    type: enumParam(s.type, BILL_TYPES),
    status: enumParam(s.status, BILL_STATUSES),
    due: flagParam(s.due),
    sort: enumParam(s.sort, SORTS),
    asc: flagParam(s.asc),
    page: pageParam(s.page),
  }),
  component: BillsPage,
});

const PRESETS = [
  { label: 'Today', from: 0 },
  { label: '7 days', from: -6 },
  { label: '30 days', from: -29 },
] as const;

/** Bill search: number or patient, date range, type, status and bills with a balance due; server paged and sortable. */
function BillsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canRead = usePermission([
    'billing.charges.read',
    'billing.op.create',
    'billing.ip.manage',
    'billing.payments.collect',
  ]);
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  useBillingLive();

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const filters: BillFilters = {
    search: search.q,
    from: search.from,
    to: search.to,
    type: search.type,
    status: search.status,
    withDueOnly: search.due,
    sort: search.sort ?? 'createdAt',
    desc: !search.asc,
    page: search.page ?? 1,
  };
  const bills = useQuery({ ...billingQueries.bills(filters), enabled: canRead });
  const set = (patch: Partial<BillsSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="Bills" />
        <NoAccess what="bills" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Bills"
        subtitle="Find a bill to reprint, collect a balance, issue a credit note or cancel it"
        actions={
          <Can permission="billing.op.create">
            <Button
              appearance="primary"
              icon={<ReceiptAddRegular />}
              onClick={() => void navigate({ to: '/billing/op' })}
            >
              New OP bill
            </Button>
          </Can>
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Bill number, UHID, mobile or name"
                autoFocus
              />
            </Field>
            <Field label="From">
              <Input
                type="date"
                value={search.from ?? ''}
                max={todayIso()}
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
            <Field label="Quick range">
              <Select
                value=""
                onChange={(_, d) => {
                  const preset = PRESETS.find((p) => p.label === d.value);
                  if (preset) {
                    set({ from: todayIso(preset.from), to: todayIso() });
                  } else if (d.value === 'all') {
                    set({ from: undefined, to: undefined });
                  }
                }}
              >
                <option value="">Choose…</option>
                {PRESETS.map((p) => (
                  <option key={p.label} value={p.label}>
                    {p.label}
                  </option>
                ))}
                <option value="all">Any date</option>
              </Select>
            </Field>
            <Field label="Type">
              <Select
                value={search.type ?? ''}
                onChange={(_, d) => set({ type: (d.value || undefined) as BillType | undefined })}
              >
                <option value="">OP and IP</option>
                {BILL_TYPES.map((t) => (
                  <option key={t} value={t}>
                    {BILL_TYPE_LABEL[t]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as BillStatus | undefined })}
              >
                <option value="">All</option>
                {BILL_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {BILL_STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={Boolean(search.due)}
              onChange={(_, d) => set({ due: d.checked ? true : undefined })}
              label="Only with balance due"
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Bills"
          rows={bills.data?.items}
          loading={bills.isLoading}
          error={bills.error}
          onRetry={() => void bills.refetch()}
          getRowId={(b) => b.id}
          onRowClick={(b) => void navigate({ to: '/billing/bills/$billId', params: { billId: b.id } })}
          sort={{ key: filters.sort ?? 'createdAt', desc: filters.desc ?? true }}
          onSortChange={(sort) => set({ sort: sort.key as SortKey, asc: sort.desc ? undefined : true })}
          columns={[
            {
              key: 'billNumber',
              header: 'Bill',
              sortable: true,
              render: (b) => (
                <span>
                  <Text font="monospace" weight="semibold">
                    {b.billNumber ?? 'Draft'}
                  </Text>
                  {b.isFinalBill && (
                    <Badge size="small" appearance="outline" style={{ marginLeft: 6 }}>
                      Final
                    </Badge>
                  )}
                </span>
              ),
            },
            {
              key: 'createdAt',
              header: 'Date',
              sortable: true,
              width: 170,
              render: (b) => formatDateTime(b.finalizedAt ?? b.createdAt),
            },
            {
              key: 'patient',
              header: 'Patient',
              render: (b) => (
                <span>
                  <Text weight="semibold">{b.patientName ?? '—'}</Text>
                  <Caption1 block>{b.uhid}</Caption1>
                </span>
              ),
            },
            { key: 'type', header: 'Type', width: 70, priority: 'low', render: (b) => BILL_TYPE_LABEL[b.type] },
            { key: 'payer', header: 'Payer', priority: 'low', render: (b) => b.payerName ?? 'Self pay' },
            {
              key: 'netAmount',
              header: 'Bill total',
              align: 'end',
              sortable: true,
              render: (b) => <Amount value={b.netAmount} />,
            },
            {
              key: 'paid',
              header: 'Paid',
              align: 'end',
              priority: 'low',
              render: (b) => <Amount value={b.paidAmount} />,
            },
            {
              key: 'dueAmount',
              header: 'Due',
              align: 'end',
              sortable: true,
              render: (b) =>
                b.dueAmount > 0 ? (
                  <Amount value={b.dueAmount} strong tone="negative" />
                ) : (
                  <Amount value={0} tone="muted" />
                ),
            },
            { key: 'status', header: 'Status', width: 140, render: (b) => <BillStatusBadge status={b.status} /> },
          ]}
          emptyTitle={search.q ? 'No bill matches your search' : 'No bills for these filters'}
          emptyDescription="Check the bill number or widen the dates."
          page={filters.page}
          pageSize={25}
          totalCount={bills.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
    </Page>
  );
}
