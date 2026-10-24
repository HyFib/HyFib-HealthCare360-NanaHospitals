import type * as Core from '@hc360/core';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { checkupKeys, type BookingDto } from '@/modules/health-checkups/api';
import { inventoryQueries, type CountDetail } from '@/modules/inventory/api';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis, type FakeRequest } from './appScreen';

// Screens that keep unsaved edits over a record read again in the background (REL-04): what they save carries the
// version those edits were made on, so a change someone else saved meanwhile is refused instead of overwritten.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const COUNT: CountDetail = {
  id: '0192a000-0000-7000-8000-00000000c001',
  number: 'SC-26-0031',
  storeId: '0192a000-0000-7000-8000-00000000c0a1',
  storeName: 'OP Pharmacy',
  status: 'InProgress',
  startedAt: '2026-10-23T03:30:00Z',
  submittedAt: null,
  itemTypeFilter: null,
  categoryFilterId: null,
  remarks: null,
  adjustmentId: null,
  lines: [
    {
      id: '0192a000-0000-7000-8000-00000000c101',
      itemId: '0192a000-0000-7000-8000-00000000c201',
      itemName: 'Paracetamol 500 mg',
      batchId: '0192a000-0000-7000-8000-00000000c301',
      batchNumber: 'PCM2411A',
      expiryDate: '2027-11-30',
      systemQuantity: 120,
      countedQuantity: null,
      varianceQuantity: null,
      unitCost: 1.2,
      remarks: null,
    },
    {
      id: '0192a000-0000-7000-8000-00000000c102',
      itemId: '0192a000-0000-7000-8000-00000000c202',
      itemName: 'Amoxicillin 500 mg',
      batchId: '0192a000-0000-7000-8000-00000000c302',
      batchNumber: 'AMX2405',
      expiryDate: '2027-05-31',
      systemQuantity: 60,
      countedQuantity: null,
      varianceQuantity: null,
      unitCost: 4.5,
      remarks: null,
    },
  ],
  version: 4,
};

const BOOKING: BookingDto = {
  id: '0192a000-0000-7000-8000-00000000e001',
  bookingNumber: 'HC-26-0107',
  patientId: '0192a000-0000-7000-8000-00000000e0a1',
  uhid: 'NH-000123',
  patientName: 'Sunita Kulkarni',
  packageId: '0192a000-0000-7000-8000-00000000e0b1',
  packageCode: 'EXEC',
  packageName: 'Executive health check',
  scheduledDate: '2026-10-23',
  status: 'InProgress',
  visitId: null,
  orderId: null,
  corporateBatchId: null,
  corporateName: null,
  corporatePayerId: null,
  notes: null,
  checkedInAt: '2026-10-23T03:30:00Z',
  summary: 'Fit for work.',
  recommendations: null,
  reviewedByPractitionerId: null,
  reviewedAt: null,
  completedAt: null,
  cancelReason: null,
  itinerary: [
    {
      id: '0192a000-0000-7000-8000-00000000e101',
      sequence: 1,
      departmentId: null,
      departmentName: 'Laboratory',
      description: 'Blood tests',
      status: 'Pending',
      completedAt: null,
      notes: null,
    },
    {
      id: '0192a000-0000-7000-8000-00000000e102',
      sequence: 2,
      departmentId: null,
      departmentName: 'Cardiology',
      description: 'ECG',
      status: 'Pending',
      completedAt: null,
      notes: null,
    },
  ],
  version: 1,
};

/** The server's answer to a save on a version that is no longer the record's. */
const concurrency = () =>
  new Response(JSON.stringify({ title: 'Changed meanwhile', status: 409, code: 'concurrency' }), {
    status: 409,
    headers: { 'content-type': 'application/problem+json' },
  });

let server: ReturnType<typeof fakeServer>;
/** The count sheet as the server has it now. */
let current: CountDetail;
/** Someone else's counts, saved just after this user's own. */
let afterSave: (() => void) | null;
/** The check-up as the server has it now. */
let booking: BookingDto;

/** Counts saved on the sheet, as the server keeps a save: on the version it was made on, which it moves on. */
function recordCounts(request: FakeRequest) {
  const body = request.body as { version: number; lines: { lineId: string; countedQuantity: number }[] };
  if (body.version !== current.version) {
    return concurrency();
  }
  const counted = new Map(body.lines.map((line) => [line.lineId, line.countedQuantity]));
  current = {
    ...current,
    lines: current.lines.map((line) => ({ ...line, countedQuantity: counted.get(line.id) ?? line.countedQuantity })),
    version: current.version + 1,
  };
  afterSave?.();
  return null;
}

/** A station closed on the check-up, which moves its version on. */
function closeStation(request: FakeRequest) {
  const itemId = request.path.split('/').at(-2);
  booking = {
    ...booking,
    itinerary: booking.itinerary.map((stop) =>
      stop.id === itemId ? { ...stop, status: 'Done', completedAt: '2026-10-23T04:10:00Z' } : stop,
    ),
    version: booking.version + 1,
  };
  return booking;
}

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  current = COUNT;
  afterSave = null;
  booking = BOOKING;
  server = fakeServer({
    [`GET /api/v1/inventory/counts/${COUNT.id}`]: () => current,
    [`PUT /api/v1/inventory/counts/${COUNT.id}/lines`]: recordCounts,
    [`POST /api/v1/inventory/counts/${COUNT.id}/submit`]: (request: FakeRequest) =>
      (request.body as { version: number }).version === current.version ? { adjustmentId: null } : concurrency(),
    'GET /api/v1/print/letterhead': { name: 'Nana Hospitals, Pune' },
    [`GET /api/v1/health-checkups/bookings/${BOOKING.id}`]: () => booking,
    [`POST /api/v1/health-checkups/bookings/${BOOKING.id}/itinerary/*`]: closeStation,
    [`PUT /api/v1/health-checkups/bookings/${BOOKING.id}/summary`]: (request: FakeRequest) => {
      const body = request.body as { summary: string; recommendations: string | null; version: number };
      if (body.version !== booking.version) {
        return concurrency();
      }
      booking = {
        ...booking,
        summary: body.summary,
        recommendations: body.recommendations,
        version: booking.version + 1,
      };
      return booking;
    },
  });
});

/** What was sent to `method path`, in order. */
const sent = (method: string, path: string) =>
  server.requests.filter((request) => request.method === method && request.path === path).map((r) => r.body);

/** A station of the check-up, by what is done there. */
const station = (description: string) =>
  within(screen.getByRole('list', { name: 'Check-up stations' }))
    .getByText(description)
    .closest('li')!;

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('editing a record someone else changes meanwhile (REL-04)', SCREEN_TESTS, () => {
  it('the count sheet saves on the version its first unsaved count was typed on', async () => {
    const { queryClient } = openScreen(`/inventory/counts/${COUNT.id}`);
    const paracetamol = await screen.findByRole(
      'spinbutton',
      { name: 'Counted quantity of Paracetamol 500 mg batch PCM2411A' },
      SLOW,
    );
    await userEvent.type(paracetamol, '118');

    // A colleague counts the amoxicillin, and the sheet is read again (as on going back to the window).
    current = {
      ...COUNT,
      lines: [COUNT.lines[0]!, { ...COUNT.lines[1]!, countedQuantity: 58, varianceQuantity: -2 }],
      version: 5,
    };
    await act(() => queryClient.refetchQueries({ queryKey: inventoryQueries.count(COUNT.id).queryKey }));
    const amoxicillin = screen.getByRole('spinbutton', {
      name: 'Counted quantity of Amoxicillin 500 mg batch AMX2405',
    });
    await waitFor(() => expect(amoxicillin).toHaveValue(58));
    expect(paracetamol).toHaveValue(118);

    await userEvent.click(screen.getByRole('button', { name: 'Save (Ctrl+S)' }));

    await waitFor(() => expect(server.requests.some((request) => request.method === 'PUT')).toBe(true));
    // The count goes with version 4, which the server refuses: it was typed before the colleague's count was seen.
    expect(server.requests.find((request) => request.method === 'PUT')?.body).toEqual({
      version: 4,
      lines: [{ lineId: COUNT.lines[0]!.id, countedQuantity: 118, remarks: null }],
    });
  });

  it('the count sheet is submitted on the version its own save of the typed counts left it at', async () => {
    openScreen(`/inventory/counts/${COUNT.id}`);
    const paracetamol = await screen.findByRole(
      'spinbutton',
      { name: 'Counted quantity of Paracetamol 500 mg batch PCM2411A' },
      SLOW,
    );
    await userEvent.type(paracetamol, '118');

    await userEvent.click(screen.getByRole('button', { name: 'Submit count' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Submit' }));

    await waitFor(() => expect(sent('POST', `/api/v1/inventory/counts/${COUNT.id}/submit`)).toHaveLength(1));
    // Saved on version 4, the sheet as shown. The save answers with nothing; the sheet read back is version 5 and holds
    // just this count on what was shown, so the count is submitted on it.
    expect(sent('PUT', `/api/v1/inventory/counts/${COUNT.id}/lines`)).toEqual([
      { version: 4, lines: [{ lineId: COUNT.lines[0]!.id, countedQuantity: 118, remarks: null }] },
    ]);
    expect(sent('POST', `/api/v1/inventory/counts/${COUNT.id}/submit`)).toEqual([{ version: 5 }]);
  });

  it('the count sheet is not submitted when counts were saved on it between its own save and the submit', async () => {
    openScreen(`/inventory/counts/${COUNT.id}`);
    const paracetamol = await screen.findByRole(
      'spinbutton',
      { name: 'Counted quantity of Paracetamol 500 mg batch PCM2411A' },
      SLOW,
    );
    await userEvent.type(paracetamol, '118');
    // A colleague's count of the amoxicillin lands just after this user's save.
    afterSave = () => {
      current = {
        ...current,
        lines: [current.lines[0]!, { ...current.lines[1]!, countedQuantity: 58, varianceQuantity: -2 }],
        version: current.version + 1,
      };
    };

    await userEvent.click(screen.getByRole('button', { name: 'Submit count' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Submit' }));

    expect(await screen.findByText('The count sheet changed meanwhile')).toBeInTheDocument();
    expect(sent('POST', `/api/v1/inventory/counts/${COUNT.id}/submit`)).toEqual([]);
    // The sheet as it is now: this user's count saved, the colleague's beside it, to check before submitting. (Found
    // once the closing confirmation no longer hides the page.)
    const amoxicillin = await screen.findByRole(
      'spinbutton',
      { name: 'Counted quantity of Amoxicillin 500 mg batch AMX2405' },
      SLOW,
    );
    await waitFor(() => expect(amoxicillin).toHaveValue(58));
    expect(paracetamol).toHaveValue(118);
  });

  it("the check-up summary keeps its version when another summary was saved before the user's own station", async () => {
    openScreen(`/health-checkups/bookings/${BOOKING.id}`);
    const summary = await screen.findByRole('textbox', { name: 'Consolidated summary' }, SLOW);
    // Another doctor saves a summary (version 2), which this screen has not read.
    booking = { ...booking, summary: 'Fit for work. Mild anaemia: repeat Hb in 3 months.', version: 2 };

    // This user closes the blood tests: the check-up comes back as version 3, with the other doctor's summary.
    await userEvent.click(within(station('Blood tests')).getByRole('button', { name: 'Done' }));
    await screen.findByText('Next: Cardiology — ECG');
    await userEvent.type(summary, ' Advised regular exercise.');
    await userEvent.click(screen.getByRole('button', { name: 'Save summary' }));

    // Saved on version 1, which the server refuses: the other doctor's summary is not written over.
    await waitFor(() => expect(sent('PUT', `/api/v1/health-checkups/bookings/${BOOKING.id}/summary`)).toHaveLength(1));
    expect(sent('PUT', `/api/v1/health-checkups/bookings/${BOOKING.id}/summary`)[0]).toMatchObject({ version: 1 });
  });

  it("the check-up summary follows the user's own station closed after someone else's, which it does not hold", async () => {
    const { queryClient } = openScreen(`/health-checkups/bookings/${BOOKING.id}`);
    const summary = await screen.findByRole('textbox', { name: 'Consolidated summary' }, SLOW);
    // The ECG is closed at its station (version 2), and the screen reads it.
    booking = {
      ...booking,
      itinerary: [
        booking.itinerary[0]!,
        { ...booking.itinerary[1]!, status: 'Done', completedAt: '2026-10-23T03:55:00Z' },
      ],
      version: 2,
    };
    await act(() => queryClient.refetchQueries({ queryKey: checkupKeys.booking(BOOKING.id) }));
    await waitFor(() => expect(within(station('ECG')).queryByRole('button', { name: 'Done' })).toBeNull());

    // This user closes the blood tests (version 3): the summary is as it was filled, so the editor follows.
    await userEvent.click(within(station('Blood tests')).getByRole('button', { name: 'Done' }));
    await screen.findByText('All stations closed');
    await userEvent.type(summary, ' Advised regular exercise.');
    await userEvent.click(screen.getByRole('button', { name: 'Save summary' }));

    await waitFor(() => expect(sent('PUT', `/api/v1/health-checkups/bookings/${BOOKING.id}/summary`)).toHaveLength(1));
    expect(sent('PUT', `/api/v1/health-checkups/bookings/${BOOKING.id}/summary`)[0]).toEqual({
      summary: 'Fit for work. Advised regular exercise.',
      recommendations: null,
      version: 3,
    });
  });
});
