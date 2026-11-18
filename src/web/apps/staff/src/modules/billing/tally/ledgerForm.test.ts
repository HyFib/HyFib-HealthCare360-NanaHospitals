import { describe, expect, it } from 'vitest';
import type { TallyLedgerMap } from '../tallyApi';
import {
  counterSalesLedger,
  defaultBankLedgers,
  gstLedger,
  isOneLine,
  ledgerFormSchema,
  toLedgerForm,
  toLedgerMap,
} from './ledgerForm';

/** The map as the server returns it: built-in names filled in, the pharmacy with a cash ledger of its own. */
const map: TallyLedgerMap = {
  defaultIncome: 'Hospital Service Income',
  incomeByDepartment: { RAD: 'Radiology Income', LAB: 'Laboratory Income' },
  discount: 'Discount Allowed',
  outputCgst: 'Output CGST',
  outputSgst: 'Output SGST',
  outputIgst: 'IGST Payable',
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
  gatewaySettlementBank: 'HDFC Current Account',
  gatewayCharges: 'Payment Gateway Charges',
  gatewayInputGst: 'Input GST',
  posSettlementBank: 'HDFC Current Account',
  posCharges: 'Card & UPI Charges (MDR)',
  pharmacy: {
    include: true,
    sales: 'Pharmacy Sales',
    onAccount: 'Pharmacy Receivables',
    modeLedgers: { Cash: 'Pharmacy Cash' },
  },
  canteen: { include: false, sales: 'Canteen Sales', onAccount: 'Staff Canteen Recoverable', modeLedgers: {} },
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
    expect(saved.gatewaySettlementBank).toBe('HDFC Current Account');
    expect(saved.gatewayCharges).toBe('Payment Gateway Charges');
    expect(saved.gatewayInputGst).toBe('Input GST');
    expect(saved.posSettlementBank).toBe('HDFC Current Account');
    expect(saved.posCharges).toBe('Card & UPI Charges (MDR)');
    expect(saved.pharmacy).toEqual({
      include: true,
      sales: null,
      onAccount: null,
      modeLedgers: { Cash: 'Pharmacy Cash' },
    });
    expect(saved.outputIgst).toBe('IGST Payable');
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

  it("refuses a doctors' payable prefix of spaces only, and keeps the space a prefix ends in", () => {
    const form = toLedgerForm(map);

    const blank = ledgerFormSchema.safeParse({ ...form, doctorPayablePrefix: '   ' });
    expect(blank.success).toBe(false);
    expect(blank.error!.issues.map((issue) => issue.path.join('.'))).toEqual(['doctorPayablePrefix']);

    const saved = toLedgerMap(ledgerFormSchema.parse({ ...form, doctorPayablePrefix: '  Dr Payable - ' }));
    expect(saved.doctorPayablePrefix).toBe('Dr Payable - ');
  });

  it('keeps the voucher types, the narration, purchases and the supplier ledgers as they are', () => {
    const saved = toLedgerMap(ledgerFormSchema.parse(toLedgerForm(map)));

    expect(saved.voucherTypes).toEqual(map.voucherTypes);
    expect(saved.patientInNarration).toBe('NameAndUhid');
    expect(saved.bankLedgers).toBeNull();
    expect(saved.pharmacyPerInvoice).toBe(true);
    expect(saved.gstLedgerPerRate).toBe(true);
    expect(saved.purchasesFrom).toBe('Pharmacy');
    expect(saved).toMatchObject({
      purchases: 'Purchase Accounts',
      purchaseReturns: 'Purchase Returns',
      inputCgst: 'Input CGST',
      inputSgst: 'Input SGST',
      inputIgst: 'Input IGST',
      vendorAdjustments: 'Purchase Rate Differences',
      ledgerPerVendor: true,
      sundryCreditors: 'Sundry Creditors',
    });
  });

  it('refuses a voucher type that is empty, on two lines or longer than 32 characters', () => {
    const form = toLedgerForm(map);

    const result = ledgerFormSchema.safeParse({
      ...form,
      voucherTypes: { ...form.voucherTypes, inPatient: ' ', laboratory: 'Lab\nSales', purchase: 'P'.repeat(33) },
    });

    expect(result.success).toBe(false);
    expect(result.error!.issues.map((issue) => [issue.path.join('.'), issue.message])).toEqual(
      expect.arrayContaining([
        ['voucherTypes.inPatient', 'Enter the voucher type as it is named in Tally.'],
        ['voucherTypes.laboratory', 'Keep the voucher type on one line.'],
        ['voucherTypes.purchase', 'Use at most 32 characters.'],
      ]),
    );
    expect(
      toLedgerMap(ledgerFormSchema.parse({ ...form, voucherTypes: { ...form.voucherTypes, outPatient: ' OP ' } }))
        .voucherTypes.outPatient,
    ).toBe('OP');
  });

  it('lists bank ledgers by hand only when asked, each once and named', () => {
    const form = { ...toLedgerForm(map), bankLedgersDefault: false };

    const listed = toLedgerMap(
      ledgerFormSchema.parse({
        ...form,
        bankLedgers: [{ ledger: ' HDFC Current Account ' }, { ledger: 'Cheques in Hand' }],
      }),
    );
    expect(listed.bankLedgers).toEqual(['HDFC Current Account', 'Cheques in Hand']);
    expect(toLedgerMap(ledgerFormSchema.parse({ ...form, bankLedgers: [] })).bankLedgers).toEqual([]);

    const wrong = ledgerFormSchema.safeParse({
      ...form,
      bankLedgers: [{ ledger: 'Bank' }, { ledger: ' bank ' }, { ledger: '' }],
    });
    expect(wrong.success).toBe(false);
    expect(wrong.error!.issues.map((issue) => [issue.path.join('.'), issue.message])).toEqual([
      ['bankLedgers.1.ledger', 'This ledger is already in the list.'],
      ['bankLedgers.2.ledger', 'Enter the ledger name as it is in Tally.'],
    ]);

    const tooMany = ledgerFormSchema.safeParse({
      ...form,
      bankLedgers: Array.from({ length: 51 }, (_, i) => ({ ledger: `Bank ${i}` })),
    });
    expect(tooMany.error!.issues.map((issue) => issue.message)).toEqual(['List at most 50 bank ledgers.']);

    // While the defaults are used, rows left from before are neither checked nor saved.
    const byDefault = ledgerFormSchema.parse({ ...form, bankLedgersDefault: true, bankLedgers: [{ ledger: '' }] });
    expect(toLedgerMap(byDefault).bankLedgers).toBeNull();
  });

  it("shows a facility's own bank ledgers as listed", () => {
    const form = toLedgerForm({ ...map, bankLedgers: ['HDFC Current Account'] });

    expect(form.bankLedgersDefault).toBe(false);
    expect(form.bankLedgers).toEqual([{ ledger: 'HDFC Current Account' }]);
  });

  it('works out the default bank ledgers as the server does', () => {
    const form = toLedgerForm(map);

    expect(defaultBankLedgers(form)).toEqual(['Bank', 'HDFC Current Account']);
    expect(
      defaultBankLedgers({
        ...form,
        modes: { ...form.modes, NetBanking: 'SBI Current', Insurance: ' ' },
        payoutBank: 'sbi current',
      }),
    ).toEqual(['SBI Current', 'Bank', 'HDFC Current Account']);
  });

  it('names GST ledgers by tax and rate when asked', () => {
    expect(gstLedger('Output CGST', true, 6)).toBe('Output CGST @ 6%');
    expect(gstLedger('Output CGST', true, 2.5)).toBe('Output CGST @ 2.5%');
    expect(gstLedger('Output CGST', false, 6)).toBe('Output CGST');
  });

  it('names counter sales ledgers by GST rate when asked', () => {
    expect(counterSalesLedger('Pharmacy Sales', true, 12)).toBe('Pharmacy Sales @ 12%');
    expect(counterSalesLedger('Pharmacy Sales', false, 12)).toBe('Pharmacy Sales');
    expect(isOneLine('Cash\tCounter')).toBe(false);
    expect(isOneLine('Cash Counter 1')).toBe(true);
  });
});
