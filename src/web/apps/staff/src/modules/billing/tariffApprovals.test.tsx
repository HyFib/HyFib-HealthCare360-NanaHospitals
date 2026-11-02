import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { TariffPlanDetail, TariffPlanListItem, TariffPriceDto } from './mastersApi';

// A tariff plan's prices and terms change only when two people agree (maker-checker), on the tariffs screen against a
// fake server: prices and terms that wait show as such, with a notice; a price saved says it was asked for, with the
// reason and the version it was read at; the plan's form holds the terms that wait, so saving again asks for nothing
// twice.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const planId = '0192a000-0000-7000-8000-00000000e001';

const LIST: TariffPlanListItem[] = [
  {
    id: planId,
    code: 'STAR',
    name: 'Star Health TPA',
    type: 'Insurance',
    payerId: null,
    payerName: null,
    facilityId: null,
    defaultDiscountPercent: 10,
    isDefault: false,
    isActive: true,
    priceCount: 1,
  },
];

const PLAN: TariffPlanDetail = {
  id: planId,
  code: 'STAR',
  name: 'Star Health TPA',
  type: 'Insurance',
  payerId: null,
  facilityId: null,
  defaultDiscountPercent: 10,
  isDefault: false,
  isActive: true,
  description: null,
  bedMultipliers: [],
  version: 3,
};

const PRICE: TariffPriceDto = {
  id: '0192a000-0000-7000-8000-00000000e101',
  serviceItemId: '0192a000-0000-7000-8000-00000000e201',
  serviceCode: 'PRC-DRESS',
  serviceName: 'Wound dressing',
  category: 'Procedure',
  basePrice: 400,
  price: 450,
  effectiveFrom: '2031-01-01',
  effectiveTo: null,
  version: 2,
};

const ASKED = {
  id: '0192a000-0000-7000-8000-00000000e901',
  kind: 'billing.tariff_prices',
  summary: 'Prices of tariff plan STAR Star Health TPA: PRC-DRESS Wound dressing at ₹500.00 from 1 Jan 2031',
};

let server: ReturnType<typeof fakeServer>;

function serve(plan: TariffPlanDetail) {
  server = fakeServer({
    'GET /api/v1/billing/tariffs': LIST,
    [`GET /api/v1/billing/tariffs/${planId}`]: plan,
    [`GET /api/v1/billing/tariffs/${planId}/prices`]: paged([PRICE]),
    [`PUT /api/v1/billing/tariffs/${planId}/prices`]: ASKED,
    'GET /api/v1/insurance/payers': [],
    'GET /api/v1/masters/lookups/services': [],
  });
}

beforeEach(() => {
  stubBrowserApis();
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('a tariff plan whose prices and terms wait for approval', SCREEN_TESTS, () => {
  it('shows the prices that wait, and says a price saved was asked for', async () => {
    serve({
      ...PLAN,
      pendingPrices: {
        approvalId: '0192a000-0000-7000-8000-00000000e902',
        set: [{ serviceItemId: PRICE.serviceItemId, price: 600, effectiveFrom: '2031-04-01', effectiveTo: null }],
        remove: [],
        requestedBy: 'Billing Desk',
        requestedAt: '2026-11-01T05:00:00Z',
      },
    });
    const user = userEvent.setup();
    openScreen(`/billing/tariffs?plan=${planId}`);

    expect(await screen.findByText('Waiting for approval', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText(/A price of ₹600\.00 from .*2031, asked for by Billing Desk/)).toBeInTheDocument();
    await expectNoAxeViolations();

    await user.click(await screen.findByText('Wound dressing'));
    const dialog = await screen.findByRole('dialog', { name: /Price of Wound dressing/ });
    const amount = within(dialog).getByRole('textbox', { name: /Plan price/ });
    await user.clear(amount);
    await user.type(amount, '500');
    await user.type(within(dialog).getByRole('textbox', { name: /Reason, for the approver/ }), 'Rate card 2031.');
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    const put = server.requests.find((request) => request.method === 'PUT');
    expect(put?.body).toMatchObject({ price: 500, reason: 'Rate card 2031.', version: 2, effectiveFrom: '2031-01-01' });
  });

  it("holds the terms that wait in the plan's form, with a notice against those in use", async () => {
    serve({
      ...PLAN,
      pendingTerms: {
        approvalId: '0192a000-0000-7000-8000-00000000e903',
        asked: {
          type: 'Insurance',
          facilityId: null,
          defaultDiscountPercent: 12,
          isDefault: false,
          isActive: true,
          bedMultipliers: [],
        },
        requestedBy: 'Billing Desk',
        requestedAt: '2026-11-01T05:00:00Z',
      },
    });
    const user = userEvent.setup();
    openScreen(`/billing/tariffs?plan=${planId}`);

    await user.click(await screen.findByRole('button', { name: 'Edit plan' }, SLOW));
    const drawer = await screen.findByRole('dialog', { name: /Star Health TPA/ });
    expect(await within(drawer).findByText('Waiting for approval')).toBeInTheDocument();
    expect(
      within(drawer).getByText(/Terms: concession 12% \(now 10%\), asked for by Billing Desk/),
    ).toBeInTheDocument();
    expect(within(drawer).getByRole('spinbutton', { name: /Default concession/ })).toHaveValue(12);
    await expectNoAxeViolations();
  });
});
