import { Button, Caption1, Field, SearchBox, Text, tokens } from '@fluentui/react-components';
import { BedRegular, PrintRegular, WalletRegular } from '@fluentui/react-icons';
import { formatDate, formatTime, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { billingQueries } from '@/modules/billing/api';
import { CollectDepositDialog, type DepositAccount } from '@/modules/billing/CollectDepositDialog';
import { useBillingLive, useBillingPrint } from '@/modules/billing/hooks';
import { ipQueries } from '@/modules/billing/ipApi';
import { modeSummaryLabel, PAYER_TYPE_LABEL } from '@/modules/billing/labels';
import { ShiftBar } from '@/modules/billing/ShiftControls';
import { Amount, IpStatusBadge, NoAccess } from '@/modules/billing/ui';

export const Route = createFileRoute('/_app/billing/deposits')({
  component: DepositsPage,
});

/** Collect an IP advance (IPB-01): find the admitted patient, collect in any mode, print the advance receipt. */
function DepositsPage() {
  const navigate = useNavigate();
  const canDeposit = usePermission('billing.deposits');
  const canReadReceipts = usePermission(['billing.charges.read', 'billing.payments.collect']);
  const canReadAccounts = usePermission(['billing.ip.manage', 'billing.charges.read']);
  const { printReceiptById } = useBillingPrint();
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [account, setAccount] = useState<DepositAccount | null>(null);
  useBillingLive();

  const today = todayIso();
  const accounts = useQuery({ ...ipQueries.accounts({ search: debounced || undefined, status: debounced ? undefined : 'Open' }), enabled: canDeposit && canReadAccounts });
  const receipts = useQuery({ ...billingQueries.receipts({ from: today, to: today, purpose: 'Deposit', pageSize: 10 }), enabled: canDeposit && canReadReceipts });

  if (!canDeposit) {
    return (
      <Page>
        <PageHeader title="Collect IP advance" />
        <NoAccess what="IP advances" />
      </Page>
    );
  }

  const active = (accounts.data?.items ?? []).filter((a) => a.status === 'Open' || a.status === 'DischargeInitiated');

  return (
    <Page>
      <PageHeader
        title="Collect IP advance"
        subtitle="Advance (deposit) for an admitted patient, in any payment mode"
        icon={<WalletRegular />}
        actions={
          canReadAccounts && (
            <Button icon={<BedRegular />} onClick={() => void navigate({ to: '/billing/ip' })}>
              All IP accounts
            </Button>
          )
        }
      />
      <ShiftBar />
      <Section title="Find the admitted patient" flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 320px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Admission number, UHID, mobile or name" autoFocus />
            </Field>
          </FilterBar>
        </div>
        {!canReadAccounts ? (
          <Caption1 style={{ padding: '0 16px 16px', display: 'block' }}>Your role cannot list admissions. Collect the advance from the patient’s IP account screen.</Caption1>
        ) : (
          <DataTable
            ariaLabel="Admitted patients"
            rows={accounts.data ? active : undefined}
            loading={accounts.isLoading}
            error={accounts.error}
            onRetry={() => void accounts.refetch()}
            getRowId={(a) => a.id}
            onRowClick={(a) => setAccount({ id: a.id, admissionNumber: a.admissionNumber, patientName: a.patientName, uhid: a.uhid, depositBalance: a.depositBalance, estimatedAmount: a.estimatedAmount })}
            columns={[
              {
                key: 'patient',
                header: 'Patient',
                render: (a) => (
                  <span>
                    <Text weight="semibold">{a.patientName ?? '—'}</Text>
                    <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                      {a.uhid} · {a.admissionNumber} · admitted {formatDate(a.admittedAt)}
                    </Caption1>
                  </span>
                ),
              },
              { key: 'payer', header: 'Payer', priority: 'low', render: (a) => a.payerName ?? PAYER_TYPE_LABEL[a.payerType] },
              { key: 'estimate', header: 'Estimate', align: 'end', priority: 'low', render: (a) => <Amount value={a.estimatedAmount} /> },
              { key: 'deposit', header: 'Advance held', align: 'end', render: (a) => <Amount value={a.depositBalance} strong /> },
              { key: 'status', header: 'Status', priority: 'low', render: (a) => <IpStatusBadge status={a.status} /> },
            ]}
            rowActions={(a) => (
              <Button
                size="small"
                appearance="primary"
                icon={<WalletRegular />}
                onClick={() => setAccount({ id: a.id, admissionNumber: a.admissionNumber, patientName: a.patientName, uhid: a.uhid, depositBalance: a.depositBalance, estimatedAmount: a.estimatedAmount })}
              >
                Collect
              </Button>
            )}
            emptyTitle={debounced ? 'No admitted patient matches' : 'Nobody is admitted right now'}
            emptyDescription="Advances can be collected only while the patient is in hospital."
            size="small"
          />
        )}
      </Section>

      {canReadReceipts && (
        <Section title="Advances collected today" flush>
          <DataTable
            ariaLabel="Advances collected today"
            rows={receipts.data?.items}
            loading={receipts.isLoading}
            error={receipts.error}
            onRetry={() => void receipts.refetch()}
            getRowId={(r) => r.id}
            columns={[
              { key: 'time', header: 'Time', width: 90, render: (r) => formatTime(r.receivedAt) },
              { key: 'no', header: 'Receipt', render: (r) => <Text font="monospace">{r.receiptNumber}</Text> },
              { key: 'patient', header: 'Patient', render: (r) => r.patientName ?? '—' },
              { key: 'mode', header: 'Mode', priority: 'low', render: (r) => modeSummaryLabel(r.modes) },
              { key: 'amount', header: 'Amount', align: 'end', render: (r) => <Amount value={r.amount} strong /> },
            ]}
            rowActions={(r) => <Button size="small" appearance="subtle" icon={<PrintRegular />} aria-label={`Reprint ${r.receiptNumber}`} onClick={() => void printReceiptById(r.id)} />}
            emptyTitle="No advance collected yet today"
            size="small"
          />
        </Section>
      )}
      {account && <CollectDepositDialog account={account} open onClose={() => setAccount(null)} />}
    </Page>
  );
}
