import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, json, openScreen, paged, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { PendingApproval, PharmacySaleDetail, PharmacySaleListItem } from './api';

// A pharmacy invoice's cancellation and return, on the sales screen with the shell, against a fake server. Both wait for
// someone who approves refunds: the dialogs say so, say how the money goes back (here ₹600 by card and ₹400 in cash: the
// card is paid back, never the till), and send the request once with the reason. A request waiting shows on the invoice,
// and the pharmacist who sold it is not offered either.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const SALE_ID = '0192a000-0000-7000-8000-00000000c001';
const SALE = `/api/v1/pharmacy/sales/${SALE_ID}`;
const SOLD_AT = new Date().toISOString();

const sale = (more: Partial<PharmacySaleDetail> = {}): PharmacySaleDetail => ({
  id: SALE_ID,
  invoiceNumber: 'NHPPH260000412',
  type: 'Counter',
  storeId: '0192a000-0000-7000-8000-00000000c010',
  storeName: 'OP Pharmacy',
  patientId: null,
  patientUhid: null,
  customerName: 'Walk-in customer',
  customerPhone: null,
  prescriptionNumber: null,
  prescriberName: null,
  prescriberRegistrationNumber: null,
  soldAt: SOLD_AT,
  soldBy: 'Suresh Patil',
  counsellingNotes: null,
  discountPercent: 0,
  grossAmount: 1000,
  discountAmount: 0,
  taxableAmount: 892.86,
  cgstAmount: 53.57,
  sgstAmount: 53.57,
  igstAmount: 0,
  buyerGstin: null,
  buyerLegalName: null,
  placeOfSupply: null,
  roundOff: 0,
  netAmount: 1000,
  paidAmount: 1000,
  dueAmount: 0,
  returnedAmount: 0,
  refundedAmount: 0,
  status: 'Paid',
  cancelReason: null,
  items: [
    {
      id: '0192a000-0000-7000-8000-00000000c101',
      itemId: '0192a000-0000-7000-8000-00000000c201',
      itemName: 'Amoxicillin 500 mg Capsule',
      genericName: 'Amoxicillin',
      hsnCode: '300410',
      schedule: 'H',
      batchNumber: 'AMX2508',
      expiryDate: '2028-03-31',
      quantity: 10,
      unitMrp: 100,
      gstRate: 12,
      grossAmount: 1000,
      discountAmount: 0,
      taxableAmount: 892.86,
      cgstAmount: 53.57,
      sgstAmount: 53.57,
      igstAmount: 0,
      netAmount: 1000,
      returnedQuantity: 0,
      isSubstitution: false,
      prescribedDrugName: null,
      substitutionReason: null,
    },
  ],
  payments: [
    {
      id: '0192a000-0000-7000-8000-00000000c301',
      mode: 'Card',
      amount: 600,
      reference: '627910123456',
      receivedAt: SOLD_AT,
      isRefund: false,
    },
    {
      id: '0192a000-0000-7000-8000-00000000c302',
      mode: 'Cash',
      amount: 400,
      reference: null,
      receivedAt: SOLD_AT,
      isRefund: false,
    },
  ],
  version: 3,
  pending: [],
  ownSale: false,
  ...more,
});

const listed: PharmacySaleListItem = {
  id: SALE_ID,
  invoiceNumber: 'NHPPH260000412',
  type: 'Counter',
  soldAt: SOLD_AT,
  storeId: '0192a000-0000-7000-8000-00000000c010',
  patientId: null,
  customerName: 'Walk-in customer',
  prescriptionNumber: null,
  netAmount: 1000,
  paidAmount: 1000,
  dueAmount: 0,
  returnedAmount: 0,
  status: 'Paid',
};

const asked = (kind: PendingApproval['kind'], summary: string) =>
  json({ id: '0192a000-0000-7000-8000-00000000c901', kind, summary, status: 'Pending' }, 202);

function answers(detail: PharmacySaleDetail, more: Record<string, unknown> = {}) {
  return {
    'GET /api/v1/pharmacy/sales': paged([listed]),
    [`GET ${SALE}`]: detail,
    [`GET ${SALE}/returns`]: [],
    'GET /api/v1/print/letterhead': { name: 'Nana Hospitals' },
    'GET /api/v1/inventory/stores': [],
    ...more,
  };
}

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("a pharmacy invoice's cancellation and return", SCREEN_TESTS, () => {
  it('asks for approval to cancel, saying the card is paid back and the cash comes from the till, and sends the reason once', async () => {
    server = fakeServer(
      answers(sale(), {
        [`POST ${SALE}/cancel`]: asked(
          'pharmacy.sale_cancel',
          'Cancel invoice NHPPH260000412 (Walk-in customer): ₹1,000.00 back',
        ),
      }),
    );
    const user = userEvent.setup();
    openScreen(`/pharmacy/sales?id=${SALE_ID}`);

    await user.click(await screen.findByRole('button', { name: 'Cancel invoice' }, SLOW));
    const dialog = await screen.findByRole('alertdialog', { name: 'Ask to cancel invoice NHPPH260000412?' }, SLOW);
    expect(
      within(dialog).getByText(
        /Someone who approves refunds decides; until then nothing changes\. When approved: ₹400\.00 in cash, ₹600\.00 back to the card \(RRN or approval code 627910123456\)/,
      ),
    ).toBeInTheDocument();
    const ask = within(dialog).getByRole('button', { name: 'Ask for approval' });
    expect(ask).toBeDisabled();
    await user.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'Billed to the wrong customer');
    await expectNoAxeViolations();

    await user.click(ask);
    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    const cancels = server.requests.filter((request) => request.method === 'POST' && request.path === `${SALE}/cancel`);
    expect(cancels).toHaveLength(1);
    expect(cancels[0]?.body).toEqual({ reason: 'Billed to the wrong customer' });
  });

  it('asks for approval of a return that pays money out, shows how it goes back, and sends it once with the reason', async () => {
    server = fakeServer(
      answers(sale(), {
        [`POST ${SALE}/returns`]: asked('pharmacy.sale_return', 'Return on invoice NHPPH260000412: ₹100.00 back'),
      }),
    );
    const user = userEvent.setup();
    openScreen(`/pharmacy/sales?id=${SALE_ID}`);

    await user.click(await screen.findByRole('button', { name: 'Return items' }, SLOW));
    const dialog = await screen.findByRole('dialog', { name: 'Return items from NHPPH260000412' }, SLOW);
    await user.type(
      within(dialog).getByRole('spinbutton', { name: 'Quantity of Amoxicillin 500 mg Capsule returned' }),
      '1',
    );

    // One capsule strip of ten: ₹100, back the way the sale was paid, in proportion; it waits for approval.
    expect(within(dialog).getByText('Waits for approval')).toBeInTheDocument();
    expect(
      within(dialog).getByText(
        /How it goes back: ₹40\.00 in cash, ₹60\.00 back to the card \(RRN or approval code 627910123456\)\./,
      ),
    ).toBeInTheDocument();
    expect(within(dialog).queryByRole('combobox', { name: /Pay back by/ })).not.toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'Doctor changed the antibiotic');
    await expectNoAxeViolations();

    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));
    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    const returns = server.requests.filter(
      (request) => request.method === 'POST' && request.path === `${SALE}/returns`,
    );
    expect(returns).toHaveLength(1);
    expect(returns[0]?.body).toEqual({
      lines: [{ saleItemId: '0192a000-0000-7000-8000-00000000c101', quantity: 1, restock: true }],
      reason: 'Doctor changed the antibiotic',
    });
  });

  it('shows a request waiting on the invoice, and offers the pharmacist who sold it neither a cancellation nor a return', async () => {
    const waiting: PendingApproval = {
      approvalId: '0192a000-0000-7000-8000-00000000c902',
      kind: 'pharmacy.sale_cancel',
      summary: 'Cancel invoice NHPPH260000412 (Walk-in customer): ₹1,000.00 back (₹400.00 cash, ₹600.00 card)',
      requestedBy: 'Anita Desai',
      requestedAt: SOLD_AT,
      askedByYou: false,
    };
    server = fakeServer(answers(sale({ pending: [waiting], ownSale: true })));
    openScreen(`/pharmacy/sales?id=${SALE_ID}`);

    expect(
      await screen.findByText(/Cancel invoice NHPPH260000412 \(Walk-in customer\): ₹1,000\.00 back/, {}, SLOW),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/asked for by Anita Desai .+ Nothing changes until someone who approves refunds approves it\./),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Cancel invoice' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Return items' })).toBeDisabled();
    await expectNoAxeViolations();
  });
});
