import { todayIso } from '@hc360/core';
import type {
  AiFindingDto,
  ImagingProcedureDto,
  ImagingStudyDetail,
  ImagingStudyListItem,
  RadiologyReportDto,
  RadiologyTatReport,
  ReportTemplateDto,
} from '@/modules/radiology/api';
import { paged, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Radiology for today: a worklist with a study at each step (ordered, arrived, scanning, acquired, reported), the
// reporting worklist, procedures and templates, turnaround, and one acquired study with a draft report as the record.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();

type Person = Pick<ImagingStudyListItem, 'patientId' | 'uhid' | 'patientName' | 'gender' | 'age'>;
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
const SURESH: Person = {
  patientId: id('5'),
  uhid: 'NH26000152',
  patientName: 'Suresh Patil',
  gender: 'Male',
  age: '47Y',
};
const FATIMA: Person = {
  patientId: id('4'),
  uhid: 'NH26000140',
  patientName: 'Fatima Sheikh',
  gender: 'Female',
  age: '28Y',
};

const study = (
  n: string,
  accessionNumber: string,
  who: Person,
  studyName: string,
  modality: ImagingStudyListItem['modality'],
  status: ImagingStudyListItem['status'],
  extra: Partial<ImagingStudyListItem> = {},
): ImagingStudyListItem => ({
  id: n === 'record' ? RECORD_ID : id(n),
  accessionNumber,
  ...who,
  patientClass: 'OutPatient',
  studyName,
  modality,
  bodyPart: null,
  priority: 'Routine',
  status,
  orderedAt: minutesAgo(180),
  scheduledAt: null,
  room: null,
  hasUrgentAiFinding: false,
  reportStatus: null,
  isCriticalFinding: false,
  ...extra,
});

const STUDIES: ImagingStudyListItem[] = [
  study('record', 'RAD2600871', ASHA, 'CT abdomen and pelvis with contrast', 'CT', 'Acquired', {
    bodyPart: 'Abdomen',
    room: 'CT-1',
    reportStatus: 'Draft',
    hasUrgentAiFinding: true,
  }),
  study('5a02', 'RAD2600874', RAVI, 'X-ray chest PA view', 'XR', 'Ordered', { bodyPart: 'Chest', priority: 'Urgent' }),
  study('5a03', 'RAD2600869', SURESH, 'MRI lumbar spine', 'MR', 'Arrived', {
    bodyPart: 'Lumbar spine',
    scheduledAt: minutesAgo(15),
    room: 'MRI-1',
    patientClass: 'InPatient',
  }),
  study('5a04', 'RAD2600866', FATIMA, 'Ultrasound obstetric (anomaly scan)', 'US', 'InProgress', {
    bodyPart: 'Pelvis',
    room: 'USG-2',
  }),
  study('5a05', 'RAD2600851', RAVI, 'X-ray left knee AP and lateral', 'XR', 'Reported', {
    bodyPart: 'Knee',
    reportStatus: 'Reported',
    orderedAt: minutesAgo(26 * 60),
  }),
];

const STUDY: ImagingStudyDetail = {
  id: RECORD_ID,
  accessionNumber: 'RAD2600871',
  ...ASHA,
  visitId: id('5b01'),
  admissionId: null,
  patientClass: 'OutPatient',
  sourceOrderNumber: 'ORD2600512',
  serviceCode: 'RAD-CTAP-C',
  studyName: 'CT abdomen and pelvis with contrast',
  modality: 'CT',
  bodyPart: 'Abdomen',
  priority: 'Routine',
  orderingPractitionerId: '0192a000-0000-7000-8000-0000000000d1',
  orderingDoctor: 'Dr. Meera Iyer',
  clinicalNotes: 'Right upper quadrant pain for 2 weeks; ? gallstones, ? liver lesion.',
  orderedAt: minutesAgo(180),
  status: 'Acquired',
  scheduledAt: minutesAgo(90),
  room: 'CT-1',
  stationAeTitle: 'NHP_CT1',
  arrivedAt: minutesAgo(80),
  startedAt: minutesAgo(60),
  acquiredAt: minutesAgo(45),
  technician: 'Mr. Vikas Pawar',
  contrastUsed: true,
  contrastAgent: 'Iohexol 350',
  contrastVolumeMl: 90,
  doseNote: 'DLP 812 mGy·cm',
  technicianNotes: null,
  studyInstanceUid: '1.2.826.0.1.3680043.10.1182.2600871',
  accessionFitsDicom: true,
  worklistProcedureId: 'RP2600871',
  pacsLinkedAt: null,
  seriesCount: null,
  instanceCount: null,
  reportedAt: null,
  verifiedAt: null,
  releasedAt: null,
  cancelReason: null,
  chargeError: null,
  packageBookingId: null,
  quantity: 1,
  patientPreparation: 'Fasting 4 hours; serum creatinine 0.8 mg/dL.',
  repeats: [],
  version: 5,
};

const REPORT: RadiologyReportDto = {
  id: id('5c01'),
  imagingStudyId: RECORD_ID,
  templateId: id('5d01'),
  technique: 'Axial CT of the abdomen and pelvis after IV contrast, portal venous phase.',
  clinicalHistory: 'Right upper quadrant pain for 2 weeks.',
  comparison: 'None.',
  findings: 'Multiple calculi in a distended gallbladder; wall 4 mm. No intra- or extra-hepatic biliary dilatation.',
  impression: 'Cholelithiasis without cholecystitis.',
  isCriticalFinding: false,
  criticalFindingNote: null,
  status: 'Draft',
  reportedBy: null,
  reportedAt: null,
  verifiedBy: null,
  verifiedAt: null,
  releasedAt: null,
  addenda: [],
  version: 2,
};

const AI_FINDINGS: AiFindingDto[] = [
  {
    id: id('5e01'),
    engine: 'qER',
    engineVersion: '4.2',
    finding: 'Possible 9 mm hypodense lesion, segment VI of the liver',
    confidence: 0.81,
    isUrgent: true,
    receivedAt: minutesAgo(40),
    status: 'Preliminary',
    reviewedBy: null,
    reviewedAt: null,
    reviewNote: null,
  },
];

const TEMPLATES: ReportTemplateDto[] = [
  {
    id: id('5d01'),
    code: 'CT-ABD-NORMAL',
    name: 'CT abdomen — normal',
    modality: 'CT',
    bodyPart: 'Abdomen',
    technique: 'Axial CT of the abdomen and pelvis after IV contrast.',
    findings: 'Liver, gallbladder, pancreas, spleen and kidneys are normal.',
    impression: 'No abnormality detected.',
    isActive: true,
    version: 1,
  },
  {
    id: id('5d02'),
    code: 'XR-CHEST-NORMAL',
    name: 'Chest X-ray — normal',
    modality: 'XR',
    bodyPart: 'Chest',
    technique: null,
    findings: 'Lungs are clear. Heart size is normal. Costophrenic angles are clear.',
    impression: 'Normal chest radiograph.',
    isActive: true,
    version: 3,
  },
];

const PROCEDURES: ImagingProcedureDto[] = [
  {
    id: id('5f01'),
    code: 'RAD-CTAP-C',
    name: 'CT abdomen and pelvis with contrast',
    modality: 'CT',
    bodyPart: 'Abdomen',
    serviceItemId: id('5f11'),
    durationMinutes: 20,
    requiresContrast: true,
    patientPreparation: 'Fasting 4 hours; serum creatinine within 30 days.',
    isActive: true,
    version: 2,
  },
  {
    id: id('5f02'),
    code: 'RAD-CXR-PA',
    name: 'X-ray chest PA view',
    modality: 'XR',
    bodyPart: 'Chest',
    serviceItemId: id('5f12'),
    durationMinutes: 5,
    requiresContrast: false,
    patientPreparation: null,
    isActive: true,
    version: 1,
  },
];

const tat = (count: number, average: number) => ({
  count,
  averageMinutes: average,
  medianMinutes: Math.round(average * 0.9),
  p90Minutes: Math.round(average * 1.8),
});

const TAT: RadiologyTatReport = {
  from: todayIso(-6),
  to: todayIso(),
  modalities: [
    {
      modality: 'XR',
      studies: 212,
      reported: 205,
      orderToAcquisition: tat(210, 38),
      acquisitionToReport: tat(205, 95),
      orderToReport: tat(205, 133),
      repeats: 9,
      repeatRatePercent: 4.2,
    },
    {
      modality: 'CT',
      studies: 64,
      reported: 60,
      orderToAcquisition: tat(64, 110),
      acquisitionToReport: tat(60, 180),
      orderToReport: tat(60, 290),
      repeats: 1,
      repeatRatePercent: 1.6,
    },
  ],
  repeatReasons: [
    { reason: 'Positioning', count: 5 },
    { reason: 'PatientMotion', count: 3 },
    { reason: 'Exposure', count: 2 },
  ],
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/radiology/studies': paged(STUDIES),
  [`GET /api/v1/radiology/studies/${RECORD_ID}`]: STUDY,
  [`GET /api/v1/radiology/studies/${RECORD_ID}/report`]: REPORT,
  // Submitting saves the draft first, then submits what was saved.
  [`PUT /api/v1/radiology/studies/${RECORD_ID}/report`]: REPORT,
  [`GET /api/v1/radiology/studies/${RECORD_ID}/ai-findings`]: AI_FINDINGS,
  'GET /api/v1/radiology/reporting-worklist': paged(
    STUDIES.filter((s) => s.status === 'Acquired' || s.status === 'Reported'),
  ),
  'GET /api/v1/radiology/templates': TEMPLATES,
  'GET /api/v1/radiology/procedures': PROCEDURES,
  'GET /api/v1/radiology/tat': TAT,
};
