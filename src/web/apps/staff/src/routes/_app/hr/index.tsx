import { Button, Caption1, makeStyles, Spinner, Text, tokens } from '@fluentui/react-components';
import {
  ArrowSyncRegular,
  CalendarPersonRegular,
  CertificateRegular,
  ClipboardTaskRegular,
  MoneyRegular,
  PeopleTeamRegular,
  PersonAddRegular,
  PersonClockRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, HOSPITAL_TIME_ZONE, todayIso, useSession } from '@hc360/core';
import { DataTable, KpiCard, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, Navigate, useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { DecisionButtons } from '@/modules/hr/ApprovalActions';
import { hrApi, hrKeys, hrQueries } from '@/modules/hr/api';
import { formatDays } from '@/modules/hr/attendance';
import {
  CREDENTIAL_VIEWERS,
  credentialExpiry,
  HR_PERMISSIONS as P,
  HR_STAFF,
  PAYROLL_VIEWERS,
  regularisationKindLabel,
} from '@/modules/hr/labels';
import { leavePeriodText } from '@/modules/hr/leave';
import { NoAccess } from '@/modules/hr/shared/NoAccess';
import { currentMonth, formatTimeOfDay, monthLabel } from '@/modules/hr/shared/dates';
import { Columns, KpiGrid, Stack } from '@/modules/hr/shared/layout';

export const Route = createFileRoute('/_app/hr/')({
  component: HrOverviewPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  kpiAction: { marginTop: '4px' },
});

function HrOverviewPage() {
  const isHrStaff = usePermission(HR_STAFF);
  const isStaff = usePermission(P.self);
  if (!isHrStaff) {
    return isStaff ? <Navigate to="/hr/me" replace /> : <NoAccess what="the HR overview" />;
  }
  return <Overview />;
}

function Overview() {
  const styles = useStyles();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityName = useSession((s) => s.user?.facilityName);
  const canEmployees = usePermission(P.employeesRead);
  const canManage = usePermission(P.employeesManage);
  const canAttendance = usePermission(P.attendanceManage);
  const canLeave = usePermission(P.leaveApprove);
  const canCredentials = usePermission(CREDENTIAL_VIEWERS);
  const canPayroll = usePermission(PAYROLL_VIEWERS);
  const today = todayIso();

  const headcount = useQuery({ ...hrQueries.employees({}, 1), enabled: canEmployees });
  const present = useQuery({
    ...hrQueries.attendance({ from: today, to: today, status: 'Present', pageSize: 1 }),
    enabled: canAttendance,
  });
  const absent = useQuery({
    ...hrQueries.attendance({ from: today, to: today, status: 'Absent', pageSize: 1 }),
    enabled: canAttendance,
  });
  const onLeave = useQuery({
    ...hrQueries.leaveRequests({ status: 'Approved', from: today, to: today, pageSize: 1 }),
    enabled: canLeave,
  });
  const expiring = useQuery({ ...hrQueries.credentials({ expiringWithinDays: 30 }), enabled: canCredentials });
  const pendingLeave = useQuery({ ...hrQueries.leaveRequests({ status: 'Pending', pageSize: 6 }), enabled: canLeave });
  const pendingRegularisations = useQuery({ ...hrQueries.regularisations('Pending', 1, 6), enabled: canAttendance });
  const runs = useQuery({ ...hrQueries.payrollRuns(currentMonth().year, 1, 3), enabled: canPayroll });

  const compute = useMutation({
    mutationFn: () => hrApi.computeAttendance(today, today),
    onSuccess: (result) => {
      notify.success(
        "Today's attendance updated",
        `${result.employeeDays} employee-days computed from the terminal punches.`,
      );
      void queryClient.invalidateQueries({ queryKey: hrKeys.attendance });
    },
    onError: (error) => notify.error(error, 'Could not compute attendance'),
  });

  const count = (value: number | undefined, loading: boolean) => (loading ? '…' : (value ?? 0).toLocaleString('en-IN'));
  const pendingTotal = (pendingLeave.data?.totalCount ?? 0) + (pendingRegularisations.data?.totalCount ?? 0);
  const longDate = new Intl.DateTimeFormat('en-IN', {
    weekday: 'long',
    day: 'numeric',
    month: 'long',
    year: 'numeric',
    timeZone: HOSPITAL_TIME_ZONE,
  }).format(new Date());
  const latestRun = runs.data?.items[0];

  return (
    <Page>
      <PageHeader
        title="HR overview"
        subtitle={`${longDate}${facilityName ? ` · ${facilityName}` : ''}`}
        icon={<PeopleTeamRegular />}
        actions={
          canManage && (
            <Button
              appearance="primary"
              icon={<PersonAddRegular />}
              onClick={() => void navigate({ to: '/hr/employees/new' })}
            >
              Add employee
            </Button>
          )
        }
      />

      <KpiGrid>
        {canEmployees && (
          <KpiCard
            label="Employees"
            value={count(headcount.data?.totalCount, headcount.isLoading)}
            hint="Current staff on the rolls"
            icon={<PeopleTeamRegular />}
            onClick={() => void navigate({ to: '/hr/employees' })}
          />
        )}
        {canAttendance && (
          <KpiCard
            label="Present today"
            value={count(present.data?.totalCount, present.isLoading)}
            icon={<PersonClockRegular />}
            hint={
              <span>
                {absent.data ? `${absent.data.totalCount} absent so far · ` : ''}
                <Button
                  size="small"
                  appearance="transparent"
                  className={styles.kpiAction}
                  icon={compute.isPending ? <Spinner size="extra-tiny" /> : <ArrowSyncRegular />}
                  disabled={compute.isPending}
                  onClick={() => compute.mutate()}
                >
                  Update from punches
                </Button>
              </span>
            }
          />
        )}
        {canLeave && (
          <KpiCard
            label="On leave today"
            value={count(onLeave.data?.totalCount, onLeave.isLoading)}
            icon={<CalendarPersonRegular />}
            hint="Approved leave"
            onClick={() => void navigate({ to: '/hr/leave', search: { status: 'Approved' } })}
          />
        )}
        {canCredentials && (
          <KpiCard
            label="Licences due"
            value={count(expiring.data?.totalCount, expiring.isLoading)}
            icon={<CertificateRegular />}
            hint="Expired or expiring in 30 days"
            onClick={() => void navigate({ to: '/hr/credentials' })}
          />
        )}
        {(canLeave || canAttendance) && (
          <KpiCard
            label="Waiting for approval"
            value={count(pendingTotal, pendingLeave.isLoading || pendingRegularisations.isLoading)}
            icon={<ClipboardTaskRegular />}
            hint="Leave and attendance corrections"
          />
        )}
      </KpiGrid>

      <Columns>
        <Stack gap={16}>
          {canLeave && (
            <Section
              title="Leave waiting for approval"
              description="Oldest first is fairest; the balance is checked again when you approve."
              actions={
                <Button appearance="subtle" onClick={() => void navigate({ to: '/hr/leave' })}>
                  All requests
                </Button>
              }
              flush
            >
              <DataTable
                ariaLabel="Leave waiting for approval"
                rows={pendingLeave.data?.items}
                loading={pendingLeave.isLoading}
                error={pendingLeave.error}
                onRetry={() => void pendingLeave.refetch()}
                getRowId={(r) => r.id}
                columns={[
                  {
                    key: 'who',
                    header: 'Employee',
                    render: (r) => (
                      <span>
                        <Text weight="semibold">{r.employeeName}</Text>
                        <Caption1 block className={styles.muted}>
                          {r.leaveTypeCode} · {formatDays(r.days)} day(s) · {leavePeriodText(r)}
                        </Caption1>
                      </span>
                    ),
                  },
                ]}
                rowActions={(r) => (
                  <DecisionButtons
                    subject={`${r.employeeName}'s leave`}
                    decide={async (approve, note) => {
                      await hrApi.decideLeave(r.id, approve, note);
                      void queryClient.invalidateQueries({ queryKey: hrKeys.leave });
                    }}
                  />
                )}
                emptyTitle="No leave waiting"
                emptyDescription="New requests appear here and in your notifications."
                size="small"
              />
            </Section>
          )}
          {canAttendance && (
            <Section
              title="Attendance corrections waiting"
              actions={
                <Button appearance="subtle" onClick={() => void navigate({ to: '/hr/attendance/regularisations' })}>
                  All corrections
                </Button>
              }
              flush
            >
              <DataTable
                ariaLabel="Attendance corrections waiting"
                rows={pendingRegularisations.data?.items}
                loading={pendingRegularisations.isLoading}
                error={pendingRegularisations.error}
                onRetry={() => void pendingRegularisations.refetch()}
                getRowId={(r) => r.id}
                columns={[
                  {
                    key: 'who',
                    header: 'Employee',
                    render: (r) => (
                      <span>
                        <Text weight="semibold">{r.employeeName}</Text>
                        <Caption1 block className={styles.muted}>
                          {formatDateOnly(r.date)} · {regularisationKindLabel[r.kind]}
                          {r.requestedIn ? ` · in ${formatTimeOfDay(r.requestedIn)}` : ''}
                          {r.requestedOut ? ` · out ${formatTimeOfDay(r.requestedOut)}` : ''}
                        </Caption1>
                      </span>
                    ),
                  },
                ]}
                rowActions={(r) => (
                  <DecisionButtons
                    subject={`${r.employeeName}'s attendance correction`}
                    decide={async (approve, note) => {
                      await hrApi.decideRegularisation(r.id, approve, note);
                      void queryClient.invalidateQueries({ queryKey: hrKeys.attendance });
                    }}
                  />
                )}
                emptyTitle="No corrections waiting"
                size="small"
              />
            </Section>
          )}
        </Stack>
        <Stack gap={16}>
          {canCredentials && (
            <Section
              title="Licences expiring soon"
              description="Council registrations and BLS/ACLS: renew before they lapse (NABH)."
              actions={
                <Button appearance="subtle" onClick={() => void navigate({ to: '/hr/credentials' })}>
                  All credentials
                </Button>
              }
              flush
            >
              <DataTable
                ariaLabel="Licences expiring soon"
                rows={expiring.data?.items.slice(0, 8)}
                loading={expiring.isLoading}
                error={expiring.error}
                onRetry={() => void expiring.refetch()}
                getRowId={(c) => c.id}
                onRowClick={(c) =>
                  void navigate({
                    to: '/hr/employees/$employeeId',
                    params: { employeeId: c.employeeId },
                    search: { tab: 'credentials' },
                  })
                }
                columns={[
                  {
                    key: 'who',
                    header: 'Employee',
                    render: (c) => (
                      <span>
                        <Text weight="semibold">{c.employeeName}</Text>
                        <Caption1 block className={styles.muted}>
                          {c.name} · {c.number}
                        </Caption1>
                      </span>
                    ),
                  },
                  {
                    key: 'stage',
                    header: 'Status',
                    render: (c) => {
                      const expiry = credentialExpiry(c.stage, c.daysToExpiry);
                      return <StatusBadge status={c.stage} tone={expiry.tone} label={expiry.label} />;
                    },
                  },
                ]}
                emptyTitle="Nothing expiring in the next 30 days"
                size="small"
              />
            </Section>
          )}
          {canPayroll && (
            <Section
              title="Payroll"
              actions={
                <Button
                  appearance="subtle"
                  icon={<MoneyRegular />}
                  onClick={() => void navigate({ to: '/hr/payroll' })}
                >
                  Open payroll
                </Button>
              }
            >
              {runs.isLoading ? (
                <Spinner size="tiny" />
              ) : latestRun ? (
                <Button
                  appearance="subtle"
                  onClick={() => void navigate({ to: '/hr/payroll/runs/$runId', params: { runId: latestRun.id } })}
                  style={{ justifyContent: 'flex-start' }}
                >
                  <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center' }}>
                    <Text weight="semibold">{monthLabel(latestRun.year, latestRun.month)}</Text>
                    <StatusBadge status={latestRun.status} />
                    {latestRun.payslipsWithErrors > 0 && (
                      <StatusBadge status="Failed" label={`${latestRun.payslipsWithErrors} with errors`} />
                    )}
                  </span>
                </Button>
              ) : (
                <Text className={styles.muted}>No payroll created this year yet.</Text>
              )}
            </Section>
          )}
        </Stack>
      </Columns>
    </Page>
  );
}
