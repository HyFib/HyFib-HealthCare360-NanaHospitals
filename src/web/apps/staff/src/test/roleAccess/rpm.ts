import { todayIso } from '@hc360/core';
import type {
  RpmAlertDto,
  RpmDashboard,
  RpmDeviceDto,
  RpmEnrollmentDetail,
  RpmEnrollmentListItem,
  RpmProgramDto,
  RpmReadingDto,
  RpmTrend,
  ThresholdDto,
} from '@/modules/virtualcare/rpm/api';
import { FACILITY_ID, paged, problem, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Remote patient monitoring: programmes, patients enrolled (one active as the record, one paused), their devices
// (one assigned, one in the cupboard), readings and trends, and alerts open and acknowledged.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const HOUR = 60;
const DAY = 24 * HOUR;

const HYPERTENSION: RpmProgramDto = {
  id: id('7101'),
  code: 'RPM-HTN',
  name: 'Hypertension home monitoring',
  kind: 'Hypertension',
  description: 'Twice-daily blood pressure with a Bluetooth cuff; nurse review on alerts.',
  expectedReadingsPerDay: 2,
  isActive: true,
  version: 2,
};
const PROGRAMS: RpmProgramDto[] = [
  HYPERTENSION,
  {
    id: id('7102'),
    code: 'RPM-DM',
    name: 'Diabetes glucose monitoring',
    kind: 'Diabetes',
    description: 'Fasting and post-meal glucose from a connected glucometer.',
    expectedReadingsPerDay: 2,
    isActive: true,
    version: 1,
  },
  {
    id: id('7103'),
    code: 'RPM-CHF',
    name: 'Heart failure after discharge (pilot)',
    kind: 'PostDischarge',
    description: null,
    expectedReadingsPerDay: 1,
    isActive: false,
    version: 4,
  },
];

const ASHA = {
  patientId: '0192a000-0000-7000-8000-000000000001',
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  gender: 'Female',
  age: '39Y',
} as const;
const SUNIL = {
  patientId: RECORD_ID,
  uhid: 'NH26000187',
  patientName: 'Sunil Vasant Deshmukh',
  gender: 'Male',
  age: '55Y',
} as const;

const ENROLLMENTS: RpmEnrollmentListItem[] = [
  {
    id: RECORD_ID,
    ...SUNIL,
    programId: HYPERTENSION.id,
    programName: HYPERTENSION.name,
    programKind: 'Hypertension',
    status: 'Active',
    startDate: todayIso(-45),
    endDate: null,
    lastReadingAt: minutesAgo(3 * HOUR),
    openAlerts: 2,
    criticalAlerts: 1,
  },
  {
    id: id('7202'),
    ...ASHA,
    programId: id('7102'),
    programName: 'Diabetes glucose monitoring',
    programKind: 'Diabetes',
    status: 'Paused',
    startDate: todayIso(-120),
    endDate: null,
    lastReadingAt: minutesAgo(9 * DAY),
    openAlerts: 0,
    criticalAlerts: 0,
  },
];

const device = (
  n: string,
  serialNumber: string,
  type: RpmDeviceDto['type'],
  enrollmentId: string | null,
  extra: Partial<RpmDeviceDto> = {},
): RpmDeviceDto => ({
  id: id(n),
  serialNumber,
  type,
  manufacturer: type === 'BpMonitor' ? 'Omron' : 'Accu-Chek',
  model: type === 'BpMonitor' ? 'HEM-7156T' : 'Instant',
  enrollmentId,
  patientId: enrollmentId === RECORD_ID ? SUNIL.patientId : null,
  assignedAt: enrollmentId ? minutesAgo(45 * DAY) : null,
  lastSeenAt: enrollmentId ? minutesAgo(3 * HOUR) : null,
  apiKeyIssuedAt: minutesAgo(46 * DAY),
  isActive: true,
  silenceAlerted: false,
  version: 2,
  ...extra,
});

const DEVICES: RpmDeviceDto[] = [
  device('7301', 'OMR-7156-2611-0042', 'BpMonitor', RECORD_ID),
  device('7302', 'ACC-INST-2603-1187', 'Glucometer', null),
  device('7303', 'OMR-7156-2511-0007', 'BpMonitor', null, { isActive: false, apiKeyIssuedAt: null }),
];

const BP_SYSTOLIC: ThresholdDto = {
  type: 'SystolicBp',
  label: 'Systolic BP',
  unit: 'mmHg',
  min: 90,
  max: 140,
  criticalMin: 80,
  criticalMax: 180,
};
const BP_DIASTOLIC: ThresholdDto = {
  type: 'DiastolicBp',
  label: 'Diastolic BP',
  unit: 'mmHg',
  min: 60,
  max: 90,
  criticalMin: 50,
  criticalMax: 110,
};
const PULSE: ThresholdDto = {
  type: 'HeartRate',
  label: 'Heart rate',
  unit: '/min',
  min: 50,
  max: 100,
  criticalMin: 40,
  criticalMax: 130,
};

const ENROLLMENT: RpmEnrollmentDetail = {
  ...ENROLLMENTS[0]!,
  facilityId: FACILITY_ID,
  phoneMasked: '98******77',
  statusReason: null,
  goals: 'Home BP below 135/85 within 3 months.',
  notes: 'Takes amlodipine 5 mg and telmisartan 40 mg.',
  careTeam: [
    { practitionerId: '0192a000-0000-7000-8000-0000000000d1', name: 'Dr. Meera Iyer', isPrimary: true },
    { practitionerId: id('d2'), name: 'Dr. Anil Rao', isPrimary: false },
  ],
  thresholds: [BP_SYSTOLIC, BP_DIASTOLIC, PULSE],
  devices: DEVICES.slice(0, 1),
  thresholdsUpdatedAt: minutesAgo(20 * DAY),
  version: 6,
};

const alert = (
  n: string,
  severity: RpmAlertDto['severity'],
  status: RpmAlertDto['status'],
  value: number,
  message: string,
  extra: Partial<RpmAlertDto> = {},
): RpmAlertDto => ({
  id: id(n),
  enrollmentId: RECORD_ID,
  patientId: SUNIL.patientId,
  uhid: SUNIL.uhid,
  patientName: SUNIL.patientName,
  kind: 'Threshold',
  severity,
  status,
  readingType: 'SystolicBp',
  value,
  unit: 'mmHg',
  message,
  raisedAt: minutesAgo(3 * HOUR),
  lastOccurredAt: minutesAgo(3 * HOUR),
  occurrences: 1,
  acknowledgedAt: status === 'Open' ? null : minutesAgo(2 * HOUR),
  resolvedAt: status === 'Resolved' ? minutesAgo(HOUR) : null,
  notes: status === 'Open' ? null : 'Spoke to the patient; repeat reading advised.',
  ...extra,
});

const ALERTS: RpmAlertDto[] = [
  alert('7401', 'Critical', 'Open', 186, 'Systolic BP 186 mmHg is above the critical limit of 180.'),
  alert('7402', 'Warning', 'Acknowledged', 148, 'Systolic BP 148 mmHg is above 140.', {
    raisedAt: minutesAgo(DAY),
    lastOccurredAt: minutesAgo(15 * HOUR),
    occurrences: 3,
  }),
  alert('7403', 'Warning', 'Resolved', 0, 'No reading from Glucometer ACC-INST-2603-1187 for 48 hours.', {
    ...ASHA,
    enrollmentId: id('7202'),
    kind: 'DeviceSilence',
    readingType: null,
    value: null,
    unit: null,
    raisedAt: minutesAgo(10 * DAY),
  }),
];

const reading = (
  n: string,
  type: RpmReadingDto['type'],
  value: number,
  unit: string,
  hoursAgo: number,
  severity: RpmReadingDto['severity'] = 'Normal',
): RpmReadingDto => ({
  id: id(n),
  type,
  value,
  unit,
  textValue: null,
  measuredAt: minutesAgo(hoursAgo * HOUR),
  receivedAt: minutesAgo(hoursAgo * HOUR - 1),
  severity,
  source: 'Device',
  deviceId: id('7301'),
});

const READINGS: RpmReadingDto[] = [
  reading('7501', 'SystolicBp', 186, 'mmHg', 3, 'Critical'),
  reading('7502', 'DiastolicBp', 102, 'mmHg', 3, 'Warning'),
  reading('7503', 'HeartRate', 88, '/min', 3),
  reading('7504', 'SystolicBp', 138, 'mmHg', 15),
  reading('7505', 'DiastolicBp', 86, 'mmHg', 15),
];

const DASHBOARD: RpmDashboard = {
  activeEnrollments: 38,
  pausedEnrollments: 4,
  openAlerts: 6,
  criticalAlerts: 1,
  unacknowledgedCritical: 1,
  silentDevices: 2,
  readingsLast24Hours: 71,
  enrollmentsWithoutReadingToday: 5,
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/rpm/dashboard': DASHBOARD,
  'GET /api/v1/rpm/programs': PROGRAMS,
  'GET /api/v1/rpm/enrollments': paged(ENROLLMENTS),
  [`GET /api/v1/rpm/enrollments/${RECORD_ID}`]: ENROLLMENT,
  [`GET /api/v1/rpm/enrollments/${RECORD_ID}/readings`]: paged(READINGS),
  // The trend and adherence charts draw on a canvas, which jsdom has not got: drawn, they take the whole screen down to
  // its error page. So the check gets no trends (the empty state) and an adherence it cannot load (the error state).
  [`GET /api/v1/rpm/enrollments/${RECORD_ID}/trends`]: [] satisfies RpmTrend[],
  [`GET /api/v1/rpm/enrollments/${RECORD_ID}/adherence`]: problem(503, 'Adherence could not be worked out'),
  'GET /api/v1/rpm/devices': DEVICES,
  'GET /api/v1/rpm/alerts': paged(ALERTS),
};
