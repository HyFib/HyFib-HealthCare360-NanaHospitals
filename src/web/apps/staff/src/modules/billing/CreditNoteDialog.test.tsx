import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { formatInr } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as BillingApi from './api';
import { billingApi, type BillDetail } from './api';
import { CreditNoteDialog } from './CreditNoteDialog';

// The server and the toasts are outside the test; what the dialog asks for is what is tested.
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof BillingApi>();
  return { ...actual, billingApi: { ...actual.billingApi, requestCreditNote: vi.fn() } };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const line = (id: string, description: string, netAmount: number, taxRate: number, isReturn = false) => ({
  id,
  description,
  sacCode: '999311',
  taxRate,
  netAmount,
  isReturn,
});

/** A paid bill with GST: a room charged at 5% (₹1,050 of it credited already), an exempt dressing and a return. */
const BILL = {
  id: 'bill-1',
  billNumber: 'NHOB260000198',
  patientName: 'Asha Devi',
  patientAmount: 6550,
  creditAmount: 1050,
  paidAmount: 6550,
  taxAmount: 300,
  lines: [
    line('room', 'Single room', 6300, 5),
    line('dressing', 'Dressing', 250, 0),
    line('returned', 'Gauze returned', -50, 0, true),
  ],
  creditNotes: [{ lines: [{ billLineId: 'room', amount: 1050 }] }],
} as unknown as BillDetail;

function renderIn(ui: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={client}>{ui}</QueryClientProvider>
    </FluentProvider>,
  );
}

describe('CreditNoteDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(billingApi.requestCreditNote).mockResolvedValue({ summary: 'Credit note of ₹1,100.00' } as never);
  });

  it('on a bill with GST names the services it corrects, each no further than what is left of it', async () => {
    const user = userEvent.setup();
    renderIn(<CreditNoteDialog bill={BILL} open onClose={vi.fn()} />);

    const services = screen.getByRole('group', { name: 'Services this credit note corrects' });
    expect(services).toHaveTextContent(`Up to ${formatInr(5250)}`);
    expect(services).toHaveTextContent(`Up to ${formatInr(250)}`);
    expect(services).not.toHaveTextContent('Gauze returned');

    await user.type(screen.getByRole('textbox', { name: /Dressing · GST 0%/ }), '300');
    expect(await screen.findByText(`At most ${formatInr(250)} can be credited on this service.`)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Ask for credit note' })).toBeDisabled();

    await user.clear(screen.getByRole('textbox', { name: /Dressing · GST 0%/ }));
    await user.type(screen.getByRole('textbox', { name: /Dressing · GST 0%/ }), '50');
    await user.type(screen.getByRole('textbox', { name: /Single room · GST 5%/ }), '1050');
    await user.type(screen.getByRole('textbox', { name: /Reason/ }), 'Charged a day too many');
    await user.click(screen.getByRole('button', { name: 'Ask for credit note' }));

    await waitFor(() =>
      expect(billingApi.requestCreditNote).toHaveBeenCalledWith(
        'bill-1',
        'CreditNote',
        1100,
        'Charged a day too many',
        [
          { billLineId: 'room', amount: 1050 },
          { billLineId: 'dressing', amount: 50 },
        ],
      ),
    );
  });

  it('asks for a write-off as an amount of the due, naming no services', async () => {
    const user = userEvent.setup();
    renderIn(<CreditNoteDialog bill={BILL} open onClose={vi.fn()} />);

    await user.click(screen.getByRole('radio', { name: /Write-off/ }));
    expect(screen.queryByRole('group', { name: 'Services this credit note corrects' })).not.toBeInTheDocument();
    await user.type(screen.getByRole('textbox', { name: /Amount/ }), '100');
    await user.type(screen.getByRole('textbox', { name: /Reason/ }), 'Cannot be recovered');
    await user.click(screen.getByRole('button', { name: 'Ask for write-off (bad debt)' }));

    await waitFor(() =>
      expect(billingApi.requestCreditNote).toHaveBeenCalledWith('bill-1', 'WriteOff', 100, 'Cannot be recovered'),
    );
  });

  it('on a bill without GST asks for the amount alone', async () => {
    const user = userEvent.setup();
    renderIn(<CreditNoteDialog bill={{ ...BILL, taxAmount: 0 } as BillDetail} open onClose={vi.fn()} />);

    expect(screen.queryByRole('group', { name: 'Services this credit note corrects' })).not.toBeInTheDocument();
    await user.type(screen.getByRole('textbox', { name: /Amount/ }), '200');
    await user.type(screen.getByRole('textbox', { name: /Reason/ }), 'Rate charged above the tariff');
    await user.click(screen.getByRole('button', { name: 'Ask for credit note' }));

    await waitFor(() =>
      expect(billingApi.requestCreditNote).toHaveBeenCalledWith(
        'bill-1',
        'CreditNote',
        200,
        'Rate charged above the tariff',
      ),
    );
  });
});
