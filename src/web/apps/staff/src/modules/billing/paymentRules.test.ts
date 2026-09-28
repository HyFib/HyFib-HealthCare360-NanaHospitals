import { describe, expect, it } from 'vitest';
import type { PaymentMode } from './labels';
import {
  addMonths,
  checkPaymentLine,
  newPaymentLine,
  summariseSplit,
  validatePaymentLine,
  type PaymentLineDraft,
  type PaymentLineInput,
} from './paymentRules';

const TODAY = '2026-09-25';

function input(mode: PaymentMode, amount: number, extra: Partial<PaymentLineInput> = {}): PaymentLineInput {
  return { mode, amount, reference: null, cardLast4: null, cardNetwork: null, chequeNumber: null, chequeBank: null, chequeDate: null, ...extra };
}

const valid = (line: PaymentLineInput, atCounter = true) => validatePaymentLine(line, TODAY, atCounter) === null;

describe('PaymentLineRules (mirrors PaymentLineRulesTests)', () => {
  it('needs no reference for cash', () => {
    expect(valid(input('Cash', 500))).toBe(true);
  });

  it('needs the last four card digits', () => {
    expect(valid(input('Card', 500))).toBe(false);
    expect(valid(input('Card', 500, { cardLast4: '12a4' }))).toBe(false);
    expect(valid(input('Card', 500, { cardLast4: '1234' }))).toBe(true);
  });

  it.each(['Upi', 'NetBanking', 'Wallet'] as const)('needs a reference for %s', (mode) => {
    expect(valid(input(mode, 500))).toBe(false);
    expect(valid(input(mode, 500, { reference: '425612345678' }))).toBe(true);
  });

  it('needs cheque number, bank and a date within three months that is not post-dated', () => {
    const cheque = (extra: Partial<PaymentLineInput> = {}) =>
      input('Cheque', 5_000, { chequeNumber: '123456', chequeBank: 'SBI', chequeDate: TODAY, ...extra });
    expect(valid(cheque())).toBe(true);
    expect(valid(cheque({ chequeNumber: '12345' }))).toBe(false);
    expect(valid(cheque({ chequeBank: null }))).toBe(false);
    expect(valid(cheque({ chequeDate: null }))).toBe(false);
    expect(valid(cheque({ chequeDate: '2026-09-26' }))).toBe(false);
    expect(valid(cheque({ chequeDate: '2026-06-25' }))).toBe(true);
    expect(valid(cheque({ chequeDate: '2026-06-24' }))).toBe(false);
  });

  it.each(['Advance', 'Insurance'] as const)('does not take %s at the counter', (mode) => {
    expect(valid(input(mode, 500, { reference: 'REF' }))).toBe(false);
    expect(valid(input(mode, 500, { reference: 'REF' }), false)).toBe(true);
  });

  it.each([0, -10, 10.005])('refuses the amount %s', (amount) => {
    expect(valid(input('Cash', amount))).toBe(false);
  });
});

describe('cheque validity dates', () => {
  it('clamps to the end of a shorter month like DateOnly.AddMonths', () => {
    expect(addMonths('2026-05-31', -3)).toBe('2026-02-28');
    expect(addMonths('2024-05-31', -3)).toBe('2024-02-29');
    expect(addMonths('2026-01-15', -3)).toBe('2025-10-15');
    expect(addMonths('2026-11-30', 3)).toBe('2027-02-28');
  });
});

describe('typed payment lines', () => {
  const draft = (mode: PaymentLineDraft['mode'], amount: string, extra: Partial<PaymentLineDraft> = {}): PaymentLineDraft => ({
    ...newPaymentLine(mode, null),
    amount,
    ...extra,
  });

  it('turns a complete line into the request and blanks into nulls', () => {
    const check = checkPaymentLine(draft('Upi', '1,250.50', { reference: ' 4256 ', cardLast4: '9999' }), TODAY);
    expect(check).toEqual({
      ok: true,
      input: { mode: 'Upi', amount: 1250.5, reference: '4256', cardLast4: null, cardNetwork: null, chequeNumber: null, chequeBank: null, chequeDate: null },
    });
  });

  it('explains what is missing', () => {
    expect(checkPaymentLine(draft('Card', '500'), TODAY)).toEqual({ ok: false, error: 'Enter the last 4 digits of the card.' });
    expect(checkPaymentLine(draft('Cash', '10.005'), TODAY)).toEqual({ ok: false, error: 'Use at most 2 decimal places.' });
  });

  it('works out the balance and the change to return from the cash handed over', () => {
    const lines = [draft('Card', '1000', { cardLast4: '1234' }), draft('Cash', '249.50')];
    expect(summariseSplit(lines, 1249.5, '500')).toEqual({ total: 1249.5, remaining: 0, cash: 249.5, change: 250.5, shortTendered: false });
    expect(summariseSplit(lines, 1500, '')).toMatchObject({ remaining: 250.5, change: null, shortTendered: false });
    expect(summariseSplit(lines, 1000, '200')).toMatchObject({ remaining: -249.5, shortTendered: true, change: null });
  });

  it('starts a line with the amount still due', () => {
    expect(newPaymentLine('Cash', 1250.5).amount).toBe('1250.50');
    expect(newPaymentLine('Cash', 0).amount).toBe('');
  });
});
