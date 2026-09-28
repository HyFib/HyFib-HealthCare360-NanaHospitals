import { Button, Text } from '@fluentui/react-components';
import { ClipboardTaskRegular, ShieldErrorRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { DataTable, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { qualityQueries } from '@/modules/quality/api';
import { IncidentStatusBadge, SeverityBadge } from '@/modules/quality/badges';
import { incidentTypeLabel, QUALITY_PERMISSIONS as Q } from '@/modules/quality/labels';
import { MyActionsTable } from '@/modules/quality/MyActions';

export const Route = createFileRoute('/_app/quality/my-reports')({
  component: () => (
    <RequirePermission permission={Q.incidentsReport} what="your incident reports">
      <MyReportsPage />
    </RequirePermission>
  ),
});

function MyReportsPage() {
  const navigate = useNavigate();
  const incidents = useQuery(qualityQueries.myIncidents());

  return (
    <Page maxWidth={1200}>
      <PageHeader
        title="My reports and actions"
        subtitle="Incidents you reported under your name, and corrective actions you own. Anonymous reports are not listed here by design."
        icon={<ClipboardTaskRegular />}
        actions={
          <Button appearance="primary" icon={<ShieldErrorRegular />} onClick={() => void navigate({ to: '/quality/report' })}>
            Report an incident
          </Button>
        }
      />
      <Section title="Actions assigned to me" description="Start, then mark done when finished; the quality team checks the effect later." flush>
        <MyActionsTable />
      </Section>
      <Section title="Incidents I reported" flush>
        <DataTable
          ariaLabel="Incidents I reported"
          rows={incidents.data}
          loading={incidents.isLoading}
          error={incidents.error}
          onRetry={() => void incidents.refetch()}
          getRowId={(i) => i.id}
          columns={[
            { key: 'number', header: 'Report', width: 150, render: (i) => <Text font="monospace">{i.incidentNumber}</Text> },
            { key: 'type', header: 'Kind', render: (i) => incidentTypeLabel[i.type] },
            { key: 'severity', header: 'Seriousness', render: (i) => <SeverityBadge severity={i.severity} /> },
            { key: 'reported', header: 'Reported', priority: 'low', render: (i) => formatDateTime(i.reportedAt) },
            { key: 'status', header: 'Progress', render: (i) => <IncidentStatusBadge status={i.status} /> },
          ]}
          emptyTitle="You have not reported any incidents under your name"
          emptyDescription="If something went wrong or nearly went wrong, report it: it helps everyone learn."
          size="small"
        />
      </Section>
    </Page>
  );
}
