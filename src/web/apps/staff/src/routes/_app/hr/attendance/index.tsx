import { Button, Field, Input, Select, Spinner } from '@fluentui/react-components';
import { ArrowSyncRegular, CalendarEditRegular, PersonClockRegular } from '@fluentui/react-icons';
import { formatDateOnly, todayIso } from '@hc360/core';
import { FilterBar, KpiCard, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { hrApi, hrKeys, hrQueries } from '@/modules/hr/api';
import { AttendanceTable } from '@/modules/hr/AttendanceTable';
import type { PickedEmployee } from '@/modules/hr/EmployeePicker';
import {
  ATTENDANCE_STATUSES,
  attendanceStatusLabel,
  HR_PERMISSIONS as P,
  type AttendanceStatus,
} from '@/modules/hr/labels';
import { ATTENDANCE_NAV } from '@/modules/hr/nav';
import { RegularisationDialog } from '@/modules/hr/RegularisationDialog';
import { addDays, isIsoDate } from '@/modules/hr/shared/dates';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { SubNav } from '@/modules/hr/shared/SubNav';
import { FilterPad, KpiGrid } from '@/modules/hr/shared/layout';

interface SheetSearch {
  date?: string;
  departmentId?: string;
  status?: AttendanceStatus;
  page?: number;
}

export const Route = createFileRoute('/_app/hr/attendance/')({
  validateSearch: (s: Record<string, unknown>): SheetSearch => ({
    date: isIsoDate(s.date) ? s.date : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    status: ATTENDANCE_STATUSES.includes(s.status as AttendanceStatus) ? (s.status as AttendanceStatus) : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: () => (
    <RequirePermission permission={P.attendanceManage} what="attendance">
      <DailySheetPage />
    </RequirePermission>
  ),
});

const COUNTED: AttendanceStatus[] = ['Present', 'Absent', 'HalfDay', 'Leave'];

function DailySheetPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments();
  const today = todayIso();
  const date = search.date ?? addDays(today, -1);
  const [correcting, setCorrecting] = useState<{ employee: PickedEmployee | null; date: string } | null>(null);
  const days = useQuery(
    hrQueries.attendance({
      from: date,
      to: date,
      departmentId: search.departmentId,
      status: search.status,
      page: search.page,
      pageSize: 50,
    }),
  );
  // One small request per status keeps the counts right whatever page or filter is shown.
  const countQueries = useQueries({
    queries: COUNTED.map((status) =>
      hrQueries.attendance({ from: date, to: date, departmentId: search.departmentId, status, pageSize: 1 }),
    ),
  });
  const counts = COUNTED.map((status, index) => ({ status, query: countQueries[index] }));

  const compute = useMutation({
    mutationFn: () => hrApi.computeAttendance(date, date),
    onSuccess: (result) => {
      notify.success('Attendance computed', `${result.employeeDays} employee-days for ${formatDateOnly(date)}.`);
      void queryClient.invalidateQueries({ queryKey: hrKeys.attendance });
    },
    onError: (error) => notify.error(error, 'Could not compute attendance'),
  });

  const set = (patch: Partial<SheetSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Attendance"
        subtitle="Computed from the terminal punches, the roster, leave and holidays. Last night's run covers the previous two days."
        icon={<PersonClockRegular />}
        actions={
          <>
            <Button icon={<CalendarEditRegular />} onClick={() => setCorrecting({ employee: null, date })}>
              Correct attendance
            </Button>
            <Button
              appearance="primary"
              icon={compute.isPending ? <Spinner size="tiny" /> : <ArrowSyncRegular />}
              disabled={compute.isPending}
              onClick={() => compute.mutate()}
            >
              Compute {date === today ? 'today' : formatDateOnly(date)}
            </Button>
          </>
        }
      />
      <SubNav items={ATTENDANCE_NAV} ariaLabel="Attendance screens" />
      <KpiGrid>
        {counts.map(({ status, query }) => (
          <KpiCard
            key={status}
            label={attendanceStatusLabel[status]}
            value={query?.data ? query.data.totalCount : '…'}
            onClick={() => set({ status: search.status === status ? undefined : status })}
            hint={search.status === status ? 'Showing only these (click to clear)' : 'Click to show only these'}
          />
        ))}
      </KpiGrid>
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Date">
              <Input
                type="date"
                value={date}
                max={today}
                onChange={(_, d) =>
                  isIsoDate(d.value) && set({ date: d.value === addDays(today, -1) ? undefined : d.value })
                }
              />
            </Field>
            <Field label="Department">
              <Select
                value={search.departmentId ?? ''}
                onChange={(_, d) => set({ departmentId: d.value || undefined })}
              >
                <option value="">All</option>
                {toOptions(departments.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as AttendanceStatus | undefined })}
              >
                <option value="">All</option>
                {ATTENDANCE_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {attendanceStatusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </FilterPad>
        <AttendanceTable
          rows={days.data?.items}
          loading={days.isLoading}
          error={days.error}
          onRetry={() => void days.refetch()}
          showEmployee
          showDate={false}
          onCorrect={(day) =>
            setCorrecting({
              employee: { id: day.employeeId, code: day.employeeCode, name: day.employeeName },
              date: day.date,
            })
          }
          onRowClick={(day) =>
            void navigate({
              to: '/hr/attendance/punches',
              search: { employeeId: day.employeeId, from: day.date, to: day.date },
            })
          }
          page={search.page ?? 1}
          pageSize={50}
          totalCount={days.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          emptyTitle={`No attendance for ${formatDateOnly(date)}`}
          emptyDescription='Use "Compute" to work it out from the punches received so far.'
        />
      </Section>
      <RegularisationDialog
        open={correcting !== null}
        onClose={() => setCorrecting(null)}
        employee={correcting?.employee ?? null}
        pickEmployee={!correcting?.employee}
        defaultDate={correcting?.date ?? date}
        submit={async (employeeId, request) => {
          const target = employeeId ?? correcting?.employee?.id;
          if (!target) {
            throw new Error('Choose the employee.');
          }
          await hrApi.requestRegularisation(target, request);
          void queryClient.invalidateQueries({ queryKey: hrKeys.attendance });
        }}
      />
    </Page>
  );
}
