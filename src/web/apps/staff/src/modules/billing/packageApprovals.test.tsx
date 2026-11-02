import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { PackageDetail, PackageListItem, TariffPlanListItem } from './mastersApi';
import { amountInputText } from './money';

// A package's price and cover change only when two people agree (maker-checker), on the screen against a fake server:
// the package's form holds the terms that wait, with a notice against those in use, and a change to its price asks for
// approval, with a reason, and says so.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const packageId = '0192a000-0000-7000-8000-00000000e001';
const serviceId = '0192a000-0000-7000-8000-00000000e101';
const generalPlan = '0192a000-0000-7000-8000-00000000e201';

const PLANS: TariffPlanListItem[] = [
  {
    id: generalPlan,
    code: 'GEN',
    name: 'General',
    type: 'General',
    payerId: null,
    payerName: null,
    facilityId: null,
    defaultDiscountPercent: 0,
    isDefault: true,
    isActive: true,
    priceCount: 0,
  },
];

const LISTED: PackageListItem = {
  id: packageId,
  code: 'PKG-CELL3',
  name: 'Cellulitis care (3 days)',
  price: 9000,
  lengthOfStayDays: 3,
  tariffPlanId: null,
  usedFor: 'InPatient',
  isActive: true,
  inclusionCount: 1,
  hbpCode: null,
};

const PACKAGE: PackageDetail = {
  id: packageId,
  code: 'PKG-CELL3',
  name: 'Cellulitis care (3 days)',
  serviceItemId: serviceId,
  serviceCode: 'PKG-CELL',
  price: 9000,
  lengthOfStayDays: 3,
  tariffPlanId: null,
  usedFor: 'InPatient',
  description: null,
  isActive: true,
  inclusions: [
    {
      id: '0192a000-0000-7000-8000-00000000e301',
      serviceItemId: null,
      serviceCode: null,
      serviceName: null,
      category: 'BedCharge',
      maxQuantity: 3,
      maxAmount: null,
      notes: 'Three bed days',
    },
  ],
  version: 3,
  hbpCode: null,
  hbpProcedureName: null,
};

const ASKED = {
  id: '0192a000-0000-7000-8000-00000000e901',
  kind: 'billing.package',
  summary: 'Package PKG-CELL3 Cellulitis care (3 days): price ₹9,000.00 → ₹9,900.00 (+10%)',
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

async function openPackage() {
  const user = userEvent.setup();
  openScreen('/billing/packages');
  const packages = await screen.findByRole('table', { name: 'Packages' }, SLOW);
  await user.click(await within(packages).findByText('Cellulitis care (3 days)', {}, SLOW));
  return { user, drawer: await screen.findByRole('dialog', { name: /Cellulitis care/ }, SLOW) };
}

describe("a package's price and cover, waiting for approval", SCREEN_TESTS, () => {
  it('holds the terms that wait in its form, with a notice against those in use', async () => {
    server = fakeServer({
      'GET /api/v1/billing/packages': [LISTED],
      [`GET /api/v1/billing/packages/${packageId}`]: {
        ...PACKAGE,
        pendingTerms: {
          approvalId: '0192a000-0000-7000-8000-00000000e902',
          asked: {
            serviceItemId: serviceId,
            price: 9900,
            tariffPlanId: null,
            usedFor: 'InPatient',
            isActive: true,
            covers: [
              { serviceItemId: null, category: 'BedCharge', maxQuantity: 3, maxAmount: null, notes: 'Three bed days' },
            ],
          },
          requestedBy: 'Billing Desk',
          requestedAt: '2026-11-01T05:00:00Z',
          serviceCode: 'PKG-CELL',
          inclusions: [
            {
              id: '00000000-0000-0000-0000-000000000000',
              serviceItemId: null,
              serviceCode: null,
              serviceName: null,
              category: 'BedCharge',
              maxQuantity: 3,
              maxAmount: null,
              notes: 'Three bed days',
            },
          ],
        },
      },
      'GET /api/v1/billing/tariffs': PLANS,
    });

    const { drawer } = await openPackage();
    expect(await within(drawer).findByText('Waiting for approval')).toBeInTheDocument();
    expect(
      within(drawer).getByText(/Price and cover: price ₹9,900\.00 \(now ₹9,000\.00\), asked for by Billing Desk/),
    ).toBeInTheDocument();
    expect(within(drawer).getByRole('textbox', { name: /Package price/ })).toHaveValue(amountInputText(9900));
    await expectNoAxeViolations();
  });

  it('asks for approval of a new price, with a reason, and says so', async () => {
    server = fakeServer({
      'GET /api/v1/billing/packages': [LISTED],
      [`GET /api/v1/billing/packages/${packageId}`]: PACKAGE,
      [`PUT /api/v1/billing/packages/${packageId}`]: ASKED,
      'GET /api/v1/billing/tariffs': PLANS,
    });

    const { user, drawer } = await openPackage();
    const price = within(drawer).getByRole('textbox', { name: /Package price/ });
    await user.clear(price);
    await user.type(price, '9900');
    await user.type(
      within(drawer).getByRole('textbox', { name: /Reason for a change to the price or cover/ }),
      'Dressings cost more.',
    );
    await user.click(within(drawer).getByRole('button', { name: 'Save package' }));

    expect((await screen.findAllByText('Asked for approval', {}, SLOW)).length).toBeGreaterThan(0);
    const puts = server.requests.filter((request) => request.method === 'PUT');
    expect(puts).toHaveLength(1);
    expect(puts[0]?.body).toMatchObject({ price: 9900, reason: 'Dressings cost more.', version: 3, isActive: true });
  });
});
