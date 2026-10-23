import { todayIso, type Letterhead, type LookupItem } from '@hc360/core';
import type { AiStatusDto } from '@/lib/ai';
import type {
  ClinicalNoteDto,
  DiagnosisDto,
  EmrWorklist,
  EncounterDetail,
  FavouriteDto,
  PatientClinicalSummary,
  PreviousVisitResponse,
  PreviousVisitSummary,
  TemplateDto,
  TemplateListItem,
  VitalsDto,
  WorklistAppointment,
  WorklistQueueEntry,
} from '@/modules/emr/api';
import type { OrderDto, OrderItemDto, OrderSetDto } from '@/modules/emr/ordersApi';
import type {
  PrescribableDrug,
  PrescriptionDto,
  PrescriptionItemDto,
  PrescriptionListItem,
} from '@/modules/emr/prescriptionsApi';
import type { VitalFlag } from '@/modules/emr/vitals';
import type { WardRound, WardRoundPatient } from '@/modules/inpatient/api';
import type { VisitListItem } from '@/modules/patients/api';
import { paged, STAFF_USER, type FakeAnswers, type FakeRequest } from '../../fakeApi';

// The doctor's screens with canned data: the worklist with each of its lists (queue, appointments, ward round), and
// the consultation of an open encounter.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const DAY = 60 * 24;

const DOCTOR_ID = STAFF_USER.practitionerId!;
const GENERAL_MEDICINE: LookupItem = { id: id('de01'), code: 'GM', name: 'General medicine' };
const DEPARTMENTS: LookupItem[] = [
  GENERAL_MEDICINE,
  { id: id('de02'), code: 'CARD', name: 'Cardiology' },
  { id: id('de03'), code: 'ORTH', name: 'Orthopaedics' },
];
const DOCTORS: LookupItem[] = [
  { id: DOCTOR_ID, code: 'D001', name: 'Dr. Meera Iyer' },
  { id: id('d2'), code: 'D002', name: 'Dr. Anil Rao' },
  { id: id('d3'), code: 'D003', name: 'Dr. Farah Khan' },
];

type Person = Pick<WorklistQueueEntry, 'patientId' | 'uhid' | 'patientName' | 'gender' | 'age'>;
const person = (
  n: string,
  uhid: string,
  patientName: string,
  gender: Person['gender'],
  age: string | null,
): Person => ({
  patientId: id(n),
  uhid,
  patientName,
  gender,
  age,
});
export const ASHA = person('1', 'NH26000123', 'Asha Devi', 'Female', '39Y');
const RAVI = person('2', 'NH26000124', 'Ravi Shankar', 'Male', '62Y');
const LAKSHMI = person('3', 'NH26000131', 'Lakshmi Narayanan', 'Female', '54Y');
const FATIMA = person('4', 'NH26000140', 'Fatima Sheikh', 'Female', '28Y');
const SURESH = person('5', 'NH26000152', 'Suresh Patil', 'Male', '47Y');
const IRFAN = person('6', 'NH26000171', 'Mohammed Irfan', 'Male', '33Y');
const RESTRICTED = person('7', 'NH26000188', 'Restricted patient', 'Male', null);
const PRIYA = person('8', 'NH26000193', 'Priya Menon', 'Female', '45Y');
const HARISH = person('9', 'NH26000201', 'Harish Chandra', 'Male', '58Y');
const DEEPA = person('a', 'NH26000077', 'Deepa Joshi', 'Female', '31Y');
const KAMALA = person('b', 'NH26000125', 'Kamala Bai', 'Female', '78Y');
const GOPAL = person('c', 'NH26000098', 'Gopal Rao', 'Male', '66Y');
const VIJAY = person('d', 'NH26000215', 'Vijay Kulkarni', 'Male', '51Y');

/** The patient fields of the worklist's side lists. */
const who = ({ patientId, uhid, patientName }: Person) => ({ patientId, uhid, patientName });

export const ENCOUNTER_ID = id('e01');
const VISIT_ID = id('b01');
const TEMPLATE_ID = id('7e01');
const DRAFT_RX_ID = id('c01');
const SIGNED_RX_ID = id('c02');

// ---------------------------------------------------------------------------------------------------- worklist

const token = (
  patient: Person,
  overrides: Partial<WorklistQueueEntry> & Pick<WorklistQueueEntry, 'tokenId' | 'tokenLabel' | 'tokenStatus'>,
): WorklistQueueEntry => ({
  priority: 'Normal',
  visitId: id(`b${overrides.tokenLabel.slice(-2)}`),
  ...patient,
  chiefComplaint: null,
  isFollowUp: false,
  issuedAt: minutesAgo(30),
  waitingMinutes: 0,
  encounterId: null,
  encounterStatus: null,
  hasActiveAllergies: false,
  hasCriticalAlerts: false,
  isRestricted: false,
  ...overrides,
});

const QUEUE: WorklistQueueEntry[] = [
  token(ASHA, {
    tokenId: id('7001'),
    tokenLabel: 'GM-012',
    tokenStatus: 'InService',
    visitId: VISIT_ID,
    chiefComplaint: 'Fever for 3 days with body ache',
    issuedAt: minutesAgo(65),
    encounterId: ENCOUNTER_ID,
    encounterStatus: 'InProgress',
    hasActiveAllergies: true,
  }),
  token(RAVI, {
    tokenId: id('7002'),
    tokenLabel: 'GM-013',
    tokenStatus: 'Called',
    priority: 'Emergency',
    chiefComplaint: 'Chest pain since morning',
    issuedAt: minutesAgo(20),
    waitingMinutes: 20,
    hasCriticalAlerts: true,
  }),
  token(LAKSHMI, {
    tokenId: id('7003'),
    tokenLabel: 'GM-014',
    tokenStatus: 'Waiting',
    priority: 'Priority',
    chiefComplaint: 'Giddiness and high sugar readings',
    issuedAt: minutesAgo(52),
    waitingMinutes: 52,
    hasActiveAllergies: true,
  }),
  token(FATIMA, {
    tokenId: id('7004'),
    tokenLabel: 'GM-015',
    tokenStatus: 'Waiting',
    isFollowUp: true,
    issuedAt: minutesAgo(12),
    waitingMinutes: 12,
  }),
  token(RESTRICTED, {
    tokenId: id('7005'),
    tokenLabel: 'GM-016',
    tokenStatus: 'Waiting',
    issuedAt: minutesAgo(8),
    waitingMinutes: 8,
    isRestricted: true,
  }),
  token(SURESH, {
    tokenId: id('7006'),
    tokenLabel: 'GM-009',
    tokenStatus: 'Completed',
    chiefComplaint: 'Review of thyroid report',
    issuedAt: minutesAgo(140),
    encounterId: id('e02'),
    encounterStatus: 'Signed',
  }),
  token(IRFAN, {
    tokenId: id('7007'),
    tokenLabel: 'GM-010',
    tokenStatus: 'Skipped',
    chiefComplaint: 'Cough and cold',
    issuedAt: minutesAgo(120),
  }),
];

const booking = (
  patient: Person,
  overrides: Partial<WorklistAppointment> & Pick<WorklistAppointment, 'id' | 'number' | 'status' | 'startsAt'>,
): WorklistAppointment => ({
  endsAt: new Date(Date.parse(overrides.startsAt) + 15 * 60_000).toISOString(),
  visitType: 'New',
  isTelehealth: false,
  reason: null,
  ...patient,
  practitionerId: DOCTOR_ID,
  practitionerName: 'Dr. Meera Iyer',
  visitId: null,
  checkedInAt: null,
  tokenLabel: null,
  encounterId: null,
  encounterStatus: null,
  hasActiveAllergies: false,
  hasCriticalAlerts: false,
  isRestricted: false,
  ...overrides,
});

const APPOINTMENTS: WorklistAppointment[] = [
  booking(SURESH, {
    id: id('2001'),
    number: 'AP2600201',
    status: 'Completed',
    startsAt: minutesAgo(150),
    visitType: 'FollowUp',
    reason: 'Thyroid report review',
    visitId: id('b09'),
    checkedInAt: minutesAgo(155),
    tokenLabel: 'GM-009',
    encounterId: id('e02'),
    encounterStatus: 'Signed',
  }),
  booking(IRFAN, { id: id('2002'), number: 'AP2600202', status: 'NoShow', startsAt: minutesAgo(120), reason: 'Cough' }),
  booking(ASHA, {
    id: id('2003'),
    number: 'AP2600203',
    status: 'InConsultation',
    startsAt: minutesAgo(60),
    reason: 'Fever',
    visitId: VISIT_ID,
    checkedInAt: minutesAgo(70),
    tokenLabel: 'GM-012',
    encounterId: ENCOUNTER_ID,
    encounterStatus: 'InProgress',
    hasActiveAllergies: true,
  }),
  booking(PRIYA, {
    id: id('2004'),
    number: 'AP2600204',
    status: 'Booked',
    startsAt: minutesAgo(40),
    reason: 'Knee pain',
  }),
  booking(FATIMA, {
    id: id('2005'),
    number: 'AP2600205',
    status: 'CheckedIn',
    startsAt: minutesAgo(10),
    visitType: 'FollowUp',
    reason: 'Thyroid follow-up',
    visitId: id('b15'),
    checkedInAt: minutesAgo(12),
    tokenLabel: 'GM-015',
  }),
  booking(HARISH, {
    id: id('2006'),
    number: 'AP2600206',
    status: 'Confirmed',
    startsAt: minutesAgo(-45),
    visitType: 'FollowUp',
    isTelehealth: true,
    reason: 'Diabetes follow-up',
    hasCriticalAlerts: true,
  }),
];

const WORKLIST: EmrWorklist = {
  date: todayIso(),
  practitionerId: DOCTOR_ID,
  departmentId: GENERAL_MEDICINE.id,
  queue: QUEUE,
  appointments: APPOINTMENTS,
  openEncounters: [
    {
      id: ENCOUNTER_ID,
      ...who(ASHA),
      type: 'OutPatient',
      startedAt: minutesAgo(20),
      chiefComplaint: 'Fever for 3 days with body ache',
    },
    { id: id('e03'), ...who(DEEPA), type: 'OutPatient', startedAt: minutesAgo(DAY + 90), chiefComplaint: 'Migraine' },
  ],
  unsignedNotes: [
    { id: id('3001'), ...who(KAMALA), type: 'ProgressNote', title: 'Ward round, day 2', createdAt: minutesAgo(200) },
    { id: id('3002'), ...who(SURESH), type: 'Procedure', title: null, createdAt: minutesAgo(130) },
  ],
  draftPrescriptions: [
    { id: DRAFT_RX_ID, prescriptionNumber: 'RX2600311', ...who(ASHA), prescribedAt: minutesAgo(10), itemCount: 3 },
  ],
  resultsToReview: [
    {
      id: id('4001'),
      reportId: id('4011'),
      kind: 'Laboratory',
      title: 'Complete blood count',
      hasCriticalFindings: true,
      releasedAt: minutesAgo(25),
      ...who(KAMALA),
    },
    {
      id: id('4002'),
      reportId: id('4012'),
      kind: 'Radiology',
      title: 'X-ray chest PA view',
      hasCriticalFindings: false,
      releasedAt: minutesAgo(80),
      ...who(RAVI),
    },
  ],
  chartDeficiencies: [
    {
      id: id('5001'),
      medicalRecordId: id('5011'),
      type: 'UnsignedEncounter',
      description: 'OP consultation of yesterday is not signed',
      dueDate: todayIso(2),
      ...who(DEEPA),
    },
  ],
};

const inpatient = (
  patient: Person,
  overrides: Partial<WardRoundPatient> & Pick<WardRoundPatient, 'admissionId' | 'admissionNumber' | 'bedLabel'>,
): WardRoundPatient => ({
  ...patient,
  wardId: id('6101'),
  wardName: 'Female medical ward',
  isCriticalCare: false,
  status: 'Admitted',
  admittedAt: minutesAgo(3 * DAY),
  dayOfStay: 3,
  expectedDischargeDate: null,
  provisionalDiagnosis: null,
  isMlc: false,
  isRestricted: false,
  consultantId: DOCTOR_ID,
  consultantName: 'Dr. Meera Iyer',
  isPrimaryConsultant: true,
  hasActiveAllergies: false,
  hasCriticalAlerts: false,
  lastVisitAt: null,
  seenToday: false,
  lastNoteAt: null,
  resultsToReview: 0,
  hasCriticalResults: false,
  vitalsAt: null,
  vitalsSeverity: null,
  vitalsFlags: [],
  ...overrides,
});

const ROUND: WardRound = {
  date: todayIso(),
  practitionerId: DOCTOR_ID,
  departmentId: null,
  patients: [
    inpatient(KAMALA, {
      admissionId: id('6001'),
      admissionNumber: 'IP2600341',
      bedLabel: 'FMW-04',
      admittedAt: minutesAgo(2 * DAY),
      dayOfStay: 2,
      provisionalDiagnosis: 'Community-acquired pneumonia',
      hasActiveAllergies: true,
      lastNoteAt: minutesAgo(200),
      resultsToReview: 2,
      hasCriticalResults: true,
      vitalsAt: minutesAgo(40),
      vitalsSeverity: 'Critical',
      vitalsFlags: ['SpO₂ 88% is critically low.', 'Respiratory rate 28/min is high.'],
    }),
    inpatient(GOPAL, {
      admissionId: id('6002'),
      admissionNumber: 'IP2600322',
      bedLabel: 'MMW-11',
      wardId: id('6102'),
      wardName: 'Male medical ward',
      admittedAt: minutesAgo(5 * DAY),
      dayOfStay: 5,
      provisionalDiagnosis: 'Acute exacerbation of COPD',
      expectedDischargeDate: todayIso(1),
      lastVisitAt: minutesAgo(90),
      seenToday: true,
      lastNoteAt: minutesAgo(85),
      vitalsAt: minutesAgo(30),
    }),
    inpatient(VIJAY, {
      admissionId: id('6003'),
      admissionNumber: 'IP2600355',
      bedLabel: 'ICU-02',
      wardId: id('6103'),
      wardName: 'Medical ICU',
      isCriticalCare: true,
      status: 'DischargeInitiated',
      admittedAt: minutesAgo(4 * DAY),
      dayOfStay: 4,
      provisionalDiagnosis: 'Organophosphate poisoning',
      isMlc: true,
      consultantId: id('d2'),
      consultantName: 'Dr. Anil Rao',
      isPrimaryConsultant: false,
      hasCriticalAlerts: true,
      lastVisitAt: minutesAgo(DAY + 60),
      vitalsAt: minutesAgo(50),
      vitalsSeverity: 'Abnormal',
      vitalsFlags: ['Pulse 112/min is high.'],
    }),
  ],
};

// ---------------------------------------------------------------------------------------------------- encounter

const VITAL_FLAGS: VitalFlag[] = [
  {
    parameter: 'temperature',
    value: 38.9,
    direction: 'High',
    severity: 'Abnormal',
    message: 'Temperature 38.9 °C is high.',
  },
  { parameter: 'pulse', value: 104, direction: 'High', severity: 'Abnormal', message: 'Pulse 104/min is high.' },
  {
    parameter: 'bloodGlucose',
    value: 182,
    direction: 'High',
    severity: 'Abnormal',
    message: 'Random blood glucose 182 mg/dL is high.',
  },
];

const SUMMARY: PatientClinicalSummary = {
  patient: {
    id: ASHA.patientId,
    uhid: ASHA.uhid,
    fullName: 'Asha Devi',
    gender: 'Female',
    dateOfBirth: '1987-03-14',
    age: '39Y',
    phone: '9876543210',
    bloodGroup: 'BPositive',
    category: 'General',
    isVip: false,
    isDeceased: false,
    photoFileId: null,
  },
  allergiesEverRecorded: true,
  allergyStatus: 'Recorded',
  allergies: [
    { id: id('a11'), allergen: 'Penicillin', type: 'Drug', severity: 'Anaphylaxis', reaction: 'Lip swelling, wheeze' },
    { id: id('a12'), allergen: 'Sulfonamides', type: 'Drug', severity: 'Moderate', reaction: 'Rash' },
  ],
  alerts: [
    {
      id: id('a21'),
      type: 'FallRisk',
      title: 'Fall risk',
      detail: 'Giddy on standing',
      severity: 'Warning',
      expiresOn: null,
    },
    {
      id: id('a22'),
      type: 'Custom',
      title: 'Hard of hearing',
      detail: 'Face the patient and speak slowly',
      severity: 'Info',
      expiresOn: null,
    },
  ],
  activeProblems: [
    {
      id: id('a31'),
      icdCode: 'E11.9',
      title: 'Type 2 diabetes mellitus without complications',
      type: 'Final',
      status: 'Chronic',
      onsetDate: '2019-06-01',
    },
    {
      id: id('a32'),
      icdCode: 'I10',
      title: 'Essential (primary) hypertension',
      type: 'Final',
      status: 'Chronic',
      onsetDate: '2021-01-15',
    },
  ],
  currentMedications: [
    {
      prescriptionId: id('c03'),
      prescriptionNumber: 'RX2600188',
      itemId: id('c31'),
      drugName: 'Glycomet 500',
      genericName: 'Metformin',
      strength: '500 mg',
      dose: '1 tab',
      frequency: 'BD',
      durationDays: 90,
      startedOn: todayIso(-50),
      endsOn: todayIso(40),
      practitionerId: id('d2'),
      prescriberName: 'Dr. Anil Rao',
      isControlledDrug: false,
    },
    {
      prescriptionId: id('c03'),
      prescriptionNumber: 'RX2600188',
      itemId: id('c32'),
      drugName: 'Telma 40',
      genericName: 'Telmisartan',
      strength: '40 mg',
      dose: '1 tab',
      frequency: 'OD',
      durationDays: 90,
      startedOn: todayIso(-50),
      endsOn: todayIso(40),
      practitionerId: id('d2'),
      prescriberName: 'Dr. Anil Rao',
      isControlledDrug: false,
    },
  ],
  lastVitals: {
    id: id('f101'),
    recordedAt: minutesAgo(15),
    temperatureCelsius: 38.9,
    pulse: 104,
    respiratoryRate: 20,
    systolicBp: 118,
    diastolicBp: 76,
    spO2: 97,
    weightKg: 58,
    bmi: 23.8,
    painScore: 4,
    bloodGlucose: 182,
    gcs: null,
    flags: VITAL_FLAGS,
  },
  recentEncounters: [
    {
      id: id('e05'),
      type: 'OutPatient',
      status: 'Signed',
      startedAt: minutesAgo(50 * DAY),
      practitionerName: 'Dr. Anil Rao',
      departmentName: 'General medicine',
      chiefComplaint: 'Diabetes review',
      diagnoses: ['E11.9 Type 2 diabetes mellitus without complications'],
    },
  ],
  resultsAwaitingReview: 1,
};

/** The diabetes review of the summary's recent encounters, as the consultation's previous visit. */
const PREVIOUS_VISIT: PreviousVisitSummary = {
  encounterId: id('e05'),
  type: 'OutPatient',
  status: 'Signed',
  startedAt: minutesAgo(50 * DAY),
  practitionerName: 'Dr. Anil Rao',
  departmentName: 'General medicine',
  chiefComplaint: 'Diabetes review',
  diagnoses: ['E11.9 Type 2 diabetes mellitus without complications'],
  assessment: 'Type 2 diabetes, fair control on metformin.',
  plan: 'Continue metformin. HbA1c before the next review.',
  followUpDate: todayIso(40),
  followUpAdvice: 'Walk 30 minutes a day; avoid sweets.',
  medicines: [
    {
      drugName: 'Glycomet 500 mg Tablet',
      strength: '500 mg',
      dose: '1 tablet',
      frequency: 'BD',
      durationDays: 90,
      instructions: 'After food',
    },
  ],
  vitals: {
    id: id('f100'),
    recordedAt: minutesAgo(50 * DAY),
    temperatureCelsius: 36.8,
    pulse: 78,
    respiratoryRate: 16,
    systolicBp: 134,
    diastolicBp: 84,
    spO2: 98,
    weightKg: 59,
    bmi: 24.2,
    painScore: null,
    bloodGlucose: null,
    gcs: null,
    flags: [],
  },
};

const ENCOUNTER: EncounterDetail = {
  id: ENCOUNTER_ID,
  patientId: ASHA.patientId,
  visitId: VISIT_ID,
  admissionId: null,
  practitionerId: DOCTOR_ID,
  practitionerName: 'Dr. Meera Iyer',
  departmentId: GENERAL_MEDICINE.id,
  departmentName: GENERAL_MEDICINE.name,
  type: 'OutPatient',
  status: 'InProgress',
  startedAt: minutesAgo(20),
  templateId: TEMPLATE_ID,
  signedAt: null,
  signedByPractitionerId: null,
  amendmentReason: null,
  amendedAt: null,
  amendments: [],
  version: 3,
  chiefComplaint: 'Fever for 3 days with body ache, headache',
  historyOfPresentIllness:
    'High-grade fever with chills for 3 days, relieved partly by paracetamol. Body ache and retro-orbital headache. ' +
    'Two episodes of vomiting today. No bleeding, no abdominal pain. Neighbour treated for dengue last week.',
  examination: 'Febrile (102 °F), pulse 104/min, BP 118/76 mmHg. Mild dehydration. No rash, no petechiae.',
  assessment: 'Acute febrile illness, probably dengue, without warning signs. Diabetes on metformin.',
  plan: 'CBC and NS1 antigen today. Oral fluids, paracetamol only. Review with reports.',
  followUpDate: todayIso(2),
  followUpAdvice: 'Come back at once for bleeding, persistent vomiting or abdominal pain.',
  referralDepartmentId: null,
  referralPractitionerId: null,
  referralExternal: null,
  referralReason: null,
};

const VITALS: VitalsDto[] = [
  {
    id: id('f101'),
    patientId: ASHA.patientId,
    encounterId: ENCOUNTER_ID,
    recordedAt: minutesAgo(15),
    recordedByUserId: id('abc1'),
    temperature: 102,
    temperatureUnit: 'Fahrenheit',
    temperatureCelsius: 38.9,
    pulse: 104,
    respiratoryRate: 20,
    systolicBp: 118,
    diastolicBp: 76,
    spO2: 97,
    onSupplementalOxygen: false,
    heightCm: 156,
    weightKg: 58,
    bmi: 23.8,
    painScore: 4,
    bloodGlucose: 182,
    glucoseTiming: 'Random',
    gcs: null,
    notes: 'At triage',
    isEnteredInError: false,
    enteredInErrorReason: null,
    flags: VITAL_FLAGS,
  },
];

const diagnosis = (
  overrides: Partial<DiagnosisDto> & Pick<DiagnosisDto, 'id' | 'icdCode' | 'title' | 'type' | 'status'>,
): DiagnosisDto => ({
  patientId: ASHA.patientId,
  encounterId: ENCOUNTER_ID,
  isPrimary: false,
  onsetDate: null,
  resolvedOn: null,
  recordedAt: minutesAgo(8),
  notes: null,
  version: 1,
  ...overrides,
});

const DIAGNOSES: DiagnosisDto[] = [
  diagnosis({
    id: id('dd1'),
    icdCode: 'A90',
    title: 'Dengue fever [classical dengue]',
    type: 'Provisional',
    status: 'Active',
    isPrimary: true,
    onsetDate: todayIso(-3),
    notes: 'NS1 antigen awaited',
  }),
  diagnosis({
    id: id('dd2'),
    icdCode: 'E11.9',
    title: 'Type 2 diabetes mellitus without complications',
    type: 'Final',
    status: 'Chronic',
    onsetDate: '2019-06-01',
  }),
  diagnosis({ id: id('dd3'), icdCode: null, title: 'Mild dehydration', type: 'Provisional', status: 'Active' }),
];

const CBC = {
  serviceItemId: id('ce1'),
  code: 'LAB-CBC',
  name: 'Complete blood count (CBC)',
  category: 'Laboratory',
} as const;
const NS1 = { serviceItemId: id('ce2'), code: 'LAB-NS1', name: 'Dengue NS1 antigen', category: 'Laboratory' } as const;
const MALARIA = {
  serviceItemId: id('ce3'),
  code: 'LAB-MPQBC',
  name: 'Malaria parasite (QBC)',
  category: 'Laboratory',
} as const;

const orderLine = (
  overrides: Partial<OrderItemDto> & Pick<OrderItemDto, 'id' | 'serviceItemId' | 'serviceCode' | 'serviceName'>,
): OrderItemDto => ({
  category: 'Laboratory',
  quantity: 1,
  status: 'Ordered',
  notes: null,
  statusChangedAt: null,
  completedAt: null,
  resultReference: null,
  cancelReason: null,
  ...overrides,
});

const ORDERS: OrderDto[] = [
  {
    id: id('0a1'),
    orderNumber: 'ORD2600457',
    patientId: ASHA.patientId,
    encounterId: ENCOUNTER_ID,
    visitId: VISIT_ID,
    admissionId: null,
    orderingPractitionerId: DOCTOR_ID,
    orderingPractitionerName: 'Dr. Meera Iyer',
    priority: 'Urgent',
    status: 'Active',
    source: 'Encounter',
    orderedAt: minutesAgo(9),
    clinicalNotes: '? Dengue, fever for 3 days',
    items: [
      orderLine({
        id: id('0b1'),
        serviceItemId: CBC.serviceItemId,
        serviceCode: CBC.code,
        serviceName: CBC.name,
        status: 'InProgress',
        statusChangedAt: minutesAgo(5),
      }),
      orderLine({
        id: id('0b2'),
        serviceItemId: NS1.serviceItemId,
        serviceCode: NS1.code,
        serviceName: NS1.name,
        notes: 'Same sample as the CBC',
      }),
      orderLine({
        id: id('0b3'),
        serviceItemId: id('ce5'),
        serviceCode: 'RAD-USGABD',
        serviceName: 'Ultrasound abdomen',
        category: 'Radiology',
        status: 'Cancelled',
        statusChangedAt: minutesAgo(6),
        cancelReason: 'Not needed today',
      }),
    ],
  },
  {
    id: id('0a2'),
    orderNumber: 'ORD2600459',
    patientId: ASHA.patientId,
    encounterId: ENCOUNTER_ID,
    visitId: VISIT_ID,
    admissionId: null,
    orderingPractitionerId: DOCTOR_ID,
    orderingPractitionerName: 'Dr. Meera Iyer',
    priority: 'Routine',
    status: 'Active',
    source: 'Encounter',
    orderedAt: minutesAgo(5),
    clinicalNotes: null,
    items: [
      orderLine({
        id: id('0b4'),
        serviceItemId: id('ce6'),
        serviceCode: 'PRC-IVCAN',
        serviceName: 'IV cannulation',
        category: 'Procedure',
      }),
    ],
  },
];

const favourite = (
  overrides: Partial<FavouriteDto> & Pick<FavouriteDto, 'id' | 'kind' | 'label' | 'displayOrder'>,
): FavouriteDto => ({
  serviceItemId: null,
  drugItemId: null,
  genericName: null,
  strength: null,
  dosageForm: null,
  route: null,
  dose: null,
  frequency: null,
  customFrequency: null,
  durationDays: null,
  instructions: null,
  isControlledDrug: false,
  ...overrides,
});

const ORDER_FAVOURITES: FavouriteDto[] = [
  favourite({ id: id('f1'), kind: 'Order', label: 'CBC', serviceItemId: CBC.serviceItemId, displayOrder: 1 }),
  favourite({ id: id('f2'), kind: 'Order', label: 'HbA1c', serviceItemId: id('ce4'), displayOrder: 2 }),
];

const DRUG_FAVOURITES: FavouriteDto[] = [
  favourite({
    id: id('f3'),
    kind: 'Drug',
    label: 'Dolo 650',
    displayOrder: 1,
    drugItemId: id('c0d1'),
    genericName: 'Paracetamol',
    strength: '650 mg',
    dosageForm: 'Tablet',
    route: 'Oral',
    dose: '1 tab',
    frequency: 'TDS',
    durationDays: 3,
    instructions: 'After food',
  }),
  favourite({
    id: id('f4'),
    kind: 'Drug',
    label: 'Pan 40',
    displayOrder: 2,
    drugItemId: id('c0d3'),
    genericName: 'Pantoprazole',
    strength: '40 mg',
    dosageForm: 'Tablet',
    route: 'Oral',
    dose: '1 tab',
    frequency: 'OD',
    durationDays: 5,
    instructions: 'Before breakfast',
  }),
];

const ORDER_SETS: OrderSetDto[] = [
  {
    id: id('0c1'),
    code: 'GM-FEVER',
    name: 'Fever work-up',
    specialtyCode: 'GM',
    description: 'Acute febrile illness, first visit',
    services: [CBC, NS1, MALARIA],
    isActive: true,
    version: 1,
  },
];

const TEMPLATES: TemplateListItem[] = [
  {
    id: TEMPLATE_ID,
    code: 'GM-FEVER',
    name: 'Acute febrile illness',
    specialtyCode: 'GM',
    isPersonal: false,
    displayOrder: 1,
    isActive: true,
  },
  {
    id: id('7e02'),
    code: 'GM-DM',
    name: 'Diabetes review',
    specialtyCode: 'GM',
    isPersonal: false,
    displayOrder: 2,
    isActive: true,
  },
  {
    id: id('7e03'),
    code: 'MI-URTI',
    name: 'My URTI note',
    specialtyCode: null,
    isPersonal: true,
    displayOrder: 3,
    isActive: true,
  },
];

const TEMPLATE: TemplateDto = {
  id: TEMPLATE_ID,
  code: 'GM-FEVER',
  name: 'Acute febrile illness',
  specialtyCode: 'GM',
  ownerPractitionerId: null,
  chiefComplaint: 'Fever',
  historyOfPresentIllness: null,
  examination: 'Conscious, oriented. Hydration, rash, petechiae, tourniquet test.',
  assessment: null,
  plan: null,
  followUpAdvice: 'Come back at once for bleeding, persistent vomiting or abdominal pain.',
  followUpDays: 2,
  commonComplaints: ['Fever', 'Body ache', 'Headache', 'Vomiting', 'Rash'],
  defaultOrders: [CBC, NS1],
  medications: [
    {
      id: id('7e11'),
      drugItemId: id('c0d1'),
      drugName: 'Dolo 650',
      genericName: 'Paracetamol',
      strength: '650 mg',
      dosageForm: 'Tablet',
      route: 'Oral',
      dose: '1 tab',
      frequency: 'TDS',
      customFrequency: null,
      durationDays: 3,
      instructions: 'After food',
    },
  ],
  displayOrder: 1,
  isActive: true,
  version: 2,
};

const rxLine = (
  overrides: Partial<PrescriptionItemDto> &
    Pick<
      PrescriptionItemDto,
      'id' | 'sequence' | 'drugName' | 'dose' | 'frequency' | 'frequencyText' | 'durationDays' | 'quantity'
    >,
): PrescriptionItemDto => ({
  drugItemId: null,
  genericName: null,
  strength: null,
  dosageForm: 'Tablet',
  route: 'Oral',
  customFrequency: null,
  dosesPerDay: null,
  instructions: null,
  isControlledDrug: false,
  controlledSchedule: null,
  ...overrides,
});

const DRAFT_RX: PrescriptionDto = {
  id: DRAFT_RX_ID,
  prescriptionNumber: 'RX2600311',
  patientId: ASHA.patientId,
  encounterId: ENCOUNTER_ID,
  visitId: VISIT_ID,
  admissionId: null,
  practitionerId: DOCTOR_ID,
  practitionerName: 'Dr. Meera Iyer',
  isInpatient: false,
  status: 'Draft',
  prescribedAt: minutesAgo(10),
  signedAt: null,
  notes: 'Give the ORS as sachets.',
  advice: 'Drink 3 litres of fluid a day. No painkillers other than paracetamol.',
  refillsAllowed: 0,
  refillsRemaining: 0,
  refillOfPrescriptionId: null,
  safetyOverrideReason: null,
  cancelledAt: null,
  cancelReason: null,
  items: [
    rxLine({
      id: id('c11'),
      sequence: 1,
      drugItemId: id('c0d1'),
      drugName: 'Dolo 650',
      genericName: 'Paracetamol',
      strength: '650 mg',
      dose: '1 tab',
      frequency: 'TDS',
      frequencyText: '1-1-1',
      dosesPerDay: 3,
      durationDays: 3,
      quantity: 9,
      instructions: 'After food',
    }),
    rxLine({
      id: id('c12'),
      sequence: 2,
      drugItemId: id('c0d2'),
      drugName: 'Electral ORS',
      genericName: 'Oral rehydration salts',
      strength: '21.8 g',
      dosageForm: 'Sachet',
      dose: '1 sachet',
      frequency: 'TDS',
      frequencyText: '1-1-1',
      dosesPerDay: 3,
      durationDays: 3,
      quantity: 9,
      instructions: 'In 1 litre of water',
    }),
    rxLine({
      id: id('c13'),
      sequence: 3,
      drugItemId: id('c0d3'),
      drugName: 'Pan 40',
      genericName: 'Pantoprazole',
      strength: '40 mg',
      dose: '1 tab',
      frequency: 'OD',
      frequencyText: '1-0-0',
      dosesPerDay: 1,
      durationDays: 5,
      quantity: 5,
      instructions: 'Before breakfast',
    }),
  ],
  version: 2,
};

const PRESCRIPTIONS: PrescriptionListItem[] = [
  {
    id: DRAFT_RX_ID,
    prescriptionNumber: DRAFT_RX.prescriptionNumber,
    patientId: ASHA.patientId,
    status: 'Draft',
    isInpatient: false,
    prescribedAt: DRAFT_RX.prescribedAt,
    signedAt: null,
    practitionerId: DOCTOR_ID,
    practitionerName: 'Dr. Meera Iyer',
    drugs: ['Dolo 650', 'Electral ORS', 'Pan 40'],
  },
  {
    id: SIGNED_RX_ID,
    prescriptionNumber: 'RX2600309',
    patientId: ASHA.patientId,
    status: 'Signed',
    isInpatient: false,
    prescribedAt: minutesAgo(18),
    signedAt: minutesAgo(17),
    practitionerId: DOCTOR_ID,
    practitionerName: 'Dr. Meera Iyer',
    drugs: ['Ondem 4 (ondansetron 4 mg)'],
  },
];

const note = (
  overrides: Partial<ClinicalNoteDto> & Pick<ClinicalNoteDto, 'id' | 'type' | 'title' | 'body' | 'createdAt'>,
): ClinicalNoteDto => ({
  patientId: ASHA.patientId,
  encounterId: ENCOUNTER_ID,
  admissionId: null,
  authorUserId: STAFF_USER.id,
  authorPractitionerId: DOCTOR_ID,
  authorName: 'Dr. Meera Iyer',
  amendsNoteId: null,
  isSigned: false,
  signedAt: null,
  version: 1,
  ...overrides,
});

const NOTES: ClinicalNoteDto[] = [
  note({
    id: id('3003'),
    type: 'Counselling',
    title: 'Dengue warning signs explained',
    body:
      'Counselled: patient and husband (Ramesh)\nTopics: warning signs, fluids, avoiding painkillers\n' +
      'Questions answered: yes\nUnderstanding confirmed: yes',
    createdAt: minutesAgo(7),
    isSigned: true,
    signedAt: minutesAgo(6),
    version: 2,
  }),
  note({
    id: id('3004'),
    type: 'Procedure',
    title: 'IV cannulation',
    body: 'Procedure: IV cannulation, left forearm, 20 G\nComplications: none',
    createdAt: minutesAgo(4),
  }),
];

/** The pharmacy's drug master, as the prescriber's search finds it, with the stock on hand. */
const DRUGS: PrescribableDrug[] = [
  {
    id: id('c0d1'),
    code: 'DOLO650',
    name: 'Dolo 650',
    genericName: 'Paracetamol',
    brandName: 'Dolo',
    strength: '650 mg',
    dosageForm: 'Tablet',
    schedule: 'H',
    isControlled: false,
    controlledSchedule: null,
    requiresPrescription: true,
    isColdChain: false,
    baseUnit: 'Tablet',
    available: 1240,
    nearestExpiry: '2027-08-31',
  },
  {
    id: id('c0d2'),
    code: 'ORS-ELECTRAL',
    name: 'Electral ORS',
    genericName: 'Oral rehydration salts',
    brandName: 'Electral',
    strength: '21.8 g',
    dosageForm: 'Sachet',
    schedule: 'Otc',
    isControlled: false,
    controlledSchedule: null,
    requiresPrescription: false,
    isColdChain: false,
    baseUnit: 'Sachet',
    available: 310,
    nearestExpiry: '2027-03-31',
  },
  {
    id: id('c0d3'),
    code: 'PAN40',
    name: 'Pan 40',
    genericName: 'Pantoprazole',
    brandName: 'Pan',
    strength: '40 mg',
    dosageForm: 'Tablet',
    schedule: 'H',
    isControlled: false,
    controlledSchedule: null,
    requiresPrescription: true,
    isColdChain: false,
    baseUnit: 'Tablet',
    available: 0,
    nearestExpiry: null,
  },
];

const AI_STATUS: AiStatusDto = {
  enabled: true,
  provider: 'Ollama',
  model: 'llama3.1:8b',
  reason: null,
  serverSpeechToText: true,
  speechProvider: 'Whisper',
  speechReason: null,
  timeoutSeconds: 90,
};

const LETTERHEAD: Letterhead = { name: 'Nana Hospitals, Pune' };

const patientPath = `/api/v1/emr/patients/${ASHA.patientId}`;

/** Today's visit, whose payer the banner shows. */
const VISIT: VisitListItem = {
  id: '0192a000-0000-7000-8000-0000000000e1',
  visitNumber: 'OP2600981',
  ...ASHA,
  type: 'OutPatient',
  status: 'InProgress',
  departmentId: null,
  departmentName: 'General Medicine',
  practitionerId: null,
  practitionerName: 'Dr. Meera Iyer',
  visitedAt: new Date().toISOString(),
  completedAt: null,
  isFollowUp: false,
  isFreeFollowUp: false,
  payerType: 'Self',
  isMlc: false,
  tokenLabel: 'GM-12',
  tokenStatus: 'InService',
};

/** What the worklist and the consultation ask the server for. */
export const CLINICAL_ANSWERS: FakeAnswers = {
  'GET /api/v1/masters/lookups/departments': DEPARTMENTS,
  'GET /api/v1/masters/lookups/practitioners': DOCTORS,
  'GET /api/v1/print/letterhead': LETTERHEAD,
  'GET /api/v1/emr/worklist': WORKLIST,
  'GET /api/v1/inpatient/rounds': ROUND,
  [`GET /api/v1/emr/encounters/${ENCOUNTER_ID}`]: ENCOUNTER,
  [`GET /api/v1/emr/encounters/${ENCOUNTER_ID}/previous-visit`]: {
    previous: PREVIOUS_VISIT,
  } satisfies PreviousVisitResponse,
  [`GET ${patientPath}/summary`]: SUMMARY,
  [`GET ${patientPath}/vitals`]: paged(VITALS),
  [`GET ${patientPath}/diagnoses`]: DIAGNOSES,
  [`GET ${patientPath}/notes`]: paged(NOTES),
  [`GET /api/v1/patients/${ASHA.patientId}/visits`]: paged([VISIT]),
  [`GET /api/v1/patients/${ASHA.patientId}/payer`]: {
    current: {
      visitId: VISIT.id,
      isCurrentVisit: true,
      payerType: 'Self',
      payerId: null,
      payerName: null,
      policyNumber: null,
      validUntil: null,
    },
  },
  'GET /api/v1/emr/templates': TEMPLATES,
  [`GET /api/v1/emr/templates/${TEMPLATE_ID}`]: TEMPLATE,
  'GET /api/v1/emr/favourites': (request: FakeRequest) =>
    request.url.searchParams.get('kind') === 'Drug' ? DRUG_FAVOURITES : ORDER_FAVOURITES,
  'GET /api/v1/clinical/orders': paged(ORDERS),
  'GET /api/v1/clinical/order-sets': ORDER_SETS,
  'GET /api/v1/clinical/prescriptions': paged(PRESCRIPTIONS),
  [`GET /api/v1/clinical/prescriptions/${DRAFT_RX_ID}`]: DRAFT_RX,
  'GET /api/v1/emr/drugs': (request: FakeRequest) => {
    const search = (request.url.searchParams.get('search') ?? '').toLowerCase();
    return DRUGS.filter((drug) => `${drug.name} ${drug.genericName}`.toLowerCase().includes(search));
  },
  'GET /api/v1/ai/status': AI_STATUS,
};
