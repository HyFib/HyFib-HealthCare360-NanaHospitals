import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as BillingApi from './api';
import { billingApi, type ReceiptDto, type RefundDto } from './api';
import { ReceiptReversalDialog, RefundNotPaidDialog } from './ReversalDialogs';
import { mayHaveReversibleMoney, mayReverseRefund } from './reversals';

// The server and the toasts are outside the test; what the dialogs ask for is what is tested.
const readReceipt = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof BillingApi>();
  return {
    ...actual,
    billingQueries: {
      ...actual.billingQueries,
      receipt: (id: string) => ({ queryKey: ['billing', 'receipt', id], queryFn: readReceipt }),
    },
    billingApi: { ...actual.billingApi, requestReceiptReversal: vi.fn(), requestRefundNotPaid: vi.fn() },
  };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const line = (id: string, mode: ReceiptDto['payments'][number]['mode'], amount: number, reversed = 0) => ({
  id,
  mode,
  amount,
  reference: mode === 'Upi' ? '429811736520' : null,
  cardLast4: null,
  cardNetwork: null,
  chequeNumber: mode === 'Cheque' ? '004512' : null,
  chequeBank: mode === 'Cheque' ? 'State Bank of India' : null,
  chequeDate: mode === 'Cheque' ? '2026-11-01' : null,
  reversed,
});

const receipt = (payments: ReceiptDto['payments']): ReceiptDto => ({
  id: 'rc-1',
  receiptNumber: 'NHRC260001342',
  purpose: 'BillPayment',
  patientId: 'pt-1',
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  billId: 'bill-1',
  billNumber: 'NHOB260000198',
  ipAccountId: null,
  amount: payments.reduce((sum, p) => sum + p.amount, 0),
  source: 'Counter',
  counter: 'OP counter 1',
  receivedByName: 'Priya Kulkarni',
  receivedAt: '2026-11-01T09:30:00Z',
  notes: null,
  payments,
});

const ASKED = { summary: '₹400.00 of receipt NHRC260001342 not received' };

function renderIn(ui: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={client}>{ui}</QueryClientProvider>
    </FluentProvider>,
  );
}

describe('ReceiptReversalDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(billingApi.requestReceiptReversal).mockResolvedValue(ASKED as never);
  });

  it('offers only the money a reversal can take back and asks for it with the reason and how it was found', async () => {
    const user = userEvent.setup();
    readReceipt.mockResolvedValue(receipt([line('cash-1', 'Cash', 200), line('chq-1', 'Cheque', 400)]));
    const onClose = vi.fn();
    renderIn(<ReceiptReversalDialog receiptId="rc-1" open onClose={onClose} />);

    const cheque = await screen.findByRole('checkbox', { name: /Cheque \(cheque 004512, State Bank of India\)/ });
    expect(cheque).toBeChecked();
    expect(screen.queryByRole('checkbox', { name: /Cash/ })).toBeNull();
    const ask = screen.getByRole('button', { name: 'Ask for the reversal' });
    expect(ask).toBeDisabled();

    await user.click(screen.getByRole('radio', { name: 'The bank returned the cheque unpaid' }));
    await user.type(screen.getByRole('textbox'), 'Short');
    expect(ask).toBeDisabled();
    await user.type(screen.getByRole('textbox'), ': return memo from SBI, insufficient funds.');
    await user.click(ask);

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(billingApi.requestReceiptReversal).toHaveBeenCalledWith(
      'rc-1',
      ['chq-1'],
      'ChequeReturned',
      'Short: return memo from SBI, insufficient funds.',
    );
    expect(notify.success).toHaveBeenCalledWith(
      'Sent for approval',
      `${ASKED.summary}. An approver, not you, makes it.`,
    );
  });

  it('says why nothing on a cash receipt, or on one already taken back, can be reversed', async () => {
    readReceipt.mockResolvedValue(receipt([line('cash-1', 'Cash', 200), line('upi-1', 'Upi', 300, 300)]));
    renderIn(<ReceiptReversalDialog receiptId="rc-1" open onClose={() => {}} />);

    expect(await screen.findByText(/Nothing on this receipt can be reversed/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ask for the reversal' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Close' })).toBeEnabled();
  });
});

describe('RefundNotPaidDialog', () => {
  const refund: RefundDto = {
    id: 'rf-1',
    refundNumber: 'NHRF260000071',
    purpose: 'Bill',
    patientId: 'pt-1',
    uhid: 'NH26000123',
    patientName: 'Asha Devi',
    billId: 'bill-1',
    ipAccountId: null,
    amount: 600,
    reason: 'Bill cancelled',
    status: 'Paid',
    requestedByName: 'Priya Kulkarni',
    requestedAt: '2026-11-01T09:00:00Z',
    decidedBy: 'u-2',
    decidedAt: '2026-11-01T09:10:00Z',
    decisionRemarks: null,
    mode: 'NetBanking',
    reference: 'NEFT0012345678',
    paidAt: '2026-11-01T09:20:00Z',
    advanceId: null,
  };

  it('asks for a paid refund to be recorded as not paid out', async () => {
    const user = userEvent.setup();
    vi.mocked(billingApi.requestRefundNotPaid).mockResolvedValue({
      summary: 'Refund NHRF260000071 of ₹600.00 not paid out',
    } as never);
    const onClose = vi.fn();
    renderIn(<RefundNotPaidDialog refund={refund} open onClose={onClose} />);

    await user.click(screen.getByRole('radio', { name: 'The transfer, card reversal or online refund failed' }));
    await user.type(screen.getByRole('textbox'), 'The bank returned the transfer: account closed.');
    await user.click(screen.getByRole('button', { name: 'Ask to record it' }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(billingApi.requestRefundNotPaid).toHaveBeenCalledWith(
      'rf-1',
      'NotSettled',
      'The bank returned the transfer: account closed.',
    );
  });

  it('is offered only for money recorded as leaving, in a mode not counted at the drawer, and only once', () => {
    expect(mayReverseRefund(refund)).toBe(true);
    expect(mayReverseRefund({ ...refund, mode: 'Cash' })).toBe(false);
    expect(mayReverseRefund({ ...refund, mode: 'Advance' })).toBe(false);
    expect(mayReverseRefund({ ...refund, status: 'Approved', mode: null, paidAt: null })).toBe(false);
    expect(
      mayReverseRefund({
        ...refund,
        notPaidOut: {
          id: 'rx-1',
          number: 'NHRX260000001',
          amount: 600,
          reason: 'NotSettled',
          note: 'The bank returned it.',
          requestedByName: 'Priya Kulkarni',
          approvedByName: 'Ravi Menon',
          reversedAt: '2026-11-02T10:00:00Z',
        },
      }),
    ).toBe(false);
  });
});

describe('mayHaveReversibleMoney', () => {
  const paid = (modes: string, reversedAmount?: number) => ({
    purpose: 'BillPayment' as const,
    modes,
    amount: 500,
    reversedAmount,
  });

  it('is true only for a receipt with money in a mode a reversal can take back, not all taken back yet', () => {
    expect(mayHaveReversibleMoney(paid('Upi'))).toBe(true);
    expect(mayHaveReversibleMoney(paid('Cash + Card'))).toBe(true);
    expect(mayHaveReversibleMoney(paid('Cash'))).toBe(false);
    expect(mayHaveReversibleMoney(paid('Advance'))).toBe(false);
    expect(mayHaveReversibleMoney(paid('Insurance'))).toBe(false);
    expect(mayHaveReversibleMoney(paid('Upi', 500))).toBe(false);
    expect(mayHaveReversibleMoney(paid('Upi', 200))).toBe(true);
  });

  it("is false for an insurer's settlement, whatever mode its money came by", () => {
    expect(mayHaveReversibleMoney({ ...paid('NetBanking'), purpose: 'PayerSettlement' })).toBe(false);
    expect(mayHaveReversibleMoney({ ...paid('NetBanking'), purpose: 'Deposit' })).toBe(true);
  });
});
