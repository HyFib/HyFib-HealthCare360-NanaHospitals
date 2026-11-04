import type * as Core from '@hc360/core';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { SimulatorOpenRecord, SimulatorRecordFinding, SimulatorRecordsView } from './simulatorRecordsApi';

// Administration › Simulator records, whole with the shell, against a fake server (audit P6): the records the simulators
// made that are still open, each with a link to the record it is corrected on, and no sign-off while any is open.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const RECORDS = '/api/v1/admin/system/simulator-records';
const SWEPT_AT = '2026-10-29T04:30:00Z';

const finding = (overrides: Partial<SimulatorRecordFinding> & Pick<SimulatorRecordFinding, 'code' | 'title'>) => ({
  severity: 'critical' as const,
  count: 0,
  amount: null,
  first: null,
  last: null,
  examples: [],
  guidance: 'What to do about them.',
  open: 0,
  openAmount: null,
  ...overrides,
});

const view = (paymentsOpen: number): SimulatorRecordsView => ({
  latest: {
    sweptAt: SWEPT_AT,
    sweptBy: 'IT Officer',
    total: 5,
    newest: '2026-10-28T18:45:00Z',
    criticalOpen: paymentsOpen,
    findings: [
      finding({
        code: 'payments',
        title: 'Online payments the payment simulator took',
        count: 2,
        amount: 900,
        first: '2026-10-27T05:00:00Z',
        last: '2026-10-28T18:45:00Z',
        examples: ['NHPRC26000124', 'NHPRC26000123'],
        open: paymentsOpen,
        openAmount: paymentsOpen ? 500 : null,
      }),
      finding({ code: 'refunds', title: 'Refunds the payment simulator said it paid back' }),
      finding({
        code: 'messages',
        title: 'Messages the simulators marked sent',
        severity: 'warning',
        count: 3,
        examples: ['otp.signin'],
        open: null,
      }),
    ],
  },
  signedOff: null,
  outstanding: true,
  liveServer: true,
});

// Made late in the evening, India time: the receipts list is opened on the receipt's own day there, not UTC's.
const OPEN_PAYMENT: SimulatorOpenRecord = {
  reference: 'NHPRC26000124',
  at: '2026-10-28T18:45:00Z',
  amount: 500,
  entity: 'Receipt',
  entityId: '0192a000-0000-7000-8000-00000000c001',
  patientId: '0192a000-0000-7000-8000-00000000c900',
};

let server: ReturnType<typeof fakeServer>;

const serve = (paymentsOpen: number) => {
  server = fakeServer({
    [`GET ${RECORDS}`]: view(paymentsOpen),
    [`GET ${RECORDS}/payments/open`]: paymentsOpen ? [OPEN_PAYMENT] : [],
    // The receipts page a record links to: it reads the letterhead for its prints as soon as it opens.
    'GET /api/v1/print/letterhead': { name: 'Nana Hospitals, Pune' },
    [`POST ${RECORDS}/sign-off`]: {
      signedOffAt: '2026-10-29T05:00:00Z',
      signedOffBy: 'IT Officer',
      sweptAt: SWEPT_AT,
      coversUntil: '2026-10-28T18:45:00Z',
      total: 5,
      note: 'Training receipts reversed; messages reviewed.',
    },
    'GET /api/v1/billing/receipts': paged([]),
  });
};

beforeEach(() => {
  stubBrowserApis();
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('simulator records screen', SCREEN_TESTS, () => {
  it('lists the records still open with a link to each, and takes no sign-off while any is open', async () => {
    serve(1);
    const user = userEvent.setup();
    const { router } = openScreen('/admin/simulator-records');

    expect(await screen.findByText('1 record made by simulators not corrected yet', {}, SLOW)).toBeInTheDocument();
    const kinds = screen.getByRole('table', { name: 'Records made by simulators' });
    const column = within(kinds)
      .getAllByRole('columnheader')
      .findIndex((header) => header.textContent === 'Still open');
    const stillOpen = (title: string) =>
      within(within(kinds).getByText(title).closest('tr')!).getAllByRole('cell')[column]?.textContent;
    expect(stillOpen('Online payments the payment simulator took')).toBe('1, ₹500.00');
    // The kinds the IT officer acknowledges have nothing to count as open.
    expect(stillOpen('Messages the simulators marked sent')).toBe('—');

    // A note is not enough while a payment the simulator took still stands as received.
    await user.type(screen.getByRole('textbox', { name: /What was done/ }), 'Training receipts reversed by billing.');
    expect(screen.getByRole('button', { name: 'Sign off this sweep' })).toBeDisabled();
    expect(screen.getByText(/1 critical record is not corrected yet/)).toBeInTheDocument();

    // The worklist is read when it is opened, and each record links to where it is corrected.
    expect(server.requests.some((request) => request.path === `${RECORDS}/payments/open`)).toBe(false);
    await user.click(
      screen.getByRole('button', { name: /Online payments the payment simulator took: 1, ₹500\.00 still open/ }),
    );
    const open = await screen.findByRole(
      'table',
      { name: 'Online payments the payment simulator took: still open' },
      SLOW,
    );
    await user.click(await within(open).findByRole('button', { name: 'Open the receipt: NHPRC26000124' }, SLOW));
    await waitFor(() => expect(router.state.location.pathname).toBe('/billing/receipts'), SLOW);
    expect(router.state.location.search).toMatchObject({ q: 'NHPRC26000124', from: '2026-10-29', to: '2026-10-29' });
    // The receipts page opens with what it asks for answered (afterEach), however quickly the test got here.
    expect(await screen.findByRole('heading', { name: 'Receipts' }, SLOW)).toBeInTheDocument();
    expect(server.requests.filter((request) => request.method === 'POST')).toEqual([]);
  });

  it('signs off the sweep reviewed, with the note, once nothing is open', async () => {
    serve(0);
    const user = userEvent.setup();
    openScreen('/admin/simulator-records');

    expect(await screen.findByText('5 records made by simulators, not signed off', {}, SLOW)).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'To correct' })).not.toBeInTheDocument();
    const signOff = screen.getByRole('button', { name: 'Sign off this sweep' });
    expect(signOff).toBeDisabled();
    await user.type(
      screen.getByRole('textbox', { name: /What was done/ }),
      '  Training receipts reversed; messages reviewed.  ',
    );
    await user.click(signOff);

    await waitFor(
      () =>
        expect(server.requests.filter((request) => request.method === 'POST')).toEqual([
          expect.objectContaining({
            path: `${RECORDS}/sign-off`,
            body: { sweptAt: SWEPT_AT, note: 'Training receipts reversed; messages reviewed.' },
          }),
        ]),
      SLOW,
    );
  });
});
