import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { formatInr } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as BillingApi from './api';
import { billingApi, type BillDetail } from './api';
import { debitGst } from './billTotals';
import { DebitNoteDialog } from './DebitNoteDialog';

vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof BillingApi>();
  return { ...actual, billingApi: { ...actual.billingApi, requestDebitNote: vi.fn() } };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

/** A corporate's bill: a lounge at 18% the company pays, a meal it does not, and a return. */
const BILL = {
  id: 'bill-1',
  billNumber: 'NHOB26000198',
  patientName: 'Asha Devi',
  payerId: 'payer-1',
  payerType: 'Corporate',
  payerName: 'Malabar Traders Ltd',
  lines: [
    { id: 'lounge', description: 'Executive lounge', taxRate: 18, isReturn: false, isPayerPayable: true },
    { id: 'meal', description: 'Attendant meal', taxRate: 5, isReturn: false, isPayerPayable: false },
    { id: 'returned', description: 'Gauze returned', taxRate: 0, isReturn: true, isPayerPayable: true },
  ],
} as unknown as BillDetail;

function renderIn(ui: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={client}>{ui}</QueryClientProvider>
    </FluentProvider>,
  );
}

describe('DebitNoteDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(billingApi.requestDebitNote).mockResolvedValue({ summary: 'Debit note of ₹236.00' } as never);
  });

  it('adds GST at each service rate, as the server rounds it', () => {
    expect(debitGst(200, 18)).toBe(36);
    expect(debitGst(10.05, 5)).toBe(0.5);
  });

  it('raises the payer’s services only, with their GST, and sends the taxable values', async () => {
    const user = userEvent.setup();
    renderIn(<DebitNoteDialog bill={BILL} open onClose={vi.fn()} />);
    const services = screen.getByRole('group', { name: 'Services this debit note raises' });
    expect(services).not.toHaveTextContent('Gauze returned');
    await expectNoAxeViolations();

    await user.click(screen.getByRole('radio', { name: /The payer/ }));
    expect(services).not.toHaveTextContent('Attendant meal');
    await user.type(screen.getByRole('textbox', { name: /Executive lounge · GST 18%/ }), '200');
    expect(services).toHaveTextContent(formatInr(236));
    await user.type(screen.getByRole('textbox', { name: /Reason/ }), 'Charged at last year’s rate');
    await user.click(screen.getByRole('button', { name: `Ask for debit note of ${formatInr(236)}` }));

    await waitFor(() =>
      expect(billingApi.requestDebitNote).toHaveBeenCalledWith('bill-1', 'Payer', 'Charged at last year’s rate', [
        { billLineId: 'lounge', taxableAmount: 200 },
      ]),
    );
  });
});
