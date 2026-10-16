import type * as Core from '@hc360/core';
import type { Letterhead } from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PatientFlags } from '@/modules/emr/api';
import type { BatchStock, ItemDetail, StoreDto } from '@/modules/inventory/api';
import type {
  LabTestDetail,
  QcStatusRow,
  ReferenceRangeDto,
  ResultSheet,
  ResultSheetLine,
  WorklistItem,
} from '@/modules/lab/api';
import type { PharmacyDashboard, QueueEntryDetail, QueueLineDto, QueueListItem } from '@/modules/pharmacy/api';
import {
  fakeServer,
  openScreen,
  paged,
  SCREEN_TESTS,
  SLOW,
  STAFF_USER,
  stubBrowserApis,
  type FakeRequest,
} from '../appScreen';

// The lab bench's result entry and the pharmacy's dispensing screens, whole (shell included) with canned data, checked
// with axe-core for WCAG 2.2 A and AA problems (SRS UX-10). Colour contrast and target sizes are checked in a browser
// instead (FRONTEND-GUIDE.md).

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
/** A calendar date `days` from today, as the server sends expiry dates (yyyy-MM-dd). */
const dateIn = (days: number) => new Date(now + days * 86_400_000).toISOString().slice(0, 10);
/** The id the API gives a patient, flags and other records end with. */
const lastSegment = (request: FakeRequest) => request.path.split('/').at(-1) ?? '';

const ASHA = '0192a000-0000-7000-8000-000000000001';
const RAVI = '0192a000-0000-7000-8000-000000000002';

const FLAGS: Record<string, PatientFlags> = {
  [ASHA]: {
    allergyStatus: 'Recorded',
    allergies: [
      {
        id: '0192a000-0000-7000-8000-000000000a11',
        allergen: 'Penicillin',
        type: 'Drug',
        severity: 'Anaphylaxis',
        reaction: 'Facial swelling and breathlessness',
      },
    ],
    alerts: [],
  },
  [RAVI]: {
    allergyStatus: 'NoKnownAllergies',
    allergies: [],
    alerts: [
      {
        id: '0192a000-0000-7000-8000-000000000a12',
        type: 'FallRisk',
        title: 'Fall risk',
        detail: 'Walks with a stick',
        severity: 'Warning',
        expiresOn: null,
      },
    ],
  },
};

// ------------------------------------------------------------------------------------------------ laboratory

const SAMPLE = '0192a000-0000-7000-8000-000000000b01';
const ORDER = '0192a000-0000-7000-8000-000000000b11';
const CBC_ITEM = '0192a000-0000-7000-8000-000000000b31';
const ESR_ITEM = '0192a000-0000-7000-8000-000000000b32';
const SMEAR_ITEM = '0192a000-0000-7000-8000-000000000b33';

const tube = (
  overrides: Partial<WorklistItem> & Pick<WorklistItem, 'sampleId' | 'barcode' | 'tests'>,
): WorklistItem => ({
  labOrderId: ORDER,
  accessionNumber: 'NHPLB260000412',
  patientId: ASHA,
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  age: '39Y',
  gender: 'Female',
  patientClass: 'OutPatient',
  priority: 'Routine',
  section: 'Hematology',
  status: 'Received',
  receivedAt: minutesAgo(40),
  pending: 0,
  entered: 0,
  verified: 0,
  critical: 0,
  ...overrides,
});

const WORKLIST: WorklistItem[] = [
  tube({
    sampleId: SAMPLE,
    barcode: '260004512',
    tests: ['Complete blood count', 'ESR', 'Peripheral smear examination'],
    priority: 'Stat',
    status: 'InProcess',
    receivedAt: minutesAgo(35),
    pending: 1,
    entered: 7,
    critical: 1,
  }),
  tube({
    sampleId: '0192a000-0000-7000-8000-000000000b02',
    barcode: '260004498',
    labOrderId: '0192a000-0000-7000-8000-000000000b12',
    accessionNumber: 'NHPLB260000405',
    patientId: RAVI,
    uhid: 'NH26000124',
    patientName: 'Ravi Shankar',
    age: '62Y',
    gender: 'Male',
    patientClass: 'InPatient',
    priority: 'Urgent',
    section: 'Biochemistry',
    tests: ['Liver function test', 'Serum creatinine'],
    receivedAt: minutesAgo(55),
    pending: 9,
  }),
  tube({
    sampleId: '0192a000-0000-7000-8000-000000000b03',
    barcode: '260004475',
    labOrderId: '0192a000-0000-7000-8000-000000000b13',
    accessionNumber: 'NHPLB260000398',
    patientId: '0192a000-0000-7000-8000-000000000003',
    uhid: 'NH26000131',
    patientName: 'Mohammed Irfan',
    age: '45Y',
    gender: 'Male',
    section: 'Biochemistry',
    status: 'InProcess',
    tests: ['Lipid profile'],
    receivedAt: minutesAgo(80),
    entered: 3,
    verified: 2,
  }),
  tube({
    sampleId: '0192a000-0000-7000-8000-000000000b04',
    barcode: '260004520',
    labOrderId: '0192a000-0000-7000-8000-000000000b14',
    accessionNumber: 'NHPLB260000415',
    patientId: '0192a000-0000-7000-8000-000000000004',
    uhid: 'NH26000140',
    patientName: 'Priya Sharma',
    age: '28Y',
    patientClass: 'Emergency',
    section: 'ClinicalPathology',
    tests: ['Urine routine examination'],
    receivedAt: minutesAgo(10),
    pending: 12,
  }),
];

const QC_STATUS: QcStatusRow[] = [
  {
    labTestId: '0192a000-0000-7000-8000-000000000b41',
    testCode: 'HB',
    testName: 'Haemoglobin',
    lotId: '0192a000-0000-7000-8000-000000000b51',
    lotNumber: 'XN-CHECK-L2-2609',
    level: 2,
    instrumentCode: 'XN-550',
    latestOutcome: 'Accepted',
    latestRunAt: minutesAgo(240),
    locked: false,
    overridden: false,
  },
  {
    labTestId: '0192a000-0000-7000-8000-000000000b49',
    testCode: 'CREAT',
    testName: 'Serum creatinine',
    lotId: '0192a000-0000-7000-8000-000000000b52',
    lotNumber: 'LYPHOCHEK-1-45861',
    level: 1,
    instrumentCode: 'AU-480',
    latestOutcome: 'Rejected',
    latestRunAt: minutesAgo(95),
    locked: true,
    overridden: false,
  },
];

const result = (
  overrides: Partial<ResultSheetLine> &
    Pick<ResultSheetLine, 'resultId' | 'labTestId' | 'testCode' | 'testName' | 'displayOrder'>,
): ResultSheetLine => ({
  labOrderItemId: CBC_ITEM,
  orderedTest: 'Complete blood count',
  groupName: null,
  resultType: 'Numeric',
  status: 'Entered',
  value: null,
  unit: null,
  decimals: 0,
  options: [],
  flag: null,
  isCritical: false,
  referenceText: null,
  previousValue: null,
  previousResultAt: null,
  deltaPercent: null,
  deltaExceeded: false,
  autoValidated: false,
  instrumentCode: 'XN-550',
  method: null,
  remarks: null,
  organism: null,
  colonyCount: null,
  susceptibilities: [],
  enteredAt: minutesAgo(20),
  verifiedAt: null,
  hasCriticalCall: false,
  version: 1,
  ...overrides,
});

const LAST_MONTH = minutesAgo(60 * 24 * 30);

const RESULTS: ResultSheetLine[] = [
  result({
    resultId: '0192a000-0000-7000-8000-000000000b21',
    labTestId: '0192a000-0000-7000-8000-000000000b41',
    testCode: 'HB',
    testName: 'Haemoglobin',
    displayOrder: 1,
    value: '9.1',
    unit: 'g/dL',
    decimals: 1,
    flag: 'Low',
    referenceText: '12.0 - 15.0',
    previousValue: 10.4,
    previousResultAt: LAST_MONTH,
    deltaPercent: 12.5,
    method: 'SLS haemoglobin',
  }),
  result({
    resultId: '0192a000-0000-7000-8000-000000000b22',
    labTestId: '0192a000-0000-7000-8000-000000000b42',
    testCode: 'TLC',
    testName: 'Total leucocyte count',
    displayOrder: 2,
    value: '7.8',
    unit: '10³/µL',
    decimals: 1,
    flag: 'Normal',
    referenceText: '4.0 - 11.0',
    previousValue: 8.2,
    previousResultAt: LAST_MONTH,
    deltaPercent: 4.9,
  }),
  result({
    resultId: '0192a000-0000-7000-8000-000000000b23',
    labTestId: '0192a000-0000-7000-8000-000000000b43',
    testCode: 'PLT',
    testName: 'Platelet count',
    displayOrder: 3,
    value: '18',
    unit: '10³/µL',
    flag: 'CriticalLow',
    isCritical: true,
    referenceText: '150 - 410',
    previousValue: 96,
    previousResultAt: LAST_MONTH,
    deltaPercent: 81.3,
    deltaExceeded: true,
  }),
  result({
    resultId: '0192a000-0000-7000-8000-000000000b24',
    labTestId: '0192a000-0000-7000-8000-000000000b44',
    testCode: 'NEUT',
    testName: 'Neutrophils',
    groupName: 'Differential count',
    displayOrder: 4,
    value: '78',
    unit: '%',
    flag: 'High',
    referenceText: '40 - 75',
  }),
  result({
    resultId: '0192a000-0000-7000-8000-000000000b25',
    labTestId: '0192a000-0000-7000-8000-000000000b45',
    testCode: 'LYMPH',
    testName: 'Lymphocytes',
    groupName: 'Differential count',
    displayOrder: 5,
    value: '16',
    unit: '%',
    flag: 'Low',
    referenceText: '20 - 40',
  }),
  result({
    resultId: '0192a000-0000-7000-8000-000000000b26',
    labOrderItemId: ESR_ITEM,
    orderedTest: 'ESR',
    labTestId: '0192a000-0000-7000-8000-000000000b46',
    testCode: 'ESR',
    testName: 'ESR',
    displayOrder: 1,
    status: 'Pending',
    unit: 'mm/hr',
    referenceText: '0 - 20',
    instrumentCode: null,
    method: 'Westergren',
    enteredAt: null,
  }),
  result({
    resultId: '0192a000-0000-7000-8000-000000000b27',
    labOrderItemId: SMEAR_ITEM,
    orderedTest: 'Peripheral smear examination',
    labTestId: '0192a000-0000-7000-8000-000000000b47',
    testCode: 'MP',
    testName: 'Malarial parasite',
    displayOrder: 1,
    resultType: 'Options',
    options: ['Not seen', 'P. vivax seen', 'P. falciparum seen', 'Mixed infection'],
    value: 'Not seen',
    flag: 'Normal',
    referenceText: 'Not seen',
    instrumentCode: null,
  }),
  result({
    resultId: '0192a000-0000-7000-8000-000000000b28',
    labOrderItemId: SMEAR_ITEM,
    orderedTest: 'Peripheral smear examination',
    labTestId: '0192a000-0000-7000-8000-000000000b48',
    testCode: 'PS',
    testName: 'Smear impression',
    displayOrder: 2,
    resultType: 'Text',
    value: 'Microcytic hypochromic red cells with anisopoikilocytosis; platelets markedly reduced.',
    instrumentCode: null,
  }),
];

const SHEET: ResultSheet = {
  sampleId: SAMPLE,
  barcode: '260004512',
  sampleStatus: 'InProcess',
  labOrderId: ORDER,
  accessionNumber: 'NHPLB260000412',
  patientId: ASHA,
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  ageSex: '39Y / F',
  clinicalNotes: 'Fatigue and bleeding gums for a week. On oral iron for three months.',
  lines: RESULTS,
};

const range = (overrides: Partial<ReferenceRangeDto>): ReferenceRangeDto => ({
  id: null,
  gender: null,
  ageFromDays: 0,
  ageToDays: null,
  low: null,
  high: null,
  criticalLow: null,
  criticalHigh: null,
  text: null,
  normalValues: null,
  criticalValues: null,
  ...overrides,
});

const RANGES: Record<string, ReferenceRangeDto[]> = {
  HB: [range({ gender: 'Female', low: 12, high: 15, criticalLow: 7, criticalHigh: 20 })],
  TLC: [range({ low: 4, high: 11, criticalLow: 2, criticalHigh: 30 })],
  PLT: [range({ low: 150, high: 410, criticalLow: 20, criticalHigh: 1000 })],
  NEUT: [range({ low: 40, high: 75 })],
  LYMPH: [range({ low: 20, high: 40 })],
  ESR: [range({ gender: 'Female', low: 0, high: 20 })],
  MP: [range({ normalValues: ['Not seen'], criticalValues: ['P. falciparum seen', 'Mixed infection'] })],
};

/** Each analyte's master, which the sheet loads for its reference intervals. */
const LAB_TESTS: LabTestDetail[] = RESULTS.map((line) => ({
  id: line.labTestId,
  code: line.testCode,
  name: line.testName,
  shortName: null,
  section: 'Hematology',
  specimenType: 'WholeBlood',
  container: 'Edta',
  method: line.method,
  tatHours: 4,
  loinc: null,
  serviceItemId: null,
  isOrderable: false,
  isPanel: false,
  resultType: line.resultType,
  unit: line.unit,
  decimals: line.decimals,
  options: line.options,
  instrumentChannelCodes: line.instrumentCode ? [line.testCode] : [],
  qcGroup: null,
  deltaThresholdPercent: 50,
  allowAutoValidation: false,
  preparation: null,
  drawSeparately: false,
  interpretation: null,
  defaultReferralLabId: null,
  displayOrder: line.displayOrder,
  isProvisional: false,
  isActive: true,
  components: [],
  ranges: RANGES[line.testCode] ?? [],
  version: 1,
}));

// ------------------------------------------------------------------------------------------------ pharmacy

const OP_PHARMACY: StoreDto = {
  id: '0192a000-0000-7000-8000-000000000e61',
  code: 'OPPH',
  name: 'OP pharmacy',
  type: 'Pharmacy',
  departmentId: null,
  departmentName: null,
  inChargeUserId: null,
  inChargeName: 'Suresh Patil',
  location: 'Ground floor, OPD block',
  isActive: true,
  version: 1,
};
const IP_PHARMACY: StoreDto = {
  ...OP_PHARMACY,
  id: '0192a000-0000-7000-8000-000000000e62',
  code: 'IPPH',
  name: 'IP pharmacy',
  location: 'First floor, ward block',
};

const queued = (
  overrides: Partial<QueueListItem> & Pick<QueueListItem, 'id' | 'prescriptionNumber' | 'patientId' | 'patientName'>,
): QueueListItem => ({
  uhid: 'NH26000123',
  gender: 'Female',
  age: '39Y',
  prescriberName: 'Dr. Meera Iyer',
  isInpatient: false,
  status: 'Pending',
  lineCount: 2,
  openLines: 2,
  hasControlledDrugs: false,
  isRefill: false,
  signedAt: minutesAgo(13),
  receivedAt: minutesAgo(12),
  waitingMinutes: 12,
  ...overrides,
});

const ENTRY_ID = '0192a000-0000-7000-8000-000000000e01';

const QUEUE: QueueListItem[] = [
  queued({
    id: ENTRY_ID,
    prescriptionNumber: 'RX2600311',
    patientId: RAVI,
    patientName: 'Ravi Shankar',
    uhid: 'NH26000124',
    gender: 'Male',
    age: '62Y',
    lineCount: 3,
    openLines: 3,
    hasControlledDrugs: true,
    signedAt: minutesAgo(25),
    receivedAt: minutesAgo(24),
    waitingMinutes: 24,
  }),
  queued({
    id: '0192a000-0000-7000-8000-000000000e02',
    prescriptionNumber: 'RX2600314',
    patientId: ASHA,
    patientName: 'Asha Devi',
    prescriberName: 'Dr. Anil Rao',
  }),
  queued({
    id: '0192a000-0000-7000-8000-000000000e03',
    prescriptionNumber: 'RX2600309',
    patientId: '0192a000-0000-7000-8000-000000000005',
    patientName: 'Fatima Shaikh',
    uhid: 'NH26000098',
    age: '54Y',
    status: 'PartiallyDispensed',
    lineCount: 4,
    openLines: 1,
    signedAt: minutesAgo(48),
    receivedAt: minutesAgo(47),
    waitingMinutes: 41,
  }),
  queued({
    id: '0192a000-0000-7000-8000-000000000e04',
    prescriptionNumber: 'RX2600317',
    patientId: '0192a000-0000-7000-8000-000000000006',
    patientName: 'Suresh Kumar',
    uhid: 'NH26000077',
    gender: 'Male',
    age: '71Y',
    prescriberName: 'Dr. Anil Rao',
    isRefill: true,
    signedAt: minutesAgo(4),
    receivedAt: minutesAgo(3),
    waitingMinutes: 3,
  }),
];

const DASHBOARD: PharmacyDashboard = {
  pendingOutpatient: 4,
  pendingInpatient: 2,
  partiallyDispensed: 1,
  pendingWardIndents: 3,
  pendingRefills: 1,
  salesToday: 38,
  salesAmountToday: 24650.5,
  refundsToday: 0,
  byMode: [
    { mode: 'Cash', amount: 11240, count: 21 },
    { mode: 'Upi', amount: 10890.5, count: 14 },
    { mode: 'Card', amount: 2520, count: 3 },
  ],
  nearExpiryBatches: 1,
  nearExpiryValueAtMrp: 84,
  nearExpiry: [
    {
      batchId: '0192a000-0000-7000-8000-000000000e55',
      storeId: OP_PHARMACY.id,
      storeName: OP_PHARMACY.name,
      itemId: '0192a000-0000-7000-8000-000000000e42',
      itemCode: 'PAN40',
      itemName: 'Pantoprazole 40 mg tablet',
      batchNumber: 'PTZ2309',
      expiryDate: dateIn(20),
      daysToExpiry: 20,
      quantity: 10,
      valueAtCost: 52,
      valueAtMrp: 84,
    },
  ],
  averageWaitMinutes: 9,
};

const PARACETAMOL = '0192a000-0000-7000-8000-000000000e41';
const PANTOPRAZOLE = '0192a000-0000-7000-8000-000000000e42';
const TRAMADOL = '0192a000-0000-7000-8000-000000000e43';

const drugLine = (
  overrides: Partial<QueueLineDto> &
    Pick<
      QueueLineDto,
      'id' | 'prescriptionLineId' | 'drugItemId' | 'drugName' | 'genericName' | 'strength' | 'requestedQuantity'
    >,
): QueueLineDto => ({
  dosageForm: 'Tablet',
  route: 'Oral',
  dose: '1 tablet',
  frequency: 'Once a day',
  durationDays: 5,
  instructions: null,
  isControlledDrug: false,
  dispensedQuantity: 0,
  remainingQuantity: overrides.requestedQuantity,
  status: 'Pending',
  available: null,
  mrp: null,
  nearestExpiry: null,
  ...overrides,
});

const ENTRY: QueueEntryDetail = {
  id: ENTRY_ID,
  prescriptionId: '0192a000-0000-7000-8000-000000000e11',
  prescriptionNumber: 'RX2600311',
  patient: {
    id: RAVI,
    uhid: 'NH26000124',
    fullName: 'Ravi Shankar',
    gender: 'Male',
    dateOfBirth: null,
    age: '62Y',
    phone: '9822012345',
    bloodGroup: 'BPositive',
    category: 'Staff',
    isVip: false,
    isDeceased: false,
    photoFileId: null,
  },
  visitId: '0192a000-0000-7000-8000-000000000e15',
  admissionId: null,
  practitionerId: STAFF_USER.practitionerId!,
  prescriberName: 'Dr. Meera Iyer',
  prescriberRegistrationNumber: 'MMC 2009/04/1234',
  isInpatient: false,
  status: 'Pending',
  signedAt: minutesAgo(25),
  receivedAt: minutesAgo(24),
  closeReason: null,
  refillsAuthorized: 0,
  refillsUsed: 0,
  refillValidUntil: null,
  refillOfEntryId: null,
  lines: [
    drugLine({
      id: '0192a000-0000-7000-8000-000000000e21',
      prescriptionLineId: '0192a000-0000-7000-8000-000000000e31',
      drugItemId: PARACETAMOL,
      drugName: 'Paracetamol 500 mg tablet',
      genericName: 'Paracetamol',
      strength: '500 mg',
      frequency: 'Three times a day',
      instructions: 'After food',
      requestedQuantity: 15,
      available: 240,
      mrp: 2.1,
      nearestExpiry: dateIn(82),
    }),
    drugLine({
      id: '0192a000-0000-7000-8000-000000000e22',
      prescriptionLineId: '0192a000-0000-7000-8000-000000000e32',
      drugItemId: PANTOPRAZOLE,
      drugName: 'Pantoprazole 40 mg tablet',
      genericName: 'Pantoprazole',
      strength: '40 mg',
      frequency: 'Once a day before breakfast',
      durationDays: 14,
      requestedQuantity: 14,
      available: 60,
      mrp: 8.4,
      nearestExpiry: dateIn(173),
    }),
    drugLine({
      id: '0192a000-0000-7000-8000-000000000e23',
      prescriptionLineId: '0192a000-0000-7000-8000-000000000e33',
      drugItemId: TRAMADOL,
      drugName: 'Tramadol 50 mg capsule',
      genericName: 'Tramadol',
      strength: '50 mg',
      dosageForm: 'Capsule',
      dose: '1 capsule',
      frequency: 'Twice a day',
      durationDays: 3,
      instructions: 'Only when the knee pain is severe',
      isControlledDrug: true,
      requestedQuantity: 6,
      available: 20,
      mrp: 9.5,
      nearestExpiry: dateIn(233),
    }),
  ],
  version: 3,
};

const drug = (
  overrides: Partial<ItemDetail> & Pick<ItemDetail, 'id' | 'code' | 'name' | 'genericName' | 'strength'>,
): ItemDetail => ({
  brandName: null,
  type: 'Drug',
  categoryId: null,
  dosageForm: 'Tablet',
  packSize: '10 tablets per strip',
  baseUnit: 'Tablet',
  purchaseUnit: 'Strip',
  purchaseUnitFactor: 10,
  hsnCode: '30049099',
  gstRate: 12,
  schedule: 'H',
  isControlled: false,
  isColdChain: false,
  vedClass: 'Essential',
  reorderLevel: 100,
  reorderQuantity: 500,
  minimumStock: 50,
  maximumStock: 1000,
  manufacturer: 'Micro Labs Ltd',
  barcode: null,
  isActive: true,
  requiresPrescription: true,
  requiresRegister: false,
  version: 1,
  ...overrides,
});

const ITEMS: ItemDetail[] = [
  drug({
    id: PARACETAMOL,
    code: 'PCM500',
    name: 'Paracetamol 500 mg tablet',
    genericName: 'Paracetamol',
    strength: '500 mg',
    schedule: 'Otc',
    requiresPrescription: false,
    barcode: '8901234500017',
  }),
  drug({
    id: PANTOPRAZOLE,
    code: 'PAN40',
    name: 'Pantoprazole 40 mg tablet',
    genericName: 'Pantoprazole',
    strength: '40 mg',
    manufacturer: 'Alkem Laboratories',
  }),
  drug({
    id: TRAMADOL,
    code: 'TRM50',
    name: 'Tramadol 50 mg capsule',
    genericName: 'Tramadol',
    strength: '50 mg',
    dosageForm: 'Capsule',
    packSize: '10 capsules per strip',
    baseUnit: 'Capsule',
    schedule: 'H1',
    isControlled: true,
    vedClass: 'Vital',
    reorderLevel: 20,
    reorderQuantity: 100,
    minimumStock: 10,
    maximumStock: 200,
    manufacturer: 'Sun Pharma',
    requiresRegister: true,
  }),
];

const batch = (
  overrides: Partial<BatchStock> & Pick<BatchStock, 'batchId' | 'itemId' | 'batchNumber' | 'daysToExpiry' | 'mrp'>,
): BatchStock => ({
  storeId: OP_PHARMACY.id,
  expiryDate: dateIn(overrides.daysToExpiry ?? 0),
  unitCost: 0,
  gstRate: 12,
  onHand: 0,
  reserved: 0,
  available: 0,
  isExpired: false,
  isBlocked: false,
  blockReason: null,
  ...overrides,
});

/** Batches of each medicine in the OP pharmacy, as the server lists them (earliest expiry first). */
const BATCHES: Record<string, BatchStock[]> = {
  [PARACETAMOL]: [
    batch({
      batchId: '0192a000-0000-7000-8000-000000000e51',
      itemId: PARACETAMOL,
      batchNumber: 'PCM2411A',
      daysToExpiry: 82,
      mrp: 2.1,
      unitCost: 1.2,
      onHand: 40,
      available: 40,
    }),
    batch({
      batchId: '0192a000-0000-7000-8000-000000000e52',
      itemId: PARACETAMOL,
      batchNumber: 'PCM2503C',
      daysToExpiry: 355,
      mrp: 2.2,
      unitCost: 1.25,
      onHand: 200,
      available: 200,
    }),
  ],
  [PANTOPRAZOLE]: [
    batch({
      batchId: '0192a000-0000-7000-8000-000000000e55',
      itemId: PANTOPRAZOLE,
      batchNumber: 'PTZ2309',
      daysToExpiry: 20,
      mrp: 8.4,
      unitCost: 5.2,
      onHand: 10,
      available: 10,
    }),
    batch({
      batchId: '0192a000-0000-7000-8000-000000000e53',
      itemId: PANTOPRAZOLE,
      batchNumber: 'PTZ2502',
      daysToExpiry: 173,
      mrp: 8.4,
      unitCost: 5.2,
      onHand: 60,
      available: 60,
    }),
  ],
  [TRAMADOL]: [
    batch({
      batchId: '0192a000-0000-7000-8000-000000000e54',
      itemId: TRAMADOL,
      batchNumber: 'TRM2406',
      daysToExpiry: 233,
      mrp: 9.5,
      unitCost: 6.1,
      gstRate: 12,
      onHand: 22,
      reserved: 2,
      available: 20,
    }),
  ],
};

const LETTERHEAD: Letterhead = { name: 'Nana Hospitals, Pune' };

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  server = fakeServer({
    'GET /api/v1/emr/patients/*': (request: FakeRequest) =>
      request.path.endsWith('/flags') ? FLAGS[request.path.split('/')[5] ?? ''] : undefined,
    'GET /api/v1/print/letterhead': LETTERHEAD,
    // No visits yet, so the banners name no payer.
    'GET /api/v1/patients/*': (request: FakeRequest) => (request.path.endsWith('/visits') ? paged([]) : undefined),
    // Laboratory
    'GET /api/v1/lab/worklist': paged(WORKLIST),
    'GET /api/v1/lab/qc/status': QC_STATUS,
    [`GET /api/v1/lab/samples/${SAMPLE}/results`]: SHEET,
    'GET /api/v1/lab/tests/*': (request: FakeRequest) => LAB_TESTS.find((test) => test.id === lastSegment(request)),
    // Pharmacy
    'GET /api/v1/pharmacy/queue': paged(QUEUE),
    'GET /api/v1/pharmacy/dashboard': DASHBOARD,
    [`GET /api/v1/pharmacy/queue/${ENTRY_ID}`]: ENTRY,
    'GET /api/v1/inventory/stores': [OP_PHARMACY, IP_PHARMACY],
    'GET /api/v1/inventory/items/*': (request: FakeRequest) => ITEMS.find((item) => item.id === lastSegment(request)),
    'GET /api/v1/pharmacy/stock/batches': (request: FakeRequest) =>
      request.url.searchParams.get('storeId') === OP_PHARMACY.id
        ? BATCHES[request.url.searchParams.get('itemId') ?? '']
        : undefined,
  });
});

afterEach(() => {
  // A screen whose request went unanswered would show an error where its content should be, and hide that from axe.
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('lab and pharmacy screens', SCREEN_TESTS, () => {
  it('lab result entry: the worklist, then a sample opened from it', async () => {
    openScreen('/lab/worklist');
    expect(await screen.findByText('Ravi Shankar', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('QC failed today', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();

    await userEvent.click(screen.getByText('Asha Devi'));
    expect(await screen.findByRole('table', { name: 'Results for sample 260004512' }, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Allergies: Penicillin (anaphylaxis)', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'ESR result in mm/hr' })).toHaveValue('');
    expect(screen.getByRole('textbox', { name: 'Haemoglobin result in g/dL' })).toHaveValue('9.1');
    await expectNoAxeViolations();
  });

  it('pharmacy dispensing queue', async () => {
    openScreen('/pharmacy/queue');
    expect(await screen.findByText('RX2600311', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByRole('tab', { name: 'Out-patients (4)' }, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('pharmacy dispensing of one prescription', async () => {
    openScreen(`/pharmacy/queue/${ENTRY_ID}`);
    expect(await screen.findByText('Prescription RX2600311', {}, SLOW)).toBeInTheDocument();
    // FEFO has picked a batch for every line, and the bill knows each medicine's GST.
    expect(await screen.findByText(/^PCM2411A ·/, {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText(/^PTZ2309 ·/, {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText(/^TRM2406 ·/, {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('CGST (included)', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Fall risk: Walks with a stick', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });
});
