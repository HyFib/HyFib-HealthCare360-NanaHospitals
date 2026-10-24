import type {
  CatalogueImportResult,
  CatalogueStatus,
  CodingDto,
  CodingSuggestion,
  IcdChapter,
  IcdCodeDto,
  MedicalRecordDto,
  MorbidityReport,
  ProcedureCodeDto,
  RecordRequestDto,
} from '@/modules/mrd/api';
import { paged, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Medical records: coding waiting to be coded and to be verified, a deficient chart with an open deficiency, record
// requests in each state, the ICD and procedure catalogues, and the morbidity index.

const now = Date.now();
const daysAgo = (days: number) => new Date(now - days * 86_400_000).toISOString();
const dateAgo = (days: number) => daysAgo(days).slice(0, 10);
const dateIn = (days: number) => dateAgo(-days);
const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;

const OTHER_CODER = id('a7');
const DOCTOR = '0192a000-0000-7000-8000-0000000000d2';

const ASHA = { patientId: id('1'), uhid: 'NH26000123', patientName: 'Asha Devi' };
const RAVI = { patientId: id('2'), uhid: 'NH26000124', patientName: 'Ravi Shankar' };
const LAKSHMI = { patientId: id('3'), uhid: 'NH26000131', patientName: 'Lakshmi Narayanan' };
const SURESH = { patientId: id('5'), uhid: 'NH26000152', patientName: 'Suresh Patil' };

// ---------------------------------------------------------------------------------------------------- coding

/** Coded by another coder, so the signed-in coder may verify it. */
const CODING: CodingDto = {
  id: RECORD_ID,
  ...ASHA,
  encounterId: null,
  admissionId: id('ad01'),
  visitId: null,
  episodeDate: dateAgo(3),
  status: 'Coded',
  coderUserId: OTHER_CODER,
  codedAt: daysAgo(1),
  verifiedByUserId: null,
  verifiedAt: null,
  notes: 'Discharge summary signed by Dr. Anil Rao.',
  // No lines: a line's Remove button changes the codes before Verify is pressed, and Verify is refused while unsaved.
  entries: [],
  version: 3,
};

const CODINGS: CodingDto[] = [
  CODING,
  {
    id: id('c0d2'),
    ...RAVI,
    encounterId: id('e02'),
    admissionId: null,
    visitId: id('b02'),
    episodeDate: dateAgo(2),
    status: 'Pending',
    coderUserId: null,
    codedAt: null,
    verifiedByUserId: null,
    verifiedAt: null,
    notes: null,
    entries: [],
    version: 1,
  },
  {
    id: id('c0d3'),
    ...LAKSHMI,
    encounterId: null,
    admissionId: id('ad03'),
    visitId: null,
    episodeDate: dateAgo(10),
    status: 'Verified',
    coderUserId: OTHER_CODER,
    codedAt: daysAgo(8),
    verifiedByUserId: id('a1'),
    verifiedAt: daysAgo(7),
    notes: null,
    entries: [
      {
        id: id('ce04'),
        kind: 'Diagnosis',
        code: 'J18.9',
        title: 'Pneumonia, unspecified organism',
        isPrimary: true,
        sequence: 1,
      },
    ],
    version: 4,
  },
];

const SUGGESTIONS: CodingSuggestion[] = [
  { code: 'E11.9', title: 'Type 2 diabetes mellitus', type: 'Final', isPrimary: true, source: 'Discharge summary' },
  { code: 'E87.6', title: 'Hypokalaemia', type: 'Final', isPrimary: false, source: 'Progress note' },
  { code: null, title: 'Fever under evaluation', type: 'Provisional', isPrimary: false, source: 'Admission note' },
];

// ---------------------------------------------------------------------------------------------------- charts

/** A deficient in-patient chart with one open deficiency (to resolve) and one resolved. */
const RECORD: MedicalRecordDto = {
  id: RECORD_ID,
  ...ASHA,
  encounterId: null,
  admissionId: id('ad01'),
  visitId: null,
  admittedAt: daysAgo(8),
  dischargedAt: daysAgo(3),
  status: 'Deficient',
  assignedPractitionerId: DOCTOR,
  dueDate: dateIn(4),
  isOverdue: false,
  isMlc: false,
  completedAt: null,
  archivedAt: null,
  archiveLocation: null,
  retainUntil: null,
  openDeficiencies: 1,
  deficiencies: [
    {
      id: id('df01'),
      type: 'MissingDischargeSummary',
      description: 'Discharge summary not signed by the treating consultant.',
      reference: null,
      assignedPractitionerId: DOCTOR,
      assignedPractitionerName: 'Dr. Anil Rao',
      dueDate: dateIn(2),
      raisedAt: daysAgo(2),
      isSystemDetected: true,
      resolvedAt: null,
      resolutionNote: null,
    },
    {
      id: id('df02'),
      type: 'MissingConsent',
      description: 'Consent for blood transfusion not filed.',
      reference: null,
      assignedPractitionerId: DOCTOR,
      assignedPractitionerName: 'Dr. Anil Rao',
      dueDate: dateAgo(1),
      raisedAt: daysAgo(3),
      isSystemDetected: false,
      resolvedAt: daysAgo(1),
      resolutionNote: 'Scanned consent added to the file.',
    },
  ],
  version: 5,
  paper: null,
};

const RECORDS: MedicalRecordDto[] = [
  RECORD,
  {
    ...RECORD,
    id: id('ec02'),
    ...RAVI,
    admissionId: id('ad02'),
    status: 'Incomplete',
    dueDate: dateAgo(2),
    isOverdue: true,
    isMlc: true,
    openDeficiencies: 0,
    deficiencies: [],
    version: 2,
  },
  {
    ...RECORD,
    id: id('ec03'),
    ...LAKSHMI,
    status: 'Complete',
    completedAt: daysAgo(20),
    retainUntil: dateIn(5 * 365),
    openDeficiencies: 0,
    deficiencies: [],
    version: 6,
  },
  {
    ...RECORD,
    id: id('ec04'),
    ...SURESH,
    admissionId: null,
    encounterId: null,
    status: 'Archived',
    completedAt: daysAgo(400),
    archivedAt: daysAgo(390),
    archiveLocation: 'Rack 4, shelf B',
    retainUntil: dateIn(700),
    openDeficiencies: 0,
    deficiencies: [],
    version: 7,
    paper: {
      fileNumber: 'MRD/2024/0812',
      isInpatient: true,
      episodeFrom: dateAgo(410),
      episodeTo: dateAgo(405),
      note: null,
    },
  },
];

const REQUESTS: RecordRequestDto[] = [
  {
    id: RECORD_ID,
    requestNumber: 'RRQ-26-00041',
    ...ASHA,
    medicalRecordId: RECORD_ID,
    requestedByName: 'Dr. Anil Rao',
    requestingDepartment: 'General medicine',
    purpose: 'PatientCare',
    details: 'Previous admission file for review at follow-up.',
    isPatientRequest: false,
    status: 'Requested',
    requestedAt: daysAgo(0.1),
    issuedAt: null,
    dueBackOn: null,
    returnedAt: null,
    cancelReason: null,
  },
  {
    id: id('fe02'),
    requestNumber: 'RRQ-26-00038',
    ...RAVI,
    medicalRecordId: id('ec02'),
    requestedByName: 'Star Health TPA desk',
    requestingDepartment: 'Insurance',
    purpose: 'Insurance',
    details: null,
    isPatientRequest: false,
    status: 'Issued',
    requestedAt: daysAgo(3),
    issuedAt: daysAgo(2),
    dueBackOn: dateIn(5),
    returnedAt: null,
    cancelReason: null,
  },
  {
    id: id('fe03'),
    requestNumber: 'RRQ-26-00031',
    ...LAKSHMI,
    medicalRecordId: id('ec03'),
    requestedByName: 'Medico-legal cell',
    requestingDepartment: null,
    purpose: 'Legal',
    details: 'Court summons, Pune district court.',
    isPatientRequest: false,
    status: 'Overdue',
    requestedAt: daysAgo(15),
    issuedAt: daysAgo(14),
    dueBackOn: dateAgo(7),
    returnedAt: null,
    cancelReason: null,
  },
  {
    id: id('fe04'),
    requestNumber: 'RRQ-26-00022',
    ...SURESH,
    medicalRecordId: id('ec04'),
    requestedByName: 'Suresh Patil',
    requestingDepartment: null,
    purpose: 'PatientCopy',
    details: null,
    isPatientRequest: true,
    status: 'Returned',
    requestedAt: daysAgo(30),
    issuedAt: daysAgo(29),
    dueBackOn: dateAgo(22),
    returnedAt: daysAgo(23),
    cancelReason: null,
  },
];

// ---------------------------------------------------------------------------------------------------- catalogues

const icd = (n: string, code: string, title: string, block: string, chapter: string): IcdCodeDto => ({
  id: id(n),
  code,
  title,
  release: 'ICD-10 2019',
  chapter,
  block,
  blockTitle: null,
  isBillable: code.includes('.'),
  usage: null,
  keywords: null,
  includedTerms: null,
  source: 'Seed',
  isActive: true,
  version: 1,
});

const ICD_CODES: IcdCodeDto[] = [
  icd('1c01', 'E11.9', 'Type 2 diabetes mellitus without complications', 'E10-E14', 'IV'),
  icd('1c02', 'I10', 'Essential (primary) hypertension', 'I10-I15', 'IX'),
  icd('1c03', 'J18.9', 'Pneumonia, unspecified', 'J09-J18', 'X'),
  { ...icd('1c04', 'A90', 'Dengue fever [classical dengue]', 'A90-A99', 'I'), keywords: 'dengue', source: 'Manual' },
];

const CLASSIFICATION: IcdChapter[] = [
  {
    number: 'I',
    title: 'Certain infectious and parasitic diseases',
    range: 'A00-B99',
    blocks: [{ range: 'A90-A99', title: 'Arthropod-borne viral fevers' }],
  },
  {
    number: 'IV',
    title: 'Endocrine, nutritional and metabolic diseases',
    range: 'E00-E90',
    blocks: [{ range: 'E10-E14', title: 'Diabetes mellitus' }],
  },
  {
    number: 'IX',
    title: 'Diseases of the circulatory system',
    range: 'I00-I99',
    blocks: [{ range: 'I10-I15', title: 'Hypertensive diseases' }],
  },
];

const LAST_IMPORT: CatalogueImportResult = {
  id: id('1a01'),
  catalogue: 'Icd10',
  release: 'ICD-10 2019',
  fileName: 'icd102019en.xml',
  sha256: '9f2c4a1b0e7d3c5a8b6f1e2d4c3b5a7f9e8d6c4b2a1f3e5d7c9b8a6f4e2d1c3b',
  fileSizeBytes: 38_400_000,
  sourceTitle: 'ICD-10 2019',
  sourceVersion: '2019',
  codesInFile: 12_420,
  added: 12_390,
  updated: 0,
  unchanged: 0,
  reinstated: 0,
  withdrawn: 0,
  leftAlone: 30,
  chapters: 22,
  blocks: 263,
  dryRun: false,
  at: daysAgo(60),
  importedBy: 'Dr. Meera Iyer',
  channel: 'Upload',
  durationMs: 41_000,
  warnings: [],
};

const CATALOGUE: CatalogueStatus = {
  catalogues: [
    {
      release: 'ICD-10 2019',
      activeCodes: 12_410,
      codableCodes: 10_210,
      withdrawnCodes: 0,
      switchedOffCodes: 10,
      fromFile: 12_390,
      fromStarterList: 0,
      addedByHand: 18,
      savedFromOnline: 2,
      classificationFromFile: true,
      lastImport: LAST_IMPORT,
    },
    {
      release: 'ICD-10-PCS',
      activeCodes: 420,
      codableCodes: 420,
      withdrawnCodes: 0,
      switchedOffCodes: 0,
      fromFile: 0,
      fromStarterList: 400,
      addedByHand: 20,
      savedFromOnline: 0,
      classificationFromFile: null,
      lastImport: null,
    },
  ],
  recentImports: [LAST_IMPORT],
};

const PROCEDURES: ProcedureCodeDto[] = [
  {
    id: id('9c01'),
    code: '0DTJ4ZZ',
    title: 'Resection of appendix, percutaneous endoscopic approach',
    system: 'ICD-10-PCS',
    category: 'Gastrointestinal',
    keywords: 'lap appendicectomy',
    source: 'Seed',
    isActive: true,
    version: 1,
  },
  {
    id: id('9c02'),
    code: '10D00Z1',
    title: 'Extraction of products of conception, low',
    system: 'ICD-10-PCS',
    category: 'Obstetrics',
    keywords: 'LSCS, caesarean',
    source: 'Seed',
    isActive: true,
    version: 1,
  },
  {
    id: id('9c03'),
    code: 'NH-PHACO',
    title: 'Phacoemulsification with foldable IOL',
    system: 'Hospital',
    category: 'Ophthalmology',
    keywords: 'cataract',
    source: 'Manual',
    isActive: true,
    version: 2,
  },
];

const MORBIDITY: MorbidityReport = {
  from: dateAgo(30),
  to: dateAgo(0),
  groupBy: 'code',
  source: 'coded',
  totalEpisodes: 214,
  rows: [
    {
      key: 'E11.9',
      title: 'Type 2 diabetes mellitus',
      chapter: 'IV',
      episodes: 48,
      patients: 41,
      male: 26,
      female: 15,
      otherOrUnknownGender: 0,
      age0To14: 0,
      age15To44: 6,
      age45To64: 22,
      age65Plus: 13,
      ageUnknown: 0,
    },
    {
      key: 'A90',
      title: 'Dengue fever',
      chapter: 'I',
      episodes: 37,
      patients: 37,
      male: 20,
      female: 17,
      otherOrUnknownGender: 0,
      age0To14: 9,
      age15To44: 21,
      age45To64: 5,
      age65Plus: 2,
      ageUnknown: 0,
    },
    {
      key: 'I10',
      title: 'Essential hypertension',
      chapter: 'IX',
      episodes: 29,
      patients: 27,
      male: 14,
      female: 13,
      otherOrUnknownGender: 0,
      age0To14: 0,
      age15To44: 3,
      age45To64: 14,
      age65Plus: 10,
      ageUnknown: 0,
    },
  ],
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/mrd/codings': paged(CODINGS),
  [`GET /api/v1/mrd/codings/${RECORD_ID}`]: CODING,
  [`GET /api/v1/mrd/codings/${RECORD_ID}/suggestions`]: SUGGESTIONS,
  'GET /api/v1/mrd/records': paged(RECORDS),
  [`GET /api/v1/mrd/records/${RECORD_ID}`]: RECORD,
  'GET /api/v1/mrd/record-requests': paged(REQUESTS),
  'GET /api/v1/mrd/catalogue': CATALOGUE,
  'GET /api/v1/mrd/icd-classification': CLASSIFICATION,
  'GET /api/v1/mrd/icd-codes': ICD_CODES,
  'GET /api/v1/mrd/procedure-codes': PROCEDURES,
  'GET /api/v1/mrd/reports/morbidity': MORBIDITY,
};
