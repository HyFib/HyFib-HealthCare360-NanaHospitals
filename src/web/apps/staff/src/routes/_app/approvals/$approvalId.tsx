import { Button, MessageBar, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { CheckmarkRegular, DismissRegular, OpenRegular, PeopleCheckmarkRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { ErrorState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { ApprovalDecisionDialog, ApprovalStatusBadge, type Decision } from '@/modules/approvals/ApprovalDecision';
import { usePermission } from '@/lib/permissions';
import { approvalQueries, type ApprovalRequestDto } from '@/modules/approvals/api';
import { Detail, DetailGrid } from '@/modules/billing/ui';
import { MASTERS_MANAGE } from '@/modules/masters/api';
import { PHARMACY_PERMISSIONS } from '@/modules/pharmacy/permissions';

export const Route = createFileRoute('/_app/approvals/$approvalId')({
  component: ApprovalPage,
});

/**
 * Where the record a request is about opens, so the approver can look at it before deciding: only where the approver may
 * open it (`canManageMasters` for a service's form).
 */
function subjectLink(
  request: ApprovalRequestDto,
  canManageMasters: boolean,
  can: { pharmacySales: boolean; wardIssues: boolean; devices: boolean },
): { label: string; to: string; search?: Record<string, string> } | null {
  switch (request.subjectType) {
    case 'Bill':
      return { label: 'Open the bill', to: `/billing/bills/${request.subjectId}` };
    case 'Patient':
      return { label: 'Open the duplicate record', to: `/patients/${request.subjectId}` };
    case 'Refund':
      return { label: 'Open the refund', to: `/billing/refunds/${request.subjectId}` };
    case 'IpAccount':
      return { label: 'Open the in-patient account', to: `/billing/ip/${request.subjectId}` };
    case 'PatientAdvance':
      return request.patientId ? { label: "Open the patient's record", to: `/patients/${request.patientId}` } : null;
    // Access given, and changes to an account (a temporary password to hand over, a two-step verification reset,
    // contact details, a practitioner link): the request names the user and what changes; the list shows the account.
    case 'User':
      return { label: 'Open the users', to: '/admin/users' };
    // A linked doctor's registration number, HPR ID or signature: the record with the identity that waits.
    case 'Practitioner':
      return canManageMasters
        ? { label: 'Open the doctor', to: '/setup/practitioners', search: { edit: request.subjectId } }
        : null;
    // Another administration desktop: the devices enrolled now.
    case 'DeviceEnrollment':
      return can.devices ? { label: 'Open the devices', to: '/admin/devices' } : null;
    case 'Role':
      return { label: 'Open the roles', to: '/admin/roles' };
    // A price asked for: the service's form shows the price charged now and the one that waits. An accountant, who
    // approves prices but does not edit services, decides from what the request says changes.
    case 'ServiceItem':
      return canManageMasters
        ? { label: 'Open the service', to: '/setup/services', search: { edit: request.subjectId } }
        : null;
    // A death certificate to confirm: the certificate itself, opened (and audited) as the patient's record.
    case 'DeathRecord':
      return { label: 'Open the death certificate', to: `/mrd/deaths/${request.subjectId}` };
    // A goods receipt to post: its lines, batches and totals, to check against the supplier's invoice.
    case 'GoodsReceiptNote':
      return { label: 'Open the goods receipt', to: `/purchase/grns/${request.subjectId}` };
    // A setting changed: the request names it, what it controls, the value in force and the one asked; the settings
    // list shows the change waiting. Only those who may change settings decide it, and they may open the list.
    case 'SystemSetting':
      return { label: 'Open the settings', to: '/admin/settings' };
    // A pharmacy cancellation or return: the invoice with its items, payments and returns, for whoever may read it. An
    // accountant, who approves refunds but does not open pharmacy invoices, decides from what the request says.
    case 'PharmacySale':
      return can.pharmacySales
        ? { label: 'Open the invoice', to: '/pharmacy/sales', search: { id: request.subjectId } }
        : null;
    // A ward return not restocked: the issue to the ward with its returns.
    case 'IpIssue':
      return can.wardIssues
        ? { label: 'Open the issue', to: '/pharmacy/ip-issues', search: { id: request.subjectId } }
        : null;
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
  const canManageMasters = usePermission(MASTERS_MANAGE);
  const canReadPharmacySales = usePermission([PHARMACY_PERMISSIONS.sales, PHARMACY_PERMISSIONS.returns]);
  const canReadWardIssues = usePermission([PHARMACY_PERMISSIONS.ipIssue, PHARMACY_PERMISSIONS.ipIndent]);
  const canManageDevices = usePermission('admin.devices.manage');
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
  const subject = subjectLink(r, canManageMasters, {
    pharmacySales: canReadPharmacySales,
    wardIssues: canReadWardIssues,
    devices: canManageDevices,
  });
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
              <Button icon={<OpenRegular />} onClick={() => void navigate({ to: subject.to, search: subject.search })}>
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
