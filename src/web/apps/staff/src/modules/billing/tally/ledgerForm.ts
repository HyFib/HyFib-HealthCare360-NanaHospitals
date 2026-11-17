import { z } from 'zod';
import type {
  TallyCounterLedgers,
  TallyLedgerMap,
  TallyNarrationPatient,
  TallyPurchaseStores,
  TallyVoucherTypes,
} from '../tallyApi';

/**
 * The ledger names form (`billing.tally.ledgers`). The rules mirror the server's `TallyLedgerMapValidator`: every
 * name on one line and at most 100 characters, department codes of letters and digits, voucher types on one line and
 * at most 32 characters, at most 50 bank ledgers. A payment mode or counter ledger left empty uses the built-in name,
 * shown as the placeholder.
 */

export const MAX_LEDGER = 100;

/** The longest voucher type name: what a voucher's record keeps on the server. */
export const MAX_VOUCHER_TYPE = 32;

export const MAX_BANK_LEDGERS = 50;

/** The voucher types the day book posts under, with the built-in names. */
export const VOUCHER_TYPE_FIELDS = [
  { key: 'inPatient', label: 'In-patient bills', builtIn: 'IP Sales' },
  { key: 'outPatient', label: 'Out-patient bills', builtIn: 'OP Sales' },
  { key: 'laboratory', label: 'Laboratory bills', builtIn: 'Lab Sales' },
  { key: 'pharmacy', label: 'Pharmacy invoices', builtIn: 'Pharmacy Sales' },
  { key: 'purchase', label: 'Purchases (goods receipts)', builtIn: 'Purchase' },
] as const satisfies readonly { key: keyof TallyVoucherTypes; label: string; builtIn: string }[];

/** What the narration of a patient's voucher says of the patient (`patientInNarration`). */
export const NARRATION_OPTIONS: { value: TallyNarrationPatient; label: string }[] = [
  { value: 'NameAndUhid', label: 'Name and UHID' },
  { value: 'UhidOnly', label: 'UHID only' },
  { value: 'None', label: 'No patient details' },
];

/** Which stores' goods receipts post as purchases (`purchasesFrom`). */
export const PURCHASE_STORE_OPTIONS: { value: TallyPurchaseStores; label: string }[] = [
  { value: 'Pharmacy', label: 'Pharmacy stores' },
  { value: 'All', label: 'Every store' },
  { value: 'None', label: 'None' },
];

/** Payment modes that have a cash or bank ledger (advances post to their own ledger). */
export const MODE_FIELDS = [
  { key: 'Cash', label: 'Cash', builtIn: 'Cash' },
  { key: 'Card', label: 'Card', builtIn: 'Card Settlement Account' },
  { key: 'Upi', label: 'UPI', builtIn: 'UPI Settlement Account' },
  { key: 'NetBanking', label: 'Net banking', builtIn: 'Bank' },
  { key: 'Cheque', label: 'Cheque', builtIn: 'Cheques in Hand' },
  { key: 'PaymentGateway', label: 'Payment gateway', builtIn: 'Payment Gateway Clearing' },
  { key: 'Wallet', label: 'Wallet', builtIn: 'Wallet Clearing' },
  { key: 'Insurance', label: 'Insurers’ settlements', builtIn: 'Bank' },
] as const;

export type ModeKey = (typeof MODE_FIELDS)[number]['key'];

export const COUNTER_MODES = [
  { key: 'Cash', label: 'Cash' },
  { key: 'Card', label: 'Card' },
  { key: 'Upi', label: 'UPI' },
] as const;

export const COUNTER_BUILT_IN = {
  pharmacy: { sales: 'Pharmacy Sales', onAccount: 'Pharmacy Receivables' },
  canteen: { sales: 'Canteen Sales', onAccount: 'Staff Canteen Recoverable' },
} as const;

/** No line breaks or other control characters (Tally ledger names are one line). */
export function isOneLine(value: string): boolean {
  return ![...value].some((c) => c.charCodeAt(0) < 32 || c.charCodeAt(0) === 127);
}

const ledger = z
  .string()
  .trim()
  .min(1, 'Enter the ledger name as it is in Tally.')
  .max(MAX_LEDGER, `Use at most ${MAX_LEDGER} characters.`)
  .refine(isOneLine, 'Keep the ledger name on one line.');

const optionalLedger = z
  .string()
  .trim()
  .max(MAX_LEDGER, `Use at most ${MAX_LEDGER} characters.`)
  .refine(isOneLine, 'Keep the ledger name on one line.');

const voucherType = z
  .string()
  .trim()
  .min(1, 'Enter the voucher type as it is named in Tally.')
  .max(MAX_VOUCHER_TYPE, `Use at most ${MAX_VOUCHER_TYPE} characters.`)
  .refine(isOneLine, 'Keep the voucher type on one line.');

const counter = z.object({
  include: z.boolean(),
  sales: optionalLedger,
  onAccount: optionalLedger,
  cash: optionalLedger,
  card: optionalLedger,
  upi: optionalLedger,
});

export const ledgerFormSchema = z
  .object({
    defaultIncome: ledger,
    discount: ledger,
    outputCgst: ledger,
    outputSgst: ledger,
    outputIgst: ledger,
    roundOff: ledger,
    patientDebtors: ledger,
    patientAdvances: ledger,
    payerReceivables: ledger,
    ledgerPerPayer: z.boolean(),
    creditNotes: ledger,
    writeOffs: ledger,
    insuranceDisallowances: ledger,
    doctorFeeExpense: ledger,
    // Not trimmed at the end: the prefix usually ends in a space ("Doctor Fees Payable - ").
    doctorPayablePrefix: z
      .string()
      .refine((v) => v.trim().length > 0, 'Enter the start of the doctors’ payable ledger names.')
      .refine((v) => v.trimStart().length <= MAX_LEDGER, `Use at most ${MAX_LEDGER} characters.`)
      .refine(isOneLine, 'Keep the ledger name on one line.'),
    tdsPayable: ledger,
    doctorRecoveries: ledger,
    payoutBank: ledger,
    gatewaySettlementBank: ledger,
    gatewayCharges: ledger,
    gatewayInputGst: ledger,
    posSettlementBank: ledger,
    posCharges: ledger,
    departments: z
      .array(
        z.object({
          code: z
            .string()
            .trim()
            .min(1, 'Enter the department code.')
            .max(20, 'Use at most 20 characters.')
            .regex(/^[A-Za-z0-9_-]+$/, 'Use letters, digits, - or _.'),
          ledger,
        }),
      )
      .max(200, 'Map at most 200 departments.'),
    modes: z.object(
      Object.fromEntries(MODE_FIELDS.map((m) => [m.key, optionalLedger])) as Record<ModeKey, typeof optionalLedger>,
    ),
    salesLedgerPerGstRate: z.boolean(),
    gstLedgerPerRate: z.boolean(),
    pharmacy: counter,
    pharmacyPerInvoice: z.boolean(),
    canteen: counter,
    purchases: ledger,
    purchasesFrom: z.enum(['Pharmacy', 'All', 'None']),
    sundryCreditors: ledger,
    ledgerPerVendor: z.boolean(),
    purchaseReturns: ledger,
    inputCgst: ledger,
    inputSgst: ledger,
    inputIgst: ledger,
    vendorAdjustments: ledger,
    voucherTypes: z.object({
      inPatient: voucherType,
      outPatient: voucherType,
      laboratory: voucherType,
      pharmacy: voucherType,
      purchase: voucherType,
    }),
    patientInNarration: z.enum(['NameAndUhid', 'UhidOnly', 'None']),
    // On: the ledgers HC360 posts bank money to (sent as null); off: the rows below.
    bankLedgersDefault: z.boolean(),
    bankLedgers: z.array(z.object({ ledger: optionalLedger })),
  })
  .superRefine((values, ctx) => {
    if (!values.bankLedgersDefault) {
      const banks = new Set<string>();
      if (values.bankLedgers.length > MAX_BANK_LEDGERS) {
        ctx.addIssue({
          code: 'custom',
          path: ['bankLedgers'],
          message: `List at most ${MAX_BANK_LEDGERS} bank ledgers.`,
        });
      }
      values.bankLedgers.forEach((row, index) => {
        const name = row.ledger.trim().toLowerCase();
        if (!name) {
          ctx.addIssue({
            code: 'custom',
            path: ['bankLedgers', index, 'ledger'],
            message: 'Enter the ledger name as it is in Tally.',
          });
        } else if (banks.has(name)) {
          ctx.addIssue({
            code: 'custom',
            path: ['bankLedgers', index, 'ledger'],
            message: 'This ledger is already in the list.',
          });
        }
        banks.add(name);
      });
    }

    const seen = new Set<string>();
    values.departments.forEach((row, index) => {
      const code = row.code.trim().toUpperCase();
      if (code && seen.has(code)) {
        ctx.addIssue({
          code: 'custom',
          path: ['departments', index, 'code'],
          message: 'This department is already in the list.',
        });
      }
      seen.add(code);
    });
  });

export type LedgerForm = z.infer<typeof ledgerFormSchema>;
export type CounterForm = LedgerForm['pharmacy'];

function counterForm(
  counter: TallyCounterLedgers | undefined,
  builtIn: { sales: string; onAccount: string },
): CounterForm {
  const mode = (key: string) =>
    Object.entries(counter?.modeLedgers ?? {}).find(([k]) => k.toLowerCase() === key.toLowerCase())?.[1] ?? '';
  return {
    include: counter?.include ?? true,
    sales: counter?.sales && counter.sales !== builtIn.sales ? counter.sales : '',
    onAccount: counter?.onAccount && counter.onAccount !== builtIn.onAccount ? counter.onAccount : '',
    cash: mode('Cash'),
    card: mode('Card'),
    upi: mode('Upi'),
  };
}

function counterMap(form: CounterForm): TallyCounterLedgers {
  const modeLedgers: Record<string, string> = {};
  for (const { key } of COUNTER_MODES) {
    const value = form[key.toLowerCase() as 'cash' | 'card' | 'upi'].trim();
    if (value) {
      modeLedgers[key] = value;
    }
  }
  return {
    include: form.include,
    sales: form.sales.trim() || null,
    onAccount: form.onAccount.trim() || null,
    modeLedgers,
  };
}

/** The form for a map as the server returns it (built-in names filled in). */
export function toLedgerForm(map: TallyLedgerMap): LedgerForm {
  const mode = (key: string) =>
    Object.entries(map.modeLedgers ?? {}).find(([k]) => k.toLowerCase() === key.toLowerCase())?.[1] ?? '';
  return {
    defaultIncome: map.defaultIncome,
    discount: map.discount,
    outputCgst: map.outputCgst,
    outputSgst: map.outputSgst,
    outputIgst: map.outputIgst,
    roundOff: map.roundOff,
    patientDebtors: map.patientDebtors,
    patientAdvances: map.patientAdvances,
    payerReceivables: map.payerReceivables,
    ledgerPerPayer: map.ledgerPerPayer,
    creditNotes: map.creditNotes,
    writeOffs: map.writeOffs,
    insuranceDisallowances: map.insuranceDisallowances,
    doctorFeeExpense: map.doctorFeeExpense,
    doctorPayablePrefix: map.doctorPayablePrefix,
    tdsPayable: map.tdsPayable,
    doctorRecoveries: map.doctorRecoveries,
    payoutBank: map.payoutBank,
    gatewaySettlementBank: map.gatewaySettlementBank,
    gatewayCharges: map.gatewayCharges,
    gatewayInputGst: map.gatewayInputGst,
    posSettlementBank: map.posSettlementBank,
    posCharges: map.posCharges,
    departments: Object.entries(map.incomeByDepartment ?? {})
      .map(([code, name]) => ({ code, ledger: name }))
      .sort((a, b) => a.code.localeCompare(b.code)),
    modes: Object.fromEntries(MODE_FIELDS.map((m) => [m.key, mode(m.key)])) as Record<ModeKey, string>,
    salesLedgerPerGstRate: map.salesLedgerPerGstRate,
    gstLedgerPerRate: map.gstLedgerPerRate,
    pharmacy: counterForm(map.pharmacy, COUNTER_BUILT_IN.pharmacy),
    pharmacyPerInvoice: map.pharmacyPerInvoice,
    canteen: counterForm(map.canteen, COUNTER_BUILT_IN.canteen),
    purchases: map.purchases,
    purchasesFrom: map.purchasesFrom,
    sundryCreditors: map.sundryCreditors,
    ledgerPerVendor: map.ledgerPerVendor,
    purchaseReturns: map.purchaseReturns,
    inputCgst: map.inputCgst,
    inputSgst: map.inputSgst,
    inputIgst: map.inputIgst,
    vendorAdjustments: map.vendorAdjustments,
    voucherTypes: { ...map.voucherTypes },
    patientInNarration: map.patientInNarration,
    bankLedgersDefault: map.bankLedgers == null,
    bankLedgers: (map.bankLedgers ?? []).map((ledger) => ({ ledger })),
  };
}

/**
 * The ledgers the day book treats as bank accounts while the facility lists none of its own, as the server works them
 * out: the net banking and insurers' mode ledgers, the payout bank and the two settlement banks, each once.
 */
export function defaultBankLedgers(
  form: Pick<LedgerForm, 'modes' | 'payoutBank' | 'gatewaySettlementBank' | 'posSettlementBank'>,
): string[] {
  const names = [
    form.modes.NetBanking.trim() || 'Bank',
    form.modes.Insurance.trim() || 'Bank',
    form.payoutBank.trim(),
    form.gatewaySettlementBank.trim(),
    form.posSettlementBank.trim(),
  ];
  const seen = new Set<string>();
  return names.filter((name) => {
    const key = name.toLowerCase();
    if (!name || seen.has(key)) {
      return false;
    }
    seen.add(key);
    return true;
  });
}

/** A GST ledger as the day book names it: per tax and rate ("Output CGST @ 6%") when asked, else the one ledger. */
export function gstLedger(ledger: string, perRate: boolean, taxRate: number): string {
  return perRate ? `${ledger} @ ${taxRate}%` : ledger;
}

/** The map to save: names trimmed, empty modes and counter names left to the built-in names. */
export function toLedgerMap(form: LedgerForm): TallyLedgerMap {
  const modeLedgers: Record<string, string> = {};
  for (const { key } of MODE_FIELDS) {
    const value = form.modes[key].trim();
    if (value) {
      modeLedgers[key] = value;
    }
  }
  return {
    defaultIncome: form.defaultIncome.trim(),
    incomeByDepartment: Object.fromEntries(
      form.departments.map((row) => [row.code.trim().toUpperCase(), row.ledger.trim()]),
    ),
    discount: form.discount.trim(),
    outputCgst: form.outputCgst.trim(),
    outputSgst: form.outputSgst.trim(),
    outputIgst: form.outputIgst.trim(),
    roundOff: form.roundOff.trim(),
    patientDebtors: form.patientDebtors.trim(),
    patientAdvances: form.patientAdvances.trim(),
    payerReceivables: form.payerReceivables.trim(),
    ledgerPerPayer: form.ledgerPerPayer,
    modeLedgers,
    creditNotes: form.creditNotes.trim(),
    writeOffs: form.writeOffs.trim(),
    insuranceDisallowances: form.insuranceDisallowances.trim(),
    doctorFeeExpense: form.doctorFeeExpense.trim(),
    doctorPayablePrefix: form.doctorPayablePrefix.trimStart(),
    tdsPayable: form.tdsPayable.trim(),
    doctorRecoveries: form.doctorRecoveries.trim(),
    payoutBank: form.payoutBank.trim(),
    gatewaySettlementBank: form.gatewaySettlementBank.trim(),
    gatewayCharges: form.gatewayCharges.trim(),
    gatewayInputGst: form.gatewayInputGst.trim(),
    posSettlementBank: form.posSettlementBank.trim(),
    posCharges: form.posCharges.trim(),
    pharmacy: counterMap(form.pharmacy),
    canteen: counterMap(form.canteen),
    salesLedgerPerGstRate: form.salesLedgerPerGstRate,
    gstLedgerPerRate: form.gstLedgerPerRate,
    pharmacyPerInvoice: form.pharmacyPerInvoice,
    purchases: form.purchases.trim(),
    purchasesFrom: form.purchasesFrom,
    sundryCreditors: form.sundryCreditors.trim(),
    ledgerPerVendor: form.ledgerPerVendor,
    purchaseReturns: form.purchaseReturns.trim(),
    inputCgst: form.inputCgst.trim(),
    inputSgst: form.inputSgst.trim(),
    inputIgst: form.inputIgst.trim(),
    vendorAdjustments: form.vendorAdjustments.trim(),
    voucherTypes: {
      inPatient: form.voucherTypes.inPatient.trim(),
      outPatient: form.voucherTypes.outPatient.trim(),
      laboratory: form.voucherTypes.laboratory.trim(),
      pharmacy: form.voucherTypes.pharmacy.trim(),
      purchase: form.voucherTypes.purchase.trim(),
    },
    patientInNarration: form.patientInNarration,
    bankLedgers: form.bankLedgersDefault
      ? null
      : form.bankLedgers.map((row) => row.ledger.trim()).filter((ledger) => ledger.length > 0),
  };
}

/** The income ledger a counter's sales post to at a GST rate, as the day book names it. */
export function counterSalesLedger(sales: string, perGstRate: boolean, rate: number): string {
  return perGstRate ? `${sales} @ ${rate}%` : sales;
}
