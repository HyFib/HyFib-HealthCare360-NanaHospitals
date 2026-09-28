import { Button, Caption1, makeStyles, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Tab, TabList, Text, tokens } from '@fluentui/react-components';
import { AddRegular, CheckmarkRegular, DeleteRegular, DismissRegular, EditRegular, MoreHorizontalRegular, TagPercentRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr } from '@hc360/core';
import { ConfirmDialog, DataTable, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { billingApi, billingKeys, billingQueries, type DiscountApprovalDto, type DiscountLimitDto } from '@/modules/billing/api';
import { DecideDiscountDialog, DiscountLimitDialog } from '@/modules/billing/DiscountDecision';
import { DISCOUNT_APPROVAL_STATUSES, type DiscountApprovalStatus } from '@/modules/billing/labels';
import { formatPercent } from '@/modules/billing/money';
import { enumParam, pageParam } from '@/modules/billing/searchParams';
import { ActionError, Amount, DiscountStatusBadge, NoAccess } from '@/modules/billing/ui';

type View = DiscountApprovalStatus | 'limits';

interface ApprovalsSearch {
  view?: View;
  page?: number;
}

export const Route = createFileRoute('/_app/billing/discount-approvals/')({
  validateSearch: (s: Record<string, unknown>): ApprovalsSearch => ({
    view: s.view === 'limits' ? 'limits' : enumParam(s.view, DISCOUNT_APPROVAL_STATUSES),
    page: pageParam(s.page),
  }),
  component: DiscountApprovalsPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
});

const VIEW_LABEL: Record<View, string> = {
  Pending: 'Awaiting decision',
  Approved: 'Approved',
  Rejected: 'Rejected',
  Withdrawn: 'Withdrawn',
  limits: 'Discount limits',
};

/** Discounts above a cashier's limit (MB-05): decide with a reason; and the per-user limits themselves. */
function DiscountApprovalsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canApprove = usePermission('billing.discounts.approve');
  const view = search.view ?? 'Pending';

  if (!canApprove) {
    return (
      <Page>
        <PageHeader title="Discount approvals" />
        <NoAccess what="discount approvals" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader title="Discount approvals" subtitle="Discounts above a cashier's limit wait here; the requester cannot approve their own" icon={<TagPercentRegular />} />
      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList selectedValue={view} onTabSelect={(_, d) => void navigate({ search: { view: d.value === 'Pending' ? undefined : (d.value as View) } })}>
            {(['Pending', 'Approved', 'Rejected', 'Withdrawn', 'limits'] as const).map((v) => (
              <Tab key={v} value={v}>
                {VIEW_LABEL[v]}
              </Tab>
            ))}
          </TabList>
        </div>
        {view === 'limits' ? (
          <LimitsView />
        ) : (
          <RequestsView status={view} page={search.page ?? 1} onPage={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })} />
        )}
      </Section>
    </Page>
  );
}

function RequestsView({ status, page, onPage }: { status: DiscountApprovalStatus; page: number; onPage: (page: number) => void }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const approvals = useQuery(billingQueries.discountApprovals(status, page));
  const [decision, setDecision] = useState<{ approval: DiscountApprovalDto; kind: 'approve' | 'reject' } | null>(null);

  return (
    <>
      <DataTable
        ariaLabel="Discount requests"
        rows={approvals.data?.items}
        loading={approvals.isLoading}
        error={approvals.error}
        onRetry={() => void approvals.refetch()}
        getRowId={(a) => a.id}
        onRowClick={(a) => void navigate({ to: '/billing/discount-approvals/$approvalId', params: { approvalId: a.id } })}
        columns={[
          {
            key: 'patient',
            header: 'Patient',
            render: (a) => (
              <span>
                <Text weight="semibold">{a.patientName ?? '—'}</Text>
                <Caption1 block className={styles.muted}>
                  {a.uhid} · {a.billNumber ?? 'draft bill'}
                </Caption1>
              </span>
            ),
          },
          {
            key: 'amount',
            header: 'Discount',
            align: 'end',
            render: (a) => (
              <span>
                <Amount value={a.requestedAmount} strong />
                <Caption1 block className={styles.muted}>
                  {a.discountBase > 0 ? `${formatPercent((a.requestedAmount / a.discountBase) * 100)} of ${formatInr(a.discountBase)}` : ''}
                </Caption1>
              </span>
            ),
          },
          { key: 'reason', header: 'Reason', render: (a) => a.reason },
          {
            key: 'by',
            header: 'Requested',
            priority: 'low',
            render: (a) => (
              <span>
                {a.requestedByName ?? '—'}
                <Caption1 block className={styles.muted}>
                  {formatDateTime(a.requestedAt)}
                </Caption1>
              </span>
            ),
          },
          ...(status === 'Pending'
            ? []
            : [
                {
                  key: 'decided',
                  header: 'Decision',
                  priority: 'low' as const,
                  render: (a: DiscountApprovalDto) => (
                    <span>
                      <DiscountStatusBadge status={a.status} />
                      <Caption1 block className={styles.muted}>
                        {a.decidedAt ? formatDateTime(a.decidedAt) : ''}
                        {a.remarks ? ` · ${a.remarks}` : ''}
                      </Caption1>
                    </span>
                  ),
                },
              ]),
        ]}
        rowActions={
          status === 'Pending'
            ? (a) => (
                <>
                  <Button size="small" appearance="primary" icon={<CheckmarkRegular />} onClick={() => setDecision({ approval: a, kind: 'approve' })}>
                    Approve
                  </Button>{' '}
                  <Button size="small" icon={<DismissRegular />} onClick={() => setDecision({ approval: a, kind: 'reject' })}>
                    Reject
                  </Button>
                </>
              )
            : undefined
        }
        emptyTitle={status === 'Pending' ? 'No discount is waiting for a decision' : 'Nothing here'}
        emptyDescription={status === 'Pending' ? 'Requests appear here when a cashier asks for more than their limit.' : undefined}
        page={page}
        pageSize={25}
        totalCount={approvals.data?.totalCount}
        onPageChange={onPage}
        size="small"
      />
      {decision && <DecideDiscountDialog approval={decision.approval} decision={decision.kind} onClose={() => setDecision(null)} />}
    </>
  );
}

function LimitsView() {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const limits = useQuery(billingQueries.discountLimits());
  const [editing, setEditing] = useState<DiscountLimitDto | 'new' | null>(null);
  const [deleting, setDeleting] = useState<DiscountLimitDto | null>(null);
  const remove = useMutation({
    mutationFn: (id: string) => billingApi.deleteDiscountLimit(id),
    onSuccess: () => {
      notify.success('Limit removed', 'The user now gets the hospital default limit.');
      setDeleting(null);
      void queryClient.invalidateQueries({ queryKey: billingKeys.discounts() });
    },
  });

  return (
    <>
      <div style={{ padding: '8px 16px 12px' }}>
        <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
          Add a limit
        </Button>
        <Caption1 block style={{ marginTop: 6 }}>
          Users without a limit may give only the hospital default (usually 0 %), so every discount they give needs approval.
        </Caption1>
      </div>
      <DataTable
        ariaLabel="Discount limits"
        rows={limits.data}
        loading={limits.isLoading}
        error={limits.error}
        onRetry={() => void limits.refetch()}
        getRowId={(l) => l.id}
        onRowClick={(l) => setEditing(l)}
        columns={[
          { key: 'user', header: 'User', render: (l) => <Text weight="semibold">{l.userName ?? '—'}</Text> },
          { key: 'percent', header: 'Up to', align: 'end', render: (l) => formatPercent(l.maxPercent) },
          { key: 'cap', header: 'Rupee cap', align: 'end', render: (l) => (l.maxAmount !== null ? <Amount value={l.maxAmount} /> : 'No cap') },
          { key: 'notes', header: 'Notes', priority: 'low', render: (l) => l.notes ?? '—' },
        ]}
        rowActions={(l) => (
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button size="small" appearance="subtle" icon={<MoreHorizontalRegular />} aria-label={`Actions for ${l.userName ?? 'user'}`} />
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                <MenuItem icon={<EditRegular />} onClick={() => setEditing(l)}>
                  Change limit
                </MenuItem>
                <MenuItem icon={<DeleteRegular />} onClick={() => setDeleting(l)}>
                  Remove limit
                </MenuItem>
              </MenuList>
            </MenuPopover>
          </Menu>
        )}
        emptyTitle="No personal limits yet"
        emptyDescription="Add a limit for each cashier who may give discounts without approval."
        size="small"
      />
      {editing && <DiscountLimitDialog limit={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
      <ConfirmDialog
        open={deleting !== null}
        title={`Remove the limit of ${deleting?.userName ?? 'this user'}?`}
        confirmLabel="Remove limit"
        destructive
        busy={remove.isPending}
        onConfirm={() => deleting && remove.mutate(deleting.id)}
        onCancel={() => setDeleting(null)}
      >
        They fall back to the hospital default limit.
        <ActionError error={remove.error} />
      </ConfirmDialog>
    </>
  );
}
