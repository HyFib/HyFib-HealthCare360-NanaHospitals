import { z } from 'zod';
import type { TallyCounterLedgers, TallyLedgerMap } from '../tallyApi';

/**
 * The ledger names form (`billing.tally.ledgers`). The rules mirror the server's `TallyLedgerMapValidator`: every
 * name on one line and at most 100 characters, department codes of letters and digits. A payment mode or counter
 * ledger left empty uses the built-in name, shown as the placeholder.
 */

export const MAX_LEDGER = 100;

/** Payment modes that have a cash or bank ledger (advances and insurers post to their own ledgers). */
export const MODE_FIELDS = [
  { key: 'Cash', label: 'Cash', builtIn: 'Cash' },
  { key: 'Card', label: 'Card', builtIn: 'Card Settlement Account' },
  { key: 'Upi', label: 'UPI', builtIn: 'UPI Settlement Account' },
  { key: 'NetBanking', label: 'Net banking', builtIn: 'Bank' },
  { key: 'Cheque', label: 'Cheque', builtIn: 'Cheques in Hand' },
  { key: 'PaymentGateway', label: 'Payment gateway', builtIn: 'Payment Gateway Clearing' },
  { key: 'Wallet', label: 'Wallet', builtIn: 'Wallet Clearing' },
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
    roundOff: ledger,
    patientDebtors: ledger,
    patientAdvances: ledger,
    payerReceivables: ledger,
    ledgerPerPayer: z.boolean(),
    creditNotes: ledger,
    writeOffs: ledger,
    insuranceDisallowances: ledger,
    doctorFeeExpense: ledger,
    doctorPayablePrefix: z
      .string()
      .min(1, 'Enter the start of the doctors’ payable ledger names.')
      .max(MAX_LEDGER, `Use at most ${MAX_LEDGER} characters.`)
      .refine(isOneLine, 'Keep the ledger name on one line.'),
    tdsPayable: ledger,
    doctorRecoveries: ledger,
    payoutBank: ledger,
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
    pharmacy: counter,
    canteen: counter,
  })
  .superRefine((values, ctx) => {
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
    departments: Object.entries(map.incomeByDepartment ?? {})
      .map(([code, name]) => ({ code, ledger: name }))
      .sort((a, b) => a.code.localeCompare(b.code)),
    modes: Object.fromEntries(MODE_FIELDS.map((m) => [m.key, mode(m.key)])) as Record<ModeKey, string>,
    salesLedgerPerGstRate: map.salesLedgerPerGstRate,
    pharmacy: counterForm(map.pharmacy, COUNTER_BUILT_IN.pharmacy),
    canteen: counterForm(map.canteen, COUNTER_BUILT_IN.canteen),
  };
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
    doctorPayablePrefix: form.doctorPayablePrefix,
    tdsPayable: form.tdsPayable.trim(),
    doctorRecoveries: form.doctorRecoveries.trim(),
    payoutBank: form.payoutBank.trim(),
    pharmacy: counterMap(form.pharmacy),
    canteen: counterMap(form.canteen),
    salesLedgerPerGstRate: form.salesLedgerPerGstRate,
  };
}

/** The income ledger a counter's sales post to at a GST rate, as the day book names it. */
export function counterSalesLedger(sales: string, perGstRate: boolean, rate: number): string {
  return perGstRate ? `${sales} @ ${rate}%` : sales;
}
