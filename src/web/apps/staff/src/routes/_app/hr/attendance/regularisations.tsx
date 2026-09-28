import { Button, Caption1, Field, Select, Text } from '@fluentui/react-components';
import { CalendarEditRegular, PersonClockRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { DecisionButtons } from '@/modules/hr/ApprovalActions';
import { hrApi, hrKeys, hrQueries } from '@/modules/hr/api';
import { APPROVAL_STATUSES, HR_PERMISSIONS as P, regularisationKindLabel, type ApprovalStatus } from '@/modules/hr/labels';
import { ATTENDANCE_NAV } from '@/modules/hr/nav';
import { RegularisationDialog } from '@/modules/hr/RegularisationDialog';
import { formatTimeOfDay } from '@/modules/hr/shared/dates';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { SubNav } from '@/modules/hr/shared/SubNav';
import { FilterPad } from '@/modules/hr/shared/layout';

interface CorrectionSearch {
  status?: ApprovalStatus | 'all';
  page?: number;
}

export const Route = createFileRoute('/_app/hr/attendance/regularisations')({
  validateSearch: (s: Record<string, unknown>): CorrectionSearch => ({
    status: s.status === 'all' || APPROVAL_STATUSES.includes(s.status as ApprovalStatus) ? (s.status as ApprovalStatus | 'all') : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: () => (
    <RequirePermission permission={P.attendanceManage} what="attendance corrections">
      <CorrectionsPage />
    </RequirePermission>
  ),
});

function CorrectionsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const [raising, setRaising] = useState(false);
  const status = search.status ?? 'Pending';
  const requests = useQuery(hrQueries.regularisations(status === 'all' ? undefined : status, search.page ?? 1));

  return (
    <Page>
      <PageHeader
        title="Attendance corrections"
        subtitle="Missed punches and official duty away from the terminal. Approving recomputes the day straight away."
        icon={<PersonClockRegular />}
        actions={
          <Button appearance="primary" icon={<CalendarEditRegular />} onClick={() => setRaising(true)}>
            Correct for an employee
          </Button>
        }
      />
      <SubNav items={ATTENDANCE_NAV} ariaLabel="Attendance screens" />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Status">
              <Select value={status} onChange={(_, d) => void navigate({ search: { status: d.value === 'Pending' ? undefined : (d.value as ApprovalStatus | 'all') } })}>
                <option value="Pending">Waiting for approval</option>
                <option value="Approved">Approved</option>
                <option value="Rejected">Rejected</option>
                <option value="Cancelled">Withdrawn</option>
                <option value="all">All</option>
              </Select>
            </Field>
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Attendance corrections"
          rows={requests.data?.items}
          loading={requests.isLoading}
          error={requests.error}
          onRetry={() => void requests.refetch()}
          getRowId={(r) => r.id}
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
            { key: 'date', header: 'Day', width: 120, render: (r) => formatDateOnly(r.date) },
            { key: 'kind', header: 'Correction', render: (r) => regularisationKindLabel[r.kind] },
            {
              key: 'times',
              header: 'Times asked for',
              render: (r) => [r.requestedIn ? `in ${formatTimeOfDay(r.requestedIn)}` : '', r.requestedOut ? `out ${formatTimeOfDay(r.requestedOut)}` : ''].filter(Boolean).join(' · ') || '—',
            },
            { key: 'reason', header: 'Reason', priority: 'low', render: (r) => r.reason },
            { key: 'raised', header: 'Asked', priority: 'low', render: (r) => formatDateTime(r.createdAt) },
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
            r.status === 'Pending' ? (
              <DecisionButtons
                subject={`${r.employeeName}'s correction for ${formatDateOnly(r.date)}`}
                decide={async (approve, note) => {
                  await hrApi.decideRegularisation(r.id, approve, note);
                  void queryClient.invalidateQueries({ queryKey: hrKeys.attendance });
                }}
              />
            ) : null
          }
          emptyTitle={status === 'Pending' ? 'Nothing waiting for approval' : 'No corrections'}
          emptyDescription={status === 'Pending' ? 'Staff ask for corrections from My HR; reporting managers can approve their team’s requests too.' : undefined}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={requests.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
      <RegularisationDialog
        open={raising}
        onClose={() => setRaising(false)}
        pickEmployee
        submit={async (employeeId, request) => {
          if (!employeeId) {
            throw new Error('Choose the employee.');
          }
          await hrApi.requestRegularisation(employeeId, request);
          void queryClient.invalidateQueries({ queryKey: hrKeys.attendance });
        }}
      />
    </Page>
  );
}
