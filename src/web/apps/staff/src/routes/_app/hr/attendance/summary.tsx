import { Badge, Button, Caption1, Checkbox, Field, Select, Spinner, Text } from '@fluentui/react-components';
import { ArrowDownloadRegular, ArrowSyncRegular, PersonClockRegular } from '@fluentui/react-icons';
import { todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { hrApi, hrKeys, hrQueries } from '@/modules/hr/api';
import { formatDays, needsAttention } from '@/modules/hr/attendance';
import { HR_PERMISSIONS as P } from '@/modules/hr/labels';
import { ATTENDANCE_NAV } from '@/modules/hr/nav';
import {
  currentMonth,
  formatMinutes,
  monthEnd,
  monthLabel,
  monthStart,
  MONTH_NAMES,
  yearOptions,
} from '@/modules/hr/shared/dates';
import { saveCsv } from '@/modules/hr/shared/files';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { SubNav } from '@/modules/hr/shared/SubNav';
import { FilterPad } from '@/modules/hr/shared/layout';

interface SummarySearch {
  year?: number;
  month?: number;
  departmentId?: string;
  attention?: boolean;
}

export const Route = createFileRoute('/_app/hr/attendance/summary')({
  validateSearch: (s: Record<string, unknown>): SummarySearch => ({
    year: typeof s.year === 'number' && s.year >= 2000 && s.year <= 2100 ? s.year : undefined,
    month: typeof s.month === 'number' && s.month >= 1 && s.month <= 12 ? s.month : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    attention: s.attention === true ? true : undefined,
  }),
  component: () => (
    <RequirePermission permission={P.attendanceManage} what="the attendance summary">
      <SummaryPage />
    </RequirePermission>
  ),
});

/** The pre-payroll check sheet: every employee's month in one line. */
function SummaryPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments();
  const now = currentMonth();
  const year = search.year ?? now.year;
  const month = search.month ?? now.month;
  const summary = useQuery(hrQueries.attendanceSummary(year, month, search.departmentId));
  const today = todayIso();
  const from = monthStart(year, month);
  const to = monthEnd(year, month) > today ? today : monthEnd(year, month);

  const rows = useMemo(
    () => (summary.data ?? []).filter((row) => !search.attention || needsAttention(row)),
    [summary.data, search.attention],
  );
  const attentionCount = (summary.data ?? []).filter(needsAttention).length;

  const compute = useMutation({
    mutationFn: () => hrApi.computeAttendance(from, to),
    onSuccess: (result) => {
      notify.success('Month recomputed', `${result.employeeDays} employee-days for ${monthLabel(year, month)}.`);
      void queryClient.invalidateQueries({ queryKey: hrKeys.attendance });
    },
    onError: (error) => notify.error(error, 'Could not compute attendance'),
  });

  const set = (patch: Partial<SummarySearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }) });

  const exportCsv = () =>
    saveCsv(
      [
        [
          'Employee code',
          'Employee',
          'Department',
          'Days recorded',
          'Present',
          'Paid leave',
          'Unpaid leave',
          'Absent',
          'Off days',
          'Late days',
          'Late minutes',
          'Overtime minutes',
          'Missed punches',
        ],
        ...rows.map((r) => [
          r.employeeCode,
          r.employeeName,
          r.departmentName ?? '',
          r.recordedDays,
          r.presentDays,
          r.paidLeaveDays,
          r.unpaidLeaveDays,
          r.absentDays,
          r.offDays,
          r.lateDays,
          r.lateMinutes,
          r.overtimeMinutes,
          r.missingPunchDays,
        ]),
      ],
      `attendance-${year}-${String(month).padStart(2, '0')}.csv`,
    );

  return (
    <Page>
      <PageHeader
        title="Attendance summary"
        subtitle="Check this sheet before running payroll: absences and missed punches become loss of pay unless corrected."
        icon={<PersonClockRegular />}
        actions={
          <>
            <Button icon={<ArrowDownloadRegular />} disabled={rows.length === 0} onClick={exportCsv}>
              Download CSV
            </Button>
            <Button
              appearance="primary"
              icon={compute.isPending ? <Spinner size="tiny" aria-hidden /> : <ArrowSyncRegular />}
              disabled={compute.isPending || from > today}
              onClick={() => compute.mutate()}
            >
              Recompute {monthLabel(year, month, true)}
            </Button>
          </>
        }
      />
      <SubNav items={ATTENDANCE_NAV} ariaLabel="Attendance screens" />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Month">
              <Select value={String(month)} onChange={(_, d) => set({ month: Number(d.value) })}>
                {MONTH_NAMES.map((name, index) => (
                  <option key={name} value={index + 1}>
                    {name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Year">
              <Select value={String(year)} onChange={(_, d) => set({ year: Number(d.value) })}>
                {yearOptions().map((y) => (
                  <option key={y} value={y}>
                    {y}
                  </option>
                ))}
              </Select>
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
            <Checkbox
              style={{ alignSelf: 'flex-end' }}
              checked={Boolean(search.attention)}
              onChange={(_, d) => set({ attention: d.checked ? true : undefined })}
              label={`Only people who need attention (${attentionCount})`}
            />
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Attendance summary"
          rows={summary.isLoading ? undefined : rows}
          loading={summary.isLoading}
          error={summary.error}
          onRetry={() => void summary.refetch()}
          getRowId={(r) => r.employeeId}
          onRowClick={(r) =>
            void navigate({
              to: '/hr/employees/$employeeId',
              params: { employeeId: r.employeeId },
              search: { tab: 'attendance' },
            })
          }
          columns={[
            {
              key: 'employee',
              header: 'Employee',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.employeeName}</Text> <Caption1>{r.employeeCode}</Caption1>
                  {r.departmentName && <Caption1 block>{r.departmentName}</Caption1>}
                </span>
              ),
            },
            { key: 'days', header: 'Days', align: 'end', width: 70, priority: 'low', render: (r) => r.recordedDays },
            { key: 'present', header: 'Present', align: 'end', width: 80, render: (r) => formatDays(r.presentDays) },
            { key: 'leave', header: 'Paid leave', align: 'end', width: 90, render: (r) => formatDays(r.paidLeaveDays) },
            {
              key: 'unpaid',
              header: 'Unpaid leave',
              align: 'end',
              width: 100,
              priority: 'low',
              render: (r) => formatDays(r.unpaidLeaveDays),
            },
            {
              key: 'absent',
              header: 'Absent',
              align: 'end',
              width: 80,
              render: (r) =>
                r.absentDays > 0 ? <Text weight="semibold">{formatDays(r.absentDays)}</Text> : formatDays(r.absentDays),
            },
            {
              key: 'off',
              header: 'Off',
              align: 'end',
              width: 60,
              priority: 'low',
              render: (r) => formatDays(r.offDays),
            },
            {
              key: 'late',
              header: 'Late',
              priority: 'low',
              render: (r) => (r.lateDays ? `${r.lateDays} day(s), ${formatMinutes(r.lateMinutes)}` : '—'),
            },
            { key: 'ot', header: 'Overtime', priority: 'low', render: (r) => formatMinutes(r.overtimeMinutes) },
            {
              key: 'missing',
              header: 'Missed punches',
              render: (r) =>
                r.missingPunchDays > 0 ? (
                  <Badge color="danger" appearance="tint">
                    {r.missingPunchDays} day(s)
                  </Badge>
                ) : (
                  '—'
                ),
            },
          ]}
          emptyTitle={search.attention ? 'Nobody needs attention this month' : 'No attendance computed for this month'}
          emptyDescription={search.attention ? undefined : 'Use "Recompute" to work it out from the punches.'}
          size="small"
        />
      </Section>
    </Page>
  );
}
