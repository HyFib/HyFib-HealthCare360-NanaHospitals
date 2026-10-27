import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
  RouterProvider,
} from '@tanstack/react-router';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { LeaveGuard } from '@/shell/LeaveGuard';
import type * as TallyApi from '../tallyApi';
import { tallyApi, type TallyLedgerMap } from '../tallyApi';
import { TallyLedgerForm } from './TallyLedgerForm';

vi.mock('../tallyApi', async (importOriginal) => ({
  ...(await importOriginal<typeof TallyApi>()),
  tallyApi: { saveLedgers: vi.fn(() => Promise.resolve()) },
}));

const map: TallyLedgerMap = {
  defaultIncome: 'Hospital Service Income',
  incomeByDepartment: { LAB: 'Laboratory Income' },
  discount: 'Discount Allowed',
  outputCgst: 'Output CGST',
  outputSgst: 'Output SGST',
  outputIgst: 'Output IGST',
  roundOff: 'Round Off',
  patientDebtors: 'Patient Receivables',
  patientAdvances: 'Patient Advances',
  payerReceivables: 'Insurance & Corporate Receivables',
  ledgerPerPayer: true,
  modeLedgers: {},
  creditNotes: 'Sales Returns & Allowances',
  writeOffs: 'Bad Debts Written Off',
  insuranceDisallowances: 'Insurance Disallowances',
  doctorFeeExpense: 'Doctor Professional Fees',
  doctorPayablePrefix: 'Doctor Fees Payable - ',
  tdsPayable: 'TDS Payable (194J)',
  doctorRecoveries: 'Doctor Recoveries',
  payoutBank: 'Bank',
  gatewaySettlementBank: 'HDFC Current Account',
  gatewayCharges: 'Payment Gateway Charges',
  gatewayInputGst: 'Input GST',
  posSettlementBank: 'HDFC Current Account',
  posCharges: 'Card & UPI Charges (MDR)',
  pharmacy: { include: true, sales: 'Pharmacy Sales', onAccount: 'Pharmacy Receivables', modeLedgers: {} },
  canteen: { include: true, sales: 'Canteen Sales', onAccount: 'Staff Canteen Recoverable', modeLedgers: {} },
  salesLedgerPerGstRate: true,
};

function renderForm(canEdit = true) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>
        <TallyLedgerForm map={map} canEdit={canEdit} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('TallyLedgerForm', () => {
  it('shows the names in use, with the built-in names as placeholders', () => {
    renderForm();

    expect(screen.getByRole('textbox', { name: 'Round off' })).toHaveValue('Round Off');
    expect(screen.getByRole('textbox', { name: 'Department code' })).toHaveValue('LAB');
    expect(screen.getAllByRole('textbox', { name: 'Card' })[0]).toHaveAttribute(
      'placeholder',
      'Card Settlement Account',
    );
    expect(screen.getByText('Pharmacy sales at 12% post to “Pharmacy Sales @ 12%”.')).toBeInTheDocument();
  });

  it('refuses an empty ledger name, then saves the corrected names', async () => {
    renderForm();
    const roundOff = screen.getByRole('textbox', { name: 'Round off' });

    await userEvent.clear(roundOff);
    await userEvent.click(screen.getByRole('button', { name: 'Save ledger names' }));
    expect(await screen.findByText('Enter the ledger name as it is in Tally.')).toBeInTheDocument();
    expect(tallyApi.saveLedgers).not.toHaveBeenCalled();

    await userEvent.type(roundOff, 'Rounding Off');
    await userEvent.click(screen.getByRole('button', { name: 'Save ledger names' }));

    await waitFor(() => expect(tallyApi.saveLedgers).toHaveBeenCalledTimes(1));
    expect(tallyApi.saveLedgers).toHaveBeenCalledWith(
      expect.objectContaining({
        roundOff: 'Rounding Off',
        incomeByDepartment: { LAB: 'Laboratory Income' },
        pharmacy: expect.objectContaining({ sales: null }),
      }),
    );
  });

  it('names the ledger for GST on supplies to another state, and saves it', async () => {
    renderForm();
    const igst = screen.getByRole('textbox', { name: 'Output IGST (other states)' });
    expect(igst).toHaveValue('Output IGST');

    await userEvent.clear(igst);
    await userEvent.type(igst, 'IGST Payable');
    await userEvent.click(screen.getByRole('button', { name: 'Save ledger names' }));

    await waitFor(() => expect(tallyApi.saveLedgers).toHaveBeenCalledTimes(1));
    expect(tallyApi.saveLedgers).toHaveBeenCalledWith(expect.objectContaining({ outputIgst: 'IGST Payable' }));
  });

  it('is read-only without the permission to set up Tally', () => {
    renderForm(false);

    expect(screen.getByRole('textbox', { name: 'Round off' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Save ledger names' })).not.toBeInTheDocument();
  });
});

// On its screen, with the app's leave guard in the root route (SRS UX-08).
function renderOnScreen() {
  const rootRoute = createRootRoute({
    component: () => (
      <>
        <Outlet />
        <LeaveGuard />
      </>
    ),
  });
  const routeTree = rootRoute.addChildren([
    createRoute({ getParentRoute: () => rootRoute, path: '/', component: () => <TallyLedgerForm map={map} canEdit /> }),
    createRoute({ getParentRoute: () => rootRoute, path: '/other', component: () => <h1>Another screen</h1> }),
  ]);
  const router = createRouter({ routeTree, history: createMemoryHistory({ initialEntries: ['/'] }) });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>
        <RouterProvider router={router} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return router;
}

describe('leaving the ledger names', () => {
  it('asks while a changed name is not saved, and not once it is', async () => {
    const router = renderOnScreen();
    const roundOff = await screen.findByRole('textbox', { name: 'Round off' });
    await userEvent.clear(roundOff);
    await userEvent.type(roundOff, 'Rounding Off');

    void router.navigate({ to: '/other' as never });
    const question = await screen.findByRole('alertdialog', { name: 'Leave without saving?' });
    await userEvent.click(within(question).getByRole('button', { name: 'Stay' }));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    expect(router.state.location.pathname).toBe('/');
    expect(roundOff).toHaveValue('Rounding Off');

    await userEvent.click(await screen.findByRole('button', { name: 'Save ledger names' }));
    await waitFor(() => expect(tallyApi.saveLedgers).toHaveBeenCalled());
    await act(() => router.navigate({ to: '/other' as never }));

    expect(await screen.findByRole('heading', { name: 'Another screen' })).toBeInTheDocument();
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
  });
});
