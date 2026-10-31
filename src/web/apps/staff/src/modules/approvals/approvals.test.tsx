import type * as Core from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ApprovalRequestDto } from '@/modules/approvals/api';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { FakeRequest } from '@/test/fakeApi';

// Maker-checker in the staff app: the accountant approves a credit note the billing desk asked for, sending the version
// of the request they saw; the person who asked may only withdraw it; and a refusal (the bill changed after it was asked
// for) stays in the dialog. The server is a fake; the toasts are outside the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const REQUEST_ID = '0192a000-0000-7000-8000-00000000a901';

const CREDIT_NOTE: ApprovalRequestDto = {
  id: REQUEST_ID,
  kind: 'billing.credit_note',
  title: 'Credit note',
  summary: 'Credit note of ₹1,200.00 on bill NHOPB2600012',
  details: [
    { label: 'Bill', value: 'NHOPB2600012' },
    { label: 'Balance due', value: '₹0.00' },
  ],
  reason: 'Physiotherapy charged at the private room rate',
  status: 'Pending',
  isExpired: false,
  subjectType: 'Bill',
  subjectId: '0192a000-0000-7000-8000-00000000b111',
  patientId: '0192a000-0000-7000-8000-000000000001',
  requestedBy: '0192a000-0000-7000-8000-000000000ca5',
  requestedByName: 'Priya Kulkarni',
  requestedAt: new Date(Date.now() - 3 * 3_600_000).toISOString(),
  expiresAt: new Date(Date.now() + 6 * 86_400_000).toISOString(),
  decidedBy: null,
  decidedByName: null,
  decidedAt: null,
  decisionNote: null,
  outcome: null,
  canDecide: true,
  canWithdraw: false,
  version: 7,
};

/** Approves refunds, credit notes and write-offs that someone else asked for. */
const ACCOUNTANT = { ...STAFF_USER, roles: ['Accountant'], permissions: ['billing.refunds.approve'] };

/** Asks for credit notes; may not approve them. */
const CASHIER = { ...STAFF_USER, roles: ['Cashier'], permissions: ['billing.refunds.request'] };

let server: ReturnType<typeof fakeServer>;

/** Serves `request` until a decision is posted, and `afterwards` (the request as the decision left it) from then on. */
function serve(request: ApprovalRequestDto, answer: (r: FakeRequest) => unknown, afterwards = request) {
  let decided = false;
  const decide = (r: FakeRequest) => {
    decided = true;
    return answer(r);
  };
  server = fakeServer({
    'GET /api/v1/approvals': () => paged([decided ? afterwards : request]),
    [`GET /api/v1/approvals/${REQUEST_ID}`]: () => (decided ? afterwards : request),
    [`POST /api/v1/approvals/${REQUEST_ID}/approve`]: decide,
    [`POST /api/v1/approvals/${REQUEST_ID}/withdraw`]: decide,
  });
}

const posts = () => server.requests.filter((r) => r.method === 'POST');

/** A refusal as the server sends it (422, application/problem+json): a general title, and why in the detail. */
const refusal = (code: string, detail: string) =>
  new Response(JSON.stringify({ status: 422, title: 'Cannot complete this action', detail, code }), {
    status: 422,
    headers: { 'content-type': 'application/problem+json' },
  });

const STALE =
  'Bill NHOPB2600012 changed after this was asked for (a payment, a refund or another credit). Ask again, against the bill as it is now.';

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('the approvals inbox', SCREEN_TESTS, () => {
  it('approves a credit note another person asked for, on the version the approver saw', async () => {
    serve(CREDIT_NOTE, () => ({ ...CREDIT_NOTE, status: 'Approved', outcome: 'NHPCN26000004', canDecide: false }));
    openScreen('/approvals', ACCOUNTANT);
    const user = userEvent.setup();

    const table = await screen.findByRole('table', { name: 'Requests waiting for my decision' }, SLOW);
    const row = await within(table).findByRole('row', { name: /Credit note of ₹1,200.00 on bill NHOPB2600012/ });
    expect(within(row).getByText('Priya Kulkarni')).toBeInTheDocument();
    await user.click(within(row).getByRole('button', { name: 'Approve' }));

    const dialog = await screen.findByRole('alertdialog', { name: 'Approve: credit note' });
    expect(within(dialog).getByText(/as it was when Priya Kulkarni asked for it/)).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox', { name: /^Note/ }), 'Rate corrected in the tariff');
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }));

    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Credit note approved', 'NHPCN26000004'));
    expect(posts().map((r) => [r.path, r.body])).toEqual([
      [`/api/v1/approvals/${REQUEST_ID}/approve`, { note: 'Rate corrected in the tariff', version: 7 }],
    ]);
  });

  it('offers the person who asked only to withdraw it', async () => {
    const mine = { ...CREDIT_NOTE, canDecide: false, canWithdraw: true };
    serve(mine, () => ({ ...mine, status: 'Withdrawn', canWithdraw: false }));
    openScreen('/approvals?view=Mine', CASHIER);
    const user = userEvent.setup();

    const table = await screen.findByRole('table', { name: 'Requests I asked for' }, SLOW);
    const row = await within(table).findByRole('row', { name: /Credit note of ₹1,200.00/ });
    expect(within(row).queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
    await user.click(within(row).getByRole('button', { name: 'Withdraw' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Withdraw: credit note' });
    await user.click(within(dialog).getByRole('button', { name: 'Withdraw' }));

    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Request withdrawn', CREDIT_NOTE.summary));
    expect(posts().map((r) => [r.path, r.body])).toEqual([
      [`/api/v1/approvals/${REQUEST_ID}/withdraw`, { version: 7 }],
    ]);
  });

  it('says why an approval was not made, and shows the request as it was left: the bill changed after it was asked for', async () => {
    // The server refuses the approval and keeps the request as failed, with why (ApprovalService.ApproveAsync).
    const failed: ApprovalRequestDto = {
      ...CREDIT_NOTE,
      status: 'Failed',
      outcome: STALE,
      decidedBy: ACCOUNTANT.id,
      decidedByName: 'Ravi Deshmukh',
      decidedAt: new Date().toISOString(),
      canDecide: false,
      version: 8,
    };
    serve(CREDIT_NOTE, () => refusal('approval.stale', STALE), failed);
    openScreen(`/approvals/${REQUEST_ID}`, ACCOUNTANT);
    const user = userEvent.setup();

    expect(await screen.findByText('Physiotherapy charged at the private room rate', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('NHOPB2600012')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Approve' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Approve: credit note' });
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }));

    expect(await within(dialog).findByText(STALE)).toBeInTheDocument();
    expect(within(dialog).getByText('Cannot complete this action')).toBeInTheDocument();
    // Not sent again from here, with the version the request had: the dialog only closes.
    expect(within(dialog).queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Close' }));

    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    expect(await screen.findByText('Approved, but not made')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
    expect(posts()).toHaveLength(1);
    expect(notify.success).not.toHaveBeenCalled();
  });
});
