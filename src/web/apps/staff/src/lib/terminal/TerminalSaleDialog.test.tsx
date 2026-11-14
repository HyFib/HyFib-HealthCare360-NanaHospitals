import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { TerminalSaleDialog } from './TerminalSaleDialog';
import type { CounterTerminalSale, TerminalSaleApi } from './types';

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

function sale(patch: Partial<CounterTerminalSale>): CounterTerminalSale {
  return {
    id: 's1',
    counter: 'canteen',
    provider: 'pinelabs',
    terminalId: 'CN-1',
    transactionId: 'unconfirmed-0199',
    amount: 70,
    mode: 'Upi',
    status: 'Unconfirmed',
    approvalCode: null,
    cardLast4: null,
    rrn: null,
    message: 'The terminal did not answer.',
    usedFor: null,
    createdAt: '2026-11-14T05:00:00Z',
    completedAt: null,
    retryAfter: '2999-11-14T05:07:00Z',
    ...patch,
  };
}

const api = {
  queryKey: ['test', 'terminal-sales'],
  start: vi.fn(),
  get: vi.fn(),
  cancel: vi.fn(),
  unused: vi.fn(),
} satisfies TerminalSaleApi;

function renderDialog() {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <FluentProvider theme={webLightTheme}>
        <TerminalSaleDialog
          open
          api={api}
          counter="canteen"
          amount={70}
          initialMode="Upi"
          maxAmount={70}
          exact
          approveLabel="Finish the bill"
          onClose={vi.fn()}
          onApproved={vi.fn()}
        />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

async function sendToTerminal() {
  await userEvent.type(await screen.findByLabelText(/Terminal/, { selector: 'input' }), 'CN-1');
  await userEvent.click(screen.getByRole('button', { name: 'Send to terminal' }));
}

describe('TerminalSaleDialog with a sale whose answer was lost', () => {
  beforeEach(() => {
    localStorage.clear();
    api.unused.mockReset().mockResolvedValue([]);
    api.start.mockReset();
    api.get.mockReset();
    api.cancel.mockReset();
  });

  it('shows what to check and keeps Try again off while the sale may still be on the terminal', async () => {
    api.start.mockResolvedValue(sale({}));
    api.get.mockResolvedValue(sale({}));
    renderDialog();

    await sendToTerminal();

    expect(await screen.findByText('Do not send this sale again yet')).toBeInTheDocument();
    const alert = screen.getByRole('alert');
    expect(alert).toHaveTextContent('may still be on the terminal');
    expect(alert).toHaveTextContent("Check the terminal's screen");
    expect(alert).toHaveTextContent('record the payment from the slip');
    expect(alert).toHaveTextContent(/10:30/);
    expect(alert).toHaveTextContent(/Sending again is possible from/);
    expect(screen.getByRole('button', { name: 'Try again' })).toBeDisabled();
    // Not shown as a decline.
    expect(screen.queryByText('Declined')).not.toBeInTheDocument();
  });

  it('lets the cashier send again once the wait is over', async () => {
    const over = sale({ retryAfter: '2000-01-01T00:00:00Z' });
    api.start.mockResolvedValue(over);
    api.get.mockResolvedValue(over);
    renderDialog();

    await sendToTerminal();

    expect(await screen.findByText(/The wait is over/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeEnabled();
  });

  it('clears the sale with the cashier’s note, which frees the terminal', async () => {
    api.start.mockResolvedValue(sale({}));
    api.get.mockResolvedValue(sale({}));
    api.cancel.mockResolvedValue(
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
    await userEvent.type(screen.getByLabelText(/Terminal shows no sale\?/), 'Screen shows the idle menu');
    await userEvent.click(clear);

    await waitFor(() => expect(api.cancel).toHaveBeenCalledWith('s1', 'Screen shows the idle menu'));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Try again' })).toBeEnabled());
  });

  it('shows a sale that lapsed as never confirmed, with the outcome unknown', async () => {
    const lapsed = sale({
      status: 'Lapsed',
      retryAfter: null,
      message: 'The terminal never confirmed this sale; the outcome is unknown.',
    });
    api.start.mockResolvedValue(lapsed);
    api.get.mockResolvedValue(lapsed);
    renderDialog();

    await sendToTerminal();

    expect(await screen.findByText('Never confirmed: the outcome is unknown')).toBeInTheDocument();
    expect(screen.getByText(/the outcome is unknown\./)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'The terminal shows no sale' })).not.toBeInTheDocument();
  });
});
