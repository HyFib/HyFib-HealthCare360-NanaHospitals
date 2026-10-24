import { todayIso } from '@hc360/core';
import type {
  ClinicalNoteDto,
  DiagnosisDto,
  DischargeSummaryDto,
  EncounterDetail,
  PatientClinicalSummary,
  PatientFlags,
  PreviousVisitResponse,
} from '@/modules/emr/api';
import type {
  ControlledDrugRegisterDto,
  DoseLimitDto,
  DrugInteractionDto,
  PrescriptionDigitalSignatureDto,
  PrescriptionDto,
  PrescriptionItemDto,
} from '@/modules/emr/prescriptionsApi';
import type { TimelineEntry } from '@/modules/emr/timeline';
import { ASHA } from '../accessibility/fixtures/clinical';
import { paged, STAFF_USER, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// The doctor's record screens the consultation fixture (accessibility/fixtures/clinical.ts) does not feed: the chart of
// the patient opened as RECORD_ID (Sunil Deshmukh, admitted) with his discharge summary in draft, an open encounter and
// a signed prescription of Asha Devi (whose chart that fixture answers), and the drug safety tables and the
// controlled-drug register.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const DAY = 24 * 60;

const DOCTOR_ID = STAFF_USER.practitionerId!;
const SUNIL = { patientId: RECORD_ID, uhid: 'NH26000187', patientName: 'Sunil Vasant Deshmukh' };
const ADMISSION_ID = RECORD_ID;

const FLAGS: PatientFlags = {
  allergyStatus: 'Recorded',
  allergies: [{ id: id('8a01'), allergen: 'Aspirin', type: 'Drug', severity: 'Moderate', reaction: 'Urticaria' }],
  alerts: [
    {
      id: id('8a02'),
      type: 'FallRisk',
      title: 'Fall risk',
      detail: 'Post-operative, on opioids',
      severity: 'Warning',
      expiresOn: null,
    },
  ],
};

const SUMMARY: PatientClinicalSummary = {
  patient: {
    id: RECORD_ID,
    uhid: SUNIL.uhid,
    fullName: SUNIL.patientName,
    gender: 'Male',
    dateOfBirth: '1971-08-22',
    age: '55Y',
    phone: '+919822054321',
    bloodGroup: 'OPositive',
    category: 'General',
    isVip: false,
    isDeceased: false,
    photoFileId: null,
  },
  allergiesEverRecorded: true,
  ...FLAGS,
  activeProblems: [
    {
      id: id('8b01'),
      icdCode: 'K80.20',
      title: 'Calculus of gallbladder without cholecystitis',
      type: 'Final',
      status: 'Active',
      onsetDate: todayIso(-30),
    },
    {
      id: id('8b02'),
      icdCode: 'E11.9',
      title: 'Type 2 diabetes mellitus',
      type: 'Final',
      status: 'Chronic',
      onsetDate: '2016-04-01',
    },
  ],
  currentMedications: [
    {
      prescriptionId: id('8c01'),
      prescriptionNumber: 'RX2600402',
      itemId: id('8c11'),
      drugName: 'Pan 40',
      genericName: 'Pantoprazole',
      strength: '40 mg',
      dose: '1 tab',
      frequency: 'OD',
      durationDays: 5,
      startedOn: todayIso(-2),
      endsOn: todayIso(3),
      practitionerId: DOCTOR_ID,
      prescriberName: 'Dr. Meera Iyer',
      isControlledDrug: false,
    },
  ],
  lastVitals: {
    id: id('8d01'),
    recordedAt: minutesAgo(90),
    temperatureCelsius: 37.1,
    pulse: 82,
    respiratoryRate: 16,
    systolicBp: 128,
    diastolicBp: 80,
    spO2: 98,
    weightKg: 74,
    bmi: 25.6,
    painScore: 3,
    bloodGlucose: 142,
    gcs: 15,
    flags: [],
  },
  recentEncounters: [
    {
      id: id('8e01'),
      type: 'InPatient',
      status: 'Signed',
      startedAt: minutesAgo(3 * DAY),
      practitionerName: 'Dr. Meera Iyer',
      departmentName: 'General surgery',
      chiefComplaint: 'Recurrent right upper abdominal pain',
      diagnoses: ['Calculus of gallbladder without cholecystitis'],
    },
  ],
  resultsAwaitingReview: 1,
};

const TIMELINE: TimelineEntry[] = [
  {
    type: 'Encounter',
    id: id('8e01'),
    occurredAt: minutesAgo(3 * DAY),
    title: 'In-patient admission note',
    summary: 'Admitted for elective laparoscopic cholecystectomy.',
    status: 'Signed',
    encounterId: id('8e01'),
    isCritical: false,
    reportKind: null,
  },
  {
    type: 'Vitals',
    id: id('8d01'),
    occurredAt: minutesAgo(90),
    title: 'Vitals',
    summary: 'BP 128/80, pulse 82, SpO₂ 98%',
    status: null,
    encounterId: null,
    isCritical: false,
    reportKind: null,
  },
];

const DIAGNOSES: DiagnosisDto[] = [];
const NOTES: ClinicalNoteDto[] = [];

const DISCHARGE: DischargeSummaryDto = {
  id: id('8f01'),
  admissionId: ADMISSION_ID,
  patientId: RECORD_ID,
  encounterId: id('8e01'),
  practitionerId: DOCTOR_ID,
  practitionerName: 'Dr. Meera Iyer',
  admittedAt: minutesAgo(3 * DAY),
  dischargedAt: null,
  status: 'Draft',
  signedAt: null,
  signedByPractitionerId: null,
  amendmentReason: null,
  amendedAt: null,
  amendmentCount: 0,
  version: 2,
  diagnoses: 'Calculus of gallbladder without cholecystitis. Type 2 diabetes mellitus.',
  diagnosisCodes: ['K80.20', 'E11.9'],
  presentingComplaints: 'Recurrent right upper abdominal pain after fatty meals for 3 months.',
  courseInHospital:
    'Laparoscopic cholecystectomy under GA on day 2; uneventful. Started orally on day 3. Sugars controlled on insulin sliding scale.',
  procedures: 'Laparoscopic cholecystectomy',
  investigationsSummary: 'USG abdomen: multiple gallbladder calculi. LFT normal. HbA1c 7.4%.',
  conditionAtDischarge: 'Stable, afebrile, tolerating diet, port sites healthy.',
  medications: 'Tab Pan 40 OD x 5 days; Tab Dolo 650 SOS; continue metformin 500 BD.',
  advice: 'Soft low-fat diet for 2 weeks; no heavy lifting for 4 weeks.',
  followUpDate: todayIso(7),
  followUpInstructions: 'Surgery OPD with the histopathology report; suture removal on day 7.',
};

const ENCOUNTER: EncounterDetail = {
  id: RECORD_ID,
  patientId: ASHA.patientId,
  visitId: id('8f11'),
  admissionId: null,
  practitionerId: DOCTOR_ID,
  practitionerName: 'Dr. Meera Iyer',
  departmentId: '0192a000-0000-7000-8000-00000000de01',
  departmentName: 'General medicine',
  type: 'OutPatient',
  status: 'InProgress',
  startedAt: minutesAgo(12),
  templateId: null,
  signedAt: null,
  signedByPractitionerId: null,
  amendmentReason: null,
  amendedAt: null,
  amendments: [],
  version: 1,
  chiefComplaint: 'Follow-up of diabetes and blood pressure',
  historyOfPresentIllness: 'Home sugars 130–160 fasting. No hypoglycaemia. Occasional morning headache.',
  examination: 'BP 142/88 mmHg, pulse 78/min. Feet: sensation intact, no ulcers.',
  assessment: 'Type 2 diabetes, fair control. Hypertension, not at target.',
  plan: 'HbA1c and lipid profile. Increase telmisartan to 80 mg. Review in 4 weeks.',
  followUpDate: todayIso(28),
  followUpAdvice: 'Walk 30 minutes a day; low-salt diet.',
  referralDepartmentId: null,
  referralPractitionerId: null,
  referralExternal: null,
  referralReason: null,
};

const rxLine = (
  n: string,
  sequence: number,
  drugName: string,
  genericName: string,
  strength: string,
  frequency: PrescriptionItemDto['frequency'],
  frequencyText: string,
  durationDays: number,
  quantity: number,
): PrescriptionItemDto => ({
  id: id(n),
  sequence,
  drugItemId: id(`${n}0`),
  drugName,
  genericName,
  strength,
  dosageForm: 'Tablet',
  route: 'Oral',
  dose: '1 tab',
  frequency,
  customFrequency: null,
  dosesPerDay: null,
  frequencyText,
  durationDays,
  quantity,
  instructions: 'After food',
  isControlledDrug: false,
  controlledSchedule: null,
});

const PRESCRIPTION: PrescriptionDto = {
  id: RECORD_ID,
  prescriptionNumber: 'RX2600188',
  patientId: ASHA.patientId,
  encounterId: id('8f21'),
  visitId: id('8f22'),
  admissionId: null,
  practitionerId: id('d2'),
  practitionerName: 'Dr. Anil Rao',
  isInpatient: false,
  status: 'Signed',
  prescribedAt: minutesAgo(50 * DAY),
  signedAt: minutesAgo(50 * DAY),
  notes: null,
  advice: 'Check fasting sugar twice a week.',
  refillsAllowed: 2,
  refillsRemaining: 1,
  refillOfPrescriptionId: null,
  safetyOverrideReason: null,
  cancelledAt: null,
  cancelReason: null,
  items: [
    rxLine('8c31', 1, 'Glycomet 500', 'Metformin', '500 mg', 'BD', 'Twice a day', 90, 180),
    rxLine('8c32', 2, 'Telma 40', 'Telmisartan', '40 mg', 'OD', 'Once a day', 90, 90),
  ],
  version: 2,
  isDigitallySigned: false,
  digitallySignedAt: null,
  digitalSignerName: null,
  finalCopyAvailable: true,
};

const SIGNATURE: PrescriptionDigitalSignatureDto = {
  state: 'NotSigned',
  policy: 'Optional',
  signedAt: null,
  signerName: null,
  signerSubject: null,
  certificateSerialNumber: null,
  certificateIssuer: null,
  certificateThumbprint: null,
  contentHash: null,
  contentMatchesRecord: false,
  chainStatus: null,
  chainTrusted: false,
  message: null,
  finalCopyAvailable: true,
};

const INTERACTIONS: DrugInteractionDto[] = [
  {
    id: id('8a11'),
    drugA: 'Clopidogrel',
    drugB: 'Omeprazole',
    severity: 'Major',
    effect: 'Omeprazole reduces the antiplatelet effect of clopidogrel.',
    management: 'Use pantoprazole instead.',
    isActive: true,
    version: 1,
  },
  {
    id: id('8a12'),
    drugA: 'Metformin',
    drugB: 'Iodinated contrast',
    severity: 'Moderate',
    effect: 'Risk of lactic acidosis if renal function worsens after contrast.',
    management: 'Hold metformin for 48 hours after contrast; check creatinine.',
    isActive: true,
    version: 2,
  },
];

const DOSE_LIMITS: DoseLimitDto[] = [
  {
    id: id('8a21'),
    genericName: 'Paracetamol',
    maxDailyDoseMg: 4000,
    route: 'Oral',
    notes: '2 g a day in liver disease.',
    isActive: true,
    version: 1,
  },
  {
    id: id('8a22'),
    genericName: 'Tramadol',
    maxDailyDoseMg: 400,
    route: null,
    notes: null,
    isActive: true,
    version: 1,
  },
];

const REGISTER: ControlledDrugRegisterDto[] = [
  {
    id: id('8a31'),
    prescriptionId: id('8a32'),
    prescriptionNumber: 'RX2600415',
    patientId: RECORD_ID,
    uhid: SUNIL.uhid,
    patientName: SUNIL.patientName,
    practitionerId: DOCTOR_ID,
    practitionerName: 'Dr. Meera Iyer',
    drugName: 'Ultracet',
    genericName: 'Tramadol + Paracetamol',
    strength: '37.5/325 mg',
    schedule: 'H1',
    quantity: 10,
    prescribedAt: minutesAgo(DAY),
    confirmedByUserId: STAFF_USER.id,
  },
];

const chart = `/api/v1/emr/patients/${RECORD_ID}`;

export const ANSWERS: FakeAnswers = {
  [`GET ${chart}/summary`]: SUMMARY,
  [`GET ${chart}/flags`]: FLAGS,
  // Ravi Kumar and Suresh Patil, whom the pharmacy, lab and physiotherapy records are about.
  'GET /api/v1/emr/patients/0192a000-0000-7000-8000-000000000002/flags': FLAGS,
  'GET /api/v1/emr/patients/0192a000-0000-7000-8000-000000000006/flags': FLAGS,
  [`GET ${chart}/timeline`]: paged(TIMELINE),
  [`GET ${chart}/diagnoses`]: DIAGNOSES,
  [`GET ${chart}/notes`]: paged(NOTES),
  [`GET /api/v1/clinical/discharge-summaries/by-admission/${ADMISSION_ID}`]: DISCHARGE,
  [`GET /api/v1/emr/encounters/${RECORD_ID}`]: ENCOUNTER,
  [`GET /api/v1/emr/encounters/${RECORD_ID}/previous-visit`]: { previous: null } satisfies PreviousVisitResponse,
  [`GET /api/v1/clinical/prescriptions/${RECORD_ID}`]: PRESCRIPTION,
  [`GET /api/v1/clinical/prescriptions/${RECORD_ID}/digital-signature`]: SIGNATURE,
  'GET /api/v1/clinical/drug-interactions': paged(INTERACTIONS),
  'GET /api/v1/clinical/dose-limits': DOSE_LIMITS,
  'GET /api/v1/clinical/controlled-drug-register': paged(REGISTER),
};
