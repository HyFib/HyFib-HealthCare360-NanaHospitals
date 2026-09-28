import { Caption1, Field, Select, Text } from '@fluentui/react-components';
import { PeopleTeamRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime } from '@hc360/core';
import { DataTable, EmptyState, Section, StatusBadge } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { DecisionButtons } from '@/modules/hr/ApprovalActions';
import { hrApi, hrKeys, hrQueries, type MyProfileDto } from '@/modules/hr/api';
import { formatDays } from '@/modules/hr/attendance';
import { regularisationKindLabel, type ApprovalStatus } from '@/modules/hr/labels';
import { leavePeriodText } from '@/modules/hr/leave';
import { SelfServicePage } from '@/modules/hr/SelfServicePage';
import { formatTimeOfDay } from '@/modules/hr/shared/dates';
import { Stack, Toolbar } from '@/modules/hr/shared/layout';

type TeamStatus = ApprovalStatus | 'all';

export const Route = createFileRoute('/_app/hr/me/team')({
  validateSearch: (s: Record<string, unknown>): { status?: TeamStatus } => ({
    status: s.status === 'all' || s.status === 'Approved' || s.status === 'Rejected' || s.status === 'Cancelled' ? (s.status as TeamStatus) : undefined,
  }),
  component: () => (
    <SelfServicePage title="My team" subtitle="Leave and attendance corrections from the people who report to you" icon={<PeopleTeamRegular />}>
      {(profile) => <Team profile={profile} />}
    </SelfServicePage>
  ),
});

function Team({ profile }: { profile: MyProfileDto }) {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const status = search.status ?? 'Pending';
  const apiStatus = status === 'all' ? undefined : status;
  const leave = useQuery({ ...hrQueries.teamLeave(apiStatus), enabled: profile.reportees > 0 });
  const corrections = useQuery({ ...hrQueries.teamRegularisations(apiStatus), enabled: profile.reportees > 0 });

  if (profile.reportees === 0) {
    return (
      <Section>
        <EmptyState icon={<PeopleTeamRegular />} title="Nobody reports to you" description="If that is wrong, ask HR to set you as the reporting manager on your team members' records." />
      </Section>
    );
  }

  const refresh = () => void queryClient.invalidateQueries({ queryKey: hrKeys.me });

  return (
    <Stack gap={16}>
      <Toolbar spaced={false}>
        <Field label="Show">
          <Select value={status} onChange={(_, d) => void navigate({ search: { status: d.value === 'Pending' ? undefined : (d.value as TeamStatus) } })}>
            <option value="Pending">Waiting for my decision</option>
            <option value="Approved">Approved</option>
            <option value="Rejected">Rejected</option>
            <option value="Cancelled">Withdrawn</option>
            <option value="all">All</option>
          </Select>
        </Field>
        <Caption1 style={{ alignSelf: 'flex-end' }}>{profile.reportees} people report to you.</Caption1>
      </Toolbar>
      <Section title="Leave requests" flush>
        <DataTable
          ariaLabel="Team leave requests"
          rows={leave.data}
          loading={leave.isLoading}
          error={leave.error}
          onRetry={() => void leave.refetch()}
          getRowId={(r) => r.id}
          columns={[
            {
              key: 'who',
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
            { key: 'status', header: 'Status', render: (r) => <StatusBadge status={r.status} /> },
          ]}
          rowActions={(r) =>
            r.status === 'Pending' ? (
              <DecisionButtons
                subject={`${r.employeeName}'s leave`}
                decide={async (approve, note) => {
                  await hrApi.teamDecideLeave(r.id, approve, note);
                  refresh();
                }}
              />
            ) : null
          }
          emptyTitle={status === 'Pending' ? 'No leave waiting for you' : 'No leave requests'}
          size="small"
        />
      </Section>
      <Section title="Attendance corrections" flush>
        <DataTable
          ariaLabel="Team attendance corrections"
          rows={corrections.data}
          loading={corrections.isLoading}
          error={corrections.error}
          onRetry={() => void corrections.refetch()}
          getRowId={(r) => r.id}
          columns={[
            {
              key: 'who',
              header: 'Employee',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.employeeName}</Text> <Caption1>{r.employeeCode}</Caption1>
                </span>
              ),
            },
            { key: 'date', header: 'Day', width: 120, render: (r) => formatDateOnly(r.date) },
            { key: 'kind', header: 'Correction', render: (r) => regularisationKindLabel[r.kind] },
            {
              key: 'times',
              header: 'Times',
              render: (r) => [r.requestedIn ? `in ${formatTimeOfDay(r.requestedIn)}` : '', r.requestedOut ? `out ${formatTimeOfDay(r.requestedOut)}` : ''].filter(Boolean).join(' · ') || '—',
            },
            { key: 'reason', header: 'Reason', priority: 'low', render: (r) => r.reason },
            { key: 'status', header: 'Status', render: (r) => <StatusBadge status={r.status} /> },
          ]}
          rowActions={(r) =>
            r.status === 'Pending' ? (
              <DecisionButtons
                subject={`${r.employeeName}'s correction for ${formatDateOnly(r.date)}`}
                decide={async (approve, note) => {
                  await hrApi.teamDecideRegularisation(r.id, approve, note);
                  refresh();
                }}
              />
            ) : null
          }
          emptyTitle={status === 'Pending' ? 'No corrections waiting for you' : 'No corrections'}
          size="small"
        />
      </Section>
    </Stack>
  );
}
