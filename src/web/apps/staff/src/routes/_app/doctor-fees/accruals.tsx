import { Badge, Caption1, Field, Input, Select, Text, tokens } from '@fluentui/react-components';
import { CoinStackRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatInr, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { SERVICE_CATEGORY_LABEL } from '@/modules/billing/labels';
import { formatPercent, sumRupees } from '@/modules/billing/money';
import { dateParam, enumParam, pageParam } from '@/modules/billing/searchParams';
import { Amount, NoAccess } from '@/modules/billing/ui';
import { ACCRUAL_STATUS_LABEL, ACCRUAL_STATUSES, feeQueries, type AccrualStatus } from '@/modules/doctor-fees/api';
import { PractitionerPicker, type PickedDoctor } from '@/modules/doctor-fees/PractitionerPicker';

interface AccrualsSearch {
  from?: string;
  to?: string;
  status?: AccrualStatus;
  page?: number;
}

export const Route = createFileRoute('/_app/doctor-fees/accruals')({
  validateSearch: (s: Record<string, unknown>): AccrualsSearch => ({
    from: dateParam(s.from),
    to: dateParam(s.to),
    status: enumParam(s.status, ACCRUAL_STATUSES),
    page: pageParam(s.page),
  }),
  component: AccrualsPage,
});

/** Fees earned per bill line (DFA-02), with reversals when a bill is cancelled. */
function AccrualsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canPayouts = usePermission('doctorfees.payouts.manage');
  const [doctor, setDoctor] = useState<PickedDoctor | null>(null);
  const from = search.from ?? todayIso(-30);
  const to = search.to ?? todayIso();
  const accruals = useQuery({
    ...feeQueries.accruals({ practitionerId: doctor?.id, from, to, status: search.status, page: search.page ?? 1 }),
    enabled: canPayouts,
  });
  const set = (patch: Partial<AccrualsSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  if (!canPayouts) {
    return (
      <Page>
        <PageHeader title="Fee accruals" />
        <NoAccess what="doctor fee accruals" />
      </Page>
    );
  }

  const pageTotal = accruals.data ? sumRupees(accruals.data.items.map((a) => a.shareAmount)) : null;

  return (
    <Page>
      <PageHeader
        title="Fee accruals"
        subtitle="Each doctor's share of each finalised bill line"
        icon={<CoinStackRegular />}
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <div style={{ flex: '1 1 260px', maxWidth: 420 }}>
              <PractitionerPicker value={doctor} onChange={setDoctor} hint="Leave empty for all doctors" />
            </div>
            <Field label="From">
              <Input type="date" value={from} max={to} onChange={(_, d) => set({ from: d.value || undefined })} />
            </Field>
            <Field label="To">
              <Input type="date" value={to} max={todayIso()} onChange={(_, d) => set({ to: d.value || undefined })} />
            </Field>
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as AccrualStatus | undefined })}
              >
                <option value="">All</option>
                {ACCRUAL_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {ACCRUAL_STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
          {pageTotal !== null && accruals.data && accruals.data.items.length > 0 && (
            <Caption1 style={{ color: tokens.colorNeutralForeground3 }}>
              {accruals.data.totalCount} fee lines · this page {formatInr(pageTotal)}
            </Caption1>
          )}
        </div>
        <DataTable
          ariaLabel="Fee accruals"
          rows={accruals.data?.items}
          loading={accruals.isLoading}
          error={accruals.error}
          onRetry={() => void accruals.refetch()}
          getRowId={(a) => a.id}
          columns={[
            { key: 'date', header: 'Earned on', width: 120, render: (a) => formatDateOnly(a.accruedOn) },
            { key: 'doctor', header: 'Doctor', render: (a) => a.practitionerName ?? '—' },
            {
              key: 'service',
              header: 'Service',
              render: (a) => (
                <span>
                  <Text>{a.description}</Text>{' '}
                  {a.isReversal && (
                    <Badge size="small" appearance="tint" color="danger">
                      Reversal
                    </Badge>
                  )}
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    Bill {a.billNumber} · {SERVICE_CATEGORY_LABEL[a.category]}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'basis',
              header: 'Billed value',
              align: 'end',
              priority: 'low',
              render: (a) => <Amount value={a.basisAmount} />,
            },
            {
              key: 'rate',
              header: 'Rate',
              align: 'end',
              priority: 'low',
              render: (a) => (a.method === 'Percentage' ? formatPercent(a.rate) : `${formatInr(a.rate)}/unit`),
            },
            {
              key: 'share',
              header: 'Share',
              align: 'end',
              render: (a) => <Amount value={a.shareAmount} strong tone={a.shareAmount < 0 ? 'negative' : undefined} />,
            },
            {
              key: 'status',
              header: 'Status',
              render: (a) => <StatusBadge status={a.status} label={ACCRUAL_STATUS_LABEL[a.status]} />,
            },
          ]}
          emptyTitle="No fees in this period"
          emptyDescription="Fees accrue when bills with a doctor on the service are finalised (or paid, depending on the hospital setting)."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={accruals.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
    </Page>
  );
}
