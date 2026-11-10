import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ApprovalRequestDto } from '@/modules/approvals/api';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { ServiceItemDto } from './api';

// A service's price changes only when two people agree (maker-checker), on the services and approval screens against a
// fake server: a price waiting for approval shows as asked, with a notice, so saving again asks for nothing twice; a save
// that changes the price says it was asked for, not that it was saved; and the approver opens the service from the
// request, if they may open it at all.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const serviceId = '0192a000-0000-7000-8000-00000000d001';
const approvalId = '0192a000-0000-7000-8000-00000000d901';

const SERVICE: ServiceItemDto = {
  id: serviceId,
  code: 'PRC-DRESS',
  name: 'Wound dressing',
  shortName: null,
  category: 'Procedure',
  departmentId: null,
  basePrice: 300,
  taxRate: 0,
  sacCode: '999312',
  isDiscountAllowed: true,
  isPriceEditable: false,
  description: null,
  isActive: true,
  version: 4,
  localNames: {},
  pendingPrice: {
    approvalId,
    asked: { basePrice: 350, taxRate: 5, isPriceEditable: false, isDiscountAllowed: true, isActive: true },
    requestedBy: 'Hospital Admin',
    requestedAt: '2026-11-01T05:00:00Z',
  },
};

/** The request as the approver sees it (ServicePrices.AskAsync). */
const REQUEST: ApprovalRequestDto = {
  id: approvalId,
  kind: 'masters.service_price',
  title: 'Price of a service',
  summary: 'Price of PRC-DRESS Wound dressing: ₹300.00 → ₹350.00; GST exempt → 5%',
  details: [
    { label: 'Service', value: 'PRC-DRESS Wound dressing' },
    { label: 'Price', value: '₹300.00 → ₹350.00' },
    { label: 'GST', value: 'exempt → 5%' },
  ],
  reason: 'New dressing kits.',
  status: 'Pending',
  isExpired: false,
  subjectType: 'ServiceItem',
  subjectId: serviceId,
  patientId: null,
  requestedBy: '0192a000-0000-7000-8000-000000000ca5',
  requestedByName: 'Hospital Admin',
  requestedAt: '2026-11-01T05:00:00Z',
  expiresAt: '2099-11-08T05:00:00Z',
  decidedBy: null,
  decidedByName: null,
  decidedAt: null,
  decisionNote: null,
  outcome: null,
  canDecide: true,
  canWithdraw: false,
  version: 1,
};

/** Approves prices; reads services but does not edit them. */
const ACCOUNTANT = { ...STAFF_USER, roles: ['Accountant'], permissions: ['masters.prices.approve', 'masters.read'] };

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  server = fakeServer({
    'GET /api/v1/masters/services': paged([{ ...SERVICE, pendingPrice: null }]),
    [`GET /api/v1/masters/services/${serviceId}`]: SERVICE,
    'GET /api/v1/masters/lookups/local-languages': [],
    'GET /api/v1/masters/lookups/departments': [],
    [`GET /api/v1/approvals/${approvalId}`]: REQUEST,
    [`PUT /api/v1/masters/services/${serviceId}`]: {
      id: '0192a000-0000-7000-8000-00000000d902',
      kind: 'masters.service_price',
      summary: 'Price of PRC-DRESS Wound dressing: ₹300.00 → ₹400.00',
    },
  });
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('a service whose price waits for approval', SCREEN_TESTS, () => {
  it('shows the price asked as given, and says a save that changes it was asked for', async () => {
    const user = userEvent.setup();
    openScreen(`/setup/services?edit=${serviceId}`);
    const drawer = await screen.findByRole('dialog', { name: /Wound dressing/ }, SLOW);

    // ₹350 with 5% GST waits: the notice says so against what is charged now, and the form shows what waits, so saving it
    // again asks for nothing twice.
    expect(await within(drawer).findByText('Waiting for approval')).toBeInTheDocument();
    expect(
      within(drawer).getByText(/Price ₹350\.00 \(now ₹300\.00\); GST 5% \(now exempt\), asked for by Hospital Admin/),
    ).toBeInTheDocument();
    const price = within(drawer).getByRole('spinbutton', { name: /Base price/ });
    expect(price).toHaveValue(350);
    expect(within(drawer).getByRole('combobox', { name: /GST/ })).toHaveValue('5');
    await expectNoAxeViolations();

    await user.clear(price);
    await user.type(price, '400');
    await user.type(within(drawer).getByRole('textbox', { name: /Reason for a price change/ }), 'New dressing kits.');
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    const put = server.requests.find((request) => request.method === 'PUT');
    expect(put?.body).toMatchObject({ basePrice: 400, taxRate: 5, reason: 'New dressing kits.', version: 4 });
  });

  it('takes an approver who may edit services from the request to the service, where the price that waits is shown', async () => {
    const user = userEvent.setup();
    openScreen(`/approvals/${approvalId}`);

    expect(await screen.findByText('New dressing kits.', {}, SLOW)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Open the service' }));
    const drawer = await screen.findByRole('dialog', { name: /Wound dressing/ }, SLOW);
    expect(await within(drawer).findByText('Waiting for approval')).toBeInTheDocument();
  });

  it('lets an accountant decide from what the request says changes, without a link to a form they may not open', async () => {
    openScreen(`/approvals/${approvalId}`, ACCOUNTANT);

    expect(await screen.findByText('exempt → 5%', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('₹300.00 → ₹350.00')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Approve' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Open the service' })).not.toBeInTheDocument();
  });
});
