import { CalendarPersonRegular, PeopleTeamRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { BALANCE_VIEWERS, CREDENTIAL_VIEWERS, HOLIDAY_VIEWERS, HR_PERMISSIONS as P, HR_STAFF, PAYROLL_VIEWERS, ROSTER_VIEWERS } from './labels';
import { PendingApprovalsWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'hr',
  title: 'Human resources',
  section: 'people',
  icon: PeopleTeamRegular,
  order: 10,
  links: [
    { label: 'My HR', to: '/hr/me', permission: P.self, keywords: ['self service', 'my leave', 'payslip', 'my roster', 'duty'] },
    { label: 'HR overview', to: '/hr', permission: HR_STAFF, keywords: ['dashboard', 'headcount', 'hrms'] },
    { label: 'Employees', to: '/hr/employees', permission: P.employeesRead, keywords: ['staff', 'employee master', 'joining', 'separation'] },
    { label: 'Duty roster', to: '/hr/roster', permission: ROSTER_VIEWERS, keywords: ['shift', 'rota', 'schedule', 'duty'] },
    { label: 'Shifts', to: '/hr/shifts', permission: P.rosterManage, keywords: ['shift timings', 'night shift'] },
    { label: 'Attendance', to: '/hr/attendance', permission: P.attendanceManage, keywords: ['biometric', 'punch', 'present', 'absent', 'regularisation'] },
    { label: 'Leave', to: '/hr/leave', permission: [P.leaveApprove, ...BALANCE_VIEWERS, ...HOLIDAY_VIEWERS.filter((p) => p !== P.self)], keywords: ['leave approval', 'holidays', 'balance'] },
    { label: 'Payroll', to: '/hr/payroll', permission: PAYROLL_VIEWERS, keywords: ['salary', 'payslip', 'pf', 'esi', 'professional tax', 'bank file'] },
    { label: 'Credentials and licences', to: '/hr/credentials', permission: CREDENTIAL_VIEWERS, keywords: ['registration', 'bls', 'acls', 'nursing council', 'expiry'] },
    { label: 'Designations', to: '/hr/designations', permission: [P.employeesRead, P.employeesManage], keywords: ['grade', 'post'] },
    { label: 'Attendance terminals', to: '/hr/devices', permission: P.attendanceManage, keywords: ['biometric device', 'zkteco', 'fingerprint'] },
  ],
  quickActions: [
    {
      label: 'Apply for leave',
      to: '/hr/me/apply-leave',
      permission: P.self,
      icon: CalendarPersonRegular,
      description: 'See your balance and send a request',
    },
  ],
  widgets: [{ id: 'hr-pending-approvals', permission: [P.leaveApprove, P.attendanceManage, P.self], span: 2, order: 70, component: PendingApprovalsWidget }],
};

export default manifest;
