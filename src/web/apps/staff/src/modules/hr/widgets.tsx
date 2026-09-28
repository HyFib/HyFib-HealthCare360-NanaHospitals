import { Badge, Caption1, makeStyles, Spinner, Text, tokens } from '@fluentui/react-components';
import { CheckmarkCircleRegular, ChevronRightRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { hrQueries } from './api';
import { HR_PERMISSIONS } from './labels';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '4px' },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    color: tokens.colorNeutralForeground1,
    textDecoration: 'none',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  label: { flex: 1, minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  done: { display: 'flex', alignItems: 'center', gap: '8px', color: tokens.colorNeutralForeground3 },
});

interface Item {
  key: string;
  label: string;
  hint: string;
  count: number;
  to: '/hr/leave' | '/hr/attendance/regularisations' | '/hr/me/team' | '/hr/me/leave';
  urgent?: boolean;
}

/**
 * "Pending approvals": what waits for this person, as HR and as a reporting manager, and their own requests that
 * wait for someone else. Every figure opens the screen where it is dealt with.
 */
export function PendingApprovalsWidget() {
  const styles = useStyles();
  const canLeave = usePermission(HR_PERMISSIONS.leaveApprove);
  const canAttendance = usePermission(HR_PERMISSIONS.attendanceManage);
  const isStaff = usePermission(HR_PERMISSIONS.self);
  const hrLeave = useQuery({ ...hrQueries.leaveRequests({ status: 'Pending', pageSize: 1 }), enabled: canLeave, refetchInterval: 120_000 });
  const hrRegularisations = useQuery({ ...hrQueries.regularisations('Pending', 1, 1), enabled: canAttendance, refetchInterval: 120_000 });
  const profile = useQuery({ ...hrQueries.myProfile(), enabled: isStaff, retry: false });
  const linked = isStaff && Boolean(profile.data);
  const manager = linked && (profile.data?.reportees ?? 0) > 0;
  const teamLeave = useQuery({ ...hrQueries.teamLeave('Pending'), enabled: manager, refetchInterval: 120_000 });
  const teamRegularisations = useQuery({ ...hrQueries.teamRegularisations('Pending'), enabled: manager, refetchInterval: 120_000 });
  const myLeave = useQuery({ ...hrQueries.myLeaveRequests(), enabled: linked });
  const myRegularisations = useQuery({ ...hrQueries.myRegularisations(), enabled: linked });

  const loading =
    (canLeave && hrLeave.isLoading) || (canAttendance && hrRegularisations.isLoading) || (isStaff && profile.isLoading) || (manager && (teamLeave.isLoading || teamRegularisations.isLoading));

  const items: Item[] = [];
  if (canLeave && hrLeave.data) {
    items.push({ key: 'hr-leave', label: 'Leave requests', hint: 'Waiting for HR', count: hrLeave.data.totalCount, to: '/hr/leave', urgent: true });
  }
  if (canAttendance && hrRegularisations.data) {
    items.push({
      key: 'hr-reg',
      label: 'Attendance corrections',
      hint: 'Waiting for HR',
      count: hrRegularisations.data.totalCount,
      to: '/hr/attendance/regularisations',
      urgent: true,
    });
  }
  if (manager) {
    items.push({
      key: 'team',
      label: "Your team's requests",
      hint: 'Leave and attendance corrections from people who report to you',
      count: (teamLeave.data?.length ?? 0) + (teamRegularisations.data?.length ?? 0),
      to: '/hr/me/team',
      urgent: true,
    });
  }
  if (linked) {
    const mine = (myLeave.data?.filter((r) => r.status === 'Pending').length ?? 0) + (myRegularisations.data?.filter((r) => r.status === 'Pending').length ?? 0);
    items.push({ key: 'mine', label: 'Your requests', hint: 'Waiting for your manager or HR', count: mine, to: '/hr/me/leave' });
  }

  const notLinked = isStaff && isApiError(profile.error) && profile.error.code === 'hr.self.not_linked';
  const visible = items.filter((item) => item.count > 0 || item.urgent);
  const waiting = items.reduce((sum, item) => sum + (item.urgent ? item.count : 0), 0);

  return (
    <Section title="Pending approvals" description={waiting > 0 ? `${waiting} waiting for your decision` : 'Leave and attendance requests'}>
      {loading ? (
        <Spinner size="tiny" label="Checking…" labelPosition="after" />
      ) : visible.length === 0 || visible.every((item) => item.count === 0) ? (
        <div className={styles.done} role="status">
          <CheckmarkCircleRegular fontSize={20} color={tokens.colorPaletteGreenForeground1} />
          <Text>{notLinked ? 'Nothing waiting. Your sign-in is not linked to an employee record; HR can link it.' : 'Nothing is waiting for approval.'}</Text>
        </div>
      ) : (
        <div className={styles.list}>
          {visible.map((item) => (
            <Link key={item.key} to={item.to} className={styles.row}>
              <span className={styles.label}>
                <Text weight="semibold">{item.label}</Text>
                <Caption1 block className={styles.muted}>
                  {item.hint}
                </Caption1>
              </span>
              <Badge appearance={item.count > 0 ? 'filled' : 'tint'} color={item.count > 0 && item.urgent ? 'danger' : 'informative'} size="large">
                {item.count}
              </Badge>
              <ChevronRightRegular aria-hidden />
            </Link>
          ))}
        </div>
      )}
    </Section>
  );
}
