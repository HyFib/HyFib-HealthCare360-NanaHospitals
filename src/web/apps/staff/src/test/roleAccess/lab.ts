import type {
  LabInstrumentDto,
  LabOrderDetail,
  LabOrderItemDto,
  LabOrderListItem,
  LabReportListItem,
  LabReportView,
  LabSampleDto,
  LabTatReport,
  LabTestDetail,
  LabTestListItem,
  LeveyJenningsChartDto,
  OutsourcedItem,
  PendingCollectionItem,
  QcLotDto,
  QcRunDto,
  QcStatusRow,
  ReferralLabDto,
  ResultSheet,
  WorklistItem,
} from '@/modules/lab/api';
import type { CriticalCallDto } from '@/modules/lab/common/types';
import { PATIENTS } from '../accessibility/fixtures/frontOffice';
import { paged, STAFF_USER, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// The laboratory: Asha Devi's order opened as RECORD_ID (in progress: a tube still to collect, a sample in the lab and
// a test sent out to a referral lab); samples waiting for collection; the bench worklist with a sheet to enter results
// on; reports verified (to release) and released; critical calls; quality control with one test locked by a rejected
// run; outsourced tests to send and to receive back; the test master, instruments and referral labs.

const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const dateIn = (days: number) => new Date(now + days * 86_400_000).toISOString().slice(0, 10);
/** Complete blood count in the masters data (roleAccess/setup.ts): the service a test is billed as. */
const CBC_SERVICE_ID = '0192a000-0000-7000-8000-000000005a03';
const id = (suffix: string) => `0192a000-0000-7000-8000-0000000078${suffix}`;
const [ASHA, RAVI] = [PATIENTS[0]!, PATIENTS[1]!];

// ------------------------------------------------------------------ test master

const CBC = RECORD_ID;
const TSH = id('02');
const CULTURE = id('03');
const HBA1C = id('04');

const test = (
  overrides: Partial<LabTestListItem> & Pick<LabTestListItem, 'id' | 'code' | 'name' | 'section'>,
): LabTestListItem => ({
  shortName: null,
  specimenType: 'WholeBlood',
  container: 'Edta',
  resultType: 'Numeric',
  unit: null,
  isPanel: false,
  isOrderable: true,
  isProvisional: false,
  isActive: true,
  tatHours: 4,
  serviceItemId: CBC_SERVICE_ID,
  price: 350,
  preparation: null,
  componentCount: 0,
  ...overrides,
});

const TESTS: LabTestListItem[] = [
  test({ id: CBC, code: 'CBC', name: 'Complete blood count', section: 'Hematology', isPanel: true, componentCount: 5 }),
  test({
    id: TSH,
    code: 'TSH',
    name: 'Thyroid stimulating hormone',
    section: 'Biochemistry',
    specimenType: 'Serum',
    container: 'Plain',
    unit: 'µIU/mL',
    tatHours: 24,
    price: 450,
  }),
  test({
    id: CULTURE,
    code: 'UCS',
    name: 'Urine culture and sensitivity',
    section: 'Microbiology',
    specimenType: 'Urine',
    container: 'SterileContainer',
    resultType: 'Culture',
    tatHours: 72,
    price: 900,
  }),
  test({
    id: HBA1C,
    code: 'HBA1C',
    name: 'Glycated haemoglobin (HbA1c)',
    section: 'Biochemistry',
    unit: '%',
    tatHours: 24,
    price: 600,
    isProvisional: true,
  }),
];

const CBC_DETAIL: LabTestDetail = {
  id: CBC,
  code: 'CBC',
  name: 'Complete blood count',
  shortName: 'CBC',
  section: 'Hematology',
  specimenType: 'WholeBlood',
  container: 'Edta',
  method: 'Automated cell counter',
  tatHours: 4,
  loinc: '58410-2',
  loincName: 'CBC panel - Blood by Automated count',
  serviceItemId: CBC_SERVICE_ID,
  isOrderable: true,
  isPanel: true,
  resultType: 'Numeric',
  unit: null,
  decimals: 1,
  options: [],
  instrumentChannelCodes: [],
  qcGroup: 'HEMA',
  deltaThresholdPercent: null,
  allowAutoValidation: false,
  preparation: null,
  drawSeparately: false,
  interpretation: null,
  defaultReferralLabId: null,
  displayOrder: 1,
  isProvisional: false,
  isActive: true,
  components: [
    { testId: id('05'), code: 'HB', name: 'Haemoglobin', displayOrder: 1, groupName: null },
    { testId: id('06'), code: 'TLC', name: 'Total leucocyte count', displayOrder: 2, groupName: null },
  ],
  ranges: [],
  version: 3,
};

const INSTRUMENTS: LabInstrumentDto[] = [
  {
    id: id('11'),
    code: 'XN550',
    name: 'Sysmex XN-550',
    manufacturer: 'Sysmex',
    model: 'XN-550',
    section: 'Hematology',
    protocol: 'Astm',
    isBidirectional: true,
    autoValidationEnabled: false,
    location: 'Haematology bench',
    isActive: true,
    version: 2,
  },
];

const REFERRAL_LABS: ReferralLabDto[] = [
  {
    id: id('21'),
    code: 'MET',
    name: 'Metropolis Healthcare, Pune',
    contactPerson: 'Rahul Kulkarni',
    phone: '+912066012345',
    email: 'pune.b2b@metropolisindia.com',
    address: 'Shivajinagar, Pune 411005',
    accreditationNumber: 'MC-2214',
    tatDays: 3,
    isActive: true,
    version: 1,
  },
];

// ------------------------------------------------------------------ orders

const BLOOD_SAMPLE = id('31');
const SERUM_SAMPLE = id('32');
const URINE_SAMPLE = id('33');

const item = (
  overrides: Partial<LabOrderItemDto> &
    Pick<LabOrderItemDto, 'id' | 'labTestId' | 'testCode' | 'testName' | 'section' | 'status'>,
): LabOrderItemDto => ({
  priority: 'Routine',
  sampleId: null,
  serviceItemId: CBC_SERVICE_ID,
  chargeId: id('a1'),
  chargeError: null,
  packageBookingId: null,
  quantity: 1,
  referralLabId: null,
  sentOutAt: null,
  externalReportReceivedAt: null,
  verifiedAt: null,
  releasedAt: null,
  cancelReason: null,
  ...overrides,
});

const sample = (
  overrides: Partial<LabSampleDto> & Pick<LabSampleDto, 'id' | 'barcode' | 'status' | 'tests'>,
): LabSampleDto => ({
  specimenType: 'WholeBlood',
  container: 'Edta',
  section: 'Hematology',
  priority: 'Routine',
  collectedAt: null,
  collectedBy: null,
  collectionSite: null,
  receivedAt: null,
  receivedBy: null,
  rejectionReason: null,
  rejectionRemarks: null,
  rejectedAt: null,
  recollectionOfSampleId: null,
  recollectedAsSampleId: null,
  ...overrides,
});

const ORDER: LabOrderDetail = {
  id: RECORD_ID,
  accessionNumber: 'LB2600412',
  patientId: ASHA.id,
  uhid: ASHA.uhid,
  patientName: ASHA.fullName,
  gender: 'Female',
  age: ASHA.age,
  visitId: '0192a000-0000-7000-8000-000000000b01',
  admissionId: null,
  patientClass: 'OutPatient',
  source: 'ClinicalOrder',
  sourceOrderNumber: 'CO2600877',
  priority: 'Routine',
  status: 'InProgress',
  orderingPractitionerId: STAFF_USER.practitionerId,
  orderingDoctor: 'Dr. Meera Iyer',
  referringDoctorName: null,
  clinicalNotes: 'Fever for three days; rule out UTI.',
  orderedAt: minutesAgo(70),
  completedAt: null,
  reportId: null,
  reportStatus: null,
  items: [
    item({
      id: id('41'),
      labTestId: CBC,
      testCode: 'CBC',
      testName: 'Complete blood count',
      section: 'Hematology',
      status: 'InLab',
      sampleId: BLOOD_SAMPLE,
    }),
    item({
      id: id('42'),
      labTestId: TSH,
      testCode: 'TSH',
      testName: 'Thyroid stimulating hormone',
      section: 'Biochemistry',
      status: 'Ordered',
      sampleId: SERUM_SAMPLE,
    }),
    item({
      id: id('43'),
      labTestId: CULTURE,
      testCode: 'UCS',
      testName: 'Urine culture and sensitivity',
      section: 'Microbiology',
      status: 'Collected',
      sampleId: URINE_SAMPLE,
      referralLabId: id('21'),
      sentOutAt: minutesAgo(20),
    }),
  ],
  samples: [
    sample({
      id: BLOOD_SAMPLE,
      barcode: 'LB2600412-01',
      status: 'Received',
      tests: ['CBC'],
      collectedAt: minutesAgo(60),
      collectedBy: 'Sneha Jadhav',
      collectionSite: 'OPD collection centre',
      receivedAt: minutesAgo(45),
      receivedBy: 'Arvind Pawar',
    }),
    sample({
      id: SERUM_SAMPLE,
      barcode: 'LB2600412-02',
      status: 'Pending',
      tests: ['TSH'],
      specimenType: 'Serum',
      container: 'Plain',
      section: 'Biochemistry',
    }),
    sample({
      id: URINE_SAMPLE,
      barcode: 'LB2600412-03',
      status: 'Collected',
      tests: ['UCS'],
      specimenType: 'Urine',
      container: 'SterileContainer',
      section: 'Microbiology',
      collectedAt: minutesAgo(55),
      collectedBy: 'Sneha Jadhav',
    }),
  ],
  version: 6,
};

const orderRow = (
  overrides: Partial<LabOrderListItem> & Pick<LabOrderListItem, 'id' | 'accessionNumber' | 'status'>,
): LabOrderListItem => ({
  patientId: ASHA.id,
  uhid: ASHA.uhid,
  patientName: ASHA.fullName,
  gender: 'Female',
  age: ASHA.age,
  patientClass: 'OutPatient',
  source: 'ClinicalOrder',
  priority: 'Routine',
  orderedAt: minutesAgo(70),
  testCount: 3,
  releasedCount: 0,
  tests: ['CBC', 'TSH', 'UCS'],
  reportId: null,
  reportStatus: null,
  ...overrides,
});

const ORDERS: LabOrderListItem[] = [
  orderRow({ id: RECORD_ID, accessionNumber: 'LB2600412', status: 'InProgress' }),
  orderRow({
    id: id('52'),
    accessionNumber: 'LB2600398',
    status: 'Completed',
    patientId: RAVI.id,
    uhid: RAVI.uhid,
    patientName: RAVI.fullName,
    gender: 'Male',
    age: RAVI.age,
    priority: 'Stat',
    orderedAt: minutesAgo(60 * 6),
    testCount: 1,
    releasedCount: 1,
    tests: ['HBA1C'],
    reportId: id('62'),
    reportStatus: 'Released',
  }),
  orderRow({
    id: id('53'),
    accessionNumber: 'LB2600415',
    status: 'Ordered',
    priority: 'Urgent',
    orderedAt: minutesAgo(10),
    testCount: 1,
    tests: ['TSH'],
  }),
];

// ------------------------------------------------------------------ samples and results

const PENDING: PendingCollectionItem[] = [
  {
    sampleId: SERUM_SAMPLE,
    barcode: 'LB2600412-02',
    labOrderId: RECORD_ID,
    accessionNumber: 'LB2600412',
    patientId: ASHA.id,
    uhid: ASHA.uhid,
    patientName: ASHA.fullName,
    gender: 'Female',
    age: ASHA.age,
    patientClass: 'OutPatient',
    priority: 'Routine',
    container: 'Plain',
    specimenType: 'Serum',
    section: 'Biochemistry',
    tests: ['TSH'],
    preparation: [],
    orderedAt: minutesAgo(70),
    isRecollection: false,
  },
  {
    sampleId: id('34'),
    barcode: 'LB2600415-01',
    labOrderId: id('53'),
    accessionNumber: 'LB2600415',
    patientId: ASHA.id,
    uhid: ASHA.uhid,
    patientName: ASHA.fullName,
    gender: 'Female',
    age: ASHA.age,
    patientClass: 'OutPatient',
    priority: 'Urgent',
    container: 'Plain',
    specimenType: 'Serum',
    section: 'Biochemistry',
    tests: ['TSH'],
    preparation: ['Fasting not needed'],
    orderedAt: minutesAgo(10),
    isRecollection: false,
  },
];

const WORKLIST: WorklistItem[] = [
  {
    sampleId: BLOOD_SAMPLE,
    barcode: 'LB2600412-01',
    labOrderId: RECORD_ID,
    accessionNumber: 'LB2600412',
    patientId: ASHA.id,
    uhid: ASHA.uhid,
    patientName: ASHA.fullName,
    age: ASHA.age,
    gender: 'Female',
    patientClass: 'OutPatient',
    priority: 'Routine',
    section: 'Hematology',
    status: 'InProcess',
    receivedAt: minutesAgo(45),
    pending: 0,
    entered: 2,
    verified: 0,
    critical: 0,
    tests: ['CBC'],
  },
];

const SHEET: ResultSheet = {
  sampleId: BLOOD_SAMPLE,
  barcode: 'LB2600412-01',
  sampleStatus: 'InProcess',
  labOrderId: RECORD_ID,
  accessionNumber: 'LB2600412',
  patientId: ASHA.id,
  uhid: ASHA.uhid,
  patientName: ASHA.fullName,
  ageSex: `${ASHA.age} / F`,
  clinicalNotes: 'Fever for three days; rule out UTI.',
  lines: [
    { name: 'Haemoglobin', code: 'HB', value: '11.2', unit: 'g/dL', flag: 'Low' as const, ref: '12.0 – 15.5' },
    {
      name: 'Total leucocyte count',
      code: 'TLC',
      value: '11800',
      unit: '/µL',
      flag: 'High' as const,
      ref: '4000 – 11000',
    },
  ].map((line, index) => ({
    resultId: id(`7${index}`),
    labOrderItemId: id('41'),
    orderedTest: 'Complete blood count',
    labTestId: id(`0${index + 5}`),
    testCode: line.code,
    testName: line.name,
    groupName: null,
    displayOrder: index + 1,
    resultType: 'Numeric' as const,
    status: 'Entered' as const,
    value: line.value,
    unit: line.unit,
    decimals: 1,
    options: [],
    flag: line.flag,
    isCritical: false,
    referenceText: line.ref,
    previousValue: null,
    previousResultAt: null,
    deltaPercent: null,
    deltaExceeded: false,
    autoValidated: false,
    instrumentCode: 'XN550',
    method: 'Automated cell counter',
    remarks: null,
    organism: null,
    colonyCount: null,
    susceptibilities: [],
    enteredAt: minutesAgo(15),
    verifiedAt: null,
    hasCriticalCall: false,
    version: 1,
  })),
};

// ------------------------------------------------------------------ reports

const REPORTS: LabReportListItem[] = [
  {
    id: RECORD_ID,
    reportNumber: 'LR2600301',
    labOrderId: id('54'),
    patientId: RAVI.id,
    uhid: RAVI.uhid,
    patientName: RAVI.fullName,
    status: 'Verified',
    isPartial: false,
    revision: 0,
    orderedAt: minutesAgo(60 * 3),
    releasedAt: null,
  },
  {
    id: id('62'),
    reportNumber: 'LR2600296',
    labOrderId: id('52'),
    patientId: RAVI.id,
    uhid: RAVI.uhid,
    patientName: RAVI.fullName,
    status: 'Released',
    isPartial: false,
    revision: 0,
    orderedAt: minutesAgo(60 * 6),
    releasedAt: minutesAgo(60 * 2),
  },
];

const REPORT: LabReportView = {
  reportId: RECORD_ID,
  labOrderId: id('54'),
  reportNumber: 'LR2600301',
  status: 'Verified',
  isPartial: false,
  revision: 0,
  letterhead: {
    facilityName: 'Nana Hospitals, Pune',
    address: '12 Prabhat Road, Erandwane, Pune 411004',
    phone: '+912025671234',
    email: 'lab@nanahospitals.in',
    nabhNumber: 'MC-5521',
    letterheadNote: null,
    logoFileId: null,
  },
  patient: { id: RAVI.id, uhid: RAVI.uhid, name: RAVI.fullName, gender: 'Male', age: RAVI.age, dateOfBirth: null },
  patientClass: 'OutPatient',
  referredBy: 'Dr. Meera Iyer',
  clinicalNotes: 'Known diabetic, on metformin.',
  orderedAt: minutesAgo(60 * 3),
  firstReleasedAt: null,
  releasedAt: null,
  sections: [
    {
      section: 'Biochemistry',
      tests: [
        {
          orderItemId: id('44'),
          code: 'HBA1C',
          name: 'Glycated haemoglobin (HbA1c)',
          status: 'Verified',
          specimenType: 'WholeBlood',
          method: 'HPLC',
          interpretation: null,
          referralLabName: null,
          collectedAt: minutesAgo(60 * 3 - 10),
          receivedAt: minutesAgo(60 * 3 - 30),
          verifiedAt: minutesAgo(30),
          releasedAt: null,
          results: [
            {
              resultId: id('75'),
              name: 'HbA1c',
              groupName: null,
              value: '7.8',
              unit: '%',
              flag: 'High',
              referenceText: 'Below 5.7',
              status: 'Verified',
              autoValidated: false,
              remarks: null,
              organism: null,
              colonyCount: null,
              susceptibilities: [],
              previousValue: 8.4,
              previousResultAt: minutesAgo(60 * 24 * 95),
            },
          ],
        },
      ],
    },
  ],
  signatories: [
    {
      userId: id('81'),
      name: 'Dr. Kavita Rane',
      qualification: 'MD (Pathology)',
      registrationNumber: 'MMC 2009/04/1123',
      signatureFileId: null,
    },
  ],
  hasAutoValidatedResults: false,
  amendments: [],
  verificationCode: null,
  verificationPath: null,
  verificationUrl: null,
};

const CRITICAL_CALLS: CriticalCallDto[] = [
  {
    id: id('91'),
    kind: 'Laboratory',
    patientId: RAVI.id,
    uhid: RAVI.uhid,
    patientName: RAVI.fullName,
    finding: 'Potassium 6.4 mmol/L (critical high)',
    informedPersonName: 'Dr. Anil Rao',
    informedPersonRole: 'Treating physician',
    method: 'Phone',
    informedAt: minutesAgo(60 * 5),
    readBackConfirmed: true,
    calledBy: 'Arvind Pawar',
    notes: null,
    labResultId: id('76'),
    imagingStudyId: null,
    cardiologyStudyId: null,
  },
];

// ------------------------------------------------------------------ quality control

const lot = (overrides: Partial<QcLotDto> & Pick<QcLotDto, 'id' | 'level' | 'lotNumber'>): QcLotDto => ({
  labTestId: id('05'),
  testCode: 'HB',
  testName: 'Haemoglobin',
  material: 'Sysmex e-CHECK (XE)',
  expiryDate: dateIn(120),
  targetMean: 12.4,
  targetSd: 0.3,
  unit: 'g/dL',
  instrumentCode: 'XN550',
  isActive: true,
  isExpired: false,
  version: 1,
  ...overrides,
});

const LOTS: QcLotDto[] = [
  lot({ id: RECORD_ID, level: 1, lotNumber: '42310801' }),
  lot({ id: id('a2'), level: 2, lotNumber: '42310802', targetMean: 15.9, targetSd: 0.4 }),
];

const QC_STATUS: QcStatusRow[] = [
  {
    labTestId: id('05'),
    testCode: 'HB',
    testName: 'Haemoglobin',
    lotId: RECORD_ID,
    lotNumber: '42310801',
    level: 1,
    instrumentCode: 'XN550',
    latestOutcome: 'Rejected',
    latestRunAt: minutesAgo(40),
    locked: true,
    overridden: false,
  },
  {
    labTestId: id('05'),
    testCode: 'HB',
    testName: 'Haemoglobin',
    lotId: id('a2'),
    lotNumber: '42310802',
    level: 2,
    instrumentCode: 'XN550',
    latestOutcome: 'Accepted',
    latestRunAt: minutesAgo(40),
    locked: false,
    overridden: false,
  },
];

const QC_RUNS: QcRunDto[] = [
  {
    id: id('a5'),
    qcLotId: RECORD_ID,
    labTestId: id('05'),
    lotNumber: '42310801',
    level: 1,
    instrumentCode: 'XN550',
    value: 13.4,
    zScore: 3.3,
    runAt: minutesAgo(40),
    outcome: 'Rejected',
    violations: ['1-3s'],
    recordedBy: 'Arvind Pawar',
    reviewedAt: null,
    reviewedBy: null,
    reviewNote: null,
    correctiveAction: null,
    overridden: false,
  },
];

const LEVEY_JENNINGS: LeveyJenningsChartDto = {
  lotId: RECORD_ID,
  testName: 'Haemoglobin',
  material: 'Sysmex e-CHECK (XE)',
  level: 1,
  lotNumber: '42310801',
  unit: 'g/dL',
  mean: 12.4,
  sd: 0.3,
  limits: [
    { label: '+2 SD', value: 13.0 },
    { label: '-2 SD', value: 11.8 },
  ],
  // No points: the screen draws them on a canvas chart (ECharts), which the test DOM cannot draw, and the page fails.
  points: [],
};

// ------------------------------------------------------------------ outsourcing and TAT

const OUTSOURCED: OutsourcedItem[] = [
  {
    itemId: id('b1'),
    labOrderId: id('55'),
    accessionNumber: 'LB2600405',
    patientId: RAVI.id,
    uhid: RAVI.uhid,
    patientName: RAVI.fullName,
    testName: 'Vitamin D (25-OH)',
    status: 'Collected',
    sampleId: id('35'),
    barcode: 'LB2600405-02',
    referralLabId: null,
    referralLabName: null,
    collectedAt: minutesAgo(60 * 2),
    sentOutAt: null,
    dispatchReference: null,
    externalReportReceivedAt: null,
    dueAt: null,
    isOverdue: false,
  },
  {
    itemId: id('43'),
    labOrderId: RECORD_ID,
    accessionNumber: 'LB2600412',
    patientId: ASHA.id,
    uhid: ASHA.uhid,
    patientName: ASHA.fullName,
    testName: 'Urine culture and sensitivity',
    status: 'Collected',
    sampleId: URINE_SAMPLE,
    barcode: 'LB2600412-03',
    referralLabId: id('21'),
    referralLabName: 'Metropolis Healthcare, Pune',
    collectedAt: minutesAgo(55),
    sentOutAt: minutesAgo(20),
    dispatchReference: 'MET-PUN-88123',
    externalReportReceivedAt: null,
    dueAt: minutesAgo(-60 * 24 * 3),
    isOverdue: false,
  },
];

const tat = { count: 18, averageMinutes: 212, medianMinutes: 185, p90Minutes: 340 };
const TAT: LabTatReport = {
  from: dateIn(-6),
  to: dateIn(0),
  overall: tat,
  // Nothing released per test and no rejections: those are drawn on canvas charts (ECharts), which the test DOM cannot
  // draw.
  tests: [
    {
      labTestId: CBC,
      testCode: 'CBC',
      testName: 'Complete blood count',
      section: 'Hematology',
      targetHours: 4,
      released: 0,
      withinTarget: 0,
      withinTargetPercent: null,
      orderToRelease: { count: 0, averageMinutes: null, medianMinutes: null, p90Minutes: null },
      receiptToVerification: { count: 0, averageMinutes: null, medianMinutes: null, p90Minutes: null },
    },
  ],
  samples: 64,
  rejections: 0,
  rejectionsByReason: [],
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/lab/tests': paged(TESTS),
  [`GET /api/v1/lab/tests/${RECORD_ID}`]: CBC_DETAIL,
  'GET /api/v1/lab/instruments': INSTRUMENTS,
  'GET /api/v1/lab/referral-labs': REFERRAL_LABS,
  'GET /api/v1/lab/orders': paged(ORDERS),
  [`GET /api/v1/lab/orders/${RECORD_ID}`]: ORDER,
  'GET /api/v1/lab/samples/pending-collection': paged(PENDING),
  'GET /api/v1/lab/worklist': paged(WORKLIST),
  [`GET /api/v1/lab/samples/${BLOOD_SAMPLE}/results`]: SHEET,
  'GET /api/v1/lab/reports': paged(REPORTS),
  [`GET /api/v1/lab/reports/${RECORD_ID}`]: REPORT,
  'GET /api/v1/lab/critical-calls': paged(CRITICAL_CALLS),
  'GET /api/v1/lab/qc/lots': LOTS,
  'GET /api/v1/lab/qc/status': QC_STATUS,
  'GET /api/v1/lab/qc/runs': paged(QC_RUNS),
  [`GET /api/v1/lab/qc/lots/${RECORD_ID}/levey-jennings`]: LEVEY_JENNINGS,
  'GET /api/v1/lab/outsourcing': paged(OUTSOURCED),
  'GET /api/v1/lab/tat': TAT,
};
