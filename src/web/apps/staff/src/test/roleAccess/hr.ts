import type {
  AttendanceDayDto,
  AttendanceSummaryRow,
  AttendanceTerminalReading,
  BiometricDeviceDto,
  CredentialDto,
  DesignationDto,
  EmployeeDetail,
  EmployeeListItem,
  HolidayDto,
  LeaveBalanceDto,
  LeaveRequestDto,
  LeaveTypeDto,
  MyPayslipItem,
  MyProfileDto,
  PayrollInputDto,
  PayrollRunDto,
  PayslipDto,
  PayslipListItem,
  ProfessionalTaxScheduleDto,
  PunchDto,
  RegularisationDto,
  RosterEntryDto,
  SalaryComponentDto,
  SalaryStructureDto,
  ShiftDto,
  UnmatchedPinDto,
} from '@/modules/hr/api';
import { paged, STAFF_USER, type FakeAnswers, type FakeRequest } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Human resources: Staff Nurse Lalitha Menon's record opened as RECORD_ID; staff of each kind; designations and
// shifts; credentials expiring, due and valid; this week's roster; attendance with a missed punch; leave and
// regularisation requests waiting for approval; biometric terminals (one online, one with a failed pull); payroll runs in
// each state that has its own action (RECORD_ID is this month's, processed: to approve), with inputs and payslips; and
// the signed-in user's own self-service page.

const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const dateIn = (days: number) => new Date(now + days * 86_400_000).toISOString().slice(0, 10);
const today = dateIn(0);
const id = (suffix: string) => `0192a000-0000-7000-8000-0000000079${suffix}`;
const year = new Date(now).getFullYear();
const month = new Date(now).getMonth() + 1;
const NURSING = { id: '0192a000-0000-7000-8000-00000000de09', name: 'Nursing' };
const GM = { id: '0192a000-0000-7000-8000-00000000de01', name: 'General medicine' };

// ------------------------------------------------------------------ masters

const STAFF_NURSE = id('d1');
const CONSULTANT = id('d2');
const RECEPTIONIST = id('d3');

const DESIGNATIONS: DesignationDto[] = [
  { id: STAFF_NURSE, code: 'SN', name: 'Staff Nurse', category: 'Nursing', grade: 4, isActive: true, version: 1 },
  { id: CONSULTANT, code: 'CONS', name: 'Consultant', category: 'Medical', grade: 8, isActive: true, version: 1 },
  {
    id: RECEPTIONIST,
    code: 'REC',
    name: 'Front office executive',
    category: 'Administrative',
    grade: 2,
    isActive: true,
    version: 2,
  },
];

const MORNING = id('30');
const NIGHT = id('31');
const shift = (
  overrides: Partial<ShiftDto> & Pick<ShiftDto, 'id' | 'code' | 'name' | 'startTime' | 'endTime'>,
): ShiftDto => ({
  breakMinutes: 30,
  graceMinutes: 10,
  isNightShift: false,
  halfDayMinutes: 240,
  fullDayMinutes: 420,
  overtimeThresholdMinutes: 30,
  durationMinutes: 480,
  color: '#0A7CA7',
  isActive: true,
  version: 1,
  ...overrides,
});

const SHIFTS: ShiftDto[] = [
  shift({
    id: MORNING,
    code: 'M',
    name: 'Morning',
    startTime: '08:00:00',
    endTime: '14:00:00',
    durationMinutes: 360,
    fullDayMinutes: 330,
  }),
  shift({
    id: id('32'),
    code: 'E',
    name: 'Evening',
    startTime: '14:00:00',
    endTime: '20:00:00',
    durationMinutes: 360,
    fullDayMinutes: 330,
    color: '#8764B8',
  }),
  shift({
    id: NIGHT,
    code: 'N',
    name: 'Night',
    startTime: '20:00:00',
    endTime: '08:00:00',
    isNightShift: true,
    durationMinutes: 720,
    fullDayMinutes: 660,
    color: '#3B3A39',
  }),
];

// ------------------------------------------------------------------ employees

const employee = (
  overrides: Partial<EmployeeListItem> & Pick<EmployeeListItem, 'id' | 'employeeCode' | 'fullName'>,
): EmployeeListItem => ({
  gender: 'Female',
  departmentId: NURSING.id,
  departmentName: NURSING.name,
  designationId: STAFF_NURSE,
  designationName: 'Staff Nurse',
  employmentType: 'Permanent',
  status: 'Active',
  joiningDate: dateIn(-900),
  phoneMasked: '98******31',
  hasUserAccount: true,
  biometricPin: '1042',
  ...overrides,
});

const EMPLOYEES: EmployeeListItem[] = [
  employee({ id: RECORD_ID, employeeCode: 'NH0412', fullName: 'Lalitha Menon' }),
  employee({
    id: id('e2'),
    employeeCode: 'NH0108',
    fullName: 'Dr. Meera Iyer',
    departmentId: GM.id,
    departmentName: GM.name,
    designationId: CONSULTANT,
    designationName: 'Consultant',
    biometricPin: '1008',
    joiningDate: dateIn(-2400),
  }),
  employee({
    id: id('e3'),
    employeeCode: 'NH0533',
    fullName: 'Kavita Pawar',
    status: 'Probation',
    employmentType: 'Contract',
    joiningDate: dateIn(-60),
    biometricPin: '1133',
  }),
  employee({
    id: id('e4'),
    employeeCode: 'NH0287',
    fullName: 'Pooja Nair',
    departmentId: null,
    departmentName: null,
    designationId: RECEPTIONIST,
    designationName: 'Front office executive',
    status: 'OnNotice',
    biometricPin: null,
    hasUserAccount: true,
  }),
];

const EMPLOYEE: EmployeeDetail = {
  id: RECORD_ID,
  employeeCode: 'NH0412',
  title: 'Ms.',
  firstName: 'Lalitha',
  middleName: null,
  lastName: 'Menon',
  fullName: 'Lalitha Menon',
  gender: 'Female',
  dateOfBirth: '1991-06-18',
  phone: '+919850112231',
  email: 'lalitha.menon@nanahospitals.in',
  address: {
    line1: '7, Sai Residency',
    line2: 'Baner Road',
    city: 'Pune',
    state: 'Maharashtra',
    pinCode: '411045',
    country: 'India',
  },
  emergencyContactName: 'Suresh Menon',
  emergencyContactPhone: '+919850112232',
  departmentId: NURSING.id,
  departmentName: NURSING.name,
  designationId: STAFF_NURSE,
  designationName: 'Staff Nurse',
  employmentType: 'Permanent',
  status: 'Active',
  joiningDate: dateIn(-900),
  confirmationDate: dateIn(-720),
  leavingDate: null,
  separationReason: null,
  separationNote: null,
  reportingManagerId: id('e5'),
  reportingManagerName: 'Sr. Grace D’Souza',
  userId: STAFF_USER.id,
  practitionerId: null,
  biometricPin: '1042',
  defaultShiftId: MORNING,
  weeklyOff: 'Sunday',
  isAttendanceExempt: false,
  paymentMode: 'BankTransfer',
  bankName: 'State Bank of India',
  bankAccountMasked: 'XXXXXXX4417',
  bankIfscMasked: 'SBIN000XXXX',
  panMasked: 'XXXXX1234X',
  uan: '100987654321',
  pfNumber: 'MH/PUN/0012345/000/0412',
  esiNumber: null,
  photoFileId: null,
  notes: null,
  version: 7,
};

const credential = (
  overrides: Partial<CredentialDto> & Pick<CredentialDto, 'id' | 'type' | 'name' | 'number' | 'stage'>,
): CredentialDto => ({
  employeeId: RECORD_ID,
  employeeCode: 'NH0412',
  employeeName: 'Lalitha Menon',
  issuingAuthority: 'Maharashtra Nursing Council',
  issuedOn: dateIn(-1800),
  expiresOn: null,
  daysToExpiry: null,
  fileId: null,
  notes: null,
  version: 1,
  ...overrides,
});

const CREDENTIALS: CredentialDto[] = [
  credential({
    id: id('c1'),
    type: 'NursingCouncil',
    name: 'Nursing council registration',
    number: 'MNC/2014/55120',
    stage: 'DueIn30Days',
    expiresOn: dateIn(21),
    daysToExpiry: 21,
  }),
  credential({
    id: id('c2'),
    type: 'BLS',
    name: 'Basic life support',
    number: 'AHA-BLS-88213',
    issuingAuthority: 'American Heart Association',
    stage: 'Expired',
    issuedOn: dateIn(-740),
    expiresOn: dateIn(-10),
    daysToExpiry: -10,
  }),
  credential({
    id: id('c3'),
    employeeId: id('e2'),
    employeeCode: 'NH0108',
    employeeName: 'Dr. Meera Iyer',
    type: 'MedicalCouncilRegistration',
    name: 'Medical council registration',
    number: 'MMC 2009/03/0451',
    issuingAuthority: 'Maharashtra Medical Council',
    stage: 'None',
    expiresOn: dateIn(900),
    daysToExpiry: 900,
  }),
];

// ------------------------------------------------------------------ roster and attendance

const ROSTER: RosterEntryDto[] = [0, 1, 2, 3, 4, 5].map((day) => ({
  id: id(`f${day}`),
  employeeId: RECORD_ID,
  employeeCode: 'NH0412',
  employeeName: 'Lalitha Menon',
  date: dateIn(day),
  shiftId: day === 5 ? null : MORNING,
  shiftCode: day === 5 ? null : 'M',
  shiftName: day === 5 ? null : 'Morning',
  color: day === 5 ? null : '#0A7CA7',
  startTime: day === 5 ? null : '08:00:00',
  endTime: day === 5 ? null : '14:00:00',
  isWeekOff: day === 5,
  notes: null,
  version: 1,
}));

const attendanceDay = (
  overrides: Partial<AttendanceDayDto> & Pick<AttendanceDayDto, 'id' | 'date' | 'status'>,
): AttendanceDayDto => ({
  employeeId: RECORD_ID,
  employeeCode: 'NH0412',
  employeeName: 'Lalitha Menon',
  shiftCode: 'M',
  firstIn: null,
  lastOut: null,
  workedMinutes: 0,
  lateMinutes: 0,
  earlyExitMinutes: 0,
  overtimeMinutes: 0,
  presentDays: 0,
  paidLeaveDays: 0,
  unpaidLeaveDays: 0,
  absentDays: 0,
  offDays: 0,
  flags: '',
  punchCount: 0,
  isLocked: false,
  ...overrides,
});

const ATTENDANCE: AttendanceDayDto[] = [
  attendanceDay({
    id: id('a1'),
    date: dateIn(-1),
    status: 'Present',
    firstIn: `${dateIn(-1)}T08:14:00+05:30`,
    lastOut: `${dateIn(-1)}T14:06:00+05:30`,
    workedMinutes: 352,
    lateMinutes: 4,
    presentDays: 1,
    flags: 'Late',
    punchCount: 2,
  }),
  attendanceDay({
    id: id('a2'),
    date: dateIn(-1),
    status: 'Absent',
    employeeId: id('e3'),
    employeeCode: 'NH0533',
    employeeName: 'Kavita Pawar',
    firstIn: `${dateIn(-1)}T07:58:00+05:30`,
    absentDays: 1,
    flags: 'MissingPunch',
    punchCount: 1,
  }),
];

const SUMMARY: AttendanceSummaryRow[] = [
  {
    employeeId: RECORD_ID,
    employeeCode: 'NH0412',
    employeeName: 'Lalitha Menon',
    departmentName: NURSING.name,
    recordedDays: 22,
    presentDays: 20,
    paidLeaveDays: 1,
    unpaidLeaveDays: 0,
    absentDays: 0,
    offDays: 1,
    lateDays: 2,
    lateMinutes: 19,
    overtimeMinutes: 90,
    missingPunchDays: 0,
  },
];

const PUNCHES: PunchDto[] = [
  {
    id: id('b1'),
    employeeId: RECORD_ID,
    employeeName: 'Lalitha Menon',
    pin: '1042',
    punchedAt: `${dateIn(-1)}T08:14:00+05:30`,
    direction: 'In',
    verifyMode: 1,
    source: 'Adms',
    deviceName: 'Main entrance terminal',
  },
];

const UNMATCHED: UnmatchedPinDto[] = [
  { pin: '1190', punchCount: 4, firstPunchAt: minutesAgo(60 * 50), lastPunchAt: minutesAgo(60 * 4) },
];

const REGULARISATIONS: RegularisationDto[] = [
  {
    id: id('34'),
    employeeId: id('e3'),
    employeeCode: 'NH0533',
    employeeName: 'Kavita Pawar',
    date: dateIn(-1),
    kind: 'MissedPunch',
    requestedIn: null,
    requestedOut: `${dateIn(-1)}T14:05:00+05:30`,
    reason: 'Terminal did not read my finger at the end of the shift.',
    status: 'Pending',
    createdAt: minutesAgo(60 * 5),
    decidedAt: null,
    decisionNote: null,
  },
];

const DEVICES: BiometricDeviceDto[] = [
  {
    id: id('b5'),
    serialNumber: 'CQZ7232460117',
    name: 'Main entrance terminal',
    location: 'Ground floor, staff entrance',
    departmentId: null,
    model: 'eSSL X990',
    allowedIpAddress: null,
    hasApiKey: true,
    isActive: true,
    lastPushAt: minutesAgo(3),
    lastHandshakeAt: minutesAgo(1),
    lastIpAddress: '10.20.1.45',
    isOnline: true,
    version: 2,
    connection: 'Push',
    pullAddress: null,
    pullPort: 4370,
    pullEveryMinutes: 15,
    lastPullAt: null,
    lastPullError: null,
    clockOffsetSeconds: 2,
    offlineSince: null,
  },
  {
    id: id('b6'),
    serialNumber: 'AEYU201960042',
    name: 'Ward block terminal',
    location: 'First floor, nurses’ station',
    departmentId: NURSING.id,
    model: 'ZKTeco K40',
    allowedIpAddress: '10.20.2.31',
    hasApiKey: false,
    isActive: true,
    lastPushAt: null,
    lastHandshakeAt: null,
    lastIpAddress: '10.20.2.31',
    isOnline: false,
    version: 4,
    connection: 'Pull',
    pullAddress: '10.20.2.31',
    pullPort: 4370,
    pullEveryMinutes: 15,
    lastPullAt: minutesAgo(95),
    lastPullError: 'Connection timed out',
    clockOffsetSeconds: -64,
    offlineSince: minutesAgo(95),
  },
];

const READING: AttendanceTerminalReading = { provider: 'essl', problem: null };

// ------------------------------------------------------------------ leave

const LEAVE_TYPES: LeaveTypeDto[] = [
  {
    id: id('35'),
    code: 'CL',
    name: 'Casual leave',
    annualQuota: 8,
    carryForwardCap: 0,
    isEncashable: false,
    isPaid: true,
    allowHalfDay: true,
    tracksBalance: true,
    countsNonWorkingDays: false,
    maxDaysPerRequest: 3,
    documentRequiredAfterDays: null,
    applicableGender: null,
    isActive: true,
    version: 1,
  },
  {
    id: id('36'),
    code: 'EL',
    name: 'Earned leave',
    annualQuota: 18,
    carryForwardCap: 45,
    isEncashable: true,
    isPaid: true,
    allowHalfDay: false,
    tracksBalance: true,
    countsNonWorkingDays: false,
    maxDaysPerRequest: null,
    documentRequiredAfterDays: null,
    applicableGender: null,
    isActive: true,
    version: 1,
  },
];

const HOLIDAYS: HolidayDto[] = [
  { id: id('37'), date: `${year}-11-08`, name: 'Diwali (Lakshmi Puja)', kind: 'Festival' },
  { id: id('38'), date: `${year}-12-25`, name: 'Christmas', kind: 'National' },
];

const BALANCES: LeaveBalanceDto[] = [
  {
    id: id('39'),
    leaveTypeId: id('35'),
    leaveTypeCode: 'CL',
    leaveTypeName: 'Casual leave',
    year,
    opening: 0,
    credited: 8,
    adjusted: 0,
    availed: 3,
    encashed: 0,
    closing: 5,
    pending: 2,
    available: 3,
  },
];

const leave = (overrides: Partial<LeaveRequestDto> & Pick<LeaveRequestDto, 'id' | 'status'>): LeaveRequestDto => ({
  employeeId: RECORD_ID,
  employeeCode: 'NH0412',
  employeeName: 'Lalitha Menon',
  leaveTypeId: id('35'),
  leaveTypeCode: 'CL',
  fromDate: dateIn(6),
  fromSession: 'FirstHalf',
  toDate: dateIn(7),
  toSession: 'SecondHalf',
  days: 2,
  reason: 'Family function in Thrissur',
  appliedAt: minutesAgo(60 * 20),
  decidedAt: null,
  decisionNote: null,
  attachmentFileId: null,
  ...overrides,
});

const LEAVE_REQUESTS: LeaveRequestDto[] = [
  leave({ id: id('3a'), status: 'Pending' }),
  leave({
    id: id('3b'),
    status: 'Approved',
    employeeId: id('e2'),
    employeeCode: 'NH0108',
    employeeName: 'Dr. Meera Iyer',
    leaveTypeId: id('36'),
    leaveTypeCode: 'EL',
    fromDate: today,
    toDate: dateIn(2),
    days: 3,
    reason: 'Conference in Bengaluru',
    decidedAt: minutesAgo(60 * 48),
  }),
];

// ------------------------------------------------------------------ payroll

const COMPONENTS: SalaryComponentDto[] = [
  {
    id: id('3c'),
    code: 'BASIC',
    name: 'Basic pay',
    kind: 'Earning',
    calculationType: 'Fixed',
    defaultValue: 0,
    isTaxable: true,
    isStatutory: false,
    sortOrder: 1,
    isActive: true,
    version: 1,
  },
  {
    id: id('3d'),
    code: 'HRA',
    name: 'House rent allowance',
    kind: 'Earning',
    calculationType: 'PercentOfBasic',
    defaultValue: 40,
    isTaxable: true,
    isStatutory: false,
    sortOrder: 2,
    isActive: true,
    version: 1,
  },
  {
    id: id('3e'),
    code: 'PF',
    name: 'Provident fund',
    kind: 'Deduction',
    calculationType: 'PercentOfBasic',
    defaultValue: 12,
    isTaxable: false,
    isStatutory: true,
    sortOrder: 10,
    isActive: true,
    version: 1,
  },
];

const PROFESSIONAL_TAX: ProfessionalTaxScheduleDto[] = [
  {
    stateCode: 'MH',
    stateName: 'Maharashtra',
    frequency: 'Monthly',
    deductionMonths: [],
    bands: [
      { incomeFrom: 0, incomeTo: 7500, amount: 0, februaryAmount: null },
      { incomeFrom: 7501, incomeTo: 10000, amount: 175, februaryAmount: null },
      { incomeFrom: 10001, incomeTo: null, amount: 200, februaryAmount: 300 },
    ],
    version: 1,
  },
];

const STRUCTURES: SalaryStructureDto[] = [
  {
    id: id('3f'),
    employeeId: RECORD_ID,
    effectiveFrom: `${year}-04-01`,
    effectiveTo: null,
    annualCtc: 480_000,
    monthlyTds: 0,
    pfApplicable: true,
    pfOnActualBasic: false,
    esiApplicable: false,
    professionalTaxState: 'MH',
    notes: null,
    monthlyGross: 38_200,
    lines: [
      {
        salaryComponentId: id('3c'),
        code: 'BASIC',
        name: 'Basic pay',
        kind: 'Earning',
        calculationType: 'Fixed',
        value: 22_000,
        monthlyAmount: 22_000,
      },
      {
        salaryComponentId: id('3d'),
        code: 'HRA',
        name: 'House rent allowance',
        kind: 'Earning',
        calculationType: 'PercentOfBasic',
        value: 40,
        monthlyAmount: 8_800,
      },
    ],
  },
];

const run = (overrides: Partial<PayrollRunDto> & Pick<PayrollRunDto, 'id' | 'month' | 'status'>): PayrollRunDto => ({
  year,
  employeeCount: 214,
  totalGross: 8_412_500,
  totalDeductions: 1_046_300,
  totalNet: 7_366_200,
  processedAt: minutesAgo(60 * 24 * 2),
  approvedAt: null,
  paidOn: null,
  paymentReference: null,
  lockedAt: null,
  notes: null,
  payslipsWithErrors: 0,
  version: 3,
  ...overrides,
});

const RUNS: PayrollRunDto[] = [
  run({ id: RECORD_ID, month, status: 'Processed', payslipsWithErrors: 1 }),
  run({ id: id('40'), month: month - 1 || 12, status: 'Approved', approvedAt: minutesAgo(60 * 24 * 25) }),
  run({
    id: id('41'),
    month: month - 2 || 11,
    status: 'Paid',
    approvedAt: minutesAgo(60 * 24 * 55),
    paidOn: dateIn(-54),
    paymentReference: 'NEFT-SBI-0912',
  }),
];

const INPUTS: PayrollInputDto[] = [
  {
    id: id('42'),
    employeeId: RECORD_ID,
    employeeCode: 'NH0412',
    employeeName: 'Lalitha Menon',
    kind: 'Earning',
    componentCode: 'NIGHT',
    description: 'Night duty allowance, 6 nights',
    amount: 1_800,
    source: 'Manual',
    sourceReference: null,
  },
];

const PAYSLIPS: PayslipListItem[] = [
  {
    id: id('43'),
    employeeId: RECORD_ID,
    employeeCode: 'NH0412',
    employeeName: 'Lalitha Menon',
    departmentName: NURSING.name,
    payableDays: 31,
    lopDays: 0,
    grossEarnings: 40_000,
    totalDeductions: 3_040,
    netPay: 36_960,
    hasErrors: false,
  },
  {
    id: id('44'),
    employeeId: id('e3'),
    employeeCode: 'NH0533',
    employeeName: 'Kavita Pawar',
    departmentName: NURSING.name,
    payableDays: 29,
    lopDays: 2,
    grossEarnings: 24_300,
    totalDeductions: 1_916,
    netPay: 22_384,
    hasErrors: true,
  },
];

const PAYSLIP: PayslipDto = {
  id: id('43'),
  payrollRunId: RECORD_ID,
  runStatus: 'Processed',
  year,
  month,
  facilityName: 'Nana Hospitals, Pune',
  employeeId: RECORD_ID,
  employeeCode: 'NH0412',
  employeeName: 'Lalitha Menon',
  designationName: 'Staff Nurse',
  departmentName: NURSING.name,
  panMasked: 'XXXXX1234X',
  uan: '100987654321',
  daysInMonth: 31,
  payableDays: 31,
  lopDays: 0,
  missingAttendanceDays: 0,
  earnings: [
    { code: 'BASIC', name: 'Basic pay', fullAmount: 22_000, amount: 22_000, isStatutory: false },
    { code: 'HRA', name: 'House rent allowance', fullAmount: 8_800, amount: 8_800, isStatutory: false },
  ],
  deductions: [
    { code: 'PF', name: 'Provident fund', fullAmount: 2_640, amount: 2_640, isStatutory: true },
    { code: 'PT', name: 'Professional tax', fullAmount: 200, amount: 200, isStatutory: true },
  ],
  grossEarnings: 40_000,
  totalDeductions: 3_040,
  netPay: 36_960,
  pfWage: 22_000,
  employerPf: 2_640,
  esiWage: 0,
  employerEsi: 0,
  paymentMode: 'BankTransfer',
  bankAccountMasked: 'XXXXXXX4417',
  warnings: [],
  hasErrors: false,
};

// ------------------------------------------------------------------ the signed-in user's self-service

const MY_PROFILE: MyProfileDto = {
  employeeId: id('e2'),
  employeeCode: 'NH0108',
  fullName: 'Dr. Meera Iyer',
  designationName: 'Consultant',
  departmentName: GM.name,
  employmentType: 'Permanent',
  status: 'Active',
  joiningDate: dateIn(-2400),
  phone: '+919822000108',
  email: 'meera.iyer@nanahospitals.in',
  reportingManagerName: 'Dr. Sanjay Kulkarni',
  biometricPin: '1008',
  paymentMode: 'BankTransfer',
  bankName: 'HDFC Bank',
  bankAccountMasked: 'XXXXXXXX2290',
  bankIfscMasked: 'HDFC000XXXX',
  panMasked: 'XXXXX6789X',
  uan: '100123456789',
  pfNumber: null,
  esiNumber: null,
  reportees: 2,
};

const MY_PAYSLIPS: MyPayslipItem[] = [
  {
    id: id('45'),
    year,
    month: month - 1 || 12,
    grossEarnings: 285_000,
    totalDeductions: 61_200,
    netPay: 223_800,
    status: 'Approved',
  },
];

/** The rows in the status asked for (?status=Pending), as the server filters them. */
const inStatus =
  <T extends { status: string }>(rows: T[]) =>
  (request: FakeRequest) => {
    const status = request.url.searchParams.get('status');
    return paged(status ? rows.filter((row) => row.status === status) : rows);
  };

const mine = (rows: { employeeId: string }[]) => rows.map((row) => ({ ...row, employeeId: id('e2') }));

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/hr/designations': DESIGNATIONS,
  'GET /api/v1/hr/shifts': SHIFTS,
  'GET /api/v1/hr/employees': paged(EMPLOYEES),
  [`GET /api/v1/hr/employees/${RECORD_ID}`]: EMPLOYEE,
  [`GET /api/v1/hr/employees/${RECORD_ID}/credentials`]: CREDENTIALS.slice(0, 2),
  [`GET /api/v1/hr/employees/${RECORD_ID}/salary-structures`]: STRUCTURES,
  'GET /api/v1/hr/credentials': paged(CREDENTIALS),
  'GET /api/v1/hr/roster': ROSTER,
  'GET /api/v1/hr/biometric-devices': DEVICES,
  'GET /api/v1/hr/biometric-devices/reading': READING,
  'GET /api/v1/hr/attendance': paged(ATTENDANCE),
  'GET /api/v1/hr/attendance/summary': SUMMARY,
  'GET /api/v1/hr/attendance/punches': PUNCHES,
  'GET /api/v1/hr/attendance/unmatched-pins': UNMATCHED,
  'GET /api/v1/hr/attendance/regularisations': inStatus(REGULARISATIONS),
  'GET /api/v1/hr/leave/types': LEAVE_TYPES,
  'GET /api/v1/hr/holidays': HOLIDAYS,
  'GET /api/v1/hr/leave/balances': BALANCES,
  'GET /api/v1/hr/leave/requests': inStatus(LEAVE_REQUESTS),
  'GET /api/v1/hr/payroll/components': COMPONENTS,
  'GET /api/v1/hr/payroll/professional-tax': PROFESSIONAL_TAX,
  'GET /api/v1/hr/payroll/runs': paged(RUNS),
  ...Object.fromEntries(RUNS.map((r) => [`GET /api/v1/hr/payroll/runs/${r.id}`, r])),
  ...Object.fromEntries(RUNS.map((r) => [`GET /api/v1/hr/payroll/runs/${r.id}/payslips`, paged(PAYSLIPS)])),
  [`GET /api/v1/hr/payroll/runs/${RECORD_ID}/inputs`]: INPUTS,
  [`GET /api/v1/hr/payroll/payslips/${PAYSLIP.id}`]: PAYSLIP,
  'GET /api/v1/hr/me/profile': MY_PROFILE,
  'GET /api/v1/hr/me/roster': mine(ROSTER),
  'GET /api/v1/hr/me/attendance': mine(ATTENDANCE.slice(0, 1)),
  'GET /api/v1/hr/me/credentials': mine(CREDENTIALS.slice(2)),
  'GET /api/v1/hr/me/leave/balances': BALANCES,
  'GET /api/v1/hr/me/leave/requests': mine(LEAVE_REQUESTS.slice(1)),
  'GET /api/v1/hr/me/attendance/regularisations': [],
  'GET /api/v1/hr/me/payslips': MY_PAYSLIPS,
  [`GET /api/v1/hr/me/payslips/${id('45')}`]: { ...PAYSLIP, id: id('45'), employeeId: id('e2') },
  'GET /api/v1/hr/me/team/leave-requests': LEAVE_REQUESTS.slice(0, 1),
  'GET /api/v1/hr/me/team/regularisations': REGULARISATIONS,
};
