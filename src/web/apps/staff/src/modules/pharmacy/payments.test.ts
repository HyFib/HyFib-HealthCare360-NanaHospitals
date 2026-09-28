import { describe, expect, it } from 'vitest';
import { changeDue, checkPayments, defaultPayments, paymentsTotal, resolvePayments, statusFor, type PaymentDraft } from './payments';

const draft = (mode: PaymentDraft['mode'], amount: number | null, reference = ''): PaymentDraft => ({ key: `${mode}-${amount}`, mode, amount, reference });

describe('counter payments', () => {
  it('lets one balance row follow the bill', () => {
    expect(resolvePayments(432.5, defaultPayments())).toEqual([{ key: 'payment-default', mode: 'Cash', amount: 432.5, reference: null, auto: true }]);
    expect(resolvePayments(0, defaultPayments())).toEqual([]);
  });

  it('splits fixed amounts and puts the rest on the balance row', () => {
    const resolved = resolvePayments(500, [draft('Card', 300.1, ' SLIP-81 '), draft('Cash', null)]);
    expect(resolved.map((p) => [p.mode, p.amount, p.reference])).toEqual([
      ['Card', 300.1, 'SLIP-81'],
      ['Cash', 199.9, null],
    ]);
    expect(paymentsTotal(resolved)).toBe(500);
  });

  it('gives the balance to the last open row and asks for the other amounts', () => {
    const resolved = resolvePayments(500, [draft('Card', null), draft('Cash', null)]);
    expect(resolved.map((p) => [p.mode, p.amount, p.auto])).toEqual([
      ['Card', 0, false],
      ['Cash', 500, true],
    ]);
    expect(checkPayments(500, resolved, false).message).toBe('Enter the amount of each payment (the last one takes the balance).');
  });

  it('refuses payments that do not add up to the bill', () => {
    const over = resolvePayments(500, [draft('Upi', 600, 'UTR1'), draft('Cash', null)]);
    expect(over).toHaveLength(1);
    expect(checkPayments(500, over, false).message).toMatch(/more than the bill/);
    expect(checkPayments(500, [{ mode: 'Cash', amount: 450, reference: null }], false).message).toMatch(/still to be allocated/);
  });

  it('needs a reference for card and UPI', () => {
    const check = checkPayments(500, [{ mode: 'Card', amount: 500, reference: null }], false);
    expect(check.ok).toBe(false);
    expect(check.field).toBe('reference');
  });

  it('allows credit only for registered patients', () => {
    const payments = [
      { mode: 'Cash' as const, amount: 200, reference: null },
      { mode: 'Credit' as const, amount: 300, reference: null },
    ];
    expect(checkPayments(500, payments, false).field).toBe('mode');
    expect(checkPayments(500, payments, true)).toEqual({ ok: true, message: null, field: null, paid: 200, due: 300, status: 'PartiallyPaid' });
    expect(checkPayments(500, [{ mode: 'Credit', amount: 500, reference: null }], true).status).toBe('Credit');
  });

  it('accepts a zero bill with no payments and caps the split at six', () => {
    expect(checkPayments(0, [], false).ok).toBe(true);
    expect(checkPayments(10, [], false).message).toBe('Record how the bill is paid.');
    const seven = Array.from({ length: 7 }, () => ({ mode: 'Cash' as const, amount: 1, reference: null }));
    expect(checkPayments(7, seven, false).ok).toBe(false);
  });

  it('works out change for cash received', () => {
    expect(changeDue(500, 432.5)).toBe(67.5);
    expect(changeDue(400, 432.5)).toBe(0);
    expect(changeDue(null, 432.5)).toBe(0);
    expect(statusFor(0, 0)).toBe('Paid');
  });
});
