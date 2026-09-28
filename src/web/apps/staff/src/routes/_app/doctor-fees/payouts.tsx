import { Button, Field, Select, Text } from '@fluentui/react-components';
import { AddRegular, MoneyHandRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { enumParam, pageParam } from '@/modules/billing/searchParams';
import { Amount, NoAccess } from '@/modules/billing/ui';
import { feeQueries, PAYOUT_STATUS_LABEL, PAYOUT_STATUSES, type PayoutStatus } from '@/modules/doctor-fees/api';
import { CreatePayoutDialog, PayoutDialog } from '@/modules/doctor-fees/PayoutDialogs';
import { PractitionerPicker, type PickedDoctor } from '@/modules/doctor-fees/PractitionerPicker';

interface PayoutsSearch {
  status?: PayoutStatus;
  page?: number;
}

export const Route = createFileRoute('/_app/doctor-fees/payouts')({
  validateSearch: (s: Record<string, unknown>): PayoutsSearch => ({
    status: enumParam(s.status, PAYOUT_STATUSES),
    page: pageParam(s.page),
  }),
  component: PayoutsPage,
});

/** Doctor payouts (DFA-03..05): prepare, approve (by someone else), pay, or discard a draft. */
function PayoutsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canPayouts = usePermission('doctorfees.payouts.manage');
  const [doctor, setDoctor] = useState<PickedDoctor | null>(null);
  const [creating, setCreating] = useState(false);
  const [opened, setOpened] = useState<string | null>(null);
  const payouts = useQuery({
    ...feeQueries.payouts({ practitionerId: doctor?.id, status: search.status, page: search.page ?? 1 }),
    enabled: canPayouts,
  });

  if (!canPayouts) {
    return (
      <Page>
        <PageHeader title="Doctor payouts" />
        <NoAccess what="doctor payouts" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Doctor payouts"
        subtitle="Monthly settlement of doctors' fees, after deductions and TDS"
        icon={<MoneyHandRegular />}
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setCreating(true)}>
            Prepare payout
          </Button>
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <div style={{ flex: '1 1 260px', maxWidth: 420 }}>
              <PractitionerPicker value={doctor} onChange={setDoctor} hint="Leave empty for all doctors" />
            </div>
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) =>
                  void navigate({ search: { status: (d.value || undefined) as PayoutStatus | undefined } })
                }
              >
                <option value="">All</option>
                {PAYOUT_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {PAYOUT_STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Doctor payouts"
          rows={payouts.data?.items}
          loading={payouts.isLoading}
          error={payouts.error}
          onRetry={() => void payouts.refetch()}
          getRowId={(p) => p.id}
          onRowClick={(p) => setOpened(p.id)}
          columns={[
            { key: 'no', header: 'Payout', width: 150, render: (p) => <Text font="monospace">{p.payoutNumber}</Text> },
            {
              key: 'doctor',
              header: 'Doctor',
              render: (p) => <Text weight="semibold">{p.practitionerName ?? '—'}</Text>,
            },
            {
              key: 'period',
              header: 'Period',
              render: (p) => `${formatDateOnly(p.periodFrom)} – ${formatDateOnly(p.periodTo)}`,
            },
            {
              key: 'gross',
              header: 'Fees',
              align: 'end',
              priority: 'low',
              render: (p) => <Amount value={p.grossAmount} />,
            },
            { key: 'tds', header: 'TDS', align: 'end', priority: 'low', render: (p) => <Amount value={p.tdsAmount} /> },
            { key: 'net', header: 'Net', align: 'end', render: (p) => <Amount value={p.netAmount} strong /> },
            {
              key: 'paid',
              header: 'Paid',
              priority: 'low',
              render: (p) => (p.paidAt ? formatDateTime(p.paidAt) : '—'),
            },
            {
              key: 'status',
              header: 'Status',
              width: 170,
              render: (p) => <StatusBadge status={p.status} label={PAYOUT_STATUS_LABEL[p.status]} />,
            },
          ]}
          emptyTitle="No payouts"
          emptyDescription="Prepare a payout for a doctor and a period."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={payouts.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
      {creating && (
        <CreatePayoutDialog
          doctor={doctor}
          onClose={() => setCreating(false)}
          onCreated={(id) => {
            setCreating(false);
            setOpened(id);
          }}
        />
      )}
      {opened && <PayoutDialog payoutId={opened} onClose={() => setOpened(null)} />}
    </Page>
  );
}
