import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { PackageDto } from './api';

// A health check-up package's price is its service's, and changes only when two people agree (maker-checker), on the
// package's screen against a fake server: the form holds the price that waits, with a notice against the price in use,
// and a new price asks for approval, with a reason, and says so.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const packageId = '0192a000-0000-7000-8000-00000000c001';

const PACKAGE: PackageDto = {
  id: packageId,
  code: 'HC-DIAB',
  name: 'Diabetes screen',
  description: null,
  gender: 'Any',
  minAgeYears: null,
  maxAgeYears: null,
  price: 1800,
  serviceItemId: '0192a000-0000-7000-8000-00000000c101',
  isActive: true,
  items: [
    {
      id: '0192a000-0000-7000-8000-00000000c201',
      kind: 'Consultation',
      serviceItemId: null,
      serviceCode: 'CONS-NEW',
      name: 'General medicine consultation',
      category: 'Consultation',
      departmentCode: 'GM',
      gender: 'Any',
      minAgeYears: null,
      sequence: 1,
    },
  ],
  version: 4,
};

const DEPARTMENTS = [{ id: '0192a000-0000-7000-8000-00000000c301', code: 'GM', name: 'General Medicine' }];

const ASKED = {
  id: '0192a000-0000-7000-8000-00000000c901',
  kind: 'masters.service_price',
  summary: 'Price of HC-DIAB Diabetes screen: ₹1,800.00 → ₹2,100.00',
};

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("a check-up package's price, waiting for approval", SCREEN_TESTS, () => {
  it('holds the price that waits in its form, with a notice against the price in use', async () => {
    server = fakeServer({
      [`GET /api/v1/health-checkups/packages/${packageId}`]: {
        ...PACKAGE,
        pendingPrice: {
          approvalId: '0192a000-0000-7000-8000-00000000c902',
          asked: { basePrice: 2100, taxRate: 0, isPriceEditable: false, isDiscountAllowed: true, isActive: true },
          requestedBy: 'Front Office',
          requestedAt: '2026-11-01T05:00:00Z',
        },
      },
      'GET /api/v1/masters/lookups/departments': DEPARTMENTS,
    });
    openScreen(`/health-checkups/packages/${packageId}`);

    expect(await screen.findByText('Waiting for approval', {}, SLOW)).toBeInTheDocument();
    expect(
      screen.getByText(/Price: price ₹2,100\.00 \(now ₹1,800\.00\), asked for by Front Office/),
    ).toBeInTheDocument();
    expect(screen.getByRole('spinbutton', { name: /Price/ })).toHaveValue(2100);
    await expectNoAxeViolations();
  });

  it('asks for approval of a new price, with a reason, and says so', async () => {
    server = fakeServer({
      [`GET /api/v1/health-checkups/packages/${packageId}`]: PACKAGE,
      [`PUT /api/v1/health-checkups/packages/${packageId}`]: ASKED,
      'GET /api/v1/masters/lookups/departments': DEPARTMENTS,
    });
    const user = userEvent.setup();
    openScreen(`/health-checkups/packages/${packageId}`);

    const price = await screen.findByRole('spinbutton', { name: /Price/ }, SLOW);
    await user.clear(price);
    await user.type(price, '2100');
    await user.type(screen.getByRole('textbox', { name: /Reason for a new price/ }), 'New laboratory rates.');
    await user.click(screen.getByRole('button', { name: /Save package/ }));

    expect((await screen.findAllByText('Asked for approval', {}, SLOW)).length).toBeGreaterThan(0);
    const puts = server.requests.filter((request) => request.method === 'PUT');
    expect(puts).toHaveLength(1);
    expect(puts[0]?.body).toMatchObject({ price: 2100, reason: 'New laboratory rates.', version: 4, isActive: true });
  });
});
