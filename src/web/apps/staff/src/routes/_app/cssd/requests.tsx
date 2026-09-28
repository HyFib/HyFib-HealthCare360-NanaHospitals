import { Badge, Button, Caption1, Field, Select, Switch, Text } from '@fluentui/react-components';
import { AddRegular, BoxArrowUpRegular, DismissCircleRegular } from '@fluentui/react-icons';
import { formatDateTime, formatRelative } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { cssdApi, cssdKeys, cssdQueries, REQUEST_STATUS_LABEL, type CssdRequestDto, type CssdRequestStatus } from '@/modules/cssd/api';
import { RequestDialog } from '@/modules/cssd/RequestDialog';
import { ReasonDialog } from '@/modules/inpatient/ReasonDialog';

const STATUSES: CssdRequestStatus[] = ['Open', 'PartiallyIssued', 'Issued', 'Cancelled'];

interface RequestsSearch {
  departmentId?: string;
  status?: CssdRequestStatus;
  all?: boolean;
}

export const Route = createFileRoute('/_app/cssd/requests')({
  validateSearch: (s: Record<string, unknown>): RequestsSearch => ({
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    status: STATUSES.includes(s.status as CssdRequestStatus) ? (s.status as CssdRequestStatus) : undefined,
    all: s.all === true ? true : undefined,
  }),
  component: CssdRequestsPage,
});

/** Department requests for sterile sets (CSSD-02): urgent first, then oldest. CSSD issues against them. */
function CssdRequestsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRequest = usePermission('cssd.requests');
  const canIssue = usePermission('cssd.process');
  const departments = useDepartments();
  const [creating, setCreating] = useState(false);
  const [cancelling, setCancelling] = useState<CssdRequestDto | null>(null);
  const openOnly = !search.all && !search.status;
  const requests = useQuery(cssdQueries.requests({ departmentId: search.departmentId, status: search.status, openOnly: openOnly || undefined }));

  const cancel = useMutation({
    mutationFn: ({ request, reason }: { request: CssdRequestDto; reason: string }) => cssdApi.cancelRequest(request.id, reason),
    onSuccess: () => {
      notify.success('Request cancelled');
      setCancelling(null);
      void queryClient.invalidateQueries({ queryKey: cssdKeys.all });
    },
  });

  const set = (patch: Partial<RequestsSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });

  return (
    <Page>
      <PageHeader
        title="CSSD requests"
        subtitle={openOnly ? 'Open requests: urgent first, then oldest' : 'All requests'}
        actions={
          canRequest && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setCreating(true)}>
              Request sets
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
            <Field label="Status">
              <Select value={search.status ?? ''} onChange={(_, d) => set({ status: (d.value || undefined) as CssdRequestStatus | undefined })}>
                <option value="">Any</option>
                {STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {REQUEST_STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch checked={!openOnly} disabled={Boolean(search.status)} onChange={(_, d) => set({ all: d.checked || undefined })} label="Include closed" />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="CSSD requests"
          rows={requests.data}
          loading={requests.isLoading}
          error={requests.error}
          onRetry={() => void requests.refetch()}
          getRowId={(r) => r.id}
          columns={[
            {
              key: 'number',
              header: 'Request',
              width: 170,
              render: (r) => (
                <span>
                  <Text font="monospace">{r.requestNumber}</Text>
                  {r.priority === 'Urgent' && (
                    <Badge size="small" color="danger" style={{ marginLeft: 6 }}>
                      Urgent
                    </Badge>
                  )}
                </span>
              ),
            },
            {
              key: 'dept',
              header: 'Department',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.departmentName ?? '—'}</Text>
                  <Caption1 block>
                    {r.requestedByName ?? ''} · {formatRelative(r.requestedAt)}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'sets',
              header: 'Sets',
              render: (r) => (
                <span>
                  {r.lines.map((l) => (
                    <Caption1 key={l.instrumentSetId} block>
                      {l.setName ?? 'Set'} × {l.quantity}
                      {l.issuedQuantity > 0 ? ` (${l.issuedQuantity} issued)` : ''}
                    </Caption1>
                  ))}
                </span>
              ),
            },
            { key: 'needed', header: 'Needed by', width: 170, priority: 'low', render: (r) => (r.requiredBy ? formatDateTime(r.requiredBy) : '—') },
            {
              key: 'status',
              header: 'Status',
              width: 130,
              render: (r) => (
                <span>
                  <StatusBadge status={r.status} label={REQUEST_STATUS_LABEL[r.status]} tone={r.status === 'PartiallyIssued' ? 'warning' : undefined} />
                  {r.cancelReason && <Caption1 block>{r.cancelReason}</Caption1>}
                </span>
              ),
            },
          ]}
          rowActions={(r) =>
            r.status === 'Open' || r.status === 'PartiallyIssued' ? (
              <span style={{ display: 'inline-flex', gap: 6 }}>
                {canIssue && (
                  <Button
                    size="small"
                    appearance="primary"
                    icon={<BoxArrowUpRegular />}
                    onClick={() => void navigate({ to: '/cssd/issue', search: { requestId: r.id, departmentId: r.departmentId } })}
                  >
                    Issue
                  </Button>
                )}
                {canRequest && (
                  <Button size="small" icon={<DismissCircleRegular />} onClick={() => setCancelling(r)} aria-label={`Cancel request ${r.requestNumber}`}>
                    Cancel
                  </Button>
                )}
              </span>
            ) : null
          }
          emptyTitle={openOnly ? 'No open requests' : 'No requests'}
          emptyDescription={canRequest ? 'Departments request sterile sets here; CSSD sees them immediately.' : undefined}
        />
      </Section>
      {creating && <RequestDialog onClose={() => setCreating(false)} />}
      <ReasonDialog
        open={cancelling !== null}
        title={cancelling ? `Cancel request ${cancelling.requestNumber}?` : 'Cancel request?'}
        description="CSSD stops preparing the sets for this request."
        confirmLabel="Cancel request"
        destructive
        busy={cancel.isPending}
        error={cancel.error}
        onConfirm={(reason) => cancelling && cancel.mutate({ request: cancelling, reason })}
        onCancel={() => {
          setCancelling(null);
          cancel.reset();
        }}
      />
    </Page>
  );
}
