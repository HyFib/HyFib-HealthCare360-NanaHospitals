import { Button } from '@fluentui/react-components';
import { ArrowUndoRegular, CheckmarkRegular, DismissRegular, MoneyHandRegular, PrintRegular, ReceiptRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { billingQueries } from '@/modules/billing/api';
import { BillingPatientBanner } from '@/modules/billing/BillingPatientBanner';
import { useBillingLive, useBillingPrint } from '@/modules/billing/hooks';
import { PAYMENT_MODE_LABEL, REFUND_PURPOSE_LABEL } from '@/modules/billing/labels';
import { DecideRefundDialog, PayRefundDialog } from '@/modules/billing/RefundDialogs';
import { Amount, Detail, DetailGrid, NoAccess, RefundStatusBadge } from '@/modules/billing/ui';

export const Route = createFileRoute('/_app/billing/refunds/$refundId')({
  component: RefundPage,
});

/** One refund (opened from the approval alert): who asked, why, the decision and the payout. */
function RefundPage() {
  const { refundId } = Route.useParams();
  const navigate = useNavigate();
  const canRead = usePermission(['billing.refunds.request', 'billing.refunds.approve', 'billing.charges.read']);
  const canApprove = usePermission('billing.refunds.approve');
  const canPay = usePermission('billing.payments.collect');
  const { printRefund } = useBillingPrint();
  const [action, setAction] = useState<'approve' | 'reject' | 'pay' | null>(null);
  useBillingLive();
  const refund = useQuery({ ...billingQueries.refund(refundId), enabled: canRead });

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="Refund" />
        <NoAccess what="refunds" />
      </Page>
    );
  }
  if (refund.isLoading) {
    return <LoadingState />;
  }
  if (refund.error || !refund.data) {
    return (
      <Page maxWidth={900}>
        <PageHeader title="Refund" breadcrumbs={[{ label: 'Refunds', onClick: () => void navigate({ to: '/billing/refunds' }) }, { label: 'Refund' }]} />
        {isApiError(refund.error) && refund.error.status === 404 ? (
          <EmptyState title="This refund was not found" />
        ) : (
          <ErrorState error={refund.error} onRetry={() => void refund.refetch()} />
        )}
      </Page>
    );
  }

  const r = refund.data;
  return (
    <Page maxWidth={1100}>
      <PageHeader
        title={`Refund ${r.refundNumber}`}
        icon={<ArrowUndoRegular />}
        subtitle={<RefundStatusBadge status={r.status} />}
        breadcrumbs={[{ label: 'Refunds', onClick: () => void navigate({ to: '/billing/refunds' }) }, { label: r.refundNumber }]}
        actions={
          <>
            {r.status === 'Requested' && canApprove && (
              <>
                <Button icon={<DismissRegular />} onClick={() => setAction('reject')}>
                  Reject
                </Button>
                <Button appearance="primary" icon={<CheckmarkRegular />} onClick={() => setAction('approve')}>
                  Approve
                </Button>
              </>
            )}
            {r.status === 'Approved' && canPay && (
              <Button appearance="primary" icon={<MoneyHandRegular />} onClick={() => setAction('pay')}>
                Pay out
              </Button>
            )}
            {r.status === 'Paid' && (
              <Button icon={<PrintRegular />} onClick={() => void printRefund(r)}>
                Print refund voucher
              </Button>
            )}
            {r.billId && (
              <Button icon={<ReceiptRegular />} onClick={() => void navigate({ to: '/billing/bills/$billId', params: { billId: r.billId! } })}>
                Open the bill
              </Button>
            )}
          </>
        }
      />
      <BillingPatientBanner patientId={r.patientId} showDues={false} />
      <Section title="Refund">
        <DetailGrid>
          <Detail label="Amount">
            <Amount value={r.amount} size="large" />
          </Detail>
          <Detail label="For">{REFUND_PURPOSE_LABEL[r.purpose]}</Detail>
          <Detail label="Reason">{r.reason}</Detail>
          <Detail label="Requested">{`${formatDateTime(r.requestedAt)} by ${r.requestedByName ?? 'the system'}`}</Detail>
          <Detail label="Decision">{r.decidedAt ? `${formatDateTime(r.decidedAt)}${r.decisionRemarks ? ` · ${r.decisionRemarks}` : ''}` : 'Not decided yet'}</Detail>
          <Detail label="Paid">{r.paidAt ? `${formatDateTime(r.paidAt)} · ${r.mode ? PAYMENT_MODE_LABEL[r.mode] : ''}${r.reference ? ` · ref ${r.reference}` : ''}` : 'Not paid yet'}</Detail>
        </DetailGrid>
      </Section>
      {action === 'pay' && <PayRefundDialog refund={r} onClose={() => setAction(null)} />}
      {(action === 'approve' || action === 'reject') && <DecideRefundDialog refund={r} decision={action} onClose={() => setAction(null)} />}
    </Page>
  );
}
