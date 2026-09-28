import { Badge, Button, Caption1, MessageBar, MessageBarBody, MessageBarTitle, Text } from '@fluentui/react-components';
import { ErrorCircleRegular, ShieldCheckmarkRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { DataTable, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { cssdQueries, ISSUE_STATUS_LABEL, type SetIssueDto } from '@/modules/cssd/api';
import { AcknowledgeRecallDialog } from '@/modules/cssd/IssueActions';
import { METHOD_LABEL } from '@/modules/cssd/rules';

export const Route = createFileRoute('/_app/cssd/recalls')({
  component: RecallsPage,
});

/**
 * Recall by load (CSSD-05): when a released load's biological indicator fails, every issue of the load is flagged.
 * Departments acknowledge what they did with each pack; patients already treated go to infection control.
 */
function RecallsPage() {
  const navigate = useNavigate();
  const canAcknowledge = usePermission('cssd.recalls');
  const open = useQuery(cssdQueries.issues({ openRecalls: true, pageSize: 100 }));
  const failed = useQuery(cssdQueries.loads({ status: 'Failed' }));
  const recalledLoads = (failed.data?.items ?? []).filter((l) => l.releasedAt !== null);
  const [acknowledging, setAcknowledging] = useState<SetIssueDto | null>(null);
  const items = open.data?.items ?? [];
  const exposed = items.filter((i) => i.patientId).length;

  return (
    <Page>
      <PageHeader title="Recalls" icon={<ErrorCircleRegular />} subtitle="Packs from loads that failed after release" />
      {items.length > 0 && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>{items.length} recalled packs need action</MessageBarTitle>
            Withdraw unused packs and return them to CSSD.
            {exposed > 0 ? ` ${exposed} were already used on patients: inform infection control.` : ''}
          </MessageBarBody>
        </MessageBar>
      )}
      <Section title="Open recalls" description="Acknowledge each pack once the action is done" flush>
        <DataTable
          ariaLabel="Open recalls"
          rows={open.data?.items}
          loading={open.isLoading}
          error={open.error}
          onRetry={() => void open.refetch()}
          getRowId={(i) => i.id}
          columns={[
            {
              key: 'pack',
              header: 'Pack',
              width: 200,
              render: (i) => (
                <span>
                  <Text font="monospace" weight="semibold">
                    {i.packNumber ?? '—'}
                  </Text>
                  <Caption1 block>{i.setName}</Caption1>
                </span>
              ),
            },
            { key: 'dept', header: 'Department', render: (i) => i.departmentName ?? '—' },
            {
              key: 'status',
              header: 'Status',
              width: 180,
              render: (i) => (
                <span>
                  <StatusBadge status={i.status} label={ISSUE_STATUS_LABEL[i.status]} tone={i.patientId ? 'danger' : undefined} />
                  {i.patientId && (
                    <Badge appearance="filled" color="danger" style={{ marginLeft: 6 }}>
                      Used on a patient
                    </Badge>
                  )}
                </span>
              ),
            },
            { key: 'flagged', header: 'Recalled', width: 170, priority: 'low', render: (i) => (i.recallFlaggedAt ? formatDateTime(i.recallFlaggedAt) : '—') },
            { key: 'reason', header: 'Reason', priority: 'low', render: (i) => i.recallReason ?? '—' },
          ]}
          rowActions={(i) => (
            <span style={{ display: 'inline-flex', gap: 6 }}>
              <Button size="small" onClick={() => void navigate({ to: '/cssd/loads/$loadId', params: { loadId: i.loadId }, search: { tab: 'recall' } })}>
                Load report
              </Button>
              {canAcknowledge && (
                <Button size="small" appearance="primary" icon={<ShieldCheckmarkRegular />} onClick={() => setAcknowledging(i)}>
                  Acknowledge
                </Button>
              )}
            </span>
          )}
          emptyTitle="No open recalls"
          emptyDescription="Recalls appear here when a released load's biological indicator fails."
        />
      </Section>
      <Section title="Loads that failed after release" flush>
        <DataTable
          ariaLabel="Recalled loads"
          rows={failed.data ? recalledLoads : undefined}
          loading={failed.isLoading}
          error={failed.error}
          onRetry={() => void failed.refetch()}
          getRowId={(l) => l.id}
          onRowClick={(l) => void navigate({ to: '/cssd/loads/$loadId', params: { loadId: l.id }, search: { tab: 'recall' } })}
          size="small"
          columns={[
            { key: 'cycle', header: 'Cycle', width: 180, render: (l) => <Text font="monospace">{l.cycleNumber}</Text> },
            { key: 'sterilizer', header: 'Sterilizer', render: (l) => `${l.sterilizerName} · ${METHOD_LABEL[l.method]}` },
            { key: 'released', header: 'Released', width: 170, render: (l) => (l.releasedAt ? formatDateTime(l.releasedAt) : '—') },
            { key: 'reason', header: 'Failure', render: (l) => l.failureReason ?? '—' },
          ]}
          emptyTitle="No released load has failed"
        />
      </Section>
      <AcknowledgeRecallDialog issue={acknowledging} onClose={() => setAcknowledging(null)} />
    </Page>
  );
}
