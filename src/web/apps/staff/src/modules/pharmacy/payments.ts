import { formatInr } from '@hc360/core';
import { dec, sum, toNumber, ZERO } from '@/modules/inventory/decimal';
import type { PaymentMode, SalePayment, SaleStatus } from './api';

/**
 * Split payments at the pharmacy counter. The last row without an amount takes "the rest of the bill", so the
 * common case (one payment for the whole bill) needs no typing and follows the bill as the cart changes; when the
 * bill is split, every other row needs its amount.
 */
export interface PaymentDraft {
  key: string;
  mode: PaymentMode;
  /** null = the balance of the bill (on the last such row), otherwise "amount not entered yet". */
  amount: number | null;
  reference: string;
}

export interface ResolvedPayment extends SalePayment {
  key: string;
  auto: boolean;
}

export interface PaymentCheck {
  ok: boolean;
  /** Why the payments cannot be accepted yet (plain language), or null. */
  message: string | null;
  field: 'amount' | 'reference' | 'mode' | null;
  paid: number;
  due: number;
  status: SaleStatus;
}

export const MAX_PAYMENTS = 6;

/** Fills the balance row, keeps rows still waiting for an amount (as zero) and drops a balance row that comes to nothing. */
export function resolvePayments(bill: number, drafts: readonly PaymentDraft[]): ResolvedPayment[] {
  let balanceRow = -1;
  drafts.forEach((draft, index) => {
    if (draft.amount === null) {
      balanceRow = index;
    }
  });
  const fixed = sum(drafts.filter((d) => d.amount !== null).map((d) => dec(d.amount)));
  const balance = dec(bill) - fixed;
  const resolved: ResolvedPayment[] = [];
  drafts.forEach((draft, index) => {
    const reference = draft.reference.trim() || null;
    if (draft.amount !== null) {
      resolved.push({ key: draft.key, mode: draft.mode, amount: draft.amount, reference, auto: false });
    } else if (index !== balanceRow) {
      resolved.push({ key: draft.key, mode: draft.mode, amount: 0, reference, auto: false });
    } else if (balance > ZERO) {
      resolved.push({ key: draft.key, mode: draft.mode, amount: toNumber(balance), reference, auto: true });
    }
  });
  return resolved;
}

export function paymentsTotal(payments: readonly { amount: number }[]): number {
  return toNumber(sum(payments.map((p) => dec(p.amount))));
}

/** Status of a bill after payment (SalePricing.StatusFor). */
export function statusFor(paid: number, due: number): SaleStatus {
  return due <= 0 ? 'Paid' : paid <= 0 ? 'Credit' : 'PartiallyPaid';
}

/**
 * Mirrors SalePricing.Settle and the request validator: every amount positive, card and UPI with a reference,
 * at most six payments, credit only for registered patients, and the split adding up exactly to the bill (the
 * counter gives change outside the system).
 */
export function checkPayments(payable: number, payments: readonly SalePayment[], creditAllowed: boolean): PaymentCheck {
  const fail = (message: string, field: PaymentCheck['field']): PaymentCheck => ({
    ok: false,
    message,
    field,
    paid: 0,
    due: payable,
    status: 'Credit',
  });
  if (payable === 0 && payments.length === 0) {
    return { ok: true, message: null, field: null, paid: 0, due: 0, status: 'Paid' };
  }
  if (payments.length === 0) {
    return fail('Record how the bill is paid.', 'amount');
  }
  if (payments.length > MAX_PAYMENTS) {
    return fail(`Split the bill into at most ${MAX_PAYMENTS} payments.`, 'amount');
  }
  if (payments.some((p) => !(p.amount > 0))) {
    return fail('Enter the amount of each payment (the last one takes the balance).', 'amount');
  }
  if (payments.some((p) => (p.mode === 'Card' || p.mode === 'Upi') && !p.reference?.trim())) {
    return fail('Enter the card slip or UPI transaction reference.', 'reference');
  }
  if (!creditAllowed && payments.some((p) => p.mode === 'Credit')) {
    return fail('Credit is allowed only for registered patients. Choose the patient or take the payment now.', 'mode');
  }
  const total = sum(payments.map((p) => dec(p.amount)));
  if (total !== dec(payable)) {
    const difference = toNumber(dec(payable) - total);
    return fail(
      difference > 0
        ? `${formatInr(difference)} is still to be allocated. Payments must add up to the bill of ${formatInr(payable)}.`
        : `Payments are ${formatInr(-difference)} more than the bill of ${formatInr(payable)}. Give change in cash and record only the bill amount.`,
      'amount',
    );
  }
  const paid = sum(payments.filter((p) => p.mode !== 'Credit').map((p) => dec(p.amount)));
  const due = dec(payable) - paid;
  return {
    ok: true,
    message: null,
    field: null,
    paid: toNumber(paid),
    due: toNumber(due),
    status: statusFor(toNumber(paid), toNumber(due)),
  };
}

/** Change to hand back for cash received (never negative). */
export function changeDue(cashReceived: number | null | undefined, cashAmount: number): number {
  if (cashReceived === null || cashReceived === undefined) {
    return 0;
  }
  const change = dec(cashReceived) - dec(cashAmount);
  return change > ZERO ? toNumber(change) : 0;
}

let nextKey = 0;

/** Stable key for a new payment row (called from event handlers, never during render). */
export function newPaymentKey(): string {
  nextKey += 1;
  return `payment-${nextKey}`;
}

/** One "balance" row in cash: the fastest default at the counter. */
export function defaultPayments(mode: PaymentMode = 'Cash'): PaymentDraft[] {
  return [{ key: 'payment-default', mode, amount: null, reference: '' }];
}
