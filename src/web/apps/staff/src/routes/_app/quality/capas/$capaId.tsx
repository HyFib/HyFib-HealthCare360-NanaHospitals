import { Caption1, MessageBar, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { ClipboardTaskRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime } from '@hc360/core';
import { ErrorState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { qualityQueries } from '@/modules/quality/api';
import { CapaStatusBadge } from '@/modules/quality/badges';
import { CapaActionButtons } from '@/modules/quality/CapaActions';
import { CapaDialog } from '@/modules/quality/CapaDialog';
import { capaSourceLabel, QUALITY_PERMISSIONS as Q } from '@/modules/quality/labels';

export const Route = createFileRoute('/_app/quality/capas/$capaId')({
  component: () => (
    <RequirePermission permission={[Q.capaManage, Q.incidentsReport]} what="this action">
      <CapaPage />
    </RequirePermission>
  ),
});

function CapaPage() {
  const { capaId } = Route.useParams();
  const navigate = useNavigate();
  const isManager = usePermission(Q.capaManage);
  const [editing, setEditing] = useState(false);
  const capa = useQuery(qualityQueries.capa(capaId));

  if (capa.isLoading) {
    return <LoadingState />;
  }
  if (capa.error || !capa.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={capa.error} onRetry={() => void capa.refetch()} />
      </Page>
    );
  }

  const c = capa.data;
  return (
    <Page maxWidth={1100}>
      <PageHeader
        title={`${c.type} action · ${c.capaNumber}`}
        subtitle={c.action}
        icon={<ClipboardTaskRegular />}
        breadcrumbs={[
          isManager
            ? { label: 'Corrective actions', onClick: () => void navigate({ to: '/quality/capas' }) }
            : { label: 'My actions', onClick: () => void navigate({ to: '/quality/capas/mine' }) },
          { label: c.capaNumber },
        ]}
        actions={<CapaActionButtons capa={c} onEdit={() => setEditing(true)} />}
      />
      {c.isOverdue && (
        <MessageBar intent="error">
          <MessageBarBody>
            <MessageBarTitle>Overdue</MessageBarTitle>
            This action was due on {formatDateOnly(c.dueDate)}.
          </MessageBarBody>
        </MessageBar>
      )}
      {c.reworkCount > 0 && c.status === 'InProgress' && (
        <MessageBar intent="warning">
          <MessageBarBody>
            <MessageBarTitle>Sent back for more work</MessageBarTitle>
            The effectiveness check found it had not worked{c.effectivenessNote ? `: ${c.effectivenessNote}` : '.'}
          </MessageBarBody>
        </MessageBar>
      )}
      <Section>
        <DetailGrid>
          <Detail label="Status">
            <CapaStatusBadge status={c.status} isOverdue={c.isOverdue} />
          </Detail>
          <Detail label="Owner">{c.ownerName}</Detail>
          <Detail label="Due by">{formatDateOnly(c.dueDate)}</Detail>
          <Detail label="Arising from">
            {c.incidentId && isManager ? (
              <Link to="/quality/incidents/$incidentId" params={{ incidentId: c.incidentId }}>
                Open the incident
              </Link>
            ) : (
              capaSourceLabel[c.source]
            )}
          </Detail>
          <Detail label="Started">{c.startedAt ? formatDateTime(c.startedAt) : null}</Detail>
          <Detail label="Done">{c.completedAt ? formatDateTime(c.completedAt) : null}</Detail>
          <Detail label="What was done" wide>
            {c.completionNote}
          </Detail>
          <Detail label="Effectiveness check due">{c.effectivenessCheckDue ? formatDateOnly(c.effectivenessCheckDue) : null}</Detail>
          <Detail label="Effective?">{c.isEffective === null ? null : c.isEffective ? 'Yes' : 'No'}</Detail>
          <Detail label="Checked">{c.verifiedAt ? formatDateTime(c.verifiedAt) : null}</Detail>
          <Detail label="Evidence" wide>
            {c.effectivenessNote}
          </Detail>
          {c.cancelReason && (
            <Detail label="Cancelled because" wide>
              {c.cancelReason}
            </Detail>
          )}
          {c.reworkCount > 0 && <Detail label="Reworked">{c.reworkCount} time(s)</Detail>}
        </DetailGrid>
        {c.status === 'Completed' && !isManager && <Caption1>The quality team will check that the action worked.</Caption1>}
      </Section>
      {isManager && <CapaDialog open={editing} onClose={() => setEditing(false)} capa={c} />}
    </Page>
  );
}
