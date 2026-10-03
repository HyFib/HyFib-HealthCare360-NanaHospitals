import { describe, expect, it } from 'vitest';
import type { TallyLedgerMap } from '../tallyApi';
import { counterSalesLedger, isOneLine, ledgerFormSchema, toLedgerForm, toLedgerMap } from './ledgerForm';

/** The map as the server returns it: built-in names filled in, the pharmacy with a cash ledger of its own. */
const map: TallyLedgerMap = {
  defaultIncome: 'Hospital Service Income',
  incomeByDepartment: { RAD: 'Radiology Income', LAB: 'Laboratory Income' },
  discount: 'Discount Allowed',
  outputCgst: 'Output CGST',
  outputSgst: 'Output SGST',
  roundOff: 'Round Off',
  patientDebtors: 'Patient Receivables',
  patientAdvances: 'Patient Advances',
  payerReceivables: 'Insurance & Corporate Receivables',
  ledgerPerPayer: true,
  modeLedgers: { card: 'HDFC Card Settlement' },
  creditNotes: 'Sales Returns & Allowances',
  writeOffs: 'Bad Debts Written Off',
  insuranceDisallowances: 'Insurance Disallowances',
  doctorFeeExpense: 'Doctor Professional Fees',
  doctorPayablePrefix: 'Doctor Fees Payable - ',
  tdsPayable: 'TDS Payable (194J)',
  doctorRecoveries: 'Doctor Recoveries',
  payoutBank: 'Bank',
  pharmacy: {
    include: true,
    sales: 'Pharmacy Sales',
    onAccount: 'Pharmacy Receivables',
    modeLedgers: { Cash: 'Pharmacy Cash' },
  },
  canteen: { include: false, sales: 'Canteen Sales', onAccount: 'Staff Canteen Recoverable', modeLedgers: {} },
  salesLedgerPerGstRate: true,
};

describe('ledger names form', () => {
  it('shows departments in order, a mode only when it has its own ledger, and built-in counter names as empty', () => {
    const form = toLedgerForm(map);

    expect(form.departments).toEqual([
      { code: 'LAB', ledger: 'Laboratory Income' },
      { code: 'RAD', ledger: 'Radiology Income' },
    ]);
    expect(form.modes.Card).toBe('HDFC Card Settlement');
    expect(form.modes.Cash).toBe('');
    expect(form.pharmacy).toEqual({
      include: true,
      sales: '',
      onAccount: '',
      cash: 'Pharmacy Cash',
      card: '',
      upi: '',
    });
    expect(form.canteen.include).toBe(false);
  });

  it('saves the same map it was given', () => {
    const saved = toLedgerMap(ledgerFormSchema.parse(toLedgerForm(map)));

    expect(saved.incomeByDepartment).toEqual({ LAB: 'Laboratory Income', RAD: 'Radiology Income' });
    expect(saved.modeLedgers).toEqual({ Card: 'HDFC Card Settlement' });
    expect(saved.pharmacy).toEqual({
      include: true,
      sales: null,
      onAccount: null,
      modeLedgers: { Cash: 'Pharmacy Cash' },
    });
    expect(saved.doctorPayablePrefix).toBe('Doctor Fees Payable - ');
    expect(saved.canteen.include).toBe(false);
  });

  it('refuses an empty name, a name on two lines and a department listed twice', () => {
    const form = toLedgerForm(map);

    const result = ledgerFormSchema.safeParse({
      ...form,
      roundOff: '  ',
      discount: 'Discount\nAllowed',
      departments: [...form.departments, { code: 'lab', ledger: 'Lab Income' }],
    });

    expect(result.success).toBe(false);
    const paths = result.error!.issues.map((issue) => issue.path.join('.'));
    expect(paths).toEqual(expect.arrayContaining(['roundOff', 'discount', 'departments.2.code']));
  });

  it('names counter sales ledgers by GST rate when asked', () => {
    expect(counterSalesLedger('Pharmacy Sales', true, 12)).toBe('Pharmacy Sales @ 12%');
    expect(counterSalesLedger('Pharmacy Sales', false, 12)).toBe('Pharmacy Sales');
    expect(isOneLine('Cash\tCounter')).toBe(false);
    expect(isOneLine('Cash Counter 1')).toBe(true);
  });
});
