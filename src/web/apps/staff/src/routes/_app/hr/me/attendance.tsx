import { Button, Caption1, Field, Select, Tooltip } from '@fluentui/react-components';
import { CalendarEditRegular, DismissRegular, PersonClockRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, todayIso } from '@hc360/core';
import { ConfirmDialog, DataTable, KpiCard, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { hrApi, hrKeys, hrQueries, type RegularisationDto } from '@/modules/hr/api';
import { attendanceTotals, formatDays } from '@/modules/hr/attendance';
import { AttendanceTable } from '@/modules/hr/AttendanceTable';
import { regularisationKindLabel } from '@/modules/hr/labels';
import { RegularisationDialog } from '@/modules/hr/RegularisationDialog';
import { SelfServicePage } from '@/modules/hr/SelfServicePage';
import { currentMonth, formatTimeOfDay, monthEnd, monthLabel, monthStart, MONTH_NAMES, yearOptions } from '@/modules/hr/shared/dates';
import { KpiGrid, Stack, Toolbar } from '@/modules/hr/shared/layout';

export const Route = createFileRoute('/_app/hr/me/attendance')({
  component: MyAttendancePage,
});

function MyAttendancePage() {
  const [correcting, setCorrecting] = useState<string | null | undefined>(undefined);
  return (
    <SelfServicePage
      title="My attendance"
      subtitle="Worked out from your punches on the attendance terminals, your roster and your leave"
      icon={<PersonClockRegular />}
      actions={
        <Button appearance="primary" icon={<CalendarEditRegular />} onClick={() => setCorrecting(null)}>
          Correct a day
        </Button>
      }
    >
      {() => <MyAttendance correcting={correcting} onCorrect={setCorrecting} />}
    </SelfServicePage>
  );
}

function MyAttendance({ correcting, onCorrect }: { correcting: string | null | undefined; onCorrect: (date: string | null | undefined) => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const now = currentMonth();
  const [year, setYear] = useState(now.year);
  const [month, setMonth] = useState(now.month);
  const [withdrawing, setWithdrawing] = useState<RegularisationDto | null>(null);
  const today = todayIso();
  const from = monthStart(year, month);
  const to = monthEnd(year, month) > today ? today : monthEnd(year, month);
  const future = from > today;
  const days = useQuery({ ...hrQueries.myAttendance(from, to), enabled: !future });
  const requests = useQuery(hrQueries.myRegularisations());
  const totals = attendanceTotals(days.data ?? []);

  const withdraw = useMutation({
    mutationFn: (id: string) => hrApi.myCancelRegularisation(id),
    onSuccess: () => {
      setWithdrawing(null);
      notify.success('Request withdrawn');
      void queryClient.invalidateQueries({ queryKey: hrKeys.me });
    },
    onError: (error) => notify.error(error, 'Could not withdraw the request'),
  });

  return (
    <Stack gap={16}>
      <Toolbar spaced={false}>
        <Field label="Month">
          <Select value={String(month)} onChange={(_, d) => setMonth(Number(d.value))}>
            {MONTH_NAMES.map((name, index) => (
              <option key={name} value={index + 1}>
                {name}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Year">
          <Select value={String(year)} onChange={(_, d) => setYear(Number(d.value))}>
            {yearOptions(2, 0).map((y) => (
              <option key={y} value={y}>
                {y}
              </option>
            ))}
          </Select>
        </Field>
      </Toolbar>
      {!future && days.data && (
        <KpiGrid>
          <KpiCard label="Present" value={formatDays(totals.present)} hint={monthLabel(year, month)} />
          <KpiCard label="On leave" value={formatDays(totals.paidLeave + totals.unpaidLeave)} />
          <KpiCard label="Absent" value={formatDays(totals.absent)} hint={totals.absent > 0 ? 'Counts as loss of pay unless corrected' : undefined} />
          <KpiCard label="Late arrivals" value={totals.lateDays} />
          <KpiCard label="Missed punches" value={totals.missing} hint={totals.missing > 0 ? 'Use "Correct" on those days' : 'None'} />
        </KpiGrid>
      )}
      <Section title={monthLabel(year, month)} flush>
        <AttendanceTable
          rows={future ? [] : days.data}
          loading={days.isLoading}
          error={days.error}
          onRetry={() => void days.refetch()}
          onCorrect={(day) => onCorrect(day.date)}
          emptyTitle={future ? 'This month has not started' : 'No attendance worked out yet'}
          emptyDescription="Attendance is worked out every night for the previous two days."
        />
      </Section>
      <Section title="My correction requests" flush>
        <DataTable
          ariaLabel="My correction requests"
          rows={requests.data}
          loading={requests.isLoading}
          error={requests.error}
          onRetry={() => void requests.refetch()}
          getRowId={(r) => r.id}
          columns={[
            { key: 'date', header: 'Day', width: 130, render: (r) => formatDateOnly(r.date) },
            { key: 'kind', header: 'Correction', render: (r) => regularisationKindLabel[r.kind] },
            {
              key: 'times',
              header: 'Times',
              render: (r) => [r.requestedIn ? `in ${formatTimeOfDay(r.requestedIn)}` : '', r.requestedOut ? `out ${formatTimeOfDay(r.requestedOut)}` : ''].filter(Boolean).join(' · ') || '—',
            },
            { key: 'asked', header: 'Asked', priority: 'low', render: (r) => formatDateTime(r.createdAt) },
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
              <Tooltip content="Withdraw this request" relationship="label">
                <Button size="small" appearance="subtle" icon={<DismissRegular />} onClick={() => setWithdrawing(r)}>
                  Withdraw
                </Button>
              </Tooltip>
            ) : null
          }
          emptyTitle="No correction requests"
          size="small"
        />
      </Section>
      <RegularisationDialog
        open={correcting !== undefined}
        onClose={() => onCorrect(undefined)}
        defaultDate={correcting ?? undefined}
        submit={async (_, request) => {
          await hrApi.myRequestRegularisation(request);
          void queryClient.invalidateQueries({ queryKey: hrKeys.me });
        }}
      />
      <ConfirmDialog
        open={withdrawing !== null}
        title="Withdraw this correction request?"
        confirmLabel="Withdraw"
        busy={withdraw.isPending}
        onConfirm={() => withdrawing && withdraw.mutate(withdrawing.id)}
        onCancel={() => setWithdrawing(null)}
      >
        {withdrawing ? `${regularisationKindLabel[withdrawing.kind]} for ${formatDateOnly(withdrawing.date)}.` : null}
      </ConfirmDialog>
    </Stack>
  );
}
