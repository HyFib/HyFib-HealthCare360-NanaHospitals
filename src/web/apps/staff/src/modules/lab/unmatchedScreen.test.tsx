import type * as Core from '@hc360/core';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { ResultSheet, ResultSheetLine, SampleLookup, UnmatchedResultDto, WorklistItem } from './api';

// The lab's unmatched analyser results, whole with the shell against a fake server: results an analyser sent that
// could not go on a sample's test wait in a list the bench sees counted, until someone who enters results assigns
// each to the right test or discards it with a reason.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const id = (suffix: string) => `0192a000-0000-7000-8000-00000000c${suffix}`;
const SAMPLE = id('001');
const HB_RESULT = id('011');
const TLC_RESULT = id('012');

const kept = (
  overrides: Partial<UnmatchedResultDto> & Pick<UnmatchedResultDto, 'id' | 'reason' | 'detail'>,
): UnmatchedResultDto => ({
  instrumentCode: 'XN550',
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
  resultedAt: '2026-10-31T04:10:00Z',
  receivedAt: '2026-10-31T04:11:00Z',
  status: 'Open',
  assignedResultId: null,
  discardReason: null,
  resolvedBy: null,
  resolvedAt: null,
  ...overrides,
});

const WAITING: UnmatchedResultDto[] = [
  kept({ id: id('101'), reason: 'NoSampleId', detail: 'XN550 sent the result without a sample id.' }),
  kept({
    id: id('102'),
    reason: 'UnknownSample',
    detail: "No sample of XN550's facility has the barcode 000004567.",
    sampleBarcode: '000004567',
    channelCode: 'WBC',
    value: '7.8',
    instrumentUnit: '10*3/uL',
    instrumentFlags: 'W',
    instrumentComment: 'Platelet clumps',
  }),
];

const line = (
  overrides: Partial<ResultSheetLine> & Pick<ResultSheetLine, 'resultId' | 'testCode' | 'testName'>,
): ResultSheetLine => ({
  labOrderItemId: id('021'),
  orderedTest: 'Complete blood count',
  labTestId: id('031'),
  groupName: null,
  displayOrder: 1,
  resultType: 'Numeric',
  status: 'Pending',
  value: null,
  unit: 'g/dL',
  decimals: 1,
  options: [],
  flag: null,
  isCritical: false,
  referenceText: '12.0 - 15.5',
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
  barcode: '000001234',
  sampleStatus: 'InProcess',
  labOrderId: id('041'),
  accessionNumber: 'NHLB260000412',
  patientId: id('051'),
  uhid: 'NH-UH-2600412',
  patientName: 'Asha Devi',
  ageSex: '42 Y / F',
  clinicalNotes: null,
  lines: [
    line({ resultId: HB_RESULT, testCode: 'HB', testName: 'Haemoglobin' }),
    line({
      resultId: TLC_RESULT,
      testCode: 'TLC',
      testName: 'Total leucocyte count',
      status: 'Verified',
      value: '7600',
      unit: '/cumm',
    }),
  ],
};

const LOOKUP: SampleLookup = {
  id: SAMPLE,
  barcode: '000001234',
  status: 'InProcess',
  labOrderId: id('041'),
  accessionNumber: 'NHLB260000412',
  patientId: id('051'),
  uhid: 'NH-UH-2600412',
  patientName: 'Asha Devi',
  ageSex: '42 Y / F',
  container: 'Edta',
  specimenType: 'WholeBlood',
  section: 'Hematology',
  priority: 'Routine',
  tests: ['Complete blood count'],
  collectedAt: '2026-10-31T03:30:00Z',
  receivedAt: '2026-10-31T03:50:00Z',
  rejectionReason: null,
};

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  server = fakeServer({
    'GET /api/v1/lab/unmatched-results': paged(WAITING),
    'GET /api/v1/lab/samples/by-barcode/000001234': LOOKUP,
    [`GET /api/v1/lab/samples/${SAMPLE}/results`]: SHEET,
    [`POST /api/v1/lab/unmatched-results/${id('101')}/assign`]: null,
    [`POST /api/v1/lab/unmatched-results/${id('102')}/discard`]: null,
    'GET /api/v1/lab/worklist': paged<WorklistItem>([]),
    'GET /api/v1/lab/qc/status': [],
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const sent = (method: string, path: string) => server.requests.filter((r) => r.method === method && r.path === path);

describe('unmatched analyser results', SCREEN_TESTS, () => {
  it('lists the results waiting, with what the analyser said and why each was kept aside', async () => {
    openScreen('/lab/unmatched');

    const table = await screen.findByRole('table', { name: 'Unmatched analyser results' }, SLOW);
    expect(await within(table).findByText('12.9 g/dL · status F', {}, SLOW)).toBeInTheDocument();
    expect(within(table).getByText('Not sent')).toBeInTheDocument();
    expect(within(table).getByText('No sample id')).toBeInTheDocument();
    expect(within(table).getByText('7.8 10*3/uL · flag W · status F')).toBeInTheDocument();
    expect(within(table).getByText('Comment: Platelet clumps')).toBeInTheDocument();
    expect(within(table).getByText('Unknown sample')).toBeInTheDocument();
    expect(within(table).getByText("No sample of XN550's facility has the barcode 000004567.")).toBeInTheDocument();
    // The waiting ones, unless asked for those already assigned or discarded.
    const asked = sent('GET', '/api/v1/lab/unmatched-results').at(-1)!;
    expect(asked.url.searchParams.get('status')).toBe('Open');
  });

  it('puts a result on the test chosen on the sample found by its barcode, never on a verified one', async () => {
    const user = userEvent.setup();
    openScreen('/lab/unmatched');
    const table = await screen.findByRole('table', { name: 'Unmatched analyser results' }, SLOW);
    await within(table).findByText('12.9 g/dL · status F', {}, SLOW);

    await user.click(within(table).getByRole('button', { name: 'Actions for the HGB result from XN550' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Assign to a test' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByRole('textbox', { name: /Sample barcode/ }), '000001234{Enter}');

    const haemoglobin = await within(dialog).findByRole('radio', { name: 'Haemoglobin: no result yet' }, SLOW);
    expect(within(dialog).getByRole('radio', { name: /Total leucocyte count: verified/ })).toBeDisabled();
    await user.click(haemoglobin);
    await user.click(within(dialog).getByRole('button', { name: 'Assign to Haemoglobin' }));

    await waitFor(() => expect(sent('POST', `/api/v1/lab/unmatched-results/${id('101')}/assign`)).toHaveLength(1));
    expect(sent('POST', `/api/v1/lab/unmatched-results/${id('101')}/assign`)[0]!.body).toEqual({ resultId: HB_RESULT });
  });

  it('discards a result with the reason given', async () => {
    const user = userEvent.setup();
    openScreen('/lab/unmatched');
    const table = await screen.findByRole('table', { name: 'Unmatched analyser results' }, SLOW);
    await within(table).findByText('Unknown sample', {}, SLOW);

    await user.click(within(table).getByRole('button', { name: 'Actions for the WBC result from XN550' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Discard' }));
    const dialog = await screen.findByRole('alertdialog');
    await user.type(within(dialog).getByRole('textbox'), 'Control or calibrator run');
    await user.click(within(dialog).getByRole('button', { name: 'Discard result' }));

    await waitFor(() => expect(sent('POST', `/api/v1/lab/unmatched-results/${id('102')}/discard`)).toHaveLength(1));
    expect(sent('POST', `/api/v1/lab/unmatched-results/${id('102')}/discard`)[0]!.body).toEqual({
      reason: 'Control or calibrator run',
    });
  });

  it('offers no assigning or discarding to whoever does not enter results', async () => {
    const user = userEvent.setup();
    openScreen('/lab/unmatched', {
      ...STAFF_USER,
      roles: ['Pathologist'],
      permissions: ['lab.results.verify', 'lab.orders.read'],
    });
    const table = await screen.findByRole('table', { name: 'Unmatched analyser results' }, SLOW);
    await within(table).findByText('Unknown sample', {}, SLOW);

    await user.click(within(table).getByRole('button', { name: 'Actions for the WBC result from XN550' }));
    expect(await screen.findByRole('menuitem', { name: 'No actions' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Assign to a test' })).not.toBeInTheDocument();
  });

  it('tells the bench at result entry how many analyser results wait to be matched', async () => {
    const user = userEvent.setup();
    const { router } = openScreen('/lab/worklist');

    expect(await screen.findByText('2 analyser results are waiting to be matched', {}, SLOW)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Match results' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/lab/unmatched'));
  });
});
