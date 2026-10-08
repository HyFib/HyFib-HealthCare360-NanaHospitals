/** HR enums exactly as the server sends them (strings), with the plain words shown on screen. */

export const HR_PERMISSIONS = {
  employeesRead: 'hr.employees.read',
  employeesManage: 'hr.employees.manage',
  attendanceManage: 'hr.attendance.manage',
  rosterManage: 'hr.roster.manage',
  leaveApprove: 'hr.leave.approve',
  payrollProcess: 'hr.payroll.process',
  payrollApprove: 'hr.payroll.approve',
  credentialsManage: 'hr.credentials.manage',
  self: 'hr.self',
} as const;

const P = HR_PERMISSIONS;

/** Anyone who works in the HR back office (the /hr overview). */
export const HR_STAFF = [
  P.employeesRead,
  P.employeesManage,
  P.attendanceManage,
  P.rosterManage,
  P.leaveApprove,
  P.payrollProcess,
  P.payrollApprove,
  P.credentialsManage,
] as const;
export const PAYROLL_VIEWERS = [P.payrollProcess, P.payrollApprove] as const;
export const CREDENTIAL_VIEWERS = [P.credentialsManage, P.employeesRead] as const;
export const ROSTER_VIEWERS = [P.rosterManage, P.attendanceManage] as const;
export const SHIFT_VIEWERS = [P.rosterManage, P.attendanceManage, P.employeesRead] as const;
export const LEAVE_TYPE_VIEWERS = [P.self, P.leaveApprove, P.employeesRead] as const;
export const HOLIDAY_VIEWERS = [P.self, P.attendanceManage, P.leaveApprove] as const;
export const BALANCE_VIEWERS = [P.leaveApprove, P.employeesRead] as const;

export const EMPLOYMENT_TYPES = ['Permanent', 'Contract', 'Consultant', 'Intern', 'Trainee'] as const;
export type EmploymentType = (typeof EMPLOYMENT_TYPES)[number];

export const EMPLOYEE_STATUSES = ['Active', 'Probation', 'OnNotice', 'Suspended', 'Separated'] as const;
export type EmployeeStatus = (typeof EMPLOYEE_STATUSES)[number];
/** Statuses that can be chosen in the form (separation has its own action). */
export const EDITABLE_EMPLOYEE_STATUSES = ['Active', 'Probation', 'OnNotice', 'Suspended'] as const;
export const employeeStatusLabel: Record<EmployeeStatus, string> = {
  Active: 'Active',
  Probation: 'On probation',
  OnNotice: 'Serving notice',
  Suspended: 'Suspended',
  Separated: 'Separated',
};

export const SEPARATION_REASONS = [
  'Resignation',
  'Termination',
  'Retirement',
  'EndOfContract',
  'Death',
  'Absconding',
  'Other',
] as const;
export type SeparationReason = (typeof SEPARATION_REASONS)[number];

export const PAYMENT_MODES = ['BankTransfer', 'Cheque', 'Cash'] as const;
export type SalaryPaymentMode = (typeof PAYMENT_MODES)[number];

export const STAFF_CATEGORIES = ['Medical', 'Nursing', 'Paramedical', 'Administrative', 'Support'] as const;
export type StaffCategory = (typeof STAFF_CATEGORIES)[number];

export const WEEKDAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'] as const;
export type Weekday = (typeof WEEKDAYS)[number];

export const CREDENTIAL_TYPES = ['MedicalCouncilRegistration', 'NursingCouncil', 'BLS', 'ACLS', 'Other'] as const;
export type CredentialType = (typeof CREDENTIAL_TYPES)[number];
export const credentialTypeLabel: Record<CredentialType, string> = {
  MedicalCouncilRegistration: 'Medical council registration',
  NursingCouncil: 'Nursing council registration',
  BLS: 'BLS (basic life support)',
  ACLS: 'ACLS (advanced cardiac life support)',
  Other: 'Other licence or certificate',
};

export type CredentialExpiryStage = 'None' | 'DueIn30Days' | 'DueIn7Days' | 'Expired';

export const ATTENDANCE_STATUSES = ['Present', 'Absent', 'HalfDay', 'Leave', 'Holiday', 'WeekOff', 'OnDuty'] as const;
export type AttendanceStatus = (typeof ATTENDANCE_STATUSES)[number];
export const attendanceStatusLabel: Record<AttendanceStatus, string> = {
  Present: 'Present',
  Absent: 'Absent',
  HalfDay: 'Half day',
  Leave: 'On leave',
  Holiday: 'Holiday',
  WeekOff: 'Week off',
  OnDuty: 'On duty',
};

export const ATTENDANCE_FLAGS = [
  'Late',
  'EarlyExit',
  'MissingPunch',
  'Overtime',
  'WorkedOnOffDay',
  'Unrostered',
  'Regularised',
] as const;
export type AttendanceFlag = (typeof ATTENDANCE_FLAGS)[number];
export const attendanceFlagLabel: Record<AttendanceFlag, string> = {
  Late: 'Late',
  EarlyExit: 'Left early',
  MissingPunch: 'Missed punch',
  Overtime: 'Overtime',
  WorkedOnOffDay: 'Worked on off day',
  Unrostered: 'No shift planned',
  Regularised: 'Corrected',
};

export const APPROVAL_STATUSES = ['Pending', 'Approved', 'Rejected', 'Cancelled'] as const;
export type ApprovalStatus = (typeof APPROVAL_STATUSES)[number];

export const REGULARISATION_KINDS = ['MissedPunch', 'OnDuty'] as const;
export type RegularisationKind = (typeof REGULARISATION_KINDS)[number];
export const regularisationKindLabel: Record<RegularisationKind, string> = {
  MissedPunch: 'Missed punch',
  OnDuty: 'On official duty',
};

export const LEAVE_SESSIONS = ['FirstHalf', 'SecondHalf'] as const;
export type LeaveSession = (typeof LEAVE_SESSIONS)[number];

export const HOLIDAY_KINDS = ['National', 'Festival', 'Restricted'] as const;
export type HolidayKind = (typeof HOLIDAY_KINDS)[number];
export const holidayKindLabel: Record<HolidayKind, string> = {
  National: 'National holiday',
  Festival: 'Festival holiday',
  Restricted: 'Restricted (optional, a working day)',
};

export const PAYROLL_RUN_STATUSES = ['Draft', 'Processed', 'Approved', 'Paid', 'Locked'] as const;
export type PayrollRunStatus = (typeof PAYROLL_RUN_STATUSES)[number];

export const PAYROLL_INPUT_KINDS = ['Earning', 'Deduction', 'LopDaysOverride', 'TdsOverride'] as const;
export type PayrollInputKind = (typeof PAYROLL_INPUT_KINDS)[number];
export const payrollInputKindLabel: Record<PayrollInputKind, string> = {
  Earning: 'One-off earning',
  Deduction: 'One-off deduction',
  LopDaysOverride: 'Loss-of-pay days (override)',
  TdsOverride: 'TDS for this month (override)',
};

export type PayrollInputSource = 'Manual' | 'Import' | 'Integration';

export const PT_FREQUENCIES = ['Monthly', 'HalfYearly'] as const;
export type ProfessionalTaxFrequency = (typeof PT_FREQUENCIES)[number];

export const SALARY_COMPONENT_KINDS = ['Earning', 'Deduction'] as const;
export type SalaryComponentKind = (typeof SALARY_COMPONENT_KINDS)[number];

export const SALARY_CALCULATION_TYPES = ['Fixed', 'PercentOfBasic'] as const;
export type SalaryCalculationType = (typeof SALARY_CALCULATION_TYPES)[number];

export type PunchDirection = 'Unknown' | 'In' | 'Out';
export type PunchSource = 'Adms' | 'DeviceApi' | 'Regularisation' | 'Pull';

export type RosterConflictKind =
  'NotEmployed' | 'DuplicateAssignment' | 'WeekOffWithShift' | 'OverlappingShifts' | 'InsufficientRest' | 'OnLeave';
export type ConflictSeverity = 'Warning' | 'Error';

export const TITLES = ['Dr.', 'Mr.', 'Ms.', 'Mrs.'] as const;

/** Plain words and colour for a credential's expiry stage ("Expires in 5 days"). */
export function credentialExpiry(
  stage: CredentialExpiryStage,
  daysToExpiry: number | null,
): { label: string; tone: 'danger' | 'warning' | 'success' | 'informative' } {
  if (stage === 'Expired' || (daysToExpiry !== null && daysToExpiry < 0)) {
    return {
      label:
        daysToExpiry !== null && daysToExpiry < 0
          ? `Expired ${-daysToExpiry} day${daysToExpiry === -1 ? '' : 's'} ago`
          : 'Expired',
      tone: 'danger',
    };
  }
  if (daysToExpiry === null) {
    return { label: 'No expiry', tone: 'informative' };
  }
  const label = daysToExpiry === 0 ? 'Expires today' : `Expires in ${daysToExpiry} day${daysToExpiry === 1 ? '' : 's'}`;
  if (stage === 'DueIn7Days' || daysToExpiry <= 7) {
    return { label, tone: 'danger' };
  }
  if (stage === 'DueIn30Days' || daysToExpiry <= 30) {
    return { label, tone: 'warning' };
  }
  return { label: 'Valid', tone: 'success' };
}
