import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as BillingApi from './api';
import { billingApi, type PosSaleDto } from './api';
import { PosSaleDialog } from './PosSaleDialog';

// The server is outside the test; the dialog's own behaviour is what is tested.
const getSale = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof BillingApi>();
  return {
    ...actual,
    billingApi: { ...actual.billingApi, startPosSale: vi.fn(), cancelPosSale: vi.fn() },
    billingQueries: {
      ...actual.billingQueries,
      posSale: (id: string) => ({ queryKey: ['billing', 'pos', id], queryFn: () => getSale(id) }),
    },
  };
});

// Fluent's MessageBar measures itself; jsdom has no ResizeObserver.
beforeAll(() => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
});

function sale(patch: Partial<PosSaleDto>): PosSaleDto {
  return {
    id: 'p1',
    provider: 'pinelabs',
    terminalId: 'OPD-1',
    transactionId: 'unconfirmed-0199',
    amount: 1250,
    mode: 'Card',
    status: 'Unconfirmed',
    approvalCode: null,
    cardLast4: null,
    rrn: null,
    message: 'The terminal did not answer.',
    receiptId: null,
    createdAt: '2026-11-14T05:00:00Z',
    completedAt: null,
    retryAfter: '2999-11-14T05:07:00Z',
    ...patch,
  };
}

function renderDialog() {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <FluentProvider theme={webLightTheme}>
        <PosSaleDialog
          open
          target={{ kind: 'bill', billId: 'b1' }}
          defaultAmount={1250}
          maxAmount={1250}
          onClose={vi.fn()}
          onApproved={vi.fn()}
        />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

async function sendToTerminal() {
  const terminal = await screen.findByLabelText(/Terminal/, { selector: 'input' });
  await userEvent.clear(terminal);
  await userEvent.type(terminal, 'OPD-1');
  await userEvent.click(screen.getByRole('button', { name: 'Send to terminal' }));
}

describe('PosSaleDialog with a sale whose answer was lost', () => {
  beforeEach(() => {
    localStorage.clear();
    getSale.mockReset();
    vi.mocked(billingApi.startPosSale).mockReset();
    vi.mocked(billingApi.cancelPosSale).mockReset();
  });

  it('shows what to check, not a decline, and keeps Try again off inside the window', async () => {
    vi.mocked(billingApi.startPosSale).mockResolvedValue(sale({}));
    getSale.mockResolvedValue(sale({}));
    renderDialog();

    await sendToTerminal();

    expect(await screen.findByText('Do not send this sale again yet')).toBeInTheDocument();
    const alert = screen.getByRole('alert');
    expect(alert).toHaveTextContent('may still be on the terminal');
    expect(alert).toHaveTextContent("Check the terminal's screen");
    expect(alert).toHaveTextContent('record the payment from the slip');
    expect(alert).toHaveTextContent(/sent at .*10:30/);
    expect(alert).toHaveTextContent(/Sending again is possible from/);
    expect(screen.getByRole('button', { name: 'Try again' })).toBeDisabled();
    expect(screen.queryByText('Declined')).not.toBeInTheDocument();
  });

  it('lets the cashier try again when the window has passed', async () => {
    const over = sale({ retryAfter: '2000-01-01T00:00:00Z' });
    vi.mocked(billingApi.startPosSale).mockResolvedValue(over);
    getSale.mockResolvedValue(over);
    renderDialog();

    await sendToTerminal();

    expect(await screen.findByText(/The wait is over/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeEnabled();
  });

  it('clears the sale with a note when the terminal shows no sale, then lets the cashier try again', async () => {
    vi.mocked(billingApi.startPosSale).mockResolvedValue(sale({}));
    getSale.mockResolvedValue(sale({}));
    vi.mocked(billingApi.cancelPosSale).mockResolvedValue(
      sale({
        status: 'Cancelled',
        retryAfter: null,
        message: 'The cashier confirmed that the terminal shows no sale.',
      }),
    );
    renderDialog();
    await sendToTerminal();

    const clear = await screen.findByRole('button', { name: 'The terminal shows no sale' });
    expect(clear).toBeDisabled();
    await userEvent.type(screen.getByLabelText(/Terminal shows no sale\?/), 'Idle menu on the screen');
    await userEvent.click(clear);

    await waitFor(() => expect(billingApi.cancelPosSale).toHaveBeenCalledWith('p1', 'Idle menu on the screen'));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Try again' })).toBeEnabled());
  });

  it('shows a lapsed sale as never confirmed', async () => {
    const lapsed = sale({
      status: 'Lapsed',
      retryAfter: null,
      message: 'The terminal never confirmed this sale; the outcome is unknown.',
    });
    vi.mocked(billingApi.startPosSale).mockResolvedValue(lapsed);
    getSale.mockResolvedValue(lapsed);
    renderDialog();

    await sendToTerminal();

    expect(await screen.findByText('Never confirmed: the outcome is unknown')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeEnabled();
  });
});
