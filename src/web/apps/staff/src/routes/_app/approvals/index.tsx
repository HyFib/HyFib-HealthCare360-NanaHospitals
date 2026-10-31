import { Button, Caption1, makeStyles, Tab, TabList, Text, tokens } from '@fluentui/react-components';
import { CheckmarkRegular, DismissRegular, PeopleCheckmarkRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { DataTable, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { ApprovalDecisionDialog, ApprovalStatusBadge, type Decision } from '@/modules/approvals/ApprovalDecision';
import { approvalQueries, type ApprovalRequestDto } from '@/modules/approvals/api';
import { pageParam } from '@/modules/billing/searchParams';

type Tabbed = 'ToDecide' | 'Mine';

interface ApprovalsSearch {
  view?: Tabbed;
  page?: number;
}

export const Route = createFileRoute('/_app/approvals/')({
  validateSearch: (s: Record<string, unknown>): ApprovalsSearch => ({
    view: s.view === 'Mine' ? 'Mine' : undefined,
    page: pageParam(s.page),
  }),
  component: ApprovalsPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * Maker-checker: the changes that need a second person. "To decide" lists what others asked for that the user may
 * approve, oldest first; nobody sees their own requests there. "Asked by me" follows the user's own requests.
 */
function ApprovalsPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const view = search.view ?? 'ToDecide';
  const page = search.page ?? 1;
  const requests = useQuery(approvalQueries.list({ view, page }));
  const [deciding, setDeciding] = useState<{ request: ApprovalRequestDto; decision: Decision } | null>(null);

  return (
    <Page>
      <PageHeader
        title="Approvals"
        subtitle="Changes one person asks for and another approves; nobody approves their own"
        icon={<PeopleCheckmarkRegular />}
      />
      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={view}
            onTabSelect={(_, d) => void navigate({ search: { view: d.value === 'Mine' ? 'Mine' : undefined } })}
          >
            <Tab value="ToDecide">To decide</Tab>
            <Tab value="Mine">Asked by me</Tab>
          </TabList>
        </div>
        <DataTable
          ariaLabel={view === 'Mine' ? 'Requests I asked for' : 'Requests waiting for my decision'}
          rows={requests.data?.items}
          loading={requests.isLoading}
          error={requests.error}
          onRetry={() => void requests.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) => void navigate({ to: '/approvals/$approvalId', params: { approvalId: r.id } })}
          columns={[
            {
              key: 'what',
              header: 'Change',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.summary}</Text>
                  <Caption1 block className={styles.muted}>
                    {r.title}
                  </Caption1>
                </span>
              ),
            },
            { key: 'reason', header: 'Reason', render: (r) => r.reason },
            {
              key: 'asked',
              header: 'Asked',
              priority: 'low',
              render: (r) => (
                <span>
                  {r.requestedByName}
                  <Caption1 block className={styles.muted}>
                    {formatDateTime(r.requestedAt)}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Status',
              render: (r) => (
                <span>
                  <ApprovalStatusBadge request={r} />
                  {r.decidedByName && (
                    <Caption1 block className={styles.muted}>
                      {r.decidedByName}
                      {r.decidedAt ? ` · ${formatDateTime(r.decidedAt)}` : ''}
                    </Caption1>
                  )}
                </span>
              ),
            },
          ]}
          rowActions={(r) =>
            r.canDecide ? (
              <>
                <Button
                  size="small"
                  appearance="primary"
                  icon={<CheckmarkRegular />}
                  onClick={() => setDeciding({ request: r, decision: 'approve' })}
                >
                  Approve
                </Button>{' '}
                <Button
                  size="small"
                  icon={<DismissRegular />}
                  onClick={() => setDeciding({ request: r, decision: 'reject' })}
                >
                  Reject
                </Button>
              </>
            ) : r.canWithdraw ? (
              <Button size="small" onClick={() => setDeciding({ request: r, decision: 'withdraw' })}>
                Withdraw
              </Button>
            ) : null
          }
          emptyTitle={view === 'Mine' ? 'You have not asked for any change' : 'Nothing is waiting for your decision'}
          emptyDescription={
            view === 'Mine'
              ? 'Credit notes, write-offs and record merges you ask for appear here until someone decides them.'
              : 'Changes others ask for that you may approve appear here, oldest first.'
          }
          page={page}
          pageSize={25}
          totalCount={requests.data?.totalCount}
          onPageChange={(next) => void navigate({ search: (prev) => ({ ...prev, page: next > 1 ? next : undefined }) })}
          size="small"
        />
      </Section>
      {deciding && (
        <ApprovalDecisionDialog
          request={deciding.request}
          decision={deciding.decision}
          onClose={() => setDeciding(null)}
        />
      )}
    </Page>
  );
}
