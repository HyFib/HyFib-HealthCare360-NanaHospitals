import type * as Core from '@hc360/core';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { ResultSheet, ResultSheetLine, WorklistItem } from './api';

// A urine entered by hand (N9), whole with the shell against a fake server: the technician types the strip's own words
// into the tests' answer lists, "-" for nil, "+-" for trace and "++" for 2+, and the sheet saves the tests' answers.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const id = (suffix: string) => `0192a000-0000-7000-8000-00000000b${suffix}`;
const SAMPLE = id('001');
const PROTEIN = id('011');
const GLUCOSE = id('012');
const NITRITE = id('013');
const GRADES = ['Nil', 'Trace', '1+', '2+', '3+', '4+'];

const line = (
  overrides: Partial<ResultSheetLine> & Pick<ResultSheetLine, 'resultId' | 'testCode' | 'testName' | 'displayOrder'>,
): ResultSheetLine => ({
  labOrderItemId: id('021'),
  orderedTest: 'Urine routine and microscopy',
  labTestId: id(`1${overrides.resultId.slice(-2)}`),
  groupName: 'Chemical examination',
  resultType: 'Options',
  status: 'Pending',
  value: null,
  unit: null,
  decimals: 0,
  options: GRADES,
  flag: null,
  isCritical: false,
  referenceText: 'Nil',
  previousValue: null,
  previousResultAt: null,
  deltaPercent: null,
  deltaExceeded: false,
  autoValidated: false,
  instrumentCode: null,
  instrumentUnit: null,
  instrumentFlags: null,
  instrumentStatus: null,
  instrumentComment: null,
  heldReasons: [],
  method: null,
  remarks: null,
  organism: null,
  colonyCount: null,
  susceptibilities: [],
  enteredAt: null,
  verifiedAt: null,
  hasCriticalCall: false,
  version: 1,
  ...overrides,
});

const SHEET: ResultSheet = {
  sampleId: SAMPLE,
  barcode: '1000004513',
  sampleStatus: 'Received',
  labOrderId: id('041'),
  accessionNumber: 'NHLB260000513',
  patientId: id('051'),
  uhid: 'NH-UH-2600513',
  patientName: 'Selvi R',
  ageSex: '34 Y / F',
  clinicalNotes: null,
  lines: [
    line({ resultId: PROTEIN, testCode: 'UR-PROT', testName: 'Protein', displayOrder: 1 }),
    line({ resultId: GLUCOSE, testCode: 'UR-GLU', testName: 'Glucose', displayOrder: 2 }),
    line({
      resultId: NITRITE,
      testCode: 'UR-NIT',
      testName: 'Nitrite',
      displayOrder: 3,
      options: ['Negative', 'Positive'],
      referenceText: 'Negative',
    }),
  ],
};

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  server = fakeServer({
    [`GET /api/v1/lab/samples/${SAMPLE}/results`]: SHEET,
    'POST /api/v1/lab/results': [],
    'GET /api/v1/lab/worklist': paged<WorklistItem>([]),
    'GET /api/v1/lab/qc/status': [],
    'GET /api/v1/lab/unmatched-results': paged([]),
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('urine strip words at result entry', SCREEN_TESTS, () => {
  it('takes "++", "+-" and "-" typed into the answer lists as 2+, Trace and Negative', async () => {
    const user = userEvent.setup();
    openScreen(`/lab/worklist?sampleId=${SAMPLE}`);

    const protein = await screen.findByRole('combobox', { name: 'Protein result' }, SLOW);
    protein.focus();
    await user.keyboard('++');
    expect(protein).toHaveValue('2+');

    const glucose = screen.getByRole('combobox', { name: 'Glucose result' });
    glucose.focus();
    await user.keyboard('+-');
    expect(glucose).toHaveValue('Trace');

    const nitrite = screen.getByRole('combobox', { name: 'Nitrite result' });
    nitrite.focus();
    await user.keyboard('-');
    expect(nitrite).toHaveValue('Negative');

    await user.click(screen.getByRole('button', { name: 'Save results' }));
    await waitFor(() =>
      expect(server.requests.filter((r) => r.method === 'POST' && r.path === '/api/v1/lab/results')).toHaveLength(1),
    );
    const body = server.requests.find((r) => r.method === 'POST' && r.path === '/api/v1/lab/results')!.body as {
      entries: { resultId: string; value: string | null }[];
    };
    expect(body.entries.map((entry) => [entry.resultId, entry.value])).toEqual([
      [PROTEIN, '2+'],
      [GLUCOSE, 'Trace'],
      [NITRITE, 'Negative'],
    ]);
  });
});
