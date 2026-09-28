import { Badge, Button, Caption1, Field, Select, Switch, Text } from '@fluentui/react-components';
import { BoxArrowUpRegular, PersonRegular, ShieldCheckmarkRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { cssdQueries, ISSUE_STATUS_LABEL, type SetIssueDto } from '@/modules/cssd/api';
import { AcknowledgeRecallDialog, RecordUseDialog } from '@/modules/cssd/IssueActions';

interface IssuesSearch {
  departmentId?: string;
  openRecalls?: boolean;
  page?: number;
}

export const Route = createFileRoute('/_app/cssd/issues')({
  validateSearch: (s: Record<string, unknown>): IssuesSearch => ({
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    openRecalls: s.openRecalls === true ? true : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: IssuedPacksPage,
});

/** Packs issued to departments: record which patient each was used on, and acknowledge recalls. */
function IssuedPacksPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canRecordUse = usePermission(['cssd.requests', 'cssd.process']);
  const canAcknowledge = usePermission('cssd.recalls');
  const canIssue = usePermission('cssd.process');
  const departments = useDepartments();
  const issues = useQuery(cssdQueries.issues({ departmentId: search.departmentId, openRecalls: search.openRecalls, page: search.page }));
  const [using, setUsing] = useState<SetIssueDto | null>(null);
  const [acknowledging, setAcknowledging] = useState<SetIssueDto | null>(null);
  const set = (patch: Partial<IssuesSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }), replace: true });

  return (
    <Page>
      <PageHeader
        title="Issued packs"
        subtitle="Record the patient each pack was used on, so a recall can reach every patient"
        actions={
          canIssue && (
            <Button appearance="primary" icon={<BoxArrowUpRegular />} onClick={() => void navigate({ to: '/cssd/issue' })}>
              Issue packs
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Department">
              <Select value={search.departmentId ?? ''} onChange={(_, d) => set({ departmentId: d.value || undefined })}>
                <option value="">All departments</option>
                {toOptions(departments.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch checked={Boolean(search.openRecalls)} onChange={(_, d) => set({ openRecalls: d.checked || undefined })} label="Open recalls only" />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Issued packs"
          rows={issues.data?.items}
          loading={issues.isLoading}
          error={issues.error}
          onRetry={() => void issues.refetch()}
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
            {
              key: 'dept',
              header: 'Issued to',
              render: (i) => (
                <span>
                  {i.departmentName ?? '—'}
                  <Caption1 block>
                    {formatDateTime(i.issuedAt)}
                    {i.issuedToName ? ` · ${i.issuedToName}` : ''}
                  </Caption1>
                </span>
              ),
            },
            { key: 'expiry', header: 'Use by', width: 120, priority: 'low', render: (i) => (i.sterileUntil ? formatDateOnly(i.sterileUntil) : '—') },
            {
              key: 'status',
              header: 'Status',
              width: 180,
              render: (i) => (
                <span>
                  <StatusBadge status={i.status} label={ISSUE_STATUS_LABEL[i.status]} tone={i.status === 'Recalled' ? 'danger' : i.status === 'Used' ? 'success' : undefined} />
                  {i.usedAt && <Caption1 block>Used {formatDateTime(i.usedAt)}</Caption1>}
                </span>
              ),
            },
            {
              key: 'recall',
              header: 'Recall',
              width: 160,
              render: (i) =>
                i.recallFlaggedAt ? (
                  i.recallAcknowledgedAt ? (
                    <Badge appearance="tint" color="success">
                      Acknowledged
                    </Badge>
                  ) : (
                    <Badge appearance="filled" color="danger">
                      Recall open
                    </Badge>
                  )
                ) : (
                  '—'
                ),
            },
          ]}
          rowActions={(i) => (
            <span style={{ display: 'inline-flex', gap: 6 }}>
              {canRecordUse && i.status === 'Issued' && !i.recallFlaggedAt && (
                <Button size="small" icon={<PersonRegular />} onClick={() => setUsing(i)}>
                  Used on patient
                </Button>
              )}
              {canAcknowledge && i.recallFlaggedAt && !i.recallAcknowledgedAt && (
                <Button size="small" appearance="primary" icon={<ShieldCheckmarkRegular />} onClick={() => setAcknowledging(i)}>
                  Acknowledge
                </Button>
              )}
            </span>
          )}
          emptyTitle={search.openRecalls ? 'No open recalls' : 'No packs issued yet'}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={issues.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
      {using && <RecordUseDialog issue={using} onClose={() => setUsing(null)} />}
      <AcknowledgeRecallDialog issue={acknowledging} onClose={() => setAcknowledging(null)} />
    </Page>
  );
}
