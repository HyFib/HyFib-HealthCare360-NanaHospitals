import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import { ANSWERS } from '@/test/roleAccess/inventory';
import type { ColdChainExcursionDto, ColdStorageUnitDto } from './coldChain';

// Stores › Cold chain: each refrigerator with its last reading and today's two checks; a reading entered by hand; an
// excursion listed until what was done about the stock is recorded.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const FRIDGE: ColdStorageUnitDto = {
  id: '0192a000-0000-7000-8000-00000000cc01',
  storeId: '0192a000-0000-7000-8000-00000000cc10',
  storeName: 'OP Pharmacy',
  code: 'F1',
  name: 'Vaccine fridge',
  kind: 'Refrigerator',
  minCelsius: 2,
  maxCelsius: 8,
  isActive: true,
  hasLogger: true,
  loggerKeyIssuedAt: '2026-11-01T04:00:00Z',
  lastReadingAt: '2026-11-08T05:00:00Z',
  lastCelsius: 11.2,
  lastOutOfRange: true,
  openExcursionId: '0192a000-0000-7000-8000-00000000cc21',
  lastLoggerReadingAt: '2026-11-08T05:00:00Z',
  morningCheck: 'Taken',
  eveningCheck: 'Upcoming',
  version: 3,
};

const EXCURSION: ColdChainExcursionDto = {
  id: FRIDGE.openExcursionId!,
  unitId: FRIDGE.id,
  startedAt: '2026-11-08T05:00:00Z',
  endedAt: null,
  lowestCelsius: 11.2,
  highestCelsius: 11.2,
  readingsOutOfRange: 1,
  minCelsius: 2,
  maxCelsius: 8,
  extreme: 'up to 11.2 °C',
  itemsAtRisk: 'Insulin glargine 100 IU/ml pen (batch G12, 10 PEN)',
  actionTaken: null,
  actionBy: null,
  actionAt: null,
};

function open() {
  stubBrowserApis();
  const server = fakeServer({
    ...ANSWERS,
    'GET /api/v1/inventory/stores': [],
    'GET /api/v1/inventory/cold-chain/units': [FRIDGE],
    'GET /api/v1/inventory/cold-chain/excursions': [EXCURSION],
    [`POST /api/v1/inventory/cold-chain/units/${FRIDGE.id}/readings`]: {
      id: '0192a000-0000-7000-8000-00000000cc31',
      celsius: 6,
      outOfRange: false,
      allowedRange: '2 °C to 8 °C',
      excursionId: null,
    },
  });
  openScreen('/inventory/cold-chain');
  return server;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('the cold-chain log', SCREEN_TESTS, () => {
  it('shows each unit, the excursion to act on and the stock at risk', async () => {
    open();
    expect((await screen.findAllByText('Vaccine fridge', {}, SLOW)).length).toBeGreaterThan(0);
    expect(screen.getAllByText('Out of range').length).toBeGreaterThan(0);
    expect(screen.getByText('Morning: Taken')).toBeInTheDocument();
    expect(await screen.findByText(EXCURSION.itemsAtRisk!, {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('records a reading entered by hand', async () => {
    const server = open();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Record' }, SLOW));
    const dialog = await screen.findByRole('dialog', {}, SLOW);
    await user.type(within(dialog).getByRole('spinbutton', { name: /Reading/ }), '6');
    await user.click(within(dialog).getByRole('button', { name: 'Record' }));
    await waitFor(
      () =>
        expect(server.requests.find((r) => r.method === 'POST' && r.path.endsWith('/readings'))?.body).toEqual({
          celsius: 6,
          takenAt: null,
          note: null,
        }),
      SLOW,
    );
  });
});
