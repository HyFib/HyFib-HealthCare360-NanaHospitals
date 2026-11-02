import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { NonPayableItemDto, PayerDetail, PayerListItem } from '@/modules/insurance/api';
import type { IpAccountDetail } from './ipApi';
import type { TariffPlanListItem } from './mastersApi';

// The plan a patient is priced on changes only when two people agree (maker-checker), on the screens against a fake
// server: the payer's form holds the terms that wait, with a notice against those in use; a stay on its payer's plan
// starts at "Default for the payer", so a new payer brings its own plan, and another plan asks for approval, with a
// reason, and says so.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const payerId = '0192a000-0000-7000-8000-00000000f001';
const accountId = '0192a000-0000-7000-8000-00000000f101';
const generalPlan = '0192a000-0000-7000-8000-00000000f201';
const staffPlan = '0192a000-0000-7000-8000-00000000f202';

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
  {
    id: staffPlan,
    code: 'STAFF',
    name: 'Staff concession',
    type: 'Staff',
    payerId: null,
    payerName: null,
    facilityId: null,
    defaultDiscountPercent: 0,
    isDefault: false,
    isActive: true,
    priceCount: 4,
  },
];

const PAYER: PayerDetail = {
  id: payerId,
  code: 'KAVERI',
  name: 'Kaveri Textiles',
  category: 'Corporate',
  contactPerson: 'Meena Iyer',
  phone: '9876543210',
  email: null,
  address: null,
  gstin: null,
  tariffPlanId: null,
  creditDays: 30,
  defaultCoPayPercent: 0,
  portalUrl: null,
  isActive: true,
  notes: null,
  version: 4,
};

const LISTED: PayerListItem = {
  id: payerId,
  code: 'KAVERI',
  name: 'Kaveri Textiles',
  category: 'Corporate',
  contactPerson: 'Meena Iyer',
  phone: '9876543210',
  tariffPlanId: null,
  tariffPlanName: null,
  creditDays: 30,
  isActive: true,
};

const ACCOUNT: IpAccountDetail = {
  id: accountId,
  admissionId: '0192a000-0000-7000-8000-00000000f301',
  admissionNumber: 'IP-2026-000412',
  patientId: '0192a000-0000-7000-8000-00000000f401',
  uhid: 'NH-000412',
  patientName: 'Suresh Kumar',
  visitId: '0192a000-0000-7000-8000-00000000f501',
  payerType: 'Self',
  payerId: null,
  payerName: null,
  tariffPlanId: generalPlan,
  tariffPlanName: 'General',
  policyId: null,
  attendingPractitionerId: null,
  attendingPractitionerName: null,
  bedServiceItemId: null,
  bedServiceCode: null,
  admittedAt: '2026-11-01T05:00:00Z',
  dischargeInitiatedAt: null,
  dischargedAt: null,
  status: 'Open',
  estimatedAmount: 20000,
  depositReceived: 0,
  depositAdjusted: 0,
  depositRefunded: 0,
  depositBalance: 0,
  packageId: null,
  packageName: null,
  dischargeWithDuesApproved: false,
  dischargeApprovalReason: null,
  notes: null,
  version: 7,
  payerPlanId: generalPlan,
};

/** The patient, as the billing banner shows them. */
const PATIENT = {
  id: '0192a000-0000-7000-8000-00000000f401',
  uhid: 'NH-000412',
  fullName: 'Suresh Kumar',
  gender: 'Male',
  age: '58 Y',
  phone: '+91******3210',
  bloodGroup: null,
  category: 'General',
  isVip: false,
  isDeceased: false,
  mergedIntoPatientId: null,
};

const ASKED = {
  id: '0192a000-0000-7000-8000-00000000f901',
  kind: 'billing.ip_plan',
  summary: "Admission IP-2026-000412 billed on Staff concession instead of General, its payer's plan",
};

/** The non-payable list: an item in force, and one asked to be added and waiting for approval. */
const KIT_ID = '0192a000-0000-7000-8000-00000000f501';
const NON_PAYABLES: NonPayableItemDto[] = [
  {
    id: KIT_ID,
    payerId: null,
    payerName: null,
    serviceItemId: '0192a000-0000-7000-8000-00000000f502',
    serviceCode: 'CONS-KIT',
    serviceName: 'Admission kit',
    category: null,
    notes: null,
    waiting: null,
  },
  {
    id: '0192a000-0000-7000-8000-00000000f503',
    payerId,
    payerName: 'Kaveri Textiles',
    serviceItemId: null,
    serviceCode: null,
    serviceName: null,
    category: 'Consumable',
    notes: 'IRDAI list',
    waiting: {
      approvalId: '0192a000-0000-7000-8000-00000000f504',
      change: 'Add',
      requestedBy: 'Insurance Desk',
      requestedAt: '2026-11-02T05:00:00Z',
    },
  },
];

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('the plan a patient is priced on, waiting for approval', SCREEN_TESTS, () => {
  it("holds a payer's terms that wait in its form, with a notice against those in use", async () => {
    server = fakeServer({
      'GET /api/v1/insurance/payers': [LISTED],
      [`GET /api/v1/insurance/payers/${payerId}`]: {
        ...PAYER,
        pendingTerms: {
          approvalId: '0192a000-0000-7000-8000-00000000f902',
          asked: { category: 'Corporate', tariffPlanId: staffPlan, defaultCoPayPercent: 10, isActive: true },
          requestedBy: 'Insurance Desk',
          requestedAt: '2026-11-01T05:00:00Z',
        },
      },
      'GET /api/v1/billing/tariffs': PLANS,
      'GET /api/v1/insurance/non-payable-items': [],
    });
    const user = userEvent.setup();
    openScreen('/insurance/payers');

    const payers = await screen.findByRole('table', { name: 'Payers' }, SLOW);
    await user.click(await within(payers).findByText('Kaveri Textiles', {}, SLOW));
    const drawer = await screen.findByRole('dialog', { name: /Kaveri Textiles/ }, SLOW);
    expect(await within(drawer).findByText('Waiting for approval')).toBeInTheDocument();
    expect(
      within(drawer).getByText(
        /Terms: tariff Staff concession \(now none\); co-pay 10% \(now 0%\), asked for by Insurance Desk/,
      ),
    ).toBeInTheDocument();
    expect(within(drawer).getByRole('spinbutton', { name: /Default co-pay/ })).toHaveValue(10);
    await expectNoAxeViolations();
  });

  it("starts a stay on its payer's plan at the default, and asks for another plan with a reason", async () => {
    server = fakeServer({
      [`GET /api/v1/billing/ip-accounts/${accountId}`]: ACCOUNT,
      [`PUT /api/v1/billing/ip-accounts/${accountId}`]: ASKED,
      'GET /api/v1/billing/tariffs': PLANS,
      'GET /api/v1/insurance/preauths': paged([]),
      'GET /api/v1/print/letterhead': { name: 'Nana Hospitals, Pune' },
      'GET /api/v1/billing/shifts/current': null,
      [`GET /api/v1/patients/${ACCOUNT.patientId}`]: PATIENT,
      [`GET /api/v1/patients/${ACCOUNT.patientId}/payer`]: { current: null },
      [`GET /api/v1/emr/patients/${ACCOUNT.patientId}/flags`]: {
        allergyStatus: 'NotRecorded',
        allergies: [],
        alerts: [],
      },
      [`GET /api/v1/billing/patients/${ACCOUNT.patientId}/dues`]: {
        patientId: ACCOUNT.patientId,
        totalDue: 0,
        advanceBalance: 0,
        bills: [],
        advances: [],
      },
    });
    const user = userEvent.setup();
    openScreen(`/billing/ip/${accountId}?tab=payer`);

    await user.click(await screen.findByRole('button', { name: 'Change payer or estimate' }, SLOW));
    const dialog = await screen.findByRole('dialog', { name: 'Payer and estimate' });
    const tariff = within(dialog).getByRole('combobox', { name: /Tariff/ });
    expect(tariff).toHaveValue('');
    expect(within(dialog).queryByRole('textbox', { name: /Reason for another plan/ })).not.toBeInTheDocument();

    await user.selectOptions(tariff, staffPlan);
    expect(
      within(dialog).getByText("A plan other than the payer's waits for someone who may approve discounts."),
    ).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox', { name: /Reason for another plan/ }), "Staff member's mother.");
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect((await screen.findAllByText('Asked for approval', {}, SLOW)).length).toBeGreaterThan(0);
    const puts = server.requests.filter((request) => request.method === 'PUT');
    expect(puts).toHaveLength(1);
    expect(puts[0]?.body).toMatchObject({
      tariffPlanId: staffPlan,
      payerType: 'Self',
      repriceUnbilledCharges: true,
      reason: "Staff member's mother.",
      version: 7,
    });
  });
  it('lists the non-payable changes waiting, and takes an item off only by asking a second person, with a reason', async () => {
    server = fakeServer({
      'GET /api/v1/insurance/payers': [LISTED],
      'GET /api/v1/billing/tariffs': PLANS,
      'GET /api/v1/insurance/non-payable-items': NON_PAYABLES,
      [`POST /api/v1/insurance/non-payable-items/${KIT_ID}/remove`]: {
        id: '0192a000-0000-7000-8000-00000000f505',
        summary: 'No longer non-payable for every payer: CONS-KIT Admission kit',
      },
    });
    const user = userEvent.setup();
    openScreen('/insurance/payers');

    const table = await screen.findByRole('table', { name: 'Non-payable items' }, SLOW);
    expect(await within(table).findByText('To be added: waiting for approval', {}, SLOW)).toBeInTheDocument();
    expect(within(table).getByText('In force')).toBeInTheDocument();
    // The item waiting to be added is not in force, and is not offered for removal.
    const removes = within(table).getAllByRole('button', { name: 'Remove item' });
    expect(removes).toHaveLength(1);
    await expectNoAxeViolations();

    await user.click(removes[0]!);
    const dialog = await screen.findByRole('alertdialog', { name: 'Take this item off the non-payable list?' });
    await user.type(
      within(dialog).getByRole('textbox', { name: /Reason, for the approver/ }),
      'Covered under the new agreement',
    );
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    expect((await screen.findAllByText('Asked for approval', {}, SLOW)).length).toBeGreaterThan(0);
    const posts = server.requests.filter((request) => request.method === 'POST');
    expect(posts).toHaveLength(1);
    expect(posts[0]?.body).toEqual({ reason: 'Covered under the new agreement' });
  });

  it('asks a second person to add an item to the non-payable list, with a reason', async () => {
    server = fakeServer({
      'GET /api/v1/insurance/payers': [LISTED],
      'GET /api/v1/billing/tariffs': PLANS,
      'GET /api/v1/insurance/non-payable-items': [],
      'POST /api/v1/insurance/non-payable-items': {
        id: '0192a000-0000-7000-8000-00000000f506',
        summary: 'Non-payable for every payer: every consumable service',
      },
    });
    const user = userEvent.setup();
    openScreen('/insurance/payers');

    await user.click(await screen.findByRole('button', { name: 'Add item' }, SLOW));
    const dialog = await screen.findByRole('dialog', { name: 'Add a non-payable item' });
    await user.click(within(dialog).getByRole('radio', { name: 'A whole category' }));
    await user.selectOptions(within(dialog).getByRole('combobox', { name: /Category/ }), 'Medicines / consumables');
    await user.type(
      within(dialog).getByRole('textbox', { name: /Reason, for the approver/ }),
      'IRDAI list, annexure II',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Ask to add' }));

    expect((await screen.findAllByText('Asked for approval', {}, SLOW)).length).toBeGreaterThan(0);
    const posts = server.requests.filter((request) => request.method === 'POST');
    expect(posts).toHaveLength(1);
    expect(posts[0]?.body).toEqual({
      payerId: null,
      serviceItemId: null,
      category: 'Consumable',
      notes: null,
      reason: 'IRDAI list, annexure II',
    });
  });
});
