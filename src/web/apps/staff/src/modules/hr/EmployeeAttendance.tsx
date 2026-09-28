import { Button, Field, Select, Spinner } from '@fluentui/react-components';
import { ArrowSyncRegular, CalendarEditRegular } from '@fluentui/react-icons';
import { todayIso } from '@hc360/core';
import { KpiCard, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { hrApi, hrKeys, hrQueries } from './api';
import { attendanceTotals, formatDays } from './attendance';
import { AttendanceTable } from './AttendanceTable';
import type { PickedEmployee } from './EmployeePicker';
import { RegularisationDialog } from './RegularisationDialog';
import { currentMonth, monthEnd, monthLabel, monthStart, MONTH_NAMES, yearOptions } from './shared/dates';
import { KpiGrid, Stack, Toolbar } from './shared/layout';

/** One employee's month of attendance with corrections (HR). */
export function EmployeeAttendance({ employee }: { employee: PickedEmployee }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const now = currentMonth();
  const [year, setYear] = useState(now.year);
  const [month, setMonth] = useState(now.month);
  const [correcting, setCorrecting] = useState<string | null | undefined>(undefined);
  const today = todayIso();
  const from = monthStart(year, month);
  const lastDay = monthEnd(year, month);
  const to = lastDay > today ? today : lastDay;
  const future = from > today;
  const days = useQuery({ ...hrQueries.attendance({ employeeId: employee.id, from, to, pageSize: 62 }), enabled: !future });
  const totals = attendanceTotals(days.data?.items ?? []);

  const compute = useMutation({
    mutationFn: () => hrApi.computeAttendance(from, to, employee.id),
    onSuccess: (result) => {
      notify.success('Attendance recomputed', `${result.employeeDays} day(s) for ${employee.name}`);
      void queryClient.invalidateQueries({ queryKey: hrKeys.attendance });
    },
    onError: (error) => notify.error(error, 'Could not compute attendance'),
  });

  return (
    <Stack gap={16}>
      <Toolbar spaced={false}>
        <Field label="Month">
          <Select value={String(month)} onChange={(_, data) => setMonth(Number(data.value))}>
            {MONTH_NAMES.map((name, index) => (
              <option key={name} value={index + 1}>
                {name}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Year">
          <Select value={String(year)} onChange={(_, data) => setYear(Number(data.value))}>
            {yearOptions().map((y) => (
              <option key={y} value={y}>
                {y}
              </option>
            ))}
          </Select>
        </Field>
        <Button
          style={{ alignSelf: 'flex-end' }}
          icon={compute.isPending ? <Spinner size="tiny" /> : <ArrowSyncRegular />}
          disabled={future || compute.isPending}
          onClick={() => compute.mutate()}
        >
          Recompute {monthLabel(year, month, true)}
        </Button>
        <Button style={{ alignSelf: 'flex-end' }} icon={<CalendarEditRegular />} onClick={() => setCorrecting(null)}>
          Correct a day
        </Button>
      </Toolbar>

      {!future && days.data && (
        <KpiGrid>
          <KpiCard label="Present" value={formatDays(totals.present)} hint="days" />
          <KpiCard label="Paid leave" value={formatDays(totals.paidLeave)} />
          <KpiCard label="Absent / loss of pay" value={formatDays(totals.absent + totals.unpaidLeave)} />
          <KpiCard label="Late arrivals" value={totals.lateDays} />
          <KpiCard label="Missed punches" value={totals.missing} hint={totals.missing > 0 ? 'Ask for a correction before payroll' : undefined} />
        </KpiGrid>
      )}

      <AttendanceTable
        rows={future ? [] : days.data?.items}
        loading={days.isLoading}
        error={days.error}
        onRetry={() => void days.refetch()}
        onCorrect={(day) => setCorrecting(day.date)}
        emptyTitle={future ? 'This month has not started' : 'No attendance computed for this month'}
      />

      <RegularisationDialog
        open={correcting !== undefined}
        onClose={() => setCorrecting(undefined)}
        employee={employee}
        defaultDate={correcting ?? undefined}
        submit={async (_, request) => {
          await hrApi.requestRegularisation(employee.id, request);
          void queryClient.invalidateQueries({ queryKey: hrKeys.attendance });
        }}
      />
    </Stack>
  );
}
