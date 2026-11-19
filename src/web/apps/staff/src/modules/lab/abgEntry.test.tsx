import type * as Core from '@hc360/core';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { ResultSheet, ResultSheetLine, WorklistItem } from './api';

// An arterial blood gas at the bench (N8), whole with the shell against a fake server: the FiO2 opens filled in with room
// air, 21 %, for the technician to confirm by saving or to change; the P/F ratio and the A-a gradient wait for the
// server to calculate them from the FiO2 entered, which it never assumes.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const id = (suffix: string) => `0192a000-0000-7000-8000-00000000a${suffix}`;
const SAMPLE = id('001');
const PO2 = id('011');
const FIO2 = id('012');
const PF = id('013');

const line = (
  overrides: Partial<ResultSheetLine> & Pick<ResultSheetLine, 'resultId' | 'testCode' | 'testName' | 'displayOrder'>,
): ResultSheetLine => ({
  labOrderItemId: id('021'),
  orderedTest: 'Arterial blood gas (ABG)',
  labTestId: id(`1${overrides.resultId.slice(-2)}`),
  groupName: 'Oxygenation',
  resultType: 'Numeric',
  status: 'Pending',
  value: null,
  unit: 'mmHg',
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
  barcode: '1000004512',
  sampleStatus: 'Received',
  labOrderId: id('041'),
  accessionNumber: 'NHLB260000512',
  patientId: id('051'),
  uhid: 'NH-UH-2600512',
  patientName: 'Murugan K',
  ageSex: '61 Y / M',
  clinicalNotes: null,
  lines: [
    line({
      resultId: PO2,
      testCode: 'ABG-PO2',
      testName: 'pO2',
      displayOrder: 1,
      decimals: 1,
      referenceText: '80 - 100',
    }),
    line({
      resultId: FIO2,
      testCode: 'ABG-FIO2',
      testName: 'FiO2 (inspired oxygen)',
      displayOrder: 2,
      unit: '%',
      suggestedValue: '21',
    }),
    line({
      resultId: PF,
      testCode: 'ABG-PF',
      testName: 'P/F ratio (pO2/FiO2)',
      displayOrder: 3,
      formula: 'pO2 ÷ (FiO2/100)',
      referenceText: '> 300',
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

const saved = () => server.requests.filter((r) => r.method === 'POST' && r.path === '/api/v1/lab/results');

describe('arterial blood gas entry', SCREEN_TESTS, () => {
  it('opens with the FiO2 filled in as room air, saved only when the technician saves', async () => {
    const user = userEvent.setup();
    openScreen(`/lab/worklist?sampleId=${SAMPLE}`);

    const fio2 = await screen.findByRole('textbox', { name: 'FiO2 (inspired oxygen) result in %' }, SLOW);
    expect(fio2).toHaveValue('21');
    expect(
      screen.getByText(
        'Filled in with 21 % (room air): change it if the patient was on oxygen. Nothing is calculated from it until it is saved.',
      ),
    ).toBeInTheDocument();
    expect(screen.getByText('Calculated by HC360 when its inputs are in: pO2 ÷ (FiO2/100).')).toBeInTheDocument();
    expect(saved()).toHaveLength(0);

    await user.type(screen.getByRole('textbox', { name: 'pO2 result in mmHg' }), '92.0');
    await user.click(screen.getByRole('button', { name: 'Save results' }));

    await waitFor(() => expect(saved()).toHaveLength(1));
    const entries = (saved()[0]!.body as { entries: { resultId: string; value: string | null }[] }).entries;
    expect(entries.map((entry) => [entry.resultId, entry.value])).toEqual([
      [PO2, '92.0'],
      [FIO2, '21'],
    ]);
  });

  it('saves the FiO2 the technician typed over room air, and no longer calls it filled in', async () => {
    const user = userEvent.setup();
    openScreen(`/lab/worklist?sampleId=${SAMPLE}`);

    const fio2 = await screen.findByRole('textbox', { name: 'FiO2 (inspired oxygen) result in %' }, SLOW);
    await user.clear(fio2);
    await user.type(fio2, '40');
    expect(screen.queryByText(/Filled in with 21 %/)).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save results' }));

    await waitFor(() => expect(saved()).toHaveLength(1));
    const entries = (saved()[0]!.body as { entries: { resultId: string; value: string | null }[] }).entries;
    expect(entries.map((entry) => [entry.resultId, entry.value])).toEqual([[FIO2, '40']]);
  });
});
