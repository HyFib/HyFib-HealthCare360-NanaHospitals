import { Button, Caption1, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { CalendarAddRegular, CalendarEditRegular, PersonRegular } from '@fluentui/react-icons';
import { formatDateOnly, humanize, todayIso } from '@hc360/core';
import { DataTable, KpiCard, Section, StatusBadge } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { hrApi, hrKeys, hrQueries, type MyProfileDto } from '@/modules/hr/api';
import { attendanceTotals, formatDays } from '@/modules/hr/attendance';
import { credentialExpiry } from '@/modules/hr/labels';
import { RegularisationDialog } from '@/modules/hr/RegularisationDialog';
import { SelfServicePage } from '@/modules/hr/SelfServicePage';
import { ShiftChip } from '@/modules/hr/ShiftChip';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { addDays, currentMonth, dayHeader, formatTimeOfDay, monthLabel, monthStart } from '@/modules/hr/shared/dates';
import { Columns, KpiGrid, Stack } from '@/modules/hr/shared/layout';

export const Route = createFileRoute('/_app/hr/me/')({
  component: MyHrPage,
});

const useStyles = makeStyles({
  day: { display: 'flex', alignItems: 'center', gap: '12px', padding: '6px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke3}` },
  dayLabel: { width: '110px', flexShrink: 0 },
  today: { color: tokens.colorBrandForeground1, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
});

function MyHrPage() {
  const navigate = useNavigate();
  const [correcting, setCorrecting] = useState(false);
  return (
    <SelfServicePage
      title="My HR"
      subtitle="Your duty roster, leave, attendance and payslips"
      icon={<PersonRegular />}
      actions={
        <>
          <Button icon={<CalendarEditRegular />} onClick={() => setCorrecting(true)}>
            Correct my attendance
          </Button>
          <Button appearance="primary" icon={<CalendarAddRegular />} onClick={() => void navigate({ to: '/hr/me/apply-leave' })}>
            Apply for leave
          </Button>
        </>
      }
    >
      {(profile) => <Overview profile={profile} correcting={correcting} onCorrectingChange={setCorrecting} />}
    </SelfServicePage>
  );
}

function Overview({ profile, correcting, onCorrectingChange }: { profile: MyProfileDto; correcting: boolean; onCorrectingChange: (open: boolean) => void }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const today = todayIso();
  const { year, month } = currentMonth();
  const roster = useQuery(hrQueries.myRoster(today, addDays(today, 13)));
  const attendance = useQuery(hrQueries.myAttendance(monthStart(year, month), today));
  const balances = useQuery(hrQueries.myBalances(year));
  const credentials = useQuery(hrQueries.myCredentials());
  const requests = useQuery(hrQueries.myLeaveRequests());
  const totals = attendanceTotals(attendance.data ?? []);
  const todayShifts = (roster.data ?? []).filter((r) => r.date === today);
  const pending = (requests.data ?? []).filter((r) => r.status === 'Pending').length;
  const mainBalances = (balances.data ?? []).filter((b) => b.available > 0 || b.availed > 0);

  const days = Array.from({ length: 14 }, (_, index) => addDays(today, index));

  return (
    <>
      <KpiGrid>
        <KpiCard
          label="Today"
          value={
            todayShifts.length === 0 ? (
              <Text size={500}>No shift planned</Text>
            ) : (
              <span style={{ display: 'inline-flex', gap: 6 }}>
                {todayShifts.map((r) => (
                  <ShiftChip key={r.id} shift={{ code: r.shiftCode, name: r.shiftName, color: r.color, startTime: r.startTime, endTime: r.endTime }} isWeekOff={r.isWeekOff} />
                ))}
              </span>
            )
          }
          hint={todayShifts[0]?.startTime ? `${formatTimeOfDay(todayShifts[0].startTime)} to ${formatTimeOfDay(todayShifts[0].endTime)}` : undefined}
        />
        <KpiCard
          label={`Present in ${monthLabel(year, month, true)}`}
          value={attendance.data ? formatDays(totals.present) : '…'}
          hint={totals.missing > 0 ? `${totals.missing} missed punch(es): ask for a correction` : `${formatDays(totals.paidLeave)} day(s) on leave`}
          onClick={() => void navigate({ to: '/hr/me/attendance' })}
        />
        {mainBalances.slice(0, 3).map((b) => (
          <KpiCard key={b.leaveTypeId} label={`${b.leaveTypeName} left`} value={formatDays(b.available)} hint={`${formatDays(b.availed)} taken this year`} onClick={() => void navigate({ to: '/hr/me/leave' })} />
        ))}
        {pending > 0 && <KpiCard label="Waiting for approval" value={pending} hint="Your leave requests" onClick={() => void navigate({ to: '/hr/me/leave' })} />}
      </KpiGrid>

      <Columns>
        <Section title="My roster" description="The next two weeks">
          {roster.isLoading ? (
            <Caption1>Loading…</Caption1>
          ) : (
            <div>
              {days.map((date) => {
                const entries = (roster.data ?? []).filter((r) => r.date === date);
                const head = dayHeader(date);
                return (
                  <div key={date} className={styles.day}>
                    <Text className={mergeClasses(styles.dayLabel, date === today ? styles.today : '')}>
                      {date === today ? 'Today' : `${head.weekday} ${head.day} ${head.month}`}
                    </Text>
                    {entries.length === 0 ? (
                      <Caption1 className={styles.muted}>Not planned yet</Caption1>
                    ) : (
                      <span style={{ display: 'inline-flex', gap: 6, flexWrap: 'wrap', alignItems: 'center' }}>
                        {entries.map((r) => (
                          <span key={r.id} style={{ display: 'inline-flex', gap: 6, alignItems: 'center' }}>
                            <ShiftChip shift={{ code: r.shiftCode, name: r.shiftName, color: r.color, startTime: r.startTime, endTime: r.endTime }} isWeekOff={r.isWeekOff} />
                            {!r.isWeekOff && r.startTime && (
                              <Caption1 className={styles.muted}>
                                {formatTimeOfDay(r.startTime)} to {formatTimeOfDay(r.endTime)}
                              </Caption1>
                            )}
                          </span>
                        ))}
                      </span>
                    )}
                  </div>
                );
              })}
            </div>
          )}
        </Section>

        <Stack gap={16}>
          <Section title="My details" description="Bank account and PAN are shown masked. Ask HR to change them.">
            <DetailGrid minColumnWidth={180}>
              <Detail label="Employee code" mono>
                {profile.employeeCode}
              </Detail>
              <Detail label="Designation">{profile.designationName}</Detail>
              <Detail label="Department">{profile.departmentName}</Detail>
              <Detail label="Reports to">{profile.reportingManagerName}</Detail>
              <Detail label="Joined">{formatDateOnly(profile.joiningDate)}</Detail>
              <Detail label="Terminal PIN" mono>
                {profile.biometricPin}
              </Detail>
              <Detail label="Salary paid by">{humanize(profile.paymentMode)}</Detail>
              <Detail label="Bank account" mono>
                {profile.bankAccountMasked ? `${profile.bankName ?? ''} ${profile.bankAccountMasked}`.trim() : null}
              </Detail>
              <Detail label="PAN" mono>
                {profile.panMasked}
              </Detail>
              <Detail label="UAN" mono>
                {profile.uan}
              </Detail>
            </DetailGrid>
          </Section>
          {(credentials.data?.length ?? 0) > 0 && (
            <Section title="My licences and certificates" flush>
              <DataTable
                ariaLabel="My licences and certificates"
                rows={credentials.data}
                getRowId={(c) => c.id}
                columns={[
                  { key: 'name', header: 'Credential', render: (c) => `${c.name} · ${c.number}` },
                  {
                    key: 'status',
                    header: 'Status',
                    render: (c) => {
                      const expiry = credentialExpiry(c.stage, c.daysToExpiry);
                      return <StatusBadge status={c.stage} tone={expiry.tone} label={expiry.label} />;
                    },
                  },
                ]}
                size="small"
              />
            </Section>
          )}
        </Stack>
      </Columns>

      <RegularisationDialog
        open={correcting}
        onClose={() => onCorrectingChange(false)}
        submit={async (_, request) => {
          await hrApi.myRequestRegularisation(request);
          void queryClient.invalidateQueries({ queryKey: hrKeys.me });
        }}
      />
    </>
  );
}
