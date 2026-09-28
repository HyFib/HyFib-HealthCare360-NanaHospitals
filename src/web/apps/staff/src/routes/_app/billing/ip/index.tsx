import { Badge, Button, Caption1, Field, SearchBox, Select, Text, tokens } from '@fluentui/react-components';
import { BedRegular, WalletRegular } from '@fluentui/react-icons';
import { formatDate, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { CollectDepositDialog, type DepositAccount } from '@/modules/billing/CollectDepositDialog';
import { useBillingLive } from '@/modules/billing/hooks';
import { ipQueries } from '@/modules/billing/ipApi';
import { IP_ACCOUNT_STATUS_LABEL, IP_ACCOUNT_STATUSES, PAYER_TYPE_LABEL, type IpAccountStatus } from '@/modules/billing/labels';
import { enumParam, pageParam, textParam } from '@/modules/billing/searchParams';
import { Amount, IpStatusBadge, NoAccess } from '@/modules/billing/ui';

interface IpSearch {
  search?: string;
  status?: IpAccountStatus;
  page?: number;
}

export const Route = createFileRoute('/_app/billing/ip/')({
  validateSearch: (s: Record<string, unknown>): IpSearch => ({
    search: textParam(s.search),
    status: enumParam(s.status, IP_ACCOUNT_STATUSES),
    page: pageParam(s.page),
  }),
  component: IpAccountsPage,
});

/** In-patient accounts (IPB): who is in, their advance, package and payer; open one for the running bill and discharge billing. */
function IpAccountsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canRead = usePermission(['billing.ip.manage', 'billing.charges.read']);
  const canDeposit = usePermission('billing.deposits');
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [deposit, setDeposit] = useState<DepositAccount | null>(null);
  useBillingLive();

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({ search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.search, navigate]);

  const accounts = useQuery({ ...ipQueries.accounts({ search: search.search, status: search.status, page: search.page ?? 1 }), enabled: canRead });

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="In-patient accounts" />
        <NoAccess what="in-patient accounts" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader title="In-patient accounts" subtitle="Running bills, advances, packages and discharge billing" icon={<BedRegular />} />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 280px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Admission number, UHID, mobile or name" autoFocus />
            </Field>
            <Field label="Status">
              <Select value={search.status ?? ''} onChange={(_, d) => void navigate({ search: (prev) => ({ ...prev, status: (d.value || undefined) as IpAccountStatus | undefined, page: undefined }) })}>
                <option value="">In hospital or with dues</option>
                {IP_ACCOUNT_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {IP_ACCOUNT_STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="In-patient accounts"
          rows={accounts.data?.items}
          loading={accounts.isLoading}
          error={accounts.error}
          onRetry={() => void accounts.refetch()}
          getRowId={(a) => a.id}
          onRowClick={(a) => void navigate({ to: '/billing/ip/$accountId', params: { accountId: a.id } })}
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              render: (a) => (
                <span>
                  <Text weight="semibold">{a.patientName ?? '—'}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {a.uhid} · {a.admissionNumber}
                  </Caption1>
                </span>
              ),
            },
            { key: 'admitted', header: 'Admitted', width: 130, render: (a) => formatDate(a.admittedAt) },
            {
              key: 'payer',
              header: 'Payer',
              priority: 'low',
              render: (a) => (
                <span>
                  {a.payerName ?? PAYER_TYPE_LABEL[a.payerType]}
                  {a.hasPackage && (
                    <Badge size="small" appearance="outline" style={{ marginLeft: 6 }}>
                      Package
                    </Badge>
                  )}
                </span>
              ),
            },
            { key: 'estimate', header: 'Estimate', align: 'end', priority: 'low', render: (a) => <Amount value={a.estimatedAmount} /> },
            { key: 'deposit', header: 'Advance held', align: 'end', render: (a) => <Amount value={a.depositBalance} strong /> },
            { key: 'status', header: 'Status', width: 170, render: (a) => <IpStatusBadge status={a.status} /> },
          ]}
          rowActions={
            canDeposit
              ? (a) =>
                  a.status === 'Open' || a.status === 'DischargeInitiated' ? (
                    <Button
                      size="small"
                      icon={<WalletRegular />}
                      onClick={() => setDeposit({ id: a.id, admissionNumber: a.admissionNumber, patientName: a.patientName, uhid: a.uhid, depositBalance: a.depositBalance, estimatedAmount: a.estimatedAmount })}
                    >
                      Advance
                    </Button>
                  ) : null
              : undefined
          }
          emptyTitle={search.search ? 'No admission matches' : 'No open in-patient accounts'}
          emptyDescription="An account opens automatically when a patient is admitted."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={accounts.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
      {deposit && <CollectDepositDialog account={deposit} open onClose={() => setDeposit(null)} />}
    </Page>
  );
}
