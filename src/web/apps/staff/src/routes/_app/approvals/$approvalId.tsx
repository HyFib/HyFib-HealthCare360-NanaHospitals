import { Button, MessageBar, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { CheckmarkRegular, DismissRegular, OpenRegular, PeopleCheckmarkRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { ErrorState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { ApprovalDecisionDialog, ApprovalStatusBadge, type Decision } from '@/modules/approvals/ApprovalDecision';
import { approvalQueries, type ApprovalRequestDto } from '@/modules/approvals/api';
import { Detail, DetailGrid } from '@/modules/billing/ui';

export const Route = createFileRoute('/_app/approvals/$approvalId')({
  component: ApprovalPage,
});

/** Where the record a request is about opens, so the approver can look at it before deciding. */
function subjectLink(request: ApprovalRequestDto): { label: string; to: string } | null {
  switch (request.subjectType) {
    case 'Bill':
      return { label: 'Open the bill', to: `/billing/bills/${request.subjectId}` };
    case 'Patient':
      return { label: 'Open the duplicate record', to: `/patients/${request.subjectId}` };
    default:
      return null;
  }
}

/** One request, opened from the inbox or from its notification: what is asked, by whom, why, and its decision. */
function ApprovalPage() {
  const { approvalId } = Route.useParams();
  const navigate = useNavigate();
  const request = useQuery(approvalQueries.detail(approvalId));
  const [decision, setDecision] = useState<Decision | null>(null);
  const back = () => void navigate({ to: '/approvals' });

  if (request.isLoading) {
    return <LoadingState label="Opening the request…" />;
  }
  if (request.error || !request.data) {
    return (
      <Page maxWidth={900}>
        <PageHeader title="Request" breadcrumbs={[{ label: 'Approvals', onClick: back }, { label: 'Request' }]} />
        <ErrorState error={request.error} onRetry={() => void request.refetch()} />
      </Page>
    );
  }

  const r = request.data;
  const subject = subjectLink(r);
  return (
    <Page maxWidth={900}>
      <PageHeader
        title={r.summary}
        icon={<PeopleCheckmarkRegular />}
        subtitle={<ApprovalStatusBadge request={r} />}
        breadcrumbs={[{ label: 'Approvals', onClick: back }, { label: r.title }]}
        actions={
          <>
            {subject && (
              <Button icon={<OpenRegular />} onClick={() => void navigate({ to: subject.to })}>
                {subject.label}
              </Button>
            )}
            {r.canDecide && (
              <>
                <Button icon={<DismissRegular />} onClick={() => setDecision('reject')}>
                  Reject
                </Button>
                <Button appearance="primary" icon={<CheckmarkRegular />} onClick={() => setDecision('approve')}>
                  Approve
                </Button>
              </>
            )}
            {r.canWithdraw && <Button onClick={() => setDecision('withdraw')}>Withdraw</Button>}
          </>
        }
      />
      {r.status === 'Pending' && !r.canDecide && !r.canWithdraw && !r.isExpired && (
        <MessageBar intent="info">
          <MessageBarBody>Someone else who may approve {r.title.toLowerCase()}s decides this request.</MessageBarBody>
        </MessageBar>
      )}
      {r.status === 'Failed' && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Approved, but not made</MessageBarTitle>
            {r.outcome} Nothing was changed.
          </MessageBarBody>
        </MessageBar>
      )}
      <Section title={r.title}>
        <DetailGrid>
          {r.details.map((d) => (
            <Detail key={d.label} label={d.label}>
              {d.value}
            </Detail>
          ))}
          <Detail label="Reason">{r.reason}</Detail>
          <Detail label="Asked for by">{`${r.requestedByName} · ${formatDateTime(r.requestedAt)}`}</Detail>
          {r.status === 'Pending' && (
            <Detail label={r.isExpired ? 'Expired' : 'Waits until'}>{formatDateTime(r.expiresAt)}</Detail>
          )}
          {r.decidedAt && (
            <Detail label={r.status === 'Withdrawn' ? 'Withdrawn' : 'Decided by'}>
              {`${r.decidedByName ?? '—'} · ${formatDateTime(r.decidedAt)}`}
            </Detail>
          )}
          {r.decisionNote && (
            <Detail label={r.status === 'Rejected' ? 'Why rejected' : 'Note'}>{r.decisionNote}</Detail>
          )}
          {r.status === 'Approved' && r.outcome && <Detail label="Made">{r.outcome}</Detail>}
        </DetailGrid>
      </Section>
      {decision && <ApprovalDecisionDialog request={r} decision={decision} onClose={() => setDecision(null)} />}
    </Page>
  );
}
