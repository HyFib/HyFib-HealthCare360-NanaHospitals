import { Badge, Button, MessageBar, MessageBarBody, MessageBarTitle, Tab, TabList } from '@fluentui/react-components';
import { BedRegular, LinkRegular, PrintRegular, WalletRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useLetterhead } from '@/lib/letterhead';
import { usePermission } from '@/lib/permissions';
import { BillingPatientBanner } from '@/modules/billing/BillingPatientBanner';
import { CollectDepositDialog } from '@/modules/billing/CollectDepositDialog';
import { useBillingLive } from '@/modules/billing/hooks';
import { ipQueries } from '@/modules/billing/ipApi';
import { IpBillsPanel, IpDischargePanel, IpPackagePanel, IpPayerPanel } from '@/modules/billing/IpPanels';
import { IpRunningBill } from '@/modules/billing/IpRunningBill';
import { PaymentLinkDialog } from '@/modules/billing/PaymentLinkDialog';
import { buildRunningBillHtml, sendToPrinter } from '@/modules/billing/print';
import { ShiftBar } from '@/modules/billing/ShiftControls';
import { IpStatusBadge, NoAccess } from '@/modules/billing/ui';

type TabId = 'bill' | 'payments' | 'package' | 'payer' | 'discharge';
const TABS: TabId[] = ['bill', 'payments', 'package', 'payer', 'discharge'];

export const Route = createFileRoute('/_app/billing/ip/$accountId')({
  validateSearch: (s: Record<string, unknown>): { tab?: TabId } => ({
    tab: TABS.includes(s.tab as TabId) && s.tab !== 'bill' ? (s.tab as TabId) : undefined,
  }),
  component: IpAccountPage,
});

/** One admission's billing: running bill, advances and bills, package, payer and pre-auths, discharge billing and clearance. */
function IpAccountPage() {
  const { accountId } = Route.useParams();
  const { tab = 'bill' } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const canRead = usePermission(['billing.ip.manage', 'billing.charges.read']);
  const canDeposit = usePermission('billing.deposits');
  const canLink = usePermission(['billing.payments.collect', 'billing.deposits']);
  const [dialog, setDialog] = useState<'deposit' | 'link' | null>(null);
  useBillingLive();
  const account = useQuery({ ...ipQueries.account(accountId), enabled: canRead });

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="In-patient account" />
        <NoAccess what="in-patient accounts" />
      </Page>
    );
  }
  if (account.isLoading) {
    return <LoadingState label="Opening the account…" />;
  }
  if (account.error || !account.data) {
    return (
      <Page maxWidth={900}>
        <PageHeader
          title="In-patient account"
          breadcrumbs={[
            { label: 'In-patient accounts', onClick: () => void navigate({ to: '/billing/ip' }) },
            { label: 'Account' },
          ]}
        />
        {isApiError(account.error) && account.error.status === 404 ? (
          <EmptyState title="This IP account was not found" />
        ) : (
          <ErrorState error={account.error} onRetry={() => void account.refetch()} />
        )}
      </Page>
    );
  }

  const a = account.data;
  const active = a.status === 'Open' || a.status === 'DischargeInitiated';
  const free = Boolean(a.freeTreatmentReason);

  const printRunningBill = async () => {
    try {
      const bill = await queryClient.fetchQuery(ipQueries.runningBill(a.id));
      await sendToPrinter(
        buildRunningBillHtml(bill, letterhead, { name: a.patientName ?? '', uhid: a.uhid }, new Date().toISOString()),
        'A4',
      );
    } catch (error) {
      notify.error(error, 'Could not print the running bill');
    }
  };

  return (
    <Page>
      <PageHeader
        title={`${a.patientName ?? 'Patient'} · ${a.admissionNumber}`}
        icon={<BedRegular />}
        subtitle={
          <>
            <IpStatusBadge status={a.status} /> Admitted {formatDateTime(a.admittedAt)}
            {a.dischargedAt ? ` · discharged ${formatDateTime(a.dischargedAt)}` : ''}{' '}
            {a.packageName && <Badge appearance="outline">{a.packageName}</Badge>}
          </>
        }
        breadcrumbs={[
          { label: 'In-patient accounts', onClick: () => void navigate({ to: '/billing/ip' }) },
          { label: a.admissionNumber },
        ]}
        actions={
          <>
            <Button icon={<PrintRegular />} onClick={() => void printRunningBill()}>
              Print running bill
            </Button>
            {canLink && active && !free && (
              <Button icon={<LinkRegular />} onClick={() => setDialog('link')}>
                Advance by link
              </Button>
            )}
            {canDeposit && active && !free && (
              <Button appearance="primary" icon={<WalletRegular />} onClick={() => setDialog('deposit')}>
                Collect advance
              </Button>
            )}
          </>
        }
      />
      {canDeposit && active && !free && <ShiftBar />}
      <BillingPatientBanner patientId={a.patientId} />
      {a.freeTreatmentReason && (
        <MessageBar intent="info" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Free treatment by law</MessageBarTitle>
            This stay is free of cost ({a.freeTreatmentReason}). No advance is taken, and its bills come to nothing.
          </MessageBarBody>
        </MessageBar>
      )}
      {a.status === 'Cancelled' && (
        <MessageBar intent="warning">
          <MessageBarBody>
            <MessageBarTitle>Admission cancelled</MessageBarTitle>
            No new advance or package can be added. Services already charged can still be billed, and any advance held
            is refunded through the refund flow.
          </MessageBarBody>
        </MessageBar>
      )}
      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={tab}
            onTabSelect={(_, d) =>
              void navigate({ search: { tab: d.value === 'bill' ? undefined : (d.value as TabId) }, replace: true })
            }
          >
            <Tab value="bill">Running bill</Tab>
            <Tab value="payments">Bills and advances</Tab>
            <Tab value="package">Package</Tab>
            <Tab value="payer">Payer and insurance</Tab>
            <Tab value="discharge">Discharge billing</Tab>
          </TabList>
        </div>
        <div style={{ padding: 16 }}>
          {tab === 'bill' && <IpRunningBill account={a} />}
          {tab === 'payments' && <IpBillsPanel account={a} />}
          {tab === 'package' && <IpPackagePanel account={a} />}
          {tab === 'payer' && <IpPayerPanel account={a} />}
          {tab === 'discharge' && <IpDischargePanel account={a} />}
        </div>
      </Section>
      {dialog === 'deposit' && (
        <CollectDepositDialog
          account={{
            id: a.id,
            admissionNumber: a.admissionNumber,
            patientName: a.patientName,
            uhid: a.uhid,
            depositBalance: a.depositBalance,
            estimatedAmount: a.estimatedAmount,
          }}
          open
          onClose={() => setDialog(null)}
        />
      )}
      {dialog === 'link' && (
        <PaymentLinkDialog open target={{ kind: 'ip', ipAccountId: a.id }} onClose={() => setDialog(null)} />
      )}
    </Page>
  );
}
