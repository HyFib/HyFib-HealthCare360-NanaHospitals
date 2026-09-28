import { Button, Caption1, Field, Select, Tooltip } from '@fluentui/react-components';
import { CalendarAddRegular, CalendarCancelRegular, CalendarPersonRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, todayIso } from '@hc360/core';
import { DataTable, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { hrApi, hrKeys, hrQueries, type LeaveRequestDto } from '@/modules/hr/api';
import { formatDays } from '@/modules/hr/attendance';
import { BalancesTable } from '@/modules/hr/EmployeeLeave';
import { canCancelLeave, leavePeriodText } from '@/modules/hr/leave';
import { SelfServicePage } from '@/modules/hr/SelfServicePage';
import { currentMonth, dayHeader, yearOptions } from '@/modules/hr/shared/dates';
import { ReasonDialog } from '@/modules/hr/shared/ReasonDialog';
import { Stack, Toolbar } from '@/modules/hr/shared/layout';

export const Route = createFileRoute('/_app/hr/me/leave')({
  component: MyLeavePage,
});

function MyLeavePage() {
  const navigate = useNavigate();
  return (
    <SelfServicePage
      title="My leave"
      subtitle="Your balance, requests and holidays"
      icon={<CalendarPersonRegular />}
      actions={
        <Button appearance="primary" icon={<CalendarAddRegular />} onClick={() => void navigate({ to: '/hr/me/apply-leave' })}>
          Apply for leave
        </Button>
      }
    >
      {() => <MyLeave />}
    </SelfServicePage>
  );
}

function MyLeave() {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [year, setYear] = useState(currentMonth().year);
  const [cancelling, setCancelling] = useState<LeaveRequestDto | null>(null);
  const balances = useQuery(hrQueries.myBalances(year));
  const requests = useQuery(hrQueries.myLeaveRequests());
  const holidays = useQuery(hrQueries.holidays(year));
  const today = todayIso();

  const cancel = useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) => hrApi.myCancelLeave(id, reason),
    onSuccess: () => {
      setCancelling(null);
      notify.success('Leave withdrawn', 'The days are back in your balance.');
      void queryClient.invalidateQueries({ queryKey: hrKeys.me });
    },
    onError: (error) => notify.error(error, 'Could not withdraw the leave'),
  });

  return (
    <Stack gap={16}>
      <Toolbar spaced={false}>
        <Field label="Year">
          <Select value={String(year)} onChange={(_, d) => setYear(Number(d.value))}>
            {yearOptions(2, 1).map((y) => (
              <option key={y} value={y}>
                {y}
              </option>
            ))}
          </Select>
        </Field>
      </Toolbar>
      <Section title={`Balance ${year}`} flush>
        <BalancesTable balances={balances.data} loading={balances.isLoading} error={balances.error} onRetry={() => void balances.refetch()} />
      </Section>
      <Section title="My requests" description="Pending requests and leave that has not started can be withdrawn." flush>
        <DataTable
          ariaLabel="My leave requests"
          rows={requests.data}
          loading={requests.isLoading}
          error={requests.error}
          onRetry={() => void requests.refetch()}
          getRowId={(r) => r.id}
          columns={[
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
          rowActions={(r) =>
            canCancelLeave(r, today, false) ? (
              <Tooltip content="Withdraw this request" relationship="label">
                <Button size="small" appearance="subtle" icon={<CalendarCancelRegular />} onClick={() => setCancelling(r)}>
                  Withdraw
                </Button>
              </Tooltip>
            ) : null
          }
          emptyTitle="No leave requests yet"
          emptyDescription='Use "Apply for leave" to send a request to your manager.'
          size="small"
        />
      </Section>
      <Section title={`Holidays ${year}`} flush>
        <DataTable
          ariaLabel="Holidays"
          rows={holidays.data}
          loading={holidays.isLoading}
          error={holidays.error}
          onRetry={() => void holidays.refetch()}
          getRowId={(h) => h.id}
          columns={[
            { key: 'date', header: 'Date', width: 150, render: (h) => `${formatDateOnly(h.date)} · ${dayHeader(h.date).weekday}` },
            { key: 'name', header: 'Holiday', render: (h) => h.name },
            { key: 'kind', header: '', width: 200, render: (h) => (h.kind === 'Restricted' ? <Caption1>Optional (restricted) holiday</Caption1> : null) },
          ]}
          emptyTitle="No holidays listed yet"
          size="small"
        />
      </Section>
      <ReasonDialog
        open={cancelling !== null}
        title="Withdraw this leave request?"
        description={cancelling ? leavePeriodText(cancelling) : undefined}
        label="Reason"
        confirmLabel="Withdraw"
        destructive
        busy={cancel.isPending}
        onConfirm={(reason) => cancelling && cancel.mutate({ id: cancelling.id, reason })}
        onCancel={() => setCancelling(null)}
      />
    </Stack>
  );
}
