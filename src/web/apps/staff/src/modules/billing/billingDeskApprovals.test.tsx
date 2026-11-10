import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PractitionerDto } from '@/modules/masters/api';
import {
  fakeServer,
  FACILITY_ID,
  openScreen,
  paged,
  SCREEN_TESTS,
  SLOW,
  STAFF_USER,
  stubBrowserApis,
} from '@/test/appScreen';
import type { DiscountLimitDto, PendingDiscountLimitDto, ShiftDto } from './api';

// The billing desk's money decisions that wait for a second person (maker-checker, slice 6), on the screens against a
// fake server: a discount limit raised asks for approval and the limits list what waits; a cash shift closed short says
// a supervisor accepts the shortage, and shows "shortage awaiting supervisor" with its request; and a doctor's fee
// asked for shows waiting on the doctor's record, the fee in force kept.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const CASHIER_ID = '0192a000-0000-7000-8000-00000000e0a1';
const LIMIT: DiscountLimitDto = {
  id: '0192a000-0000-7000-8000-00000000e001',
  userId: CASHIER_ID,
  userName: 'Asha Kulkarni',
  maxPercent: 5,
  maxAmount: 500,
  notes: 'OP counter',
  version: 3,
};
const PENDING_LIMIT: PendingDiscountLimitDto = {
  approvalId: '0192a000-0000-7000-8000-00000000e901',
  userId: '0192a000-0000-7000-8000-00000000e0a2',
  userName: 'Ravi Desai',
  before: 'the default (0%)',
  asked: '10%',
  requestedBy: 'Hospital Admin',
  requestedAt: '2026-11-08T04:30:00Z',
  askedByYou: false,
};

const SHIFT_ID = '0192a000-0000-7000-8000-00000000e101';
const SHIFT: ShiftDto = {
  id: SHIFT_ID,
  userId: CASHIER_ID,
  userName: 'Asha Kulkarni',
  counter: 'OP counter 1',
  status: 'Closed',
  openedAt: '2026-11-08T03:00:00Z',
  closedAt: '2026-11-08T11:00:00Z',
  openingFloat: 1000,
  expectedCash: 5200,
  declaredCash: 5100,
  variance: -100,
  totals: [{ mode: 'Cash', collected: 4200, refunded: 0, net: 4200 }],
  receiptCount: 12,
  notes: '₹100 note missing',
  version: 5,
  shortageAwaiting: true,
  shortageAcceptedBy: null,
  shortageAcceptedAt: null,
  shortageApprovalId: '0192a000-0000-7000-8000-00000000e902',
};

const OPEN_SHIFT: ShiftDto = {
  ...SHIFT,
  id: '0192a000-0000-7000-8000-00000000e102',
  userId: STAFF_USER.id,
  userName: STAFF_USER.displayName,
  counter: 'OP counter 2',
  status: 'Open',
  closedAt: null,
  expectedCash: 2000,
  declaredCash: null,
  variance: null,
  notes: null,
  shortageAwaiting: false,
  shortageApprovalId: null,
  shortageTolerance: 0,
};

const DOCTOR_ID = '0192a000-0000-7000-8000-00000000e201';
const DOCTOR: PractitionerDto = {
  id: DOCTOR_ID,
  code: 'DRFEES',
  title: 'Dr.',
  firstName: 'Farida',
  lastName: 'Khan',
  displayName: 'Dr. Farida Khan',
  gender: 'Female',
  qualification: 'MS (General Surgery)',
  registrationCouncil: 'Maharashtra Medical Council',
  registrationNumber: 'MMC 123456',
  hprId: null,
  specialtyId: null,
  specialtyName: null,
  departmentId: null,
  departmentName: null,
  kind: 'Consultant',
  phone: null,
  email: null,
  consultationFeeNew: 500,
  consultationFeeFollowUp: 300,
  followUpValidityDays: 7,
  languages: [],
  bio: null,
  photoFileId: null,
  signatureFileId: null,
  availableForOnlineBooking: false,
  availableForTelehealth: false,
  facilityIds: [],
  isActive: true,
  version: 9,
  linkedAccounts: [],
  pendingIdentity: null,
  pendingFee: {
    approvalId: '0192a000-0000-7000-8000-00000000e903',
    asked: { newVisit: 777, followUp: 400 },
    requestedBy: 'Hospital Admin',
    requestedAt: '2026-11-08T05:00:00Z',
  },
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

describe("the billing desk's decisions that wait for a second person", SCREEN_TESTS, () => {
  it('lists the limits that wait, and asks for approval to raise one, saying who decides', async () => {
    server = fakeServer({
      'GET /api/v1/billing/discount-limits': [LIMIT],
      'GET /api/v1/billing/discount-limits/pending': [PENDING_LIMIT],
      'PUT /api/v1/billing/discount-limits': {
        id: '0192a000-0000-7000-8000-00000000e904',
        kind: 'billing.discount_limit',
        summary: 'Discount limit of Asha Kulkarni: 5%, at most ₹500.00 → 10%',
      },
    });
    const user = userEvent.setup();
    openScreen('/billing/discount-approvals?view=limits');

    // What waits is listed, in words, with the request.
    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText(/Ravi Desai: the default \(0%\) → 10%, asked for by Hospital Admin/)).toBeInTheDocument();
    await expectNoAxeViolations();

    // Lowering is saved at once; taking the cap away raises the limit, so the dialog asks for approval.
    await user.click(await screen.findByText('Asha Kulkarni'));
    const dialog = await screen.findByRole('dialog', { name: 'Discount limit of Asha Kulkarni' });
    const percent = within(dialog).getByRole('textbox', { name: /Maximum discount without approval/ });
    await user.clear(percent);
    await user.type(percent, '4');
    expect(within(dialog).getByRole('button', { name: 'Save limit' })).toBeInTheDocument();
    await user.clear(percent);
    await user.type(percent, '10');
    expect(within(dialog).getByText(/Raising it .* waits for another approver/)).toBeInTheDocument();
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    expect(await screen.findAllByText('Asked for approval', {}, SLOW)).not.toHaveLength(0);
    const put = server.requests.find((request) => request.method === 'PUT');
    expect(put?.body).toMatchObject({ userId: CASHIER_ID, maxPercent: 10, maxAmount: 500, version: 3 });
  });

  it('shows a shift closed short as awaiting a supervisor, with its request, and asks when a drawer is closed short', async () => {
    server = fakeServer({
      'GET /api/v1/billing/shifts': paged([SHIFT, OPEN_SHIFT]),
      [`GET /api/v1/billing/shifts/${SHIFT_ID}`]: SHIFT,
      [`GET /api/v1/billing/shifts/${OPEN_SHIFT.id}`]: OPEN_SHIFT,
      'GET /api/v1/billing/shifts/current': OPEN_SHIFT,
      [`POST /api/v1/billing/shifts/${OPEN_SHIFT.id}/close`]: {
        ...OPEN_SHIFT,
        status: 'Closed',
        declaredCash: 1900,
        variance: -100,
        shortageAwaiting: true,
        shortageApprovalId: '0192a000-0000-7000-8000-00000000e905',
      },
    });
    const user = userEvent.setup();
    openScreen('/billing/shifts');

    const table = await screen.findByRole('table', {}, SLOW);
    expect(await within(table).findByText('Closed — shortage awaiting supervisor', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();

    await user.click(within(table).getByText('OP counter 1'));
    const detail = await screen.findByRole('dialog', { name: /Asha Kulkarni · OP counter 1/ });
    expect(await within(detail).findByText('Awaiting a supervisor, who is not the cashier')).toBeInTheDocument();
    expect(within(detail).getByRole('button', { name: 'Open the request' })).toBeInTheDocument();
    await user.click(within(detail).getByRole('button', { name: 'Done' }));

    // Closing my own drawer ₹100 short: it closes, and a supervisor other than me accepts the shortage.
    await user.click(within(table).getByText('OP counter 2'));
    const mine = await screen.findByRole('dialog', { name: /OP counter 2/ });
    await user.click(await within(mine).findByRole('button', { name: 'Close this shift' }));
    const closing = await screen.findByRole('alertdialog', { name: 'Close shift at OP counter 2' });
    await user.type(within(closing).getByRole('textbox', { name: /Cash counted in the drawer/ }), '1900');
    expect(within(closing).getByText(/A supervisor other than the cashier accepts the shortage/)).toBeInTheDocument();
    await user.type(within(closing).getByRole('textbox', { name: /Notes/ }), 'Change given wrongly');
    await expectNoAxeViolations();
    await user.click(within(closing).getByRole('button', { name: 'Close and ask for approval' }));

    expect(await screen.findByText('Shift closed, shortage asked for approval', {}, SLOW)).toBeInTheDocument();
    const post = server.requests.find((request) => request.method === 'POST');
    expect(post?.body).toMatchObject({ declaredCash: 1900, notes: 'Change given wrongly', version: 5 });
  });

  it("shows a doctor's fee asked for on their record, with the fee in force kept until it is approved", async () => {
    server = fakeServer({
      'GET /api/v1/masters/practitioners': paged([{ ...DOCTOR, pendingFee: null }]),
      [`GET /api/v1/masters/practitioners/${DOCTOR_ID}`]: DOCTOR,
      'GET /api/v1/masters/specialties': [],
      'GET /api/v1/masters/facilities': [{ id: FACILITY_ID, code: 'NHP', name: 'Nana Hospitals, Pune' }],
      'GET /api/v1/masters/lookups/departments': [],
    });
    openScreen(`/setup/practitioners?edit=${DOCTOR_ID}`);

    const drawer = await screen.findByRole('dialog', { name: /Dr\. Farida Khan/ }, SLOW);
    expect(await within(drawer).findByText('Waiting for approval')).toBeInTheDocument();
    expect(
      within(drawer).getByText(
        /A consultation fee of ₹777\.00 for a new visit and ₹400\.00 for a follow-up, asked for by Hospital Admin/,
      ),
    ).toBeInTheDocument();
    expect(
      within(drawer).getByText(
        /Someone else who approves prices decides it; until then the fees shown here are charged/,
      ),
    ).toBeInTheDocument();
    expect(within(drawer).getByRole('spinbutton', { name: /Fee for a new visit/ })).toHaveValue(500);
    await expectNoAxeViolations();
  });
});
