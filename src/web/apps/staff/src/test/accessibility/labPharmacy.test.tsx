import type * as Core from '@hc360/core';
import type { Letterhead } from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PatientFlags } from '@/modules/emr/api';
import type {
  LabTestDetail,
  QcStatusRow,
  ReferenceRangeDto,
  ResultSheet,
  ResultSheetLine,
  UnmatchedResultDto,
  WorklistItem,
} from '@/modules/lab/api';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, stubBrowserApis, type FakeRequest } from '../appScreen';
import { ASHA, ENTRY_ID, PHARMACY_ANSWERS, RAVI } from './fixtures/pharmacy';

// The lab bench's result entry and the pharmacy's dispensing screens, whole (shell included) with canned data, checked
// with axe-core for WCAG 2.2 A and AA problems (SRS UX-10). Colour contrast and target sizes are checked in a browser
// instead (FRONTEND-GUIDE.md).

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
/** The id the API gives a patient, flags and other records end with. */
const lastSegment = (request: FakeRequest) => request.path.split('/').at(-1) ?? '';

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
  instrumentUnit: null,
  instrumentFlags: null,
  instrumentStatus: 'F',
  instrumentComment: null,
  heldReasons: [],
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
  loincName: null,
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

/** An analyser result sent without a sample id, waiting to be matched: the worklist says so. */
const UNMATCHED: UnmatchedResultDto[] = [
  {
    id: '0192a000-0000-7000-8000-000000000c11',
    instrumentCode: 'XN-550',
    sampleBarcode: null,
    sampleId: null,
    patientId: null,
    uhid: null,
    patientName: null,
    labResultId: null,
    testName: null,
    channelCode: 'HGB',
    value: '12.9',
    instrumentUnit: 'g/dL',
    instrumentFlags: null,
    instrumentStatus: 'F',
    instrumentComment: null,
    resultedAt: minutesAgo(30),
    receivedAt: minutesAgo(29),
    reason: 'NoSampleId',
    detail: 'XN-550 sent the result without a sample id.',
    status: 'Open',
    assignedResultId: null,
    discardReason: null,
    resolvedBy: null,
    resolvedAt: null,
  },
];

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
    'GET /api/v1/patients/*': (request: FakeRequest) =>
      request.path.endsWith('/visits') ? paged([]) : request.path.endsWith('/payer') ? { current: null } : undefined,
    // Laboratory
    'GET /api/v1/lab/worklist': paged(WORKLIST),
    'GET /api/v1/lab/qc/status': QC_STATUS,
    'GET /api/v1/lab/unmatched-results': paged(UNMATCHED),
    [`GET /api/v1/lab/samples/${SAMPLE}/results`]: SHEET,
    'GET /api/v1/lab/tests/*': (request: FakeRequest) => LAB_TESTS.find((test) => test.id === lastSegment(request)),
    // Pharmacy
    ...PHARMACY_ANSWERS,
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
    expect(await screen.findByText('An analyser result is waiting to be matched', {}, SLOW)).toBeInTheDocument();
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
