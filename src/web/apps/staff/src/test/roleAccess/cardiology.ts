import type {
  AbpmFindings,
  CardiologyReportDto,
  CardiologyStudyDetail,
  CardiologyStudyListItem,
  EcgFindings,
  EchoFindings,
  HolterFindings,
  ReportedCardiologyStudy,
  TmtFindings,
} from '@/modules/cardiology/api';
import { paged, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Cardiology for today: a worklist with a study at each step (ordered, recording, acquired, reported), the reporting
// worklist, and one acquired echo with its device file and a draft report as the record.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();

type Person = Pick<CardiologyStudyListItem, 'patientId' | 'uhid' | 'patientName' | 'gender' | 'age'>;
const ASHA: Person = {
  patientId: '0192a000-0000-7000-8000-000000000001',
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  gender: 'Female',
  age: '39Y',
};
const RAVI: Person = {
  patientId: '0192a000-0000-7000-8000-000000000002',
  uhid: 'NH26000124',
  patientName: 'Ravi Shankar',
  gender: 'Male',
  age: '62Y',
};
const HARISH: Person = {
  patientId: id('9'),
  uhid: 'NH26000201',
  patientName: 'Harish Chandra',
  gender: 'Male',
  age: '58Y',
};
const GOPAL: Person = { patientId: id('c'), uhid: 'NH26000098', patientName: 'Gopal Rao', gender: 'Male', age: '66Y' };

const study = (
  n: string,
  accessionNumber: string,
  who: Person,
  studyName: string,
  studyType: CardiologyStudyListItem['studyType'],
  status: CardiologyStudyListItem['status'],
  extra: Partial<CardiologyStudyListItem> = {},
): CardiologyStudyListItem => ({
  id: n === 'record' ? RECORD_ID : id(n),
  accessionNumber,
  ...who,
  patientClass: 'OutPatient',
  studyName,
  studyType,
  priority: 'Routine',
  status,
  orderedAt: minutesAgo(150),
  scheduledAt: null,
  reportStatus: null,
  isCriticalFinding: false,
  ...extra,
});

const STUDIES: CardiologyStudyListItem[] = [
  study('record', 'CAR2600412', ASHA, '2D echocardiography with colour Doppler', 'Echo', 'Acquired', {
    reportStatus: 'Draft',
  }),
  study('6a02', 'CAR2600415', RAVI, 'ECG (12-lead)', 'Ecg', 'Ordered', { priority: 'Urgent' }),
  study('6a03', 'CAR2600409', HARISH, 'Treadmill test (Bruce protocol)', 'Tmt', 'InProgress', {
    scheduledAt: minutesAgo(30),
  }),
  study('6a04', 'CAR2600398', GOPAL, 'Holter monitoring, 24 hours', 'Holter', 'Reported', {
    reportStatus: 'Reported',
    orderedAt: minutesAgo(30 * 60),
    patientClass: 'InPatient',
  }),
];

const NO_ECG: EcgFindings = {
  heartRate: null,
  rhythm: null,
  prInterval: null,
  qrsDuration: null,
  qtInterval: null,
  qtcInterval: null,
  axis: null,
  stTChanges: null,
  interpretation: null,
};
const ECHO: EchoFindings = {
  lvef: 45,
  leftVentricle: 'Mildly dilated; global hypokinesia',
  rightVentricle: 'Normal size and function',
  leftAtrium: 'Mildly dilated (4.2 cm)',
  rightAtrium: 'Normal',
  valves: 'Mild mitral regurgitation; other valves normal',
  rwma: 'Hypokinesia of the inferior wall',
  diastolicFunction: 'Grade I diastolic dysfunction',
  pericardium: 'No effusion',
  conclusion: null,
};
const NO_TMT: TmtFindings = {
  protocol: null,
  durationSeconds: null,
  mets: null,
  maxHeartRate: null,
  targetHeartRatePercent: null,
  bpResponse: null,
  symptoms: null,
  stChanges: null,
  reasonForTermination: null,
  result: null,
};
const NO_HOLTER: HolterFindings = {
  durationHours: null,
  minHeartRate: null,
  meanHeartRate: null,
  maxHeartRate: null,
  ventricularEctopics: null,
  supraventricularEctopics: null,
  pauses: null,
  longestPause: null,
  arrhythmias: null,
  stChanges: null,
};
const NO_ABPM: AbpmFindings = {
  daytimeMeanSystolic: null,
  daytimeMeanDiastolic: null,
  nighttimeMeanSystolic: null,
  nighttimeMeanDiastolic: null,
  dippingPattern: null,
};

const REPORT: CardiologyReportDto = {
  id: id('6c01'),
  studyType: 'Echo',
  ecg: NO_ECG,
  echo: ECHO,
  tmt: NO_TMT,
  holter: NO_HOLTER,
  abpm: NO_ABPM,
  conclusion: 'Inferior wall hypokinesia with mildly reduced LV systolic function (EF 45%).',
  recommendations: 'Cardiology OPD review; consider coronary angiography.',
  isCriticalFinding: false,
  criticalFindingNote: null,
  status: 'Draft',
  reportedBy: null,
  reportedAt: null,
  verifiedBy: null,
  verifiedAt: null,
  releasedAt: null,
  version: 1,
};

const STUDY: CardiologyStudyDetail = {
  id: RECORD_ID,
  accessionNumber: 'CAR2600412',
  ...ASHA,
  visitId: id('6b01'),
  admissionId: null,
  patientClass: 'OutPatient',
  sourceOrderNumber: 'ORD2600498',
  serviceCode: 'CAR-ECHO',
  studyName: '2D echocardiography with colour Doppler',
  studyType: 'Echo',
  priority: 'Routine',
  orderingPractitionerId: '0192a000-0000-7000-8000-0000000000d1',
  orderingDoctor: 'Dr. Meera Iyer',
  clinicalNotes: 'Exertional breathlessness for 2 months; ECG shows Q waves in the inferior leads.',
  orderedAt: minutesAgo(150),
  status: 'Acquired',
  scheduledAt: minutesAgo(90),
  room: 'Echo lab 1',
  startedAt: minutesAgo(70),
  acquiredAt: minutesAgo(45),
  technician: 'Ms. Pooja Kamble',
  deviceName: 'GE Vivid T8',
  technicianNotes: 'Poor parasternal window.',
  reportedAt: null,
  verifiedAt: null,
  releasedAt: null,
  cancelReason: null,
  chargeError: null,
  packageBookingId: null,
  quantity: 1,
  files: [
    {
      id: id('6d01'),
      fileName: 'CAR2600412_echo.dcm',
      contentType: 'application/dicom',
      sizeBytes: 48_213_504,
      pacs: null,
    },
    {
      id: id('6d02'),
      fileName: 'CAR2600412_measurements.pdf',
      contentType: 'application/pdf',
      sizeBytes: 212_480,
      pacs: null,
    },
  ],
  report: REPORT,
  version: 4,
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/cardiology/studies': paged(STUDIES),
  [`GET /api/v1/cardiology/studies/${RECORD_ID}`]: STUDY,
  // Submitting saves the draft first, then submits what was saved.
  [`PUT /api/v1/cardiology/studies/${RECORD_ID}/report`]: {
    ...STUDY,
    report: REPORT,
  } satisfies ReportedCardiologyStudy,
  'GET /api/v1/cardiology/reporting-worklist': paged(
    STUDIES.filter((s) => s.status === 'Acquired' || s.status === 'Reported'),
  ),
};
