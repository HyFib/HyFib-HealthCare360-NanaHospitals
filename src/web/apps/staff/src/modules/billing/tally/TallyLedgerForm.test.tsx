import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
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

  it('is read-only without the permission to set up Tally', () => {
    renderForm(false);

    expect(screen.getByRole('textbox', { name: 'Round off' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Save ledger names' })).not.toBeInTheDocument();
  });
});
