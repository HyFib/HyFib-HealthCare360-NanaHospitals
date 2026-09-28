import { Button, makeStyles } from '@fluentui/react-components';
import { AddRegular, DocumentCheckmarkRegular, ShieldCheckmarkRegular } from '@fluentui/react-icons';
import { formatRelative } from '@hc360/core';
import { DataTable, KpiCard, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { Amount, NoAccess } from '@/modules/billing/ui';
import { insuranceQueries } from '@/modules/insurance/api';
import { CreateClaimDialog } from '@/modules/insurance/ClaimDialogs';
import { PREAUTH_STATUS_LABEL, preAuthTone, type PreAuthStatus } from '@/modules/insurance/labels';
import { PreAuthDialog } from '@/modules/insurance/PreAuthDialog';
import { ClaimsPendingWidget } from '@/modules/insurance/widgets';

export const Route = createFileRoute('/_app/insurance/')({
  component: InsuranceDeskPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(190px, 1fr))' },
  grid: { display: 'grid', gap: '16px', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', alignItems: 'start', '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
});

function PreAuthKpi({ status, label, hint }: { status: PreAuthStatus; label: string; hint: string }) {
  const navigate = useNavigate();
  const count = useQuery(insuranceQueries.preAuthCount(status));
  return (
    <KpiCard
      label={label}
      value={count.data === undefined ? '…' : count.data.toLocaleString('en-IN')}
      hint={hint}
      onClick={() => void navigate({ to: '/insurance/preauths', search: { status } })}
    />
  );
}

/** The insurance desk: pre-authorisations waiting on someone, claims by stage, and what insurers owe. */
function InsuranceDeskPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const canPreAuth = usePermission(['insurance.preauth.manage', 'insurance.claims.manage', 'billing.ip.manage']);
  const canManagePreAuth = usePermission('insurance.preauth.manage');
  const canClaims = usePermission('insurance.claims.manage');
  const canPolicies = usePermission('insurance.policies.manage');
  const canPayers = usePermission('insurance.payers.manage');
  const [dialog, setDialog] = useState<'preauth' | 'claim' | null>(null);
  const queries = useQuery({ ...insuranceQueries.preAuths({ status: 'Query' }), enabled: canPreAuth });

  if (!canPreAuth && !canClaims && !canPolicies && !canPayers) {
    return (
      <Page>
        <PageHeader title="Insurance desk" />
        <NoAccess what="the insurance desk" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Insurance desk"
        subtitle="Cashless approvals, claims and what insurers owe"
        icon={<ShieldCheckmarkRegular />}
        actions={
          <>
            {canPolicies && <Button onClick={() => void navigate({ to: '/insurance/policies' })}>Patient policies</Button>}
            {canClaims && (
              <Button icon={<DocumentCheckmarkRegular />} onClick={() => setDialog('claim')}>
                New claim
              </Button>
            )}
            {canManagePreAuth && (
              <Button appearance="primary" icon={<AddRegular />} onClick={() => setDialog('preauth')}>
                New pre-authorisation
              </Button>
            )}
          </>
        }
      />
      {canPreAuth && (
        <div className={styles.kpis}>
          <PreAuthKpi status="Query" label="Insurer asked a question" hint="Answer to keep the approval moving" />
          <PreAuthKpi status="Submitted" label="Waiting for insurer" hint="Submitted pre-authorisations" />
          <PreAuthKpi status="Draft" label="Drafts" hint="Not yet submitted" />
          <PreAuthKpi status="PartiallyApproved" label="Partly approved" hint="Consider an enhancement" />
        </div>
      )}
      <div className={styles.grid}>
        {canClaims && <ClaimsPendingWidget />}
        {canPreAuth && (
          <Section title="Questions from insurers" description="Pre-authorisations waiting for your answer" flush>
            <DataTable
              ariaLabel="Pre-authorisations with a query"
              rows={queries.data?.items}
              loading={queries.isLoading}
              error={queries.error}
              onRetry={() => void queries.refetch()}
              getRowId={(p) => p.id}
              onRowClick={(p) => void navigate({ to: '/insurance/preauths/$preAuthId', params: { preAuthId: p.id } })}
              size="small"
              columns={[
                { key: 'patient', header: 'Patient', render: (p) => p.patientName ?? p.uhid ?? '—' },
                { key: 'payer', header: 'Insurer', priority: 'low', render: (p) => p.payerName ?? '—' },
                { key: 'amount', header: 'Requested', align: 'end', render: (p) => <Amount value={p.requestedAmount} /> },
                { key: 'when', header: 'Submitted', priority: 'low', render: (p) => formatRelative(p.submittedAt ?? p.createdAt) },
                { key: 'status', header: 'Status', render: (p) => <StatusBadge status={p.status} tone={preAuthTone(p.status)} label={PREAUTH_STATUS_LABEL[p.status]} /> },
              ]}
              emptyTitle="No open questions"
              emptyDescription="Nothing is waiting for an answer."
            />
          </Section>
        )}
      </div>
      {dialog === 'preauth' && (
        <PreAuthDialog preAuth={null} onClose={() => setDialog(null)} onSaved={(id) => void navigate({ to: '/insurance/preauths/$preAuthId', params: { preAuthId: id } })} />
      )}
      {dialog === 'claim' && <CreateClaimDialog onClose={() => setDialog(null)} onCreated={(id) => void navigate({ to: '/insurance/claims/$claimId', params: { claimId: id } })} />}
    </Page>
  );
}
