import { Button, MessageBar, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { CheckmarkRegular, DismissRegular, TagPercentRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr, type PagedResult } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { api } from '@/lib/api';
import { usePermission } from '@/lib/permissions';
import { billingQueries, type DiscountApprovalDto } from '@/modules/billing/api';
import { BillingPatientBanner } from '@/modules/billing/BillingPatientBanner';
import { BillLinesTable } from '@/modules/billing/BillLinesTable';
import { BillTotalsCard } from '@/modules/billing/BillTotalsCard';
import { DecideDiscountDialog } from '@/modules/billing/DiscountDecision';
import { DISCOUNT_APPROVAL_STATUSES } from '@/modules/billing/labels';
import { formatPercent } from '@/modules/billing/money';
import { Amount, Detail, DetailGrid, DiscountStatusBadge, NoAccess } from '@/modules/billing/ui';

export const Route = createFileRoute('/_app/billing/discount-approvals/$approvalId')({
  component: DiscountApprovalPage,
});

/**
 * One discount request, opened from the approval alert, next to the bill it applies to so the approver sees exactly
 * what is being discounted. (There is no single-request endpoint: the request is found in the status lists.)
 */
async function findApproval(id: string, signal: AbortSignal): Promise<DiscountApprovalDto | null> {
  for (const status of DISCOUNT_APPROVAL_STATUSES) {
    const page = await api.get<PagedResult<DiscountApprovalDto>>('/api/v1/billing/discount-approvals', { query: { status, pageSize: 200 }, signal });
    const found = page.items.find((a) => a.id === id);
    if (found) {
      return found;
    }
  }
  return null;
}

function DiscountApprovalPage() {
  const { approvalId } = Route.useParams();
  const navigate = useNavigate();
  const canApprove = usePermission('billing.discounts.approve');
  const [decision, setDecision] = useState<'approve' | 'reject' | null>(null);
  const approval = useQuery({
    queryKey: ['billing', 'discounts', 'approval', approvalId],
    queryFn: ({ signal }) => findApproval(approvalId, signal),
    enabled: canApprove,
  });
  const bill = useQuery({ ...billingQueries.bill(approval.data?.billId ?? ''), enabled: Boolean(approval.data?.billId) });

  if (!canApprove) {
    return (
      <Page>
        <PageHeader title="Discount request" />
        <NoAccess what="discount approvals" />
      </Page>
    );
  }
  if (approval.isLoading) {
    return <LoadingState label="Finding the request…" />;
  }
  const back = () => void navigate({ to: '/billing/discount-approvals' });
  if (approval.error) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={approval.error} onRetry={() => void approval.refetch()} />
      </Page>
    );
  }
  const a = approval.data;
  if (!a) {
    return (
      <Page maxWidth={900}>
        <PageHeader title="Discount request" breadcrumbs={[{ label: 'Discount approvals', onClick: back }, { label: 'Request' }]} />
        <EmptyState title="This request is no longer listed" description="It may be older than the recent requests. Open the list to see the latest." action={<Button onClick={back}>Discount approvals</Button>} />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title={`Discount of ${formatInr(a.requestedAmount)}`}
        icon={<TagPercentRegular />}
        subtitle={<DiscountStatusBadge status={a.status} />}
        breadcrumbs={[{ label: 'Discount approvals', onClick: back }, { label: a.patientName ?? 'Request' }]}
        actions={
          a.status === 'Pending' && (
            <>
              <Button icon={<DismissRegular />} onClick={() => setDecision('reject')}>
                Reject
              </Button>
              <Button appearance="primary" icon={<CheckmarkRegular />} onClick={() => setDecision('approve')}>
                Approve
              </Button>
            </>
          )
        }
      />
      <BillingPatientBanner patientId={a.patientId} showDues={false} />
      <Section title="Request">
        <DetailGrid>
          <Detail label="Discount asked">
            <Amount value={a.requestedAmount} size="large" />
          </Detail>
          <Detail label="Share of the discountable value">{a.discountBase > 0 ? `${formatPercent((a.requestedAmount / a.discountBase) * 100)} of ${formatInr(a.discountBase)}` : '—'}</Detail>
          <Detail label="How">
            {a.billDiscountPercent !== null ? `${formatPercent(a.billDiscountPercent)} on the bill` : a.billDiscountAmount !== null ? `${formatInr(a.billDiscountAmount)} on the bill` : 'On individual services'}
          </Detail>
          <Detail label="Reason">{a.reason}</Detail>
          <Detail label="Requested">{`${a.requestedByName ?? '—'} · ${formatDateTime(a.requestedAt)}`}</Detail>
          {a.decidedAt && <Detail label="Decided">{`${formatDateTime(a.decidedAt)}${a.remarks ? ` · ${a.remarks}` : ''}`}</Detail>}
        </DetailGrid>
      </Section>
      {bill.data && (
        <>
          {bill.data.status !== 'Draft' && (
            <MessageBar intent="info">
              <MessageBarBody>
                <MessageBarTitle>The bill is no longer a draft</MessageBarTitle>
                Requests on finalised or discarded bills are withdrawn automatically.
              </MessageBarBody>
            </MessageBar>
          )}
          <Section title="The bill as it stands (before this discount)" flush>
            <BillLinesTable bill={bill.data} />
          </Section>
          <Section>
            <BillTotalsCard bill={bill.data} />
          </Section>
        </>
      )}
      {decision && <DecideDiscountDialog approval={a} decision={decision} onClose={() => setDecision(null)} onDecided={() => void approval.refetch()} />}
    </Page>
  );
}
