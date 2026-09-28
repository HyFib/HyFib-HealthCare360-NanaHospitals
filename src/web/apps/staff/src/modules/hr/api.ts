import type { Address, Gender, PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type {
  ApprovalStatus,
  AttendanceStatus,
  ConflictSeverity,
  CredentialExpiryStage,
  CredentialType,
  EmployeeStatus,
  EmploymentType,
  HolidayKind,
  LeaveSession,
  PayrollInputKind,
  PayrollInputSource,
  PayrollRunStatus,
  ProfessionalTaxFrequency,
  PunchDirection,
  PunchSource,
  RegularisationKind,
  RosterConflictKind,
  SalaryCalculationType,
  SalaryComponentKind,
  SalaryPaymentMode,
  SeparationReason,
  StaffCategory,
  Weekday,
} from './labels';

// ------------------------------------------------------------------ employees and masters

export interface EmployeeListItem {
  id: string;
  employeeCode: string;
  fullName: string;
  gender: Gender;
  departmentId: string | null;
  departmentName: string | null;
  designationId: string | null;
  designationName: string | null;
  employmentType: EmploymentType;
  status: EmployeeStatus;
  joiningDate: string;
  phoneMasked: string | null;
  hasUserAccount: boolean;
  biometricPin: string | null;
}

export interface EmployeeDetail {
  id: string;
  employeeCode: string;
  title: string | null;
  firstName: string;
  middleName: string | null;
  lastName: string | null;
  fullName: string;
  gender: Gender;
  dateOfBirth: string | null;
  phone: string | null;
  email: string | null;
  address: Address;
  emergencyContactName: string | null;
  emergencyContactPhone: string | null;
  departmentId: string | null;
  departmentName: string | null;
  designationId: string | null;
  designationName: string | null;
  employmentType: EmploymentType;
  status: EmployeeStatus;
  joiningDate: string;
  confirmationDate: string | null;
  leavingDate: string | null;
  separationReason: SeparationReason | null;
  separationNote: string | null;
  reportingManagerId: string | null;
  reportingManagerName: string | null;
  userId: string | null;
  practitionerId: string | null;
  biometricPin: string | null;
  defaultShiftId: string | null;
  weeklyOff: Weekday | null;
  isAttendanceExempt: boolean;
  paymentMode: SalaryPaymentMode;
  bankName: string | null;
  bankAccountMasked: string | null;
  bankIfscMasked: string | null;
  panMasked: string | null;
  uan: string | null;
  pfNumber: string | null;
  esiNumber: string | null;
  photoFileId: string | null;
  notes: string | null;
  version: number;
}

export interface BankDetailsInput {
  bankName: string | null;
  accountNumber: string;
  ifsc: string;
}

export interface SaveEmployeeRequest {
  title: string | null;
  firstName: string;
  middleName: string | null;
  lastName: string | null;
  gender: Gender;
  dateOfBirth: string | null;
  phone: string | null;
  email: string | null;
  address: Address | null;
  emergencyContactName: string | null;
  emergencyContactPhone: string | null;
  departmentId: string | null;
  designationId: string | null;
  employmentType: EmploymentType;
  status: EmployeeStatus;
  joiningDate: string;
  confirmationDate: string | null;
  reportingManagerId: string | null;
  userId: string | null;
  practitionerId: string | null;
  biometricPin: string | null;
  defaultShiftId: string | null;
  weeklyOff: Weekday | null;
  isAttendanceExempt: boolean;
  paymentMode: SalaryPaymentMode;
  /** null keeps the stored PAN, "" removes it. */
  pan: string | null;
  uan: string | null;
  pfNumber: string | null;
  esiNumber: string | null;
  photoFileId: string | null;
  notes: string | null;
  bank: BankDetailsInput | null;
  version: number | null;
}

export interface UpdateBankDetailsRequest {
  bankName: string | null;
  accountNumber: string;
  ifsc: string;
  confirmSharedAccount: boolean;
  version: number;
}

export interface SeparateEmployeeRequest {
  lastWorkingDay: string;
  reason: SeparationReason;
  note: string | null;
  version: number;
}

export interface EmployeeCreated {
  id: string;
  employeeCode: string;
}

export interface DesignationDto {
  id: string;
  code: string;
  name: string;
  category: StaffCategory;
  grade: number;
  isActive: boolean;
  version: number;
}

export interface SaveDesignationRequest {
  code: string;
  name: string;
  category: StaffCategory;
  grade: number;
  isActive: boolean;
  version: number | null;
}

export interface CredentialDto {
  id: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  type: CredentialType;
  name: string;
  number: string;
  issuingAuthority: string | null;
  issuedOn: string | null;
  expiresOn: string | null;
  stage: CredentialExpiryStage;
  daysToExpiry: number | null;
  fileId: string | null;
  notes: string | null;
  version: number;
}

export interface SaveCredentialRequest {
  type: CredentialType;
  name: string;
  number: string;
  issuingAuthority: string | null;
  issuedOn: string | null;
  expiresOn: string | null;
  fileId: string | null;
  notes: string | null;
  version: number | null;
}

// ------------------------------------------------------------------ shifts and roster

export interface ShiftDto {
  id: string;
  code: string;
  name: string;
  startTime: string;
  endTime: string;
  breakMinutes: number;
  graceMinutes: number;
  isNightShift: boolean;
  halfDayMinutes: number;
  fullDayMinutes: number;
  overtimeThresholdMinutes: number;
  durationMinutes: number;
  color: string | null;
  isActive: boolean;
  version: number;
}

export interface SaveShiftRequest {
  code: string;
  name: string;
  startTime: string;
  endTime: string;
  breakMinutes: number;
  graceMinutes: number;
  isNightShift: boolean;
  halfDayMinutes: number | null;
  fullDayMinutes: number | null;
  overtimeThresholdMinutes: number;
  color: string | null;
  isActive: boolean;
  version: number | null;
}

export interface RosterEntryDto {
  id: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  date: string;
  shiftId: string | null;
  shiftCode: string | null;
  shiftName: string | null;
  color: string | null;
  startTime: string | null;
  endTime: string | null;
  isWeekOff: boolean;
  notes: string | null;
}

export interface RosterAssignmentInput {
  employeeId: string;
  date: string;
  shiftId: string | null;
  isWeekOff: boolean;
  notes: string | null;
}

export interface SaveRosterRequest {
  assignments: RosterAssignmentInput[];
  replaceExisting: boolean;
  acknowledgeWarnings: boolean;
}

export interface CopyRosterWeekRequest {
  sourceWeekStart: string;
  targetWeekStart: string;
  departmentId: string | null;
  acknowledgeWarnings: boolean;
}

export interface RosterConflictDto {
  employeeId: string;
  employeeName: string | null;
  date: string;
  kind: RosterConflictKind;
  severity: ConflictSeverity;
  message: string;
}

export interface RosterCheckResult {
  canSave: boolean;
  hasWarnings: boolean;
  conflicts: RosterConflictDto[];
}

export interface RosterSaveResult {
  saved: number;
  replaced: number;
  warnings: RosterConflictDto[];
}

// ------------------------------------------------------------------ biometric attendance

export interface BiometricDeviceDto {
  id: string;
  serialNumber: string;
  name: string;
  location: string | null;
  departmentId: string | null;
  model: string | null;
  allowedIpAddress: string | null;
  hasApiKey: boolean;
  isActive: boolean;
  lastPushAt: string | null;
  lastHandshakeAt: string | null;
  lastIpAddress: string | null;
  isOnline: boolean;
  version: number;
}

export interface SaveBiometricDeviceRequest {
  serialNumber: string;
  name: string;
  location: string | null;
  departmentId: string | null;
  model: string | null;
  allowedIpAddress: string | null;
  isActive: boolean;
  version: number | null;
}

export interface DeviceKeyIssued {
  deviceId: string;
  serialNumber: string;
  apiKey: string;
}

export interface PunchDto {
  id: string;
  employeeId: string | null;
  employeeName: string | null;
  pin: string;
  punchedAt: string;
  direction: PunchDirection;
  verifyMode: number | null;
  source: PunchSource;
  deviceName: string | null;
}

export interface UnmatchedPinDto {
  pin: string;
  punchCount: number;
  firstPunchAt: string;
  lastPunchAt: string;
}

export interface AttendanceDayDto {
  id: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  date: string;
  status: AttendanceStatus;
  shiftCode: string | null;
  firstIn: string | null;
  lastOut: string | null;
  workedMinutes: number;
  lateMinutes: number;
  earlyExitMinutes: number;
  overtimeMinutes: number;
  presentDays: number;
  paidLeaveDays: number;
  unpaidLeaveDays: number;
  absentDays: number;
  offDays: number;
  /** Flags enum as sent by the server: "None" or "Late, MissingPunch". */
  flags: string;
  punchCount: number;
  isLocked: boolean;
}

export interface AttendanceSummaryRow {
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  departmentName: string | null;
  recordedDays: number;
  presentDays: number;
  paidLeaveDays: number;
  unpaidLeaveDays: number;
  absentDays: number;
  offDays: number;
  lateDays: number;
  lateMinutes: number;
  overtimeMinutes: number;
  missingPunchDays: number;
}

export interface RegularisationDto {
  id: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  date: string;
  kind: RegularisationKind;
  requestedIn: string | null;
  requestedOut: string | null;
  reason: string;
  status: ApprovalStatus;
  createdAt: string;
  decidedAt: string | null;
  decisionNote: string | null;
}

export interface RequestRegularisationRequest {
  date: string;
  kind: RegularisationKind;
  requestedIn: string | null;
  requestedOut: string | null;
  reason: string;
}

// ------------------------------------------------------------------ leave

export interface LeaveTypeDto {
  id: string;
  code: string;
  name: string;
  annualQuota: number;
  carryForwardCap: number;
  isEncashable: boolean;
  isPaid: boolean;
  allowHalfDay: boolean;
  tracksBalance: boolean;
  countsNonWorkingDays: boolean;
  maxDaysPerRequest: number | null;
  documentRequiredAfterDays: number | null;
  applicableGender: Gender | null;
  isActive: boolean;
  version: number;
}

export type SaveLeaveTypeRequest = Omit<LeaveTypeDto, 'id' | 'version'> & { version: number | null };

export interface HolidayDto {
  id: string;
  date: string;
  name: string;
  kind: HolidayKind;
}

export interface LeaveBalanceDto {
  id: string | null;
  leaveTypeId: string;
  leaveTypeCode: string;
  leaveTypeName: string;
  year: number;
  opening: number;
  credited: number;
  adjusted: number;
  availed: number;
  encashed: number;
  closing: number;
  pending: number;
  available: number;
}

export interface CreditLeaveResult {
  employees: number;
  balancesCreated: number;
}

export interface AdjustLeaveBalanceRequest {
  employeeId: string;
  leaveTypeId: string;
  year: number;
  days: number;
  reason: string;
}

export interface ApplyLeaveRequest {
  leaveTypeId: string;
  fromDate: string;
  fromSession: LeaveSession;
  toDate: string;
  toSession: LeaveSession;
  reason: string;
  attachmentFileId: string | null;
}

export interface LeaveRequestDto {
  id: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  leaveTypeId: string;
  leaveTypeCode: string;
  fromDate: string;
  fromSession: LeaveSession;
  toDate: string;
  toSession: LeaveSession;
  days: number;
  reason: string;
  status: ApprovalStatus;
  appliedAt: string;
  decidedAt: string | null;
  decisionNote: string | null;
  attachmentFileId: string | null;
}

// ------------------------------------------------------------------ payroll

export interface SalaryComponentDto {
  id: string;
  code: string;
  name: string;
  kind: SalaryComponentKind;
  calculationType: SalaryCalculationType;
  defaultValue: number;
  isTaxable: boolean;
  isStatutory: boolean;
  sortOrder: number;
  isActive: boolean;
  version: number;
}

export interface SaveSalaryComponentRequest {
  code: string;
  name: string;
  kind: SalaryComponentKind;
  calculationType: SalaryCalculationType;
  defaultValue: number;
  isTaxable: boolean;
  sortOrder: number;
  isActive: boolean;
  version: number | null;
}

export interface ProfessionalTaxBandDto {
  incomeFrom: number;
  incomeTo: number | null;
  amount: number;
  februaryAmount: number | null;
}

export interface ProfessionalTaxScheduleDto {
  stateCode: string;
  stateName: string;
  frequency: ProfessionalTaxFrequency;
  deductionMonths: number[];
  bands: ProfessionalTaxBandDto[];
}

export interface SaveProfessionalTaxRequest {
  stateName: string;
  frequency: ProfessionalTaxFrequency;
  deductionMonths: number[];
  bands: ProfessionalTaxBandDto[];
}

export interface SalaryStructureLineInput {
  salaryComponentId: string;
  value: number;
}

export interface SaveSalaryStructureRequest {
  effectiveFrom: string;
  annualCtc: number | null;
  monthlyTds: number;
  pfApplicable: boolean;
  pfOnActualBasic: boolean;
  esiApplicable: boolean;
  professionalTaxState: string | null;
  notes: string | null;
  lines: SalaryStructureLineInput[];
}

export interface SalaryStructureLineDto {
  salaryComponentId: string;
  code: string;
  name: string;
  kind: SalaryComponentKind;
  calculationType: SalaryCalculationType;
  value: number;
  monthlyAmount: number;
}

export interface SalaryStructureDto {
  id: string;
  employeeId: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  annualCtc: number | null;
  monthlyTds: number;
  pfApplicable: boolean;
  pfOnActualBasic: boolean;
  esiApplicable: boolean;
  professionalTaxState: string | null;
  notes: string | null;
  monthlyGross: number;
  lines: SalaryStructureLineDto[];
}

export interface PayrollRunDto {
  id: string;
  year: number;
  month: number;
  status: PayrollRunStatus;
  employeeCount: number;
  totalGross: number;
  totalDeductions: number;
  totalNet: number;
  processedAt: string | null;
  approvedAt: string | null;
  paidOn: string | null;
  paymentReference: string | null;
  lockedAt: string | null;
  notes: string | null;
  payslipsWithErrors: number;
  version: number;
}

export interface PayrollProcessResult {
  payslips: number;
  skipped: number;
  payslipsWithErrors: number;
  messages: string[];
}

export interface PayrollInputDto {
  id: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  kind: PayrollInputKind;
  componentCode: string;
  description: string;
  amount: number;
  source: PayrollInputSource;
  sourceReference: string | null;
}

export interface SavePayrollInputRequest {
  employeeId: string;
  kind: PayrollInputKind;
  componentCode: string | null;
  description: string;
  amount: number;
}

export interface PayslipListItem {
  id: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  departmentName: string | null;
  payableDays: number;
  lopDays: number;
  grossEarnings: number;
  totalDeductions: number;
  netPay: number;
  hasErrors: boolean;
}

export interface PayslipLineDto {
  code: string;
  name: string;
  fullAmount: number;
  amount: number;
  isStatutory: boolean;
}

export interface PayslipDto {
  id: string;
  payrollRunId: string;
  runStatus: PayrollRunStatus;
  year: number;
  month: number;
  facilityName: string | null;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  designationName: string | null;
  departmentName: string | null;
  panMasked: string | null;
  uan: string | null;
  daysInMonth: number;
  payableDays: number;
  lopDays: number;
  missingAttendanceDays: number;
  earnings: PayslipLineDto[];
  deductions: PayslipLineDto[];
  grossEarnings: number;
  totalDeductions: number;
  netPay: number;
  pfWage: number;
  employerPf: number;
  esiWage: number;
  employerEsi: number;
  paymentMode: SalaryPaymentMode;
  bankAccountMasked: string | null;
  warnings: string[];
  hasErrors: boolean;
}

// ------------------------------------------------------------------ self-service

export interface MyProfileDto {
  employeeId: string;
  employeeCode: string;
  fullName: string;
  designationName: string | null;
  departmentName: string | null;
  employmentType: EmploymentType;
  status: EmployeeStatus;
  joiningDate: string;
  phone: string | null;
  email: string | null;
  reportingManagerName: string | null;
  biometricPin: string | null;
  paymentMode: SalaryPaymentMode;
  bankName: string | null;
  bankAccountMasked: string | null;
  bankIfscMasked: string | null;
  panMasked: string | null;
  uan: string | null;
  pfNumber: string | null;
  esiNumber: string | null;
  reportees: number;
}

export interface MyPayslipItem {
  id: string;
  year: number;
  month: number;
  grossEarnings: number;
  totalDeductions: number;
  netPay: number;
  status: PayrollRunStatus;
}

// ------------------------------------------------------------------ queries

export interface EmployeeFilters {
  search?: string;
  page?: number;
  departmentId?: string;
  designationId?: string;
  status?: EmployeeStatus;
  employmentType?: EmploymentType;
  includeSeparated?: boolean;
  sort?: string;
  desc?: boolean;
}

export interface CredentialFilters {
  search?: string;
  page?: number;
  expiringWithinDays?: number;
  type?: CredentialType;
  departmentId?: string;
}

export interface AttendanceFilters {
  from?: string;
  to?: string;
  employeeId?: string;
  departmentId?: string;
  status?: AttendanceStatus;
  page?: number;
  pageSize?: number;
}

export interface LeaveRequestFilters {
  status?: ApprovalStatus;
  employeeId?: string;
  from?: string;
  to?: string;
  page?: number;
  pageSize?: number;
}

export const hrKeys = {
  all: ['hr'] as const,
  employees: ['hr', 'employees'] as const,
  masters: ['hr', 'masters'] as const,
  credentials: ['hr', 'credentials'] as const,
  roster: ['hr', 'roster'] as const,
  devices: ['hr', 'devices'] as const,
  attendance: ['hr', 'attendance'] as const,
  leave: ['hr', 'leave'] as const,
  payroll: ['hr', 'payroll'] as const,
  me: ['hr', 'me'] as const,
};

const MASTER_STALE = 5 * 60_000;

export const hrQueries = {
  employees: (filters: EmployeeFilters, pageSize = 25) =>
    queryOptions({
      queryKey: ['hr', 'employees', 'list', filters, pageSize],
      queryFn: ({ signal }) => api.get<PagedResult<EmployeeListItem>>('/api/v1/hr/employees', { query: { ...filters, pageSize }, signal }),
      placeholderData: keepPreviousData,
    }),
  employee: (id: string) =>
    queryOptions({
      queryKey: ['hr', 'employees', 'detail', id],
      queryFn: ({ signal }) => api.get<EmployeeDetail>(`/api/v1/hr/employees/${id}`, { signal }),
    }),
  designations: (includeInactive = false) =>
    queryOptions({
      queryKey: ['hr', 'masters', 'designations', includeInactive],
      queryFn: ({ signal }) => api.get<DesignationDto[]>('/api/v1/hr/designations', { query: { includeInactive }, signal }),
      staleTime: MASTER_STALE,
    }),
  credentials: (filters: CredentialFilters) =>
    queryOptions({
      queryKey: ['hr', 'credentials', 'list', filters],
      queryFn: ({ signal }) => api.get<PagedResult<CredentialDto>>('/api/v1/hr/credentials', { query: { ...filters, pageSize: 25 }, signal }),
      placeholderData: keepPreviousData,
    }),
  employeeCredentials: (employeeId: string) =>
    queryOptions({
      queryKey: ['hr', 'credentials', 'employee', employeeId],
      queryFn: ({ signal }) => api.get<CredentialDto[]>(`/api/v1/hr/employees/${employeeId}/credentials`, { signal }),
    }),
  shifts: (includeInactive = false) =>
    queryOptions({
      queryKey: ['hr', 'masters', 'shifts', includeInactive],
      queryFn: ({ signal }) => api.get<ShiftDto[]>('/api/v1/hr/shifts', { query: { includeInactive }, signal }),
      staleTime: MASTER_STALE,
    }),
  roster: (from: string, to: string, departmentId?: string | null, employeeId?: string | null) =>
    queryOptions({
      queryKey: ['hr', 'roster', from, to, departmentId ?? 'all', employeeId ?? 'all'],
      queryFn: ({ signal }) => api.get<RosterEntryDto[]>('/api/v1/hr/roster', { query: { from, to, departmentId, employeeId }, signal }),
    }),
  devices: () =>
    queryOptions({
      queryKey: ['hr', 'devices'],
      queryFn: ({ signal }) => api.get<BiometricDeviceDto[]>('/api/v1/hr/biometric-devices', { signal }),
      refetchInterval: 60_000,
    }),
  attendance: (filters: AttendanceFilters) =>
    queryOptions({
      queryKey: ['hr', 'attendance', 'days', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<AttendanceDayDto>>('/api/v1/hr/attendance', { query: { ...filters, pageSize: filters.pageSize ?? 50 }, signal }),
      placeholderData: keepPreviousData,
    }),
  attendanceSummary: (year: number, month: number, departmentId?: string | null) =>
    queryOptions({
      queryKey: ['hr', 'attendance', 'summary', year, month, departmentId ?? 'all'],
      queryFn: ({ signal }) => api.get<AttendanceSummaryRow[]>('/api/v1/hr/attendance/summary', { query: { year, month, departmentId }, signal }),
    }),
  punches: (from: string, to: string, employeeId?: string | null) =>
    queryOptions({
      queryKey: ['hr', 'attendance', 'punches', from, to, employeeId ?? 'all'],
      queryFn: ({ signal }) => api.get<PunchDto[]>('/api/v1/hr/attendance/punches', { query: { from, to, employeeId }, signal }),
    }),
  unmatchedPins: () =>
    queryOptions({
      queryKey: ['hr', 'attendance', 'unmatched-pins'],
      queryFn: ({ signal }) => api.get<UnmatchedPinDto[]>('/api/v1/hr/attendance/unmatched-pins', { signal }),
    }),
  regularisations: (status: ApprovalStatus | undefined, page = 1, pageSize = 25) =>
    queryOptions({
      queryKey: ['hr', 'attendance', 'regularisations', status ?? 'all', page, pageSize],
      queryFn: ({ signal }) =>
        api.get<PagedResult<RegularisationDto>>('/api/v1/hr/attendance/regularisations', { query: { status, page, pageSize }, signal }),
      placeholderData: keepPreviousData,
    }),
  leaveTypes: (includeInactive = false) =>
    queryOptions({
      queryKey: ['hr', 'masters', 'leave-types', includeInactive],
      queryFn: ({ signal }) => api.get<LeaveTypeDto[]>('/api/v1/hr/leave/types', { query: { includeInactive }, signal }),
      staleTime: MASTER_STALE,
    }),
  holidays: (year: number) =>
    queryOptions({
      queryKey: ['hr', 'masters', 'holidays', year],
      queryFn: ({ signal }) => api.get<HolidayDto[]>('/api/v1/hr/holidays', { query: { year }, signal }),
      staleTime: MASTER_STALE,
    }),
  leaveBalances: (employeeId: string, year: number) =>
    queryOptions({
      queryKey: ['hr', 'leave', 'balances', employeeId, year],
      queryFn: ({ signal }) => api.get<LeaveBalanceDto[]>('/api/v1/hr/leave/balances', { query: { employeeId, year }, signal }),
    }),
  leaveRequests: (filters: LeaveRequestFilters) =>
    queryOptions({
      queryKey: ['hr', 'leave', 'requests', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<LeaveRequestDto>>('/api/v1/hr/leave/requests', { query: { ...filters, pageSize: filters.pageSize ?? 25 }, signal }),
      placeholderData: keepPreviousData,
    }),
  components: () =>
    queryOptions({
      queryKey: ['hr', 'payroll', 'components'],
      queryFn: ({ signal }) => api.get<SalaryComponentDto[]>('/api/v1/hr/payroll/components', { signal }),
      staleTime: MASTER_STALE,
    }),
  professionalTax: () =>
    queryOptions({
      queryKey: ['hr', 'payroll', 'professional-tax'],
      queryFn: ({ signal }) => api.get<ProfessionalTaxScheduleDto[]>('/api/v1/hr/payroll/professional-tax', { signal }),
      staleTime: MASTER_STALE,
    }),
  salaryStructures: (employeeId: string) =>
    queryOptions({
      queryKey: ['hr', 'payroll', 'structures', employeeId],
      queryFn: ({ signal }) => api.get<SalaryStructureDto[]>(`/api/v1/hr/employees/${employeeId}/salary-structures`, { signal }),
    }),
  payrollRuns: (year: number | undefined, page = 1, pageSize = 25) =>
    queryOptions({
      queryKey: ['hr', 'payroll', 'runs', year ?? 'all', page, pageSize],
      queryFn: ({ signal }) => api.get<PagedResult<PayrollRunDto>>('/api/v1/hr/payroll/runs', { query: { year, page, pageSize }, signal }),
      placeholderData: keepPreviousData,
    }),
  payrollRun: (id: string) =>
    queryOptions({
      queryKey: ['hr', 'payroll', 'run', id],
      queryFn: ({ signal }) => api.get<PayrollRunDto>(`/api/v1/hr/payroll/runs/${id}`, { signal }),
    }),
  payrollInputs: (runId: string) =>
    queryOptions({
      queryKey: ['hr', 'payroll', 'inputs', runId],
      queryFn: ({ signal }) => api.get<PayrollInputDto[]>(`/api/v1/hr/payroll/runs/${runId}/inputs`, { signal }),
    }),
  payslips: (runId: string, filters: { search?: string; errorsOnly?: boolean; page?: number; pageSize?: number }) =>
    queryOptions({
      queryKey: ['hr', 'payroll', 'payslips', runId, filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<PayslipListItem>>(`/api/v1/hr/payroll/runs/${runId}/payslips`, {
          query: { ...filters, pageSize: filters.pageSize ?? 50 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  payslip: (id: string) =>
    queryOptions({
      queryKey: ['hr', 'payroll', 'payslip', id],
      queryFn: ({ signal }) => api.get<PayslipDto>(`/api/v1/hr/payroll/payslips/${id}`, { signal }),
    }),

  myProfile: () =>
    queryOptions({
      queryKey: ['hr', 'me', 'profile'],
      queryFn: ({ signal }) => api.get<MyProfileDto>('/api/v1/hr/me/profile', { signal }),
      staleTime: 5 * 60_000,
    }),
  myRoster: (from: string, to: string) =>
    queryOptions({
      queryKey: ['hr', 'me', 'roster', from, to],
      queryFn: ({ signal }) => api.get<RosterEntryDto[]>('/api/v1/hr/me/roster', { query: { from, to }, signal }),
    }),
  myAttendance: (from: string, to: string) =>
    queryOptions({
      queryKey: ['hr', 'me', 'attendance', from, to],
      queryFn: ({ signal }) => api.get<AttendanceDayDto[]>('/api/v1/hr/me/attendance', { query: { from, to }, signal }),
    }),
  myCredentials: () =>
    queryOptions({
      queryKey: ['hr', 'me', 'credentials'],
      queryFn: ({ signal }) => api.get<CredentialDto[]>('/api/v1/hr/me/credentials', { signal }),
    }),
  myBalances: (year: number) =>
    queryOptions({
      queryKey: ['hr', 'me', 'balances', year],
      queryFn: ({ signal }) => api.get<LeaveBalanceDto[]>('/api/v1/hr/me/leave/balances', { query: { year }, signal }),
    }),
  myLeaveRequests: () =>
    queryOptions({
      queryKey: ['hr', 'me', 'leave-requests'],
      queryFn: ({ signal }) => api.get<LeaveRequestDto[]>('/api/v1/hr/me/leave/requests', { signal }),
    }),
  myRegularisations: () =>
    queryOptions({
      queryKey: ['hr', 'me', 'regularisations'],
      queryFn: ({ signal }) => api.get<RegularisationDto[]>('/api/v1/hr/me/attendance/regularisations', { signal }),
    }),
  myPayslips: () =>
    queryOptions({
      queryKey: ['hr', 'me', 'payslips'],
      queryFn: ({ signal }) => api.get<MyPayslipItem[]>('/api/v1/hr/me/payslips', { signal }),
    }),
  myPayslip: (id: string) =>
    queryOptions({
      queryKey: ['hr', 'me', 'payslip', id],
      queryFn: ({ signal }) => api.get<PayslipDto>(`/api/v1/hr/me/payslips/${id}`, { signal }),
    }),
  teamLeave: (status?: ApprovalStatus) =>
    queryOptions({
      queryKey: ['hr', 'me', 'team-leave', status ?? 'all'],
      queryFn: ({ signal }) => api.get<LeaveRequestDto[]>('/api/v1/hr/me/team/leave-requests', { query: { status }, signal }),
    }),
  teamRegularisations: (status?: ApprovalStatus) =>
    queryOptions({
      queryKey: ['hr', 'me', 'team-regularisations', status ?? 'all'],
      queryFn: ({ signal }) => api.get<RegularisationDto[]>('/api/v1/hr/me/team/regularisations', { query: { status }, signal }),
    }),
};

// ------------------------------------------------------------------ commands

export const hrApi = {
  createEmployee: (request: SaveEmployeeRequest) => api.post<EmployeeCreated>('/api/v1/hr/employees', request),
  updateEmployee: (id: string, request: SaveEmployeeRequest) => api.put<void>(`/api/v1/hr/employees/${id}`, request),
  updateBankDetails: (id: string, request: UpdateBankDetailsRequest) => api.put<void>(`/api/v1/hr/employees/${id}/bank-details`, request),
  separate: (id: string, request: SeparateEmployeeRequest) => api.post<void>(`/api/v1/hr/employees/${id}/separate`, request),
  saveDesignation: (id: string | null, request: SaveDesignationRequest) =>
    id ? api.put<void>(`/api/v1/hr/designations/${id}`, request) : api.post<unknown>('/api/v1/hr/designations', request),

  saveCredential: (employeeId: string, id: string | null, request: SaveCredentialRequest) =>
    id ? api.put<void>(`/api/v1/hr/credentials/${id}`, request) : api.post<unknown>(`/api/v1/hr/employees/${employeeId}/credentials`, request),
  deleteCredential: (id: string) => api.delete<void>(`/api/v1/hr/credentials/${id}`),

  saveShift: (id: string | null, request: SaveShiftRequest) =>
    id ? api.put<void>(`/api/v1/hr/shifts/${id}`, request) : api.post<unknown>('/api/v1/hr/shifts', request),
  checkRoster: (request: SaveRosterRequest) => api.post<RosterCheckResult>('/api/v1/hr/roster/check', request),
  saveRoster: (request: SaveRosterRequest) => api.put<RosterSaveResult>('/api/v1/hr/roster', request),
  copyRosterWeek: (request: CopyRosterWeekRequest) => api.post<RosterSaveResult>('/api/v1/hr/roster/copy-week', request),
  deleteRosterEntry: (id: string) => api.delete<void>(`/api/v1/hr/roster/${id}`),

  registerDevice: (request: SaveBiometricDeviceRequest) => api.post<DeviceKeyIssued>('/api/v1/hr/biometric-devices', request),
  updateDevice: (id: string, request: SaveBiometricDeviceRequest) => api.put<void>(`/api/v1/hr/biometric-devices/${id}`, request),
  rotateDeviceKey: (id: string) => api.post<DeviceKeyIssued>(`/api/v1/hr/biometric-devices/${id}/rotate-key`),

  computeAttendance: (from: string, to: string, employeeId?: string | null) =>
    api.post<{ employeeDays: number }>('/api/v1/hr/attendance/compute', { from, to, employeeId: employeeId ?? null }),
  decideRegularisation: (id: string, approve: boolean, note: string | null) =>
    api.post<void>(`/api/v1/hr/attendance/regularisations/${id}/${approve ? 'approve' : 'reject'}`, { note }),
  requestRegularisation: (employeeId: string, request: RequestRegularisationRequest) =>
    api.post<string>(`/api/v1/hr/employees/${employeeId}/regularisations`, request),

  saveLeaveType: (id: string | null, request: SaveLeaveTypeRequest) =>
    id ? api.put<void>(`/api/v1/hr/leave/types/${id}`, request) : api.post<unknown>('/api/v1/hr/leave/types', request),
  addHoliday: (request: { date: string; name: string; kind: HolidayKind }) => api.post<unknown>('/api/v1/hr/holidays', request),
  deleteHoliday: (id: string) => api.delete<void>(`/api/v1/hr/holidays/${id}`),
  creditLeave: (year: number) => api.post<CreditLeaveResult>('/api/v1/hr/leave/balances/credit', { year }),
  adjustLeave: (request: AdjustLeaveBalanceRequest) => api.post<void>('/api/v1/hr/leave/balances/adjust', request),
  applyLeave: (employeeId: string, request: ApplyLeaveRequest) => api.post<string>(`/api/v1/hr/employees/${employeeId}/leave-requests`, request),
  decideLeave: (id: string, approve: boolean, note: string | null) =>
    api.post<void>(`/api/v1/hr/leave/requests/${id}/${approve ? 'approve' : 'reject'}`, { note }),
  cancelLeave: (id: string, reason: string) => api.post<void>(`/api/v1/hr/leave/requests/${id}/cancel`, { reason }),

  saveComponent: (id: string | null, request: SaveSalaryComponentRequest) =>
    id ? api.put<void>(`/api/v1/hr/payroll/components/${id}`, request) : api.post<unknown>('/api/v1/hr/payroll/components', request),
  saveProfessionalTax: (stateCode: string, request: SaveProfessionalTaxRequest) =>
    api.put<void>(`/api/v1/hr/payroll/professional-tax/${encodeURIComponent(stateCode)}`, request),
  createSalaryStructure: (employeeId: string, request: SaveSalaryStructureRequest) =>
    api.post<unknown>(`/api/v1/hr/employees/${employeeId}/salary-structures`, request),

  createRun: (request: { year: number; month: number; notes: string | null }) => api.post<string>('/api/v1/hr/payroll/runs', request),
  deleteRun: (id: string) => api.delete<void>(`/api/v1/hr/payroll/runs/${id}`),
  processRun: (id: string) => api.post<PayrollProcessResult>(`/api/v1/hr/payroll/runs/${id}/process`),
  approveRun: (id: string) => api.post<void>(`/api/v1/hr/payroll/runs/${id}/approve`),
  reopenRun: (id: string, reason: string) => api.post<void>(`/api/v1/hr/payroll/runs/${id}/reopen`, { reason }),
  markRunPaid: (id: string, paidOn: string, reference: string) => api.post<void>(`/api/v1/hr/payroll/runs/${id}/mark-paid`, { paidOn, reference }),
  lockRun: (id: string) => api.post<void>(`/api/v1/hr/payroll/runs/${id}/lock`),
  bankTransferFile: (id: string) => api.blob(`/api/v1/hr/payroll/runs/${id}/bank-transfer.csv`, { timeoutMs: 120_000 }),
  addInput: (runId: string, request: SavePayrollInputRequest) => api.post<string>(`/api/v1/hr/payroll/runs/${runId}/inputs`, request),
  importInputs: (runId: string, csv: string) => api.post<{ imported: number }>(`/api/v1/hr/payroll/runs/${runId}/inputs/import`, { csv }),
  deleteInput: (inputId: string) => api.delete<void>(`/api/v1/hr/payroll/inputs/${inputId}`),

  myApplyLeave: (request: ApplyLeaveRequest) => api.post<string>('/api/v1/hr/me/leave/requests', request),
  myCancelLeave: (id: string, reason: string) => api.post<void>(`/api/v1/hr/me/leave/requests/${id}/cancel`, { reason }),
  myRequestRegularisation: (request: RequestRegularisationRequest) => api.post<string>('/api/v1/hr/me/attendance/regularisations', request),
  myCancelRegularisation: (id: string) => api.post<void>(`/api/v1/hr/me/attendance/regularisations/${id}/cancel`),
  teamDecideLeave: (id: string, approve: boolean, note: string | null) =>
    api.post<void>(`/api/v1/hr/me/team/leave-requests/${id}/${approve ? 'approve' : 'reject'}`, { note }),
  teamDecideRegularisation: (id: string, approve: boolean, note: string | null) =>
    api.post<void>(`/api/v1/hr/me/team/regularisations/${id}/${approve ? 'approve' : 'reject'}`, { note }),
};
