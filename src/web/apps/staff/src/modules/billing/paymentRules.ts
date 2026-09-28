import type { CounterPaymentMode, PaymentMode } from './labels';
import { amountInputText, fromPaise, hasAtMostTwoDecimals, parseRupees, toPaise } from './money';

/**
 * Counter payment rules, mirroring the server's `PaymentLineRules` and `PaymentLineInputValidator`, so the cashier
 * sees what is missing before pressing Collect. The server checks everything again.
 */

/** What the cashier types for one mode of a (split) payment. Every field is text, exactly as typed. */
export interface PaymentLineDraft {
  key: string;
  mode: CounterPaymentMode;
  amount: string;
  reference: string;
  cardLast4: string;
  cardNetwork: string;
  chequeNumber: string;
  chequeBank: string;
  chequeDate: string;
}

/** PaymentLineInput as posted to /receipts and /deposits. */
export interface PaymentLineInput {
  mode: PaymentMode;
  amount: number;
  reference: string | null;
  cardLast4: string | null;
  cardNetwork: string | null;
  chequeNumber: string | null;
  chequeBank: string | null;
  chequeDate: string | null;
}

/** Indian cheques are valid for three months from their date. */
export const CHEQUE_VALIDITY_MONTHS = 3;
/** CollectPaymentRequestValidator: at most 10 modes in one receipt. */
export const MAX_SPLIT_MODES = 10;

const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/;

/** `DateOnly.AddMonths`: the day is clamped to the end of a shorter month (31 May − 3 months = 28/29 Feb). */
export function addMonths(isoDate: string, months: number): string {
  const match = ISO_DATE.exec(isoDate);
  if (!match) {
    return isoDate;
  }
  const year = Number(match[1]);
  const month = Number(match[2]) - 1 + months;
  const day = Number(match[3]);
  const targetYear = year + Math.floor(month / 12);
  const targetMonth = ((month % 12) + 12) % 12;
  const lastDay = new Date(Date.UTC(targetYear, targetMonth + 1, 0)).getUTCDate();
  const clamped = Math.min(day, lastDay);
  return `${String(targetYear).padStart(4, '0')}-${String(targetMonth + 1).padStart(2, '0')}-${String(clamped).padStart(2, '0')}`;
}

export function isLast4(value: string | null | undefined): boolean {
  return /^[0-9]{4}$/.test(value ?? '');
}

/**
 * `PaymentLineRules.Validate` for one line keyed in at the counter. Returns the server's message, or null when the
 * line is complete. `today` is the hospital (IST) date as yyyy-MM-dd.
 */
export function validatePaymentLine(line: PaymentLineInput, today: string, atCounter = true): string | null {
  if (!(line.amount > 0)) {
    return 'Each payment amount must be greater than zero.';
  }
  if (!hasAtMostTwoDecimals(line.amount)) {
    return 'Amounts can have at most 2 decimal places.';
  }
  const has = (value: string | null) => Boolean(value && value.trim());
  switch (line.mode) {
    case 'Advance':
    case 'Insurance':
      if (atCounter) {
        return 'Advance adjustments and insurance settlements are recorded from the IP account and the claim.';
      }
      if (line.mode === 'Insurance' && !has(line.reference)) {
        return 'Enter the transaction reference (UTR).';
      }
      return null;
    case 'Card':
      return isLast4(line.cardLast4) ? null : 'Enter the last 4 digits of the card.';
    case 'Upi':
    case 'NetBanking':
    case 'Wallet':
    case 'PaymentGateway':
      return has(line.reference) ? null : 'Enter the transaction reference (UTR).';
    case 'Cheque': {
      if (!/^[0-9]{6}$/.test(line.chequeNumber ?? '')) {
        return 'Enter the 6-digit cheque number.';
      }
      if (!has(line.chequeBank)) {
        return 'Enter the bank the cheque is drawn on.';
      }
      if (!line.chequeDate || !ISO_DATE.test(line.chequeDate)) {
        return 'Enter the cheque date.';
      }
      if (line.chequeDate > today) {
        return 'Post-dated cheques are not accepted.';
      }
      if (line.chequeDate < addMonths(today, -CHEQUE_VALIDITY_MONTHS)) {
        return 'This cheque is older than 3 months and no longer valid.';
      }
      return null;
    }
    default:
      return null;
  }
}

/** Field-length rules of PaymentLineInputValidator (checked before the business rules). */
function lengthProblem(line: PaymentLineDraft): string | null {
  if (line.reference.trim().length > 64) {
    return 'The reference can have at most 64 characters.';
  }
  if (line.cardNetwork.trim().length > 32) {
    return 'The card network can have at most 32 characters.';
  }
  if (line.chequeBank.trim().length > 100) {
    return 'The bank name can have at most 100 characters.';
  }
  return null;
}

const blankToNull = (value: string) => (value.trim() ? value.trim() : null);

export type PaymentLineCheck = { ok: true; input: PaymentLineInput } | { ok: false; error: string };

/** Reads a typed line into a request line, or explains what is missing. */
export function checkPaymentLine(line: PaymentLineDraft, today: string): PaymentLineCheck {
  const amount = parseRupees(line.amount);
  if (!amount.ok) {
    return { ok: false, error: amount.error };
  }
  const tooLong = lengthProblem(line);
  if (tooLong) {
    return { ok: false, error: tooLong };
  }
  const input: PaymentLineInput = {
    mode: line.mode,
    amount: amount.rupees,
    reference:
      line.mode === 'Upi' || line.mode === 'NetBanking' || line.mode === 'Wallet' ? blankToNull(line.reference) : null,
    cardLast4: line.mode === 'Card' ? blankToNull(line.cardLast4) : null,
    cardNetwork: line.mode === 'Card' ? blankToNull(line.cardNetwork) : null,
    chequeNumber: line.mode === 'Cheque' ? blankToNull(line.chequeNumber) : null,
    chequeBank: line.mode === 'Cheque' ? blankToNull(line.chequeBank) : null,
    chequeDate: line.mode === 'Cheque' ? blankToNull(line.chequeDate) : null,
  };
  const problem = validatePaymentLine(input, today);
  return problem ? { ok: false, error: problem } : { ok: true, input };
}

export interface SplitSummary {
  /** Sum of the amounts typed so far (unreadable amounts count as zero). */
  total: number;
  /** Due minus total: positive = still to collect, negative = more than the balance. */
  remaining: number;
  /** Cash part of the split (what the drawer receives). */
  cash: number;
  /** Change to hand back when the patient gives more cash than the cash part; null when not known. */
  change: number | null;
  /** Cash received is less than the cash part. */
  shortTendered: boolean;
}

/** Totals of a split payment, with the change to return from the cash handed over (cash tendered). */
export function summariseSplit(
  lines: readonly PaymentLineDraft[],
  due: number | null,
  cashTendered: string,
): SplitSummary {
  let total = 0n;
  let cash = 0n;
  for (const line of lines) {
    const parsed = parseRupees(line.amount);
    if (parsed.ok) {
      total += parsed.paise;
      if (line.mode === 'Cash') {
        cash += parsed.paise;
      }
    }
  }
  const tendered = cashTendered.trim() ? parseRupees(cashTendered) : null;
  const tenderedPaise = tendered?.ok ? tendered.paise : null;
  return {
    total: fromPaise(total),
    remaining: due === null ? 0 : fromPaise(toPaise(due) - total),
    cash: fromPaise(cash),
    change: tenderedPaise !== null && tenderedPaise >= cash ? fromPaise(tenderedPaise - cash) : null,
    shortTendered: tenderedPaise !== null && tenderedPaise < cash,
  };
}

let sequence = 0;

/** A fresh line (the key only identifies the row on screen). */
export function newPaymentLine(mode: CounterPaymentMode, amount: number | null): PaymentLineDraft {
  sequence += 1;
  return {
    key: `line-${sequence}`,
    mode,
    amount: amount !== null && amount > 0 ? amountInputText(amount) : '',
    reference: '',
    cardLast4: '',
    cardNetwork: '',
    chequeNumber: '',
    chequeBank: '',
    chequeDate: '',
  };
}
