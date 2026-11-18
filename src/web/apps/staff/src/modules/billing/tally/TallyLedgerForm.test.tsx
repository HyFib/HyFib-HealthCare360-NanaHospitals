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
  purchaseReturns: 'Purchase Returns',
  inputCgst: 'Input CGST',
  inputSgst: 'Input SGST',
  inputIgst: 'Input IGST',
  vendorAdjustments: 'Purchase Rate Differences',
  ledgerPerVendor: true,
  sundryCreditors: 'Sundry Creditors',
  purchases: 'Purchase Accounts',
  purchasesFrom: 'Pharmacy',
  gstLedgerPerRate: true,
  pharmacyPerInvoice: true,
  voucherTypes: {
    inPatient: 'IP Sales',
    outPatient: 'OP Sales',
    laboratory: 'Lab Sales',
    pharmacy: 'Pharmacy Sales',
    purchase: 'Purchase',
  },
  patientInNarration: 'NameAndUhid',
  bankLedgers: null,
};

function renderForm(canEdit = true, shown: TallyLedgerMap = map) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>
        <TallyLedgerForm map={shown} canEdit={canEdit} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

async function save() {
  await userEvent.click(screen.getByRole('button', { name: 'Save ledger names' }));
  await waitFor(() => expect(tallyApi.saveLedgers).toHaveBeenCalledTimes(1));
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
    expect(screen.getByRole('textbox', { name: 'Laboratory bills' })).toBeDisabled();
    expect(screen.getByRole('radio', { name: 'UHID only' })).toBeDisabled();
    expect(screen.getByRole('switch', { name: 'The ledgers HC360 posts bank money to' })).toBeDisabled();
    expect(screen.getByRole('combobox', { name: 'Goods receipts posted as purchases' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Save ledger names' })).not.toBeInTheDocument();
  });
});

describe('TallyLedgerForm: voucher types, narrations and how things post', () => {
  it('shows the voucher types, refuses an empty one and saves a renamed one', async () => {
    vi.mocked(tallyApi.saveLedgers).mockClear();
    renderForm();
    const lab = screen.getByRole('textbox', { name: 'Laboratory bills' });
    expect(lab).toHaveValue('Lab Sales');
    expect(lab).toHaveAttribute('maxlength', '32');
    expect(screen.getByRole('textbox', { name: 'In-patient bills' })).toHaveValue('IP Sales');
    expect(screen.getByRole('textbox', { name: 'Purchases (goods receipts)' })).toHaveValue('Purchase');

    await userEvent.clear(lab);
    await userEvent.click(screen.getByRole('button', { name: 'Save ledger names' }));
    expect(await screen.findByText('Enter the voucher type as it is named in Tally.')).toBeInTheDocument();
    expect(tallyApi.saveLedgers).not.toHaveBeenCalled();

    await userEvent.type(lab, 'Sales - Lab');
    await save();
    expect(tallyApi.saveLedgers).toHaveBeenCalledWith(
      expect.objectContaining({
        voucherTypes: expect.objectContaining({ laboratory: 'Sales - Lab', inPatient: 'IP Sales' }),
      }),
    );
  });

  it('names the patient as chosen, with the name and UHID by default', async () => {
    vi.mocked(tallyApi.saveLedgers).mockClear();
    renderForm();
    expect(screen.getByRole('radio', { name: 'Name and UHID' })).toBeChecked();
    expect(screen.getByText(/always named by UHID only/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('radio', { name: 'UHID only' }));
    await save();

    expect(tallyApi.saveLedgers).toHaveBeenCalledWith(expect.objectContaining({ patientInNarration: 'UhidOnly' }));
  });

  it('shows the bank ledgers in use, and lists them by hand starting from those', async () => {
    vi.mocked(tallyApi.saveLedgers).mockClear();
    renderForm();
    expect(screen.getByText('Bank ledgers now: Bank, HDFC Current Account.')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('switch', { name: 'The ledgers HC360 posts bank money to' }));
    expect(screen.getByRole('textbox', { name: 'Bank ledger 1' })).toHaveValue('Bank');
    expect(screen.getByRole('textbox', { name: 'Bank ledger 2' })).toHaveValue('HDFC Current Account');
    expect(screen.getByText('The cheque ledger gets bank details only when it is listed here.')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Add a bank ledger' }));
    await userEvent.type(screen.getByRole('textbox', { name: 'Bank ledger 3' }), 'bank');
    await userEvent.click(screen.getByRole('button', { name: 'Save ledger names' }));
    expect(await screen.findByText('This ledger is already in the list.')).toBeInTheDocument();
    expect(tallyApi.saveLedgers).not.toHaveBeenCalled();

    await userEvent.clear(screen.getByRole('textbox', { name: 'Bank ledger 3' }));
    await userEvent.type(screen.getByRole('textbox', { name: 'Bank ledger 3' }), 'Cheques in Hand');
    await userEvent.click(screen.getByRole('button', { name: 'Remove bank ledger 1' }));
    await save();

    expect(tallyApi.saveLedgers).toHaveBeenCalledWith(
      expect.objectContaining({ bankLedgers: ['HDFC Current Account', 'Cheques in Hand'] }),
    );
  });

  it("sends no list while the default bank ledgers are used, and shows a facility's own list", async () => {
    vi.mocked(tallyApi.saveLedgers).mockClear();
    renderForm(true, { ...map, bankLedgers: ['SBI Current Account'] });
    expect(screen.getByRole('switch', { name: 'The ledgers HC360 posts bank money to' })).not.toBeChecked();
    expect(screen.getByRole('textbox', { name: 'Bank ledger 1' })).toHaveValue('SBI Current Account');

    await userEvent.click(screen.getByRole('switch', { name: 'The ledgers HC360 posts bank money to' }));
    await save();

    expect(tallyApi.saveLedgers).toHaveBeenCalledWith(expect.objectContaining({ bankLedgers: null }));
  });

  it('posts GST per rate, the pharmacy per invoice and purchases as set', async () => {
    vi.mocked(tallyApi.saveLedgers).mockClear();
    renderForm();
    expect(screen.getByText('CGST on a supply at 12% posts to “Output CGST @ 6%”.')).toBeInTheDocument();
    expect(screen.getByText('Purchases at 12% post to “Purchase Accounts @ 12%”.')).toBeInTheDocument();
    expect(screen.getByRole('switch', { name: 'One voucher per invoice' })).toBeChecked();

    await userEvent.click(screen.getByRole('switch', { name: 'A GST ledger for each tax and rate' }));
    expect(screen.getByText('CGST on a supply at 12% posts to “Output CGST”.')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('switch', { name: 'One voucher per invoice' }));
    await userEvent.selectOptions(
      screen.getByRole('combobox', { name: 'Goods receipts posted as purchases' }),
      'Every store',
    );
    const purchases = screen.getByRole('textbox', { name: 'Purchases' });
    await userEvent.clear(purchases);
    await userEvent.type(purchases, 'Purchase of Medicines');
    await save();

    expect(tallyApi.saveLedgers).toHaveBeenCalledWith(
      expect.objectContaining({
        gstLedgerPerRate: false,
        pharmacyPerInvoice: false,
        purchasesFrom: 'All',
        purchases: 'Purchase of Medicines',
        inputCgst: 'Input CGST',
        sundryCreditors: 'Sundry Creditors',
      }),
    );
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
