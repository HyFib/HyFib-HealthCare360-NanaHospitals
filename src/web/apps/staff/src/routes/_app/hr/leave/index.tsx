import { Button, Caption1, Field, Input, Select, Text, Tooltip } from '@fluentui/react-components';
import {
  ArrowDownloadRegular,
  CalendarAddRegular,
  CalendarCancelRegular,
  CalendarPersonRegular,
} from '@fluentui/react-icons';
import { formatDateTime, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, Navigate, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { DecisionButtons } from '@/modules/hr/ApprovalActions';
import { hrApi, hrKeys, hrQueries, type LeaveRequestDto } from '@/modules/hr/api';
import { formatDays } from '@/modules/hr/attendance';
import { EmployeePicker, type PickedEmployee } from '@/modules/hr/EmployeePicker';
import { APPROVAL_STATUSES, BALANCE_VIEWERS, HR_PERMISSIONS as P, type ApprovalStatus } from '@/modules/hr/labels';
import { canCancelLeave, leavePeriodText } from '@/modules/hr/leave';
import { LeaveOnBehalfDialog } from '@/modules/hr/LeaveOnBehalfDialog';
import { LEAVE_NAV } from '@/modules/hr/nav';
import { isIsoDate } from '@/modules/hr/shared/dates';
import { downloadStoredFile } from '@/modules/hr/shared/files';
import { NoAccess } from '@/modules/hr/shared/NoAccess';
import { ReasonDialog } from '@/modules/hr/shared/ReasonDialog';
import { SubNav } from '@/modules/hr/shared/SubNav';
import { FilterPad } from '@/modules/hr/shared/layout';

interface LeaveSearch {
  status?: ApprovalStatus | 'all';
  employeeId?: string;
  employeeName?: string;
  from?: string;
  to?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/hr/leave/')({
  validateSearch: (s: Record<string, unknown>): LeaveSearch => ({
    status:
      s.status === 'all' || APPROVAL_STATUSES.includes(s.status as ApprovalStatus)
        ? (s.status as ApprovalStatus | 'all')
        : undefined,
    employeeId: typeof s.employeeId === 'string' && s.employeeId ? s.employeeId : undefined,
    employeeName: typeof s.employeeName === 'string' && s.employeeName ? s.employeeName : undefined,
    from: isIsoDate(s.from) ? s.from : undefined,
    to: isIsoDate(s.to) ? s.to : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: LeaveRoute,
});

function LeaveRoute() {
  const canApprove = usePermission(P.leaveApprove);
  const canBalances = usePermission(BALANCE_VIEWERS);
  const canHolidays = usePermission([P.attendanceManage]);
  if (canApprove) {
    return <LeaveRequestsPage />;
  }
  if (canBalances) {
    return <Navigate to="/hr/leave/balances" replace />;
  }
  return canHolidays ? <Navigate to="/hr/leave/holidays" replace /> : <NoAccess what="leave management" />;
}

function LeaveRequestsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [applying, setApplying] = useState(false);
  const [cancelling, setCancelling] = useState<LeaveRequestDto | null>(null);
  const status = search.status ?? 'Pending';
  const today = todayIso();
  const requests = useQuery(
    hrQueries.leaveRequests({
      status: status === 'all' ? undefined : status,
      employeeId: search.employeeId,
      from: search.from,
      to: search.to,
      page: search.page,
    }),
  );
  const picked: PickedEmployee | null = search.employeeId
    ? { id: search.employeeId, code: '', name: search.employeeName ?? 'Selected employee' }
    : null;

  const cancel = useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) => hrApi.cancelLeave(id, reason),
    onSuccess: () => {
      setCancelling(null);
      notify.success('Leave cancelled', 'The employee has been notified and the days returned to the balance.');
      void queryClient.invalidateQueries({ queryKey: hrKeys.leave });
    },
    onError: (error) => notify.error(error, 'Could not cancel the leave'),
  });

  const set = (patch: Partial<LeaveSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Leave"
        subtitle="Requests from staff. The balance is checked again when you approve, and nobody can approve their own request."
        icon={<CalendarPersonRegular />}
        actions={
          <Button appearance="primary" icon={<CalendarAddRegular />} onClick={() => setApplying(true)}>
            Apply for an employee
          </Button>
        }
      />
      <SubNav items={LEAVE_NAV} ariaLabel="Leave screens" />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Status">
              <Select
                value={status}
                onChange={(_, d) =>
                  set({ status: d.value === 'Pending' ? undefined : (d.value as ApprovalStatus | 'all') })
                }
              >
                <option value="Pending">Waiting for approval</option>
                <option value="Approved">Approved</option>
                <option value="Rejected">Rejected</option>
                <option value="Cancelled">Cancelled</option>
                <option value="all">All</option>
              </Select>
            </Field>
            <div style={{ minWidth: 260 }}>
              <EmployeePicker
                value={picked}
                onChange={(value) => set({ employeeId: value?.id, employeeName: value?.name })}
                hint="Leave empty for everyone."
              />
            </div>
            <Field label="Leave on or after">
              <Input type="date" value={search.from ?? ''} onChange={(_, d) => set({ from: d.value || undefined })} />
            </Field>
            <Field label="Leave on or before">
              <Input type="date" value={search.to ?? ''} onChange={(_, d) => set({ to: d.value || undefined })} />
            </Field>
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Leave requests"
          rows={requests.data?.items}
          loading={requests.isLoading}
          error={requests.error}
          onRetry={() => void requests.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) =>
            void navigate({
              to: '/hr/employees/$employeeId',
              params: { employeeId: r.employeeId },
              search: { tab: 'leave' },
            })
          }
          columns={[
            {
              key: 'employee',
              header: 'Employee',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.employeeName}</Text> <Caption1>{r.employeeCode}</Caption1>
                </span>
              ),
            },
            { key: 'type', header: 'Leave', width: 70, render: (r) => r.leaveTypeCode },
            { key: 'period', header: 'Dates', render: (r) => leavePeriodText(r) },
            { key: 'days', header: 'Days', width: 70, align: 'end', render: (r) => formatDays(r.days) },
            { key: 'reason', header: 'Reason', priority: 'low', render: (r) => r.reason },
            { key: 'applied', header: 'Applied', priority: 'low', render: (r) => formatDateTime(r.appliedAt) },
            {
              key: 'status',
              header: 'Status',
              render: (r) => (
                <span>
                  <StatusBadge status={r.status} />
                  {r.decisionNote && <Caption1 block>{r.decisionNote}</Caption1>}
                </span>
              ),
            },
          ]}
          rowActions={(r) => (
            <span style={{ display: 'inline-flex', gap: 4 }}>
              {r.attachmentFileId && (
                <Tooltip content="Download the supporting document" relationship="label">
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<ArrowDownloadRegular />}
                    onClick={() =>
                      void downloadStoredFile(r.attachmentFileId!).catch((error: unknown) =>
                        notify.error(error, 'Could not open the document'),
                      )
                    }
                  />
                </Tooltip>
              )}
              {r.status === 'Pending' && (
                <DecisionButtons
                  subject={`${r.employeeName}'s leave`}
                  decide={async (approve, note) => {
                    await hrApi.decideLeave(r.id, approve, note);
                    void queryClient.invalidateQueries({ queryKey: hrKeys.leave });
                  }}
                />
              )}
              {r.status === 'Approved' && canCancelLeave(r, today, true) && (
                <Tooltip content="Cancel this leave" relationship="label">
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<CalendarCancelRegular />}
                    onClick={() => setCancelling(r)}
                  />
                </Tooltip>
              )}
            </span>
          )}
          emptyTitle={status === 'Pending' ? 'No leave waiting for approval' : 'No leave requests match'}
          emptyDescription={status === 'Pending' ? 'New requests appear here and in your notifications.' : undefined}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={requests.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
      <LeaveOnBehalfDialog open={applying} onClose={() => setApplying(false)} />
      <ReasonDialog
        open={cancelling !== null}
        title="Cancel this leave?"
        description={
          cancelling
            ? `${cancelling.employeeName}: ${leavePeriodText(cancelling)}. The days go back to the balance and past days are recomputed.`
            : undefined
        }
        label="Reason (shown to the employee)"
        confirmLabel="Cancel leave"
        destructive
        busy={cancel.isPending}
        onConfirm={(reason) => cancelling && cancel.mutate({ id: cancelling.id, reason })}
        onCancel={() => setCancelling(null)}
      />
    </Page>
  );
}
