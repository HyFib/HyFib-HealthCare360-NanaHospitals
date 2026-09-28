import { Badge } from '@fluentui/react-components';
import { ReceiptRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Page, PageHeader } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { billingQueries } from '@/modules/billing/api';
import { BillingPatientBanner } from '@/modules/billing/BillingPatientBanner';
import { BillWorkspace } from '@/modules/billing/BillWorkspace';
import { useBillingLive } from '@/modules/billing/hooks';
import { billTitle } from '@/modules/billing/print';
import { ShiftBar } from '@/modules/billing/ShiftControls';
import { BillStatusBadge, NoAccess } from '@/modules/billing/ui';

export const Route = createFileRoute('/_app/billing/bills/$billId')({
  component: BillDetailPage,
});

/** One bill: lines (returns shown as returns), totals with GST, payments, credit notes, refunds; collect, reprint, cancel. */
function BillDetailPage() {
  const { billId } = Route.useParams();
  const navigate = useNavigate();
  const canRead = usePermission(['billing.charges.read', 'billing.op.create', 'billing.ip.manage', 'billing.payments.collect']);
  useBillingLive();
  const bill = useQuery({
    ...billingQueries.bill(billId),
    enabled: canRead,
    refetchInterval: (query) => (query.state.data?.pendingDiscount ? 15_000 : false),
  });

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="Bill" />
        <NoAccess what="bills" />
      </Page>
    );
  }
  if (bill.isLoading) {
    return <LoadingState label="Opening the bill…" />;
  }
  if (bill.error || !bill.data) {
    return (
      <Page maxWidth={900}>
        <PageHeader title="Bill" breadcrumbs={[{ label: 'Bills', onClick: () => void navigate({ to: '/billing/bills' }) }, { label: 'Bill' }]} />
        {isApiError(bill.error) && bill.error.status === 404 ? (
          <EmptyState title="This bill was not found" description="A discarded draft disappears; search the bill list again." />
        ) : (
          <ErrorState error={bill.error} onRetry={() => void bill.refetch()} />
        )}
      </Page>
    );
  }

  const b = bill.data;
  const title = b.billNumber ? `Bill ${b.billNumber}` : 'Draft bill';
  return (
    <Page>
      <PageHeader
        title={title}
        icon={<ReceiptRegular />}
        subtitle={
          <>
            {billTitle(b)} <BillStatusBadge status={b.status} /> {b.payerName && <Badge appearance="outline">{b.payerName}</Badge>}
          </>
        }
        breadcrumbs={[{ label: 'Bills', onClick: () => void navigate({ to: '/billing/bills' }) }, { label: title }]}
      />
      <ShiftBar />
      <BillingPatientBanner patientId={b.patientId} />
      <BillWorkspace
        bill={b}
        onDiscarded={() => void navigate({ to: '/billing/op', search: { patientId: b.patientId } })}
        allowServiceEntry={b.type === 'OutPatient'}
      />
    </Page>
  );
}
