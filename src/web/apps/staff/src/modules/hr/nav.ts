/** Tabs between the sibling screens of each HR area (rendered with SubNav). */
import { BALANCE_VIEWERS, HR_PERMISSIONS as P, PAYROLL_VIEWERS } from './labels';
import type { SubNavItem } from './shared/SubNav';

export const ATTENDANCE_NAV: SubNavItem[] = [
  { label: 'Daily sheet', to: '/hr/attendance', permission: P.attendanceManage },
  { label: 'Month summary', to: '/hr/attendance/summary', permission: P.attendanceManage },
  { label: 'Corrections', to: '/hr/attendance/regularisations', permission: P.attendanceManage },
  { label: 'Punches', to: '/hr/attendance/punches', permission: P.attendanceManage },
  { label: 'Terminals', to: '/hr/devices', permission: P.attendanceManage },
];

export const LEAVE_NAV: SubNavItem[] = [
  { label: 'Requests', to: '/hr/leave', permission: P.leaveApprove },
  { label: 'Balances', to: '/hr/leave/balances', permission: BALANCE_VIEWERS },
  { label: 'Holidays', to: '/hr/leave/holidays', permission: [P.attendanceManage, P.leaveApprove] },
  { label: 'Leave types', to: '/hr/leave/types', permission: [P.employeesRead, P.employeesManage, P.leaveApprove] },
];

export const PAYROLL_NAV: SubNavItem[] = [
  { label: 'Payroll runs', to: '/hr/payroll', permission: PAYROLL_VIEWERS },
  { label: 'Pay heads', to: '/hr/payroll/components', permission: PAYROLL_VIEWERS },
  { label: 'Professional tax', to: '/hr/payroll/professional-tax', permission: PAYROLL_VIEWERS },
];

export const SELF_NAV: SubNavItem[] = [
  { label: 'Overview', to: '/hr/me', permission: P.self },
  { label: 'Leave', to: '/hr/me/leave', permission: P.self },
  { label: 'Attendance', to: '/hr/me/attendance', permission: P.self },
  { label: 'Payslips', to: '/hr/me/payslips', permission: P.self },
];

/** Added to SELF_NAV for reporting managers. */
export const TEAM_NAV_ITEM: SubNavItem = { label: 'My team', to: '/hr/me/team', permission: P.self };
